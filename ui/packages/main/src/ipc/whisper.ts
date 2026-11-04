import type { IpcMain, WebContents } from 'electron';
import { createHash } from 'node:crypto';
import { createWriteStream } from 'node:fs';
import { mkdir, rm, stat, rename } from 'node:fs/promises';
import * as path from 'node:path';
import {
  CHANNEL,
  type WhisperDeleteModelRequest,
  type WhisperDeleteModelResponse,
  type WhisperDownloadModelRequest,
  type WhisperDownloadModelResponse,
  type WhisperDownloadProgressEvent,
  type WhisperInstalledModel,
  type WhisperListInstalledResponse,
  type WhisperVariant,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';
import {
  WHISPER_MODEL_FILES,
  verifyWhisperModel,
  writeWhisperSidecar,
  type WhisperModelTable,
} from '../worker/whisper-model.js';

const HF_BASE = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main';

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

/**
 * Lists, downloads and deletes the Whisper models in `root`. A download is kept only when its size and SHA-256
 * match `table`; anything else removes the partial file and rejects.
 */
export function registerWhisperHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  fetchFn: typeof fetch,
  logger: Logger,
  root: string,
  table: WhisperModelTable = WHISPER_MODEL_FILES,
): () => void {
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
      return { installed };
    } catch (cause: unknown) {
      logger.warn('whisper: listInstalled failed', { err: String(cause) });
      return { installed: [] };
    }
  });

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
      const pinned = table[variant];
      await mkdir(root, { recursive: true });

      const destination = fileFor(variant);
      const partial = `${destination}.partial`;
      const url = `${HF_BASE}/${pinned.file}`;
      const sender: WebContents = event.sender;

      await rm(partial, { force: true }).catch(() => undefined);

      let response: Response;
      try {
        response = await fetchFn(url, { method: 'GET' });
      } catch (cause: unknown) {
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
        const payload: WhisperDownloadProgressEvent = {
          requestId,
          variant,
          percent,
          completedBytes: received,
          totalBytes: pinned.size,
        };
        if (!sender.isDestroyed()) sender.send(CHANNEL.whisperDownloadProgress, payload);
      };

      const hash = createHash('sha256');
      const out = createWriteStream(partial, { flags: 'w' });
      const reader = response.body.getReader();
      const writeChunk = (chunk: Uint8Array): Promise<void> =>
        new Promise((resolve, reject) => {
          out.write(chunk, (err) => (err ? reject(err) : resolve()));
        });

      let failure: Error | null = null;
      try {
        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          if (value === undefined) continue;
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
      return { variant, path: destination };
    },
  );

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
      return { variant: r.variant };
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.whisperListInstalled);
    ipcMain.removeHandler(CHANNEL.whisperDownloadModel);
    ipcMain.removeHandler(CHANNEL.whisperDeleteModel);
  };
}
