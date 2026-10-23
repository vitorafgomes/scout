import { describe, it, expect, beforeEach, mock } from 'bun:test';
import {
  CHANNEL,
  ControlPlaneNotImplementedError,
  ControlPlaneUnavailableError,
  readIpcError,
  type FeatureDto,
} from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { registerFeaturesHandlers } from '../src/ipc/features.js';
import type { FeaturesClient } from '../src/ipc/features.js';
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
    ...overrides,
  };
}

function register(ipcMain: FakeIpcMain, client: StubClient, logger: Logger): () => void {
  return registerFeaturesHandlers(ipcMain, async () => client as unknown as FeaturesClient, logger);
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
    await ipcMain.handlers.get(CHANNEL.featureEnable)!(NULL_EVENT, { id: 'kubernetes' });
    await ipcMain.handlers.get(CHANNEL.featureDisable)!(NULL_EVENT, { id: 'github' });

    expect(client.deleteFeatureCredentials).toHaveBeenCalledWith('github');
    expect(client.enableFeature).toHaveBeenCalledWith('kubernetes');
    expect(client.disableFeature).toHaveBeenCalledWith('github');
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

  it('the returned unregister removes every handler', () => {
    const unregister = register(ipcMain, makeClient(), logger);

    expect(ipcMain.handlers.size).toBe(6);
    unregister();
    expect(ipcMain.handlers.size).toBe(0);
  });
});
