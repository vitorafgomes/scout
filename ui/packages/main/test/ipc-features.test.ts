import { describe, it, expect, beforeEach, mock } from 'bun:test';
import {
  CHANNEL,
  ControlPlaneNotImplementedError,
  ControlPlaneUnavailableError,
  readIpcError,
  ScoutError,
  ControlPlaneWriteConfirmationError,
  type FeatureDto,
  type PendingActionDto,
  type WorkerStatus,
  type WriteLogEntryDto,
} from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { registerFeaturesHandlers } from '../src/ipc/features.js';
import { restartWorkerIfRunning } from '../src/ipc/github.js';
import type { FeaturesClient, FeaturesHooks } from '../src/ipc/features.js';
import type { Logger } from '../src/util/logger.js';

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

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

const githubDto: FeatureDto = {
  id: 'github',
  displayName: 'GitHub',
  description: 'GitHub integration',
  requiresCredentials: true,
  hasCredentials: true,
  enabled: true,
  status: 'enabled',
  lastError: null,
  credentialFields: [],
  settingsFields: [],
  settings: {},
  scopeMatrix: null,
  setupSteps: null,
  writeClass: 'none',
  granted: false,
  grantedAt: null,
};

const youtubeDto: FeatureDto = {
  ...githubDto,
  id: 'youtube',
  displayName: 'YouTube',
  scopeMatrix: [{ scope: 'videos.insert', actions: ['Upload a video'], access: 'irreversible' }],
  writeClass: 'executive',
  granted: true,
  grantedAt: '2026-11-02T10:00:00Z',
};

const pendingUpload: PendingActionDto = {
  id: 'act-1',
  featureId: 'youtube',
  action: 'Upload video',
  target: 'channel UC1',
  summary: 'Upload "Intro" as private',
  requestedAt: '2026-11-02T10:00:00Z',
  status: 'pending',
  decidedAt: null,
};

const writeEntry: WriteLogEntryDto = {
  at: '2026-11-02T11:00:00Z',
  featureId: 'youtube',
  action: 'Upload video',
  target: 'channel UC1',
  outcome: 'succeeded',
  approvedBy: 'owner',
  actionId: 'act-1',
};

type StubClient = { [K in keyof FeaturesClient]: ReturnType<typeof mock> };

function makeClient(overrides: Partial<StubClient> = {}): StubClient {
  return {
    listFeatures: mock(async () => [githubDto]),
    setFeatureCredentials: mock(async () => githubDto),
    setupFeature: mock(async () => githubDto),
    deleteFeatureCredentials: mock(async () => githubDto),
    enableFeature: mock(async () => githubDto),
    disableFeature: mock(async () => githubDto),
    setWriteGrant: mock(async () => youtubeDto),
    listPendingActions: mock(async () => [pendingUpload]),
    approveAction: mock(async () => ({ ...pendingUpload, status: 'approved' })),
    rejectAction: mock(async () => ({ ...pendingUpload, status: 'rejected' })),
    listWrites: mock(async () => [writeEntry]),
    ...overrides,
  };
}

function register(
  ipcMain: FakeIpcMain,
  client: StubClient,
  logger: Logger,
  hooks?: FeaturesHooks,
): () => void {
  return registerFeaturesHandlers(
    ipcMain,
    async () => client as unknown as FeaturesClient,
    logger,
    hooks,
  );
}

async function rejection(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => null,
    (err: unknown) => err,
  );
}

function makeRecordingLogger(): { logger: Logger; lines: string[] } {
  const lines: string[] = [];
  const record =
    (level: string) =>
    (...args: unknown[]): void => {
      lines.push(`${level} ${JSON.stringify(args)}`);
    };
  const logger: Logger = {
    debug: record('debug'),
    info: record('info'),
    warn: record('warn'),
    error: record('error'),
    child: () => logger,
  };
  return { logger, lines };
}

describe('registerFeaturesHandlers', () => {
  let ipcMain: FakeIpcMain;
  let logger: Logger;

  beforeEach(() => {
    ipcMain = makeFakeIpcMain();
    logger = noopLogger();
  });

  it('featuresList returns the control plane features as { features }', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const result = await ipcMain.handlers.get(CHANNEL.featuresList)!(NULL_EVENT);

    expect(result).toEqual({ features: [githubDto] });
  });

  it('featureSetCredentials forwards the id and the credential body', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const result = await ipcMain.handlers.get(CHANNEL.featureSetCredentials)!(NULL_EVENT, {
      id: 'github',
      body: { pat: 'abc' },
    });

    expect(client.setFeatureCredentials).toHaveBeenCalledWith('github', { pat: 'abc' });
    expect(result).toEqual(githubDto);
  });

  it('featureSetup forwards credentials and settings', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const result = await ipcMain.handlers.get(CHANNEL.featureSetup)!(NULL_EVENT, {
      id: 'github',
      credentials: { pat: 'ghp_xxx' },
      settings: { default_repo_url: 'https://github.com/acme/repo' },
    });

    expect(client.setupFeature).toHaveBeenCalledWith(
      'github',
      { pat: 'ghp_xxx' },
      { default_repo_url: 'https://github.com/acme/repo' },
    );
    expect(result).toEqual(githubDto);
  });

  it('featureDeleteCredentials, featureEnable and featureDisable forward the id', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    await ipcMain.handlers.get(CHANNEL.featureDeleteCredentials)!(NULL_EVENT, { id: 'github' });
    await ipcMain.handlers.get(CHANNEL.featureEnable)!(NULL_EVENT, {
      id: 'kubernetes',
      confirmWrites: false,
    });
    await ipcMain.handlers.get(CHANNEL.featureDisable)!(NULL_EVENT, { id: 'github' });

    expect(client.deleteFeatureCredentials).toHaveBeenCalledWith('github');
    expect(client.enableFeature).toHaveBeenCalledWith('kubernetes', false);
    expect(client.disableFeature).toHaveBeenCalledWith('github');
  });

  it("deleting a write-capable feature's credentials restarts the worker so the revoked grant leaves it", async () => {
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
    const client = makeClient({
      deleteFeatureCredentials: mock(async (id: string) =>
        id === 'youtube' ? youtubeDto : githubDto,
      ),
    });
    register(ipcMain, client, logger, {
      onWriteGrantChanged: () => {
        restart = restartWorkerIfRunning(worker, logger);
      },
    });
    const deleteCredentials = ipcMain.handlers.get(CHANNEL.featureDeleteCredentials)!;

    await deleteCredentials(NULL_EVENT, { id: 'github' });
    await restart;
    const afterReadOnly = [...calls];
    await deleteCredentials(NULL_EVENT, { id: 'youtube' });
    await restart;

    expect(afterReadOnly).toEqual([]);
    expect(calls).toEqual(['stop', 'start']);
  });

  it('an enable of a write-capable feature that fails still restarts the worker, because the host cleared the grant', async () => {
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
    const client = makeClient({
      enableFeature: mock(async () => {
        throw new Error('youtube could not be activated');
      }),
    });
    register(ipcMain, client, logger, {
      onWriteGrantChanged: () => {
        restart = restartWorkerIfRunning(worker, logger);
      },
    });

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.featureEnable)!(NULL_EVENT, {
        id: 'youtube',
        confirmWrites: true,
      }),
    );
    await restart;

    expect(thrown).not.toBeNull();
    expect(calls).toEqual(['stop', 'start']);
  });

  it('an unknown feature id is rejected with features/invalid-id before reaching the control plane', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.featureEnable)!(NULL_EVENT, { id: 'unknown-thing' }),
    );

    expect((thrown as { code?: string }).code).toBe('features/invalid-id');
    expect(client.enableFeature).not.toHaveBeenCalled();
  });

  it('the youtube feature id is accepted and reaches the control plane', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.featureDisable)!(NULL_EVENT, { id: 'youtube' }),
    );

    expect(thrown).toBeNull();
    expect(client.disableFeature).toHaveBeenCalledWith('youtube');
  });

  it('a control plane that is not running reaches the renderer as control-plane/unavailable', async () => {
    registerFeaturesHandlers(
      ipcMain,
      () => Promise.reject(new ControlPlaneUnavailableError('socket did not appear')),
      logger,
    );

    const thrown = await rejection(ipcMain.handlers.get(CHANNEL.featuresList)!(NULL_EVENT));

    expect(readIpcError(new Error((thrown as Error).message))).toEqual({
      code: 'control-plane/unavailable',
      message: 'socket did not appear',
    });
  });

  it('turning a feature on that the host does not implement reaches the renderer as control-plane/not-implemented', async () => {
    const client = makeClient({
      enableFeature: mock(async () => {
        throw new ControlPlaneNotImplementedError();
      }),
    });
    register(ipcMain, client, logger);

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.featureEnable)!(NULL_EVENT, { id: 'github' }),
    );

    expect(readIpcError(new Error((thrown as Error).message)).code).toBe(
      'control-plane/not-implemented',
    );
  });

  it('a credential value is never written to the log when saving it fails', async () => {
    const secret = 'ghp_SECRET_VALUE_123';
    const { logger: recording, lines } = makeRecordingLogger();
    const client = makeClient({
      setFeatureCredentials: mock(async () => {
        throw new Error('invalid argument');
      }),
      setupFeature: mock(async () => {
        throw new Error('invalid argument');
      }),
    });
    register(ipcMain, client, recording);

    await rejection(
      ipcMain.handlers.get(CHANNEL.featureSetCredentials)!(NULL_EVENT, {
        id: 'github',
        body: { pat: secret },
      }),
    );
    await rejection(
      ipcMain.handlers.get(CHANNEL.featureSetup)!(NULL_EVENT, {
        id: 'github',
        credentials: { pat: secret },
        settings: {},
      }),
    );

    expect(lines.length).toBe(2);
    expect(lines.join('\n')).not.toContain(secret);
  });

  it('featureEnable forwards confirmWrites only when the renderer sent exactly true', async () => {
    const client = makeClient({ enableFeature: mock(async () => youtubeDto) });
    register(ipcMain, client, logger);
    const enable = ipcMain.handlers.get(CHANNEL.featureEnable)!;

    await enable(NULL_EVENT, { id: 'youtube', confirmWrites: true });
    await enable(NULL_EVENT, { id: 'youtube', confirmWrites: 'true' });
    await enable(NULL_EVENT, { id: 'youtube' });

    expect(client.enableFeature.mock.calls).toEqual([
      ['youtube', true],
      ['youtube', false],
      ['youtube', false],
    ]);
  });

  it('enabling a write-capable feature without confirming reaches the renderer as control-plane/write-confirmation-required with the message', async () => {
    const onWriteGrantChanged = mock(() => {});
    const client = makeClient({
      enableFeature: mock(async () => {
        throw new ControlPlaneWriteConfirmationError(
          'Confirm the writes of youtube before enabling it',
        );
      }),
    });
    register(ipcMain, client, logger, { onWriteGrantChanged });

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.featureEnable)!(NULL_EVENT, {
        id: 'youtube',
        confirmWrites: false,
      }),
    );

    expect(readIpcError(new Error((thrown as Error).message))).toEqual({
      code: 'control-plane/write-confirmation-required',
      message: 'Confirm the writes of youtube before enabling it',
    });
    expect(onWriteGrantChanged).not.toHaveBeenCalled();
  });

  it('featureWriteGrant forwards the id, the grant and confirmWrites, and tells main the grants changed', async () => {
    const onWriteGrantChanged = mock(() => {});
    const client = makeClient();
    register(ipcMain, client, logger, { onWriteGrantChanged });
    const grant = ipcMain.handlers.get(CHANNEL.featureWriteGrant)!;

    const result = await grant(NULL_EVENT, { id: 'youtube', granted: true, confirmWrites: true });
    await grant(NULL_EVENT, { id: 'youtube', granted: false, confirmWrites: false });
    await grant(NULL_EVENT, { id: 'youtube', granted: 'yes', confirmWrites: 1 });

    expect(result).toEqual(youtubeDto);
    expect(client.setWriteGrant.mock.calls).toEqual([
      ['youtube', true, true],
      ['youtube', false, false],
      ['youtube', false, false],
    ]);
    expect(onWriteGrantChanged.mock.calls.length).toBe(3);
  });

  it("a write grant runs main's own check for that feature first and reaches the control plane only after it; a refused check grants nothing and reaches the renderer with its reason", async () => {
    const order: string[] = [];
    let refuse = false;
    const client = makeClient({
      setWriteGrant: mock(async (id: string, granted: boolean) => {
        order.push(`grant ${id} ${granted}`);
        return youtubeDto;
      }),
    });
    register(ipcMain, client, logger, {
      prepareWriteGrant: async (id) => {
        order.push(`prepare ${id}`);
        if (refuse) {
          throw new ScoutError(
            'youtube/scopes-missing',
            'Google did not grant youtube.force-ssl, so writes stay off.',
          );
        }
      },
    });
    const grant = ipcMain.handlers.get(CHANNEL.featureWriteGrant)!;

    await grant(NULL_EVENT, { id: 'youtube', granted: true, confirmWrites: true });
    refuse = true;
    const thrown = await rejection(
      grant(NULL_EVENT, { id: 'youtube', granted: true, confirmWrites: true }),
    );

    expect(order).toEqual(['prepare youtube', 'grant youtube true', 'prepare youtube']);
    expect(readIpcError(new Error((thrown as Error).message))).toEqual({
      code: 'youtube/scopes-missing',
      message: 'Google did not grant youtube.force-ssl, so writes stay off.',
    });
  });

  it('enabling a feature with its writes confirmed runs the same check first, and a refused check enables nothing', async () => {
    const client = makeClient();
    register(ipcMain, client, logger, {
      prepareWriteGrant: async () => {
        throw new ScoutError('youtube/sign-in-cancelled', 'The Google sign-in was cancelled.');
      },
    });

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.featureEnable)!(NULL_EVENT, {
        id: 'youtube',
        confirmWrites: true,
      }),
    );

    expect(readIpcError(new Error((thrown as Error).message)).code).toBe(
      'youtube/sign-in-cancelled',
    );
    expect(client.enableFeature).not.toHaveBeenCalled();
  });

  it("revoking a write grant revokes it on the control plane first, then runs main's own revoke step, then tells main the grants changed; a revoke step that fails is logged and the revoked grant is still returned", async () => {
    const order: string[] = [];
    const { logger: recording, lines } = makeRecordingLogger();
    let fail = false;
    const client = makeClient({
      setWriteGrant: mock(async (id: string, granted: boolean) => {
        order.push(`grant ${id} ${granted}`);
        return youtubeDto;
      }),
    });
    register(ipcMain, client, recording, {
      afterWriteRevoke: async (id) => {
        order.push(`after ${id}`);
        if (fail) throw new ScoutError('youtube/token-file-not-removed', 'still connected');
      },
      onWriteGrantChanged: () => order.push('changed'),
    });
    const grant = ipcMain.handlers.get(CHANNEL.featureWriteGrant)!;

    await grant(NULL_EVENT, { id: 'youtube', granted: false, confirmWrites: false });
    fail = true;
    const returned = await grant(NULL_EVENT, {
      id: 'youtube',
      granted: false,
      confirmWrites: false,
    });

    expect(order).toEqual([
      'grant youtube false',
      'after youtube',
      'changed',
      'grant youtube false',
      'after youtube',
      'changed',
    ]);
    expect(returned).toEqual(youtubeDto);
    expect(
      lines.some(
        (l) =>
          l.includes('features: revoke step failed') &&
          l.includes('youtube/token-file-not-removed'),
      ),
    ).toBe(true);
  });

  it('a write grant for an unknown feature id is refused before reaching the control plane', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.featureWriteGrant)!(NULL_EVENT, {
        id: 'youtube-publish',
        granted: true,
        confirmWrites: true,
      }),
    );

    expect((thrown as { code?: string }).code).toBe('features/invalid-id');
    expect(client.setWriteGrant).not.toHaveBeenCalled();
  });

  it('granting without confirming reaches the renderer as control-plane/write-confirmation-required and changes no grant', async () => {
    const onWriteGrantChanged = mock(() => {});
    const client = makeClient({
      setWriteGrant: mock(async () => {
        throw new ControlPlaneWriteConfirmationError(
          'Confirm the writes of youtube before granting them',
        );
      }),
    });
    register(ipcMain, client, logger, { onWriteGrantChanged });

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.featureWriteGrant)!(NULL_EVENT, {
        id: 'youtube',
        granted: true,
        confirmWrites: false,
      }),
    );

    expect(readIpcError(new Error((thrown as Error).message)).code).toBe(
      'control-plane/write-confirmation-required',
    );
    expect(onWriteGrantChanged).not.toHaveBeenCalled();
  });

  it('actionsList returns the pending actions as { actions }', async () => {
    register(ipcMain, makeClient(), logger);

    const result = await ipcMain.handlers.get(CHANNEL.actionsList)!(NULL_EVENT);

    expect(result).toEqual({ actions: [pendingUpload] });
  });

  it('approving and rejecting forward the action id and the reason, return the decided action and announce the change', async () => {
    const onActionsChanged = mock(() => {});
    const client = makeClient();
    register(ipcMain, client, logger, { onActionsChanged });

    const approved = await ipcMain.handlers.get(CHANNEL.actionsApprove)!(NULL_EVENT, {
      id: 'act-1',
    });
    const rejected = await ipcMain.handlers.get(CHANNEL.actionsReject)!(NULL_EVENT, {
      id: 'act-1',
      reason: 'Wrong thumbnail',
    });

    expect(client.approveAction.mock.calls).toEqual([['act-1', undefined]]);
    expect(client.rejectAction.mock.calls).toEqual([['act-1', 'Wrong thumbnail']]);
    expect([(approved as PendingActionDto).status, (rejected as PendingActionDto).status]).toEqual([
      'approved',
      'rejected',
    ]);
    expect(onActionsChanged.mock.calls.length).toBe(2);
  });

  it('an action decision without an action id is refused before reaching the control plane', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.actionsApprove)!(NULL_EVENT, { id: '' }),
    );

    expect((thrown as { code?: string }).code).toBe('actions/invalid-id');
    expect(client.approveAction).not.toHaveBeenCalled();
  });

  it('writesList forwards the limit and returns the entries', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const result = await ipcMain.handlers.get(CHANNEL.writesList)!(NULL_EVENT, { limit: 50 });

    expect(client.listWrites.mock.calls).toEqual([[50]]);
    expect(result).toEqual({ entries: [writeEntry] });
  });

  it('a control plane that is down while listing pending actions reaches the renderer as control-plane/unavailable', async () => {
    const client = makeClient({
      listPendingActions: mock(async () => {
        throw new ControlPlaneUnavailableError('socket reset');
      }),
    });
    register(ipcMain, client, logger);

    const thrown = await rejection(ipcMain.handlers.get(CHANNEL.actionsList)!(NULL_EVENT));

    expect(readIpcError(new Error((thrown as Error).message))).toEqual({
      code: 'control-plane/unavailable',
      message: 'socket reset',
    });
  });

  it('the returned unregister removes every handler', () => {
    const unregister = register(ipcMain, makeClient(), logger);

    expect(ipcMain.handlers.size).toBe(11);
    unregister();
    expect(ipcMain.handlers.size).toBe(0);
  });
});
