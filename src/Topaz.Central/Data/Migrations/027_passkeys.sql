-- Passkeys (1.9): login without a password by fingerprint, face or PIN of the device (WebAuthn).
-- A passkey is itself two-factor (the device + its biometrics), so it replaces password + code.
CREATE TABLE user_passkeys (
    credential_id bytea PRIMARY KEY,                  -- from the authenticator, unique worldwide
    user_id       uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    public_key    bytea NOT NULL,                     -- COSE key (ES256 or RS256)
    sign_count    bigint NOT NULL DEFAULT 0,          -- grows with each use on most devices; a drop = cloned key
    name          text NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    last_used_at  timestamptz
);
CREATE INDEX ix_user_passkeys_user ON user_passkeys (user_id);

-- The UI role sees passkeys of the users it sees (users has its own organization policy).
SELECT app_enable_rls('user_passkeys', 'user_id IN (SELECT id FROM users)');
