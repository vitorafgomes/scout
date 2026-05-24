import { app, BrowserWindow, ipcMain, shell, session } from 'electron';
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
import { VaultUnavailableError } from '@scout/shared';
import type { Supervisor } from './worker/supervisor.js';

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

        // .NET configuration env-var convention: each ":" path segment is
        // mapped to "__". The worker binds Scout:Llm from configuration, so
        //   Scout:Llm:Provider          -> SCOUT__LLM__PROVIDER
        //   Scout:Llm:Ollama:Model      -> SCOUT__LLM__OLLAMA__MODEL
        //   Scout:Llm:Ollama:Endpoint   -> SCOUT__LLM__OLLAMA__ENDPOINT
        // Verified against src/Scout.Core/Llm/LlmOptions.cs.
        const llmProvider = (await vault.get('llm.provider')) ?? 'Ollama';
        const llmModel = await vault.get('llm.model');
        const llmEndpoint = await vault.get('llm.endpoint');

        // env is passed to spawn; never logged
        const env: Record<string, string> = {
          SCOUT__CHATADAPTER: 'Discord',
          SCOUT__LLM__PROVIDER: llmProvider,
          DOTNET_ENVIRONMENT: 'Production',
          SCOUT__ADAPTERS__DISCORD__BOTTOKEN: token,
        };
        if (llmModel !== null) env['SCOUT__LLM__OLLAMA__MODEL'] = llmModel;
        if (llmEndpoint !== null) env['SCOUT__LLM__OLLAMA__ENDPOINT'] = llmEndpoint;
        return env;
      },
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

    mainWindow.on('closed', () => {
      mainWindow = null;
    });

    attachBrowserWindow(mainWindow, supervisor);

    logger.info('main window created', { dev: DEV });
  })
  .catch((err: unknown) => {
    logger.error('app.whenReady failed', err);
    app.quit();
  });

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
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
  void runningWorker
    .stop()
    .catch((err: unknown) => {
      logger.warn('before-quit: worker stop failed', { err: String(err) });
    })
    .finally(() => {
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
    });
});

app.on('second-instance', () => {
  if (mainWindow != null && !mainWindow.isDestroyed()) {
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.focus();
  }
});
