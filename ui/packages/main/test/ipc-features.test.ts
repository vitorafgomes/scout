import { describe, it, expect, beforeEach, mock } from 'bun:test';
import { CHANNEL, ProviderUnreachableError, type FeatureDto } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { registerFeaturesHandlers } from '../src/ipc/features.js';
import type { WorkerHttpClient } from '../src/worker/http-client.js';
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

interface StubClient {
  get: ReturnType<typeof mock>;
  post: ReturnType<typeof mock>;
  put: ReturnType<typeof mock>;
  delete: ReturnType<typeof mock>;
}

function makeClient(overrides: Partial<StubClient> = {}): StubClient {
  return {
    get: mock(async () => undefined),
    post: mock(async () => undefined),
    put: mock(async () => undefined),
    delete: mock(async () => undefined),
    ...overrides,
  };
}

describe('registerFeaturesHandlers', () => {
  let ipcMain: FakeIpcMain;
  let logger: Logger;

  beforeEach(() => {
    ipcMain = makeFakeIpcMain();
    logger = noopLogger();
  });

  describe('case 1: featuresList wraps GET /features into FeaturesListResponse', () => {
    it('returns { features: [...] }', async () => {
      const client = makeClient({ get: mock(async () => [githubDto]) });
      registerFeaturesHandlers(ipcMain, client as unknown as WorkerHttpClient, logger);

      const handler = ipcMain.handlers.get(CHANNEL.featuresList)!;
      const result = await handler(NULL_EVENT);

      expect(client.get).toHaveBeenCalledWith('/features');
      expect(result).toEqual({ features: [githubDto] });
    });
  });

  describe('case 2: featureSetCredentials posts to /features/:id/credentials with body', () => {
    it('forwards the body verbatim', async () => {
      const client = makeClient({ post: mock(async () => githubDto) });
      registerFeaturesHandlers(ipcMain, client as unknown as WorkerHttpClient, logger);

      const handler = ipcMain.handlers.get(CHANNEL.featureSetCredentials)!;
      const result = await handler(NULL_EVENT, { id: 'github', body: { token: 'abc' } });

      expect(client.post).toHaveBeenCalledWith('/features/github/credentials', { token: 'abc' });
      expect(result).toEqual(githubDto);
    });
  });

  describe('case 2b: featureSetup posts to /features/:id/setup with credentials and settings', () => {
    it('forwards both sections in the request body', async () => {
      const client = makeClient({ post: mock(async () => githubDto) });
      registerFeaturesHandlers(ipcMain, client as unknown as WorkerHttpClient, logger);

      const handler = ipcMain.handlers.get(CHANNEL.featureSetup)!;
      const result = await handler(NULL_EVENT, {
        id: 'github',
        credentials: { pat: 'ghp_xxx' },
        settings: { default_repo_url: 'https://github.com/acme/repo' },
      });

      expect(client.post).toHaveBeenCalledWith('/features/github/setup', {
        credentials: { pat: 'ghp_xxx' },
        settings: { default_repo_url: 'https://github.com/acme/repo' },
      });
      expect(result).toEqual(githubDto);
    });
  });

  describe('case 3: featureDeleteCredentials issues DELETE /features/:id/credentials', () => {
    it('delegates to client.delete', async () => {
      const client = makeClient({ delete: mock(async () => githubDto) });
      registerFeaturesHandlers(ipcMain, client as unknown as WorkerHttpClient, logger);

      const handler = ipcMain.handlers.get(CHANNEL.featureDeleteCredentials)!;
      const result = await handler(NULL_EVENT, { id: 'github' });

      expect(client.delete).toHaveBeenCalledWith('/features/github/credentials');
      expect(result).toEqual(githubDto);
    });
  });

  describe('case 4: featureEnable / featureDisable post to the correct path', () => {
    it('enable posts to /features/:id/enable', async () => {
      const client = makeClient({ post: mock(async () => githubDto) });
      registerFeaturesHandlers(ipcMain, client as unknown as WorkerHttpClient, logger);

      const handler = ipcMain.handlers.get(CHANNEL.featureEnable)!;
      await handler(NULL_EVENT, { id: 'github' });

      expect(client.post).toHaveBeenCalledWith('/features/github/enable');
    });

    it('disable posts to /features/:id/disable', async () => {
      const client = makeClient({ post: mock(async () => githubDto) });
      registerFeaturesHandlers(ipcMain, client as unknown as WorkerHttpClient, logger);

      const handler = ipcMain.handlers.get(CHANNEL.featureDisable)!;
      await handler(NULL_EVENT, { id: 'github' });

      expect(client.post).toHaveBeenCalledWith('/features/github/disable');
    });
  });

  describe('case 5: unknown feature id is rejected with features/invalid-id', () => {
    it('throws before reaching the worker', async () => {
      const client = makeClient();
      registerFeaturesHandlers(ipcMain, client as unknown as WorkerHttpClient, logger);

      const handler = ipcMain.handlers.get(CHANNEL.featureEnable)!;
      let thrown: unknown = null;
      try {
        await handler(NULL_EVENT, { id: 'unknown-thing' });
      } catch (err: unknown) {
        thrown = err;
      }
      expect(thrown).not.toBeNull();
      expect((thrown as { code?: string }).code).toBe('features/invalid-id');
      expect(client.post).not.toHaveBeenCalled();
    });
  });

  describe('case 6: worker error code propagates through serialization', () => {
    it('preserves the worker error code on the rejection', async () => {
      const workerErr = new ProviderUnreachableError('worker-down');
      const client = makeClient({
        get: mock(async () => {
          throw workerErr;
        }),
      });
      registerFeaturesHandlers(ipcMain, client as unknown as WorkerHttpClient, logger);

      const handler = ipcMain.handlers.get(CHANNEL.featuresList)!;
      let thrown: unknown = null;
      try {
        await handler(NULL_EVENT);
      } catch (err: unknown) {
        thrown = err;
      }
      expect(thrown).not.toBeNull();
      expect((thrown as { code?: string }).code).toBe('providers/unreachable');
    });
  });

  describe('case 7: returned unregister removes every handler', () => {
    it('clears all channels', () => {
      const client = makeClient();
      const unregister = registerFeaturesHandlers(
        ipcMain,
        client as unknown as WorkerHttpClient,
        logger,
      );

      expect(ipcMain.handlers.size).toBe(6);
      unregister();
      expect(ipcMain.handlers.size).toBe(0);
    });
  });
});
