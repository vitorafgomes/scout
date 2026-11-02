import { describe, it, expect } from 'bun:test';
import {
  CHANNEL,
  ControlPlaneUnavailableError,
  readIpcError,
  type FeatureDto,
  type FeatureId,
  type WorkerStatus,
} from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { restartWorkerIfRunning } from '../src/ipc/github.js';
import { registerYouTubeHandlers, revokeYouTubeWriteGrant } from '../src/ipc/youtube.js';
import {
  API_KEY,
  AUTH_CODE,
  CLIENT_FILE,
  CLIENT_SECRET,
  ONE_HOUR_MS,
  TOKEN_FILE,
  connect,
  json,
  makeGoogleFake,
} from './google-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

function makeFakeIpcMain(): Pick<IpcMain, 'handle' | 'removeHandler'> & {
  handlers: Map<string, IpcHandler>;
} {
  const handlers = new Map<string, IpcHandler>();
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

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

function recordingControlPlane(calls: string[]) {
  return async () => ({
    async setWriteGrant(id: FeatureId, granted: boolean, confirmWrites: boolean) {
      calls.push(`grant ${id} ${granted} ${confirmWrites}`);
      return {} as FeatureDto;
    },
  });
}

function recordingWorker(calls: string[]) {
  return {
    status: { state: 'running', restarts: 0 } as WorkerStatus,
    async stop() {
      calls.push('stop');
      return { exitCode: 0 };
    },
    async start() {
      calls.push('start');
      return { pid: 42 };
    },
  };
}

function wiredToControlPlane(
  calls: string[],
  controlPlane: Parameters<typeof revokeYouTubeWriteGrant>[0] = recordingControlPlane(calls),
) {
  const worker = recordingWorker(calls);
  let restart: Promise<void> = Promise.resolve();
  const fake = makeGoogleFake({
    onConnectionChanged: () => {
      restart = restartWorkerIfRunning(worker, fake.deps.logger);
    },
    revokeWriteGrant: () =>
      revokeYouTubeWriteGrant(controlPlane, fake.deps.logger, () => calls.push('features changed')),
  });
  return { fake, restarted: () => restart };
}

async function connectedWithRunningWorker(calls: string[], method: 'oauth' | 'apiKey') {
  const worker = recordingWorker(calls);
  let restart: Promise<void> = Promise.resolve();
  const fake = makeGoogleFake({
    onConnectionChanged: () => {
      restart = restartWorkerIfRunning(worker, fake.deps.logger);
    },
  });
  if (method === 'oauth') await connect(fake);
  else await fake.auth.saveApiKey(API_KEY);
  await restart;
  calls.length = 0;
  return { fake, restarted: () => restart };
}

function invoke(
  ipc: ReturnType<typeof makeFakeIpcMain>,
  channel: string,
  ...args: unknown[]
): Promise<unknown> {
  const handler = ipc.handlers.get(channel);
  if (handler === undefined) throw new Error(`no handler for ${channel}`);
  return Promise.resolve(handler(NULL_EVENT, ...args));
}

describe('YouTube IPC', () => {
  it('sends the renderer the channel title and the method, never a token, the code or the client secret', async () => {
    const fake = makeGoogleFake();
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger, recordingControlPlane([]));

    const pending = await invoke(ipc, CHANNEL.youtubeSignIn);
    await fake.auth.settled();
    const status = await invoke(ipc, CHANNEL.youtubeStatus);

    expect(pending).toMatchObject({ pending: true, connected: false });
    expect(status).toEqual({
      configured: true,
      connected: true,
      method: 'oauth',
      channelId: 'UCscoutTestChannel',
      channelTitle: 'Scout Test Channel',
      pending: false,
      error: null,
      clientConfigPath: CLIENT_FILE,
      source: 'file',
    });
    const payloads = JSON.stringify([pending, status]);
    for (const secret of ['ya29.', '1//refresh', AUTH_CODE, CLIENT_SECRET]) {
      expect(payloads).not.toContain(secret);
    }
  });

  it('keeps the error code across the IPC boundary when sign-in is not configured', async () => {
    const fake = makeGoogleFake();
    fake.files.delete(CLIENT_FILE);
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger, recordingControlPlane([]));

    const thrown = await invoke(ipc, CHANNEL.youtubeSignIn).catch((err: unknown) => err);

    expect(readIpcError(new Error((thrown as Error).message))).toEqual({
      code: 'youtube/not-configured',
      message:
        'Sign in with Google needs a Google OAuth client; use an API key instead, or add the client file.',
    });
  });

  it('accepts a pasted key from the renderer, never sends it back, and a rejected key error carries no key', async () => {
    const fake = makeGoogleFake();
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger, recordingControlPlane([]));

    const saved = await invoke(ipc, CHANNEL.youtubeSaveApiKey, { apiKey: API_KEY });
    const cleared = await invoke(ipc, CHANNEL.youtubeClearApiKey);
    fake.keyCheck = () => json({ error: { message: `bad key ${API_KEY}` } }, 400);
    const thrown = await invoke(ipc, CHANNEL.youtubeSaveApiKey, { apiKey: API_KEY }).catch(
      (err: unknown) => err,
    );

    expect(saved).toMatchObject({ connected: true, method: 'apiKey' });
    expect(cleared).toMatchObject({ connected: false, method: null });
    expect(readIpcError(new Error((thrown as Error).message)).code).toBe('youtube/key-rejected');
    expect(JSON.stringify([saved, cleared, (thrown as Error).message, ...fake.logs])).not.toContain(
      API_KEY,
    );
  });

  it('disconnect removes the token file, clears the session and restarts the running worker', async () => {
    const calls: string[] = [];
    const worker = {
      status: { state: 'running', restarts: 0 } as WorkerStatus,
      async stop() {
        calls.push('stop');
        return { exitCode: 0 };
      },
      async start() {
        calls.push('start');
        return { pid: 42 };
      },
    };
    let restart: Promise<void> = Promise.resolve();
    const fake = makeGoogleFake({
      onConnectionChanged: () => {
        restart = restartWorkerIfRunning(worker, fake.deps.logger);
      },
    });
    await connect(fake);
    await restart;
    calls.length = 0;
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger, recordingControlPlane([]));

    const status = await invoke(ipc, CHANNEL.youtubeSignOut);
    await restart;

    expect(status).toMatchObject({ connected: false, channelTitle: null });
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(fake.vault.has('youtube.session')).toBe(false);
    expect(calls).toEqual(['stop', 'start']);
  });

  it('Disconnecting YouTube revokes its write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = await connectedWithRunningWorker(calls, 'oauth');
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger, recordingControlPlane(calls), () =>
      calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeSignOut);
    await restarted();

    expect(status).toMatchObject({ connected: false });
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it('a control plane that is down does not block the YouTube disconnect and is logged', async () => {
    const calls: string[] = [];
    const { fake, restarted } = await connectedWithRunningWorker(calls, 'oauth');
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      async () => ({
        setWriteGrant: async () => {
          throw new ControlPlaneUnavailableError();
        },
      }),
      () => calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeSignOut);
    await restarted();

    expect(status).toMatchObject({ connected: false, channelTitle: null });
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(calls).toEqual(['stop', 'start']);
    expect(fake.logs).toContain(
      'warn youtube: write grant not revoked [{"code":"control-plane/unavailable"}]',
    );
    expect(JSON.stringify(fake.logs)).not.toContain('ya29.');
  });

  it('removing the API key that is the only YouTube credential revokes its write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = await connectedWithRunningWorker(calls, 'apiKey');
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger, recordingControlPlane(calls), () =>
      calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeClearApiKey);
    await restarted();

    expect(status).toMatchObject({ connected: false, method: null });
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it('removing the API key while Google sign-in stays connected keeps the write grant', async () => {
    const calls: string[] = [];
    const { fake, restarted } = await connectedWithRunningWorker(calls, 'oauth');
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger, recordingControlPlane(calls), () =>
      calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeClearApiKey);
    await restarted();

    expect(status).toMatchObject({ connected: true, method: 'oauth' });
    expect(calls).toEqual([]);
  });

  it('Signing in to YouTube revokes its write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = wiredToControlPlane(calls);

    await connect(fake);
    await restarted();

    expect((await fake.auth.status()).connected).toBe(true);
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it('Saving a YouTube API key revokes its write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = wiredToControlPlane(calls);
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger, recordingControlPlane(calls), () =>
      calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeSaveApiKey, { apiKey: API_KEY });
    await restarted();

    expect(status).toMatchObject({ connected: true, method: 'apiKey' });
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it('Google refusing the saved sign-in on refresh revokes the YouTube write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = wiredToControlPlane(calls);
    await connect(fake);
    await restarted();
    calls.length = 0;
    fake.tokenReplies.push(json({ error: 'invalid_grant' }, 400));

    fake.clock.now += ONE_HOUR_MS;
    await fake.auth.refreshIfDue();
    await restarted();

    expect((await fake.auth.status()).connected).toBe(false);
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it('a control plane that is down does not block the YouTube sign-in and is logged', async () => {
    const calls: string[] = [];
    const { fake, restarted } = wiredToControlPlane(calls, async () => ({
      setWriteGrant: async () => {
        throw new ControlPlaneUnavailableError();
      },
    }));

    await connect(fake);
    await restarted();

    expect(await fake.auth.status()).toMatchObject({
      connected: true,
      method: 'oauth',
      error: null,
    });
    expect(calls).toEqual(['stop', 'start']);
    expect(fake.logs).toContain(
      'warn youtube: write grant not revoked [{"code":"control-plane/unavailable"}]',
    );
  });

  it('clearing the API key when the YouTube status cannot be read still revokes its write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = await connectedWithRunningWorker(calls, 'apiKey');
    const ipc = makeFakeIpcMain();
    const unreadableStatus = {
      signIn: () => fake.auth.signIn(),
      cancelSignIn: () => fake.auth.cancelSignIn(),
      signOut: () => fake.auth.signOut(),
      saveApiKey: (raw: string) => fake.auth.saveApiKey(raw),
      clearApiKey: () => fake.auth.clearApiKey(),
      status: async (): Promise<never> => {
        throw new Error('the vault could not be read');
      },
    };
    registerYouTubeHandlers(
      ipc,
      unreadableStatus,
      fake.deps.logger,
      recordingControlPlane(calls),
      () => calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeClearApiKey);
    await restarted();

    expect(status).toMatchObject({ connected: false, method: null });
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it('removes all six handlers on dispose', () => {
    const fake = makeGoogleFake();
    const ipc = makeFakeIpcMain();
    const dispose = registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      recordingControlPlane([]),
    );
    const registered = [...ipc.handlers.keys()].sort();

    dispose();

    expect(registered).toEqual(
      [
        CHANNEL.youtubeCancelSignIn,
        CHANNEL.youtubeClearApiKey,
        CHANNEL.youtubeSaveApiKey,
        CHANNEL.youtubeSignIn,
        CHANNEL.youtubeSignOut,
        CHANNEL.youtubeStatus,
      ].sort(),
    );
    expect(ipc.handlers.size).toBe(0);
  });
});
