import { app, type IpcMain, type WebContents } from 'electron';
import { createWriteStream } from 'node:fs';
import { mkdir, rm, stat, readdir, rename } from 'node:fs/promises';
import * as path from 'node:path';
import {
  CHANNEL,
  type WhisperDeleteModelRequest,
  type WhisperDeleteModelResponse,
  type WhisperDownloadModelRequest,
  type WhisperDownloadModelResponse,
  type WhisperDownloadProgressEvent,
  type WhisperListInstalledResponse,
  type WhisperVariant,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';

const HF_BASE = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main';

const VARIANT_TO_FILE: Readonly<Record<WhisperVariant, string>> = Object.freeze({
  'large-v3': 'ggml-large-v3.bin',
  'large-v3-turbo': 'ggml-large-v3-turbo.bin',
});

const VALID_VARIANTS: ReadonlySet<string> = new Set(Object.keys(VARIANT_TO_FILE));

function isVariant(value: unknown): value is WhisperVariant {
  return typeof value === 'string' && VALID_VARIANTS.has(value);
}

function whisperRootDir(): string {
  return path.join(app.getPath('userData'), 'whisper-models');
}

function fileFor(variant: WhisperVariant): string {
  return path.join(whisperRootDir(), VARIANT_TO_FILE[variant]);
}

async function fileExists(p: string): Promise<boolean> {
  try {
    const s = await stat(p);
    return s.isFile() && s.size > 0;
  } catch {
    return false;
  }
}

export function registerWhisperHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  fetchFn: typeof fetch,
  logger: Logger,
): () => void {
  ipcMain.handle(CHANNEL.whisperListInstalled, async (): Promise<WhisperListInstalledResponse> => {
    const root = whisperRootDir();
    try {
      await mkdir(root, { recursive: true });
      const entries = await readdir(root);
      const installed: WhisperVariant[] = [];
      for (const [variant, filename] of Object.entries(VARIANT_TO_FILE)) {
        if (entries.includes(filename)) {
          const ok = await fileExists(path.join(root, filename));
          if (ok) installed.push(variant as WhisperVariant);
        }
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
      const root = whisperRootDir();
      await mkdir(root, { recursive: true });

      const destination = fileFor(variant);
      const partial = `${destination}.partial`;
      const url = `${HF_BASE}/${VARIANT_TO_FILE[variant]}`;
      const sender: WebContents = event.sender;

      // Best-effort cleanup of a previous interrupted attempt.
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

      const totalHeader = response.headers.get('content-length');
      const totalBytes = totalHeader === null ? 0 : Number.parseInt(totalHeader, 10) || 0;

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
          totalBytes,
        };
        if (!sender.isDestroyed()) sender.send(CHANNEL.whisperDownloadProgress, payload);
      };

      const out = createWriteStream(partial, { flags: 'w' });
      const reader = response.body.getReader();
      const writeChunk = (chunk: Uint8Array): Promise<void> =>
        new Promise((resolve, reject) => {
          out.write(chunk, (err) => (err ? reject(err) : resolve()));
        });

      try {
        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          if (value !== undefined) {
            received += value.byteLength;
            const percent =
              totalBytes > 0 ? Math.min(99, Math.floor((received / totalBytes) * 100)) : 0;
            emit(percent);
            await writeChunk(value);
          }
        }
        await new Promise<void>((resolve, reject) => {
          out.end((err: unknown) => (err instanceof Error ? reject(err) : resolve()));
        });
      } catch (cause: unknown) {
        out.destroy();
        await rm(partial, { force: true }).catch(() => undefined);
        logger.warn('whisper: download stream failed', { variant, err: String(cause) });
        throw new Error(`whisper: write failed (${String(cause)})`);
      } finally {
        try {
          reader.releaseLock();
        } catch {
          // ignore
        }
      }

      await rename(partial, destination);
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
      return { variant: r.variant };
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.whisperListInstalled);
    ipcMain.removeHandler(CHANNEL.whisperDownloadModel);
    ipcMain.removeHandler(CHANNEL.whisperDeleteModel);
  };
}
