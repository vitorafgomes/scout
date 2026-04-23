import { describe, it, expect } from 'bun:test';
import path from 'node:path';
import { resolveWorkerBinary } from '../src/worker/resolve-binary.js';

describe('resolveWorkerBinary', () => {
  describe('dev mode (app absent)', () => {
    it('returns repo-relative path for linux-x64', () => {
      const result = resolveWorkerBinary({
        platform: 'linux',
        arch: 'x64',
        cwd: '/repo/ui/packages/main/src/worker',
      });
      expect(result).toContain('build/worker/linux-x64/Scout.Host.Worker');
      expect(result).not.toContain('.exe');
    });

    it('returns repo-relative path for osx-arm64', () => {
      const result = resolveWorkerBinary({
        platform: 'darwin',
        arch: 'arm64',
        cwd: '/repo/ui/packages/main/src/worker',
      });
      expect(result).toContain('build/worker/osx-arm64/Scout.Host.Worker');
      expect(result).not.toContain('.exe');
    });

    it('returns repo-relative path for osx-x64', () => {
      const result = resolveWorkerBinary({
        platform: 'darwin',
        arch: 'x64',
        cwd: '/repo/ui/packages/main/src/worker',
      });
      expect(result).toContain('build/worker/osx-x64/Scout.Host.Worker');
    });

    it('appends .exe on win32', () => {
      const result = resolveWorkerBinary({
        platform: 'win32',
        arch: 'x64',
        cwd: '/repo/ui/packages/main/src/worker',
      });
      expect(result).toContain('build/worker/win-x64/Scout.Host.Worker.exe');
    });

    it('treats isPackaged===false as dev mode', () => {
      const result = resolveWorkerBinary({
        app: { isPackaged: false },
        platform: 'linux',
        arch: 'x64',
        cwd: '/repo/ui/packages/main/src/worker',
      });
      expect(result).toContain('build/worker/linux-x64/Scout.Host.Worker');
    });
  });

  describe('prod mode (app.isPackaged === true)', () => {
    it('returns resourcesPath-based path for linux', () => {
      const result = resolveWorkerBinary({
        app: { isPackaged: true },
        resourcesPath: '/opt/scout/resources',
        platform: 'linux',
        arch: 'x64',
        cwd: '/any',
      });
      expect(result).toBe(
        path.join('/opt/scout/resources', 'worker', 'linux-x64', 'Scout.Host.Worker'),
      );
    });

    it('returns resourcesPath-based path for macOS arm64', () => {
      const result = resolveWorkerBinary({
        app: { isPackaged: true },
        resourcesPath: '/Applications/Scout.app/Contents/Resources',
        platform: 'darwin',
        arch: 'arm64',
        cwd: '/any',
      });
      expect(result).toBe(
        path.join(
          '/Applications/Scout.app/Contents/Resources',
          'worker',
          'osx-arm64',
          'Scout.Host.Worker',
        ),
      );
    });

    it('appends .exe in prod mode on win32', () => {
      const result = resolveWorkerBinary({
        app: { isPackaged: true },
        resourcesPath: 'C:\\Program Files\\Scout\\resources',
        platform: 'win32',
        arch: 'x64',
        cwd: 'C:\\any',
      });
      expect(result).toContain('Scout.Host.Worker.exe');
    });
  });
});
