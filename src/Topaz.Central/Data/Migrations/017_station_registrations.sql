-- Stations connect by request: a new agent registers without credentials, a superadmin approves it
-- after comparing the verification code shown on the station. Shared enrollment keys are gone.

CREATE TABLE station_registrations (
    id                  uuid        PRIMARY KEY,
    claim_hash          text        NOT NULL UNIQUE,        -- hex SHA-256 of the agent's secret
    verification_code   text        NOT NULL,
    station_name        text        NOT NULL,
    machine_name        text,
    agent_version       text,
    os                  text,
    rid                 text,
    ip                  text,
    topaz_info          jsonb,
    previous_station_id uuid,
    status              text        NOT NULL DEFAULT 'pending'
                        CHECK (status IN ('pending', 'approved', 'claimed', 'rejected', 'expired')),
    created_at          timestamptz NOT NULL DEFAULT now(),
    last_poll_at        timestamptz NOT NULL DEFAULT now(),
    poll_count          int         NOT NULL DEFAULT 0,
    decided_by          text,
    decided_at          timestamptz,
    note                text,
    station_id          uuid        REFERENCES stations (id) ON DELETE SET NULL
);
CREATE INDEX ix_station_registrations_pending ON station_registrations (created_at) WHERE status = 'pending';
CREATE INDEX ix_station_registrations_ip ON station_registrations (ip) WHERE status = 'pending';

CREATE TABLE registration_blocks (
    ip         text        PRIMARY KEY,
    until      timestamptz NOT NULL,
    blocked_by text,
    reason     text,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Superadmin: everything an owner can do, plus station connection, agent updates, users and the audit log.
ALTER TABLE users DROP CONSTRAINT users_role_check;
ALTER TABLE users ADD CONSTRAINT users_role_check CHECK (role IN ('superadmin', 'owner', 'manager', 'viewer'));
UPDATE users SET role = 'superadmin', security_stamp = gen_random_uuid()
WHERE id = (SELECT id FROM users WHERE role = 'owner' AND is_active ORDER BY created_at LIMIT 1)
  AND NOT EXISTS (SELECT 1 FROM users WHERE role = 'superadmin');

-- Alerts about a registration have no station yet.
ALTER TABLE alerts ALTER COLUMN station_id DROP NOT NULL;
ALTER TABLE alerts ADD COLUMN registration_id uuid REFERENCES station_registrations (id) ON DELETE CASCADE;
CREATE UNIQUE INDEX alerts_registration_uq ON alerts (rule_code, dedupe_key) WHERE station_id IS NULL;

INSERT INTO alert_rules (code, title, description, severity, threshold, unit, sort) VALUES
 ('station_registration', 'АЗС ожидает подключения',
  'Новый агент прислал заявку на подключение. Одобряет суперадминистратор в разделе «АЗС → Подключение», сверив код подтверждения с установщиком',
  'warning', NULL, NULL, 17);
