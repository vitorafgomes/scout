import { describe, it, expect, beforeEach } from 'bun:test';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { registerLlmHandlers } from '../src/ipc/llm.js';
import { CHANNEL, type OllamaModelInfo, type VaultKey } from '@scout/shared';
import { createLogger } from '../src/util/logger.js';

const logger = createLogger('test', { sink: () => {} });

const noVault = (): Promise<string | null> => Promise.resolve(null);

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => Promise<unknown>;

interface FakeIpcMain extends Pick<IpcMain, 'handle' | 'removeHandler'> {
  handlers: Map<string, IpcHandler>;
}

function makeFakeIpcMain(): FakeIpcMain {
  const handlers: Map<string, IpcHandler> = new Map();
  return {
    handlers,
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
}

function invoke(ipc: FakeIpcMain, channel: string, req?: unknown): Promise<unknown> {
  const fn = ipc.handlers.get(channel);
  if (!fn) throw new Error(`No handler for ${channel}`);
  return fn(null as unknown as IpcMainInvokeEvent, req);
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('registerLlmHandlers — listOllamaModels', () => {
  let ipc: FakeIpcMain;
  let unregister: () => void;

  beforeEach(() => {
    ipc = makeFakeIpcMain();
  });

  it('registers the listOllamaModels handler', () => {
    unregister = registerLlmHandlers(
      ipc,
      (() => Promise.resolve(jsonResponse({ models: [] }))) as unknown as typeof fetch,
      logger,
      noVault,
    );
    expect(ipc.handlers.has(CHANNEL.llmListOllamaModels)).toBe(true);
    unregister();
    expect(ipc.handlers.has(CHANNEL.llmListOllamaModels)).toBe(false);
  });

  it('returns mapped models on a happy-path response', async () => {
    const observedUrls: string[] = [];
    const fakeFetch = (async (url: unknown) => {
      observedUrls.push(String(url));
      return jsonResponse({
        models: [
          { name: 'llama3.1', modified_at: '2026-01-02T00:00:00Z', size: 4_700_000_000 },
          { name: 'qwen2.5', modified_at: '2026-01-03T00:00:00Z', size: 8_300_000_000 },
        ],
      });
    }) as unknown as typeof fetch;

    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    const result = (await invoke(ipc, CHANNEL.llmListOllamaModels, {
      endpoint: 'http://localhost:11434',
    })) as { models: readonly OllamaModelInfo[] };

    expect(observedUrls[0]).toBe('http://localhost:11434/api/tags');
    expect(result.models).toHaveLength(2);
    expect(result.models[0]).toEqual({
      name: 'llama3.1',
      modifiedAt: '2026-01-02T00:00:00Z',
      sizeBytes: 4_700_000_000,
      capabilities: null,
    });
    expect(result.models[1]?.name).toBe('qwen2.5');
  });

  it('strips a trailing slash before joining /api/tags', async () => {
    let observedUrl = '';
    const fakeFetch = (async (url: unknown) => {
      observedUrl = String(url);
      return jsonResponse({ models: [] });
    }) as unknown as typeof fetch;

    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    await invoke(ipc, CHANNEL.llmListOllamaModels, {
      endpoint: 'http://localhost:11434/',
    });

    expect(observedUrl).toBe('http://localhost:11434/api/tags');
  });

  it('rejects with OllamaUnreachableError on HTTP 500 without retry', async () => {
    let calls = 0;
    const fakeFetch = (async () => {
      calls += 1;
      return new Response('boom', { status: 500 });
    }) as unknown as typeof fetch;

    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    let caught: unknown;
    try {
      await invoke(ipc, CHANNEL.llmListOllamaModels, {
        endpoint: 'http://localhost:11434',
      });
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(Error);
    expect((caught as Error & { code?: string }).code).toBe('llm/ollama-unreachable');
    expect(calls).toBe(1);
  });

  it('rejects with OllamaUnreachableError on HTTP 404', async () => {
    const fakeFetch = (async () =>
      new Response('nope', { status: 404 })) as unknown as typeof fetch;
    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    let caught: unknown;
    try {
      await invoke(ipc, CHANNEL.llmListOllamaModels, {
        endpoint: 'http://localhost:11434',
      });
    } catch (e) {
      caught = e;
    }
    expect((caught as Error & { code?: string }).code).toBe('llm/ollama-unreachable');
  });

  it('rejects with OllamaUnreachableError when fetch throws (network down)', async () => {
    const fakeFetch = (async () => {
      throw new Error('ECONNREFUSED');
    }) as unknown as typeof fetch;
    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    let caught: unknown;
    try {
      await invoke(ipc, CHANNEL.llmListOllamaModels, {
        endpoint: 'http://localhost:11434',
      });
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(Error);
    expect((caught as Error & { code?: string }).code).toBe('llm/ollama-unreachable');
  });

  it('rejects with OllamaUnreachableError when the response is not valid JSON', async () => {
    const fakeFetch = (async () =>
      new Response('<html>not ollama</html>', {
        status: 200,
        headers: { 'Content-Type': 'text/html' },
      })) as unknown as typeof fetch;
    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    let caught: unknown;
    try {
      await invoke(ipc, CHANNEL.llmListOllamaModels, {
        endpoint: 'http://localhost:11434',
      });
    } catch (e) {
      caught = e;
    }
    expect((caught as Error & { code?: string }).code).toBe('llm/ollama-unreachable');
  });

  it('rejects when JSON has wrong shape (missing models array)', async () => {
    const fakeFetch = (async () => jsonResponse({ wrong: 'shape' })) as unknown as typeof fetch;
    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    let caught: unknown;
    try {
      await invoke(ipc, CHANNEL.llmListOllamaModels, {
        endpoint: 'http://localhost:11434',
      });
    } catch (e) {
      caught = e;
    }
    expect((caught as Error & { code?: string }).code).toBe('llm/ollama-unreachable');
  });

  it('does NOT call fetch for a javascript: scheme endpoint', async () => {
    let calls = 0;
    const fakeFetch = (async () => {
      calls += 1;
      return jsonResponse({ models: [] });
    }) as unknown as typeof fetch;

    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    let caught: unknown;
    try {
      await invoke(ipc, CHANNEL.llmListOllamaModels, {
        endpoint: 'javascript:alert(1)',
      });
    } catch (e) {
      caught = e;
    }
    expect((caught as Error & { code?: string }).code).toBe('llm/ollama-unreachable');
    expect(calls).toBe(0);
  });

  it('does NOT call fetch for a file: scheme endpoint', async () => {
    let calls = 0;
    const fakeFetch = (async () => {
      calls += 1;
      return jsonResponse({ models: [] });
    }) as unknown as typeof fetch;

    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    let caught: unknown;
    try {
      await invoke(ipc, CHANNEL.llmListOllamaModels, {
        endpoint: 'file:///etc/passwd',
      });
    } catch (e) {
      caught = e;
    }
    expect((caught as Error & { code?: string }).code).toBe('llm/ollama-unreachable');
    expect(calls).toBe(0);
  });

  it('does NOT call fetch for an empty endpoint', async () => {
    let calls = 0;
    const fakeFetch = (async () => {
      calls += 1;
      return jsonResponse({ models: [] });
    }) as unknown as typeof fetch;

    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    let caught: unknown;
    try {
      await invoke(ipc, CHANNEL.llmListOllamaModels, { endpoint: '' });
    } catch (e) {
      caught = e;
    }
    expect((caught as Error & { code?: string }).code).toBe('llm/ollama-unreachable');
    expect(calls).toBe(0);
  });

  it('rejects with OllamaUnreachableError on AbortError (simulated timeout)', async () => {
    const fakeFetch = (async (_url: unknown, init?: { signal?: AbortSignal }) => {
      // Simulate the AbortError that fetch raises when our internal AbortController fires.
      const sig = init?.signal;
      if (sig?.aborted === true) {
        throw new DOMException('aborted', 'AbortError');
      }
      throw new DOMException('aborted', 'AbortError');
    }) as unknown as typeof fetch;

    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    let caught: unknown;
    try {
      await invoke(ipc, CHANNEL.llmListOllamaModels, {
        endpoint: 'http://localhost:11434',
      });
    } catch (e) {
      caught = e;
    }
    expect((caught as Error & { code?: string }).code).toBe('llm/ollama-unreachable');
  });

  it('accepts https endpoints', async () => {
    let observedUrl = '';
    const fakeFetch = (async (url: unknown) => {
      observedUrl = String(url);
      return jsonResponse({ models: [] });
    }) as unknown as typeof fetch;

    registerLlmHandlers(ipc, fakeFetch, logger, noVault);

    await invoke(ipc, CHANNEL.llmListOllamaModels, {
      endpoint: 'https://ollama.example.com:11434',
    });

    expect(observedUrl).toBe('https://ollama.example.com:11434/api/tags');
  });
});

interface ShowCall {
  readonly url: string;
  readonly body: unknown;
}

/** A local Ollama that lists `rows` on /api/tags and answers /api/show per model as `show` says. */
function listingOllama(
  rows: readonly Record<string, unknown>[],
  show: (model: string) => Promise<Response>,
): { fetch: typeof fetch; shown: ShowCall[] } {
  const shown: ShowCall[] = [];
  const fetchFn = (async (url: unknown, init?: RequestInit) => {
    if (String(url).endsWith('/api/tags')) return jsonResponse({ models: rows });
    const body = JSON.parse(String(init?.body)) as { model: string };
    shown.push({ url: String(url), body });
    return show(body.model);
  }) as unknown as typeof fetch;
  return { fetch: fetchFn, shown };
}

function tagsRow(name: string, digest: string, details?: Record<string, unknown>) {
  return { name, modified_at: '2026-11-01T00:00:00Z', size: 2_019_393_189, digest, details };
}

async function listModels(ipc: FakeIpcMain): Promise<Record<string, unknown>[]> {
  const { models } = (await invoke(ipc, CHANNEL.llmListOllamaModels, {
    endpoint: 'http://localhost:11434',
  })) as { models: Record<string, unknown>[] };
  return models;
}

describe('registerLlmHandlers — listOllamaModels reads each model’s details and capabilities', () => {
  let ipc: FakeIpcMain;

  beforeEach(() => {
    ipc = makeFakeIpcMain();
  });

  it('each listed model carries the family, size in parameters and quantization of /api/tags and the capabilities of one /api/show', async () => {
    const ollama = listingOllama(
      [
        tagsRow('sample-chat:3b', 'digest-a', {
          family: 'llama',
          parameter_size: '3.2B',
          quantization_level: 'Q4_K_M',
        }),
        tagsRow('sample-embed:latest', 'digest-b', { family: 'bert' }),
      ],
      async (model) =>
        jsonResponse({
          capabilities: model === 'sample-chat:3b' ? ['completion', 'tools'] : ['embedding'],
        }),
    );
    registerLlmHandlers(ipc, ollama.fetch, logger, noVault);

    const models = await listModels(ipc);

    expect({ models, shown: ollama.shown }).toEqual({
      models: [
        {
          name: 'sample-chat:3b',
          modifiedAt: '2026-11-01T00:00:00Z',
          sizeBytes: 2_019_393_189,
          family: 'llama',
          parameterSize: '3.2B',
          quantization: 'Q4_K_M',
          capabilities: ['completion', 'tools'],
        },
        {
          name: 'sample-embed:latest',
          modifiedAt: '2026-11-01T00:00:00Z',
          sizeBytes: 2_019_393_189,
          family: 'bert',
          capabilities: ['embedding'],
        },
      ],
      shown: [
        { url: 'http://localhost:11434/api/show', body: { model: 'sample-chat:3b' } },
        { url: 'http://localhost:11434/api/show', body: { model: 'sample-embed:latest' } },
      ],
    });
  });

  it.each([
    [
      'times out',
      () => Promise.reject(new DOMException('The operation timed out.', 'TimeoutError')),
    ],
    ['answers HTTP 500', () => Promise.resolve(new Response('boom', { status: 500 }))],
    ['resets the connection', () => Promise.reject(new TypeError('fetch failed: ECONNRESET'))],
    ['answers a body that is not JSON', () => Promise.resolve(new Response('<html>no</html>'))],
    ['answers with no capabilities field', () => Promise.resolve(jsonResponse({ details: {} }))],
  ])(
    'a show call that %s leaves that model’s capabilities unknown and the list still answers',
    async (_case, answer) => {
      const ollama = listingOllama(
        [tagsRow('sample-chat:3b', 'digest-a'), tagsRow('sample-other:1b', 'digest-c')],
        (model) =>
          model === 'sample-chat:3b'
            ? answer()
            : Promise.resolve(jsonResponse({ capabilities: ['completion'] })),
      );
      registerLlmHandlers(ipc, ollama.fetch, logger, noVault);

      const models = await listModels(ipc);

      expect(models.map((m) => [m['name'], m['capabilities']])).toEqual([
        ['sample-chat:3b', null],
        ['sample-other:1b', ['completion']],
      ]);
    },
  );

  it('each show call is bounded by its own 2 s timeout', async () => {
    const signals: (AbortSignal | undefined)[] = [];
    const ollama = listingOllama([tagsRow('sample-chat:3b', 'digest-a')], async () =>
      jsonResponse({ capabilities: ['completion'] }),
    );
    const timed = ((url: unknown, init?: RequestInit) => {
      if (String(url).endsWith('/api/show')) signals.push(init?.signal ?? undefined);
      return ollama.fetch(url as string, init);
    }) as unknown as typeof fetch;
    registerLlmHandlers(ipc, timed, logger, noVault);

    await listModels(ipc);
    const signal = signals[0];
    await new Promise((resolve) => setTimeout(resolve, 2_100));

    expect({
      calls: signals.length,
      abortedAfter2s: signal?.aborted,
      reason: signal?.reason?.name,
    }).toEqual({
      calls: 1,
      abortedAfter2s: true,
      reason: 'TimeoutError',
    });
  });

  it('a model’s capabilities are read once per digest while they are known, and a failed read is asked again only after a minute', async () => {
    let fail = true;
    let now = 1_000_000;
    const ollama = listingOllama(
      [tagsRow('sample-chat:3b', 'digest-a'), tagsRow('sample-flaky:1b', 'digest-f')],
      async (model) => {
        if (model === 'sample-flaky:1b' && fail) {
          return jsonResponse({ error: "model 'sample-flaky:1b' not found" }, 404);
        }
        return jsonResponse({ capabilities: ['completion'] });
      },
    );
    registerLlmHandlers(ipc, ollama.fetch, logger, noVault, () => now);

    const first = await listModels(ipc);
    fail = false;
    now += 61_000;
    const second = await listModels(ipc);
    const third = await listModels(ipc);

    expect({
      shown: ollama.shown.map((c) => (c.body as { model: string }).model),
      first: first.map((m) => m['capabilities']),
      second: second.map((m) => m['capabilities']),
      third: third.map((m) => m['capabilities']),
    }).toEqual({
      shown: ['sample-chat:3b', 'sample-flaky:1b', 'sample-flaky:1b'],
      first: [['completion'], null],
      second: [['completion'], ['completion']],
      third: [['completion'], ['completion']],
    });
  });

  it('a show that failed is not asked again for a minute, and is asked again after it', async () => {
    const t0 = 1_000_000;
    let now = t0;
    const lines: string[] = [];
    const warnings = createLogger('test', { sink: (line) => lines.push(line) });
    const ollama = listingOllama([tagsRow('sample-flaky:1b', 'digest-f')], async () =>
      Promise.reject(new TypeError('fetch failed: ECONNRESET')),
    );
    registerLlmHandlers(ipc, ollama.fetch, warnings, noVault, () => now);

    const atT0 = await listModels(ipc);
    const shownAtT0 = ollama.shown.length;
    now = t0 + 4_000;
    const at4s = await listModels(ipc);
    const shownAt4s = ollama.shown.length;
    now = t0 + 61_000;
    await listModels(ipc);

    expect({
      shown: ollama.shown.map((c) => [c.url, (c.body as { model: string }).model]),
      shownAtT0,
      shownAt4s,
      capabilities: [atT0[0]?.['capabilities'], at4s[0]?.['capabilities']],
      warned: lines
        .map((line) => JSON.parse(line) as { msg: string; ctx?: { model?: string } })
        .filter((entry) => entry.msg === 'llm: model details read failed')
        .map((entry) => entry.ctx?.model),
    }).toEqual({
      shown: [
        ['http://localhost:11434/api/show', 'sample-flaky:1b'],
        ['http://localhost:11434/api/show', 'sample-flaky:1b'],
      ],
      shownAtT0: 1,
      shownAt4s: 1,
      capabilities: [null, null],
      warned: ['sample-flaky:1b', 'sample-flaky:1b'],
    });
  });
});

interface SentRequest {
  readonly url: string;
  readonly method: string;
  readonly body: unknown;
}

interface FakeOllama {
  readonly fetch: typeof fetch;
  readonly sent: SentRequest[];
}

function fakeOllama(loaded: readonly string[], refuses: readonly string[] = []): FakeOllama {
  const sent: SentRequest[] = [];
  const fetchFn = (async (url: unknown, init?: RequestInit) => {
    const body = typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined;
    sent.push({ url: String(url), method: init?.method ?? 'GET', body });
    if (String(url).endsWith('/api/ps')) {
      return jsonResponse({ models: loaded.map((name) => ({ name, model: name, size: 1 })) });
    }
    const model = (body as { model: string }).model;
    return refuses.includes(model)
      ? new Response('{"error":"refused"}', { status: 500 })
      : jsonResponse({ model, response: '', done: true, done_reason: 'unload' });
  }) as unknown as typeof fetch;
  return { fetch: fetchFn, sent };
}

function vaultOf(
  values: Partial<Record<VaultKey, string>>,
): (key: VaultKey) => Promise<string | null> {
  return (key) => Promise.resolve(values[key] ?? null);
}

function unloadRequests(sent: readonly SentRequest[]): SentRequest[] {
  return sent.filter((r) => r.url.endsWith('/api/generate'));
}

describe('registerLlmHandlers — unloadOllamaModels', () => {
  let ipc: FakeIpcMain;

  beforeEach(() => {
    ipc = makeFakeIpcMain();
  });

  const rows: ReadonlyArray<{
    readonly name: string;
    readonly vault: Partial<Record<VaultKey, string>>;
    readonly loaded: readonly string[];
    readonly request?: unknown;
    readonly sent: readonly string[];
  }> = [
    {
      name: "Scout's chat model loaded: one unload for it",
      vault: { 'llm.model': 'fixture-chat:7b' },
      loaded: ['fixture-chat:7b'],
      sent: ['fixture-chat:7b'],
    },
    {
      name: "Scout's model not loaded: no unload",
      vault: { 'llm.model': 'fixture-chat:7b' },
      loaded: [],
      sent: [],
    },
    {
      name: "two of Scout's models loaded: two unloads",
      vault: { 'llm.model': 'fixture-chat:7b' },
      loaded: ['fixture-chat:7b', 'nomic-embed-text:latest'],
      sent: ['fixture-chat:7b', 'nomic-embed-text:latest'],
    },
    {
      name: 'the skill-matching model loaded alone: one unload for it',
      vault: {},
      loaded: ['nomic-embed-text:latest'],
      sent: ['nomic-embed-text:latest'],
    },
    {
      name: 'the translation model on Ollama loaded: one unload for it',
      vault: {
        'llm.translation-model': 'fixture-translate:3b',
        'llm.translation-provider': 'Ollama',
      },
      loaded: ['fixture-translate:3b'],
      sent: ['fixture-translate:3b'],
    },
    {
      name: "a translation model of another provider is not Scout's Ollama model: no unload",
      vault: {
        'llm.translation-model': 'fixture-translate:3b',
        'llm.translation-provider': 'OpenAI',
      },
      loaded: ['fixture-translate:3b'],
      sent: [],
    },
    {
      name: 'a bare name matches its :latest tag',
      vault: { 'llm.model': 'fixture-chat' },
      loaded: ['fixture-chat:latest'],
      sent: ['fixture-chat:latest'],
    },
    {
      name: "another tag of the same model is not Scout's: no unload",
      vault: { 'llm.model': 'fixture-chat:7b' },
      loaded: ['fixture-chat:latest'],
      sent: [],
    },
    {
      name: "a request that names another model still unloads only Scout's",
      vault: { 'llm.model': 'fixture-chat:7b' },
      loaded: ['fixture-chat:7b', 'other-program-model:latest'],
      request: { models: ['other-program-model:latest'], endpoint: 'http://192.0.2.1:11434' },
      sent: ['fixture-chat:7b'],
    },
  ];

  it.each(rows)(
    "unload sends keep_alive 0 for each of Scout's models that Ollama has loaded: $name",
    async (row) => {
      const ollama = fakeOllama(row.loaded);
      registerLlmHandlers(ipc, ollama.fetch, logger, vaultOf(row.vault));

      const result = await invoke(ipc, CHANNEL.llmUnloadOllamaModels, row.request);

      expect(unloadRequests(ollama.sent)).toEqual(
        row.sent.map((model) => ({
          url: 'http://localhost:11434/api/generate',
          method: 'POST',
          body: { model, keep_alive: 0 },
        })),
      );
      expect(result).toEqual({ unloaded: row.sent, refused: [] });
    },
  );

  it('a model another program loaded is left loaded', async () => {
    const ollama = fakeOllama(['other-program-model:latest', 'fixture-chat:7b']);
    registerLlmHandlers(ipc, ollama.fetch, logger, vaultOf({ 'llm.model': 'fixture-chat:7b' }));

    const result = await invoke(ipc, CHANNEL.llmUnloadOllamaModels);

    expect(unloadRequests(ollama.sent).map((r) => r.body)).toEqual([
      { model: 'fixture-chat:7b', keep_alive: 0 },
    ]);
    expect(result).toEqual({ unloaded: ['fixture-chat:7b'], refused: [] });
  });

  it('asks the Ollama at the stored endpoint, and at localhost:11434 when the stored one is not an http URL', async () => {
    const stored = fakeOllama(['fixture-chat:7b']);
    registerLlmHandlers(
      ipc,
      stored.fetch,
      logger,
      vaultOf({ 'llm.model': 'fixture-chat:7b', 'llm.endpoint': 'http://127.0.0.1:11500/' }),
    );
    await invoke(ipc, CHANNEL.llmUnloadOllamaModels);

    const invalid = fakeOllama([]);
    const second = makeFakeIpcMain();
    registerLlmHandlers(second, invalid.fetch, logger, vaultOf({ 'llm.endpoint': 'file:///etc' }));
    await invoke(second, CHANNEL.llmUnloadOllamaModels);

    expect(stored.sent.map((r) => r.url)).toEqual([
      'http://127.0.0.1:11500/api/ps',
      'http://127.0.0.1:11500/api/generate',
    ]);
    expect(invalid.sent.map((r) => r.url)).toEqual(['http://localhost:11434/api/ps']);
  });

  it.each([
    ['the connection is refused', () => Promise.reject(new TypeError('fetch failed'))],
    ['it answers HTTP 500', () => Promise.resolve(new Response('boom', { status: 500 }))],
    [
      'it answers a body that is not its loaded-models list',
      () => Promise.resolve(jsonResponse({ nope: true })),
    ],
  ])(
    'when Ollama does not answer the loaded-models read, unload fails with the typed unreachable error and sends no unload: %s',
    async (_case, answer) => {
      const sent: string[] = [];
      const fetchFn = ((url: unknown) => {
        sent.push(String(url));
        return answer();
      }) as unknown as typeof fetch;
      registerLlmHandlers(ipc, fetchFn, logger, vaultOf({ 'llm.model': 'fixture-chat:7b' }));

      await expect(invoke(ipc, CHANNEL.llmUnloadOllamaModels)).rejects.toMatchObject({
        code: 'llm/ollama-unreachable',
        message: expect.stringContaining('[scout:llm/ollama-unreachable] '),
      });
      expect(sent).toEqual(['http://localhost:11434/api/ps']);
    },
  );

  it('when Ollama refuses one unload with a 500, the other models are still unloaded and the refused one is not reported as freed', async () => {
    const ollama = fakeOllama(['fixture-chat:7b', 'nomic-embed-text:latest'], ['fixture-chat:7b']);
    registerLlmHandlers(ipc, ollama.fetch, logger, vaultOf({ 'llm.model': 'fixture-chat:7b' }));

    const result = await invoke(ipc, CHANNEL.llmUnloadOllamaModels);

    expect(unloadRequests(ollama.sent).map((r) => r.body)).toEqual([
      { model: 'fixture-chat:7b', keep_alive: 0 },
      { model: 'nomic-embed-text:latest', keep_alive: 0 },
    ]);
    expect(result).toEqual({ unloaded: ['nomic-embed-text:latest'], refused: ['fixture-chat:7b'] });
  });

  it('when an unload request itself fails, that model is reported refused, never freed', async () => {
    const ollama = fakeOllama(['fixture-chat:7b']);
    const failingUnload = ((url: unknown, init?: RequestInit) =>
      String(url).endsWith('/api/generate')
        ? Promise.reject(new DOMException('The operation timed out.', 'TimeoutError'))
        : ollama.fetch(url as string, init)) as unknown as typeof fetch;
    registerLlmHandlers(ipc, failingUnload, logger, vaultOf({ 'llm.model': 'fixture-chat:7b' }));

    const result = await invoke(ipc, CHANNEL.llmUnloadOllamaModels);

    expect(result).toEqual({ unloaded: [], refused: ['fixture-chat:7b'] });
  });
});
