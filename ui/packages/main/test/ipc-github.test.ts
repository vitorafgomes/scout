import { describe, it, expect } from 'bun:test';
import { CHANNEL, readIpcError, type WorkerStatus } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { registerGitHubHandlers, restartWorkerIfRunning } from '../src/ipc/github.js';
import { TOKEN_FILE, connect, makeGitHubFake } from './github-fakes.js';

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

function makeWorker(state: WorkerStatus['state']): {
  status: WorkerStatus;
  calls: string[];
  stop(): Promise<{ exitCode: number | null }>;
  start(): Promise<{ pid: number }>;
} {
  const calls: string[] = [];
  return {
    status: { state, restarts: 0 },
    calls,
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

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

function invoke(ipc: ReturnType<typeof makeFakeIpcMain>, channel: string): Promise<unknown> {
  const handler = ipc.handlers.get(channel);
  if (handler === undefined) throw new Error(`no handler for ${channel}`);
  return Promise.resolve(handler(NULL_EVENT));
}

describe('GitHub IPC', () => {
  it('sends the renderer the code and the login, never a token or the device code', async () => {
    const fake = makeGitHubFake();
    const ipc = makeFakeIpcMain();
    registerGitHubHandlers(ipc, fake.auth, fake.deps.logger);
    fake.tokenReplies.push({
      access_token: 'ghu_first',
      expires_in: 28800,
      refresh_token: 'ghr_first',
    });

    const pending = await invoke(ipc, CHANNEL.githubSignIn);
    await fake.auth.settled();
    const status = await invoke(ipc, CHANNEL.githubStatus);

    expect(pending).toEqual({
      userCode: 'WDJB-MJHT',
      verificationUri: 'https://github.com/login/device',
    });
    expect(status).toMatchObject({ connected: true, login: 'vitorafgomes' });
    const payloads = JSON.stringify([pending, status]);
    expect(payloads).not.toContain('ghu_');
    expect(payloads).not.toContain('ghr_');
    expect(payloads).not.toContain('device-');
  });

  it('accepts a pasted token from the renderer and never sends it back', async () => {
    const fake = makeGitHubFake();
    const ipc = makeFakeIpcMain();
    registerGitHubHandlers(ipc, fake.auth, fake.deps.logger);
    const handler = ipc.handlers.get(CHANNEL.githubSaveToken)!;

    const saved = await handler(NULL_EVENT, { token: 'github_pat_11ABCDEFG0ipc' });
    const status = await invoke(ipc, CHANNEL.githubStatus);

    expect(saved).toMatchObject({ connected: true, method: 'token', login: 'vitorafgomes' });
    expect(JSON.stringify([saved, status])).not.toContain('github_pat_11ABCDEFG0ipc');
  });

  it('keeps the error code across the IPC boundary when the App is not configured', async () => {
    const fake = makeGitHubFake({ clientId: '' });
    const ipc = makeFakeIpcMain();
    registerGitHubHandlers(ipc, fake.auth, fake.deps.logger);

    let thrown: unknown = null;
    try {
      await invoke(ipc, CHANNEL.githubSignIn);
    } catch (err: unknown) {
      thrown = err;
    }

    expect(readIpcError(new Error((thrown as Error).message))).toEqual({
      code: 'github/not-configured',
      message: 'The Scout GitHub App is not configured yet.',
    });
  });

  it('disconnect deletes the token file, clears the session and restarts the running worker', async () => {
    const worker = makeWorker('running');
    const fake = makeGitHubFake();
    let restart: Promise<void> = Promise.resolve();
    fake.deps.onConnectionChanged = () => {
      restart = restartWorkerIfRunning(worker, fake.deps.logger);
    };
    await connect(fake);
    worker.calls.length = 0;
    const ipc = makeFakeIpcMain();
    registerGitHubHandlers(ipc, fake.auth, fake.deps.logger);

    const status = await invoke(ipc, CHANNEL.githubSignOut);
    await restart;

    expect(status).toMatchObject({ connected: false, login: null });
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(fake.vault.has('github.session')).toBe(false);
    expect(worker.calls).toEqual(['stop', 'start']);
  });

  it('removes all five handlers on dispose', () => {
    const fake = makeGitHubFake();
    const ipc = makeFakeIpcMain();
    const dispose = registerGitHubHandlers(ipc, fake.auth, fake.deps.logger);
    const registered = [...ipc.handlers.keys()].sort();

    dispose();

    expect(registered).toEqual(
      [
        CHANNEL.githubCancel,
        CHANNEL.githubSaveToken,
        CHANNEL.githubSignIn,
        CHANNEL.githubSignOut,
        CHANNEL.githubStatus,
      ].sort(),
    );
    expect(ipc.handlers.size).toBe(0);
  });
});

describe('restartWorkerIfRunning', () => {
  it('stops then starts a running worker so it picks up the GitHub connection', async () => {
    const worker = makeWorker('running');

    await restartWorkerIfRunning(worker, makeGitHubFake().deps.logger);

    expect(worker.calls).toEqual(['stop', 'start']);
  });

  it('leaves a stopped worker alone; it reads the connection when it next starts', async () => {
    const worker = makeWorker('stopped');

    await restartWorkerIfRunning(worker, makeGitHubFake().deps.logger);

    expect(worker.calls).toEqual([]);
  });
});
