-- Workplaces (1.11): a station can sell from several Topaz workplaces at once (main + "Дополнительное рабочее место").
-- One shift (sysSessions) has one local shift per workplace (sysLocalSessions) with its own operator; every sale
-- points to its local shift (flOrders/trFastTradeH/flRecepts.LocalSessionID). Filled by projections, backfilled here.

CREATE TABLE workplaces (                                   -- sysWorkbenchs
    station_id     uuid        NOT NULL REFERENCES stations (id),
    workbench_id   bigint      NOT NULL,                    -- WorkbenchID
    name           text,                                    -- Name ("Основное рабочее место", "Дополнительное рабочее место")
    computer_name  text,                                    -- ComputerName
    type_id        int,                                     -- WorkbenchTypeID (sysWorkbenchTypes: 0/1 основное, 3 дополнительное, 4 доп. торговли, 5 администратора)
    is_deleted     boolean,                                 -- Deleted
    updated_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, workbench_id)
);

CREATE TABLE workplace_shifts (                             -- sysLocalSessions: a workplace's part of a shift
    station_id        uuid        NOT NULL REFERENCES stations (id),
    local_session_id  bigint      NOT NULL,                 -- LocalSessionID
    session_id        bigint,                               -- SessionID
    workbench_id      bigint,                               -- WorkbenchID
    user_id           bigint,                               -- UserID (sysUsers)
    started_at        timestamptz,                          -- StartDateTime
    ended_at          timestamptz,                          -- EndDateTime (NULL = open)
    updated_at        timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, local_session_id)
);
CREATE INDEX ix_workplace_shifts_session ON workplace_shifts (station_id, session_id);

CREATE TABLE station_users (                                -- sysUsers: only the login (the operator's name; Name is "Пользователь 1")
    station_id  uuid        NOT NULL REFERENCES stations (id),
    user_id     bigint      NOT NULL,                       -- UserID
    login       text,                                       -- Login (= sysSessions.UserName)
    is_deleted  boolean,                                    -- Deleted
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, user_id)
);

SELECT app_enable_rls('workplaces', 'station_id = ANY ((SELECT app_stations())::uuid[])');
SELECT app_enable_rls('workplace_shifts', 'station_id = ANY ((SELECT app_stations())::uuid[])');
SELECT app_enable_rls('station_users', 'station_id = ANY ((SELECT app_stations())::uuid[])');

ALTER TABLE fuel_sales ADD COLUMN local_session_id bigint;      -- LocalSessionID
ALTER TABLE shop_receipts ADD COLUMN local_session_id bigint;   -- LocalSessionID
ALTER TABLE fuel_receipts ADD COLUMN local_session_id bigint;   -- LocalSessionID

INSERT INTO workplaces (station_id, workbench_id, name, computer_name, type_id, is_deleted)
SELECT station_id, (data->>'WorkbenchID')::bigint, data->>'Name', data->>'ComputerName', (data->>'WorkbenchTypeID')::int, (data->>'Deleted')::int <> 0
FROM records WHERE dataset = 'sysWorkbenchs' AND NOT deleted;

INSERT INTO workplace_shifts (station_id, local_session_id, session_id, workbench_id, user_id, started_at, ended_at)
SELECT station_id, (data->>'LocalSessionID')::bigint, (data->>'SessionID')::bigint, (data->>'WorkbenchID')::bigint, (data->>'UserID')::bigint,
       (data->>'StartDateTime')::timestamptz, (data->>'EndDateTime')::timestamptz
FROM records WHERE dataset = 'sysLocalSessions' AND NOT deleted;

INSERT INTO station_users (station_id, user_id, login, is_deleted)
SELECT station_id, (data->>'UserID')::bigint, data->>'Login', (data->>'Deleted')::int <> 0
FROM records WHERE dataset = 'sysUsers' AND NOT deleted;

UPDATE fuel_sales t SET local_session_id = (r.data->>'LocalSessionID')::bigint
FROM records r WHERE r.dataset = 'flOrders' AND r.station_id = t.station_id AND r.key = t.order_id::text AND r.data->>'LocalSessionID' IS NOT NULL;
UPDATE shop_receipts t SET local_session_id = (r.data->>'LocalSessionID')::bigint
FROM records r WHERE r.dataset = 'trFastTradeH' AND r.station_id = t.station_id AND r.key = t.receipt_id::text AND r.data->>'LocalSessionID' IS NOT NULL;
UPDATE fuel_receipts t SET local_session_id = (r.data->>'LocalSessionID')::bigint
FROM records r WHERE r.dataset = 'flRecepts' AND r.station_id = t.station_id AND r.key = t.receipt_id::text AND r.data->>'LocalSessionID' IS NOT NULL;

-- A local shift with its operator and a short workplace label. The operator is the login of the workplace's user;
-- the shift's UserName when the user is unknown. is_main: the main workplace (types 0, 1, 2, 10, 255), else additional.
CREATE VIEW v_workplace_shifts WITH (security_invoker = true) AS
SELECT w.station_id, w.local_session_id, w.session_id, w.workbench_id, w.user_id, w.started_at, w.ended_at,
       COALESCE(NULLIF(trim(u.login), ''), s.user_name, '#' || w.user_id) AS operator,
       COALESCE(p.type_id, 0) IN (0, 1, 2, 10, 255) AS is_main,
       CASE WHEN COALESCE(p.type_id, 0) IN (0, 1, 2, 10, 255) THEN 'основное' ELSE 'дополнительное' END AS workplace,
       p.name AS workplace_name, p.computer_name
FROM workplace_shifts w
LEFT JOIN workplaces p ON p.station_id = w.station_id AND p.workbench_id = w.workbench_id
LEFT JOIN station_users u ON u.station_id = w.station_id AND u.user_id = w.user_id
LEFT JOIN shifts s ON s.station_id = w.station_id AND s.session_id = w.session_id;

-- Operators of a shift as text for messages: "оператор Инна" or "операторы: Оля (доп. место), Инна (осн. место)".
-- locals: only the operators of these local shifts (e.g. of the orders an alert is about); NULL or empty = all of the shift.
-- Without local shifts (older data, a station on one workplace without them) it is the shift's UserName.
CREATE FUNCTION shift_operators_text(st uuid, sess bigint, locals bigint[] DEFAULT NULL) RETURNS text
LANGUAGE sql STABLE AS $$
    WITH ws AS (
        SELECT DISTINCT operator, is_main FROM v_workplace_shifts
        WHERE station_id = st AND session_id = sess
          AND (locals IS NULL OR cardinality(array_remove(locals, NULL)) = 0 OR local_session_id = ANY (locals))),
    places AS (SELECT count(DISTINCT is_main) AS n FROM v_workplace_shifts WHERE station_id = st AND session_id = sess),
    names AS (
        SELECT operator || CASE WHEN (SELECT n FROM places) > 1 THEN CASE WHEN bool_or(is_main) AND bool_or(NOT is_main) THEN ' (оба места)'
                                                                       WHEN bool_or(is_main) THEN ' (осн. место)' ELSE ' (доп. место)' END
                                ELSE '' END AS label,
               bool_or(is_main) AS main
        FROM ws GROUP BY operator)
    SELECT CASE WHEN (SELECT count(*) FROM names) = 0 THEN 'оператор ' || COALESCE((SELECT user_name FROM shifts WHERE station_id = st AND session_id = sess), '?')
                WHEN (SELECT count(*) FROM names) = 1 THEN 'оператор ' || (SELECT label FROM names)
                ELSE 'операторы: ' || (SELECT string_agg(label, ', ' ORDER BY main DESC, label) FROM names) END
$$;
