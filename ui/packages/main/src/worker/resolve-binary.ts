import path from 'node:path';
import fs from 'node:fs';
import type { App } from 'electron';

type AppSubset = Pick<App, 'isPackaged'>;

export interface ResolveBinaryDeps {
  app?: AppSubset;
  resourcesPath?: string;
  platform: NodeJS.Platform;
  arch: NodeJS.Architecture;
  cwd: string;
}

function ridFromPlatformArch(platform: NodeJS.Platform, arch: NodeJS.Architecture): string {
  if (platform === 'win32') return 'win-x64';
  if (platform === 'darwin') return arch === 'arm64' ? 'osx-arm64' : 'osx-x64';
  return 'linux-x64';
}

function binaryName(platform: NodeJS.Platform): string {
  return platform === 'win32' ? 'Scout.Host.Console.exe' : 'Scout.Host.Console';
}

export function findRepoRoot(startDir: string): string {
  let dir = startDir;
  for (let i = 0; i < 20; i++) {
    if (fs.existsSync(path.join(dir, 'ui', 'build', 'worker'))) return dir;
    if (fs.existsSync(path.join(dir, 'Scout.slnx'))) return dir;
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  // Fallback: assume cwd is inside ui/packages/main/src/worker or similar
  return path.resolve(startDir, '../../..');
}

export function resolveWorkerBinary(deps: ResolveBinaryDeps): string {
  const { app, resourcesPath, platform, arch, cwd } = deps;
  const rid = ridFromPlatformArch(platform, arch);
  const binary = binaryName(platform);

  if (app?.isPackaged === true && resourcesPath != null) {
    return path.join(resourcesPath, 'worker', rid, binary);
  }

  const repoRoot = findRepoRoot(cwd);
  return path.join(repoRoot, 'ui', 'build', 'worker', rid, binary);
}

export function resolveControlPlaneBinary(
  deps: ResolveBinaryDeps,
  env: NodeJS.ProcessEnv = process.env,
): string {
  const override = env['SCOUT_CONTROLPLANE_BIN'];
  if (override != null && override !== '') return override;

  const { app, resourcesPath, platform, arch, cwd } = deps;
  const binary = platform === 'win32' ? 'Scout.Host.ControlPlane.exe' : 'Scout.Host.ControlPlane';

  if (app?.isPackaged === true && resourcesPath != null) {
    return path.join(resourcesPath, 'worker', ridFromPlatformArch(platform, arch), binary);
  }

  const bin = path.join(findRepoRoot(cwd), 'src', 'Scout.Host.ControlPlane', 'bin');
  const release = path.join(bin, 'Release', 'net10.0', binary);
  return fs.existsSync(release) ? release : path.join(bin, 'Debug', 'net10.0', binary);
}

export function resolveProtoDir(
  deps: Pick<ResolveBinaryDeps, 'app' | 'resourcesPath' | 'cwd'>,
): string {
  const { app, resourcesPath, cwd } = deps;
  if (app?.isPackaged === true && resourcesPath != null) {
    return path.join(resourcesPath, 'protos');
  }
  return path.join(findRepoRoot(cwd), 'src', 'Scout.ControlPlane.Contracts', 'protos');
}

/** The tray icon: shipped under `resources/icons` when packaged, read from `ui/build/icons` otherwise. */
export function resolveTrayIcon(
  deps: Pick<ResolveBinaryDeps, 'app' | 'resourcesPath' | 'cwd'>,
): string {
  const { app, resourcesPath, cwd } = deps;
  if (app?.isPackaged === true && resourcesPath != null) {
    return path.join(resourcesPath, 'icons', 'icon.png');
  }
  return path.join(findRepoRoot(cwd), 'ui', 'build', 'icons', 'icon.png');
}

/**
 * The ffmpeg, caption font, piper, voices folder, rhubarb, mouth shapes folder, tesseract and its language data
 * folder the worker works with; an empty path for any the build did not bundle.
 */
export interface MediaToolPaths {
  readonly ffmpeg: string;
  readonly font: string;
  readonly piper: string;
  readonly voices: string;
  readonly rhubarb: string;
  readonly mouths: string;
  readonly tesseract: string;
  readonly tessdata: string;
}

/** The nine mouth shapes rhubarb names, one PNG each. */
const MOUTH_SHAPES = ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'X'].map((shape) => `${shape}.png`);

/**
 * The bundled media tools: under `resources/media-tools/<platform>-<arch>` and `resources/fonts` when packaged, read
 * from `ui/packages/main/resources` otherwise; each one only when its file or folder is there, the voices folder only
 * when it holds a voice model, the mouths folder only when it holds the nine mouth shapes.
 */
export function resolveMediaTools(
  deps: Omit<ResolveBinaryDeps, 'platform' | 'arch'> &
    Partial<Pick<ResolveBinaryDeps, 'platform' | 'arch'>>,
  exists: (file: string) => boolean = fs.existsSync,
  list: (dir: string) => string[] = (dir) => (fs.existsSync(dir) ? fs.readdirSync(dir) : []),
): MediaToolPaths {
  const { app, resourcesPath, cwd, platform = process.platform, arch = process.arch } = deps;
  const root =
    app?.isPackaged === true && resourcesPath != null
      ? resourcesPath
      : path.join(findRepoRoot(cwd), 'ui', 'packages', 'main', 'resources');
  const exe = platform === 'win32' ? '.exe' : '';
  const tools = path.join(root, 'media-tools', `${platform}-${arch}`);
  const voices = path.join(root, 'voices');
  const mouths = path.join(root, 'mouths');
  const present = (file: string): string => (exists(file) ? file : '');
  const shapes = list(mouths);
  return {
    ffmpeg: present(path.join(tools, `ffmpeg${exe}`)),
    font: present(path.join(root, 'fonts', 'NotoSans-Regular.ttf')),
    piper: present(path.join(tools, 'piper', `piper${exe}`)),
    voices: list(voices).some((name) => name.endsWith('.onnx')) ? voices : '',
    rhubarb: present(path.join(tools, 'rhubarb', `rhubarb${exe}`)),
    mouths: MOUTH_SHAPES.every((shape) => shapes.includes(shape)) ? mouths : '',
    tesseract: present(path.join(tools, 'tesseract', 'AppRun')),
    tessdata: present(path.join(tools, 'tesseract', 'tessdata')),
  };
}
