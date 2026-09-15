const CODE_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
// The 32-symbol alphabet maps bytes uniformly through their low five bits.
const CODE_ALPHABET_MASK = CODE_ALPHABET.length - 1;
if ((CODE_ALPHABET.length & CODE_ALPHABET_MASK) !== 0) throw new Error('discord-link-code-alphabet-must-be-power-of-two');
const CODE_LENGTH = 10;
const CODE_TTL_MS = 10 * 60 * 1000;
const DISCORD_ID = /^\d{17,20}$/;

function toBase64Url(bytes) {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
}

async function hmac(value, secret) {
  if (typeof secret !== 'string' || secret.length < 32) return null;
  const key = await crypto.subtle.importKey(
    'raw', new TextEncoder().encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign'],
  );
  const signature = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(value));
  return toBase64Url(new Uint8Array(signature));
}

function constantTimeEqual(left, right) {
  if (typeof left !== 'string' || typeof right !== 'string' || left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index += 1) {
    difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  }
  return difference === 0;
}

export function normalizeLinkCode(value) {
  if (typeof value !== 'string') return null;
  const code = value.toUpperCase().replaceAll('-', '').replaceAll(' ', '');
  return code.length === CODE_LENGTH && [...code].every(character => CODE_ALPHABET.includes(character))
    ? code
    : null;
}

export async function authorizeDiscordBot(request, secret) {
  const authorization = request.headers.get('Authorization') ?? '';
  const candidate = authorization.startsWith('Bearer ') ? authorization.slice(7) : '';
  const [actual, expected] = await Promise.all([
    hmac('discord-service-auth', candidate),
    hmac('discord-service-auth', secret),
  ]);
  return actual !== null && expected !== null && constantTimeEqual(actual, expected);
}

export async function createDiscordLinkCode(db, uid, secret, now = new Date()) {
  const bytes = crypto.getRandomValues(new Uint8Array(CODE_LENGTH));
  const code = [...bytes].map(value => CODE_ALPHABET[value & CODE_ALPHABET_MASK]).join('');
  const codeHash = await hmac(code, secret);
  if (codeHash === null) throw new Error('discord-link-misconfigured');
  const createdAt = now.toISOString();
  const expiresAt = new Date(now.getTime() + CODE_TTL_MS).toISOString();
  await db.batch([
    db.prepare('DELETE FROM discord_link_codes WHERE account_uid = ?').bind(uid),
    db.prepare(`INSERT INTO discord_link_codes
      (code_hash, account_uid, expires_at, created_at) VALUES (?, ?, ?, ?)`)
      .bind(codeHash, uid, expiresAt, createdAt),
  ]);
  return { code, expiresAt };
}

export async function redeemDiscordLinkCode(db, rawCode, discordUserId, secret, now = new Date()) {
  const code = normalizeLinkCode(rawCode);
  if (code === null || !DISCORD_ID.test(discordUserId ?? '')) return { ok: false, code: 'invalid-link' };
  const codeHash = await hmac(code, secret);
  if (codeHash === null) throw new Error('discord-link-misconfigured');
  const nowIso = now.toISOString();
  const claimed = await db.prepare(`UPDATE discord_link_codes SET used_at = ?
    WHERE code_hash = ? AND used_at IS NULL AND expires_at > ?`)
    .bind(nowIso, codeHash, nowIso).run();
  if (claimed.meta?.changes !== 1) return { ok: false, code: 'invalid-link' };
  const linkCode = await db.prepare('SELECT account_uid FROM discord_link_codes WHERE code_hash = ?')
    .bind(codeHash).first();
  if (typeof linkCode?.account_uid !== 'string') return { ok: false, code: 'invalid-link' };
  try {
    await db.prepare(`INSERT INTO discord_account_links
      (account_uid, discord_user_id, created_at, updated_at) VALUES (?, ?, ?, ?)
      ON CONFLICT(account_uid) DO UPDATE SET discord_user_id = excluded.discord_user_id,
        updated_at = excluded.updated_at`)
      .bind(linkCode.account_uid, discordUserId, nowIso, nowIso).run();
  } catch (error) {
    if (String(error).includes('UNIQUE constraint failed: discord_account_links.discord_user_id')) {
      return { ok: false, code: 'discord-already-linked' };
    }
    throw error;
  }
  return { ok: true };
}

export async function fetchDiscordRoleLinks(db, nowIso = new Date().toISOString()) {
  const result = await db.prepare(`SELECT l.discord_user_id,
    CASE
      WHEN MAX(CASE WHEN e.entitlement_key = 'ralven_max' THEN 1 ELSE 0 END) = 1 THEN 'max'
      WHEN MAX(CASE WHEN e.entitlement_key = 'ralven_pro' THEN 1 ELSE 0 END) = 1 THEN 'pro'
      ELSE 'free'
    END AS tier
    FROM discord_account_links l
    LEFT JOIN account_entitlements e ON e.account_uid = l.account_uid
      AND e.entitlement_key IN ('ralven_pro', 'ralven_max')
      AND e.state IN ('active', 'grace_period')
      AND e.valid_from <= ? AND e.valid_until > ?
    GROUP BY l.account_uid, l.discord_user_id
    ORDER BY l.discord_user_id`).bind(nowIso, nowIso).all();
  return Array.isArray(result?.results) ? result.results : [];
}
