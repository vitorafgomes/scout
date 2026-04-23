import { describe, it, expect, beforeEach, mock } from 'bun:test';
import { registerWorkerHandlers } from '../src/ipc/worker.js';
import type { Supervisor } from '../src/worker/supervisor.js';
import { CHANNEL, WorkerBinaryMissingError } from '@scout/shared';
import type { WorkerEvent, WorkerStatus } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import type { Logger } from '../src/util/logger.js';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => Promise<unknown> | unknown;

interface FakeIpcMain extends Pick<IpcMain, 'handle' | 'removeHandler'> {
  handlers: Map<string, IpcHandler>;
}

function makeFakeIpcMain(): FakeIpcMain {
  const handlers = new Map<string, IpcHandler>();
  return {
    handlers,
    handle(channel: string, handler: IpcHandler) {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string) {
      handlers.delete(channel);
    },
  };
}

function makeNullLogger(): Logger {
  return {
    debug() {},
    info() {},
    warn() {},
    error() {},
    child() {
      return makeNullLogger();
    },
  };
}

const IDLE_STATUS: WorkerStatus = { state: 'idle', restarts: 0 };
const RUNNING_STATUS: WorkerStatus = {
  state: 'running',
  pid: 99,
  restarts: 0,
  startedAt: '2026-04-23T10:00:00.000Z',
};

function makeSupervisor(overrides: Partial<Supervisor> = {}): Supervisor {
  const _listeners = new Set<(e: WorkerEvent) => void>();
  return {
    status: IDLE_STATUS,
    start: mock(async () => ({ pid: 99 })),
    stop: mock(async () => ({ exitCode: 0 })),
    onEvent(listener) {
      _listeners.add(listener);
      return () => _listeners.delete(listener);
    },
    getSnapshot: mock(() => []),
    ...overrides,
  };
}

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('registerWorkerHandlers', () => {
  let ipcMain: FakeIpcMain;
  let logger: Logger;

  beforeEach(() => {
    ipcMain = makeFakeIpcMain();
    logger = makeNullLogger();
  });

  describe('workerStart channel', () => {
    it('registers handler for workerStart', () => {
      const supervisor = makeSupervisor();
      registerWorkerHandlers(ipcMain, supervisor, logger);
      expect(ipcMain.handlers.has(CHANNEL.workerStart)).toBe(true);
    });

    it('calls supervisor.start() and returns pid', async () => {
      const supervisor = makeSupervisor();
      registerWorkerHandlers(ipcMain, supervisor, logger);

      const handler = ipcMain.handlers.get(CHANNEL.workerStart)!;
      const result = await handler(NULL_EVENT);
      expect(result).toEqual({ pid: 99 });
      expect(supervisor.start).toHaveBeenCalledTimes(1);
    });

    it('propagates error code when supervisor.start() rejects', async () => {
      const supervisor = makeSupervisor({
        start: mock(async () => {
          throw new WorkerBinaryMissingError('binary missing');
        }),
      });
      registerWorkerHandlers(ipcMain, supervisor, logger);

      const handler = ipcMain.handlers.get(CHANNEL.workerStart)!;
      let caught: (Error & { code?: string }) | undefined;
      try {
        await handler(NULL_EVENT);
      } catch (e: unknown) {
        caught = e as Error & { code?: string };
      }
      expect(caught).toBeDefined();
      expect(caught?.code).toBe('worker/binary-missing');
    });
  });

  describe('workerStop channel', () => {
    it('registers handler for workerStop', () => {
      const supervisor = makeSupervisor();
      registerWorkerHandlers(ipcMain, supervisor, logger);
      expect(ipcMain.handlers.has(CHANNEL.workerStop)).toBe(true);
    });

    it('calls supervisor.stop() and returns exitCode', async () => {
      const supervisor = makeSupervisor();
      registerWorkerHandlers(ipcMain, supervisor, logger);

      const handler = ipcMain.handlers.get(CHANNEL.workerStop)!;
      const result = await handler(NULL_EVENT);
      expect(result).toEqual({ exitCode: 0 });
      expect(supervisor.stop).toHaveBeenCalledTimes(1);
    });
  });

  describe('workerStatus channel', () => {
    it('registers handler for workerStatus', () => {
      const supervisor = makeSupervisor();
      registerWorkerHandlers(ipcMain, supervisor, logger);
      expect(ipcMain.handlers.has(CHANNEL.workerStatus)).toBe(true);
    });

    it('returns a snapshot of supervisor.status', () => {
      const supervisor = makeSupervisor();
      Object.defineProperty(supervisor, 'status', { get: () => RUNNING_STATUS });
      registerWorkerHandlers(ipcMain, supervisor, logger);

      const handler = ipcMain.handlers.get(CHANNEL.workerStatus)!;
      const result = handler(NULL_EVENT);
      expect(result).toEqual(RUNNING_STATUS);
    });
  });

  describe('cleanup', () => {
    it('unregisters all three channels on cleanup', () => {
      const supervisor = makeSupervisor();
      const cleanup = registerWorkerHandlers(ipcMain, supervisor, logger);

      expect(ipcMain.handlers.has(CHANNEL.workerStart)).toBe(true);
      expect(ipcMain.handlers.has(CHANNEL.workerStop)).toBe(true);
      expect(ipcMain.handlers.has(CHANNEL.workerStatus)).toBe(true);

      cleanup();

      expect(ipcMain.handlers.has(CHANNEL.workerStart)).toBe(false);
      expect(ipcMain.handlers.has(CHANNEL.workerStop)).toBe(false);
      expect(ipcMain.handlers.has(CHANNEL.workerStatus)).toBe(false);
    });

    it('calling cleanup twice does not throw', () => {
      const supervisor = makeSupervisor();
      const cleanup = registerWorkerHandlers(ipcMain, supervisor, logger);
      cleanup();
      expect(() => cleanup()).not.toThrow();
    });
  });
});
