import { describe, expect, it } from 'bun:test';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import type { AppNotification, WorkerEvent, WorkerStatus } from '@scout/shared';
import { NotificationStore } from '../src/notifications/notification-store.js';
import {
  WORKER_GAVE_UP_DETAIL,
  WORKER_GAVE_UP_TITLE,
  WORKER_RESTARTING_TITLE,
  watchWorkerHealth,
} from '../src/notifications/worker-health.js';
import { createSupervisor } from '../src/worker/supervisor.js';
import { recordingLogger } from './kubernetes-fakes.js';

function makeStore(): NotificationStore {
  return new NotificationStore({
    vault: { get: async () => null, store: async () => undefined },
    push: () => undefined,
    now: () => 1_000,
    setTimer: () => () => undefined,
    logger: recordingLogger().logger,
  });
}

function fakeSupervisor(initial: WorkerStatus) {
  const listeners = new Set<(e: WorkerEvent) => void>();
  return {
    status: initial,
    onEvent(listener: (e: WorkerEvent) => void) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    move(status: WorkerStatus) {
      for (const listener of listeners) listener({ kind: 'state', status });
    },
  };
}

const s = (state: WorkerStatus['state'], restarts: number, gaveUp = false): WorkerStatus =>
  gaveUp ? { state, restarts, gaveUp: true } : { state, restarts };

type Entry = Pick<AppNotification, 'kind' | 'title'> | null;
const restarting: Entry = { kind: 'running', title: WORKER_RESTARTING_TITLE };
const gaveUp: Entry = { kind: 'failed', title: WORKER_GAVE_UP_TITLE };

const ROWS: readonly [string, readonly WorkerStatus[], Entry][] = [
  ['a crash while running: restarting', [s('running', 0), s('starting', 1)], restarting],
  [
    'a crash, then running again: cleared',
    [s('running', 0), s('starting', 1), s('running', 1)],
    null,
  ],
  [
    'crashes until the supervisor gives up: gave up',
    [s('running', 0), s('starting', 1), s('crashed', 1), s('starting', 2), s('crashed', 2, true)],
    gaveUp,
  ],
  [
    'gave up, then the owner starts it and it runs: cleared',
    [s('starting', 1), s('crashed', 1, true), s('starting', 0), s('running', 0)],
    null,
  ],
  [
    'a crash, then the owner stops it: cleared',
    [s('running', 0), s('starting', 1), s('stopped', 1)],
    null,
  ],
  [
    'a restart the owner or main asked for: nothing',
    [s('running', 0), s('stopped', 0), s('starting', 0), s('running', 0)],
    null,
  ],
  [
    'a first start that fails: nothing, its screen reports it',
    [s('starting', 0), s('crashed', 0, true)],
    null,
  ],
  ['a normal start: nothing', [s('starting', 0), s('running', 0)], null],
];

describe('the worker health entry in the notification center', () => {
  it('covers 8 transition sequences', () => {
    expect(ROWS.length).toBe(8);
  });

  for (const [name, statuses, expected] of ROWS) {
    it(name, () => {
      const store = makeStore();
      const supervisor = fakeSupervisor(s('idle', 0));
      watchWorkerHealth(supervisor, store);

      for (const status of statuses) supervisor.move(status);

      const entries = store.list().filter((n) => n.id === 'worker:health');
      expect(entries.map((n) => ({ kind: n.kind, title: n.title }))).toEqual(
        expected === null ? [] : [expected],
      );
    });
  }

  it('the gave-up entry says where to start Scout again, and replaces the restarting one', () => {
    const store = makeStore();
    const supervisor = fakeSupervisor(s('running', 0));
    watchWorkerHealth(supervisor, store);

    supervisor.move(s('starting', 1));
    supervisor.move(s('crashed', 1, true));

    expect(store.list().map((n) => [n.id, n.kind, n.detail])).toEqual([
      ['worker:health', 'failed', WORKER_GAVE_UP_DETAIL],
    ]);
  });

  it('a running worker that exits on its own shows the restarting entry, through the real supervisor', async () => {
    const children: (EventEmitter & { stdout: PassThrough; stderr: PassThrough })[] = [];
    const spawn = () => {
      const child = Object.assign(new EventEmitter(), {
        pid: 40 + children.length,
        stdout: new PassThrough(),
        stderr: new PassThrough(),
        kill: () => undefined,
      });
      children.push(child);
      return child as never;
    };
    const supervisor = createSupervisor({
      spawn,
      logger: recordingLogger().logger,
      resolveBinary: () => '/fake/worker',
      checkBinaryExists: () => true,
      buildEnv: async () => ({}),
      readinessTimeoutMs: 500,
      stopTimeoutMs: 200,
      restartPolicy: { maxFailures: 5, windowMs: 60_000, backoffMs: [10_000] },
    });
    const store = makeStore();
    watchWorkerHealth(supervisor, store);
    const started = supervisor.start();
    await Promise.resolve();
    await Promise.resolve();
    children[0]!.stdout.write('SCOUT_READY\n');
    await started;

    children[0]!.emit('exit', 1, null);

    expect(store.list().map((n) => [n.id, n.kind])).toEqual([['worker:health', 'running']]);
    await supervisor.stop();
  });
});
