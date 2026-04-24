import { describe, it, expect } from 'bun:test';
import { tryRecoverStaleLock } from '../src/util/singleton-lock.js';
import type { SingletonLockDeps } from '../src/util/singleton-lock.js';
import type { Logger } from '../src/util/logger.js';

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

function makeDeps(overrides: Partial<SingletonLockDeps> = {}): SingletonLockDeps & {
  unlinkCalls: string[];
} {
  const unlinkCalls: string[] = [];

  return {
    userDataDir: '/home/user/.config/scout',
    hostname: 'my-host',
    readlink: async () => null,
    unlink: async (p) => {
      unlinkCalls.push(p);
    },
    isPidAlive: () => false,
    logger: makeNullLogger(),
    unlinkCalls,
    ...overrides,
  };
}

describe('tryRecoverStaleLock', () => {
  it('returns no-lock when readlink returns null', async () => {
    const deps = makeDeps({ readlink: async () => null });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(false);
    expect(result.reason).toBe('no-lock');
    expect(deps.unlinkCalls).toHaveLength(0);
  });

  it('returns other-host and does not unlink when host differs', async () => {
    const deps = makeDeps({ readlink: async () => 'other-host-1234' });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(false);
    expect(result.reason).toBe('other-host');
    expect(deps.unlinkCalls).toHaveLength(0);
  });

  it('returns fresh-pid-alive and does not unlink when same host and PID is alive', async () => {
    const deps = makeDeps({
      readlink: async () => 'my-host-5678',
      isPidAlive: () => true,
    });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(false);
    expect(result.reason).toBe('fresh-pid-alive');
    expect(deps.unlinkCalls).toHaveLength(0);
  });

  it('clears lock and removes three companion files when same host and PID is dead', async () => {
    const deps = makeDeps({
      readlink: async () => 'my-host-9999',
      isPidAlive: () => false,
    });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(true);
    expect(result.reason).toBe('same-host-dead-pid');
    expect(deps.unlinkCalls).toHaveLength(3);
    expect(deps.unlinkCalls).toContain('/home/user/.config/scout/SingletonLock');
    expect(deps.unlinkCalls).toContain('/home/user/.config/scout/SingletonCookie');
    expect(deps.unlinkCalls).toContain('/home/user/.config/scout/SingletonSocket');
  });

  it('correctly parses hyphenated hostnames (last dash splits host from pid)', async () => {
    const deps = makeDeps({
      hostname: 'pop-os',
      readlink: async () => 'pop-os-12345',
      isPidAlive: () => false,
    });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(true);
    expect(result.reason).toBe('same-host-dead-pid');
  });

  it('succeeds even when companion files are absent (ENOENT tolerated by caller-supplied unlink)', async () => {
    // The SingletonLockDeps contract states that the caller-supplied unlink
    // must already be tolerant of ENOENT — this mirrors what createProdSingletonLockDeps
    // does. tryRecoverStaleLock itself does not catch ENOENT; it is the wiring
    // layer that swallows it. This test verifies that a well-behaved unlink
    // implementation that silences ENOENT allows recovery to succeed.
    const callLog: string[] = [];
    const deps = makeDeps({
      readlink: async () => 'my-host-4242',
      isPidAlive: () => false,
      unlink: async (p) => {
        callLog.push(p);
        // SingletonCookie is absent — caller swallows ENOENT (as prod wiring does)
        // and returns normally without throwing.
      },
    });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(true);
    expect(result.reason).toBe('same-host-dead-pid');
    expect(callLog).toHaveLength(3);
    expect(callLog).toContain('/home/user/.config/scout/SingletonLock');
    expect(callLog).toContain('/home/user/.config/scout/SingletonCookie');
    expect(callLog).toContain('/home/user/.config/scout/SingletonSocket');
  });

  it('propagates unexpected unlink errors (e.g. EACCES) instead of swallowing them', async () => {
    const deps = makeDeps({
      readlink: async () => 'my-host-1111',
      isPidAlive: () => false,
      unlink: async () => {
        const err = Object.assign(new Error('EACCES: permission denied'), { code: 'EACCES' });
        throw err;
      },
    });

    // The module propagates arbitrary errors — the caller treats this as "cannot
    // recover" and should fall through to quit(). We verify the promise rejects
    // rather than silently returning { cleared: true }.
    await expect(tryRecoverStaleLock(deps)).rejects.toThrow('EACCES');
  });

  it('returns invalid-target when lock target has no dash', async () => {
    const deps = makeDeps({ readlink: async () => 'singleprocess' });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(false);
    expect(result.reason).toBe('invalid-target');
    expect(deps.unlinkCalls).toHaveLength(0);
  });

  it('returns invalid-target when pid portion has trailing garbage', async () => {
    const deps = makeDeps({ readlink: async () => 'pop-os-abc' });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(false);
    expect(result.reason).toBe('invalid-target');
    expect(deps.unlinkCalls).toHaveLength(0);
  });

  it('returns invalid-target when pid portion is empty', async () => {
    const deps = makeDeps({ readlink: async () => 'pop-os-' });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(false);
    expect(result.reason).toBe('invalid-target');
    expect(deps.unlinkCalls).toHaveLength(0);
  });

  it('returns invalid-target when target is empty string', async () => {
    const deps = makeDeps({ readlink: async () => '' });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(false);
    expect(result.reason).toBe('invalid-target');
    expect(deps.unlinkCalls).toHaveLength(0);
  });

  it('returns invalid-target when host prefix is empty', async () => {
    const deps = makeDeps({ readlink: async () => '-12345' });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(false);
    expect(result.reason).toBe('invalid-target');
    expect(deps.unlinkCalls).toHaveLength(0);
  });

  it('returns other-host (not invalid-target) when parse succeeds but host differs', async () => {
    const deps = makeDeps({ readlink: async () => 'other-host-1234' });
    const result = await tryRecoverStaleLock(deps);

    expect(result.cleared).toBe(false);
    expect(result.reason).toBe('other-host');
    expect(deps.unlinkCalls).toHaveLength(0);
  });
});
