import type { ChildProcess, spawn as nodeSpawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { chmodSync, lstatSync, mkdirSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import type { Logger } from '../util/logger.js';

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
      ...process.env,
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
