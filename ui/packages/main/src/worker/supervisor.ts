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
  /**
   * Signals the worker's whole process tree: the negative-pid process group on
   * POSIX (so grandchild provider daemons launched by the .NET host die too),
   * or a `taskkill /t` tree-kill on Windows. Defaults to `defaultKillProcessTree`.
   * Injectable so tests never signal a real OS pid.
   */
  killProcessTree?: (pid: number, signal: NodeJS.Signals | 'SIGKILL') => void;
}

export interface Supervisor {
  readonly status: WorkerStatus;
  readonly httpEndpoint: string | null;
  start(): Promise<{ pid: number }>;
  stop(): Promise<{ exitCode: number | null }>;
  onEvent(listener: (e: WorkerEvent) => void): () => void;
  getSnapshot(): readonly WorkerEvent[];
}

const DEFAULT_POLICY: RestartPolicy = {
  maxFailures: 5,
  windowMs: 60_000,
  backoffMs: [1000, 2000, 4000, 8000, 16000],
};

function isFile(filePath: string): boolean {
  try {
    return statSync(filePath).isFile();
  } catch {
    return false;
  }
}

// Production default for SupervisorDeps.killProcessTree. On POSIX, `pid` is
// expected to already be the negative process-group id (the caller decides
// the sign); on win32 it is the plain child pid, since taskkill's `/t` flag
// walks the tree itself.
function defaultKillProcessTree(pid: number, signal: NodeJS.Signals | 'SIGKILL'): void {
  if (process.platform === 'win32') {
    nodeSpawn('taskkill', ['/pid', String(pid), '/t']);
    return;
  }
  process.kill(pid, signal);
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
): WorkerStatus {
  // Collect only the defined optional fields in a plain object, then cast
  // once to WorkerStatus. The cast is safe because every required field
  // (state, restarts) is provided and every optional field is either
  // present with the correct type or absent entirely.
  const optional: Record<string, unknown> = {};
  if (pid !== undefined) optional['pid'] = pid;
  if (startedAt !== undefined) optional['startedAt'] = startedAt;
  if (lastExitCode !== undefined) optional['lastExitCode'] = lastExitCode;
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
    killProcessTree = defaultKillProcessTree,
  } = deps;

  const ringBuffer: WorkerEvent[] = [];
  const listeners = new Set<(e: WorkerEvent) => void>();

  let _status: WorkerStatus = { state: 'idle', restarts: 0 };
  let _child: ChildProcess | null = null;
  let _startedAt: string | undefined;
  let _httpEndpoint: string | null = null;

  let failureTimes: number[] = [];
  let restartCount = 0;
  // Handle of the pending crash-restart backoff timer (see scheduleRestart),
  // so stop() can cancel it and prevent a scheduled restart from resurrecting
  // a worker the user just stopped.
  let restartTimer: ReturnType<typeof setTimeout> | null = null;

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

  // Kills the whole worker process tree, not just the direct child: the .NET
  // host launches provider daemons as grandchildren (SystemProcessLauncher /
  // Process.Start), which would otherwise orphan and keep running after the
  // supervisor signals only the direct pid. On POSIX the child is spawned
  // detached (its own process-group leader), so signalling -pid reaches the
  // whole group; on win32, taskkill's /t already walks the tree.
  function kill(child: ChildProcess, signal: NodeJS.Signals | 'SIGKILL'): void {
    const pid = child.pid;
    if (pid == null) {
      try {
        child.kill(signal);
      } catch {
        // process may already be gone
      }
      return;
    }

    try {
      killProcessTree(process.platform === 'win32' ? pid : -pid, signal);
    } catch (err: unknown) {
      const code = (err as NodeJS.ErrnoException | undefined)?.code;
      if (code === 'ESRCH') {
        logger.warn('supervisor: process group already gone; falling back to direct signal', {
          pid,
        });
      } else {
        logger.warn('supervisor: killProcessTree failed; falling back to direct signal', {
          pid,
          err: String(err),
        });
      }
      try {
        child.kill(signal);
      } catch {
        // process may already be gone
      }
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
      transition(makeStatus('crashed', restartCount, undefined, _startedAt));
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

    restartTimer = setTimeout(() => {
      restartTimer = null;
      // A stop() that ran during the backoff window already transitioned to
      // 'stopped'/'idle' and cleared this timer in the common case; this
      // guard is the last line of defense so a race can never resurrect a
      // worker the user explicitly stopped.
      if (_status.state === 'stopped' || _status.state === 'idle') {
        return;
      }
      void doStart().catch((err: unknown) => {
        logger.error('supervisor: restart attempt failed', err);
        transition(makeStatus('crashed', restartCount, undefined, _startedAt));
      });
    }, delay);
  }

  async function doStart(): Promise<{ pid: number }> {
    const binaryPath = resolveBinary();

    if (!checkBinaryExists(binaryPath)) {
      throw new WorkerBinaryMissingError(
        `Worker binary not found: ${binaryPath}. Run 'bun run worker:publish' first.`,
      );
    }

    const extraEnv = await buildEnv();
    // env is intentionally not logged — it contains secrets.
    // SCOUT__PARENT_WATCHDOG=1 makes the host self-terminate on stdin EOF, so it
    // cannot orphan if this supervisor dies ungracefully (crash / SIGKILL).
    const childEnv = { ...process.env, ...extraEnv, SCOUT__PARENT_WATCHDOG: '1' };

    // stdin is a pipe (not 'ignore') so the host's parent-death watchdog gets
    // EOF when we die. We keep the write end open and never write to it.
    // detached on POSIX makes the child its own process-group leader (via
    // setsid), so `kill()` can later signal the whole group — including
    // grandchild provider daemons the .NET host spawns — instead of orphaning
    // them. Windows has no process-group concept here; taskkill /t handles it.
    const child = spawn(binaryPath, [], {
      env: childEnv,
      cwd: path.dirname(binaryPath),
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      detached: process.platform !== 'win32',
    });

    _child = child;

    if (child.pid == null) {
      transition(makeStatus('crashed', restartCount, undefined, _startedAt));
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
        transition(makeStatus('crashed', restartCount, undefined, _startedAt));
        reject(new Error('worker/readiness-timeout: SCOUT_READY not received within timeout'));
      }, readinessTimeoutMs);

      attachLogStream(child, {
        emit,
        ringBuffer,
        ringLimit,
        onHttpEndpoint(endpoint) {
          _httpEndpoint = endpoint;
          logger.info('supervisor: worker http endpoint registered', { endpoint });
        },
        onReady() {
          if (settled) return;
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
        transition(makeStatus('crashed', restartCount, undefined, _startedAt));
        reject(err);
      });

      child.once('exit', (code, signal) => {
        _httpEndpoint = null;
        // Without this, _child keeps pointing at the dead process during the
        // crash-restart backoff window: stop() would then await this same
        // ChildProcess's 'exit' event a second time, which Node never
        // re-fires, hanging the returned Promise forever.
        _child = null;
        emit({ kind: 'exit', code, signal });
        logger.info('supervisor: worker exited', {
          code: code ?? undefined,
          signal: signal ?? undefined,
        });

        if (!settled) {
          settled = true;
          clearTimeout(readinessTimer);
          transition(makeStatus('crashed', restartCount, undefined, _startedAt));
          reject(new Error(`worker exited before SCOUT_READY (code=${String(code)})`));
          return;
        }

        if (_status.state !== 'stopped') {
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

    get httpEndpoint(): string | null {
      return _httpEndpoint;
    },

    async start(): Promise<{ pid: number }> {
      if (_status.state === 'running' || _status.state === 'starting') {
        return { pid: _status.pid ?? 0 };
      }
      failureTimes = [];
      restartCount = 0;
      _startedAt = undefined;
      _httpEndpoint = null;

      transition(makeStatus('starting', 0));
      return doStart();
    },

    async stop(): Promise<{ exitCode: number | null }> {
      // Cancel any pending crash-restart backoff timer first: without this,
      // a restart scheduled before this stop() call would still fire later
      // and resurrect a worker the user just explicitly stopped.
      if (restartTimer !== null) {
        clearTimeout(restartTimer);
        restartTimer = null;
      }

      const child = _child;
      // Covers the case where _child was not yet nulled by the exit handler
      // (e.g. a caller holding a stale reference) but the process has
      // already exited or been signalled — waiting on 'exit' again would
      // hang, since Node never re-fires an event that already fired.
      const alreadyExited =
        child != null &&
        (child.killed || (child.exitCode !== null && child.exitCode !== undefined));

      if (
        child == null ||
        alreadyExited ||
        _status.state === 'stopped' ||
        _status.state === 'idle'
      ) {
        transition(makeStatus('stopped', restartCount, undefined, _startedAt));
        return { exitCode: child?.exitCode ?? null };
      }

      return new Promise<{ exitCode: number | null }>((resolve) => {
        let settled = false;
        const settleOnce = (exitCode: number | null): void => {
          if (settled) return;
          settled = true;
          transition(makeStatus('stopped', restartCount, undefined, _startedAt));
          resolve({ exitCode });
        };

        const killTimer = setTimeout(() => {
          logger.warn('supervisor: stop timeout; sending SIGKILL');
          kill(child, 'SIGKILL');
          // Resolve here too: if 'exit' never re-fires (e.g. the process was
          // already gone before this SIGKILL), the Promise must still settle
          // instead of hanging past the kill timer indefinitely.
          settleOnce(child.exitCode ?? null);
        }, stopTimeoutMs);

        child.once('exit', (code) => {
          clearTimeout(killTimer);
          settleOnce(code);
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
  };
}
