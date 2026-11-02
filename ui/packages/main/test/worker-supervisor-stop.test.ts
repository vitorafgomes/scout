import { afterEach, describe, expect, it, jest, mock } from 'bun:test';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import type { WorkerStatus } from '@scout/shared';
import { createSupervisor, type SupervisorDeps } from '../src/worker/supervisor.js';
import { installAppLifecycle, type AppLifecycleDeps } from '../src/shell/app-lifecycle.js';
import { createPresenceNotifier } from '../src/shell/notifications.js';
import { recordingLogger } from './kubernetes-fakes.js';

interface FakeChild extends EventEmitter {
  pid: number;
  stdout: PassThrough;
  stderr: PassThrough;
  kill: (signal?: string) => void;
}

function makeChild(pid: number): FakeChild {
  const child = new EventEmitter() as FakeChild;
  child.pid = pid;
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.kill = () => undefined;
  return child;
}

function setup(overrides: Partial<SupervisorDeps> = {}) {
  const children: FakeChild[] = [];
  const spawn = mock(() => {
    const child = makeChild(100 + children.length);
    children.push(child);
    return child as never;
  });
  const envBuilds = { count: 0 };
  let envImpl: () => Promise<Record<string, string>> = async () => ({});
  const states: WorkerStatus[] = [];
  const shown: string[] = [];
  const notifier = createPresenceNotifier({
    platform: 'win32',
    isSupported: () => true,
    show: (_title, body) => shown.push(body),
    vault: { get: async () => null, store: async () => undefined },
    logger: recordingLogger().logger,
  });
  const supervisor = createSupervisor({
    spawn: spawn as unknown as SupervisorDeps['spawn'],
    logger: recordingLogger().logger,
    resolveBinary: () => '/fake/Scout.Host.Console',
    checkBinaryExists: () => true,
    buildEnv: () => {
      envBuilds.count += 1;
      return envImpl();
    },
    readinessTimeoutMs: 30,
    stopTimeoutMs: 200,
    restartPolicy: { maxFailures: 5, windowMs: 60_000, backoffMs: [5] },
    ...overrides,
  });
  supervisor.onEvent((event) => {
    if (event.kind !== 'state') return;
    states.push(event.status);
    notifier.workerStatus(event.status);
  });
  return {
    supervisor,
    children,
    spawn,
    states,
    shown,
    envBuilds,
    setBuildEnv(next: () => Promise<Record<string, string>>) {
      envImpl = next;
    },
  };
}

async function waitFor(predicate: () => boolean, ms = 1000): Promise<void> {
  const deadline = Date.now() + ms;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error('condition not reached');
    await new Promise((r) => setTimeout(r, 2));
  }
}

async function flush(): Promise<void> {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

async function startRunning(s: ReturnType<typeof setup>): Promise<void> {
  const started = s.supervisor.start();
  await waitFor(() => s.children.length === 1);
  s.children[0]!.stdout.write('SCOUT_READY\n');
  await started;
}

/** Resolves to 'pending' when the promise has not settled after the microtask queue drains. */
function settlesPromptly<T>(promise: Promise<T>): Promise<T | 'pending'> {
  return Promise.race([promise, flush().then(() => 'pending' as const)]);
}

afterEach(() => {
  jest.useRealTimers();
});

describe('crash notification only when the supervisor gives up', () => {
  it('a readiness timeout the supervisor restarts from (crashed, starting, running) emits nothing', async () => {
    const s = setup();
    await startRunning(s);

    s.children[0]!.emit('exit', 1, null);
    await waitFor(() => s.children.length === 2);
    await waitFor(() => s.supervisor.status.state === 'crashed');
    s.children[1]!.emit('exit', null, 'SIGKILL');
    await waitFor(() => s.children.length === 3);
    s.children[2]!.stdout.write('SCOUT_READY\n');
    await waitFor(() => s.supervisor.status.state === 'running');

    expect(s.states.find((st) => st.state === 'crashed')).toEqual({
      state: 'crashed',
      restarts: 1,
      startedAt: expect.any(String),
    });
    expect(s.shown).toEqual([]);
  });

  it('an exhausted restart policy reports gaveUp and notifies once', async () => {
    const s = setup({ restartPolicy: { maxFailures: 1, windowMs: 60_000, backoffMs: [5] } });
    await startRunning(s);

    s.children[0]!.emit('exit', 1, null);
    await waitFor(() => s.children.length === 2);
    await waitFor(() => s.supervisor.status.state === 'crashed');
    s.children[1]!.emit('exit', null, 'SIGKILL');
    await waitFor(() => s.supervisor.status.gaveUp === true);

    expect(s.supervisor.status).toMatchObject({ state: 'crashed', gaveUp: true });
    expect(s.shown).toEqual(['Scout stopped and could not restart. Open Scout to start it again.']);
  });
});

describe('stop() when no worker process is alive', () => {
  it('during the restart backoff resolves at once and no restart happens afterwards', async () => {
    const s = setup({ restartPolicy: { maxFailures: 5, windowMs: 60_000, backoffMs: [1000] } });
    await startRunning(s);
    jest.useFakeTimers();

    s.children[0]!.emit('exit', 1, null);
    expect(s.supervisor.status.state).toBe('starting');
    const result = await settlesPromptly(s.supervisor.stop());
    jest.advanceTimersByTime(10_000);
    await flush();

    expect(result).toEqual({ exitCode: null });
    expect(s.envBuilds.count).toBe(1);
    expect(s.spawn).toHaveBeenCalledTimes(1);
    expect(s.supervisor.status.state).toBe('stopped');
  });

  it('a stop that lands while a restart is preparing its environment spawns nothing', async () => {
    let releaseEnv: () => void = () => undefined;
    const s = setup({
      restartPolicy: { maxFailures: 5, windowMs: 60_000, backoffMs: [5] },
    });
    await startRunning(s);
    const slowEnv = new Promise<Record<string, string>>((resolve) => {
      releaseEnv = () => resolve({});
    });
    s.setBuildEnv(() => slowEnv);

    s.children[0]!.emit('exit', 1, null);
    await waitFor(() => s.envBuilds.count === 2);
    await s.supervisor.stop();
    releaseEnv();
    await flush();

    expect(s.spawn).toHaveBeenCalledTimes(1);
    expect(s.supervisor.status.state).toBe('stopped');
  });

  it('after a terminal crash resolves at once', async () => {
    const s = setup({ restartPolicy: { maxFailures: 0, windowMs: 60_000, backoffMs: [5] } });
    await startRunning(s);

    s.children[0]!.emit('exit', 1, null);
    expect(s.supervisor.status).toMatchObject({ state: 'crashed', gaveUp: true });
    const result = await settlesPromptly(s.supervisor.stop());

    expect(result).toEqual({ exitCode: null });
    expect(s.supervisor.status.state).toBe('stopped');
  });

  it('Quit during the restart backoff exits the app and leaves no worker behind', async () => {
    const s = setup({ restartPolicy: { maxFailures: 5, windowMs: 60_000, backoffMs: [1000] } });
    await startRunning(s);
    jest.useFakeTimers();
    const app = Object.assign(new EventEmitter(), {
      exitCode: null as number | null,
      quit() {
        app.emit('before-quit', { preventDefault: () => undefined });
      },
      exit(code: number) {
        app.exitCode = code;
      },
    });
    installAppLifecycle({
      app: app as unknown as AppLifecycleDeps['app'],
      platform: 'linux',
      supervisor: () => s.supervisor,
      keepRunning: () => true,
      window: () => null,
      createWindow: () => undefined,
      onHiddenToTray: () => undefined,
      onAsk: () => undefined,
      dispose: () => undefined,
      logger: recordingLogger().logger,
    });

    s.children[0]!.emit('exit', 1, null);
    app.quit();
    await flush();
    jest.advanceTimersByTime(10_000);
    await flush();

    expect(app.exitCode).toBe(0);
    expect(s.spawn).toHaveBeenCalledTimes(1);
  });
});
