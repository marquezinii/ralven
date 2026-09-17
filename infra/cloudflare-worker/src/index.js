import { validateBatch } from './validateEvent.js';
import { validateBugReport } from './bugReports/validateSubmission.js';
import { MAX_BUG_REPORT_LIMIT, recentBugReports } from './bugReports/queries.js';
import { validateUpdaterEvent } from './updaterEvents/validateSubmission.js';
import { recentUpdaterEvents } from './updaterEvents/queries.js';
import { describeUpdaterEventCode } from './updaterEvents/catalog.js';
import { createPasswordAuthProvider } from './auth/passwordAuthProvider.js';
import { requireFirebaseUser } from './auth/firebaseIdToken.js';
import {
  validateAccountProfile,
  createAccountProfile,
  deleteAccount,
  fetchAccountProfile,
  normalizeUsername,
  isUsernameAvailable,
  resumeAccountDeletions,
} from './auth/accountProfile.js';
import {
  completeRecovery,
  deleteRecoveryCodes,
  findRecoveryCode,
  generateRecoveryCodes,
  isRecentAuthentication,
  recoveryRateLimitKey,
  releaseRecoveryCode,
  replaceRecoveryCodes,
  reserveRecoveryCode,
  tokenPassesAccountCutoff,
  validateEnrollmentId,
  validateRecoveryRequest,
} from './auth/accountSecurity.js';
import {
  accountHasTotpEnrollment,
  accountSessionIsCurrent,
  deleteFirebaseAccount,
  provePendingTotpEnrollment,
  removeMfaAndRevokeSessions,
} from './auth/firebaseAdmin.js';
import { rateLimitKey, withinRateLimit, withinRequiredRateLimit } from './rateLimit.js';
import * as queries from './stats/queries.js';
import { toCsv } from './stats/csv.js';
import { buildCorsHeaders, isAllowedDashboardOrigin, withCorsHeaders } from './cors.js';
import { hasExactJsonContentType, hasExactKeys, hasJsonContentType, isPlainObject, jsonResponse, readBoundedJson } from './requestSecurity.js';
import { createCsrfToken, isValidCsrfToken } from './auth/crypto.js';
import { validateLiveAlertUpdate } from './liveAlert/validateSubmission.js';
import { buildLiveAlertUpsert, toLiveAlertResponse } from './liveAlert/store.js';
import { fetchAccountEntitlements } from './billing/entitlements.js';
import { handleAsaasWebhook, refreshEntitlementStatement, revokeEntitlementStatement } from './billing/asaasBilling.js';
import { createAccountCheckout, cancelAccountBilling, fetchAccountBilling, syncAccountBilling } from './billing/accountBilling.js';
import { BillingError } from './billing/asaasApi.js';
import { billingReturnPage } from './billing/returnPage.js';
import { handleRalvenAi } from './ralvenAi.js';
import {
  authorizeDiscordBot,
  createDiscordLinkCode,
  fetchDiscordRoleLinks,
  redeemDiscordLinkCode,
} from './discordLink.js';

const MAX_TELEMETRY_BODY_BYTES = 512 * 1024;
const MAX_BUG_REPORT_BODY_BYTES = 128 * 1024;
const MAX_UPDATER_EVENT_BODY_BYTES = 4 * 1024;
const MAX_ACCOUNT_PROFILE_BODY_BYTES = 4 * 1024;
const MAX_ACCOUNT_SECURITY_BODY_BYTES = 8 * 1024;
const MAX_LIVE_ALERT_BODY_BYTES = 4 * 1024;

// Ralven anonymous telemetry + bug reports + admin dashboard API
// Worker. See wrangler.toml and README.md for deployment status of each
// route. Bug reports are text-only -- no attachment/screenshot support, no
// R2 dependency -- everything lives in D1.
//
// Routes:
//   POST    /telemetry             -- ingest a batch of telemetry events (no auth; validated server-side)
//   POST    /bugs                  -- ingest one bug report, text-only (no auth; validated server-side)
//   POST    /account/profile       -- create the username/first/last-name profile for a Firebase account (requires a valid Firebase ID token)
//   GET     /account/profile       -- read the caller's own username/first/last-name profile (requires a valid Firebase ID token)
//   DELETE  /account               -- delete Firebase first, then the caller's D1 data
//   POST    /account/mfa/recovery-codes -- generate one-time TOTP recovery codes after recent authentication
//   POST    /account/mfa/recover   -- recover a TOTP-blocked sign-in without returning Firebase tokens
//   GET     /account/entitlements  -- read the caller's server-authoritative access tier (requires a valid Firebase ID token)
//   POST    /account/discord/link-code -- create a short-lived one-time Discord link code
//   POST    /discord/link/redeem   -- consume a link code (Discord bot service authentication)
//   GET     /discord/role-sync     -- list linked users and authoritative tiers (Discord bot service authentication)
//   GET     /account/billing       -- offer and reconciled subscription status (Firebase ID token)
//   POST    /account/billing/checkout -- hosted monthly checkout for the accepted server offer
//   POST    /account/billing/cancel -- stop future renewals after provider confirmation
//   POST    /ai/message            -- Pro + Ralven AI contextual guidance over a bounded diagnostic summary
//   GET     /account/username-available -- advisory "is this username free?" probe for the registration form (no auth; rate limited per IP)
//   POST    /billing/asaas/webhook -- authenticate and reconcile one Asaas billing event
//   POST    /admin/login           -- { password } -> session cookie
//   POST    /admin/logout          -- clears the session cookie
//   GET     /admin/csrf            -- session-bound CSRF token (requires a valid session)
//   GET     /api/stats/:name       -- one chart's data (requires a valid session)
//   GET     /api/stats/:name.csv   -- same data as CSV (requires a valid session)
//   GET     /api/bugs              -- recent bug reports, newest first (requires a valid session)
//   GET     /live-alert            -- current admin-broadcast alert, { id, message, active, severity } (no auth; rate limited per IP)
//   POST    /admin/live-alert      -- { message?, active, severity? } -> upsert the single live alert row (requires a valid session)
//   OPTIONS *                      -- CORS preflight for the routes above
//
// The dashboard is served from a different origin than this Worker (a
// Cloudflare Pages domain, or a different localhost port while testing
// locally), so every response carries CORS headers scoped to the origins
// configured in the DASHBOARD_ORIGIN var -- see cors.js.

const STATS_BUILDERS = {
  'runs-per-day': queries.optimizationRunsPerDay,
  'os-versions': queries.osVersionBreakdown,
  'app-versions': queries.appVersionBreakdown,
  'average-time': queries.averageOptimizationTimeMs,
  'success-rate': queries.successRate,
  'app-initializations-per-day': queries.appInitializationsPerDay,
  'abandoned-optimizations': queries.abandonedOptimizationFlows,
  'gtav-benchmark-outcomes': queries.gtaVBenchmarkOutcomes,
  'errors-by-version': queries.errorsByVersion,
  'error-categories': queries.errorCategoryBreakdown,
  'bug-codes': queries.bugCodeBreakdown,
  'recent-failures': queries.recentFailures,
  'top-cpu': queries.topCpuModels,
  'top-gpu': queries.topGpuModels,
  'ram-buckets': queries.ramBucketBreakdown,
  'outcomes': queries.optimizationOutcomeBreakdown,
  'profiles': queries.profileBreakdown,
  'actions': queries.actionUsage,
  'reliability-by-version': queries.reliabilityByVersion,
  'account-summary': queries.accountSummary,
  'accounts-per-day': queries.accountsPerDay,
  'ai-summary': queries.aiUsageSummary,
  'ai-per-day': queries.aiUsagePerDay,
  'billing-subscriptions': queries.billingSubscriptionBreakdown,
  'billing-payments': queries.billingPaymentSummary,
  'updater-summary': queries.updaterSummary,
};

// Every route below answers with a JSON body -- this is the one shared shape
// (Content-Type plus whatever status/extra headers a route needs). Cache-
// Control is deliberately not set here: withCorsHeaders already defaults it
// to 'no-store' on every response the fetch handler returns, so repeating it
// per route would just be the same value twice.
// A failed read never distinguishes which query broke: the message is the same
// for every stats route, so it stays a single constant.
const DB_QUERY_FAILED = 'Database query failed';

/** CSV download shape shared by the stats and bug-report exports. */
function csvResponse(rows, filename) {
  return new Response(toCsv(rows), {
    status: 200,
    headers: {
      'Content-Type': 'text/csv; charset=utf-8',
      'Content-Disposition': `attachment; filename="${filename.replace(/[^a-zA-Z0-9_-]/g, '_')}.csv"`,
    },
  });
}

// Shared shape for the four routes backed by a `[[ratelimits]]` binding.
// Returns the 429 Response to return immediately, or null when the caller is
// within budget. `required` distinguishes the write routes (fail closed, see
// withinRequiredRateLimit) from the advisory username lookup (fail open, see
// withinRateLimit).
async function rejectIfRateLimited(limiter, request, required = true) {
  const withinLimit = required
    ? await withinRequiredRateLimit(limiter, rateLimitKey(request))
    : await withinRateLimit(limiter, rateLimitKey(request));
  return withinLimit ? null : jsonResponse({ error: 'rate-limited' }, 429);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const corsHeaders = buildCorsHeaders(request.headers.get('Origin'), env.DASHBOARD_ORIGIN);

    if (request.method === 'OPTIONS') {
      return new Response(null, { status: 204, headers: corsHeaders });
    }

    const response = await route(request, env, url);
    return withCorsHeaders(response, corsHeaders);
  },
  async scheduled(_controller, env, ctx) {
    ctx.waitUntil(resumeAccountDeletions(
      env.TELEMETRY_DB,
      (uid) => deleteFirebaseAccount(env, uid),
    ));
  },
};

async function route(request, env, url) {
  if (request.method === 'GET' && url.pathname === '/billing/return') return billingReturnPage();
  if (request.method === 'POST'
    && url.pathname.startsWith('/admin/')
    && !isAllowedDashboardOrigin(request.headers.get('Origin'), env.DASHBOARD_ORIGIN)) {
    return new Response('Forbidden', { status: 403 });
  }

  if (request.method === 'POST'
    && (url.pathname === '/admin/login' || url.pathname === '/admin/live-alert')
    && !hasExactJsonContentType(request)) {
    return new Response('Unsupported Media Type', { status: 415 });
  }

  if (request.method === 'POST' && url.pathname === '/telemetry') {
    return handleTelemetryIngest(request, env);
  }

  if (request.method === 'POST' && url.pathname === '/bugs') {
    return handleBugReportIngest(request, env);
  }
  if (request.method === 'POST' && url.pathname === '/updater-events') {
    return handleUpdaterEventIngest(request, env);
  }
  if (request.method === 'POST' && url.pathname === '/ai/message') {
    return handleRalvenAi(request, env, {
      requireUser: (candidate) => requireAccountUser(candidate, env),
    });
  }
  if (request.method === 'POST' && url.pathname === '/account/profile') {
    return handleAccountProfileCreate(request, env);
  }
  if (request.method === 'GET' && url.pathname === '/account/profile') {
    return handleAccountProfileGet(request, env);
  }
  if (request.method === 'DELETE' && url.pathname === '/account/profile') {
    return jsonResponse({ error: 'use-account-deletion' }, 410);
  }
  if (request.method === 'DELETE' && url.pathname === '/account') {
    return handleAccountDelete(request, env);
  }
  if (request.method === 'POST' && url.pathname === '/account/mfa/recovery-codes') {
    return handleRecoveryCodesCreate(request, env);
  }
  if (request.method === 'DELETE' && url.pathname === '/account/mfa/recovery-codes') {
    return handleRecoveryCodesDelete(request, env);
  }
  if (request.method === 'POST' && url.pathname === '/account/mfa/recover') {
    return handleMfaRecovery(request, env);
  }
  if (request.method === 'GET' && url.pathname === '/account/entitlements') {
    return handleAccountEntitlementsGet(request, env);
  }
  if (request.method === 'POST' && url.pathname === '/account/discord/link-code') {
    return handleDiscordLinkCodeCreate(request, env);
  }
  if (request.method === 'POST' && url.pathname === '/discord/link/redeem') {
    return handleDiscordLinkRedeem(request, env);
  }
  if (request.method === 'GET' && url.pathname === '/discord/role-sync') {
    return handleDiscordRoleSync(request, env);
  }
  if ((request.method === 'GET' && url.pathname === '/account/billing')
    || (request.method === 'POST' && ['/account/billing/checkout', '/account/billing/cancel'].includes(url.pathname))) {
    return handleAccountBilling(request, env, url.pathname);
  }
  if (request.method === 'GET' && url.pathname === '/account/username-available') {
    return handleUsernameAvailability(request, env, url);
  }
  if (request.method === 'GET' && url.pathname === '/live-alert') {
    return handleLiveAlertGet(request, env);
  }
  if (request.method === 'POST' && url.pathname === '/admin/live-alert') {
    return handleLiveAlertUpdate(request, env);
  }
  if (request.method === 'POST' && url.pathname === '/billing/asaas/webhook') {
    return handleAsaasWebhook(request, env);
  }

  if (request.method === 'GET' && url.pathname === '/admin/csrf') {
    return handleAdminCsrfToken(request, env);
  }

  if (request.method === 'POST' && url.pathname === '/admin/login') {
    return createPasswordAuthProvider(env).login(request);
  }

  if (request.method === 'POST' && url.pathname === '/admin/logout') {
    return createPasswordAuthProvider(env).logout(request);
  }

  if (request.method === 'GET' && url.pathname.startsWith('/api/stats/')) {
    return handleStatsRequest(request, env, url);
  }

  if (request.method === 'GET' && (url.pathname === '/api/bugs' || url.pathname === '/api/bugs.csv')) {
    return handleBugReportsList(request, env, url);
  }
  if (request.method === 'GET' && url.pathname === '/api/updater-events') {
    return handleUpdaterEventsList(request, env, url);
  }

  return new Response('Not found', { status: 404 });
}

async function handleUpdaterEventIngest(request, env) {
  const limited = await rejectIfRateLimited(env.UPDATER_EVENT_LIMITER, request);
  if (limited) return limited;

  const payload = await readBoundedJson(request, MAX_UPDATER_EVENT_BODY_BYTES);
  if (payload === null) return new Response('Invalid JSON', { status: 400 });
  const event = validateUpdaterEvent(payload);
  if (event === null) return new Response('Updater event failed validation', { status: 400 });
  await env.TELEMETRY_DB.prepare(
    `INSERT OR IGNORE INTO updater_events
       (event_id, stage, outcome, error_code, previous_version, candidate_version, environment, received_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
  ).bind(event.eventId, event.stage, event.outcome, event.errorCode, event.previousVersion,
    event.candidateVersion, event.environment, new Date().toISOString()).run();
  return new Response(null, { status: 202 });
}

// Advisory "is this username free?" probe for the registration form, so the
// user is told a name is taken while typing instead of only after the
// Firebase account already exists and the profile insert fails with 409.
//
// Necessarily unauthenticated -- it runs before the account does -- so it is
// rate limited per IP and answers a bare boolean: never who holds the name,
// never anything about that account. Enumeration is still theoretically
// possible at the allowed rate; the exposure is limited to "this display
// name exists", which the app shows publicly anyway.
//
// Advisory, not authoritative: the UNIQUE index on account_profiles remains
// the only real arbiter, and handleAccountProfileCreate still returns 409.
async function handleUsernameAvailability(request, env, url) {
  const limited = await rejectIfRateLimited(env.USERNAME_LOOKUP_LIMITER, request, false);
  if (limited) return limited;

  const normalized = normalizeUsername(url.searchParams.get('u'));
  if (normalized === null) {
    return jsonResponse({ error: 'invalid-username' }, 400);
  }

  const available = await isUsernameAvailable(env.TELEMETRY_DB, normalized);
  return jsonResponse({ available });
}

// Public broadcast the desktop app polls (startup + hourly) to show an
// admin-authored banner -- see docs/superpowers/specs/2026-08-17-live-alerts-design.md.
// Necessarily unauthenticated, same trade as the username lookup above: a
// rate-limited, read-only, advisory GET.
async function handleLiveAlertGet(request, env) {
  const limited = await rejectIfRateLimited(env.LIVE_ALERT_LIMITER, request, false);
  if (limited) return limited;

  const row = await env.TELEMETRY_DB
    .prepare('SELECT message, active, severity, updated_at FROM live_alert WHERE id = 1')
    .first();
  return jsonResponse(toLiveAlertResponse(row));
}

// Admin-only write side of the same feature. `message` is optional so the
// dashboard's "Desativar" button can turn the alert off without resending
// the stored text -- see src/liveAlert/validateSubmission.js.
async function handleLiveAlertUpdate(request, env) {
  const auth = await createPasswordAuthProvider(env).requireSession(request);
  if (!auth.authorized) return auth.response;

  if (!(await isValidCsrfToken(
    request.headers.get('X-Ralven-Csrf-Token'),
    auth.sessionId,
    env.ADMIN_CSRF_SECRET,
  ))) {
    return new Response('Forbidden', { status: 403 });
  }

  const payload = await readBoundedJson(request, MAX_LIVE_ALERT_BODY_BYTES);
  if (payload === null) return new Response('Invalid JSON', { status: 400 });

  const update = validateLiveAlertUpdate(payload);
  if (update === null) return jsonResponse({ error: 'invalid-live-alert' }, 400);

  const { sql, params } = buildLiveAlertUpsert(update, new Date().toISOString());
  await env.TELEMETRY_DB.prepare(sql).bind(...params).run();
  return jsonResponse({ success: true });
}

async function handleAdminCsrfToken(request, env) {
  if (!isAllowedDashboardOrigin(request.headers.get('Origin'), env.DASHBOARD_ORIGIN)) {
    return new Response('Forbidden', { status: 403 });
  }

  const auth = await createPasswordAuthProvider(env).requireSession(request);
  if (!auth.authorized) return auth.response;

  const csrfToken = await createCsrfToken(auth.sessionId, env.ADMIN_CSRF_SECRET);
  if (!csrfToken) return jsonResponse({ error: 'server-misconfigured' }, 500);
  return jsonResponse({ csrfToken });
}

// Completes the profile of a Firebase-authenticated account with the
// fields Firebase Authentication REST doesn't manage: a unique username,
// first name, last name. Requires a valid Firebase ID token (verified
// server-side, see auth/firebaseIdToken.js) -- the uid is always taken from
// the verified token, never from the request body.
async function handleAccountProfileCreate(request, env) {
  const auth = await requireAccountUser(request, env);
  if (!auth.authorized) return auth.response;
  if (!auth.emailVerified) return jsonResponse({ error: 'email-verification-required' }, 403);

  const payload = await readBoundedJson(request, MAX_ACCOUNT_PROFILE_BODY_BYTES);
  if (payload === null) return new Response('Invalid JSON', { status: 400 });

  const profile = validateAccountProfile(payload);
  if (profile === null) {
    return jsonResponse({ error: 'invalid-profile' }, 400);
  }

  const result = await createAccountProfile(env.TELEMETRY_DB, auth.uid, profile);
  if (!result.ok) {
    const status = result.code === 'username-taken' || result.code === 'uid-taken' ? 409 : 500;
    return jsonResponse({ error: result.code }, status);
  }

  return jsonResponse({ success: true }, 201);
}

async function handleAccountProfileGet(request, env) {
  const auth = await requireAccountUser(request, env);
  if (!auth.authorized) return auth.response;

  const profile = await fetchAccountProfile(env.TELEMETRY_DB, auth.uid);
  if (profile === null) {
    return jsonResponse({ error: 'profile-not-found' }, 404);
  }

  return jsonResponse(profile);
}

async function handleAccountDelete(request, env) {
  const auth = await requireAccountUser(request, env, true);
  if (!auth.authorized) return auth.response;

  try {
    const result = await deleteAccount(
      env.TELEMETRY_DB,
      auth.uid,
      (uid) => deleteFirebaseAccount(env, uid),
    );
    if (!result.ok) return jsonResponse({ error: result.code }, 409);
    return result.pending
      ? jsonResponse({ status: 'deletion-pending' }, 202)
      : new Response(null, { status: 204 });
  } catch {
    return jsonResponse({ error: 'account-deletion-unavailable' }, 503);
  }
}

async function requireAccountUser(request, env, recent = false) {
  const auth = await requireFirebaseUser(request);
  if (!auth.authorized) return auth;
  if (!await withinRequiredRateLimit(env.ACCOUNT_ROUTE_LIMITER, `account:${auth.uid}`)) {
    return { authorized: false, response: jsonResponse({ error: 'account-rate-limited' }, 429) };
  }
  try {
    if (!await accountSessionIsCurrent(env, auth.uid, auth.issuedAt)) {
      return { authorized: false, response: jsonResponse({ error: 'unauthorized' }, 401) };
    }
    if (!await tokenPassesAccountCutoff(env.TELEMETRY_DB, auth)) {
      return { authorized: false, response: jsonResponse({ error: 'unauthorized' }, 401) };
    }
  } catch {
    return { authorized: false, response: jsonResponse({ error: 'account-security-unavailable' }, 503) };
  }
  if (recent && !isRecentAuthentication(auth)) {
    return { authorized: false, response: jsonResponse({ error: 'reauthentication-required' }, 401) };
  }
  return auth;
}

async function handleRecoveryCodesCreate(request, env) {
  const auth = await requireAccountUser(request, env, true);
  if (!auth.authorized) return auth.response;
  if (!hasExactJsonContentType(request)) return jsonResponse({ error: 'invalid-content-type' }, 415);
  const payload = await readBoundedJson(request, MAX_ACCOUNT_SECURITY_BODY_BYTES);
  const enrollmentId = validateEnrollmentId(payload?.mfaEnrollmentId);
  if (enrollmentId === null || !isPlainObject(payload) || Object.keys(payload).length !== 1) {
    return jsonResponse({ error: 'invalid-request' }, 400);
  }

  try {
    if (!await accountHasTotpEnrollment(env, auth.uid, enrollmentId)) {
      return jsonResponse({ error: 'mfa-enrollment-not-found' }, 404);
    }
    const recoveryCodes = generateRecoveryCodes();
    await replaceRecoveryCodes(
      env.TELEMETRY_DB,
      auth.uid,
      enrollmentId,
      env.MFA_RECOVERY_CODE_HMAC_SECRET,
      recoveryCodes,
    );
    return jsonResponse({ recoveryCodes });
  } catch {
    return jsonResponse({ error: 'recovery-codes-unavailable' }, 503);
  }
}

async function handleRecoveryCodesDelete(request, env) {
  const auth = await requireAccountUser(request, env, true);
  if (!auth.authorized) return auth.response;
  if (!hasExactJsonContentType(request)) return jsonResponse({ error: 'invalid-content-type' }, 415);
  const payload = await readBoundedJson(request, MAX_ACCOUNT_SECURITY_BODY_BYTES);
  const enrollmentId = validateEnrollmentId(payload?.mfaEnrollmentId);
  if (enrollmentId === null || !isPlainObject(payload) || Object.keys(payload).length !== 1) {
    return jsonResponse({ error: 'invalid-request' }, 400);
  }
  try {
    await deleteRecoveryCodes(
      env.TELEMETRY_DB,
      auth.uid,
      enrollmentId,
      env.MFA_RECOVERY_CODE_HMAC_SECRET,
    );
    return new Response(null, { status: 204 });
  } catch {
    return jsonResponse({ error: 'recovery-codes-unavailable' }, 503);
  }
}

async function handleMfaRecovery(request, env) {
  if (!hasExactJsonContentType(request)) return jsonResponse({ error: 'invalid-content-type' }, 415);
  const payload = validateRecoveryRequest(await readBoundedJson(request, MAX_ACCOUNT_SECURITY_BODY_BYTES));
  if (payload === null) return jsonResponse({ error: 'invalid-request' }, 400);

  try {
    const limitKey = await recoveryRateLimitKey(
      env.MFA_RECOVERY_CODE_HMAC_SECRET,
      payload.mfaEnrollmentId,
      rateLimitKey(request),
    );
    if (!await withinRequiredRateLimit(env.ACCOUNT_RECOVERY_LIMITER, limitKey)) {
      return jsonResponse({ error: 'account-rate-limited' }, 429);
    }
    const proof = await provePendingTotpEnrollment(
      env,
      payload.mfaPendingCredential,
      payload.mfaEnrollmentId,
    );
    if (!proof.valid) return jsonResponse({ error: 'invalid-recovery-proof' }, 401);

    const recovery = await findRecoveryCode(
      env.TELEMETRY_DB,
      payload.mfaEnrollmentId,
      payload.recoveryCode,
      env.MFA_RECOVERY_CODE_HMAC_SECRET,
    );
    if (!recovery) return jsonResponse({ error: 'invalid-recovery-code' }, 401);
    if (proof.uid !== null && proof.uid !== recovery.uid) {
      return jsonResponse({ error: 'invalid-recovery-proof' }, 401);
    }
    if (!await reserveRecoveryCode(env.TELEMETRY_DB, recovery.id)) {
      return jsonResponse({ error: 'recovery-in-progress' }, 409);
    }

    const validAfter = Math.floor(Date.now() / 1000);
    try {
      await removeMfaAndRevokeSessions(env, recovery.uid, validAfter);
    } catch {
      await releaseRecoveryCode(env.TELEMETRY_DB, recovery.id);
      return jsonResponse({ error: 'recovery-unavailable' }, 503);
    }
    await completeRecovery(env.TELEMETRY_DB, recovery.id, recovery.uid, validAfter);
    return jsonResponse({ success: true });
  } catch {
    return jsonResponse({ error: 'recovery-unavailable' }, 503);
  }
}

async function handleAccountEntitlementsGet(request, env) {
  const auth = await requireAccountUser(request, env);
  if (!auth.authorized) return auth.response;

  try {
    const now = new Date().toISOString();
    await env.TELEMETRY_DB.batch([
      refreshEntitlementStatement(env.TELEMETRY_DB, auth.uid, now),
      revokeEntitlementStatement(env.TELEMETRY_DB, auth.uid, now),
    ]);
    return jsonResponse(await fetchAccountEntitlements(env.TELEMETRY_DB, auth.uid));
  } catch {
    return jsonResponse({ error: 'entitlements-unavailable' }, 500);
  }
}

async function handleDiscordLinkCodeCreate(request, env) {
  const auth = await requireAccountUser(request, env);
  if (!auth.authorized) return auth.response;
  if (!hasExactJsonContentType(request)) return jsonResponse({ error: 'invalid-content-type' }, 415);
  const payload = await readBoundedJson(request, 128);
  if (!hasExactKeys(payload, [])) {
    return jsonResponse({ error: 'invalid-request' }, 400);
  }
  try {
    return jsonResponse(await createDiscordLinkCode(
      env.TELEMETRY_DB, auth.uid, env.RALVEN_DISCORD_BOT_SECRET,
    ), 201);
  } catch {
    return jsonResponse({ error: 'discord-link-unavailable' }, 503);
  }
}

async function handleDiscordLinkRedeem(request, env) {
  if (!await authorizeDiscordBot(request, env.RALVEN_DISCORD_BOT_SECRET)) {
    return jsonResponse({ error: 'unauthorized' }, 401);
  }
  if (!hasExactJsonContentType(request)) return jsonResponse({ error: 'invalid-content-type' }, 415);
  const payload = await readBoundedJson(request, 256);
  if (!hasExactKeys(payload, ['code', 'discordUserId'])) {
    return jsonResponse({ error: 'invalid-request' }, 400);
  }
  try {
    const result = await redeemDiscordLinkCode(
      env.TELEMETRY_DB, payload.code, payload.discordUserId, env.RALVEN_DISCORD_BOT_SECRET,
    );
    return result.ok ? jsonResponse({ success: true }) : jsonResponse({ error: result.code }, 409);
  } catch {
    return jsonResponse({ error: 'discord-link-unavailable' }, 503);
  }
}

async function handleDiscordRoleSync(request, env) {
  if (!await authorizeDiscordBot(request, env.RALVEN_DISCORD_BOT_SECRET)) {
    return jsonResponse({ error: 'unauthorized' }, 401);
  }
  try {
    return jsonResponse({ links: await fetchDiscordRoleLinks(env.TELEMETRY_DB) });
  } catch {
    return jsonResponse({ error: 'discord-sync-unavailable' }, 503);
  }
}

async function handleAccountBilling(request, env, path) {
  const auth = await requireAccountUser(request, env, request.method === 'POST');
  if (!auth.authorized) return auth.response;
  try {
    if (request.method === 'GET') {
      if (!await withinRequiredRateLimit(env.BILLING_READ_LIMITER, `billing:${auth.uid}`)) {
        return jsonResponse({ error: 'billing-rate-limited' }, 429);
      }
      await syncAccountBilling(env, auth);
      return jsonResponse(await fetchAccountBilling(env, auth.uid));
    }
    if (!await withinRequiredRateLimit(env.BILLING_WRITE_LIMITER, `billing:${auth.uid}`)) {
      return jsonResponse({ error: 'billing-rate-limited' }, 429);
    }
    if (!hasJsonContentType(request)) {
      return jsonResponse({ error: 'invalid-content-type' }, 415);
    }
    const payload = await readBoundedJson(request, 1024);
    if (!isPlainObject(payload)) return jsonResponse({ error: 'invalid-request' }, 400);
    if (path.endsWith('/checkout')) return jsonResponse(await createAccountCheckout(env, auth, payload));
    if (Object.keys(payload).length !== 0) return jsonResponse({ error: 'invalid-request' }, 400);
    return jsonResponse(await cancelAccountBilling(env, auth));
  } catch (error) {
    return jsonResponse({ error: error instanceof BillingError ? error.code : 'billing-temporarily-unavailable' },
      error instanceof BillingError ? error.status : 503);
  }
}

async function handleUpdaterEventsList(request, env, url) {
  const auth = await createPasswordAuthProvider(env).requireSession(request);
  if (!auth.authorized) return auth.response;
  const { sql, params } = recentUpdaterEvents({
    environment: url.searchParams.get('environment') || undefined,
    version: url.searchParams.get('version') || undefined,
  }, url.searchParams.get('limit'));
  try {
    const { results } = await env.TELEMETRY_DB.prepare(sql).bind(...params).all();
    return jsonResponse(results.map((event) => ({ ...event, ...describeUpdaterEventCode(event.error_code) })));
  } catch {
    return jsonResponse({ error: DB_QUERY_FAILED }, 500);
  }
}

async function handleTelemetryIngest(request, env) {
  const limited = await rejectIfRateLimited(env.TELEMETRY_LIMITER, request);
  if (limited) return limited;

  const payload = await readBoundedJson(request, MAX_TELEMETRY_BODY_BYTES);
  if (payload === null) return new Response('Invalid JSON', { status: 400 });

  const events = validateBatch(payload);
  if (events === null) {
    return new Response('Event batch failed validation', { status: 400 });
  }

  const receivedAt = new Date().toISOString();
  const statements = [];
  for (const event of events) {
    statements.push(
      env.TELEMETRY_DB
        .prepare(
          `INSERT INTO telemetry_events
             (event_id, event_name, execution_time_ms, app_version, error_category, bug_code,
              os_version, system_architecture, cpu_model, gpu_model,
              ram_bucket_gib, profile, environment, received_at,
              five_m_install_detected, gta_edition, optimization_target_count,
              windows_build, disk_type, free_space_gib_bucket, run_timestamp,
              days_since_last_run_bucket, backup_created, backup_restored,
              elevation_used, process_count_at_start, operation_id)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
           ON CONFLICT(event_id) DO NOTHING`,
        )
        .bind(
          event.eventId,
          event.eventName,
          event.executionTimeMs,
          event.appVersion,
          event.errorCategory,
          event.bugCode,
          event.osVersion,
          event.systemArchitecture,
          event.cpuModel,
          event.gpuModel,
          event.ramBucketGiB,
          event.profile,
          event.environment,
          receivedAt,
          event.fiveMInstallDetected === null ? null : Number(event.fiveMInstallDetected),
          event.gtaEdition,
          event.optimizationTargetCount,
          event.windowsBuild,
          event.diskType,
          event.freeSpaceGiBBucket,
          event.runTimestamp,
          event.daysSinceLastRunBucket,
          event.backupCreated === null ? null : Number(event.backupCreated),
          event.backupRestored === null ? null : Number(event.backupRestored),
          event.elevationUsed === null ? null : Number(event.elevationUsed),
          event.processCountAtStart,
          event.operationId,
        ),
    );
    for (const actionId of event.actionIds) {
      statements.push(
        env.TELEMETRY_DB
          .prepare(
            `INSERT OR IGNORE INTO telemetry_event_actions (telemetry_event_id, action_id)
             SELECT id, ? FROM telemetry_events WHERE event_id = ?`,
          )
          .bind(actionId, event.eventId),
      );
    }
  }

  try {
    // D1 batch() is transactional: a failed event or action statement rolls
    // back the entire request, so the client can retry this exact payload.
    await env.TELEMETRY_DB.batch(statements);
  } catch (err) {
    console.error('Telemetry transaction failed:', err?.message || 'unknown');
    return jsonResponse({ error: 'Database write failed' }, 500);
  }

  return new Response(null, { status: 202 });
}

async function handleStatsRequest(request, env, url) {
  const auth = await createPasswordAuthProvider(env).requireSession(request);
  if (!auth.authorized) {
    return auth.response;
  }

  const asCsv = url.pathname.endsWith('.csv');
  const name = url.pathname
    .slice('/api/stats/'.length)
    .replace(/\.csv$/, '');

  const builder = STATS_BUILDERS[name];
  if (!builder) {
    return new Response('Unknown stat', { status: 404 });
  }

  const filters = {
    from: url.searchParams.get('from') || undefined,
    to: url.searchParams.get('to') || undefined,
    appVersion: url.searchParams.get('version') || undefined,
    environment: url.searchParams.get('environment') || undefined,
  };

  const { sql, params } = builder(filters);
  try {
    const { results } = await env.TELEMETRY_DB.prepare(sql).bind(...params).all();
    if (asCsv) {
      return csvResponse(results, name);
    }
    return jsonResponse(results);
  } catch {
    return jsonResponse({ error: DB_QUERY_FAILED }, 500);
  }
}

async function handleBugReportIngest(request, env) {
  const limited = await rejectIfRateLimited(env.BUG_REPORT_LIMITER, request);
  if (limited) return limited;

  const payload = await readBoundedJson(request, MAX_BUG_REPORT_BODY_BYTES);
  if (payload === null) return new Response('Invalid JSON', { status: 400 });

  const report = validateBugReport(payload);
  if (report === null) {
    return new Response('Bug report failed validation', { status: 400 });
  }

  await env.TELEMETRY_DB
    .prepare(
      `INSERT OR IGNORE INTO bug_reports
         (report_id, category, bug_code, summary, description, app_version, profile,
          technical_summary, email, log_text, environment, received_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    )
    .bind(
      report.reportId,
      report.category,
      report.bugCode,
      report.summary,
      report.description,
      report.appVersion,
      report.profile,
      report.technicalSummary,
      report.email,
      report.logText,
      report.environment,
      new Date().toISOString(),
    )
    .run();

  return jsonResponse({ success: true }, 202);
}

async function handleBugReportsList(request, env, url) {
  const auth = await createPasswordAuthProvider(env).requireSession(request);
  if (!auth.authorized) {
    return auth.response;
  }

  const filters = {
    environment: url.searchParams.get('environment') || undefined,
    version: url.searchParams.get('version') || undefined,
    category: url.searchParams.get('category') || undefined,
    from: url.searchParams.get('from') || undefined,
    to: url.searchParams.get('to') || undefined,
  };
  const requestedLimit = Number(url.searchParams.get('limit')) || undefined;
  const limit = url.pathname.endsWith('.csv') && requestedLimit === undefined
    ? MAX_BUG_REPORT_LIMIT
    : requestedLimit;

  const { sql, params } = recentBugReports(filters, limit);
  try {
    const { results } = await env.TELEMETRY_DB.prepare(sql).bind(...params).all();
    if (url.pathname.endsWith('.csv')) {
      return csvResponse(results, 'bug_reports');
    }
    return jsonResponse(results);
  } catch {
    return jsonResponse({ error: DB_QUERY_FAILED }, 500);
  }
}
