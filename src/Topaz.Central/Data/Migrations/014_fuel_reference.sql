-- Reference catalog of fuel types, editable in the UI, and confirmation of station fuel mappings.
-- A station fuel without a station_fuel_map row is "not confirmed": reports use the suggested type
-- (the type other stations confirmed for the same name, else a guess by name) until an owner confirms it.

ALTER TABLE fuel_types ADD COLUMN is_active boolean NOT NULL DEFAULT true;
CREATE UNIQUE INDEX fuel_types_name_uq ON fuel_types (lower(name));
CREATE SEQUENCE fuel_type_code_seq;

ALTER TABLE station_fuel_map
    ADD COLUMN confirmed_by text,
    ADD COLUMN confirmed_at timestamptz NOT NULL DEFAULT now();

-- Name key used to recognize the same fuel at different stations: "Аи-92 ", "АИ 92", "аи92" -> "аи92".
CREATE FUNCTION fuel_name_key(p_name text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT regexp_replace(replace(lower(coalesce(p_name, '')), 'ё', 'е'), '[^a-zа-я0-9]+', '', 'g')
$$;

-- What stations confirmed for each name key: the most common type wins (ties: catalog order).
CREATE VIEW v_fuel_learned AS
SELECT DISTINCT ON (x.name_key) x.name_key, x.fuel_type, x.stations, x.example_station_id
FROM (
    SELECT fuel_name_key(r.data->>'Name') AS name_key, m.fuel_type,
           count(DISTINCT m.station_id) AS stations, (array_agg(m.station_id ORDER BY m.confirmed_at))[1] AS example_station_id
    FROM station_fuel_map m
    JOIN records r ON r.station_id = m.station_id AND r.dataset = 'dcFuels' AND r.key = m.fuel_id::text AND NOT r.deleted
    GROUP BY 1, 2
) x
JOIN fuel_types ft ON ft.code = x.fuel_type
WHERE x.name_key <> ''
ORDER BY x.name_key, x.stations DESC, ft.sort;

-- Existing columns keep their positions (dependent views); "manual" now means "confirmed".
CREATE OR REPLACE VIEW v_station_fuels AS
SELECT r.station_id, r.key::bigint AS fuel_id, r.data->>'Name' AS fuel_name, COALESCE((r.data->>'Deleted')::int, 0) <> 0 AS is_deleted,
       COALESCE(m.fuel_type, l.fuel_type, guess_fuel_type(r.data->>'Name')) AS fuel_type, m.fuel_type IS NOT NULL AS manual,
       COALESCE(l.fuel_type, guess_fuel_type(r.data->>'Name')) AS suggested_type,
       CASE WHEN l.fuel_type IS NOT NULL THEN 'learned' WHEN guess_fuel_type(r.data->>'Name') <> 'OTHER' THEN 'name' ELSE 'none' END AS suggestion_source,
       l.example_station_id AS suggested_from,
       -- needs an owner's decision: a real (not deleted, not a "<...>" placeholder) fuel without a confirmed type
       m.fuel_type IS NULL AND COALESCE((r.data->>'Deleted')::int, 0) = 0 AND (r.data->>'Name') NOT LIKE '<%' AS needs_confirmation
FROM records r
LEFT JOIN station_fuel_map m ON m.station_id = r.station_id AND m.fuel_id = r.key::bigint
LEFT JOIN v_fuel_learned l ON l.name_key = fuel_name_key(r.data->>'Name')
WHERE r.dataset = 'dcFuels' AND NOT r.deleted;

INSERT INTO alert_rules (code, title, description, severity, threshold, unit, sort) VALUES
 ('fuel_unmapped', 'Топливо АЗС не сопоставлено со справочником',
  'У АЗС есть виды топлива без подтверждённого вида из справочника (Настройки → Виды топлива). До подтверждения отчёты используют предложенный вид',
  'info', NULL, NULL, 13);
