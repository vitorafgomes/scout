import type { IpcMain } from 'electron';
import {
  CHANNEL,
  encodeIpcErrorMessage,
  serializeError,
  type DesktopSettings,
  type DesktopToggleRequest,
} from '@scout/shared';
import type { LoginItem } from '../shell/login-item.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';

const KEEP_RUNNING_KEY = 'app.keep-running';

export interface DesktopHandlerDeps {
  vault: Pick<Vault, 'get' | 'store'>;
  loginItem: LoginItem;
  trayAvailable: boolean;
  logger: Logger;
}

export interface DesktopHandle {
  /** Whether closing the window leaves Scout running; false whenever the tray is unavailable. */
  keepRunning(): boolean;
  dispose(): void;
}

/** Registers the Desktop settings channels after reading the stored keep-running choice (on unless the owner turned it off). */
export async function registerDesktopHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  deps: DesktopHandlerDeps,
): Promise<DesktopHandle> {
  const { vault, loginItem, trayAvailable, logger } = deps;

  let keepRunningChoice = (await vault.get(KEEP_RUNNING_KEY).catch(() => null)) !== 'false';

  function keepRunning(): boolean {
    return trayAvailable && keepRunningChoice;
  }

  async function settings(): Promise<DesktopSettings> {
    return { keepRunning: keepRunning(), startAtLogin: await loginItem.isEnabled(), trayAvailable };
  }

  async function relay(
    name: string,
    call: () => Promise<DesktopSettings>,
  ): Promise<DesktopSettings> {
    try {
      return await call();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      logger.warn(`desktop: ${name} failed`, { code: serialized.code });
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
  }

  ipcMain.handle(CHANNEL.desktopGetSettings, () => relay('getSettings', settings));
  ipcMain.handle(CHANNEL.desktopSetKeepRunning, (_event, req: DesktopToggleRequest | undefined) =>
    relay('setKeepRunning', async () => {
      const enabled = req?.enabled === true;
      await vault.store(KEEP_RUNNING_KEY, String(enabled));
      keepRunningChoice = enabled;
      logger.info('desktop: keep running changed', { enabled });
      return settings();
    }),
  );
  ipcMain.handle(CHANNEL.desktopSetStartAtLogin, (_event, req: DesktopToggleRequest | undefined) =>
    relay('setStartAtLogin', async () => {
      const enabled = req?.enabled === true;
      await loginItem.setEnabled(enabled);
      logger.info('desktop: start at login changed', { enabled });
      return settings();
    }),
  );

  return {
    keepRunning,
    dispose() {
      ipcMain.removeHandler(CHANNEL.desktopSetStartAtLogin);
      ipcMain.removeHandler(CHANNEL.desktopSetKeepRunning);
      ipcMain.removeHandler(CHANNEL.desktopGetSettings);
    },
  };
}
