import { describe, expect, it } from 'vitest';

import { detectLevel, detectLevels } from './log-level';

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

describe('detectLevels', () => {
  const stderr = (line: string) => ({ kind: 'stderr', line });
  const levelsOf = (lines: readonly { kind: string; line: string }[]) =>
    detectLevels(lines).map((l) => l.level);

  it('gives the stack under a warn or fail header the level of that header', () => {
    const lines = [
      stderr('warn: Scout.Skills.SkillSelector[0]'),
      stderr('      Failed to build skill index; running with no skills.'),
      stderr(
        '      System.Net.Http.HttpRequestException: Response status code does not indicate success: 429.',
      ),
      stderr('         at Scout.Llm.Embeddings.GenerateAsync(IEnumerable`1 values)'),
      stderr('         at Scout.Skills.SkillSelector.BuildIndexAsync()'),
      stderr('      --- End of stack trace from previous location ---'),
      stderr('         at Scout.Skills.SkillSelector.SelectAsync(String message)'),
      stderr('fail: Scout.Host.Console[0]'),
      stderr('      Debug trace of the request follows'),
      stderr('   --- End of inner exception stack trace ---'),
      stderr('   '),
    ];

    expect(levelsOf(lines)).toEqual([
      'warn',
      'warn',
      'warn',
      'warn',
      'warn',
      'warn',
      'warn',
      'error',
      'error',
      'error',
      'error',
    ]);
  });

  it('classifies a standalone line after a stack block on its own content again', () => {
    const lines = [
      stderr('fail: Scout.Host.Console[0]'),
      stderr('         at Scout.Host.Console.Program.Main()'),
      stderr('info: Microsoft.Hosting.Lifetime[0]'),
      stderr('      Application started. Press Ctrl+C to shut down.'),
      stderr('dbug: Scout.Conversation[0]'),
    ];

    const [header, , info, message, debug] = levelsOf(lines);
    expect([header, info, message, debug]).toEqual(['error', 'info', 'info', 'debug']);
  });

  it('inherits only from the same stream', () => {
    const lines = [
      stderr('fail: Scout.Host.Console[0]'),
      { kind: 'stdout', line: 'info: Scout.Host.Console[0]' },
      stderr('         at Scout.Host.Console.Program.Main()'),
      { kind: 'stdout', line: '      SCOUT_READY' },
    ];

    expect(levelsOf(lines)).toEqual(['error', 'info', 'error', 'info']);
  });

  it('falls back to the line content for a continuation line with no header before it', () => {
    expect(levelsOf([stderr('   --- End of stack trace from previous location ---')])).toEqual([
      'debug',
    ]);
    expect(levelsOf([stderr('      System.Exception: unhandled error')])).toEqual(['error']);
  });
});
