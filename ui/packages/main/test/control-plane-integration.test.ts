import { describe, it, expect, beforeAll, afterAll } from 'bun:test';
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { ControlPlaneUnauthenticatedError } from '@scout/shared';
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
    extraEnv: { Scout__Providers__Ollama__PingTimeout: '00:00:00.100' },
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
});
