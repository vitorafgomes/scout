import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { CHANNEL } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { registerWhisperHandlers } from '../src/ipc/whisper.js';
import type { WhisperModelTable } from '../src/worker/whisper-model.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const MODEL = 'base model bytes';
const sha256 = (text: string) => createHash('sha256').update(text).digest('hex');
const pin = (file: string, content: string) => ({
  file,
  size: Buffer.byteLength(content),
  sha256: sha256(content),
});
const TABLE: WhisperModelTable = {
  tiny: pin('ggml-tiny.bin', 'tiny'),
  base: pin('ggml-base.bin', MODEL),
  small: pin('ggml-small.bin', 'small'),
  'large-v3-turbo': pin('ggml-large-v3-turbo.bin', 'turbo'),
  'large-v3': pin('ggml-large-v3.bin', 'large'),
};

function body(chunks: readonly string[], failAfter?: number) {
  const reads = { count: 0, cancelled: false };
  let next = 0;
  const stream = new ReadableStream<Uint8Array>({
    pull(controller) {
      reads.count += 1;
      if (failAfter !== undefined && next === failAfter) {
        controller.error(new Error('ECONNRESET'));
        return;
      }
      if (next === chunks.length) {
        controller.close();
        return;
      }
      controller.enqueue(new TextEncoder().encode(chunks[next++]!));
    },
    cancel() {
      reads.cancelled = true;
    },
  });
  return { stream, reads };
}

describe('whisper model download', () => {
  let dir: string;
  let handlers: Map<string, IpcHandler>;
  let sent: unknown[];
  let fetched: string[];
  let respond: () => Promise<Response>;
  let logged: ReturnType<typeof recordingLogger>;
  let dispose: () => void;

  beforeEach(async () => {
    dir = await mkdtemp(path.join(os.tmpdir(), 'scout-whisper-ipc-'));
    handlers = new Map();
    sent = [];
    fetched = [];
    logged = recordingLogger();
    const ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'> = {
      handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
      removeHandler: (channel: string) => void handlers.delete(channel),
    };
    const fetchFn = (async (url: string) => {
      fetched.push(url);
      return respond();
    }) as unknown as typeof fetch;
    dispose = registerWhisperHandlers(ipcMain, fetchFn, logged.logger, dir, TABLE);
  });

  afterEach(async () => {
    dispose();
    await rm(dir, { recursive: true, force: true });
  });

  function invoke(channel: string, req?: unknown): Promise<unknown> {
    const sender = { isDestroyed: () => false, send: (_c: string, p: unknown) => sent.push(p) };
    return Promise.resolve(
      handlers.get(channel)!({ sender } as unknown as IpcMainInvokeEvent, req),
    );
  }

  async function rejection(promise: Promise<unknown>): Promise<string> {
    try {
      await promise;
    } catch (err: unknown) {
      return (err as Error).message;
    }
    throw new Error('expected a rejection');
  }

  const download = () =>
    invoke(CHANNEL.whisperDownloadModel, { variant: 'base', requestId: 'req-1' });

  it('a verified download is kept with its sidecar, and listInstalled reports it verified', async () => {
    respond = async () =>
      new Response(body(['base model ', 'bytes']).stream, {
        headers: { 'content-length': String(TABLE.base.size) },
      });

    const result = await download();
    const sidecar = JSON.parse(await readFile(path.join(dir, 'ggml-base.bin.sha256'), 'utf8'));

    expect(fetched).toEqual([
      'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin',
    ]);
    expect(result).toEqual({ variant: 'base', path: path.join(dir, 'ggml-base.bin') });
    expect(await readFile(path.join(dir, 'ggml-base.bin'), 'utf8')).toBe(MODEL);
    expect(sidecar.sha256).toBe(TABLE.base.sha256);
    expect(sent.at(-1)).toEqual({
      requestId: 'req-1',
      variant: 'base',
      percent: 100,
      completedBytes: TABLE.base.size,
      totalBytes: TABLE.base.size,
    });
    expect(await invoke(CHANNEL.whisperListInstalled)).toEqual({
      installed: [{ variant: 'base', verified: true }],
    });
  });

  it('a download whose sidecar cannot be written is still kept and resolves with its path', async () => {
    respond = async () =>
      new Response(body(['base model ', 'bytes']).stream, {
        headers: { 'content-length': String(TABLE.base.size) },
      });
    await mkdir(path.join(dir, 'ggml-base.bin.sha256'));

    const result = await download();

    expect(result).toEqual({ variant: 'base', path: path.join(dir, 'ggml-base.bin') });
    expect(await readFile(path.join(dir, 'ggml-base.bin'), 'utf8')).toBe(MODEL);
    expect(await invoke(CHANNEL.whisperListInstalled)).toEqual({
      installed: [{ variant: 'base', verified: true }],
    });
  });

  it('a download whose digest differs keeps nothing', async () => {
    respond = async () =>
      new Response(body(['BASE MODEL BYTES']).stream, {
        headers: { 'content-length': String(TABLE.base.size) },
      });

    const message = await rejection(download());

    expect(message).toBe('whisper: checksum mismatch for ggml-base.bin');
    expect(await readdir(dir)).toEqual([]);
  });

  it('a content-length other than the pinned size is refused and the body cancelled before anything is written', async () => {
    const offered = body([MODEL, ' and more']);
    respond = async () =>
      new Response(offered.stream, {
        headers: { 'content-length': String(TABLE.base.size + 9) },
      });

    const message = await rejection(download());

    expect(message).toBe(
      `whisper: HuggingFace offered ${TABLE.base.size + 9} bytes for ggml-base.bin, not the pinned ${TABLE.base.size}`,
    );
    expect(offered.reads.cancelled).toBe(true);
    expect(await readdir(dir)).toEqual([]);
  });

  it('a body longer than the pinned size without a content-length is cut off and keeps nothing', async () => {
    respond = async () => new Response(body([MODEL, ' and more', ' still more']).stream);

    const message = await rejection(download());

    expect(message).toBe(
      `whisper: HuggingFace sent more than the pinned ${TABLE.base.size} bytes for ggml-base.bin`,
    );
    expect(await readdir(dir)).toEqual([]);
  });

  it('a body shorter than the pinned size keeps nothing', async () => {
    respond = async () => new Response(body(['base model']).stream);

    const message = await rejection(download());

    expect(message).toBe(
      `whisper: HuggingFace sent 10 of the pinned ${TABLE.base.size} bytes for ggml-base.bin`,
    );
    expect(await readdir(dir)).toEqual([]);
  });

  it('a connection reset mid-download keeps nothing and says the download failed', async () => {
    respond = async () => new Response(body(['base model '], 1).stream);

    const message = await rejection(download());

    expect(message).toContain('whisper: write failed');
    expect(message).toContain('ECONNRESET');
    expect(await readdir(dir)).toEqual([]);
  });

  it('a network failure or an HTTP 503 keeps nothing and says why', async () => {
    respond = async () => {
      throw new TypeError('fetch failed');
    };
    const network = await rejection(download());
    respond = async () => new Response('busy', { status: 503 });
    const busy = await rejection(download());

    expect(network).toBe('whisper: download failed (TypeError: fetch failed)');
    expect(busy).toBe('whisper: HuggingFace returned HTTP 503');
    expect(await readdir(dir)).toEqual([]);
  });

  it('listInstalled reports a file whose hash differs as installed but not verified', async () => {
    await writeFile(path.join(dir, 'ggml-base.bin'), 'BASE MODEL BYTES');
    await writeFile(path.join(dir, 'ggml-tiny.bin'), 'tiny');

    expect(await invoke(CHANNEL.whisperListInstalled)).toEqual({
      installed: [
        { variant: 'tiny', verified: true },
        { variant: 'base', verified: false },
      ],
    });
  });

  it('delete removes the model, its partial download and its sidecar', async () => {
    for (const name of ['ggml-base.bin', 'ggml-base.bin.partial', 'ggml-base.bin.sha256']) {
      await writeFile(path.join(dir, name), 'x');
    }
    await writeFile(path.join(dir, 'ggml-tiny.bin'), 'tiny');

    await invoke(CHANNEL.whisperDeleteModel, { variant: 'base' });

    expect(await readdir(dir)).toEqual(['ggml-tiny.bin']);
  });
});
