// Firebase Authentication REST only manages email/password/uid -- it has no
// concept of a username, first name, or last name. This module is the
// server-side half of the profile completion step that fills that gap:
// uniqueness of `username` can only be enforced centrally, never trusted
// from the client, so it lives here behind requireFirebaseUser (see
// firebaseIdToken.js) rather than in the App itself.

const NAME_PATTERN = /^\p{L}[\p{L} '-]{0,59}$/u;
const USERNAME_PATTERN = /^[a-zA-Z][a-zA-Z0-9_]{2,23}$/;
const CURRENT_TERMS_VERSION = '2026-08-02';
const PROFILE_FIELDS = ['username', 'firstName', 'lastName', 'termsVersion'];

/**
 * Validates and normalizes the profile-completion payload. Returns null on
 * any violation -- the caller responds 400 without leaking which field
 * failed, matching the other ingest validators in this Worker.
 *
 * @param {unknown} payload
 * @returns {{ username: string, usernameNormalized: string, firstName: string, lastName: string, termsVersion: string } | null}
 */
export function validateAccountProfile(payload) {
  if (payload === null || typeof payload !== 'object' || Array.isArray(payload)
    || Object.keys(payload).length !== PROFILE_FIELDS.length
    || !PROFILE_FIELDS.every(field => Object.hasOwn(payload, field))) {
    return null;
  }

  const { username, firstName, lastName, termsVersion } = payload;
  if (typeof username !== 'string' || typeof firstName !== 'string' || typeof lastName !== 'string'
    || termsVersion !== CURRENT_TERMS_VERSION) {
    return null;
  }

  const trimmedUsername = username.trim();
  const trimmedFirstName = firstName.trim();
  const trimmedLastName = lastName.trim();

  if (!USERNAME_PATTERN.test(trimmedUsername)) {
    return null;
  }

  if (!NAME_PATTERN.test(trimmedFirstName) || !NAME_PATTERN.test(trimmedLastName)) {
    return null;
  }

  return {
    username: trimmedUsername,
    usernameNormalized: trimmedUsername.toLowerCase(),
    firstName: trimmedFirstName,
    lastName: trimmedLastName,
    termsVersion,
  };
}

/**
 * Inserts the profile row for `uid`, the Firebase UID from the verified ID
 * token -- never anything client-supplied. Returns a discriminated result
 * instead of throwing so the route handler can map it to the right HTTP
 * status without inspecting D1's error message shape.
 *
 * @param {D1Database} db
 * @param {string} uid
 * @param {ReturnType<typeof validateAccountProfile>} profile
 * @returns {Promise<{ ok: true } | { ok: false, code: 'username-taken' | 'uid-taken' | 'unknown' }>}
 */
export async function createAccountProfile(db, uid, profile) {
  try {
    const now = new Date().toISOString();
    const inserted = await db
      .prepare(
        `INSERT INTO account_profiles
           (uid, username, username_normalized, first_name, last_name, terms_version, terms_accepted_at, created_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?)
         ON CONFLICT DO NOTHING`,
      )
      .bind(
        uid,
        profile.username,
        profile.usernameNormalized,
        profile.firstName,
        profile.lastName,
        profile.termsVersion,
        now,
        now,
      )
      .run();

    if (inserted.meta?.changes === 1) {
      return { ok: true };
    }

    const existing = await fetchAccountProfile(db, uid);
    if (existing === null) {
      // The only other unique key on this table is username_normalized.
      // Using the post-conflict state instead of a SQLite error string keeps
      // the API stable across D1/SQLite error-message variants.
      return { ok: false, code: 'username-taken' };
    }
    if (existing.username !== profile.username
      || existing.firstName !== profile.firstName
      || existing.lastName !== profile.lastName) {
      return { ok: false, code: 'uid-taken' };
    }

    await db
      .prepare('UPDATE account_profiles SET terms_version = ?, terms_accepted_at = ? WHERE uid = ?')
      .bind(profile.termsVersion, now, uid)
      .run();
    return { ok: true };
  } catch {
    return { ok: false, code: 'unknown' };
  }
}

/** Account deletion is blocked until every linked billing flow is cancelled. */
export async function isAccountDeletionBlocked(db, uid) {
  const billing = await db.prepare(
    "SELECT 1 AS blocked FROM billing_checkout_intents WHERE account_uid = ? AND state <> 'cancelled' LIMIT 1",
  ).bind(uid).first();
  return billing !== null;
}

/** Called only after Firebase has confirmed deletion of the account. */
export async function deleteAccountProfile(db, uid) {
  await db.prepare('DELETE FROM account_profiles WHERE uid = ?').bind(uid).run();
}

export async function deleteAccount(db, uid, deleteFirebase) {
  if (await isAccountDeletionBlocked(db, uid)) return { ok: false, code: 'billing-cancellation-required' };
  const now = new Date();
  const validAfter = Math.floor(now.getTime() / 1000);
  await db.batch([
    db.prepare(
      `INSERT INTO account_auth_cutoffs (account_uid, valid_after, updated_at) VALUES (?, ?, ?)
       ON CONFLICT(account_uid) DO UPDATE SET valid_after = excluded.valid_after, updated_at = excluded.updated_at`,
    ).bind(uid, validAfter, now.toISOString()),
    db.prepare(
      `INSERT INTO account_deletion_jobs (account_uid, requested_at) VALUES (?, ?)
       ON CONFLICT(account_uid) DO NOTHING`,
    ).bind(uid, now.toISOString()),
  ]);

  try {
    await completeAccountDeletion(db, uid, deleteFirebase);
    return { ok: true, pending: false };
  } catch {
    return { ok: true, pending: true };
  }
}

export async function completeAccountDeletion(db, uid, deleteFirebase) {
  await deleteFirebase(uid);
  await deleteAccountProfile(db, uid);
  await db.prepare('DELETE FROM account_deletion_jobs WHERE account_uid = ?').bind(uid).run();
}

export async function resumeAccountDeletions(db, deleteFirebase, limit = 20) {
  const rows = await db.prepare(
    'SELECT account_uid FROM account_deletion_jobs ORDER BY requested_at LIMIT ?',
  ).bind(limit).all();
  for (const row of rows.results ?? []) {
    try {
      await completeAccountDeletion(db, row.account_uid, deleteFirebase);
    } catch {
      // The durable job remains for the next scheduled retry.
    }
  }
}

/**
 * Validates a standalone username -- the availability probe carries only
 * that one field, not a whole profile -- and returns its normalized form,
 * or null when the value could never be accepted by createAccountProfile
 * anyway. Sharing USERNAME_PATTERN keeps the probe from ever reporting
 * "available" for a name the insert would reject.
 *
 * @param {unknown} value
 * @returns {string | null}
 */
export function normalizeUsername(value) {
  if (typeof value !== 'string') {
    return null;
  }

  const trimmed = value.trim();
  return USERNAME_PATTERN.test(trimmed) ? trimmed.toLowerCase() : null;
}

/**
 * True when no account currently holds `usernameNormalized`. Advisory only:
 * uniqueness is still enforced by the UNIQUE index at insert time, so a name
 * can be claimed between this check and the actual registration. The caller
 * must keep handling the 409 from createAccountProfile.
 *
 * @param {D1Database} db
 * @param {string} usernameNormalized
 * @returns {Promise<boolean>}
 */
export async function isUsernameAvailable(db, usernameNormalized) {
  const row = await db
    .prepare('SELECT 1 AS taken FROM account_profiles WHERE username_normalized = ?')
    .bind(usernameNormalized)
    .first();
  return row === null;
}

/**
 * Reads back the profile for `uid`, the Firebase UID from the verified ID
 * token -- never a client-supplied identifier, so one account can only ever
 * read its own profile. Used on login and session restore, when the client
 * needs the first name for the app's greeting but only has an ID token, not
 * the fields Firebase itself never stored.
 *
 * @param {D1Database} db
 * @param {string} uid
 * @returns {Promise<{ username: string, firstName: string, lastName: string, termsVersion: string | null } | null>}
 */
export async function fetchAccountProfile(db, uid) {
  const row = await db
    .prepare('SELECT username, first_name, last_name, terms_version FROM account_profiles WHERE uid = ?')
    .bind(uid)
    .first();
  if (row === null) {
    return null;
  }

  return {
    username: row.username,
    firstName: row.first_name,
    lastName: row.last_name,
    termsVersion: row.terms_version,
  };
}
