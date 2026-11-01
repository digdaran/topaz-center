-- Station PC health from heartbeats (agent 1.2.1+): disk space, Topaz DB size, clock skew.

ALTER TABLE stations
    ADD COLUMN clock_skew_seconds numeric,   -- PC local wall clock minus true time in the station zone (+ = fast)
    ADD COLUMN host_checked_at    timestamptz;

-- One row per station and day (last value of the day): DB growth and disk trend.
CREATE TABLE station_host_daily (
    station_id    uuid   NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    day           date   NOT NULL,
    db_size       bigint,
    db_disk_free  bigint,
    db_disk_total bigint,
    sys_disk_free bigint,
    sys_disk_total bigint,
    PRIMARY KEY (station_id, day)
);

-- Disk trend over the last 14 days: DB growth per day and how fast free space shrinks.
CREATE VIEW v_station_disk AS
SELECT s.id AS station_id,
       (s.last_heartbeat->'host'->>'db_disk_free_bytes')::bigint  AS db_disk_free,
       (s.last_heartbeat->'host'->>'db_disk_total_bytes')::bigint AS db_disk_total,
       (s.last_heartbeat->'host'->>'db_file_bytes')::bigint       AS db_size,
       (s.last_heartbeat->'host'->>'system_disk_free_bytes')::bigint  AS sys_disk_free,
       (s.last_heartbeat->'host'->>'system_disk_total_bytes')::bigint AS sys_disk_total,
       g.db_growth_per_day, g.free_drop_per_day
FROM stations s
LEFT JOIN LATERAL (
    SELECT (max(d.db_size) FILTER (WHERE d.day = x.last_day) - max(d.db_size) FILTER (WHERE d.day = x.first_day))::numeric
               / NULLIF(x.last_day - x.first_day, 0) AS db_growth_per_day,
           (max(COALESCE(d.db_disk_free, d.sys_disk_free)) FILTER (WHERE d.day = x.first_day)
              - max(COALESCE(d.db_disk_free, d.sys_disk_free)) FILTER (WHERE d.day = x.last_day))::numeric
               / NULLIF(x.last_day - x.first_day, 0) AS free_drop_per_day
    FROM station_host_daily d,
         LATERAL (SELECT min(day) AS first_day, max(day) AS last_day FROM station_host_daily
                  WHERE station_id = s.id AND day >= current_date - 14) x
    WHERE d.station_id = s.id AND d.day IN (x.first_day, x.last_day)
    GROUP BY x.first_day, x.last_day
) g ON true;

INSERT INTO alert_rules (code, title, description, severity, threshold, unit, sort) VALUES
 ('disk_low', 'Мало места на диске АЗС',
  'Свободного места на диске с базой Топаза или на системном диске меньше порога: Топаз может остановиться', 'warning', 2, 'ГБ', 15),
 ('clock_skew', 'Часы ПК АЗС сбились',
  'Время на ПК АЗС (с учётом часового пояса Windows) отличается от точного больше порога: в отчётах будет неверное время операций', 'warning', 5, 'мин', 16);
