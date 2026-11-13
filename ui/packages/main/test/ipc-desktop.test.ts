import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { CHANNEL, VaultUnavailableError, readIpcError, type DesktopSettings } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { registerDesktopHandlers } from '../src/ipc/desktop.js';
import { createLoginItem, type LoginItem } from '../src/shell/login-item.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

function memoryLoginItem(): LoginItem {
  let enabled = false;
  return {
    isEnabled: async () => enabled,
    setEnabled: async (next) => {
      enabled = next;
    },
  };
}

async function setup(
  options: {
    trayAvailable?: boolean;
    entries?: Map<string, string>;
    vault?: {
      get(key: string): Promise<string | null>;
      store(key: string, v: string): Promise<void>;
    };
    loginItem?: LoginItem;
  } = {},
) {
  const handlers = new Map<string, IpcHandler>();
  const ipc: Pick<IpcMain, 'handle' | 'removeHandler'> = {
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
  const entries = options.entries ?? new Map<string, string>();
  const vault = options.vault ?? {
    get: async (key: string) => entries.get(key) ?? null,
    store: async (key: string, value: string) => {
      entries.set(key, value);
    },
  };
  const { logger, lines } = recordingLogger();
  const desktop = await registerDesktopHandlers(ipc, {
    vault,
    loginItem: options.loginItem ?? memoryLoginItem(),
    trayAvailable: options.trayAvailable ?? true,
    logger,
  });
  const invoke = (channel: string, req?: unknown) =>
    Promise.resolve(handlers.get(channel)!(NULL_EVENT, req)) as Promise<DesktopSettings>;
  return { desktop, invoke, entries, lines, handlers };
}

describe('Keep Scout running when the window is closed', () => {
  it('is on by default', async () => {
    const { desktop, invoke } = await setup();

    expect(desktop.keepRunning()).toBe(true);
    expect(await invoke(CHANNEL.desktopGetSettings)).toEqual({
      keepRunning: true,
      startAtLogin: false,
      trayAvailable: true,
    });
  });

  it('turning it off is remembered for the next app run', async () => {
    const first = await setup();

    const answer = await first.invoke(CHANNEL.desktopSetKeepRunning, { enabled: false });
    const nextRun = await setup({ entries: first.entries });

    expect(answer.keepRunning).toBe(false);
    expect(first.desktop.keepRunning()).toBe(false);
    expect(nextRun.desktop.keepRunning()).toBe(false);
  });

  it('is forced off while the tray is unavailable, and Settings is told why', async () => {
    const { desktop, invoke } = await setup({ trayAvailable: false });

    expect(desktop.keepRunning()).toBe(false);
    expect(await invoke(CHANNEL.desktopGetSettings)).toEqual({
      keepRunning: false,
      startAtLogin: false,
      trayAvailable: false,
    });
  });

  it('stays on when the keyring cannot be read', async () => {
    const { desktop } = await setup({
      vault: {
        get: () => Promise.reject(new VaultUnavailableError()),
        store: () => Promise.reject(new VaultUnavailableError()),
      },
    });

    expect(desktop.keepRunning()).toBe(true);
  });

  it('a failed save rejects with the vault error and keeps the previous choice', async () => {
    const { desktop, invoke } = await setup({
      vault: {
        get: async () => null,
        store: () => Promise.reject(new VaultUnavailableError()),
      },
    });

    const error = await invoke(CHANNEL.desktopSetKeepRunning, { enabled: false }).catch(
      (err: unknown) => err,
    );

    expect(readIpcError(error).code).toBe('vault/unavailable');
    expect(desktop.keepRunning()).toBe(true);
  });
});

describe('Start Scout when I log in', () => {
  let home: string;

  beforeEach(async () => {
    home = await mkdtemp(path.join(os.tmpdir(), 'scout-home-'));
  });

  afterEach(async () => {
    await rm(home, { recursive: true, force: true });
  });

  const linuxLoginItem = () =>
    createLoginItem({
      platform: 'linux',
      home,
      executable: '/opt/Scout/scout-desktop',
      app: {
        getLoginItemSettings: () => ({ openAtLogin: false }),
        setLoginItemSettings: () => undefined,
      },
    });

  it('is off by default and turning it on reports it on', async () => {
    const { invoke } = await setup({ loginItem: linuxLoginItem() });

    expect((await invoke(CHANNEL.desktopGetSettings)).startAtLogin).toBe(false);
    expect((await invoke(CHANNEL.desktopSetStartAtLogin, { enabled: true })).startAtLogin).toBe(
      true,
    );
  });

  it('when the autostart file cannot be written, the call fails with a typed error and the setting stays off', async () => {
    await mkdir(path.join(home, '.config'), { recursive: true });
    await writeFile(path.join(home, '.config', 'autostart'), 'not a folder');
    const { invoke, lines } = await setup({ loginItem: linuxLoginItem() });

    const error = await invoke(CHANNEL.desktopSetStartAtLogin, { enabled: true }).catch(
      (err: unknown) => err,
    );

    expect(readIpcError(error)).toEqual({
      code: 'desktop/autostart-failed',
      message: 'Could not change whether Scout starts when you log in.',
    });
    expect((await invoke(CHANNEL.desktopGetSettings)).startAtLogin).toBe(false);
    expect(lines.join('\n')).not.toContain(home);
  });
});
