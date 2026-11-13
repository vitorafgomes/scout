import { describe, it, expect } from 'bun:test';
import {
  CHANNEL,
  ControlPlaneUnavailableError,
  readIpcError,
  type FeatureDto,
  type WorkerStatus,
} from '@scout/shared';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { readGrantedWriteFeatures } from '../src/worker/build-env.js';
import { revokeFeatureWriteGrant, vaultPendingRevokes } from '../src/ipc/write-grant-revoke.js';
import { memoryPendingRevokes } from './pending-revokes-fake.js';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import {
  registerGitHubBotHandlers,
  registerGitHubHandlers,
  restartWorkerIfRunning,
} from '../src/ipc/github.js';
import { restartPath } from './restart-fakes.js';
import {
  ACCESS_TOKENS_URL,
  CONVERSIONS_URL,
  INSTALLATIONS_URL,
  PRIVATE_KEY,
  REPOSITORIES_URL,
  conversion,
  createBot,
  installationToken,
  makeBotFake,
  reply,
} from './github-bot-fakes.js';
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
      restart = restartWorkerIfRunning(restartPath(worker), fake.deps.logger);
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

  it('Disconnect GitHub revokes the GitHub write grant, as Disconnect YouTube revokes the YouTube one', async () => {
    const revoked: string[] = [];
    const fake = makeGitHubFake({
      revokeWriteGrant: async () => {
        revoked.push('github');
      },
    });
    await connect(fake);
    revoked.length = 0;
    const ipc = makeFakeIpcMain();
    registerGitHubHandlers(ipc, fake.auth, fake.deps.logger);

    await invoke(ipc, CHANNEL.githubSignOut);

    expect(revoked).toEqual(['github']);
  });

  it('connecting GitHub with a token of another account revokes the GitHub write grant', async () => {
    const revoked: string[] = [];
    const fake = makeGitHubFake({
      revokeWriteGrant: async () => {
        revoked.push('github');
      },
    });
    await connect(fake);
    revoked.length = 0;
    fake.user = () =>
      new Response(JSON.stringify({ login: 'octocat' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    const ipc = makeFakeIpcMain();
    registerGitHubHandlers(ipc, fake.auth, fake.deps.logger);

    const saved = await ipc.handlers.get(CHANNEL.githubSaveToken)!(NULL_EVENT, {
      token: 'github_pat_11ABCDEFG0other',
    });

    expect(saved).toMatchObject({ connected: true, login: 'octocat' });
    expect(revoked).toEqual(['github']);
  });

  it('GitHub refusing the saved sign-in on refresh revokes the GitHub write grant before the connection change is announced', async () => {
    const calls: string[] = [];
    const fake = makeGitHubFake({
      revokeWriteGrant: async () => {
        calls.push('revoke');
      },
    });
    await connect(fake);
    fake.deps.onConnectionChanged = () => calls.push('connection changed');
    calls.length = 0;
    fake.tokenReplies.push({ error: 'bad_refresh_token' });

    fake.clock.now += 8 * 60 * 60 * 1000;
    await fake.auth.refreshIfDue();

    expect(calls).toEqual(['revoke', 'connection changed']);
  });

  it('a control plane that is down does not block the GitHub Disconnect, pushes no features change and is logged with its code', async () => {
    const pushed: string[] = [];
    const fake = makeGitHubFake();
    fake.deps.revokeWriteGrant = () =>
      revokeFeatureWriteGrant(
        'github',
        async () => {
          throw new ControlPlaneUnavailableError('socket reset');
        },
        memoryPendingRevokes(),
        fake.deps.logger,
        () => pushed.push('features changed'),
      );
    await connect(fake);
    const ipc = makeFakeIpcMain();
    registerGitHubHandlers(ipc, fake.auth, fake.deps.logger);

    const status = await invoke(ipc, CHANNEL.githubSignOut);

    expect(status).toMatchObject({ connected: false, login: null });
    expect(fake.vault.has('github.session')).toBe(false);
    expect(pushed).toEqual([]);
    expect(fake.logs).toContain(
      'warn github: write grant not revoked [{"code":"control-plane/unavailable"}]',
    );
  });

  it('a revoke the control plane confirms asks GitHub false for the github feature and announces the features change', async () => {
    const calls: string[] = [];
    const fake = makeGitHubFake();
    fake.deps.revokeWriteGrant = () =>
      revokeFeatureWriteGrant(
        'github',
        async () => ({
          async setWriteGrant(id: string, granted: boolean, confirmWrites: boolean) {
            calls.push(`grant ${id} ${granted} ${confirmWrites}`);
            return {} as FeatureDto;
          },
        }),
        memoryPendingRevokes(),
        fake.deps.logger,
        () => calls.push('features changed'),
      );
    await connect(fake);
    calls.length = 0;
    const ipc = makeFakeIpcMain();
    registerGitHubHandlers(ipc, fake.auth, fake.deps.logger);

    await invoke(ipc, CHANNEL.githubSignOut);

    expect(calls).toEqual(['grant github false false', 'features changed']);
  });

  it('a GitHub Disconnect while the control plane is down is revoked as soon as it is back, before the next worker start reads the grants', async () => {
    const pending = memoryPendingRevokes();
    const calls: string[] = [];
    let granted = false;
    let up = true;
    const controlPlane = {
      async setWriteGrant(id: string, grant: boolean) {
        if (!up) throw new ControlPlaneUnavailableError('socket reset');
        calls.push(`grant ${id} ${grant}`);
        granted = grant;
        return {} as FeatureDto;
      },
      async listFeatures() {
        calls.push('list');
        return [{ id: 'github', granted } as FeatureDto];
      },
    };
    const fake = makeGitHubFake();
    fake.deps.revokeWriteGrant = () =>
      revokeFeatureWriteGrant('github', async () => controlPlane, pending, fake.deps.logger);
    await connect(fake);
    granted = true;
    up = false;
    const ipc = makeFakeIpcMain();
    registerGitHubHandlers(ipc, fake.auth, fake.deps.logger);

    await invoke(ipc, CHANNEL.githubSignOut);
    const whileDown = await pending.list();
    up = true;
    calls.length = 0;
    const grants = await readGrantedWriteFeatures(controlPlane, fake.deps.logger, pending);

    expect({ whileDown, calls, grants, after: await pending.list() }).toEqual({
      whileDown: ['github'],
      calls: ['grant github false', 'list'],
      grants: [],
      after: [],
    });
  });

  it('a pending GitHub revoke the control plane still refuses stays pending, and the worker starts without the GitHub grant', async () => {
    const pending = memoryPendingRevokes();
    await pending.add('github');
    const controlPlane = {
      async setWriteGrant(): Promise<FeatureDto> {
        throw new ControlPlaneUnavailableError('socket reset');
      },
      async listFeatures() {
        return [
          { id: 'github', granted: true } as FeatureDto,
          { id: 'youtube', granted: true } as FeatureDto,
        ];
      },
    };
    const fake = makeGitHubFake();

    const grants = await readGrantedWriteFeatures(controlPlane, fake.deps.logger, pending);

    expect({ grants, pending: await pending.list() }).toEqual({
      grants: ['youtube'],
      pending: ['github'],
    });
    expect(fake.logs).toContain(
      'warn features: pending write revoke still not taken [{"id":"github","code":"control-plane/unavailable"}]',
    );
  });

  it("a pending revoke is kept in main's vault, so it survives a restart of main", async () => {
    const vault = new Map<string, string>();
    const over = (store: Map<string, string>) =>
      vaultPendingRevokes({
        get: async (key) => store.get(key) ?? null,
        store: async (key, value) => {
          store.set(key, value);
        },
        delete: async (key) => store.delete(key),
      });

    await over(vault).add('github');

    expect({ restarted: await over(vault).list(), key: [...vault.keys()] }).toEqual({
      restarted: ['github'],
      key: ['features.pending-revokes'],
    });
  });

  it('main wires the GitHub revoke, the features push on a GitHub connection change, the pending revokes and the main-window check', () => {
    const main = readFileSync(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');
    const gitHub = main.slice(main.indexOf('const auth = new GitHubAuth({'));
    const features = main.slice(main.indexOf('unregisterFeatures = registerFeaturesHandlers('));

    expect(gitHub).toContain(
      'onConnectionChanged: () => {\n        gitHubActivity?.reconnected();\n        sendToAllWindows(CHANNEL.featuresChanged, gitHubLogger);',
    );
    expect(gitHub).toContain(
      "revokeWriteGrant: () =>\n        revokeFeatureWriteGrant(\n          'github',",
    );
    expect(gitHub.slice(0, gitHub.indexOf('logger: gitHubLogger,'))).toContain('pendingRevokes,');
    const senders = main.slice(main.indexOf('const senders: SenderHooks = {'));
    expect(senders).toContain(
      "if (sender === mainWindow.webContents) return 'main';\n        }\n        if (operatorWindow?.owns(sender) === true) return 'operator';\n        return quickAsk?.windowOf(sender) ?? 'unknown';\n      },\n      isAppPage: appPageCheck(appPage),\n    };",
    );
    expect(features.slice(0, features.indexOf('});'))).toContain('...senders,');
    expect(main).toContain(
      "logger.child({ module: 'operator' }),\n      chooseWithDialog(dialog),\n      senders,",
    );
    expect(main).toContain(
      'replayPendingRevokes(client, pendingRevokes, youTubeLogger, featuresChanged)',
    );
    expect(main).toContain(
      "readGrantedWriteFeatures(\n            connection.client,\n            logger.child({ module: 'supervisor' }),\n            pendingRevokes,",
    );
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

    await restartWorkerIfRunning(restartPath(worker), makeGitHubFake().deps.logger);

    expect(worker.calls).toEqual(['stop', 'start']);
  });

  it('leaves a stopped worker alone; it reads the connection when it next starts', async () => {
    const worker = makeWorker('stopped');

    await restartWorkerIfRunning(restartPath(worker), makeGitHubFake().deps.logger);

    expect(worker.calls).toEqual([]);
  });
});

describe('GitHub bot IPC', () => {
  it("sends the renderer the bot's status, never its private key or its installation token", async () => {
    const fake = makeBotFake();
    const ipc = makeFakeIpcMain();
    registerGitHubBotHandlers(ipc, fake.bot, fake.deps.logger);
    reply(fake, CONVERSIONS_URL, conversion());
    reply(fake, INSTALLATIONS_URL, { status: 200, body: [{ id: 77 }] });
    reply(fake, ACCESS_TOKENS_URL, installationToken('2026-11-07T13:00:00Z'));
    reply(fake, REPOSITORIES_URL, {
      status: 200,
      body: { repositories: [{ full_name: 'octocat/shop' }] },
    });

    await createBot(fake);
    const status = await invoke(ipc, CHANNEL.githubBotStatus);

    expect(status).toMatchObject({ state: 'installed', repositories: ['octocat/shop'] });
    const payload = JSON.stringify(status);
    expect(payload).not.toContain(PRIVATE_KEY.split('\n')[1]!);
    expect(payload).not.toContain('ghs_');
  });

  it('refuses a malformed switch request from the renderer with the unknown-action message', async () => {
    const fake = makeBotFake();
    const ipc = makeFakeIpcMain();
    registerGitHubBotHandlers(ipc, fake.bot, fake.deps.logger);
    const handler = ipc.handlers.get(CHANNEL.githubBotSetWrites)!;

    const refused = await Promise.resolve(handler(NULL_EVENT, { writes: 'review' })).catch(
      (err: unknown) => readIpcError(err).message,
    );

    expect(refused).toBe('Scout does not know one of those actions.');
  });
});

describe('GitHub IPC: how many repositories the App is installed on', () => {
  it('scout:github.status gives the renderer the sum of the App installations repositories as a number, never a token', async () => {
    const fake = makeGitHubFake();
    fake.installations = { total_count: 2, installations: [{ id: 101 }, { id: 202 }] };
    fake.installationRepositories = new Map([
      [101, { total_count: 6 }],
      [202, { total_count: 8 }],
    ]);
    const ipc = makeFakeIpcMain();
    registerGitHubHandlers(ipc, fake.auth, fake.deps.logger);
    await connect(fake);

    const status = await invoke(ipc, CHANNEL.githubStatus);

    expect(status).toMatchObject({
      connected: true,
      method: 'browser',
      login: 'vitorafgomes',
      installed: true,
      repositories: 14,
    });
    expect(JSON.stringify(status)).not.toContain('ghu_');
  });
});
