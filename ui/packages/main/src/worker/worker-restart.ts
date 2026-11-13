import {
  ScoutError,
  type AppNotification,
  type WhisperUseModelResponse,
  type WhisperVariant,
  type WorkerRestartResponse,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { NotificationStore } from '../notifications/notification-store.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Supervisor } from './supervisor.js';

export const RESTART_ENTRY_ID = 'whisper-model-restart';
export const RESTART_DEFERRED_TITLE =
  'The voice model is ready; Scout restarts to use it when the live listen ends.';
export const RESTARTING_TITLE = 'Scout is restarting to use the voice model.';
export const RESTARTED_TITLE = 'Scout restarted and now uses the voice model.';
export const RESTART_FAILED_TITLE = 'Scout could not restart to use the voice model.';
export const RESTART_MOOT_TITLE = 'Scout stopped; it uses the voice model when it next starts.';
export const WORKER_RESTART_ENTRY_ID = 'worker-restart';
export const WORKER_RESTART_WAITS_TITLE =
  'Scout restarts once the running answer or the live listen ends.';
export const WORKER_RESTARTED_TITLE = 'Scout restarted.';
export const WORKER_RESTART_FAILED_TITLE = 'Scout could not restart.';
export const WORKER_RESTART_DROPPED_TITLE =
  'Scout stopped; it reads the change when it next starts.';

export interface WorkerRestartDeps {
  supervisor: Pick<Supervisor, 'status' | 'start' | 'stop'>;
  /** The model the worker would load if it started now, or null for none. */
  currentModel(): Promise<string | null>;
  /** The model the running worker was started with, or null for none. */
  loadedModel(): string | null;
  /** True while a live listen is open in the quick-ask window. */
  listening(): boolean;
  /** True while a quick-ask answer is running, a question held for the worker included; omitted, no answer waits. */
  answering?: () => boolean;
  store: Pick<NotificationStore, 'record'>;
  /** Runs after a restart that loaded the new model. */
  onRestarted(): void;
  /** Runs once a restart's stop and start have settled, whatever the outcome, after `restarting()` turned false. */
  onRestartSettled?: () => void;
  logger: Logger;
}

/**
 * The worker's one restart path. A running worker restarts at once, or once the running answer and the live listen
 * have ended; a Whisper model it would load and did not is one reason, any setting it reads only as it starts another.
 */
export interface WorkerRestart {
  /** A Whisper model was downloaded, chosen or removed; resolves with what happened. */
  modelChanged(): Promise<WorkerRestartResponse>;
  /**
   * A change the worker reads only as it starts; resolves with what happened. `announce` is for a restart no card
   * asked for: its wait and its outcome go to the bell. A deferred restart that fails goes to the bell either way.
   */
  restart(reason: string, options?: { announce?: boolean }): Promise<WorkerRestartResponse>;
  /** The quick-ask window's live listening started or ended; an end runs a deferred restart once nothing runs. */
  listeningChanged(listening: boolean): void;
  /** A quick-ask answer started or ended; an end runs a deferred restart once nothing runs. */
  answeringChanged(answering: boolean): void;
  /** True from a restart's stop until its start settles. */
  restarting(): boolean;
  /** The model file the running worker loaded, null for none, or undefined while no worker runs. */
  loadedWhileRunning(): string | null | undefined;
}

function entry(kind: AppNotification['kind'], title: string, detail = '') {
  return { id: RESTART_ENTRY_ID, kind, title, detail, source: 'worker' as const };
}

function workerEntry(kind: AppNotification['kind'], title: string, detail = '') {
  return { id: WORKER_RESTART_ENTRY_ID, kind, title, detail, source: 'worker' as const };
}

/**
 * The worker registers its Whisper transcriber only when a model exists as it starts, and reads its provider, models
 * and connections only as it starts, so each of those changes needs a restart. A restart never cuts a running answer
 * or a live listen: it waits, and one restart runs once the last of them ends. Requests are handled one at a time,
 * so a second Whisper change during a restart finds the model already loaded and does nothing. A deferred restart
 * that finds the worker stopped is dropped, and its bell entry says so.
 */
export function createWorkerRestart(deps: WorkerRestartDeps): WorkerRestart {
  const { supervisor, store, logger } = deps;
  let voiceWaits = false;
  let otherWaits = false;
  let announced = false;
  let inFlight = false;
  let chain: Promise<WorkerRestartResponse> = Promise.resolve({ outcome: 'none' });

  function running(): boolean {
    const state = supervisor.status.state;
    return state === 'running' || state === 'starting';
  }

  function busy(): boolean {
    return deps.listening() || deps.answering?.() === true;
  }

  function moot(): WorkerRestartResponse {
    otherWaits = false;
    if (announced) {
      announced = false;
      store.record(workerEntry('done', WORKER_RESTART_DROPPED_TITLE));
    }
    if (voiceWaits) {
      voiceWaits = false;
      store.record(entry('done', RESTART_MOOT_TITLE));
    }
    return { outcome: 'none' };
  }

  async function attempt(
    reason: string,
    voice: boolean,
    announce = false,
  ): Promise<WorkerRestartResponse> {
    if (!running()) return moot();
    if (voice && (await deps.currentModel()) === deps.loadedModel()) return { outcome: 'none' };
    if (busy()) {
      if (voice) {
        voiceWaits = true;
        store.record(entry('running', RESTART_DEFERRED_TITLE));
      } else {
        otherWaits = true;
        if (announce && !announced) {
          announced = true;
          store.record(workerEntry('running', WORKER_RESTART_WAITS_TITLE));
        }
      }
      return { outcome: 'deferred' };
    }
    const withVoice = voice || voiceWaits;
    const otherWaited = otherWaits;
    const wasAnnounced = announced;
    voiceWaits = false;
    otherWaits = false;
    announced = false;
    if (withVoice) store.record(entry('running', RESTARTING_TITLE));
    logger.info('worker restart: restarting the worker', { reason });
    inFlight = true;
    try {
      await supervisor.stop();
      await supervisor.start();
    } catch (err: unknown) {
      const failure = err instanceof Error ? err.message : String(err);
      logger.warn('worker restart: the worker did not restart', { reason: failure, cause: reason });
      if (withVoice) store.record(entry('failed', RESTART_FAILED_TITLE, failure));
      if (otherWaited || wasAnnounced) {
        store.record(workerEntry('failed', WORKER_RESTART_FAILED_TITLE, failure));
      }
      return { outcome: 'failed', reason: failure };
    } finally {
      inFlight = false;
      deps.onRestartSettled?.();
    }
    if (withVoice) {
      store.record(entry('done', RESTARTED_TITLE));
      deps.onRestarted();
    }
    if (wasAnnounced) store.record(workerEntry('done', WORKER_RESTARTED_TITLE));
    return { outcome: 'now' };
  }

  function enqueue(
    reason: string,
    voice: boolean,
    announce = false,
  ): Promise<WorkerRestartResponse> {
    const run = (): Promise<WorkerRestartResponse> => attempt(reason, voice, announce);
    chain = chain.then(run, run);
    return chain;
  }

  function ended(): void {
    if ((voiceWaits || otherWaits) && !busy()) void enqueue('deferred', false);
  }

  function loadedWhileRunning(): string | null | undefined {
    return running() ? deps.loadedModel() : undefined;
  }

  return {
    modelChanged: () => enqueue('voice model', true),
    restart: (reason, options) => enqueue(reason, false, options?.announce === true),
    listeningChanged: (listening) => {
      if (!listening) ended();
    },
    answeringChanged: (answering) => {
      if (!answering) ended();
    },
    restarting: () => inFlight,
    loadedWhileRunning,
  };
}

/** Saves a variant as the model in use and has the worker load it; a restart that fails rejects with its reason. */
export function createWhisperModelUser(
  vault: Pick<Vault, 'store'>,
  restart: Pick<WorkerRestart, 'modelChanged'>,
): (variant: WhisperVariant) => Promise<WhisperUseModelResponse> {
  return async (variant) => {
    await vault.store('stt.model', `whisper-${variant}`);
    const result = await restart.modelChanged();
    if (result.outcome === 'failed') {
      throw new ScoutError('whisper/restart-failed', result.reason);
    }
    return { variant, restart: result.outcome };
  };
}
