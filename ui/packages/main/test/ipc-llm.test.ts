import { describe, it, expect, beforeEach } from 'bun:test';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { registerLlmHandlers } from '../src/ipc/llm.js';
import { CHANNEL } from '@scout/shared';
import { createLogger } from '../src/util/logger.js';

const logger = createLogger('test', { sink: () => {} });

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
    );
    expect(ipc.handlers.has(CHANNEL.llmListOllamaModels)).toBe(true);
    unregister();
    expect(ipc.handlers.has(CHANNEL.llmListOllamaModels)).toBe(false);
  });

  it('returns mapped models on a happy-path response', async () => {
    let observedUrl = '';
    const fakeFetch = (async (url: unknown) => {
      observedUrl = String(url);
      return jsonResponse({
        models: [
          { name: 'llama3.1', modified_at: '2026-01-02T00:00:00Z', size: 4_700_000_000 },
          { name: 'qwen2.5', modified_at: '2026-01-03T00:00:00Z', size: 8_300_000_000 },
        ],
      });
    }) as unknown as typeof fetch;

    registerLlmHandlers(ipc, fakeFetch, logger);

    const result = (await invoke(ipc, CHANNEL.llmListOllamaModels, {
      endpoint: 'http://localhost:11434',
    })) as { models: ReadonlyArray<{ name: string; modifiedAt: string; sizeBytes: number }> };

    expect(observedUrl).toBe('http://localhost:11434/api/tags');
    expect(result.models).toHaveLength(2);
    expect(result.models[0]).toEqual({
      name: 'llama3.1',
      modifiedAt: '2026-01-02T00:00:00Z',
      sizeBytes: 4_700_000_000,
    });
    expect(result.models[1]?.name).toBe('qwen2.5');
  });

  it('strips a trailing slash before joining /api/tags', async () => {
    let observedUrl = '';
    const fakeFetch = (async (url: unknown) => {
      observedUrl = String(url);
      return jsonResponse({ models: [] });
    }) as unknown as typeof fetch;

    registerLlmHandlers(ipc, fakeFetch, logger);

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

    registerLlmHandlers(ipc, fakeFetch, logger);

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
    registerLlmHandlers(ipc, fakeFetch, logger);

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
    registerLlmHandlers(ipc, fakeFetch, logger);

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
    registerLlmHandlers(ipc, fakeFetch, logger);

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
    registerLlmHandlers(ipc, fakeFetch, logger);

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

    registerLlmHandlers(ipc, fakeFetch, logger);

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

    registerLlmHandlers(ipc, fakeFetch, logger);

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

    registerLlmHandlers(ipc, fakeFetch, logger);

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

    registerLlmHandlers(ipc, fakeFetch, logger);

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

    registerLlmHandlers(ipc, fakeFetch, logger);

    await invoke(ipc, CHANNEL.llmListOllamaModels, {
      endpoint: 'https://ollama.example.com:11434',
    });

    expect(observedUrl).toBe('https://ollama.example.com:11434/api/tags');
  });
});
