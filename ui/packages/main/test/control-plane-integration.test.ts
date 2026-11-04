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
  ControlPlaneNotFoundError,
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
const developerCommentsBefore = snapshotOf(developerStateFile('comments.json'));

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

        const refused = await client.produceProject(started.id, '', '', 'gentle').then(
          () => null,
          (err: unknown) => err,
        );
        await client.decideClaim(started.id, 'c1', 'supported');
        const queued = await client.produceProject(started.id, '', '', 'gentle');

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
    'ProduceProject keeps the narration voice the owner chose for the video, and the host refuses a malformed one with its sentence and keeps the project as it was',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      await client.setWriteGrant('youtube', true, true);
      try {
        const started = await client.startProject('Records in C#');
        await saveAsWorker({
          id: started.id,
          topic: started.topic,
          origin: started.origin,
          status: 'script_ready',
        });

        const refused = await client.produceProject(started.id, 'Cori medium', '', 'gentle').then(
          () => null,
          (err: unknown) => err,
        );
        const untouched = await client.getProject(started.id);
        const queued = await client.produceProject(
          started.id,
          'piper:en_GB-aru-medium#3',
          '',
          'gentle',
        );
        const reread = await client.getProject(started.id);

        expect(refused).toBeInstanceOf(ControlPlaneInvalidArgumentError);
        expect((refused as Error).message).toBe(
          'A narration voice is an id of at most 80 letters, digits, _ : . # or -.',
        );
        expect([untouched.status, untouched.narrationVoice]).toEqual(['script_ready', '']);
        expect([queued.status, queued.narrationVoice]).toEqual([
          'production_queued',
          'piper:en_GB-aru-medium#3',
        ]);
        expect(reread.narrationVoice).toBe('piper:en_GB-aru-medium#3');
      } finally {
        await client.setWriteGrant('youtube', false, false);
      }
    },
    TEST_TIMEOUT_MS,
  );

  it(
    'ProduceProject keeps the visual brief and the motion, the host refuses a 501-character brief with its sentence, and GetVoiceSamplesDirectory answers an absolute folder under the data folder that scout-media serves',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      await client.setWriteGrant('youtube', true, true);
      try {
        const started = await client.startProject('Pattern matching in C#');
        await saveAsWorker({
          id: started.id,
          topic: started.topic,
          origin: started.origin,
          status: 'script_ready',
        });

        const refused = await client.produceProject(started.id, '', 'b'.repeat(501), 'still').then(
          () => null,
          (err: unknown) => err,
        );
        const queued = await client.produceProject(
          started.id,
          '',
          'Chalk sketches on a dark slate',
          'dynamic',
        );
        const reread = await client.getProject(started.id);
        const samples = await client.getVoiceSamplesDirectory();
        writeFileSync(path.join(samples, 'piper-en_GB-aru-medium-3.wav'), 'RIFF sample');
        const served = await createMediaProtocolHandler(
          (id) => client.getMediaDirectory(id),
          Date.now,
          () => client.getVoiceSamplesDirectory(),
        )(new Request(`${MEDIA_SCHEME}://voice-samples/piper-en_GB-aru-medium-3.wav`));

        expect(refused).toBeInstanceOf(ControlPlaneInvalidArgumentError);
        expect((refused as Error).message).toBe('A visual brief is at most 500 characters.');
        expect([queued.status, queued.visualBrief, queued.motion]).toEqual([
          'production_queued',
          'Chalk sketches on a dark slate',
          'dynamic',
        ]);
        expect([reread.visualBrief, reread.motion]).toEqual([
          'Chalk sketches on a dark slate',
          'dynamic',
        ]);
        expect([path.isAbsolute(samples), samples.startsWith(socketDir)]).toEqual([true, true]);
        expect([served.status, served.headers.get('content-type')]).toEqual([200, 'audio/wav']);
      } finally {
        await client.setWriteGrant('youtube', false, false);
      }
    },
    TEST_TIMEOUT_MS,
  );

  it(
    'DeleteProject removes a script-ready project without a grant, GetProject then answers not-found, and a producing project is refused with the sentence',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      await client.setWriteGrant('youtube', true, true);
      try {
        const ready = await client.startProject('Span<T> in C#');
        await saveAsWorker({
          id: ready.id,
          topic: ready.topic,
          origin: ready.origin,
          status: 'script_ready',
          claims: [],
        });
        const running = await client.startProject('Records in C#');
        const runningIdentity = { id: running.id, topic: running.topic, origin: running.origin };
        await saveAsWorker({ ...runningIdentity, status: 'script_ready', claims: [] });
        await client.produceProject(running.id, '', '', 'gentle');
        await takeForProductionAsWorker(running.id);
        await saveAsWorker({ ...runningIdentity, status: 'producing', claims: [] });
        await client.setWriteGrant('youtube', false, false);

        await client.deleteProject(ready.id);
        const gone = await client.getProject(ready.id).then(
          () => null,
          (err: unknown) => err,
        );
        const refused = await client.deleteProject(running.id).then(
          () => null,
          (err: unknown) => err,
        );
        const listed = (await client.listProjects(50)).map((p) => p.id);

        expect(gone).toBeInstanceOf(ControlPlaneNotFoundError);
        expect(refused).toBeInstanceOf(ControlPlaneInvalidArgumentError);
        expect((refused as Error).message).toBe(
          'A project that is researching, scripting, producing, assembling or uploading cannot be deleted; wait for it to finish or fail.',
        );
        expect(listed).toContain(running.id);
        expect(listed).not.toContain(ready.id);
      } finally {
        await client.setWriteGrant('youtube', false, false);
      }
    },
    TEST_TIMEOUT_MS,
  );

  it(
    'DelegateProject is refused without the YouTube grant, and with it queues the script-ready project for production, delegated, in the voice the owner chose, whatever claims are pending',
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
        await client.setWriteGrant('youtube', false, false);
        const ungranted = await client
          .delegateProject(started.id, 'OpenAI:nova', '', 'gentle')
          .then(
            () => null,
            (err: unknown) => err,
          );
        await client.setWriteGrant('youtube', true, true);
        const delegated = await client.delegateProject(started.id, 'OpenAI:nova', '', 'gentle');

        expect(ungranted).toBeInstanceOf(ControlPlanePermissionDeniedError);
        expect((ungranted as Error).message).toBe(
          'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.',
        );
        expect([delegated.status, delegated.delegated, delegated.narrationVoice]).toEqual([
          'production_queued',
          true,
          'OpenAI:nova',
        ]);
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
        stockPictures: false,
        provider: '',
        ffmpegAvailable: false,
        youtubeSignedIn: false,
        notes: ['The Scout worker has not reported yet.'],
        voices: [],
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
        await client.produceProject(started.id, '', '', 'gentle');
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

  it(
    "an owner background attached through main lands in the host's media directory as owner-background-<hex>, the project records it with no scene, and scout-media serves it",
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      const handlers = new Map<string, (event: unknown, req: unknown) => Promise<unknown>>();
      const source = path.join(socketDir, 'Studio wall.jpg');
      writeFileSync(source, Buffer.from([0xff, 0xd8, 0xff, 0xe0]));
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

        const { project } = (await handlers.get('scout:operator.sceneMedia')!(null, {
          projectId: started.id,
          sceneId: '',
          kind: 'background',
        })) as { project: { backgroundFile: string | null; status: string } };
        const fileName = project.backgroundFile!;
        const directory = await client.getMediaDirectory(started.id);
        const served = await createMediaProtocolHandler((id) => client.getMediaDirectory(id))(
          new Request(`${MEDIA_SCHEME}://${started.id}/${fileName}`),
        );

        expect(fileName).toMatch(/^owner-background-[0-9a-f]{12}\.jpg$/);
        expect(project.status).toBe('script_ready');
        expect(readdirSync(directory)).toEqual([fileName]);
        expect([served.status, served.headers.get('content-type')]).toEqual([200, 'image/jpeg']);
      } finally {
        await client.setWriteGrant('youtube', false, false);
      }
    },
    TEST_TIMEOUT_MS,
  );
});

async function mediaReadyAsWorker(client: ControlPlaneClient, topic: string) {
  const started = await client.startProject(topic);
  const identity = { id: started.id, topic: started.topic, origin: started.origin };
  const scenes = [
    {
      id: 's1',
      order: 0,
      narration: 'Records compare by value.',
      visualPrompt: 'Two equal cards',
      status: 'ready',
      audioSource: 'none',
      visualSource: 'text_card',
      durationMs: 4000,
    },
  ];
  await saveAsWorker({ ...identity, status: 'script_ready' });
  await client.produceProject(started.id, '', '', 'gentle');
  await takeForProductionAsWorker(started.id);
  await saveAsWorker({ ...identity, status: 'producing', scenes });
  await saveAsWorker({ ...identity, status: 'media_ready', scenes });
  return { identity, scenes };
}

describe('operator assembly over UDS', () => {
  it(
    'AssembleProject refuses a project whose media is not ready, naming its status',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      await client.setWriteGrant('youtube', true, true);
      try {
        const started = await client.startProject('Records in C#');

        const refused = await client.assembleProject(started.id).then(
          () => null,
          (err: unknown) => err,
        );

        expect(refused).toBeInstanceOf(ControlPlaneInvalidArgumentError);
        expect((refused as Error).message).toBe(
          'Only a project with its media ready, an assembled one, or a failed one with every scene ready is assembled; this one is queued.',
        );
      } finally {
        await client.setWriteGrant('youtube', false, false);
      }
    },
    TEST_TIMEOUT_MS,
  );

  it(
    "the owner assembles a media-ready video, the worker's assembled package arrives with its files served by scout-media, and the owner edits its metadata, confirms the rights and approves it",
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      await client.setWriteGrant('youtube', true, true);
      try {
        const { identity, scenes } = await mediaReadyAsWorker(client, 'Records in C#');
        const queued = await client.assembleProject(identity.id);
        await takeForProductionAsWorker(identity.id);
        const directory = await client.getMediaDirectory(identity.id);
        for (const name of ['video.mp4', 'short-1.mp4', 'thumbnail.png']) {
          writeFileSync(path.join(directory, name), `${name} bytes`);
        }
        await saveAsWorker({
          ...identity,
          status: 'assembled',
          scenes,
          package: {
            status: 'assembled',
            videoFile: 'video.mp4',
            shorts: [
              { file: 'short-1.mp4', startMs: 0, durationMs: 30000, hook: 'Equal by value.' },
            ],
            thumbnailFile: 'thumbnail.png',
            checks: [{ name: 'Loudness', result: 'pass', detail: '-14 LUFS' }],
            title: 'Records explained',
            description: 'Value equality in C#.',
            tags: ['dotnet'],
            durationMs: 4000,
          },
        });
        const served = await createMediaProtocolHandler((id) => client.getMediaDirectory(id))(
          new Request(`${MEDIA_SCHEME}://${identity.id}/video.mp4`),
        );

        const unconfirmed = await client.approvePackage(identity.id).then(
          () => null,
          (err: unknown) => err,
        );
        const edited = await client.editPackageMetadata(
          identity.id,
          '  Records, explained ',
          ' Value equality in C#. ',
          [' dotnet ', 'records'],
        );
        const confirmed = await client.confirmMediaRights(identity.id);
        const approved = await client.approvePackage(identity.id);

        expect([queued.status, queued.package?.status]).toEqual([
          'assembly_queued',
          'assembly_queued',
        ]);
        expect([served.status, served.headers.get('content-type')]).toEqual([200, 'video/mp4']);
        expect(unconfirmed).toBeInstanceOf(ControlPlaneInvalidArgumentError);
        expect((unconfirmed as Error).message).toBe('Confirm the media rights first.');
        expect(edited.package).toMatchObject({
          title: 'Records, explained',
          description: 'Value equality in C#.',
          tags: ['dotnet', 'records'],
          metadataEdited: true,
          videoFile: 'video.mp4',
          shorts: [{ file: 'short-1.mp4', startMs: 0, durationMs: 30000, hook: 'Equal by value.' }],
          thumbnailFile: 'thumbnail.png',
        });
        expect(confirmed.package?.rightsConfirmed).toBe(true);
        expect([approved.status, approved.package?.status]).toEqual(['approved', 'approved']);
        expect(Number.isNaN(Date.parse(approved.package?.approvedAt ?? ''))).toBe(false);
      } finally {
        await client.setWriteGrant('youtube', false, false);
      }
    },
    TEST_TIMEOUT_MS,
  );
});

async function approvedAsOwner(client: ControlPlaneClient, topic: string) {
  const { identity, scenes } = await mediaReadyAsWorker(client, topic);
  await client.assembleProject(identity.id);
  await takeForProductionAsWorker(identity.id);
  const directory = await client.getMediaDirectory(identity.id);
  for (const name of ['video.mp4', 'thumbnail.png']) {
    writeFileSync(path.join(directory, name), `${name} bytes`);
  }
  await saveAsWorker({
    ...identity,
    status: 'assembled',
    scenes,
    package: {
      status: 'assembled',
      videoFile: 'video.mp4',
      thumbnailFile: 'thumbnail.png',
      checks: [{ name: 'Loudness', result: 'pass', detail: '-14 LUFS' }],
      title: 'Records explained',
      description: 'Value equality in C#.',
      tags: ['dotnet'],
      durationMs: 4000,
    },
  });
  await client.confirmMediaRights(identity.id);
  await client.approvePackage(identity.id);
  return { identity, scenes };
}

describe('operator publishing over UDS', () => {
  it(
    'a publish without the YouTube grant is refused, a schedule with unlisted is refused with the control plane words, and an approved package published private with a future time keeps the owner settings',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      const future = new Date(Date.now() + 7 * 24 * 3_600_000).toISOString();
      await client.setWriteGrant('youtube', true, true);
      try {
        const { identity } = await approvedAsOwner(client, 'Records in C#');
        await client.setWriteGrant('youtube', false, false);
        const ungranted = await client
          .publishPackage(identity.id, 'private', null, false, false)
          .then(
            () => null,
            (err: unknown) => err,
          );
        await client.setWriteGrant('youtube', true, true);
        const unlisted = await client
          .publishPackage(identity.id, 'unlisted', future, false, false)
          .then(
            () => null,
            (err: unknown) => err,
          );

        const published = await client.publishPackage(identity.id, 'private', future, false, true);

        expect(ungranted).toBeInstanceOf(ControlPlanePermissionDeniedError);
        expect(unlisted).toBeInstanceOf(ControlPlaneInvalidArgumentError);
        expect((unlisted as Error).message).toBe(
          'A video with a publish time is uploaded as private; YouTube makes it public at that time.',
        );
        expect(published.publication).toMatchObject({
          status: 'publish_approved',
          videoId: null,
          privacyStatus: 'private',
          madeForKids: false,
          syntheticMedia: true,
          experiment: null,
        });
        expect(Date.parse(published.publication?.publishAt ?? '')).toBe(Date.parse(future));
      } finally {
        await client.setWriteGrant('youtube', false, false);
      }
    },
    TEST_TIMEOUT_MS,
  );

  it(
    "the worker's scheduled video and its proposal reach the desktop, the owner accepts the proposal and starts a 48-hour title experiment of the control and the accepted title, an adoption before a winner is refused, and deleting the YouTube credentials removes the snapshots and keeps the video id",
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);
      const future = new Date(Date.now() + 7 * 24 * 3_600_000).toISOString();
      await client.setWriteGrant('youtube', true, true);
      try {
        const { identity, scenes } = await approvedAsOwner(client, 'Records in C#');
        const queued = await client.publishPackage(identity.id, 'private', future, false, false);
        await saveAsWorker({
          ...identity,
          status: 'approved',
          scenes,
          publication: {
            status: 'scheduled',
            videoId: 'vid-records-1',
            privacyStatus: 'private',
            publishAt: queued.publication?.publishAt,
            madeForKids: false,
            syntheticMedia: false,
            uploadedAt: new Date().toISOString(),
            snapshots: [
              {
                window: '24h',
                takenAt: new Date().toISOString(),
                views: 120,
                averageViewPercentage: 41.5,
                retention: [
                  { elapsedRatio: 0, audienceWatchRatio: 1 },
                  { elapsedRatio: 1, audienceWatchRatio: 0.3 },
                ],
              },
            ],
            proposals: [
              {
                id: 'pr1',
                kind: 'title',
                text: 'Records in 60 seconds',
                reason: 'CTR under the channel median',
                status: 'proposed',
              },
            ],
          },
        });

        const read = await client.getProject(identity.id);
        const accepted = await client.decideProposal(identity.id, 'pr1', true);
        const started = await client.startExperiment(identity.id, 48);
        const adoption = await client.adoptExperimentWinner(identity.id).then(
          () => null,
          (err: unknown) => err,
        );
        await client.deleteFeatureCredentials('youtube');
        const purged = await client.getProject(identity.id);

        expect(read.publication?.videoId).toBe('vid-records-1');
        expect(read.publication?.snapshots[0]).toMatchObject({
          window: '24h',
          views: 120,
          impressions: null,
          impressionsCtr: null,
          retention: [
            { elapsedRatio: 0, audienceWatchRatio: 1 },
            { elapsedRatio: 1, audienceWatchRatio: 0.3 },
          ],
        });
        expect(accepted.publication?.proposals.map((p) => p.status)).toEqual(['accepted']);
        expect(started.publication?.experiment).toMatchObject({ kind: 'title', status: 'running' });
        expect(started.publication?.experiment?.arms.map((a) => [a.id, a.label, a.value])).toEqual([
          ['control', 'Control', 'Records explained'],
          ['variant-a', 'Variant A', 'Records in 60 seconds'],
        ]);
        expect(adoption).toBeInstanceOf(ControlPlaneInvalidArgumentError);
        expect((adoption as Error).message).toBe(
          'Only a concluded experiment with a winner is adopted.',
        );
        expect(purged.publication?.snapshots).toEqual([]);
        expect(purged.publication?.videoId).toBe('vid-records-1');
        expect(purged.publication?.proposals.map((p) => p.id)).toEqual(['pr1']);
      } finally {
        await client.setWriteGrant('youtube', false, false);
      }
    },
    TEST_TIMEOUT_MS,
  );
});

describe('operator comments over UDS', () => {
  it(
    'real-host comments round trip: the desktop reads no comments before a sync, a sync request is refused without the YouTube grant and stamped with it, and a reply approval for a comment the host does not have is refused as unknown',
    async () => {
      if (process.platform === 'win32') {
        expect(result.kind).toBe('unsupported');
        return;
      }
      const client = connect((result as LaunchedControlPlane).token);

      const before = await client.listComments();
      const ungranted = await client.requestCommentSync().then(
        () => null,
        (err: unknown) => err,
      );
      await client.setWriteGrant('youtube', true, true);
      try {
        const requested = await client.requestCommentSync();
        const listed = await client.listComments();
        const unknown = await client.approveReply('Ugx-unknown', 'Thanks for watching.').then(
          () => null,
          (err: unknown) => err,
        );

        expect(before).toEqual({
          comments: [],
          ideas: [],
          syncedAt: null,
          syncRequestedAt: null,
          syncError: null,
        });
        expect(ungranted).toBeInstanceOf(ControlPlanePermissionDeniedError);
        expect(Number.isNaN(Date.parse(requested.syncRequestedAt ?? ''))).toBe(false);
        expect(requested.syncError).toBeNull();
        expect(listed.syncRequestedAt).toBe(requested.syncRequestedAt);
        expect(unknown).toBeInstanceOf(ControlPlaneNotFoundError);
        expect((unknown as Error).message).toBe("Unknown comment 'Ugx-unknown'.");
        expect(existsSync(path.join(socketDir, 'comments.json'))).toBe(true);
        expect(snapshotOf(developerStateFile('comments.json'))).toBe(developerCommentsBefore);
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
