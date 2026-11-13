import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type LocalSpeakResponse,
  type LocalVoice,
  type LocalVoicesResponse,
  type WorkerEvent,
} from '@scout/shared';
import type { Supervisor } from '../worker/supervisor.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';

export const SPEAK_TIMEOUT_MS = 60_000;
export const MAX_SPEAK_CHARS = 2000;
export const SPEAK_TIMEOUT_REPLY = 'Scout did not read that aloud in time.';
export const SPEAK_FAILED_REPLY = 'Scout could not read that aloud; see Scout logs.';
export const SPEAK_STOPPED_REPLY = 'Scout stopped before it read that aloud.';
export const SPEAK_INVALID_REPLY = `A sentence to read aloud is 1 to ${MAX_SPEAK_CHARS} characters.`;
export const SPEAK_NOT_RUNNING_REPLY = 'Scout is not running. Start it from the tray or the app.';
export const SAMPLE_INVALID_REPLY =
  'A sample names one voice: an AI Source voice or a local voice id.';
export const VOICES_TIMEOUT_MS = 10_000;
export const VOICES_TIMEOUT_REPLY = 'Scout did not list its voices in time.';
export const VOICES_FAILED_REPLY = 'Scout could not list its voices; see Scout logs.';
export const VOICES_STOPPED_REPLY = 'Scout stopped before it listed its voices.';

/** The vault keys of the voice saved in Models, Speech; main reads both for every sentence it sends. */
export const SPEECH_VOICE_KEYS = Object.freeze({
  provider: 'speech.voice.provider',
  local: 'speech.voice.local',
} as const);

const VOICE_ID = /^[A-Za-z0-9_:.#-]{1,80}$/;

export interface SpeechDeps {
  readonly ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>;
  readonly supervisor: Pick<Supervisor, 'writeStdin' | 'onEvent'>;
  readonly vault: Pick<Vault, 'get'>;
  readonly logger: Logger;
}

export interface Speech {
  /** Settles the speak or voices request a worker line answers; false when the line is not one. */
  apply(p: Record<string, unknown>): boolean;
  dispose(): void;
}

interface VoiceFields {
  readonly providerVoice?: string;
  readonly localVoice?: string;
  readonly localOnly?: true;
}

interface Pending {
  readonly settle: (outcome: unknown) => void;
  readonly failed: string;
  readonly stopped: string;
}

function ipcError(code: string, message: string): Error {
  const serialized = serializeError(new ScoutError(code, message));
  return Object.assign(new Error(encodeIpcErrorMessage(serialized)), { code: serialized.code });
}

function voiceId(value: unknown): string | undefined {
  return typeof value === 'string' && VOICE_ID.test(value) ? value : undefined;
}

function sampleVoice(sample: unknown): VoiceFields | null {
  if (typeof sample !== 'object' || sample === null || Object.keys(sample).length !== 1) {
    return null;
  }
  const s = sample as Record<string, unknown>;
  const provider = voiceId(s['provider']);
  if (provider !== undefined) return { providerVoice: provider };
  const local = voiceId(s['local']);
  return local === undefined ? null : { localVoice: local, localOnly: true };
}

function toVoices(p: Record<string, unknown>): LocalVoicesResponse | null {
  const { provider, providerVoices, localVoices, defaultLocal } = p;
  if (
    typeof provider !== 'string' ||
    !Array.isArray(providerVoices) ||
    !providerVoices.every((v) => typeof v === 'string') ||
    !Array.isArray(localVoices)
  ) {
    return null;
  }
  const local: LocalVoice[] = [];
  for (const v of localVoices as unknown[]) {
    const r = v as Record<string, unknown> | null;
    if (typeof r?.['id'] !== 'string' || typeof r['label'] !== 'string') return null;
    local.push({ id: r['id'], label: r['label'] });
  }
  return {
    provider,
    providerVoices: providerVoices as string[],
    localVoices: local,
    defaultLocal: typeof defaultLocal === 'string' ? defaultLocal : null,
  };
}

/**
 * The read-aloud channels. A sentence from a window goes to the worker as a `speak` line under its own `v-<n>` id, in
 * the voice saved in Models, Speech, read from the vault for every sentence so a new choice needs no restart, or in the
 * voice of a sample; the worker's `audio` line, or its `fail` with the reason, settles it. A voices request goes as a
 * `voices` line under `vl-<n>` and the worker's `voices` line settles it. A worker that exits, or no answer in time,
 * settles either with a sentence. The sentence is never logged, only its id and length.
 */
export function registerSpeech(deps: SpeechDeps): Speech {
  const { ipcMain, supervisor, vault, logger } = deps;
  const pending = new Map<string, Pending>();
  let count = 0;

  async function savedVoice(): Promise<VoiceFields> {
    const read = (key: (typeof SPEECH_VOICE_KEYS)[keyof typeof SPEECH_VOICE_KEYS]) =>
      vault.get(key).then(voiceId, (err: unknown) => {
        logger.warn('quick-ask: the saved voice could not be read', { key, error: String(err) });
        return undefined;
      });
    const [providerVoice, localVoice] = await Promise.all([
      read(SPEECH_VOICE_KEYS.provider),
      read(SPEECH_VOICE_KEYS.local),
    ]);
    return {
      ...(providerVoice === undefined ? {} : { providerVoice }),
      ...(localVoice === undefined ? {} : { localVoice }),
    };
  }

  function send<T>(
    id: string,
    line: Record<string, unknown>,
    timeoutMs: number,
    replies: { readonly timeout: string; readonly failed: string; readonly stopped: string },
    log: Record<string, unknown>,
  ): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      const settle = (outcome: unknown): void => {
        clearTimeout(timer);
        pending.delete(id);
        if (outcome instanceof Error) reject(outcome);
        else resolve(outcome as T);
      };
      const timer = setTimeout(() => {
        logger.warn('quick-ask: the worker did not answer a speech request in time', { id });
        settle(ipcError('local/speak-failed', replies.timeout));
      }, timeoutMs);
      pending.set(id, { settle, failed: replies.failed, stopped: replies.stopped });
      const accepted = supervisor.writeStdin(JSON.stringify(line), (err) => {
        logger.warn('quick-ask: speech request not delivered to the worker', {
          id,
          error: err.message,
        });
        settle(ipcError('local/speak-failed', replies.stopped));
      });
      if (!accepted) {
        settle(ipcError('local/not-running', SPEAK_NOT_RUNNING_REPLY));
        return;
      }
      logger.info('quick-ask: speech request sent to the worker', { id, ...log });
    });
  }

  async function speak(req: unknown): Promise<LocalSpeakResponse> {
    const r = req as Record<string, unknown> | null | undefined;
    const text = r?.['text'];
    if (typeof text !== 'string' || text.trim().length === 0 || text.length > MAX_SPEAK_CHARS) {
      throw ipcError('local/invalid-request', SPEAK_INVALID_REPLY);
    }
    const sample = r?.['sample'];
    const voice = sample === undefined ? await savedVoice() : sampleVoice(sample);
    if (voice === null) throw ipcError('local/invalid-request', SAMPLE_INVALID_REPLY);
    count += 1;
    const id = `v-${count}`;
    return send<LocalSpeakResponse>(
      id,
      { type: 'speak', id, text, ...voice },
      SPEAK_TIMEOUT_MS,
      { timeout: SPEAK_TIMEOUT_REPLY, failed: SPEAK_FAILED_REPLY, stopped: SPEAK_STOPPED_REPLY },
      { chars: text.length, sample: sample !== undefined, ...voice },
    );
  }

  function voices(): Promise<LocalVoicesResponse> {
    count += 1;
    const id = `vl-${count}`;
    return send<LocalVoicesResponse>(
      id,
      { type: 'voices', id },
      VOICES_TIMEOUT_MS,
      {
        timeout: VOICES_TIMEOUT_REPLY,
        failed: VOICES_FAILED_REPLY,
        stopped: VOICES_STOPPED_REPLY,
      },
      {},
    );
  }

  const unsubscribe = supervisor.onEvent((event: WorkerEvent) => {
    if (event.kind !== 'exit') return;
    for (const p of [...pending.values()]) p.settle(ipcError('local/speak-failed', p.stopped));
  });

  ipcMain.handle(CHANNEL.localSpeak, (_event, req: unknown) => speak(req));
  ipcMain.handle(CHANNEL.localVoices, () => voices());

  return {
    apply(p: Record<string, unknown>): boolean {
      const id = p['id'];
      const request = typeof id === 'string' ? pending.get(id) : undefined;
      if (request === undefined) return false;
      const { event, contentType, base64, text } = p;
      if (event === 'audio' && typeof contentType === 'string' && typeof base64 === 'string') {
        request.settle({ contentType, base64 });
      } else if (event === 'voices') {
        request.settle(toVoices(p) ?? ipcError('local/speak-failed', request.failed));
      } else if (event === 'fail') {
        request.settle(
          ipcError(
            'local/speak-failed',
            typeof text === 'string' && text.length > 0 ? text : request.failed,
          ),
        );
      }
      return true;
    },
    dispose(): void {
      unsubscribe();
      ipcMain.removeHandler(CHANNEL.localSpeak);
      ipcMain.removeHandler(CHANNEL.localVoices);
      for (const p of [...pending.values()]) p.settle(ipcError('local/speak-failed', p.stopped));
    },
  };
}
