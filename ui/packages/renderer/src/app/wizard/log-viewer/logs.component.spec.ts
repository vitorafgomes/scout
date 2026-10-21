import { describe, expect, it } from 'vitest';

import { classifyWorkerLine } from './logs.component';

describe('classifyWorkerLine', () => {
  it('maps .NET console logger prefixes on stderr to their real level', () => {
    expect(classifyWorkerLine('stderr', 'info: Microsoft.Hosting.Lifetime[0]', 'info')).toBe(
      'info',
    );
    expect(classifyWorkerLine('stderr', 'warn: Scout.Skills.SkillSelector[0]', 'info')).toBe(
      'warn',
    );
    expect(classifyWorkerLine('stderr', 'fail: Scout.Llm.ChatGateway[0]', 'info')).toBe('error');
    expect(classifyWorkerLine('stderr', 'crit: Anything[0]', 'info')).toBe('error');
    expect(classifyWorkerLine('stderr', 'dbug: Anything[0]', 'info')).toBe('debug');
    expect(classifyWorkerLine('stderr', 'trce: Anything[0]', 'info')).toBe('debug');
  });

  it('treats [scout] status markers on stderr as info', () => {
    expect(
      classifyWorkerLine('stderr', '[scout] env=Production adapters configured: Discord', 'error'),
    ).toBe('info');
    expect(classifyWorkerLine('stderr', '[scout] host started', 'error')).toBe('info');
  });

  it('inherits the previous entry level on indented continuation lines', () => {
    expect(
      classifyWorkerLine('stderr', '      Application started. Press Ctrl+C to shut down.', 'info'),
    ).toBe('info');
    expect(classifyWorkerLine('stderr', '      something went wrong', 'error')).toBe('error');
    expect(classifyWorkerLine('stderr', '   at Scout.Program.Main()', 'error')).toBe('error');
  });

  it('keeps raw unformatted stderr as error', () => {
    expect(
      classifyWorkerLine('stderr', 'Unhandled exception. System.InvalidOperationException', 'info'),
    ).toBe('error');
  });

  it('classifies stdout markers as info', () => {
    expect(classifyWorkerLine('stdout', 'SCOUT_READY', 'info')).toBe('info');
    expect(classifyWorkerLine('stdout', 'SCOUT_HTTP=127.0.0.1:45611', 'info')).toBe('info');
  });
});
