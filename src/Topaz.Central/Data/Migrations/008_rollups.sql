-- Daily rollups for long-period analytics (KPIs, day/week/month series, station comparison, ABC/XYZ).
-- One row per station × local day × fuel × payment mode (fuel) or × item × payment mode (shop):
-- ~20x fewer rows than raw sales. Kept current by Processing/RollupRefresher from rollup_dirty,
-- which the batch processor fills in the same transaction as the data change.

CREATE TABLE agg_fuel_daily (
    station_id      uuid    NOT NULL,
    local_date      date    NOT NULL,
    fuel_id         bigint  NOT NULL,
    payment_mode_id bigint  NOT NULL,
    payment_kind    text    NOT NULL,
    revenue         numeric NOT NULL,   -- Σ Amount
    liters          numeric NOT NULL,   -- Σ Volume of sales (volume > 0, paid)
    orders          int     NOT NULL,   -- sales count
    aborted         int     NOT NULL,   -- volume = 0
    service_liters  numeric NOT NULL,   -- unpaid dispensing (tech pumping, overflow)
    discounts       numeric NOT NULL,
    last_price      numeric,            -- price of the day's last sale
    last_sold_at    timestamptz,
    PRIMARY KEY (station_id, local_date, fuel_id, payment_mode_id, payment_kind)
);
CREATE INDEX ix_agg_fuel_daily_date ON agg_fuel_daily (local_date);

CREATE TABLE agg_shop_daily (
    station_id      uuid    NOT NULL,
    local_date      date    NOT NULL,
    item_id         bigint  NOT NULL,
    payment_mode_id bigint  NOT NULL,
    quantity        numeric NOT NULL,   -- returns negative
    amount          numeric NOT NULL,
    lines           int     NOT NULL,
    receipts        int     NOT NULL,   -- receipts containing the item
    PRIMARY KEY (station_id, local_date, item_id, payment_mode_id)
);
CREATE INDEX ix_agg_shop_daily_date ON agg_shop_daily (local_date);

-- Receipt counts cannot be summed from item rows (a receipt has several items).
CREATE TABLE agg_shop_receipts_daily (
    station_id uuid    NOT NULL,
    local_date date    NOT NULL,
    receipts   int     NOT NULL,
    amount     numeric NOT NULL,
    PRIMARY KEY (station_id, local_date)
);

-- Days to recompute: kind 'fuel' | 'shop' with a UTC day, or 'all' (whole station, e.g. time zone changed).
CREATE TABLE rollup_dirty (
    id         bigserial PRIMARY KEY,
    station_id uuid NOT NULL,
    kind       text NOT NULL CHECK (kind IN ('fuel', 'shop', 'all')),
    utc_date   date
);
INSERT INTO rollup_dirty (station_id, kind) SELECT id, 'all' FROM stations;

-- Fuel rollup with names and canonical type, for queries.
CREATE VIEW v_agg_fuel AS
SELECT a.*, sn.station, sf.fuel_name, COALESCE(sf.fuel_type, 'OTHER') AS fuel_type, rp.data->>'Name' AS payment_mode
FROM agg_fuel_daily a
JOIN v_station_names sn ON sn.station_id = a.station_id
LEFT JOIN v_station_fuels sf ON sf.station_id = a.station_id AND sf.fuel_id = a.fuel_id
LEFT JOIN records rp ON rp.station_id = a.station_id AND rp.dataset = 'dcPaymentModes' AND rp.key = a.payment_mode_id::text;

CREATE VIEW v_agg_shop AS
SELECT a.*, k.item_key, k.name AS item_name, k.group_name
FROM agg_shop_daily a
LEFT JOIN v_item_keys k ON k.station_id = a.station_id AND k.item_id = a.item_id;
