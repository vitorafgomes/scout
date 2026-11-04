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
}

export interface Supervisor {
  readonly status: WorkerStatus;
  readonly httpEndpoint: string | null;
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
  } = deps;

  const ringBuffer: WorkerEvent[] = [];
  const listeners = new Set<(e: WorkerEvent) => void>();

  let _status: WorkerStatus = { state: 'idle', restarts: 0 };
  let _child: ChildProcess | null = null;
  let _startedAt: string | undefined;
  let _httpEndpoint: string | null = null;

  let failureTimes: number[] = [];
  let restartCount = 0;
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

  function kill(child: ChildProcess, signal: NodeJS.Signals | 'SIGKILL'): void {
    try {
      if (process.platform === 'win32' && child.pid != null) {
        nodeSpawn('taskkill', ['/pid', String(child.pid), '/t']);
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

    restartTimer = setTimeout(() => {
      restartTimer = null;
      void doStart().catch((err: unknown) => {
        if (_status.state === 'stopped') return;
        logger.error('supervisor: restart attempt failed', err);
        crashed();
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
    if (_status.state === 'stopped') {
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
        ringBuffer,
        ringLimit,
        onPaired: (userId) => deps.onPaired?.(userId),
        onPairingBurned: () => deps.onPairingBurned?.(),
        onLocalEvent: (json) => deps.onLocalEvent?.(json),
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
        crashed();
        reject(err);
      });

      child.once('exit', (code, signal) => {
        if (_child === child) _child = null;
        _httpEndpoint = null;
        emit({ kind: 'exit', code, signal });
        logger.info('supervisor: worker exited', {
          code: code ?? undefined,
          signal: signal ?? undefined,
        });

        if (!settled) {
          settled = true;
          clearTimeout(readinessTimer);
          if (_status.state !== 'stopped') crashed();
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
      if (restartTimer !== null) {
        clearTimeout(restartTimer);
        restartTimer = null;
      }
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
          transition(makeStatus('stopped', restartCount, undefined, _startedAt));
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
  'scout__tools__tesseractpath',
  'scout__tools__tessdatapath',
  'scout__tools__locale',
  'scout__meetings__dbpath',
]);

/**
 * The parent environment minus every variable the worker would read as its write gate, its write
 * grants, a control-plane token, an operator media tool path, a screen text tool setting or the
 * meetings database path, in any casing or ':' spelling, with or without the ASPNETCORE_ or DOTNET_
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
