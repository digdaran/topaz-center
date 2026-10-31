-- Purchase costs (entered in the web UI: Topaz has none), margins in the daily rollups, sales plans.

-- Fuel purchase price per litre for a station fuel, effective from a moment (typically a delivery).
CREATE TABLE fuel_costs (
    id              bigserial   PRIMARY KEY,
    station_id      uuid        NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    fuel_id         bigint      NOT NULL,
    valid_from      timestamptz NOT NULL,
    price_per_liter numeric(15, 4) NOT NULL CHECK (price_per_liter >= 0),
    receipt_id      bigint,                 -- fuel delivery (flRecepts) the price belongs to, if any
    comment         text,
    created_by      text,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_fuel_costs_lookup ON fuel_costs (station_id, fuel_id, valid_from DESC);

-- Goods purchase cost per unit by item key (barcode or name), for one station or all (station_id NULL).
CREATE TABLE item_costs (
    id         bigserial   PRIMARY KEY,
    station_id uuid        REFERENCES stations (id) ON DELETE CASCADE,
    item_key   text        NOT NULL,
    valid_from timestamptz NOT NULL,
    cost       numeric(15, 4) NOT NULL CHECK (cost >= 0),
    comment    text,
    created_by text,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_item_costs_lookup ON item_costs (item_key, valid_from DESC);

-- Monthly plans per station: metric fuel_liters (optionally per fuel type), fuel_revenue, shop_revenue.
CREATE TABLE sales_plans (
    station_id uuid    NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    month      date    NOT NULL CHECK (extract(day FROM month) = 1),
    metric     text    NOT NULL CHECK (metric IN ('fuel_liters', 'fuel_revenue', 'shop_revenue')),
    fuel_type  text    NOT NULL DEFAULT '',  -- '' = all fuels
    value      numeric NOT NULL CHECK (value >= 0),
    updated_by text,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, month, metric, fuel_type)
);

-- Cost of what was sold, computed when a day is (re)built. NULL when no cost was known.
ALTER TABLE agg_fuel_daily
    ADD COLUMN cost_amount     numeric,   -- Σ litres × purchase price, for sales with a known price
    ADD COLUMN costed_liters   numeric NOT NULL DEFAULT 0,
    ADD COLUMN costed_revenue  numeric NOT NULL DEFAULT 0,
    ADD COLUMN service_cost    numeric;   -- purchase value of unpaid dispensing (tech pumping, overflow)
ALTER TABLE agg_shop_daily
    ADD COLUMN cost_amount    numeric,
    ADD COLUMN costed_amount  numeric NOT NULL DEFAULT 0;

-- Recreate the rollup views so they expose the new columns.
DROP VIEW v_agg_fuel;
CREATE VIEW v_agg_fuel AS
SELECT a.*, sn.station, sf.fuel_name, COALESCE(sf.fuel_type, 'OTHER') AS fuel_type, rp.data->>'Name' AS payment_mode
FROM agg_fuel_daily a
JOIN v_station_names sn ON sn.station_id = a.station_id
LEFT JOIN v_station_fuels sf ON sf.station_id = a.station_id AND sf.fuel_id = a.fuel_id
LEFT JOIN records rp ON rp.station_id = a.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = a.payment_mode_id::text;

DROP VIEW v_agg_shop;
CREATE VIEW v_agg_shop AS
SELECT a.*, k.item_key, k.name AS item_name, k.group_name
FROM agg_shop_daily a
LEFT JOIN v_item_keys k ON k.station_id = a.station_id AND k.item_id = a.item_id;
