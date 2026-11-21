-- 1.22: how a sale was really paid comes from Topaz's own till (rgCashes → cash_moves) when the till knows the sale.
-- A card sale whose card was declined is paid in cash: Topaz keeps the card payment mode, drops the "Б" mark and books
-- the money into the till (DocTypeID 6, DocID = OrderID; shop: 5, DocID = FastTradeHID). A "Базовый" payment mode lets
-- the operator choose cash or card at payment time, so its name says nothing. Checked on prod (21.11.2026, 90 days,
-- 4 stations): every sale of a cash mode is in the till, no card/contract sale is, except the declined-card ones.
--
-- till of a sale: 'in' — the till has the sale's document; 'out' — the shift's till has records but not this sale;
-- 'unknown' — no till records for the shift (not sent yet, older data): the payment mode decides, as before.

CREATE INDEX ix_cash_moves_doc ON cash_moves (station_id, doc_type_id, doc_id);
CREATE INDEX ix_cash_moves_session ON cash_moves (station_id, session_id);

-- Category of one sale (see 021): unpaid stays unpaid; "Б" at a cash mode is a card; a non-cash mode paid into the
-- till is cash; a cash mode paid past a till that was kept is a card.
CREATE FUNCTION sale_pay_category(mode_category text, payment_kind text, till text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE
        WHEN payment_kind = 'none' THEN 'none'
        WHEN mode_category = 'cash' AND payment_kind IN ('cashless', 'mixed') THEN 'card'
        WHEN mode_category NOT IN ('cash', 'none') AND payment_kind = 'cash' AND till = 'in' THEN 'cash'
        WHEN mode_category = 'cash' AND payment_kind = 'cash' AND till = 'out' THEN 'card'
        ELSE mode_category
    END
$$;

DROP VIEW v_fuel_sales_local;
CREATE VIEW v_fuel_sales_local WITH (security_invoker = true) AS
SELECT fs.*, sn.station, sn.time_zone,
       (fs.sold_at AT TIME ZONE sn.time_zone)                       AS local_ts,
       (fs.sold_at AT TIME ZONE sn.time_zone)::date                 AS local_date,
       extract(hour FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_hour,
       extract(isodow FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_dow,
       sf.fuel_name, rp.data->>'Name' AS payment_mode,
       (rt.data->>'Num')::int AS tank_num, (rh.data->>'Num')::int AS hose_num,
       (fs.volume > 0 AND fs.payment_kind <> 'none') AS is_sale,
       COALESCE(sf.fuel_type, 'OTHER') AS fuel_type,
       sale_pay_category(COALESCE(ps.category, pm.category_guess, 'cash'), fs.payment_kind, t.till) AS pay_category,
       t.till
FROM fuel_sales fs
JOIN v_station_names sn ON sn.station_id = fs.station_id
-- the till state, written out here (as a SQL function it is not inlined: 8x slower over a station's history)
CROSS JOIN LATERAL (
    SELECT CASE WHEN EXISTS (SELECT 1 FROM cash_moves cm WHERE cm.station_id = fs.station_id AND cm.doc_type_id = 6
                               AND cm.doc_id = fs.order_id AND cm.amount <> 0) THEN 'in'
                WHEN EXISTS (SELECT 1 FROM cash_moves cm WHERE cm.station_id = fs.station_id AND cm.session_id = fs.session_id) THEN 'out'
                ELSE 'unknown' END AS till) t
LEFT JOIN v_station_fuels sf ON sf.station_id = fs.station_id AND sf.fuel_id = fs.fuel_id
LEFT JOIN records rp ON rp.station_id = fs.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = fs.payment_mode_id::text
LEFT JOIN payment_modes pm ON pm.station_id = fs.station_id AND pm.payment_mode_id = fs.payment_mode_id
LEFT JOIN payment_mode_settings ps ON ps.station_id = fs.station_id AND ps.payment_mode_id = fs.payment_mode_id
LEFT JOIN records rt ON rt.station_id = fs.station_id AND rt.dataset = 'dcTanks' AND rt.key = fs.tank_id::text
LEFT JOIN records rh ON rh.station_id = fs.station_id AND rh.dataset = 'dcHoses' AND rh.key = fs.hose_id::text;

-- Shop: Topaz leaves AmountCash/AmountCashless of receipts empty, so the till is the only evidence of how a receipt
-- was paid. payment_kind of a receipt follows fuel_sales (a cashless amount = card at a cash mode).
DROP VIEW v_shop_lines_local;
CREATE VIEW v_shop_lines_local WITH (security_invoker = true) AS
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
       l.price, l.amount_discount, r.fiscal_number,
       sale_pay_category(COALESCE(ps.category, pm.category_guess, 'cash'), t.payment_kind, t.till) AS pay_category,
       t.payment_kind, t.till
FROM shop_receipt_lines l
JOIN shop_receipts r ON r.station_id = l.station_id AND r.receipt_id = l.receipt_id
JOIN v_station_names sn ON sn.station_id = l.station_id
CROSS JOIN LATERAL (
    SELECT CASE WHEN EXISTS (SELECT 1 FROM cash_moves cm WHERE cm.station_id = r.station_id AND cm.doc_type_id = 5
                               AND cm.doc_id = r.receipt_id AND cm.amount <> 0) THEN 'in'
                WHEN EXISTS (SELECT 1 FROM cash_moves cm WHERE cm.station_id = r.station_id AND cm.session_id = r.session_id) THEN 'out'
                ELSE 'unknown' END AS till,
           CASE WHEN r.amount_cashless > 0 AND r.amount_cash > 0 THEN 'mixed'
                WHEN r.amount_cashless > 0 THEN 'cashless'
                ELSE 'cash' END AS payment_kind) t
LEFT JOIN records ri ON ri.station_id = l.station_id AND ri.dataset = 'dcItems' AND ri.key = l.item_id::text
LEFT JOIN records rg ON rg.station_id = l.station_id AND rg.dataset = 'dcItemsGrps' AND rg.key = ri.data->>'ItemsGrpID'
LEFT JOIN records rp ON rp.station_id = r.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = r.payment_mode_id::text
LEFT JOIN payment_modes pm ON pm.station_id = r.station_id AND pm.payment_mode_id = r.payment_mode_id
LEFT JOIN payment_mode_settings ps ON ps.station_id = r.station_id AND ps.payment_mode_id = r.payment_mode_id;

-- Rollups keep the evidence (payment kind, till) so the category is still computed when read (an owner's change of a
-- payment mode shows at once, see 021).
ALTER TABLE agg_fuel_daily ADD COLUMN till text NOT NULL DEFAULT 'unknown';
ALTER TABLE agg_fuel_daily DROP CONSTRAINT agg_fuel_daily_pkey;
ALTER TABLE agg_fuel_daily ADD PRIMARY KEY (station_id, local_date, fuel_id, payment_mode_id, payment_kind, till);

ALTER TABLE agg_shop_daily ADD COLUMN payment_kind text NOT NULL DEFAULT 'cash';
ALTER TABLE agg_shop_daily ADD COLUMN till text NOT NULL DEFAULT 'unknown';
ALTER TABLE agg_shop_daily DROP CONSTRAINT agg_shop_daily_pkey;
ALTER TABLE agg_shop_daily ADD PRIMARY KEY (station_id, local_date, item_id, payment_mode_id, payment_kind, till);

DROP VIEW v_agg_fuel;
CREATE VIEW v_agg_fuel WITH (security_invoker = true) AS
SELECT a.*, sn.station, sf.fuel_name, COALESCE(sf.fuel_type, 'OTHER') AS fuel_type, rp.data->>'Name' AS payment_mode,
       sale_pay_category(COALESCE(ps.category, pm.category_guess, 'cash'), a.payment_kind, a.till) AS pay_category
FROM agg_fuel_daily a
JOIN v_station_names sn ON sn.station_id = a.station_id
LEFT JOIN v_station_fuels sf ON sf.station_id = a.station_id AND sf.fuel_id = a.fuel_id
LEFT JOIN records rp ON rp.station_id = a.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = a.payment_mode_id::text
LEFT JOIN payment_modes pm ON pm.station_id = a.station_id AND pm.payment_mode_id = a.payment_mode_id
LEFT JOIN payment_mode_settings ps ON ps.station_id = a.station_id AND ps.payment_mode_id = a.payment_mode_id;

DROP VIEW v_agg_shop;
CREATE VIEW v_agg_shop WITH (security_invoker = true) AS
SELECT a.*, k.item_key, k.name AS item_name, k.group_name,
       sale_pay_category(COALESCE(ps.category, pm.category_guess, 'cash'), a.payment_kind, a.till) AS pay_category
FROM agg_shop_daily a
LEFT JOIN v_item_keys k ON k.station_id = a.station_id AND k.item_id = a.item_id
LEFT JOIN payment_modes pm ON pm.station_id = a.station_id AND pm.payment_mode_id = a.payment_mode_id
LEFT JOIN payment_mode_settings ps ON ps.station_id = a.station_id AND ps.payment_mode_id = a.payment_mode_id;

-- Categories (and the VAT base: contract and fuel card sales) can change: recompute.
INSERT INTO rollup_dirty (station_id, kind) SELECT id, 'all' FROM stations;
