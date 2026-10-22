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
