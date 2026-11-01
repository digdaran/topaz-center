-- Organizations: several station owners share one server and never see each other.
-- Application filter: StationScope (every UI query). Second line: row level security on the UI connection pool.

CREATE TABLE organizations (
    id              uuid        PRIMARY KEY,
    name            text        NOT NULL,
    name_normalized text        NOT NULL UNIQUE,
    is_active       boolean     NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE stations              ADD COLUMN organization_id uuid REFERENCES organizations (id);
ALTER TABLE users                 ADD COLUMN organization_id uuid REFERENCES organizations (id);
ALTER TABLE notification_channels ADD COLUMN organization_id uuid REFERENCES organizations (id) ON DELETE CASCADE;
ALTER TABLE item_costs            ADD COLUMN organization_id uuid REFERENCES organizations (id) ON DELETE CASCADE;
ALTER TABLE audit_log             ADD COLUMN organization_id uuid;

-- Everything that exists goes into one organization ("Основная"); the superadmin renames it and adds others.
DO $$
DECLARE org uuid := gen_random_uuid();
BEGIN
    INSERT INTO organizations (id, name, name_normalized) VALUES (org, 'Основная', 'основная');
    UPDATE stations SET organization_id = org;
    UPDATE users SET organization_id = org WHERE role <> 'superadmin';
    UPDATE notification_channels SET organization_id = org;
    UPDATE item_costs SET organization_id = org;
    UPDATE audit_log SET organization_id = org;
END $$;

ALTER TABLE users ADD CONSTRAINT users_org_check CHECK ((role = 'superadmin') = (organization_id IS NULL));
ALTER TABLE item_costs ALTER COLUMN organization_id SET NOT NULL;
CREATE INDEX ix_stations_org ON stations (organization_id);
CREATE INDEX ix_audit_log_org ON audit_log (organization_id, at DESC);

-- Rule thresholds per organization on top of the shared defaults (alert_rules).
CREATE TABLE alert_rule_overrides (
    organization_id uuid    NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    code            text    NOT NULL REFERENCES alert_rules (code) ON DELETE CASCADE,
    enabled         boolean,
    threshold       numeric,
    severity        text CHECK (severity IN ('critical', 'warning', 'info')),
    PRIMARY KEY (organization_id, code)
);

-- The rule as it applies to one station (its organization's override, else the default).
CREATE VIEW v_station_rules AS
SELECT s.id AS station_id, r.code, COALESCE(o.enabled, r.enabled) AS enabled, COALESCE(o.threshold, r.threshold) AS threshold,
       COALESCE(o.severity, r.severity) AS severity
FROM stations s
CROSS JOIN alert_rules r
LEFT JOIN alert_rule_overrides o ON o.organization_id = s.organization_id AND o.code = r.code;

-- Fuel suggestions learn only from stations of the same organization (no names of other owners' stations).
CREATE OR REPLACE VIEW v_fuel_learned AS
SELECT DISTINCT ON (x.organization_id, x.name_key) x.name_key, x.fuel_type, x.stations, x.example_station_id, x.organization_id
FROM (
    SELECT st.organization_id, fuel_name_key(r.data->>'Name') AS name_key, m.fuel_type,
           count(DISTINCT m.station_id) AS stations, (array_agg(m.station_id ORDER BY m.confirmed_at))[1] AS example_station_id
    FROM station_fuel_map m
    JOIN stations st ON st.id = m.station_id
    JOIN records r ON r.station_id = m.station_id AND r.dataset = 'dcFuels' AND r.key = m.fuel_id::text AND NOT r.deleted
    GROUP BY 1, 2, 3
) x
JOIN fuel_types ft ON ft.code = x.fuel_type
WHERE x.name_key <> ''
ORDER BY x.organization_id, x.name_key, x.stations DESC, ft.sort;

CREATE OR REPLACE VIEW v_station_fuels AS
SELECT r.station_id, r.key::bigint AS fuel_id, r.data->>'Name' AS fuel_name, COALESCE((r.data->>'Deleted')::int, 0) <> 0 AS is_deleted,
       COALESCE(m.fuel_type, l.fuel_type, guess_fuel_type(r.data->>'Name')) AS fuel_type, m.fuel_type IS NOT NULL AS manual,
       COALESCE(l.fuel_type, guess_fuel_type(r.data->>'Name')) AS suggested_type,
       CASE WHEN l.fuel_type IS NOT NULL THEN 'learned' WHEN guess_fuel_type(r.data->>'Name') <> 'OTHER' THEN 'name' ELSE 'none' END AS suggestion_source,
       l.example_station_id AS suggested_from,
       m.fuel_type IS NULL AND COALESCE((r.data->>'Deleted')::int, 0) = 0 AND (r.data->>'Name') NOT LIKE '<%' AS needs_confirmation
FROM records r
JOIN stations st ON st.id = r.station_id
LEFT JOIN station_fuel_map m ON m.station_id = r.station_id AND m.fuel_id = r.key::bigint
LEFT JOIN v_fuel_learned l ON l.name_key = fuel_name_key(r.data->>'Name') AND l.organization_id IS NOT DISTINCT FROM st.organization_id
WHERE r.dataset = 'dcFuels' AND NOT r.deleted;

-- ---------------------------------------------------------------- row level security
-- The UI pool runs as role topaz_ui (connection option "-c role=topaz_ui"): row level security applies to it,
-- unlike the owner/superuser the service otherwise uses. Views run with the caller's rights (security_invoker),
-- so they cannot be a way around the policies either.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'topaz_ui') THEN
        CREATE ROLE topaz_ui NOLOGIN;
    END IF;
END $$;
GRANT topaz_ui TO CURRENT_USER;
GRANT USAGE ON SCHEMA public TO topaz_ui;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO topaz_ui;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO topaz_ui;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO topaz_ui;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO topaz_ui;

-- Policies apply to role topaz_ui only (the UI pool); it sets app.stations / app.org / app.platform per request (UiDb).
-- Ingestion, background jobs, migrations and psql run as the owner and are not restricted.

CREATE FUNCTION app_is_ui() RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT COALESCE(current_setting('app.scope', true), '') = 'ui'
$$;
CREATE FUNCTION app_stations() RETURNS uuid[] LANGUAGE sql STABLE AS $$
    SELECT CASE WHEN COALESCE(current_setting('app.stations', true), '') = '' THEN '{}'::uuid[] ELSE current_setting('app.stations')::uuid[] END
$$;
CREATE FUNCTION app_org() RETURNS uuid LANGUAGE sql STABLE AS $$
    SELECT NULLIF(current_setting('app.org', true), '')::uuid
$$;
CREATE FUNCTION app_platform() RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT COALESCE(current_setting('app.platform', true), '') = 'on'
$$;

-- Data of stations: every table with a station_id column, except the special cases below.
DO $$
DECLARE t text;
BEGIN
    FOR t IN
        SELECT c.table_name FROM information_schema.columns c
        JOIN information_schema.tables tb ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name AND tb.table_type = 'BASE TABLE'
        WHERE c.table_schema = 'public' AND c.column_name = 'station_id'
          AND c.table_name NOT IN ('alerts', 'item_costs', 'station_registrations', 'user_stations')
    LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        -- "(SELECT f())": evaluated once per query (an InitPlan), not once per row.
        EXECUTE format('CREATE POLICY org_scope ON %I TO topaz_ui USING (station_id = ANY ((SELECT app_stations())::uuid[])) WITH CHECK (station_id = ANY ((SELECT app_stations())::uuid[]))', t);
    END LOOP;
END $$;

CREATE FUNCTION app_enable_rls(t text, cond text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY org_scope ON %I TO topaz_ui USING (%s) WITH CHECK (%s)', t, cond, cond);
END $$;

SELECT app_enable_rls('stations', 'id = ANY ((SELECT app_stations())::uuid[]) OR (SELECT app_platform())');
SELECT app_enable_rls('alerts', 'station_id = ANY ((SELECT app_stations())::uuid[]) OR (station_id IS NULL AND (SELECT app_platform()))');
SELECT app_enable_rls('item_costs', 'station_id = ANY ((SELECT app_stations())::uuid[]) OR (station_id IS NULL AND organization_id = (SELECT app_org()))');
SELECT app_enable_rls('user_stations', 'station_id = ANY ((SELECT app_stations())::uuid[]) OR (SELECT app_platform())');
SELECT app_enable_rls('users', 'organization_id = (SELECT app_org()) OR (SELECT app_platform())');
SELECT app_enable_rls('audit_log', 'organization_id = (SELECT app_org()) OR (SELECT app_platform())');
SELECT app_enable_rls('organizations', 'id = (SELECT app_org()) OR (SELECT app_platform())');
SELECT app_enable_rls('notification_channels', 'organization_id = (SELECT app_org()) OR (organization_id IS NULL AND (SELECT app_platform()))');
SELECT app_enable_rls('alert_rule_overrides', 'organization_id = (SELECT app_org())');
SELECT app_enable_rls('station_registrations', '(SELECT app_platform())');
SELECT app_enable_rls('registration_blocks', '(SELECT app_platform())');

-- Every view checks the caller's rights and policies (a later migration adding a view must do the same; a test checks it).
DO $$
DECLARE v text;
BEGIN
    FOR v IN SELECT viewname FROM pg_views WHERE schemaname = 'public' LOOP
        EXECUTE format('ALTER VIEW %I SET (security_invoker = true)', v);
    END LOOP;
END $$;
