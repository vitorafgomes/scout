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
import { MAIN_EVENT, SENDERS, SENDER_HOOKS, WINDOWS, fromMainFrame } from './sender-fakes.js';
import { restartPath } from './restart-fakes.js';
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
  scopeMatrix: [
    {
      scope: 'videos.insert',
      summary: 'Public, private, unlisted or scheduled',
      actions: ['Upload a video'],
      access: 'irreversible',
    },
  ],
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
  signedAs: null,
  controlStatus: null,
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
  hooks: Partial<FeaturesHooks> = {},
): () => void {
  return registerFeaturesHandlers(
    ipcMain,
    async () => client as unknown as FeaturesClient,
    logger,
    { ...SENDER_HOOKS, ...hooks },
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

    const result = await ipcMain.handlers.get(CHANNEL.featuresList)!(MAIN_EVENT);

    expect(result).toEqual({ features: [githubDto] });
  });

  it('featureSetCredentials forwards the id and the credential body', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const result = await ipcMain.handlers.get(CHANNEL.featureSetCredentials)!(MAIN_EVENT, {
      id: 'github',
      body: { pat: 'abc' },
    });

    expect(client.setFeatureCredentials).toHaveBeenCalledWith('github', { pat: 'abc' });
    expect(result).toEqual(githubDto);
  });

  it('featureSetup forwards credentials and settings', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const result = await ipcMain.handlers.get(CHANNEL.featureSetup)!(MAIN_EVENT, {
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

    await ipcMain.handlers.get(CHANNEL.featureDeleteCredentials)!(MAIN_EVENT, { id: 'github' });
    await ipcMain.handlers.get(CHANNEL.featureEnable)!(MAIN_EVENT, {
      id: 'kubernetes',
      confirmWrites: false,
    });
    await ipcMain.handlers.get(CHANNEL.featureDisable)!(MAIN_EVENT, { id: 'github' });

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
        restart = restartWorkerIfRunning(restartPath(worker), logger);
      },
    });
    const deleteCredentials = ipcMain.handlers.get(CHANNEL.featureDeleteCredentials)!;

    await deleteCredentials(MAIN_EVENT, { id: 'github' });
    await restart;
    const afterReadOnly = [...calls];
    await deleteCredentials(MAIN_EVENT, { id: 'youtube' });
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
        restart = restartWorkerIfRunning(restartPath(worker), logger);
      },
    });

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.featureEnable)!(MAIN_EVENT, {
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
      ipcMain.handlers.get(CHANNEL.featureEnable)!(MAIN_EVENT, { id: 'unknown-thing' }),
    );

    expect((thrown as { code?: string }).code).toBe('features/invalid-id');
    expect(client.enableFeature).not.toHaveBeenCalled();
  });

  it('the youtube feature id is accepted and reaches the control plane', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.featureDisable)!(MAIN_EVENT, { id: 'youtube' }),
    );

    expect(thrown).toBeNull();
    expect(client.disableFeature).toHaveBeenCalledWith('youtube');
  });

  it('a control plane that is not running reaches the renderer as control-plane/unavailable', async () => {
    registerFeaturesHandlers(
      ipcMain,
      () => Promise.reject(new ControlPlaneUnavailableError('socket did not appear')),
      logger,
      SENDER_HOOKS,
    );

    const thrown = await rejection(ipcMain.handlers.get(CHANNEL.featuresList)!(MAIN_EVENT));

    expect(readIpcError(new Error((thrown as Error).message))).toEqual({
      code: 'control-plane/unavailable',
      message: 'The Scout control plane did not answer.',
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
      ipcMain.handlers.get(CHANNEL.featureEnable)!(MAIN_EVENT, { id: 'github' }),
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
      ipcMain.handlers.get(CHANNEL.featureSetCredentials)!(MAIN_EVENT, {
        id: 'github',
        body: { pat: secret },
      }),
    );
    await rejection(
      ipcMain.handlers.get(CHANNEL.featureSetup)!(MAIN_EVENT, {
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

    await enable(MAIN_EVENT, { id: 'youtube', confirmWrites: true });
    await enable(MAIN_EVENT, { id: 'youtube', confirmWrites: 'true' });
    await enable(MAIN_EVENT, { id: 'youtube' });

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
      ipcMain.handlers.get(CHANNEL.featureEnable)!(MAIN_EVENT, {
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

    const result = await grant(MAIN_EVENT, { id: 'youtube', granted: true, confirmWrites: true });
    await grant(MAIN_EVENT, { id: 'youtube', granted: false, confirmWrites: false });
    await grant(MAIN_EVENT, { id: 'youtube', granted: 'yes', confirmWrites: 1 });

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

    await grant(MAIN_EVENT, { id: 'youtube', granted: true, confirmWrites: true });
    refuse = true;
    const thrown = await rejection(
      grant(MAIN_EVENT, { id: 'youtube', granted: true, confirmWrites: true }),
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
      ipcMain.handlers.get(CHANNEL.featureEnable)!(MAIN_EVENT, {
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

    await grant(MAIN_EVENT, { id: 'youtube', granted: false, confirmWrites: false });
    fail = true;
    const returned = await grant(MAIN_EVENT, {
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
      ipcMain.handlers.get(CHANNEL.featureWriteGrant)!(MAIN_EVENT, {
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
      ipcMain.handlers.get(CHANNEL.featureWriteGrant)!(MAIN_EVENT, {
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

    const result = await ipcMain.handlers.get(CHANNEL.actionsList)!(MAIN_EVENT);

    expect(result).toEqual({ actions: [pendingUpload] });
  });

  it('approving and rejecting forward the action id and the reason, return the decided action and announce the change', async () => {
    const onActionsChanged = mock(() => {});
    const client = makeClient();
    register(ipcMain, client, logger, { onActionsChanged });

    const approved = await ipcMain.handlers.get(CHANNEL.actionsApprove)!(MAIN_EVENT, {
      id: 'act-1',
    });
    const rejected = await ipcMain.handlers.get(CHANNEL.actionsReject)!(MAIN_EVENT, {
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
      ipcMain.handlers.get(CHANNEL.actionsApprove)!(MAIN_EVENT, { id: '' }),
    );

    expect((thrown as { code?: string }).code).toBe('actions/invalid-id');
    expect(client.approveAction).not.toHaveBeenCalled();
  });

  it('writesList forwards the limit and returns the entries', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);

    const result = await ipcMain.handlers.get(CHANNEL.writesList)!(MAIN_EVENT, { limit: 50 });

    expect(client.listWrites.mock.calls).toEqual([[50]]);
    expect(result).toEqual({ entries: [writeEntry] });
  });

  it('a control plane that is down while listing pending actions reaches the renderer as one sentence, and the spawn failure goes to the log only', async () => {
    const raw =
      'The control-plane host did not start: spawn /tmp/scout-e2e/no-control-plane ENOENT';
    const client = makeClient({
      listPendingActions: mock(async () => {
        throw new ControlPlaneUnavailableError(raw);
      }),
    });
    const { logger: recording, lines } = makeRecordingLogger();
    register(ipcMain, client, recording);

    const thrown = await rejection(ipcMain.handlers.get(CHANNEL.actionsList)!(MAIN_EVENT));

    expect(readIpcError(new Error((thrown as Error).message))).toEqual({
      code: 'control-plane/unavailable',
      message: 'The Scout control plane did not answer.',
    });
    expect((thrown as Error).message).not.toMatch(/\/tmp|spawn|ENOENT/);
    expect(
      lines.filter((line) => line.includes('actions: list failed') && line.includes(raw)),
    ).toHaveLength(1);
  });

  it('a write grant asked from a window other than the main window, such as quick ask, is refused and grants nothing', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);
    const grant = ipcMain.handlers.get(CHANNEL.featureWriteGrant)!;

    const refused = await rejection(
      grant(fromMainFrame(WINDOWS['quick-ask']), {
        id: 'youtube',
        granted: true,
        confirmWrites: true,
      }),
    );

    expect(refused).toBeInstanceOf(Error);
    expect(client.setWriteGrant).not.toHaveBeenCalled();
  });

  it('turning a feature on with its writes confirmed from a window other than the main window, such as quick ask, is refused and enables nothing', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);
    const enable = ipcMain.handlers.get(CHANNEL.featureEnable)!;

    const refused = await rejection(
      enable(fromMainFrame(WINDOWS['quick-ask']), {
        id: 'youtube',
        confirmWrites: true,
      }),
    );

    expect(refused).toBeInstanceOf(Error);
    expect(client.enableFeature).not.toHaveBeenCalled();
  });

  it('a write grant asked from the main window reaches the control plane', async () => {
    const client = makeClient();
    register(ipcMain, client, logger);
    const grant = ipcMain.handlers.get(CHANNEL.featureWriteGrant)!;

    await grant(MAIN_EVENT, {
      id: 'youtube',
      granted: true,
      confirmWrites: true,
    });

    expect(client.setWriteGrant.mock.calls).toEqual([['youtube', true, true]]);
  });

  it('a refusal from another window says writes are allowed only from the Scout window and runs no grant check', async () => {
    const prepareWriteGrant = mock(async () => undefined);
    register(ipcMain, makeClient(), logger, { prepareWriteGrant });

    const thrown = await rejection(
      ipcMain.handlers.get(CHANNEL.featureWriteGrant)!(fromMainFrame(WINDOWS.operator), {
        id: 'youtube',
        granted: true,
        confirmWrites: true,
      }),
    );

    expect(readIpcError(new Error((thrown as Error).message))).toEqual({
      code: 'features/not-main-window',
      message: 'Writes can be allowed only from the Scout window.',
    });
    expect(prepareWriteGrant).not.toHaveBeenCalled();
  });

  const GRANT_REFUSAL = {
    code: 'features/not-main-window',
    message: 'Writes can be allowed only from the Scout window.',
  };
  const DECIDE_REFUSAL = {
    code: 'actions/not-approval-window',
    message:
      'Actions can be approved or rejected only from the Scout, quick-ask or operator window.',
  };
  const CREDENTIALS_REFUSAL = {
    code: 'features/not-main-window',
    message: 'Credentials can be entered only in the Scout window.',
  };
  const guarded = [
    {
      rule: 'credentials are entered only in the main window',
      call: "saving a feature's credentials",
      channel: CHANNEL.featureSetCredentials,
      request: { id: 'github', body: { pat: 'ghp_never_logged' } },
      reaches: 'setFeatureCredentials',
      sent: ['github', { pat: 'ghp_never_logged' }],
      allowed: ['main'],
      refusal: CREDENTIALS_REFUSAL,
    },
    {
      rule: 'credentials are entered only in the main window',
      call: 'setting a feature up',
      channel: CHANNEL.featureSetup,
      request: { id: 'github', credentials: { pat: 'ghp_never_logged' }, settings: {} },
      reaches: 'setupFeature',
      sent: ['github', { pat: 'ghp_never_logged' }, {}],
      allowed: ['main'],
      refusal: CREDENTIALS_REFUSAL,
    },
    {
      rule: 'only the main window grants',
      call: 'granting writes',
      channel: CHANNEL.featureWriteGrant,
      request: { id: 'youtube', granted: true, confirmWrites: true },
      reaches: 'setWriteGrant',
      sent: ['youtube', true, true],
      allowed: ['main'],
      refusal: GRANT_REFUSAL,
    },
    {
      rule: 'only the main window grants',
      call: 'revoking writes',
      channel: CHANNEL.featureWriteGrant,
      request: { id: 'youtube', granted: false, confirmWrites: false },
      reaches: 'setWriteGrant',
      sent: ['youtube', false, false],
      allowed: ['main'],
      refusal: GRANT_REFUSAL,
    },
    {
      rule: 'only the main window grants',
      call: 'turning a feature on with its writes confirmed',
      channel: CHANNEL.featureEnable,
      request: { id: 'youtube', confirmWrites: true },
      reaches: 'enableFeature',
      sent: ['youtube', true],
      allowed: ['main'],
      refusal: GRANT_REFUSAL,
    },
    {
      rule: 'only the main window grants',
      call: 'turning a feature on',
      channel: CHANNEL.featureEnable,
      request: { id: 'youtube' },
      reaches: 'enableFeature',
      sent: ['youtube', false],
      allowed: ['main'],
      refusal: GRANT_REFUSAL,
    },
    {
      rule: 'only the main window grants',
      call: 'turning a feature off',
      channel: CHANNEL.featureDisable,
      request: { id: 'youtube' },
      reaches: 'disableFeature',
      sent: ['youtube'],
      allowed: ['main'],
      refusal: GRANT_REFUSAL,
    },
    {
      rule: 'only the main window grants',
      call: "deleting a feature's credentials",
      channel: CHANNEL.featureDeleteCredentials,
      request: { id: 'youtube' },
      reaches: 'deleteFeatureCredentials',
      sent: ['youtube'],
      allowed: ['main'],
      refusal: GRANT_REFUSAL,
    },
    {
      rule: 'only card-showing windows decide',
      call: 'approving an action',
      channel: CHANNEL.actionsApprove,
      request: { id: 'act-1', reason: 'looks right' },
      reaches: 'approveAction',
      sent: ['act-1', 'looks right'],
      allowed: ['main', 'quick-ask', 'operator'],
      refusal: DECIDE_REFUSAL,
    },
    {
      rule: 'only card-showing windows decide',
      call: 'rejecting an action',
      channel: CHANNEL.actionsReject,
      request: { id: 'act-1', reason: 'looks wrong' },
      reaches: 'rejectAction',
      sent: ['act-1', 'looks wrong'],
      allowed: ['main', 'quick-ask', 'operator'],
      refusal: DECIDE_REFUSAL,
    },
  ] as const;
  for (const { rule, call, channel, request, reaches, sent, allowed, refusal } of guarded) {
    for (const [from, event, kind] of SENDERS) {
      if ((allowed as readonly string[]).includes(kind)) {
        it(`${channel}, ${rule}: ${call} from ${from} reaches the control plane and logs no refusal`, async () => {
          const { logger: recording, lines } = makeRecordingLogger();
          const client = makeClient();
          register(ipcMain, client, recording);

          const answer = await ipcMain.handlers.get(channel)!(event, request);

          expect(client[reaches].mock.calls).toEqual([[...sent]]);
          expect(answer).toEqual(await client[reaches].mock.results[0]!.value);
          expect(lines.filter((line) => line.includes('refused'))).toEqual([]);
        });
      } else {
        it(`${channel}, ${rule}: ${call} from ${from} is refused with ${refusal.code}, reaches nothing, and is logged at warn with the channel and the sender kind, never the payload`, async () => {
          const { logger: recording, lines } = makeRecordingLogger();
          const client = makeClient();
          const prepareWriteGrant = mock(async () => undefined);
          register(ipcMain, client, recording, { prepareWriteGrant });

          const thrown = await rejection(ipcMain.handlers.get(channel)!(event, request));

          expect(thrown).toBeInstanceOf(Error);
          expect(readIpcError(new Error((thrown as Error).message))).toEqual(refusal);
          expect(client[reaches]).not.toHaveBeenCalled();
          expect(prepareWriteGrant).not.toHaveBeenCalled();
          expect(lines).toEqual([
            `warn ["ipc: call refused for its sender",{"channel":"${channel}","sender":"${kind}"}]`,
          ]);
        });
      }
    }
  }

  for (const [channel, request, failing] of [
    [
      CHANNEL.featureWriteGrant,
      { id: 'youtube', granted: true, confirmWrites: true },
      'setWriteGrant',
    ],
    [CHANNEL.featureEnable, { id: 'youtube', confirmWrites: true }, 'enableFeature'],
  ] as const) {
    it(`${channel}: a grant the control plane refuses whose undo also fails keeps the control plane's error for the renderer and logs the failed undo with its code`, async () => {
      const { logger: recording, lines } = makeRecordingLogger();
      const client = makeClient({
        [failing]: mock(async () => {
          throw new ControlPlaneUnavailableError('socket reset');
        }),
      });
      register(ipcMain, client, recording, {
        prepareWriteGrant: async () => undefined,
        afterWriteRevoke: async () => {
          throw new ScoutError('youtube/revoke-failed', 'Google could not be reached.');
        },
      });

      const thrown = await rejection(ipcMain.handlers.get(channel)!(MAIN_EVENT, request));

      expect(readIpcError(new Error((thrown as Error).message)).code).toBe(
        'control-plane/unavailable',
      );
      expect(lines).toContain(
        'warn ["features: undoing the grant check failed",{"id":"youtube","code":"youtube/revoke-failed"}]',
      );
    });
  }

  it('the returned unregister removes every handler', () => {
    const unregister = register(ipcMain, makeClient(), logger);

    expect(ipcMain.handlers.size).toBe(11);
    unregister();
    expect(ipcMain.handlers.size).toBe(0);
  });
});
