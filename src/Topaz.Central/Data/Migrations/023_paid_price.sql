-- 1.7.2: the price a customer actually paid per litre. Topaz keeps two prices on an order: Price is the list price
-- ("OrigPrice" in its own shift export) and PricePump is the price the pump sold at, after a payment mode's discount
-- (НАЛИЧКА 48 → 47); Amount = litres × PricePump. Screens showed Price, so a discounted sale looked undiscounted.

CREATE FUNCTION fuel_price_paid(price numeric, price_pump numeric) RETURNS numeric
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE WHEN price_pump > 0 THEN price_pump ELSE price END
$$;

-- Same view as in 003 plus two columns at the end: the paid price and the discount as everywhere else in reports
-- (fuel_discount). amount_discount stays Topaz's raw field for reconciliation.
CREATE OR REPLACE VIEW v_shift_fuel_outcomes WITH (security_invoker = true) AS
SELECT fs.station_id, fs.session_id, t.num AS tank_num, h.num AS hose_num, f.name AS fuel_name,
       pm.name AS payment_mode, fs.remark, a.name AS agent_name, fs.card_code,
       sum(fs.volume) AS volume, sum(fs.amount) AS amount, sum(fs.mass) AS mass, max(fs.price) AS price,
       count(*) AS order_count, sum(fs.amount_cash) AS amount_cash, sum(fs.amount_cashless) AS amount_cashless,
       sum(fs.amount_discount) AS amount_discount, sum(fs.amount_bonuses) AS amount_bonuses,
       sum(fs.amount) FILTER (WHERE fs.payment_kind = 'cash') AS amount_paid_cash,
       sum(fs.amount) FILTER (WHERE fs.payment_kind = 'cashless') AS amount_paid_cashless,
       max(fuel_price_paid(fs.price, fs.price_pump)) AS price_paid,
       sum(fuel_discount(fs.amount_discount, fs.price, fs.price_pump, fs.volume)) AS discount
FROM fuel_sales fs
LEFT JOIN v_tanks t ON t.station_id = fs.station_id AND t.tank_id = fs.tank_id
LEFT JOIN v_hoses h ON h.station_id = fs.station_id AND h.hose_id = fs.hose_id
LEFT JOIN v_fuels f ON f.station_id = fs.station_id AND f.fuel_id = fs.fuel_id
LEFT JOIN v_payment_modes pm ON pm.station_id = fs.station_id AND pm.payment_mode_id = fs.payment_mode_id
LEFT JOIN v_agents a ON a.station_id = fs.station_id AND a.agent_id = fs.agent_id
GROUP BY 1, 2, 3, 4, 5, 6, 7, 8, 9;
