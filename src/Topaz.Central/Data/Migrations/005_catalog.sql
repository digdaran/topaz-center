-- Cross-station catalogs: canonical fuel types (station fuel names differ: "Аи92", "АИ-92", "92")
-- and item keys (barcode, else normalized name), so stations can be compared and summed.

CREATE TABLE fuel_types (
    code text PRIMARY KEY,
    name text NOT NULL,
    sort int  NOT NULL
);
INSERT INTO fuel_types (code, name, sort) VALUES
    ('AI92', 'АИ-92', 1), ('AI95', 'АИ-95', 2), ('AI98', 'АИ-98/100', 3), ('DT', 'ДТ', 4),
    ('LPG', 'СУГ', 5), ('CNG', 'Метан', 6), ('OTHER', 'Прочее', 9);

-- Manual overrides; without a row the type is guessed from the name.
CREATE TABLE station_fuel_map (
    station_id uuid   NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    fuel_id    bigint NOT NULL,
    fuel_type  text   NOT NULL REFERENCES fuel_types (code),
    PRIMARY KEY (station_id, fuel_id)
);

CREATE FUNCTION guess_fuel_type(p_name text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE
        WHEN n ~ '(98|100)' THEN 'AI98'
        WHEN n ~ '95' THEN 'AI95'
        WHEN n ~ '92' THEN 'AI92'
        WHEN n ~ '(^|[^а-яa-z])(дт|диз|dt|diesel)' OR n ~ '^д[тt]' THEN 'DT'
        WHEN n ~ '(суг|пропан|lpg|автогаз)' THEN 'LPG'
        WHEN n ~ '(метан|кпг|cng|компримир)' THEN 'CNG'
        WHEN n ~ '^газ' THEN 'LPG'
        ELSE 'OTHER' END
    FROM (SELECT lower(coalesce(p_name, '')) AS n) x
$$;

CREATE VIEW v_station_fuels AS
SELECT f.station_id, f.fuel_id, f.name AS fuel_name, f.is_deleted,
       COALESCE(m.fuel_type, guess_fuel_type(f.name)) AS fuel_type, m.fuel_type IS NOT NULL AS manual
FROM v_fuels f LEFT JOIN station_fuel_map m ON m.station_id = f.station_id AND m.fuel_id = f.fuel_id;

-- Fuel sales with the canonical type (appended column keeps the view compatible).
CREATE OR REPLACE VIEW v_fuel_sales_local AS
SELECT fs.*, sn.station, sn.time_zone,
       (fs.sold_at AT TIME ZONE sn.time_zone)                       AS local_ts,
       (fs.sold_at AT TIME ZONE sn.time_zone)::date                 AS local_date,
       extract(hour FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_hour,
       extract(isodow FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_dow,
       f.name AS fuel_name, pm.name AS payment_mode, t.num AS tank_num, h.num AS hose_num,
       (fs.volume > 0 AND fs.payment_kind <> 'none') AS is_sale,
       COALESCE(m.fuel_type, guess_fuel_type(f.name)) AS fuel_type
FROM fuel_sales fs
JOIN v_station_names sn ON sn.station_id = fs.station_id
LEFT JOIN v_fuels f ON f.station_id = fs.station_id AND f.fuel_id = fs.fuel_id
LEFT JOIN station_fuel_map m ON m.station_id = fs.station_id AND m.fuel_id = fs.fuel_id
LEFT JOIN v_payment_modes pm ON pm.station_id = fs.station_id AND pm.payment_mode_id = fs.payment_mode_id
LEFT JOIN v_tanks t ON t.station_id = fs.station_id AND t.tank_id = fs.tank_id
LEFT JOIN v_hoses h ON h.station_id = fs.station_id AND h.hose_id = fs.hose_id;

-- One key per item across stations: barcode if present, else normalized name.
CREATE VIEW v_item_keys AS
SELECT i.station_id, i.item_id, i.name, i.item_code, i.group_id, g.name AS group_name,
       COALESCE(NULLIF(trim(i.item_code), ''), 'name:' || lower(regexp_replace(trim(i.name), '\s+', ' ', 'g'))) AS item_key
FROM v_items i LEFT JOIN v_item_groups g ON g.station_id = i.station_id AND g.group_id = i.group_id;

-- Goods register rows with local time (stock movements: +receipts, −sales, ±acts).
CREATE VIEW v_item_moves AS
SELECT r.station_id, (r.data->>'ItemID')::bigint AS item_id, (r.data->>'StorageID')::bigint AS storage_id,
       (r.data->>'DocTypeID')::int AS doc_type_id, (r.data->>'Date')::timestamptz AS moved_at,
       (r.data->>'Quantity')::numeric AS quantity
FROM records r WHERE r.dataset = 'rgItemRests' AND NOT r.deleted;
