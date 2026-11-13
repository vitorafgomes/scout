import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { spawn, type SpawnOptions } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { CHANNEL, type WhisperVariant } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { createWhisperDownloader, registerWhisperHandlers } from '../src/ipc/whisper.js';
import { registerVaultHandlers } from '../src/ipc/vault.js';
import { NotificationStore } from '../src/notifications/notification-store.js';
import {
  createVoiceNotice,
  registerNotificationActions,
} from '../src/notifications/voice-notice.js';
import type { Vault } from '../src/vault/safe-storage-vault.js';
import { buildEnv } from '../src/worker/build-env.js';
import { createSupervisor, type Supervisor } from '../src/worker/supervisor.js';
import { chooseWhisperModel, type WhisperModelTable } from '../src/worker/whisper-model.js';
import { createWhisperModelUser, createWorkerRestart } from '../src/worker/worker-restart.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const SPAWNS_TIMEOUT_MS = 30_000;

const CONTENT: Record<WhisperVariant, string> = {
  tiny: 'tiny model',
  base: 'base model bytes',
  small: 'small model',
  'large-v3-turbo': 'turbo model',
  'large-v3': 'large model',
};
const sha256 = (text: string) => createHash('sha256').update(text).digest('hex');
const TABLE = Object.fromEntries(
  (Object.keys(CONTENT) as WhisperVariant[]).map((variant) => [
    variant,
    {
      file: `ggml-${variant}.bin`,
      size: Buffer.byteLength(CONTENT[variant]),
      sha256: sha256(CONTENT[variant]),
    },
  ]),
) as unknown as WhisperModelTable;

/**
 * The whole of main's model-switch path, as `main.ts` composes it: the real vault, Whisper and notification
 * handlers, the real restart policy, the real `buildEnv` and the real supervisor, which spawns a fake worker script
 * that records the model path each spawn received. The vault is a map and the download is served from memory.
 */
async function app(root: string, saved: string | null) {
  const { logger } = recordingLogger();
  const models = path.join(root, 'whisper-models');
  await mkdir(models, { recursive: true });
  const spawns = path.join(root, 'spawns.txt');
  const worker = path.join(root, 'fake-worker.mjs');
  await writeFile(
    worker,
    [
      "import { appendFileSync } from 'node:fs';",
      "appendFileSync(process.argv[2], (process.env.SCOUT__TRANSCRIPTION__MODELPATH ?? 'none') + '\\n');",
      "process.stdout.write('SCOUT_READY\\n');",
      'process.stdin.resume();',
      "process.stdin.on('end', () => process.exit(0));",
      '',
    ].join('\n'),
  );

  const stored = new Map<string, string>(saved === null ? [] : [['stt.model', saved]]);
  const vault = {
    store: async (key: string, value: string) => void stored.set(key, value),
    get: async (key: string) => stored.get(key) ?? null,
    delete: async (key: string) => stored.delete(key),
    has: async (key: string) => stored.has(key),
  } as unknown as Vault;
  const handlers = new Map<string, IpcHandler>();
  const ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'> = {
    handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
    removeHandler: (channel: string) => void handlers.delete(channel),
  };
  const listening = { now: false };
  let loaded: string | null = null;
  const currentModel = async () =>
    chooseWhisperModel(models, await vault.get('stt.model').catch(() => null), TABLE);

  const supervisor: Supervisor = createSupervisor({
    spawn: ((_binary: string, _args: readonly string[], options: SpawnOptions) =>
      spawn(process.execPath, [worker, spawns], options)) as unknown as typeof spawn,
    logger,
    resolveBinary: () => worker,
    killTree: (_pid, done) => done(),
    buildEnv: async () =>
      buildEnv({
        vaultGet: (key) => vault.get(key),
        whisperModelPath: (loaded = await currentModel()),
      }),
    readinessTimeoutMs: 5_000,
    stopTimeoutMs: 2_000,
  });
  const store = new NotificationStore({
    vault: { get: async () => null, store: async () => undefined },
    push: () => undefined,
    now: () => Date.now(),
    setTimer: () => () => undefined,
    logger,
  });
  const restart = createWorkerRestart({
    supervisor,
    currentModel,
    loadedModel: () => loaded,
    listening: () => listening.now,
    store,
    onRestarted: () => voiceNotice.restarted(),
    logger,
  });
  const fetchFn = (async (url: string) => {
    const variant = (Object.keys(TABLE) as WhisperVariant[]).find((v) =>
      url.endsWith(`/${TABLE[v].file}`),
    )!;
    return new Response(CONTENT[variant], {
      headers: { 'content-length': String(TABLE[variant].size) },
    });
  }) as unknown as typeof fetch;
  const downloads = createWhisperDownloader(fetchFn, logger, models, TABLE, () => {
    voiceNotice.modelInstalled();
    void restart.modelChanged();
  });
  const voiceNotice = createVoiceNotice({
    store,
    now: () => Date.now(),
    download: downloads,
    logger,
  });
  registerVaultHandlers(ipcMain, vault, logger, (key) => {
    if (key === 'stt.model') void restart.modelChanged();
  });
  registerNotificationActions(ipcMain, voiceNotice);
  registerWhisperHandlers(ipcMain, fetchFn, logger, models, TABLE, downloads, {
    useModel: createWhisperModelUser(vault, restart),
    readPreferred: () => vault.get('stt.model'),
    onDeleted: () => void restart.modelChanged(),
    loadedModel: () => restart.loadedWhileRunning(),
  });

  const invoke = (channel: string, req?: unknown) =>
    Promise.resolve(
      handlers.get(channel)!(
        { sender: { isDestroyed: () => false, send: () => undefined } } as never,
        req,
      ),
    );
  const install = async (...variants: WhisperVariant[]) => {
    for (const v of variants) await writeFile(path.join(models, TABLE[v].file), CONTENT[v]);
  };
  const spawned = async (): Promise<string[]> =>
    (await readFile(spawns, 'utf8').catch(() => '')).split('\n').filter((line) => line !== '');
  const settle = async () => {
    let seen = '';
    let quietSince = Date.now();
    for (const until = Date.now() + 5_000; Date.now() < until;) {
      await new Promise((resolve) => setTimeout(resolve, 20));
      const now = `${supervisor.status.state}|${(await spawned()).length}`;
      if (now !== seen) {
        seen = now;
        quietSince = Date.now();
      } else if (Date.now() - quietSince >= 300 && supervisor.status.state !== 'starting') return;
    }
  };
  const inUse = async (): Promise<string | null> => {
    const { inUse: variant } = (await invoke(CHANNEL.whisperListInstalled)) as {
      inUse: WhisperVariant | null;
    };
    return variant === null ? null : path.join(models, TABLE[variant].file);
  };
  const file = (variant: WhisperVariant) => path.join(models, TABLE[variant].file);
  return { supervisor, invoke, install, spawned, settle, inUse, file, listening, restart, store };
}

describe('the owner changes the voice model, and the worker that runs afterwards uses it', () => {
  let root: string;
  let world: Awaited<ReturnType<typeof app>> | null = null;

  beforeEach(async () => {
    root = await mkdtemp(path.join(os.tmpdir(), 'scout-model-switch-'));
  });

  afterEach(async () => {
    await world?.supervisor.stop().catch(() => undefined);
    world = null;
    await rm(root, { recursive: true, force: true });
  });

  async function running(saved: string | null, ...installed: WhisperVariant[]) {
    world = await app(root, saved);
    await world.install(...installed);
    await world.supervisor.start();
    return world;
  }

  describe('Use this on another model did nothing while Scout ran', () => {
    it(
      '1. two models installed, worker on A: Use this on B restarts once and the new worker loads B, the model In use',
      async () => {
        const w = await running('whisper-tiny', 'tiny', 'base');

        await w.invoke(CHANNEL.whisperUseModel, { variant: 'base' });
        await w.settle();

        expect(await w.spawned()).toEqual([w.file('tiny'), w.file('base')]);
        expect(await w.inUse()).toBe(w.file('base'));
      },
      SPAWNS_TIMEOUT_MS,
    );
  });

  describe('removing the in-use model left the worker on a deleted file', () => {
    it(
      '2. worker on A: removing A while B is installed restarts once and the new worker loads B, the model In use',
      async () => {
        const w = await running(null, 'base', 'large-v3-turbo');

        await w.invoke(CHANNEL.whisperDeleteModel, { variant: 'large-v3-turbo' });
        await w.settle();

        expect(await w.spawned()).toEqual([w.file('large-v3-turbo'), w.file('base')]);
        expect(await w.inUse()).toBe(w.file('base'));
      },
      SPAWNS_TIMEOUT_MS,
    );

    it(
      '3. worker on A: removing A with nothing else installed restarts once, the new worker gets no model, and nothing is In use',
      async () => {
        const w = await running(null, 'large-v3-turbo');

        await w.invoke(CHANNEL.whisperDeleteModel, { variant: 'large-v3-turbo' });
        await w.settle();

        expect(await w.spawned()).toEqual([w.file('large-v3-turbo'), 'none']);
        expect(await w.inUse()).toBeNull();
      },
      SPAWNS_TIMEOUT_MS,
    );
  });

  describe('voice messages kept saying they need a Whisper model after a download: the worker was not restarted', () => {
    const DOWNLOADS: Record<string, (w: Awaited<ReturnType<typeof app>>) => Promise<void>> = {
      'the bell action': async (w) => {
        w.store.record({
          id: 'voice-no-model:1',
          kind: 'failed',
          title: 'A voice message could not be transcribed: no Whisper model is installed.',
          detail: '1 voice message not transcribed, the last from Discord.',
          actions: [{ id: 'whisper-download-base', label: 'Download the base model (142 MB)' }],
          source: 'worker',
        });
        await w.invoke(CHANNEL.notificationsAct, {
          id: 'voice-no-model:1',
          action: 'whisper-download-base',
        });
      },
      'the Desktop dialog': async (w) => {
        await w.invoke(CHANNEL.whisperDownloadModel, { variant: 'base', requestId: 'desktop-1' });
      },
      'the Transcription screen': async (w) => {
        await w.invoke(CHANNEL.whisperDownloadModel, { variant: 'base', requestId: 'whisper-1' });
        await w.invoke(CHANNEL.vaultStore, { key: 'stt.model', value: 'whisper-base' });
      },
    };

    for (const [from, download] of Object.entries(DOWNLOADS)) {
      it(
        `4. no model installed: base downloaded from ${from} restarts once and the new worker loads base, the model In use`,
        async () => {
          const w = await running(null);

          await download(w);
          await w.settle();

          expect(await w.spawned()).toEqual(['none', w.file('base')]);
          expect(await w.inUse()).toBe(w.file('base'));
        },
        SPAWNS_TIMEOUT_MS,
      );
    }
  });

  it(
    '5. a live listen open during Use this: no restart until the listen ends, In use still shows A meanwhile, then exactly one restart, and the new worker loads B, now In use',
    async () => {
      const w = await running('whisper-tiny', 'tiny', 'base');
      w.listening.now = true;

      await w.invoke(CHANNEL.whisperUseModel, { variant: 'base' });
      await w.settle();
      const whileListening = await w.spawned();
      const inUseWhileListening = await w.inUse();
      w.listening.now = false;
      w.restart.listeningChanged(false);
      await w.settle();

      expect(whileListening).toEqual([w.file('tiny')]);
      expect(inUseWhileListening).toBe(w.file('tiny'));
      expect(await w.spawned()).toEqual([w.file('tiny'), w.file('base')]);
      expect(await w.inUse()).toBe(w.file('base'));
    },
    SPAWNS_TIMEOUT_MS,
  );

  it(
    '6. worker stopped: Use this on B, then start, spawns once with B and no extra restart',
    async () => {
      world = await app(root, 'whisper-tiny');
      const w = world;
      await w.install('tiny', 'base');

      await w.invoke(CHANNEL.whisperUseModel, { variant: 'base' });
      await w.supervisor.start();
      await w.settle();

      expect(await w.spawned()).toEqual([w.file('base')]);
      expect(await w.inUse()).toBe(w.file('base'));
    },
    SPAWNS_TIMEOUT_MS,
  );

  it(
    '7. the saved choice is not installed: the worker loads the best verified model, and In use names that one',
    async () => {
      const w = await running('whisper-base', 'large-v3-turbo');

      expect(await w.spawned()).toEqual([w.file('large-v3-turbo')]);
      expect(await w.inUse()).toBe(w.file('large-v3-turbo'));
      const { preferred } = (await w.invoke(CHANNEL.whisperListInstalled)) as { preferred: string };
      expect(preferred).toBe('base');
    },
    SPAWNS_TIMEOUT_MS,
  );
});
