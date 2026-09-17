/** Parses a JSON Request or Response without buffering more than `maximumBytes`. */
export async function readBoundedJson(message, maximumBytes) {
  const declaredLength = Number(message.headers.get('Content-Length'));
  if ((Number.isFinite(declaredLength) && declaredLength > maximumBytes) || !message.body) {
    return null;
  }

  const reader = message.body.getReader();
  const decoder = new TextDecoder('utf-8', { fatal: true });
  let bytesRead = 0;
  let json = '';
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      bytesRead += value.byteLength;
      if (bytesRead > maximumBytes) {
        await reader.cancel();
        return null;
      }
      json += decoder.decode(value, { stream: true });
    }
    json += decoder.decode();
    return JSON.parse(json);
  } catch {
    // Parser errors can contain fragments of credentials or provider PII.
    console.error('readBoundedJson failed: invalid or unreadable JSON');
    return null;
  }
}

/** Admin JSON endpoints intentionally accept only the exact media type. */
export function hasExactJsonContentType(request) {
  return request.headers.get('Content-Type') === 'application/json';
}

/**
 * Account-facing JSON endpoints also accept an explicit `charset=utf-8`.
 * Deliberately looser than `hasExactJsonContentType`; naming the difference
 * keeps it a decision instead of an inline regex that drifts unnoticed.
 */
export function hasJsonContentType(request) {
  return /^application\/json(?:\s*;\s*charset=utf-8)?$/i.test(request.headers.get('Content-Type') ?? '');
}

/** Single JSON response shape for every route; four modules built it separately. */
export function jsonResponse(body, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...extraHeaders },
  });
}

/** A JSON object, never an array and never null — the shape every payload guard means. */
export function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/** True when `value` is a plain object carrying exactly `keys`, no more and no fewer. */
export function hasExactKeys(value, keys) {
  if (!isPlainObject(value)) return false;
  return Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
}
