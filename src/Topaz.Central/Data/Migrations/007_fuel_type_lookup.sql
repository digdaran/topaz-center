-- The fuel type guess (regexes) was evaluated for every sale row. It depends only on (station, fuel),
-- so compute it once per station fuel and hash-join the few resulting rows.

CREATE OR REPLACE VIEW v_station_fuels AS
SELECT r.station_id, r.key::bigint AS fuel_id, r.data->>'Name' AS fuel_name, COALESCE((r.data->>'Deleted')::int, 0) <> 0 AS is_deleted,
       COALESCE(m.fuel_type, guess_fuel_type(r.data->>'Name')) AS fuel_type, m.fuel_type IS NOT NULL AS manual
FROM records r LEFT JOIN station_fuel_map m ON m.station_id = r.station_id AND m.fuel_id = r.key::bigint
WHERE r.dataset = 'dcFuels' AND NOT r.deleted;

CREATE OR REPLACE VIEW v_fuel_sales_local AS
SELECT fs.*, sn.station, sn.time_zone,
       (fs.sold_at AT TIME ZONE sn.time_zone)                       AS local_ts,
       (fs.sold_at AT TIME ZONE sn.time_zone)::date                 AS local_date,
       extract(hour FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_hour,
       extract(isodow FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_dow,
       sf.fuel_name, rp.data->>'Name' AS payment_mode,
       (rt.data->>'Num')::int AS tank_num, (rh.data->>'Num')::int AS hose_num,
       (fs.volume > 0 AND fs.payment_kind <> 'none') AS is_sale,
       COALESCE(sf.fuel_type, 'OTHER') AS fuel_type
FROM fuel_sales fs
JOIN v_station_names sn ON sn.station_id = fs.station_id
LEFT JOIN v_station_fuels sf ON sf.station_id = fs.station_id AND sf.fuel_id = fs.fuel_id
LEFT JOIN records rp ON rp.station_id = fs.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = fs.payment_mode_id::text
LEFT JOIN records rt ON rt.station_id = fs.station_id AND rt.dataset = 'dcTanks' AND rt.key = fs.tank_id::text
LEFT JOIN records rh ON rh.station_id = fs.station_id AND rh.dataset = 'dcHoses' AND rh.key = fs.hose_id::text;
