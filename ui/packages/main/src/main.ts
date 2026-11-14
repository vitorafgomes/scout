import {
  app,
  BrowserWindow,
  desktopCapturer,
  dialog,
  globalShortcut,
  ipcMain,
  Menu,
  nativeImage,
  nativeTheme,
  Notification,
  powerMonitor,
  protocol,
  screen,
  shell,
  session,
  Tray,
  type BrowserWindowConstructorOptions,
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
import { installAppMenu, withoutMenuBar } from './shell/app-menu.js';
import { registerDetectHandler } from './ipc/detect.js';
import { registerVaultHandlers } from './ipc/vault.js';
import { registerAppHandlers } from './ipc/app.js';
import { registerDiscordHandlers } from './ipc/discord.js';
import {
  registerWorkerHandlers,
  registerWorkerRestartHandler,
  attachBrowserWindow,
} from './ipc/worker.js';
import { registerLlmHandlers } from './ipc/llm.js';
import { registerLlmPullHandlers } from './ipc/llm-pull.js';
import { registerLmStudioHandlers } from './ipc/lm-studio.js';
import { createWhisperDownloader, registerWhisperHandlers, whisperRootDir } from './ipc/whisper.js';
import { registerFeaturesHandlers } from './ipc/features.js';
import {
  CREDENTIAL_GUARDS,
  QUICK_ASK_GUARDS,
  SHORTCUT_GUARDS,
  SHOW_MAIN_GUARDS,
  guardChannels,
  senderCheck,
  type SenderHooks,
} from './ipc/sender-check.js';
import {
  replayPendingRevokes,
  revokeFeatureWriteGrant,
  vaultPendingRevokes,
} from './ipc/write-grant-revoke.js';
import { registerOperatorHandlers } from './ipc/operator.js';
import { registerOperatorWindow, type OperatorWindow } from './operator/operator-window.js';
import {
  MEDIA_SCHEME,
  MEDIA_SCHEME_PRIVILEGES,
  chooseWithDialog,
  createMediaProtocolHandler,
} from './operator/media.js';
import { registerProviderKeyHandlers, registerProviderModelHandlers } from './ipc/provider-keys.js';
import {
  ADMIN_KEY_VAULT_KEYS,
  createOpenRouterKeyReader,
  createOrgSpendReader,
  createUsageStore,
  registerOrgSpendHandlers,
  registerUsageHandlers,
} from './ipc/usage.js';
import { registerChatGptHandlers } from './ipc/chatgpt.js';
import { ChatGptAuth } from './oauth/chatgpt-auth.js';
import {
  registerGitHubBotHandlers,
  registerGitHubHandlers,
  restartWorkerIfRunning,
} from './ipc/github.js';
import { GitHubBot } from './oauth/github-bot.js';
import { pushToWindows, registerGitHubVerdictHandlers } from './ipc/github-verdict.js';
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
import { registerDockerHandlers } from './ipc/docker.js';
import { createDocker, githubSnapshotsFolder, sandboxWorkFolder } from './docker/docker.js';
import { readAllowedUserIds, registerAccessHandlers } from './ipc/access.js';
import { readSlackTokens, registerSlackHandlers } from './ipc/slack.js';
import { registerDesktopHandlers, type DesktopHandle } from './ipc/desktop.js';
import { registerTheme, windowBackground, type ThemeHandle } from './shell/theme.js';
import { installAppLifecycle, launchWindow } from './shell/app-lifecycle.js';
import { createLoginItem, refreshLoginEntry } from './shell/login-item.js';
import { ownExecutable } from './shell/own-executable.js';
import { restartThroughX11, x11RelaunchArgs } from './shell/x11-relaunch.js';
import { createPresenceNotifier, reportPairing } from './shell/notifications.js';
import {
  NotificationStore,
  pushToWindows as pushNotifications,
  registerNotificationHandlers,
} from './notifications/notification-store.js';
import { verdictNotification, workerNotice } from './notifications/sources.js';
import { createVoiceNotice, registerNotificationActions } from './notifications/voice-notice.js';
import {
  createJobControls,
  registerJobControlHandler,
  type JobControls,
} from './notifications/job-controls.js';
import { GitHubActivityPoll } from './notifications/github-activity.js';
import { watchPendingApprovals, type PendingApprovals } from './notifications/pending-approvals.js';
import { watchWorkerHealth } from './notifications/worker-health.js';
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
  resolveWorkerBinaryWithOverride,
} from './worker/resolve-binary.js';
import { CHANNEL, ControlPlaneUnavailableError } from '@scout/shared';
import { connectControlPlane, type ControlPlaneConnection } from './control-plane/launcher.js';
import type { ControlPlaneClient } from './control-plane/client.js';
import { buildEnv, migrateLegacyApiKey, readGrantedWriteFeatures } from './worker/build-env.js';
import { chooseWhisperModel } from './worker/whisper-model.js';
import {
  createWhisperModelUser,
  createWorkerRestart,
  type WorkerRestart,
} from './worker/worker-restart.js';
import type { Supervisor } from './worker/supervisor.js';
import {
  allowMicrophoneOnlyForApp,
  allowScreenCaptureOnlyForApp,
  appPageCheck,
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
let workerRestart: WorkerRestart | null = null;
let loadedWhisperModel: string | null = null;
let unregisterFeatures: (() => void) | null = null;
let unregisterOperator: (() => void) | null = null;
let unregisterProviderKeys: (() => void) | null = null;
let unregisterUsage: (() => void) | null = null;
let unregisterChatGpt: (() => void) | null = null;
let chatGptAuth: ChatGptAuth | null = null;
let unregisterGitHub: (() => void) | null = null;
let unregisterKubernetes: (() => void) | null = null;
let unregisterDocker: (() => void) | null = null;
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
let theme: ThemeHandle | null = null;
let updateNotice: UpdateNoticeHandle | null = null;
let openMainWindow: ((show: boolean) => BrowserWindow) | null = null;
let notificationStore: NotificationStore | null = null;
let unregisterNotifications: (() => void) | null = null;
let unregisterNotificationActions: (() => void) | null = null;
let jobControls: JobControls | null = null;
let unregisterJobControls: (() => void) | null = null;
let gitHubActivity: GitHubActivityPoll | null = null;
let pendingApprovals: PendingApprovals | null = null;
let unwatchWorkerHealth: (() => void) | null = null;

function setTimer(ms: number, callback: () => void): () => void {
  const timer = setTimeout(callback, ms);
  return () => clearTimeout(timer);
}

const transparentWindows = new WeakSet<object>();

function newWindow(opts: BrowserWindowConstructorOptions): BrowserWindow {
  const created = new BrowserWindow(withoutMenuBar(opts));
  if (opts.transparent === true) transparentWindows.add(created);
  return created;
}

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
  gitHubActivity?.dispose();
  pendingApprovals?.dispose();
  unwatchWorkerHealth?.();
  unregisterNotifications?.();
  unregisterNotificationActions?.();
  unregisterJobControls?.();
  jobControls?.dispose();
  void notificationStore?.flush();
  theme?.dispose();
  unregisterProviderKeys?.();
  unregisterUsage?.();
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
  unregisterDocker?.();
  unregisterAccess?.();
  unregisterSlack?.();
  unregisterFeatures?.();
  unregisterGitHubVerdict?.();
  unregisterOperator?.();
  unregisterWhisper?.();
  workerRestart = null;
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
    installAppMenu({
      platform: process.platform,
      setApplicationMenu: (menu) => Menu.setApplicationMenu(menu),
      buildFromTemplate: (template) => Menu.buildFromTemplate(template),
    });
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

    const indexHtmlPath = path.join(
      __dirname,
      '..',
      '..',
      'renderer',
      'dist',
      'browser',
      'index.html',
    );
    const appPage = { indexHtmlPath, ...(DEV ? { devServerUrl: 'http://localhost:4200' } : {}) };
    const senders: SenderHooks = {
      windowOf: (sender) => {
        if (sender != null && mainWindow?.isDestroyed() === false) {
          if (sender === mainWindow.webContents) return 'main';
        }
        if (operatorWindow?.owns(sender) === true) return 'operator';
        return quickAsk?.windowOf(sender) ?? 'unknown';
      },
      isAppPage: appPageCheck(appPage),
    };
    const credentialIpc = guardChannels(
      ipcMain,
      senderCheck(senders, logger.child({ module: 'ipc' })),
      CREDENTIAL_GUARDS,
    );
    const vault = createVault(createProductionVaultDeps(logger.child({ module: 'vault' })));
    const currentWhisperModel = async (): Promise<string | null> =>
      chooseWhisperModel(
        whisperRootDir(app.getPath('userData')),
        await vault.get('stt.model').catch(() => null),
      );
    unregisterVault = registerVaultHandlers(
      credentialIpc,
      vault,
      logger.child({ module: 'vault' }),
      (key) => {
        if (key === 'stt.model') void workerRestart?.modelChanged();
      },
    );
    notifier = createPresenceNotifier({
      platform: process.platform,
      isSupported: () => Notification.isSupported(),
      show: (title, body) => new Notification({ title, body }).show(),
      vault,
      logger: notifierLogger,
    });
    const presence = notifier;

    const notifications = new NotificationStore({
      vault,
      push: pushNotifications(() => BrowserWindow.getAllWindows()),
      now: () => Date.now(),
      setTimer,
      logger: logger.child({ module: 'notifications' }),
    });
    notificationStore = notifications;
    unregisterNotifications = registerNotificationHandlers(ipcMain, notifications);
    void notifications.load();
    const whisperLogger = logger.child({ module: 'whisper' });
    const whisperDownloads = createWhisperDownloader(
      globalThis.fetch,
      whisperLogger,
      whisperRootDir(app.getPath('userData')),
      undefined,
      () => {
        voiceNotice.modelInstalled();
        void workerRestart?.modelChanged();
      },
    );
    const voiceNotice = createVoiceNotice({
      store: notifications,
      now: () => Date.now(),
      download: whisperDownloads,
      logger: logger.child({ module: 'voice-notice' }),
    });
    unregisterNotificationActions = registerNotificationActions(ipcMain, voiceNotice);

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

    unregisterApp = registerAppHandlers(
      guardChannels(
        ipcMain,
        senderCheck(senders, logger.child({ module: 'ipc' })),
        SHOW_MAIN_GUARDS,
      ),
      shell,
      app,
      () => lifecycle.showWindow(),
      logger.child({ module: 'app' }),
    );

    const discordLogger = logger.child({ module: 'discord' });
    unregisterDiscord = registerDiscordHandlers(credentialIpc, {
      fetch: globalThis.fetch,
      vault,
      onTokenChanged: () => {
        if (workerRestart !== null) void restartWorkerIfRunning(workerRestart, discordLogger);
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
      credentialIpc,
      chatGptAuth,
      logger.child({ module: 'chatgpt' }),
    );
    chatGptAuth.start().catch((err: unknown) => {
      logger.warn('chatgpt: start failed', { err: String(err) });
    });

    const pendingRevokes = vaultPendingRevokes(vault);
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
        gitHubActivity?.reconnected();
        sendToAllWindows(CHANNEL.featuresChanged, gitHubLogger);
        if (workerRestart !== null) void restartWorkerIfRunning(workerRestart, gitHubLogger);
      },
      revokeWriteGrant: () =>
        revokeFeatureWriteGrant(
          'github',
          async () => {
            if (controlPlane === null) throw new ControlPlaneUnavailableError();
            return controlPlane.client;
          },
          pendingRevokes,
          gitHubLogger,
          () => sendToAllWindows(CHANNEL.featuresChanged, gitHubLogger),
        ),
      onResume: (listener) => {
        powerMonitor.on('resume', listener);
        return () => powerMonitor.removeListener('resume', listener);
      },
      onTokenRefreshed: () => gitHubActivity?.tokenRefreshed(),
      logger: gitHubLogger,
    });
    gitHubAuth = auth;
    unregisterGitHub = registerGitHubHandlers(credentialIpc, auth, gitHubLogger);
    auth.start().catch((err: unknown) => {
      gitHubLogger.warn('github: start failed', { err: String(err) });
    });
    gitHubActivity = new GitHubActivityPoll({
      fetch: globalThis.fetch,
      account: async () => {
        const token = await auth.accessToken();
        const login = await auth.login();
        return token === null || login === null ? null : { token, login };
      },
      bot: async () => (await gitHubBot?.mentionTarget()) ?? null,
      record: (entry) => notifications.record(entry),
      clearEntries: () => notifications.remove((n) => n.source === 'github'),
      now: () => Date.now(),
      setTimer,
      logger: logger.child({ module: 'github-activity' }),
    });
    gitHubActivity.start();
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
        if (workerRestart !== null) void restartWorkerIfRunning(workerRestart, botLogger);
      },
      onResume: (listener) => {
        powerMonitor.on('resume', listener);
        return () => powerMonitor.removeListener('resume', listener);
      },
      logger: botLogger,
    });
    gitHubBot = bot;
    unregisterGitHubBot = registerGitHubBotHandlers(credentialIpc, bot, botLogger);
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
    const dockerWorkFolder = sandboxWorkFolder(process.platform, process.env, os.homedir());
    const docker = createDocker({
      exec: (cmd, args, timeoutMs) => createProductionDeps().exec(cmd, args, timeoutMs),
      dockerPath: 'docker',
      workFolder: dockerWorkFolder,
      snapshotsFolder: githubSnapshotsFolder(process.platform, process.env, os.homedir()),
      now: () => Date.now(),
      logger: logger.child({ module: 'docker' }),
    });
    const jobs = createJobControls({
      store: notifications,
      client: () => connection.client,
      setTimer,
      runningTest: (repository, pullRequest) => docker.running(repository, pullRequest),
      now: () => Date.now(),
      logger: logger.child({ module: 'job-controls' }),
    });
    jobControls = jobs;
    unregisterJobControls = registerJobControlHandler(
      ipcMain,
      jobs,
      senders,
      logger.child({ module: 'ipc' }),
    );

    const youTubeLogger = logger.child({ module: 'youtube' });
    const featuresChanged = (): void => sendToAllWindows(CHANNEL.featuresChanged, youTubeLogger);
    const youTubeControlPlane = (): Promise<ControlPlaneClient> => connection.client;
    void connection.client
      .then((client) =>
        replayPendingRevokes(client, pendingRevokes, youTubeLogger, featuresChanged),
      )
      .catch((err: unknown) => {
        youTubeLogger.warn('features: pending write revokes not sent', { err: String(err) });
      });
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
        featuresChanged();
        if (workerRestart !== null) void restartWorkerIfRunning(workerRestart, youTubeLogger);
      },
      revokeWriteGrant: () =>
        revokeYouTubeWriteGrant(
          youTubeControlPlane,
          pendingRevokes,
          youTubeLogger,
          featuresChanged,
        ),
      onResume: (listener) => {
        powerMonitor.on('resume', listener);
        return () => powerMonitor.removeListener('resume', listener);
      },
      logger: youTubeLogger,
    });
    googleAuth = google;
    unregisterYouTube = registerYouTubeHandlers(
      credentialIpc,
      google,
      youTubeLogger,
      youTubeControlPlane,
      pendingRevokes,
      featuresChanged,
    );
    google.start().catch((err: unknown) => {
      youTubeLogger.warn('youtube: start failed', { err: String(err) });
    });

    const kubernetesLogger = logger.child({ module: 'kubernetes' });
    unregisterKubernetes = registerKubernetesHandlers(credentialIpc, {
      vault,
      detect: () =>
        detectKubeconfig({ env: process.env, homedir: os.homedir(), logger: kubernetesLogger }),
      onSelectionChanged: () => {
        if (workerRestart !== null) void restartWorkerIfRunning(workerRestart, kubernetesLogger);
      },
      logger: kubernetesLogger,
    });

    unregisterDocker = registerDockerHandlers(ipcMain, docker);

    const accessLogger = logger.child({ module: 'access' });
    const access = registerAccessHandlers(credentialIpc, {
      vault,
      onAllowedUsersChanged: () => {
        if (workerRestart !== null) void restartWorkerIfRunning(workerRestart, accessLogger);
      },
      logger: accessLogger,
    });
    unregisterAccess = access.dispose;

    const slackLogger = logger.child({ module: 'slack' });
    unregisterSlack = registerSlackHandlers(credentialIpc, {
      vault,
      fetch: globalThis.fetch,
      onConnectionChanged: () => {
        if (workerRestart !== null) void restartWorkerIfRunning(workerRestart, slackLogger);
      },
      logger: slackLogger,
    });

    const usageLogger = logger.child({ module: 'usage' });
    const usage = createUsageStore({
      file: path.join(app.getPath('userData'), 'usage.json'),
      now: () => new Date(),
      logger: usageLogger,
      broadcast: (snapshot) => {
        for (const target of BrowserWindow.getAllWindows()) {
          try {
            if (!target.isDestroyed()) target.webContents.send(CHANNEL.usageChanged, snapshot);
          } catch {
            usageLogger.debug('usage: a window closed before the totals reached it');
          }
        }
      },
    });
    const unregisterUsageChannels = registerUsageHandlers(
      ipcMain,
      usage,
      createOpenRouterKeyReader({
        fetch: globalThis.fetch,
        apiKey: () => vault.get('llm.openrouter.api-key'),
        now: () => new Date(),
        logger: usageLogger,
      }),
    );
    const orgSpendReader = (provider: 'OpenAI' | 'Anthropic') =>
      createOrgSpendReader({
        provider,
        fetch: globalThis.fetch,
        adminKey: () => vault.get(ADMIN_KEY_VAULT_KEYS[provider]),
        now: () => new Date(),
        logger: usageLogger,
      });
    const unregisterOrgSpend = registerOrgSpendHandlers(
      credentialIpc,
      { OpenAI: orgSpendReader('OpenAI'), Anthropic: orgSpendReader('Anthropic') },
      vault,
    );
    unregisterUsage = () => {
      unregisterUsageChannels();
      unregisterOrgSpend();
    };

    supervisor = createSupervisor({
      spawn,
      logger: logger.child({ module: 'supervisor' }),
      resolveBinary: () =>
        resolveWorkerBinaryWithOverride({
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
          whisperModelPath: (loadedWhisperModel = await currentWhisperModel()),
          grantedWriteFeatures: await readGrantedWriteFeatures(
            connection.client,
            logger.child({ module: 'supervisor' }),
            pendingRevokes,
          ),
          workerGate: connection.workerGate(),
          mediaTools: resolveMediaTools({
            app,
            resourcesPath: process.resourcesPath,
            cwd: __dirname,
          }),
          locale: app.getLocale(),
          userDataDir: app.getPath('userData'),
          sandboxWorkDirectory: dockerWorkFolder,
        }),
      onPaired: (userId) =>
        void reportPairing(userId, access, () => readAllowedUserIds(vault), presence),
      onPairingBurned: () => access.pairingBurned(),
      onLocalEvent: (json) => {
        usage.handleWorkerLine(json);
        quickAsk?.handleWorkerLine(json);
        jobs.handleWorkerLine(json);
        const notice = workerNotice(json, () => notifications.nextId('worker'));
        if (notice !== null) jobs.notice(notice);
        voiceNotice.handleWorkerLine(json);
      },
    });

    const workerSupervisor = supervisor;
    const restart = createWorkerRestart({
      supervisor: workerSupervisor,
      currentModel: currentWhisperModel,
      loadedModel: () => loadedWhisperModel,
      listening: () => quickAsk?.isListening() ?? false,
      answering: () => quickAsk?.isAnswering() ?? false,
      store: notifications,
      onRestarted: () => voiceNotice.restarted(),
      onRestartSettled: () => quickAsk?.restartSettled(),
      logger: logger.child({ module: 'worker-restart' }),
    });
    workerRestart = restart;
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
      if (event.status.state === 'crashed' || event.status.state === 'stopped') {
        jobs.workerStopped();
      }
    });
    unwatchWorkerHealth = watchWorkerHealth(workerSupervisor, notifications);

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
    theme = await registerTheme({
      ipcMain,
      vault,
      nativeTheme,
      windows: () => BrowserWindow.getAllWindows(),
      transparent: (target) => transparentWindows.has(target),
      logger: logger.child({ module: 'theme' }),
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

    const unregisterWorkerHandlers = registerWorkerHandlers(
      ipcMain,
      supervisor,
      logger.child({ module: 'worker' }),
    );
    const unregisterWorkerRestart = registerWorkerRestartHandler(
      ipcMain,
      restart,
      logger.child({ module: 'worker' }),
    );
    unregisterWorker = () => {
      unregisterWorkerRestart();
      unregisterWorkerHandlers();
    };

    const featuresLogger = logger.child({ module: 'features' });
    unregisterFeatures = registerFeaturesHandlers(
      ipcMain,
      () => connection.client,
      featuresLogger,
      {
        onWriteGrantChanged: () => {
          sendToAllWindows(CHANNEL.featuresChanged, featuresLogger);
          if (workerRestart !== null) void restartWorkerIfRunning(workerRestart, featuresLogger);
        },
        onActionsChanged: () => {
          sendToAllWindows(CHANNEL.actionsChanged, featuresLogger);
          void pendingApprovals?.check();
        },
        ...senders,
        ...youTubeWriteGrantHooks(google, () =>
          deleteYouTubeData(youTubeControlPlane, youTubeLogger),
        ),
      },
    );
    pendingApprovals = watchPendingApprovals({
      list: async () => (await connection.client).listPendingActions(),
      store: notifications,
      setTimer,
      logger: logger.child({ module: 'approvals' }),
    });
    void pendingApprovals.check();
    const verdictLogger = logger.child({ module: 'github-verdict' });
    const pushVerdict = pushToWindows(() => BrowserWindow.getAllWindows());
    unregisterGitHubVerdict = registerGitHubVerdictHandlers(
      ipcMain,
      () => connection.client,
      (status) => {
        pushVerdict(status);
        notifications.record(verdictNotification(status));
      },
      verdictLogger,
      senders,
    );
    unregisterOperator = registerOperatorHandlers(
      ipcMain,
      () => connection.client,
      logger.child({ module: 'operator' }),
      chooseWithDialog(dialog),
      senders,
    );
    protocol.handle(
      MEDIA_SCHEME,
      createMediaProtocolHandler(
        async (id) => (await connection.client).getMediaDirectory(id),
        Date.now,
        async () => (await connection.client).getVoiceSamplesDirectory(),
      ),
    );

    unregisterLlm = registerLlmHandlers(
      ipcMain,
      globalThis.fetch,
      logger.child({ module: 'llm' }),
      (key) => vault.get(key),
    );
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
      whisperLogger,
      whisperRootDir(app.getPath('userData')),
      undefined,
      whisperDownloads,
      {
        useModel: createWhisperModelUser(vault, restart),
        readPreferred: () => vault.get('stt.model'),
        onDeleted: () => void restart.modelChanged(),
        loadedModel: () => restart.loadedWhileRunning(),
      },
    );

    session.defaultSession.webRequest.onHeadersReceived((details, callback) => {
      callback({
        responseHeaders: {
          ...details.responseHeaders,
          'Content-Security-Policy': [DEV ? CSP_DEV : CSP_PROD],
        },
      });
    });

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

    allowMicrophoneOnlyForApp(session.defaultSession, appPage);
    quickAsk = await registerQuickAsk({
      ipcMain: guardChannels(ipcMain, senderCheck(senders, logger.child({ module: 'ipc' })), {
        ...SHORTCUT_GUARDS,
        ...QUICK_ASK_GUARDS,
      }),
      supervisor: workerSupervisor,
      createWindow: newWindow,
      preloadPath,
      indexHtmlPath,
      ...(DEV ? { devServerUrl: 'http://localhost:4200' } : {}),
      globalShortcut,
      vault,
      logger: logger.child({ module: 'quick-ask' }),
      screen,
      setTimer,
      platform: process.platform,
      env: process.env,
      execPath: process.execPath,
      windows: () => BrowserWindow.getAllWindows(),
      locale: () => app.getLocale(),
      showSaveDialog: (options) => dialog.showSaveDialog(options),
      writeFile: (file, text) => fsWriteFile(file, text, 'utf8'),
      documentsDir: () => app.getPath('documents'),
      background: () => windowBackground(nativeTheme),
      onListeningChanged: (listening) => workerRestart?.listeningChanged(listening),
      onAnsweringChanged: (answering) => workerRestart?.answeringChanged(answering),
      restarting: () => workerRestart?.restarting() ?? false,
    });
    allowScreenCaptureOnlyForApp(
      session.defaultSession,
      appPage,
      desktopCapturer,
      quickAsk.screenCapture,
    );
    operatorWindow = await registerOperatorWindow({
      ipcMain,
      createWindow: newWindow,
      preloadPath,
      indexHtmlPath,
      ...(DEV ? { devServerUrl: 'http://localhost:4200' } : {}),
      vault,
      screen,
      background: () => windowBackground(nativeTheme),
      logger: logger.child({ module: 'operator-window' }),
    });

    openMainWindow = (show) => {
      const window = createMainWindow({
        createWindow: newWindow,
        logger,
        preloadPath,
        indexHtmlPath,
        showOnReady: show,
        background: windowBackground(nativeTheme),
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
