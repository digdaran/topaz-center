-- Web interface: users, roles, station access, audit; station display settings;
-- local-time views that every analytics query is built on.

ALTER TABLE stations
    ADD COLUMN time_zone        text    NOT NULL DEFAULT 'Europe/Moscow', -- IANA zone of Topaz local timestamps
    ADD COLUMN time_zone_manual boolean NOT NULL DEFAULT false,           -- set by the owner: agent heartbeat no longer overrides it
    ADD COLUMN display_name     text,
    ADD COLUMN region           text,
    ADD COLUMN group_name       text,
    ADD COLUMN address          text;

CREATE TABLE users (
    id              uuid PRIMARY KEY,
    login           text        NOT NULL,
    login_normalized text       NOT NULL UNIQUE,
    name            text        NOT NULL,
    role            text        NOT NULL CHECK (role IN ('owner', 'manager', 'viewer')),
    password_hash   text        NOT NULL,
    security_stamp  uuid        NOT NULL,          -- changes on password/role/access change: invalidates sessions
    is_active       boolean     NOT NULL DEFAULT true,
    failed_logins   int         NOT NULL DEFAULT 0,
    locked_until    timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    last_login_at   timestamptz
);

-- Stations a manager/viewer may see. Owners see every station.
CREATE TABLE user_stations (
    user_id    uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    station_id uuid NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, station_id)
);

CREATE TABLE audit_log (
    id         bigserial   PRIMARY KEY,
    user_id    uuid,
    user_login text,
    action     text        NOT NULL,
    entity     text,
    details    jsonb,
    at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_audit_log_at ON audit_log (at DESC);

CREATE INDEX ix_fuel_sales_station_sold ON fuel_sales (station_id, sold_at);
CREATE INDEX ix_shop_receipts_station_doc ON shop_receipts (station_id, doc_at);
CREATE INDEX ix_shifts_station_started ON shifts (station_id, started_at);

CREATE VIEW v_station_names AS
SELECT id AS station_id, COALESCE(NULLIF(display_name, ''), name) AS station, time_zone, region, group_name
FROM stations;

-- Fuel sales with station-local time parts and reference names.
CREATE VIEW v_fuel_sales_local AS
SELECT fs.*, sn.station, sn.time_zone,
       (fs.sold_at AT TIME ZONE sn.time_zone)                       AS local_ts,
       (fs.sold_at AT TIME ZONE sn.time_zone)::date                 AS local_date,
       extract(hour FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_hour,
       extract(isodow FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_dow,
       f.name AS fuel_name, pm.name AS payment_mode, t.num AS tank_num, h.num AS hose_num,
       (fs.volume > 0 AND fs.payment_kind <> 'none') AS is_sale
FROM fuel_sales fs
JOIN v_station_names sn ON sn.station_id = fs.station_id
LEFT JOIN v_fuels f ON f.station_id = fs.station_id AND f.fuel_id = fs.fuel_id
LEFT JOIN v_payment_modes pm ON pm.station_id = fs.station_id AND pm.payment_mode_id = fs.payment_mode_id
LEFT JOIN v_tanks t ON t.station_id = fs.station_id AND t.tank_id = fs.tank_id
LEFT JOIN v_hoses h ON h.station_id = fs.station_id AND h.hose_id = fs.hose_id;

-- Shop receipt lines with receipt header, local time and names. Returns have a negative sign.
CREATE VIEW v_item_groups AS
SELECT station_id, (data->>'ItemsGrpID')::bigint AS group_id, data->>'Name' AS name, (data->>'ParentID')::bigint AS parent_id
FROM records WHERE dataset = 'dcItemsGrps' AND NOT deleted;

CREATE VIEW v_shop_lines_local AS
SELECT l.station_id, l.line_id, l.receipt_id, r.session_id, r.doc_at, r.doc_num, r.is_return, r.payment_mode_id,
       sn.station, sn.time_zone,
       (r.doc_at AT TIME ZONE sn.time_zone)                       AS local_ts,
       (r.doc_at AT TIME ZONE sn.time_zone)::date                 AS local_date,
       extract(hour FROM r.doc_at AT TIME ZONE sn.time_zone)::int AS local_hour,
       extract(isodow FROM r.doc_at AT TIME ZONE sn.time_zone)::int AS local_dow,
       l.item_id, i.name AS item_name, i.item_code, i.group_id, g.name AS group_name, pm.name AS payment_mode,
       CASE WHEN r.is_return THEN -l.quantity ELSE l.quantity END AS quantity,
       CASE WHEN r.is_return THEN -l.total ELSE l.total END       AS amount,
       l.price, l.amount_discount
FROM shop_receipt_lines l
JOIN shop_receipts r ON r.station_id = l.station_id AND r.receipt_id = l.receipt_id
JOIN v_station_names sn ON sn.station_id = l.station_id
LEFT JOIN v_items i ON i.station_id = l.station_id AND i.item_id = l.item_id
LEFT JOIN v_item_groups g ON g.station_id = l.station_id AND g.group_id = i.group_id
LEFT JOIN v_payment_modes pm ON pm.station_id = r.station_id AND pm.payment_mode_id = r.payment_mode_id;
