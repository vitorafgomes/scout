import type {
  BrowserWindow,
  BrowserWindowConstructorOptions,
  IpcMain,
  Rectangle,
  Screen,
} from 'electron';
import { CHANNEL, serializeError } from '@scout/shared';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';
import { toBounds } from '../quick-ask/quick-ask.js';

export const OPERATOR_WINDOW_TITLE = 'Scout - YouTube operator';

const WINDOW_VAULT_KEY = 'operator.window';
const OPERATOR_SIZE = { width: 1400, height: 900 };
const OPERATOR_MIN_SIZE = { minWidth: 960, minHeight: 640 };
const ROUTE = 'operator';

export interface OperatorWindowDeps {
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>;
  createWindow: (opts: BrowserWindowConstructorOptions) => BrowserWindow;
  preloadPath: string | undefined;
  indexHtmlPath: string;
  devServerUrl?: string;
  vault: Pick<Vault, 'get' | 'store'>;
  screen: Pick<Screen, 'getAllDisplays'>;
  logger: Logger;
}

/** The YouTube operator window: one per app, opened or focused on `scout:operator.window`. */
export interface OperatorWindow {
  open(): void;
  /** Saves the window's place, closes it and unregisters its channel. */
  dispose(): void;
}

/** Registers `scout:operator.window`; the window shares the main window's session, preload and hardening. */
export async function registerOperatorWindow(deps: OperatorWindowDeps): Promise<OperatorWindow> {
  const { ipcMain, vault, logger } = deps;
  let window: BrowserWindow | null = null;
  let savedBounds: Rectangle | null = null;

  try {
    savedBounds = toBounds(await vault.get(WINDOW_VAULT_KEY));
  } catch (err: unknown) {
    logger.warn('operator: stored operator window place unreadable; using the default size', {
      code: serializeError(err).code,
    });
  }

  function rememberBounds(target: BrowserWindow): void {
    savedBounds = target.getBounds();
    vault.store(WINDOW_VAULT_KEY, JSON.stringify(savedBounds)).catch((err: unknown) => {
      logger.warn('operator: operator window place not saved; it lasts until Scout quits', {
        code: serializeError(err).code,
      });
    });
  }

  function onSomeDisplay(point: { x: number; y: number }): boolean {
    return deps.screen
      .getAllDisplays()
      .some(
        ({ workArea: a }) =>
          point.x >= a.x && point.x < a.x + a.width && point.y >= a.y && point.y < a.y + a.height,
      );
  }

  function create(): BrowserWindow {
    const created = deps.createWindow({
      ...OPERATOR_SIZE,
      ...OPERATOR_MIN_SIZE,
      title: OPERATOR_WINDOW_TITLE,
      show: false,
      resizable: true,
      backgroundColor: '#111111',
      webPreferences: {
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
        webSecurity: true,
        allowRunningInsecureContent: false,
        experimentalFeatures: false,
        ...(deps.preloadPath != null ? { preload: deps.preloadPath } : {}),
      },
    });
    if (savedBounds !== null && onSomeDisplay(savedBounds)) created.setBounds(savedBounds);
    created.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    created.webContents.on('will-navigate', (e, url) => {
      e.preventDefault();
      logger.warn('operator: navigation blocked', { url });
    });
    created.on('page-title-updated', (e) => e.preventDefault());
    created.once('ready-to-show', () => {
      if (!created.isDestroyed()) created.show();
    });
    created.on('moved', () => rememberBounds(created));
    created.on('resized', () => rememberBounds(created));
    created.on('close', () => rememberBounds(created));
    created.on('closed', () => {
      if (window === created) window = null;
    });
    if (deps.devServerUrl != null) void created.loadURL(`${deps.devServerUrl}/#/${ROUTE}`);
    else void created.loadFile(deps.indexHtmlPath, { hash: `/${ROUTE}` });
    return created;
  }

  function open(): void {
    if (window === null || window.isDestroyed()) {
      window = create();
      return;
    }
    if (window.isMinimized()) window.restore();
    window.show();
    window.focus();
  }

  ipcMain.handle(CHANNEL.operatorWindow, () => open());

  return {
    open,
    dispose(): void {
      ipcMain.removeHandler(CHANNEL.operatorWindow);
      if (window !== null && !window.isDestroyed()) {
        rememberBounds(window);
        window.destroy();
      }
      window = null;
    },
  };
}
