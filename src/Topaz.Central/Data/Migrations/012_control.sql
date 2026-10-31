-- Stage 4: fuel stock planning, anomaly rules and found alerts, notification channels.

-- Per-tank planning settings (Topaz has the capacity and dead rest, not the ordering policy).
CREATE TABLE tank_settings (
    station_id      uuid    NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    tank_id         bigint  NOT NULL,
    min_liters      numeric NOT NULL DEFAULT 0 CHECK (min_liters >= 0),   -- order before the stock falls below this
    lead_time_days  numeric NOT NULL DEFAULT 2 CHECK (lead_time_days >= 0),
    safety_days     numeric NOT NULL DEFAULT 1 CHECK (safety_days >= 0),
    updated_by      text,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, tank_id)
);

-- Anomaly rules with editable thresholds. severity: critical | warning | info.
CREATE TABLE alert_rules (
    code        text    PRIMARY KEY,
    title       text    NOT NULL,
    description text    NOT NULL,
    severity    text    NOT NULL CHECK (severity IN ('critical', 'warning', 'info')),
    threshold   numeric,
    unit        text,
    enabled     boolean NOT NULL DEFAULT true,
    sort        int     NOT NULL
);
INSERT INTO alert_rules (code, title, description, severity, threshold, unit, sort) VALUES
 ('counter_gap', 'Разрыв счётчика ТРК между сменами', 'Счётчик рукава на начало смены больше, чем на конец предыдущей: топливо отпущено между сменами, мимо учёта', 'critical', 0.5, 'л', 1),
 ('counter_mismatch', 'Счётчик ТРК не совпадает с заказами', 'Разница счётчика рукава за смену отличается от суммы отпусков по заказам', 'warning', 1, 'л', 2),
 ('aborted_share', 'Много прерванных наливов', 'Доля заказов с нулевым объёмом в смене выше порога (не меньше 5 заказов)', 'warning', 8, '%', 3),
 ('service_volume', 'Большой отпуск без оплаты', 'Тех. прокачки и переливы за смену больше порога', 'warning', 50, 'л', 4),
 ('discount_share', 'Много скидок', 'Скидки за смену больше порога от выручки топлива', 'info', 3, '%', 5),
 ('price_mismatch', 'Цена заказа отличается от цены ТРК', 'В смене есть отпуски, где цена заказа не равна цене на колонке', 'warning', 0.01, '₽', 6),
 ('receipt_discrepancy', 'Недостача при приёме топлива', 'Фактически принято меньше, чем по документу, больше порога', 'warning', 0.5, '%', 7),
 ('shift_too_long', 'Смена не закрыта', 'Смена открыта дольше порога', 'warning', 30, 'ч', 8),
 ('low_fuel', 'Топливо заканчивается', 'Запаса в резервуаре меньше, чем на срок поставки плюс страховой запас (или ниже минимума)', 'critical', NULL, NULL, 9),
 ('goods_out', 'Товар закончился', 'Товар продавался последние 28 дней, а остаток нулевой', 'info', NULL, NULL, 10),
 ('station_offline', 'АЗС не на связи', 'Агент не выходил на связь дольше порога', 'critical', 15, 'мин', 11),
 ('data_rejected', 'Данные не принимаются', 'Есть пакеты, которые сервер отклонил или не смог обработать', 'critical', NULL, NULL, 12);

CREATE TABLE alerts (
    id          bigserial   PRIMARY KEY,
    station_id  uuid        NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    rule_code   text        NOT NULL REFERENCES alert_rules (code),
    dedupe_key  text        NOT NULL,           -- one alert per rule, station and object (shift, hose, day, ...)
    severity    text        NOT NULL,
    title       text        NOT NULL,
    details     text,
    value       numeric,
    session_id  bigint,                         -- link to the shift card when relevant
    occurred_at timestamptz,
    detected_at timestamptz NOT NULL DEFAULT now(),
    status      text        NOT NULL DEFAULT 'new' CHECK (status IN ('new', 'seen', 'closed')),
    status_by   text,
    notified_at timestamptz,
    UNIQUE (rule_code, station_id, dedupe_key)
);
CREATE INDEX ix_alerts_station_detected ON alerts (station_id, detected_at DESC);
CREATE INDEX ix_alerts_unnotified ON alerts (id) WHERE notified_at IS NULL;

CREATE TABLE notification_channels (
    id           bigserial PRIMARY KEY,
    kind         text    NOT NULL DEFAULT 'telegram' CHECK (kind IN ('telegram')),
    target       text    NOT NULL,              -- Telegram chat id
    title        text,
    min_severity text    NOT NULL DEFAULT 'warning' CHECK (min_severity IN ('critical', 'warning', 'info')),
    daily_digest boolean NOT NULL DEFAULT true,
    enabled      boolean NOT NULL DEFAULT true,
    last_digest  date,
    created_at   timestamptz NOT NULL DEFAULT now()
);

-- Current book stock of every tank: the latest shift's start + receipts − dispensing
-- (the open shift's values are kept current by the agent every minute).
CREATE VIEW v_tank_book_stock AS
SELECT DISTINCT ON (t.station_id, t.tank_id)
       t.station_id, t.tank_id, t.session_id, t.tank_num, t.fuel_name, t.price,
       t.start_volume + t.receipt_volume - t.pump_volume AS book_volume,
       t.updated_at, s.started_at AS session_started_at, s.ended_at AS session_ended_at
FROM tank_shift_states t
JOIN shifts s ON s.station_id = t.station_id AND s.session_id = t.session_id
ORDER BY t.station_id, t.tank_id, t.session_id DESC;
