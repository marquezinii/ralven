import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  authorizeDiscordBot,
  createDiscordLinkCode,
  fetchDiscordRoleLinks,
  normalizeLinkCode,
  redeemDiscordLinkCode,
} from '../src/discordLink.js';

test('Discord link input and service authentication fail closed', async () => {
  assert.equal(normalizeLinkCode('ABCD-EFGH23'), 'ABCDEFGH23');
  assert.equal(normalizeLinkCode('short'), null);
  const secret = 'a-test-secret-with-at-least-32-characters';
  assert.equal(await authorizeDiscordBot(new Request('https://example.test', {
    headers: { Authorization: `Bearer ${secret}` },
  }), secret), true);
  assert.equal(await authorizeDiscordBot(new Request('https://example.test', {
    headers: { Authorization: 'Bearer wrong' },
  }), secret), false);
});

test('Discord role sync maps authoritative entitlements to exclusive tiers', async () => {
  let bound;
  const db = {
    prepare(sql) {
      assert.match(sql, /ralven_max/);
      return { bind(...parameters) {
        bound = parameters;
        return { async all() {
          return { results: [
            { discord_user_id: '11111111111111111', tier: 'free' },
            { discord_user_id: '22222222222222222', tier: 'pro' },
            { discord_user_id: '33333333333333333', tier: 'max' },
          ] };
        } };
      } };
    },
  };
  const now = '2026-09-14T12:00:00.000Z';
  assert.deepEqual(await fetchDiscordRoleLinks(db, now), [
    { discord_user_id: '11111111111111111', tier: 'free' },
    { discord_user_id: '22222222222222222', tier: 'pro' },
    { discord_user_id: '33333333333333333', tier: 'max' },
  ]);
  assert.deepEqual(bound, [now, now]);
});

test('Discord link codes are stored as digests and can be redeemed only once', async () => {
  const state = { codeHash: null, uid: null, used: false, discordUserId: null };
  const statement = sql => ({
    parameters: [],
    bind(...parameters) {
      this.parameters = parameters;
      return this;
    },
    async run() {
      if (sql.startsWith('UPDATE discord_link_codes')) {
        const [, hash] = this.parameters;
        if (hash !== state.codeHash || state.used) return { meta: { changes: 0 } };
        state.used = true;
        return { meta: { changes: 1 } };
      }
      if (sql.startsWith('INSERT INTO discord_link_codes')) {
        [state.codeHash, state.uid] = this.parameters;
      }
      if (sql.startsWith('INSERT INTO discord_account_links')) state.discordUserId = this.parameters[1];
      return { meta: { changes: 1 } };
    },
    async first() {
      return this.parameters[0] === state.codeHash ? { account_uid: state.uid } : null;
    },
  });
  const db = {
    prepare: sql => statement(sql),
    batch: statements => Promise.all(statements.map(item => item.run())),
  };
  const secret = 'a-test-secret-with-at-least-32-characters';
  const created = await createDiscordLinkCode(db, 'firebase-uid', secret, new Date('2026-09-14T12:00:00Z'));
  assert.equal(state.codeHash.includes(created.code), false);
  assert.deepEqual(
    await redeemDiscordLinkCode(db, created.code, '12345678901234567', secret, new Date('2026-09-14T12:01:00Z')),
    { ok: true },
  );
  assert.equal(state.discordUserId, '12345678901234567');
  assert.deepEqual(
    await redeemDiscordLinkCode(db, created.code, '12345678901234567', secret, new Date('2026-09-14T12:02:00Z')),
    { ok: false, code: 'invalid-link' },
  );
});
