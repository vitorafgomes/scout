import { describe, it, expect, mock } from 'bun:test';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
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
    it.skipIf(process.platform === 'win32')('kills with SIGKILL when child does not exit within stopTimeout', async () => {
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
        resolveBinary: () => '/nonexistent/path/Scout.Host.Worker',
        checkBinaryExists: () => false,
      });
      void child;
      const supervisor = createSupervisor(deps);

      await expect(supervisor.start()).rejects.toMatchObject({
        code: 'worker/binary-missing',
      });
    });
  });
});
