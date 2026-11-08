-- Two-factor login (1.8): TOTP from an authenticator app, mandatory for superadmins (Topaz__RequireTotpForSuperadmin).
-- totp_secret / totp_pending are encrypted with the data protection keys (volume "keys"); recovery codes are SHA-256.
ALTER TABLE users
    ADD COLUMN totp_secret     text,         -- active secret; NULL = two-factor login off
    ADD COLUMN totp_pending    text,         -- secret being set up, until the first code confirms it
    ADD COLUMN totp_last_step  bigint,       -- last accepted 30-second step: a code works only once
    ADD COLUMN totp_enabled_at timestamptz,
    ADD COLUMN recovery_codes  text[] NOT NULL DEFAULT '{}';
