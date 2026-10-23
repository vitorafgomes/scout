import { describe, it, expect, beforeEach } from 'bun:test';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { CHANNEL, readIpcError } from '@scout/shared';
import {
  registerProviderKeyHandlers,
  registerProviderModelHandlers,
} from '../src/ipc/provider-keys.js';
import { createLogger } from '../src/util/logger.js';

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

interface FetchCall {
  readonly url: string;
  readonly init: RequestInit | undefined;
}

const KEY = 'sk-secret-key-must-never-leak';

describe('registerProviderKeyHandlers - validateKey', () => {
  let ipc: FakeIpcMain;
  let calls: FetchCall[];
  let logLines: string[];

  function register(
    fetchFn: (url: string, init?: RequestInit) => Promise<Response>,
    timeoutMs?: number,
  ) {
    const logger = createLogger('test', { level: 'debug', sink: (line) => logLines.push(line) });
    const recording = (async (url: string, init?: RequestInit) => {
      calls.push({ url, init });
      return fetchFn(url, init);
    }) as unknown as typeof fetch;
    registerProviderKeyHandlers(ipc, recording, logger, timeoutMs);
  }

  function validate(req: unknown): Promise<unknown> {
    const fn = ipc.handlers.get(CHANNEL.providersValidateKey);
    if (!fn) throw new Error('validateKey handler not registered');
    return fn(null as unknown as IpcMainInvokeEvent, req);
  }

  function headersOf(call: FetchCall | undefined): Headers {
    return new Headers(call?.init?.headers);
  }

  beforeEach(() => {
    ipc = makeFakeIpcMain();
    calls = [];
    logLines = [];
  });

  it('OpenAI: a key the models list accepts (200) validates, sent as a Bearer token to the OpenAI models URL', async () => {
    register(async () => new Response('{"data":[]}', { status: 200 }));

    const res = await validate({ provider: 'OpenAI', apiKey: KEY });

    expect(res).toEqual({ ok: true });
    expect(calls).toHaveLength(1);
    expect(calls[0]?.url).toBe('https://api.openai.com/v1/models');
    expect(calls[0]?.init?.method).toBe('GET');
    expect(headersOf(calls[0]).get('authorization')).toBe(`Bearer ${KEY}`);
    expect(headersOf(calls[0]).get('x-goog-api-key')).toBeNull();
  });

  it('OpenAI: a key the provider rejects (401) is reported as invalid-key', async () => {
    register(async () => new Response('{"error":{}}', { status: 401 }));

    expect(await validate({ provider: 'OpenAI', apiKey: KEY })).toEqual({
      ok: false,
      reason: 'invalid-key',
    });
  });

  it('a 403 from the provider is reported as invalid-key', async () => {
    register(async () => new Response('', { status: 403 }));

    expect(await validate({ provider: 'Gemini', apiKey: KEY })).toEqual({
      ok: false,
      reason: 'invalid-key',
    });
  });

  it('Gemini: the key goes in the x-goog-api-key header to the Gemini models URL, never as a Bearer token', async () => {
    register(async () => new Response('{"models":[]}', { status: 200 }));

    const res = await validate({ provider: 'Gemini', apiKey: KEY });

    expect(res).toEqual({ ok: true });
    expect(calls[0]?.url).toBe('https://generativelanguage.googleapis.com/v1beta/models');
    expect(calls[0]?.url).not.toContain(KEY);
    expect(headersOf(calls[0]).get('x-goog-api-key')).toBe(KEY);
    expect(headersOf(calls[0]).get('authorization')).toBeNull();
  });

  const GEMINI_INVALID_KEY_BODY = JSON.stringify({
    error: {
      code: 400,
      message: 'API key not valid. Please pass a valid API key.',
      status: 'INVALID_ARGUMENT',
      details: [
        {
          '@type': 'type.googleapis.com/google.rpc.ErrorInfo',
          reason: 'API_KEY_INVALID',
          domain: 'googleapis.com',
          metadata: { service: 'generativelanguage.googleapis.com' },
        },
      ],
    },
  });

  it('Gemini: a 400 whose body says API_KEY_INVALID is reported as invalid-key', async () => {
    register(async () => new Response(GEMINI_INVALID_KEY_BODY, { status: 400 }));

    expect(await validate({ provider: 'Gemini', apiKey: KEY })).toEqual({
      ok: false,
      reason: 'invalid-key',
    });
  });

  it('Gemini: a 400 with another reason or a body that is not JSON is reported as unexpected with its status', async () => {
    register(
      async () =>
        new Response(GEMINI_INVALID_KEY_BODY.replace('API_KEY_INVALID', 'OTHER_REASON'), {
          status: 400,
        }),
    );
    expect(await validate({ provider: 'Gemini', apiKey: KEY })).toEqual({
      ok: false,
      reason: 'unexpected',
      status: 400,
    });

    ipc = makeFakeIpcMain();
    register(async () => new Response('<html>bad request</html>', { status: 400 }));
    expect(await validate({ provider: 'Gemini', apiKey: KEY })).toEqual({
      ok: false,
      reason: 'unexpected',
      status: 400,
    });
  });

  it('OpenAI: a 400 is reported as unexpected even with the Gemini invalid-key body', async () => {
    register(async () => new Response(GEMINI_INVALID_KEY_BODY, { status: 400 }));

    expect(await validate({ provider: 'OpenAI', apiKey: KEY })).toEqual({
      ok: false,
      reason: 'unexpected',
      status: 400,
    });
  });

  it('a 500 from the provider is reported as unexpected with its status', async () => {
    register(async () => new Response('oops', { status: 500 }));

    expect(await validate({ provider: 'OpenAI', apiKey: KEY })).toEqual({
      ok: false,
      reason: 'unexpected',
      status: 500,
    });
  });

  it('failing call: a fetch that rejects is reported as unreachable, and neither the response nor any log line carries the key', async () => {
    register(async () => {
      throw new TypeError(`fetch failed: invalid header value ${KEY}`);
    });

    const res = await validate({ provider: 'OpenAI', apiKey: KEY });

    expect(res).toEqual({ ok: false, reason: 'unreachable' });
    expect(JSON.stringify(res)).not.toContain(KEY);
    expect(logLines.length).toBeGreaterThan(0);
    for (const line of logLines) expect(line).not.toContain(KEY);
  });

  it('a provider that does not answer within the timeout is reported as unreachable', async () => {
    register(
      (_url, init) =>
        new Promise<Response>((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () =>
            reject(new DOMException('aborted', 'AbortError')),
          );
        }),
      20,
    );

    const started = Date.now();
    const res = await validate({ provider: 'Gemini', apiKey: KEY });

    expect(res).toEqual({ ok: false, reason: 'unreachable' });
    expect(Date.now() - started).toBeLessThan(2_000);
  });

  it('an unknown provider or empty key is refused without a network call, and the error does not echo the key', async () => {
    register(async () => new Response('', { status: 200 }));

    let thrown: unknown = null;
    try {
      await validate({ provider: 'OpenRouter', apiKey: KEY });
    } catch (err) {
      thrown = err;
    }
    expect(thrown).toBeInstanceOf(Error);
    expect((thrown as Error).message).not.toContain(KEY);
    await expect(validate({ provider: 'OpenAI', apiKey: '   ' })).rejects.toThrow(
      /providers\/invalid-request/,
    );
    expect(calls).toHaveLength(0);
  });

  it('unregister removes the handler', () => {
    const logger = createLogger('test', { sink: () => {} });
    const unregister = registerProviderKeyHandlers(ipc, globalThis.fetch, logger);
    expect(ipc.handlers.has(CHANNEL.providersValidateKey)).toBe(true);
    unregister();
    expect(ipc.handlers.has(CHANNEL.providersValidateKey)).toBe(false);
  });
});

const OPENAI_MODELS_BODY = JSON.stringify({
  object: 'list',
  data: [
    { id: 'gpt-4o-mini', object: 'model' },
    { id: 'o3-mini', object: 'model' },
    { id: 'chatgpt-4o-latest', object: 'model' },
    { id: 'gpt-4.1', object: 'model' },
    { id: 'text-embedding-3-small', object: 'model' },
    { id: 'whisper-1', object: 'model' },
    { id: 'tts-1', object: 'model' },
    { id: 'gpt-4o-mini-tts', object: 'model' },
    { id: 'dall-e-3', object: 'model' },
    { id: 'omni-moderation-latest', object: 'model' },
    { id: 'gpt-4o-realtime-preview', object: 'model' },
    { id: 'gpt-4o-audio-preview', object: 'model' },
    { id: 'gpt-image-1', object: 'model' },
    { id: 'gpt-4o-transcribe', object: 'model' },
    { id: 'gpt-4o-search-preview', object: 'model' },
    { id: 'babbage-002', object: 'model' },
  ],
});

const GEMINI_MODELS_BODY = JSON.stringify({
  models: [
    {
      name: 'models/gemini-2.5-flash',
      displayName: 'Gemini 2.5 Flash',
      supportedGenerationMethods: ['generateContent', 'countTokens'],
    },
    {
      name: 'models/text-embedding-004',
      displayName: 'Text Embedding 004',
      supportedGenerationMethods: ['embedContent'],
    },
    {
      name: 'models/gemini-2.5-pro',
      displayName: 'Gemini 2.5 Pro',
      supportedGenerationMethods: ['generateContent'],
    },
    {
      name: 'models/imagen-4.0-generate-001',
      displayName: 'Imagen 4',
      supportedGenerationMethods: ['predict'],
    },
  ],
});

describe('registerProviderModelHandlers - listModels', () => {
  let ipc: FakeIpcMain;
  let calls: FetchCall[];
  let logLines: string[];
  let stored: Record<string, string>;

  function register(fetchFn: (url: string, init?: RequestInit) => Promise<Response>): void {
    const logger = createLogger('test', { level: 'debug', sink: (line) => logLines.push(line) });
    const recording = (async (url: string, init?: RequestInit) => {
      calls.push({ url, init });
      return fetchFn(url, init);
    }) as unknown as typeof fetch;
    const vault = { get: async (key: string) => stored[key] ?? null };
    registerProviderModelHandlers(ipc, recording, vault, logger, 50);
  }

  function listModels(req: unknown): Promise<unknown> {
    const fn = ipc.handlers.get(CHANNEL.llmListModels);
    if (!fn) throw new Error('listModels handler not registered');
    return fn(null as unknown as IpcMainInvokeEvent, req);
  }

  async function failureOf(req: unknown): Promise<{ code: string | null; message: string }> {
    try {
      await listModels(req);
    } catch (err) {
      return readIpcError(err);
    }
    throw new Error('expected listModels to fail');
  }

  function headersOf(call: FetchCall | undefined): Headers {
    return new Headers(call?.init?.headers);
  }

  beforeEach(() => {
    ipc = makeFakeIpcMain();
    calls = [];
    logLines = [];
    stored = { 'llm.openai.api-key': KEY, 'llm.gemini.api-key': KEY };
  });

  it('OpenAI: lists only chat models (gpt-, o, chatgpt- ids) with the stored key as a Bearer token, and no log line carries the key', async () => {
    register(async () => new Response(OPENAI_MODELS_BODY, { status: 200 }));

    const res = await listModels({ provider: 'OpenAI' });

    expect(res).toEqual({
      models: [
        { id: 'chatgpt-4o-latest', displayName: 'chatgpt-4o-latest' },
        { id: 'gpt-4.1', displayName: 'gpt-4.1' },
        { id: 'gpt-4o-mini', displayName: 'gpt-4o-mini' },
        { id: 'o3-mini', displayName: 'o3-mini' },
      ],
    });
    expect(calls).toHaveLength(1);
    expect(calls[0]?.url).toBe('https://api.openai.com/v1/models');
    expect(calls[0]?.init?.method).toBe('GET');
    expect(headersOf(calls[0]).get('authorization')).toBe(`Bearer ${KEY}`);
    expect(JSON.stringify(res)).not.toContain(KEY);
    expect(logLines.length).toBeGreaterThan(0);
    for (const line of logLines) expect(line).not.toContain(KEY);
  });

  it('Gemini: lists only models that support generateContent, id without "models/" and the API display name, with the key in x-goog-api-key and never in the URL', async () => {
    register(async () => new Response(GEMINI_MODELS_BODY, { status: 200 }));

    const res = await listModels({ provider: 'Gemini' });

    expect(res).toEqual({
      models: [
        { id: 'gemini-2.5-flash', displayName: 'Gemini 2.5 Flash' },
        { id: 'gemini-2.5-pro', displayName: 'Gemini 2.5 Pro' },
      ],
    });
    expect(
      calls[0]?.url.startsWith('https://generativelanguage.googleapis.com/v1beta/models'),
    ).toBe(true);
    expect(calls[0]?.url).not.toContain(KEY);
    expect(headersOf(calls[0]).get('x-goog-api-key')).toBe(KEY);
    expect(headersOf(calls[0]).get('authorization')).toBeNull();
    for (const line of logLines) expect(line).not.toContain(KEY);
  });

  it('failing call: a 401 is reported as the key rejected, without the key in the error', async () => {
    register(async () => new Response('{"error":{}}', { status: 401 }));

    const failure = await failureOf({ provider: 'OpenAI' });

    expect(failure.code).toBe('providers/key-rejected');
    expect(failure.message).toBe('OpenAI rejected the stored key.');
  });

  it('a Gemini 400 whose body says API_KEY_INVALID is reported as the key rejected', async () => {
    register(
      async () =>
        new Response(
          JSON.stringify({ error: { code: 400, details: [{ reason: 'API_KEY_INVALID' }] } }),
          { status: 400 },
        ),
    );

    expect((await failureOf({ provider: 'Gemini' })).code).toBe('providers/key-rejected');
  });

  it('failing call: a network error is reported as unreachable, and neither the error nor any log line carries the key', async () => {
    register(async () => {
      throw new TypeError(`fetch failed: invalid header value ${KEY}`);
    });

    const failure = await failureOf({ provider: 'OpenAI' });

    expect(failure.code).toBe('providers/unreachable');
    expect(failure.message).toBe('OpenAI could not be reached.');
    expect(failure.message).not.toContain(KEY);
    expect(logLines.length).toBeGreaterThan(0);
    for (const line of logLines) expect(line).not.toContain(KEY);
  });

  it('a provider that does not answer within the timeout is reported as unreachable', async () => {
    register(
      (_url, init) =>
        new Promise<Response>((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () =>
            reject(new DOMException('aborted', 'AbortError')),
          );
        }),
    );

    expect((await failureOf({ provider: 'Gemini' })).code).toBe('providers/unreachable');
  });

  it('a 500 is reported as a failed request with its status', async () => {
    register(async () => new Response('oops', { status: 500 }));

    const failure = await failureOf({ provider: 'Gemini' });

    expect(failure.code).toBe('providers/request-failed');
    expect(failure.message).toContain('HTTP 500');
  });

  it('a provider with no stored key is not connected and nothing is sent', async () => {
    stored = {};
    register(async () => new Response(OPENAI_MODELS_BODY, { status: 200 }));

    expect((await failureOf({ provider: 'OpenAI' })).code).toBe('providers/not-connected');
    expect(calls).toHaveLength(0);
  });

  it('an unknown provider is refused without a network call', async () => {
    register(async () => new Response(OPENAI_MODELS_BODY, { status: 200 }));

    expect((await failureOf({ provider: 'OpenRouter' })).code).toBe('providers/invalid-request');
    expect(calls).toHaveLength(0);
  });
});
