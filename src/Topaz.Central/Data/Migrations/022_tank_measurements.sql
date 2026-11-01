-- 1.7: tank level gauges. Where a tank has an electronic gauge linked in Topaz (dcTanks.LevelGauge), Topaz stores the
-- measured stock at every shift close (flSesTanks.FactFuelVolume, height, density, temperature) and starts the next shift
-- from it; tanks without a gauge are "virtual" — book stock only (start + receipts − dispensing).

-- Gauge per tank (from the Topaz tank reference).
CREATE VIEW v_tank_gauges WITH (security_invoker = true) AS
SELECT station_id, key::bigint AS tank_id, (data->>'Num')::int AS tank_num,
       COALESCE(NULLIF(trim(data->>'LevelGauge'), ''), NULL) IS NOT NULL AS has_gauge,
       COALESCE((data->>'IsNonDimensional')::int, 0) <> 0 AS non_dimensional
FROM records WHERE dataset = 'dcTanks' AND NOT deleted AND key ~ '^[0-9]+$';

-- Book against measurement at each shift close where the stock was measured (by a gauge or entered by hand).
-- diff < 0: less fuel than the book says (shortage); diff > 0: surplus.
CREATE VIEW v_tank_measurements WITH (security_invoker = true) AS
SELECT t.station_id, t.tank_id, t.tank_num, t.fuel_name, t.session_id, s.session_num, s.started_at, s.ended_at,
       t.start_volume + t.receipt_volume - t.pump_volume AS book_volume, t.fact_volume,
       t.fact_volume - (t.start_volume + t.receipt_volume - t.pump_volume) AS diff,
       t.pump_volume, t.receipt_volume, t.density, t.temperature, t.height, t.water
FROM tank_shift_states t
JOIN shifts s ON s.station_id = t.station_id AND s.session_id = t.session_id
WHERE s.ended_at IS NOT NULL AND t.fact_volume IS NOT NULL;

INSERT INTO alert_rules (code, title, description, severity, threshold, unit, sort) VALUES
 ('tank_shortage', 'Недостача в резервуаре по замеру',
  'При закрытии смены замер уровнемера (или введённый вручную) показал меньше топлива, чем по учёту Топаза, больше чем на порог',
  'warning', 20, 'л', 19),
 ('tank_surplus', 'Излишек в резервуаре по замеру',
  'При закрытии смены замер показал больше топлива, чем по учёту, больше чем на порог: чаще всего это слив, не оформленный в Топазе приходом',
  'warning', 200, 'л', 20);
