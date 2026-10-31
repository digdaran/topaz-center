-- Normalized (typed) tables for the main transactional data. Filled by the batch processor from
-- records (see Processing/Projections.cs). Column comments name the Topaz source column.

CREATE TABLE shifts (                                       -- sysSessions
    station_id    uuid        NOT NULL REFERENCES stations (id),
    session_id    bigint      NOT NULL,                     -- SessionID
    session_num   int,                                      -- SessionNum
    user_name     text,                                     -- UserName
    started_at    timestamptz,                              -- StartDateTime
    ended_at      timestamptz,                              -- EndDateTime (NULL = shift open)
    is_deleted    boolean,                                  -- Deleted
    updated_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, session_id)
);

CREATE TABLE fuel_sales (                                   -- flOrders: fuel dispensing orders
    station_id       uuid        NOT NULL REFERENCES stations (id),
    order_id         bigint      NOT NULL,                  -- OrderID
    session_id       bigint,                                -- SessionID
    order_num        int,                                   -- OrderNum
    sold_at          timestamptz,                           -- DateTime
    fuel_id          bigint,                                -- FuelID
    tank_id          bigint,                                -- TankID
    hose_id          bigint,                                -- HoseID
    payment_mode_id  bigint,                                -- PaymentModeID
    volume           numeric(15, 2),                        -- Volume, l
    volume_ordered   numeric(15, 2),                        -- VolumeOrdered
    price            numeric(15, 2),                        -- Price
    amount           numeric(15, 2),                        -- Amount
    amount_cash      numeric(15, 2),                        -- AmountCash
    amount_cashless  numeric(15, 2),                        -- AmountCashless
    amount_discount  numeric(15, 2),                        -- AmountDiscount
    amount_bonuses   numeric(15, 2),                        -- AmountBonuses
    mass             numeric(15, 2),                        -- Mass
    density          numeric(15, 4),                        -- Density
    temperature      numeric(15, 1),                        -- Temperature
    status_order     int,                                   -- StatusOrder
    order_source     int,                                   -- OrderSource
    card_type        int,                                   -- CardType
    card_code        text,                                  -- CardCode
    fiscal_number    int,                                   -- FiscalNumber
    driver_id        bigint,                                -- DriverID
    agent_id         bigint,                                -- AgentID
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, order_id)
);
CREATE INDEX ix_fuel_sales_sold_at ON fuel_sales (sold_at);
CREATE INDEX ix_fuel_sales_session ON fuel_sales (station_id, session_id);

CREATE TABLE tank_shift_states (                            -- flSesTanks: tank balance per shift
    station_id       uuid        NOT NULL REFERENCES stations (id),
    session_id       bigint      NOT NULL,                  -- SessionID
    tank_id          bigint      NOT NULL,                  -- TankID
    tank_num         int,                                   -- TankNum
    tank_name        text,                                  -- TankName
    fuel_name        text,                                  -- FuelName
    price            numeric(15, 2),                        -- Price
    start_volume     numeric(15, 2),                        -- StartFuelVolume
    receipt_volume   numeric(15, 2),                        -- ReceptVolume
    pump_volume      numeric(15, 2),                        -- PumpVolume
    fact_volume      numeric(15, 2),                        -- FactFuelVolume (measured at close)
    loss_volume      numeric(15, 2),                        -- LossVolume
    surplus_volume   numeric(15, 2),                        -- SurplusVolume
    start_mass       numeric(15, 2),                        -- StartMass
    mass             numeric(15, 2),                        -- Mass
    density          numeric(15, 4),                        -- Density
    temperature      numeric(15, 1),                        -- Temperature
    height           int,                                   -- Height
    water            int,                                   -- Water
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, session_id, tank_id)
);

CREATE TABLE hose_shift_counters (                          -- flSesHoses: dispenser totalizers per shift
    station_id    uuid        NOT NULL REFERENCES stations (id),
    session_id    bigint      NOT NULL,                     -- SessionID
    hose_id       bigint      NOT NULL,                     -- HoseID
    pump_num      int,                                      -- PumpNum
    num_in_pump   int,                                      -- NumInPump
    tank_num      int,                                      -- TankNum
    fuel_name     text,                                     -- FuelName
    start_counter numeric(15, 2),                           -- StartAdder
    end_counter   numeric(15, 2),                           -- EndAdder
    updated_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, session_id, hose_id)
);

CREATE TABLE fuel_receipts (                                -- flRecepts: fuel deliveries into tanks
    station_id       uuid        NOT NULL REFERENCES stations (id),
    receipt_id       bigint      NOT NULL,                  -- ReceptID
    session_id       bigint,                                -- SessionID
    received_at      timestamptz,                           -- ReceptDateTime
    tank_id          bigint,                                -- TankID
    fuel_id          bigint,                                -- FuelID
    partner_id       bigint,                                -- PartnerID
    doc_number       text,                                  -- DocNumber
    car_number       text,                                  -- DocCarNumber
    doc_volume       numeric(15, 2),                        -- DocVolume
    doc_mass         numeric(15, 2),                        -- DocMass
    doc_density      numeric(15, 4),                        -- DocDensity
    doc_temperature  numeric(15, 1),                        -- DocTemperature
    doc_price        numeric(15, 2),                        -- DocFuelPrice
    measured_volume  numeric(15, 2),                        -- MeasuredVolume
    discrepancy      numeric(15, 2),                        -- Discrepancy
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, receipt_id)
);

CREATE TABLE shop_receipts (                                -- trFastTradeH: shop (non-fuel) sale receipts
    station_id       uuid        NOT NULL REFERENCES stations (id),
    receipt_id       bigint      NOT NULL,                  -- FastTradeHID
    session_id       bigint,                                -- SessionID
    doc_num          int,                                   -- NDoc
    doc_at           timestamptz,                           -- DateDoc
    is_return        boolean,                               -- IsReturn
    cash_id          bigint,                                -- CashID
    storage_id       bigint,                                -- StorageID
    partner_id       bigint,                                -- PartnerID
    payment_mode_id  bigint,                                -- PaymentModeID
    amount_cash      numeric(15, 2),                        -- AmountCash
    amount_cashless  numeric(15, 2),                        -- AmountCashless
    amount_discount  numeric(15, 2),                        -- AmountDiscount
    amount_bonuses   numeric(15, 2),                        -- AmountBonuses
    status           int,                                   -- Status
    fiscal_number    int,                                   -- FiscalNumber
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, receipt_id)
);
CREATE INDEX ix_shop_receipts_doc_at ON shop_receipts (doc_at);

CREATE TABLE shop_receipt_lines (                           -- trFastTradeD
    station_id       uuid        NOT NULL REFERENCES stations (id),
    line_id          bigint      NOT NULL,                  -- FastTradeDID
    receipt_id       bigint,                                -- FastTradeHID
    item_id          bigint,                                -- ItemID
    item_unit_id     bigint,                                -- ItemUnitID
    quantity         numeric(15, 3),                        -- Quantity
    price            numeric(15, 2),                        -- Price
    amount           numeric(15, 2),                        -- Amount
    amount_discount  numeric(15, 2),                        -- AmountDiscount
    nds_amount       numeric(15, 2),                        -- NdsAmount
    total            numeric(15, 2),                        -- Total
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, line_id)
);
CREATE INDEX ix_shop_receipt_lines_receipt ON shop_receipt_lines (station_id, receipt_id);

CREATE TABLE station_events (                               -- sysEvents: Topaz event journal
    station_id     uuid        NOT NULL REFERENCES stations (id),
    event_id       bigint      NOT NULL,                    -- EventID
    occurred_at    timestamptz,                             -- DateTime
    category_id    bigint,                                  -- EventCategoryID
    message        text,                                    -- EventString
    computer_name  text,                                    -- ComputerName
    source         text,                                    -- EventSource
    object         text,                                    -- EventObject
    updated_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, event_id)
);
CREATE INDEX ix_station_events_occurred ON station_events (occurred_at);

-- Reference data straight from records (tiny tables; views keep them always in sync).
CREATE VIEW v_fuels AS
SELECT station_id, (data->>'FuelID')::bigint AS fuel_id, data->>'Name' AS name, data->>'FullName' AS full_name,
       (data->>'Price')::numeric(15,2) AS price, (data->>'Deleted')::int <> 0 AS is_deleted, data->>'ExtCode' AS ext_code
FROM records WHERE dataset = 'dcFuels' AND NOT deleted;

CREATE VIEW v_tanks AS
SELECT station_id, (data->>'TankID')::bigint AS tank_id, (data->>'Num')::int AS num, data->>'Name' AS name,
       (data->>'FuelID')::bigint AS fuel_id, (data->>'MaxVolume')::numeric(15,2) AS max_volume,
       (data->>'Deleted')::int <> 0 AS is_deleted
FROM records WHERE dataset = 'dcTanks' AND NOT deleted;

CREATE VIEW v_pumps AS
SELECT station_id, (data->>'PumpID')::bigint AS pump_id, (data->>'Num')::int AS num, (data->>'Deleted')::int <> 0 AS is_deleted
FROM records WHERE dataset = 'dcPumps' AND NOT deleted;

CREATE VIEW v_hoses AS
SELECT station_id, (data->>'HoseID')::bigint AS hose_id, (data->>'PumpID')::bigint AS pump_id, (data->>'TankID')::bigint AS tank_id,
       (data->>'Num')::int AS num, (data->>'NumInPump')::int AS num_in_pump, (data->>'Deleted')::int <> 0 AS is_deleted
FROM records WHERE dataset = 'dcHoses' AND NOT deleted;

CREATE VIEW v_payment_modes AS
SELECT station_id, (data->>'PaymentModeID')::bigint AS payment_mode_id, data->>'Name' AS name, data->>'ExtCode' AS ext_code
FROM records WHERE dataset = 'dcPaymentModes' AND NOT deleted;

CREATE VIEW v_items AS
SELECT station_id, (data->>'ItemID')::bigint AS item_id, (data->>'ItemsGrpID')::bigint AS group_id, data->>'Name' AS name,
       data->>'FullName' AS full_name, data->>'ItemCode' AS item_code, (data->>'IsService')::int <> 0 AS is_service
FROM records WHERE dataset = 'dcItems' AND NOT deleted;

CREATE VIEW v_storages AS
SELECT station_id, (data->>'StorageID')::bigint AS storage_id, data->>'Name' AS name
FROM records WHERE dataset = 'dcStorages' AND NOT deleted;

-- Goods on hand = sum of the goods register (rgItemRests) per storage and item.
CREATE VIEW v_item_stock AS
SELECT station_id, (data->>'StorageID')::bigint AS storage_id, (data->>'ItemID')::bigint AS item_id,
       sum((data->>'Quantity')::numeric(15,3)) AS quantity
FROM records WHERE dataset = 'rgItemRests' AND NOT deleted
GROUP BY 1, 2, 3;

-- Fuel sales enriched with names, for BI.
CREATE VIEW v_fuel_sales AS
SELECT s.name AS station, fs.*, f.name AS fuel_name, pm.name AS payment_mode
FROM fuel_sales fs
JOIN stations s ON s.id = fs.station_id
LEFT JOIN v_fuels f ON f.station_id = fs.station_id AND f.fuel_id = fs.fuel_id
LEFT JOIN v_payment_modes pm ON pm.station_id = fs.station_id AND pm.payment_mode_id = fs.payment_mode_id;

-- Station health at a glance.
CREATE VIEW v_station_status AS
SELECT s.id, s.name, s.status, s.agent_version, s.machine_name, s.last_ip, s.last_seen_at,
       now() - s.last_seen_at AS silent_for,
       (s.last_heartbeat->>'queue_pending')::int AS agent_queue_pending,
       (s.last_heartbeat->>'queue_dead')::int AS agent_queue_dead,
       (s.last_heartbeat->>'topaz_db_ok')::boolean AS topaz_db_ok,
       s.last_heartbeat->>'last_collect_error' AS last_collect_error,
       (SELECT count(*) FROM raw_batches b WHERE b.station_id = s.id AND b.status = 'received') AS server_backlog,
       (SELECT count(*) FROM raw_batches b WHERE b.station_id = s.id AND b.status = 'failed') AS server_failed
FROM stations s;
