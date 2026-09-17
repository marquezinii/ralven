// Small Web Crypto helpers shared by the admin auth modules. Uses only
// globalThis.crypto (available natively in the Workers runtime and in
// modern Node, which is why these can be unit tested with plain `node --test`
// without Miniflare) -- no third-party crypto dependency.

// OWASP 2023 recommends 210,000 for PBKDF2-SHA256, but the Workers runtime
// (based on BoringSSL, not Node's OpenSSL) hard-caps PBKDF2 at 100,000
// iterations and throws NotSupportedError above that -- confirmed against
// the real deployed Worker. 100,000 is itself still an accepted, merely
// slightly dated, OWASP baseline for PBKDF2-SHA256, so this is a safe
// ceiling, not a meaningful weakening.
const PBKDF2_ITERATIONS = 100_000;
const SALT_BYTES = 16;
const HASH_BYTES = 32;

function toBase64(bytes) {
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }

  return btoa(binary);
}

function fromBase64(value) {
  return Uint8Array.from(atob(value), (character) => character.charCodeAt(0));
}

export function toBase64Url(bytes) {
  return toBase64(bytes).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
}

function timingSafeEqual(a, b) {
  if (a.length !== b.length) {
    return false;
  }

  let diff = 0;
  for (let i = 0; i < a.length; i++) {
    diff |= a[i] ^ b[i];
  }

  return diff === 0;
}

async function deriveBits(password, salt, iterations) {
  const keyMaterial = await crypto.subtle.importKey(
    'raw',
    new TextEncoder().encode(password),
    'PBKDF2',
    false,
    ['deriveBits'],
  );
  const bits = await crypto.subtle.deriveBits(
    { name: 'PBKDF2', salt, iterations, hash: 'SHA-256' },
    keyMaterial,
    HASH_BYTES * 8,
  );
  return new Uint8Array(bits);
}

/**
 * Hashes a plaintext password into the self-contained format
 * `pbkdf2$<iterations>$<saltBase64>$<hashBase64>`. Run this once, locally
 * (see scripts/hash-admin-password.mjs), and store only the result as the
 * `ADMIN_PASSWORD_HASH` Worker secret -- the plaintext password never
 * touches the repository or the deployed Worker.
 */
export async function hashPassword(password, iterations = PBKDF2_ITERATIONS) {
  const salt = crypto.getRandomValues(new Uint8Array(SALT_BYTES));
  const hash = await deriveBits(password, salt, iterations);
  return `pbkdf2$${iterations}$${toBase64(salt)}$${toBase64(hash)}`;
}

/**
 * Verifies a submitted password against a stored hash produced by
 * {@link hashPassword}. Never throws on a malformed stored hash -- returns
 * `false`, so a misconfigured secret fails closed instead of crashing the
 * Worker.
 */
export async function verifyPassword(password, storedHash) {
  if (typeof storedHash !== 'string') {
    return false;
  }

  const parts = storedHash.split('$');
  if (parts.length !== 4 || parts[0] !== 'pbkdf2') {
    return false;
  }

  const iterations = Number.parseInt(parts[1], 10);
  if (!Number.isFinite(iterations) || iterations <= 0) {
    return false;
  }

  let salt;
  let expectedHash;
  try {
    salt = fromBase64(parts[2]);
    expectedHash = fromBase64(parts[3]);
  } catch {
    return false;
  }

  const actualHash = await deriveBits(password, salt, iterations);
  return timingSafeEqual(actualHash, expectedHash);
}

/** Generates a random, URL-safe session ID (256 bits of entropy). */
export function generateSessionId() {
  return toBase64Url(crypto.getRandomValues(new Uint8Array(32)));
}

/** Stores only a one-way digest of a bearer session token in D1. */
export async function hashSessionId(sessionId) {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(sessionId));
  return toBase64Url(new Uint8Array(digest));
}

/** Derives a session-bound CSRF token without exposing the HttpOnly cookie. */
export async function createCsrfToken(sessionId, secret) {
  if (typeof sessionId !== 'string' || typeof secret !== 'string' || secret.length < 32) {
    return null;
  }

  const key = await crypto.subtle.importKey(
    'raw',
    new TextEncoder().encode(secret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const signature = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(sessionId));
  return toBase64Url(new Uint8Array(signature));
}

/** Verifies the custom CSRF header with a fixed-time comparison. */
export async function isValidCsrfToken(token, sessionId, secret) {
  const expected = await createCsrfToken(sessionId, secret);
  if (typeof token !== 'string' || !expected || token.length !== expected.length) {
    return false;
  }

  return timingSafeEqual(new TextEncoder().encode(token), new TextEncoder().encode(expected));
}

/**
 * HMACs a client IP with the `IP_HASH_SECRET` Worker secret so brute-force
 * tracking never stores a reversible IP address, only a keyed digest of it.
 */
export async function hashIp(ip, secret) {
  const key = await crypto.subtle.importKey(
    'raw',
    new TextEncoder().encode(secret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const signature = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(ip));
  return toBase64Url(new Uint8Array(signature));
}
