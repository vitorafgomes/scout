import { afterEach, beforeEach, describe, expect, it, jest, spyOn } from 'bun:test';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { CHANNEL } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { registerWhisperHandlers } from '../src/ipc/whisper.js';
import * as whisperModel from '../src/worker/whisper-model.js';
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
  let respond: (init?: RequestInit) => Promise<Response>;
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
    const fetchFn = (async (url: string, init?: RequestInit) => {
      fetched.push(url);
      return respond(init);
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
      inUse: 'base',
      preferred: null,
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
      inUse: 'base',
      preferred: null,
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
      inUse: 'tiny',
      preferred: null,
    });
  });

  it('A .partial path that cannot be written rejects the download with the reason and raises no uncaught error in main', async () => {
    respond = async () =>
      new Response(body(['base model ', 'bytes']).stream, {
        headers: { 'content-length': String(TABLE.base.size) },
      });
    await mkdir(path.join(dir, 'ggml-base.bin.partial'));

    const message = await rejection(download());
    for (let i = 0; i < 10; i++) await new Promise((resolve) => setImmediate(resolve));

    expect(message).toContain('EISDIR');
  });

  describe('a stalled download', () => {
    const stall = () =>
      new Response(new ReadableStream<Uint8Array>({ pull: () => new Promise(() => undefined) }), {
        headers: { 'content-length': String(TABLE.base.size) },
      });
    const ticks = async () => {
      for (let i = 0; i < 20; i++) await new Promise((resolve) => setImmediate(resolve));
    };
    const until = async (ready: () => boolean) => {
      for (let i = 0; i < 100_000 && !ready(); i++) {
        await new Promise((resolve) => setImmediate(resolve));
      }
      await ticks();
    };
    const requested = (count: number) => until(() => fetched.length >= count);
    const outcomeOf = async (promise: Promise<unknown>): Promise<string> => {
      let outcome = 'pending';
      promise.then(
        () => (outcome = 'resolved'),
        (err: unknown) => (outcome = (err as Error).message),
      );
      await until(() => outcome !== 'pending');
      return outcome;
    };
    const answersOnlyAnAbort = (init?: RequestInit) =>
      new Promise<Response>((_resolve, reject) => {
        if (init?.signal?.aborted === true) reject(new Error('AbortError'));
        init?.signal?.addEventListener('abort', () => reject(new Error('AbortError')));
      });

    beforeEach(() => {
      jest.useFakeTimers();
    });

    afterEach(() => {
      jest.useRealTimers();
    });

    it('A download that receives no byte for 90 s ends with a rejection instead of hanging', async () => {
      respond = async () => stall();
      const state = { settled: 'pending' };
      void download().then(
        () => void (state.settled = 'resolved'),
        () => void (state.settled = 'rejected'),
      );
      await requested(1);
      jest.advanceTimersByTime(90_000);
      await until(() => state.settled !== 'pending');

      expect(state.settled).toBe('rejected');
    });

    it('A retry after a stalled download starts a fresh download instead of joining the dead one', async () => {
      respond = async () => stall();
      let ended = false;
      void download()
        .catch(() => undefined)
        .finally(() => (ended = true));
      await requested(1);
      jest.advanceTimersByTime(90_000);
      await until(() => ended);
      void download().catch(() => undefined);
      await requested(2);

      expect(fetched.length).toBe(2);
    });

    it('A HuggingFace that never answers the request ends the download after the stall window with the reason, and keeps nothing', async () => {
      respond = answersOnlyAnAbort;
      const outcome = download();
      await requested(1);
      jest.advanceTimersByTime(60_000);

      expect(await outcomeOf(outcome)).toBe(
        'whisper: HuggingFace sent nothing for 60 s; the download stopped',
      );
      expect(await readdir(dir)).toEqual([]);
    });

    it('A download still receiving bytes is not stopped by the stall window', async () => {
      let push!: (text: string) => void;
      let close!: () => void;
      respond = async () =>
        new Response(
          new ReadableStream<Uint8Array>({
            start(controller) {
              push = (text) => controller.enqueue(new TextEncoder().encode(text));
              close = () => controller.close();
            },
          }),
          { headers: { 'content-length': String(TABLE.base.size) } },
        );
      const done = download();
      await requested(1);
      push('base model ');
      await until(() => sent.length >= 1);
      jest.advanceTimersByTime(50_000);
      push('bytes');
      await until(() => sent.length >= 2);
      jest.advanceTimersByTime(50_000);
      close();

      expect(await done).toEqual({ variant: 'base', path: path.join(dir, 'ggml-base.bin') });
    });

    it('Cancel stops a running download: it rejects as cancelled, keeps no partial file, and Download starts afresh', async () => {
      respond = async () => stall();
      const outcome = download();
      await requested(1);

      await invoke(CHANNEL.whisperCancelDownload, { variant: 'base' });
      const message = await outcomeOf(outcome);
      const left = await readdir(dir);
      void download().catch(() => undefined);
      await requested(2);

      expect({ message, left, fetches: fetched.length }).toEqual({
        message: '[scout:whisper/download-cancelled] whisper: the download was cancelled',
        left: [],
        fetches: 2,
      });
    });

    it('Cancel from one window rejects the same download as cancelled in every window waiting on it', async () => {
      respond = async () => stall();
      const first = invoke(CHANNEL.whisperDownloadModel, {
        variant: 'base',
        requestId: 'window-a',
      });
      await requested(1);
      const second = invoke(CHANNEL.whisperDownloadModel, {
        variant: 'base',
        requestId: 'window-b',
      });
      await ticks();

      const outcomes = Promise.all([outcomeOf(first), outcomeOf(second)]);
      await invoke(CHANNEL.whisperCancelDownload, { variant: 'base' });

      expect({ outcomes: await outcomes, fetches: fetched.length }).toEqual({
        outcomes: [
          '[scout:whisper/download-cancelled] whisper: the download was cancelled',
          '[scout:whisper/download-cancelled] whisper: the download was cancelled',
        ],
        fetches: 1,
      });
    });

    it('Cancel while HuggingFace has not answered yet aborts the request and rejects as cancelled', async () => {
      const aborted: string[] = [];
      respond = (init) => {
        init?.signal?.addEventListener('abort', () => aborted.push('aborted'));
        return answersOnlyAnAbort(init);
      };
      const outcome = download();
      await requested(1);

      await invoke(CHANNEL.whisperCancelDownload, { variant: 'base' });

      expect({ message: await outcomeOf(outcome), aborted }).toEqual({
        message: '[scout:whisper/download-cancelled] whisper: the download was cancelled',
        aborted: ['aborted'],
      });
    });
  });

  it('A file at the pinned size with a wrong hash is hashed at most once across two listInstalled polls', async () => {
    await writeFile(path.join(dir, 'ggml-large-v3.bin'), 'LARGE');
    const hashed = spyOn(whisperModel, 'sha256OfFile');
    try {
      await invoke(CHANNEL.whisperListInstalled);
      const afterFirst = hashed.mock.calls.length;
      await invoke(CHANNEL.whisperListInstalled);

      expect({ hashedOnFirstPoll: afterFirst >= 1, hashes: hashed.mock.calls.length }).toEqual({
        hashedOnFirstPoll: true,
        hashes: 1,
      });
    } finally {
      hashed.mockRestore();
    }
  });

  it('listInstalled rejects when main cannot read the models folder, so the renderer can tell unknown from none', async () => {
    const blocked = path.join(dir, 'blocked');
    await writeFile(blocked, 'a file where the models folder should be');
    const handlers = new Map<string, IpcHandler>();
    const ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'> = {
      handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
      removeHandler: (channel: string) => void handlers.delete(channel),
    };
    const disposeBlocked = registerWhisperHandlers(
      ipcMain,
      globalThis.fetch,
      logged.logger,
      blocked,
      TABLE,
    );
    try {
      const answer = await Promise.resolve(
        handlers.get(CHANNEL.whisperListInstalled)!({} as IpcMainInvokeEvent),
      ).then(
        (value) => ({ resolved: value }),
        () => 'rejected',
      );

      expect(answer).toBe('rejected');
    } finally {
      disposeBlocked();
    }
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

describe('the Whisper model table the renderer lists', () => {
  it('scout:whisper.modelTable returns main pinned table, variant, file and exact bytes, in main order, and nothing else', async () => {
    const handlers = new Map<string, IpcHandler>();
    const ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'> = {
      handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
      removeHandler: (channel: string) => void handlers.delete(channel),
    };
    const dispose = registerWhisperHandlers(
      ipcMain,
      (async () => new Response(null, { status: 500 })) as unknown as typeof fetch,
      recordingLogger().logger,
      path.join(os.tmpdir(), 'scout-whisper-table-unused'),
    );

    const table = await handlers.get(CHANNEL.whisperModelTable)!({} as IpcMainInvokeEvent);
    dispose();

    expect(table).toEqual({
      models: [
        { variant: 'tiny', file: 'ggml-tiny.bin', size: 77_691_713 },
        { variant: 'base', file: 'ggml-base.bin', size: 147_951_465 },
        { variant: 'small', file: 'ggml-small.bin', size: 487_601_967 },
        { variant: 'large-v3-turbo', file: 'ggml-large-v3-turbo.bin', size: 1_624_555_275 },
        { variant: 'large-v3', file: 'ggml-large-v3.bin', size: 3_095_033_483 },
      ],
    });
    expect(handlers.has(CHANNEL.whisperModelTable)).toBe(false);
  });
});

describe('In use showed base while the worker used large-v3-turbo', () => {
  describe('the voice model in use, as main reports it', () => {
    let dir: string;

    beforeEach(async () => {
      dir = await mkdtemp(path.join(os.tmpdir(), 'scout-whisper-in-use-'));
    });

    afterEach(async () => {
      await rm(dir, { recursive: true, force: true });
    });

    const CONTENT: Record<string, string> = {
      tiny: 'tiny',
      base: MODEL,
      small: 'small',
      'large-v3-turbo': 'turbo',
      'large-v3': 'large',
    };

    const ROWS: readonly [string | null, readonly string[], string | null, string | null][] = [
      ['whisper-base', ['large-v3-turbo'], 'large-v3-turbo', 'base'],
      ['whisper-base', ['base', 'large-v3-turbo'], 'base', 'base'],
      [null, ['tiny', 'small'], 'small', null],
      ['whisper-base', [], null, 'base'],
      [null, [], null, null],
      ['whisper-nonsense', ['tiny'], 'tiny', null],
    ];

    for (const [saved, installed, inUse, preferred] of ROWS) {
      it(`saved ${saved ?? 'nothing'}, installed ${installed.join(' and ') || 'none'}: in use ${inUse ?? 'none'}, preferred ${preferred ?? 'none'}`, async () => {
        for (const variant of installed) {
          await writeFile(
            path.join(dir, TABLE[variant as keyof typeof TABLE].file),
            CONTENT[variant]!,
          );
        }
        const handlers = new Map<string, IpcHandler>();
        const ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'> = {
          handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
          removeHandler: (channel: string) => void handlers.delete(channel),
        };
        const dispose = registerWhisperHandlers(
          ipcMain,
          globalThis.fetch,
          recordingLogger().logger,
          dir,
          TABLE,
          undefined,
          { readPreferred: async () => saved },
        );

        const answer = (await handlers.get(CHANNEL.whisperListInstalled)!(
          {} as IpcMainInvokeEvent,
        )) as { inUse: unknown; preferred: unknown };
        dispose();

        expect({ inUse: answer.inUse, preferred: answer.preferred }).toEqual({ inUse, preferred });
      });
    }
  });
});
