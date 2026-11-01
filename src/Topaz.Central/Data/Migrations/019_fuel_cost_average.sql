-- 1.6: fuel cost by moving average per tank, VAT mode per organization, discounts by card.

-- ---------------------------------------------------------------- discounts
-- A fuel sale's discount: Topaz's own discount field, else the gap between the order price and the
-- (lower) pump price that the customer actually paid (stations where a payment mode gives the discount).
CREATE FUNCTION fuel_discount(amount_discount numeric, price numeric, price_pump numeric, volume numeric) RETURNS numeric
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE WHEN COALESCE(amount_discount, 0) <> 0 THEN amount_discount
                WHEN price_pump > 0 AND price > price_pump AND volume > 0 THEN round(volume * (price - price_pump), 2)
                ELSE 0 END
$$;

-- Payment modes that give a discount: marked by the owner, else guessed from the name ("Дисконтная…", "СКИДКА").
CREATE TABLE payment_mode_settings (
    station_id      uuid    NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    payment_mode_id bigint  NOT NULL,
    is_discount     boolean NOT NULL,
    updated_by      text,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, payment_mode_id)
);
SELECT app_enable_rls('payment_mode_settings', 'station_id = ANY ((SELECT app_stations())::uuid[])');

-- Name match without relying on the database locale for Cyrillic case folding.
CREATE VIEW v_payment_mode_flags WITH (security_invoker = true) AS
SELECT m.station_id, m.payment_mode_id, m.name,
       COALESCE(s.is_discount, COALESCE(m.name, '') ~ '([Дд][Ии][Сс][Кк][Оо][Нн][Тт]|[Сс][Кк][Ии][Дд][Кк])') AS is_discount,
       s.is_discount IS NOT NULL AS manual
FROM v_payment_modes m
LEFT JOIN payment_mode_settings s ON s.station_id = m.station_id AND s.payment_mode_id = m.payment_mode_id;

-- Old "order price ≠ pump price" alerts were card discounts; the rule now finds only real mismatches.
DELETE FROM alerts WHERE rule_code = 'price_mismatch' AND status <> 'closed';
UPDATE alert_rules SET title = 'Цена отпуска не сходится',
       description = 'Клиент заплатил больше цены заказа, сумма не равна «литры × цена колонки» или цену снизили мимо скидок Топаза при недисконтном виде оплаты'
WHERE code = 'price_mismatch';

-- ---------------------------------------------------------------- VAT
-- Margin with VAT (as Topaz revenue) or without: VAT at the rate is taken out of sales with a receipt only.
ALTER TABLE organizations
    ADD COLUMN margin_vat text NOT NULL DEFAULT 'gross' CHECK (margin_vat IN ('gross', 'net')),
    ADD COLUMN vat_rate numeric(5, 2) NOT NULL DEFAULT 22 CHECK (vat_rate >= 0 AND vat_rate < 100);

ALTER TABLE agg_fuel_daily
    ADD COLUMN fiscal_revenue numeric NOT NULL DEFAULT 0,  -- revenue of sales with a fiscal receipt
    ADD COLUMN costed_vat     numeric NOT NULL DEFAULT 0;  -- VAT inside costed_revenue ('net' organizations)
ALTER TABLE agg_shop_daily
    ADD COLUMN fiscal_amount  numeric NOT NULL DEFAULT 0,
    ADD COLUMN costed_vat     numeric NOT NULL DEFAULT 0;

DROP VIEW v_agg_fuel;
CREATE VIEW v_agg_fuel WITH (security_invoker = true) AS
SELECT a.*, sn.station, sf.fuel_name, COALESCE(sf.fuel_type, 'OTHER') AS fuel_type, rp.data->>'Name' AS payment_mode
FROM agg_fuel_daily a
JOIN v_station_names sn ON sn.station_id = a.station_id
LEFT JOIN v_station_fuels sf ON sf.station_id = a.station_id AND sf.fuel_id = a.fuel_id
LEFT JOIN records rp ON rp.station_id = a.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = a.payment_mode_id::text;

DROP VIEW v_agg_shop;
CREATE VIEW v_agg_shop WITH (security_invoker = true) AS
SELECT a.*, k.item_key, k.name AS item_name, k.group_name
FROM agg_shop_daily a
LEFT JOIN v_item_keys k ON k.station_id = a.station_id AND k.item_id = a.item_id;

CREATE OR REPLACE VIEW v_shop_lines_local WITH (security_invoker = true) AS
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
       l.price, l.amount_discount, r.fiscal_number
FROM shop_receipt_lines l
JOIN shop_receipts r ON r.station_id = l.station_id AND r.receipt_id = l.receipt_id
JOIN v_station_names sn ON sn.station_id = l.station_id
LEFT JOIN records ri ON ri.station_id = l.station_id AND ri.dataset = 'dcItems' AND ri.key = l.item_id::text
LEFT JOIN records rg ON rg.station_id = l.station_id AND rg.dataset = 'dcItemsGrps' AND rg.key = ri.data->>'ItemsGrpID'
LEFT JOIN records rp ON rp.station_id = r.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = r.payment_mode_id::text;

-- VAT rate taken out of a station's margins: 0 for organizations that count margin with VAT.
CREATE FUNCTION station_margin_vat_rate(station uuid) RETURNS numeric LANGUAGE sql STABLE AS $$
    SELECT COALESCE((SELECT CASE WHEN o.margin_vat = 'net' THEN o.vat_rate ELSE 0 END
                     FROM stations s JOIN organizations o ON o.id = s.organization_id WHERE s.id = station), 0)
$$;

-- ---------------------------------------------------------------- fuel cost: moving average per tank
-- Derived from fuel receipts, their prices (fuel_costs.receipt_id) and "stock price on a date"
-- (fuel_costs without receipt). Rebuilt by Processing/FuelCostAverager before the fuel rollups.
CREATE TABLE fuel_cost_avg (
    station_id    uuid        NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    tank_id       bigint      NOT NULL,
    valid_from    timestamptz NOT NULL,
    seq           int         NOT NULL,         -- order of events at the same moment
    fuel_id       bigint      NOT NULL,
    avg_price     numeric(15, 4) NOT NULL,      -- cost of a litre in the tank from valid_from on
    source        text        NOT NULL CHECK (source IN ('receipt', 'receipt_unpriced', 'stock_price')),
    receipt_id    bigint,
    stock_before  numeric(15, 2),               -- book stock of the tank just before the receipt
    liters        numeric(15, 2),
    receipt_price numeric(15, 4),
    PRIMARY KEY (station_id, tank_id, valid_from, seq)
);
CREATE INDEX ix_fuel_cost_avg_fuel ON fuel_cost_avg (station_id, fuel_id, valid_from DESC);
SELECT app_enable_rls('fuel_cost_avg', 'station_id = ANY ((SELECT app_stations())::uuid[])');

INSERT INTO alert_rules (code, title, description, severity, threshold, unit, sort) VALUES
 ('receipt_without_cost', 'Не введена цена слива',
  'Слив топлива за последние 30 дней без закупочной цены дольше порога. Пока цены нет, литры слива считаются по текущей средней цене резервуара',
  'warning', 24, 'ч', 18);

-- Everything is recomputed with the new cost method and discounts.
INSERT INTO rollup_dirty (station_id, kind) SELECT id, 'all' FROM stations;
