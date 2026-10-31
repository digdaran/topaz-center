-- Core ingestion schema: stations, raw batches (as received), generic current-state records and their history.

CREATE TABLE stations (
    id               uuid PRIMARY KEY,
    name             text        NOT NULL,
    name_normalized  text        NOT NULL UNIQUE,           -- lower(trim(name)); the station identity
    token_hash       bytea,                                 -- SHA-256 of the bearer token; token itself is never stored
    token_issued_at  timestamptz,
    status           text        NOT NULL DEFAULT 'active'
                     CHECK (status IN ('active', 'reenroll', 'revoked')),
    created_at       timestamptz NOT NULL DEFAULT now(),
    last_seen_at     timestamptz,
    last_ip          text,
    agent_version    text,
    machine_name     text,
    last_batch_seq   bigint,
    last_heartbeat   jsonb
);
CREATE UNIQUE INDEX ux_stations_token_hash ON stations (token_hash) WHERE token_hash IS NOT NULL;

-- Every accepted batch exactly as received. (station_id, batch_id) is the idempotency key.
CREATE TABLE raw_batches (
    id              bigserial   PRIMARY KEY,
    station_id      uuid        NOT NULL REFERENCES stations (id),
    batch_id        uuid        NOT NULL,
    seq             bigint      NOT NULL,
    schema_version  int         NOT NULL,
    agent_version   text,
    created_at      timestamptz NOT NULL,                   -- agent clock
    received_at     timestamptz NOT NULL DEFAULT now(),     -- server clock
    item_count      int         NOT NULL,
    payload_sha256  bytea       NOT NULL,
    payload         jsonb,                                  -- cleared after the retention period
    status          text        NOT NULL DEFAULT 'received'
                    CHECK (status IN ('received', 'processed', 'failed')),
    attempts        int         NOT NULL DEFAULT 0,
    error           text,
    processed_at    timestamptz,
    CONSTRAINT ux_raw_batches_idempotency UNIQUE (station_id, batch_id)
);
CREATE INDEX ix_raw_batches_unprocessed ON raw_batches (station_id, id) WHERE status <> 'processed';
CREATE INDEX ix_raw_batches_received_at ON raw_batches (received_at);

-- Current state of every source row of every station (any dataset, including future ones).
CREATE TABLE records (
    station_id     uuid        NOT NULL REFERENCES stations (id),
    dataset        text        NOT NULL,                    -- Topaz table name, e.g. flOrders
    key            text        NOT NULL,                    -- source primary key ('|' for composite)
    session_id     bigint,                                  -- Topaz SessionID (shift), when applicable
    hash           text,
    data           jsonb,                                   -- last known row (kept after delete)
    deleted        boolean     NOT NULL DEFAULT false,
    version        int         NOT NULL DEFAULT 1,
    first_seen_at  timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    last_batch_id  bigint      NOT NULL,
    PRIMARY KEY (station_id, dataset, key)
);
CREATE INDEX ix_records_dataset_updated ON records (dataset, updated_at);
CREATE INDEX ix_records_session ON records (station_id, dataset, session_id) WHERE session_id IS NOT NULL;

-- Audit trail: every change of every record (post-close corrections stay visible).
CREATE TABLE record_versions (
    id          bigserial   PRIMARY KEY,
    station_id  uuid        NOT NULL,
    dataset     text        NOT NULL,
    key         text        NOT NULL,
    version     int         NOT NULL,
    op          text        NOT NULL CHECK (op IN ('upsert', 'delete')),
    hash        text,
    data        jsonb,
    raw_batch_id bigint     NOT NULL,
    changed_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_record_versions_key ON record_versions (station_id, dataset, key, version);
CREATE INDEX ix_record_versions_changed ON record_versions (changed_at);
