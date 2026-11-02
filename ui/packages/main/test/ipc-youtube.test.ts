import { describe, it, expect } from 'bun:test';
import { CHANNEL, readIpcError, type WorkerStatus } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { restartWorkerIfRunning } from '../src/ipc/github.js';
import { registerYouTubeHandlers } from '../src/ipc/youtube.js';
import {
  API_KEY,
  AUTH_CODE,
  CLIENT_FILE,
  CLIENT_SECRET,
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
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger);

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
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger);

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
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger);

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
    registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger);

    const status = await invoke(ipc, CHANNEL.youtubeSignOut);
    await restart;

    expect(status).toMatchObject({ connected: false, channelTitle: null });
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(fake.vault.has('youtube.session')).toBe(false);
    expect(calls).toEqual(['stop', 'start']);
  });

  it('removes all six handlers on dispose', () => {
    const fake = makeGoogleFake();
    const ipc = makeFakeIpcMain();
    const dispose = registerYouTubeHandlers(ipc, fake.auth, fake.deps.logger);
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
