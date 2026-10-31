-- Goods register (rgItemRests) as a typed table: stock = Σ quantity. Filled by the batch processor
-- projection like the other typed tables; backfilled here from records.

CREATE TABLE item_moves (
    station_id  uuid        NOT NULL REFERENCES stations (id),
    move_id     bigint      NOT NULL,   -- rgItemRestID
    item_id     bigint,                 -- ItemID
    storage_id  bigint,                 -- StorageID
    doc_type_id int,                    -- DocTypeID (1 приход, 3 ввод остатков, 5 чек, 19/20 акты ТМЦ, ...)
    doc_id      bigint,                 -- DocID
    moved_at    timestamptz,            -- Date
    quantity    numeric(15, 3),         -- Quantity (+ receipt, − sale/write-off)
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, move_id)
);
CREATE INDEX ix_item_moves_item ON item_moves (station_id, item_id);

INSERT INTO item_moves (station_id, move_id, item_id, storage_id, doc_type_id, doc_id, moved_at, quantity)
SELECT station_id, (data->>'rgItemRestID')::bigint, (data->>'ItemID')::bigint, (data->>'StorageID')::bigint,
       (data->>'DocTypeID')::int, (data->>'DocID')::bigint, (data->>'Date')::timestamptz, (data->>'Quantity')::numeric
FROM records WHERE dataset = 'rgItemRests' AND NOT deleted;

CREATE OR REPLACE VIEW v_item_moves AS
SELECT station_id, item_id, storage_id, doc_type_id, moved_at, quantity::numeric AS quantity FROM item_moves;

CREATE OR REPLACE FUNCTION item_rests_at(p_station uuid, p_at timestamptz)
RETURNS TABLE (storage_id bigint, item_id bigint, item_name text, quantity numeric)
LANGUAGE sql STABLE AS $$
    SELECT m.storage_id, m.item_id, k.name, sum(m.quantity)
    FROM item_moves m
    LEFT JOIN v_item_keys k ON k.station_id = m.station_id AND k.item_id = m.item_id
    WHERE m.station_id = p_station AND m.moved_at <= p_at
    GROUP BY 1, 2, 3
    HAVING sum(m.quantity) <> 0
$$;
