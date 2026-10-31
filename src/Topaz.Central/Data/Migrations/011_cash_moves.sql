-- Money register (rgCashes) as a typed table for fast cash-flow reports; filled by the projection,
-- backfilled here.

CREATE TABLE cash_moves (
    station_id  uuid        NOT NULL REFERENCES stations (id),
    move_id     bigint      NOT NULL,   -- RgCashID
    session_id  bigint,                 -- SessionID
    moved_at    timestamptz,            -- Date
    doc_type_id int,                    -- DocTypeID (5 чек, 6 отпуск топлива, 14 внесение, 15 выдача, ...)
    doc_id      bigint,                 -- DocID
    cash_id     bigint,                 -- CashID
    amount      numeric(15, 2),         -- Amount (+ in, − out)
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, move_id)
);
CREATE INDEX ix_cash_moves_station_time ON cash_moves (station_id, moved_at);

INSERT INTO cash_moves (station_id, move_id, session_id, moved_at, doc_type_id, doc_id, cash_id, amount)
SELECT station_id, (data->>'RgCashID')::bigint, (data->>'SessionID')::bigint, (data->>'Date')::timestamptz, (data->>'DocTypeID')::int,
       (data->>'DocID')::bigint, (data->>'CashID')::bigint, (data->>'Amount')::numeric
FROM records WHERE dataset = 'rgCashes' AND NOT deleted;
