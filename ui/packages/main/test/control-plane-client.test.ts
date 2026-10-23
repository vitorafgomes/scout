import { describe, it, expect, afterEach } from 'bun:test';
import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import { mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  ControlPlaneNotImplementedError,
  ControlPlaneUnavailableError,
  type FeatureDto,
} from '@scout/shared';
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

interface FeaturesCall {
  method: string;
  request: unknown;
  token: grpc.MetadataValue[];
}

const githubWire = {
  id: 'github',
  displayName: 'GitHub',
  description: 'Query repositories.',
  requiresCredentials: true,
  hasCredentials: false,
  enabled: false,
  status: 'NEEDS_CREDENTIALS',
  credentialFields: [
    {
      key: 'pat',
      kind: 'password',
      label: 'Personal Access Token',
      helperText: 'Required scopes: repo.',
      createLinkUrl: 'https://github.com/settings/tokens/new',
      createLinkLabel: 'Create token on GitHub',
    },
  ],
  settingsFields: [
    { key: 'default_repo_url', kind: 'text', label: 'Default repository URL', required: true },
  ],
  settings: { default_repo_url: 'https://github.com/acme/repo' },
  scopeMatrix: [{ scope: 'repo', actions: ['Read pull requests'] }],
  hasScopeMatrix: true,
  setupSteps: ['Create a token.'],
  hasSetupSteps: true,
};

const kubernetesWire = {
  id: 'kubernetes',
  displayName: 'Kubernetes',
  description: 'Describe pods.',
  requiresCredentials: true,
  hasCredentials: true,
  enabled: false,
  status: 'DISABLED',
  lastError: 'cluster unreachable',
  credentialFields: [{ key: 'context', kind: 'text', label: 'Active context (optional)' }],
  settingsFields: [],
  settings: {},
  scopeMatrix: [],
  hasScopeMatrix: false,
  setupSteps: [],
  hasSetupSteps: false,
};

const githubDto: FeatureDto = {
  id: 'github',
  displayName: 'GitHub',
  description: 'Query repositories.',
  requiresCredentials: true,
  hasCredentials: false,
  enabled: false,
  status: 'needs-credentials',
  lastError: null,
  credentialFields: [
    {
      key: 'pat',
      kind: 'password',
      label: 'Personal Access Token',
      helperText: 'Required scopes: repo.',
      createLinkUrl: 'https://github.com/settings/tokens/new',
      createLinkLabel: 'Create token on GitHub',
    },
  ],
  settingsFields: [
    {
      key: 'default_repo_url',
      kind: 'text',
      label: 'Default repository URL',
      helperText: null,
      createLinkUrl: null,
      createLinkLabel: null,
      required: true,
    },
  ],
  settings: { default_repo_url: 'https://github.com/acme/repo' },
  scopeMatrix: [{ scope: 'repo', actions: ['Read pull requests'] }],
  setupSteps: ['Create a token.'],
};

const kubernetesDto: FeatureDto = {
  id: 'kubernetes',
  displayName: 'Kubernetes',
  description: 'Describe pods.',
  requiresCredentials: true,
  hasCredentials: true,
  enabled: false,
  status: 'disabled',
  lastError: 'cluster unreachable',
  credentialFields: [
    {
      key: 'context',
      kind: 'text',
      label: 'Active context (optional)',
      helperText: null,
      createLinkUrl: null,
      createLinkLabel: null,
    },
  ],
  settingsFields: [],
  settings: {},
  scopeMatrix: null,
  setupSteps: null,
};

async function startFeaturesServer(
  socketPath: string,
  calls: FeaturesCall[],
  enableStatus?: grpc.status,
): Promise<grpc.Server> {
  const definition = protoLoader.loadSync(path.join(PROTO_DIR, 'features.proto'), {
    enums: String,
    defaults: true,
  });
  const loaded = grpc.loadPackageDefinition(definition) as unknown as {
    scout: { controlplane: { v1: { Features: grpc.ServiceClientConstructor } } };
  };
  const handler =
    (method: string, response: () => unknown, failWith?: grpc.status) =>
    (call: grpc.ServerUnaryCall<unknown, unknown>, callback: grpc.sendUnaryData<unknown>) => {
      calls.push({ method, request: call.request, token: call.metadata.get('x-scout-token') });
      if (failWith === undefined) callback(null, response());
      else callback({ code: failWith, details: 'not wired' }, null);
    };
  const server = new grpc.Server();
  server.addService(loaded.scout.controlplane.v1.Features.service, {
    ListFeatures: handler('ListFeatures', () => ({ features: [githubWire, kubernetesWire] })),
    SetCredentials: handler('SetCredentials', () => githubWire),
    Setup: handler('Setup', () => githubWire),
    DeleteCredentials: handler('DeleteCredentials', () => githubWire),
    Enable: handler('Enable', () => githubWire, enableStatus),
    Disable: handler('Disable', () => githubWire),
  });
  await new Promise<void>((resolve, reject) => {
    server.bindAsync(`unix:${socketPath}`, grpc.ServerCredentials.createInsecure(), (err) =>
      err ? reject(err) : resolve(),
    );
  });
  return server;
}

describe('createControlPlaneClient features', () => {
  it('listFeatures maps the wire features to the renderer DTOs exactly', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: FeaturesCall[] = [];
    server = await startFeaturesServer(socketPath, calls);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const features = await client.listFeatures();

    expect(features).toEqual([githubDto, kubernetesDto]);
    expect(calls.map((c) => c.token)).toEqual([[TOKEN]]);
  });

  it('enable, disable, setCredentials, setup and deleteCredentials send the feature id, the body and the token', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: FeaturesCall[] = [];
    server = await startFeaturesServer(socketPath, calls);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const enabled = await client.enableFeature('github');
    await client.disableFeature('kubernetes');
    await client.setFeatureCredentials('github', { pat: 'ghp_abc' });
    await client.setupFeature(
      'github',
      { pat: 'ghp_abc' },
      { default_repo_url: 'https://github.com/acme/repo', old_key: null },
    );
    await client.deleteFeatureCredentials('github');

    expect(enabled).toEqual(githubDto);
    expect(calls.map(({ method, request }) => ({ method, request }))).toEqual([
      { method: 'Enable', request: { id: 'github' } },
      { method: 'Disable', request: { id: 'kubernetes' } },
      { method: 'SetCredentials', request: { id: 'github', credentials: { pat: 'ghp_abc' } } },
      {
        method: 'Setup',
        request: {
          id: 'github',
          credentials: { pat: 'ghp_abc' },
          settings: { default_repo_url: 'https://github.com/acme/repo' },
          clearedSettings: ['old_key'],
        },
      },
      { method: 'DeleteCredentials', request: { id: 'github' } },
    ]);
    expect(calls.every((c) => c.token.length === 1 && c.token[0] === TOKEN)).toBe(true);
  });

  it('an UNIMPLEMENTED Enable rejects with ControlPlaneNotImplementedError, not the raw gRPC text', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    server = await startFeaturesServer(socketPath, [], grpc.status.UNIMPLEMENTED);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const error = await client.enableFeature('github').then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneNotImplementedError);
    expect((error as Error).message).toBe('Turning this feature on is not available yet.');
  });

  it('listFeatures rejects with ControlPlaneUnavailableError within the deadline when nothing listens', async () => {
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
    const error = await client.listFeatures().then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(Date.now() - startedAt).toBeLessThan(5_000);
  });
});
