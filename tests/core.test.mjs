import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { DEFAULT_SETTINGS, finite, validateSettings, validateAdapter, selectCredentials, discover, requestJson, parseUsage, parseNewApi, parseOpenRouter, parseDeepSeek, parseCustom, queryProvider, Monitor, QueryError } from '../src/core.mjs';

// Invented credentials, injected HTTP mocks and temporary databases only.
const ID_A = 'p-0123456789abcdef0123';
const ID_B = 'p-abcdef0123456789abcd';
const row = (id = 'a', base = 'https://relay-a.example/v1', key = 'fixture-key', extra = {}) => ({
  id, name: `测试中转 ${id}`, app_type: 'codex', is_current: 0,
  settings_config: JSON.stringify({ auth: { OPENAI_API_KEY: key }, config: `model_provider = "fixture"\n[model_providers.fixture]\nbase_url = "${base}"` }), ...extra,
});
const credentials = () => selectCredentials([row('a'), row('b', 'https://relay-a.example/v1', 'fixture-second', { is_current: 1 })]);
const credential = () => credentials()[0];
const usage = (remaining = 12) => ({ remaining, unit: 'USD', planName: '钱包余额', mode: 'unrestricted', usage: { today: { actual_cost: 0.2 }, total: { actual_cost: 3 } } });
const parsed = remaining => parseUsage(usage(remaining));
const response = (body, init) => new Response(JSON.stringify(body), init);
const emptySettings = () => ({ intervalSeconds: 300, thresholds: {}, adapters: {} });

test('numeric parsing preserves zero and rejects missing or non-finite values', () => {
  assert.equal(finite(0), 0); assert.equal(finite('0'), 0); assert.equal(finite('-1.2'), -1.2);
  for (const value of [null, undefined, '', ' ', NaN, Infinity, true, {}, []]) assert.equal(finite(value), null);
});

test('settings use dynamic stable IDs without mutating defaults', () => {
  const before = JSON.stringify(DEFAULT_SETTINGS);
  const settings = validateSettings({ intervalSeconds: 60, thresholds: { [ID_A]: 0 }, adapters: { [ID_B]: { type: 'auto' } } });
  assert.equal(settings.intervalSeconds, 60); assert.equal(settings.thresholds[ID_A], 0); assert.equal(settings.adapters[ID_B].type, 'auto'); assert.equal(JSON.stringify(DEFAULT_SETTINGS), before);
  for (const input of [null, [], { extra: 1 }, { intervalSeconds: 59 }, { intervalSeconds: 86401 }, { intervalSeconds: 1.5 }, { thresholds: [] }, { thresholds: { [ID_A]: -1 } }, { thresholds: { [ID_A]: Infinity } }, { thresholds: { [ID_A]: '5' } }, { adapters: [] }]) assert.throws(() => validateSettings(input));
});

test('settings reject inherited property names and malformed IDs', () => {
  for (const key of ['constructor', 'toString', '__proto__', 'xindu', 'p-xyz', 'p-' + 'a'.repeat(21)]) {
    assert.throws(() => validateSettings({ thresholds: JSON.parse(`{"${key}":5}`) }));
    assert.throws(() => validateSettings({ adapters: JSON.parse(`{"${key}":{"type":"auto"}}`) }));
  }
});

test('adapter settings require a supported protocol or precise custom mapping', () => {
  for (const type of ['auto', 'sub2api', 'newapi-token', 'newapi-account', 'openrouter', 'deepseek']) assert.equal(validateAdapter({ type }).type, type);
  const custom = validateAdapter({ type: 'custom', path: '/api/balance', remainingPath: 'data.balance', divisor: 100, unit: 'CNY', balanceKind: 'account' });
  assert.equal(custom.path, '/api/balance'); assert.equal(custom.remainingPath, 'data.balance'); assert.equal(custom.divisor, 100);
  for (const invalid of [null, [], { type: 'arbitrary' }, { type: 'custom' }, { type: 'custom', path: 'https://other.example/balance', remainingPath: 'data.balance', unit: 'USD' }, { type: 'custom', path: '//other.example/balance', remainingPath: 'data.balance', unit: 'USD' }, { type: 'custom', path: '/balance?key=secret', remainingPath: 'balance', unit: 'USD' }, { type: 'custom', path: '/balance', remainingPath: '__proto__.balance', unit: 'USD' }, { type: 'custom', path: '/balance', remainingPath: 'balance', unit: 'USD', divisor: 0 }]) assert.throws(() => validateAdapter(invalid));
});

test('discovery keeps every arbitrary-brand account rather than collapsing by host', () => {
  const selected = credentials(); assert.equal(selected.length, 2);
  assert.ok(selected.every(c => /^p-[a-f0-9]{20}$/.test(c.id))); assert.equal(new Set(selected.map(c => c.id)).size, 2);
  assert.deepEqual(selected.map(c => c.key), ['fixture-key', 'fixture-second']); assert.equal(selected[0].origin, 'https://relay-a.example'); assert.equal(selected[0].basePath, '/v1'); assert.equal(selected[1].current, true);
});

test('IDs survive name, key and origin changes while fingerprints invalidate credentials', () => {
  const first = selectCredentials([row('a')])[0];
  const renamed = selectCredentials([row('a', undefined, undefined, { name: '新名称', is_current: 1 })])[0];
  const rotated = selectCredentials([row('a', undefined, 'rotated-fixture')])[0];
  const moved = selectCredentials([row('a', 'https://relay-b.example/v1')])[0];
  assert.equal(first.id, renamed.id); assert.equal(first.id, rotated.id); assert.equal(first.id, moved.id); assert.equal(first.fingerprint, renamed.fingerprint);
  assert.notEqual(first.fingerprint, rotated.fingerprint); assert.notEqual(first.fingerprint, moved.fingerprint); assert.equal(first.fingerprint.includes('fixture-key'), false);
});

test('the same row ID in different applications remains distinct', () => {
  const selected = selectCredentials([row('same'), row('same', undefined, undefined, { app_type: 'claude' })]); assert.equal(selected.length, 2); assert.notEqual(selected[0].id, selected[1].id);
});

test('Codex selects the configured TOML provider instead of another block', () => {
  const settings_config = JSON.stringify({ auth: { OPENAI_API_KEY: 'fixture-key' }, config: 'model_provider = "active"\n[model_providers.old]\nbase_url = "https://wrong.example/v1"\n[model_providers."active"]\nbase_url = "https://active.example/v1"' });
  assert.equal(selectCredentials([row('a', undefined, undefined, { settings_config })])[0].origin, 'https://active.example');
});

test('Claude, Gemini and direct options work without a brand whitelist', () => {
  const selected = selectCredentials([
    { id: 'claude', name: 'Claude fixture', app_type: 'claude', is_current: 1, settings_config: JSON.stringify({ env: { ANTHROPIC_BASE_URL: 'https://claude.example', ANTHROPIC_AUTH_TOKEN: ' claude-fixture ' } }) },
    { id: 'gemini', name: 'Gemini fixture', app_type: 'gemini', settings_config: JSON.stringify({ env: { GOOGLE_GEMINI_BASE_URL: 'https://gemini.example', GEMINI_API_KEY: 'gemini-fixture' } }) },
    { id: 'camel', name: 'Direct fixture', app_type: 'codex', settings_config: JSON.stringify({ baseUrl: 'https://camel.example/api', apiKey: 'camel-fixture' }) },
    { id: 'snake', name: 'Direct fixture 2', app_type: 'codex', settings_config: JSON.stringify({ base_url: 'https://snake.example', api_key: 'snake-fixture' }) },
  ]);
  assert.equal(selected.length, 4); assert.deepEqual(selected.map(c => c.key), ['claude-fixture', 'gemini-fixture', 'camel-fixture', 'snake-fixture']); assert.ok(selected.every(c => !c.blockedReason));
});

test('invalid addresses, missing keys and malformed JSON stay visible but blocked', () => {
  for (const base of ['http://relay.example', 'https://user:password@relay.example', 'https://relay.example/?key=secret', 'https://relay.example/#secret']) {
    const selected = selectCredentials([row('bad', base)]); assert.equal(selected.length, 1); assert.ok(selected[0].blockedReason, base);
  }
  for (const invalid of [row('empty', undefined, ' '), row('broken', undefined, undefined, { settings_config: 'broken-json' })]) {
    const selected = selectCredentials([invalid]); assert.equal(selected.length, 1); assert.ok(selected[0].blockedReason);
  }
});

test('dynamic discovery does not contain a brand blacklist', () => {
  for (const fixture of [row('excluded', 'https://tokenmetro.com'), row('excluded-name', 'https://other.example', undefined, { name: 'TokenMetro' })]) {
    const selected = selectCredentials([fixture]); assert.equal(selected.length, 1); assert.equal(selected[0].blockedReason || '', '');
  }
  assert.equal(selectCredentials([row('unknown', 'https://new-brand.example')])[0].blockedReason || '', '');
});

test('SQLite discovery is read only and retains all records', async t => {
  const dir = await mkdtemp(path.join(os.tmpdir(), 'relay-balance-test-')); t.after(() => rm(dir, { recursive: true, force: true }));
  const file = path.join(dir, 'fixture.db'); const db = new DatabaseSync(file);
  db.exec('CREATE TABLE providers(id TEXT,name TEXT,app_type TEXT,settings_config TEXT,is_current INTEGER,meta TEXT)');
  const insert = db.prepare('INSERT INTO providers VALUES(?,?,?,?,?,?)');
  for (const provider of [row('a'), row('b', undefined, 'second-fixture')]) insert.run(provider.id, provider.name, provider.app_type, provider.settings_config, provider.is_current, '{}'); db.close();
  const result = discover(file); assert.equal(result.length, 2); assert.equal(result[0].key, 'fixture-key');
  const reopened = new DatabaseSync(file, { readOnly: true }); assert.equal(reopened.prepare('SELECT COUNT(*) AS count FROM providers').get().count, 2); assert.equal(reopened.prepare('SELECT settings_config FROM providers WHERE id=?').get('a').settings_config, row('a').settings_config); reopened.close();
});

test('wallet values preserve zero, negative balance and actual usage', () => {
  const zero = parsed(0); assert.equal(zero.remaining, 0); assert.equal(zero.unit, 'USD'); assert.equal(zero.todayUsage, 0.2); assert.equal(zero.totalUsage, 3); assert.equal(zero.unlimited, false); assert.match(zero.balanceKindLabel, /账户|钱包/);
  assert.equal(parsed(-2).remaining, -2); assert.equal(parseUsage({ quota: { remaining: '7.5' }, unit: 'cny' }).remaining, 7.5); assert.notEqual(parseUsage({ balance: 1, unit: '<script>' }).unit, '<script>');
});

test('missing or inactive balances are errors instead of invented zero balances', () => {
  for (const data of [{}, { remaining: null }, { remaining: 'invalid' }, { remaining: Infinity }, { remaining: 5, success: false }, { remaining: 5, isValid: false }, { remaining: 5, is_active: false }, { remaining: 5, error: 'private server message' }]) assert.throws(() => parseUsage(data), QueryError);
  assert.throws(() => parseUsage({}), { code: 'schema_error' });
});

test('New API quotas stay raw without authoritative conversion information', () => {
  const raw = { code: true, data: { object: 'token_usage', total_available: 250000, total_used: 50000, unlimited_quota: false } };
  const result = parseNewApi(raw, {}); assert.equal(result.remaining, 250000); assert.match(result.unit, /quota|额度/i); assert.equal(result.unlimited, false);
  for (const quota_per_unit of [undefined, 0, -1, null, 'invalid']) {
    const value = parseNewApi(raw, { data: { quota_per_unit, quota_display_type: 'USD' } }); assert.equal(value.remaining, 250000); assert.notEqual(value.unit, 'USD');
  }
});

test('New API token and account balances use the server-provided USD conversion', () => {
  const status = { data: { quota_per_unit: 100000, quota_display_type: 'USD' } };
  const token = parseNewApi({ data: { object: 'token_usage', total_available: 250000, total_used: 50000 } }, status); assert.equal(token.remaining, 2.5); assert.equal(token.unit, 'USD');
  const account = parseNewApi({ success: true, data: { quota: 400000, used_quota: 100000 } }, status, { account: true }); assert.equal(account.remaining, 4); assert.equal(account.unit, 'USD'); assert.match(account.balanceKindLabel, /账户/);
});

test('New API unlimited quota is not a zero or negative monetary balance', () => {
  const unlimited = parseNewApi({ data: { object: 'token_usage', total_available: 0, total_used: 100000, unlimited_quota: true } }, { data: { quota_per_unit: 500000, quota_display_type: 'USD' } }); assert.equal(unlimited.unlimited, true); assert.equal(unlimited.remaining, null);
});

test('Sub2API window quotas preserve the tightest bound and identify its meaning', () => {
  const result = parseUsage({ mode: 'quota_limited', isValid: true, rate_limits: [{ window: '5h', limit: 10, remaining: 3 }, { window: '1d', limit: 30, remaining: 12 }] });
  assert.equal(result.remaining, 3); assert.match(result.balanceKindLabel, /窗口/);
  assert.throws(() => parseUsage({ mode: 'quota_limited', rate_limits: [] }), QueryError);
});

test('Sub2API unlimited subscriptions do not display the -1 sentinel as debt', () => {
  const result = parseUsage({ mode: 'unrestricted', planName: 'Unlimited fixture', unit: 'USD', remaining: -1, subscription: { daily_limit_usd: null, weekly_limit_usd: null, monthly_limit_usd: null } });
  assert.equal(result.remaining, null); assert.equal(result.unlimited, true);
  assert.equal(parsed(-1).remaining, -1); assert.equal(parsed(-1).unlimited, false);
});

test('New API CNY conversion requires an explicit positive exchange rate', () => {
  const raw = { data: { object: 'token_usage', total_available: 250000, total_used: 0 } };
  const status = { data: { quota_per_unit: 100000, quota_display_type: 'CNY', usd_exchange_rate: 7 } };
  const cny = parseNewApi(raw, status); assert.equal(cny.remaining, 17.5); assert.equal(cny.unit, 'CNY');
  const missing = parseNewApi(raw, { data: { quota_per_unit: 100000, quota_display_type: 'CNY' } });
  assert.equal(missing.remaining, 250000); assert.notEqual(missing.unit, 'CNY');
});

test('OpenRouter computes credit minus usage but rejects incomplete credit data', () => {
  const result = parseOpenRouter({ data: { total_credits: 20, total_usage: 4.5 } }); assert.equal(result.remaining, 15.5); assert.equal(result.unit, 'USD'); assert.match(result.balanceKindLabel, /账户/);
  assert.equal(parseOpenRouter({ data: { total_credits: 0, total_usage: 2 } }).remaining, -2);
  for (const raw of [{}, { data: { total_credits: 20 } }, { data: { total_usage: 4 } }]) assert.throws(() => parseOpenRouter(raw), QueryError);
});

test('DeepSeek reads authoritative totals without adding currencies together', () => {
  const result = parseDeepSeek({ is_available: true, balance_infos: [{ currency: 'CNY', total_balance: '12.5', granted_balance: '2.5', topped_up_balance: '10' }] });
  assert.equal(result.remaining, 12.5); assert.equal(result.unit, 'CNY');
  assert.throws(() => parseDeepSeek({ is_available: true, balance_infos: [] }), QueryError);
  assert.throws(() => parseDeepSeek({ is_available: true, balance_infos: [{ currency: 'CNY', total_balance: '12' }, { currency: 'USD', total_balance: '4' }] }), QueryError);
});

test('custom mappings require explicit numeric field, divisor, unit and meaning', () => {
  const adapter = { type: 'custom', path: '/balance', remainingPath: 'data.balance', divisor: 100, unit: 'CNY', balanceKind: 'account' };
  const result = parseCustom({ data: { balance: '1250' } }, adapter); assert.equal(result.remaining, 12.5); assert.equal(result.unit, 'CNY'); assert.match(result.balanceKindLabel, /账户/);
  assert.equal(parseCustom({ data: { balance: 0 } }, adapter).remaining, 0); assert.throws(() => parseCustom({ data: {} }, adapter), QueryError); assert.throws(() => parseCustom({ data: { balance: 'unknown' } }, adapter), QueryError);
});

test('HTTP uses same-origin GET, bearer auth, manual redirects and a timeout', async () => {
  let observed;
  const data = await requestJson(credential(), '/v1/usage', { fetchImpl: async (url, options) => { observed = { url, options }; return response(usage()); } });
  assert.equal(data.remaining, 12); assert.equal(String(observed.url), 'https://relay-a.example/v1/usage'); assert.equal(observed.options.method, 'GET'); assert.equal(observed.options.headers.Authorization, 'Bearer fixture-key'); assert.equal(observed.options.redirect, 'manual'); assert.ok(observed.options.signal instanceof AbortSignal);
});

test('HTTP blocks cross-origin URLs and credential-bearing routes before fetch', async () => {
  let calls = 0; const fetchImpl = async () => { calls++; return response({}); };
  for (const route of ['https://other.example/balance', '//other.example/balance', '/balance?key=fixture-key', '/balance#fixture-key', '/balance/fixture-key']) await assert.rejects(requestJson(credential(), route, { fetchImpl }));
  await assert.rejects(requestJson({ ...credential(), origin: 'http://relay-a.example' }, '/balance', { fetchImpl })); assert.equal(calls, 0);
  await assert.rejects(requestJson(credential(), '/balance', { fetchImpl: async () => new Response('', { status: 302, headers: { location: 'https://other.example' } }) }), { code: 'redirect' });
});

test('HTTP errors hide upstream bodies and keys while preserving retry limits', async () => {
  for (const status of [401, 403]) await assert.rejects(requestJson(credential(), '/balance', { fetchImpl: async () => new Response('fixture-key private details', { status }) }), error => error.code === 'invalid_key' && !error.message.includes('fixture-key'));
  for (const status of [429, 503]) await assert.rejects(requestJson(credential(), '/balance', { fetchImpl: async () => new Response('', { status, headers: { 'Retry-After': '120' } }) }), error => error.code === 'rate_limit' && error.retryAfterSeconds === 120);
  await assert.rejects(requestJson(credential(), '/balance', { fetchImpl: async () => { throw new Error('fixture-key private details'); } }), error => error.code === 'network_error' && !error.message.includes('fixture-key'));
  await assert.rejects(requestJson(credential(), '/balance', { fetchImpl: async () => new Response('<html>fixture-key</html>') }), { code: 'not_json' });
  await assert.rejects(requestJson(credential(), '/balance', { fetchImpl: async () => new Response('x'.repeat(2 * 1024 * 1024 + 1)) }), { code: 'response_size' });
});

test('explicit Sub2API and custom adapters stay on the configured origin', async () => {
  const seen = []; const sub = await queryProvider(credential(), { type: 'sub2api' }, async url => { seen.push(String(url)); return response(usage(8)); });
  assert.equal(sub.remaining, 8); assert.ok(seen.length >= 1); assert.ok(seen.every(url => url.startsWith('https://relay-a.example/')));
  const custom = await queryProvider(credential(), { type: 'custom', path: '/billing/balance', remainingPath: 'data.balance', divisor: 10, unit: 'USD', balanceKind: 'key' }, async url => { assert.equal(String(url), 'https://relay-a.example/billing/balance'); return response({ data: { balance: 75 } }); }); assert.equal(custom.remaining, 7.5);
});

test('monitor returns separate accounts without credentials or metadata', async () => {
  const monitor = new Monitor(emptySettings(), { discoverFn: credentials, queryFn: async () => parsed(5) });
  const result = await monitor.refresh(); assert.equal(result.providers.length, 2); assert.equal(result.providers[0].lowBalance, true); assert.equal(result.providers[1].current, true); assert.equal(result.providers[0].status, 'ok'); assert.equal(result.refreshing, false);
  for (const p of result.providers) for (const key of ['key', 'fingerprint', 'settings_config', 'metaUsage']) assert.equal(Object.hasOwn(p, key), false);
  for (const secret of ['fixture-key', 'fixture-second', credentials()[0].fingerprint]) assert.equal(JSON.stringify(result).includes(secret), false);
});

test('monitor coalesces concurrent queries and throttles unchanged refreshes', async () => {
  let release; const wait = new Promise(resolve => { release = resolve; }); let calls = 0;
  const monitor = new Monitor(emptySettings(), { discoverFn: credentials, queryFn: async () => { calls++; await wait; return parsed(20); } });
  const first = monitor.refresh(); const second = monitor.refresh(); release(); await Promise.all([first, second]); await monitor.refresh(); assert.equal(calls, 2);
});

test('refresh discovers additions and removals even inside the throttle', async () => {
  let rows = [row('a')]; let calls = 0;
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials(rows), queryFn: async () => { calls++; return parsed(20); } });
  const first = await monitor.refresh(); const firstId = first.providers[0].id; rows = [row('b', 'https://second.example')];
  const next = await monitor.refresh(); assert.equal(next.providers.length, 1); assert.notEqual(next.providers[0].id, firstId); assert.equal(next.providers[0].status, 'ok'); assert.equal(calls, 2);
  rows = []; const empty = await monitor.refresh(); assert.deepEqual(empty.providers, []); assert.ok(empty.message);
});

test('rotating a key wipes prior balances and forces a fresh query', async () => {
  let key = 'first-secret'; let fail = false;
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a', undefined, key)]), queryFn: async () => { if (fail) throw new QueryError('invalid_key', '无查询权限'); return parsed(2); } });
  const first = await monitor.refresh(); key = 'replacement-secret'; fail = true; const next = await monitor.refresh(); assert.equal(next.providers[0].id, first.providers[0].id);
  assert.equal(next.providers[0].remaining, null); assert.notEqual(next.providers[0].status, 'stale'); assert.equal(next.providers[0].lowBalance, false); assert.equal(next.providers[0].lastSuccessAt, null); assert.equal(JSON.stringify(monitor.snapshot()).includes('secret'), false);
});

test('changing origin cannot reuse the previous host balance', async () => {
  let origin = 'https://first.example'; let fail = false;
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a', origin)]), queryFn: async () => { if (fail) throw new QueryError('network_error', '网络失败'); return parsed(2); } });
  await monitor.refresh(); origin = 'https://second.example'; fail = true; const next = await monitor.refresh(); assert.equal(next.providers[0].origin, origin); assert.equal(next.providers[0].remaining, null); assert.notEqual(next.providers[0].status, 'stale');
});

test('same-credential failures retain a clearly stale balance and suppress alerts', async () => {
  let fail = false; const monitor = new Monitor(emptySettings(), { discoverFn: credentials, queryFn: async () => { if (fail) throw new QueryError('network_error', '已脱敏错误'); return parsed(2); } });
  const initial = await monitor.refresh(); fail = true; const stale = await monitor.refresh({ force: true });
  assert.equal(stale.providers[0].remaining, 2); assert.equal(stale.providers[0].status, 'stale'); assert.equal(stale.providers[0].lowBalance, false); assert.equal(stale.providers[0].lastSuccessAt, initial.providers[0].lastSuccessAt);
});

test('quota, unlimited and unknown-currency balances do not cause money alerts', async () => {
  for (const result of [{ ...parsed(0), unit: 'quota' }, { ...parsed(0), unit: '未标单位' }, { ...parsed(0), remaining: null, unlimited: true }]) {
    const monitor = new Monitor(emptySettings(), { discoverFn: credentials, queryFn: async () => result }); const state = await monitor.refresh(); assert.ok(state.providers.every(p => p.lowBalance === false));
  }
});

test('monitor respects Retry-After separately for each account', async () => {
  let calls = 0; const monitor = new Monitor(emptySettings(), { discoverFn: credentials, queryFn: async () => { calls++; throw new QueryError('rate_limit', '限流', 300); } });
  await monitor.refresh(); await monitor.refresh({ force: true }); assert.equal(calls, 2);
});

test('empty and unreadable databases never fabricate balances or expose errors', async () => {
  const empty = new Monitor(emptySettings(), { discoverFn: () => [] }); const state = await empty.refresh(); assert.deepEqual(state.providers, []); assert.ok(state.message);
  const broken = new Monitor(emptySettings(), { discoverFn: () => { throw new Error('secret fixture-key database details'); } }); const result = await broken.refresh(); assert.deepEqual(result.providers, []); assert.ok(result.message); assert.equal(JSON.stringify(result).includes('fixture-key'), false);
});

test('checkForChanges discovers current-selection edits and new rows', async () => {
  let rows = [row('a')]; const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials(rows), queryFn: async () => parsed(8) });
  await monitor.refresh(); rows = [row('a', undefined, undefined, { is_current: 1, name: '改名配置' }), row('b')]; await monitor.checkForChanges(); const state = monitor.snapshot();
  assert.equal(state.providers.length, 2); assert.equal(state.providers[0].current, true); assert.equal(state.providers[0].name, '改名配置');
});

test('auto discovery identifies arbitrary-brand New API and fetches public conversion without credentials', async () => {
  const seen = [];
  const result = await queryProvider(credential(), { type: 'auto' }, async (url, options) => {
    seen.push(url.pathname);
    if (url.pathname === '/v1/usage') return response({}, { status: 404 });
    if (url.pathname === '/api/usage/token/') { assert.equal(options.headers.Authorization, 'Bearer fixture-key'); return response({ data: { object: 'token_usage', total_available: 250000, total_used: 0 } }); }
    if (url.pathname === '/api/status') { assert.equal(Object.hasOwn(options.headers, 'Authorization'), false); return response({ data: { quota_per_unit: 100000, quota_display_type: 'USD' } }); }
    throw new Error('unexpected mock route');
  });
  assert.equal(result.remaining, 2.5); assert.equal(result.unit, 'USD'); assert.equal(result.adapter, 'newapi-token');
  assert.deepEqual(seen, ['/v1/usage', '/api/usage/token/', '/api/status']);
});

test('auto probing preserves reverse-proxy prefixes and marks absent interfaces unsupported', async () => {
  const prefixed = selectCredentials([row('prefix', 'https://proxy.example/service/v1')])[0]; const seen = [];
  await assert.rejects(queryProvider(prefixed, { type: 'auto' }, async url => { seen.push(url.pathname); return response({}, { status: 404 }); }), { code: 'unsupported' });
  assert.deepEqual(seen, ['/service/v1/usage', '/service/api/usage/token/']);
});

test('auto probing stops on redirects and rate limits instead of repeated credential requests', async () => {
  for (const [status, code] of [[302, 'redirect'], [429, 'rate_limit']]) {
    let calls = 0;
    await assert.rejects(queryProvider(credential(), { type: 'auto' }, async () => { calls++; return response({}, { status }); }), { code }); assert.equal(calls, 1);
  }
});

test('account metadata is accepted only at the configured origin and never evaluates scripts', async () => {
  const metadata = { usage_script: { enabled: true, baseUrl: 'https://relay-a.example', accessToken: 'account-fixture-token', userId: 42, script: 'throw new Error("MUST NEVER EXECUTE");' } };
  const c = selectCredentials([row('meta', undefined, undefined, { meta: JSON.stringify(metadata) })])[0];
  assert.deepEqual(c.metaUsage, { accessToken: 'account-fixture-token', userId: '42' });
  const seen = [];
  const result = await queryProvider(c, { type: 'auto' }, async (url, options) => {
    seen.push(url.pathname);
    if (url.pathname === '/api/user/self') { assert.equal(options.headers.Authorization, 'Bearer account-fixture-token'); assert.equal(options.headers['New-Api-User'], '42'); return response({ success: true, data: { quota: 900000, used_quota: 100000 } }); }
    assert.equal(url.pathname, '/api/status'); assert.equal(Object.hasOwn(options.headers, 'Authorization'), false); return response({ data: { quota_per_unit: 100000, quota_display_type: 'USD' } });
  });
  assert.equal(result.remaining, 9); assert.equal(result.adapter, 'newapi-account'); assert.deepEqual(seen, ['/api/user/self', '/api/status']);
  const monitor = new Monitor(emptySettings(), { discoverFn: () => [c], queryFn: async () => result }); await monitor.refresh();
  const publicState = JSON.stringify(monitor.snapshot()); assert.equal(publicState.includes('account-fixture-token'), false); assert.equal(publicState.includes('MUST NEVER EXECUTE'), false);
});

test('cross-origin, URL-credential and malformed metadata cannot authorize account requests', async () => {
  for (const overrides of [{ baseUrl: 'https://other.example' }, { baseUrl: 'https://user:password@relay-a.example' }, { baseUrl: 'https://relay-a.example?key=secret' }, { userId: '42\r\nX-Test: secret' }, { accessToken: 'secret\r\nX-Test: secret' }, { enabled: false }]) {
    const usage_script = { enabled: true, baseUrl: 'https://relay-a.example', accessToken: 'account-fixture-token', userId: 42, ...overrides };
    const c = selectCredentials([row('meta', undefined, undefined, { meta: JSON.stringify({ usage_script }) })])[0]; assert.equal(c.metaUsage, null);
    let calls = 0; await assert.rejects(requestJson(c, '/api/user/self', { auth: 'account', fetchImpl: async () => { calls++; return response({}); } }), { code: 'account_auth' }); assert.equal(calls, 0);
  }
});

test('monitor bounds concurrent origins and serializes accounts sharing one origin', async () => {
  const rows = [row('a', 'https://same.example'), row('b', 'https://same.example'), row('c', 'https://second.example'), row('d', 'https://third.example'), row('e', 'https://fourth.example')];
  const active = new Set(), waiting = []; let total = 0, max = 0;
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials(rows), queryFn: async c => {
    assert.equal(active.has(c.origin), false); active.add(c.origin); max = Math.max(max, active.size); total++;
    await new Promise(resolve => waiting.push(resolve)); active.delete(c.origin); return parsed(10);
  } });
  const refresh = monitor.refresh(); await new Promise(setImmediate); assert.equal(total, 3);
  while (total < rows.length || active.size) { waiting.splice(0).forEach(resolve => resolve()); await new Promise(setImmediate); }
  await refresh; assert.equal(total, rows.length); assert.equal(max, 3); assert.ok(monitor.snapshot().providers.every(p => p.status === 'ok'));
});

test('a response started before key rotation cannot overwrite the replacement account', async () => {
  let key = 'old-fixture-key', releaseOld; const oldGate = new Promise(resolve => { releaseOld = resolve; }); const calls = [];
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a', undefined, key)]), queryFn: async c => { calls.push(c.key); if (c.key === 'old-fixture-key') { await oldGate; return parsed(999); } return parsed(7); } });
  const oldRefresh = monitor.refresh(); await new Promise(setImmediate); key = 'new-fixture-key'; const changed = monitor.checkForChanges();
  assert.equal(monitor.snapshot().providers[0].remaining, null); releaseOld(); await Promise.all([oldRefresh, changed]);
  assert.deepEqual(calls, ['old-fixture-key', 'new-fixture-key']); assert.equal(monitor.snapshot().providers[0].remaining, 7); assert.equal(monitor.snapshot().providers[0].status, 'ok');
});

test('a removed configuration cannot be resurrected by its in-flight response', async () => {
  let rows = [row('a')], release; const gate = new Promise(resolve => { release = resolve; });
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials(rows), queryFn: async () => { await gate; return parsed(99); } });
  const first = monitor.refresh(); await new Promise(setImmediate); rows = []; const removed = monitor.checkForChanges(); release(); await Promise.all([first, removed]); assert.deepEqual(monitor.snapshot().providers, []);
});

test('unsupported auto entries pause periodic and manual refresh until explicitly retried', async t => {
  let calls = 0, now = Date.now(); t.mock.method(Date, 'now', () => now);
  const monitor = new Monitor(emptySettings(), { discoverFn: credentials, queryFn: async () => { calls++; throw new QueryError('unsupported', '请选择继续识别或手动配置'); } });
  await monitor.refresh(); assert.equal(calls, 2); assert.ok(monitor.snapshot().providers.every(p => p.status === 'unsupported'));
  now += 301000; await monitor.refresh(); await monitor.refresh({ force: true }); await monitor.checkForChanges();
  assert.equal(calls, 2); assert.ok(monitor.snapshot().providers.every(p => p.remaining === null && p.lowBalance === false));
});

test('explicit detection retries only the selected unsupported account and preserves settings', async () => {
  let supported = false, release; const wait = new Promise(resolve => { release = resolve; }); const calls = [];
  const settings = emptySettings(); settings.thresholds[credentials()[0].id] = 3;
  const monitor = new Monitor(settings, { discoverFn: credentials, queryFn: async (c, adapter) => {
    calls.push({ id: c.id, origin: c.origin, key: c.key, adapter });
    if (!supported) throw new QueryError('unsupported', '无法识别'); await wait; return { ...parsed(9), adapter: 'sub2api' };
  } });
  await monitor.refresh(); supported = true; const before = JSON.stringify(monitor.settings); const selected = credentials()[0];
  const retry = monitor.retryDetection(selected.id);
  await new Promise(setImmediate); const pending = monitor.snapshot(); assert.equal(pending.refreshing, true); assert.equal(pending.providers[0].status, 'pending');
  release(); await retry;
  assert.equal(calls.length, 3); assert.deepEqual(calls[2], { id: selected.id, origin: selected.origin, key: selected.key, adapter: { type: 'auto' } });
  assert.equal(monitor.snapshot().providers[0].remaining, 9); assert.equal(monitor.snapshot().providers[1].status, 'unsupported'); assert.equal(JSON.stringify(monitor.settings), before);
});

test('explicit detection rejects malformed, removed and already resolved entries', async () => {
  let rows = [row('a')], calls = 0;
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials(rows), queryFn: async () => { calls++; return parsed(5); } });
  await monitor.refresh(); const id = monitor.snapshot().providers[0].id;
  for (const invalid of [undefined, null, '', 'constructor', 'https://other.example', 'p-00000000000000000000', id]) await assert.rejects(monitor.retryDetection(invalid));
  assert.equal(calls, 1);
  rows = []; await assert.rejects(monitor.retryDetection(id)); assert.equal(calls, 1);
});

test('key and adapter changes resume detection while display-name changes stay paused', async () => {
  let rows = [row('a')], supported = false, calls = 0;
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials(rows), queryFn: async () => { calls++; if (!supported) throw new QueryError('unsupported', '不可识别'); return parsed(8); } });
  await monitor.refresh(); rows = [row('a', undefined, undefined, { name: '修改名称' })]; await monitor.refresh({ force: true }); assert.equal(calls, 1);
  supported = true; rows = [row('a', undefined, 'replacement-fixture')]; await monitor.checkForChanges(); assert.equal(calls, 2); assert.equal(monitor.snapshot().providers[0].status, 'ok');
  supported = false; rows = [row('a', undefined, 'another-fixture')]; await monitor.checkForChanges(); assert.equal(monitor.snapshot().providers[0].status, 'unsupported');
  supported = true; monitor.settings.adapters[monitor.snapshot().providers[0].id] = { type: 'sub2api' }; await monitor.checkForChanges(); assert.equal(calls, 4); assert.equal(monitor.snapshot().providers[0].status, 'ok');
});

test('explicit detection cannot bypass an upstream retry deadline', async () => {
  let calls = 0;
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async () => { calls++; throw new QueryError('unsupported', '暂不能识别', 300); } });
  await monitor.refresh(); const id = monitor.snapshot().providers[0].id;
  try { await monitor.retryDetection(id); } catch (error) { assert.equal(error.message.includes('fixture-key'), false); }
  await monitor.refresh({ force: true }); assert.equal(calls, 1);
});

test('first explicit retry is immediate but repeated clicks are throttled for ten seconds', async t => {
  let calls = 0, now = Date.now(); t.mock.method(Date, 'now', () => now);
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async () => { calls++; if (calls < 3) throw new QueryError('unsupported', '无法识别'); return parsed(10); } });
  await monitor.refresh(); const id = monitor.snapshot().providers[0].id;
  await monitor.retryDetection(id); assert.equal(calls, 2);
  await assert.rejects(monitor.retryDetection(id), { code: 'retry_soon' }); assert.equal(calls, 2);
  now += 10000; await monitor.retryDetection(id); assert.equal(calls, 3); assert.equal(monitor.snapshot().providers[0].remaining, 10);
});

test('a previously supported adapter that disappears becomes unresolved without stale money', async () => {
  let available = true, calls = 0;
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async (_c, adapter) => {
    calls++; if (available) return { ...parsed(2), adapter: 'sub2api' };
    throw new QueryError(adapter.type === 'auto' ? 'unsupported' : 'endpoint_missing', '接口已变更');
  } });
  await monitor.refresh(); available = false; await monitor.refresh({ force: true });
  const result = monitor.snapshot().providers[0]; assert.equal(result.status, 'unsupported'); assert.equal(result.remaining, null); assert.equal(result.lastSuccessAt, null); assert.equal(result.lowBalance, false);
  await monitor.refresh({ force: true }); assert.equal(calls, 3);
});

test('automatic adaptation can retry a query error and recover without zeroing the balance', async () => {
  let recover = false; const received = [];
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async (_c, adapter) => {
    received.push(adapter); if (!recover) throw new QueryError('invalid_key', '无查询权限'); return { ...parsed(7), adapter: 'newapi-token' };
  } });
  await monitor.refresh(); const failed = monitor.snapshot().providers[0]; assert.equal(failed.status, 'error'); assert.equal(failed.remaining, null); assert.equal(failed.lowBalance, false);
  recover = true; await monitor.retryDetection(failed.id); const recovered = monitor.snapshot().providers[0];
  assert.deepEqual(received, [{ type: 'auto' }, { type: 'auto' }]); assert.equal(recovered.status, 'ok'); assert.equal(recovered.remaining, 7); assert.ok(recovered.lastSuccessAt);
});

test('retrying stale automatic data preserves success time until a newly detected result arrives', async () => {
  let step = 0, release; const gate = new Promise(resolve => { release = resolve; }); const adapters = [];
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async (_c, adapter) => {
    adapters.push(adapter); if (step === 0) return { ...parsed(2), adapter: 'sub2api' };
    if (step === 1) throw new QueryError('network_error', '暂时失败'); await gate; return { ...parsed(12), adapter: 'newapi-token' };
  } });
  await monitor.refresh(); const first = monitor.snapshot().providers[0]; step = 1; await monitor.refresh({ force: true });
  const stale = monitor.snapshot().providers[0]; assert.equal(stale.status, 'stale'); assert.equal(stale.remaining, 2); assert.equal(stale.lastSuccessAt, first.lastSuccessAt);
  step = 2; const retry = monitor.retryDetection(first.id); await new Promise(setImmediate);
  const pending = monitor.snapshot().providers[0]; assert.equal(pending.status, 'pending'); assert.equal(pending.remaining, 2); assert.equal(pending.lastSuccessAt, first.lastSuccessAt); assert.equal(pending.lowBalance, false);
  await new Promise(resolve => setTimeout(resolve, 10)); release(); await retry; const refreshed = monitor.snapshot().providers[0];
  assert.deepEqual(adapters, [{ type: 'auto' }, { type: 'sub2api' }, { type: 'auto' }]); assert.equal(refreshed.status, 'ok'); assert.equal(refreshed.remaining, 12); assert.ok(Date.parse(refreshed.lastSuccessAt) > Date.parse(first.lastSuccessAt)); assert.equal(refreshed.updatedAt, refreshed.lastSuccessAt);
});

test('manual and custom adaptation retries keep their saved mapping and thresholds', async () => {
  for (const mapping of [{ type: 'sub2api' }, { type: 'custom', path: '/account/balance', remainingPath: 'data.remaining', unit: 'CNY', divisor: 100, balanceKind: 'account' }]) {
    const id = credentials()[0].id; const settings = { ...emptySettings(), thresholds: { [id]: 3 }, adapters: { [id]: mapping } }; const received = []; let recover = false;
    const monitor = new Monitor(settings, { discoverFn: () => selectCredentials([row('a')]), queryFn: async (_c, adapter) => { received.push(structuredClone(adapter)); if (!recover) throw new QueryError('schema_error', '接口响应格式已变化'); return mapping.type === 'custom' ? parseCustom({ data: { remaining: 1250 } }, adapter) : parsed(12.5); } });
    await monitor.refresh(); assert.equal(monitor.snapshot().providers[0].status, 'error'); const saved = JSON.stringify(monitor.settings);
    recover = true; await monitor.retryDetection(id); const result = monitor.snapshot().providers[0];
    assert.deepEqual(received, [mapping, mapping]); assert.equal(JSON.stringify(monitor.settings), saved); assert.deepEqual(result.adapterConfig, mapping); assert.equal(result.threshold, 3); assert.equal(result.remaining, 12.5); assert.equal(result.status, 'ok');
  }
});

test('authentication failure during adaptation retains old money only as stale data', async () => {
  let fail = false;
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async () => { if (fail) throw new QueryError('invalid_key', '权限失效'); return parsed(3); } });
  await monitor.refresh(); const first = monitor.snapshot().providers[0]; fail = true; await monitor.refresh({ force: true }); await monitor.retryDetection(first.id);
  const result = monitor.snapshot().providers[0]; assert.equal(result.status, 'stale'); assert.equal(result.remaining, 3); assert.equal(result.lastSuccessAt, first.lastSuccessAt); assert.equal(result.lowBalance, false);
});

test('query errors with Retry-After reject adaptation until the upstream deadline', async t => {
  let now = Date.now(), calls = 0; t.mock.method(Date, 'now', () => now);
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async () => { calls++; if (calls === 1) throw new QueryError('rate_limit', '限流', 120); return parsed(11); } });
  await monitor.refresh(); const id = monitor.snapshot().providers[0].id; assert.equal(monitor.snapshot().providers[0].status, 'error');
  await assert.rejects(monitor.retryDetection(id), { code: 'rate_limit' }); await monitor.refresh({ force: true }); assert.equal(calls, 1);
  now += 120000; await monitor.retryDetection(id); assert.equal(calls, 2); assert.equal(monitor.snapshot().providers[0].remaining, 11);
});

test('missing credentials and an already pending query cannot start adaptation', async () => {
  let calls = 0;
  const missing = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('missing', undefined, ' ')]), queryFn: async () => { calls++; return parsed(1); } });
  await missing.refresh(); const absent = missing.snapshot().providers[0]; assert.equal(absent.status, 'missing'); await assert.rejects(missing.retryDetection(absent.id)); assert.equal(calls, 0);
  let release; const gate = new Promise(resolve => { release = resolve; });
  const pending = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('pending')]), queryFn: async () => { calls++; await gate; return parsed(1); } });
  const initial = pending.refresh(); await new Promise(setImmediate); await assert.rejects(pending.retryDetection(pending.snapshot().providers[0].id)); release(); await initial; assert.equal(calls, 1);
});

test('visibility settings validate stable IDs, remove duplicates and keep defaults immutable', () => {
  const before = JSON.stringify(DEFAULT_SETTINGS);
  assert.deepEqual(validateSettings({}).hiddenProviders, []);
  assert.deepEqual(validateSettings({ hiddenProviders: [ID_A, ID_B, ID_A] }).hiddenProviders, [ID_A, ID_B]);
  for (const hiddenProviders of ['all', {}, [null], ['constructor'], ['p-nope'], ['https://relay.example'], Array.from({ length: 10001 }, (_, i) => 'p-' + i.toString(16).padStart(20, '0'))]) assert.throws(() => validateSettings({ hiddenProviders }));
  assert.equal(JSON.stringify(DEFAULT_SETTINGS), before);
});

test('hidden providers expose only identity and stop periodic, manual and explicit queries', async t => {
  let calls = 0, now = Date.now(); t.mock.method(Date, 'now', () => now);
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async () => { calls++; return parsed(2); } });
  await monitor.refresh(); const id = monitor.snapshot().providers[0].id; const original = JSON.stringify(monitor.settings);
  const hiddenSettings = monitor.settingsWithVisibility(id, true); assert.equal(JSON.stringify(monitor.settings), original);
  monitor.settings = hiddenSettings; await monitor.checkForChanges(); now += 301000; await monitor.refresh(); await monitor.refresh({ force: true });
  const state = monitor.snapshot(); assert.deepEqual(state.providers, []); assert.equal(state.hiddenProviders.length, 1); assert.equal(calls, 1);
  assert.deepEqual(Object.keys(state.hiddenProviders[0]).sort(), ['app', 'id', 'name', 'origin']);
  assert.equal(state.hiddenProviders[0].id, id); assert.equal(JSON.stringify(state).includes('fixture-key'), false); assert.equal(Object.hasOwn(state.hiddenProviders[0], 'remaining'), false);
  await assert.rejects(monitor.retryDetection(id)); assert.equal(calls, 1);
});

test('hidden identity persists across restart, rename, key and origin changes until restored', async () => {
  let rows = [row('a')], calls = 0;
  const dependencies = { discoverFn: () => selectCredentials(rows), queryFn: async () => { calls++; return parsed(calls); } };
  const monitor = new Monitor(emptySettings(), dependencies); await monitor.refresh(); const id = monitor.snapshot().providers[0].id;
  monitor.settings = monitor.settingsWithVisibility(id, true); await monitor.checkForChanges();
  rows = [row('a', 'https://replacement.example/api/v1', 'rotated-fixture', { name: '改名后配置' })]; await monitor.refresh({ force: true }); assert.equal(calls, 1);
  const restarted = new Monitor(JSON.parse(JSON.stringify(monitor.settings)), dependencies); await restarted.refresh();
  assert.deepEqual(restarted.snapshot().providers, []); assert.equal(restarted.snapshot().hiddenProviders[0].id, id); assert.equal(restarted.snapshot().hiddenProviders[0].name, '改名后配置'); assert.equal(restarted.snapshot().hiddenProviders[0].origin, 'https://replacement.example'); assert.equal(calls, 1);
  restarted.settings = restarted.settingsWithVisibility(id, false); await restarted.checkForChanges(); const restored = restarted.snapshot();
  assert.equal(calls, 2); assert.deepEqual(restored.hiddenProviders, []); assert.equal(restored.providers[0].remaining, 2); assert.equal(restored.providers[0].status, 'ok');
});

test('visibility rejects invalid IDs, missing entries and non-boolean choices', async () => {
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async () => parsed(8) }); await monitor.refresh(); const id = monitor.snapshot().providers[0].id;
  for (const invalid of [null, undefined, 'constructor', 'p-00000000000000000000']) assert.throws(() => monitor.settingsWithVisibility(invalid, true));
  for (const hidden of [null, undefined, 0, 1, 'true', {}, []]) assert.throws(() => monitor.settingsWithVisibility(id, hidden));
  assert.deepEqual(monitor.snapshot().providers.map(p => p.id), [id]);
});

test('deleted hidden providers leave the hidden list without losing their saved preference', async () => {
  let rows = [row('a')]; const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials(rows), queryFn: async () => parsed(1) }); await monitor.refresh(); const id = monitor.snapshot().providers[0].id;
  monitor.settings = monitor.settingsWithVisibility(id, true); await monitor.checkForChanges(); rows = []; await monitor.checkForChanges();
  assert.deepEqual(monitor.snapshot().hiddenProviders, []); assert.deepEqual(monitor.snapshot().providers, []); assert.ok(monitor.settings.hiddenProviders.includes(id));
  rows = [row('a')]; await monitor.checkForChanges(); assert.deepEqual(monitor.snapshot().providers, []); assert.equal(monitor.snapshot().hiddenProviders[0].id, id);
});

test('a hidden in-flight response cannot reappear or contaminate a restored entry', async () => {
  let calls = 0, releaseOld; const oldGate = new Promise(resolve => { releaseOld = resolve; });
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async () => { calls++; if (calls === 1) { await oldGate; return parsed(999); } return parsed(7); } });
  const first = monitor.refresh(); await new Promise(setImmediate); const id = monitor.snapshot().providers[0].id;
  monitor.settings = monitor.settingsWithVisibility(id, true); const hidden = monitor.checkForChanges(); assert.deepEqual(monitor.snapshot().providers, []);
  releaseOld(); await Promise.all([first, hidden]); assert.deepEqual(monitor.snapshot().providers, []); assert.equal(calls, 1);
  monitor.settings = monitor.settingsWithVisibility(id, false); await monitor.checkForChanges(); assert.equal(calls, 2); assert.equal(monitor.snapshot().providers[0].remaining, 7);
});

test('hiding and restoring before an older query completes discards that older result', async () => {
  let calls = 0, releaseOld; const oldGate = new Promise(resolve => { releaseOld = resolve; });
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async () => { calls++; if (calls === 1) { await oldGate; return parsed(999); } return parsed(8); } });
  const first = monitor.refresh(); await new Promise(setImmediate); const id = monitor.snapshot().providers[0].id;
  monitor.settings = monitor.settingsWithVisibility(id, true); const hidden = monitor.checkForChanges(); monitor.settings = monitor.settingsWithVisibility(id, false); const restored = monitor.checkForChanges();
  assert.equal(monitor.snapshot().providers[0].remaining, null); releaseOld(); await Promise.all([first, hidden, restored]);
  assert.equal(calls, 2); assert.equal(monitor.snapshot().providers[0].remaining, 8); assert.equal(monitor.snapshot().providers[0].status, 'ok');
});

test('hiding and restoring cannot bypass a provider retry deadline', async t => {
  let now = Date.now(), calls = 0; t.mock.method(Date, 'now', () => now);
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async () => { calls++; if (calls === 1) throw new QueryError('rate_limit', '请等待', 120); return parsed(6); } });
  await monitor.refresh(); const id = monitor.snapshot().providers[0].id;
  monitor.settings = monitor.settingsWithVisibility(id, true); await monitor.checkForChanges(); monitor.settings = monitor.settingsWithVisibility(id, false); await monitor.checkForChanges(); await monitor.refresh({ force: true }); assert.equal(calls, 1);
  now += 120000; await monitor.checkForChanges(); assert.equal(calls, 2); assert.equal(monitor.snapshot().providers[0].remaining, 6);
});

test('hiding and restoring retains the explicit retry click throttle', async () => {
  let calls = 0;
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: async () => { calls++; throw new QueryError('network_error', '连接失败'); } });
  await monitor.refresh(); const id = monitor.snapshot().providers[0].id; await monitor.retryDetection(id);
  monitor.settings = monitor.settingsWithVisibility(id, true); await monitor.checkForChanges(); monitor.settings = monitor.settingsWithVisibility(id, false); await monitor.checkForChanges();
  assert.equal(calls, 3); await assert.rejects(monitor.retryDetection(id), { code: 'retry_soon' }); assert.equal(calls, 3);
});

test('account quota ignores a token unlimited flag and still requires a real account balance', () => {
  const status = { data: { quota_per_unit: 100000, quota_display_type: 'USD' } };
  for (const quota of [0, 120000]) {
    const result = parseNewApi({ success: true, data: { quota, used_quota: 40000, unlimited_quota: true } }, status, { account: true });
    assert.ok(Math.abs(result.remaining - quota / 100000) < 1e-12); assert.equal(result.unit, 'USD'); assert.equal(result.unlimited, false); assert.match(result.balanceKindLabel, /账户/);
  }
  assert.throws(() => parseNewApi({ data: { unlimited_quota: true } }, status, { account: true }), QueryError);
});

test('unauthorized model keys never fall back to billing sentinel balances', async () => {
  const seen = [];
  const mockFetch = async url => {
    seen.push(url.pathname);
    if (url.pathname.includes('billing')) return response({ hard_limit_usd: 100000000, total_usage: 0 });
    return response({ error: 'fixture-key has no account permission' }, { status: 401 });
  };
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: (c, adapter) => queryProvider(c, adapter, mockFetch) });
  await monitor.refresh(); const state = monitor.snapshot().providers[0];
  assert.deepEqual(seen, ['/v1/usage', '/api/usage/token/']); assert.equal(state.status, 'error'); assert.equal(state.remaining, null); assert.equal(state.unlimited, false); assert.equal(state.lowBalance, false); assert.equal(JSON.stringify(state).includes('fixture-key'), false);
});

test('a late rate-limit response still delays the same credentials after hide and restore', async t => {
  let now = Date.now(), calls = 0, release; t.mock.method(Date, 'now', () => now);
  const gate = new Promise(resolve => { release = resolve; });
  const mockFetch = async () => { calls++; if (calls === 1) { await gate; return response({}, { status: 429, headers: { 'Retry-After': '120' } }); } return response(usage(8)); };
  const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: (c, adapter, _fetch, isActive) => queryProvider(c, adapter, mockFetch, isActive) });
  const first = monitor.refresh(); await new Promise(setImmediate); const id = monitor.snapshot().providers[0].id;
  monitor.settings = monitor.settingsWithVisibility(id, true); const hidden = monitor.checkForChanges(); monitor.settings = monitor.settingsWithVisibility(id, false); const restored = monitor.checkForChanges();
  release(); await Promise.all([first, hidden, restored]); await monitor.refresh({ force: true });
  assert.equal(calls, 1); assert.equal(monitor.snapshot().providers[0].remaining, null); assert.equal(monitor.snapshot().providers[0].lastSuccessAt, null);
  now += 120000; await monitor.checkForChanges(); assert.equal(calls, 2); assert.equal(monitor.snapshot().providers[0].remaining, 8); assert.equal(monitor.snapshot().providers[0].status, 'ok');
});

test('hiding a pending query prevents both automatic and cached protocol fallback requests', async () => {
  for (const cached of [false, true]) {
    let seed = cached, release; const gate = new Promise(resolve => { release = resolve; }); const routes = [];
    const mockFetch = async url => { routes.push(url.pathname); if (seed) { seed = false; return response(usage(3)); } await gate; return response({}, { status: 404 }); };
    const monitor = new Monitor(emptySettings(), { discoverFn: () => selectCredentials([row('a')]), queryFn: (c, adapter, _fetch, isActive) => queryProvider(c, adapter, mockFetch, isActive) });
    if (cached) await monitor.refresh();
    const pending = monitor.refresh({ force: true }); await new Promise(setImmediate); const id = monitor.snapshot().providers[0].id;
    monitor.settings = monitor.settingsWithVisibility(id, true); const hidden = monitor.checkForChanges(); release(); await Promise.all([pending, hidden]);
    assert.deepEqual(routes, cached ? ['/v1/usage', '/v1/usage'] : ['/v1/usage']); assert.deepEqual(monitor.snapshot().providers, []); assert.equal(monitor.snapshot().hiddenProviders[0].id, id);
  }
});

test('amount rules validate complete manual mappings and normalize automatic modes', () => {
  const rule = { amountMode: 'manual', unit: 'CNY', divisor: 100, balanceKind: 'account' };
  for (const type of ['auto', 'sub2api', 'newapi-token', 'newapi-account', 'openrouter', 'deepseek']) assert.deepEqual(validateAdapter({ type, ...rule }), { type, ...rule });
  for (const amountMode of [undefined, null, 'auto']) assert.deepEqual(validateAdapter({ type: 'auto', amountMode, unit: 'CNY', divisor: 100, balanceKind: 'account' }), { type: 'auto' });
  for (const invalid of [{ amountMode: 'invalid' }, { amountMode: 1 }, { ...rule, unit: '' }, { ...rule, unit: '<script>' }, { ...rule, divisor: 0 }, { ...rule, divisor: -1 }, { ...rule, divisor: Infinity }, { ...rule, divisor: '100' }, { ...rule, balanceKind: 'wallet-guess' }]) assert.throws(() => validateAdapter({ type: 'newapi-token', ...invalid }));
  assert.deepEqual(validateSettings({ adapters: { [ID_A]: { type: 'auto', ...rule } } }).adapters[ID_A], { type: 'auto', ...rule });
});

test('manual New API amounts divide raw quota once while leaving usage in its original currency', async () => {
  const rule = { type: 'newapi-token', amountMode: 'manual', unit: 'CNY', divisor: 100, balanceKind: 'account' };
  const result = await queryProvider(credential(), rule, async url => url.pathname === '/api/status'
    ? response({ data: { quota_per_unit: 100000, quota_display_type: 'USD' } })
    : response({ data: { object: 'token_usage', total_available: 250000, total_used: 50000 } }));
  assert.equal(result.remaining, 2500); assert.equal(result.unit, 'CNY'); assert.equal(result.totalUsage, 0.5); assert.equal(result.usageUnit, 'USD');
  assert.match(result.balanceKindLabel, /手动指定/); assert.match(result.balanceKindLabel, /账户/);
});

test('manual balance scope is labelled as user specified and never relabels usage amounts', async () => {
  const result = await queryProvider(credential(), { type: 'sub2api', amountMode: 'manual', unit: 'EUR', divisor: 100, balanceKind: 'account' }, async () => response({ ...usage(1200), mode: 'quota_limited', planName: 'Key quota' }));
  assert.equal(result.remaining, 12); assert.equal(result.unit, 'EUR'); assert.match(result.balanceKindLabel, /账户/); assert.match(result.balanceKindLabel, /手动指定/);
  assert.equal(result.todayUsage, 0.2); assert.equal(result.totalUsage, 3); assert.equal(result.usageUnit, 'USD');
});

test('manual account quota and existing custom mappings each apply their divisor exactly once', async () => {
  const metadata = { usage_script: { enabled: true, baseUrl: 'https://relay-a.example', accessToken: 'account-fixture-token', userId: 42 } };
  const c = selectCredentials([row('meta', undefined, undefined, { meta: JSON.stringify(metadata) })])[0];
  const account = await queryProvider(c, { type: 'newapi-account', amountMode: 'manual', unit: 'CNY', divisor: 100, balanceKind: 'account' }, async url => url.pathname === '/api/status'
    ? response({ data: { quota_per_unit: 100000, quota_display_type: 'USD' } })
    : response({ data: { quota: 125000, used_quota: 50000 } }));
  assert.equal(account.remaining, 1250); assert.equal(account.totalUsage, 0.5); assert.equal(account.usageUnit, 'USD');
  const custom = await queryProvider(credential(), { type: 'custom', amountMode: 'manual', path: '/balance', remainingPath: 'data.balance', unit: 'CNY', divisor: 100, balanceKind: 'account' }, async () => response({ data: { balance: 1250 } }));
  assert.equal(custom.remaining, 12.5); assert.equal(custom.unit, 'CNY');
});

test('manual rules cannot turn unlimited, missing or unauthorized responses into account money', async () => {
  const adapter = { type: 'newapi-token', amountMode: 'manual', unit: 'CNY', divisor: 100, balanceKind: 'account' };
  const unlimited = await queryProvider(credential(), adapter, async url => url.pathname === '/api/status'
    ? response({ data: { quota_per_unit: 100000, quota_display_type: 'USD' } })
    : response({ data: { object: 'token_usage', total_available: 0, total_used: 100000, unlimited_quota: true } }));
  assert.equal(unlimited.remaining, null); assert.equal(unlimited.unlimited, true);
  await assert.rejects(queryProvider(credential(), adapter, async () => response({ data: {} })), QueryError);
  await assert.rejects(queryProvider(credential(), adapter, async () => response({ error: 'no account permission' }, { status: 401 })), { code: 'invalid_key' });
});

test('automatic detection and cached adapters keep rules consistent, then clear them when automatic amounts return', async () => {
  const id = credential().id, rule = { amountMode: 'manual', unit: 'CNY', divisor: 100, balanceKind: 'account' }; const seen = [];
  const monitor = new Monitor({ ...emptySettings(), adapters: { [id]: { type: 'auto', ...rule } } }, { discoverFn: () => [credential()], queryFn: (c, adapter, _fetch, isActive) => { seen.push(structuredClone(adapter)); return queryProvider(c, adapter, async () => response(usage(1200)), isActive); } });
  await monitor.refresh(); assert.equal(monitor.snapshot().providers[0].remaining, 12); await monitor.refresh({ force: true });
  const second = monitor.snapshot().providers[0]; assert.equal(second.remaining, 12); assert.equal(second.unit, 'CNY'); assert.equal(second.totalUsage, 3); assert.equal(second.usageUnit, 'USD');
  assert.deepEqual(seen.slice(0, 2), [{ type: 'auto', ...rule }, { type: 'sub2api', ...rule }]);
  monitor.settings.adapters[id] = { type: 'auto', ...rule, unit: 'EUR', divisor: 200, balanceKind: 'key' }; await monitor.checkForChanges();
  const changed = monitor.snapshot().providers[0]; assert.equal(changed.remaining, 6); assert.equal(changed.unit, 'EUR'); assert.match(changed.balanceKindLabel, /手动指定/); assert.match(changed.balanceKindLabel, /Key/);
  monitor.settings.adapters[id] = { type: 'auto' }; await monitor.checkForChanges(); const restored = monitor.snapshot().providers[0];
  assert.equal(restored.remaining, 1200); assert.equal(restored.unit, 'USD'); assert.equal(restored.balanceKindLabel.includes('手动指定'), false); assert.deepEqual(restored.adapterConfig, { type: 'auto' }); assert.deepEqual(seen.at(-1), { type: 'auto' });
});

test('get-balance retry targets only the selected unlimited key and preserves its manual rule', async () => {
  const selected = credentials()[0], rule = { amountMode: 'manual', unit: 'CNY', divisor: 100, balanceKind: 'account' }; let hasBalance = false; const calls = [];
  const monitor = new Monitor({ ...emptySettings(), adapters: { [selected.id]: { type: 'auto', ...rule } } }, { discoverFn: credentials, queryFn: (c, adapter, _fetch, isActive) => {
    calls.push({ id: c.id, adapter: structuredClone(adapter) });
    return queryProvider(c, adapter, async url => {
      if (url.pathname === '/v1/usage') return response({}, { status: 404 });
      if (url.pathname === '/api/status') return response({ data: { quota_per_unit: 100000, quota_display_type: 'USD' } });
      return response({ data: { object: 'token_usage', total_available: hasBalance && c.id === selected.id ? 700 : 0, total_used: 0, unlimited_quota: !(hasBalance && c.id === selected.id) } });
    }, isActive);
  } });
  await monitor.refresh(); assert.ok(monitor.snapshot().providers.every(p => p.status === 'ok' && p.unlimited && p.remaining === null)); const saved = JSON.stringify(monitor.settings);
  hasBalance = true; await monitor.retryDetection(selected.id); const state = monitor.snapshot();
  assert.equal(calls.length, 3); assert.deepEqual(calls[2], { id: selected.id, adapter: { type: 'auto', ...rule } });
  assert.equal(state.providers[0].remaining, 7); assert.equal(state.providers[0].unit, 'CNY'); assert.equal(state.providers[1].remaining, null); assert.equal(state.providers[1].unlimited, true); assert.equal(JSON.stringify(monitor.settings), saved);
  const otherId = state.providers[1].id; monitor.settings = monitor.settingsWithVisibility(otherId, true); await monitor.checkForChanges(); await assert.rejects(monitor.retryDetection(otherId)); assert.equal(calls.length, 3);
});

test('get-balance retry for an unlimited key still observes upstream rate limits', async t => {
  let now = Date.now(), calls = 0; t.mock.method(Date, 'now', () => now);
  const monitor = new Monitor(emptySettings(), { discoverFn: () => [credential()], queryFn: async () => {
    calls++; if (calls === 1) return { ...parseNewApi({ data: { unlimited_quota: true, total_available: 0 } }, {}), adapter: 'newapi-token' };
    if (calls === 2) throw new QueryError('rate_limit', '请稍后再获取余额', 120); return { ...parsed(9), adapter: 'sub2api' };
  } });
  await monitor.refresh(); const id = monitor.snapshot().providers[0].id; await monitor.retryDetection(id); assert.equal(calls, 2);
  await assert.rejects(monitor.retryDetection(id), { code: 'rate_limit' }); await monitor.refresh({ force: true }); assert.equal(calls, 2);
  now += 120000; await monitor.retryDetection(id); assert.equal(calls, 3); assert.equal(monitor.snapshot().providers[0].remaining, 9);
});

test('usage units do not retain an older currency when later metadata is unavailable', async () => {
  const selected = credential(); let missingMetadata = false;
  const mapping = { type: 'newapi-token', amountMode: 'manual', unit: 'CNY', divisor: 100, balanceKind: 'key' };
  const monitor = new Monitor({ ...emptySettings(), adapters: { [selected.id]: mapping } }, {
    discoverFn: () => [selected],
    queryFn: (c, adapter, _fetch, isActive) => queryProvider(c, adapter, async url => {
      if (url.pathname === '/api/status') return response({ data: missingMetadata ? {} : { quota_per_unit: 100000, quota_display_type: 'USD' } });
      return response({ data: { total_available: missingMetadata ? 0 : 250000, total_used: 50000, unlimited_quota: missingMetadata } });
    }, isActive),
  });
  await monitor.refresh();
  assert.equal(monitor.snapshot().providers[0].usageUnit, 'USD');
  assert.equal(monitor.snapshot().providers[0].totalUsage, 0.5);
  missingMetadata = true; await monitor.refresh({ force: true });
  const next = monitor.snapshot().providers[0];
  assert.equal(next.remaining, null); assert.equal(next.unlimited, true);
  assert.equal(next.totalUsage, 50000); assert.equal(next.usageUnit, 'quota');
});
