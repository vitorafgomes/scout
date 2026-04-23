import { describe, it, expect, beforeEach } from 'bun:test';
import { createServer as createNetServer } from 'node:net';
import { awaitCallback, findFreePort } from '../src/oauth/loopback-server.js';
import {
  OAuthStateMismatchError,
  OAuthUserCancelledError,
  OAuthLoopbackPortInUseError,
  DiscordTokenExchangeError,
} from '@scout/shared';
import { createLogger } from '../src/util/logger.js';

const logger = createLogger('test', { sink: () => {} });

async function getFreePort(): Promise<number> {
  return new Promise<number>((resolve, reject) => {
    const srv = createNetServer();
    srv.listen(0, '127.0.0.1', () => {
      const addr = srv.address();
      if (addr !== null && typeof addr === 'object') {
        const p = addr.port;
        srv.close(() => resolve(p));
      } else {
        srv.close(() => reject(new Error('no address')));
      }
    });
    srv.on('error', reject);
  });
}

describe('awaitCallback', () => {
  let port: number;

  beforeEach(async () => {
    port = await getFreePort();
  });

  it('resolves with code and state on valid GET /callback', async () => {
    const callbackPromise = awaitCallback({
      expectedState: 'mystate',
      port,
      hostname: '127.0.0.1',
      timeoutMs: 5000,
      logger,
    });

    await new Promise<void>((r) => setTimeout(r, 30));

    const resp = await fetch(`http://127.0.0.1:${port}/callback?code=authcode&state=mystate`);
    expect(resp.status).toBe(200);

    const result = await callbackPromise;
    expect(result.code).toBe('authcode');
    expect(result.state).toBe('mystate');
  });

  it('server closes within 100ms after valid callback', async () => {
    const callbackPromise = awaitCallback({
      expectedState: 'st',
      port,
      hostname: '127.0.0.1',
      timeoutMs: 5000,
      logger,
    });

    await new Promise<void>((r) => setTimeout(r, 30));
    await fetch(`http://127.0.0.1:${port}/callback?code=c&state=st`);
    await callbackPromise;

    await new Promise<void>((r) => setTimeout(r, 100));

    let refused = false;
    try {
      await fetch(`http://127.0.0.1:${port}/callback?code=c2&state=st`);
    } catch {
      refused = true;
    }
    expect(refused).toBe(true);
  });

  it('rejects with OAuthStateMismatchError on state mismatch', async () => {
    const callbackPromise = awaitCallback({
      expectedState: 'correct',
      port,
      hostname: '127.0.0.1',
      timeoutMs: 5000,
      logger,
    });

    await new Promise<void>((r) => setTimeout(r, 30));
    await fetch(`http://127.0.0.1:${port}/callback?code=c&state=wrong`);

    await expect(callbackPromise).rejects.toBeInstanceOf(OAuthStateMismatchError);
  });

  it('rejects with OAuthUserCancelledError on access_denied', async () => {
    const callbackPromise = awaitCallback({
      expectedState: 'st',
      port,
      hostname: '127.0.0.1',
      timeoutMs: 5000,
      logger,
    });

    await new Promise<void>((r) => setTimeout(r, 30));
    await fetch(
      `http://127.0.0.1:${port}/callback?error=access_denied&error_description=User+denied`,
    );

    await expect(callbackPromise).rejects.toBeInstanceOf(OAuthUserCancelledError);
  });

  it('non-access_denied error rejects with DiscordTokenExchangeError', async () => {
    const callbackPromise = awaitCallback({
      expectedState: 'st',
      port,
      hostname: '127.0.0.1',
      timeoutMs: 5000,
      logger,
    });

    await new Promise<void>((r) => setTimeout(r, 30));
    await fetch(
      `http://127.0.0.1:${port}/callback?error=server_error&error_description=Something+broke`,
    );

    await expect(callbackPromise).rejects.toBeInstanceOf(DiscordTokenExchangeError);
  });

  it('returns 404 for unknown paths and keeps listening for /callback', async () => {
    const callbackPromise = awaitCallback({
      expectedState: 'st',
      port,
      hostname: '127.0.0.1',
      timeoutMs: 5000,
      logger,
    });

    await new Promise<void>((r) => setTimeout(r, 30));

    const bogus = await fetch(`http://127.0.0.1:${port}/hello`);
    expect(bogus.status).toBe(404);

    const good = await fetch(`http://127.0.0.1:${port}/callback?code=abc&state=st`);
    expect(good.status).toBe(200);

    const result = await callbackPromise;
    expect(result.code).toBe('abc');
  });

  it('rejects with OAuthUserCancelledError after timeout', async () => {
    const callbackPromise = awaitCallback({
      expectedState: 'st',
      port,
      hostname: '127.0.0.1',
      timeoutMs: 80,
      logger,
    });

    await expect(callbackPromise).rejects.toBeInstanceOf(OAuthUserCancelledError);
  });
});

describe('findFreePort', () => {
  it('returns the preferred port when it is free', async () => {
    const freePort = await getFreePort();
    const result = await findFreePort(freePort, 0);
    expect(result).toBe(freePort);
  });

  it('throws OAuthLoopbackPortInUseError when no port is available', async () => {
    await expect(findFreePort(53682, 0, async (_p) => false)).rejects.toBeInstanceOf(
      OAuthLoopbackPortInUseError,
    );
  });

  it('falls through to the next port when preferred is taken', async () => {
    let callCount = 0;
    const probe = async (_port: number): Promise<boolean> => {
      callCount++;
      return callCount > 1;
    };

    const result = await findFreePort(53682, 3, probe);
    expect(result).toBe(53683);
  });
});
