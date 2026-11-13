import { describe, it, expect } from 'bun:test';
import { createProductionDeps } from '../src/detect/deps.js';

describe('createProductionDeps / subprocess timeout', () => {
  it(
    'kills child process after timeout and returns non-zero exit code',
    async () => {
      if (process.platform === 'win32') {
        return;
      }

      const deps = createProductionDeps();
      const start = Date.now();

      const result = await deps.exec('sleep', ['10'], 300);

      const elapsed = Date.now() - start;

      expect(result.exitCode).not.toBe(0);
      expect(result.failure).toBe('timeout');
      expect(elapsed).toBeLessThan(2000);
    },
    { timeout: 5000 },
  );

  it('says the command is missing when it cannot start, and nothing when it ran and exited non-zero', async () => {
    const deps = createProductionDeps();

    const missing = await deps.exec('scout-no-such-command-xyz', [], 2000);
    const exited = await deps.exec(process.execPath, ['-e', 'process.exit(3)'], 5000);

    expect(missing.failure).toBe('missing');
    expect(exited.exitCode).toBe(3);
    expect(exited.failure).toBeUndefined();
  });
});
