import type { Logger } from './logger.js';

export interface SingletonLockDeps {
  userDataDir: string;
  hostname: string;
  readlink: (path: string) => Promise<string | null>;
  unlink: (path: string) => Promise<void>;
  isPidAlive: (pid: number) => boolean;
  logger: Logger;
}

export interface SingletonLockRecoveryResult {
  cleared: boolean;
  reason?: 'no-lock' | 'fresh-pid-alive' | 'same-host-dead-pid' | 'other-host' | 'invalid-target';
}

const STRICT_PID_RE = /^\d+$/;

function parseLockTarget(target: string): { host: string; pid: number } | { invalid: true } | null {
  if (target.length === 0) return { invalid: true };

  const lastDash = target.lastIndexOf('-');
  if (lastDash === -1) return { invalid: true };

  const rawPid = target.slice(lastDash + 1);
  const host = target.slice(0, lastDash);

  if (rawPid.length === 0 || host.length === 0) return { invalid: true };

  if (!STRICT_PID_RE.test(rawPid)) return { invalid: true };

  if (rawPid.length > 1 && rawPid[0] === '0') return { invalid: true };

  const pid = parseInt(rawPid, 10);
  if (!Number.isFinite(pid)) return { invalid: true };

  return { host, pid };
}

export async function tryRecoverStaleLock(
  deps: SingletonLockDeps,
): Promise<SingletonLockRecoveryResult> {
  const { userDataDir, hostname, readlink, unlink, isPidAlive, logger } = deps;

  const lockPath = `${userDataDir}/SingletonLock`;
  const target = await readlink(lockPath);

  if (target === null) {
    return { cleared: false, reason: 'no-lock' };
  }

  const parsed = parseLockTarget(target);
  if (parsed === null || 'invalid' in parsed) {
    logger.warn('SingletonLock target has unexpected format; leaving untouched', { target });
    return { cleared: false, reason: 'invalid-target' };
  }

  const { host, pid } = parsed;

  if (host !== hostname) {
    return { cleared: false, reason: 'other-host' };
  }

  if (isPidAlive(pid)) {
    return { cleared: false, reason: 'fresh-pid-alive' };
  }

  // Same host, dead PID — safe to remove the three companion files.
  // Each unlink is attempted independently; ENOENT is tolerated by the
  // caller-supplied unlink implementation. Any other error (EACCES, etc.)
  // propagates so the caller can decide whether to proceed or quit.
  await unlink(lockPath);
  await unlink(`${userDataDir}/SingletonCookie`);
  await unlink(`${userDataDir}/SingletonSocket`);

  logger.info('cleared stale SingletonLock', { pid });

  return { cleared: true, reason: 'same-host-dead-pid' };
}
