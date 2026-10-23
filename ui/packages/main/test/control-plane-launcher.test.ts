import { describe, it, expect, mock, afterEach } from 'bun:test';
import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import { spawn as nodeSpawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { chmodSync, mkdirSync, mkdtempSync, rmSync, statSync, symlinkSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { ControlPlaneUnavailableError } from '@scout/shared';
import { connectControlPlane, launchControlPlane } from '../src/control-plane/launcher.js';
import type { ControlPlaneLauncherDeps } from '../src/control-plane/launcher.js';
import type { Logger } from '../src/util/logger.js';
import { findRepoRoot, resolveControlPlaneBinary } from '../src/worker/resolve-binary.js';

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

  it('drops inherited variables that would move the listener or override the socket and token', () => {
    const hostile: Record<string, string> = {
      scout__controlplane__socketpath: '/evil',
      'Scout:ControlPlane:SocketPath': '/evil2',
      scout_controlplane_token: 'f'.repeat(64),
      DOTNET_Scout__ControlPlane__SocketPath: '/evil3',
      Kestrel__Endpoints__Http__Url: 'http://0.0.0.0:5000',
      'kestrel:Endpoints:Https:Url': 'https://0.0.0.0:5001',
      ASPNETCORE_Kestrel__Endpoints__Other__Url: 'http://0.0.0.0:5002',
      ASPNETCORE_URLS: 'http://*:80',
      ASPNETCORE_HTTP_PORTS: '8080',
      ASPNETCORE_HTTPS_PORTS: '8443',
      DOTNET_URLS: 'http://*:81',
      URLS: 'http://*:82',
    };
    const kept = { ASPNETCORE_ENVIRONMENT: 'Development', SCOUT_TEST_KEEP: 'kept' };
    const saved = { ...process.env };
    Object.assign(process.env, hostile, kept);
    try {
      const { deps, calls } = makeDeps({ mintToken: () => TOKEN });

      const launched = launchControlPlane(deps);

      if (launched.kind !== 'launched') throw new Error(`expected a launch, got ${launched.kind}`);
      const env = calls[0]!.env;
      expect(Object.keys(hostile).filter((key) => key in env)).toEqual([]);
      expect(env['Scout__ControlPlane__SocketPath']).toBe(launched.socketPath);
      expect(env['SCOUT_CONTROLPLANE_TOKEN']).toBe(TOKEN);
      expect([env['ASPNETCORE_ENVIRONMENT'], env['SCOUT_TEST_KEEP']]).toEqual([
        'Development',
        'kept',
      ]);
    } finally {
      for (const key of Object.keys(process.env)) if (!(key in saved)) delete process.env[key];
      Object.assign(process.env, saved);
    }
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

describe('connectControlPlane', () => {
  const PROTO_DIR = '/unused/protos';

  async function rejectionOf(promise: Promise<unknown>): Promise<unknown> {
    return promise.then(
      () => null,
      (err: unknown) => err,
    );
  }

  it('a missing host binary leaves the control plane unavailable without a crash and without logging the token', async () => {
    if (process.platform === 'win32') {
      const { deps } = makeDeps({ platform: 'win32' });
      expect(
        await rejectionOf(connectControlPlane({ ...deps, protoDir: PROTO_DIR }).client),
      ).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const { deps, lines } = makeDeps({
      spawn: nodeSpawn,
      resolveBinary: () => path.join(os.tmpdir(), 'scout-missing', 'Scout.Host.ControlPlane'),
      mintToken: () => TOKEN,
    });

    const startedAt = Date.now();
    const connection = connectControlPlane({ ...deps, protoDir: PROTO_DIR, socketWaitMs: 2_000 });
    const error = await rejectionOf(connection.client);
    connection.dispose();

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(Date.now() - startedAt).toBeLessThan(2_000);
    expect(
      lines.some((line) => line.startsWith('warn') && line.includes('control-plane: unavailable')),
    ).toBe(true);
    expect(lines.filter((line) => line.includes(TOKEN))).toEqual([]);
  });

  it('a host that exits before opening its socket leaves the control plane unavailable', async () => {
    const { deps, child } = makeDeps();

    const connection = connectControlPlane({ ...deps, protoDir: PROTO_DIR, socketWaitMs: 5_000 });
    setTimeout(() => child.emit('exit', 1, null), 10);
    const error = await rejectionOf(connection.client);

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect((error as Error).message).toContain('exited before it opened its socket');
  });

  it('a socket that never appears leaves the control plane unavailable after the bounded wait', async () => {
    const { deps } = makeDeps();

    const connection = connectControlPlane({ ...deps, protoDir: PROTO_DIR, socketWaitMs: 100 });
    const error = await rejectionOf(connection.client);
    connection.dispose();

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect((error as Error).message).toContain('did not appear within 100 ms');
  });

  it('a socket wait that times out kills the host it launched', async () => {
    const { deps, child } = makeDeps();

    const error = await rejectionOf(
      connectControlPlane({ ...deps, protoDir: PROTO_DIR, socketWaitMs: 50 }).client,
    );

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(child.killed).toBe(true);
  });

  describe('with a host listening on the socket', () => {
    const REAL_PROTO_DIR = path.join(
      findRepoRoot(process.cwd()),
      'src',
      'Scout.ControlPlane.Contracts',
      'protos',
    );
    let server: grpc.Server | null = null;

    afterEach(() => {
      server?.forceShutdown();
      server = null;
    });

    async function connectToListeningHost() {
      const made = makeDeps();
      const socketPath = path.join(made.socketDir, `controlplane-${process.pid}.sock`);
      const definition = protoLoader.loadSync(path.join(REAL_PROTO_DIR, 'features.proto'));
      const loaded = grpc.loadPackageDefinition(definition) as unknown as {
        scout: { controlplane: { v1: { Features: grpc.ServiceClientConstructor } } };
      };
      server = new grpc.Server();
      server.addService(loaded.scout.controlplane.v1.Features.service, {
        ListFeatures: (_call: unknown, callback: grpc.sendUnaryData<unknown>) =>
          callback(null, { features: [] }),
      });
      const connection = connectControlPlane({
        ...made.deps,
        protoDir: REAL_PROTO_DIR,
        socketWaitMs: 5_000,
      });
      await new Promise<void>((resolve, reject) => {
        server!.bindAsync(`unix:${socketPath}`, grpc.ServerCredentials.createInsecure(), (err) =>
          err ? reject(err) : resolve(),
        );
      });
      const client = await connection.client;
      expect(await client.listFeatures()).toEqual([]);
      return { connection, client, child: made.child };
    }

    it('a host that exits mid-session makes the next Features call fail fast as unavailable', async () => {
      if (process.platform === 'win32') return;
      const { client, child } = await connectToListeningHost();

      child.emit('exit', 1, null);
      const startedAt = Date.now();
      const error = await rejectionOf(client.listFeatures());

      expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
      expect(Date.now() - startedAt).toBeLessThan(1_000);
    });

    it('dispose kills the host and closes the client', async () => {
      if (process.platform === 'win32') return;
      const { connection, client, child } = await connectToListeningHost();

      connection.dispose();
      const error = await rejectionOf(client.listFeatures());

      expect(child.killed).toBe(true);
      expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    });
  });

  it('a launch that throws becomes ControlPlaneUnavailableError instead of escaping', async () => {
    const { deps, calls } = makeDeps({ socketDir: path.join(os.tmpdir(), 'd'.repeat(100)) });

    const error = await rejectionOf(connectControlPlane({ ...deps, protoDir: PROTO_DIR }).client);

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect((error as Error).message).toContain('100 characters');
    expect(calls).toHaveLength(0);
  });

  it('win32 is unavailable and names named pipes', async () => {
    const { deps, calls } = makeDeps({ platform: 'win32' });

    const error = await rejectionOf(connectControlPlane({ ...deps, protoDir: PROTO_DIR }).client);

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect((error as Error).message).toContain('named pipe');
    expect(calls).toHaveLength(0);
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
