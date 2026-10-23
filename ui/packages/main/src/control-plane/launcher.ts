import type { ChildProcess, spawn as nodeSpawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { chmodSync, existsSync, lstatSync, mkdirSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { ControlPlaneUnavailableError } from '@scout/shared';
import type { Logger } from '../util/logger.js';
import { createControlPlaneClient, type ControlPlaneClient } from './client.js';

export interface ControlPlaneLauncherDeps {
  spawn: typeof nodeSpawn;
  resolveBinary: () => string;
  logger: Logger;
  platform: NodeJS.Platform;
  mintToken?: () => string;
  socketDir?: string;
  extraEnv?: Record<string, string>;
  getUid?: () => number | undefined;
}

export interface LaunchedControlPlane {
  readonly kind: 'launched';
  readonly socketPath: string;
  readonly token: string;
  readonly child: ChildProcess;
  dispose(): void;
}

export interface UnsupportedControlPlane {
  readonly kind: 'unsupported';
  readonly reason: string;
}

const MAX_SOCKET_PATH_LENGTH = 100;

const RESERVED_KEYS = new Set([
  'scout_controlplane_token',
  'scout__controlplane__socketpath',
  'urls',
  'http_ports',
  'https_ports',
]);

function isReservedHostKey(key: string): boolean {
  const normalized = key
    .toLowerCase()
    .replaceAll(':', '__')
    .replace(/^(aspnetcore_|dotnet_)/, '');
  return RESERVED_KEYS.has(normalized) || normalized.startsWith('kestrel__');
}

/**
 * The parent environment minus every variable that could move the host's listener or
 * override its socket path or session token, in any casing or ':' spelling, with or
 * without the ASPNETCORE_ or DOTNET_ prefix.
 */
export function inheritableEnv(env: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
  return Object.fromEntries(Object.entries(env).filter(([key]) => !isReservedHostKey(key)));
}

function mintSessionToken(): string {
  return randomBytes(32).toString('hex');
}

function secureSocketDir(socketDir: string, getUid: () => number | undefined): void {
  mkdirSync(socketDir, { recursive: true, mode: 0o700 });
  const stats = lstatSync(socketDir);
  if (stats.isSymbolicLink()) {
    throw new Error(`control-plane socket directory ${socketDir} is a symbolic link`);
  }
  if (!stats.isDirectory()) {
    throw new Error(`control-plane socket directory ${socketDir} is not a directory`);
  }
  const uid = getUid();
  if (uid !== undefined && stats.uid !== uid) {
    throw new Error(
      `control-plane socket directory ${socketDir} is owned by uid ${stats.uid}, not ${uid}`,
    );
  }
  if ((stats.mode & 0o077) !== 0) {
    chmodSync(socketDir, 0o700);
  }
}

function defaultSocketDir(): string {
  const runtimeDir = process.env['XDG_RUNTIME_DIR'];
  if (runtimeDir != null && runtimeDir !== '') return path.join(runtimeDir, 'scout');
  return path.join(os.tmpdir(), `scout-${process.getuid?.() ?? 'user'}`);
}

/**
 * Spawns Scout.Host.ControlPlane bound to a per-session Unix socket and hands it a fresh
 * session token through its environment. The token never reaches the logger.
 */
export function launchControlPlane(
  deps: ControlPlaneLauncherDeps,
): LaunchedControlPlane | UnsupportedControlPlane {
  const { spawn, resolveBinary, logger, platform, mintToken = mintSessionToken } = deps;

  if (platform === 'win32') {
    return {
      kind: 'unsupported',
      reason:
        'The control-plane host listens on a Windows named pipe, which the desktop gRPC client does not dial yet.',
    };
  }

  const socketDir = deps.socketDir ?? defaultSocketDir();
  const socketPath = path.join(socketDir, `controlplane-${process.pid}.sock`);
  if (socketPath.length >= MAX_SOCKET_PATH_LENGTH) {
    throw new Error(
      `control-plane socket path must stay under ${MAX_SOCKET_PATH_LENGTH} characters for a Unix socket: ${socketPath}`,
    );
  }
  secureSocketDir(socketDir, deps.getUid ?? (() => process.getuid?.()));

  const binaryPath = resolveBinary();
  const token = mintToken();

  // ponytail: no restart policy and no parent-death watchdog; reuse the worker supervisor's policy in phase 3.
  const child = spawn(binaryPath, [], {
    env: {
      ...inheritableEnv(process.env),
      ...deps.extraEnv,
      Scout__ControlPlane__SocketPath: socketPath,
      SCOUT_CONTROLPLANE_TOKEN: token,
    },
    cwd: path.dirname(binaryPath),
    stdio: ['ignore', 'ignore', 'inherit'],
    windowsHide: true,
    detached: false,
  });

  child.once('error', (err) => {
    logger.error('control-plane: host process error', err, { binary: binaryPath });
  });
  child.once('exit', (code, signal) => {
    logger.info('control-plane: host exited', {
      code: code ?? undefined,
      signal: signal ?? undefined,
    });
  });
  logger.info('control-plane: host spawned', { pid: child.pid, binary: binaryPath, socketPath });

  return {
    kind: 'launched',
    socketPath,
    token,
    child,
    dispose() {
      child.kill('SIGTERM');
    },
  };
}

export interface ConnectControlPlaneDeps extends ControlPlaneLauncherDeps {
  protoDir: string;
  socketWaitMs?: number;
}

export interface ControlPlaneConnection {
  /** Resolves once the host's socket exists; rejects with ControlPlaneUnavailableError otherwise. */
  readonly client: Promise<ControlPlaneClient>;
  dispose(): void;
}

function waitForSocket(launched: LaunchedControlPlane, timeoutMs: number): Promise<void> {
  const { child, socketPath } = launched;
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve, reject) => {
    const settle = (err?: Error): void => {
      clearInterval(timer);
      child.off('exit', onExit);
      child.off('error', onError);
      if (err === undefined) resolve();
      else reject(err);
    };
    const onExit = (): void =>
      settle(
        new ControlPlaneUnavailableError(
          'The control-plane host exited before it opened its socket',
        ),
      );
    const onError = (err: Error): void =>
      settle(
        new ControlPlaneUnavailableError(`The control-plane host did not start: ${err.message}`, {
          cause: err,
        }),
      );
    const timer = setInterval(() => {
      if (existsSync(socketPath)) settle();
      else if (Date.now() > deadline)
        settle(
          new ControlPlaneUnavailableError(
            `The control-plane socket did not appear within ${timeoutMs} ms`,
          ),
        );
    }, 25);
    child.once('exit', onExit);
    child.once('error', onError);
  });
}

/**
 * Launches the control-plane host, waits for its socket and opens a gRPC client to it.
 * Any failure on the way surfaces as ControlPlaneUnavailableError on the client promise.
 */
export function connectControlPlane(deps: ConnectControlPlaneDeps): ControlPlaneConnection {
  let launched: LaunchedControlPlane | null = null;
  let client: ControlPlaneClient | null = null;

  const connecting = (async (): Promise<ControlPlaneClient> => {
    const result = launchControlPlane(deps);
    if (result.kind === 'unsupported') throw new ControlPlaneUnavailableError(result.reason);
    launched = result;
    await waitForSocket(result, deps.socketWaitMs ?? 10_000);
    client = createControlPlaneClient({
      socketPath: result.socketPath,
      token: result.token,
      protoDir: deps.protoDir,
      platform: deps.platform,
    });
    result.child.once('exit', () => client?.close());
    return client;
  })().catch((err: unknown) => {
    launched?.dispose();
    const unavailable =
      err instanceof ControlPlaneUnavailableError
        ? err
        : new ControlPlaneUnavailableError(
            `The control plane could not be started: ${err instanceof Error ? err.message : String(err)}`,
            { cause: err },
          );
    deps.logger.warn('control-plane: unavailable', { reason: unavailable.message });
    throw unavailable;
  });
  connecting.catch(() => undefined);

  return {
    client: connecting,
    dispose(): void {
      client?.close();
      launched?.dispose();
    },
  };
}
