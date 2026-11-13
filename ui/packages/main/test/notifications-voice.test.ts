import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { CHANNEL, readIpcError, type AppNotification } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { createWhisperDownloader, registerWhisperHandlers } from '../src/ipc/whisper.js';
import { NotificationStore } from '../src/notifications/notification-store.js';
import { workerNotice } from '../src/notifications/sources.js';
import {
  VOICE_ACTIONS,
  VOICE_MODEL_INSTALLED_DETAIL,
  VOICE_MODEL_INSTALLED_TITLE,
  VOICE_NO_MODEL_TITLE,
  createVoiceNotice,
  registerNotificationActions,
} from '../src/notifications/voice-notice.js';
import type { WhisperModelTable } from '../src/worker/whisper-model.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const MINUTE = 60_000;
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

const line = (platform: unknown = 'discord', extra: Record<string, unknown> = {}) =>
  JSON.stringify({ id: null, event: 'voice-no-model', platform, ...extra });

function gate() {
  let open: () => void = () => undefined;
  const opened = new Promise<void>((resolve) => {
    open = resolve;
  });
  return { opened, open };
}

describe('the voice message notice in the notification center', () => {
  let dir: string;
  let respond: () => Promise<Response>;
  let fetched: string[];

  beforeEach(async () => {
    dir = await mkdtemp(path.join(os.tmpdir(), 'scout-voice-notice-'));
    fetched = [];
    respond = async () =>
      new Response(MODEL, { headers: { 'content-length': String(Buffer.byteLength(MODEL)) } });
  });

  afterEach(async () => {
    await rm(dir, { recursive: true, force: true });
  });

  function setup() {
    const clock = { now: 1_000_000 };
    const { logger, lines } = recordingLogger();
    const store = new NotificationStore({
      vault: { get: async () => null, store: async () => undefined },
      push: () => undefined,
      now: () => clock.now,
      setTimer: () => () => undefined,
      logger,
    });
    const fetchFn = (async (url: string) => {
      fetched.push(url);
      return respond();
    }) as unknown as typeof fetch;
    const download = createWhisperDownloader(fetchFn, logger, dir, TABLE, () =>
      voice.modelInstalled(),
    );
    const voice = createVoiceNotice({ store, now: () => clock.now, download, logger });
    const handlers = new Map<string, IpcHandler>();
    const ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'> = {
      handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
      removeHandler: (channel: string) => void handlers.delete(channel),
    };
    const dispose = registerNotificationActions(ipcMain, voice);
    registerWhisperHandlers(ipcMain, fetchFn, logger, dir, TABLE, download);
    const act = async (req: unknown): Promise<unknown> =>
      handlers.get(CHANNEL.notificationsAct)!(null as unknown as IpcMainInvokeEvent, req);
    const settled = async () => {
      for (let i = 0; i < 20; i += 1) await new Promise((resolve) => setTimeout(resolve, 0));
    };
    const voiceEntries = () => store.list().filter((n) => n.id.startsWith('voice-no-model:'));
    return { clock, store, voice, act, settled, voiceEntries, lines, handlers, dispose };
  }

  it('a voice message without a model becomes one unread entry with the two actions', () => {
    const t = setup();

    t.voice.handleWorkerLine(line('discord'));

    const [entry] = t.voiceEntries();
    expect(t.voiceEntries()).toHaveLength(1);
    expect(entry).toMatchObject({
      kind: 'failed',
      title: 'A voice message could not be transcribed: no Whisper model is installed.',
      detail: '1 voice message not transcribed, the last from Discord.',
      read: false,
      source: 'worker',
      actions: [
        { id: 'whisper-download-base', label: 'Download the base model (142 MB)' },
        { id: 'open-models-transcription', label: 'Open Models, Transcription' },
      ],
    });
  });

  const repeats: [string, number[], string[], string][] = [
    [
      'three within an hour update one entry',
      [0, 30, 59],
      ['discord', 'slack', 'teams'],
      '3 voice messages not transcribed, the last from Teams.',
    ],
    [
      'one 61 minutes after the first opens a second entry',
      [0, 61],
      ['discord', 'desktop'],
      '1 voice message not transcribed, the last from this computer.',
    ],
  ];

  for (const [name, minutes, platforms, latest] of repeats) {
    it(`repeated voice messages: ${name}`, () => {
      const t = setup();
      const start = t.clock.now;

      minutes.forEach((minute, i) => {
        t.clock.now = start + minute * MINUTE;
        t.voice.handleWorkerLine(line(platforms[i]));
      });

      const expectedEntries = minutes.at(-1)! < 60 ? 1 : 2;
      expect(t.voiceEntries()).toHaveLength(expectedEntries);
      expect(t.voiceEntries()[0]!.detail).toBe(latest);
      expect(t.voiceEntries().at(-1)!.at).toBe(start);
    });
  }

  for (const [name, json] of [
    ['another event', JSON.stringify({ id: null, event: 'usage', source: 'Ollama' })],
    ['a conversation event', JSON.stringify({ id: 'q-1', event: 'voice-no-model' })],
    ['not JSON', '{voice'],
  ] as const) {
    it(`${name} records nothing`, () => {
      const t = setup();

      t.voice.handleWorkerLine(json);

      expect(t.store.list()).toEqual([]);
    });
  }

  it('a platform main does not know is shown as a chat, never as what the worker sent', () => {
    const t = setup();

    t.voice.handleWorkerLine(line('<b>someone@example.com</b>'));

    expect(t.voiceEntries()[0]!.detail).toBe(
      '1 voice message not transcribed, the last from a chat.',
    );
  });

  it('the worker notice reader leaves the voice line to this entry', () => {
    expect(workerNotice(line(), () => 'worker:1')).toBeNull();
  });

  it('the download action downloads the base model, shows its progress in the entry, then marks it done', async () => {
    const t = setup();
    t.voice.handleWorkerLine(line());
    const id = t.voiceEntries()[0]!.id;
    const release = gate();
    respond = async () => {
      await release.opened;
      return new Response(MODEL, {
        headers: { 'content-length': String(Buffer.byteLength(MODEL)) },
      });
    };

    await t.act({ id, action: 'whisper-download-base' });
    const during = t.voiceEntries()[0]!;
    release.open();
    await t.settled();

    expect(fetched).toEqual([
      'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin',
    ]);
    expect(during).toMatchObject({ kind: 'running', progress: 0, actions: VOICE_ACTIONS });
    expect(await readFile(path.join(dir, 'ggml-base.bin'), 'utf8')).toBe(MODEL);
    expect(t.voiceEntries()).toEqual([
      expect.objectContaining({
        id,
        kind: 'done',
        title: VOICE_MODEL_INSTALLED_TITLE,
        detail: VOICE_MODEL_INSTALLED_DETAIL,
        read: false,
      }) as AppNotification,
    ]);
    expect(t.voiceEntries()[0]!.actions).toBeUndefined();
  });

  for (const [name, failure, reason] of [
    [
      'HuggingFace answering 503',
      async () => new Response('busy', { status: 503 }),
      'The base model was not downloaded: whisper: HuggingFace returned HTTP 503',
    ],
    [
      'the connection reset',
      async () => {
        throw new Error('ECONNRESET');
      },
      'The base model was not downloaded: whisper: download failed (Error: ECONNRESET)',
    ],
  ] as const) {
    it(`a failing download, ${name}, shows its reason in the entry and keeps the actions to try again`, async () => {
      const t = setup();
      t.voice.handleWorkerLine(line());
      const id = t.voiceEntries()[0]!.id;
      respond = failure;

      await t.act({ id, action: 'whisper-download-base' });
      await t.settled();

      expect(t.voiceEntries()[0]).toMatchObject({
        id,
        kind: 'failed',
        detail: reason,
        actions: VOICE_ACTIONS,
      });
      expect(t.lines.some((l) => l.includes('the base model was not downloaded'))).toBe(true);
    });
  }

  it('a second click while the download runs starts no second download', async () => {
    const t = setup();
    t.voice.handleWorkerLine(line());
    const id = t.voiceEntries()[0]!.id;
    const release = gate();
    respond = async () => {
      await release.opened;
      return new Response(MODEL, {
        headers: { 'content-length': String(Buffer.byteLength(MODEL)) },
      });
    };

    await t.act({ id, action: 'whisper-download-base' });
    await t.act({ id, action: 'whisper-download-base' });
    release.open();
    await t.settled();

    expect(fetched).toHaveLength(1);
    expect(t.voiceEntries()[0]!.kind).toBe('done');
  });

  it('a voice message that arrives during the download keeps counting from the messages before it', async () => {
    const t = setup();
    for (const platform of ['discord', 'slack', 'teams']) t.voice.handleWorkerLine(line(platform));
    const id = t.voiceEntries()[0]!.id;
    const release = gate();
    respond = async () => {
      await release.opened;
      return new Response(MODEL, {
        headers: { 'content-length': String(Buffer.byteLength(MODEL)) },
      });
    };
    await t.act({ id, action: 'whisper-download-base' });

    t.voice.handleWorkerLine(line('discord'));
    const during = t.voiceEntries()[0]!.detail;
    release.open();
    await t.settled();

    expect(during).toContain('4 voice messages');
  });

  it('a model downloaded later from Local Models marks every open voice entry done', async () => {
    const t = setup();
    t.voice.handleWorkerLine(line('discord'));
    t.clock.now += 90 * MINUTE;
    t.voice.handleWorkerLine(line('slack'));
    const sender = { isDestroyed: () => false, send: () => undefined };

    await t.handlers.get(CHANNEL.whisperDownloadModel)!(
      { sender } as unknown as IpcMainInvokeEvent,
      {
        variant: 'base',
        requestId: 'local-models-1',
      },
    );

    expect(t.voiceEntries().map((n) => [n.kind, n.title])).toEqual([
      ['done', VOICE_MODEL_INSTALLED_TITLE],
      ['done', VOICE_MODEL_INSTALLED_TITLE],
    ]);
  });

  it('after the model is installed, a new voice message opens a new entry', async () => {
    const t = setup();
    t.voice.handleWorkerLine(line());
    t.voice.modelInstalled();

    t.voice.handleWorkerLine(line());

    expect(t.voiceEntries().map((n) => [n.kind, n.title])).toEqual([
      ['failed', VOICE_NO_MODEL_TITLE],
      ['done', VOICE_MODEL_INSTALLED_TITLE],
    ]);
  });

  for (const [name, req] of [
    [
      'an entry main does not hold',
      { id: 'voice-no-model:missing', action: 'whisper-download-base' },
    ],
    [
      'the Models action, which runs in the window',
      { id: 'X', action: 'open-models-transcription' },
    ],
    ['an unknown action', { id: 'X', action: 'delete-everything' }],
    ['no request', undefined],
  ] as const) {
    it(`refuses ${name} and downloads nothing`, async () => {
      const t = setup();
      t.voice.handleWorkerLine(line());
      const id = t.voiceEntries()[0]!.id;
      const request = req === undefined ? undefined : { ...req, id: req.id === 'X' ? id : req.id };

      const error = await t.act(request).catch((err: unknown) => err);

      expect(readIpcError(error)).toEqual({
        code: 'notifications/invalid-request',
        message: 'Scout could not run that action.',
      });
      expect(fetched).toEqual([]);
    });
  }

  it('refuses an entry without the download action, such as a GitHub entry', async () => {
    const t = setup();
    t.store.record({ id: 'github:1', kind: 'github', title: 'PR', detail: '', source: 'github' });

    const error = await t.act({ id: 'github:1', action: 'whisper-download-base' }).catch((e) => e);

    expect(readIpcError(error).code).toBe('notifications/invalid-request');
    expect(fetched).toEqual([]);
  });

  it('dispose removes the action channel', () => {
    const t = setup();

    t.dispose();

    expect(t.handlers.has(CHANNEL.notificationsAct)).toBe(false);
  });
});

describe('main.ts wiring of the voice notice', () => {
  it('every worker line reaches the voice notice, and the one Whisper downloader serves Local Models and the entry, marking the entry done', async () => {
    const main = await readFile(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');
    const localEvent = main.slice(main.indexOf('onLocalEvent: (json) => {'));

    expect(localEvent.slice(0, localEvent.indexOf('},'))).toContain(
      'voiceNotice.handleWorkerLine(json);',
    );
    expect(main).toMatch(
      /createWhisperDownloader\([\s\S]*?\(\) => \{\s*voiceNotice\.modelInstalled\(\);/,
    );
    expect(main).toMatch(/registerWhisperHandlers\([\s\S]*?whisperDownloads,/);
    expect(main).toContain('registerNotificationActions(ipcMain, voiceNotice);');
    expect(main).toContain('unregisterNotificationActions?.();');
  });
});
