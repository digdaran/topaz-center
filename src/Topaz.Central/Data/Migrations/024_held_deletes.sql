-- 1.7.4: Topaz's database "свёртка" (TopazAZSDataUtility.exe) removes the documents of old shifts. The agent then sees
-- those rows vanish and sends deletes, which would erase the station's sales history here. Deletes of rows of a shift
-- closed more than Topaz:HoldDeletesAfterDays ago are not applied: they are kept in held_deletes and raise an alert.
-- A row that comes back (the database restored from a copy) leaves held_deletes again.
CREATE TABLE held_deletes (
    station_id   uuid        NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    dataset      text        NOT NULL,
    key          text        NOT NULL,
    session_id   bigint,
    raw_batch_id bigint      NOT NULL,
    held_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, dataset, key)
);
SELECT app_enable_rls('held_deletes', 'station_id = ANY ((SELECT app_stations())::uuid[])');

INSERT INTO alert_rules (code, title, description, severity, threshold, unit, sort) VALUES
 ('history_deleted', 'Из базы АЗС удалены данные старых смен',
  'В базе Топаза пропали наливы или чеки давно закрытых смен (обычно после свёртки базы). В сервисе история сохранена, удаление не применено',
  'critical', NULL, NULL, 21),
 ('payment_mode_twin', 'Скидка не применилась: вид оплаты-двойник',
  'В смене есть наливы без скидки по виду оплаты, у которого есть «двойник» с тем же названием и скидкой: оператор, вероятно, пробивает не тот вид оплаты',
  'warning', NULL, NULL, 22),
 ('hose_overflows', 'Частые переливы на рукаве',
  'За последние 7 дней по одному рукаву зарегистрировано переливов не меньше порога: стоит проверить ТРК (счётчики, дискретность, округление)',
  'warning', 5, 'раз', 23);

-- Held deletes per station and day they arrived (one history_deleted alert each), with the shifts they belong to.
CREATE VIEW v_held_delete_days WITH (security_invoker = true) AS
SELECT d.station_id, (d.held_at AT TIME ZONE 'UTC')::date AS day, count(*) AS rows, max(d.held_at) AS last_at,
       min(s.session_num) AS num_from, max(s.session_num) AS num_to, min(s.started_at) AS from_at, max(s.ended_at) AS to_at
FROM held_deletes d
LEFT JOIN shifts s ON s.station_id = d.station_id AND s.session_id = d.session_id
GROUP BY 1, 2;
