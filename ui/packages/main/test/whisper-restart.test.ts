import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { CHANNEL, readIpcError, type WorkerStatus } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { createWhisperDownloader, registerWhisperHandlers } from '../src/ipc/whisper.js';
import { registerVaultHandlers } from '../src/ipc/vault.js';
import { NotificationStore } from '../src/notifications/notification-store.js';
import {
  VOICE_TRANSCRIBING_DETAIL,
  createVoiceNotice,
  registerNotificationActions,
} from '../src/notifications/voice-notice.js';
import type { Vault } from '../src/vault/safe-storage-vault.js';
import { chooseWhisperModel, type WhisperModelTable } from '../src/worker/whisper-model.js';
import {
  RESTARTED_TITLE,
  RESTART_DEFERRED_TITLE,
  RESTART_ENTRY_ID,
  RESTART_FAILED_TITLE,
  createWhisperModelUser,
  createWorkerRestart,
} from '../src/worker/worker-restart.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const sha256 = (text: string) => createHash('sha256').update(text).digest('hex');
const pin = (file: string, content: string) => ({
  file,
  size: Buffer.byteLength(content),
  sha256: sha256(content),
});
const BASE = 'base model bytes';
const TABLE: WhisperModelTable = {
  tiny: pin('ggml-tiny.bin', 'tiny model'),
  base: pin('ggml-base.bin', BASE),
  small: pin('ggml-small.bin', 'small model'),
  'large-v3-turbo': pin('ggml-large-v3-turbo.bin', 'turbo model'),
  'large-v3': pin('ggml-large-v3.bin', 'large model'),
};

interface Options {
  readonly worker: 'running' | 'stopped';
  readonly listening: boolean;
  readonly loaded: 'none' | 'base' | 'large-v3-turbo';
  readonly startFails?: string;
}

async function world(dir: string, options: Options) {
  const { logger } = recordingLogger();
  const calls: string[] = [];
  const status: { state: WorkerStatus['state'] } = { state: options.worker };
  const supervisor = {
    get status(): WorkerStatus {
      return { state: status.state, restarts: 0 } as WorkerStatus;
    },
    stop: async () => {
      calls.push('stop');
      status.state = 'stopped';
      return { exitCode: 0 };
    },
    start: async () => {
      calls.push('start');
      if (options.startFails !== undefined) throw new Error(options.startFails);
      status.state = 'running';
      loaded = await chooseWhisperModel(dir, stored.get('stt.model') ?? null, TABLE);
      return { pid: 1 };
    },
  };
  const stored = new Map<string, string>();
  const vault = {
    store: async (key: string, value: string) => void stored.set(key, value),
    get: async (key: string) => stored.get(key) ?? null,
    delete: async (key: string) => stored.delete(key),
  } as unknown as Vault;
  let loaded: string | null =
    options.loaded === 'none' ? null : path.join(dir, TABLE[options.loaded].file);
  const listening = { now: options.listening };
  const store = new NotificationStore({
    vault: { get: async () => null, store: async () => undefined },
    push: () => undefined,
    now: () => 1_000_000,
    setTimer: () => () => undefined,
    logger,
  });
  const handlers = new Map<string, IpcHandler>();
  const ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'> = {
    handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
    removeHandler: (channel: string) => void handlers.delete(channel),
  };
  const fetchFn = (async () =>
    new Response(BASE, {
      headers: { 'content-length': String(Buffer.byteLength(BASE)) },
    })) as unknown as typeof fetch;
  const restart = createWorkerRestart({
    supervisor,
    currentModel: async () => chooseWhisperModel(dir, stored.get('stt.model') ?? null, TABLE),
    loadedModel: () => loaded,
    listening: () => listening.now,
    store,
    onRestarted: () => voice.restarted(),
    logger,
  });
  const download = createWhisperDownloader(fetchFn, logger, dir, TABLE, () => {
    voice.modelInstalled();
    void restart.modelChanged();
  });
  const voice = createVoiceNotice({ store, now: () => 1_000_000, download, logger });
  registerWhisperHandlers(ipcMain, fetchFn, logger, dir, TABLE, download, {
    useModel: createWhisperModelUser(vault, restart),
    readPreferred: async () => stored.get('stt.model') ?? null,
    onDeleted: () => void restart.modelChanged(),
    loadedModel: () => restart.loadedWhileRunning(),
  });
  registerVaultHandlers(ipcMain, vault, logger, (key) => {
    if (key === 'stt.model') void restart.modelChanged();
  });
  registerNotificationActions(ipcMain, voice);
  const invoke = (channel: string, req?: unknown) =>
    Promise.resolve(
      handlers.get(channel)!(
        { sender: { isDestroyed: () => false, send: () => undefined } } as never,
        req,
      ),
    );
  const settled = async () => {
    for (let i = 0; i < 30; i += 1) await new Promise((resolve) => setTimeout(resolve, 0));
  };
  const entry = () => store.list().find((n) => n.id === RESTART_ENTRY_ID);
  return {
    calls,
    supervisor,
    restart,
    listening,
    invoke,
    settled,
    entry,
    store,
    voice,
    stored,
    loadedModel: () => loaded,
  };
}

describe('a new Whisper model restarts a running worker so it loads', () => {
  let dir: string;

  beforeEach(async () => {
    dir = await mkdtemp(path.join(os.tmpdir(), 'scout-whisper-restart-'));
  });

  afterEach(async () => {
    await rm(dir, { recursive: true, force: true });
  });

  const TRIGGERS = {
    'a download completes': {
      loaded: 'none' as const,
      fire: async (w: Awaited<ReturnType<typeof world>>): Promise<unknown> => {
        await w.invoke(CHANNEL.whisperDownloadModel, { variant: 'base', requestId: 'r1' });
        return null;
      },
    },
    '"Use this" picks another model': {
      loaded: 'base' as const,
      fire: async (w: Awaited<ReturnType<typeof world>>): Promise<unknown> => {
        await writeFile(path.join(dir, 'ggml-base.bin'), BASE);
        await writeFile(path.join(dir, 'ggml-tiny.bin'), 'tiny model');
        return w.invoke(CHANNEL.whisperUseModel, { variant: 'tiny' });
      },
    },
  };

  for (const [trigger, { loaded, fire }] of Object.entries(TRIGGERS)) {
    for (const worker of ['running', 'stopped'] as const) {
      for (const listening of [false, true]) {
        const expected = worker === 'stopped' ? 'none' : listening ? 'deferred' : 'now';
        it(`${trigger}, worker ${worker}, live listen ${listening ? 'open' : 'closed'}: restart ${expected}`, async () => {
          const w = await world(dir, { worker, listening, loaded });

          const answer = await fire(w);
          await w.settled();
          const before = [...w.calls];
          const shown = w.entry()?.title ?? null;
          w.listening.now = false;
          w.restart.listeningChanged(false);
          await w.settled();

          expect(before).toEqual(expected === 'now' ? ['stop', 'start'] : []);
          expect(shown).toBe(
            expected === 'now'
              ? RESTARTED_TITLE
              : expected === 'deferred'
                ? RESTART_DEFERRED_TITLE
                : null,
          );
          expect(w.calls).toEqual(expected === 'none' ? [] : ['stop', 'start']);
          if (trigger !== 'a download completes') {
            expect(answer).toEqual({ variant: 'tiny', restart: expected });
          }
        });
      }
    }
  }

  for (const other of [true, false]) {
    for (const listening of [false, true]) {
      const expected = listening ? 'deferred' : 'now';
      it(`removing the model the worker loaded, ${other ? 'base still installed' : 'no other model'}, live listen ${listening ? 'open' : 'closed'}: restart ${expected}, and the worker then loads ${other ? 'base' : 'no model'}`, async () => {
        await writeFile(path.join(dir, 'ggml-large-v3-turbo.bin'), 'turbo model');
        if (other) await writeFile(path.join(dir, 'ggml-base.bin'), BASE);
        const w = await world(dir, { worker: 'running', listening, loaded: 'large-v3-turbo' });

        await w.invoke(CHANNEL.whisperDeleteModel, { variant: 'large-v3-turbo' });
        await w.settled();
        const before = [...w.calls];
        w.listening.now = false;
        w.restart.listeningChanged(false);
        await w.settled();
        const list = (await w.invoke(CHANNEL.whisperListInstalled)) as { inUse: string | null };

        expect(before).toEqual(expected === 'now' ? ['stop', 'start'] : []);
        expect(w.calls).toEqual(['stop', 'start']);
        expect(w.loadedModel()).toBe(other ? path.join(dir, 'ggml-base.bin') : null);
        expect(list.inUse).toBe(other ? 'base' : null);
      });
    }
  }

  it('a deferred restart under an open live listen: In use still shows the model the worker loaded, A, and B once the restart ran', async () => {
    await writeFile(path.join(dir, 'ggml-base.bin'), BASE);
    await writeFile(path.join(dir, 'ggml-tiny.bin'), 'tiny model');
    const w = await world(dir, { worker: 'running', listening: true, loaded: 'base' });
    const inUse = async () =>
      ((await w.invoke(CHANNEL.whisperListInstalled)) as { inUse: string | null }).inUse;

    const answer = await w.invoke(CHANNEL.whisperUseModel, { variant: 'tiny' });
    await w.settled();
    const deferred = await inUse();
    w.listening.now = false;
    w.restart.listeningChanged(false);
    await w.settled();

    expect(answer).toEqual({ variant: 'tiny', restart: 'deferred' });
    expect(deferred).toBe('base');
    expect(w.calls).toEqual(['stop', 'start']);
    expect(await inUse()).toBe('tiny');
  });

  it('a deferred restart made moot by stopping Scout no longer says Scout restarts when the live listen ends', async () => {
    await writeFile(path.join(dir, 'ggml-base.bin'), BASE);
    await writeFile(path.join(dir, 'ggml-tiny.bin'), 'tiny model');
    const w = await world(dir, { worker: 'running', listening: true, loaded: 'base' });
    await w.invoke(CHANNEL.whisperUseModel, { variant: 'tiny' });
    await w.settled();
    const whileListening = w.entry()?.title;

    await w.supervisor.stop();
    w.listening.now = false;
    w.restart.listeningChanged(false);
    await w.settled();

    expect(whileListening).toBe(RESTART_DEFERRED_TITLE);
    expect(w.entry()?.title).not.toBe(RESTART_DEFERRED_TITLE);
  });

  it('removing a model the worker did not load restarts nothing', async () => {
    await writeFile(path.join(dir, 'ggml-large-v3-turbo.bin'), 'turbo model');
    await writeFile(path.join(dir, 'ggml-base.bin'), BASE);
    const w = await world(dir, { worker: 'running', listening: false, loaded: 'large-v3-turbo' });

    await w.invoke(CHANNEL.whisperDeleteModel, { variant: 'base' });
    await w.settled();

    expect(w.calls).toEqual([]);
    expect(w.entry()).toBeUndefined();
  });

  it('clearing the saved choice from any screen goes through the restart path', async () => {
    await writeFile(path.join(dir, 'ggml-large-v3-turbo.bin'), 'turbo model');
    await writeFile(path.join(dir, 'ggml-tiny.bin'), 'tiny model');
    const w = await world(dir, { worker: 'running', listening: false, loaded: 'none' });
    await w.invoke(CHANNEL.vaultStore, { key: 'stt.model', value: 'whisper-tiny' });
    await w.settled();
    const afterStore = [...w.calls];

    await w.invoke(CHANNEL.vaultDelete, { key: 'stt.model' });
    await w.settled();

    expect(afterStore).toEqual(['stop', 'start']);
    expect(w.calls).toEqual(['stop', 'start', 'stop', 'start']);
    expect(w.loadedModel()).toBe(path.join(dir, 'ggml-large-v3-turbo.bin'));
  });

  for (const variant of ['../x', '__proto__', 'constructor', 'large-v4']) {
    it(`"Use this" with ${JSON.stringify(variant)}, a variant outside the pinned table, is refused: nothing is saved and nothing restarts`, async () => {
      await writeFile(path.join(dir, 'ggml-base.bin'), BASE);
      const w = await world(dir, { worker: 'running', listening: false, loaded: 'none' });

      const error = await w.invoke(CHANNEL.whisperUseModel, { variant }).catch((e: unknown) => e);
      await w.settled();

      expect({
        refusal: (error as Error | undefined)?.message,
        saved: w.stored.get('stt.model') ?? null,
        restarts: w.calls,
        notice: w.entry()?.title ?? null,
      }).toEqual({
        refusal: `whisper: unknown variant "${variant}"`,
        saved: null,
        restarts: [],
        notice: null,
      });
    });
  }

  it('the restart fails: the entry says Scout could not restart, with the reason', async () => {
    const w = await world(dir, {
      worker: 'running',
      listening: false,
      loaded: 'none',
      startFails: 'spawn ENOENT Scout.Host.Console',
    });

    await w.invoke(CHANNEL.whisperDownloadModel, { variant: 'base', requestId: 'r1' });
    await w.settled();

    expect(w.entry()).toMatchObject({
      kind: 'failed',
      title: RESTART_FAILED_TITLE,
      detail: 'spawn ENOENT Scout.Host.Console',
    });
  });

  it('"Use this" while Scout runs and the restart fails rejects with the reason, so the window keeps the previous model', async () => {
    const w = await world(dir, {
      worker: 'running',
      listening: false,
      loaded: 'base',
      startFails: 'spawn ENOENT Scout.Host.Console',
    });
    await writeFile(path.join(dir, 'ggml-base.bin'), BASE);
    await writeFile(path.join(dir, 'ggml-tiny.bin'), 'tiny model');

    const error = await w.invoke(CHANNEL.whisperUseModel, { variant: 'tiny' }).catch((e) => e);

    expect(readIpcError(error)).toEqual({
      code: 'whisper/restart-failed',
      message: 'spawn ENOENT Scout.Host.Console',
    });
    expect(w.entry()).toMatchObject({ kind: 'failed', detail: 'spawn ENOENT Scout.Host.Console' });
  });

  it('a voice entry whose download completes leads to exactly one worker restart, and then says Scout now transcribes voice messages', async () => {
    const w = await world(dir, { worker: 'running', listening: false, loaded: 'none' });
    w.voice.handleWorkerLine(
      JSON.stringify({ id: null, event: 'voice-no-model', platform: 'discord' }),
    );
    const id = w.store.list().find((n) => n.id.startsWith('voice-no-model:'))!.id;

    await w.invoke(CHANNEL.notificationsAct, { id, action: 'whisper-download-base' });
    await w.settled();
    await w.invoke(CHANNEL.vaultStore, { key: 'stt.model', value: 'whisper-base' });
    await w.settled();

    expect(w.calls).toEqual(['stop', 'start']);
    expect(w.store.list().find((n) => n.id === id)).toMatchObject({
      kind: 'done',
      detail: VOICE_TRANSCRIBING_DETAIL,
    });
  });
});

describe('main.ts wiring of the Whisper restart', () => {
  it('every download, every stt.model store, Use this and the end of the last live listen reach the restart, and the worker records the model it loaded', async () => {
    const main = await readFile(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');

    expect(main).toMatch(
      /createWhisperDownloader\([\s\S]*?voiceNotice\.modelInstalled\(\);\s*void workerRestart\?\.modelChanged\(\);/,
    );
    expect(main).toMatch(
      /registerVaultHandlers\([\s\S]*?if \(key === 'stt\.model'\) void workerRestart\?\.modelChanged\(\);/,
    );
    expect(main).toContain(
      'onListeningChanged: (listening) => workerRestart?.listeningChanged(listening),',
    );
    expect(main).toContain('whisperModelPath: (loadedWhisperModel = await currentWhisperModel()),');
    expect(main).toContain('onRestarted: () => voiceNotice.restarted(),');
    expect(main).toMatch(
      /registerWhisperHandlers\([\s\S]*?whisperDownloads,\s*\{\s*useModel: createWhisperModelUser\(vault, restart\),\s*readPreferred: \(\) => vault\.get\('stt\.model'\),\s*onDeleted: \(\) => void restart\.modelChanged\(\),\s*loadedModel: \(\) => restart\.loadedWhileRunning\(\),\s*\},\s*\);/,
    );
  });
});

describe('main.ts wiring of the one restart path', () => {
  it('the restart waits for the quick-ask answer as well as the live listen, quick ask holds questions during it, and the restart channel and every connection change go through it', async () => {
    const main = await readFile(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');

    expect(main).toContain('answering: () => quickAsk?.isAnswering() ?? false,');
    expect(main).toContain(
      'onAnsweringChanged: (answering) => workerRestart?.answeringChanged(answering),',
    );
    expect(main).toContain('restarting: () => workerRestart?.restarting() ?? false,');
    expect(main).toContain('onRestartSettled: () => quickAsk?.restartSettled(),');
    expect(main).toMatch(/registerWorkerRestartHandler\(\s*ipcMain,\s*restart,/);
    expect(main).not.toMatch(/restartWorkerIfRunning\(supervisor/);
    expect(main.match(/void restartWorkerIfRunning\(workerRestart, /g)?.length).toBe(8);
  });
});
