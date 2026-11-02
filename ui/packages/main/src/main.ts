import {
  app,
  BrowserWindow,
  globalShortcut,
  ipcMain,
  Menu,
  nativeImage,
  Notification,
  powerMonitor,
  screen,
  shell,
  session,
  Tray,
} from 'electron';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import {
  readlink as fsReadlink,
  unlink as fsUnlink,
  writeFile as fsWriteFile,
  rename as fsRename,
  rm as fsRm,
} from 'node:fs/promises';
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
import { registerWhisperHandlers, whisperRootDir } from './ipc/whisper.js';
import { registerProvidersHandlers } from './ipc/providers.js';
import { registerFeaturesHandlers } from './ipc/features.js';
import { registerProviderKeyHandlers, registerProviderModelHandlers } from './ipc/provider-keys.js';
import { registerChatGptHandlers } from './ipc/chatgpt.js';
import { ChatGptAuth } from './oauth/chatgpt-auth.js';
import { registerGitHubHandlers, restartWorkerIfRunning } from './ipc/github.js';
import { readKubernetesSelection, registerKubernetesHandlers } from './ipc/kubernetes.js';
import { readAllowedUserIds, registerAccessHandlers } from './ipc/access.js';
import { readSlackTokens, registerSlackHandlers } from './ipc/slack.js';
import { registerDesktopHandlers, type DesktopHandle } from './ipc/desktop.js';
import { installAppLifecycle, launchWindow } from './shell/app-lifecycle.js';
import { createLoginItem } from './shell/login-item.js';
import { restartThroughX11, x11RelaunchArgs } from './shell/x11-relaunch.js';
import { createPresenceNotifier, reportPairing } from './shell/notifications.js';
import { createTrayController, type TrayController } from './tray/tray.js';
import { detectKubeconfig } from './kubernetes/kubeconfig.js';
import {
  GITHUB_APP_CLIENT_ID,
  GITHUB_APP_SLUG,
  GitHubAuth,
  abortableSleep,
} from './oauth/github-auth.js';
import { WorkerHttpClient } from './worker/http-client.js';
import { awaitCallback, findFreePort } from './oauth/loopback-server.js';
import { createProductionDeps } from './detect/deps.js';
import { createVault } from './vault/safe-storage-vault.js';
import { createProductionVaultDeps } from './vault/deps.js';
import { createSupervisor } from './worker/supervisor.js';
import {
  resolveControlPlaneBinary,
  resolveProtoDir,
  resolveTrayIcon,
  resolveWorkerBinary,
} from './worker/resolve-binary.js';
import { connectControlPlane, type ControlPlaneConnection } from './control-plane/launcher.js';
import { buildEnv, migrateLegacyApiKey } from './worker/build-env.js';
import { findBestWhisperModel } from './worker/whisper-model.js';
import type { Supervisor } from './worker/supervisor.js';
import {
  allowMicrophoneOnlyForApp,
  openQuickAsk,
  registerQuickAsk,
  type QuickAsk,
} from './quick-ask/quick-ask.js';

const logger = createLogger('main');

const relaunch = x11RelaunchArgs(
  process.platform,
  process.env,
  process.argv.slice(1),
  process.execPath,
  app.isPackaged,
);
const relaunching =
  relaunch === null
    ? Promise.resolve(false)
    : restartThroughX11(relaunch, { spawn, exit: (code) => app.exit(code), logger });

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
let controlPlane: ControlPlaneConnection | null = null;
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
let unregisterProviderKeys: (() => void) | null = null;
let unregisterChatGpt: (() => void) | null = null;
let chatGptAuth: ChatGptAuth | null = null;
let unregisterGitHub: (() => void) | null = null;
let unregisterKubernetes: (() => void) | null = null;
let unregisterAccess: (() => void) | null = null;
let unregisterSlack: (() => void) | null = null;
let gitHubAuth: GitHubAuth | null = null;
let quickAsk: QuickAsk | null = null;
let trayController: TrayController | null = null;
let desktop: DesktopHandle | null = null;
let openMainWindow: ((show: boolean) => BrowserWindow) | null = null;

function disposeAll(): void {
  quickAsk?.dispose();
  trayController?.destroy();
  desktop?.dispose();
  unregisterProviderKeys?.();
  chatGptAuth?.stop();
  unregisterChatGpt?.();
  controlPlane?.dispose();
  gitHubAuth?.stop();
  unregisterGitHub?.();
  unregisterKubernetes?.();
  unregisterAccess?.();
  unregisterSlack?.();
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
}

const notifierLogger = logger.child({ module: 'notifications' });
let notifier: ReturnType<typeof createPresenceNotifier> | null = null;

const lifecycle = installAppLifecycle({
  app,
  platform: process.platform,
  supervisor: () => supervisor,
  keepRunning: () => desktop?.keepRunning() ?? false,
  window: () => mainWindow,
  createWindow: () => openMainWindow?.(true),
  onHiddenToTray: () => void notifier?.hiddenToTray(),
  onAsk: openQuickAsk,
  dispose: disposeAll,
  logger,
});

app
  .whenReady()
  .then(async () => {
    if (await relaunching) return;
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
    notifier = createPresenceNotifier({
      platform: process.platform,
      isSupported: () => Notification.isSupported(),
      show: (title, body) => new Notification({ title, body }).show(),
      vault,
      logger: notifierLogger,
    });
    const presence = notifier;

    await migrateLegacyApiKey(vault).catch((err: unknown) => {
      logger.warn('vault: legacy API key not migrated', {
        error: err instanceof Error ? err.name : typeof err,
      });
    });

    unregisterDetect = registerDetectHandler(
      ipcMain,
      createProductionDeps(),
      logger.child({ module: 'detect' }),
    );

    unregisterApp = registerAppHandlers(ipcMain, shell, app, logger.child({ module: 'app' }));

    const discordLogger = logger.child({ module: 'discord' });
    unregisterDiscord = registerDiscordHandlers(ipcMain, {
      fetch: globalThis.fetch,
      vault,
      onTokenChanged: () => {
        if (supervisor !== null) void restartWorkerIfRunning(supervisor, discordLogger);
      },
      logger: discordLogger,
    });

    const chatGptTokenFile = path.join(app.getPath('userData'), 'chatgpt-token.json');
    chatGptAuth = new ChatGptAuth({
      fetch: globalThis.fetch,
      awaitCallback,
      findFreePort,
      openExternal: (url) => shell.openExternal(url),
      vault,
      tokenFilePath: chatGptTokenFile,
      fs: { writeFile: fsWriteFile, rename: fsRename, rm: fsRm },
      now: () => Date.now(),
      logger: logger.child({ module: 'chatgpt' }),
    });
    unregisterChatGpt = registerChatGptHandlers(
      ipcMain,
      chatGptAuth,
      logger.child({ module: 'chatgpt' }),
    );
    chatGptAuth.start().catch((err: unknown) => {
      logger.warn('chatgpt: start failed', { err: String(err) });
    });

    const gitHubTokenFile = path.join(app.getPath('userData'), 'github-token.json');
    const gitHubLogger = logger.child({ module: 'github' });
    const auth = new GitHubAuth({
      fetch: globalThis.fetch,
      vault,
      tokenFilePath: gitHubTokenFile,
      fs: { writeFile: fsWriteFile, rename: fsRename, rm: fsRm },
      now: () => Date.now(),
      sleep: abortableSleep,
      clientId: process.env['SCOUT_GITHUB_APP_CLIENT_ID'] || GITHUB_APP_CLIENT_ID,
      appSlug: process.env['SCOUT_GITHUB_APP_SLUG'] || GITHUB_APP_SLUG,
      onConnectionChanged: () => {
        if (supervisor !== null) void restartWorkerIfRunning(supervisor, gitHubLogger);
      },
      onResume: (listener) => {
        powerMonitor.on('resume', listener);
        return () => powerMonitor.removeListener('resume', listener);
      },
      logger: gitHubLogger,
    });
    gitHubAuth = auth;
    unregisterGitHub = registerGitHubHandlers(ipcMain, auth, gitHubLogger);
    auth.start().catch((err: unknown) => {
      gitHubLogger.warn('github: start failed', { err: String(err) });
    });

    const kubernetesLogger = logger.child({ module: 'kubernetes' });
    unregisterKubernetes = registerKubernetesHandlers(ipcMain, {
      vault,
      detect: () =>
        detectKubeconfig({ env: process.env, homedir: os.homedir(), logger: kubernetesLogger }),
      onSelectionChanged: () => {
        if (supervisor !== null) void restartWorkerIfRunning(supervisor, kubernetesLogger);
      },
      logger: kubernetesLogger,
    });

    const accessLogger = logger.child({ module: 'access' });
    const access = registerAccessHandlers(ipcMain, {
      vault,
      onAllowedUsersChanged: () => {
        if (supervisor !== null) void restartWorkerIfRunning(supervisor, accessLogger);
      },
      logger: accessLogger,
    });
    unregisterAccess = access.dispose;

    const slackLogger = logger.child({ module: 'slack' });
    unregisterSlack = registerSlackHandlers(ipcMain, {
      vault,
      fetch: globalThis.fetch,
      onConnectionChanged: () => {
        if (supervisor !== null) void restartWorkerIfRunning(supervisor, slackLogger);
      },
      logger: slackLogger,
    });

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
      buildEnv: async () =>
        buildEnv({
          vaultGet: (key) => vault.get(key),
          chatGptTokenFile,
          gitHubEnv: await auth.workerEnv(),
          kubernetesSelection: await readKubernetesSelection(vault),
          allowedUserIds: await readAllowedUserIds(vault),
          pairing: access.pendingPairing(),
          slackTokens: await readSlackTokens(vault),
          whisperModelPath: await findBestWhisperModel(whisperRootDir()),
        }),
      onPaired: (userId) =>
        void reportPairing(userId, access, () => readAllowedUserIds(vault), presence),
      onPairingBurned: () => access.pairingBurned(),
      onLocalEvent: (json) => quickAsk?.handleWorkerLine(json),
    });

    const workerSupervisor = supervisor;
    trayController = createTrayController({
      createTray: (iconPath) => {
        if (!existsSync(iconPath)) throw new Error('tray icon missing');
        const icon = nativeImage.createFromPath(iconPath);
        return new Tray(
          process.platform === 'darwin' ? icon.resize({ width: 16, height: 16 }) : icon,
        );
      },
      buildContextMenu: (template) => Menu.buildFromTemplate(template),
      iconPath: resolveTrayIcon({ app, resourcesPath: process.resourcesPath, cwd: __dirname }),
      onOpen: () => lifecycle.showWindow(),
      onAskScout: openQuickAsk,
      onStart: () => workerSupervisor.start(),
      onStop: () => workerSupervisor.stop(),
      onQuit: () => app.quit(),
      logger: logger.child({ module: 'tray' }),
      initialStatus: workerSupervisor.status,
    });
    workerSupervisor.onEvent((event) => {
      if (event.kind !== 'state') return;
      trayController?.setStatus(event.status);
      presence.workerStatus(event.status);
    });

    desktop = await registerDesktopHandlers(ipcMain, {
      vault,
      loginItem: createLoginItem({
        platform: process.platform,
        home: os.homedir(),
        executable: process.env['APPIMAGE'] || process.execPath,
        app,
      }),
      trayAvailable: trayController !== null,
      logger: logger.child({ module: 'desktop' }),
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
    });

    unregisterProviders = registerProvidersHandlers(
      ipcMain,
      workerHttpClient,
      logger.child({ module: 'providers' }),
    );

    const binaryDeps = {
      app,
      resourcesPath: process.resourcesPath,
      platform: process.platform,
      arch: process.arch,
      cwd: __dirname,
    };
    const connection = connectControlPlane({
      spawn,
      resolveBinary: () => resolveControlPlaneBinary(binaryDeps),
      protoDir: resolveProtoDir(binaryDeps),
      logger: logger.child({ module: 'control-plane' }),
      platform: process.platform,
    });
    controlPlane = connection;

    unregisterFeatures = registerFeaturesHandlers(
      ipcMain,
      () => connection.client,
      logger.child({ module: 'features' }),
    );

    unregisterLlm = registerLlmHandlers(ipcMain, globalThis.fetch, logger.child({ module: 'llm' }));
    const unregisterKeyCheck = registerProviderKeyHandlers(
      ipcMain,
      globalThis.fetch,
      logger.child({ module: 'provider-keys' }),
    );
    const unregisterModelList = registerProviderModelHandlers(
      ipcMain,
      globalThis.fetch,
      vault,
      logger.child({ module: 'provider-keys' }),
    );
    unregisterProviderKeys = () => {
      unregisterKeyCheck();
      unregisterModelList();
    };
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

    allowMicrophoneOnlyForApp(session.defaultSession, {
      indexHtmlPath,
      ...(DEV ? { devServerUrl: 'http://localhost:4200' } : {}),
    });
    quickAsk = await registerQuickAsk({
      ipcMain,
      supervisor: workerSupervisor,
      createWindow: (opts) => new BrowserWindow(opts),
      preloadPath,
      indexHtmlPath,
      ...(DEV ? { devServerUrl: 'http://localhost:4200' } : {}),
      globalShortcut,
      vault,
      logger: logger.child({ module: 'quick-ask' }),
      screen,
      platform: process.platform,
      env: process.env,
      execPath: process.execPath,
      windows: () => BrowserWindow.getAllWindows(),
    });

    openMainWindow = (show) => {
      const window = createMainWindow({
        createWindow: (opts) => new BrowserWindow(opts),
        logger,
        preloadPath,
        indexHtmlPath,
        showOnReady: show,
        ...(DEV ? { devServerUrl: 'http://localhost:4200' } : {}),
      });
      window.on('close', (event) => lifecycle.onWindowClose(event));
      window.on('closed', () => {
        if (mainWindow === window) mainWindow = null;
      });
      mainWindow = window;
      attachBrowserWindow(window, workerSupervisor);
      logger.info('main window created', { dev: DEV, hidden: !show });
      return window;
    };
    const openWindow = openMainWindow;
    launchWindow({
      argv: process.argv,
      platform: process.platform,
      trayAvailable: trayController !== null,
      notifier: presence,
      openWindow,
      onAsk: openQuickAsk,
    });
  })
  .catch((err: unknown) => {
    logger.error('app.whenReady failed', err);
    app.quit();
  });
