-- Agent self-update: packages shipped inside the server image (signed in CI), staged rollout pilot -> others.

CREATE TABLE agent_releases (
    version       text        NOT NULL,
    rid           text        NOT NULL,
    file          text        NOT NULL,
    sha256        text        NOT NULL,
    size          bigint      NOT NULL,
    first_seen_at timestamptz NOT NULL DEFAULT now(), -- start of the pilot period
    present       boolean     NOT NULL DEFAULT true,  -- the files are in this image
    PRIMARY KEY (version, rid)
);

CREATE TABLE agent_update_settings (
    id                 int         PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    mode               text        NOT NULL DEFAULT 'auto' CHECK (mode IN ('auto', 'off')),
    paused             boolean     NOT NULL DEFAULT false,
    pilot_delay_hours  int         NOT NULL DEFAULT 24 CHECK (pilot_delay_hours BETWEEN 0 AND 720),
    release_now_version text,      -- "update all now": skip the pilot wait for this version
    updated_by         text,
    updated_at         timestamptz NOT NULL DEFAULT now()
);
INSERT INTO agent_update_settings DEFAULT VALUES;

ALTER TABLE stations
    ADD COLUMN agent_rid    text,
    ADD COLUMN update_group text    NOT NULL DEFAULT 'normal' CHECK (update_group IN ('pilot', 'normal', 'excluded')),
    ADD COLUMN update_now   boolean NOT NULL DEFAULT false;

-- Latest reported state per station and version (from heartbeats).
CREATE TABLE agent_update_reports (
    station_id uuid        NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    version    text        NOT NULL,
    status     text        NOT NULL CHECK (status IN ('downloading', 'installing', 'succeeded', 'failed')),
    error      text,
    at         timestamptz NOT NULL,
    PRIMARY KEY (station_id, version)
);

INSERT INTO alert_rules (code, title, description, severity, threshold, unit, sort) VALUES
 ('agent_update_failed', 'Обновление агента не удалось',
  'Агент не смог скачать, проверить или установить новую версию (при неудачной установке он сам вернулся к прежней)',
  'warning', NULL, NULL, 14);
