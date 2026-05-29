import { describe, it, expect, beforeEach } from 'bun:test';
import { CHANNEL } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { registerProvidersHandlers } from '../src/ipc/providers.js';
import { WorkerHttpClient } from '../src/worker/http-client.js';
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
  const logger: Logger = { debug: fn, info: fn, warn: fn, error: fn, child: () => logger };
  return logger;
}

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

describe('registerProvidersHandlers', () => {
  let ipcMain: FakeIpcMain;
  let logger: Logger;

  beforeEach(() => {
    ipcMain = makeFakeIpcMain();
    logger = noopLogger();
  });

  describe('null http endpoint surfaces worker/http-unavailable', () => {
    it('providersList rejects with code worker/http-unavailable when endpoint is null', async () => {
      const nullEndpointClient = new WorkerHttpClient({
        getEndpoint: () => null,
        fetch: globalThis.fetch,
        logger,
      });
      registerProvidersHandlers(ipcMain, nullEndpointClient, logger);

      const handler = ipcMain.handlers.get(CHANNEL.providersList)!;
      let thrown: unknown = null;
      try {
        await handler(NULL_EVENT);
      } catch (err) {
        thrown = err;
      }

      expect(thrown).not.toBeNull();
      expect((thrown as { code?: string }).code).toBe('worker/http-unavailable');
    });

    it('providerStart rejects with code worker/http-unavailable when endpoint is null', async () => {
      const nullEndpointClient = new WorkerHttpClient({
        getEndpoint: () => null,
        fetch: globalThis.fetch,
        logger,
      });
      registerProvidersHandlers(ipcMain, nullEndpointClient, logger);

      const handler = ipcMain.handlers.get(CHANNEL.providerStart)!;
      let thrown: unknown = null;
      try {
        await handler(NULL_EVENT, { id: 'ollama' });
      } catch (err) {
        thrown = err;
      }

      expect(thrown).not.toBeNull();
      expect((thrown as { code?: string }).code).toBe('worker/http-unavailable');
    });
  });

  describe('unregister removes all handlers', () => {
    it('clears all 6 channels', () => {
      const client = new WorkerHttpClient({
        getEndpoint: () => null,
        fetch: globalThis.fetch,
        logger,
      });
      const unregister = registerProvidersHandlers(ipcMain, client, logger);

      expect(ipcMain.handlers.size).toBe(6);
      unregister();
      expect(ipcMain.handlers.size).toBe(0);
    });
  });
});
