import { describe, it, expect } from 'bun:test';
import { registerDetectHandler } from '../src/ipc/detect.js';
import type { DetectDeps } from '../src/detect/types.js';
import type { Logger } from '../src/util/logger.js';
import type { DetectedClient } from '@scout/shared';
import { CHANNEL } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

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

const THREE_CLIENTS: readonly DetectedClient[] = [
  { kind: 'discord', installed: true, method: 'desktop-file' },
  { kind: 'slack', installed: false, method: 'none' },
  { kind: 'teams', installed: false, method: 'none' },
];

function makeDeps(platform: NodeJS.Platform = 'linux'): DetectDeps {
  return {
    exists: () => Promise.resolve(false),
    exec: () => Promise.resolve({ stdout: '', stderr: '', exitCode: 1 }),
    readFile: () => Promise.resolve(''),
    env: () => undefined,
    homedir: () => '/home/test',
    platform: () => platform,
  };
}

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

describe('registerDetectHandler', () => {
  it('invoke path returns { clients } with three entries', async () => {
    const ipcMain = makeFakeIpcMain();
    registerDetectHandler(ipcMain, makeDeps(), makeNullLogger());

    const handler = ipcMain.handlers.get(CHANNEL.detectClients);
    expect(handler).toBeDefined();

    const result = (await handler?.(null as unknown as IpcMainInvokeEvent)) as {
      clients: readonly DetectedClient[];
    };
    expect(result.clients).toHaveLength(3);
    expect(result.clients.map((c) => c.kind)).toEqual(['discord', 'slack', 'teams']);
  });

  it('handler re-throws serialised error when detectAll rejects', async () => {
    const ipcMain = makeFakeIpcMain();
    const failingDeps: DetectDeps = {
      ...makeDeps(),
      platform: () => {
        throw new Error('platform probe failed');
      },
    };

    registerDetectHandler(ipcMain, failingDeps, makeNullLogger());

    const handler = ipcMain.handlers.get(CHANNEL.detectClients);
    let threw = false;
    let caughtErr: Error | undefined;
    try {
      await handler?.(null as unknown as IpcMainInvokeEvent);
    } catch (err: unknown) {
      threw = true;
      if (err instanceof Error) caughtErr = err;
    }

    expect(threw).toBe(true);
    expect(caughtErr).toBeDefined();
    expect(caughtErr?.message).toContain('platform probe failed');
  });

  it('unregister() removes the handler from ipcMain', () => {
    const ipcMain = makeFakeIpcMain();
    const unregister = registerDetectHandler(ipcMain, makeDeps(), makeNullLogger());

    expect(ipcMain.handlers.has(CHANNEL.detectClients)).toBe(true);
    unregister();
    expect(ipcMain.handlers.has(CHANNEL.detectClients)).toBe(false);
  });

  it('registered channel name matches CHANNEL.detectClients constant', () => {
    const ipcMain = makeFakeIpcMain();
    registerDetectHandler(ipcMain, makeDeps(), makeNullLogger());

    expect(ipcMain.handlers.has('scout:detect.clients')).toBe(true);
  });

  it('three returned clients have distinct kinds', async () => {
    const ipcMain = makeFakeIpcMain();
    registerDetectHandler(ipcMain, makeDeps(), makeNullLogger());

    const handler = ipcMain.handlers.get(CHANNEL.detectClients);
    const result = (await handler?.(null as unknown as IpcMainInvokeEvent)) as {
      clients: readonly DetectedClient[];
    };

    const kinds = result.clients.map((c) => c.kind);
    const unique = new Set(kinds);
    expect(unique.size).toBe(3);

    expect(THREE_CLIENTS[0]?.kind).toBe('discord');
  });
});
