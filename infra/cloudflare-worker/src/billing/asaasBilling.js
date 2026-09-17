import { jsonResponse as json, readBoundedJson } from '../requestSecurity.js';
import { asaasApi, BILLING_PROVIDER, BillingError, cents, eventKey, monthlyPeriodEnd,
  providerDate, PROVIDER_ID } from './asaasApi.js';

const EVENT_ID = /^[A-Za-z0-9_&-]{1,128}$/;
const CHECKOUT_EVENTS = new Set(['CHECKOUT_CREATED', 'CHECKOUT_PAID', 'CHECKOUT_CANCELED', 'CHECKOUT_EXPIRED']);
const SUBSCRIPTION_EVENTS = new Set(['SUBSCRIPTION_CREATED', 'SUBSCRIPTION_UPDATED', 'SUBSCRIPTION_INACTIVATED', 'SUBSCRIPTION_DELETED']);
const PAYMENT_EVENTS = new Set([
  'PAYMENT_CREATED', 'PAYMENT_UPDATED', 'PAYMENT_CONFIRMED', 'PAYMENT_RECEIVED',
  'PAYMENT_CREDIT_CARD_CAPTURE_REFUSED', 'PAYMENT_OVERDUE', 'PAYMENT_DELETED', 'PAYMENT_RESTORED',
  'PAYMENT_AWAITING_RISK_ANALYSIS', 'PAYMENT_APPROVED_BY_RISK_ANALYSIS', 'PAYMENT_REPROVED_BY_RISK_ANALYSIS',
  'PAYMENT_REFUNDED', 'PAYMENT_PARTIALLY_REFUNDED', 'PAYMENT_REFUND_IN_PROGRESS', 'PAYMENT_REFUND_DENIED',
  'PAYMENT_CHARGEBACK_REQUESTED', 'PAYMENT_CHARGEBACK_DISPUTE', 'PAYMENT_AWAITING_CHARGEBACK_REVERSAL',
]);
const DAY_MS = 24 * 60 * 60 * 1000;
const CHECKOUT_RECONCILIATION_WINDOW_MS = 180 * DAY_MS;
const PAYMENT_RECHECK_INTERVAL_MS = 15 * 60 * 1000;
const HISTORICAL_RECHECK_INTERVAL_MS = 30 * DAY_MS;
// ponytail: ten details plus one list cap at 21 calls; queue reconciliation if a checkout legitimately exceeds that budget.
const MAX_PAYMENT_RECONCILIATIONS = 10;

async function sameSecret(actual, expected) {
  if (typeof actual !== 'string' || typeof expected !== 'string') return false;
  const encoder = new TextEncoder();
  const [left, right] = await Promise.all([
    crypto.subtle.digest('SHA-256', encoder.encode(actual)),
    crypto.subtle.digest('SHA-256', encoder.encode(expected)),
  ]);
  const a = new Uint8Array(left); const b = new Uint8Array(right);
  let difference = 0;
  for (let i = 0; i < a.length; i++) difference |= a[i] ^ b[i];
  return difference === 0 && actual.length === expected.length;
}

async function startEvent(db, requestId, resourceId, now) {
  const existing = await db.prepare(`SELECT resource_id, processing_outcome FROM billing_webhook_events
    WHERE provider = ? AND provider_request_id = ?`).bind(BILLING_PROVIDER, requestId).first();
  if (existing?.resource_id !== undefined) {
    if (existing.resource_id !== resourceId) throw new BillingError('request-id-conflict', 409);
    if (['processed', 'ignored'].includes(existing.processing_outcome)) return false;
  }
  await db.prepare(`INSERT INTO billing_webhook_events
    (provider, provider_request_id, resource_id, received_at, processing_outcome, processed_at)
    VALUES (?, ?, ?, ?, 'pending', NULL) ON CONFLICT(provider, provider_request_id) DO NOTHING`)
    .bind(BILLING_PROVIDER, requestId, resourceId, now).run();
  return true;
}

async function finishEvent(db, requestId, resourceId, outcome, now) {
  await db.prepare(`UPDATE billing_webhook_events SET processing_outcome = ?, processed_at = ?
    WHERE provider = ? AND provider_request_id = ? AND resource_id = ?`)
    .bind(outcome, now, BILLING_PROVIDER, requestId, resourceId).run();
}

async function eventRowId(db, requestId, resourceId) {
  const row = await db.prepare(`SELECT id FROM billing_webhook_events
    WHERE provider = ? AND provider_request_id = ? AND resource_id = ?`)
    .bind(BILLING_PROVIDER, requestId, resourceId).first();
  if (!Number.isSafeInteger(row?.id)) throw new BillingError('billing-temporarily-unavailable');
  return row.id;
}

function subscriptionState(subscription) {
  if (subscription.deleted === true || subscription.status === 'EXPIRED') return 'cancelled';
  if (subscription.status === 'INACTIVE') return 'paused';
  if (subscription.status === 'ACTIVE') return 'authorized';
  return null;
}

function validateSubscription(subscription, intent) {
  const state = subscriptionState(subscription);
  return subscription && PROVIDER_ID.test(subscription.id ?? '') && state !== null
    && subscription.billingType === 'CREDIT_CARD' && subscription.cycle === 'MONTHLY'
    && cents(subscription.value) === intent.amount_cents ? state : null;
}

async function persistSubscription(db, intent, subscription, requestId, resourceId, now) {
  const state = validateSubscription(subscription, intent);
  if (state === null) throw new BillingError('billing-payment-mismatch', 409);
  const owner = await db.prepare(`SELECT account_uid, checkout_intent_id FROM billing_subscriptions
    WHERE provider = ? AND provider_subscription_id = ?`).bind(BILLING_PROVIDER, subscription.id).first();
  if (owner && (owner.account_uid !== intent.account_uid || owner.checkout_intent_id !== intent.id)) {
    throw new BillingError('billing-payment-mismatch', 409);
  }
  const lastEventId = await eventRowId(db, requestId, resourceId);
  await db.batch([
    db.prepare(`INSERT INTO billing_subscriptions
      (id, account_uid, checkout_intent_id, provider, provider_subscription_id, offer_key, state,
       provider_updated_at, last_event_id, created_at, updated_at)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(provider, provider_subscription_id) DO UPDATE SET state = excluded.state,
        provider_updated_at = excluded.provider_updated_at, last_event_id = excluded.last_event_id,
        updated_at = excluded.updated_at
      WHERE billing_subscriptions.account_uid = excluded.account_uid
        AND billing_subscriptions.checkout_intent_id = excluded.checkout_intent_id`)
      .bind(`${BILLING_PROVIDER}:${subscription.id}`, intent.account_uid, intent.id, BILLING_PROVIDER,
        subscription.id, intent.offer_key, state, now, lastEventId, now, now),
    db.prepare(`UPDATE billing_checkout_intents SET state = ?, updated_at = ?
      WHERE id = ? AND account_uid = ? AND provider = ? AND provider_checkout_id = ?
        AND EXISTS (SELECT 1 FROM billing_subscriptions WHERE provider = ?
          AND provider_subscription_id = ? AND account_uid = ? AND checkout_intent_id = ?)`)
      .bind(state === 'cancelled' ? 'cancelled' : 'completed', now, intent.id, intent.account_uid,
        BILLING_PROVIDER, intent.provider_checkout_id, BILLING_PROVIDER, subscription.id,
        intent.account_uid, intent.id),
  ]);
  const linked = await db.prepare(`SELECT id FROM billing_subscriptions WHERE provider = ?
    AND provider_subscription_id = ? AND account_uid = ? AND checkout_intent_id = ?`)
    .bind(BILLING_PROVIDER, subscription.id, intent.account_uid, intent.id).first();
  if (!linked) throw new BillingError('billing-temporarily-unavailable');
  return linked.id;
}

function completedRefundCents(payment) {
  if (payment.refunds == null) return 0;
  if (!Array.isArray(payment.refunds)) return null;
  let total = 0;
  for (const refund of payment.refunds) {
    if (!refund || !['PENDING', 'CANCELLED', 'DONE'].includes(refund.status)) return null;
    if (refund.status !== 'DONE') continue;
    const value = cents(refund.value);
    if (value === null || !Number.isSafeInteger(total + value)) return null;
    total += value;
  }
  return total;
}

function paymentState(payment, refundedCents) {
  if (refundedCents > 0 || payment.status === 'REFUNDED') return 'refunded';
  if (payment.chargeback && ['REQUESTED', 'DISPUTE', 'AWAITING_REVERSAL'].includes(payment.chargeback.status)) return 'charged_back';
  if (['CONFIRMED', 'RECEIVED'].includes(payment.status)) return 'approved';
  if (['CREDIT_CARD_CAPTURE_REFUSED', 'REPROVED_BY_RISK_ANALYSIS'].includes(payment.status)) return 'rejected';
  if (payment.deleted === true || payment.status === 'DELETED') return 'cancelled';
  if (['PENDING', 'OVERDUE', 'AWAITING_RISK_ANALYSIS', 'AUTHORIZED', 'REFUND_IN_PROGRESS'].includes(payment.status)) return 'pending';
  return null;
}

export function refreshEntitlementStatement(db, uid, now) {
  return db.prepare(`INSERT INTO account_entitlements
    (account_uid, entitlement_key, state, subscription_id, valid_from, valid_until,
     provider_updated_at, last_event_id, updated_at)
    SELECT s.account_uid, entitlement.entitlement_key, 'active', s.id, p.period_start, p.period_end,
      p.provider_updated_at, p.last_event_id, ?
    FROM billing_payments p JOIN billing_subscriptions s ON s.id = p.subscription_id
    CROSS JOIN (SELECT 'ralven_pro' AS entitlement_key UNION ALL SELECT 'ralven_ai') entitlement
    WHERE s.account_uid = ? AND p.state = 'approved' AND p.refunded_cents = 0
      AND p.period_start <= ? AND p.period_end > ?
    ORDER BY p.period_end DESC, p.provider_payment_id DESC, entitlement.entitlement_key LIMIT 2
    ON CONFLICT(account_uid, entitlement_key) DO UPDATE SET state = 'active',
      subscription_id = excluded.subscription_id, valid_from = excluded.valid_from,
      valid_until = excluded.valid_until, provider_updated_at = excluded.provider_updated_at,
      last_event_id = excluded.last_event_id, updated_at = excluded.updated_at`)
    .bind(now, uid, now, now);
}

export function revokeEntitlementStatement(db, uid, now) {
  return db.prepare(`UPDATE account_entitlements SET state = 'revoked', updated_at = ?
    WHERE account_uid = ? AND entitlement_key IN ('ralven_pro', 'ralven_ai') AND state <> 'revoked'
      AND NOT EXISTS (SELECT 1 FROM billing_payments p JOIN billing_subscriptions s ON s.id = p.subscription_id
        WHERE s.account_uid = ? AND p.state = 'approved' AND p.refunded_cents = 0
          AND p.period_start <= ? AND p.period_end > ?)`)
    .bind(now, uid, uid, now, now);
}

async function intentForPayment(db, payment, expectedIntent) {
  const byCheckout = PROVIDER_ID.test(payment.checkoutSession ?? '')
    ? await db.prepare(`SELECT * FROM billing_checkout_intents WHERE provider = ? AND provider_checkout_id = ?`)
      .bind(BILLING_PROVIDER, payment.checkoutSession).first() : null;
  const bySubscription = PROVIDER_ID.test(payment.subscription ?? '')
    ? await db.prepare(`SELECT i.* FROM billing_checkout_intents i JOIN billing_subscriptions s
      ON s.checkout_intent_id = i.id WHERE s.provider = ? AND s.provider_subscription_id = ?`)
      .bind(BILLING_PROVIDER, payment.subscription).first() : null;
  const intent = expectedIntent ?? byCheckout ?? bySubscription;
  if (intent && ((byCheckout && byCheckout.id !== intent.id) || (bySubscription && bySubscription.id !== intent.id))) {
    throw new BillingError('billing-payment-mismatch', 409);
  }
  return intent;
}

export async function reconcilePayment(env, paymentId, requestId = null, options = {}, expectedIntent = null) {
  if (!PROVIDER_ID.test(paymentId ?? '')) throw new BillingError('invalid-provider-response');
  if (requestId) {
    const existing = await env.TELEMETRY_DB.prepare(`SELECT resource_id, processing_outcome FROM billing_webhook_events
      WHERE provider = ? AND provider_request_id = ?`).bind(BILLING_PROVIDER, requestId).first();
    if (existing) {
      if (existing.resource_id !== paymentId) throw new BillingError('request-id-conflict', 409);
      if (['processed', 'ignored'].includes(existing.processing_outcome)) return true;
    }
  }
  const payment = await asaasApi(env, `/payments/${encodeURIComponent(paymentId)}`, options);
  if (payment.id !== paymentId || payment.billingType !== 'CREDIT_CARD' || !PROVIDER_ID.test(payment.subscription ?? '')) {
    throw new BillingError('invalid-provider-response');
  }
  const refundedCents = completedRefundCents(payment);
  const state = paymentState(payment, refundedCents);
  const periodStart = providerDate(payment.confirmedDate ?? payment.paymentDate ?? payment.clientPaymentDate ?? payment.dueDate);
  const periodEnd = periodStart && monthlyPeriodEnd(periodStart);
  if (refundedCents === null || state === null || periodStart === null || periodEnd === null) {
    throw new BillingError('invalid-provider-response');
  }
  const intent = await intentForPayment(env.TELEMETRY_DB, payment, expectedIntent);
  const key = requestId ?? await eventKey('asaas-payment-sync',
    `${payment.id}:${state}:${refundedCents}:${periodStart}:${periodEnd}`);
  const now = new Date().toISOString();
  if (!intent) {
    if (await startEvent(env.TELEMETRY_DB, key, paymentId, now)) await finishEvent(env.TELEMETRY_DB, key, paymentId, 'ignored', now);
    return false;
  }
  if (intent.provider !== BILLING_PROVIDER || cents(payment.value) !== intent.amount_cents
    || (payment.checkoutSession && payment.checkoutSession !== intent.provider_checkout_id)) {
    throw new BillingError('billing-payment-mismatch', 409);
  }
  if (refundedCents > intent.amount_cents) throw new BillingError('invalid-provider-response');
  if (!await startEvent(env.TELEMETRY_DB, key, paymentId, now)) {
    await env.TELEMETRY_DB.prepare(`UPDATE billing_payments SET provider_updated_at = ?, updated_at = ?
      WHERE provider_payment_id = ?`).bind(now, now, paymentId).run();
    return true;
  }
  const subscription = await asaasApi(env, `/subscriptions/${encodeURIComponent(payment.subscription)}`, options);
  const subscriptionId = await persistSubscription(env.TELEMETRY_DB, intent, subscription, key, paymentId, now);
  const paymentOwner = await env.TELEMETRY_DB.prepare(
    'SELECT subscription_id FROM billing_payments WHERE provider_payment_id = ?').bind(paymentId).first();
  if (paymentOwner && paymentOwner.subscription_id !== subscriptionId) {
    throw new BillingError('billing-payment-mismatch', 409);
  }
  const lastEventId = await eventRowId(env.TELEMETRY_DB, key, paymentId);
  await env.TELEMETRY_DB.batch([
    env.TELEMETRY_DB.prepare(`INSERT INTO billing_payments
      (provider_payment_id, subscription_id, state, amount_cents, refunded_cents, currency,
       period_start, period_end, provider_updated_at, last_event_id, updated_at)
      VALUES (?, ?, ?, ?, ?, 'BRL', ?, ?, ?, ?, ?)
      ON CONFLICT(provider_payment_id) DO UPDATE SET state = excluded.state,
        amount_cents = excluded.amount_cents, refunded_cents = excluded.refunded_cents,
        period_start = excluded.period_start, period_end = excluded.period_end,
        provider_updated_at = excluded.provider_updated_at, last_event_id = excluded.last_event_id,
        updated_at = excluded.updated_at WHERE billing_payments.subscription_id = excluded.subscription_id`)
      .bind(paymentId, subscriptionId, state, intent.amount_cents, refundedCents,
        periodStart, periodEnd, now, lastEventId, now),
    refreshEntitlementStatement(env.TELEMETRY_DB, intent.account_uid, now),
    revokeEntitlementStatement(env.TELEMETRY_DB, intent.account_uid, now),
    env.TELEMETRY_DB.prepare(`UPDATE billing_webhook_events SET processing_outcome = 'processed', processed_at = ?
      WHERE provider = ? AND provider_request_id = ? AND resource_id = ?`)
      .bind(now, BILLING_PROVIDER, key, paymentId),
  ]);
  return true;
}

export async function reconcileCheckout(env, intent, options = {}) {
  if (!PROVIDER_ID.test(intent.provider_checkout_id ?? '')) throw new BillingError('invalid-provider-response');
  const now = Date.now();
  const windowStart = new Date(now - CHECKOUT_RECONCILIATION_WINDOW_MS).toISOString();
  const recentRecheckAfter = now - PAYMENT_RECHECK_INTERVAL_MS;
  const historicalRecheckBefore = new Date(now - HISTORICAL_RECHECK_INTERVAL_MS).toISOString();
  const result = await asaasApi(env, `/payments?checkoutSession=${encodeURIComponent(intent.provider_checkout_id)}`
    + `&dueDate%5Bge%5D=${windowStart.slice(0, 10)}&limit=100&offset=0`, options);
  if (!Array.isArray(result.data) || result.hasMore === true) throw new BillingError('billing-checkout-reconciliation-required', 409);
  let remaining = MAX_PAYMENT_RECONCILIATIONS;
  for (const item of result.data) {
    if (!PROVIDER_ID.test(item?.id ?? '')) throw new BillingError('invalid-provider-response');
    if (remaining === 0) continue;
    const existing = await env.TELEMETRY_DB.prepare(`SELECT p.period_end, p.provider_updated_at
      FROM billing_payments p JOIN billing_subscriptions s ON s.id = p.subscription_id
      WHERE p.provider_payment_id = ? AND s.checkout_intent_id = ? AND s.account_uid = ?`)
      .bind(item.id, intent.id, intent.account_uid).first();
    const periodEnd = Date.parse(existing?.period_end ?? '');
    const providerUpdatedAt = Date.parse(existing?.provider_updated_at ?? '');
    if ((Number.isFinite(periodEnd) && periodEnd < Date.parse(windowStart))
      || (Number.isFinite(providerUpdatedAt) && providerUpdatedAt >= recentRecheckAfter)) continue;
    await reconcilePayment(env, item.id, null, options, intent);
    remaining--;
  }
  if (remaining > 0) {
    const historical = await env.TELEMETRY_DB.prepare(`SELECT p.provider_payment_id
      FROM billing_payments p JOIN billing_subscriptions s ON s.id = p.subscription_id
      WHERE s.checkout_intent_id = ? AND s.account_uid = ? AND p.period_end < ? AND p.provider_updated_at < ?
      ORDER BY p.provider_updated_at, p.provider_payment_id LIMIT 1`)
      .bind(intent.id, intent.account_uid, windowStart, historicalRecheckBefore).first();
    if (historical) {
      if (!PROVIDER_ID.test(historical.provider_payment_id ?? '')) throw new BillingError('invalid-provider-response');
      await reconcilePayment(env, historical.provider_payment_id, null, options, intent);
    }
  }
  return result.data.length;
}

async function reconcileSubscriptionEvent(env, subscriptionId, requestId, event, options) {
  const db = env.TELEMETRY_DB;
  const row = await db.prepare(`SELECT i.* FROM billing_checkout_intents i JOIN billing_subscriptions s
    ON s.checkout_intent_id = i.id WHERE s.provider = ? AND s.provider_subscription_id = ?`)
    .bind(BILLING_PROVIDER, subscriptionId).first();
  const now = new Date().toISOString();
  if (!row) {
    if (await startEvent(db, requestId, subscriptionId, now)) await finishEvent(db, requestId, subscriptionId, 'ignored', now);
    return;
  }
  if (!await startEvent(db, requestId, subscriptionId, now)) return;
  if (event === 'SUBSCRIPTION_DELETED') {
    await db.batch([
      db.prepare(`UPDATE billing_subscriptions SET state = 'cancelled', provider_updated_at = ?, updated_at = ?
        WHERE provider = ? AND provider_subscription_id = ?`).bind(now, now, BILLING_PROVIDER, subscriptionId),
      db.prepare(`UPDATE billing_checkout_intents SET state = 'cancelled', updated_at = ? WHERE id = ?`).bind(now, row.id),
      db.prepare(`UPDATE billing_webhook_events SET processing_outcome = 'processed', processed_at = ?
        WHERE provider = ? AND provider_request_id = ?`).bind(now, BILLING_PROVIDER, requestId),
    ]);
    return;
  }
  const subscription = await asaasApi(env, `/subscriptions/${encodeURIComponent(subscriptionId)}`, options);
  await persistSubscription(db, row, subscription, requestId, subscriptionId, now);
  await finishEvent(db, requestId, subscriptionId, 'processed', now);
}

export async function handleAsaasWebhook(request, env, options = {}) {
  if (!await sameSecret(request.headers.get('asaas-access-token'), env?.ASAAS_WEBHOOK_TOKEN)) {
    return json({ error: 'invalid-webhook-token' }, 401);
  }
  if (!/^application\/json(?:\s*;\s*charset=utf-8)?$/i.test(request.headers.get('Content-Type') ?? '')) {
    return json({ error: 'invalid-content-type' }, 415);
  }
  const body = await readBoundedJson(request, 64 * 1024);
  if (!body || typeof body !== 'object' || !EVENT_ID.test(body.id ?? '') || typeof body.event !== 'string') {
    return json({ error: 'invalid-webhook' }, 400);
  }
  const event = body.event;
  const resourceId = CHECKOUT_EVENTS.has(event) ? body.checkout?.id
    : SUBSCRIPTION_EVENTS.has(event) ? body.subscription?.id
      : PAYMENT_EVENTS.has(event) ? body.payment?.id : null;
  if (!PROVIDER_ID.test(resourceId ?? '')) return json({ error: 'invalid-webhook' }, 400);
  try {
    if (CHECKOUT_EVENTS.has(event)) {
      const now = new Date().toISOString();
      if (!await startEvent(env.TELEMETRY_DB, body.id, resourceId, now)) return json({ accepted: true });
      const intent = await env.TELEMETRY_DB.prepare(`SELECT * FROM billing_checkout_intents
        WHERE provider = ? AND provider_checkout_id = ?`).bind(BILLING_PROVIDER, resourceId).first();
      if (!intent) await finishEvent(env.TELEMETRY_DB, body.id, resourceId, 'ignored', now);
      else if (event === 'CHECKOUT_PAID') {
        const count = await reconcileCheckout(env, intent, options);
        await finishEvent(env.TELEMETRY_DB, body.id, resourceId, count > 0 ? 'processed' : 'failed', now);
        if (count === 0) throw new BillingError('billing-payment-awaiting-subscription');
      } else {
        const state = ['CHECKOUT_CANCELED', 'CHECKOUT_EXPIRED'].includes(event) ? 'cancelled' : 'pending';
        await env.TELEMETRY_DB.prepare(`UPDATE billing_checkout_intents SET state = ?, updated_at = ?
          WHERE id = ? AND state IN ('created', 'pending')`).bind(state, now, intent.id).run();
        await finishEvent(env.TELEMETRY_DB, body.id, resourceId, 'processed', now);
      }
    } else if (PAYMENT_EVENTS.has(event)) {
      await reconcilePayment(env, resourceId, body.id, options);
    } else if (SUBSCRIPTION_EVENTS.has(event)) {
      await reconcileSubscriptionEvent(env, resourceId, body.id, event, options);
    }
    return json({ accepted: true });
  } catch (error) {
    try { await finishEvent(env.TELEMETRY_DB, body.id, resourceId, 'failed', new Date().toISOString()); } catch { /* retry */ }
    return json({ error: error instanceof BillingError ? error.code : 'billing-temporarily-unavailable' },
      error instanceof BillingError ? error.status : 503);
  }
}
