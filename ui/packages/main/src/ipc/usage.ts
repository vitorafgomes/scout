import { promises as fs } from 'node:fs';
import type { IpcMain } from 'electron';
import {
  CHANNEL,
  type AdminKeyProvider,
  type OrgSpendState,
  type ProviderKeyValidateResponse,
  type OpenRouterKeyInfo,
  type OpenRouterKeyState,
  type SourceUsage,
  type SourceUsageTotals,
  type UsageSnapshot,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { MainOnlyVaultKey, Vault } from '../vault/safe-storage-vault.js';

const OPENROUTER_KEY_URL = 'https://openrouter.ai/api/v1/key';
const OPENROUTER_READ_INTERVAL_MS = 10 * 60 * 1000;
const OPENROUTER_TIMEOUT_MS = 5_000;
const EMPTY_TOTALS: SourceUsageTotals = Object.freeze({ tokens: 0, answers: 0, cost: null });

interface StoredSource extends SourceUsage {
  readonly day: string;
  readonly monthKey: string;
}

interface UsageFile {
  readonly v: 1;
  readonly sources: Record<string, StoredSource>;
}

export interface UsageStoreDeps {
  /** The JSON file under Scout's own folder that keeps the totals across restarts. */
  readonly file: string;
  readonly now: () => Date;
  readonly logger: Logger;
  /** Pushes the new totals to every window after each change. */
  readonly broadcast: (usage: UsageSnapshot) => void;
}

export interface UsageStore {
  /** Applies the JSON of one `SCOUT_LOCAL ` stdout line: a `usage` or `usage-limit` event; anything else is ignored. */
  handleWorkerLine(json: string): void;
  snapshot(): Promise<UsageSnapshot>;
}

function pad(n: number): string {
  return String(n).padStart(2, '0');
}

function dayOf(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function monthOf(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}`;
}

function count(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : null;
}

function addTo(
  totals: SourceUsageTotals,
  tokens: number | null,
  cost: number | null,
): SourceUsageTotals {
  return {
    tokens: totals.tokens + (tokens ?? 0),
    answers: totals.answers + 1,
    cost: cost === null ? totals.cost : (totals.cost ?? 0) + cost,
  };
}

function rolled(source: StoredSource, now: Date): StoredSource {
  const day = dayOf(now);
  const monthKey = monthOf(now);
  return {
    ...source,
    day,
    monthKey,
    today: source.day === day ? source.today : EMPTY_TOTALS,
    month: source.monthKey === monthKey ? source.month : EMPTY_TOTALS,
  };
}

function isStoredSource(value: unknown): value is StoredSource {
  const s = value as Record<string, unknown> | null;
  const totals = (t: unknown): boolean => {
    const r = t as Record<string, unknown> | null;
    return (
      typeof r === 'object' &&
      r !== null &&
      typeof r['tokens'] === 'number' &&
      typeof r['answers'] === 'number' &&
      (r['cost'] === null || typeof r['cost'] === 'number')
    );
  };
  return (
    typeof s === 'object' &&
    s !== null &&
    typeof s['day'] === 'string' &&
    typeof s['monthKey'] === 'string' &&
    totals(s['today']) &&
    totals(s['month']) &&
    typeof s['lastReported'] === 'boolean' &&
    (s['lastModel'] === null || typeof s['lastModel'] === 'string') &&
    (s['limitAt'] === null || typeof s['limitAt'] === 'string')
  );
}

/** Keeps per AI source what Scout itself used today and this month, from the usage events the worker prints. */
export function createUsageStore(deps: UsageStoreDeps): UsageStore {
  let sources: Record<string, StoredSource> | null = null;
  let loading: Promise<Record<string, StoredSource>> | null = null;
  let writing: Promise<void> = Promise.resolve();
  let pending: Promise<void> = Promise.resolve();

  async function load(): Promise<Record<string, StoredSource>> {
    let raw: string;
    try {
      raw = await fs.readFile(deps.file, 'utf8');
    } catch {
      return {};
    }
    try {
      const parsed = JSON.parse(raw) as Partial<UsageFile> | null;
      const entries = Object.entries(parsed?.sources ?? {});
      if (parsed?.v !== 1 || !entries.every(([, value]) => isStoredSource(value))) {
        throw new Error('unexpected shape');
      }
      return Object.fromEntries(entries);
    } catch {
      deps.logger.warn('usage: the usage file could not be read; counting starts empty');
      return {};
    }
  }

  function ensureLoaded(): Promise<Record<string, StoredSource>> {
    if (sources !== null) return Promise.resolve(sources);
    loading ??= load().then((loaded) => (sources = loaded));
    return loading;
  }

  function current(all: Record<string, StoredSource>): UsageSnapshot {
    const now = deps.now();
    return Object.fromEntries(
      Object.entries(all).map(([name, source]) => {
        const { day: _day, monthKey: _month, ...usage } = rolled(source, now);
        return [name, usage];
      }),
    );
  }

  function save(all: Record<string, StoredSource>): void {
    const body = JSON.stringify({ v: 1, sources: all } satisfies UsageFile);
    const tmp = `${deps.file}.tmp`;
    writing = writing
      .then(() => fs.writeFile(tmp, body, 'utf8'))
      .then(() => fs.rename(tmp, deps.file))
      .catch((err: unknown) => {
        deps.logger.warn('usage: the usage file could not be written', {
          error: err instanceof Error ? err.name : typeof err,
        });
      });
  }

  async function apply(event: Record<string, unknown>): Promise<void> {
    const name = event['source'];
    const kind = event['event'];
    if (typeof name !== 'string' || name.length === 0) return;
    if (kind !== 'usage' && kind !== 'usage-limit') return;
    const all = await ensureLoaded();
    const now = deps.now();
    const before = rolled(
      all[name] ?? {
        day: dayOf(now),
        monthKey: monthOf(now),
        today: EMPTY_TOTALS,
        month: EMPTY_TOTALS,
        lastModel: null,
        lastReported: true,
        limitAt: null,
      },
      now,
    );
    if (kind === 'usage-limit') {
      all[name] = { ...before, limitAt: now.toISOString() };
    } else {
      const prompt = count(event['promptTokens']);
      const completion = count(event['completionTokens']);
      const total =
        count(event['totalTokens']) ??
        (prompt === null && completion === null ? null : (prompt ?? 0) + (completion ?? 0));
      const cost = count(event['cost']);
      const model = event['model'];
      all[name] = {
        ...before,
        today: addTo(before.today, total, cost),
        month: addTo(before.month, total, cost),
        lastModel: typeof model === 'string' ? model : before.lastModel,
        lastReported: total !== null,
        limitAt: null,
      };
    }
    save(all);
    deps.broadcast(current(all));
  }

  return {
    handleWorkerLine(json: string): void {
      let parsed: unknown;
      try {
        parsed = JSON.parse(json);
      } catch {
        return;
      }
      if (typeof parsed !== 'object' || parsed === null) return;
      pending = pending.then(() => apply(parsed as Record<string, unknown>));
    },
    async snapshot(): Promise<UsageSnapshot> {
      await pending;
      await writing;
      return current(await ensureLoaded());
    },
  };
}

export interface OpenRouterKeyReaderDeps {
  readonly fetch: typeof fetch;
  /** The connected OpenRouter key, or null while none is stored. */
  readonly apiKey: () => Promise<string | null>;
  readonly now: () => Date;
  readonly logger: Logger;
  readonly timeoutMs?: number;
  readonly intervalMs?: number;
}

export interface OpenRouterKeyReader {
  /** Reads the key's balance at most once per interval; between reads, and after a failure, it returns the last value with its time. */
  read(): Promise<OpenRouterKeyState | null>;
}

function amount(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function keyInfoOf(body: unknown): OpenRouterKeyInfo | null {
  const data = (body as { data?: Record<string, unknown> } | null)?.data;
  if (typeof data !== 'object' || data === null) return null;
  const reset = data['limit_reset'];
  return {
    limit: amount(data['limit']),
    limitRemaining: amount(data['limit_remaining']),
    limitReset: typeof reset === 'string' && reset.length > 0 ? reset : null,
    usageDaily: amount(data['usage_daily']),
    usageMonthly: amount(data['usage_monthly']),
  };
}

interface ThrottledRead<S> {
  read(): Promise<S | null>;
  /** Makes `next`, read with `apiKey`, the last value, as if `read` had just fetched it. */
  accept(apiKey: string, next: S): void;
}

function throttledRead<S>(
  apiKeyOf: () => Promise<string | null>,
  now: () => Date,
  intervalMs: number,
  empty: S,
  fetchWith: (apiKey: string, last: S) => Promise<S>,
): ThrottledRead<S> {
  let state = empty;
  let lastKey: string | null = null;
  let lastAttempt: number | null = null;
  let inFlight: { readonly apiKey: string; readonly read: Promise<S> } | null = null;

  return {
    async read(): Promise<S | null> {
      const apiKey = (await apiKeyOf())?.trim() ?? '';
      if (apiKey.length === 0) return null;
      if (inFlight?.apiKey === apiKey) return inFlight.read;
      if (apiKey !== lastKey) {
        state = empty;
        lastKey = apiKey;
        lastAttempt = null;
      }
      const at = now().getTime();
      if (lastAttempt !== null && at - lastAttempt < intervalMs) return state;
      lastAttempt = at;
      const read: Promise<S> = fetchWith(apiKey, state).then((next) => {
        if (lastKey === apiKey) state = next;
        if (inFlight?.read === read) inFlight = null;
        return next;
      });
      inFlight = { apiKey, read };
      return read;
    },
    accept(apiKey: string, next: S): void {
      state = next;
      lastKey = apiKey;
      lastAttempt = now().getTime();
    },
  };
}

/** The one declared read on the OpenRouter source: GET /api/v1/key with the connected key, which is sent only in that request's header and never logged. */
export function createOpenRouterKeyReader(deps: OpenRouterKeyReaderDeps): OpenRouterKeyReader {
  const timeoutMs = deps.timeoutMs ?? OPENROUTER_TIMEOUT_MS;
  const intervalMs = deps.intervalMs ?? OPENROUTER_READ_INTERVAL_MS;

  async function fetchKey(apiKey: string, state: OpenRouterKeyState): Promise<OpenRouterKeyState> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const response = await deps.fetch(OPENROUTER_KEY_URL, {
        method: 'GET',
        headers: { Authorization: `Bearer ${apiKey}` },
        signal: controller.signal,
      });
      deps.logger.info('usage: OpenRouter key read answered', { status: response.status });
      if (response.status === 401 || response.status === 403) {
        return { ...state, error: 'invalid-key' };
      }
      const info = response.ok ? keyInfoOf(await response.json()) : null;
      if (info === null) return { ...state, error: 'unexpected' };
      return { info, readAt: deps.now().toISOString(), error: null };
    } catch (err: unknown) {
      deps.logger.warn('usage: OpenRouter key read could not reach OpenRouter', {
        error: err instanceof Error ? err.name : typeof err,
      });
      return { ...state, error: 'unreachable' };
    } finally {
      clearTimeout(timer);
    }
  }

  const reader = throttledRead<OpenRouterKeyState>(
    deps.apiKey,
    deps.now,
    intervalMs,
    { info: null, readAt: null, error: null },
    fetchKey,
  );
  return { read: () => reader.read() };
}

/** The main-only vault key that holds each provider's optional Admin key; never sent to the renderer or the worker. */
export const ADMIN_KEY_VAULT_KEYS: Readonly<Record<AdminKeyProvider, MainOnlyVaultKey>> =
  Object.freeze({
    OpenAI: 'llm.openai.admin-key',
    Anthropic: 'llm.anthropic.admin-key',
  });

const ANTHROPIC_VERSION = '2026-10-21';

function monthStart(now: Date): Date {
  return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
}

function costRequest(
  provider: AdminKeyProvider,
  adminKey: string,
  now: Date,
): { url: string; headers: Record<string, string> } {
  const start = monthStart(now);
  switch (provider) {
    case 'OpenAI':
      return {
        url: `https://api.openai.com/v1/organization/costs?start_time=${start.getTime() / 1000}&bucket_width=1d&limit=31`,
        headers: { Authorization: `Bearer ${adminKey}` },
      };
    case 'Anthropic':
      return {
        url: `https://api.anthropic.com/v1/organizations/cost_report?starting_at=${encodeURIComponent(start.toISOString().slice(0, 19) + 'Z')}&bucket_width=1d&limit=31`,
        headers: { 'x-api-key': adminKey, 'anthropic-version': ANTHROPIC_VERSION },
      };
  }
}

function spendOf(
  provider: AdminKeyProvider,
  body: unknown,
): { amount: number; currency: string | null } | null {
  const buckets = (body as { data?: unknown } | null)?.data;
  if (!Array.isArray(buckets)) return null;
  let total = 0;
  let currency: string | null = null;
  for (const bucket of buckets as ReadonlyArray<{ results?: unknown } | null>) {
    const results = bucket?.results;
    if (!Array.isArray(results)) return null;
    for (const result of results as ReadonlyArray<Record<string, unknown> | null>) {
      const amount =
        provider === 'OpenAI'
          ? (result?.['amount'] as Record<string, unknown> | undefined)
          : result;
      if (amount === undefined || amount === null) continue;
      const value =
        provider === 'OpenAI' ? amount['value'] : Number(amount['amount'] ?? Number.NaN) / 100;
      const code = amount['currency'];
      if (typeof value !== 'number' || !Number.isFinite(value)) return null;
      if (typeof code === 'string') {
        if (currency !== null && currency.toLowerCase() !== code.toLowerCase()) return null;
        currency = code;
      }
      total += value;
    }
  }
  return { amount: total, currency };
}

export interface OrgSpendReaderDeps {
  readonly provider: AdminKeyProvider;
  readonly fetch: typeof fetch;
  /** The stored Admin key, or null while none is stored. */
  readonly adminKey: () => Promise<string | null>;
  readonly now: () => Date;
  readonly logger: Logger;
  readonly timeoutMs?: number;
  readonly intervalMs?: number;
}

export interface OrgSpendReader {
  /** Reads this month's organization spend at most once per interval; between reads, and after a failure, it returns the last value with its time. */
  read(): Promise<OrgSpendState | null>;
  /** Reads with a key that is not stored yet, to check it; a successful read becomes the last value for that key. */
  check(adminKey: string): Promise<OrgSpendState>;
}

/** The declared read of an optional Admin key: this month's organization costs, with the key sent only in that request's header and never logged. */
export function createOrgSpendReader(deps: OrgSpendReaderDeps): OrgSpendReader {
  const timeoutMs = deps.timeoutMs ?? OPENROUTER_TIMEOUT_MS;
  const intervalMs = deps.intervalMs ?? OPENROUTER_READ_INTERVAL_MS;
  const empty: OrgSpendState = { amount: null, currency: null, readAt: null, error: null };

  async function fetchSpend(adminKey: string, state: OrgSpendState): Promise<OrgSpendState> {
    const { url, headers } = costRequest(deps.provider, adminKey, deps.now());
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const response = await deps.fetch(url, {
        method: 'GET',
        headers,
        signal: controller.signal,
      });
      deps.logger.info('usage: organization cost read answered', {
        provider: deps.provider,
        status: response.status,
      });
      if (response.status === 401 || response.status === 403) {
        return { ...state, error: 'invalid-key' };
      }
      const spend = response.ok
        ? spendOf(deps.provider, await response.json().catch(() => null))
        : null;
      if (spend === null) return { ...state, error: 'unexpected' };
      return { ...spend, readAt: deps.now().toISOString(), error: null };
    } catch (err: unknown) {
      deps.logger.warn('usage: organization cost read could not reach the provider', {
        provider: deps.provider,
        error: err instanceof Error ? err.name : typeof err,
      });
      return { ...state, error: 'unreachable' };
    } finally {
      clearTimeout(timer);
    }
  }

  const reader = throttledRead(deps.adminKey, deps.now, intervalMs, empty, fetchSpend);
  return {
    read: () => reader.read(),
    async check(adminKey: string): Promise<OrgSpendState> {
      const next = await fetchSpend(adminKey, empty);
      if (next.error === null) reader.accept(adminKey, next);
      return next;
    },
  };
}

function isAdminKeyProvider(value: unknown): value is AdminKeyProvider {
  return value === 'OpenAI' || value === 'Anthropic';
}

function invalidRequest(): Error {
  return Object.assign(
    new Error(
      'usage/invalid-request: provider must be OpenAI or Anthropic and adminKey a non-empty string',
    ),
    { code: 'usage/invalid-request' },
  );
}

/** Registers the Admin key IPC: this month's organization spend, and storing or removing the key, which the renderer can send but never read back. */
export function registerOrgSpendHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  readers: Readonly<Record<AdminKeyProvider, OrgSpendReader>>,
  vault: Pick<Vault, 'store' | 'delete'>,
): () => void {
  const providerOf = (req: unknown): AdminKeyProvider => {
    const provider = ((req ?? {}) as Record<string, unknown>)['provider'];
    if (!isAdminKeyProvider(provider)) throw invalidRequest();
    return provider;
  };
  ipcMain.handle(CHANNEL.usageOrgSpend, (_event, req: unknown) => readers[providerOf(req)].read());
  ipcMain.handle(
    CHANNEL.usageAdminKeySave,
    async (_event, req: unknown): Promise<ProviderKeyValidateResponse> => {
      const provider = providerOf(req);
      const adminKey = ((req ?? {}) as Record<string, unknown>)['adminKey'];
      if (typeof adminKey !== 'string' || adminKey.trim().length === 0) throw invalidRequest();
      const checked = await readers[provider].check(adminKey.trim());
      if (checked.error !== null) return { ok: false, reason: checked.error };
      await vault.store(ADMIN_KEY_VAULT_KEYS[provider], adminKey.trim());
      return { ok: true };
    },
  );
  ipcMain.handle(CHANNEL.usageAdminKeyRemove, async (_event, req: unknown): Promise<void> => {
    await vault.delete(ADMIN_KEY_VAULT_KEYS[providerOf(req)]);
  });
  return () => {
    ipcMain.removeHandler(CHANNEL.usageOrgSpend);
    ipcMain.removeHandler(CHANNEL.usageAdminKeySave);
    ipcMain.removeHandler(CHANNEL.usageAdminKeyRemove);
  };
}

/** Registers the usage IPC: the totals Scout counted, and the OpenRouter balance when an OpenRouter key is stored. */
export function registerUsageHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  store: UsageStore,
  openRouter: OpenRouterKeyReader,
): () => void {
  ipcMain.handle(CHANNEL.usageGet, () => store.snapshot());
  ipcMain.handle(CHANNEL.usageOpenRouterKey, () => openRouter.read());
  return () => {
    ipcMain.removeHandler(CHANNEL.usageGet);
    ipcMain.removeHandler(CHANNEL.usageOpenRouterKey);
  };
}
