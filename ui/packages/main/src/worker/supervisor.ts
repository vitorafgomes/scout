import { spawn as nodeSpawn } from 'node:child_process';
import { statSync } from 'node:fs';
import path from 'node:path';
import type { ChildProcess } from 'node:child_process';
import type { WorkerEvent, WorkerStatus } from '@scout/shared';
import { WorkerBinaryMissingError } from '@scout/shared';
import { attachLogStream } from './log-stream.js';
import type { Logger } from '../util/logger.js';

export interface RestartPolicy {
  maxFailures: number;
  windowMs: number;
  backoffMs: number[];
}

export interface SupervisorDeps {
  spawn: typeof nodeSpawn;
  logger: Logger;
  resolveBinary: () => string;
  buildEnv: () => Promise<Record<string, string>>;
  /** Override binary existence check; defaults to fs.statSync. Useful in tests. */
  checkBinaryExists?: (path: string) => boolean;
  ringLimit?: number;
  readinessTimeoutMs?: number;
  stopTimeoutMs?: number;
  restartPolicy?: RestartPolicy;
  /** Called with the user id of each `SCOUT_PAIRED=` line the worker prints on stdout. */
  onPaired?: (userId: string) => void;
  /** Called when the worker prints `SCOUT_PAIRING_BURNED`: its pairing code is no longer accepted. */
  onPairingBurned?: () => void;
  /** Called with the JSON of each `SCOUT_LOCAL ` line the worker prints on stdout. */
  onLocalEvent?: (json: string) => void;
  /** The platform the stop path is chosen for; defaults to `process.platform`. */
  platform?: NodeJS.Platform;
  /** On Windows, ends a process and every process it started, then calls `done`; defaults to {@link taskkillTree}. */
  killTree?: (pid: number, done: () => void) => void;
  /** How long a hard stop on Windows waits for `killTree` before it kills the worker process anyway. */
  killTreeTimeoutMs?: number;
}

export interface Supervisor {
  readonly status: WorkerStatus;
  start(): Promise<{ pid: number }>;
  stop(): Promise<{ exitCode: number | null }>;
  onEvent(listener: (e: WorkerEvent) => void): () => void;
  getSnapshot(): readonly WorkerEvent[];
  /**
   * Writes one line to the running worker's stdin. Returns false when no worker is
   * running; `onError` gets a write that fails after it was queued (a closed pipe).
   */
  writeStdin(line: string, onError: (err: Error) => void): boolean;
}

const DEFAULT_POLICY: RestartPolicy = {
  maxFailures: 5,
  windowMs: 60_000,
  backoffMs: [1000, 2000, 4000, 8000, 16000],
};

/**
 * Ends a Windows process and every process it started with `taskkill /t /f`, then calls `done`; a failure is
 * logged, never thrown.
 */
export function taskkillTree(
  spawn: typeof nodeSpawn,
  logger: Logger,
): (pid: number, done: () => void) => void {
  return (pid, done) => {
    spawn('taskkill', ['/pid', String(pid), '/t', '/f'], { stdio: 'ignore', windowsHide: true })
      .once('error', (err: Error) => {
        logger.warn('supervisor: taskkill failed', { error: err.message });
        done();
      })
      .once('exit', (code: number | null) => {
        if (code !== 0) logger.warn('supervisor: taskkill failed', { exitCode: code ?? undefined });
        done();
      });
  };
}

function isFile(filePath: string): boolean {
  try {
    return statSync(filePath).isFile();
  } catch {
    return false;
  }
}

// Build a WorkerStatus value omitting optional fields when undefined so
// exactOptionalPropertyTypes is satisfied. Filter-then-spread is the only
// tsc-clean pattern for readonly optional properties.
function makeStatus(
  state: WorkerStatus['state'],
  restarts: number,
  pid?: number,
  startedAt?: string,
  lastExitCode?: number,
  gaveUp?: true,
): WorkerStatus {
  // Collect only the defined optional fields in a plain object, then cast
  // once to WorkerStatus. The cast is safe because every required field
  // (state, restarts) is provided and every optional field is either
  // present with the correct type or absent entirely.
  const optional: Record<string, unknown> = {};
  if (pid !== undefined) optional['pid'] = pid;
  if (startedAt !== undefined) optional['startedAt'] = startedAt;
  if (lastExitCode !== undefined) optional['lastExitCode'] = lastExitCode;
  if (gaveUp !== undefined) optional['gaveUp'] = gaveUp;
  return { state, restarts, ...optional } as unknown as WorkerStatus;
}

export function createSupervisor(deps: SupervisorDeps): Supervisor {
  const {
    spawn,
    logger,
    resolveBinary,
    buildEnv,
    checkBinaryExists = isFile,
    ringLimit = 2000,
    readinessTimeoutMs = 45_000,
    stopTimeoutMs = 5_000,
    restartPolicy = DEFAULT_POLICY,
    platform = process.platform,
    killTree = taskkillTree(nodeSpawn, logger),
    killTreeTimeoutMs = 5_000,
  } = deps;

  const ringBuffer: WorkerEvent[] = [];
  const listeners = new Set<(e: WorkerEvent) => void>();

  let _status: WorkerStatus = { state: 'idle', restarts: 0 };
  let _child: ChildProcess | null = null;
  let _startedAt: string | undefined;

  let failureTimes: number[] = [];
  let restartCount = 0;
  let restartTimer: ReturnType<typeof setTimeout> | null = null;
  let generation = 0;

  function emit(event: WorkerEvent): void {
    if (event.kind !== 'state') {
      ringBuffer.push(event);
      if (ringBuffer.length > ringLimit) {
        ringBuffer.splice(0, ringBuffer.length - ringLimit);
      }
    }
    for (const listener of listeners) {
      try {
        listener(event);
      } catch {
        // never let a misbehaving listener crash the supervisor
      }
    }
  }

  function transition(next: WorkerStatus): void {
    _status = next;
    emit({ kind: 'state', status: next });
  }

  /** Crashed; final when no child is left to exit and no restart is scheduled. */
  function crashed(): void {
    const final = _child === null && restartTimer === null;
    transition(
      makeStatus(
        'crashed',
        restartCount,
        undefined,
        _startedAt,
        undefined,
        final ? true : undefined,
      ),
    );
  }

  /**
   * POSIX sends the signal. Windows closes stdin to stop; to kill it ends the process tree first and the process
   * itself once that answered, or once `killTreeTimeoutMs` passed without an answer.
   */
  function kill(child: ChildProcess, signal: NodeJS.Signals | 'SIGKILL'): void {
    if (platform === 'win32' && signal === 'SIGKILL' && child.pid != null) {
      let ended = false;
      const end = (): void => {
        if (ended) return;
        ended = true;
        clearTimeout(bound);
        child.kill('SIGKILL');
      };
      const bound = setTimeout(() => {
        logger.warn('supervisor: taskkill gave no answer; killing the worker', {
          killTreeTimeoutMs,
        });
        end();
      }, killTreeTimeoutMs);
      try {
        killTree(child.pid, end);
      } catch (err: unknown) {
        logger.warn('supervisor: taskkill failed', {
          error: err instanceof Error ? err.message : String(err),
        });
        end();
      }
      return;
    }
    try {
      if (platform === 'win32' && signal !== 'SIGKILL') {
        child.stdin?.end();
      } else {
        child.kill(signal);
      }
    } catch {
      // process may already be gone
    }
  }

  function scheduleRestart(exitCode: number | null, signal: string | null): void {
    const now = Date.now();
    failureTimes = failureTimes.filter((t) => now - t < restartPolicy.windowMs);
    failureTimes.push(now);

    if (failureTimes.length > restartPolicy.maxFailures) {
      logger.error('supervisor: crash policy exhausted; not restarting', undefined, {
        failures: failureTimes.length,
        window: restartPolicy.windowMs,
      });
      crashed();
      return;
    }

    const backoffIndex = Math.min(failureTimes.length - 1, restartPolicy.backoffMs.length - 1);
    const delay = restartPolicy.backoffMs[backoffIndex] ?? restartPolicy.backoffMs.at(-1) ?? 1000;

    logger.warn('supervisor: worker exited unexpectedly; scheduling restart', {
      exitCode: exitCode ?? undefined,
      signal: signal ?? undefined,
      delay,
      attempt: failureTimes.length,
    });

    transition(makeStatus('starting', restartCount, undefined, _startedAt));

    const gen = generation;
    restartTimer = setTimeout(() => {
      restartTimer = null;
      void doStart().catch((err: unknown) => {
        if (gen !== generation) return;
        logger.error('supervisor: restart attempt failed', err);
        crashed();
      });
    }, delay);
  }

  /** Settles with the start under way: the pid once running, a rejection once crashed or stopped. */
  function whenRunning(): Promise<{ pid: number }> {
    return new Promise((resolve, reject) => {
      const listener = (event: WorkerEvent): void => {
        if (event.kind !== 'state' || event.status.state === 'starting') return;
        listeners.delete(listener);
        if (event.status.state === 'running') resolve({ pid: event.status.pid ?? 0 });
        else
          reject(
            new Error(
              `worker/start-failed: the worker ${event.status.state} before it was running`,
            ),
          );
      };
      listeners.add(listener);
    });
  }

  /** Spawns the worker for the current start; a stop or a newer start makes this one give up. */
  async function doStart(): Promise<{ pid: number }> {
    const gen = generation;
    const binaryPath = resolveBinary();

    if (!checkBinaryExists(binaryPath)) {
      throw new WorkerBinaryMissingError(
        `Worker binary not found: ${binaryPath}. Run 'bun run worker:publish' first.`,
      );
    }

    const extraEnv = await buildEnv();
    if (gen !== generation) {
      throw new Error('worker/stopped: stop requested before the worker was spawned');
    }
    // env is intentionally not logged — it contains secrets.
    // SCOUT__PARENT_WATCHDOG=1 makes the host self-terminate on stdin EOF, so it
    // cannot orphan if this supervisor dies ungracefully (crash / SIGKILL).
    const childEnv = {
      ...workerInheritableEnv(process.env),
      ...extraEnv,
      SCOUT__PARENT_WATCHDOG: '1',
    };

    // stdin is a pipe (not 'ignore') so the host's parent-death watchdog gets
    // EOF when we die. We keep the write end open; quick-ask requests are the
    // only lines written to it (writeStdin).
    const child = spawn(binaryPath, [], {
      env: childEnv,
      cwd: path.dirname(binaryPath),
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      detached: false,
    });

    _child = child;
    child.stdin?.on('error', (err: Error) => {
      logger.warn('supervisor: worker stdin closed', { error: err.message });
    });

    if (child.pid == null) {
      _child = null;
      crashed();
      throw new Error('worker/spawn-failed: child process has no PID after spawn');
    }

    const spawnedPid = child.pid;
    logger.info('supervisor: worker spawned', { pid: spawnedPid, binary: binaryPath });

    return new Promise<{ pid: number }>((resolve, reject) => {
      let settled = false;

      const readinessTimer = setTimeout(() => {
        if (settled) return;
        settled = true;
        logger.error('supervisor: readiness timeout', undefined, { readinessTimeoutMs });
        kill(child, 'SIGKILL');
        crashed();
        reject(new Error('worker/readiness-timeout: SCOUT_READY not received within timeout'));
      }, readinessTimeoutMs);

      attachLogStream(child, {
        emit,
        onPaired: (userId) => deps.onPaired?.(userId),
        onPairingBurned: () => deps.onPairingBurned?.(),
        onLocalEvent: (json) => deps.onLocalEvent?.(json),
        onReady() {
          if (settled || gen !== generation) return;
          settled = true;
          clearTimeout(readinessTimer);
          _startedAt = new Date().toISOString();
          transition(makeStatus('running', restartCount, spawnedPid, _startedAt));
          resolve({ pid: spawnedPid });
        },
      });

      child.once('error', (err) => {
        if (settled) return;
        settled = true;
        clearTimeout(readinessTimer);
        logger.error('supervisor: child process error', err);
        crashed();
        reject(err);
      });

      child.once('exit', (code, signal) => {
        const current = _child === child;
        logger.info('supervisor: worker exited', {
          code: code ?? undefined,
          signal: signal ?? undefined,
        });
        if (current) {
          _child = null;
          emit({ kind: 'exit', code, signal });
        }

        if (!settled) {
          settled = true;
          clearTimeout(readinessTimer);
          if (gen === generation) crashed();
          reject(new Error(`worker exited before SCOUT_READY (code=${String(code)})`));
          return;
        }

        if (current && gen === generation) {
          restartCount += 1;
          scheduleRestart(code, signal);
        }
      });
    });
  }

  return {
    get status(): WorkerStatus {
      return _status;
    },

    async start(): Promise<{ pid: number }> {
      if (_status.state === 'running') return { pid: _status.pid ?? 0 };
      if (_status.state === 'starting') return whenRunning();
      failureTimes = [];
      restartCount = 0;
      _startedAt = undefined;
      generation += 1;
      const gen = generation;

      transition(makeStatus('starting', 0));
      return doStart().catch((err: unknown) => {
        if (gen === generation && _status.state === 'starting') crashed();
        throw err;
      });
    },

    async stop(): Promise<{ exitCode: number | null }> {
      if (restartTimer !== null) {
        clearTimeout(restartTimer);
        restartTimer = null;
      }
      generation += 1;
      const gen = generation;
      const child = _child;
      if (child == null || _status.state === 'stopped' || _status.state === 'idle') {
        transition(makeStatus('stopped', restartCount, undefined, _startedAt));
        return { exitCode: null };
      }

      return new Promise<{ exitCode: number | null }>((resolve) => {
        const killTimer = setTimeout(() => {
          logger.warn('supervisor: stop timeout; sending SIGKILL');
          kill(child, 'SIGKILL');
        }, stopTimeoutMs);

        child.once('exit', (code) => {
          clearTimeout(killTimer);
          if (gen === generation) {
            transition(makeStatus('stopped', restartCount, undefined, _startedAt));
          }
          resolve({ exitCode: code });
        });

        transition(makeStatus('stopped', restartCount, undefined, _startedAt));
        kill(child, 'SIGTERM');
      });
    },

    onEvent(listener: (e: WorkerEvent) => void): () => void {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },

    getSnapshot(): readonly WorkerEvent[] {
      return [...ringBuffer];
    },

    writeStdin(line: string, onError: (err: Error) => void): boolean {
      const stdin = _child?.stdin;
      if (_status.state !== 'running' || stdin == null || !stdin.writable) return false;
      try {
        stdin.write(line + '\n', (err) => {
          if (err != null) onError(err);
        });
        return true;
      } catch {
        return false;
      }
    },
  };
}

const RESERVED_WORKER_KEYS = new Set([
  'scout__features__grantedwrites',
  'scout_controlplane_token',
  'scout_controlplane_worker_token',
  'scout__operator__ffmpegpath',
  'scout__operator__fontpath',
  'scout__operator__piperpath',
  'scout__operator__voicespath',
  'scout__operator__rhubarbpath',
  'scout__operator__mouthspath',
  'scout__operator__presenterspath',
  'scout__operator__facemodelspath',
  'scout__tools__tesseractpath',
  'scout__tools__tessdatapath',
  'scout__tools__locale',
  'scout__meetings__dbpath',
  'scout__speech__directory',
  'scout__sandbox__enabled',
  'scout__sandbox__workdirectory',
]);

/**
 * The parent environment minus every variable the worker would read as its write gate, its write
 * grants, a control-plane token, an operator media tool path, a screen text tool setting, the
 * meetings database path, the folder the local voice renders in, or the Docker runs switch and work folder, in any casing or ':' spelling, with or without the ASPNETCORE_ or DOTNET_
 * prefix; only the values buildEnv sets reach the worker.
 */
function workerInheritableEnv(env: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
  return Object.fromEntries(
    Object.entries(env).filter(([key]) => {
      const normalized = key
        .toLowerCase()
        .replaceAll(':', '__')
        .replace(/^(aspnetcore_|dotnet_)/, '');
      return !RESERVED_WORKER_KEYS.has(normalized) && !normalized.startsWith('scout__workergate__');
    }),
  );
}
