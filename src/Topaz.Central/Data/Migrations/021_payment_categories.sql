-- 1.7: payment categories — cash, bank card, contract (ведомости), fuel cards, unpaid — per payment mode of a station.
-- Topaz has no type for a payment mode (only its name), and the "Б" mark of a cashless sale is missing on some
-- stations (bank cards and contract sales recorded with AmountCash): the category comes from the name, the owner
-- can override it in Settings → Payment modes.

-- Lower case for Cyrillic regardless of the database locale.
CREATE FUNCTION lower_ru(s text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT lower(translate(s, 'АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ', 'абвгдеёжзийклмнопрстуфхцчшщъыьэюя'))
$$;

-- Category guessed from a payment mode name (first match wins: "НАЛИЧКА + Дисконтная карта" is cash,
-- "Банк. Терминал + Дисконтная карта" is a card, "Ведомость/Карты" is a contract).
CREATE FUNCTION payment_category_guess(name text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE
        WHEN lower_ru(COALESCE(name, '')) ~ '(прокачк|перелив|бесплатн)' THEN 'none'
        WHEN lower_ru(COALESCE(name, '')) ~ '(ведом|договор)' THEN 'contract'
        WHEN lower_ru(COALESCE(name, '')) ~ '(e1|е1|топливн)' THEN 'fuel_card'
        WHEN lower_ru(COALESCE(name, '')) ~ '(банк|терминал|сбп|эквайр|сбер)' THEN 'card'
        WHEN lower_ru(COALESCE(name, '')) ~ 'налич' THEN 'cash'
        WHEN lower_ru(COALESCE(name, '')) ~ 'карт' THEN 'card'
        ELSE 'cash'
    END
$$;

CREATE FUNCTION payment_discount_guess(name text) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$
    SELECT lower_ru(COALESCE(name, '')) ~ '(дисконт|скидк)'
$$;

-- Category of one sale: unpaid stays unpaid; a sale marked cashless by Topaz ("Б", AmountCashless) at a cash mode
-- was paid by card (a terminal sale recorded under "Наличные").
CREATE FUNCTION sale_pay_category(mode_category text, payment_kind text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE
        WHEN payment_kind = 'none' THEN 'none'
        WHEN mode_category = 'cash' AND payment_kind IN ('cashless', 'mixed') THEN 'card'
        ELSE mode_category
    END
$$;

-- Payment modes with their guesses, kept by a trigger on records: reports join this small table instead of
-- matching names for every row (regular expressions per sale row made the overview ~30x slower).
CREATE TABLE payment_modes (
    station_id      uuid    NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    payment_mode_id bigint  NOT NULL,
    name            text,
    category_guess  text    NOT NULL,
    discount_guess  boolean NOT NULL,
    PRIMARY KEY (station_id, payment_mode_id)
);
SELECT app_enable_rls('payment_modes', 'station_id = ANY ((SELECT app_stations())::uuid[])');

INSERT INTO payment_modes (station_id, payment_mode_id, name, category_guess, discount_guess)
SELECT station_id, key::bigint, data->>'Name', payment_category_guess(data->>'Name'), payment_discount_guess(data->>'Name')
FROM records WHERE dataset = 'dcPaymentModes' AND key ~ '^-?[0-9]+$';

CREATE FUNCTION payment_modes_sync() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.key ~ '^-?[0-9]+$' THEN
        INSERT INTO payment_modes (station_id, payment_mode_id, name, category_guess, discount_guess)
        VALUES (NEW.station_id, NEW.key::bigint, NEW.data->>'Name', payment_category_guess(NEW.data->>'Name'), payment_discount_guess(NEW.data->>'Name'))
        ON CONFLICT (station_id, payment_mode_id) DO UPDATE SET name = EXCLUDED.name, category_guess = EXCLUDED.category_guess,
            discount_guess = EXCLUDED.discount_guess;
    END IF;
    RETURN NULL;
END $$;
CREATE TRIGGER trg_payment_modes_sync AFTER INSERT OR UPDATE ON records
    FOR EACH ROW WHEN (NEW.dataset = 'dcPaymentModes') EXECUTE FUNCTION payment_modes_sync();

ALTER TABLE payment_mode_settings ALTER COLUMN is_discount DROP NOT NULL;
ALTER TABLE payment_mode_settings ADD COLUMN category text CHECK (category IN ('cash', 'card', 'contract', 'fuel_card', 'none'));

DROP VIEW v_payment_mode_flags;
CREATE VIEW v_payment_mode_flags WITH (security_invoker = true) AS
SELECT m.station_id, m.payment_mode_id, m.name,
       COALESCE(s.is_discount, m.discount_guess) AS is_discount, s.is_discount IS NOT NULL AS manual,
       COALESCE(s.category, m.category_guess) AS category, s.category IS NOT NULL AS category_manual
FROM payment_modes m
LEFT JOIN payment_mode_settings s ON s.station_id = m.station_id AND s.payment_mode_id = m.payment_mode_id;

-- Sales views and rollup views carry the sale's category (computed when read: an owner's change shows at once).
CREATE OR REPLACE VIEW v_fuel_sales_local WITH (security_invoker = true) AS
SELECT fs.*, sn.station, sn.time_zone,
       (fs.sold_at AT TIME ZONE sn.time_zone)                       AS local_ts,
       (fs.sold_at AT TIME ZONE sn.time_zone)::date                 AS local_date,
       extract(hour FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_hour,
       extract(isodow FROM fs.sold_at AT TIME ZONE sn.time_zone)::int AS local_dow,
       sf.fuel_name, rp.data->>'Name' AS payment_mode,
       (rt.data->>'Num')::int AS tank_num, (rh.data->>'Num')::int AS hose_num,
       (fs.volume > 0 AND fs.payment_kind <> 'none') AS is_sale,
       COALESCE(sf.fuel_type, 'OTHER') AS fuel_type,
       sale_pay_category(COALESCE(ps.category, pm.category_guess, 'cash'), fs.payment_kind) AS pay_category
FROM fuel_sales fs
JOIN v_station_names sn ON sn.station_id = fs.station_id
LEFT JOIN v_station_fuels sf ON sf.station_id = fs.station_id AND sf.fuel_id = fs.fuel_id
LEFT JOIN records rp ON rp.station_id = fs.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = fs.payment_mode_id::text
LEFT JOIN payment_modes pm ON pm.station_id = fs.station_id AND pm.payment_mode_id = fs.payment_mode_id
LEFT JOIN payment_mode_settings ps ON ps.station_id = fs.station_id AND ps.payment_mode_id = fs.payment_mode_id
LEFT JOIN records rt ON rt.station_id = fs.station_id AND rt.dataset = 'dcTanks' AND rt.key = fs.tank_id::text
LEFT JOIN records rh ON rh.station_id = fs.station_id AND rh.dataset = 'dcHoses' AND rh.key = fs.hose_id::text;

-- Shop: by the receipt's payment mode (the category of a mode; a receipt paid cashless at a cash mode is a card).
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
       l.price, l.amount_discount, r.fiscal_number,
       COALESCE(ps.category, pm.category_guess, 'cash') AS pay_category
FROM shop_receipt_lines l
JOIN shop_receipts r ON r.station_id = l.station_id AND r.receipt_id = l.receipt_id
JOIN v_station_names sn ON sn.station_id = l.station_id
LEFT JOIN records ri ON ri.station_id = l.station_id AND ri.dataset = 'dcItems' AND ri.key = l.item_id::text
LEFT JOIN records rg ON rg.station_id = l.station_id AND rg.dataset = 'dcItemsGrps' AND rg.key = ri.data->>'ItemsGrpID'
LEFT JOIN records rp ON rp.station_id = r.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = r.payment_mode_id::text
LEFT JOIN payment_modes pm ON pm.station_id = r.station_id AND pm.payment_mode_id = r.payment_mode_id
LEFT JOIN payment_mode_settings ps ON ps.station_id = r.station_id AND ps.payment_mode_id = r.payment_mode_id;

DROP VIEW v_agg_fuel;
CREATE VIEW v_agg_fuel WITH (security_invoker = true) AS
SELECT a.*, sn.station, sf.fuel_name, COALESCE(sf.fuel_type, 'OTHER') AS fuel_type, rp.data->>'Name' AS payment_mode,
       sale_pay_category(COALESCE(ps.category, pm.category_guess, 'cash'), a.payment_kind) AS pay_category
FROM agg_fuel_daily a
JOIN v_station_names sn ON sn.station_id = a.station_id
LEFT JOIN v_station_fuels sf ON sf.station_id = a.station_id AND sf.fuel_id = a.fuel_id
LEFT JOIN records rp ON rp.station_id = a.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = a.payment_mode_id::text
LEFT JOIN payment_modes pm ON pm.station_id = a.station_id AND pm.payment_mode_id = a.payment_mode_id
LEFT JOIN payment_mode_settings ps ON ps.station_id = a.station_id AND ps.payment_mode_id = a.payment_mode_id;

DROP VIEW v_agg_shop;
CREATE VIEW v_agg_shop WITH (security_invoker = true) AS
SELECT a.*, k.item_key, k.name AS item_name, k.group_name,
       COALESCE(ps.category, pm.category_guess, 'cash') AS pay_category
FROM agg_shop_daily a
LEFT JOIN v_item_keys k ON k.station_id = a.station_id AND k.item_id = a.item_id
LEFT JOIN payment_modes pm ON pm.station_id = a.station_id AND pm.payment_mode_id = a.payment_mode_id
LEFT JOIN payment_mode_settings ps ON ps.station_id = a.station_id AND ps.payment_mode_id = a.payment_mode_id;

-- VAT ('net' organizations) now also comes out of contract and fuel card sales: recompute.
INSERT INTO rollup_dirty (station_id, kind) SELECT id, 'all' FROM stations;
