import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { DatabaseSync } from 'node:sqlite';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { createInterface } from 'node:readline';
import { once } from 'node:events';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// Fixtures either have no key or use an explicit child-only fetch mock.
function startWorker(directory, database, { preload } = {}) {
  const env = { ...process.env, CC_SWITCH_DB: database, RELAY_BALANCE_STATE_DIR: directory };
  delete env.NODE_OPTIONS; delete env.NODE_PATH;
  const args = [...(preload ? ['--import', pathToFileURL(preload).href] : []), fileURLToPath(new URL('../src/desktop-worker.mjs', import.meta.url))];
  const child = spawn(process.execPath, args, { env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  child.stderr.resume(); const exit = once(child, 'exit'); const input = createInterface({ input: child.stdout });
  const messages = []; let wake;
  input.on('line', line => { messages.push(JSON.parse(line)); wake?.(); });
  const next = async (predicate = () => true, timeout = 4000) => {
    const deadline = Date.now() + timeout;
    while (true) {
      while (messages.length) { const message = messages.shift(); if (predicate(message)) return message; }
      const remaining = deadline - Date.now(); if (remaining <= 0) throw new Error('worker response timeout');
      await new Promise((resolve, reject) => {
        const timer = setTimeout(() => { wake = null; reject(new Error('worker response timeout')); }, remaining);
        wake = () => { wake = null; clearTimeout(timer); resolve(); };
      });
    }
  };
  return { directory, database, child, exit, input, next, send: value => child.stdin.write(JSON.stringify(value) + '\n') };
}

async function fixture(t) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'relay-desktop-fixture-'));
  const database = path.join(directory, 'fixture.db');
  const db = new DatabaseSync(database);
  db.exec('CREATE TABLE providers(id TEXT,name TEXT,app_type TEXT,settings_config TEXT,is_current INTEGER,meta TEXT)'); db.close();
  const sessions = [];
  const start = options => { const session = startWorker(directory, database, options); sessions.push(session); return session; };
  t.after(async () => {
    for (const session of sessions) { if (session.child.exitCode === null) session.child.kill(); await session.exit; session.input.close(); }
    await rm(directory, { recursive: true, force: true });
  });
  return { ...start(), start };
}

test('worker supports empty inventory, validates and persists settings, exits on pipe close', { timeout: 15000 }, async t => {
  const f = await fixture(t);
  const first = await f.next(); assert.equal(first.type, 'snapshot'); assert.deepEqual(first.data.providers, []); assert.ok(first.data.message);
  f.send({ method: 'settings', settings: { intervalSeconds: 59 } }); assert.equal((await f.next(m => m.type === 'error')).type, 'error');
  const id = 'p-0123456789abcdef0123';
  const settings = { intervalSeconds: 600, thresholds: { [id]: 2 }, adapters: { [id]: { type: 'auto' } } };
  f.send({ method: 'settings', settings });
  const saved = await f.next(m => m.type === 'settings_saved'); assert.equal(saved.type, 'settings_saved'); assert.equal(saved.data.intervalSeconds, 600);
  const persisted = JSON.parse(await readFile(path.join(f.directory, 'settings.json'), 'utf8'));
  assert.equal(persisted.intervalSeconds, 600); assert.equal(persisted.thresholds[id], 2); assert.equal(persisted.adapters[id].type, 'auto');
  f.child.stdin.write('this is not JSON\n'); assert.equal((await f.next(m => m.type === 'error')).type, 'error');
  f.send({ method: 'status' }); assert.equal((await f.next(m => m.type === 'snapshot')).type, 'snapshot');
  f.send({ method: 'unknown' }); assert.equal((await f.next(m => m.type === 'error')).type, 'error');
  for (const providerId of [undefined, null, 'constructor', 'https://other.example', 'p-0123456789abcdef0123']) {
    f.send({ method: 'detect', providerId }); assert.equal((await f.next(m => m.type === 'error')).type, 'error');
  }
  f.send({ method: 'status' }); assert.deepEqual((await f.next(m => m.type === 'snapshot')).data.providers, []);
  f.child.stdin.end(); const [code] = await f.exit; assert.equal(code, 0);
});

test('worker discovers CC Switch additions and removals in background', { timeout: 22000 }, async t => {
  const f = await fixture(t); assert.deepEqual((await f.next()).data.providers, []);
  let db = new DatabaseSync(f.database);
  db.prepare('INSERT INTO providers VALUES(?,?,?,?,?,?)').run('new-row', '新品牌测试', 'codex', JSON.stringify({ base_url: 'https://fixture.example', api_key: '' }), 1, '{}'); db.close();
  const added = await f.next(m => m.type === 'snapshot' && m.data.providers.length === 1, 8500);
  const provider = added.data.providers[0]; assert.match(provider.id, /^p-[a-f0-9]{20}$/); assert.equal(provider.name, '新品牌测试');
  assert.equal(provider.status, 'missing'); assert.equal(provider.remaining, null); assert.equal(Object.hasOwn(provider, 'key'), false);
  db = new DatabaseSync(f.database); db.exec('DELETE FROM providers'); db.close();
  const removed = await f.next(m => m.type === 'snapshot' && m.data.providers.length === 0, 8500);
  assert.ok(removed.data.message); f.send({ method: 'stop' }); const [code] = await f.exit; assert.equal(code, 0);
});

test('visibility IPC persists independently across restart, preserves settings and leaves CC Switch unchanged', { timeout: 15000 }, async t => {
  const f = await fixture(t); await f.next();
  const config = JSON.stringify({ base_url: 'https://fixture.example', api_key: '' });
  let db = new DatabaseSync(f.database); db.prepare('INSERT INTO providers VALUES(?,?,?,?,?,?)').run('hidden-row', '可隐藏配置', 'codex', config, 1, '{}'); db.close();
  f.send({ method: 'refresh' }); const added = await f.next(m => m.type === 'snapshot' && m.data.providers.length === 1); const id = added.data.providers[0].id;
  const mapping = { type: 'custom', path: '/balance', remainingPath: 'data.balance', divisor: 100, unit: 'CNY', balanceKind: 'account' };
  f.send({ method: 'settings', settings: { intervalSeconds: 600, thresholds: { [id]: 3 }, adapters: { [id]: mapping } } }); await f.next(m => m.type === 'settings_saved');
  f.send({ method: 'visibility', providerId: id, hidden: true });
  const hidden = await f.next(m => m.type === 'error' || m.type === 'settings_saved' || m.type === 'snapshot' && m.data.hiddenProviders?.length === 1);
  assert.equal(hidden.type, 'snapshot'); assert.deepEqual(hidden.data.providers, []); assert.deepEqual(Object.keys(hidden.data.hiddenProviders[0]).sort(), ['app', 'id', 'name', 'origin']);
  let saved = JSON.parse(await readFile(path.join(f.directory, 'settings.json'), 'utf8')); assert.deepEqual(saved.hiddenProviders, [id]); assert.equal(saved.thresholds[id], 3); assert.deepEqual(saved.adapters[id], mapping);
  f.send({ method: 'settings', settings: { intervalSeconds: 120, thresholds: { [id]: 4 }, adapters: {}, hiddenProviders: [] } }); await f.next(m => m.type === 'settings_saved');
  saved = JSON.parse(await readFile(path.join(f.directory, 'settings.json'), 'utf8')); assert.deepEqual(saved.hiddenProviders, [id]); assert.equal(saved.thresholds[id], 4); assert.deepEqual(saved.adapters[id], mapping);
  f.send({ method: 'stop' }); await f.exit; const restarted = f.start(); const initial = await restarted.next(m => m.type === 'snapshot');
  assert.deepEqual(initial.data.providers, []); assert.equal(initial.data.hiddenProviders[0].id, id);
  for (const request of [{ providerId: id, hidden: 'true' }, { providerId: id, hidden: null }, { providerId: 'p-00000000000000000000', hidden: false }]) {
    restarted.send({ method: 'visibility', ...request }); assert.equal((await restarted.next(m => m.type === 'error')).type, 'error');
  }
  restarted.send({ method: 'visibility', providerId: id, hidden: false }); const visible = await restarted.next(m => m.type === 'snapshot' && m.data.providers.length === 1);
  assert.deepEqual(visible.data.hiddenProviders, []); assert.equal(visible.data.providers[0].threshold, 4); assert.deepEqual(visible.data.providers[0].adapterConfig, mapping);
  saved = JSON.parse(await readFile(path.join(f.directory, 'settings.json'), 'utf8')); assert.deepEqual(saved.hiddenProviders, []);
  db = new DatabaseSync(f.database, { readOnly: true }); const rows = db.prepare('SELECT * FROM providers').all(); db.close(); assert.equal(rows.length, 1); assert.equal(rows[0].settings_config, config); assert.equal(rows[0].name, '可隐藏配置'); assert.equal(rows[0].is_current, 1);
});

test('visibility is saved while a manual refresh or detect request is still waiting on HTTP', { timeout: 15000 }, async t => {
  for (const method of ['refresh', 'detect']) {
    const f = await fixture(t); await f.next(); f.send({ method: 'stop' }); await f.exit;
    const preload = path.join(f.directory, 'offline-fetch-mock.mjs');
    // All fetch calls are intercepted in this child. The manual request stays
    // unresolved until the worker exits, proving visibility does not await it.
    await writeFile(preload, `let count = 0;
globalThis.fetch = async (url, options) => {
  if (new URL(url).origin !== 'https://fixture.example' || options.method !== 'GET') throw new Error('unexpected fixture request');
  if (++count <= ${method === 'detect' ? 2 : 0}) return new Response('{}', { status: 401 });
  process.stdout.write(JSON.stringify({ type: 'fixture_request_started', data: { route: new URL(url).pathname } }) + '\\n');
  return new Promise(() => {});
};
`, 'utf8');
    const worker = f.start({ preload }); await worker.next(m => m.type === 'snapshot' && m.data.providers.length === 0);
    const db = new DatabaseSync(f.database); db.prepare('INSERT INTO providers VALUES(?,?,?,?,?,?)').run('pending-row', '待移出配置', 'codex', JSON.stringify({ base_url: 'https://fixture.example/v1', api_key: 'offline-fixture-key' }), 1, '{}'); db.close();
    worker.send({ method: 'refresh' });
    if (method === 'detect') {
      const failed = await worker.next(m => m.type === 'snapshot' && m.data.providers[0]?.status === 'error');
      worker.send({ method: 'detect', providerId: failed.data.providers[0].id });
    }
    await worker.next(m => m.type === 'fixture_request_started');
    const pending = await worker.next(m => m.type === 'snapshot' && m.data.refreshing && m.data.providers.length === 1); const id = pending.data.providers[0].id;
    worker.send({ method: 'visibility', providerId: id, hidden: true });
    const hidden = await worker.next(m => m.type === 'snapshot' && m.data.hiddenProviders?.some(p => p.id === id), 3000);
    assert.equal(hidden.data.refreshing, true); assert.deepEqual(hidden.data.providers, []);
    const saved = JSON.parse(await readFile(path.join(f.directory, 'settings.json'), 'utf8')); assert.deepEqual(saved.hiddenProviders, [id]);
    worker.send({ method: 'stop' }); const [code] = await worker.exit; assert.equal(code, 0);
  }
});
