import { describe, it, expect, mock, afterEach } from 'bun:test';
import { EventEmitter } from 'node:events';
import { chmodSync, mkdirSync, mkdtempSync, rmSync, statSync, symlinkSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { launchControlPlane } from '../src/control-plane/launcher.js';
import type { ControlPlaneLauncherDeps } from '../src/control-plane/launcher.js';
import type { Logger } from '../src/util/logger.js';
import { resolveControlPlaneBinary } from '../src/worker/resolve-binary.js';

interface FakeChild extends EventEmitter {
  pid: number;
  kill: (signal?: string) => boolean;
  killed: boolean;
}

function makeChild(pid = 4242): FakeChild {
  const child = new EventEmitter() as FakeChild;
  child.pid = pid;
  child.killed = false;
  child.kill = () => {
    child.killed = true;
    return true;
  };
  return child;
}

interface SpawnCall {
  command: string;
  env: Record<string, string | undefined>;
}

function makeFakeSpawn(child: FakeChild) {
  const calls: SpawnCall[] = [];
  const spawn = mock((command: string, _args: string[], options: { env: SpawnCall['env'] }) => {
    calls.push({ command, env: options.env });
    return child as never;
  });
  return { spawn, calls };
}

function makeRecordingLogger(): { logger: Logger; lines: string[] } {
  const lines: string[] = [];
  const record =
    (level: string) =>
    (...args: unknown[]): void => {
      lines.push(
        `${level} ${JSON.stringify(args, (_k, v: unknown) => (v instanceof Error ? { name: v.name, message: v.message } : v))}`,
      );
    };
  const logger: Logger = {
    debug: record('debug'),
    info: record('info'),
    warn: record('warn'),
    error: record('error'),
    child() {
      return logger;
    },
  };
  return { logger, lines };
}

const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

const BINARY = '/fake/bin/Scout.Host.ControlPlane';
const TOKEN = 'ab'.repeat(32);

function makeDeps(overrides: Partial<ControlPlaneLauncherDeps> = {}) {
  const child = makeChild();
  const { spawn, calls } = makeFakeSpawn(child);
  const { logger, lines } = makeRecordingLogger();
  const socketDir = mkdtempSync(path.join(os.tmpdir(), 'scout-cpl-'));
  tempDirs.push(socketDir);
  const deps: ControlPlaneLauncherDeps = {
    spawn: spawn as unknown as ControlPlaneLauncherDeps['spawn'],
    resolveBinary: () => BINARY,
    logger,
    platform: 'linux',
    socketDir,
    ...overrides,
  };
  return { deps, child, calls, lines, socketDir };
}

describe('launchControlPlane', () => {
  it('spawns the control-plane host with a fresh session token and an explicit socket path in its environment', () => {
    const { deps, child, calls, socketDir } = makeDeps();

    const launched = launchControlPlane(deps);

    if (launched.kind !== 'launched') throw new Error(`expected a launch, got ${launched.kind}`);
    expect(calls).toHaveLength(1);
    expect(calls[0]?.command).toBe(BINARY);
    expect(launched.token).toMatch(/^[0-9a-f]{64}$/);
    expect(calls[0]?.env['SCOUT_CONTROLPLANE_TOKEN']).toBe(launched.token);
    expect(launched.socketPath.startsWith(socketDir + path.sep)).toBe(true);
    expect(launched.socketPath.length).toBeLessThan(100);
    expect(calls[0]?.env['Scout__ControlPlane__SocketPath']).toBe(launched.socketPath);
    expect(launched.child).toBe(child as never);

    const second = launchControlPlane(makeDeps().deps);
    if (second.kind !== 'launched') throw new Error(`expected a launch, got ${second.kind}`);
    expect(second.token).not.toBe(launched.token);

    launched.dispose();
    expect(child.killed).toBe(true);
  });

  it('never logs the session token', () => {
    const { deps, child, lines } = makeDeps({ mintToken: () => TOKEN });

    const launched = launchControlPlane(deps);
    child.emit('error', new Error('spawn failed'));
    child.emit('exit', 1, null);
    if (launched.kind === 'launched') launched.dispose();

    expect(launched.kind).toBe('launched');
    expect(lines.length).toBeGreaterThanOrEqual(3);
    expect(lines.filter((line) => line.includes(TOKEN))).toEqual([]);
  });

  it('refuses a socket path of 100 characters or more', () => {
    const { deps, calls } = makeDeps({ socketDir: path.join(os.tmpdir(), 'd'.repeat(100)) });

    expect(() => launchControlPlane(deps)).toThrow(/100 characters/);
    expect(calls).toHaveLength(0);
  });

  it('refuses a socket directory that is a symlink', () => {
    if (process.platform === 'win32') {
      const { deps, calls } = makeDeps();
      expect(launchControlPlane({ ...deps, platform: 'win32' }).kind).toBe('unsupported');
      expect(calls).toHaveLength(0);
      return;
    }

    const parent = mkdtempSync(path.join(os.tmpdir(), 'scout-cpl-'));
    tempDirs.push(parent);
    const realDir = path.join(parent, 'real');
    mkdirSync(realDir, { mode: 0o700 });
    const linkDir = path.join(parent, 'link');
    symlinkSync(realDir, linkDir);
    const { deps, calls } = makeDeps({ socketDir: linkDir });

    expect(() => launchControlPlane(deps)).toThrow(/symbolic link/);
    expect(calls).toHaveLength(0);
  });

  it('refuses a socket directory owned by another uid', () => {
    if (process.platform === 'win32') {
      const { deps, calls } = makeDeps();
      expect(launchControlPlane({ ...deps, platform: 'win32' }).kind).toBe('unsupported');
      expect(calls).toHaveLength(0);
      return;
    }

    const socketDir = mkdtempSync(path.join(os.tmpdir(), 'scout-cpl-'));
    tempDirs.push(socketDir);
    chmodSync(socketDir, 0o755);
    const { uid, mode } = statSync(socketDir);
    const { deps, calls } = makeDeps({ socketDir, getUid: () => uid + 1 });

    expect(() => launchControlPlane(deps)).toThrow(/owned by uid/);
    expect(calls).toHaveLength(0);
    expect(statSync(socketDir).mode).toBe(mode);
  });

  it('tightens a pre-existing world-readable socket directory to 0700', () => {
    if (process.platform === 'win32') {
      const { deps, calls } = makeDeps();
      expect(launchControlPlane({ ...deps, platform: 'win32' }).kind).toBe('unsupported');
      expect(calls).toHaveLength(0);
      return;
    }

    const parent = mkdtempSync(path.join(os.tmpdir(), 'scout-cpl-'));
    tempDirs.push(parent);
    const socketDir = path.join(parent, 'open');
    mkdirSync(socketDir);
    chmodSync(socketDir, 0o777);
    const { deps, calls } = makeDeps({ socketDir });

    launchControlPlane(deps);

    expect(statSync(socketDir).mode & 0o777).toBe(0o700);
    expect(calls).toHaveLength(1);
  });

  it('passes extraEnv to the host and keeps its own socket and token variables', () => {
    const { deps, calls } = makeDeps({
      mintToken: () => TOKEN,
      extraEnv: {
        SCOUT_CONTROLPLANE_TOKEN: 'x',
        Scout__ControlPlane__SocketPath: '/elsewhere.sock',
        Scout__Providers__Ollama__PingTimeout: '00:00:00.100',
      },
    });

    const launched = launchControlPlane(deps);

    if (launched.kind !== 'launched') throw new Error(`expected a launch, got ${launched.kind}`);
    expect(calls[0]?.env['SCOUT_CONTROLPLANE_TOKEN']).toBe(TOKEN);
    expect(calls[0]?.env['Scout__ControlPlane__SocketPath']).toBe(launched.socketPath);
    expect(calls[0]?.env['Scout__Providers__Ollama__PingTimeout']).toBe('00:00:00.100');
  });

  it('reports unsupported on win32 instead of spawning', () => {
    const { deps, calls } = makeDeps({ platform: 'win32' });

    const result = launchControlPlane(deps);

    expect(calls).toHaveLength(0);
    expect(result.kind).toBe('unsupported');
    if (result.kind !== 'unsupported') throw new Error('expected unsupported');
    expect(result.reason).toContain('named pipe');
  });
});

describe('resolveControlPlaneBinary', () => {
  it('uses SCOUT_CONTROLPLANE_BIN as the host binary when it is set', () => {
    const resolved = resolveControlPlaneBinary(
      { platform: 'linux', arch: 'x64', cwd: process.cwd() },
      { SCOUT_CONTROLPLANE_BIN: '/opt/custom/Scout.Host.ControlPlane' },
    );

    expect(resolved).toBe('/opt/custom/Scout.Host.ControlPlane');
  });
});
