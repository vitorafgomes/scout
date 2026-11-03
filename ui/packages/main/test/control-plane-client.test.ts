import { describe, it, expect, afterEach } from 'bun:test';
import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import { mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  ControlPlaneInvalidArgumentError,
  ControlPlaneNotFoundError,
  ControlPlanePermissionDeniedError,
  ControlPlaneNotImplementedError,
  ControlPlaneUnavailableError,
  ControlPlaneWriteConfirmationError,
  type FeatureDto,
  type OperatorProjectDto,
  type PendingActionDto,
  type WriteLogEntryDto,
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
  scopeMatrix: [{ scope: 'repo', actions: ['Read pull requests'], access: 0 }],
  hasScopeMatrix: true,
  setupSteps: ['Create a token.'],
  hasSetupSteps: true,
  writeClass: 0,
  granted: false,
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
  writeClass: 0,
  granted: false,
};

const youtubeWire = {
  id: 'youtube',
  displayName: 'YouTube',
  description: 'Run the channel.',
  requiresCredentials: true,
  hasCredentials: true,
  enabled: true,
  status: 'ENABLED',
  credentialFields: [],
  settingsFields: [],
  settings: {},
  scopeMatrix: [
    { scope: 'youtube.readonly', actions: ['Read the review queue'], access: 0 },
    { scope: 'drafts', actions: ['Save a draft'], access: 1 },
    { scope: 'youtube.upload', actions: ['Upload a video'], access: 2 },
  ],
  hasScopeMatrix: true,
  setupSteps: [],
  hasSetupSteps: false,
  writeClass: 2,
  granted: true,
  grantedAt: '2026-11-02T10:00:00.0000000Z',
};

const descriptiveWire = { ...youtubeWire, id: 'descriptive', writeClass: 1, granted: false };
delete (descriptiveWire as Partial<typeof youtubeWire>).grantedAt;

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
  scopeMatrix: [{ scope: 'repo', actions: ['Read pull requests'], access: 'read' }],
  setupSteps: ['Create a token.'],
  writeClass: 'none',
  granted: false,
  grantedAt: null,
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
  writeClass: 'none',
  granted: false,
  grantedAt: null,
};

const pendingWire = {
  id: 'act-1',
  featureId: 'youtube',
  action: 'Upload video',
  target: 'channel UC1',
  summary: 'Upload "Intro" as private',
  requestedAt: '2026-11-02T10:00:00Z',
  status: 'pending',
};

const pendingDto: PendingActionDto = { ...pendingWire, decidedAt: null };

const writeWire = {
  at: '2026-11-02T11:00:00Z',
  featureId: 'youtube',
  action: 'Upload video',
  target: 'channel UC1',
  outcome: 'succeeded',
  approvedBy: 'owner',
  actionId: 'act-1',
};

const unapprovedWriteWire = {
  at: '2026-11-02T09:00:00Z',
  featureId: 'youtube',
  action: 'Save script',
  target: 'drafts/intro.md',
  outcome: 'succeeded',
};

async function startFeaturesServer(
  socketPath: string,
  calls: FeaturesCall[],
  enableStatus?: grpc.status,
  enableDetails = 'not wired',
  grantStatus?: grpc.status,
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
      else callback({ code: failWith, details: enableDetails }, null);
    };
  const server = new grpc.Server();
  server.addService(loaded.scout.controlplane.v1.Features.service, {
    ListFeatures: handler('ListFeatures', () => ({
      features: [githubWire, kubernetesWire, youtubeWire, descriptiveWire],
    })),
    SetCredentials: handler('SetCredentials', () => githubWire),
    Setup: handler('Setup', () => githubWire),
    DeleteCredentials: handler('DeleteCredentials', () => githubWire),
    Enable: handler('Enable', () => githubWire, enableStatus),
    Disable: handler('Disable', () => githubWire),
    SetWriteGrant: handler('SetWriteGrant', () => youtubeWire, grantStatus),
    ListPendingActions: handler('ListPendingActions', () => ({ actions: [pendingWire] })),
    ApproveAction: handler('ApproveAction', () => ({
      ...pendingWire,
      status: 'approved',
      decidedAt: '2026-11-02T10:05:00Z',
    })),
    RejectAction: handler('RejectAction', () => ({ ...pendingWire, status: 'rejected' })),
    ListWrites: handler('ListWrites', () => ({ entries: [writeWire, unapprovedWriteWire] })),
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

    expect(features.slice(0, 2)).toEqual([githubDto, kubernetesDto]);
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

    const enabled = await client.enableFeature('github', false);
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
      { method: 'Enable', request: { id: 'github', confirmWrites: false } },
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

    const error = await client.enableFeature('github', false).then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneNotImplementedError);
    expect((error as Error).message).toBe('Turning this feature on is not available yet.');
  });

  it('listFeatures maps each scope row access, the write class and the grant of a write-capable feature', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    server = await startFeaturesServer(socketPath, []);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const [, , youtube, descriptive] = await client.listFeatures();

    expect(youtube?.scopeMatrix).toEqual([
      { scope: 'youtube.readonly', actions: ['Read the review queue'], access: 'read' },
      { scope: 'drafts', actions: ['Save a draft'], access: 'write' },
      { scope: 'youtube.upload', actions: ['Upload a video'], access: 'irreversible' },
    ]);
    expect([youtube?.writeClass, youtube?.granted, youtube?.grantedAt]).toEqual([
      'executive',
      true,
      '2026-11-02T10:00:00.0000000Z',
    ]);
    expect([descriptive?.writeClass, descriptive?.granted, descriptive?.grantedAt]).toEqual([
      'descriptive',
      false,
      null,
    ]);
  });

  it('enableFeature and setWriteGrant send confirmWrites true when the owner confirmed the writes', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: FeaturesCall[] = [];
    server = await startFeaturesServer(socketPath, calls);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    await client.enableFeature('youtube', true);
    const granted = await client.setWriteGrant('youtube', true, true);
    await client.setWriteGrant('youtube', false, false);

    expect(granted.granted).toBe(true);
    expect(calls.map(({ method, request }) => ({ method, request }))).toEqual([
      { method: 'Enable', request: { id: 'youtube', confirmWrites: true } },
      { method: 'SetWriteGrant', request: { id: 'youtube', granted: true, confirmWrites: true } },
      { method: 'SetWriteGrant', request: { id: 'youtube', granted: false, confirmWrites: false } },
    ]);
  });

  it('a FAILED_PRECONDITION Enable rejects with ControlPlaneWriteConfirmationError carrying the control plane message', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    server = await startFeaturesServer(
      socketPath,
      [],
      grpc.status.FAILED_PRECONDITION,
      'Confirm the writes of youtube before enabling it',
    );
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const error = await client.enableFeature('youtube', false).then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneWriteConfirmationError);
    expect((error as Error).message).toBe('Confirm the writes of youtube before enabling it');
  });

  it('a FAILED_PRECONDITION Enable for missing credentials is not taken for a write confirmation', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    server = await startFeaturesServer(
      socketPath,
      [],
      grpc.status.FAILED_PRECONDITION,
      'Feature github is missing required credentials',
    );
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const error = await client.enableFeature('github', false).then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).not.toBeInstanceOf(ControlPlaneWriteConfirmationError);
    expect((error as grpc.ServiceError).code).toBe(grpc.status.FAILED_PRECONDITION);
  });

  it('a FAILED_PRECONDITION SetWriteGrant rejects with ControlPlaneWriteConfirmationError carrying the control plane message', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    server = await startFeaturesServer(
      socketPath,
      [],
      undefined,
      'Confirm the writes of youtube before granting them',
      grpc.status.FAILED_PRECONDITION,
    );
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const error = await client.setWriteGrant('youtube', true, false).then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneWriteConfirmationError);
    expect((error as Error).message).toBe('Confirm the writes of youtube before granting them');
  });

  it('lists pending actions, approves and rejects one with the reason, and lists the writes with the limit', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: FeaturesCall[] = [];
    server = await startFeaturesServer(socketPath, calls);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const pending = await client.listPendingActions();
    const approved = await client.approveAction('act-1');
    const rejected = await client.rejectAction('act-1', 'Wrong thumbnail');
    const writes = await client.listWrites(50);

    expect(pending).toEqual([pendingDto]);
    expect([approved.status, approved.decidedAt]).toEqual(['approved', '2026-11-02T10:05:00Z']);
    expect([rejected.status, rejected.decidedAt]).toEqual(['rejected', null]);
    expect(writes).toEqual<WriteLogEntryDto[]>([
      writeWire,
      { ...unapprovedWriteWire, approvedBy: null, actionId: null },
    ]);
    expect(calls.map(({ method, request }) => ({ method, request }))).toEqual([
      { method: 'ListPendingActions', request: {} },
      { method: 'ApproveAction', request: { id: 'act-1' } },
      { method: 'RejectAction', request: { id: 'act-1', reason: 'Wrong thumbnail' } },
      { method: 'ListWrites', request: { limit: 50 } },
    ]);
  });

  it('listPendingActions rejects with ControlPlaneUnavailableError within the deadline when nothing listens', async () => {
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
    const error = await client.listPendingActions().then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(Date.now() - startedAt).toBeLessThan(5_000);
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

interface OperatorCall {
  method: string;
  request: unknown;
  token: grpc.MetadataValue[];
}

const mandateWire = {
  objective: 'Grow a .NET channel',
  audience: 'Backend developers',
  pillars: ['Performance', 'Testing'],
  cadence: 'weekly',
  language: 'en',
  voiceAndStyle: 'calm, precise',
  blockedTopics: ['politics'],
  targetMetric: 'views',
  targetValue: '10000',
  updatedAt: '2026-11-03T09:00:00Z',
};

const projectWire = {
  id: 'p1',
  topic: 'Span<T> in C#',
  status: 'script_ready',
  origin: 'desktop',
  createdAt: '2026-11-03T09:00:00Z',
  updatedAt: '2026-11-03T09:05:00Z',
  candidates: [{ title: 'Span<T> without fear', reason: 'Fits Performance', fit: 0.82 }],
  script: {
    hook: 'Your allocations are lying to you.',
    sections: [{ heading: 'Why Span', narration: 'Span is a view over memory.' }],
    callToAction: 'Subscribe for more.',
    titleVariants: ['Span<T> explained'],
    description: 'A tour of Span<T>.',
    tags: ['dotnet', 'span'],
    chapters: [{ at: '00:00', title: 'Intro' }],
  },
  claims: [
    {
      id: 'c1',
      text: 'Span<T> is a ref struct.',
      risk: 'low',
      sourceUrls: ['https://learn.microsoft.com/dotnet/api/system.span-1'],
      status: 'pending',
    },
  ],
};

const queuedWire = {
  id: 'p2',
  topic: 'Records in C#',
  status: 'queued',
  origin: 'desktop',
  createdAt: '2026-11-03T10:00:00Z',
  updatedAt: '2026-11-03T10:00:00Z',
  candidates: [],
  claims: [],
  error: 'model unavailable',
};

const projectDto: OperatorProjectDto = {
  ...projectWire,
  claims: [{ ...projectWire.claims[0]!, note: null }],
  error: null,
};

async function startOperatorServer(
  socketPath: string,
  calls: OperatorCall[],
  options: {
    mandate?: object;
    failWith?: { code: grpc.status; details: string };
  } = {},
): Promise<grpc.Server> {
  const definition = protoLoader.loadSync(path.join(PROTO_DIR, 'operator.proto'), {
    enums: String,
    defaults: true,
  });
  const loaded = grpc.loadPackageDefinition(definition) as unknown as {
    scout: { controlplane: { v1: { Operator: grpc.ServiceClientConstructor } } };
  };
  const handler =
    (method: string, response: () => unknown) =>
    (call: grpc.ServerUnaryCall<unknown, unknown>, callback: grpc.sendUnaryData<unknown>) => {
      calls.push({ method, request: call.request, token: call.metadata.get('x-scout-token') });
      if (options.failWith === undefined) callback(null, response());
      else callback(options.failWith, null);
    };
  const server = new grpc.Server();
  server.addService(loaded.scout.controlplane.v1.Operator.service, {
    GetMandate: handler('GetMandate', () => options.mandate ?? mandateWire),
    SaveMandate: handler('SaveMandate', () => mandateWire),
    ListProjects: handler('ListProjects', () => ({ projects: [projectWire, queuedWire] })),
    GetProject: handler('GetProject', () => projectWire),
    StartProject: handler('StartProject', () => queuedWire),
    DecideClaim: handler('DecideClaim', () => ({
      ...projectWire,
      claims: [{ ...projectWire.claims[0], status: 'waived', note: 'Common knowledge' }],
    })),
  });
  await new Promise<void>((resolve, reject) => {
    server.bindAsync(`unix:${socketPath}`, grpc.ServerCredentials.createInsecure(), (err) =>
      err ? reject(err) : resolve(),
    );
  });
  return server;
}

describe('createControlPlaneClient operator', () => {
  it('maps the mandate, the projects with candidates, script and claims, and sends each request with the token', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: OperatorCall[] = [];
    server = await startOperatorServer(socketPath, calls);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });
    const { updatedAt: _stamp, ...fields } = mandateWire;

    const mandate = await client.getMandate();
    const saved = await client.saveMandate(fields);
    const projects = await client.listProjects(50);
    const one = await client.getProject('p1');
    const started = await client.startProject('Records in C#');
    const decided = await client.decideClaim('p1', 'c1', 'waived', 'Common knowledge');
    await client.decideClaim('p1', 'c1', 'supported');

    expect(mandate).toEqual(mandateWire);
    expect(saved).toEqual(mandateWire);
    expect(projects).toEqual([
      projectDto,
      { ...queuedWire, script: null, error: 'model unavailable' },
    ]);
    expect(one).toEqual(projectDto);
    expect([started.id, started.status, started.script]).toEqual(['p2', 'queued', null]);
    expect([decided.claims[0]?.status, decided.claims[0]?.note]).toEqual([
      'waived',
      'Common knowledge',
    ]);
    expect(calls.map(({ method, request }) => ({ method, request }))).toEqual([
      { method: 'GetMandate', request: {} },
      { method: 'SaveMandate', request: fields },
      { method: 'ListProjects', request: { limit: 50 } },
      { method: 'GetProject', request: { id: 'p1' } },
      { method: 'StartProject', request: { topic: 'Records in C#' } },
      {
        method: 'DecideClaim',
        request: { projectId: 'p1', claimId: 'c1', status: 'waived', note: 'Common knowledge' },
      },
      { method: 'DecideClaim', request: { projectId: 'p1', claimId: 'c1', status: 'supported' } },
    ]);
    expect(calls.every((c) => c.token.length === 1 && c.token[0] === TOKEN)).toBe(true);
  });

  it('getMandate answers null while no mandate was saved, whether the host answers an empty mandate or NOT_FOUND', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const emptySocket = tempSocketPath();
    const empty = await startOperatorServer(emptySocket, [], { mandate: {} });
    const emptyClient = createControlPlaneClient({
      socketPath: emptySocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });
    const socketPath = tempSocketPath();
    server = await startOperatorServer(socketPath, [], {
      failWith: { code: grpc.status.NOT_FOUND, details: 'No mandate saved' },
    });
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const fromEmpty = await emptyClient.getMandate();
    const fromNotFound = await client.getMandate();
    emptyClient.close();
    empty.forceShutdown();

    expect([fromEmpty, fromNotFound]).toEqual([null, null]);
  });

  it('an INVALID_ARGUMENT from the operator rejects with ControlPlaneInvalidArgumentError carrying the control plane message', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    server = await startOperatorServer(socketPath, [], {
      failWith: { code: grpc.status.INVALID_ARGUMENT, details: 'objective is required' },
    });
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const error = await client.saveMandate({ ...mandateWire, objective: '' }).then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneInvalidArgumentError);
    expect((error as Error).message).toBe('objective is required');
  });

  it('a PERMISSION_DENIED start rejects with ControlPlanePermissionDeniedError carrying the write gate reason', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const reason =
      'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.';
    const socketPath = tempSocketPath();
    server = await startOperatorServer(socketPath, [], {
      failWith: { code: grpc.status.PERMISSION_DENIED, details: reason },
    });
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const error = await client.startProject('Records in C#').then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlanePermissionDeniedError);
    expect((error as Error).message).toBe(reason);
  });

  it('a NOT_FOUND project rejects with ControlPlaneNotFoundError carrying the control plane message', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    server = await startOperatorServer(socketPath, [], {
      failWith: { code: grpc.status.NOT_FOUND, details: 'Project p9 not found' },
    });
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const error = await client.getProject('p9').then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneNotFoundError);
    expect((error as Error).message).toBe('Project p9 not found');
  });

  it('listProjects rejects with ControlPlaneUnavailableError within the deadline when nothing listens', async () => {
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
    const error = await client.listProjects(50).then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(Date.now() - startedAt).toBeLessThan(5_000);
  });
});
