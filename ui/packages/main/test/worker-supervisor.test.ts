import { describe, it, expect, jest, mock } from 'bun:test';
import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import { createSupervisor, taskkillTree } from '../src/worker/supervisor.js';
import { createWorkerRestart } from '../src/worker/worker-restart.js';
import type { SupervisorDeps } from '../src/worker/supervisor.js';
import type { WorkerEvent } from '@scout/shared';
import type { Logger } from '../src/util/logger.js';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function makeNullLogger(): Logger {
  return {
    debug() {},
    info() {},
    warn() {},
    error() {},
    child() {
      return makeNullLogger();
    },
  };
}

interface FakeChild extends EventEmitter {
  pid: number;
  stdout: PassThrough;
  stderr: PassThrough;
  kill: (signal?: string) => void;
  killed: boolean;
  lastSignal: string | null;
}

function makeChild(pid = 42): FakeChild {
  const child = new EventEmitter() as FakeChild;
  child.pid = pid;
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.killed = false;
  child.lastSignal = null;
  child.kill = (signal = 'SIGTERM') => {
    child.killed = true;
    child.lastSignal = signal;
  };
  return child;
}

function emitLine(child: FakeChild, stream: 'stdout' | 'stderr', line: string): void {
  child[stream].write(line + '\n');
}

function emitReady(child: FakeChild): void {
  emitLine(child, 'stdout', 'SCOUT_READY');
}

function makeFakeSpawn(child: FakeChild) {
  return mock(() => child as never);
}

function makeDeps(overrides: Partial<SupervisorDeps> = {}): SupervisorDeps & { child: FakeChild } {
  const child = makeChild();
  const spawnFn = makeFakeSpawn(child);

  return {
    child,
    spawn: spawnFn,
    logger: makeNullLogger(),
    resolveBinary: () => '/fake/Scout.Host.Worker',
    checkBinaryExists: () => true,
    buildEnv: async () => ({ SCOUT__CHATADAPTER: 'Discord' }),
    killTree: (_pid, done) => done(),
    ringLimit: 100,
    readinessTimeoutMs: 200,
    stopTimeoutMs: 200,
    restartPolicy: { maxFailures: 5, windowMs: 60_000, backoffMs: [50, 100, 200, 400, 800] },
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('createSupervisor', () => {
  describe('initial state', () => {
    it('starts in idle state', () => {
      const { child, ...deps } = makeDeps();
      void child;
      const supervisor = createSupervisor(deps);
      expect(supervisor.status.state).toBe('idle');
    });

    it('getSnapshot returns empty array initially', () => {
      const { child, ...deps } = makeDeps();
      void child;
      const supervisor = createSupervisor(deps);
      expect(supervisor.getSnapshot()).toHaveLength(0);
    });
  });

  describe('happy path: start → SCOUT_READY → running', () => {
    it('resolves start with pid when SCOUT_READY is received', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);

      const startPromise = supervisor.start();
      await Promise.resolve(); // allow spawn to be called
      await Promise.resolve();
      emitReady(child);

      const result = await startPromise;
      expect(result.pid).toBe(42);
    });

    it('transitions to running state after SCOUT_READY', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      expect(supervisor.status.state).toBe('running');
    });

    it('emits a state=running event', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);
      const events: WorkerEvent[] = [];
      supervisor.onEvent((e) => events.push(e));

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      const stateEvents = events.filter((e) => e.kind === 'state');
      const runningEvent = stateEvents.find(
        (e) => e.kind === 'state' && e.status.state === 'running',
      );
      expect(runningEvent).toBeDefined();
    });

    it('startedAt is set after becoming running', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      expect(supervisor.status.startedAt).toBeDefined();
    });
  });

  describe('readiness timeout', () => {
    it('rejects start if SCOUT_READY never arrives within timeout', async () => {
      const { child, ...deps } = makeDeps({ readinessTimeoutMs: 30 });
      void child;
      const supervisor = createSupervisor(deps);

      await expect(supervisor.start()).rejects.toThrow();
    });

    it('transitions to crashed after readiness timeout', async () => {
      const { child, ...deps } = makeDeps({ readinessTimeoutMs: 30 });
      void child;
      const supervisor = createSupervisor(deps);

      try {
        await supervisor.start();
      } catch {
        // expected
      }

      expect(supervisor.status.state).toBe('crashed');
    });
  });

  describe('graceful stop', () => {
    it('resolves stop with exitCode 0 when child exits cleanly', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      const stopPromise = supervisor.stop();
      child.emit('exit', 0, null);
      const result = await stopPromise;

      expect(result.exitCode).toBe(0);
    });

    it('transitions to stopped state after stop', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      const stopPromise = supervisor.stop();
      child.emit('exit', 0, null);
      await stopPromise;

      expect(supervisor.status.state).toBe('stopped');
    });

    it('sends SIGTERM on stop (non-Windows)', async () => {
      const originalPlatform = process.platform;
      Object.defineProperty(process, 'platform', { value: 'linux', configurable: true });

      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      const stopPromise = supervisor.stop();
      child.emit('exit', 0, null);
      await stopPromise;

      expect(child.lastSignal).toBe('SIGTERM');

      Object.defineProperty(process, 'platform', { value: originalPlatform, configurable: true });
    });
  });

  describe('hard stop (SIGKILL path)', () => {
    it('kills with SIGKILL when child does not exit within stopTimeout', async () => {
      const { child, ...deps } = makeDeps({ stopTimeoutMs: 30 });
      const supervisor = createSupervisor(deps);

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      // Do NOT emit exit — supervisor should SIGKILL after timeout
      const stopPromise = supervisor.stop();

      // Wait long enough for the timeout to fire
      await new Promise<void>((resolve) => setTimeout(resolve, 100));

      // Emit exit so stop resolves
      child.emit('exit', null, 'SIGKILL');
      await stopPromise;

      // Should have attempted SIGKILL (last signal after SIGTERM failed)
      expect(child.killed).toBe(true);
    });
  });

  describe('stop on Windows', () => {
    type KillTree = (pid: number, done: () => void, signals: string[]) => void;

    /** A worker that exits when killed and, when `exitsOnStdinEnd`, when its stdin closes; `signals` records each step. */
    async function runningOnWindows(exitsOnStdinEnd: boolean, killTree: KillTree) {
      const child = Object.assign(makeChild(), { stdin: new PassThrough() });
      const signals: string[] = [];
      const warnings: string[] = [];
      child.kill = (signal = 'SIGTERM') => {
        signals.push(signal);
        setImmediate(() => child.emit('exit', null, signal));
      };
      child.stdin.on('finish', () => {
        signals.push('stdin closed');
        if (exitsOnStdinEnd) setImmediate(() => child.emit('exit', 0, null));
      });
      const { child: _unused, ...deps } = makeDeps({
        spawn: makeFakeSpawn(child) as unknown as SupervisorDeps['spawn'],
        platform: 'win32',
        killTree: (pid, done) => killTree(pid, done, signals),
        stopTimeoutMs: 30,
        logger: { ...makeNullLogger(), warn: (msg: string) => void warnings.push(msg) },
      });
      void _unused;
      const supervisor = createSupervisor(deps);
      const started = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await started;
      const stop = () =>
        Promise.race([
          supervisor.stop(),
          new Promise((resolve) => setTimeout(() => resolve('stop still pending after 1 s'), 1000)),
        ]);
      return { supervisor, signals, warnings, stop };
    }

    it('stop closes the worker stdin, the worker exits on it, and nothing is killed', async () => {
      const { supervisor, signals, stop } = await runningOnWindows(true, (pid, done, steps) => {
        steps.push(`taskkill ${pid}`);
        done();
      });

      const stopped = await stop();

      expect({ stopped, signals, state: supervisor.status.state }).toEqual({
        stopped: { exitCode: 0 },
        signals: ['stdin closed'],
        state: 'stopped',
      });
    });

    it('a worker that ignores its closed stdin is ended after the stop timeout: taskkill /t /f runs for its pid first, and the process itself is killed only once taskkill has answered', async () => {
      let whenTaskkillAnswered: string[] = [];
      const { supervisor, signals, stop } = await runningOnWindows(false, (pid, done, steps) => {
        steps.push(`taskkill ${pid}`);
        setTimeout(() => {
          whenTaskkillAnswered = [...steps];
          done();
        }, 20);
      });

      const stopped = await stop();

      expect({ stopped, whenTaskkillAnswered, signals, state: supervisor.status.state }).toEqual({
        stopped: { exitCode: null },
        whenTaskkillAnswered: ['stdin closed', 'taskkill 42'],
        signals: ['stdin closed', 'taskkill 42', 'SIGKILL'],
        state: 'stopped',
      });
    });

    it('when taskkill cannot run, the worker is still ended and stop resolves', async () => {
      const { supervisor, signals, stop } = await runningOnWindows(false, () => {
        throw new Error('spawn taskkill ENOENT');
      });

      const stopped = await stop();

      expect({ stopped, signals, state: supervisor.status.state }).toEqual({
        stopped: { exitCode: null },
        signals: ['stdin closed', 'SIGKILL'],
        state: 'stopped',
      });
    });

    it('a taskkill that never answers does not leave stop pending: the process itself is killed 5 s after taskkill started, not before', async () => {
      const { supervisor, signals } = await runningOnWindows(false, (pid, _done, steps) => {
        steps.push(`taskkill ${pid}`);
      });
      jest.useFakeTimers();
      try {
        const stopped = supervisor.stop();
        await new Promise((resolve) => process.nextTick(resolve));
        jest.advanceTimersByTime(30);
        jest.advanceTimersByTime(4_999);
        const justBefore = [...signals];
        jest.advanceTimersByTime(1);
        const atTheBound = [...signals];
        jest.useRealTimers();

        const settled = await Promise.race([
          stopped,
          new Promise((resolve) => setTimeout(() => resolve('stop still pending after 1 s'), 1000)),
        ]);

        expect({ justBefore, atTheBound, stopped: settled }).toEqual({
          justBefore: ['stdin closed', 'taskkill 42'],
          atTheBound: ['stdin closed', 'taskkill 42', 'SIGKILL'],
          stopped: { exitCode: null },
        });
      } finally {
        jest.useRealTimers();
      }
    });
    it.each([
      ['answers', (done: () => void) => done()],
      [
        'answers and then throws',
        (done: () => void) => {
          done();
          throw new Error('taskkill threw after answering');
        },
      ],
    ])(
      'a taskkill that %s kills the process exactly once, and the 5 s bound later neither kills it again nor logs a missing answer',
      async (_name, answer) => {
        const { supervisor, signals, warnings } = await runningOnWindows(
          false,
          (_pid, done) => void answer(done),
        );
        jest.useFakeTimers();
        try {
          const stopped = supervisor.stop();
          await new Promise((resolve) => process.nextTick(resolve));
          jest.advanceTimersByTime(30);
          jest.advanceTimersByTime(10_000);
          jest.useRealTimers();
          const settled = await Promise.race([
            stopped,
            new Promise((resolve) =>
              setTimeout(() => resolve('stop still pending after 1 s'), 1000),
            ),
          ]);

          expect({
            stopped: settled,
            kills: signals.filter((signal) => signal === 'SIGKILL').length,
            noAnswerLogged: warnings.includes(
              'supervisor: taskkill gave no answer; killing the worker',
            ),
          }).toEqual({ stopped: { exitCode: null }, kills: 1, noAnswerLogged: false });
        } finally {
          jest.useRealTimers();
        }
      },
    );
  });

  describe('taskkillTree', () => {
    function fakeTaskkill() {
      const calls: { command: string; args: readonly string[] }[] = [];
      const taskkill = new EventEmitter();
      const spawn = ((command: string, args: readonly string[]) => {
        calls.push({ command, args });
        return taskkill;
      }) as unknown as SupervisorDeps['spawn'];
      const warnings: string[] = [];
      const logger = { ...makeNullLogger(), warn: (msg: string) => void warnings.push(msg) };
      let answered = 0;
      const done = () => void (answered += 1);
      return { calls, taskkill, spawn, warnings, logger, done, answered: () => answered };
    }

    it('ends the process and every process it started, forcefully, and answers once it exits', () => {
      const t = fakeTaskkill();

      taskkillTree(t.spawn, t.logger)(42, t.done);
      const answeredBeforeExit = t.answered();
      t.taskkill.emit('exit', 0, null);

      expect({
        calls: t.calls,
        warnings: t.warnings,
        answeredBeforeExit,
        answered: t.answered(),
      }).toEqual({
        calls: [{ command: 'taskkill', args: ['/pid', '42', '/t', '/f'] }],
        warnings: [],
        answeredBeforeExit: 0,
        answered: 1,
      });
    });

    it('a taskkill that cannot be spawned is logged instead of crashing main, and answers', () => {
      const t = fakeTaskkill();

      taskkillTree(t.spawn, t.logger)(42, t.done);
      t.taskkill.emit('error', new Error('spawn taskkill ENOENT'));

      expect({ warnings: t.warnings, answered: t.answered() }).toEqual({
        warnings: ['supervisor: taskkill failed'],
        answered: 1,
      });
    });

    it('a taskkill that exits non-zero is logged, and answers', () => {
      const t = fakeTaskkill();

      taskkillTree(t.spawn, t.logger)(42, t.done);
      t.taskkill.emit('exit', 128, null);

      expect({ warnings: t.warnings, answered: t.answered() }).toEqual({
        warnings: ['supervisor: taskkill failed'],
        answered: 1,
      });
    });
  });

  describe('onEvent / getSnapshot', () => {
    it('onEvent returns an unsubscribe function', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);
      const received: WorkerEvent[] = [];

      const unsub = supervisor.onEvent((e) => received.push(e));

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      const countBefore = received.length;
      unsub();

      // Emit more events after unsubscribe — should not appear
      emitLine(child, 'stdout', 'after-unsub');
      await new Promise<void>((resolve) => setTimeout(resolve, 20));

      expect(received.length).toBe(countBefore);
    });

    it('getSnapshot returns a copy of the ring buffer', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitLine(child, 'stdout', 'line-before-ready');
      emitReady(child);
      await startPromise;

      const snapshot = supervisor.getSnapshot();
      expect(Array.isArray(snapshot)).toBe(true);
      expect(snapshot.some((e) => e.kind === 'stdout')).toBe(true);
    });

    it('keeps each worker line once in the snapshot', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);
      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      emitLine(child, 'stdout', 'x');
      await new Promise<void>((resolve) => setTimeout(resolve, 20));

      expect(supervisor.getSnapshot().filter((e) => 'line' in e && e.line === 'x').length).toBe(1);
    });

    it('keeps only the most recent lines once the ring passes its limit', async () => {
      const { child, ...deps } = makeDeps({ ringLimit: 3 });
      const supervisor = createSupervisor(deps);
      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      for (const line of ['a', 'b', 'c', 'd', 'e']) emitLine(child, 'stderr', line);
      await new Promise<void>((resolve) => setTimeout(resolve, 20));

      expect(supervisor.getSnapshot().map((e) => ('line' in e ? e.line : e.kind))).toEqual([
        'c',
        'd',
        'e',
      ]);
    });
  });

  describe('env var security', () => {
    it('does not emit the bot token in any log event', async () => {
      const SECRET = 'super-secret-bot-token-12345';
      const { child, ...deps } = makeDeps({
        buildEnv: async () => ({
          SCOUT__CHATADAPTER: 'Discord',
          SCOUT__ADAPTERS__DISCORD__BOTTOKEN: SECRET,
        }),
      });
      const supervisor = createSupervisor(deps);
      const events: WorkerEvent[] = [];
      supervisor.onEvent((e) => events.push(e));

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      const allText = JSON.stringify(events);
      expect(allText).not.toContain(SECRET);
    });

    it('does not appear in the ring buffer', async () => {
      const SECRET = 'do-not-log-this-token';
      const { child, ...deps } = makeDeps({
        buildEnv: async () => ({
          SCOUT__ADAPTERS__DISCORD__BOTTOKEN: SECRET,
        }),
      });
      const supervisor = createSupervisor(deps);

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      const snapshot = JSON.stringify(supervisor.getSnapshot());
      expect(snapshot).not.toContain(SECRET);
    });

    it('never passes an inherited worker gate, write grant, control-plane token or operator media tool variable to the worker, in any spelling, and passes the values buildEnv sets', async () => {
      const inherited: Record<string, string> = {
        scout__workergate__token: 'inherited-token',
        'Scout:WorkerGate:Token': 'inherited-token',
        DOTNET_Scout__WorkerGate__Token: 'inherited-token',
        aspnetcore_scout__workergate__socketpath: '/tmp/other.sock',
        'Scout:WorkerGate:SocketPath': '/tmp/other.sock',
        scout__features__grantedwrites: 'youtube',
        'DOTNET_Scout:Features:GrantedWrites': 'youtube',
        scout_controlplane_token: 'desktop-token',
        Scout_ControlPlane_Worker_Token: 'worker-token',
        DOTNET_SCOUT_CONTROLPLANE_TOKEN: 'desktop-token',
        scout__operator__ffmpegpath: '/tmp/other/ffmpeg',
        'Scout:Operator:FontPath': '/tmp/other/font.ttf',
        DOTNET_Scout__Operator__FontPath: '/tmp/other/font.ttf',
        'ASPNETCORE_SCOUT:OPERATOR:FFMPEGPATH': '/tmp/other/ffmpeg',
        scout__operator__piperpath: '/tmp/other/piper',
        'Scout:Operator:VoicesPath': '/tmp/other/voices',
        DOTNET_Scout__Operator__PiperPath: '/tmp/other/piper',
        'ASPNETCORE_SCOUT:OPERATOR:VOICESPATH': '/tmp/other/voices',
        scout__operator__rhubarbpath: '/tmp/other/rhubarb',
        'Scout:Operator:MouthsPath': '/tmp/other/mouths',
        DOTNET_Scout__Operator__RhubarbPath: '/tmp/other/rhubarb',
        'ASPNETCORE_SCOUT:OPERATOR:MOUTHSPATH': '/tmp/other/mouths',
        scout__operator__presenterspath: '/tmp/other/presenters',
        'Scout:Operator:FaceModelsPath': '/tmp/other/face',
        DOTNET_Scout__Operator__PresentersPath: '/tmp/other/presenters',
        'ASPNETCORE_SCOUT:OPERATOR:FACEMODELSPATH': '/tmp/other/face',
        scout__tools__tesseractpath: '/tmp/other/tesseract',
        'Scout:Tools:TessdataPath': '/tmp/other/tessdata',
        DOTNET_SCOUT__TOOLS__LOCALE: 'xx',
      };
      const unrelatedKey = 'SCOUT__TEST__SUPERVISOR_INHERITED';
      const keys = [...Object.keys(inherited), unrelatedKey];
      const saved = new Map(keys.map((key) => [key, process.env[key]]));
      Object.assign(process.env, inherited, { [unrelatedKey]: 'kept' });
      try {
        const fromBuildEnv = {
          SCOUT__WORKERGATE__SOCKETPATH: '/run/scout/cp.sock',
          SCOUT__WORKERGATE__TOKEN: 'gate-token',
          SCOUT__FEATURES__GRANTEDWRITES: '',
          SCOUT__OPERATOR__FFMPEGPATH: '/opt/Scout/resources/media-tools/linux-x64/ffmpeg',
          SCOUT__OPERATOR__FONTPATH: '',
          SCOUT__OPERATOR__PIPERPATH: '/opt/Scout/resources/media-tools/linux-x64/piper/piper',
          SCOUT__OPERATOR__VOICESPATH: '',
          SCOUT__OPERATOR__RHUBARBPATH:
            '/opt/Scout/resources/media-tools/linux-x64/rhubarb/rhubarb',
          SCOUT__OPERATOR__MOUTHSPATH: '',
          SCOUT__OPERATOR__PRESENTERSPATH: '/opt/Scout/resources/presenters',
          SCOUT__OPERATOR__FACEMODELSPATH: '',
        };
        const { child, ...deps } = makeDeps({ buildEnv: async () => ({ ...fromBuildEnv }) });
        const supervisor = createSupervisor(deps);
        const startPromise = supervisor.start();
        await Promise.resolve();
        await Promise.resolve();
        emitReady(child);
        await startPromise;

        const spawnCalls = (deps.spawn as unknown as { mock: { calls: unknown[][] } }).mock.calls;
        const { env } = spawnCalls[0]?.[2] as { env: Record<string, string | undefined> };
        expect(Object.keys(env).filter((key) => key in inherited)).toEqual([]);
        expect(env).toMatchObject({ ...fromBuildEnv, [unrelatedKey]: 'kept' });
      } finally {
        for (const [key, value] of saved) {
          if (value === undefined) delete process.env[key];
          else process.env[key] = value;
        }
      }
    });
  });

  describe('state transition order', () => {
    it('emits starting → running in order', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);
      const states: string[] = [];
      supervisor.onEvent((e) => {
        if (e.kind === 'state') states.push(e.status.state);
      });

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      expect(states[0]).toBe('starting');
      expect(states[states.length - 1]).toBe('running');
    });
  });

  describe('WorkerBinaryMissingError', () => {
    it('rejects start when the binary does not exist', async () => {
      const { child, ...deps } = makeDeps({
        resolveBinary: () => '/nonexistent/path/Scout.Host.Console',
        checkBinaryExists: () => false,
      });
      void child;
      const supervisor = createSupervisor(deps);

      await expect(supervisor.start()).rejects.toMatchObject({
        code: 'worker/binary-missing',
      });
    });

    it.each([
      ['the binary is missing', { checkBinaryExists: () => false }],
      [
        'building the environment fails',
        { buildEnv: () => Promise.reject(new Error('keyring locked')) },
      ],
    ] as const)(
      'a start that fails before the worker is spawned because %s ends crashed, and the next Start is refused instead of waiting',
      async (_label, overrides) => {
        const { child, ...deps } = makeDeps(overrides);
        void child;
        const supervisor = createSupervisor(deps);
        const first = supervisor.start().catch((err: unknown) => err);
        const pressed = supervisor.start().catch((err: unknown) => err);

        await first;
        const outcome = await Promise.race([
          pressed,
          new Promise((resolve) => setTimeout(() => resolve('still waiting'), 100)),
        ]);

        expect(supervisor.status.state).toBe('crashed');
        expect(outcome).toBeInstanceOf(Error);
      },
    );
  });

  describe('exit before SCOUT_READY', () => {
    it('rejects start and transitions to crashed when child exits before emitting SCOUT_READY', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();

      // Exit without ever emitting SCOUT_READY
      child.emit('exit', 1, null);

      let thrown: unknown = null;
      try {
        await startPromise;
      } catch (err) {
        thrown = err;
      }

      expect(thrown).not.toBeNull();
      expect(supervisor.status.state).toBe('crashed');
    });
  });

  describe('Start while the worker is starting', () => {
    it('B4 Start pressed while the worker is starting answers once the worker is running, with its pid', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);
      const launch = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();

      let stateWhenAnswered = null as string | null;
      const pressed = supervisor.start().then((result) => {
        stateWhenAnswered = supervisor.status.state;
        return result;
      });
      await new Promise<void>((resolve) => setTimeout(resolve, 10));
      emitReady(child);
      await launch;
      const result = await pressed;

      expect(stateWhenAnswered).toBe('running');
      expect(result).toEqual({ pid: 42 });
    });

    it('B4 Start pressed while the worker is starting is refused once that start fails', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);
      const launch = supervisor.start().catch((err: unknown) => err);
      await Promise.resolve();
      await Promise.resolve();
      const pressed = supervisor.start().catch((err: unknown) => err);
      await new Promise<void>((resolve) => setTimeout(resolve, 10));

      child.emit('exit', 1, null);
      await launch;

      expect(await pressed).toBeInstanceOf(Error);
      expect(((await pressed) as Error).message).toBe(
        'worker/start-failed: the worker crashed before it was running',
      );
    });
  });
});

describe('createSupervisor: quick-ask stdin', () => {
  async function running(stdin: Writable, overrides: Partial<SupervisorDeps> = {}) {
    const { child, ...deps } = makeDeps(overrides);
    (child as unknown as { stdin: Writable }).stdin = stdin;
    const supervisor = createSupervisor(deps);
    const started = supervisor.start();
    await Promise.resolve();
    await Promise.resolve();
    emitReady(child);
    await started;
    return { supervisor, child };
  }

  it('writes one line to the running worker stdin', async () => {
    const written: string[] = [];
    const stdin = new Writable({
      write(chunk: Buffer, _enc, cb) {
        written.push(chunk.toString());
        cb();
      },
    });
    const { supervisor } = await running(stdin);

    const accepted = supervisor.writeStdin('{"type":"ask","id":"q-1","text":"hi"}', () => {});

    expect(accepted).toBe(true);
    expect(written).toEqual(['{"type":"ask","id":"q-1","text":"hi"}\n']);
  });

  it('refuses a write when no worker is running', () => {
    const { child, ...deps } = makeDeps();
    void child;
    const supervisor = createSupervisor(deps);

    expect(supervisor.writeStdin('{"type":"ask"}', () => {})).toBe(false);
  });

  it('reports a stdin write that fails with EPIPE to the caller and logs it instead of crashing', async () => {
    const warnings: string[] = [];
    const logger = { ...makeNullLogger(), warn: (msg: string) => warnings.push(msg) };
    const stdin = new Writable({
      write(_chunk, _enc, cb) {
        cb(Object.assign(new Error('write EPIPE'), { code: 'EPIPE' }));
      },
    });
    const { supervisor } = await running(stdin, { logger });
    const failures: string[] = [];

    supervisor.writeStdin('{"type":"ask","id":"q-1","text":"hi"}', (err) =>
      failures.push(err.message),
    );
    await new Promise<void>((resolve) => setTimeout(resolve, 20));

    expect(failures).toEqual(['write EPIPE']);
    expect(warnings).toContain('supervisor: worker stdin closed');
  });

  it('hands SCOUT_LOCAL lines to onLocalEvent and keeps them out of the snapshot', async () => {
    const local: string[] = [];
    const stdin = new Writable({ write: (_c, _e, cb) => cb() });
    const { supervisor, child } = await running(stdin, {
      onLocalEvent: (json) => local.push(json),
    });

    emitLine(child, 'stdout', 'SCOUT_LOCAL {"id":"q-1","event":"begin"}');
    await new Promise<void>((resolve) => setTimeout(resolve, 80));

    expect(local).toEqual(['{"id":"q-1","event":"begin"}']);
    expect(supervisor.getSnapshot().some((e) => 'line' in e && e.line.includes('q-1'))).toBe(false);
  });
});

describe('a Whisper model change while the worker starts', () => {
  it('a Whisper model change while the worker is starting spawns one worker, not two', async () => {
    let releaseEnv!: () => void;
    const envReady = new Promise<void>((resolve) => (releaseEnv = resolve));
    const { child, ...deps } = makeDeps({
      buildEnv: async () => {
        await envReady;
        return { SCOUT__CHATADAPTER: 'Discord' };
      },
    });
    const supervisor = createSupervisor(deps);
    const restart = createWorkerRestart({
      supervisor,
      currentModel: async () => '/models/ggml-base.bin',
      loadedModel: () => '/models/ggml-tiny.bin',
      listening: () => false,
      store: { record: () => undefined },
      onRestarted: () => undefined,
      logger: makeNullLogger(),
    });

    void supervisor.start().catch(() => undefined);
    const changed = restart.modelChanged();
    await new Promise<void>((resolve) => setTimeout(resolve, 10));
    releaseEnv();
    await new Promise<void>((resolve) => setTimeout(resolve, 10));
    emitReady(child);
    await changed;

    expect(deps.spawn).toHaveBeenCalledTimes(1);
  });
});
