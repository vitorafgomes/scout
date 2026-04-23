import { describe, it, expect, beforeEach } from 'bun:test';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { registerAppHandlers } from '../src/ipc/app.js';
import { createLogger } from '../src/util/logger.js';

const logger = createLogger('test', { sink: () => {} });

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

function invoke(ipc: FakeIpcMain, channel: string, req?: unknown): Promise<unknown> {
  const fn = ipc.handlers.get(channel);
  if (!fn) throw new Error(`No handler for ${channel}`);
  return fn(null as unknown as IpcMainInvokeEvent, req);
}

function makeShell() {
  const calls: string[] = [];
  return {
    calls,
    openExternal: async (url: string) => {
      calls.push(url);
    },
  };
}

function makeApp(version = '1.2.3') {
  let quit = false;
  return {
    getVersion: () => version,
    quit: () => {
      quit = true;
    },
    wasQuitCalled: () => quit,
  };
}

describe('registerAppHandlers — appOpenExternal', () => {
  let ipc: FakeIpcMain;
  let shell: ReturnType<typeof makeShell>;
  let app: ReturnType<typeof makeApp>;

  beforeEach(() => {
    ipc = makeFakeIpcMain();
    shell = makeShell();
    app = makeApp();
    registerAppHandlers(ipc, shell, app, logger);
  });

  it('allows https://discord.com URLs and calls shell.openExternal', async () => {
    await invoke(ipc, 'scout:app.openExternal', {
      url: 'https://discord.com/oauth2/authorize?client_id=1',
    });
    expect(shell.calls).toContain('https://discord.com/oauth2/authorize?client_id=1');
  });

  it('allows https://discordapp.com', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://discordapp.com/path' });
    expect(shell.calls.length).toBe(1);
  });

  it('allows https://support-dev.discord.com', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://support-dev.discord.com/hc' });
    expect(shell.calls.length).toBe(1);
  });

  it('allows https://github.com', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://github.com/scout-org/scout' });
    expect(shell.calls.length).toBe(1);
  });

  it('rejects https://evil.example and does NOT call shell.openExternal', async () => {
    let caught: unknown;
    try {
      await invoke(ipc, 'scout:app.openExternal', { url: 'https://evil.example/steal' });
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(Error);
    expect((caught as Error & { code?: string }).code).toBe('app/external-host-not-allowed');
    expect(shell.calls.length).toBe(0);
  });

  it('rejects http:// URLs (non-HTTPS)', async () => {
    let caught: unknown;
    try {
      await invoke(ipc, 'scout:app.openExternal', { url: 'http://discord.com/oauth2' });
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(Error);
    expect((caught as Error & { code?: string }).code).toBe('app/external-host-not-allowed');
    expect(shell.calls.length).toBe(0);
  });

  it('rejects javascript: scheme', async () => {
    let caught: unknown;
    try {
      await invoke(ipc, 'scout:app.openExternal', { url: 'javascript:alert(1)' });
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(Error);
    expect((caught as Error & { code?: string }).code).toBe('app/external-host-not-allowed');
    expect(shell.calls.length).toBe(0);
  });

  it('rejects file:// scheme', async () => {
    let caught: unknown;
    try {
      await invoke(ipc, 'scout:app.openExternal', { url: 'file:///etc/passwd' });
    } catch (e) {
      caught = e;
    }
    expect((caught as Error & { code?: string }).code).toBe('app/external-host-not-allowed');
    expect(shell.calls.length).toBe(0);
  });
});

describe('registerAppHandlers — appGetVersion', () => {
  it('returns the app version', async () => {
    const ipc = makeFakeIpcMain();
    const app = makeApp('2.0.0');
    registerAppHandlers(ipc, makeShell(), app, logger);
    const result = (await invoke(ipc, 'scout:app.getVersion')) as { version: string };
    expect(result.version).toBe('2.0.0');
  });
});

describe('registerAppHandlers — appQuit', () => {
  it('calls app.quit', async () => {
    const ipc = makeFakeIpcMain();
    const app = makeApp();
    registerAppHandlers(ipc, makeShell(), app, logger);
    await invoke(ipc, 'scout:app.quit');
    expect(app.wasQuitCalled()).toBe(true);
  });
});

describe('registerAppHandlers — unregister', () => {
  it('removes all three handlers', () => {
    const ipc = makeFakeIpcMain();
    const unregister = registerAppHandlers(ipc, makeShell(), makeApp(), logger);
    unregister();
    expect(ipc.handlers.has('scout:app.openExternal')).toBe(false);
    expect(ipc.handlers.has('scout:app.getVersion')).toBe(false);
    expect(ipc.handlers.has('scout:app.quit')).toBe(false);
  });
});
