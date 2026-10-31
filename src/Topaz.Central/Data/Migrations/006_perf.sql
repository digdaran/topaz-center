-- Faster analytics views: reference names are joined on the records primary key
-- (station_id, dataset, key) instead of a JSON field. The planner can then prove uniqueness and drop
-- joins whose columns a query does not use, and use the PK index for the others.

CREATE OR REPLACE VIEW v_fuel_sales_local AS
SELECT fs.*, sn.station, sn.time_zone,
       (fs.sold_at AT TIME ZONE sn.time_zone)                       AS local_ts,
       (fs.sold_at AT TIME ZONE sn.time_zone)::date                 AS local_date,
       extract(hour FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_hour,
       extract(isodow FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_dow,
       rf.data->>'Name' AS fuel_name, rp.data->>'Name' AS payment_mode,
       (rt.data->>'Num')::int AS tank_num, (rh.data->>'Num')::int AS hose_num,
       (fs.volume > 0 AND fs.payment_kind <> 'none') AS is_sale,
       COALESCE(m.fuel_type, guess_fuel_type(rf.data->>'Name')) AS fuel_type
FROM fuel_sales fs
JOIN v_station_names sn ON sn.station_id = fs.station_id
LEFT JOIN records rf ON rf.station_id = fs.station_id AND rf.dataset = 'dcFuels' AND rf.key = fs.fuel_id::text
LEFT JOIN station_fuel_map m ON m.station_id = fs.station_id AND m.fuel_id = fs.fuel_id
LEFT JOIN records rp ON rp.station_id = fs.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = fs.payment_mode_id::text
LEFT JOIN records rt ON rt.station_id = fs.station_id AND rt.dataset = 'dcTanks' AND rt.key = fs.tank_id::text
LEFT JOIN records rh ON rh.station_id = fs.station_id AND rh.dataset = 'dcHoses' AND rh.key = fs.hose_id::text;

CREATE OR REPLACE VIEW v_shop_lines_local AS
SELECT l.station_id, l.line_id, l.receipt_id, r.session_id, r.doc_at, r.doc_num, r.is_return, r.payment_mode_id,
       sn.station, sn.time_zone,
       (r.doc_at AT TIME ZONE sn.time_zone)                       AS local_ts,
       (r.doc_at AT TIME ZONE sn.time_zone)::date                 AS local_date,
       extract(hour FROM r.doc_at AT TIME ZONE sn.time_zone)::int AS local_hour,
       extract(isodow FROM r.doc_at AT TIME ZONE sn.time_zone)::int AS local_dow,
       l.item_id, ri.data->>'Name' AS item_name, ri.data->>'ItemCode' AS item_code, (ri.data->>'ItemsGrpID')::bigint AS group_id,
       rg.data->>'Name' AS group_name, rp.data->>'Name' AS payment_mode,
       CASE WHEN r.is_return THEN -l.quantity ELSE l.quantity END AS quantity,
       CASE WHEN r.is_return THEN -l.total ELSE l.total END       AS amount,
       l.price, l.amount_discount
FROM shop_receipt_lines l
JOIN shop_receipts r ON r.station_id = l.station_id AND r.receipt_id = l.receipt_id
JOIN v_station_names sn ON sn.station_id = l.station_id
LEFT JOIN records ri ON ri.station_id = l.station_id AND ri.dataset = 'dcItems' AND ri.key = l.item_id::text
LEFT JOIN records rg ON rg.station_id = l.station_id AND rg.dataset = 'dcItemsGrps' AND rg.key = ri.data->>'ItemsGrpID'
LEFT JOIN records rp ON rp.station_id = r.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = r.payment_mode_id::text;

CREATE OR REPLACE VIEW v_item_keys AS
SELECT i.station_id, i.key::bigint AS item_id, i.data->>'Name' AS name, i.data->>'ItemCode' AS item_code,
       (i.data->>'ItemsGrpID')::bigint AS group_id, g.data->>'Name' AS group_name,
       COALESCE(NULLIF(trim(i.data->>'ItemCode'), ''), 'name:' || lower(regexp_replace(trim(i.data->>'Name'), '\s+', ' ', 'g'))) AS item_key
FROM records i
LEFT JOIN records g ON g.station_id = i.station_id AND g.dataset = 'dcItemsGrps' AND g.key = i.data->>'ItemsGrpID'
WHERE i.dataset = 'dcItems' AND NOT i.deleted;

-- Goods register lookups by station.
CREATE INDEX ix_records_station_dataset ON records (station_id, dataset);
