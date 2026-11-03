import { describe, it, expect, beforeAll, afterAll } from 'bun:test';
import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import {
  ControlPlaneInvalidArgumentError,
  ControlPlaneNotImplementedError,
  ControlPlanePermissionDeniedError,
  ControlPlaneUnauthenticatedError,
  ControlPlaneWriteConfirmationError,
} from '@scout/shared';
import { createControlPlaneClient } from '../src/control-plane/client.js';
import type { ControlPlaneClient } from '../src/control-plane/client.js';
import { launchControlPlane } from '../src/control-plane/launcher.js';
import { registerOperatorHandlers } from '../src/ipc/operator.js';
import { MEDIA_SCHEME, createMediaProtocolHandler } from '../src/operator/media.js';
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

function developerStateFile(name: string): string {
  return path.join(path.dirname(developerFeatureStateFile()), name);
}

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
const developerOperatorBefore = ['mandate.json', 'projects.json'].map((name) =>
  snapshotOf(developerStateFile(name)),
);

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
    'the worker token the launcher hands the real host may not list or grant features',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const worker = connect((result as LaunchedControlPlane).workerToken);

      const listed = await worker.listFeatures().then(
        () => null,
        (err: unknown) => err,
      );
      const granted = await worker.setWriteGrant('youtube', true, true).then(
        () => null,
        (err: unknown) => err,
      );
      const youtube = (await connect((result as LaunchedControlPlane).token).listFeatures()).find(
        (feature) => feature.id === 'youtube',
      );

      expect([listed, granted].map((err) => (err as { code?: unknown } | null)?.code)).toEqual([
        7, 7,
      ]);
      expect(youtube?.granted).toBe(false);
    },
    TEST_TIMEOUT_MS,
  );

  it(
    'ListFeatures returns github, kubernetes and youtube from the real Scout.Host.ControlPlane binary',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }

      const features = await connect((result as LaunchedControlPlane).token).listFeatures();

      expect(features.map((f) => [f.id, f.displayName])).toEqual([
        ['github', 'GitHub'],
        ['kubernetes', 'Kubernetes'],
        ['youtube', 'YouTube'],
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
      const enableError = await client.enableFeature('github', false).then(
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
    'youtube declares executive writes with read, write and irreversible rows and starts without a grant',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }

      const youtube = (await connect((result as LaunchedControlPlane).token).listFeatures()).find(
        (f) => f.id === 'youtube',
      );
      const accesses = new Set(youtube?.scopeMatrix?.map((row) => row.access));

      expect([youtube?.writeClass, youtube?.granted, youtube?.grantedAt]).toEqual([
        'executive',
        false,
        null,
      ]);
      expect([...accesses].sort()).toEqual(['irreversible', 'read', 'write']);
    },
    TEST_TIMEOUT_MS,
  );

  it(
    'Enable of youtube without confirming its writes is refused with the confirmation message, and with confirming says it is not available yet',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      await client.setFeatureCredentials('youtube', { api_key: 'integration-key' });

      const unconfirmed = await client.enableFeature('youtube', false).then(
        () => null,
        (err: unknown) => err,
      );
      const confirmed = await client.enableFeature('youtube', true).then(
        () => null,
        (err: unknown) => err,
      );

      expect(unconfirmed).toBeInstanceOf(ControlPlaneWriteConfirmationError);
      expect((unconfirmed as Error).message).toStartWith(
        'Confirm the writes of youtube before enabling it',
      );
      expect(confirmed).toBeInstanceOf(ControlPlaneNotImplementedError);
    },
    TEST_TIMEOUT_MS,
  );

  it(
    'SetWriteGrant records the owner grant of youtube only with confirmWrites, and a revoke clears it',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);

      const unconfirmed = await client.setWriteGrant('youtube', true, false).then(
        () => null,
        (err: unknown) => err,
      );
      const granted = await client.setWriteGrant('youtube', true, true);
      const revoked = await client.setWriteGrant('youtube', false, false);

      expect(unconfirmed).toBeInstanceOf(ControlPlaneWriteConfirmationError);
      expect((unconfirmed as Error).message).toStartWith(
        'Confirm the writes of youtube before granting them',
      );
      expect([granted.granted, granted.grantedAt === null]).toEqual([true, false]);
      expect([revoked.granted, revoked.grantedAt]).toEqual([false, null]);
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

describe('operator over UDS', () => {
  it(
    'the desktop saves the mandate with SaveMandate and reads the same mandate back with GetMandate',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      const fields = {
        objective: 'Grow a .NET channel',
        audience: 'Backend developers',
        pillars: ['Performance', 'Testing'],
        cadence: 'weekly',
        language: 'en',
        voiceAndStyle: 'calm',
        blockedTopics: ['politics'],
        targetMetric: 'watch_hours',
        targetValue: '500',
      };

      const before = await client.getMandate();
      const saved = await client.saveMandate(fields);
      const read = await client.getMandate();

      expect(before).toBeNull();
      expect(read).toEqual(saved);
      expect({ ...read, updatedAt: undefined }).toEqual({ ...fields, updatedAt: undefined });
      expect(Number.isNaN(Date.parse(read?.updatedAt ?? ''))).toBe(false);
      expect(existsSync(path.join(socketDir, 'mandate.json'))).toBe(true);
    },
    TEST_TIMEOUT_MS,
  );

  it(
    'StartProject is refused without the owner grant of youtube, and with it queues a project from the desktop that ListProjects shows queued',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);

      const refused = await client.startProject('Records in C#').then(
        () => null,
        (err: unknown) => err,
      );
      await client.setWriteGrant('youtube', true, true);
      const { started, listed } = await (async () => {
        try {
          const project = await client.startProject('  Span<T> in C# ');
          return { started: project, listed: await client.listProjects(50) };
        } finally {
          await client.setWriteGrant('youtube', false, false);
        }
      })();

      expect(refused).toBeInstanceOf(ControlPlanePermissionDeniedError);
      expect((refused as Error).message).toBe(
        'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.',
      );
      expect([started.topic, started.status, started.origin]).toEqual([
        'Span<T> in C#',
        'queued',
        'desktop',
      ]);
      expect(listed.find((p) => p.id === started.id)?.status).toBe('queued');
    },
    TEST_TIMEOUT_MS,
  );

  it(
    "operator state is written to the test's temp folder and never to the developer's mandate or projects file",
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }

      const client = connect((result as LaunchedControlPlane).token);
      await client.setWriteGrant('youtube', true, true);
      try {
        await client.startProject('Records in C#');
      } finally {
        await client.setWriteGrant('youtube', false, false);
      }

      expect(existsSync(path.join(socketDir, 'projects.json'))).toBe(true);
      expect(
        ['mandate.json', 'projects.json'].map((name) => snapshotOf(developerStateFile(name))),
      ).toEqual(developerOperatorBefore);
    },
    TEST_TIMEOUT_MS,
  );
});

function saveAsWorker(project: object): Promise<unknown> {
  return callAsWorker('SaveOperatorProject', { project });
}

async function takeForProductionAsWorker(projectId: string): Promise<void> {
  for (let i = 0; i < 20; i++) {
    const next = (await callAsWorker('NextQueuedProject', {})) as { project?: { id: string } };
    if (next.project === undefined || next.project === null) break;
    if (next.project.id === projectId) return;
  }
  throw new Error(`The worker never took project ${projectId} for production.`);
}

function callAsWorker(method: string, request: object): Promise<unknown> {
  const definition = protoLoader.loadSync('protos/worker.proto', {
    includeDirs: [path.dirname(PROTO_DIR)],
    enums: String,
    defaults: true,
  });
  const loaded = grpc.loadPackageDefinition(definition) as unknown as {
    scout: { controlplane: { v1: { WorkerGate: grpc.ServiceClientConstructor } } };
  };
  const launched = result as LaunchedControlPlane;
  const gate = new loaded.scout.controlplane.v1.WorkerGate(
    `unix:${path.resolve(launched.socketPath)}`,
    grpc.credentials.createInsecure(),
  );
  const metadata = new grpc.Metadata();
  metadata.set('x-scout-token', launched.workerToken);
  return new Promise((resolve, reject) => {
    (
      gate as unknown as Record<
        string,
        (
          req: object,
          md: grpc.Metadata,
          cb: (err: grpc.ServiceError | null, res: unknown) => void,
        ) => void
      >
    )[method]!.call(gate, request, metadata, (err: grpc.ServiceError | null, res: unknown) => {
      gate.close();
      if (err === null) resolve(res);
      else reject(err);
    });
  });
}

describe('operator production over UDS', () => {
  it(
    'ProduceProject is refused while a claim is pending, naming how many, and queues production once every claim is decided',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      await client.setWriteGrant('youtube', true, true);
      try {
        const started = await client.startProject('Span<T> in C#');
        await saveAsWorker({
          id: started.id,
          topic: started.topic,
          origin: started.origin,
          status: 'script_ready',
          claims: [{ id: 'c1', text: 'Span<T> is a ref struct.', risk: 'low', status: 'pending' }],
        });

        const refused = await client.produceProject(started.id).then(
          () => null,
          (err: unknown) => err,
        );
        await client.decideClaim(started.id, 'c1', 'supported');
        const queued = await client.produceProject(started.id);

        expect(refused).toBeInstanceOf(ControlPlaneInvalidArgumentError);
        expect((refused as Error).message).toBe(
          '1 claim is still pending; decide it before production.',
        );
        expect(queued.status).toBe('production_queued');
      } finally {
        await client.setWriteGrant('youtube', false, false);
      }
    },
    TEST_TIMEOUT_MS,
  );

  it(
    'GetReadiness before the worker has reported answers nothing available, no provider, no report time and the note that the worker has not reported',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);

      const readiness = await client.getReadiness();

      expect(readiness).toEqual({
        speechAvailable: false,
        imagesAvailable: false,
        provider: '',
        ffmpegAvailable: false,
        youtubeSignedIn: false,
        notes: ['The Scout worker has not reported yet.'],
        reportedAt: null,
      });
    },
    TEST_TIMEOUT_MS,
  );

  it(
    "an owner image attached through main lands in the host's media directory under a generated name, the scene records it, and scout-media serves it",
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      const handlers = new Map<string, (event: unknown, req: unknown) => Promise<unknown>>();
      const source = path.join(socketDir, 'Owner picture.png');
      writeFileSync(source, Buffer.from([0x89, 0x50, 0x4e, 0x47]));
      registerOperatorHandlers(
        {
          handle: (channel: string, handler: (event: unknown, req: unknown) => Promise<unknown>) =>
            handlers.set(channel, handler),
          removeHandler: (channel: string) => handlers.delete(channel),
        } as never,
        async () => client,
        nullLogger,
        async () => source,
      );
      await client.setWriteGrant('youtube', true, true);
      try {
        const started = await client.startProject('Records in C#');
        await saveAsWorker({
          id: started.id,
          topic: started.topic,
          origin: started.origin,
          status: 'script_ready',
        });
        await client.produceProject(started.id);
        await takeForProductionAsWorker(started.id);
        await saveAsWorker({
          id: started.id,
          topic: started.topic,
          origin: started.origin,
          status: 'producing',
          scenes: [
            {
              id: 's1',
              order: 0,
              narration: 'Records compare by value.',
              visualPrompt: 'Two equal cards',
              status: 'planned',
              audioSource: 'none',
              visualSource: 'text_card',
            },
          ],
        });

        const { project } = (await handlers.get('scout:operator.sceneMedia')!(null, {
          projectId: started.id,
          sceneId: 's1',
          kind: 'visual',
        })) as { project: { scenes: { visualFile: string | null; visualSource: string }[] } };
        const fileName = project.scenes[0]!.visualFile!;
        const directory = await client.getMediaDirectory(started.id);
        const served = await createMediaProtocolHandler((id) => client.getMediaDirectory(id))(
          new Request(`${MEDIA_SCHEME}://${started.id}/${fileName}`),
        );

        expect(fileName).toMatch(/^owner-s1-[0-9a-f]{12}\.png$/);
        expect(project.scenes[0]!.visualSource).toBe('owner');
        expect(directory.startsWith(socketDir)).toBe(true);
        expect(readdirSync(directory)).toEqual([fileName]);
        expect([served.status, served.headers.get('content-type')]).toEqual([200, 'image/png']);
      } finally {
        await client.setWriteGrant('youtube', false, false);
      }
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

        expect(features.length).toBe(3);
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
