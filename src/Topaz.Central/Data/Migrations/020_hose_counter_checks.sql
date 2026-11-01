-- Hose counter checks that survive a history upload and a totalizer rollover.

-- Litres between two counter readings. A totalizer wraps at a power of ten (999 999,99 → 0): a reading close to
-- that limit followed by a small one is a rollover, not minus a million litres.
CREATE FUNCTION counter_delta(from_counter numeric, to_counter numeric) RETURNS numeric LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE
        WHEN to_counter >= from_counter THEN to_counter - from_counter
        WHEN from_counter >= 1
             AND from_counter >= 0.9 * power(10::numeric, ceil(log(10::numeric, from_counter + 1)))
             AND to_counter < 0.1 * power(10::numeric, ceil(log(10::numeric, from_counter + 1)))
            THEN to_counter - from_counter + power(10::numeric, ceil(log(10::numeric, from_counter + 1)))
        ELSE to_counter - from_counter
    END
$$;

-- Counter at the start of a shift against the end of the shift right before it (by shift number). While a
-- station's history is still uploading the previous shift may be missing: then there is nothing to compare.
CREATE VIEW v_hose_counter_gaps WITH (security_invoker = true) AS
SELECT h.station_id, h.hose_id, h.session_id, s.session_num, s.started_at, h.start_counter, p.end_counter AS prev_end,
       counter_delta(p.end_counter, h.start_counter) AS gap
FROM hose_shift_counters h
JOIN shifts s ON s.station_id = h.station_id AND s.session_id = h.session_id
JOIN shifts ps ON ps.station_id = s.station_id AND ps.session_num = s.session_num - 1
JOIN hose_shift_counters p ON p.station_id = h.station_id AND p.session_id = ps.session_id AND p.hose_id = h.hose_id;

-- A closed shift: litres by the counter against litres by the orders of that hose.
CREATE VIEW v_hose_counter_mismatches WITH (security_invoker = true) AS
SELECT h.station_id, h.hose_id, h.session_id, s.session_num, s.started_at, s.ended_at,
       counter_delta(h.start_counter, h.end_counter) AS by_counter, COALESCE(o.liters, 0) AS by_orders
FROM hose_shift_counters h
JOIN shifts s ON s.station_id = h.station_id AND s.session_id = h.session_id AND s.ended_at IS NOT NULL
LEFT JOIN LATERAL (SELECT sum(f.volume) AS liters FROM fuel_sales f
                   WHERE f.station_id = h.station_id AND f.session_id = h.session_id AND f.hose_id = h.hose_id) o ON true;

CREATE INDEX IF NOT EXISTS ix_shifts_num ON shifts (station_id, session_num);
