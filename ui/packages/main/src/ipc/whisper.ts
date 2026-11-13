import type { IpcMain, WebContents } from 'electron';
import { createHash } from 'node:crypto';
import { createWriteStream } from 'node:fs';
import { mkdir, rm, stat, rename } from 'node:fs/promises';
import * as path from 'node:path';
import {
  CHANNEL,
  ScoutError,
  WHISPER_DOWNLOAD_CANCELLED,
  encodeIpcErrorMessage,
  serializeError,
  type WhisperCancelDownloadRequest,
  type WhisperDeleteModelRequest,
  type WhisperDeleteModelResponse,
  type WhisperDownloadModelRequest,
  type WhisperDownloadModelResponse,
  type WhisperDownloadProgressEvent,
  type WhisperInstalledModel,
  type WhisperListInstalledResponse,
  type WhisperModelTableResponse,
  type WhisperUseModelRequest,
  type WhisperUseModelResponse,
  type WhisperVariant,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';
import {
  WHISPER_MODEL_FILES,
  chooseWhisperModel,
  variantOf,
  verifyWhisperModel,
  writeWhisperSidecar,
  type WhisperModelTable,
} from '../worker/whisper-model.js';

const HF_BASE = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main';

/** How long a download may go without a byte from HuggingFace before it stops. */
export const WHISPER_STALL_MS = 60_000;

/** The folder under Scout's user data that holds the Whisper models. */
export function whisperRootDir(userData: string): string {
  return path.join(userData, 'whisper-models');
}

async function fileExists(p: string): Promise<boolean> {
  try {
    const s = await stat(p);
    return s.isFile() && s.size > 0;
  } catch {
    return false;
  }
}

/** Downloads one pinned Whisper model; `onProgress` gets the percent and the bytes received. */
export interface WhisperDownloader {
  (
    variant: WhisperVariant,
    onProgress: (percent: number, completedBytes: number) => void,
  ): Promise<WhisperDownloadModelResponse>;
  /** Stops the running download of `variant`, if any; every caller waiting on it sees it reject. */
  cancel?(variant: WhisperVariant): void;
}

interface Stop {
  readonly controller: AbortController;
  reason: string | null;
  cancelled: boolean;
  reader: ReadableStreamDefaultReader<Uint8Array> | null;
}

function stopDownload(stop: Stop, reason: string, cancelled = false): void {
  if (stop.reason !== null) return;
  stop.reason = reason;
  stop.cancelled = cancelled;
  stop.controller.abort();
  void stop.reader?.cancel().catch(() => undefined);
}

/** Why a stopped download rejects: a Cancel carries `WHISPER_DOWNLOAD_CANCELLED`, a stall a plain reason. */
function stoppedError(stop: Stop): Error {
  const message = `whisper: ${stop.reason ?? 'the download stopped'}`;
  return stop.cancelled ? new ScoutError(WHISPER_DOWNLOAD_CANCELLED, message) : new Error(message);
}

/**
 * Downloads the Whisper models into `root`, one at a time per variant: a second request for a variant already
 * downloading joins that download and its progress. A download is kept only when its size and SHA-256 match `table`;
 * anything else removes the partial file and rejects, and so does a download cancelled or one that receives
 * nothing for `stallMs`. `onDownloaded` runs after each kept download.
 */
export function createWhisperDownloader(
  fetchFn: typeof fetch,
  logger: Logger,
  root: string,
  table: WhisperModelTable = WHISPER_MODEL_FILES,
  onDownloaded: (variant: WhisperVariant) => void = () => undefined,
  stallMs: number = WHISPER_STALL_MS,
): WhisperDownloader {
  const fileFor = (variant: WhisperVariant): string => path.join(root, table[variant].file);
  const running = new Map<
    WhisperVariant,
    {
      done: Promise<WhisperDownloadModelResponse>;
      listeners: Set<(percent: number, completedBytes: number) => void>;
      stop: Stop;
    }
  >();

  async function run(
    variant: WhisperVariant,
    listeners: ReadonlySet<(percent: number, completedBytes: number) => void>,
    stop: Stop,
  ): Promise<WhisperDownloadModelResponse> {
    let stallTimer: ReturnType<typeof setTimeout> | undefined;
    const armStall = (): void => {
      clearTimeout(stallTimer);
      stallTimer = setTimeout(
        () =>
          stopDownload(
            stop,
            `HuggingFace sent nothing for ${Math.round(stallMs / 1000)} s; the download stopped`,
          ),
        stallMs,
      );
    };
    try {
      armStall();
      return await fetchAndKeep(variant, listeners, stop, armStall);
    } finally {
      clearTimeout(stallTimer);
    }
  }

  async function fetchAndKeep(
    variant: WhisperVariant,
    listeners: ReadonlySet<(percent: number, completedBytes: number) => void>,
    stop: Stop,
    armStall: () => void,
  ): Promise<WhisperDownloadModelResponse> {
    const pinned = table[variant];
    await mkdir(root, { recursive: true });

    const destination = fileFor(variant);
    const partial = `${destination}.partial`;
    const url = `${HF_BASE}/${pinned.file}`;

    await rm(partial, { force: true }).catch(() => undefined);
    if (stop.reason !== null) throw stoppedError(stop);

    let response: Response;
    try {
      response = await fetchFn(url, { method: 'GET', signal: stop.controller.signal });
    } catch (cause: unknown) {
      if (stop.reason !== null) throw stoppedError(stop);
      logger.warn('whisper: fetch failed', { url, err: String(cause) });
      throw new Error(`whisper: download failed (${String(cause)})`);
    }
    if (!response.ok) {
      logger.warn('whisper: non-2xx', { url, status: response.status });
      throw new Error(`whisper: HuggingFace returned HTTP ${response.status}`);
    }
    if (response.body === null) {
      throw new Error('whisper: response body was empty');
    }
    const offered = response.headers.get('content-length');
    if (offered !== null && Number(offered) !== pinned.size) {
      await response.body.cancel().catch(() => undefined);
      logger.warn('whisper: size differs from the pin', { variant, offered });
      throw new Error(
        `whisper: HuggingFace offered ${offered} bytes for ${pinned.file}, not the pinned ${pinned.size}`,
      );
    }

    let received = 0;
    let lastEmittedPercent = -1;
    const emit = (percent: number): void => {
      if (percent === lastEmittedPercent) return;
      lastEmittedPercent = percent;
      for (const listener of listeners) listener(percent, received);
    };

    const hash = createHash('sha256');
    const out = createWriteStream(partial, { flags: 'w' });
    let writeError: Error | null = null;
    out.on('error', (err: Error) => {
      writeError ??= err;
    });
    const reader = response.body.getReader();
    stop.reader = reader;
    if (stop.reason !== null) void reader.cancel().catch(() => undefined);
    const writeChunk = (chunk: Uint8Array): Promise<void> =>
      new Promise((resolve, reject) => {
        if (writeError !== null) {
          reject(writeError);
          return;
        }
        out.write(chunk, (err) => (err ? reject(err) : resolve()));
      });

    let failure: Error | null = null;
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        if (value === undefined) continue;
        armStall();
        received += value.byteLength;
        if (received > pinned.size) {
          failure = new Error(
            `whisper: HuggingFace sent more than the pinned ${pinned.size} bytes for ${pinned.file}`,
          );
          await reader.cancel().catch(() => undefined);
          break;
        }
        hash.update(value);
        emit(Math.min(99, Math.floor((received / pinned.size) * 100)));
        await writeChunk(value);
      }
      await new Promise<void>((resolve, reject) => {
        out.end((err: unknown) => (err instanceof Error ? reject(err) : resolve()));
      });
    } catch (cause: unknown) {
      failure = new Error(`whisper: write failed (${String(cause)})`);
    } finally {
      reader.releaseLock();
    }

    if (stop.reason !== null) failure = stoppedError(stop);
    if (failure === null && received !== pinned.size) {
      failure = new Error(
        `whisper: HuggingFace sent ${received} of the pinned ${pinned.size} bytes for ${pinned.file}`,
      );
    }
    if (failure === null && hash.digest('hex') !== pinned.sha256) {
      failure = new Error(`whisper: checksum mismatch for ${pinned.file}`);
    }
    if (failure !== null) {
      out.destroy();
      await rm(partial, { force: true }).catch(() => undefined);
      logger.warn('whisper: download not kept', { variant, err: failure.message });
      throw failure;
    }

    await rename(partial, destination);
    await writeWhisperSidecar(destination, pinned.sha256).catch(() => undefined);
    emit(100);
    onDownloaded(variant);
    return { variant, path: destination };
  }

  const download: WhisperDownloader = (variant, onProgress) => {
    const current = running.get(variant);
    if (current !== undefined) {
      current.listeners.add(onProgress);
      return current.done;
    }
    const listeners = new Set([onProgress]);
    const stop: Stop = {
      controller: new AbortController(),
      reason: null,
      cancelled: false,
      reader: null,
    };
    const done = run(variant, listeners, stop).finally(() => running.delete(variant));
    running.set(variant, { done, listeners, stop });
    return done;
  };
  download.cancel = (variant: WhisperVariant): void => {
    const current = running.get(variant);
    if (current !== undefined) stopDownload(current.stop, 'the download was cancelled', true);
  };
  return download;
}

/** How the handlers reach the model choice: saving it, reading it, and hearing that a model file was deleted. */
export interface WhisperModelChoice {
  /** "Use this": saves the variant as `stt.model` and has the worker load it. */
  readonly useModel?: (variant: WhisperVariant) => Promise<WhisperUseModelResponse>;
  /** The saved `stt.model` value, or null. */
  readonly readPreferred?: () => Promise<string | null>;
  /** The model file the running worker loaded, null for none, or undefined while no worker runs. */
  readonly loadedModel?: () => string | null | undefined;
  /** A model file was deleted, so the model the worker would load may have changed. */
  readonly onDeleted?: () => void;
}

/**
 * Lists, downloads and deletes the Whisper models in `root`, and tells the renderer which models it pins; a download runs through `download`, with its progress
 * sent to the window that asked.
 */
export function registerWhisperHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  fetchFn: typeof fetch,
  logger: Logger,
  root: string,
  table: WhisperModelTable = WHISPER_MODEL_FILES,
  download: WhisperDownloader = createWhisperDownloader(fetchFn, logger, root, table),
  model: WhisperModelChoice = {},
): () => void {
  const useModel =
    model.useModel ??
    (async (): Promise<WhisperUseModelResponse> => {
      throw new ScoutError('whisper/unavailable', 'Scout cannot change the voice model here.');
    });
  const readPreferred = model.readPreferred ?? (async () => null);
  const onDeleted = model.onDeleted ?? (() => undefined);
  const isVariant = (value: unknown): value is WhisperVariant =>
    typeof value === 'string' && Object.hasOwn(table, value);
  const fileFor = (variant: WhisperVariant): string => path.join(root, table[variant].file);

  ipcMain.handle(CHANNEL.whisperListInstalled, async (): Promise<WhisperListInstalledResponse> => {
    try {
      await mkdir(root, { recursive: true });
      const installed: WhisperInstalledModel[] = [];
      for (const variant of Object.keys(table) as WhisperVariant[]) {
        const file = fileFor(variant);
        if (!(await fileExists(file))) continue;
        installed.push({ variant, verified: await verifyWhisperModel(file, table[variant]) });
      }
      const saved = await readPreferred().catch(() => null);
      const loaded = model.loadedModel?.();
      const chosen = loaded !== undefined ? loaded : await chooseWhisperModel(root, saved, table);
      const inUse =
        (Object.keys(table) as WhisperVariant[]).find((v) => fileFor(v) === chosen) ?? null;
      return { installed, inUse, preferred: variantOf(saved, table) };
    } catch (cause: unknown) {
      logger.warn('whisper: listInstalled failed', { err: String(cause) });
      throw cause;
    }
  });

  ipcMain.handle(CHANNEL.whisperModelTable, (): WhisperModelTableResponse => ({
    models: (Object.keys(table) as WhisperVariant[]).map((variant) => ({
      variant,
      file: table[variant].file,
      size: table[variant].size,
    })),
  }));

  ipcMain.handle(
    CHANNEL.whisperUseModel,
    async (_event, req: unknown): Promise<WhisperUseModelResponse> => {
      const r = req as Partial<WhisperUseModelRequest>;
      if (!isVariant(r?.variant)) {
        throw new Error(`whisper: unknown variant "${String(r?.variant)}"`);
      }
      try {
        return await useModel(r.variant);
      } catch (err: unknown) {
        const serialized = serializeError(err);
        throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
          code: serialized.code,
        });
      }
    },
  );

  ipcMain.handle(
    CHANNEL.whisperDownloadModel,
    async (event, req: unknown): Promise<WhisperDownloadModelResponse> => {
      const r = req as Partial<WhisperDownloadModelRequest>;
      if (!isVariant(r?.variant)) {
        throw new Error(`whisper: unknown variant "${String(r?.variant)}"`);
      }
      const requestId = typeof r?.requestId === 'string' ? r.requestId : '';
      if (requestId.length === 0) {
        throw new Error('whisper: requestId must be a non-empty string');
      }
      const variant = r.variant;
      const sender: WebContents = event.sender;
      try {
        return await download(variant, (percent, completedBytes) => {
          const payload: WhisperDownloadProgressEvent = {
            requestId,
            variant,
            percent,
            completedBytes,
            totalBytes: table[variant].size,
          };
          if (!sender.isDestroyed()) sender.send(CHANNEL.whisperDownloadProgress, payload);
        });
      } catch (err: unknown) {
        if (!(err instanceof ScoutError)) throw err;
        throw Object.assign(new Error(encodeIpcErrorMessage(serializeError(err))), {
          code: err.code,
        });
      }
    },
  );

  ipcMain.handle(CHANNEL.whisperCancelDownload, (_event, req: unknown): void => {
    const r = req as Partial<WhisperCancelDownloadRequest>;
    if (isVariant(r?.variant)) download.cancel?.(r.variant);
  });

  ipcMain.handle(
    CHANNEL.whisperDeleteModel,
    async (_event, req: unknown): Promise<WhisperDeleteModelResponse> => {
      const r = req as Partial<WhisperDeleteModelRequest>;
      if (!isVariant(r?.variant)) {
        throw new Error(`whisper: unknown variant "${String(r?.variant)}"`);
      }
      const target = fileFor(r.variant);
      await rm(target, { force: true });
      await rm(`${target}.partial`, { force: true });
      await rm(`${target}.sha256`, { force: true });
      onDeleted();
      return { variant: r.variant };
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.whisperListInstalled);
    ipcMain.removeHandler(CHANNEL.whisperModelTable);
    ipcMain.removeHandler(CHANNEL.whisperUseModel);
    ipcMain.removeHandler(CHANNEL.whisperDownloadModel);
    ipcMain.removeHandler(CHANNEL.whisperCancelDownload);
    ipcMain.removeHandler(CHANNEL.whisperDeleteModel);
  };
}
