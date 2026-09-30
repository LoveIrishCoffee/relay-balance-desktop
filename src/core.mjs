import { DatabaseSync } from 'node:sqlite';
import { createHash } from 'node:crypto';
import { readFile, mkdir, writeFile, rename } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

export const runtimeDir = process.env.RELAY_BALANCE_STATE_DIR || path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), '.local', 'share'), 'RelayBalanceDesktop', 'state');
export const dbPath = process.env.CC_SWITCH_DB || path.join(os.homedir(), '.cc-switch', 'cc-switch.db');
export const DEFAULT_SETTINGS = Object.freeze({ intervalSeconds: 300, thresholds: Object.freeze({}), adapters: Object.freeze({}), hiddenProviders: Object.freeze([]) });
export const finite = value => (typeof value === 'number' || typeof value === 'string' && value.trim() !== '') && Number.isFinite(Number(value)) ? Number(value) : null;
const hash = value => createHash('sha256').update(value).digest('hex');
const object = value => !!value && typeof value === 'object' && !Array.isArray(value);
const idPattern = /^p-[a-f0-9]{20}$/;
const labels = { auto: '自动识别', sub2api: 'Sub2API', 'newapi-token': 'New API · Key 额度', 'newapi-account': 'New API · 账户余额', openrouter: 'OpenRouter', deepseek: 'DeepSeek', custom: '自定义查询' };
const unitPattern = /^(?:[A-Za-z]{2,12}|[¥$€£]|人民币|积分|点数)$/;
const safeText = (value, limit = 120) => typeof value === 'string' ? value.replace(/[\x00-\x1f\x7f]/g, ' ').slice(0, limit) : '';
const unitOf = value => typeof value === 'string' && unitPattern.test(value) ? (/^[a-z]{3}$/i.test(value) ? value.toUpperCase() : value) : '未标单位';
const routeOK = route => typeof route === 'string' && route.length <= 256 && /^\/[A-Za-z0-9_./-]*$/.test(route) && !route.startsWith('//') && !route.split('/').some(p => p === '.' || p === '..');

export async function readJson(file, fallback = null) { try { return JSON.parse(await readFile(file, 'utf8')); } catch { return fallback; } }
export async function saveJson(file, value) {
  await mkdir(path.dirname(file), { recursive: true });
  const temp = file + '.' + process.pid + '.tmp';
  await writeFile(temp, JSON.stringify(value, null, 2), { mode: 0o600 });
  await rename(temp, file);
}
export function validateAdapter(input) {
  if (!object(input) || !Object.hasOwn(labels, input.type)) throw new Error('不支持的查询方式');
  if (Object.keys(input).some(k => !['type', 'path', 'remainingPath', 'unit', 'divisor', 'balanceKind'].includes(k))) throw new Error('查询设置格式错误');
  if (input.type !== 'custom') return { type: input.type };
  if (!routeOK(input.path)) throw new Error('接口须为同站点路径，不能包含查询参数或跳转');
  if (typeof input.remainingPath !== 'string' || input.remainingPath.length > 200 || !/^[A-Za-z_][A-Za-z0-9_]*(?:\.(?:[A-Za-z_][A-Za-z0-9_]*|[0-9]+))*$/.test(input.remainingPath) || input.remainingPath.split('.').some(p => ['__proto__', 'constructor', 'prototype'].includes(p))) throw new Error('余额字段路径无效');
  if (typeof input.unit !== 'string' || !unitPattern.test(input.unit)) throw new Error('请明确填写余额单位');
  const divisor = input.divisor ?? 1;
  if (typeof divisor !== 'number' || !Number.isFinite(divisor) || divisor <= 0 || divisor > 1e15) throw new Error('换算除数须大于零');
  const balanceKind = input.balanceKind || 'quota';
  if (!['account', 'key', 'quota'].includes(balanceKind)) throw new Error('余额类型无效');
  return { type: 'custom', path: input.path, remainingPath: input.remainingPath, unit: input.unit, divisor, balanceKind };
}
export function validateSettings(input) {
  if (!object(input) || Object.keys(input).some(k => !['intervalSeconds', 'thresholds', 'adapters', 'hiddenProviders'].includes(k))) throw new Error('设置格式错误');
  const intervalSeconds = input.intervalSeconds ?? 300;
  if (!Number.isInteger(intervalSeconds) || intervalSeconds < 60 || intervalSeconds > 86400) throw new Error('刷新间隔须为 60–86400 秒');
  const thresholds = {}, adapters = {};
  for (const [name, target] of [['thresholds', thresholds], ['adapters', adapters]]) {
    const values = input[name] ?? {};
    if (!object(values) || Object.keys(values).length > 10000) throw new Error('设置项目过多或格式无效');
    for (const [id, value] of Object.entries(values)) {
      if (!idPattern.test(id)) throw new Error('配置标识无效');
      if (name === 'adapters') target[id] = validateAdapter(value);
      else {
        if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > 1e9) throw new Error('余额阈值无效');
        target[id] = value;
      }
    }
  }
  const hidden = input.hiddenProviders ?? [];
  if (!Array.isArray(hidden) || hidden.length > 10000 || hidden.some(id => typeof id !== 'string' || !idPattern.test(id))) throw new Error('已移出配置列表无效');
  return { intervalSeconds, thresholds, adapters, hiddenProviders: [...new Set(hidden)] };
}
export async function loadSettings() {
  try {
    const saved = await readJson(path.join(runtimeDir, 'settings.json'), DEFAULT_SETTINGS);
    // Migrate the three v1.0 preference keys once; discovery itself is unrestricted.
    if (object(saved?.thresholds) && Object.keys(saved.thresholds).some(k => ['xindu', 'mtmai', 'namax'].includes(k))) {
      const oldHosts = { 'xindu.xyz': 'xindu', 'yuepa8.com': 'mtmai', 'namax.ai': 'namax', 'www.namax.ai': 'namax' };
      const thresholds = Object.fromEntries(Object.entries(saved.thresholds).filter(([k]) => idPattern.test(k)));
      for (const credential of discover()) {
        const old = credential.origin ? oldHosts[new URL(credential.origin).hostname] : null;
        if (old && Object.hasOwn(saved.thresholds, old)) thresholds[credential.id] ??= saved.thresholds[old];
      }
      return validateSettings({ intervalSeconds: saved.intervalSeconds, thresholds, adapters: saved.adapters || {}, hiddenProviders: saved.hiddenProviders || [] });
    }
    return validateSettings(saved);
  }
  catch { return structuredClone(DEFAULT_SETTINGS); }
}

function codexProvider(config) {
  if (typeof config !== 'string') return {};
  const value = (text, name) => text.match(new RegExp('^\\s*' + name + '\\s*=\\s*["\']([^"\'\\r\\n]+)["\']', 'm'))?.[1];
  const selected = value(config, 'model_provider');
  const candidates = [];
  for (const block of config.split(/(?=^\s*\[)/m)) {
    const name = block.match(/^\s*\[model_providers\.(?:"([^"]+)"|'([^']+)'|([^\]]+))\]/)?.slice(1).find(Boolean);
    if (name && (!selected || name === selected)) candidates.push({ base: value(block, 'base_url'), key: value(block, 'experimental_bearer_token'), envKey: value(block, 'env_key') });
  }
  return candidates.length === 1 ? candidates[0] : {};
}
function jsonObject(value) { if (typeof value === 'string') value = JSON.parse(value); return object(value) ? value : {}; }
export function selectCredentials(rows) {
  if (!Array.isArray(rows) || rows.length > 1000) throw new Error('配置数量超过 1000');
  const result = [];
  for (const row of rows) {
    const app = safeText(row.app_type || 'unknown', 40);
    const id = 'p-' + hash(String(row.app_type || '') + '\0' + String(row.id)).slice(0, 20);
    let config = {}, meta = {}, invalid = false;
    try { config = jsonObject(row.settings_config); } catch { invalid = true; }
    try { meta = jsonObject(row.meta); } catch { /* Optional metadata need not block ordinary API credentials. */ }
    const cp = codexProvider(config.config), env = object(config.env) ? config.env : {};
    const base = env.ANTHROPIC_BASE_URL || env.GOOGLE_GEMINI_BASE_URL || cp.base || config.base_url || config.baseUrl || config.baseURL || config.options?.baseURL;
    const keyValue = cp.key || (cp.envKey && (env[cp.envKey] || config.auth?.[cp.envKey])) || config.auth?.OPENAI_API_KEY || env.ANTHROPIC_AUTH_TOKEN || env.ANTHROPIC_API_KEY || env.GEMINI_API_KEY || env.GOOGLE_API_KEY || env.OPENROUTER_API_KEY || config.api_key || config.apiKey || config.options?.apiKey;
    const key = typeof keyValue === 'string' ? keyValue.trim() : '';
    // Empty official login records do not represent an API account.
    if (!invalid && !base && !key && (row.category === 'official' || /^(official|default)$/i.test(String(row.id)) || !Object.keys(config).length)) continue;
    let origin = '', basePath = '', blockedReason = '', metaUsage = null;
    if (invalid) blockedReason = 'CC Switch 配置无法解析';
    try {
      if (typeof base !== 'string') throw new Error();
      const url = new URL(base);
      if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash || !routeOK(url.pathname) || key && base.includes(key)) throw new Error();
      origin = url.origin; basePath = url.pathname.replace(/\/$/, '');
    } catch { blockedReason ||= '缺少有效的 HTTPS API 地址，请在 CC Switch 检查配置'; }
    if (!key || /[\r\n\x00]/.test(key)) blockedReason ||= '缺少 API Key，请在 CC Switch 完成配置';
    const usage = meta.usage_script || meta.usageScript;
    if (object(usage) && usage.enabled !== false && origin) {
      try {
        const url = new URL(usage.baseUrl || base);
        if (url.protocol === 'https:' && url.origin === origin && !url.username && !url.password && !url.search && !url.hash) {
          const token = typeof usage.accessToken === 'string' ? usage.accessToken.trim() : '';
          const userId = String(usage.userId ?? '');
          if (token && !/[\r\n\x00]/.test(token) && /^\d{1,20}$/.test(userId)) metaUsage = { accessToken: token, userId };
        }
      } catch { /* Never forward metadata credentials to a different origin. */ }
    }
    let name = safeText(row.name, 120) || '未命名配置';
    for (const secret of [key, metaUsage?.accessToken]) if (secret) name = name.split(secret).join('[已隐藏]');
    result.push({ id, name, app, origin, basePath, key, current: row.is_current === 1 || row.is_current === true, blockedReason, metaUsage, fingerprint: hash(JSON.stringify([origin, basePath, key, metaUsage, blockedReason])) });
  }
  if (new Set(result.map(c => c.id)).size !== result.length) throw new Error('配置标识重复');
  return result;
}
export function discover(file = dbPath) {
  let db;
  try {
    db = new DatabaseSync(file, { readOnly: true });
    db.exec('PRAGMA query_only = ON; PRAGMA busy_timeout = 3000;');
    return selectCredentials(db.prepare('SELECT * FROM providers LIMIT 1001').all());
  } finally { db?.close(); }
}

export class QueryError extends Error {
  constructor(code, message, retryAfterSeconds = 0) { super(message); this.code = code; this.retryAfterSeconds = retryAfterSeconds; }
}
export async function requestJson(credential, route, { fetchImpl = fetch, auth = 'key' } = {}) {
  let target;
  try {
    const origin = new URL(credential.origin);
    if (!routeOK(route) || origin.origin !== credential.origin || origin.protocol !== 'https:' || origin.username || origin.password || origin.search || origin.hash) throw new Error();
    if ([credential.key, credential.metaUsage?.accessToken].some(secret => secret && route.includes(secret))) throw new Error();
    target = new URL(route, origin);
    if (target.origin !== origin.origin) throw new Error();
  } catch { throw new QueryError('unsafe_target', '接口地址无效，仅允许同站点 HTTPS 查询'); }
  const headers = { Accept: 'application/json' };
  if (auth === 'key') headers.Authorization = 'Bearer ' + credential.key;
  else if (auth === 'account') {
    if (!credential.metaUsage?.accessToken || !/^\d{1,20}$/.test(credential.metaUsage.userId)) throw new QueryError('account_auth', '账户接口需要 CC Switch 用量配置中的访问令牌和用户 ID');
    headers.Authorization = 'Bearer ' + credential.metaUsage.accessToken;
    headers['New-Api-User'] = credential.metaUsage.userId;
  } else if (auth !== 'none') throw new QueryError('unsafe_auth', '查询凭据类型无效');
  let response;
  try {
    response = await fetchImpl(target, { method: 'GET', headers, redirect: 'manual', signal: AbortSignal.timeout(12000) });
    if (response.status >= 300 && response.status < 400) throw new QueryError('redirect', '接口发生跳转，已停止发送凭据');
    if ([401, 403].includes(response.status)) throw new QueryError('invalid_key', '凭据无效或没有查询权限，请在 CC Switch 更新配置');
    if (response.status === 429 || response.status === 503 && response.headers.has('retry-after')) {
      const raw = response.headers.get('retry-after');
      const delay = finite(raw) ?? ((Date.parse(raw) - Date.now()) / 1000);
      throw new QueryError('rate_limit', '查询受到限流，稍后自动重试', Math.max(60, Math.min(86400, Number.isFinite(delay) ? delay : 300)));
    }
    if ([404, 405].includes(response.status)) throw new QueryError('endpoint_missing', '站点未提供此查询接口');
    if (!response.ok) throw new QueryError('http_error', `查询接口返回 HTTP ${response.status}`);
    if (!response.body) throw new QueryError('not_json', '接口未返回可识别的数据');
    const reader = response.body.getReader(), chunks = []; let size = 0;
    while (true) {
      const { done, value } = await reader.read(); if (done) break;
      size += value.byteLength;
      if (size > 2 * 1024 * 1024) { await reader.cancel(); throw new QueryError('response_size', '查询响应过大'); }
      chunks.push(value);
    }
    try { return JSON.parse(Buffer.concat(chunks).toString('utf8')); }
    catch { throw new QueryError('not_json', '接口未返回可识别的数据'); }
  } catch (error) {
    if (error instanceof QueryError) throw error;
    throw new QueryError('network_error', '网络连接失败或超时，稍后自动重试');
  } finally { if (response?.body && !response.bodyUsed) await response.body.cancel().catch(() => {}); }
}
function active(raw) {
  if (!object(raw) || raw.success === false || raw.code === false || raw.isValid === false || raw.is_active === false || raw.error) throw new QueryError('inactive', '站点返回无效或停用的账户状态');
}
function schema() { throw new QueryError('schema_error', '接口未返回可识别的余额字段，不能推断为零'); }
export function parseUsage(raw) {
  active(raw);
  let remaining = finite(raw.remaining ?? raw.quota?.remaining ?? raw.balance);
  let unit = unitOf(raw.unit || raw.quota?.unit), unlimited = false;
  let balanceKindLabel = '接口返回的剩余额度';
  let message = '余额含义以站点套餐和接口为准';
  if (raw.mode === 'unrestricted' && raw.planName === '钱包余额') {
    balanceKindLabel = '账户钱包余额'; message = '站点钱包余额；用量为当前 API Key 的实际花费';
  } else if (raw.mode === 'quota_limited' || object(raw.quota)) {
    balanceKindLabel = 'API Key 剩余额度'; message = 'API Key 限额，可能不等于账户余额';
  } else if (object(raw.subscription)) {
    balanceKindLabel = '订阅剩余额度'; message = '套餐周期额度，非账户钱包余额';
    const limits = ['daily_limit_usd', 'weekly_limit_usd', 'monthly_limit_usd'];
    if (remaining === -1 && limits.every(k => Object.hasOwn(raw.subscription, k) && (raw.subscription[k] === null || finite(raw.subscription[k]) === 0))) { unlimited = true; remaining = null; }
  }
  if (!unlimited && remaining === null && Array.isArray(raw.rate_limits)) {
    const amounts = raw.rate_limits.filter(r => finite(r.limit) > 0 && finite(r.remaining) !== null).map(r => Number(r.remaining));
    if (amounts.length) { remaining = Math.min(...amounts); balanceKindLabel = '窗口剩余额度'; message = '各限额窗口中最小剩余额度，非账户钱包余额'; }
  }
  if (!unlimited && remaining === null) schema();
  return { remaining, unit, balanceKindLabel, unlimited, todayUsage: finite(raw.usage?.today?.actual_cost), totalUsage: finite(raw.usage?.total?.actual_cost), message };
}
export function parseNewApi(raw, statusRaw, { account = false } = {}) {
  active(raw); const data = raw.data;
  if (!object(data)) schema();
  const unlimited = !account && data.unlimited_quota === true;
  const amount = finite(account ? data.quota : data.total_available), used = finite(account ? data.used_quota : data.total_used);
  if (!unlimited && amount === null) schema();
  const status = statusRaw?.data || {}, scale = finite(status.quota_per_unit), display = status.quota_display_type;
  let factor = 1, unit = 'quota';
  if (scale > 0) {
    if (display === 'USD') { factor = 1 / scale; unit = 'USD'; }
    if (display === 'CNY' && finite(status.usd_exchange_rate) > 0) { factor = Number(status.usd_exchange_rate) / scale; unit = 'CNY'; }
    if (display === 'CUSTOM' && finite(status.custom_currency_exchange_rate) > 0 && unitOf(status.custom_currency_symbol) !== '未标单位') { factor = Number(status.custom_currency_exchange_rate) / scale; unit = unitOf(status.custom_currency_symbol); }
  }
  const remaining = unlimited ? null : amount * factor;
  if (remaining !== null && !Number.isFinite(remaining)) schema();
  return { remaining, unit, unlimited, todayUsage: null, totalUsage: used === null || !Number.isFinite(used * factor) ? null : used * factor,
    balanceKindLabel: account ? '账户余额' : unlimited ? 'API Key 限额（非余额）' : 'API Key 剩余额度',
    message: unlimited ? '此 API Key 未设限额，不代表账户资金无限；当前查询未获取账户余额，账户余额仍可能不足' : unit === 'quota' ? '站点未提供可靠货币换算，显示原始额度' : account ? '账户接口返回，按站点公布的比例换算' : 'Key 剩余额度，按站点公布的比例换算；不等于账户余额' };
}
export function parseOpenRouter(raw) {
  active(raw); const credits = finite(raw.data?.total_credits), used = finite(raw.data?.total_usage);
  if (credits === null || used === null || !Number.isFinite(credits - used)) schema();
  return { remaining: credits - used, unit: 'USD', unlimited: false, todayUsage: null, totalUsage: used, balanceKindLabel: '账户余额', message: '站点总充值减累计用量' };
}
export function parseDeepSeek(raw) {
  active(raw);
  if (!Array.isArray(raw.balance_infos) || raw.balance_infos.length !== 1) throw new QueryError('schema_error', '余额币种不唯一，无法合并显示，请配置明确的余额字段');
  const data = raw.balance_infos[0], remaining = finite(data?.total_balance), unit = unitOf(data?.currency);
  if (remaining === null || unit === '未标单位') schema();
  return { remaining, unit, unlimited: false, todayUsage: null, totalUsage: null, balanceKindLabel: '账户余额', message: raw.is_available === false ? '站点返回余额，但当前账户不可用' : '站点账户余额接口返回' };
}
export function parseCustom(raw, input) {
  active(raw); const adapter = validateAdapter(input);
  let value = raw;
  for (const key of adapter.remainingPath.split('.')) value = value != null && Object.hasOwn(value, key) ? value[key] : undefined;
  const number = finite(value), remaining = number === null ? null : number / adapter.divisor;
  if (remaining === null || !Number.isFinite(remaining)) schema();
  return { remaining, unit: unitOf(adapter.unit), unlimited: false, todayUsage: null, totalUsage: null,
    balanceKindLabel: { account: '账户余额', key: 'API Key 剩余额度', quota: '自定义剩余额度' }[adapter.balanceKind], message: '按用户配置的接口字段和单位读取，请以站点文档核对' };
}
function routes(credential) {
  const base = credential.basePath || '';
  const prefix = base.replace(/\/(?:v1|v1beta)$/, '');
  return { usage: prefix + '/v1/usage', token: prefix + '/api/usage/token/', status: prefix + '/api/status', account: prefix + '/api/user/self' };
}
async function queryWith(credential, adapter, fetchImpl, isActive) {
  const route = routes(credential), get = (url, auth = 'key') => {
    if (!isActive()) throw new QueryError('cancelled', '配置已移出或更改，停止后续查询');
    return requestJson(credential, url, { fetchImpl, auth });
  };
  let result;
  if (adapter.type === 'sub2api') result = parseUsage(await get(route.usage));
  else if (adapter.type === 'newapi-token' || adapter.type === 'newapi-account') {
    const account = adapter.type === 'newapi-account';
    const raw = await get(account ? route.account : route.token, account ? 'account' : 'key');
    // Conversion discovery is public and optional. Unknown ratios must stay raw.
    let status = null; try { status = await get(route.status, 'none'); } catch (error) { if (error.code === 'rate_limit' || error.code === 'cancelled') throw error; }
    result = parseNewApi(raw, status, { account });
  } else if (adapter.type === 'openrouter') result = parseOpenRouter(await get('/api/v1/credits'));
  else if (adapter.type === 'deepseek') result = parseDeepSeek(await get('/user/balance'));
  else if (adapter.type === 'custom') result = parseCustom(await get(adapter.path), adapter);
  else throw new QueryError('unsupported', '未选择可识别的查询方式');
  return { ...result, adapter: adapter.type, adapterLabel: labels[adapter.type] };
}
export async function queryProvider(credential, input = { type: 'auto' }, fetchImpl = fetch, isActive = () => true) {
  const adapter = validateAdapter(input);
  if (adapter.type !== 'auto') return queryWith(credential, adapter, fetchImpl, isActive);
  const hostname = new URL(credential.origin).hostname;
  const candidates = hostname === 'openrouter.ai' ? ['openrouter'] : hostname === 'api.deepseek.com' ? ['deepseek'] : [...(credential.metaUsage ? ['newapi-account'] : []), 'sub2api', 'newapi-token'];
  let authError;
  for (const type of candidates) {
    try { return await queryWith(credential, { type }, fetchImpl, isActive); }
    catch (error) {
      if (error.code === 'invalid_key' || error.code === 'inactive') authError = error;
      else if (!['endpoint_missing', 'schema_error', 'not_json', 'account_auth'].includes(error.code)) throw error;
    }
  }
  if (authError) throw authError;
  throw new QueryError('unsupported', '适配失败，未识别到余额接口。可点击“适配”重新尝试');
}

export class Monitor {
  constructor(settings = DEFAULT_SETTINGS, { discoverFn = discover, queryFn = queryProvider } = {}) {
    this.settings = validateSettings(settings); this.discoverFn = discoverFn; this.queryFn = queryFn;
    this.entries = new Map(); this.checkedAt = null; this.message = ''; this.inflight = null; this.pendingChanges = false;
  }
  snapshot() {
    const hidden = new Set(this.settings.hiddenProviders);
    const entries = [...this.entries.values()];
    return { providers: entries.filter(({ state }) => !hidden.has(state.id)).map(({ state }) => {
      const threshold = this.settings.thresholds[state.id] ?? 5;
      return { ...state, threshold, adapterConfig: this.settings.adapters[state.id] || { type: 'auto' }, lowBalance: state.status === 'ok' && !state.unlimited && state.remaining !== null && ['USD', 'CNY', 'EUR', 'GBP', 'JPY', 'HKD'].includes(state.unit) && state.remaining <= threshold };
    }), hiddenProviders: entries.filter(({ state }) => hidden.has(state.id)).map(({ state: { id, name, app, origin } }) => ({ id, name, app, origin })),
      checkedAt: this.checkedAt, intervalSeconds: this.settings.intervalSeconds, refreshing: !!this.inflight, message: this.message };
  }
  settingsWithVisibility(id, hidden) {
    if (typeof id !== 'string' || !idPattern.test(id) || typeof hidden !== 'boolean') throw new QueryError('invalid_visibility', '移出或恢复的配置无效');
    if (!this.reconcile() || !this.entries.has(id)) throw new QueryError('invalid_visibility', '配置已不在 CC Switch 中，请刷新列表');
    const ids = new Set(this.settings.hiddenProviders);
    if (hidden) ids.add(id); else ids.delete(id);
    return validateSettings({ ...this.settings, hiddenProviders: [...ids] });
  }
  reconcile() {
    let credentials;
    try { credentials = this.discoverFn(); }
    catch {
      this.message = '无法只读打开 CC Switch 数据库，请确认已安装并配置 CC Switch，或检查 CC_SWITCH_DB 路径';
      for (const entry of this.entries.values()) { entry.state.status = entry.state.lastSuccessAt ? 'stale' : 'error'; entry.state.message = this.message; }
      return false;
    }
    const next = new Map();
    const hiddenIds = new Set(this.settings.hiddenProviders);
    for (const credential of credentials) {
      const adapter = this.settings.adapters[credential.id] || { type: 'auto' };
      const hidden = hiddenIds.has(credential.id);
      const signature = credential.fingerprint + JSON.stringify(adapter);
      let entry = this.entries.get(credential.id);
      if (!entry || entry.signature !== signature) {
        entry = { credential, adapter, signature, hidden, lastAttempt: 0, retryAt: 0, autoAdapter: null, needsQuery: !hidden, paused: false,
          state: { id: credential.id, name: credential.name, app: credential.app, origin: credential.origin, current: credential.current,
            status: credential.blockedReason ? 'missing' : 'pending', message: credential.blockedReason || '正在识别余额接口', remaining: null, todayUsage: null, totalUsage: null, unlimited: false, unit: '', balanceKindLabel: '', adapter: adapter.type, adapterLabel: labels[adapter.type], updatedAt: null, lastSuccessAt: null } };
      } else { entry.credential = credential; Object.assign(entry.state, { name: credential.name, current: credential.current, app: credential.app }); }
      if (entry.hidden !== hidden) {
        // Replace the entry so a response started before removal cannot update a restored row.
        // Retain Retry-After and click throttling across a hide/restore cycle.
        entry = { ...entry, hidden, needsQuery: !hidden, paused: false, autoAdapter: null, lastAttempt: 0,
          state: { ...entry.state, status: credential.blockedReason ? 'missing' : 'pending', message: credential.blockedReason || '恢复后重新查询余额' } };
      }
      next.set(credential.id, entry);
    }
    this.entries = next; this.message = next.size ? '' : 'CC Switch 中尚未发现配置了 API 的中转站';
    return true;
  }
  async checkForChanges() { return this.refresh({ changedOnly: true }); }
  async retryDetection(id) {
    if (!idPattern.test(id)) throw new QueryError('invalid_id', '配置标识无效');
    if (!this.reconcile()) return this.snapshot();
    const entry = this.entries.get(id);
    if (!entry || entry.hidden || !['unsupported','error','stale'].includes(entry.state.status) || entry.credential.blockedReason) throw new QueryError('invalid_detection', '仅适配或查询失败的配置可以重试');
    if (entry.retryAt > Date.now()) throw new QueryError('rate_limit', '站点要求等待，限流结束后才能重试');
    if (Date.now() - (entry.lastManualDetection || 0) < 10000) throw new QueryError('retry_soon', '请等待 10 秒后再继续识别');
    entry.lastManualDetection = Date.now();
    entry.paused = false; entry.needsQuery = true; entry.autoAdapter = null; entry.lastAttempt = 0;
    entry.state.status = 'pending'; entry.state.message = entry.adapter.type === 'auto' ? '正在重新检测已支持的余额协议' : '正在按已保存的手动配置重试查询';
    return this.refresh({ changedOnly: true });
  }
  async refresh({ force = false, changedOnly = false } = {}) {
    const valid = this.reconcile();
    if (this.inflight) {
      if (valid && [...this.entries.values()].some(e => !e.hidden && e.needsQuery)) this.pendingChanges = true;
      await this.inflight; return this.snapshot();
    }
    if (!valid) return this.snapshot();
    const candidates = [...this.entries.values()].filter(e => !e.hidden && !e.paused && !e.credential.blockedReason && Date.now() >= e.retryAt && (e.needsQuery || !changedOnly && (force || Date.now() - e.lastAttempt >= 10000)));
    if (!candidates.length) return this.snapshot();
    this.inflight = this.perform(candidates);
    try { await this.inflight; } finally { this.inflight = null; }
    if (this.pendingChanges) { this.pendingChanges = false; return this.refresh({ changedOnly: true }); }
    return this.snapshot();
  }
  async perform(candidates) {
    const queue = candidates.slice(), origins = new Set();
    const run = async () => {
      while (queue.length) {
        const index = queue.findIndex(e => !origins.has(e.credential.origin));
        if (index < 0) return;
        const entry = queue.splice(index, 1)[0];
        if (this.entries.get(entry.state.id) !== entry || entry.hidden || this.settings.hiddenProviders.includes(entry.state.id)) continue;
        const isActive = () => this.entries.get(entry.state.id) === entry && !entry.hidden && !this.settings.hiddenProviders.includes(entry.state.id);
        origins.add(entry.credential.origin); entry.lastAttempt = Date.now(); entry.needsQuery = false;
        try {
          let result;
          try { result = await this.queryFn(entry.credential, entry.autoAdapter || entry.adapter, undefined, isActive); }
          catch (error) {
            if (isActive() && entry.autoAdapter && ['endpoint_missing', 'schema_error', 'not_json', 'unsupported'].includes(error.code)) { entry.autoAdapter = null; result = await this.queryFn(entry.credential, entry.adapter, undefined, isActive); }
            else throw error;
          }
          if (this.entries.get(entry.state.id) === entry) {
            const now = new Date().toISOString();
            if (entry.adapter.type === 'auto' && Object.hasOwn(labels, result.adapter) && result.adapter !== 'auto') entry.autoAdapter = { type: result.adapter };
            Object.assign(entry.state, result, { status: 'ok', updatedAt: now, lastSuccessAt: now }); entry.retryAt = 0;
          }
        } catch (error) {
          const current = this.entries.get(entry.state.id);
          // A cancelled result cannot restore money, but its server's wait deadline still applies.
          if (error.retryAfterSeconds && current?.credential.fingerprint === entry.credential.fingerprint) {
            current.retryAt = Math.max(current.retryAt, Date.now() + error.retryAfterSeconds * 1000);
          }
          if (this.entries.get(entry.state.id) === entry) {
            entry.paused = error.code === 'unsupported';
            if (entry.paused) Object.assign(entry.state, { remaining: null, todayUsage: null, totalUsage: null, unlimited: false, unit: '', balanceKindLabel: '', lastSuccessAt: null });
            Object.assign(entry.state, { status: entry.paused ? 'unsupported' : entry.state.lastSuccessAt ? 'stale' : 'error', updatedAt: new Date().toISOString(), message: error instanceof QueryError ? error.message : '查询失败，稍后自动重试' });
          }
        } finally { origins.delete(entry.credential.origin); }
      }
    };
    await Promise.all([run(), run(), run()]); this.checkedAt = new Date().toISOString();
  }
}
