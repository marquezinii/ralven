# Ralven telemetry + dashboard API Worker

**Deployed** at
`https://api.vemryx.com`.

The Cloudflare Worker name, D1 database name/ID and Firebase project ID remain
as compatibility identifiers until a separately provisioned migration exists.
The legacy `workers.dev` route is disabled; public clients use only the Ralven
domain above.

This is the Cloudflare Worker + D1 backend for the anonymous telemetry
pipeline described in [`docs/telemetry.md`](../../docs/telemetry.md) and the
bug-report pipeline described in
[`docs/bug-reports.md`](../../docs/bug-reports.md), plus the authenticated
stats/bugs API the [dashboard](../dashboard/README.md) reads from.
FormSubmit has been fully removed from the .NET client for both telemetry
and bug reports — this Worker is the only transport for both.

Both `/telemetry` and `/bugs` are **live and deployed**. Bug reports are
text-only — no attachment/screenshot support, no R2 dependency. That
feature was dropped after R2 turned out to require an account-level
activation in the Cloudflare Dashboard (accepting R2's terms, possibly
confirming billing even on the free tier) that couldn't be done from this
environment; rather than block on that, the report form stayed D1-only
(category, summary, description, app version, profile, optional technical
summary, optional email, optional plain-text log excerpt capped at 100 KB).

## What's here

- `wrangler.toml` — Worker config. A single deployed instance (the
  top-level, unnamed environment — `wrangler deploy` with no `--env`)
  handles both Development- and Production-tagged telemetry; the
  `environment` column on each row is what separates them for the
  dashboard's filters, not a second deployment. (An earlier
  `env.development`/`env.production` named-environment design was removed:
  it added no benefit since D1 already distinguishes rows by that column.)
- `migrations/` — the authoritative D1 history. `0000_initial_schema.sql`
  provides the historical baseline for an empty database; later files evolve
  it incrementally. The release workflow adopts the known legacy bootstrap
  only when its v5/profile-terms shape is confirmed, then applies every later
  migration (including `0006_telemetry_event_idempotency.sql`) before the
  Worker deploys. Use `wrangler d1 migrations apply`, never `schema.sql`, for
  a database that may later receive an upgrade.
- `schema.sql` — a human-readable current-schema reference. It is not a deploy
  or bootstrap input because it does not record migration history.
- `src/validateEvent.js` — pure, dependency-free validation of one event or a
  batch. The Worker never trusts client-side validation alone; every field is
  re-checked against the same allowlist server-side.
- `src/index.js` — routes: `POST /telemetry` (ingest), `POST /admin/login` /
  `POST /admin/logout`, `GET /api/stats/:name[.csv]` (protected), plus CORS
  handling (`src/cors.js`) for every response since the dashboard is served
  from a different origin than this Worker. Telemetry accepts up to 16 events
  per request so its events and normalized actions fit in one atomic D1 batch.
- `src/liveAlert/` — the single-row admin broadcast the dashboard writes
  (`POST /admin/live-alert`, session-protected) and the desktop app polls at
  startup plus once an hour (`GET /live-alert`, public, rate limited). See
  `docs/superpowers/specs/2026-08-17-live-alerts-design.md`.
- `src/auth/` — the custom admin authentication (see below).
- `src/billing/` — provider webhook verification/reconciliation and the
  authenticated checkout/cancellation and payment-backed entitlement read model.
  Sales are disabled by default until provider sandbox validation. See
  [`docs/billing.md`](../../docs/billing.md).
- `src/discordLink.js` — short-lived account-link codes and the authenticated
  Discord bot role-sync contract. D1 stores only code HMACs and Discord IDs.
- `src/stats/` — `queries.js` (pure SQL+params builders, one per dashboard
  metric) and `csv.js` (pure CSV serialization for exports). In addition to
  optimization, version, hardware and failure statistics, protected aggregate
  metrics cover account growth, action/profile adoption, reliability by
  release, updater outcomes, Ralven AI volume/cost and billing health. Account,
  AI and billing queries never select user/profile/provider identifiers or
  interactive content. Telemetry/updater metrics accept
  `?from=&to=&version=&environment=`; account, AI and payment metrics use the
  same period while ignoring filters that do not apply to their domain.
- `test/` — unit tests for everything pure-logic above, run with Node's
  built-in test runner (no Miniflare/wrangler required):

  ```bash
  npm test
  ```

## Admin dashboard authentication

Per an explicit decision (no external domain, no Cloudflare Access, no
Google/GitHub OAuth — the dashboard is served from a plain `*.pages.dev`
URL), authentication is a small, self-contained system:

- **Password**: never stored in code or in `wrangler.toml`. Run
  `npm run hash-admin-password` locally, which prompts for a password and
  prints a self-contained `pbkdf2$<iterations>$<salt>$<hash>` string
  (PBKDF2-SHA256 via the Workers-native `crypto.subtle` — no third-party
  crypto dependency). **100,000 iterations**, not the OWASP-recommended
  210,000: the Workers runtime (BoringSSL, not Node's OpenSSL) hard-caps
  PBKDF2 at 100,000 and throws `NotSupportedError` above that — found only
  once actually deployed, since Node itself has no such cap and the test
  suite runs under Node. 100,000 remains an accepted OWASP baseline for
  PBKDF2-SHA256. That hash string, and only that string, becomes the
  `ADMIN_PASSWORD_HASH` Worker secret (`wrangler secret put
  ADMIN_PASSWORD_HASH`). The plaintext password is never written to disk,
  committed, or logged.
- **Brute-force protection**: `login_attempts` tracks failed logins per
  HMAC'd IP (`src/auth/bruteForceGuard.js`, keyed by the `IP_HASH_SECRET`
  Worker secret — the real IP itself is never stored). Five failed attempts
  within 15 minutes locks that IP out for 15 minutes; the counter resets once
  the window passes.
- **Sessions**: server-side, revocable and persisted for 30 days (`admin_sessions`, `src/auth/
  sessionStore.js`) — a random 256-bit session ID is the *only* thing stored
  in the browser cookie (`__Host-`, `HttpOnly`, `Secure`, `SameSite=None`), so logout
  or manually clearing the site data or the table actually invalidates it immediately, unlike
  a stateless signed token that can only be waited out. `SameSite=None`
  (not `Strict`/`Lax`) is required because the dashboard (`*.pages.dev`) and
  this Worker (`*.workers.dev`) are genuinely different registrable
  domains — a stricter policy silently never sends the cookie back on a
  cross-site `fetch`, which is exactly what made the first deployment's
  login appear to succeed but leave the dashboard stuck on the login screen.
- **CSRF e limites de entrada**: a publicação do alerta exige um dos origins
  exatos de `DASHBOARD_ORIGIN`, o cabeçalho `X-Ralven-Csrf-Token` e
  `Content-Type: application/json` exato. O token é derivado no Worker da
  sessão e de `ADMIN_CSRF_SECRET`, fica somente em memória no dashboard e é
  recuperado em `GET /admin/csrf` após um recarregamento. Todos os JSON
  continuam limitados por rota antes do parse.
- **Swappable by design**: `src/auth/passwordAuthProvider.js` exposes exactly
  three functions — `login`, `logout`, `requireSession` — and `index.js` only
  ever calls those three. A future OAuth-based provider (Google/GitHub, or
  Cloudflare Access) only needs to implement the same three functions with
  the same signatures; no route or stats-endpoint code would need to change.

**Known test gap**: the pure decision logic behind each of these
(`crypto.js`, `bruteForceGuard.js`, `sessionStore.js`, `stats/queries.js`,
`stats/csv.js`, `cors.js`) is unit tested. The D1-touching glue in
`passwordAuthProvider.js` and the D1-backed routing in `index.js` are not
covered end-to-end by an automated test — that would require Miniflare (a
simulated Workers/D1 runtime), which was not set up in this environment. The
origin gate and bounded request reader are covered without D1. The rest was
validated manually against the real deployment (see "Verified end-to-end" below); two
real bugs (the PBKDF2 iteration cap and the `SameSite` cookie policy) were
only caught that way, not by the unit tests, which is exactly why this gap
is called out rather than assumed harmless.

## Product accounts

The desktop application uses Firebase Authentication directly through its
official REST API. This Worker does not receive account passwords or refresh
tokens. Authenticated account-specific routes accept a Firebase ID token over
HTTPS as `Authorization: Bearer <idToken>`, verify it with
`src/auth/firebaseIdToken.js` (`requireFirebaseUser` /
`verifyFirebaseIdToken`), and use only the Firebase UID (`sub`) as the
permanent internal identifier — never email.

Verification is fail-closed: RS256 only, Google JWKS
(`securetoken@system.gserviceaccount.com`), required claims
`aud = fivemcleaner-app`,
`iss = https://securetoken.google.com/fivemcleaner-app`, unexpired `exp`, and
required `iat` and non-empty `sub`. Invalid tokens produce a generic HTTP 401
`{ "error": "unauthorized" }` with no claim detail. The pure verifier is unit
tested.

All authenticated `/account/*` routes share a required, fail-closed
`ACCOUNT_ROUTE_LIMITER` bucket keyed by verified UID. They also consult
`account_auth_cutoffs`, including `/ai/message`, so recovery and deletion close
the otherwise valid offline ID-token window. Critical mutations require
`auth_time` within five minutes. The Worker also compares every account token's
`iat` with Firebase `validSince`, rejecting disabled users and sessions revoked
by password or other credential changes.

`POST /account/profile` is built on it: Firebase manages
email/password/uid only, so this route stores the fields it doesn't —
username (globally unique, case-insensitive), first name, last name and the
accepted current terms version — in `account_profiles`, keyed by the verified
Firebase UID. It accepts only an `email_verified=true` token. A username
conflict returns `409 { "error": "username-taken" }`; the client is expected
to let the user pick another one without discarding the Firebase account
already created. `DELETE /account` checks the billing block, persists a cutoff
and durable deletion job, deletes Firebase through the administrative API, and
only then removes the D1 profile and cascading account data. A scheduled retry
resumes an interrupted deletion every 15 minutes; the cutoff and job do not
cascade with the profile. The old `DELETE /account/profile` returns 410 and can
no longer create split state. See `src/auth/accountProfile.js`.

TOTP recovery uses three endpoints:

- `POST /account/mfa/recovery-codes` with `{ "mfaEnrollmentId": "..." }`
  requires recent authentication, verifies enrollment ownership, atomically
  replaces prior codes and returns ten plaintext codes once. D1 stores only
  keyed SHA-256 HMACs.
- `DELETE /account/mfa/recovery-codes` with the same body idempotently removes
  that generation after normal MFA withdrawal.
- `POST /account/mfa/recover` accepts `mfaPendingCredential`,
  `mfaEnrollmentId` and `recoveryCode`. It first proves that Firebase accepts
  the pending/enrollment pair as a TOTP challenge, reserves the code, removes
  MFA and revokes refresh tokens administratively, then consumes the code.
  Any Firebase token from the deliberately invalid probe is verified internally
  and never returned.

Recovery uses the required, fail-closed `ACCOUNT_RECOVERY_LIMITER`, keyed by an
HMAC of the stable enrollment and caller IP, so requesting new pending
credentials does not reset the attempt budget. No password, pending credential,
Firebase token, raw enrollment ID or plaintext recovery code is persisted or logged. Apply
migrations through `0013_account_mfa_recovery.sql` and configure a service
account limited to `firebaseauth.users.get`, `firebaseauth.users.update` and
`firebaseauth.users.delete`:

```bash
wrangler secret put FIREBASE_WEB_API_KEY
wrangler secret put FIREBASE_ADMIN_CLIENT_EMAIL
wrangler secret put FIREBASE_ADMIN_PRIVATE_KEY
wrangler secret put MFA_RECOVERY_CODE_HMAC_SECRET
```

TOTP enrollment additionally requires Firebase Authentication with Identity
Platform. The desktop setting stays hidden while `firebaseTotpEnabled` is
`false`; enable it in the shipped app configuration only after Identity
Platform and all four Worker secrets above are operational. An account that
already has a TOTP factor can still open the management flow while the flag is
off, so disabling the rollout flag cannot strand an existing account.

Discord linking uses `POST /account/discord/link-code` for the authenticated
Ralven account and the service-authenticated `POST /discord/link/redeem` and
`GET /discord/role-sync` routes for the official bot. Codes expire after ten
minutes, are single-use, and are stored only as HMAC-SHA-256 digests. Apply
`0015_discord_account_link.sql` and configure the same distinct 32+ character
secret in the Worker and bot before enabling the integration:

```bash
wrangler secret put RALVEN_DISCORD_BOT_SECRET
```

`GET /account/username-available?u=<name>` answers `{ "available": true|false }`
for the registration form, so a taken name is reported while the user types
instead of only after the Firebase account already exists. It is the one
D1-backed route with no authentication — it necessarily runs *before* the
account does — so three things bound it:

- a per-IP `[[ratelimits]]` binding (`USERNAME_LOOKUP_LIMITER`, 20 requests
  per 60s, declared in `wrangler.toml`; see `src/rateLimit.js`). The binding
  is optional at runtime: `wrangler dev` and `node --test` run without it and
  the route stays open, which is the right trade for a read-only probe;
- the same `USERNAME_PATTERN` the insert uses, so a malformed name is
  rejected with `400 { "error": "invalid-username" }` without reaching D1;
- a bare boolean answer — never who holds a name, never anything else about
  that account.

It is deliberately **advisory**. The UNIQUE index on `account_profiles`
remains the only arbiter, a name can be claimed between the probe and the
registration, and `POST /account/profile` still returns 409. The desktop
client treats a rate-limited, failed or unreadable answer as "unknown" and
never as "available".

The public write routes use separate required `[[ratelimits]]` bindings:
`TELEMETRY_LIMITER`, `BUG_REPORT_LIMITER`, and `UPDATER_EVENT_LIMITER`. Unlike
the advisory username lookup, those routes fail closed when a binding is
missing or unavailable so a deployment mistake cannot silently expose D1 to
unbounded writes. Local handler tests must provide an explicit limiter stub
when they exercise one of those routes.

`GET /live-alert` follows the same advisory, fail-open trade as the username
lookup (`LIVE_ALERT_LIMITER`, 30/60s per IP) — it is read-only, unauthenticated
by necessity (every installed app reads it), and never exposes anything more
sensitive than the one message an admin chose to broadcast.

## Ralven AI

`POST /ai/message` is an authenticated, verified-email route that requires both
`ralven_pro` and the separate `ralven_ai` entitlement. It validates a bounded
allowlisted diagnostic summary, applies a required rate limit per Firebase UID,
deduplicates client requests and reserves budget in D1 before calling the
OpenAI Responses API. It exposes no tools and returns only an answer plus one
standard profile name. Provider responses are capped at 64 KiB; ambiguous
failures retain the reservation when measured usage is unavailable. See
[`docs/ralven-ai.md`](../../docs/ralven-ai.md).

Activation requires migrations through `0011_ralven_ai_foundation.sql`, the
distinct Worker secrets `OPENAI_API_KEY` and
`RALVEN_AI_SAFETY_IDENTIFIER_SECRET`, and an explicit
`RALVEN_AI_ENABLED=true`. The non-secret model, price and budget values are
declared in `wrangler.toml`; missing or inconsistent limits and limiter
bindings fail closed.

## Billing and recurring subscriptions

`GET /account/entitlements` uses the same verified Firebase UID as the profile
routes and returns only the current tier, entitlement keys and validity. Missing
or expired access is a normal `free` response; provider identifiers are never
returned.

`POST /billing/asaas/webhook` validates a dedicated `asaas-access-token`,
deduplicates the event ID, and fetches the canonical payment and subscription
with a Worker-only API key. Only a `CONFIRMED` or `RECEIVED` card payment
linked to the server checkout, without completed refund or chargeback, grants
the independent `ralven_pro` and `ralven_ai` keys for its monthly period.
Checkout or subscription status alone never grants either entitlement.
Both required credentials are Worker secrets:

```bash
wrangler secret put ASAAS_ACCESS_TOKEN
wrangler secret put ASAAS_WEBHOOK_TOKEN
```

`GET /account/billing`, `POST /account/billing/checkout` (`{ offerKey }`) and
`POST /account/billing/cancel` (`{}`) use Firebase authentication. Offers include
their price in the key to prevent stale consent from accepting another price.
Checkout creation is serialized by a durable intent. Because Asaas Checkout
does not document an idempotency key, an ambiguous create is never retried.
Cancellation stops the checkout or subscription, preserves already paid access
and permits account deletion only after the provider accepts it.

Apply migrations through `0011_ralven_ai_foundation.sql` with this code.
`ASAAS_BILLING_ENABLED` remains `false` in the committed configuration;
activation requires the two secrets, `ASAAS_RETURN_URL` (HTTPS),
`ASAAS_ENVIRONMENT`, `ASAAS_AMOUNT_CENTS` (default 1990, monthly BRL), and the required
`BILLING_WRITE_LIMITER` / `BILLING_READ_LIMITER` bindings. Account refresh
reconciles payments linked to the checkout, recovering missed notifications.
Configure Checkout, subscription and payment events in the Asaas dashboard.
Complete the sandbox and commercial-readiness
steps in [`docs/billing.md`](../../docs/billing.md) before enabling sales.

The Worker itself serves a script-free, query-independent return page at
`GET /billing/return`. After deploying this code, use
`https://api.vemryx.com/billing/return`
as `ASAAS_RETURN_URL`. It directs the user back to Ralven Pro and
**Atualizar assinatura**, makes no approval claim, and uses CSP with a style
nonce, `no-store`, `no-referrer` and frame protection.

Legacy Worker product tables (`user_accounts` / sessions), if still present on
remote D1 from the pre-Firebase system, are not migrated. There are no real
users to preserve; cleanup is a separate authorized deploy/migration task.

## Verified end-to-end

The ingestion-to-dashboard flow was verified against the real deployed Worker
with a temporary telemetry event that was removed afterwards. The production
dashboard now lives at `https://dashboard.vemryx.com`; authentication and live
statistics were verified there after the custom domain became active.

## Deploying and rotating secrets

```bash
npm install
npm run db:bootstrap:local        # creates a fresh local database through migrations
npm run db:migrate:local          # applies pending migrations to an existing local database
npm run test:migrations           # empty, historical, and failed-migration local D1 checks

npm run hash-admin-password       # prints the ADMIN_PASSWORD_HASH value
wrangler secret put ADMIN_PASSWORD_HASH
wrangler secret put IP_HASH_SECRET   # any long random string
wrangler secret put ADMIN_CSRF_SECRET # distinct long random string
wrangler secret put ASAAS_ACCESS_TOKEN
wrangler secret put ASAAS_WEBHOOK_TOKEN
wrangler secret put FIREBASE_WEB_API_KEY
wrangler secret put FIREBASE_ADMIN_CLIENT_EMAIL
wrangler secret put FIREBASE_ADMIN_PRIVATE_KEY
wrangler secret put MFA_RECOVERY_CODE_HMAC_SECRET
wrangler secret put RALVEN_DISCORD_BOT_SECRET

wrangler d1 migrations apply TELEMETRY_DB --remote   # captures a D1 backup; touches the real database — ask first
wrangler deploy   # touches Cloudflare — ask first
```

The real deployment uses plain `wrangler deploy`/`npm run deploy` with no
`--env` — the old `deploy:development`/`deploy:production` scripts targeted
named-environment sections that have been removed.
