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
      expect(elapsed).toBeLessThan(2000);
    },
    { timeout: 5000 },
  );
});
