import { app, BrowserWindow, ipcMain, shell, session, Tray, Menu, nativeImage } from 'electron';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
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
import { registerLlmHandlers } from './ipc/llm.js';
import { registerLlmPullHandlers } from './ipc/llm-pull.js';
import { registerLmStudioHandlers } from './ipc/lm-studio.js';
import { registerWhisperHandlers } from './ipc/whisper.js';
import { registerProvidersHandlers } from './ipc/providers.js';
import { registerFeaturesHandlers } from './ipc/features.js';
import { WorkerHttpClient } from './worker/http-client.js';
import { awaitCallback, findFreePort } from './oauth/loopback-server.js';
import { createProductionDeps } from './detect/deps.js';
import { createVault } from './vault/safe-storage-vault.js';
import { createProductionVaultDeps } from './vault/deps.js';
import { createSupervisor } from './worker/supervisor.js';
import { resolveWorkerBinary } from './worker/resolve-binary.js';
import { buildEnv } from './worker/build-env.js';
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
let unregisterTrayStatus: (() => void) | null = null;
// Closing the window hides it to the tray instead of quitting (see the main
// window's 'close' handler below). Only a real quit path — the tray's Quit
// item or 'before-quit' — sets this, letting the window actually close.
let isQuitting = false;
let unregisterVault: (() => void) | null = null;
let unregisterDetect: (() => void) | null = null;
let unregisterApp: (() => void) | null = null;
let unregisterDiscord: (() => void) | null = null;
let unregisterWorker: (() => void) | null = null;
let unregisterLlm: (() => void) | null = null;
let unregisterLlmPull: (() => void) | null = null;
let unregisterLmStudio: (() => void) | null = null;
let unregisterWhisper: (() => void) | null = null;
let unregisterProviders: (() => void) | null = null;
let unregisterFeatures: (() => void) | null = null;

function showAndFocusMainWindow(): void {
  if (mainWindow == null || mainWindow.isDestroyed()) return;
  if (mainWindow.isMinimized()) mainWindow.restore();
  if (!mainWindow.isVisible()) mainWindow.show();
  mainWindow.focus();
}

// Builds the tray's Electron Tray instance from an icon file path. Resolves
// robustly for both dev and the packaged AppImage: the icon ships as a sibling
// resource under packages/main/resources (asarUnpack'd, same as index.html),
// so a single dist-relative path works in both cases — mirroring how
// indexHtmlPath/preloadPath resolve packaged resources elsewhere in this file.
// A missing or undecodable icon must never crash tray creation: fall back to
// an empty image and just log.
function createTrayIcon(iconPath: string, log: typeof logger): Tray {
  if (!existsSync(iconPath)) {
    log.warn('tray: icon file not found; using empty image', { iconPath });
    return new Tray(nativeImage.createEmpty());
  }

  const image = nativeImage.createFromPath(iconPath);
  if (image.isEmpty()) {
    log.warn('tray: icon loaded but resulted in an empty image', { iconPath });
  }
  return new Tray(image);
}

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

    // Mint the control-plane token once at startup. The same value is injected
    // into the worker's env (as SCOUT_CONTROLPLANE_TOKEN) and sent by the HTTP
    // client (as x-scout-token), so the loopback surface is not unauthenticated.
    const controlPlaneToken = randomBytes(32).toString('hex');

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
      // env is assembled by buildEnv; never logged — it contains secrets
      buildEnv: () => buildEnv({ vaultGet: (key) => vault.get(key), controlPlaneToken }),
    });

    unregisterWorker = registerWorkerHandlers(
      ipcMain,
      supervisor,
      logger.child({ module: 'worker' }),
    );

    const workerHttpClient = new WorkerHttpClient({
      getEndpoint: () => supervisor?.httpEndpoint ?? null,
      fetch: globalThis.fetch,
      logger: logger.child({ module: 'worker-http' }),
      token: controlPlaneToken,
    });

    unregisterProviders = registerProvidersHandlers(
      ipcMain,
      workerHttpClient,
      logger.child({ module: 'providers' }),
    );

    unregisterFeatures = registerFeaturesHandlers(
      ipcMain,
      workerHttpClient,
      logger.child({ module: 'features' }),
    );

    unregisterLlm = registerLlmHandlers(ipcMain, globalThis.fetch, logger.child({ module: 'llm' }));
    unregisterLmStudio = registerLmStudioHandlers(
      ipcMain,
      globalThis.fetch,
      logger.child({ module: 'lm-studio' }),
    );
    unregisterLlmPull = registerLlmPullHandlers(
      ipcMain,
      globalThis.fetch,
      logger.child({ module: 'llm-pull' }),
    );
    unregisterWhisper = registerWhisperHandlers(
      ipcMain,
      globalThis.fetch,
      logger.child({ module: 'whisper' }),
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

    // Closing the window (the X button) hides it to the tray instead of
    // quitting, so the worker keeps running in the background — only the
    // tray's Quit item (or a real app quit) proceeds past this guard.
    mainWindow.on('close', (event) => {
      if (!isQuitting) {
        event.preventDefault();
        mainWindow?.hide();
      }
    });

    mainWindow.on('closed', () => {
      mainWindow = null;
    });

    attachBrowserWindow(mainWindow, supervisor);

    logger.info('main window created', { dev: DEV });

    const trayIconPath = path.join(__dirname, '..', 'resources', 'icon.png');

    trayController = createTrayController({
      createTray: (iconPath) => createTrayIcon(iconPath, logger.child({ module: 'tray' })),
      buildContextMenu: (template) => Menu.buildFromTemplate(template),
      iconPath: trayIconPath,
      onShowWindow: showAndFocusMainWindow,
      onStopWorker: () => supervisor?.stop().then(() => undefined) ?? Promise.resolve(),
      onQuit: () => {
        isQuitting = true;
        app.quit();
      },
      logger: logger.child({ module: 'tray' }),
      initialStatus: supervisor.status,
    });

    unregisterTrayStatus = supervisor.onEvent((event) => {
      if (event.kind === 'state') trayController?.setStatus(event.status);
    });

    // Start the worker (and its control API) as soon as the app opens when
    // provisioning already exists, instead of depending on the welcome screen
    // being visited. The renderer's own start call stays as a no-op fallback —
    // supervisor.start() is idempotent while starting/running.
    void (async () => {
      try {
        if (!vault.availability().available) return;
        if (!(await vault.has('discord.bot-token'))) return;
        await supervisor?.start();
        logger.info('worker auto-started on launch');
      } catch (err: unknown) {
        logger.warn('worker auto-start on launch failed', { err: String(err) });
      }
    })();
  })
  .catch((err: unknown) => {
    logger.error('app.whenReady failed', err);
    app.quit();
  });

app.on('window-all-closed', () => {
  // With hide-to-tray, closing the window's 'close' handler prevents it from
  // ever actually closing (see above), so this only fires on a real quit —
  // but guard on isQuitting anyway rather than relying on that invariant.
  if (isQuitting) app.quit();
});

app.on('before-quit', (event) => {
  isQuitting = true;
  logger.info('before-quit');

  const runningWorker = supervisor;
  if (
    runningWorker === null ||
    runningWorker.status.state === 'stopped' ||
    runningWorker.status.state === 'idle'
  ) {
    // No running worker — unregister synchronously and allow the quit.
    trayController?.destroy();
    unregisterTrayStatus?.();
    unregisterFeatures?.();
    unregisterProviders?.();
    unregisterWhisper?.();
    unregisterLlmPull?.();
    unregisterLmStudio?.();
    unregisterLlm?.();
    unregisterWorker?.();
    unregisterDiscord?.();
    unregisterApp?.();
    unregisterDetect?.();
    unregisterVault?.();
    return;
  }

  // Worker is running; stop it gracefully before allowing the quit.
  event.preventDefault();

  let exited = false;
  const exitOnce = (): void => {
    if (exited) return;
    exited = true;
    trayController?.destroy();
    unregisterTrayStatus?.();
    unregisterFeatures?.();
    unregisterProviders?.();
    unregisterWhisper?.();
    unregisterLlmPull?.();
    unregisterLmStudio?.();
    unregisterLlm?.();
    unregisterWorker?.();
    unregisterDiscord?.();
    unregisterApp?.();
    unregisterDetect?.();
    unregisterVault?.();
    app.exit(0);
  };

  // Never let a hung stop() strand the process: a windowless Electron main would
  // keep the worker's stdin open, so its parent-death watchdog would never fire
  // and the backend would outlive the closed UI. Force-exit after a bounded
  // grace period regardless; exiting closes stdin, which the watchdog treats as
  // its cue to self-terminate (belt to stop()'s SIGKILL suspenders).
  const forceExit = setTimeout(exitOnce, 8_000);
  void runningWorker
    .stop()
    .catch((err: unknown) => {
      logger.warn('before-quit: worker stop failed', { err: String(err) });
    })
    .finally(() => {
      clearTimeout(forceExit);
      exitOnce();
    });
});

app.on('second-instance', () => {
  // The window may be hidden to the tray rather than destroyed; showAndFocus
  // handles both the minimized and hidden cases.
  showAndFocusMainWindow();
});
