import { describe, it, expect, beforeAll, afterAll } from 'bun:test';
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync, statSync } from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { ControlPlaneNotImplementedError, ControlPlaneUnauthenticatedError } from '@scout/shared';
import { createControlPlaneClient } from '../src/control-plane/client.js';
import type { ControlPlaneClient } from '../src/control-plane/client.js';
import { launchControlPlane } from '../src/control-plane/launcher.js';
import type {
  LaunchedControlPlane,
  UnsupportedControlPlane,
} from '../src/control-plane/launcher.js';
import { findRepoRoot, resolveControlPlaneBinary } from '../src/worker/resolve-binary.js';
import type { Logger } from '../src/util/logger.js';

const CALL_DEADLINE_MS = 20_000;
const TEST_TIMEOUT_MS = 30_000;
const SOCKET_WAIT_MS = 5_000;
const PROTO_DIR = path.join(
  findRepoRoot(process.cwd()),
  'src',
  'Scout.ControlPlane.Contracts',
  'protos',
);

const nullLogger: Logger = {
  debug() {},
  info() {},
  warn() {},
  error() {},
  child() {
    return nullLogger;
  },
};

let result: LaunchedControlPlane | UnsupportedControlPlane;
const clients: ControlPlaneClient[] = [];
const socketDir = mkdtempSync(path.join(os.tmpdir(), 'scout-cpi-'));
const featureStateFile = path.join(socketDir, 'features.json');

function developerFeatureStateFile(): string {
  if (process.platform === 'darwin') {
    return path.join(os.homedir(), 'Library', 'Application Support', 'Scout', 'features.json');
  }
  const dataHome = process.env['XDG_DATA_HOME'];
  const root =
    dataHome != null && dataHome !== '' ? dataHome : path.join(os.homedir(), '.local', 'share');
  return path.join(root, 'Scout', 'features.json');
}

function snapshotOf(file: string): number | null {
  return statSync(file, { throwIfNoEntry: false })?.mtimeMs ?? null;
}

const developerStateBefore = snapshotOf(developerFeatureStateFile());

function connect(token: string): ControlPlaneClient {
  if (result.kind !== 'launched') throw new Error('control-plane host was not launched');
  const client = createControlPlaneClient({
    socketPath: result.socketPath,
    token,
    protoDir: PROTO_DIR,
    deadlineMs: CALL_DEADLINE_MS,
  });
  clients.push(client);
  return client;
}

beforeAll(async () => {
  const binary = resolveControlPlaneBinary({
    platform: process.platform,
    arch: process.arch,
    cwd: process.cwd(),
  });
  if (process.platform !== 'win32' && !existsSync(binary)) {
    throw new Error(
      `Scout.Host.ControlPlane binary not found at ${binary}. Run 'dotnet build src/Scout.Host.ControlPlane' first.`,
    );
  }
  result = launchControlPlane({
    spawn,
    resolveBinary: () => binary,
    logger: nullLogger,
    platform: process.platform,
    socketDir,
    extraEnv: {
      Scout__Providers__Ollama__PingTimeout: '00:00:00.100',
      Scout__Features__StateStore__FilePath: featureStateFile,
    },
  });
  if (result.kind !== 'launched') return;
  const waitUntil = Date.now() + SOCKET_WAIT_MS;
  while (!existsSync(result.socketPath)) {
    if (Date.now() > waitUntil) {
      throw new Error(
        `control-plane socket ${result.socketPath} did not appear within ${SOCKET_WAIT_MS} ms`,
      );
    }
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}, TEST_TIMEOUT_MS);

afterAll(async () => {
  for (const client of clients) client.close();
  if (result.kind === 'launched') {
    const { child } = result;
    if (child.exitCode === null && child.signalCode === null) {
      const exited = new Promise<void>((resolve) => child.once('exit', () => resolve()));
      result.dispose();
      await exited;
    }
  }
  rmSync(socketDir, { recursive: true, force: true });
  expect(existsSync(socketDir)).toBe(false);
});

describe('control plane over UDS', () => {
  it(
    'ListProviders round-trips over UDS against the real Scout.Host.ControlPlane binary',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }

      const response = await connect((result as LaunchedControlPlane).token).listProviders();

      expect(response.providers.map((provider) => provider.id)).toEqual(['OLLAMA', 'LM_STUDIO']);
    },
    TEST_TIMEOUT_MS,
  );

  it(
    'a client with a wrong token is rejected as UNAUTHENTICATED',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }

      const error = await connect('0'.repeat(64))
        .listProviders()
        .then(
          () => null,
          (err: unknown) => err,
        );

      expect(error).toBeInstanceOf(ControlPlaneUnauthenticatedError);
    },
    TEST_TIMEOUT_MS,
  );

  it(
    'ListFeatures returns github and kubernetes from the real Scout.Host.ControlPlane binary',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }

      const features = await connect((result as LaunchedControlPlane).token).listFeatures();

      expect(features.map((f) => [f.id, f.displayName])).toEqual([
        ['github', 'GitHub'],
        ['kubernetes', 'Kubernetes'],
      ]);
      expect(features[0]?.credentialFields.map((f) => f.key)).toEqual(['pat']);
    },
    TEST_TIMEOUT_MS,
  );

  it(
    'SetCredentials with a pat moves github from needs-credentials to disabled, and Enable says it is not available yet',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      const before = (await client.listFeatures()).find((f) => f.id === 'github');

      const saved = await client.setFeatureCredentials('github', { pat: 'ghp_integration' });
      const enableError = await client.enableFeature('github').then(
        () => null,
        (err: unknown) => err,
      );

      expect([before?.hasCredentials, before?.status]).toEqual([false, 'needs-credentials']);
      expect([saved.hasCredentials, saved.status]).toEqual([true, 'disabled']);
      expect(enableError).toBeInstanceOf(ControlPlaneNotImplementedError);
    },
    TEST_TIMEOUT_MS,
  );

  it(
    "feature state is written to the test's temp file and never to the developer's features.json",
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }

      const disabled = await connect((result as LaunchedControlPlane).token).disableFeature(
        'github',
      );

      expect(disabled.enabled).toBe(false);
      expect(existsSync(featureStateFile)).toBe(true);
      expect(snapshotOf(developerFeatureStateFile())).toBe(developerStateBefore);
    },
    TEST_TIMEOUT_MS,
  );
});

function freeTcpPort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address() as net.AddressInfo;
      server.close(() => resolve(port));
    });
  });
}

function tcpConnects(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = net.connect(port, '127.0.0.1');
    socket.once('connect', () => {
      socket.destroy();
      resolve(true);
    });
    socket.once('error', () => resolve(false));
  });
}

describe('control-plane host environment', () => {
  it(
    'an inherited Kestrel endpoint does not make the real host listen on TCP',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const port = await freeTcpPort();
      const dir = mkdtempSync(path.join(os.tmpdir(), 'scout-cpe-'));
      process.env['Kestrel__Endpoints__Http__Url'] = `http://127.0.0.1:${port}`;
      const launched = launchControlPlane({
        spawn,
        resolveBinary: () =>
          resolveControlPlaneBinary({
            platform: process.platform,
            arch: process.arch,
            cwd: process.cwd(),
          }),
        logger: nullLogger,
        platform: process.platform,
        socketDir: dir,
        extraEnv: { Scout__Features__StateStore__FilePath: path.join(dir, 'features.json') },
      });
      delete process.env['Kestrel__Endpoints__Http__Url'];
      try {
        if (launched.kind !== 'launched') throw new Error('control-plane host was not launched');
        const client = createControlPlaneClient({
          socketPath: launched.socketPath,
          token: launched.token,
          protoDir: PROTO_DIR,
          deadlineMs: CALL_DEADLINE_MS,
        });
        const features = await client.listFeatures();
        client.close();

        expect(features.length).toBe(2);
        expect(await tcpConnects(port)).toBe(false);
      } finally {
        if (launched.kind === 'launched') {
          const exited = new Promise<void>((resolve) =>
            launched.child.once('exit', () => resolve()),
          );
          launched.dispose();
          await exited;
        }
        rmSync(dir, { recursive: true, force: true });
      }
    },
    TEST_TIMEOUT_MS,
  );
});
