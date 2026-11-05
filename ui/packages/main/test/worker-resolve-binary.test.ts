import { describe, it, expect } from 'bun:test';
import path from 'node:path';
import { resolveMediaTools, resolveWorkerBinary } from '../src/worker/resolve-binary.js';

// Normalize paths to POSIX so assertions work on both POSIX and Windows runners.
const posix = (p: string): string => p.replace(/\\/g, '/');

describe('resolveWorkerBinary', () => {
  describe('dev mode (app absent)', () => {
    it('returns repo-relative path for linux-x64', () => {
      const result = resolveWorkerBinary({
        platform: 'linux',
        arch: 'x64',
        cwd: '/repo/ui/packages/main/src/worker',
      });
      expect(posix(result)).toContain('build/worker/linux-x64/Scout.Host.Console');
      expect(result).not.toContain('.exe');
    });

    it('returns repo-relative path for osx-arm64', () => {
      const result = resolveWorkerBinary({
        platform: 'darwin',
        arch: 'arm64',
        cwd: '/repo/ui/packages/main/src/worker',
      });
      expect(posix(result)).toContain('build/worker/osx-arm64/Scout.Host.Console');
      expect(result).not.toContain('.exe');
    });

    it('returns repo-relative path for osx-x64', () => {
      const result = resolveWorkerBinary({
        platform: 'darwin',
        arch: 'x64',
        cwd: '/repo/ui/packages/main/src/worker',
      });
      expect(posix(result)).toContain('build/worker/osx-x64/Scout.Host.Console');
    });

    it('appends .exe on win32', () => {
      const result = resolveWorkerBinary({
        platform: 'win32',
        arch: 'x64',
        cwd: '/repo/ui/packages/main/src/worker',
      });
      expect(posix(result)).toContain('build/worker/win-x64/Scout.Host.Console.exe');
    });

    it('treats isPackaged===false as dev mode', () => {
      const result = resolveWorkerBinary({
        app: { isPackaged: false },
        platform: 'linux',
        arch: 'x64',
        cwd: '/repo/ui/packages/main/src/worker',
      });
      expect(posix(result)).toContain('build/worker/linux-x64/Scout.Host.Console');
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
        path.join('/opt/scout/resources', 'worker', 'linux-x64', 'Scout.Host.Console'),
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
          'Scout.Host.Console',
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
      expect(result).toContain('Scout.Host.Console.exe');
    });
  });
});

describe('resolveMediaTools', () => {
  const packaged = {
    app: { isPackaged: true },
    resourcesPath: '/opt/Scout/resources',
    cwd: '/any',
  };

  it('finds the packaged Windows ffmpeg.exe of its platform and arch, and the regular Noto Sans', () => {
    const present = new Set([
      path.join('/opt/Scout/resources', 'media-tools', 'win32-x64', 'ffmpeg.exe'),
      path.join('/opt/Scout/resources', 'fonts', 'NotoSans-Regular.ttf'),
    ]);

    const tools = resolveMediaTools(
      { ...packaged, platform: 'win32', arch: 'x64' },
      (file) => present.has(file),
      () => [],
    );

    expect(tools).toMatchObject({
      ffmpeg: path.join('/opt/Scout/resources', 'media-tools', 'win32-x64', 'ffmpeg.exe'),
      font: path.join('/opt/Scout/resources', 'fonts', 'NotoSans-Regular.ttf'),
    });
  });

  it('finds the piper executable of its platform and the voices folder only when it holds a voice model, and answers empty paths otherwise', () => {
    const piperExe = (exe: string) =>
      path.join('/opt/Scout/resources', 'media-tools', 'linux-x64', 'piper', exe);
    const voices = path.join('/opt/Scout/resources', 'voices');

    const linux = resolveMediaTools(
      { ...packaged, platform: 'linux', arch: 'x64' },
      (file) => file === piperExe('piper'),
      () => ['LICENSES.txt', 'en_GB-cori-medium.onnx'],
    );
    const windows = resolveMediaTools(
      { ...packaged, platform: 'win32', arch: 'x64' },
      (file) =>
        file ===
        path.join('/opt/Scout/resources', 'media-tools', 'win32-x64', 'piper', 'piper.exe'),
      () => [],
    );

    expect([linux.piper, linux.voices]).toEqual([piperExe('piper'), voices]);
    expect([windows.piper.endsWith('piper.exe'), windows.voices]).toEqual([true, '']);
  });

  it('finds the rhubarb executable of its platform and the mouths folder only when it holds the nine mouth shapes', () => {
    const rhubarbExe = (target: string, exe: string) =>
      path.join('/opt/Scout/resources', 'media-tools', target, 'rhubarb', exe);
    const mouths = path.join('/opt/Scout/resources', 'mouths');
    const nine = ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'X'].map((shape) => `${shape}.png`);

    const linux = resolveMediaTools(
      { ...packaged, platform: 'linux', arch: 'x64' },
      (file) => file === rhubarbExe('linux-x64', 'rhubarb'),
      (dir) => (dir === mouths ? ['LICENSE.txt', 'VERSION', ...nine] : []),
    );
    const windows = resolveMediaTools(
      { ...packaged, platform: 'win32', arch: 'x64' },
      (file) => file === rhubarbExe('win32-x64', 'rhubarb.exe'),
      (dir) => (dir === mouths ? nine.filter((name) => name !== 'X.png') : []),
    );

    expect([linux.rhubarb, linux.mouths]).toEqual([rhubarbExe('linux-x64', 'rhubarb'), mouths]);
    expect([windows.rhubarb, windows.mouths]).toEqual([rhubarbExe('win32-x64', 'rhubarb.exe'), '']);
  });

  it("finds Scout's presenters folder only when every picture its presenters.json names is there, and the face models folder only with both models", () => {
    const presenters = path.join('/opt/Scout/resources', 'presenters');
    const manifest = path.join(presenters, 'presenters.json');
    const face = path.join('/opt/Scout/resources', 'media-tools', 'linux-x64', 'face');
    const listed = JSON.stringify([
      { id: 'ada', label: 'Ada', file: 'ada.png' },
      { id: 'kofi', label: 'Kofi', file: 'kofi.png' },
    ]);
    const resolve = (present: readonly string[], body = listed) =>
      resolveMediaTools(
        { ...packaged, platform: 'linux', arch: 'x64' },
        (file) => present.includes(file),
        () => [],
        () => body,
      );
    const everything = [
      manifest,
      path.join(presenters, 'ada.png'),
      path.join(presenters, 'kofi.png'),
      path.join(face, 'blazeface-short-range.onnx'),
      path.join(face, 'face-landmarker.onnx'),
    ];

    const complete = resolve(everything);
    const missingPicture = resolve(everything.filter((f) => !f.endsWith('kofi.png')));
    const noManifest = resolve(everything.filter((f) => f !== manifest));
    const unreadable = resolve(everything, '{ not json');
    const escaping = resolve(everything, JSON.stringify([{ id: 'x', file: '../x.png' }]));
    const empty = resolve(everything, '[]');
    const oneModel = resolve(everything.filter((f) => !f.endsWith('face-landmarker.onnx')));

    expect([complete.presenters, complete.faceModels]).toEqual([presenters, face]);
    expect([
      missingPicture.presenters,
      noManifest.presenters,
      unreadable.presenters,
      escaping.presenters,
      empty.presenters,
    ]).toEqual(['', '', '', '', '']);
    expect([oneModel.faceModels, oneModel.presenters]).toEqual(['', presenters]);
  });

  it('answers an empty path for every tool the build did not bundle, as on macOS', () => {
    const tools = resolveMediaTools(
      { ...packaged, platform: 'darwin', arch: 'arm64' },
      () => false,
    );

    expect(tools).toEqual({
      ffmpeg: '',
      font: '',
      piper: '',
      voices: '',
      rhubarb: '',
      mouths: '',
      tesseract: '',
      tessdata: '',
      presenters: '',
      faceModels: '',
    });
  });

  it('in development reads ui/packages/main/resources of the repo', () => {
    const repo = path.resolve(import.meta.dirname, '../../../..');
    const seen: string[] = [];

    resolveMediaTools(
      { platform: 'linux', arch: 'x64', cwd: path.join(repo, 'ui/packages/main/src') },
      (file) => {
        seen.push(file);
        return false;
      },
      () => [],
    );

    expect(seen.map(posix)).toEqual([
      posix(path.join(repo, 'ui/packages/main/resources/media-tools/linux-x64/ffmpeg')),
      posix(path.join(repo, 'ui/packages/main/resources/fonts/NotoSans-Regular.ttf')),
      posix(path.join(repo, 'ui/packages/main/resources/media-tools/linux-x64/piper/piper')),
      posix(path.join(repo, 'ui/packages/main/resources/media-tools/linux-x64/rhubarb/rhubarb')),
      posix(path.join(repo, 'ui/packages/main/resources/media-tools/linux-x64/tesseract/AppRun')),
      posix(path.join(repo, 'ui/packages/main/resources/media-tools/linux-x64/tesseract/tessdata')),
      posix(path.join(repo, 'ui/packages/main/resources/presenters/presenters.json')),
      posix(
        path.join(
          repo,
          'ui/packages/main/resources/media-tools/linux-x64/face/blazeface-short-range.onnx',
        ),
      ),
    ]);
  });
});
