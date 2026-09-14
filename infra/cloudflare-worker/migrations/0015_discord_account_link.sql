-- Links a verified Ralven account to exactly one Discord user without storing
-- e-mail addresses, Discord names, or plaintext one-time codes.

CREATE TABLE IF NOT EXISTS discord_link_codes (
    code_hash TEXT PRIMARY KEY NOT NULL CHECK (length(code_hash) = 43),
    account_uid TEXT NOT NULL REFERENCES account_profiles (uid) ON DELETE CASCADE,
    expires_at TEXT NOT NULL,
    used_at TEXT,
    created_at TEXT NOT NULL,
    CHECK (expires_at > created_at),
    CHECK (used_at IS NULL OR used_at >= created_at)
);

CREATE INDEX IF NOT EXISTS idx_discord_link_codes_account
    ON discord_link_codes (account_uid, expires_at);

CREATE TABLE IF NOT EXISTS discord_account_links (
    account_uid TEXT PRIMARY KEY NOT NULL REFERENCES account_profiles (uid) ON DELETE CASCADE,
    discord_user_id TEXT NOT NULL UNIQUE
        CHECK (length(discord_user_id) BETWEEN 17 AND 20 AND discord_user_id NOT GLOB '*[^0-9]*'),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    CHECK (updated_at >= created_at)
);
