import {
  app,
  BrowserWindow,
  desktopCapturer,
  dialog,
  globalShortcut,
  ipcMain,
  Menu,
  nativeImage,
  Notification,
  powerMonitor,
  protocol,
  screen,
  shell,
  session,
  Tray,
} from 'electron';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import {
  readFile as fsReadFile,
  readlink as fsReadlink,
  unlink as fsUnlink,
  writeFile as fsWriteFile,
  rename as fsRename,
  rm as fsRm,
} from 'node:fs/promises';
import os from 'node:os';
import { autoUpdater } from 'electron-updater';
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
import { registerOperatorHandlers } from './ipc/operator.js';
import { registerOperatorWindow, type OperatorWindow } from './operator/operator-window.js';
import {
  MEDIA_SCHEME,
  MEDIA_SCHEME_PRIVILEGES,
  chooseWithDialog,
  createMediaProtocolHandler,
} from './operator/media.js';
import { registerProviderKeyHandlers, registerProviderModelHandlers } from './ipc/provider-keys.js';
import { registerChatGptHandlers } from './ipc/chatgpt.js';
import { ChatGptAuth } from './oauth/chatgpt-auth.js';
import {
  registerGitHubBotHandlers,
  registerGitHubHandlers,
  restartWorkerIfRunning,
} from './ipc/github.js';
import { GitHubBot } from './oauth/github-bot.js';
import { registerGitHubVerdictHandlers } from './ipc/github-verdict.js';
import {
  registerYouTubeHandlers,
  deleteYouTubeData,
  revokeYouTubeWriteGrant,
  youTubeWriteGrantHooks,
} from './ipc/youtube.js';
import {
  DEFAULT_GOOGLE_CLIENT_ID,
  DEFAULT_GOOGLE_CLIENT_SECRET,
  GoogleAuth,
} from './oauth/google-auth.js';
import { readKubernetesSelection, registerKubernetesHandlers } from './ipc/kubernetes.js';
import { readAllowedUserIds, registerAccessHandlers } from './ipc/access.js';
import { readSlackTokens, registerSlackHandlers } from './ipc/slack.js';
import { registerDesktopHandlers, type DesktopHandle } from './ipc/desktop.js';
import { installAppLifecycle, launchWindow } from './shell/app-lifecycle.js';
import { createLoginItem, refreshLoginEntry } from './shell/login-item.js';
import { ownExecutable } from './shell/own-executable.js';
import { restartThroughX11, x11RelaunchArgs } from './shell/x11-relaunch.js';
import { createPresenceNotifier, reportPairing } from './shell/notifications.js';
import { createTrayController, type TrayController } from './tray/tray.js';
import {
  canSelfUpdate,
  registerUpdateNotice,
  type UpdateNoticeHandle,
} from './update/update-notice.js';
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
  resolveMediaTools,
  resolveProtoDir,
  resolveTrayIcon,
  resolveWorkerBinary,
} from './worker/resolve-binary.js';
import { CHANNEL } from '@scout/shared';
import { connectControlPlane, type ControlPlaneConnection } from './control-plane/launcher.js';
import type { ControlPlaneClient } from './control-plane/client.js';
import { buildEnv, migrateLegacyApiKey, readGrantedWriteFeatures } from './worker/build-env.js';
import { chooseWhisperModel } from './worker/whisper-model.js';
import type { Supervisor } from './worker/supervisor.js';
import {
  allowMicrophoneOnlyForApp,
  allowScreenCaptureOnlyForApp,
  openQuickAsk,
  registerQuickAsk,
  withSystemAudioFeatures,
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
  "img-src 'self' data: scout-media:",
  'media-src scout-media:',
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
  "img-src 'self' data: scout-media:",
  'media-src scout-media:',
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
let unregisterOperator: (() => void) | null = null;
let unregisterProviderKeys: (() => void) | null = null;
let unregisterChatGpt: (() => void) | null = null;
let chatGptAuth: ChatGptAuth | null = null;
let unregisterGitHub: (() => void) | null = null;
let unregisterKubernetes: (() => void) | null = null;
let unregisterAccess: (() => void) | null = null;
let unregisterSlack: (() => void) | null = null;
let gitHubAuth: GitHubAuth | null = null;
let gitHubBot: GitHubBot | null = null;
let unregisterGitHubBot: (() => void) | null = null;
let unregisterGitHubVerdict: (() => void) | null = null;
let unregisterYouTube: (() => void) | null = null;
let googleAuth: GoogleAuth | null = null;
let quickAsk: QuickAsk | null = null;
let operatorWindow: OperatorWindow | null = null;
let trayController: TrayController | null = null;
let desktop: DesktopHandle | null = null;
let updateNotice: UpdateNoticeHandle | null = null;
let openMainWindow: ((show: boolean) => BrowserWindow) | null = null;

function sendToAllWindows(channel: string, log: typeof logger): void {
  for (const target of BrowserWindow.getAllWindows()) {
    try {
      if (!target.isDestroyed()) target.webContents.send(channel);
    } catch {
      log.debug(`${channel}: a window closed before the change reached it`);
    }
  }
}

function disposeAll(): void {
  quickAsk?.dispose();
  operatorWindow?.dispose();
  trayController?.destroy();
  updateNotice?.dispose();
  desktop?.dispose();
  unregisterProviderKeys?.();
  chatGptAuth?.stop();
  unregisterChatGpt?.();
  controlPlane?.dispose();
  gitHubAuth?.stop();
  unregisterGitHub?.();
  gitHubBot?.stop();
  unregisterGitHubBot?.();
  googleAuth?.stop();
  unregisterYouTube?.();
  unregisterKubernetes?.();
  unregisterAccess?.();
  unregisterSlack?.();
  unregisterFeatures?.();
  unregisterGitHubVerdict?.();
  unregisterOperator?.();
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

protocol.registerSchemesAsPrivileged([MEDIA_SCHEME_PRIVILEGES]);

if (process.platform === 'linux') {
  app.commandLine.appendSwitch(
    'enable-features',
    withSystemAudioFeatures(app.commandLine.getSwitchValue('enable-features')),
  );
}

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
    const botLogger = logger.child({ module: 'github-bot' });
    const bot = new GitHubBot({
      fetch: globalThis.fetch,
      vault,
      tokenFilePath: path.join(app.getPath('userData'), 'github-bot-token.json'),
      fs: { writeFile: fsWriteFile, rename: fsRename, rm: fsRm },
      now: () => Date.now(),
      awaitCallback,
      findFreePort,
      openExternal: (url) => shell.openExternal(url),
      ownerLogin: () => auth.login(),
      onChanged: () => {
        if (supervisor !== null) void restartWorkerIfRunning(supervisor, botLogger);
      },
      onResume: (listener) => {
        powerMonitor.on('resume', listener);
        return () => powerMonitor.removeListener('resume', listener);
      },
      logger: botLogger,
    });
    gitHubBot = bot;
    unregisterGitHubBot = registerGitHubBotHandlers(ipcMain, bot, botLogger);
    bot.start().catch((err: unknown) => {
      botLogger.warn('github-bot: start failed', { err: String(err) });
    });

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

    const youTubeLogger = logger.child({ module: 'youtube' });
    const featuresChanged = (): void => sendToAllWindows(CHANNEL.featuresChanged, youTubeLogger);
    const youTubeControlPlane = (): Promise<ControlPlaneClient> => connection.client;
    const google = new GoogleAuth({
      fetch: globalThis.fetch,
      awaitCallback,
      findFreePort,
      openExternal: (url) => shell.openExternal(url),
      vault,
      tokenFilePath: path.join(app.getPath('userData'), 'youtube-token.json'),
      clientConfigPath: path.join(app.getPath('userData'), 'google-oauth-client.json'),
      env: process.env,
      bundledClientPath: path.join(
        app.getAppPath(),
        'packages',
        'main',
        'resources',
        'google-client.json',
      ),
      defaultClient: {
        clientId: DEFAULT_GOOGLE_CLIENT_ID,
        clientSecret: DEFAULT_GOOGLE_CLIENT_SECRET,
      },
      fs: {
        readFile: (p) => fsReadFile(p, 'utf8'),
        writeFile: fsWriteFile,
        rename: fsRename,
        rm: fsRm,
      },
      now: () => Date.now(),
      onConnectionChanged: () => {
        if (supervisor !== null) void restartWorkerIfRunning(supervisor, youTubeLogger);
      },
      revokeWriteGrant: () =>
        revokeYouTubeWriteGrant(youTubeControlPlane, youTubeLogger, featuresChanged),
      onResume: (listener) => {
        powerMonitor.on('resume', listener);
        return () => powerMonitor.removeListener('resume', listener);
      },
      logger: youTubeLogger,
    });
    googleAuth = google;
    unregisterYouTube = registerYouTubeHandlers(
      ipcMain,
      google,
      youTubeLogger,
      youTubeControlPlane,
      featuresChanged,
    );
    google.start().catch((err: unknown) => {
      youTubeLogger.warn('youtube: start failed', { err: String(err) });
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
          gitHubBot: await bot.workerConfig(),
          youTubeEnv: await google.workerEnv(),
          kubernetesSelection: await readKubernetesSelection(vault),
          allowedUserIds: await readAllowedUserIds(vault),
          pairing: access.pendingPairing(),
          slackTokens: await readSlackTokens(vault),
          whisperModelPath: await chooseWhisperModel(
            whisperRootDir(app.getPath('userData')),
            await vault.get('stt.model').catch(() => null),
          ),
          grantedWriteFeatures: await readGrantedWriteFeatures(
            connection.client,
            logger.child({ module: 'supervisor' }),
          ),
          workerGate: connection.workerGate(),
          mediaTools: resolveMediaTools({
            app,
            resourcesPath: process.resourcesPath,
            cwd: __dirname,
          }),
          locale: app.getLocale(),
          userDataDir: app.getPath('userData'),
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

    const loginItemDeps = {
      platform: process.platform,
      home: os.homedir(),
      executable: ownExecutable(process.env, process.execPath),
      app,
    };
    void refreshLoginEntry(loginItemDeps).catch((err: unknown) => {
      logger.warn('start-at-login entry not refreshed', { err: String(err) });
    });
    desktop = await registerDesktopHandlers(ipcMain, {
      vault,
      loginItem: createLoginItem(loginItemDeps),
      trayAvailable: trayController !== null,
      logger: logger.child({ module: 'desktop' }),
    });

    updateNotice = await registerUpdateNotice({
      ipcMain,
      vault,
      fetch: globalThis.fetch,
      logger: logger.child({ module: 'update' }),
      currentVersion: app.getVersion(),
      packaged: app.isPackaged,
      platform: process.platform,
      arch: process.arch,
      env: process.env,
      execPath: process.execPath,
      windows: () => BrowserWindow.getAllWindows(),
      now: () => new Date(),
      setTimer: (ms, fn) => {
        const timer = setTimeout(fn, ms);
        return () => clearTimeout(timer);
      },
      onNoticeChanged: (version) => trayController?.setUpdateAvailable(version),
      updater: canSelfUpdate(app.isPackaged, process.platform, process.env, process.execPath)
        ? autoUpdater
        : null,
      prepareForRestart: () => lifecycle.prepareForRestart(),
      recoverFromFailedRestart: () => lifecycle.recoverFromFailedRestart(),
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

    const featuresLogger = logger.child({ module: 'features' });
    unregisterFeatures = registerFeaturesHandlers(
      ipcMain,
      () => connection.client,
      featuresLogger,
      {
        onWriteGrantChanged: () => {
          sendToAllWindows(CHANNEL.featuresChanged, featuresLogger);
          if (supervisor !== null) void restartWorkerIfRunning(supervisor, featuresLogger);
        },
        onActionsChanged: () => sendToAllWindows(CHANNEL.actionsChanged, featuresLogger),
        ...youTubeWriteGrantHooks(google, () =>
          deleteYouTubeData(youTubeControlPlane, youTubeLogger),
        ),
      },
    );
    const verdictLogger = logger.child({ module: 'github-verdict' });
    unregisterGitHubVerdict = registerGitHubVerdictHandlers(
      ipcMain,
      () => connection.client,
      (status) => {
        for (const target of BrowserWindow.getAllWindows()) {
          if (!target.isDestroyed()) target.webContents.send(CHANNEL.githubVerdict, status);
        }
      },
      verdictLogger,
    );
    unregisterOperator = registerOperatorHandlers(
      ipcMain,
      () => connection.client,
      logger.child({ module: 'operator' }),
      chooseWithDialog(dialog),
    );
    protocol.handle(
      MEDIA_SCHEME,
      createMediaProtocolHandler(
        async (id) => (await connection.client).getMediaDirectory(id),
        Date.now,
        async () => (await connection.client).getVoiceSamplesDirectory(),
      ),
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
      whisperRootDir(app.getPath('userData')),
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

    const appPage = { indexHtmlPath, ...(DEV ? { devServerUrl: 'http://localhost:4200' } : {}) };
    allowMicrophoneOnlyForApp(session.defaultSession, appPage);
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
      locale: () => app.getLocale(),
      showSaveDialog: (options) => dialog.showSaveDialog(options),
      writeFile: (file, text) => fsWriteFile(file, text, 'utf8'),
      documentsDir: () => app.getPath('documents'),
    });
    allowScreenCaptureOnlyForApp(
      session.defaultSession,
      appPage,
      desktopCapturer,
      quickAsk.screenCapture,
    );
    operatorWindow = await registerOperatorWindow({
      ipcMain,
      createWindow: (opts) => new BrowserWindow(opts),
      preloadPath,
      indexHtmlPath,
      ...(DEV ? { devServerUrl: 'http://localhost:4200' } : {}),
      vault,
      screen,
      logger: logger.child({ module: 'operator-window' }),
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
