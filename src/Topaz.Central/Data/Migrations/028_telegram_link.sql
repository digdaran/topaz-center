-- Telegram by link (1.10): a user opens t.me/<bot>?start=<code> (own chat) or ?startgroup=<code> (a group),
-- the bot receives "/start <code>" and creates the notification channel. No chat id to type in.

-- A channel with user_id is the user's own chat: it hears only about the stations this user can see.
ALTER TABLE notification_channels ADD COLUMN user_id uuid REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE notification_channels ADD CONSTRAINT notification_channels_user_key UNIQUE (user_id);

-- A group is added to an organization once (re-adding it by link updates the existing row).
DELETE FROM notification_channels a USING notification_channels b
WHERE a.user_id IS NULL AND b.user_id IS NULL AND a.target = b.target
  AND a.organization_id IS NOT DISTINCT FROM b.organization_id AND a.id > b.id;
CREATE UNIQUE INDEX ux_notification_channels_group ON notification_channels (organization_id, target) NULLS NOT DISTINCT WHERE user_id IS NULL;

-- One-time link codes, 15 minutes; only the SHA-256 of the code is kept.
CREATE TABLE telegram_link_codes (
    code_hash       text PRIMARY KEY,
    kind            text NOT NULL CHECK (kind IN ('personal', 'group')),
    user_id         uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,  -- who asked for the link
    organization_id uuid REFERENCES organizations (id) ON DELETE CASCADE,  -- NULL: superadmin's own chat or a platform group
    min_severity    text NOT NULL CHECK (min_severity IN ('critical', 'warning', 'info')),
    daily_digest    boolean NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    used_at         timestamptz
);
SELECT app_enable_rls('telegram_link_codes', 'organization_id = (SELECT app_org()) OR (SELECT app_platform())');

-- Where the bot stopped reading updates (getUpdates offset), so a restart does not replay old /start commands.
CREATE TABLE telegram_state (
    id            int PRIMARY KEY CHECK (id = 1),
    update_offset bigint NOT NULL DEFAULT 0
);
INSERT INTO telegram_state (id) VALUES (1);
SELECT app_enable_rls('telegram_state', '(SELECT app_platform())');
