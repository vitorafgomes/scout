import { describe, it, expect } from 'bun:test';
import { createLogger } from '../src/util/logger.js';

describe('createLogger', () => {
  it('writes one JSON-parseable line to the sink on info()', () => {
    const lines: string[] = [];
    const logger = createLogger('x', { sink: (l) => lines.push(l) });

    logger.info('hello');

    expect(lines).toHaveLength(1);
    const parsed = JSON.parse(lines[0]!) as Record<string, unknown>;
    expect(parsed['lvl']).toBe('info');
    expect(parsed['msg']).toBe('hello');
    expect(parsed['name']).toBe('x');
    expect(typeof parsed['ts']).toBe('string');
  });

  it('includes err.name and err.message but NOT err.stack for native Error', () => {
    const lines: string[] = [];
    const logger = createLogger('x', { sink: (l) => lines.push(l) });
    const err = new TypeError('something went wrong');

    logger.error('boom', err);

    const parsed = JSON.parse(lines[0]!) as Record<string, unknown>;
    const errField = parsed['err'] as Record<string, string>;
    expect(errField['name']).toBe('TypeError');
    expect(errField['message']).toBe('something went wrong');
    expect(Object.keys(errField)).not.toContain('stack');
  });

  it('child() merges bindings into every emitted line', () => {
    const lines: string[] = [];
    const logger = createLogger('x', { sink: (l) => lines.push(l) });
    const child = logger.child({ reqId: '42' });

    child.info('request received');

    const parsed = JSON.parse(lines[0]!) as Record<string, unknown>;
    const ctx = parsed['ctx'] as Record<string, unknown>;
    expect(ctx['reqId']).toBe('42');
  });

  it('child() merges call-site ctx on top of bindings', () => {
    const lines: string[] = [];
    const logger = createLogger('x', { sink: (l) => lines.push(l) });
    const child = logger.child({ service: 'main' });

    child.warn('something odd', { detail: 'extra' });

    const parsed = JSON.parse(lines[0]!) as Record<string, unknown>;
    const ctx = parsed['ctx'] as Record<string, unknown>;
    expect(ctx['service']).toBe('main');
    expect(ctx['detail']).toBe('extra');
  });

  it('SCOUT_LOG_LEVEL=warn suppresses debug and info', () => {
    const original = process.env['SCOUT_LOG_LEVEL'];
    process.env['SCOUT_LOG_LEVEL'] = 'warn';

    const lines: string[] = [];
    const logger = createLogger('x', { sink: (l) => lines.push(l) });

    logger.debug('should be suppressed');
    logger.info('also suppressed');
    logger.warn('this passes');

    expect(lines).toHaveLength(1);
    const parsed = JSON.parse(lines[0]!) as Record<string, unknown>;
    expect(parsed['lvl']).toBe('warn');

    process.env['SCOUT_LOG_LEVEL'] = original;
  });

  it('opts.level takes precedence over SCOUT_LOG_LEVEL env', () => {
    const original = process.env['SCOUT_LOG_LEVEL'];
    process.env['SCOUT_LOG_LEVEL'] = 'error';

    const lines: string[] = [];
    const logger = createLogger('x', { level: 'debug', sink: (l) => lines.push(l) });

    logger.debug('visible');

    expect(lines).toHaveLength(1);

    process.env['SCOUT_LOG_LEVEL'] = original;
  });

  it('emits ctx field only when context is non-empty', () => {
    const lines: string[] = [];
    const logger = createLogger('x', { sink: (l) => lines.push(l) });

    logger.info('no ctx');

    const parsed = JSON.parse(lines[0]!) as Record<string, unknown>;
    expect(Object.keys(parsed)).not.toContain('ctx');
  });

  it('error() without err arg does not include err field', () => {
    const lines: string[] = [];
    const logger = createLogger('x', { sink: (l) => lines.push(l) });

    logger.error('plain error');

    const parsed = JSON.parse(lines[0]!) as Record<string, unknown>;
    expect(Object.keys(parsed)).not.toContain('err');
  });

  it('default sink writes to process.stderr (no explicit sink)', () => {
    // Verifies factory resolves without throwing when sink is omitted.
    const logger = createLogger('smoke');
    expect(() => logger.info('stderr smoke')).not.toThrow();
  });
});
