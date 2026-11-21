-- 1.23: purchasing for the shops (Склад → Закупка). The owner buys the goods himself (cash and carry) for all
-- stations at once: a shopping list for the next trip, quantities from the demand on days the item was in stock,
-- "bought" lines stay on order until Topaz's incoming bill (trInBillH/D) shows them.

-- Incoming bills of goods (Topaz "приходная накладная"): prices and quantities of past purchases, receipt of orders.
-- The supplier is always "Основной поставщик" on prod, so it is kept but not used.
CREATE TABLE purchase_bills (
    station_id  uuid        NOT NULL REFERENCES stations (id),
    bill_id     bigint      NOT NULL,   -- InBillHID
    doc_num     text,                   -- NDoc
    doc_at      timestamptz,            -- DateDoc
    partner_id  bigint,                 -- PartnerID
    storage_id  bigint,                 -- StorageID
    is_return   boolean,                -- IsReturn
    within_vat  boolean,                -- WithinNDS: line prices include VAT
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, bill_id)
);
CREATE TABLE purchase_bill_lines (
    station_id  uuid        NOT NULL REFERENCES stations (id),
    line_id     bigint      NOT NULL,   -- InBillDID
    bill_id     bigint,                 -- InBillHID
    item_id     bigint,                 -- ItemID
    item_unit_id bigint,                -- ItemUnitID
    quantity    numeric(15, 3),         -- Quantity
    price       numeric(15, 4),         -- Price
    total       numeric(15, 2),         -- Total
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, line_id)
);
CREATE INDEX ix_purchase_bill_lines_item ON purchase_bill_lines (station_id, item_id);
CREATE INDEX ix_purchase_bill_lines_bill ON purchase_bill_lines (station_id, bill_id);
SELECT app_enable_rls('purchase_bills', 'station_id = ANY ((SELECT app_stations())::uuid[])');
SELECT app_enable_rls('purchase_bill_lines', 'station_id = ANY ((SELECT app_stations())::uuid[])');

INSERT INTO purchase_bills (station_id, bill_id, doc_num, doc_at, partner_id, storage_id, is_return, within_vat)
SELECT station_id, (data->>'InBillHID')::bigint, data->>'NDoc', (data->>'DateDoc')::timestamptz, (data->>'PartnerID')::bigint,
       (data->>'StorageID')::bigint, (data->>'IsReturn')::int <> 0, (data->>'WithinNDS')::int <> 0
FROM records WHERE dataset = 'trInBillH' AND NOT deleted;
INSERT INTO purchase_bill_lines (station_id, line_id, bill_id, item_id, item_unit_id, quantity, price, total)
SELECT station_id, (data->>'InBillDID')::bigint, (data->>'InBillHID')::bigint, (data->>'ItemID')::bigint, (data->>'ItemUnitID')::bigint,
       (data->>'Quantity')::numeric, (data->>'Price')::numeric, (data->>'Total')::numeric
FROM records WHERE dataset = 'trInBillD' AND NOT deleted;

-- Trips of an organization: weekdays (ISO 1 = Monday … 7 = Sunday), days from the purchase to the shelf, service
-- levels by ABC class (the chance not to run out before the next trip), history the demand is taken from.
CREATE TABLE purchase_settings (
    organization_id uuid    PRIMARY KEY REFERENCES organizations (id) ON DELETE CASCADE,
    trip_weekdays   int[]   NOT NULL DEFAULT '{1,2,3,4,5,6}' CHECK (cardinality(trip_weekdays) BETWEEN 1 AND 7 AND trip_weekdays <@ '{1,2,3,4,5,6,7}'),
    lead_days       int     NOT NULL DEFAULT 0 CHECK (lead_days BETWEEN 0 AND 14),
    service_a       numeric NOT NULL DEFAULT 97 CHECK (service_a BETWEEN 50 AND 99.9),
    service_b       numeric NOT NULL DEFAULT 93 CHECK (service_b BETWEEN 50 AND 99.9),
    service_c       numeric NOT NULL DEFAULT 85 CHECK (service_c BETWEEN 50 AND 99.9),
    history_days    int     NOT NULL DEFAULT 56 CHECK (history_days BETWEEN 14 AND 180),
    updated_by      text,
    updated_at      timestamptz NOT NULL DEFAULT now()
);
SELECT app_enable_rls('purchase_settings', 'organization_id = (SELECT app_org())');

-- The owner's plan for an item at a station: 'auto' (by demand), 'core' (always in stock: at least one pack even
-- without recent sales), 'stop' (do not buy: sell out); optional minimum, maximum and pack size.
CREATE TABLE item_plan (
    station_id  uuid    NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    item_key    text    NOT NULL,
    mode        text    NOT NULL DEFAULT 'auto' CHECK (mode IN ('auto', 'core', 'stop')),
    min_qty     numeric CHECK (min_qty >= 0),
    max_qty     numeric CHECK (max_qty > 0),
    pack_qty    numeric CHECK (pack_qty > 0),
    note        text,
    updated_by  text,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, item_key),
    CHECK (max_qty IS NULL OR min_qty IS NULL OR max_qty >= min_qty)
);
SELECT app_enable_rls('item_plan', 'station_id = ANY ((SELECT app_stations())::uuid[])');

-- Shopping lists: a draft is edited, "bought" puts its lines on order until Topaz's incoming bills show them.
CREATE TABLE purchase_lists (
    id              bigserial   PRIMARY KEY,
    organization_id uuid        NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    trip_date       date        NOT NULL,
    status          text        NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'bought', 'closed')),
    note            text,
    created_by      text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    bought_at       timestamptz,
    closed_at       timestamptz
);
CREATE INDEX ix_purchase_lists_org ON purchase_lists (organization_id, created_at DESC);
SELECT app_enable_rls('purchase_lists', 'organization_id = (SELECT app_org())');

CREATE TABLE purchase_list_lines (
    list_id         bigint      NOT NULL REFERENCES purchase_lists (id) ON DELETE CASCADE,
    station_id      uuid        NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    item_id         bigint      NOT NULL,
    item_key        text        NOT NULL,
    name            text,
    group_name      text,
    suggested_qty   numeric     NOT NULL DEFAULT 0,
    qty             numeric     NOT NULL CHECK (qty >= 0),
    price           numeric,
    PRIMARY KEY (list_id, station_id, item_id)
);
CREATE INDEX ix_purchase_list_lines_item ON purchase_list_lines (station_id, item_id);
SELECT app_enable_rls('purchase_list_lines', 'station_id = ANY ((SELECT app_stations())::uuid[])');

-- Lines of bought lists with what Topaz has received since the purchase (bills dated from the purchase day on, at
-- the station's local date). Several open lists of one item share the receipts in the order they were bought.
CREATE VIEW v_purchase_on_order WITH (security_invoker = true) AS
WITH open_lines AS (
    SELECT l.list_id, l.station_id, l.item_id, l.qty, pl.bought_at,
           (pl.bought_at AT TIME ZONE sn.time_zone)::date AS bought_day,
           sum(l.qty) OVER (PARTITION BY l.station_id, l.item_id ORDER BY pl.bought_at, l.list_id) - l.qty AS qty_before
    FROM purchase_list_lines l
    JOIN purchase_lists pl ON pl.id = l.list_id AND pl.status = 'bought'
    JOIN v_station_names sn ON sn.station_id = l.station_id
    WHERE l.qty > 0),
first_open AS (SELECT station_id, item_id, min(bought_day) AS since FROM open_lines GROUP BY 1, 2),
received AS (
    SELECT f.station_id, f.item_id, COALESCE(sum(bl.quantity), 0) AS qty
    FROM first_open f
    JOIN v_station_names sn ON sn.station_id = f.station_id
    LEFT JOIN purchase_bill_lines bl ON bl.station_id = f.station_id AND bl.item_id = f.item_id
    LEFT JOIN purchase_bills b ON b.station_id = bl.station_id AND b.bill_id = bl.bill_id
    WHERE bl.line_id IS NULL OR (NOT COALESCE(b.is_return, false) AND (b.doc_at AT TIME ZONE sn.time_zone)::date >= f.since)
    GROUP BY 1, 2)
SELECT o.list_id, o.station_id, o.item_id, o.qty, o.bought_at,
       LEAST(o.qty, GREATEST(0, COALESCE(r.qty, 0) - o.qty_before)) AS received_qty,
       o.qty - LEAST(o.qty, GREATEST(0, COALESCE(r.qty, 0) - o.qty_before)) AS open_qty
FROM open_lines o LEFT JOIN received r ON r.station_id = o.station_id AND r.item_id = o.item_id;
