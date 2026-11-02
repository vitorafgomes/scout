import type { BrowserWindow, IpcMain } from 'electron';
import {
  CHANNEL,
  compareVersions,
  encodeIpcErrorMessage,
  pickDownload,
  serializeError,
  type DesktopToggleRequest,
  type ReleaseAsset,
  type UpdateNotice,
  type UpdateProgress,
  type UpdateSkipVersionRequest,
  type UpdateState,
} from '@scout/shared';
import { ownAppImage } from '../shell/own-executable.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';

const LATEST_RELEASE_URL = 'https://api.github.com/repos/vitorafgomes/scout/releases/latest';
const RELEASES_PAGE_URL = 'https://github.com/vitorafgomes/scout/releases';
const CHECK_ENABLED_KEY = 'update.check-enabled';
const SKIPPED_VERSION_KEY = 'update.skipped-version';
const LAST_CHECK_KEY = 'update.last-check';
const FIRST_CHECK_DELAY_MS = 10_000;
const CHECK_INTERVAL_MS = 86_400_000;
const REQUEST_TIMEOUT_MS = 15_000;
const PREPARE_RESTART_TIMEOUT_MS = 10_000;
const FAILURE_MESSAGES: Readonly<Record<string, string>> = {
  checksum: 'The download did not match the release; nothing was installed',
  timeout: 'The download timed out; nothing was installed',
  'no asset': 'This release has no update for this package; nothing was installed',
  incomplete: 'The download did not finish; nothing was installed',
  apply: 'Scout could not replace its own file; it keeps running this version',
};

export interface UpdaterProgress {
  percent: number;
  transferred: number;
  total: number;
}

/** The part of electron-updater's AppUpdater that Update and restart uses. */
export interface Updater {
  autoDownload: boolean;
  autoInstallOnAppQuit: boolean;
  allowPrerelease: boolean;
  checkForUpdates(): Promise<{ readonly updateInfo: { readonly version: string } } | null>;
  downloadUpdate(): Promise<unknown>;
  quitAndInstall(isSilent?: boolean, isForceRunAfter?: boolean): void;
  on(event: 'download-progress', listener: (info: UpdaterProgress) => void): unknown;
  on(event: 'update-downloaded', listener: () => void): unknown;
  on(event: 'error', listener: (err: Error) => void): unknown;
  removeListener(event: 'download-progress', listener: (info: UpdaterProgress) => void): unknown;
  removeListener(event: 'update-downloaded', listener: () => void): unknown;
  removeListener(event: 'error', listener: (err: Error) => void): unknown;
}

/**
 * True when this package can replace itself: the packaged Windows installer, or a packaged AppImage whose
 * executable runs from the mount in `APPDIR`, so an `APPIMAGE` inherited from another AppImage never counts.
 */
export function canSelfUpdate(
  packaged: boolean,
  platform: string,
  env: Readonly<Record<string, string | undefined>>,
  execPath: string,
): boolean {
  if (!packaged) return false;
  return platform === 'win32' || (platform === 'linux' && ownAppImage(env, execPath) !== null);
}

function failureKind(err: unknown): string {
  const { code, statusCode, message } = (err ?? {}) as Record<string, unknown>;
  const text = typeof message === 'string' ? message : String(err);
  if (code === 'ERR_CHECKSUM_MISMATCH' || /sha512/i.test(text)) return 'checksum';
  if (code === 'ERR_UPDATER_CHANNEL_FILE_NOT_FOUND' || code === 'ERR_UPDATER_ASSET_NOT_FOUND') {
    return 'no asset';
  }
  if (typeof statusCode === 'number') return `http ${statusCode}`;
  const status = /\bstatus (\d{3})\b/.exec(text);
  if (status !== null) return `http ${status[1]}`;
  if (/timed?[ _]?out/i.test(text)) return 'timeout';
  return 'network';
}

export interface UpdateNoticeDeps {
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>;
  vault: Pick<Vault, 'get' | 'store'>;
  fetch: typeof globalThis.fetch;
  /** How long one request may take before it is aborted; 15 s unless set. */
  requestTimeoutMs?: number;
  logger: Logger;
  currentVersion: string;
  /** False in a development build, which never checks. */
  packaged: boolean;
  platform: string;
  arch: string;
  env: Record<string, string | undefined>;
  /** The running executable; inside an AppImage it sits under `APPDIR`. */
  execPath: string;
  windows: () => readonly BrowserWindow[];
  now: () => Date;
  /** Runs fn once after ms and returns a function that cancels it. */
  setTimer: (ms: number, fn: () => void) => () => void;
  /** The tray's hook: the version on offer, or null when nothing is. */
  onNoticeChanged: (version: string | null) => void;
  /** electron-updater's autoUpdater when this package can self-update, otherwise null. */
  updater: Updater | null;
  /** Stops the worker and frees the single-instance lock, so the new version can start while this one exits. */
  prepareForRestart: () => Promise<void>;
  /** Undoes prepareForRestart when the updater could not apply the update and Scout keeps running. */
  recoverFromFailedRestart: () => Promise<void>;
}

export interface UpdateNoticeHandle {
  dispose(): void;
}

class CheckFailed extends Error {
  constructor(readonly kind: string) {
    super(kind);
  }
}

const normalise = (version: string): string => version.trim().replace(/^v/i, '');

/**
 * Checks GitHub Releases for a newer Scout Desktop at start-up and daily and offers it to every window; downloads
 * and installs only when the user clicks Update and restart, and only where the package can replace itself.
 */
export async function registerUpdateNotice(deps: UpdateNoticeDeps): Promise<UpdateNoticeHandle> {
  const { ipcMain, vault, logger, currentVersion, packaged } = deps;

  const read = (key: Parameters<typeof vault.get>[0]) =>
    vault.get(key).catch((err: unknown) => {
      logger.warn('update: vault read failed', { key, code: serializeError(err).code });
      return null;
    });
  let checkEnabled = (await read(CHECK_ENABLED_KEY)) !== 'false';
  let skippedVersion = await read(SKIPPED_VERSION_KEY);
  let lastCheck: { at: string; result: string } | null = null;
  try {
    const stored = JSON.parse((await read(LAST_CHECK_KEY)) ?? 'null') as unknown;
    if (
      typeof stored === 'object' &&
      stored !== null &&
      typeof (stored as Record<string, unknown>)['at'] === 'string' &&
      typeof (stored as Record<string, unknown>)['result'] === 'string'
    ) {
      lastCheck = stored as { at: string; result: string };
    }
  } catch {
    lastCheck = null;
  }
  let notice: UpdateNotice | null = null;
  let inFlight: Promise<void> | null = null;
  let cancelTimer: (() => void) | null = null;
  let disposed = false;
  let installing = false;
  let detachUpdater: (() => void) | null = null;
  const canInstall =
    deps.updater !== null && canSelfUpdate(packaged, deps.platform, deps.env, deps.execPath);

  function state(): UpdateState {
    return {
      checkEnabled,
      lastCheckAt: lastCheck?.at ?? null,
      lastResult: lastCheck?.result ?? null,
      notice,
      packaged,
    };
  }

  function broadcast(channel: string, payload: unknown): void {
    for (const target of deps.windows()) {
      try {
        if (!target.isDestroyed()) target.webContents.send(channel, payload);
      } catch {
        logger.debug('update: a window closed before the message reached it');
      }
    }
  }

  function setNotice(next: UpdateNotice | null): void {
    notice = next;
    broadcast(CHANNEL.updateNotice, next);
    deps.onNoticeChanged(next?.latestVersion ?? null);
  }

  function progress(phase: UpdateProgress['phase'], extra: Partial<UpdateProgress> = {}): void {
    const payload: UpdateProgress = {
      phase,
      percent: null,
      transferred: null,
      total: null,
      message: null,
      ...extra,
    };
    broadcast(CHANNEL.updateProgress, payload);
  }

  async function record(result: string): Promise<void> {
    lastCheck = { at: deps.now().toISOString(), result };
    await vault.store(LAST_CHECK_KEY, JSON.stringify(lastCheck)).catch(() => {
      logger.info('update: last check not stored');
    });
  }

  async function fetchLatest(): Promise<Record<string, unknown>> {
    let response: Response;
    try {
      response = await deps.fetch(LATEST_RELEASE_URL, {
        method: 'GET',
        headers: {
          Accept: 'application/vnd.github+json',
          'User-Agent': `Scout-Desktop/${currentVersion}`,
        },
        signal: AbortSignal.timeout(deps.requestTimeoutMs ?? REQUEST_TIMEOUT_MS),
      });
    } catch (err: unknown) {
      throw new CheckFailed(
        err instanceof Error && err.name === 'TimeoutError' ? 'timeout' : 'network',
      );
    }
    if (!response.ok) throw new CheckFailed(`http ${response.status}`);
    let body: unknown;
    try {
      body = await response.json();
    } catch {
      throw new CheckFailed('parse');
    }
    if (typeof body !== 'object' || body === null) throw new CheckFailed('parse');
    if (typeof (body as Record<string, unknown>)['tag_name'] !== 'string') {
      throw new CheckFailed('parse');
    }
    return body as Record<string, unknown>;
  }

  async function runCheck(): Promise<void> {
    let result: string;
    try {
      const release = await fetchLatest();
      const latest = normalise(release['tag_name'] as string);
      const text = (field: string): string =>
        typeof release[field] === 'string' ? (release[field] as string) : '';
      if (
        release['draft'] === true ||
        release['prerelease'] === true ||
        compareVersions(latest, currentVersion) <= 0
      ) {
        result = 'up-to-date';
        setNotice(null);
      } else if (latest === skippedVersion) {
        result = `skipped ${latest}`;
        setNotice(null);
      } else {
        const releaseUrl = text('html_url') || RELEASES_PAGE_URL;
        const assets = (Array.isArray(release['assets']) ? release['assets'] : []).filter(
          (asset): asset is ReleaseAsset =>
            typeof asset?.name === 'string' && typeof asset?.browser_download_url === 'string',
        );
        result = `available ${latest}`;
        setNotice({
          currentVersion,
          latestVersion: latest,
          notes: text('body').replace(/\r\n/g, '\n'),
          releaseUrl,
          downloadUrl: pickDownload(
            {
              assets,
              platform: deps.platform,
              arch: deps.arch,
              appImage: (deps.env['APPIMAGE'] ?? '') !== '',
            },
            releaseUrl,
          ),
          publishedAt: text('published_at'),
          canInstall,
        });
      }
      logger.info('update: checked', { result });
    } catch (err: unknown) {
      result = `error ${err instanceof CheckFailed ? err.kind : 'parse'}`;
      logger.info('update: check failed', { result });
    }
    await record(result);
  }

  async function prepareForRestart(): Promise<void> {
    const prepared = await new Promise<boolean>((resolve) => {
      const cancel = deps.setTimer(PREPARE_RESTART_TIMEOUT_MS, () => resolve(false));
      deps.prepareForRestart().then(
        () => {
          cancel();
          resolve(true);
        },
        (err: unknown) => {
          cancel();
          logger.warn('update: preparing the restart failed', { code: serializeError(err).code });
          resolve(true);
        },
      );
    });
    if (!prepared) {
      logger.warn('update: the restart was not prepared within 10 s; restarting anyway');
    }
  }

  async function install(target: UpdateNotice, updater: Updater): Promise<void> {
    let downloaded = false;
    let failure: unknown = null;
    let lastPercent: number | null = null;
    const onProgress = (info: UpdaterProgress): void => {
      const percent = Math.floor(info.percent);
      if (percent === lastPercent) return;
      lastPercent = percent;
      progress('downloading', { percent, transferred: info.transferred, total: info.total });
    };
    const onDownloaded = (): void => {
      downloaded = true;
      progress('ready');
    };
    const onError = (err: Error): void => {
      failure ??= err;
    };
    updater.on('download-progress', onProgress);
    updater.on('update-downloaded', onDownloaded);
    updater.on('error', onError);
    detachUpdater = () => {
      updater.removeListener('download-progress', onProgress);
      updater.removeListener('update-downloaded', onDownloaded);
      updater.removeListener('error', onError);
      detachUpdater = null;
    };
    let kind: string | null = null;
    let changed = false;
    try {
      updater.autoDownload = false;
      updater.autoInstallOnAppQuit = false;
      updater.allowPrerelease = false;
      progress('checking');
      const offered = await updater.checkForUpdates();
      changed = normalise(offered?.updateInfo.version ?? '') !== target.latestVersion;
      if (!changed) {
        await updater.downloadUpdate();
        if (!downloaded) throw new CheckFailed('incomplete');
        progress('installing');
        await prepareForRestart();
        failure = null;
        updater.quitAndInstall(false, true);
        if (failure !== null) {
          kind = 'apply';
          await deps.recoverFromFailedRestart();
        }
      }
    } catch (err: unknown) {
      kind = err instanceof CheckFailed ? err.kind : failureKind(err);
    } finally {
      detachUpdater?.();
    }
    if (changed) {
      logger.info('update: install stopped, the release changed');
      progress('failed', { message: 'The release changed; check again' });
      await check();
    } else if (kind !== null) {
      logger.warn('update: install failed', { kind });
      progress('failed', {
        message: FAILURE_MESSAGES[kind] ?? `The update failed (${kind}); nothing was installed`,
      });
      await record(`error install ${kind}`);
    }
  }

  function check(): Promise<void> {
    if (!packaged) return Promise.resolve();
    inFlight ??= runCheck().finally(() => {
      inFlight = null;
    });
    return inFlight;
  }

  function schedule(ms: number): void {
    cancelTimer = deps.setTimer(ms, () => {
      cancelTimer = null;
      void check().finally(() => {
        if (checkEnabled && !disposed && cancelTimer === null) schedule(CHECK_INTERVAL_MS);
      });
    });
  }

  function stopTimer(): void {
    cancelTimer?.();
    cancelTimer = null;
  }

  async function relay(name: string, call: () => Promise<UpdateState>): Promise<UpdateState> {
    try {
      return await call();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      logger.warn(`update: ${name} failed`, { code: serialized.code });
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
  }

  ipcMain.handle(CHANNEL.updateGetState, () => relay('getState', async () => state()));
  ipcMain.handle(CHANNEL.updateSetCheckEnabled, (_event, req: DesktopToggleRequest | undefined) =>
    relay('setCheckEnabled', async () => {
      const enabled = req?.enabled === true;
      checkEnabled = enabled;
      if (!enabled) stopTimer();
      else if (packaged && cancelTimer === null) schedule(FIRST_CHECK_DELAY_MS);
      await vault.store(CHECK_ENABLED_KEY, String(enabled)).catch((err: unknown) => {
        logger.warn('update: check setting not stored', { code: serializeError(err).code });
      });
      return state();
    }),
  );
  ipcMain.handle(CHANNEL.updateCheckNow, () =>
    relay('checkNow', async () => {
      await check();
      return state();
    }),
  );
  ipcMain.handle(CHANNEL.updateSkipVersion, (_event, req: UpdateSkipVersionRequest | undefined) =>
    relay('skipVersion', async () => {
      const version = normalise(typeof req?.version === 'string' ? req.version : '');
      if (version === '') return state();
      await vault.store(SKIPPED_VERSION_KEY, version);
      skippedVersion = version;
      setNotice(null);
      return state();
    }),
  );
  ipcMain.handle(CHANNEL.updateRemindLater, () =>
    relay('remindLater', async () => {
      setNotice(null);
      return state();
    }),
  );

  ipcMain.handle(CHANNEL.updateInstall, () =>
    relay('install', async () => {
      if (installing) return state();
      const target = notice;
      if (target === null || !target.canInstall || deps.updater === null) {
        progress('failed', {
          message:
            target === null
              ? 'There is no update to install'
              : 'This copy of Scout cannot update itself; use Download',
        });
        return state();
      }
      installing = true;
      try {
        await install(target, deps.updater);
      } finally {
        installing = false;
      }
      return state();
    }),
  );

  if (packaged && checkEnabled) schedule(FIRST_CHECK_DELAY_MS);

  return {
    dispose() {
      disposed = true;
      stopTimer();
      detachUpdater?.();
      ipcMain.removeHandler(CHANNEL.updateInstall);
      ipcMain.removeHandler(CHANNEL.updateRemindLater);
      ipcMain.removeHandler(CHANNEL.updateSkipVersion);
      ipcMain.removeHandler(CHANNEL.updateCheckNow);
      ipcMain.removeHandler(CHANNEL.updateSetCheckEnabled);
      ipcMain.removeHandler(CHANNEL.updateGetState);
    },
  };
}
