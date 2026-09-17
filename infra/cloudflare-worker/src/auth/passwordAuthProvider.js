// Glue between the pure auth logic (crypto.js, bruteForceGuard.js,
// sessionStore.js) and the D1 binding + Request/Response objects. Kept
// separate and thin so the decision logic underneath stays unit testable
// without Miniflare -- this file itself is exercised by manual review and,
// once deployed, real requests, not by an automated D1-backed test (see
// infra/cloudflare-worker/README.md for that known gap).
//
// Designed to be swappable: a future OAuth-based provider only needs to
// implement the same three functions (login, logout, requireSession) with
// the same signatures for index.js to use it instead, with no changes to
// the routing or the rest of the Worker.

import { createCsrfToken, hashIp, verifyPassword } from './crypto.js';
import {
  FAILURE_WINDOW_MS,
  LOCKOUT_DURATION_MS,
  MAX_FAILED_ATTEMPTS,
  isLockedOut,
  stateAfterSuccess,
} from './bruteForceGuard.js';
import {
  buildExpiredSessionCookie,
  buildSessionCookie,
  createSessionRow,
  isSessionValid,
  readSessionCookie,
  hashToken,
} from './sessionStore.js';
import { jsonResponse, readBoundedJson } from '../requestSecurity.js';

const MAX_LOGIN_BODY_BYTES = 4 * 1024;
const MAX_PASSWORD_CHARACTERS = 1024;

function clientIp(request) {
  // Cloudflare always sets this header at the edge; it cannot be spoofed by
  // the client because Cloudflare overwrites any client-supplied value.
  return request.headers.get('CF-Connecting-IP') ?? 'unknown';
}

async function getLoginAttempt(db, ipHash) {
  return db
    .prepare('SELECT failed_count, first_failed_at, locked_until FROM login_attempts WHERE ip_hash = ?')
    .bind(ipHash)
    .first();
}

async function saveLoginAttempt(db, ipHash, row) {
  if (row === null) {
    await db.prepare('DELETE FROM login_attempts WHERE ip_hash = ?').bind(ipHash).run();
    return;
  }

  await db
    .prepare(
      `INSERT INTO login_attempts (ip_hash, failed_count, first_failed_at, locked_until)
       VALUES (?, ?, ?, ?)
       ON CONFLICT(ip_hash) DO UPDATE SET
         failed_count = excluded.failed_count,
         first_failed_at = excluded.first_failed_at,
         locked_until = excluded.locked_until`,
    )
    .bind(ipHash, row.failed_count, row.first_failed_at, row.locked_until)
    .run();
}

export function buildFailedLoginMutation(ipHash, now) {
  const nowIso = now.toISOString();
  const windowStartIso = new Date(now.getTime() - FAILURE_WINDOW_MS).toISOString();
  const lockoutUntilIso = new Date(now.getTime() + LOCKOUT_DURATION_MS).toISOString();
  return {
    sql: `INSERT INTO login_attempts (ip_hash, failed_count, first_failed_at, locked_until)
          VALUES (?, 1, ?, NULL)
          ON CONFLICT(ip_hash) DO UPDATE SET
            failed_count = CASE
              WHEN login_attempts.first_failed_at < ? THEN 1
              ELSE login_attempts.failed_count + 1
            END,
            first_failed_at = CASE
              WHEN login_attempts.first_failed_at < ? THEN excluded.first_failed_at
              ELSE login_attempts.first_failed_at
            END,
            locked_until = CASE
              WHEN login_attempts.first_failed_at < ? THEN NULL
              WHEN login_attempts.failed_count + 1 >= ? THEN ?
              ELSE NULL
            END`,
    bindings: [
      ipHash,
      nowIso,
      windowStartIso,
      windowStartIso,
      windowStartIso,
      MAX_FAILED_ATTEMPTS,
      lockoutUntilIso,
    ],
  };
}

async function recordFailedLoginAttempt(db, ipHash, now) {
  const mutation = buildFailedLoginMutation(ipHash, now);
  await db.prepare(mutation.sql).bind(...mutation.bindings).run();
}

async function getSession(db, id) {
  const hashedId = await hashToken(id);
  return db
    .prepare('SELECT id, created_at, expires_at, revoked_at FROM admin_sessions WHERE id = ?')
    .bind(hashedId)
    .first();
}

async function saveSession(db, row) {
  const hashedId = await hashToken(row.id);
  await db
    .prepare('INSERT INTO admin_sessions (id, created_at, expires_at, revoked_at) VALUES (?, ?, ?, ?)')
    .bind(hashedId, row.created_at, row.expires_at, row.revoked_at)
    .run();
}

async function revokeSession(db, id, now) {
  const hashedId = await hashToken(id);
  await db
    .prepare('UPDATE admin_sessions SET revoked_at = ? WHERE id = ?')
    .bind(now.toISOString(), hashedId)
    .run();
}

export function createPasswordAuthProvider(env, now = () => new Date()) {
  const db = env.TELEMETRY_DB;

  return {
    async login(request) {
      const ip = clientIp(request);
      const ipHash = await hashIp(ip, env.IP_HASH_SECRET);
      const nowValue = now();

      const attemptRow = await getLoginAttempt(db, ipHash);
      if (isLockedOut(attemptRow, nowValue)) {
        return jsonResponse({ error: 'too-many-attempts' }, 429);
      }

      const body = await readBoundedJson(request, MAX_LOGIN_BODY_BYTES);
      const password = body?.password;
      if (body === null) {
        return jsonResponse({ error: 'invalid-request' }, 400);
      }

      const isValid = typeof password === 'string'
        && password.length <= MAX_PASSWORD_CHARACTERS
        && (await verifyPassword(password, env.ADMIN_PASSWORD_HASH));
      if (!isValid) {
        await recordFailedLoginAttempt(db, ipHash, nowValue);
        return jsonResponse({ error: 'invalid-credentials' }, 401);
      }

      await saveLoginAttempt(db, ipHash, stateAfterSuccess());
      const session = createSessionRow(nowValue);
      const csrfToken = await createCsrfToken(session.id, env.ADMIN_CSRF_SECRET);
      if (!csrfToken) {
        return jsonResponse({ error: 'server-misconfigured' }, 500);
      }
      await saveSession(db, session);

      return jsonResponse({ success: true, csrfToken }, 200, {
        'Set-Cookie': buildSessionCookie(session.id, session.expires_at),
      });
    },

    async logout(request) {
      const sessionId = readSessionCookie(request.headers.get('Cookie'));
      if (sessionId) {
        await revokeSession(db, sessionId, now());
      }

      return jsonResponse({ success: true }, 200, {
        'Set-Cookie': buildExpiredSessionCookie(),
      });
    },

    /**
     * Returns `{ authorized: true }` when the request carries a valid
     * session, or `{ authorized: false, response }` with a ready-to-return
     * 401 `Response` otherwise -- callers just check `authorized` and
     * return `response` immediately when it is `false`.
     */
    async requireSession(request) {
      const sessionId = readSessionCookie(request.headers.get('Cookie'));
      const session = sessionId ? await getSession(db, sessionId) : null;

      if (!isSessionValid(session, now())) {
        return { authorized: false, response: jsonResponse({ error: 'unauthorized' }, 401) };
      }

      return { authorized: true, sessionId };
    },
  };
}
