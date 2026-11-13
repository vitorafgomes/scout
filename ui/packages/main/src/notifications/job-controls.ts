import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ControlPlaneUnavailableError,
  ScoutError,
  encodeIpcErrorMessage,
  jobControls,
  serializeError,
  type AppNotification,
  type JobControlAction,
  type JobControlState,
  type WriteLogEntryDto,
} from '@scout/shared';
import type { ControlPlaneClient, JobControlRequest } from '../control-plane/client.js';
import { guardChannels, senderCheck, type SenderHooks } from '../ipc/sender-check.js';
import type { Logger } from '../util/logger.js';
import type { NotificationInput, NotificationStore } from './notification-store.js';
import { PAUSED_BY_YOU, testsId } from './sources.js';

export type JobControlClient = Pick<ControlPlaneClient, 'controlJob' | 'listWrites'>;

export interface JobControlsDeps {
  store: Pick<NotificationStore, 'list' | 'record' | 'markRead'>;
  client(): Promise<JobControlClient>;
  /** Runs `callback` once after `ms`; returns the cancel function. */
  setTimer(ms: number, callback: () => void): () => void;
  now(): number;
  logger: Logger;
}

/** How often main reads the write log for the record of a click the control plane queued. */
export const RECORD_POLL_MS = 2_000;
/**
 * How long main waits for that record: the two minutes the control plane keeps a click before it drops it as expired,
 * plus 30 seconds for the worker's poll and its settle.
 */
export const RECORD_WAIT_MS = 150_000;
/**
 * How long main waits for the worker's notice once the record says the click was applied: an audit's pause, stop and
 * continue are noticed only when the area in progress ends, which the worker gives ten minutes by default, plus a minute
 * for the notice to arrive.
 */
export const NOTICE_WAIT_MS = 660_000;
const WRITES_READ = 50;

export const NO_ANSWER =
  'The Scout control plane did not answer, so this click may not have reached Scout. Try again.';
export const NOT_CONFIRMED =
  'Not confirmed: Scout did not say within two and a half minutes whether it applied this click; look at the job before clicking again.';
export const NOT_REPORTED =
  'Scout recorded this click as applied, but the job did not report it; look at the job before clicking again.';
export const WORKER_STOPPED =
  'Scout stopped before it said how this click ended; look at the job before clicking again.';
export const TESTS_RUNNING = 'Running in a container on this computer.';
export const TESTS_FINISHED = 'The result is in the conversation.';
export const TESTS_ANSWER_FAILED = 'The answer ended before the tests reported a result.';
export const TESTS_WORKER_STOPPED = 'Scout stopped before the tests reported a result.';
const REFUSED = 'Scout could not send that control.';

const NAME = '[A-Za-z0-9_.-]+';
const RUN_TESTS = new RegExp(`^Run pull request tests: (${NAME}) (${NAME}) (\\d+)$`);
const AUDIT_ID = new RegExp(`^audit:(${NAME})/(${NAME})$`);
const TESTS_ID = new RegExp(`^tests:(${NAME})/(${NAME})#(\\d+)$`);
const AREA = /^Area (\d+) of (\d+) done/;
const STOP_COUNTS = /(\d+) of (\d+) areas done, \d+ not covered, (\d+) findings/;

/** The bell's pause, continue and stop of the jobs it shows, and the entries of the test runs a local turn starts. */
export interface JobControls {
  /**
   * Sends the owner's click on an entry this store holds and offers that control for, the job read from the entry's
   * own id; refuses any other entry or action, and a second click while one is unanswered.
   */
  control(id: string, action: JobControlAction): Promise<void>;
  /**
   * Records a worker notice: a running job's next area keeps the unanswered click and its note on the entry, and an
   * audit's pause keeps an unanswered Stop, which then ends the paused audit; any other notice on a job's entry ends
   * them; what answers the owner's own click leaves the entry read when it was, and a note on the entry makes it
   * unread.
   */
  notice(input: NotificationInput): void;
  /**
   * Applies the JSON of one `SCOUT_LOCAL` line: a test run's progress opens its entry, and the end of the last turn that
   * runs that pull request's tests closes it.
   */
  handleWorkerLine(json: string): void;
  /** The worker stopped: its test runs ended without a result and no unanswered click will be answered by it. */
  workerStopped(): void;
  dispose(): void;
}

function targetOf(id: string, action: JobControlAction): JobControlRequest | null {
  const audit = AUDIT_ID.exec(id);
  if (audit !== null) return { kind: 'audit', owner: audit[1]!, repo: audit[2]!, action };
  const tests = TESTS_ID.exec(id);
  if (tests === null || action !== 'stop') return null;
  const number = Number(tests[3]);
  if (!Number.isSafeInteger(number) || number < 1) return null;
  return { kind: 'tests', owner: tests[1]!, repo: tests[2]!, number, action };
}

function isJob(id: string): boolean {
  return id.startsWith('audit:') || id.startsWith('tests:');
}

function parse(json: string): Record<string, unknown> {
  try {
    const parsed: unknown = JSON.parse(json);
    return typeof parsed === 'object' && parsed !== null ? (parsed as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

/**
 * Keeps the state of the owner's clicks on the bell's jobs: asked, then waiting for the worker once the control plane
 * queued the click, until the worker's notice changes the entry or its record in the write log says the click changed
 * nothing, was not confirmed or ended a paused audit; a refusal, a control plane that does not answer, no record within
 * {@link RECORD_WAIT_MS}, and no notice within {@link NOTICE_WAIT_MS} of a record that says applied each show their
 * sentence on the entry and offer the controls again. The entry never shows a state the worker has not reported.
 */
export function createJobControls(deps: JobControlsDeps): JobControls {
  const { store, logger } = deps;
  const following = new Map<string, () => void>();
  const turns = new Map<string, Set<string>>();

  function entry(id: string): AppNotification | undefined {
    return store.list().find((n) => n.id === id);
  }

  function put(n: AppNotification, control: JobControlState | null, note: string | null): void {
    const { read, control: _control, note: _note, ...rest } = n;
    store.record({
      ...rest,
      ...(control === null ? {} : { control }),
      ...(note === null ? {} : { note }),
    });
    if (read && note === null) store.markRead([n.id]);
  }

  function settle(id: string, note: string): void {
    stopFollowing(id);
    const n = entry(id);
    if (n?.control !== undefined) put(n, null, note);
  }

  function stopFollowing(id: string): void {
    following.get(id)?.();
    following.delete(id);
  }

  function follow(id: string, controlId: string, deadline: number): void {
    following.set(
      id,
      deps.setTimer(RECORD_POLL_MS, () => {
        following.delete(id);
        void check(id, controlId, deadline);
      }),
    );
  }

  async function recordOf(controlId: string): Promise<WriteLogEntryDto | undefined> {
    try {
      const writes = await (await deps.client()).listWrites(WRITES_READ);
      return writes.find((w) => w.actionId === controlId && w.controlStatus !== null);
    } catch (err: unknown) {
      logger.warn('job controls: the write log was not read', { code: serializeError(err).code });
      return undefined;
    }
  }

  async function check(id: string, controlId: string, deadline: number): Promise<void> {
    if (entry(id)?.control?.phase !== 'waiting') return;
    const record = await recordOf(controlId);
    const n = entry(id);
    if (n?.control?.phase !== 'waiting' || following.has(id)) return;
    if (record === undefined) {
      if (deps.now() >= deadline) settle(id, NOT_CONFIRMED);
      else follow(id, controlId, deadline);
      return;
    }
    if (record.controlStatus !== 'applied') {
      settle(id, record.outcome);
      return;
    }
    awaitNotice(id, record.outcome, deps.now() + NOTICE_WAIT_MS);
  }

  function awaitNotice(id: string, outcome: string, until: number): void {
    const n = entry(id);
    if (n?.control?.phase !== 'waiting') return;
    if (n.kind === 'paused' && n.control.action === 'stop') {
      const counts = STOP_COUNTS.exec(outcome);
      const { read, control: _control, note: _note, progress: _progress, ...rest } = n;
      store.record({
        ...rest,
        kind: 'done',
        title: `Audit of ${n.id.slice('audit:'.length)} stopped`,
        detail:
          counts === null
            ? outcome
            : `${counts[1]} of ${counts[2]} areas · ${counts[3]} findings kept`,
      });
      if (read) store.markRead([n.id]);
      return;
    }
    if (deps.now() >= until) {
      settle(id, NOT_REPORTED);
      return;
    }
    following.set(
      id,
      deps.setTimer(RECORD_POLL_MS, () => {
        following.delete(id);
        awaitNotice(id, outcome, until);
      }),
    );
  }

  async function control(id: string, action: JobControlAction): Promise<void> {
    const n = entry(id);
    const target = n === undefined ? null : targetOf(n.id, action);
    if (
      n === undefined ||
      target === null ||
      n.control !== undefined ||
      !jobControls(n).includes(action)
    ) {
      throw new ScoutError('notifications/invalid-control', REFUSED);
    }
    put(n, { action, phase: 'asking' }, null);
    store.markRead([id]);
    let answer: Awaited<ReturnType<JobControlClient['controlJob']>>;
    try {
      answer = await (await deps.client()).controlJob(target);
    } catch (err: unknown) {
      logger.warn('job controls: the control plane did not take the click', {
        code: serializeError(err).code,
      });
      settle(
        id,
        err instanceof ControlPlaneUnavailableError
          ? NO_ANSWER
          : `Scout could not send this click: ${err instanceof Error ? err.message : String(err)}`,
      );
      return;
    }
    logger.info('job controls: the click was answered', {
      kind: target.kind,
      action,
      accepted: answer.accepted,
    });
    if (!answer.accepted || answer.controlId === null) {
      settle(id, answer.reason);
      return;
    }
    const asked = entry(id);
    if (asked?.control === undefined) return;
    put(asked, { action, phase: 'waiting' }, null);
    follow(id, answer.controlId, deps.now() + RECORD_WAIT_MS);
  }

  function notice(input: NotificationInput): void {
    const previous = entry(input.id);
    if (previous === undefined || !isJob(input.id)) {
      store.record(input);
      return;
    }
    const area = AREA.exec(previous.detail);
    const detail =
      input.kind === 'paused' && input.detail === PAUSED_BY_YOU && area !== null
        ? `${PAUSED_BY_YOU} · area ${area[1]} of ${area[2]} done`
        : input.detail;
    if (input.kind === 'paused' && previous.control?.action === 'stop') {
      store.record({ ...input, detail, control: previous.control });
    } else if (previous.kind !== 'running' || input.kind !== 'running') {
      stopFollowing(input.id);
      store.record({ ...input, detail });
    } else {
      store.record({
        ...input,
        detail,
        ...(previous.control === undefined ? {} : { control: previous.control }),
        ...(previous.note === undefined ? {} : { note: previous.note }),
      });
    }
    if (previous.read && previous.control !== undefined) store.markRead([input.id]);
  }

  function endTurn(turnId: string, kind: 'done' | 'failed'): void {
    const keys = turns.get(turnId);
    turns.delete(turnId);
    const live = new Set([...turns.values()].flatMap((held) => [...held]));
    for (const key of keys ?? []) {
      const n = entry(key);
      if (n?.kind !== 'running' || live.has(key)) continue;
      stopFollowing(key);
      store.record({
        id: key,
        kind,
        title: kind === 'done' ? `${n.title} finished` : `${n.title} did not finish`,
        detail: kind === 'done' ? TESTS_FINISHED : TESTS_ANSWER_FAILED,
        at: n.at,
        source: 'worker',
      });
      if (n.read && n.control !== undefined) store.markRead([key]);
    }
  }

  function handleWorkerLine(json: string): void {
    const p = parse(json);
    const turnId = p['id'];
    if (typeof turnId !== 'string') return;
    if (p['event'] === 'complete' || p['event'] === 'fail') {
      endTurn(turnId, p['event'] === 'complete' ? 'done' : 'failed');
      return;
    }
    const run = p['event'] === 'progress' ? RUN_TESTS.exec(String(p['text'])) : null;
    if (run === null) return;
    const pullRequest = `${run[1]}/${run[2]}#${run[3]}`;
    const key = testsId(pullRequest);
    turns.set(turnId, (turns.get(turnId) ?? new Set()).add(key));
    if (entry(key)?.kind === 'running') return;
    stopFollowing(key);
    store.record({
      id: key,
      kind: 'running',
      title: `Tests for ${pullRequest}`,
      detail: TESTS_RUNNING,
      source: 'worker',
    });
  }

  function workerStopped(): void {
    turns.clear();
    for (const n of store.list()) {
      if (n.id.startsWith('tests:') && n.kind === 'running') {
        stopFollowing(n.id);
        store.record({
          id: n.id,
          kind: 'failed',
          title: `${n.title} did not finish`,
          detail: TESTS_WORKER_STOPPED,
          at: n.at,
          source: 'worker',
        });
      } else if (isJob(n.id) && n.control !== undefined) {
        settle(n.id, WORKER_STOPPED);
      }
    }
  }

  return {
    control,
    notice,
    handleWorkerLine,
    workerStopped,
    dispose(): void {
      for (const cancel of following.values()) cancel();
      following.clear();
    },
  };
}

/**
 * `scout:notifications.control` with `{id, action}`: the bell's Pause, Continue and Stop, taken only from the windows
 * that show the bell, the main window and the operator window; the job comes from main's own entry.
 */
export function registerJobControlHandler(
  ownIpcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  jobs: Pick<JobControls, 'control'>,
  senders: SenderHooks,
  logger: Logger,
): () => void {
  const ipcMain = guardChannels(ownIpcMain, senderCheck(senders, logger), {
    [CHANNEL.notificationsControl]: {
      allowed: ['main', 'operator'],
      refusal: () =>
        new ScoutError(
          'ipc/not-bell-window',
          'Jobs can be paused, continued or stopped only from the bell of the Scout window or the operator window.',
        ),
    },
  });
  ipcMain.handle(CHANNEL.notificationsControl, async (_event, raw: unknown) => {
    try {
      const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
      const { id, action } = r;
      if (
        typeof id !== 'string' ||
        (action !== 'pause' && action !== 'continue' && action !== 'stop')
      ) {
        throw new ScoutError('notifications/invalid-control', REFUSED);
      }
      await jobs.control(id, action);
    } catch (err: unknown) {
      const serialized = serializeError(err);
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
  });
  return () => ownIpcMain.removeHandler(CHANNEL.notificationsControl);
}
