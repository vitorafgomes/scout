process.env.TZ = 'Europe/London';

import { describe, it, expect, beforeEach, afterEach } from 'bun:test';
import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import {
  CHANNEL,
  type AdminKeyProvider,
  type OpenRouterKeyState,
  type OrgSpendState,
  type UsageSnapshot,
} from '@scout/shared';
import {
  createOpenRouterKeyReader,
  createOrgSpendReader,
  createUsageStore,
  registerOrgSpendHandlers,
  registerUsageHandlers,
  type OrgSpendReader,
} from '../src/ipc/usage.js';
import { createLogger } from '../src/util/logger.js';

const WORKER_USAGE_LINE =
  '{"id":null,"event":"usage","source":"OpenRouter","model":"openai/gpt-4o-mini","promptTokens":10,"completionTokens":5,"totalTokens":15,"cost":0.0012}';
const WORKER_LIMIT_LINE = '{"id":null,"event":"usage-limit","source":"ChatGptPlan"}';
const KEY = 'sk-or-v1-secret-key-must-never-leak';

function usage(source: string, totalTokens?: number, extra: Record<string, unknown> = {}): string {
  return JSON.stringify({
    id: null,
    event: 'usage',
    source,
    model: 'm',
    ...(totalTokens === undefined ? {} : { totalTokens }),
    ...extra,
  });
}

describe('the usage store', () => {
  let dir: string;
  let file: string;
  let clock: Date;
  let logLines: string[];
  let pushed: UsageSnapshot[];

  function store() {
    return createUsageStore({
      file,
      now: () => clock,
      logger: createLogger('test', { level: 'debug', sink: (line) => logLines.push(line) }),
      broadcast: (snapshot) => pushed.push(snapshot),
    });
  }

  beforeEach(() => {
    dir = mkdtempSync(path.join(os.tmpdir(), 'scout-usage-'));
    file = path.join(dir, 'usage.json');
    clock = new Date(2026, 6, 14, 12, 0);
    logLines = [];
    pushed = [];
  });

  afterEach(() => {
    rmSync(dir, { recursive: true, force: true });
  });

  it('runs this file in London time, where 00:30 local in July is still the day before in UTC', () => {
    expect(new Date(2026, 6, 15, 0, 30).toISOString()).toBe('2026-07-14T23:30:00.000Z');
  });

  it('counts the line the worker prints for an answer into today and this month, with the cost OpenRouter reported', async () => {
    const s = store();

    s.handleWorkerLine(WORKER_USAGE_LINE);

    expect(await s.snapshot()).toEqual({
      OpenRouter: {
        today: { tokens: 15, answers: 1, cost: 0.0012 },
        month: { tokens: 15, answers: 1, cost: 0.0012 },
        lastModel: 'openai/gpt-4o-mini',
        lastReported: true,
        limitAt: null,
      },
    });
  });

  it('adds every answer of the same source, and keeps sources apart', async () => {
    const s = store();

    s.handleWorkerLine(usage('OpenAI', 100));
    s.handleWorkerLine(usage('OpenAI', 50));
    s.handleWorkerLine(usage('Gemini', 7));
    const snapshot = await s.snapshot();

    expect(snapshot['OpenAI']?.today).toEqual({ tokens: 150, answers: 2, cost: null });
    expect(snapshot['Gemini']?.month).toEqual({ tokens: 7, answers: 1, cost: null });
  });

  it('counts twenty answers that arrive together, none lost', async () => {
    const s = store();

    for (let i = 0; i < 20; i += 1) s.handleWorkerLine(usage('OpenAI', 1));

    expect((await s.snapshot())['OpenAI']?.today).toEqual({ tokens: 20, answers: 20, cost: null });
    expect(JSON.parse(readFileSync(file, 'utf8')).sources.OpenAI.month.answers).toBe(20);
  });

  it('starts a new today after local midnight even while UTC is still on the day before', async () => {
    const s = store();
    clock = new Date(2026, 6, 14, 23, 50);
    s.handleWorkerLine(usage('OpenAI', 100));
    await s.snapshot();

    clock = new Date(2026, 6, 15, 0, 30);
    const snapshot = await s.snapshot();

    expect(snapshot['OpenAI']?.today).toEqual({ tokens: 0, answers: 0, cost: null });
    expect(snapshot['OpenAI']?.month).toEqual({ tokens: 100, answers: 1, cost: null });
  });

  it('starts a new month on the first local day of the month', async () => {
    const s = store();
    clock = new Date(2026, 6, 31, 23, 50);
    s.handleWorkerLine(usage('OpenAI', 100));
    await s.snapshot();

    clock = new Date(2026, 7, 1, 0, 30);
    s.handleWorkerLine(usage('OpenAI', 5));
    const snapshot = await s.snapshot();

    expect(snapshot['OpenAI']?.today).toEqual({ tokens: 5, answers: 1, cost: null });
    expect(snapshot['OpenAI']?.month).toEqual({ tokens: 5, answers: 1, cost: null });
  });

  it('keeps the totals in its file across a restart of the app', async () => {
    const first = store();
    first.handleWorkerLine(usage('Mistral', 42));
    await first.snapshot();

    const second = store();

    expect((await second.snapshot())['Mistral']?.month).toEqual({
      tokens: 42,
      answers: 1,
      cost: null,
    });
  });

  it('starts empty from a corrupt file, logs it once, and writes a good file again', async () => {
    writeFileSync(file, '{not json');
    const s = store();

    expect(await s.snapshot()).toEqual({});
    s.handleWorkerLine(usage('OpenAI', 3));
    await s.snapshot();

    expect(logLines.filter((l) => l.includes('the usage file could not be read'))).toHaveLength(1);
    expect(JSON.parse(readFileSync(file, 'utf8')).sources.OpenAI.today.tokens).toBe(3);
  });

  it('starts empty from a file of the wrong shape and logs it once', async () => {
    writeFileSync(file, JSON.stringify({ v: 1, sources: { OpenAI: { today: 'lots' } } }));
    const s = store();

    expect(await s.snapshot()).toEqual({});
    expect(await s.snapshot()).toEqual({});
    expect(logLines.filter((l) => l.includes('the usage file could not be read'))).toHaveLength(1);
  });

  it('keeps counting in memory and logs once when the usage file cannot be written', async () => {
    writeFileSync(path.join(dir, 'not-a-folder'), 'x');
    file = path.join(dir, 'not-a-folder', 'usage.json');
    const s = store();

    s.handleWorkerLine(usage('OpenAI', 5));
    const snapshot = await s.snapshot();

    expect(snapshot['OpenAI']?.today).toEqual({ tokens: 5, answers: 1, cost: null });
    expect(logLines.filter((l) => l.includes('the usage file could not be written'))).toHaveLength(
      1,
    );
    expect(pushed).toHaveLength(1);
  });

  it('counts an answer without usage as an answer with no tokens and says the model does not report it', async () => {
    const s = store();

    s.handleWorkerLine(usage('HuggingFace'));

    const source = (await s.snapshot())['HuggingFace'];
    expect(source?.today).toEqual({ tokens: 0, answers: 1, cost: null });
    expect(source?.lastReported).toBe(false);
  });

  it('keeps the time the plan reached its usage limit until the next answer of the plan clears it', async () => {
    const s = store();
    clock = new Date(2026, 6, 14, 14, 30);

    s.handleWorkerLine(WORKER_LIMIT_LINE);
    const limited = await s.snapshot();
    s.handleWorkerLine(usage('OpenAI', 1));
    const otherSource = await s.snapshot();
    s.handleWorkerLine(usage('ChatGptPlan', 9));
    const cleared = await s.snapshot();

    expect(limited['ChatGptPlan']?.limitAt).toBe(new Date(2026, 6, 14, 14, 30).toISOString());
    expect(limited['ChatGptPlan']?.today.answers).toBe(0);
    expect(otherSource['ChatGptPlan']?.limitAt).toBe(new Date(2026, 6, 14, 14, 30).toISOString());
    expect(cleared['ChatGptPlan']?.limitAt).toBeNull();
  });

  it('pushes the new totals to the windows after each change', async () => {
    const s = store();

    s.handleWorkerLine(usage('OpenAI', 4));
    await s.snapshot();

    expect(pushed).toHaveLength(1);
    expect(pushed[0]?.['OpenAI']?.today.tokens).toBe(4);
  });

  it('ignores the lines of a chat turn and lines that are not JSON, and writes nothing for them', async () => {
    const s = store();

    s.handleWorkerLine('{"id":"t1","event":"complete","text":"hello"}');
    s.handleWorkerLine('{"id":null,"event":"notice","text":"done"}');
    s.handleWorkerLine('not json');

    expect(await s.snapshot()).toEqual({});
    expect(pushed).toHaveLength(0);
    expect(existsSync(file)).toBe(false);
  });
});

interface FetchCall {
  readonly url: string;
  readonly init: RequestInit | undefined;
}

describe('the OpenRouter key read', () => {
  let calls: FetchCall[];
  let logLines: string[];
  let clock: Date;
  let key: string | null;

  const BODY = {
    data: {
      label: 'sk-or-v1-abc...',
      limit: 20,
      limit_remaining: 12.4,
      limit_reset: 'monthly',
      usage: 7.6,
      usage_daily: 0.5,
      usage_weekly: 2,
      usage_monthly: 7.6,
      is_free_tier: false,
    },
  };

  function reader(
    respond: (call: number, init?: RequestInit) => Promise<Response>,
    timeoutMs?: number,
  ) {
    return createOpenRouterKeyReader({
      fetch: (async (url: string, init?: RequestInit) => {
        calls.push({ url, init });
        return respond(calls.length, init);
      }) as unknown as typeof fetch,
      apiKey: async () => key,
      now: () => clock,
      logger: createLogger('test', { level: 'debug', sink: (line) => logLines.push(line) }),
      ...(timeoutMs === undefined ? {} : { timeoutMs }),
    });
  }

  const ok = () => Promise.resolve(new Response(JSON.stringify(BODY), { status: 200 }));

  beforeEach(() => {
    calls = [];
    logLines = [];
    clock = new Date(2026, 6, 14, 12, 0);
    key = KEY;
  });

  it('reads nothing and says not connected while no OpenRouter key is stored', async () => {
    key = null;

    expect(await reader(ok).read()).toBeNull();
    expect(calls).toHaveLength(0);
  });

  it('reads the money left, the limit, its reset and the usage from GET /api/v1/key with the key as a bearer token', async () => {
    const state = await reader(ok).read();

    expect(state).toEqual({
      info: {
        limit: 20,
        limitRemaining: 12.4,
        limitReset: 'monthly',
        usageDaily: 0.5,
        usageMonthly: 7.6,
      },
      readAt: clock.toISOString(),
      error: null,
    });
    expect(calls).toHaveLength(1);
    expect(calls[0]?.url).toBe('https://openrouter.ai/api/v1/key');
    expect(calls[0]?.init?.method).toBe('GET');
    expect(new Headers(calls[0]?.init?.headers).get('authorization')).toBe(`Bearer ${KEY}`);
  });

  it('reads a key with no limit as no limit', async () => {
    const r = reader(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({ data: { limit: null, limit_remaining: null, limit_reset: null } }),
          {
            status: 200,
          },
        ),
      ),
    );

    expect((await r.read())?.info).toEqual({
      limit: null,
      limitRemaining: null,
      limitReset: null,
      usageDaily: null,
      usageMonthly: null,
    });
  });

  it('reads at most once every ten minutes', async () => {
    const r = reader(ok);

    await r.read();
    clock = new Date(clock.getTime() + 9 * 60 * 1000);
    await r.read();
    expect(calls).toHaveLength(1);

    clock = new Date(clock.getTime() + 60 * 1000);
    await r.read();
    expect(calls).toHaveLength(2);
  });

  it('shares one request between two reads that arrive together', async () => {
    const r = reader(ok);

    const [a, b] = await Promise.all([r.read(), r.read()]);

    expect(calls).toHaveLength(1);
    expect(a).toEqual(b);
  });

  it("a read after Change key never answers with the old key's balance, even while the old key's read is still in flight", async () => {
    const NEW_KEY = 'sk-or-v1-new-key';
    const newBody = { data: { ...BODY.data, limit: 50, limit_remaining: 49 } };
    let releaseOld: (response: Response) => void = () => undefined;
    const r = reader((call) =>
      call === 1
        ? new Promise<Response>((resolve) => (releaseOld = resolve))
        : Promise.resolve(new Response(JSON.stringify(newBody), { status: 200 })),
    );

    const oldRead = r.read();
    while (calls.length === 0) await Promise.resolve();
    key = NEW_KEY;
    const newRead = r.read();
    releaseOld(new Response(JSON.stringify(BODY), { status: 200 }));
    await oldRead;
    const state = await newRead;

    expect(state?.info?.limitRemaining).toBe(49);
    expect(new Headers(calls[1]?.init?.headers).get('authorization')).toBe(`Bearer ${NEW_KEY}`);
  });

  it('counts a failed read toward the ten minutes, so opening the screen again does not call OpenRouter again', async () => {
    const r = reader(() => Promise.resolve(new Response('busy', { status: 503 })));

    await r.read();
    clock = new Date(clock.getTime() + 60 * 1000);
    await r.read();

    expect(calls).toHaveLength(1);
  });

  it('keeps the last value and its time when OpenRouter rejects the key with 401', async () => {
    const r = reader((call) =>
      call === 1 ? ok() : Promise.resolve(new Response('{}', { status: 401 })),
    );
    const first = (await r.read()) as OpenRouterKeyState;
    clock = new Date(clock.getTime() + 11 * 60 * 1000);

    const second = await r.read();

    expect(second).toEqual({ info: first.info, readAt: first.readAt, error: 'invalid-key' });
  });

  it('keeps the last value and its time when OpenRouter answers 5xx', async () => {
    const r = reader((call) =>
      call === 1 ? ok() : Promise.resolve(new Response('oops', { status: 502 })),
    );
    const first = (await r.read()) as OpenRouterKeyState;
    clock = new Date(clock.getTime() + 11 * 60 * 1000);

    const second = await r.read();

    expect(second).toEqual({ info: first.info, readAt: first.readAt, error: 'unexpected' });
  });

  it('keeps the last value and its time when the read times out', async () => {
    const never = (_call: number, init?: RequestInit) =>
      new Promise<Response>((_resolve, reject) => {
        init?.signal?.addEventListener('abort', () =>
          reject(new DOMException('aborted', 'AbortError')),
        );
      });
    const r = reader((call, init) => (call === 1 ? ok() : never(call, init)), 10);
    const first = (await r.read()) as OpenRouterKeyState;
    clock = new Date(clock.getTime() + 11 * 60 * 1000);

    const second = await r.read();

    expect(second).toEqual({ info: first.info, readAt: first.readAt, error: 'unreachable' });
  });

  it("reads again at once with a new key after Change key, and drops the old key's balance", async () => {
    const r = reader((call) =>
      call === 1
        ? ok()
        : Promise.resolve(
            new Response('{"data":{"limit":5,"limit_remaining":5}}', { status: 200 }),
          ),
    );
    await r.read();
    key = 'sk-or-v1-the-new-key';
    clock = new Date(clock.getTime() + 60 * 1000);

    const state = await r.read();

    expect(calls).toHaveLength(2);
    expect(new Headers(calls[1]?.init?.headers).get('authorization')).toBe(
      'Bearer sk-or-v1-the-new-key',
    );
    expect(state?.info?.limitRemaining).toBe(5);
  });

  it('never writes the key to the log, whether the read succeeds or fails', async () => {
    const r = reader((call) =>
      call === 1 ? ok() : Promise.reject(new TypeError(`fetch failed for ${KEY}`)),
    );

    await r.read();
    clock = new Date(clock.getTime() + 11 * 60 * 1000);
    await r.read();

    expect(logLines.length).toBeGreaterThan(1);
    expect(logLines.join('\n')).not.toContain(KEY);
  });
});

const ADMIN_KEY = 'sk-admin-organization-key-must-never-leak';

const OPENAI_COSTS = {
  object: 'page',
  data: [
    {
      object: 'bucket',
      start_time: 1783296000,
      end_time: 1783382400,
      results: [{ object: 'organization.costs.result', amount: { value: 10.15, currency: 'usd' } }],
    },
    {
      object: 'bucket',
      start_time: 1783382400,
      end_time: 1783468800,
      results: [{ object: 'organization.costs.result', amount: { value: 2.25, currency: 'usd' } }],
    },
    { object: 'bucket', start_time: 1783468800, end_time: 1783555200, results: [] },
  ],
  has_more: false,
  next_page: null,
};

const ANTHROPIC_COSTS = {
  data: [
    {
      starting_at: '2026-07-01T00:00:00Z',
      ending_at: '2026-07-02T00:00:00Z',
      results: [{ amount: '1015.0', currency: 'USD' }],
    },
    {
      starting_at: '2026-07-02T00:00:00Z',
      ending_at: '2026-07-03T00:00:00Z',
      results: [{ amount: '225', currency: 'USD' }],
    },
    { starting_at: '2026-07-03T00:00:00Z', ending_at: '2026-07-04T00:00:00Z', results: [] },
  ],
  has_more: false,
  next_page: null,
};

const COSTS: Record<AdminKeyProvider, unknown> = {
  OpenAI: OPENAI_COSTS,
  Anthropic: ANTHROPIC_COSTS,
};
const CURRENCY: Record<AdminKeyProvider, string> = { OpenAI: 'usd', Anthropic: 'USD' };

describe('the organization spend read with an Admin key', () => {
  let calls: FetchCall[];
  let logLines: string[];
  let clock: Date;
  let adminKey: string | null;

  function reader(
    provider: AdminKeyProvider,
    respond: (call: number, init?: RequestInit) => Promise<Response>,
    timeoutMs?: number,
  ): OrgSpendReader {
    return createOrgSpendReader({
      provider,
      fetch: (async (url: string, init?: RequestInit) => {
        calls.push({ url, init });
        return respond(calls.length, init);
      }) as unknown as typeof fetch,
      adminKey: async () => adminKey,
      now: () => clock,
      logger: createLogger('test', { level: 'debug', sink: (line) => logLines.push(line) }),
      ...(timeoutMs === undefined ? {} : { timeoutMs }),
    });
  }

  const status = (code: number) => () =>
    Promise.resolve(new Response('{"error":{}}', { status: code }));
  const costs = (provider: AdminKeyProvider) => () =>
    Promise.resolve(new Response(JSON.stringify(COSTS[provider]), { status: 200 }));
  const never = (_call: number, init?: RequestInit) =>
    new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () =>
        reject(new DOMException('aborted', 'AbortError')),
      );
    });

  beforeEach(() => {
    calls = [];
    logLines = [];
    clock = new Date(Date.UTC(2026, 6, 14, 12, 0));
    adminKey = ADMIN_KEY;
  });

  it('OpenAI: reads the month-to-date cost from GET /v1/organization/costs since the first of the month (UTC), daily buckets, with the Admin key as a bearer token', async () => {
    const state = await reader('OpenAI', costs('OpenAI')).read();

    expect(state).toEqual({
      amount: 12.4,
      currency: 'usd',
      readAt: clock.toISOString(),
      error: null,
    });
    expect(calls).toHaveLength(1);
    expect(calls[0]?.url).toBe(
      `https://api.openai.com/v1/organization/costs?start_time=${Date.UTC(2026, 6, 1) / 1000}&bucket_width=1d&limit=31`,
    );
    expect(calls[0]?.init?.method).toBe('GET');
    expect(new Headers(calls[0]?.init?.headers).get('authorization')).toBe(`Bearer ${ADMIN_KEY}`);
  });

  it('Claude API: reads the month-to-date cost from GET /v1/organizations/cost_report, amounts in cents, with the Admin key in x-api-key and the API version', async () => {
    const state = await reader('Anthropic', costs('Anthropic')).read();

    expect(state).toEqual({
      amount: 12.4,
      currency: 'USD',
      readAt: clock.toISOString(),
      error: null,
    });
    expect(calls[0]?.url).toBe(
      'https://api.anthropic.com/v1/organizations/cost_report?starting_at=2026-07-01T00%3A00%3A00Z&bucket_width=1d&limit=31',
    );
    const headers = new Headers(calls[0]?.init?.headers);
    expect(headers.get('x-api-key')).toBe(ADMIN_KEY);
    expect(headers.get('anthropic-version')).toBe('2026-10-21');
    expect(headers.get('authorization')).toBeNull();
  });

  it('reads nothing while no Admin key is stored', async () => {
    adminKey = null;

    expect(await reader('OpenAI', costs('OpenAI')).read()).toBeNull();
    expect(calls).toHaveLength(0);
  });

  it('reads at most once every ten minutes', async () => {
    const r = reader('Anthropic', costs('Anthropic'));

    await r.read();
    clock = new Date(clock.getTime() + 9 * 60 * 1000);
    await r.read();
    expect(calls).toHaveLength(1);

    clock = new Date(clock.getTime() + 60 * 1000);
    await r.read();
    expect(calls).toHaveLength(2);
  });

  const failures: readonly {
    readonly name: string;
    readonly respond: (call: number, init?: RequestInit) => Promise<Response>;
    readonly error: OrgSpendState['error'];
  }[] = [
    { name: 'rejects the key with 401', respond: status(401), error: 'invalid-key' },
    { name: 'refuses the key with 403', respond: status(403), error: 'invalid-key' },
    { name: 'answers 5xx', respond: status(503), error: 'unexpected' },
    { name: 'does not answer before the timeout', respond: never, error: 'unreachable' },
  ];
  for (const provider of ['OpenAI', 'Anthropic'] as const) {
    for (const failure of failures) {
      it(`${provider}: when the provider ${failure.name}, the read keeps the last value with its time, names the failure and never logs the key`, async () => {
        const r = reader(
          provider,
          (call, init) => (call === 1 ? costs(provider)() : failure.respond(call, init)),
          10,
        );
        const first = (await r.read()) as OrgSpendState;
        clock = new Date(clock.getTime() + 11 * 60 * 1000);

        const second = await r.read();

        expect(second).toEqual({
          amount: 12.4,
          currency: CURRENCY[provider],
          readAt: first.readAt,
          error: failure.error,
        });
        expect(logLines.length).toBeGreaterThan(1);
        expect(logLines.join('\n')).not.toContain(ADMIN_KEY);
      });
    }
  }

  it('a body that is not a cost report is unexpected', async () => {
    const r = reader('OpenAI', () =>
      Promise.resolve(new Response('{"object":"list"}', { status: 200 })),
    );

    expect(await r.read()).toEqual({
      amount: null,
      currency: null,
      readAt: null,
      error: 'unexpected',
    });
  });
});

describe('registerOrgSpendHandlers', () => {
  type Handler = (event: IpcMainInvokeEvent, req?: unknown) => Promise<unknown>;
  let handlers: Map<string, Handler>;
  let stored: Map<string, string>;
  let calls: FetchCall[];
  let logLines: string[];
  let respond: () => Promise<Response>;

  const invoke = (channel: string, req?: unknown) =>
    handlers.get(channel)!(null as unknown as IpcMainInvokeEvent, req);

  beforeEach(() => {
    handlers = new Map();
    stored = new Map();
    calls = [];
    logLines = [];
    respond = () => Promise.resolve(new Response(JSON.stringify(OPENAI_COSTS), { status: 200 }));
    const ipc = {
      handle: (channel: string, fn: Handler) => handlers.set(channel, fn),
      removeHandler: (channel: string) => handlers.delete(channel),
    } as unknown as Pick<IpcMain, 'handle' | 'removeHandler'>;
    const logger = createLogger('test', { level: 'debug', sink: (line) => logLines.push(line) });
    const make = (provider: AdminKeyProvider, key: string) =>
      createOrgSpendReader({
        provider,
        fetch: (async (url: string, init?: RequestInit) => {
          calls.push({ url, init });
          return respond();
        }) as unknown as typeof fetch,
        adminKey: async () => stored.get(key) ?? null,
        now: () => new Date(Date.UTC(2026, 6, 14)),
        logger,
      });
    registerOrgSpendHandlers(
      ipc,
      {
        OpenAI: make('OpenAI', 'llm.openai.admin-key'),
        Anthropic: make('Anthropic', 'llm.anthropic.admin-key'),
      },
      {
        store: async (key, value) => void stored.set(key, value),
        delete: async (key) => stored.delete(key),
      },
    );
  });

  it('checks the Admin key with the provider and stores it under the main-only key only when accepted; the spend then shows without a second call', async () => {
    const saved = await invoke(CHANNEL.usageAdminKeySave, {
      provider: 'OpenAI',
      adminKey: ` ${ADMIN_KEY} `,
    });

    expect(saved).toEqual({ ok: true });
    expect(stored.get('llm.openai.admin-key')).toBe(ADMIN_KEY);
    expect(await invoke(CHANNEL.usageOrgSpend, { provider: 'OpenAI' })).toMatchObject({
      amount: 12.4,
      error: null,
    });
    expect(calls).toHaveLength(1);
  });

  it('does not store an Admin key the provider rejects, and says so', async () => {
    respond = () => Promise.resolve(new Response('{}', { status: 401 }));

    const saved = await invoke(CHANNEL.usageAdminKeySave, {
      provider: 'Anthropic',
      adminKey: ADMIN_KEY,
    });

    expect(saved).toEqual({ ok: false, reason: 'invalid-key' });
    expect(stored.size).toBe(0);
    expect(await invoke(CHANNEL.usageOrgSpend, { provider: 'Anthropic' })).toBeNull();
  });

  it("Remove deletes only that provider's Admin key, and the spend reads null again", async () => {
    stored.set('llm.openai.admin-key', ADMIN_KEY);
    stored.set('llm.anthropic.admin-key', 'other');

    await invoke(CHANNEL.usageAdminKeyRemove, { provider: 'OpenAI' });

    expect([...stored.keys()]).toEqual(['llm.anthropic.admin-key']);
    expect(await invoke(CHANNEL.usageOrgSpend, { provider: 'OpenAI' })).toBeNull();
  });

  it('the Admin key never reaches the renderer: no answer and no log line carries it, success or failure', async () => {
    const answers: unknown[] = [];
    answers.push(
      await invoke(CHANNEL.usageAdminKeySave, { provider: 'OpenAI', adminKey: ADMIN_KEY }),
    );
    answers.push(await invoke(CHANNEL.usageOrgSpend, { provider: 'OpenAI' }));
    respond = () => Promise.reject(new TypeError(`fetch failed for ${ADMIN_KEY}`));
    answers.push(
      await invoke(CHANNEL.usageAdminKeySave, { provider: 'Anthropic', adminKey: ADMIN_KEY }),
    );
    let refused: unknown = null;
    try {
      await invoke(CHANNEL.usageAdminKeySave, { provider: 'Gemini', adminKey: ADMIN_KEY });
    } catch (err) {
      refused = err;
    }

    expect(refused).toBeInstanceOf(Error);
    expect((refused as Error).message).not.toContain(ADMIN_KEY);
    expect(JSON.stringify(answers)).not.toContain(ADMIN_KEY);
    expect(logLines.length).toBeGreaterThan(0);
    expect(logLines.join('\n')).not.toContain(ADMIN_KEY);
  });
});

describe('registerUsageHandlers', () => {
  it('answers the usage channel with the totals and the OpenRouter channel with the key read', async () => {
    const handlers = new Map<string, (event: IpcMainInvokeEvent) => Promise<unknown>>();
    const ipc = {
      handle: (channel: string, fn: (event: IpcMainInvokeEvent) => Promise<unknown>) =>
        handlers.set(channel, fn),
      removeHandler: (channel: string) => handlers.delete(channel),
    } as unknown as Pick<IpcMain, 'handle' | 'removeHandler'>;
    const snapshot: UsageSnapshot = {};
    const keyState: OpenRouterKeyState = { info: null, readAt: null, error: 'unreachable' };

    const dispose = registerUsageHandlers(
      ipc,
      { handleWorkerLine: () => undefined, snapshot: async () => snapshot },
      { read: async () => keyState },
    );

    expect(await handlers.get(CHANNEL.usageGet)?.(null as unknown as IpcMainInvokeEvent)).toBe(
      snapshot,
    );
    expect(
      await handlers.get(CHANNEL.usageOpenRouterKey)?.(null as unknown as IpcMainInvokeEvent),
    ).toBe(keyState);
    dispose();
    expect(handlers.size).toBe(0);
  });
});
