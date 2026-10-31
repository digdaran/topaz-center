-- Views reproducing the Topaz shift report ("экспорт смены", Type="Aggregated").
-- Verified against a real export of shift 80 (docs/topaz-schema.md, "Сверка с отчётами Топаза").

-- flOrders.Remark = 'Б' marks a cashless (bank terminal) payment; the payment mode name alone is
-- not enough (a "Терминал ..." order without 'Б' is booked as cash). AmountCash/AmountCashless hold
-- the prepaid amount before the per-litre discount; Amount is the final cost.
ALTER TABLE fuel_sales
    ADD COLUMN remark      text,             -- Remark
    ADD COLUMN price_pump  numeric(15, 2),   -- PricePump
    ADD COLUMN tax_str     text,             -- TaxStr
    ADD COLUMN payment_kind text GENERATED ALWAYS AS (
        CASE WHEN amount = 0 THEN 'none'
             WHEN amount_cashless > 0 AND amount_cash > 0 THEN 'mixed'
             WHEN amount_cashless > 0 THEN 'cashless'
             ELSE 'cash' END) STORED;

UPDATE fuel_sales f SET remark = r.data->>'Remark', price_pump = (r.data->>'PricePump')::numeric, tax_str = r.data->>'TaxStr'
FROM records r
WHERE r.dataset = 'flOrders' AND r.station_id = f.station_id AND r.key = f.order_id::text;

CREATE VIEW v_agents AS
SELECT station_id, (data->>'AgentID')::bigint AS agent_id, data->>'Name' AS name, (data->>'PartnerID')::bigint AS partner_id,
       data->>'Code' AS code, data->>'CarNumber' AS car_number, data->>'ExtCode' AS ext_code
FROM records WHERE dataset = 'dcAgents' AND NOT deleted;

CREATE VIEW v_partners AS
SELECT station_id, (data->>'PartnerID')::bigint AS partner_id, data->>'Name' AS name, data->>'FullName' AS full_name,
       data->>'Inn' AS inn, data->>'ExtCode' AS ext_code
FROM records WHERE dataset = 'dcPartners' AND NOT deleted;

CREATE VIEW v_cashes AS
SELECT station_id, (data->>'CashID')::bigint AS cash_id, data->>'Name' AS name
FROM records WHERE dataset = 'dcCashes' AND NOT deleted;

CREATE VIEW v_doc_types AS
SELECT station_id, (data->>'DocTypeID')::int AS doc_type_id, data->>'Name' AS name
FROM records WHERE dataset = 'sysDocTypes' AND NOT deleted;

-- = OutcomesByRetail: fuel sold per shift. Topaz groups payment modes by NAME (several ids can share
-- one name, e.g. "НАЛИЧКА" = 109 and 115) and splits by Remark; zero-volume orders are counted.
CREATE VIEW v_shift_fuel_outcomes AS
SELECT fs.station_id, fs.session_id, t.num AS tank_num, h.num AS hose_num, f.name AS fuel_name,
       pm.name AS payment_mode, fs.remark, a.name AS agent_name, fs.card_code,
       sum(fs.volume) AS volume, sum(fs.amount) AS amount, sum(fs.mass) AS mass, max(fs.price) AS price,
       count(*) AS order_count, sum(fs.amount_cash) AS amount_cash, sum(fs.amount_cashless) AS amount_cashless,
       sum(fs.amount_discount) AS amount_discount, sum(fs.amount_bonuses) AS amount_bonuses,
       sum(fs.amount) FILTER (WHERE fs.payment_kind = 'cash') AS amount_paid_cash,
       sum(fs.amount) FILTER (WHERE fs.payment_kind = 'cashless') AS amount_paid_cashless
FROM fuel_sales fs
LEFT JOIN v_tanks t ON t.station_id = fs.station_id AND t.tank_id = fs.tank_id
LEFT JOIN v_hoses h ON h.station_id = fs.station_id AND h.hose_id = fs.hose_id
LEFT JOIN v_fuels f ON f.station_id = fs.station_id AND f.fuel_id = fs.fuel_id
LEFT JOIN v_payment_modes pm ON pm.station_id = fs.station_id AND pm.payment_mode_id = fs.payment_mode_id
LEFT JOIN v_agents a ON a.station_id = fs.station_id AND a.agent_id = fs.agent_id
GROUP BY 1, 2, 3, 4, 5, 6, 7, 8, 9;

-- = CashFlow: cash drawer movements per shift from the money register rgCashes
-- (fuel and shop sales booked as cash, cash in/out). Cashless ('Б') sales do not appear here.
CREATE VIEW v_shift_cash_flow AS
SELECT r.station_id, (r.data->>'SessionID')::bigint AS session_id, (r.data->>'DocTypeID')::int AS doc_type_id,
       dt.name AS doc_type, (r.data->>'CashID')::bigint AS cash_id, c.name AS cash_name,
       count(*) AS operations, sum((r.data->>'Amount')::numeric) AS amount
FROM records r
LEFT JOIN v_doc_types dt ON dt.station_id = r.station_id AND dt.doc_type_id = (r.data->>'DocTypeID')::int
LEFT JOIN v_cashes c ON c.station_id = r.station_id AND c.cash_id = (r.data->>'CashID')::bigint
WHERE r.dataset = 'rgCashes' AND NOT r.deleted
GROUP BY 1, 2, 3, 4, 5, 6;

-- = ItemOutcomesByRetail: shop goods sold per shift. Amount = sum of line Total.
-- (The Topaz "ItemOutcomesByPaysheet.Price" is a SUM of line prices, not a price: do not use it.)
CREATE VIEW v_shift_item_sales AS
SELECT r.station_id, r.session_id, l.item_id, i.name AS item_name, pm.name AS payment_mode, p.name AS partner_name,
       r.is_return, sum(l.quantity) AS quantity, sum(l.total) AS amount, max(l.price) AS price, count(*) AS lines
FROM shop_receipts r
JOIN shop_receipt_lines l ON l.station_id = r.station_id AND l.receipt_id = r.receipt_id
LEFT JOIN v_items i ON i.station_id = r.station_id AND i.item_id = l.item_id
LEFT JOIN v_payment_modes pm ON pm.station_id = r.station_id AND pm.payment_mode_id = r.payment_mode_id
LEFT JOIN v_partners p ON p.station_id = r.station_id AND p.partner_id = r.partner_id
GROUP BY 1, 2, 3, 4, 5, 6, 7;

-- = ItemRests: goods on hand at a moment (the shift report uses the shift end time).
-- rgItemRests.Date is local station time with offset, stored by the agent as ISO text.
CREATE FUNCTION item_rests_at(p_station uuid, p_at timestamptz)
RETURNS TABLE (storage_id bigint, item_id bigint, item_name text, quantity numeric)
LANGUAGE sql STABLE AS $$
    SELECT (r.data->>'StorageID')::bigint, (r.data->>'ItemID')::bigint, i.name, sum((r.data->>'Quantity')::numeric)
    FROM records r
    LEFT JOIN v_items i ON i.station_id = r.station_id AND i.item_id = (r.data->>'ItemID')::bigint
    WHERE r.station_id = p_station AND r.dataset = 'rgItemRests' AND NOT r.deleted
      AND (r.data->>'Date')::timestamptz <= p_at
    GROUP BY 1, 2, 3
    HAVING sum((r.data->>'Quantity')::numeric) <> 0
$$;

-- Shift totals: one row per shift with the headline numbers of the Topaz shift report.
CREATE VIEW v_shift_totals AS
SELECT s.station_id, st.name AS station, s.session_id, s.session_num, s.user_name, s.started_at, s.ended_at,
       (SELECT sum(volume) FROM fuel_sales f WHERE f.station_id = s.station_id AND f.session_id = s.session_id) AS fuel_volume,
       (SELECT sum(amount) FROM fuel_sales f WHERE f.station_id = s.station_id AND f.session_id = s.session_id) AS fuel_amount,
       (SELECT sum(amount) FROM fuel_sales f WHERE f.station_id = s.station_id AND f.session_id = s.session_id AND f.payment_kind = 'cash') AS fuel_cash,
       (SELECT sum(amount) FROM fuel_sales f WHERE f.station_id = s.station_id AND f.session_id = s.session_id AND f.payment_kind = 'cashless') AS fuel_cashless,
       (SELECT sum(amount) FROM fuel_sales f WHERE f.station_id = s.station_id AND f.session_id = s.session_id AND f.payment_kind = 'mixed') AS fuel_mixed,
       (SELECT sum(l.total) FROM shop_receipts r JOIN shop_receipt_lines l ON l.station_id = r.station_id AND l.receipt_id = r.receipt_id
         WHERE r.station_id = s.station_id AND r.session_id = s.session_id) AS shop_amount,
       (SELECT sum(end_counter - start_counter) FROM hose_shift_counters h WHERE h.station_id = s.station_id AND h.session_id = s.session_id) AS hose_counters_delta
FROM shifts s JOIN stations st ON st.id = s.station_id;

-- v_fuel_sales expanded fs.* when created in 002; recreate it so it includes the new columns.
DROP VIEW v_fuel_sales;
CREATE VIEW v_fuel_sales AS
SELECT s.name AS station, fs.*, f.name AS fuel_name, pm.name AS payment_mode
FROM fuel_sales fs
JOIN stations s ON s.id = fs.station_id
LEFT JOIN v_fuels f ON f.station_id = fs.station_id AND f.fuel_id = fs.fuel_id
LEFT JOIN v_payment_modes pm ON pm.station_id = fs.station_id AND pm.payment_mode_id = fs.payment_mode_id;
