import { app, BrowserWindow, ipcMain, shell, session, Tray, Menu } from 'electron';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { readlink as fsReadlink, unlink as fsUnlink } from 'node:fs/promises';
import os from 'node:os';
import { createLogger } from './util/logger.js';
import { tryRecoverStaleLock } from './util/singleton-lock.js';
import type { SingletonLockDeps } from './util/singleton-lock.js';
import { isPidAlive } from './util/singleton-lock-deps.js';
import { createMainWindow } from './browser-window-factory.js';
import { registerDetectHandler } from './ipc/detect.js';
import { registerVaultHandlers } from './ipc/vault.js';
import { registerAppHandlers } from './ipc/app.js';
import { registerDiscordHandlers } from './ipc/discord.js';
import { registerWorkerHandlers, attachBrowserWindow } from './ipc/worker.js';
import { awaitCallback, findFreePort } from './oauth/loopback-server.js';
import { createProductionDeps } from './detect/deps.js';
import { createVault } from './vault/safe-storage-vault.js';
import { createProductionVaultDeps } from './vault/deps.js';
import { createSupervisor } from './worker/supervisor.js';
import { resolveWorkerBinary } from './worker/resolve-binary.js';
import { VaultUnavailableError } from '@scout/shared';
import type { Supervisor } from './worker/supervisor.js';
import { createTrayController } from './tray/tray.js';
import type { TrayController } from './tray/tray.js';

const logger = createLogger('main');

function createProdSingletonLockDeps(log: typeof logger): SingletonLockDeps {
  return {
    userDataDir: app.getPath('userData'),
    hostname: os.hostname(),
    readlink: async (p) => {
      try {
        return await fsReadlink(p);
      } catch {
        return null;
      }
    },
    unlink: async (p) => {
      try {
        await fsUnlink(p);
      } catch (err: unknown) {
        if ((err as NodeJS.ErrnoException).code === 'ENOENT') return;
        throw err;
      }
    },
    isPidAlive,
    logger: log,
  };
}

const __dirname = import.meta.dirname;

const DEV = process.env['NODE_ENV'] === 'development';

const CSP_PROD = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data:",
  "font-src 'self'",
  "connect-src 'self'",
  "frame-src 'none'",
  "object-src 'none'",
  "base-uri 'none'",
  "form-action 'none'",
].join('; ');

const CSP_DEV = [
  "default-src 'self'",
  "script-src 'self' http://localhost:4200",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data:",
  "font-src 'self'",
  "connect-src 'self' http://localhost:4200 ws://localhost:4200",
  "frame-src 'none'",
  "object-src 'none'",
  "base-uri 'none'",
  "form-action 'none'",
].join('; ');

let mainWindow: BrowserWindow | null = null;
let supervisor: Supervisor | null = null;
let trayController: TrayController | null = null;
let isQuitting = false;
let unregisterVault: (() => void) | null = null;
let unregisterDetect: (() => void) | null = null;
let unregisterApp: (() => void) | null = null;
let unregisterDiscord: (() => void) | null = null;
let unregisterWorker: (() => void) | null = null;

app
  .whenReady()
  .then(async () => {
    const gotLock = app.requestSingleInstanceLock();
    if (!gotLock) {
      let result;
      try {
        result = await tryRecoverStaleLock(createProdSingletonLockDeps(logger));
      } catch (err: unknown) {
        logger.warn('singleton-lock recovery failed', { err });
        app.quit();
        return;
      }
      if (result.cleared && app.requestSingleInstanceLock()) {
        logger.info('reclaimed lock after cleaning stale entry');
      } else {
        logger.info('another instance is running', { reason: result.reason });
        app.quit();
        return;
      }
    }

    const vault = createVault(createProductionVaultDeps(logger.child({ module: 'vault' })));
    unregisterVault = registerVaultHandlers(ipcMain, vault, logger.child({ module: 'vault' }));

    unregisterDetect = registerDetectHandler(
      ipcMain,
      createProductionDeps(),
      logger.child({ module: 'detect' }),
    );

    unregisterApp = registerAppHandlers(ipcMain, shell, app, logger.child({ module: 'app' }));

    unregisterDiscord = registerDiscordHandlers(
      ipcMain,
      {
        awaitCallback,
        fetch: globalThis.fetch,
        shell,
        logger: logger.child({ module: 'discord' }),
        findFreePort,
      },
      logger.child({ module: 'discord' }),
    );

    supervisor = createSupervisor({
      spawn,
      logger: logger.child({ module: 'supervisor' }),
      resolveBinary: () =>
        resolveWorkerBinary({
          app,
          resourcesPath: process.resourcesPath,
          platform: process.platform,
          arch: process.arch,
          cwd: __dirname,
        }),
      buildEnv: async (): Promise<Record<string, string>> => {
        const token = await vault.get('discord.bot-token');
        if (token === null) {
          throw new VaultUnavailableError(
            'discord.bot-token not found in vault — complete the Discord wizard first',
          );
        }

        // env is passed to spawn; never logged
        return {
          SCOUT__CHATADAPTER: 'Discord',
          SCOUT__LLMPROVIDER: 'Ollama',
          DOTNET_ENVIRONMENT: 'Production',
          SCOUT__ADAPTERS__DISCORD__BOTTOKEN: token,
        };
      },
    });

    unregisterWorker = registerWorkerHandlers(
      ipcMain,
      supervisor,
      logger.child({ module: 'worker' }),
    );

    session.defaultSession.webRequest.onHeadersReceived((details, callback) => {
      callback({
        responseHeaders: {
          ...details.responseHeaders,
          'Content-Security-Policy': [DEV ? CSP_DEV : CSP_PROD],
        },
      });
    });

    const indexHtmlPath = path.join(
      __dirname,
      '..',
      '..',
      'renderer',
      'dist',
      'browser',
      'index.html',
    );

    const candidatePreloadPath = path.join(__dirname, '../../preload/dist/preload.cjs');
    let preloadPath: string | undefined;

    if (existsSync(candidatePreloadPath)) {
      preloadPath = candidatePreloadPath;
    } else {
      logger.warn('preload not found; window opens without preload bridge', {
        path: candidatePreloadPath,
      });
      preloadPath = undefined;
    }

    mainWindow = createMainWindow({
      createWindow: (opts) => new BrowserWindow(opts),
      logger,
      preloadPath,
      indexHtmlPath,
      ...(DEV ? { devServerUrl: 'http://localhost:4200' } : {}),
    });

    // Hide instead of closing so the app stays alive in the tray.
    mainWindow.on('close', (event) => {
      if (!isQuitting) {
        event.preventDefault();
        mainWindow?.hide();
      }
    });

    // Null out the reference only when the window is actually destroyed.
    mainWindow.on('closed', () => {
      mainWindow = null;
    });

    const iconPath = app.isPackaged
      ? path.join(process.resourcesPath, 'icons', 'icon.png')
      : path.join(__dirname, '..', '..', '..', 'build', 'icons', 'icon.png');

    trayController = createTrayController({
      createTray: (p) => new Tray(p),
      buildContextMenu: (t) => Menu.buildFromTemplate(t),
      iconPath,
      onShowWindow: () => {
        if (mainWindow == null || mainWindow.isDestroyed()) return;
        if (mainWindow.isMinimized()) mainWindow.restore();
        mainWindow.show();
        mainWindow.focus();
      },
      onStopWorker: async () => {
        if (supervisor != null) await supervisor.stop();
      },
      onQuit: () => {
        isQuitting = true;
        app.quit();
      },
      logger: logger.child({ module: 'tray' }),
    });

    // Keep tray status in sync with supervisor state transitions.
    supervisor.onEvent((e) => {
      if (e.kind === 'state') trayController?.setStatus(e.status);
    });

    attachBrowserWindow(mainWindow, supervisor);

    logger.info('main window created', { dev: DEV });
  })
  .catch((err: unknown) => {
    logger.error('app.whenReady failed', err);
    app.quit();
  });

// The app stays alive when the window is hidden to the tray.
// Quit is only triggered via the tray menu or before-quit lifecycle.
app.on('window-all-closed', () => {
  // Intentionally empty: do not quit on window close on any platform.
});

app.on('before-quit', (event) => {
  logger.info('before-quit');

  const runningWorker = supervisor;
  if (
    runningWorker === null ||
    runningWorker.status.state === 'stopped' ||
    runningWorker.status.state === 'idle'
  ) {
    // No running worker — unregister synchronously and allow the quit.
    trayController?.destroy();
    trayController = null;
    unregisterWorker?.();
    unregisterDiscord?.();
    unregisterApp?.();
    unregisterDetect?.();
    unregisterVault?.();
    return;
  }

  // Worker is running; stop it gracefully before allowing the quit.
  event.preventDefault();
  void runningWorker
    .stop()
    .catch((err: unknown) => {
      logger.warn('before-quit: worker stop failed', { err: String(err) });
    })
    .finally(() => {
      trayController?.destroy();
      trayController = null;
      unregisterWorker?.();
      unregisterDiscord?.();
      unregisterApp?.();
      unregisterDetect?.();
      unregisterVault?.();
      app.exit(0);
    });
});

app.on('second-instance', () => {
  if (mainWindow != null && !mainWindow.isDestroyed()) {
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.show();
    mainWindow.focus();
  }
});
