-- Schema for the Ralven anonymous telemetry D1 database.
-- Mirrors the closed allowlist documented in docs/telemetry.md (version 2 of
-- the privacy consent): no file paths, no machine identifiers, no free
-- text -- CPU/GPU model and RAM bucket are coarse hardware categories, not
-- unique machine fingerprints.

CREATE TABLE IF NOT EXISTS telemetry_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id TEXT NOT NULL UNIQUE,
    event_name TEXT NOT NULL,
    execution_time_ms INTEGER NOT NULL,
    app_version TEXT NOT NULL,
    error_category TEXT,
    bug_code TEXT,
    os_version TEXT,
    system_architecture TEXT,
    cpu_model TEXT,
    gpu_model TEXT,
    ram_bucket_gib INTEGER,
    profile TEXT,
    environment TEXT NOT NULL,
    received_at TEXT NOT NULL,
    -- v5: expanded optional diagnostic fields sent only with current consent;
    -- see migrations/0004_telemetry_v5_fields.sql and docs/telemetry.md.
    five_m_install_detected INTEGER,
    gta_edition TEXT,
    optimization_target_count INTEGER,
    windows_build INTEGER,
    disk_type TEXT,
    free_space_gib_bucket INTEGER,
    run_timestamp TEXT,
    days_since_last_run_bucket INTEGER,
    backup_created INTEGER,
      backup_restored INTEGER,
      elevation_used INTEGER,
      process_count_at_start INTEGER,
      operation_id TEXT
);

CREATE INDEX IF NOT EXISTS idx_telemetry_events_received_at
    ON telemetry_events (received_at);

CREATE INDEX IF NOT EXISTS idx_telemetry_events_environment
    ON telemetry_events (environment);

CREATE INDEX IF NOT EXISTS idx_telemetry_events_app_version
    ON telemetry_events (app_version);

CREATE INDEX IF NOT EXISTS idx_telemetry_events_operation_id
    ON telemetry_events (operation_id);

-- One row per action ID applied in an optimization-completed event, so
-- "most used function" can be aggregated with a simple GROUP BY instead of
-- unpacking a JSON array in every query.
CREATE TABLE IF NOT EXISTS telemetry_event_actions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    telemetry_event_id INTEGER NOT NULL REFERENCES telemetry_events (id),
    action_id TEXT NOT NULL,
    UNIQUE (telemetry_event_id, action_id)
);

CREATE INDEX IF NOT EXISTS idx_telemetry_event_actions_action_id
    ON telemetry_event_actions (action_id);

CREATE INDEX IF NOT EXISTS idx_telemetry_event_actions_event_id
    ON telemetry_event_actions (telemetry_event_id);

-- Admin dashboard authentication (custom, no external identity provider —
-- see infra/cloudflare-worker/README.md for the design rationale). IPs are
-- never stored in the clear: only an HMAC of the IP (keyed by the
-- IP_HASH_SECRET Worker secret) is kept, just enough to rate-limit repeated
-- failures without retaining a reversible identifier.
CREATE TABLE IF NOT EXISTS login_attempts (
    ip_hash TEXT PRIMARY KEY,
    failed_count INTEGER NOT NULL DEFAULT 0,
    first_failed_at TEXT NOT NULL,
    locked_until TEXT
);

-- Server-side admin sessions: an opaque, random session ID is the only
-- thing stored in the browser cookie, so a session can always be revoked
-- (logout, or manually clearing this table) instead of having to wait out
-- a stateless token's expiry.
CREATE TABLE IF NOT EXISTS admin_sessions (
    id TEXT PRIMARY KEY,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    revoked_at TEXT
);

CREATE INDEX IF NOT EXISTS idx_admin_sessions_expires_at
    ON admin_sessions (expires_at);

-- Text-only bug reports, replacing the previous FormSubmit-based flow. There
-- is no attachment/screenshot support and no R2 dependency.
CREATE TABLE IF NOT EXISTS bug_reports (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    report_id TEXT NOT NULL UNIQUE,
    category TEXT NOT NULL,
    bug_code TEXT NOT NULL,
    summary TEXT NOT NULL,
    description TEXT NOT NULL,
    app_version TEXT NOT NULL,
    profile TEXT NOT NULL,
    technical_summary TEXT,
    email TEXT,
    log_text TEXT,
    environment TEXT NOT NULL,
    received_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_bug_reports_received_at
    ON bug_reports (received_at);

CREATE INDEX IF NOT EXISTS idx_bug_reports_environment
    ON bug_reports (environment);

CREATE TABLE IF NOT EXISTS updater_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id TEXT NOT NULL UNIQUE,
    stage TEXT NOT NULL,
    outcome TEXT NOT NULL,
    error_code TEXT NOT NULL,
    previous_version TEXT,
    candidate_version TEXT NOT NULL,
    environment TEXT NOT NULL,
    received_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_updater_events_received_at ON updater_events (received_at);
CREATE INDEX IF NOT EXISTS idx_updater_events_candidate_version ON updater_events (candidate_version);

-- Profile-completion data for a Firebase-authenticated account. Firebase
-- Authentication REST only owns email/password/uid -- it has no username,
-- first name, or last name -- so this is the sole place a username's
-- uniqueness is enforced (see docs/architecture.md and
-- src/auth/accountProfile.js). `uid` is the Firebase UID (JWT `sub`),
-- validated server-side by src/auth/firebaseIdToken.js; never trust a
-- client-supplied identifier here.
CREATE TABLE IF NOT EXISTS account_profiles (
    uid TEXT PRIMARY KEY,
    username TEXT NOT NULL,
    username_normalized TEXT NOT NULL,
    first_name TEXT NOT NULL,
    last_name TEXT NOT NULL,
    terms_version TEXT NOT NULL,
    terms_accepted_at TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_account_profiles_username_normalized
    ON account_profiles (username_normalized);

-- One-time TOTP recovery material. Enrollment identifiers and codes are
-- represented only by keyed SHA-256 HMACs; plaintext codes leave the Worker
-- once, in the generation response. Reservations serialize concurrent use
-- without consuming a code before the Firebase administrator operation wins.
CREATE TABLE IF NOT EXISTS account_mfa_recovery_codes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_uid TEXT NOT NULL REFERENCES account_profiles(uid) ON DELETE CASCADE,
    enrollment_tag TEXT NOT NULL CHECK(length(enrollment_tag) = 64),
    code_hash TEXT NOT NULL CHECK(length(code_hash) = 64),
    generation_id TEXT NOT NULL CHECK(length(generation_id) = 36),
    created_at TEXT NOT NULL,
    reserved_until TEXT,
    used_at TEXT,
    UNIQUE(enrollment_tag, code_hash),
    CHECK(used_at IS NULL OR reserved_until IS NULL)
);

CREATE INDEX IF NOT EXISTS idx_account_mfa_recovery_lookup
    ON account_mfa_recovery_codes(enrollment_tag, code_hash, used_at);

-- Local revocation cutoff closes the normal one-hour offline JWT window after
-- recovery. Firebase validSince separately revokes refresh tokens.
CREATE TABLE IF NOT EXISTS account_auth_cutoffs (
    account_uid TEXT PRIMARY KEY,
    valid_after INTEGER NOT NULL CHECK(valid_after > 0),
    updated_at TEXT NOT NULL
);

-- Durable outbox for deletion retries. It intentionally has no profile FK:
-- the identity may already be gone when D1 cleanup resumes.
CREATE TABLE IF NOT EXISTS account_deletion_jobs (
    account_uid TEXT PRIMARY KEY,
    requested_at TEXT NOT NULL
);

-- Billing state is keyed only by the Firebase UID already verified by the
-- Worker. Provider references are opaque identifiers; no email, checkout URL,
-- webhook body, credential, or secret is persisted here.
CREATE TABLE IF NOT EXISTS billing_checkout_intents (
    id TEXT PRIMARY KEY NOT NULL,
    account_uid TEXT NOT NULL REFERENCES account_profiles (uid) ON DELETE CASCADE,
    provider TEXT NOT NULL CHECK (length(provider) BETWEEN 1 AND 32),
    external_reference TEXT NOT NULL UNIQUE CHECK (length(external_reference) BETWEEN 1 AND 128),
    offer_key TEXT NOT NULL CHECK (length(offer_key) BETWEEN 1 AND 64),
    amount_cents INTEGER NOT NULL CHECK (amount_cents > 0),
    currency TEXT NOT NULL CHECK (currency = 'BRL'),
    provider_checkout_id TEXT CHECK (provider_checkout_id IS NULL OR length(provider_checkout_id) BETWEEN 1 AND 128),
    create_attempt_started_at TEXT,
    state TEXT NOT NULL CHECK (state IN ('created', 'pending', 'completed', 'cancelled')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    CHECK (updated_at >= created_at)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_billing_checkout_intents_provider_checkout
    ON billing_checkout_intents (provider, provider_checkout_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_billing_checkout_intents_account_contract
    ON billing_checkout_intents (id, account_uid, provider, offer_key);
CREATE UNIQUE INDEX IF NOT EXISTS idx_billing_one_open_checkout
    ON billing_checkout_intents(account_uid) WHERE state <> 'cancelled';

-- Persist only the authenticated event envelope plus internal processing
-- state. Resource details are fetched from the provider before any grant.
CREATE TABLE IF NOT EXISTS billing_webhook_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    provider TEXT NOT NULL CHECK (length(provider) BETWEEN 1 AND 32),
    provider_request_id TEXT NOT NULL CHECK (length(provider_request_id) BETWEEN 1 AND 128),
    resource_id TEXT NOT NULL CHECK (length(resource_id) BETWEEN 1 AND 128),
    received_at TEXT NOT NULL,
    processing_outcome TEXT NOT NULL DEFAULT 'pending'
        CHECK (processing_outcome IN ('pending', 'processed', 'ignored', 'failed')),
    processed_at TEXT,
    UNIQUE (provider, provider_request_id),
    CHECK (
        (processing_outcome = 'pending' AND processed_at IS NULL)
        OR (processing_outcome <> 'pending' AND processed_at IS NOT NULL)
    )
);

CREATE TABLE IF NOT EXISTS billing_subscriptions (
    id TEXT PRIMARY KEY NOT NULL,
    account_uid TEXT NOT NULL REFERENCES account_profiles (uid) ON DELETE CASCADE,
    checkout_intent_id TEXT NOT NULL UNIQUE,
    provider TEXT NOT NULL CHECK (length(provider) BETWEEN 1 AND 32),
    provider_subscription_id TEXT NOT NULL CHECK (length(provider_subscription_id) BETWEEN 1 AND 128),
    offer_key TEXT NOT NULL CHECK (length(offer_key) BETWEEN 1 AND 64),
    state TEXT NOT NULL CHECK (state IN ('pending', 'authorized', 'paused', 'cancelled')),
    provider_updated_at TEXT NOT NULL
        CHECK (length(provider_updated_at) = 24 AND substr(provider_updated_at, 5, 1) = '-' AND substr(provider_updated_at, 8, 1) = '-' AND substr(provider_updated_at, 11, 1) = 'T' AND substr(provider_updated_at, 14, 1) = ':' AND substr(provider_updated_at, 17, 1) = ':' AND substr(provider_updated_at, 20, 1) = '.' AND substr(provider_updated_at, 24, 1) = 'Z'),
    last_event_id INTEGER REFERENCES billing_webhook_events (id),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (provider, provider_subscription_id),
    CHECK (updated_at >= created_at),
    FOREIGN KEY (checkout_intent_id, account_uid, provider, offer_key)
        REFERENCES billing_checkout_intents (id, account_uid, provider, offer_key)
        ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_billing_subscriptions_account_contract
    ON billing_subscriptions (id, account_uid);

CREATE TABLE IF NOT EXISTS billing_payments (
    provider_payment_id TEXT PRIMARY KEY NOT NULL,
    subscription_id TEXT NOT NULL REFERENCES billing_subscriptions(id) ON DELETE CASCADE,
    state TEXT NOT NULL CHECK(state IN ('approved', 'pending', 'rejected', 'refunded', 'cancelled', 'charged_back')),
    amount_cents INTEGER NOT NULL CHECK(amount_cents > 0),
    refunded_cents INTEGER NOT NULL CHECK(refunded_cents >= 0),
    currency TEXT NOT NULL CHECK(currency = 'BRL'),
    period_start TEXT NOT NULL,
    period_end TEXT NOT NULL CHECK(period_end > period_start),
    provider_updated_at TEXT NOT NULL,
    last_event_id INTEGER NOT NULL REFERENCES billing_webhook_events(id),
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_billing_payment_subscription_period
    ON billing_payments(subscription_id, period_start, period_end);

-- This is the server-authoritative access snapshot read by the app. Paid
-- access is always time-bounded and traceable to a normalized subscription
-- plus the webhook event that most recently changed it.
CREATE TABLE IF NOT EXISTS account_entitlements (
    account_uid TEXT NOT NULL REFERENCES account_profiles (uid) ON DELETE CASCADE,
    entitlement_key TEXT NOT NULL CHECK (length(entitlement_key) BETWEEN 1 AND 64),
    state TEXT NOT NULL CHECK (state IN ('active', 'grace_period', 'expired', 'revoked')),
    subscription_id TEXT NOT NULL,
    valid_from TEXT NOT NULL
        CHECK (length(valid_from) = 24 AND substr(valid_from, 5, 1) = '-' AND substr(valid_from, 8, 1) = '-' AND substr(valid_from, 11, 1) = 'T' AND substr(valid_from, 14, 1) = ':' AND substr(valid_from, 17, 1) = ':' AND substr(valid_from, 20, 1) = '.' AND substr(valid_from, 24, 1) = 'Z'),
    valid_until TEXT NOT NULL
        CHECK (length(valid_until) = 24 AND substr(valid_until, 5, 1) = '-' AND substr(valid_until, 8, 1) = '-' AND substr(valid_until, 11, 1) = 'T' AND substr(valid_until, 14, 1) = ':' AND substr(valid_until, 17, 1) = ':' AND substr(valid_until, 20, 1) = '.' AND substr(valid_until, 24, 1) = 'Z'),
    provider_updated_at TEXT NOT NULL
        CHECK (length(provider_updated_at) = 24 AND substr(provider_updated_at, 5, 1) = '-' AND substr(provider_updated_at, 8, 1) = '-' AND substr(provider_updated_at, 11, 1) = 'T' AND substr(provider_updated_at, 14, 1) = ':' AND substr(provider_updated_at, 17, 1) = ':' AND substr(provider_updated_at, 20, 1) = '.' AND substr(provider_updated_at, 24, 1) = 'Z'),
    last_event_id INTEGER REFERENCES billing_webhook_events (id),
    updated_at TEXT NOT NULL,
    PRIMARY KEY (account_uid, entitlement_key),
    CHECK (valid_until >= valid_from),
    FOREIGN KEY (subscription_id, account_uid)
        REFERENCES billing_subscriptions (id, account_uid)
        ON DELETE CASCADE
);

-- Single-row broadcast the admin dashboard writes to and the desktop app
-- polls (startup + hourly) -- see
-- docs/superpowers/specs/2026-08-17-live-alerts-design.md. There is only
-- ever one active alert at a time; id is always 1, seeded below.
CREATE TABLE IF NOT EXISTS live_alert (
    id INTEGER PRIMARY KEY,
    message TEXT NOT NULL DEFAULT '',
    active INTEGER NOT NULL DEFAULT 0,
    severity TEXT NOT NULL DEFAULT 'important' CHECK (severity IN ('info', 'important', 'critical')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

INSERT OR IGNORE INTO live_alert (id, message, active, updated_at)
    VALUES (1, '', 0, datetime('now'));

-- Monthly Ralven AI spend ledger. Interactive content is never stored.
CREATE TABLE IF NOT EXISTS ralven_ai_usage (
    request_id TEXT PRIMARY KEY NOT NULL,
    account_uid TEXT NOT NULL REFERENCES account_profiles (uid) ON DELETE CASCADE,
    billing_period TEXT NOT NULL CHECK (
        length(billing_period) = 7 AND substr(billing_period, 5, 1) = '-'
    ),
    state TEXT NOT NULL CHECK (state IN ('reserved', 'completed', 'failed')),
    reserved_cost_microusd INTEGER NOT NULL CHECK (reserved_cost_microusd > 0),
    actual_cost_microusd INTEGER CHECK (actual_cost_microusd IS NULL OR actual_cost_microusd >= 0),
    input_tokens INTEGER CHECK (input_tokens IS NULL OR input_tokens >= 0),
    cached_input_tokens INTEGER CHECK (cached_input_tokens IS NULL OR cached_input_tokens >= 0),
    cache_write_tokens INTEGER CHECK (cache_write_tokens IS NULL OR cache_write_tokens >= 0),
    output_tokens INTEGER CHECK (output_tokens IS NULL OR output_tokens >= 0),
    reasoning_tokens INTEGER CHECK (reasoning_tokens IS NULL OR reasoning_tokens >= 0),
    created_at TEXT NOT NULL,
    completed_at TEXT,
    CHECK (
        (state = 'reserved' AND completed_at IS NULL)
        OR (state <> 'reserved' AND completed_at IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS idx_ralven_ai_usage_account_period
    ON ralven_ai_usage (account_uid, billing_period);
CREATE INDEX IF NOT EXISTS idx_ralven_ai_usage_period
    ON ralven_ai_usage (billing_period);
CREATE INDEX IF NOT EXISTS idx_ralven_ai_usage_created_at
    ON ralven_ai_usage (created_at);

-- Short-lived Discord codes are stored only as keyed digests. Each Ralven
-- account and Discord user can participate in at most one active link.
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
