import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { DatabaseSync } from 'node:sqlite';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { createInterface } from 'node:readline';
import { once } from 'node:events';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Missing-key fixtures exercise IPC without making external requests.
async function fixture(t) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'relay-desktop-fixture-'));
  const database = path.join(directory, 'fixture.db');
  const db = new DatabaseSync(database);
  db.exec('CREATE TABLE providers(id TEXT,name TEXT,app_type TEXT,settings_config TEXT,is_current INTEGER,meta TEXT)'); db.close();
  const env = { ...process.env, CC_SWITCH_DB: database, RELAY_BALANCE_STATE_DIR: directory };
  delete env.NODE_OPTIONS; delete env.NODE_PATH;
  const child = spawn(process.execPath, [fileURLToPath(new URL('../src/desktop-worker.mjs', import.meta.url))], { env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
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
  t.after(async () => {
    if (child.exitCode === null) child.kill();
    await exit; input.close(); await rm(directory, { recursive: true, force: true });
  });
  return { directory, database, child, exit, next, send: value => child.stdin.write(JSON.stringify(value) + '\n') };
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
