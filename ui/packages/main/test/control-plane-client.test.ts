import { describe, it, expect, afterEach } from 'bun:test';
import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import { mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { ControlPlaneUnavailableError } from '@scout/shared';
import { createControlPlaneClient } from '../src/control-plane/client.js';
import type { ControlPlaneClient } from '../src/control-plane/client.js';
import { findRepoRoot } from '../src/worker/resolve-binary.js';

const PROTO_DIR = path.join(
  findRepoRoot(process.cwd()),
  'src',
  'Scout.ControlPlane.Contracts',
  'protos',
);
const TOKEN = 'cd'.repeat(32);

const tempDirs: string[] = [];

function tempSocketPath(): string {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'scout-cpc-'));
  tempDirs.push(dir);
  return path.join(dir, 'cp.sock');
}

function providersService(): grpc.ServiceDefinition {
  const definition = protoLoader.loadSync(path.join(PROTO_DIR, 'providers.proto'));
  const loaded = grpc.loadPackageDefinition(definition) as unknown as {
    scout: { controlplane: { v1: { Providers: grpc.ServiceClientConstructor } } };
  };
  return loaded.scout.controlplane.v1.Providers.service;
}

async function startServer(
  socketPath: string,
  received: grpc.Metadata[],
  respond = true,
): Promise<grpc.Server> {
  const server = new grpc.Server();
  server.addService(providersService(), {
    ListProviders: (
      call: grpc.ServerUnaryCall<unknown, unknown>,
      callback: grpc.sendUnaryData<unknown>,
    ) => {
      received.push(call.metadata);
      if (respond) callback(null, {});
    },
  });
  await new Promise<void>((resolve, reject) => {
    server.bindAsync(`unix:${socketPath}`, grpc.ServerCredentials.createInsecure(), (err) =>
      err ? reject(err) : resolve(),
    );
  });
  return server;
}

function dialError(platform?: NodeJS.Platform): unknown {
  try {
    createControlPlaneClient({
      socketPath: tempSocketPath(),
      token: TOKEN,
      protoDir: PROTO_DIR,
      ...(platform === undefined ? {} : { platform }),
    }).close();
    return null;
  } catch (err) {
    return err;
  }
}

let client: ControlPlaneClient | null = null;
let server: grpc.Server | null = null;

afterEach(() => {
  client?.close();
  server?.forceShutdown();
  client = null;
  server = null;
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

describe('createControlPlaneClient', () => {
  it('sends the session token as x-scout-token metadata over the Unix socket', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const received: grpc.Metadata[] = [];
    server = await startServer(socketPath, received);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const response = await client.listProviders();

    expect(response.providers).toEqual([]);
    expect(received).toHaveLength(1);
    expect(received[0]?.get('x-scout-token')).toEqual([TOKEN]);
  });

  it('rejects with ControlPlaneUnavailableError within the deadline when nothing listens on the socket', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    client = createControlPlaneClient({
      socketPath: tempSocketPath(),
      token: TOKEN,
      protoDir: PROTO_DIR,
      deadlineMs: 500,
    });

    const startedAt = Date.now();
    const error = await client.listProviders().then(
      () => null,
      (err: unknown) => err,
    );
    const elapsedMs = Date.now() - startedAt;

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(elapsedMs).toBeLessThan(5_000);
    expect(String(error)).not.toContain(TOKEN);
  });

  it('maps a call deadline on a live but silent host to ControlPlaneUnavailableError', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const received: grpc.Metadata[] = [];
    server = await startServer(socketPath, received, false);
    client = createControlPlaneClient({
      socketPath,
      token: TOKEN,
      protoDir: PROTO_DIR,
      deadlineMs: 300,
    });

    const error = await client.listProviders().then(
      () => null,
      (err: unknown) => err,
    );

    expect(received).toHaveLength(1);
    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(((error as Error).cause as grpc.ServiceError).code).toBe(grpc.status.DEADLINE_EXCEEDED);
  });

  it('refuses to dial on win32 and names named pipes', () => {
    const error = dialError('win32');

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect((error as Error).message).toContain('named pipe');
  });
});
