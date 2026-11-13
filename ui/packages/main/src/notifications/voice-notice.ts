import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type AppNotification,
  type NotificationAction,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { WhisperDownloader } from '../ipc/whisper.js';
import type { NotificationStore } from './notification-store.js';

export const VOICE_NO_MODEL_TITLE =
  'A voice message could not be transcribed: no Whisper model is installed.';
export const VOICE_MODEL_INSTALLED_TITLE = 'A Whisper model is installed.';
export const VOICE_MODEL_INSTALLED_DETAIL = 'Scout transcribes voice messages after it restarts.';
export const VOICE_TRANSCRIBING_DETAIL = 'Scout now transcribes voice messages.';
export const DOWNLOADING_DETAIL = 'Downloading the base model.';
export const VOICE_ACTIONS: readonly NotificationAction[] = [
  { id: 'whisper-download-base', label: 'Download the base model (142 MB)' },
  { id: 'open-models-transcription', label: 'Open Models, Transcription' },
];

const ID_PREFIX = 'voice-no-model';
const SAME_ENTRY_MS = 60 * 60 * 1000;
const PLATFORMS: Readonly<Record<string, string>> = {
  discord: 'Discord',
  slack: 'Slack',
  teams: 'Teams',
  desktop: 'this computer',
};
const REFUSED = 'Scout could not run that action.';

export interface VoiceNoticeDeps {
  store: Pick<NotificationStore, 'list' | 'record' | 'nextId'>;
  now(): number;
  download: WhisperDownloader;
  logger: Logger;
}

/** The notification center's entry for voice messages Scout could not transcribe for lack of a Whisper model. */
export interface VoiceNotice {
  /** Applies the JSON of one `SCOUT_LOCAL` line; only a `voice-no-model` event records or updates the entry. */
  handleWorkerLine(json: string): void;
  /** Runs an entry's download action: the base model, its progress in the entry, its failure's reason in the entry. */
  downloadBase(id: string): void;
  /** Marks every open voice entry done; a Whisper model was downloaded, from the entry or from Local Models. */
  modelInstalled(): void;
  /** The worker restarted with the model: every done voice entry says Scout now transcribes voice messages. */
  restarted(): void;
}

function isVoiceEntry(n: AppNotification): boolean {
  return n.id.startsWith(`${ID_PREFIX}:`);
}

function countIn(detail: string): number {
  const found = /^(\d+) voice messages? /.exec(detail)?.[1];
  return found === undefined ? 0 : Number(found);
}

function detailFor(count: number, platform: string): string {
  return `${count} voice message${count === 1 ? '' : 's'} not transcribed, the last from ${platform}.`;
}

/**
 * Records one entry per hour of voice messages that arrived without a Whisper model: the first message opens it,
 * every later one within the hour updates it, counting on through a download that shows its own detail, and a message
 * after that hour opens another. The entry offers the
 * base model's download and the Models screen; a download marks every open entry done.
 */
export function createVoiceNotice(deps: VoiceNoticeDeps): VoiceNotice {
  const { store, now, logger } = deps;
  let downloading: string | null = null;
  const counts = new Map<string, number>();

  function openEntry(): AppNotification | undefined {
    return store.list().find((n) => isVoiceEntry(n) && n.kind !== 'done');
  }

  function handleWorkerLine(json: string): void {
    let parsed: unknown;
    try {
      parsed = JSON.parse(json);
    } catch {
      return;
    }
    const p = (typeof parsed === 'object' && parsed !== null ? parsed : {}) as Record<
      string,
      unknown
    >;
    if (p['event'] !== 'voice-no-model' || p['id'] !== null) return;
    const platform =
      typeof p['platform'] === 'string' && Object.hasOwn(PLATFORMS, p['platform'])
        ? PLATFORMS[p['platform']]!
        : 'a chat';
    const open = openEntry();
    const sameHour = open !== undefined && now() - open.at < SAME_ENTRY_MS;
    const count = sameHour ? (counts.get(open.id) ?? countIn(open.detail)) + 1 : 1;
    const id = sameHour ? open.id : store.nextId(ID_PREFIX);
    counts.set(id, count);
    store.record({
      id,
      kind: sameHour && open.kind === 'running' ? 'running' : 'failed',
      title: VOICE_NO_MODEL_TITLE,
      detail: detailFor(count, platform),
      ...(sameHour ? { at: open.at } : {}),
      ...(sameHour && open.progress !== undefined ? { progress: open.progress } : {}),
      actions: VOICE_ACTIONS,
      source: 'worker',
    });
  }

  function downloadBase(id: string): void {
    const entry = store.list().find((n) => n.id === id);
    if (
      entry === undefined ||
      !isVoiceEntry(entry) ||
      !(entry.actions ?? []).some((a) => a.id === 'whisper-download-base')
    ) {
      throw new ScoutError('notifications/invalid-request', REFUSED);
    }
    if (downloading !== null) return;
    downloading = id;
    const progressOf = (progress: number) =>
      store.record({
        ...entry,
        kind: 'running',
        detail: DOWNLOADING_DETAIL,
        progress,
        actions: VOICE_ACTIONS,
      });
    progressOf(0);
    deps
      .download('base', (percent) => progressOf(percent))
      .catch((err: unknown) => {
        const reason = err instanceof Error ? err.message : String(err);
        logger.warn('voice notice: the base model was not downloaded', { reason });
        store.record({
          ...entry,
          kind: 'failed',
          detail: `The base model was not downloaded: ${reason}`,
          actions: VOICE_ACTIONS,
        });
      })
      .finally(() => {
        downloading = null;
      });
  }

  function modelInstalled(): void {
    for (const entry of store.list().filter((n) => isVoiceEntry(n) && n.kind !== 'done')) {
      store.record({
        id: entry.id,
        kind: 'done',
        title: VOICE_MODEL_INSTALLED_TITLE,
        detail: VOICE_MODEL_INSTALLED_DETAIL,
        at: entry.at,
        source: entry.source,
      });
    }
  }

  function restarted(): void {
    for (const entry of store
      .list()
      .filter(
        (n) => isVoiceEntry(n) && n.kind === 'done' && n.detail !== VOICE_TRANSCRIBING_DETAIL,
      )) {
      store.record({
        id: entry.id,
        kind: 'done',
        title: entry.title,
        detail: VOICE_TRANSCRIBING_DETAIL,
        at: entry.at,
        source: entry.source,
      });
    }
  }

  return { handleWorkerLine, downloadBase, modelInstalled, restarted };
}

/** `scout:notifications.act`: an entry's action main runs; the Models action runs in the window, never here. */
export function registerNotificationActions(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  voice: Pick<VoiceNotice, 'downloadBase'>,
): () => void {
  ipcMain.handle(CHANNEL.notificationsAct, (_event, raw: unknown) => {
    try {
      const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
      if (typeof r['id'] !== 'string' || r['action'] !== 'whisper-download-base') {
        throw new ScoutError('notifications/invalid-request', REFUSED);
      }
      voice.downloadBase(r['id']);
    } catch (err: unknown) {
      const serialized = serializeError(err);
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
  });
  return () => ipcMain.removeHandler(CHANNEL.notificationsAct);
}
