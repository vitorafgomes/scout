import { afterEach, describe, expect, it } from 'bun:test';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { CHANNEL, readIpcError } from '@scout/shared';
import { createSupervisor, type SupervisorDeps } from '../src/worker/supervisor.js';
import {
  WORKER_RESTARTED_TITLE,
  WORKER_RESTART_DROPPED_TITLE,
  WORKER_RESTART_ENTRY_ID,
  WORKER_RESTART_FAILED_TITLE,
  WORKER_RESTART_WAITS_TITLE,
  createWorkerRestart,
} from '../src/worker/worker-restart.js';
import { registerWorkerRestartHandler } from '../src/ipc/worker.js';
import { restartWorkerIfRunning } from '../src/ipc/github.js';
import { NotificationStore } from '../src/notifications/notification-store.js';
import { recordingLogger } from './kubernetes-fakes.js';
import { type IpcHandler, sender, setup, tearDown, turn } from './quick-ask-fakes.js';

afterEach(tearDown);

describe('main restart path: a restart waits for the running answer and the live listen', () => {
  interface FakeChild extends EventEmitter {
    pid: number;
    stdin: PassThrough;
    stdout: PassThrough;
    stderr: PassThrough;
    kill(): boolean;
  }

  /** A real supervisor over fake worker processes; each child records the lines main writes to it. */
  function fakeWorker(
    options: {
      ready?: 'at once' | 'never for the first' | 'by hand';
      exit?: 'at once' | 'by hand';
      buildFailsFrom?: number;
      buildHeldFrom?: number;
      gaveUpAfterOneFailure?: boolean;
    } = {},
  ) {
    const children: FakeChild[] = [];
    const heard: string[][] = [];
    let builds = 0;
    let releaseBuild: () => void = () => undefined;
    const buildHeld = new Promise<void>((resolve) => (releaseBuild = resolve));
    const spawn = (() => {
      const index = children.length;
      const child = Object.assign(new EventEmitter(), {
        pid: 50 + index,
        stdin: new PassThrough(),
        stdout: new PassThrough(),
        stderr: new PassThrough(),
        kill: () => {
          if (options.exit !== 'by hand') setImmediate(() => child.emit('exit', 0, null));
          return true;
        },
      }) as FakeChild;
      heard.push([]);
      child.stdin.on('finish', () => {
        if (options.exit !== 'by hand') setImmediate(() => child.emit('exit', 0, null));
      });
      child.stdin.on('data', (chunk: Buffer) => {
        heard[index]!.push(
          ...chunk
            .toString()
            .split('\n')
            .filter((l) => l.length > 0),
        );
      });
      children.push(child);
      const ready = options.ready ?? 'at once';
      if (ready === 'at once' || (ready === 'never for the first' && index > 0)) {
        setImmediate(() => child.stdout.write('SCOUT_READY\n'));
      }
      return child;
    }) as unknown as SupervisorDeps['spawn'];
    const supervisor = createSupervisor({
      spawn,
      logger: recordingLogger().logger,
      resolveBinary: () => '/fake/Scout.Host.Console',
      checkBinaryExists: () => true,
      killTree: (_pid, done) => done(),
      buildEnv: async () => {
        builds += 1;
        if (options.buildHeldFrom !== undefined && builds >= options.buildHeldFrom) {
          await buildHeld;
        }
        if (options.buildFailsFrom !== undefined && builds >= options.buildFailsFrom) {
          throw new Error('keyring locked');
        }
        return {};
      },
      readinessTimeoutMs: options.ready === 'never for the first' ? 40 : 2000,
      stopTimeoutMs: 200,
      restartPolicy: {
        maxFailures: options.gaveUpAfterOneFailure === true ? 0 : 5,
        windowMs: 60_000,
        backoffMs: [10],
      },
    });
    return { supervisor, children, heard, releaseBuild, builds: () => builds };
  }

  async function until(condition: () => boolean): Promise<void> {
    for (let i = 0; i < 400 && !condition(); i += 1) {
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
  }

  /** Long enough for a fake worker to stop and start again, for a wait where nothing should happen. */
  async function settled(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 50));
  }

  /** Quick ask, the restart path and the restart channel wired to each other as main.ts wires them. */
  async function wired(worker: ReturnType<typeof fakeWorker>) {
    let quickAsk: {
      isListening(): boolean;
      isAnswering(): boolean;
      restartSettled(): void;
    } | null = null;
    const { logger } = recordingLogger();
    const store = new NotificationStore({
      vault: { get: async () => null, store: async () => undefined },
      push: () => undefined,
      now: () => 1_000_000,
      setTimer: () => () => undefined,
      logger,
    });
    const restart = createWorkerRestart({
      supervisor: worker.supervisor,
      currentModel: async () => null,
      loadedModel: () => null,
      listening: () => quickAsk?.isListening() ?? false,
      answering: () => quickAsk?.isAnswering() ?? false,
      store,
      onRestarted: () => undefined,
      onRestartSettled: () => quickAsk?.restartSettled(),
      logger: recordingLogger().logger,
    });
    const t = await setup({
      worker: worker.supervisor,
      onListeningChanged: (listening) => restart.listeningChanged(listening),
      onAnsweringChanged: (answering) => restart.answeringChanged(answering),
      restarting: () => restart.restarting(),
    });
    quickAsk = t.quickAsk;
    registerWorkerRestartHandler(
      {
        handle: (channel: string, handler: IpcHandler) => void t.handlers.set(channel, handler),
        removeHandler: (channel: string) => void t.handlers.delete(channel),
      },
      restart,
      recordingLogger().logger,
    );
    return {
      ...t,
      restart: () => t.invoke(CHANNEL.workerRestart, sender(), { reason: 'AI Source' }),
      mainRestart: () => restartWorkerIfRunning(restart, logger),
      bell: () =>
        store
          .list()
          .filter((entry) => entry.id === WORKER_RESTART_ENTRY_ID)
          .map(({ kind, title, detail }): { kind: string; title: string; detail: string } => ({
            kind,
            title,
            detail,
          })),
      line: (json: object) => t.quickAsk.handleWorkerLine(JSON.stringify(json)),
    };
  }

  const ROWS = [
    { busy: 'an answer running', answer: true, listen: false, stopped: false, fails: false },
    { busy: 'a live listen open', answer: false, listen: true, stopped: false, fails: false },
    {
      busy: 'an answer running and a live listen open',
      answer: true,
      listen: true,
      stopped: false,
      fails: false,
    },
    { busy: 'nothing running', answer: false, listen: false, stopped: false, fails: false },
    { busy: 'the worker stopped', answer: false, listen: false, stopped: true, fails: false },
    {
      busy: 'nothing running, and the restarted worker cannot start',
      answer: false,
      listen: false,
      stopped: false,
      fails: true,
    },
  ] as const;

  for (const row of ROWS) {
    const deferred = row.answer || row.listen;
    const expected = row.stopped
      ? { outcome: 'none' }
      : row.fails
        ? { outcome: 'failed', reason: 'keyring locked' }
        : deferred
          ? { outcome: 'deferred' }
          : { outcome: 'now' };
    it(`a restart asked with ${row.busy} answers ${expected.outcome}, and the worker restarts ${deferred ? 'once both have ended' : expected.outcome === 'now' ? 'at once' : 'never'}`, async () => {
      const worker = fakeWorker(row.fails ? { buildFailsFrom: 2 } : {});
      const t = await wired(worker);
      await worker.supervisor.start();
      if (row.stopped) await worker.supervisor.stop();
      if (row.answer)
        await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'is prod green?' });
      const listen = row.listen
        ? ((await t.invoke(CHANNEL.localListen, sender())) as { id: string }).id
        : null;

      const restartsOnce = !row.stopped && !row.fails;
      const answer = await t.restart();
      const spawnedWhileBusy = worker.children.length;
      if (row.answer) t.line({ id: 'q-1', event: 'complete', text: 'yes' });
      if (restartsOnce && !row.listen) await until(() => worker.children.length > 1);
      else await settled();
      const spawnedAfterTheAnswer = worker.children.length;
      if (listen !== null) t.line({ id: listen, event: 'complete' });
      if (restartsOnce) {
        await until(
          () => worker.supervisor.status.state === 'running' && worker.children.length > 1,
        );
      } else {
        await settled();
      }

      expect({
        answer,
        spawnedWhileBusy,
        spawnedAfterTheAnswer,
        spawned: worker.children.length,
        state: worker.supervisor.status.state,
      }).toEqual({
        answer: expected,
        spawnedWhileBusy: deferred || row.stopped || row.fails ? 1 : 2,
        spawnedAfterTheAnswer: row.listen || row.stopped || row.fails ? 1 : 2,
        spawned: row.stopped || row.fails ? 1 : 2,
        state: row.stopped ? 'stopped' : row.fails ? 'crashed' : 'running',
      });
    });
  }

  const BELL = [
    { asks: 'main itself', then: 'the restart runs', fails: false, stops: false },
    { asks: 'main itself', then: 'Scout was stopped first', fails: false, stops: true },
    { asks: 'main itself', then: 'the restarted worker cannot start', fails: true, stops: false },
    { asks: 'a card', then: 'the restart runs', fails: false, stops: false },
    { asks: 'a card', then: 'the restarted worker cannot start', fails: true, stops: false },
  ] as const;

  for (const row of BELL) {
    const announced = row.asks === 'main itself';
    const waiting = announced
      ? [{ kind: 'running', title: WORKER_RESTART_WAITS_TITLE, detail: '' }]
      : [];
    const afterwards = row.fails
      ? [{ kind: 'failed', title: WORKER_RESTART_FAILED_TITLE, detail: 'keyring locked' }]
      : !announced
        ? []
        : row.stops
          ? [{ kind: 'done', title: WORKER_RESTART_DROPPED_TITLE, detail: '' }]
          : [{ kind: 'done', title: WORKER_RESTARTED_TITLE, detail: '' }];
    it(`a deferred restart ${row.asks} asked for, when ${row.then}: the bell shows ${waiting.length === 0 ? 'nothing' : 'that Scout restarts once the answer or listen ends'} while it waits, then ${afterwards[0]?.title ?? 'nothing'}`, async () => {
      const worker = fakeWorker(row.fails ? { buildFailsFrom: 2 } : {});
      const t = await wired(worker);
      await worker.supervisor.start();
      await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'is prod green?' });

      if (announced) await t.mainRestart();
      else await t.restart();
      const whileWaiting = t.bell();
      if (row.stops) await worker.supervisor.stop();
      t.line({ id: 'q-1', event: 'complete', text: 'yes' });
      await until(() =>
        row.stops || row.fails
          ? t.bell().some((entry) => entry.kind !== 'running')
          : worker.children.length > 1 && worker.supervisor.status.state === 'running',
      );
      await settled();

      expect({ whileWaiting, settled: t.bell() }).toEqual({
        whileWaiting: waiting,
        settled: afterwards,
      });
    });
  }

  it("a question asked between a restart's stop and its start is held and sent once the restarted worker runs", async () => {
    const worker = fakeWorker({ exit: 'by hand' });
    const t = await wired(worker);
    await worker.supervisor.start();

    const restarting = t.restart();
    await until(() => worker.supervisor.status.state === 'stopped');
    const asked = await Promise.resolve(
      t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'is prod green?' }),
    ).then(
      () => 'held',
      (err: unknown) => readIpcError(err).message,
    );
    worker.children[0]!.emit('exit', 0, null);
    const answer = await restarting;
    await until(() => (worker.heard[1] ?? []).length > 0);

    expect({
      asked,
      answer,
      sentToTheRestartedWorker: worker.heard[1],
      turns: await t.invoke(CHANNEL.localGetTurns, sender()),
    }).toEqual({
      asked: 'held',
      answer: { outcome: 'now' },
      sentToTheRestartedWorker: ['{"type":"ask","id":"q-1","text":"is prod green?"}'],
      turns: [turn('q-1', 'is prod green?')],
    });
    const stopping = worker.supervisor.stop();
    worker.children[1]!.emit('exit', 0, null);
    await stopping;
  });

  it('a question held while Scout starts keeps a restart waiting, is sent once the worker runs, and the restart follows its answer', async () => {
    const worker = fakeWorker({ ready: 'by hand' });
    const t = await wired(worker);
    const starting = worker.supervisor.start().catch(() => undefined);
    await until(() => worker.children.length === 1);
    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'is prod green?' });

    const answer = await Promise.race([
      t.restart(),
      new Promise((resolve) => setTimeout(() => resolve('still restarting'), 200)),
    ]);
    worker.children[0]!.stdout.write('SCOUT_READY\n');
    await starting;
    await until(() => (worker.heard[0] ?? []).length > 0);
    const spawnedBeforeTheAnswer = worker.children.length;
    t.line({ id: 'q-1', event: 'complete', text: 'yes' });
    await until(() => worker.children.length > 1);
    worker.children[1]?.stdout.write('SCOUT_READY\n');
    await until(() => worker.supervisor.status.state === 'running');

    expect({
      answer,
      sent: worker.heard[0],
      spawnedBeforeTheAnswer,
      spawned: worker.children.length,
    }).toEqual({
      answer: { outcome: 'deferred' },
      sent: ['{"type":"ask","id":"q-1","text":"is prod green?"}'],
      spawnedBeforeTheAnswer: 1,
      spawned: 2,
    });
  });

  it("a question held when a Stop lands in a restart's start half gets the not-running reply, is never sent, and keeps no later restart waiting", async () => {
    const worker = fakeWorker({ buildHeldFrom: 2 });
    const t = await wired(worker);
    await worker.supervisor.start();

    const restarting = t.restart();
    await until(() => worker.builds() === 2);
    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'is prod green?' });
    await worker.supervisor.stop();
    worker.releaseBuild();
    const answer = await restarting;
    const afterTheRestart = await t.invoke(CHANNEL.localGetTurns, sender());
    await worker.supervisor.start();
    const later = await t.restart();
    await until(() => worker.supervisor.status.state === 'running');

    expect({
      answer: (answer as { outcome: string }).outcome,
      afterTheRestart,
      sent: worker.heard.flat().filter((line) => line.includes('"type":"ask"')),
      later,
    }).toEqual({
      answer: 'failed',
      afterTheRestart: [
        turn('q-1', 'is prod green?', {
          reply: 'Scout is not running. Start it from the tray or the app.',
          state: 'failed',
          notRunning: true,
        }),
      ],
      sent: [],
      later: { outcome: 'now' },
    });
  });

  for (const gaveUp of [false, true]) {
    it(`a question held through a readiness timeout is ${gaveUp ? 'refused once the worker gives up' : 'sent once the automatic restart runs'}`, async () => {
      const worker = fakeWorker({
        ready: 'never for the first',
        ...(gaveUp ? { gaveUpAfterOneFailure: true } : {}),
      });
      const t = await wired(worker);
      void worker.supervisor.start().catch(() => undefined);
      await until(() => worker.children.length === 1);
      await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'is prod green?' });

      await until(() =>
        gaveUp ? worker.supervisor.status.gaveUp === true : (worker.heard[1] ?? []).length > 0,
      );

      expect({
        sentToTheRestartedWorker: worker.heard[1] ?? [],
        turns: await t.invoke(CHANNEL.localGetTurns, sender()),
      }).toEqual(
        gaveUp
          ? {
              sentToTheRestartedWorker: [],
              turns: [
                turn('q-1', 'is prod green?', {
                  reply: 'Scout is not running. Start it from the tray or the app.',
                  state: 'failed',
                  notRunning: true,
                }),
              ],
            }
          : {
              sentToTheRestartedWorker: ['{"type":"ask","id":"q-1","text":"is prod green?"}'],
              turns: [turn('q-1', 'is prod green?')],
            },
      );
    });
  }

  for (const row of [
    {
      second: 'the restarted worker starts',
      fails: false,
      answers: [{ outcome: 'now' }, { outcome: 'now' }],
      spawned: 3,
      state: 'running',
    },
    {
      second: 'the restarted worker cannot start',
      fails: true,
      answers: [{ outcome: 'failed', reason: 'keyring locked' }, { outcome: 'none' }],
      spawned: 1,
      state: 'crashed',
    },
  ] as const) {
    it(`two restarts asked one after the other, when ${row.second}: they answer ${row.answers.map((answer) => answer.outcome).join(' then ')}, and the worker ends ${row.state}`, async () => {
      const worker = fakeWorker(row.fails ? { buildFailsFrom: 2 } : {});
      const t = await wired(worker);
      await worker.supervisor.start();

      const answers = await Promise.all([t.restart(), t.restart()]);
      await settled();

      expect({
        answers,
        spawned: worker.children.length,
        state: worker.supervisor.status.state,
      }).toEqual({ answers: [...row.answers], spawned: row.spawned, state: row.state });
    });
  }
});
