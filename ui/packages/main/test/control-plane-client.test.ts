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
  script: {
    ...projectWire.script,
    sections: [{ ...projectWire.script.sections[0]!, startMs: 0, endMs: 0 }],
  },
  claims: [{ ...projectWire.claims[0]!, note: null }],
  error: null,
  scenes: [],
  package: null,
  publication: null,
  narrationVoice: '',
  backgroundFile: null,
  delegated: false,
  direction: '',
  motion: 'gentle',
  format: 'generated_pictures',
  recordingFile: null,
  recordingMs: 0,
  presenter: '',
  presenterFile: null,
  mouthBox: null,
};

const readyScene = {
  id: 's1',
  order: 0,
  narration: 'Span is a view over memory.',
  visualPrompt: 'A ruler laid over a row of bytes',
  locked: false,
  status: 'ready',
  audioSource: 'generated',
  visualSource: 'stock',
  visualQuery: 'ruler over bytes',
  visualCredit: 'Ruler by Jane Doe, CC BY 4.0, https://example.org/ruler',
  audioFile: 'scene-s1.mp3',
  visualFile: 'owner-s1-0a1b2c3d4e5f.png',
  durationMs: 4200,
};

const failedScene = {
  id: 's2',
  order: 1,
  narration: 'It never allocates.',
  visualPrompt: 'An empty heap',
  locked: true,
  status: 'failed',
  error: 'Speech is not available with this provider.',
  audioSource: 'none',
  visualSource: 'text_card',
  durationMs: 0,
};

const producingWire = { ...projectWire, status: 'producing', scenes: [readyScene, failedScene] };

const delegatedWire = {
  ...projectWire,
  status: 'production_queued',
  narrationVoice: 'OpenAI:nova',
  backgroundFile: 'owner-background-0a1b2c3d4e5f.png',
  delegated: true,
  direction: 'Watercolour diagrams',
  motion: 'still',
};

const editedWire = {
  ...projectWire,
  status: 'production_queued',
  direction: 'Explain it to a junior developer, no jargon',
  format: 'own_pictures',
  scenes: [
    {
      ...readyScene,
      narration: 'Span is a window over memory you already have.',
      status: 'planned',
      audioSource: 'none',
      audioFile: undefined,
      durationMs: 0,
    },
  ],
};

const packageWire = {
  status: 'assembled',
  videoFile: 'video.mp4',
  shorts: [
    { file: 'short-1.mp4', startMs: 12000, durationMs: 45000, hook: 'Span never allocates.' },
  ],
  thumbnailFile: 'thumbnail.png',
  checks: [
    { name: 'Loudness', result: 'pass', detail: '-14 LUFS' },
    { name: 'Length', result: 'warn', detail: 'Under 60 s' },
  ],
  rightsConfirmed: false,
  title: 'Span<T> explained',
  description: 'A tour of Span<T>.',
  tags: ['dotnet', 'span'],
  durationMs: 98000,
  metadataEdited: false,
};

const assembledWire = { ...projectWire, status: 'assembled', package: packageWire };

const publicationWire = {
  status: 'scheduled',
  videoId: 'dQw4w9WgXcQ',
  privacyStatus: 'private',
  publishAt: '2026-11-10T09:00:00Z',
  madeForKids: false,
  syntheticMedia: true,
  uploadedAt: '2026-11-03T12:00:00Z',
  snapshots: [
    {
      window: '24h',
      takenAt: '2026-11-04T12:00:00Z',
      views: 1234,
      minutesWatched: 2050.5,
      averageViewDurationSeconds: 99.7,
      averageViewPercentage: 41.2,
      subscribersGained: 12,
      subscribersLost: 1,
      likes: 88,
      comments: 9,
      shares: 4,
      retention: [
        { elapsedRatio: 0, audienceWatchRatio: 1 },
        { elapsedRatio: 0.5, audienceWatchRatio: 0.42 },
      ],
    },
    {
      window: '7d',
      takenAt: '2026-11-10T12:00:00Z',
      views: 5000,
      minutesWatched: 8000,
      averageViewDurationSeconds: 96,
      averageViewPercentage: 39.5,
      subscribersGained: 40,
      subscribersLost: 3,
      likes: 300,
      comments: 31,
      shares: 17,
      impressions: 64000,
      impressionsCtr: 5.1,
      retention: [],
    },
  ],
  proposals: [
    {
      id: 'pr1',
      kind: 'title',
      text: 'Span<T> in 5 minutes',
      reason: 'CTR under the channel median',
      status: 'proposed',
    },
    {
      id: 'pr2',
      kind: 'thumbnail',
      text: 'Bigger face',
      reason: 'Low CTR',
      status: 'accepted',
      thumbnailFile: 'thumbnail-proposal-pr2.png',
    },
  ],
  experiment: {
    id: 'e1',
    kind: 'title',
    status: 'decided',
    arms: [
      {
        id: 'control',
        label: 'Control',
        value: 'Span<T> explained',
        startedAt: '2026-11-11T00:00:00Z',
        endedAt: '2026-11-12T00:00:00Z',
        impressions: 2000,
        clicks: 80,
        averageViewPercentage: 40,
      },
      {
        id: 'a1',
        label: 'Variant',
        value: 'Span<T> in 5 minutes',
        startedAt: '2026-11-12T00:00:00Z',
        impressions: 0,
        clicks: 0,
        averageViewPercentage: 0,
      },
    ],
    winnerArmId: 'a1',
    result: 'Variant wins: z = 2.31 on 2000 and 2100 impressions.',
  },
};

const publishedWire = { ...assembledWire, status: 'approved', publication: publicationWire };

const commentWire = {
  id: 'Ugx1',
  threadId: 'Ugx1',
  videoId: 'vid-1',
  videoTitle: 'Records explained',
  author: 'Ana',
  text: 'Could you cover Span<T> next?',
  publishedAt: '2026-11-03T08:00:00Z',
  likeCount: 3,
  kind: 'request',
  theme: 'Span<T>',
  status: 'replied',
  draft: 'Yes, it is next.',
  approvedText: 'Yes, it is next week.',
  replyId: 'Ugx1.r1',
  error: 'none',
  decidedAt: '2026-11-03T09:00:00Z',
  fetchedAt: '2026-11-03T08:05:00Z',
};

const commentsWire = {
  comments: [
    commentWire,
    {
      ...commentWire,
      id: 'Ugx2',
      threadId: 'Ugx2',
      kind: 'spam',
      status: 'quarantined',
      draft: '',
      approvedText: undefined,
      replyId: undefined,
      error: undefined,
      decidedAt: undefined,
    },
  ],
  ideas: [
    {
      id: 'idea-Ugx1',
      text: 'Span<T> in depth',
      commentId: 'Ugx1',
      status: 'used',
      projectId: 'p9',
    },
    { id: 'idea-Ugx3', text: 'Records vs classes', commentId: 'Ugx3', status: 'proposed' },
  ],
  syncedAt: '2026-11-03T08:05:00Z',
  syncRequestedAt: '2026-11-03T09:30:00Z',
  syncError: 'YouTube answered 503.',
};

const readinessWire = {
  speechAvailable: false,
  imagesAvailable: true,
  stockPictures: true,
  provider: 'openai',
  ffmpegAvailable: true,
  youtubeSignedIn: false,
  notes: ['Speech needs OpenAI or Azure OpenAI; narration comes from your own audio files.'],
  voices: [
    {
      id: 'piper:en_GB-aru-medium#3',
      label: 'Aru <speaker> (medium)',
      source: 'piper',
      sampleFile: 'piper-en_GB-aru-medium-3.wav',
    },
    { id: 'OpenAI:nova', label: 'nova (OpenAI)', source: 'OpenAI' },
  ],
};

const ownVideoWire = {
  id: 'p3',
  topic: 'My talk at the meetup',
  status: 'script_ready',
  origin: 'desktop',
  createdAt: '2026-11-04T09:00:00Z',
  updatedAt: '2026-11-04T09:20:00Z',
  candidates: [],
  claims: [],
  script: {
    hook: '',
    sections: [
      { heading: '', narration: 'Hello everyone.', startMs: 0, endMs: 2400 },
      { heading: '', narration: 'Today we look at Span.', startMs: 2400, endMs: 5100 },
    ],
    callToAction: '',
    titleVariants: [],
    description: '',
    tags: [],
    chapters: [],
  },
  scenes: [
    {
      id: 's1',
      order: 0,
      narration: 'Hello everyone.',
      visualPrompt: '',
      locked: false,
      status: 'ready',
      audioSource: 'recording',
      visualSource: 'recording',
      durationMs: 2400,
      clipStartMs: 0,
      clipEndMs: 2400,
    },
    {
      id: 's2',
      order: 1,
      narration: 'Today we look at Span.',
      visualPrompt: '',
      locked: false,
      status: 'ready',
      audioSource: 'recording',
      visualSource: 'recording',
      durationMs: 2700,
      clipStartMs: 2400,
      clipEndMs: 5100,
    },
  ],
  format: 'own_video',
  recordingFile: 'owner-recording-0a1b2c3d4e5f.mp4',
  recordingMs: 5100,
};

const characterWire = {
  ...projectWire,
  status: 'script_ready',
  format: 'animated_character',
  presenter: 'A friendly robot in a workshop',
  presenterFile: 'owner-presenter-0a1b2c3d4e5f.png',
  mouthBox: { x: 0.4, y: 0.6, width: 0.2, height: 0.1 },
  scenes: [{ ...readyScene, visualSource: 'presenter', visualFile: undefined }],
};

async function startOperatorServer(
  socketPath: string,
  calls: OperatorCall[],
  options: {
    mandate?: object;
    failWith?: { code: grpc.status; details: string };
    comments?: object;
    project?: object;
    readiness?: object;
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
    GetProject: handler('GetProject', () => options.project ?? projectWire),
    StartProject: handler('StartProject', () => queuedWire),
    DecideClaim: handler('DecideClaim', () => ({
      ...projectWire,
      claims: [{ ...projectWire.claims[0], status: 'waived', note: 'Common knowledge' }],
    })),
    ProduceProject: handler('ProduceProject', () => ({
      ...projectWire,
      status: 'production_queued',
      narrationVoice: 'piper:en_GB-aru-medium#3',
      direction: 'Chalk on slate',
      motion: 'dynamic',
    })),
    DelegateProject: handler('DelegateProject', () => delegatedWire),
    DeleteProject: handler('DeleteProject', () => ({})),
    ReorderScenes: handler('ReorderScenes', () => producingWire),
    SetSceneLocked: handler('SetSceneLocked', () => producingWire),
    RegenerateScene: handler('RegenerateScene', () => producingWire),
    AttachSceneMedia: handler('AttachSceneMedia', () => producingWire),
    GetMediaDirectory: handler('GetMediaDirectory', () => ({
      path: '/home/owner/.local/share/Scout/operator/p1/media',
    })),
    GetVoiceSamplesDirectory: handler('GetVoiceSamplesDirectory', () => ({
      path: '/home/owner/.local/share/Scout/voice-samples',
    })),
    GetReadiness: handler('GetReadiness', () => options.readiness ?? readinessWire),
    AssembleProject: handler('AssembleProject', () => ({
      ...projectWire,
      status: 'assembly_queued',
      package: { status: 'assembly_queued', error: 'The last assembly failed.' },
    })),
    ConfirmMediaRights: handler('ConfirmMediaRights', () => assembledWire),
    PublishPackage: handler('PublishPackage', () => ({
      ...assembledWire,
      publication: { status: 'publish_approved', privacyStatus: 'unlisted', madeForKids: true },
    })),
    DecideProposal: handler('DecideProposal', () => publishedWire),
    StartExperiment: handler('StartExperiment', () => publishedWire),
    AdoptExperimentWinner: handler('AdoptExperimentWinner', () => publishedWire),
    EditPackageMetadata: handler('EditPackageMetadata', () => assembledWire),
    EditSceneText: handler('EditSceneText', () => editedWire),
    RegenerateNarration: handler('RegenerateNarration', () => editedWire),
    SetMouthBox: handler('SetMouthBox', () => characterWire),
    RegeneratePresenter: handler('RegeneratePresenter', () => ({
      ...characterWire,
      presenterFile: undefined,
    })),
    ListComments: handler('ListComments', () => options.comments ?? commentsWire),
    RequestCommentSync: handler('RequestCommentSync', () => ({ comments: [], ideas: [] })),
    DecideComment: handler('DecideComment', () => commentsWire),
    ApproveReply: handler('ApproveReply', () => commentsWire),
    DecideIdea: handler('DecideIdea', () => commentsWire),
    ApprovePackage: handler('ApprovePackage', () => ({
      ...assembledWire,
      status: 'approved',
      package: { ...packageWire, status: 'approved', approvedAt: '2026-11-03T11:00:00Z' },
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
    const started = await client.startProject('Records in C#', 'generated_pictures', '', '');
    const decided = await client.decideClaim('p1', 'c1', 'waived', 'Common knowledge');
    await client.decideClaim('p1', 'c1', 'supported');

    expect(mandate).toEqual(mandateWire);
    expect(saved).toEqual(mandateWire);
    expect(projects).toEqual([
      projectDto,
      {
        ...queuedWire,
        script: null,
        error: 'model unavailable',
        scenes: [],
        package: null,
        publication: null,
        narrationVoice: '',
        backgroundFile: null,
        delegated: false,
        direction: '',
        motion: 'gentle',
        format: 'generated_pictures',
        recordingFile: null,
        recordingMs: 0,
        presenter: '',
        presenterFile: null,
        mouthBox: null,
      },
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
      {
        method: 'StartProject',
        request: {
          topic: 'Records in C#',
          format: 'generated_pictures',
          direction: '',
          presenter: '',
        },
      },
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

    const error = await client.startProject('Records in C#', 'generated_pictures', '', '').then(
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

  it("maps the scenes with their sources, files and errors, the project voice, background, delegation, direction and motion, and the readiness with each voice's sample or null, and sends produce and delegate with the voice, the direction and the motion, reorder, lock, regenerate, attach of a scene file and of a background, media directory, voice samples directory and readiness with the token", async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: OperatorCall[] = [];
    server = await startOperatorServer(socketPath, calls);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const produced = await client.produceProject(
      'p1',
      'piper:en_GB-aru-medium#3',
      'Chalk on slate',
      'dynamic',
      '',
    );
    const delegated = await client.delegateProject(
      'p1',
      'OpenAI:nova',
      'Watercolour diagrams',
      'still',
      '',
    );
    const reordered = await client.reorderScenes('p1', ['s2', 's1']);
    await client.setSceneLocked('p1', 's2', true);
    await client.regenerateScene('p1', 's1');
    await client.attachSceneMedia('p1', 's1', 'visual', 'owner-s1-0a1b2c3d4e5f.png');
    await client.attachSceneMedia('p1', '', 'background', 'owner-background-0a1b2c3d4e5f.png');
    const directory = await client.getMediaDirectory('p1');
    const samples = await client.getVoiceSamplesDirectory();
    const readiness = await client.getReadiness();

    expect([
      produced.id,
      produced.status,
      produced.scenes,
      produced.narrationVoice,
      produced.backgroundFile,
      produced.delegated,
      produced.direction,
      produced.motion,
    ]).toEqual([
      'p1',
      'production_queued',
      [],
      'piper:en_GB-aru-medium#3',
      null,
      false,
      'Chalk on slate',
      'dynamic',
    ]);
    expect(delegated).toEqual({
      ...projectDto,
      status: 'production_queued',
      narrationVoice: 'OpenAI:nova',
      backgroundFile: 'owner-background-0a1b2c3d4e5f.png',
      delegated: true,
      direction: 'Watercolour diagrams',
      motion: 'still',
    });
    expect(reordered.scenes).toEqual([
      { ...readyScene, error: null, clipStartMs: 0, clipEndMs: 0 },
      {
        ...failedScene,
        visualQuery: '',
        visualCredit: null,
        audioFile: null,
        visualFile: null,
        clipStartMs: 0,
        clipEndMs: 0,
      },
    ]);
    expect(directory).toBe('/home/owner/.local/share/Scout/operator/p1/media');
    expect(samples).toBe('/home/owner/.local/share/Scout/voice-samples');
    expect(readiness).toEqual({
      ...readinessWire,
      voices: [
        {
          id: 'piper:en_GB-aru-medium#3',
          label: 'Aru <speaker> (medium)',
          source: 'piper',
          sampleFile: 'piper-en_GB-aru-medium-3.wav',
        },
        { id: 'OpenAI:nova', label: 'nova (OpenAI)', source: 'OpenAI', sampleFile: null },
      ],
      reportedAt: null,
      transcriptionAvailable: false,
      lipSyncAvailable: false,
    });
    expect(calls.map(({ method, request }) => ({ method, request }))).toEqual([
      {
        method: 'ProduceProject',
        request: {
          id: 'p1',
          narrationVoice: 'piper:en_GB-aru-medium#3',
          direction: 'Chalk on slate',
          motion: 'dynamic',
          presenter: '',
        },
      },
      {
        method: 'DelegateProject',
        request: {
          id: 'p1',
          narrationVoice: 'OpenAI:nova',
          direction: 'Watercolour diagrams',
          motion: 'still',
          presenter: '',
        },
      },
      { method: 'ReorderScenes', request: { projectId: 'p1', sceneIds: ['s2', 's1'] } },
      { method: 'SetSceneLocked', request: { projectId: 'p1', sceneId: 's2', locked: true } },
      { method: 'RegenerateScene', request: { projectId: 'p1', sceneId: 's1' } },
      {
        method: 'AttachSceneMedia',
        request: {
          projectId: 'p1',
          sceneId: 's1',
          kind: 'visual',
          fileName: 'owner-s1-0a1b2c3d4e5f.png',
        },
      },
      {
        method: 'AttachSceneMedia',
        request: {
          projectId: 'p1',
          sceneId: '',
          kind: 'background',
          fileName: 'owner-background-0a1b2c3d4e5f.png',
        },
      },
      { method: 'GetMediaDirectory', request: { id: 'p1' } },
      { method: 'GetVoiceSamplesDirectory', request: {} },
      { method: 'GetReadiness', request: {} },
    ]);
    expect(calls.every((c) => c.token.length === 1 && c.token[0] === TOKEN)).toBe(true);
  });

  it('start, scene text and narration regeneration send the wire keys and the project maps format and direction', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: OperatorCall[] = [];
    server = await startOperatorServer(socketPath, calls);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const started = await client.startProject(
      'Span<T> in C#',
      'own_pictures',
      'Explain it to a junior developer, no jargon',
      '',
    );
    const edited = await client.editSceneText(
      'p1',
      's1',
      'Span is a window over memory you already have.',
    );
    const renarrated = await client.regenerateNarration('p1', 's1');

    expect(calls.map(({ method, request }) => ({ method, request }))).toEqual([
      {
        method: 'StartProject',
        request: {
          topic: 'Span<T> in C#',
          format: 'own_pictures',
          direction: 'Explain it to a junior developer, no jargon',
          presenter: '',
        },
      },
      {
        method: 'EditSceneText',
        request: {
          projectId: 'p1',
          sceneId: 's1',
          text: 'Span is a window over memory you already have.',
        },
      },
      { method: 'RegenerateNarration', request: { projectId: 'p1', sceneId: 's1' } },
    ]);
    expect(calls.every((c) => c.token.length === 1 && c.token[0] === TOKEN)).toBe(true);
    expect([started.format, started.direction]).toEqual(['generated_pictures', '']);
    expect([edited.status, edited.format, edited.direction]).toEqual([
      'production_queued',
      'own_pictures',
      'Explain it to a junior developer, no jargon',
    ]);
    expect(edited.scenes).toEqual([
      {
        ...readyScene,
        narration: 'Span is a window over memory you already have.',
        status: 'planned',
        audioSource: 'none',
        audioFile: null,
        durationMs: 0,
        error: null,
        clipStartMs: 0,
        clipEndMs: 0,
      },
    ]);
    expect(renarrated).toEqual(edited);
  });

  it('reads the recording, the clips, the transcript times and whether Scout can transcribe', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: OperatorCall[] = [];
    server = await startOperatorServer(socketPath, calls, {
      project: ownVideoWire,
      readiness: { ...readinessWire, transcriptionAvailable: true },
    });
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const plainSocket = tempSocketPath();
    const plain = await startOperatorServer(plainSocket, []);
    const plainClient = createControlPlaneClient({
      socketPath: plainSocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });

    const read = await client.getProject('p3');
    const readiness = await client.getReadiness();
    await client.attachSceneMedia('p3', '', 'recording', 'owner-recording-0a1b2c3d4e5f.mp4');
    const without = await plainClient.getReadiness();
    plainClient.close();
    plain.forceShutdown();

    expect([read.format, read.recordingFile, read.recordingMs]).toEqual([
      'own_video',
      'owner-recording-0a1b2c3d4e5f.mp4',
      5100,
    ]);
    expect(read.script?.sections).toEqual([
      { heading: '', narration: 'Hello everyone.', startMs: 0, endMs: 2400 },
      { heading: '', narration: 'Today we look at Span.', startMs: 2400, endMs: 5100 },
    ]);
    expect(read.scenes.map((s) => [s.id, s.clipStartMs, s.clipEndMs, s.visualSource])).toEqual([
      ['s1', 0, 2400, 'recording'],
      ['s2', 2400, 5100, 'recording'],
    ]);
    expect([readiness.transcriptionAvailable, without.transcriptionAvailable]).toEqual([
      true,
      false,
    ]);
    expect(calls.find((c) => c.method === 'AttachSceneMedia')?.request).toEqual({
      projectId: 'p3',
      sceneId: '',
      kind: 'recording',
      fileName: 'owner-recording-0a1b2c3d4e5f.mp4',
    });
  });

  it('maps an animated character project with its presenter, presenter picture and mouth box, lip sync in the readiness, a format it does not know as generated pictures, and sends the presenter, SetMouthBox and RegeneratePresenter with their wire keys', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: OperatorCall[] = [];
    server = await startOperatorServer(socketPath, calls, {
      project: characterWire,
      readiness: { ...readinessWire, lipSyncAvailable: true },
    });
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });
    const unknownSocket = tempSocketPath();
    const unknown = await startOperatorServer(unknownSocket, [], {
      project: { ...projectWire, format: 'talking_head' },
    });
    const unknownClient = createControlPlaneClient({
      socketPath: unknownSocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });

    const read = await client.getProject('p1');
    const readiness = await client.getReadiness();
    await client.startProject('Span<T> in C#', 'animated_character', '', 'A friendly robot');
    await client.produceProject('p1', '', '', 'gentle', 'A friendly robot');
    await client.delegateProject('p1', '', '', 'gentle', 'A cartoon owl');
    const marked = await client.setMouthBox('p1', { x: 0.4, y: 0.6, width: 0.2, height: 0.1 });
    const regenerated = await client.regeneratePresenter('p1');
    const otherFormat = await unknownClient.getProject('p1');
    unknownClient.close();
    unknown.forceShutdown();

    expect([read.format, read.presenter, read.presenterFile, read.mouthBox]).toEqual([
      'animated_character',
      'A friendly robot in a workshop',
      'owner-presenter-0a1b2c3d4e5f.png',
      { x: 0.4, y: 0.6, width: 0.2, height: 0.1 },
    ]);
    expect(read.scenes.map((scene) => [scene.visualSource, scene.visualFile])).toEqual([
      ['presenter', null],
    ]);
    expect(readiness.lipSyncAvailable).toBe(true);
    expect(marked.mouthBox).toEqual({ x: 0.4, y: 0.6, width: 0.2, height: 0.1 });
    expect([regenerated.presenterFile, regenerated.presenter]).toEqual([
      null,
      'A friendly robot in a workshop',
    ]);
    expect([otherFormat.format, otherFormat.presenter, otherFormat.mouthBox]).toEqual([
      'generated_pictures',
      '',
      null,
    ]);
    expect(
      calls
        .filter(({ method }) => method !== 'GetProject' && method !== 'GetReadiness')
        .map(({ method, request }) => ({ method, request })),
    ).toEqual([
      {
        method: 'StartProject',
        request: {
          topic: 'Span<T> in C#',
          format: 'animated_character',
          direction: '',
          presenter: 'A friendly robot',
        },
      },
      {
        method: 'ProduceProject',
        request: {
          id: 'p1',
          narrationVoice: '',
          direction: '',
          motion: 'gentle',
          presenter: 'A friendly robot',
        },
      },
      {
        method: 'DelegateProject',
        request: {
          id: 'p1',
          narrationVoice: '',
          direction: '',
          motion: 'gentle',
          presenter: 'A cartoon owl',
        },
      },
      {
        method: 'SetMouthBox',
        request: { projectId: 'p1', mouthBox: { x: 0.4, y: 0.6, width: 0.2, height: 0.1 } },
      },
      { method: 'RegeneratePresenter', request: { id: 'p1' } },
    ]);
    expect(calls.every((c) => c.token.length === 1 && c.token[0] === TOKEN)).toBe(true);
  });

  it('a mouth box the control plane refuses reaches the caller as invalid-argument, and the mouth box and the presenter regeneration with nothing listening as unavailable within the deadline', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const reason = 'The project has no presenter picture to mark.';
    const refusingSocket = tempSocketPath();
    server = await startOperatorServer(refusingSocket, [], {
      failWith: { code: grpc.status.INVALID_ARGUMENT, details: reason },
    });
    client = createControlPlaneClient({
      socketPath: refusingSocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });
    const unreachableClient = createControlPlaneClient({
      socketPath: tempSocketPath(),
      token: TOKEN,
      protoDir: PROTO_DIR,
      deadlineMs: 500,
    });
    const box = { x: 0.4, y: 0.6, width: 0.2, height: 0.1 };

    const refused = await client.setMouthBox('p1', box).then(
      () => null,
      (err: unknown) => err,
    );
    const startedAt = Date.now();
    const unreachable = await Promise.all([
      unreachableClient.setMouthBox('p1', box).then(
        () => null,
        (err: unknown) => err,
      ),
      unreachableClient.regeneratePresenter('p1').then(
        () => null,
        (err: unknown) => err,
      ),
    ]);
    const elapsed = Date.now() - startedAt;
    unreachableClient.close();

    expect(refused).toBeInstanceOf(ControlPlaneInvalidArgumentError);
    expect((refused as Error).message).toBe(reason);
    expect(unreachable.map((err) => err instanceof ControlPlaneUnavailableError)).toEqual([
      true,
      true,
    ]);
    expect(elapsed).toBeLessThan(5_000);
  });

  it('a scene text the control plane refuses or cannot answer reaches the caller as invalid-argument or unavailable', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const reason = "Scene 's1' is locked; unlock it before editing its text.";
    const refusingSocket = tempSocketPath();
    server = await startOperatorServer(refusingSocket, [], {
      failWith: { code: grpc.status.INVALID_ARGUMENT, details: reason },
    });
    client = createControlPlaneClient({
      socketPath: refusingSocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });
    const unreachableClient = createControlPlaneClient({
      socketPath: tempSocketPath(),
      token: TOKEN,
      protoDir: PROTO_DIR,
      deadlineMs: 500,
    });

    const refused = await client.editSceneText('p1', 's1', 'New text.').then(
      () => null,
      (err: unknown) => err,
    );
    const startedAt = Date.now();
    const unreachable = await unreachableClient.editSceneText('p1', 's1', 'New text.').then(
      () => null,
      (err: unknown) => err,
    );
    const elapsed = Date.now() - startedAt;
    const narrationUnreachable = await unreachableClient.regenerateNarration('p1', 's1').then(
      () => null,
      (err: unknown) => err,
    );
    unreachableClient.close();

    expect(refused).toBeInstanceOf(ControlPlaneInvalidArgumentError);
    expect((refused as Error).message).toBe(reason);
    expect(unreachable).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(narrationUnreachable).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(elapsed).toBeLessThan(5_000);
  });

  it('a produce refused while claims are pending rejects with ControlPlaneInvalidArgumentError carrying how many are pending', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const reason = '2 claims are still pending; decide every claim before producing the video.';
    const socketPath = tempSocketPath();
    server = await startOperatorServer(socketPath, [], {
      failWith: { code: grpc.status.INVALID_ARGUMENT, details: reason },
    });
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const error = await client.produceProject('p1', '', '', 'gentle', '').then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneInvalidArgumentError);
    expect((error as Error).message).toBe(reason);
  });

  it('a produce without the YouTube write grant rejects with ControlPlanePermissionDeniedError, and an unknown project media directory with ControlPlaneNotFoundError', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const deniedSocket = tempSocketPath();
    const denied = await startOperatorServer(deniedSocket, [], {
      failWith: { code: grpc.status.PERMISSION_DENIED, details: 'youtube may not write' },
    });
    const deniedClient = createControlPlaneClient({
      socketPath: deniedSocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });
    const socketPath = tempSocketPath();
    server = await startOperatorServer(socketPath, [], {
      failWith: { code: grpc.status.NOT_FOUND, details: 'Project p9 not found' },
    });
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const refused = await deniedClient.produceProject('p1', '', '', 'gentle', '').then(
      () => null,
      (err: unknown) => err,
    );
    const missing = await client.getMediaDirectory('p9').then(
      () => null,
      (err: unknown) => err,
    );
    deniedClient.close();
    denied.forceShutdown();

    expect(refused).toBeInstanceOf(ControlPlanePermissionDeniedError);
    expect((refused as Error).message).toBe('youtube may not write');
    expect(missing).toBeInstanceOf(ControlPlaneNotFoundError);
    expect((missing as Error).message).toBe('Project p9 not found');
  });

  it('maps the video package with its Shorts, checks and absent optional fields, and sends assemble, media rights, metadata and approval with the token', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: OperatorCall[] = [];
    server = await startOperatorServer(socketPath, calls);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const queued = await client.assembleProject('p1');
    const confirmed = await client.confirmMediaRights('p1');
    await client.editPackageMetadata('p1', 'Span<T> explained', 'A tour of Span<T>.', [
      'dotnet',
      'span',
    ]);
    const approved = await client.approvePackage('p1');

    expect([queued.status, queued.package]).toEqual([
      'assembly_queued',
      {
        status: 'assembly_queued',
        videoFile: null,
        shorts: [],
        thumbnailFile: null,
        checks: [],
        rightsConfirmed: false,
        approvedAt: null,
        title: '',
        description: '',
        tags: [],
        durationMs: 0,
        error: 'The last assembly failed.',
        metadataEdited: false,
      },
    ]);
    expect(confirmed.package).toEqual({ ...packageWire, approvedAt: null, error: null });
    expect([approved.status, approved.package?.status, approved.package?.approvedAt]).toEqual([
      'approved',
      'approved',
      '2026-11-03T11:00:00Z',
    ]);
    expect(calls.map(({ method, request }) => ({ method, request }))).toEqual([
      { method: 'AssembleProject', request: { id: 'p1' } },
      { method: 'ConfirmMediaRights', request: { id: 'p1' } },
      {
        method: 'EditPackageMetadata',
        request: {
          projectId: 'p1',
          title: 'Span<T> explained',
          description: 'A tour of Span<T>.',
          tags: ['dotnet', 'span'],
        },
      },
      { method: 'ApprovePackage', request: { id: 'p1' } },
    ]);
    expect(calls.every((c) => c.token.length === 1 && c.token[0] === TOKEN)).toBe(true);
  });

  it('maps the publication with its snapshots, retention, proposals and experiment and absent optional fields as null, and sends publish, proposal decision, experiment start and adoption with the token', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: OperatorCall[] = [];
    server = await startOperatorServer(socketPath, calls);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const requested = await client.publishPackage('p1', 'unlisted', null, true, false);
    await client.publishPackage('p1', 'private', '2026-11-10T09:00:00.000Z', false, true);
    const decided = await client.decideProposal('p1', 'pr1', false);
    await client.startExperiment('p1', 48);
    await client.adoptExperimentWinner('p1');

    expect(requested.publication).toEqual({
      status: 'publish_approved',
      videoId: null,
      privacyStatus: 'unlisted',
      publishAt: null,
      madeForKids: true,
      syntheticMedia: false,
      uploadedAt: null,
      error: null,
      snapshots: [],
      proposals: [],
      experiment: null,
    });
    expect(decided.publication as unknown).toEqual({
      ...publicationWire,
      error: null,
      snapshots: [
        { ...publicationWire.snapshots[0]!, impressions: null, impressionsCtr: null },
        publicationWire.snapshots[1]!,
      ],
      proposals: [
        { ...publicationWire.proposals[0]!, thumbnailFile: null },
        publicationWire.proposals[1]!,
      ],
      experiment: {
        ...publicationWire.experiment,
        arms: [
          publicationWire.experiment.arms[0]!,
          { ...publicationWire.experiment.arms[1]!, endedAt: null },
        ],
      },
    });
    expect(calls.map(({ method, request }) => ({ method, request }))).toEqual([
      {
        method: 'PublishPackage',
        request: {
          projectId: 'p1',
          privacyStatus: 'unlisted',
          madeForKids: true,
          syntheticMedia: false,
        },
      },
      {
        method: 'PublishPackage',
        request: {
          projectId: 'p1',
          privacyStatus: 'private',
          publishAt: '2026-11-10T09:00:00.000Z',
          madeForKids: false,
          syntheticMedia: true,
        },
      },
      { method: 'DecideProposal', request: { projectId: 'p1', proposalId: 'pr1', accept: false } },
      { method: 'StartExperiment', request: { projectId: 'p1', armHours: 48 } },
      { method: 'AdoptExperimentWinner', request: { id: 'p1' } },
    ]);
    expect(calls.every((c) => c.token.length === 1 && c.token[0] === TOKEN)).toBe(true);
  });

  it('publishPackage rejects with ControlPlaneUnavailableError within the deadline when nothing listens, and a publish the gate refuses with ControlPlanePermissionDeniedError', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const deniedSocket = tempSocketPath();
    server = await startOperatorServer(deniedSocket, [], {
      failWith: { code: grpc.status.PERMISSION_DENIED, details: 'youtube may not write' },
    });
    const deniedClient = createControlPlaneClient({
      socketPath: deniedSocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });
    client = createControlPlaneClient({
      socketPath: tempSocketPath(),
      token: TOKEN,
      protoDir: PROTO_DIR,
      deadlineMs: 500,
    });

    const startedAt = Date.now();
    const unreachable = await client.publishPackage('p1', 'private', null, false, false).then(
      () => null,
      (err: unknown) => err,
    );
    const elapsed = Date.now() - startedAt;
    const refused = await deniedClient.publishPackage('p1', 'private', null, false, false).then(
      () => null,
      (err: unknown) => err,
    );
    deniedClient.close();

    expect(unreachable).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(elapsed).toBeLessThan(5_000);
    expect(refused).toBeInstanceOf(ControlPlanePermissionDeniedError);
    expect((refused as Error).message).toBe('youtube may not write');
  });

  it('an approval the control plane refuses rejects with ControlPlaneInvalidArgumentError carrying what still blocks it, and an assembly without the grant with ControlPlanePermissionDeniedError', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const blocks = 'Confirm the media rights first. 1 check failed.';
    const deniedSocket = tempSocketPath();
    const denied = await startOperatorServer(deniedSocket, [], {
      failWith: { code: grpc.status.PERMISSION_DENIED, details: 'youtube may not write' },
    });
    const deniedClient = createControlPlaneClient({
      socketPath: deniedSocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });
    const socketPath = tempSocketPath();
    server = await startOperatorServer(socketPath, [], {
      failWith: { code: grpc.status.INVALID_ARGUMENT, details: blocks },
    });
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const refused = await client.approvePackage('p1').then(
      () => null,
      (err: unknown) => err,
    );
    const ungranted = await deniedClient.assembleProject('p1').then(
      () => null,
      (err: unknown) => err,
    );
    deniedClient.close();
    denied.forceShutdown();

    expect(refused).toBeInstanceOf(ControlPlaneInvalidArgumentError);
    expect((refused as Error).message).toBe(blocks);
    expect(ungranted).toBeInstanceOf(ControlPlanePermissionDeniedError);
    expect((ungranted as Error).message).toBe('youtube may not write');
  });

  it('delegateProject rejects with ControlPlaneUnavailableError within the deadline when nothing listens, and a delegation without the YouTube grant with ControlPlanePermissionDeniedError carrying the gate reason', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const reason =
      'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.';
    const deniedSocket = tempSocketPath();
    const denied = await startOperatorServer(deniedSocket, [], {
      failWith: { code: grpc.status.PERMISSION_DENIED, details: reason },
    });
    const deniedClient = createControlPlaneClient({
      socketPath: deniedSocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });
    client = createControlPlaneClient({
      socketPath: tempSocketPath(),
      token: TOKEN,
      protoDir: PROTO_DIR,
      deadlineMs: 500,
    });

    const startedAt = Date.now();
    const unreachable = await client.delegateProject('p1', '', '', 'gentle', '').then(
      () => null,
      (err: unknown) => err,
    );
    const elapsed = Date.now() - startedAt;
    const refused = await deniedClient.delegateProject('p1', '', '', 'gentle', '').then(
      () => null,
      (err: unknown) => err,
    );
    deniedClient.close();
    denied.forceShutdown();

    expect(unreachable).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(elapsed).toBeLessThan(5_000);
    expect(refused).toBeInstanceOf(ControlPlanePermissionDeniedError);
    expect((refused as Error).message).toBe(reason);
  });

  it('getVoiceSamplesDirectory rejects with ControlPlaneUnavailableError within the deadline when nothing listens', async () => {
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
    const unreachable = await client.getVoiceSamplesDirectory().then(
      () => null,
      (err: unknown) => err,
    );

    expect(unreachable).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(Date.now() - startedAt).toBeLessThan(5_000);
  });

  it('deleteProject sends the id with the token, maps NOT_FOUND to ControlPlaneNotFoundError, and rejects with ControlPlaneUnavailableError within the deadline when nothing listens', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const calls: OperatorCall[] = [];
    const socketPath = tempSocketPath();
    server = await startOperatorServer(socketPath, calls);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });
    const deleted = await client.deleteProject('p1');
    const missingSocket = tempSocketPath();
    const missing = await startOperatorServer(missingSocket, [], {
      failWith: { code: grpc.status.NOT_FOUND, details: 'Project p9 not found' },
    });
    const missingClient = createControlPlaneClient({
      socketPath: missingSocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });
    const notFound = await missingClient.deleteProject('p9').then(
      () => null,
      (err: unknown) => err,
    );
    missingClient.close();
    missing.forceShutdown();
    const unreachableClient = createControlPlaneClient({
      socketPath: tempSocketPath(),
      token: TOKEN,
      protoDir: PROTO_DIR,
      deadlineMs: 500,
    });
    const startedAt = Date.now();
    const unreachable = await unreachableClient.deleteProject('p1').then(
      () => null,
      (err: unknown) => err,
    );
    const elapsed = Date.now() - startedAt;
    unreachableClient.close();

    expect(deleted).toBeUndefined();
    expect(calls.map((c) => [c.method, c.request, c.token])).toEqual([
      ['DeleteProject', { id: 'p1' }, [TOKEN]],
    ]);
    expect(notFound).toBeInstanceOf(ControlPlaneNotFoundError);
    expect((notFound as Error).message).toBe('Project p9 not found');
    expect(unreachable).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(elapsed).toBeLessThan(5_000);
  });

  it('approvePackage rejects with ControlPlaneUnavailableError within the deadline when nothing listens', async () => {
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
    const error = await client.approvePackage('p1').then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(Date.now() - startedAt).toBeLessThan(5_000);
  });

  it('getReadiness rejects with ControlPlaneUnavailableError within the deadline when nothing listens', async () => {
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
    const error = await client.getReadiness().then(
      () => null,
      (err: unknown) => err,
    );

    expect(error).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(Date.now() - startedAt).toBeLessThan(5_000);
  });

  it('maps the comments, the ideas and the sync state with absent optional fields as null, and sends list, sync, decision, reply approval and idea decision with the token', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const socketPath = tempSocketPath();
    const calls: OperatorCall[] = [];
    server = await startOperatorServer(socketPath, calls);
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const listed = await client.listComments();
    const requested = await client.requestCommentSync();
    await client.decideComment('Ugx2', 'hold');
    await client.approveReply('Ugx1', 'Yes, it is next week.');
    await client.decideIdea('idea-Ugx3', false);

    expect(listed).toEqual({
      comments: [
        commentWire,
        {
          ...commentWire,
          id: 'Ugx2',
          threadId: 'Ugx2',
          kind: 'spam',
          status: 'quarantined',
          draft: '',
          approvedText: null,
          replyId: null,
          error: null,
          decidedAt: null,
        },
      ],
      ideas: [
        {
          id: 'idea-Ugx1',
          text: 'Span<T> in depth',
          commentId: 'Ugx1',
          status: 'used',
          projectId: 'p9',
        },
        {
          id: 'idea-Ugx3',
          text: 'Records vs classes',
          commentId: 'Ugx3',
          status: 'proposed',
          projectId: null,
        },
      ],
      syncedAt: '2026-11-03T08:05:00Z',
      syncRequestedAt: '2026-11-03T09:30:00Z',
      syncError: 'YouTube answered 503.',
    });
    expect(requested).toEqual({
      comments: [],
      ideas: [],
      syncedAt: null,
      syncRequestedAt: null,
      syncError: null,
    });
    expect(calls.map(({ method, request }) => ({ method, request }))).toEqual([
      { method: 'ListComments', request: {} },
      { method: 'RequestCommentSync', request: {} },
      { method: 'DecideComment', request: { commentId: 'Ugx2', decision: 'hold' } },
      { method: 'ApproveReply', request: { commentId: 'Ugx1', text: 'Yes, it is next week.' } },
      { method: 'DecideIdea', request: { ideaId: 'idea-Ugx3', use: false } },
    ]);
    expect(calls.every((c) => c.token.length === 1 && c.token[0] === TOKEN)).toBe(true);
  });

  it('a ListComments answer over the 4 MB gRPC default, 1000 comments of 2000 two-byte characters with 1000-character drafts and replies, reaches the desktop mapped', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const comments = Array.from({ length: 1000 }, (_, i) => ({
      ...commentWire,
      id: `Ugx${i}`,
      threadId: `Ugx${i}`,
      text: 'é'.repeat(2000),
      draft: 'ç'.repeat(1000),
      approvedText: 'ã'.repeat(1000),
    }));
    const socketPath = tempSocketPath();
    server = await startOperatorServer(socketPath, [], {
      comments: { ...commentsWire, comments },
    });
    client = createControlPlaneClient({ socketPath, token: TOKEN, protoDir: PROTO_DIR });

    const listed = await client.listComments();

    expect(
      comments.reduce(
        (bytes, c) => bytes + Buffer.byteLength(c.text + c.draft + c.approvedText),
        0,
      ),
    ).toBeGreaterThan(4 * 1024 * 1024);
    expect(listed.comments.length).toBe(1000);
    expect(listed.comments[999]).toEqual({ ...comments[999]!, decidedAt: commentWire.decidedAt });
    expect(listed.syncError).toBe('YouTube answered 503.');
  });

  it('approveReply rejects with ControlPlaneUnavailableError within the deadline when nothing listens, a reply the control plane refuses with ControlPlaneInvalidArgumentError and an unknown comment with ControlPlaneNotFoundError, each with its message', async () => {
    if (process.platform === 'win32') {
      expect(dialError()).toBeInstanceOf(ControlPlaneUnavailableError);
      return;
    }

    const quarantined = 'A quarantined comment gets no reply; mark it not spam first.';
    const unknownSocket = tempSocketPath();
    const unknown = await startOperatorServer(unknownSocket, [], {
      failWith: { code: grpc.status.NOT_FOUND, details: "Unknown comment 'Ugx9'." },
    });
    const unknownClient = createControlPlaneClient({
      socketPath: unknownSocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });
    const refusingSocket = tempSocketPath();
    server = await startOperatorServer(refusingSocket, [], {
      failWith: { code: grpc.status.INVALID_ARGUMENT, details: quarantined },
    });
    const refusingClient = createControlPlaneClient({
      socketPath: refusingSocket,
      token: TOKEN,
      protoDir: PROTO_DIR,
    });
    client = createControlPlaneClient({
      socketPath: tempSocketPath(),
      token: TOKEN,
      protoDir: PROTO_DIR,
      deadlineMs: 500,
    });

    const startedAt = Date.now();
    const unreachable = await client.approveReply('Ugx1', 'Thanks!').then(
      () => null,
      (err: unknown) => err,
    );
    const elapsed = Date.now() - startedAt;
    const refused = await refusingClient.approveReply('Ugx2', 'Thanks!').then(
      () => null,
      (err: unknown) => err,
    );
    const missing = await unknownClient.approveReply('Ugx9', 'Thanks!').then(
      () => null,
      (err: unknown) => err,
    );
    refusingClient.close();
    unknownClient.close();
    unknown.forceShutdown();

    expect(unreachable).toBeInstanceOf(ControlPlaneUnavailableError);
    expect(elapsed).toBeLessThan(5_000);
    expect(refused).toBeInstanceOf(ControlPlaneInvalidArgumentError);
    expect((refused as Error).message).toBe(quarantined);
    expect(missing).toBeInstanceOf(ControlPlaneNotFoundError);
    expect((missing as Error).message).toBe("Unknown comment 'Ugx9'.");
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
