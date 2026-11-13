import { describe, it, expect, mock, afterEach } from 'bun:test';
import { isPidAlive } from '../src/util/singleton-lock-deps.js';

function makeErrnoError(code: string): NodeJS.ErrnoException {
  const err = new Error(code) as NodeJS.ErrnoException;
  err.code = code;
  return err;
}

describe('isPidAlive', () => {
  const originalKill = process.kill.bind(process);

  afterEach(() => {
    process.kill = originalKill as typeof process.kill;
  });

  it('returns true when process.kill does not throw', () => {
    process.kill = mock(() => true) as typeof process.kill;

    expect(isPidAlive(1234)).toBe(true);
  });

  it('returns false when process.kill throws ESRCH (no such process)', () => {
    process.kill = mock(() => {
      throw makeErrnoError('ESRCH');
    }) as typeof process.kill;

    expect(isPidAlive(1234)).toBe(false);
  });

  it('returns true when process.kill throws EPERM (process alive but not ours)', () => {
    process.kill = mock(() => {
      throw makeErrnoError('EPERM');
    }) as typeof process.kill;

    expect(isPidAlive(1234)).toBe(true);
  });

  it('returns true when process.kill throws an unknown error code (conservative)', () => {
    process.kill = mock(() => {
      throw makeErrnoError('EINVAL');
    }) as typeof process.kill;

    expect(isPidAlive(1234)).toBe(true);
  });

  it('returns true for NaN pid without calling process.kill', () => {
    const killSpy = mock(() => true) as typeof process.kill;
    process.kill = killSpy;

    expect(isPidAlive(NaN)).toBe(true);
    expect((killSpy as ReturnType<typeof mock>).mock.calls).toHaveLength(0);
  });

  it('returns true for zero pid without calling process.kill', () => {
    const killSpy = mock(() => true) as typeof process.kill;
    process.kill = killSpy;

    expect(isPidAlive(0)).toBe(true);
    expect((killSpy as ReturnType<typeof mock>).mock.calls).toHaveLength(0);
  });

  it('returns true for negative pid without calling process.kill', () => {
    const killSpy = mock(() => true) as typeof process.kill;
    process.kill = killSpy;

    expect(isPidAlive(-1)).toBe(true);
    expect((killSpy as ReturnType<typeof mock>).mock.calls).toHaveLength(0);
  });
});
