import { describe, expect, it } from 'vitest';

import { detectLevel } from './log-level';

describe('detectLevel', () => {
  it('treats info-prefixed lines as info', () => {
    expect(detectLevel('info: Microsoft.Hosting.Lifetime[0]')).toBe('info');
    expect(detectLevel('      Application started. Press Ctrl+C to shut down.')).toBe('info');
  });

  it('treats fail/crit/error lines as error', () => {
    expect(detectLevel('fail: Scout.Host.Console[0]')).toBe('error');
    expect(detectLevel('crit: Scout.Host.Console[0]')).toBe('error');
    expect(detectLevel('System.Exception: unhandled error')).toBe('error');
  });

  it('treats warn lines as warn', () => {
    expect(detectLevel('warn: something looks off')).toBe('warn');
  });

  it('treats debug/trace lines as debug', () => {
    expect(detectLevel('dbug: verbose detail')).toBe('debug');
    expect(detectLevel('trce: very verbose')).toBe('debug');
  });
});
