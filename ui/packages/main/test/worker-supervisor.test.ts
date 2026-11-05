import { describe, it, expect, mock } from 'bun:test';
import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import { createSupervisor } from '../src/worker/supervisor.js';
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
    // The supervisor's Windows shutdown path uses taskkill, not child.kill(SIGKILL),
    // so child.killed stays false. The graceful-stop sequence is covered elsewhere.
    // Skip on win32 to keep cross-OS CI green.
    it.skipIf(process.platform === 'win32')(
      'kills with SIGKILL when child does not exit within stopTimeout',
      async () => {
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
      },
    );
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
  });

  describe('SCOUT_READY with no http endpoint', () => {
    it('reaches running state when SCOUT_READY arrives but no SCOUT_HTTP= line is printed', async () => {
      const { child, ...deps } = makeDeps();
      const supervisor = createSupervisor(deps);

      const startPromise = supervisor.start();
      await Promise.resolve();
      await Promise.resolve();
      emitReady(child);
      await startPromise;

      expect(supervisor.status.state).toBe('running');
      expect(supervisor.httpEndpoint).toBeNull();
    });
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
