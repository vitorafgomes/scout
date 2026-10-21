import { describe, it, expect, mock } from 'bun:test';
import { WorkerHttpClient } from '../src/worker/http-client.js';
import type { Logger } from '../src/util/logger.js';

function noopLogger(): Logger {
  const fn = (): void => {};
  const logger: Logger = {
    debug: fn,
    info: fn,
    warn: fn,
    error: fn,
    child: () => logger,
  };
  return logger;
}

function makeClient(fetchImpl: typeof fetch): WorkerHttpClient {
  return new WorkerHttpClient({
    getEndpoint: () => 'http://localhost:5000',
    fetch: fetchImpl,
    logger: noopLogger(),
  });
}

describe('WorkerHttpClient.delete', () => {
  it('issues a DELETE request and returns the parsed JSON body', async () => {
    const fetchImpl = mock(async (_url: string | URL | Request, init?: RequestInit) => {
      expect(init?.method).toBe('DELETE');
      return new Response(JSON.stringify({ ok: true }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      });
    });
    const client = makeClient(fetchImpl as unknown as typeof fetch);

    const result = await client.delete<{ ok: boolean }>('/features/github/credentials');

    expect(result).toEqual({ ok: true });
    expect(fetchImpl).toHaveBeenCalledTimes(1);
  });

  it('returns undefined on 204 No Content', async () => {
    const fetchImpl = mock(async () => new Response(null, { status: 204 }));
    const client = makeClient(fetchImpl as unknown as typeof fetch);

    const result = await client.delete<void>('/features/github/credentials');

    expect(result).toBeUndefined();
  });

  it('throws a mapped error for 4xx responses with a JSON body', async () => {
    const fetchImpl = mock(
      async () =>
        new Response(JSON.stringify({ code: 'providers/unknown', message: 'no such feature' }), {
          status: 404,
          headers: { 'content-type': 'application/json' },
        }),
    );
    const client = makeClient(fetchImpl as unknown as typeof fetch);

    let thrown: unknown = null;
    try {
      await client.delete<void>('/features/unknown/credentials');
    } catch (err: unknown) {
      thrown = err;
    }
    expect(thrown).not.toBeNull();
    expect((thrown as Error).message).toBe('no such feature');
  });
});
