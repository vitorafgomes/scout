import { describe, it, expect, afterEach, beforeEach, mock } from 'bun:test';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  CHANNEL,
  ControlPlaneInvalidArgumentError,
  ControlPlaneNotFoundError,
  ControlPlanePermissionDeniedError,
  ControlPlaneUnavailableError,
  readIpcError,
  type MandateDto,
  type OperatorProjectDto,
  type ReadinessDto,
  type SceneMediaKind,
} from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { registerOperatorHandlers } from '../src/ipc/operator.js';
import type { OperatorClient } from '../src/ipc/operator.js';
import type { Logger } from '../src/util/logger.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => Promise<unknown>;

interface FakeIpcMain extends Pick<IpcMain, 'handle' | 'removeHandler'> {
  handlers: Map<string, IpcHandler>;
}

function makeFakeIpcMain(): FakeIpcMain {
  const handlers: Map<string, IpcHandler> = new Map();
  return {
    handlers,
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
}

const logger: Logger = {
  debug() {},
  info() {},
  warn() {},
  error() {},
  child() {
    return logger;
  },
};

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

const mandate: MandateDto = {
  objective: 'Grow a .NET channel',
  audience: 'Backend developers',
  pillars: ['Performance', 'Testing'],
  cadence: 'weekly',
  language: 'en',
  voiceAndStyle: 'calm',
  blockedTopics: ['politics'],
  targetMetric: 'views',
  targetValue: '10000',
  updatedAt: '2026-11-03T09:00:00Z',
};

const project: OperatorProjectDto = {
  id: 'p1',
  topic: 'Span<T> in C#',
  status: 'queued',
  origin: 'desktop',
  createdAt: '2026-11-03T09:00:00Z',
  updatedAt: '2026-11-03T09:00:00Z',
  candidates: [],
  script: null,
  claims: [],
  error: null,
  scenes: [],
  package: null,
};

const readiness: ReadinessDto = {
  speechAvailable: false,
  imagesAvailable: false,
  provider: 'google',
  ffmpegAvailable: true,
  youtubeSignedIn: true,
  notes: ['Speech and images need OpenAI or Azure OpenAI.'],
  reportedAt: '2026-11-03T09:00:00Z',
};

const PNG = Buffer.from([0x89, 0x50, 0x4e, 0x47]);
const tempDirs: string[] = [];

function tempDir(): string {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'scout-ipc-media-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

type StubClient = { [K in keyof OperatorClient]: ReturnType<typeof mock> };

function makeClient(overrides: Partial<StubClient> = {}): StubClient {
  return {
    getMandate: mock(async () => mandate),
    saveMandate: mock(async () => mandate),
    listProjects: mock(async () => [project]),
    getProject: mock(async () => project),
    startProject: mock(async () => project),
    decideClaim: mock(async () => project),
    produceProject: mock(async () => ({ ...project, status: 'production_queued' })),
    reorderScenes: mock(async () => project),
    setSceneLocked: mock(async () => project),
    regenerateScene: mock(async () => project),
    attachSceneMedia: mock(async () => project),
    getMediaDirectory: mock(async () => '/nowhere'),
    getReadiness: mock(async () => readiness),
    assembleProject: mock(async () => ({ ...project, status: 'assembly_queued' })),
    confirmMediaRights: mock(async () => project),
    editPackageMetadata: mock(async () => project),
    approvePackage: mock(async () => ({ ...project, status: 'approved' })),
    ...overrides,
  };
}

async function rejection(
  promise: Promise<unknown>,
): Promise<{ code: string | null; message: string }> {
  const thrown = await promise.then(
    () => 'resolved without an error',
    (err: unknown) => err,
  );
  return readIpcError(thrown instanceof Error ? new Error(thrown.message) : thrown);
}

describe('registerOperatorHandlers', () => {
  let ipcMain: FakeIpcMain;
  let client: StubClient;
  let chosen: string | null;
  let chooseFile: ReturnType<typeof mock>;

  beforeEach(() => {
    ipcMain = makeFakeIpcMain();
    client = makeClient();
    chosen = null;
    chooseFile = mock(async (_kind: SceneMediaKind) => chosen);
    registerOperatorHandlers(
      ipcMain,
      async () => client as unknown as OperatorClient,
      logger,
      chooseFile,
    );
  });

  const invoke = (channel: string, req?: unknown): Promise<unknown> =>
    ipcMain.handlers.get(channel)!(NULL_EVENT, req);

  it('the mandate channel returns the saved mandate, and null while none is saved', async () => {
    const saved = await invoke(CHANNEL.operatorMandate);
    client.getMandate.mockImplementation(async () => null);
    const none = await invoke(CHANNEL.operatorMandate);

    expect(saved).toEqual({ mandate });
    expect(none).toEqual({ mandate: null });
  });

  it('saving the mandate forwards the trimmed fields only, without empty pillars or blocked topics', async () => {
    await invoke(CHANNEL.operatorSaveMandate, {
      objective: '  Grow a .NET channel ',
      audience: ' Backend developers',
      pillars: [' Performance ', '', 'Testing'],
      cadence: 'weekly ',
      language: ' en',
      voiceAndStyle: 'calm',
      blockedTopics: ['politics', '  '],
      targetMetric: 'views',
      targetValue: ' 10000 ',
      updatedAt: 'forged',
      extra: 'dropped',
    });

    const { updatedAt: _stamp, ...fields } = mandate;
    expect(client.saveMandate.mock.calls).toEqual([[fields]]);
  });

  it('a mandate without an objective or a language, with more than 5 pillars or a pillar over 80 characters, is refused before reaching the control plane', async () => {
    const { updatedAt: _stamp, ...fields } = mandate;

    const refused = [
      await rejection(invoke(CHANNEL.operatorSaveMandate, { ...fields, objective: '  ' })),
      await rejection(invoke(CHANNEL.operatorSaveMandate, { ...fields, language: '' })),
      await rejection(
        invoke(CHANNEL.operatorSaveMandate, { ...fields, pillars: ['a', 'b', 'c', 'd', 'e', 'f'] }),
      ),
      await rejection(
        invoke(CHANNEL.operatorSaveMandate, { ...fields, pillars: ['x'.repeat(81)] }),
      ),
    ];
    const accepted = await invoke(CHANNEL.operatorSaveMandate, {
      ...fields,
      pillars: ['a', 'b', 'c', 'd', 'x'.repeat(80)],
    });

    expect(refused).toEqual([
      { code: 'operator/invalid-request', message: 'The objective is required.' },
      { code: 'operator/invalid-request', message: 'The language is required.' },
      { code: 'operator/invalid-request', message: 'At most 5 pillars.' },
      { code: 'operator/invalid-request', message: 'Each pillar is at most 80 characters.' },
    ]);
    expect(accepted).toEqual(mandate);
    expect(client.saveMandate).toHaveBeenCalledTimes(1);
  });

  it('starting a project forwards the trimmed topic and refuses one under 3 or over 200 characters', async () => {
    const started = await invoke(CHANNEL.operatorStartProject, { topic: '  Span<T> in C# ' });
    const short = await rejection(invoke(CHANNEL.operatorStartProject, { topic: ' ab ' }));
    const long = await rejection(invoke(CHANNEL.operatorStartProject, { topic: 'x'.repeat(201) }));
    await invoke(CHANNEL.operatorStartProject, { topic: 'x'.repeat(200) });

    expect(started).toEqual(project);
    expect(client.startProject.mock.calls).toEqual([['Span<T> in C#'], ['x'.repeat(200)]]);
    expect([short, long]).toEqual([
      { code: 'operator/invalid-request', message: 'The topic must be 3 to 200 characters.' },
      { code: 'operator/invalid-request', message: 'The topic must be 3 to 200 characters.' },
    ]);
  });

  it('a claim decision forwards supported and unsupported without a note, and waived with its trimmed note', async () => {
    await invoke(CHANNEL.operatorDecideClaim, {
      projectId: 'p1',
      claimId: 'c1',
      status: 'supported',
    });
    await invoke(CHANNEL.operatorDecideClaim, {
      projectId: 'p1',
      claimId: 'c2',
      status: 'unsupported',
      note: ' ',
    });
    await invoke(CHANNEL.operatorDecideClaim, {
      projectId: 'p1',
      claimId: 'c3',
      status: 'waived',
      note: ' Common knowledge ',
    });

    expect(client.decideClaim.mock.calls).toEqual([
      ['p1', 'c1', 'supported', undefined],
      ['p1', 'c2', 'unsupported', undefined],
      ['p1', 'c3', 'waived', 'Common knowledge'],
    ]);
  });

  it('a waived claim without a note, an unknown status or a missing id is refused before reaching the control plane', async () => {
    const refused = [
      await rejection(
        invoke(CHANNEL.operatorDecideClaim, {
          projectId: 'p1',
          claimId: 'c1',
          status: 'waived',
          note: '  ',
        }),
      ),
      await rejection(
        invoke(CHANNEL.operatorDecideClaim, { projectId: 'p1', claimId: 'c1', status: 'approved' }),
      ),
      await rejection(
        invoke(CHANNEL.operatorDecideClaim, { projectId: 'p1', status: 'supported' }),
      ),
      await rejection(invoke(CHANNEL.operatorDecideClaim, { claimId: 'c1', status: 'supported' })),
    ];

    expect(refused).toEqual([
      { code: 'operator/invalid-request', message: 'A waived claim needs a note saying why.' },
      {
        code: 'operator/invalid-request',
        message: 'A claim is decided as supported, unsupported or waived.',
      },
      { code: 'operator/invalid-request', message: 'The claim id is required.' },
      { code: 'operator/invalid-request', message: 'The project id is required.' },
    ]);
    expect(client.decideClaim).not.toHaveBeenCalled();
  });

  it('the project list forwards a bounded limit and a project read forwards its id', async () => {
    const listed = await invoke(CHANNEL.operatorProjects, { limit: 20 });
    await invoke(CHANNEL.operatorProjects, { limit: 10_000 });
    await invoke(CHANNEL.operatorProjects, {});
    const one = await invoke(CHANNEL.operatorProject, { id: 'p1' });
    const noId = await rejection(invoke(CHANNEL.operatorProject, { id: '' }));

    expect(listed).toEqual({ projects: [project] });
    expect(client.listProjects.mock.calls).toEqual([[20], [200], [50]]);
    expect(one).toEqual(project);
    expect(client.getProject.mock.calls).toEqual([['p1']]);
    expect(noId).toEqual({
      code: 'operator/invalid-request',
      message: 'The project id is required.',
    });
  });

  it('the control plane refusing a request reaches the renderer as control-plane/invalid-argument with its message', async () => {
    client.saveMandate.mockImplementation(async () => {
      throw new ControlPlaneInvalidArgumentError('cadence is not recognised');
    });
    const { updatedAt: _stamp, ...fields } = mandate;

    const refused = await rejection(invoke(CHANNEL.operatorSaveMandate, fields));

    expect(refused).toEqual({
      code: 'control-plane/invalid-argument',
      message: 'cadence is not recognised',
    });
  });

  it('a start the owner has not granted reaches the renderer as control-plane/permission-denied with the gate reason', async () => {
    const reason =
      'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.';
    client.startProject.mockImplementation(async () => {
      throw new ControlPlanePermissionDeniedError(reason);
    });

    const refused = await rejection(
      invoke(CHANNEL.operatorStartProject, { topic: 'Records in C#' }),
    );

    expect(refused).toEqual({ code: 'control-plane/permission-denied', message: reason });
  });

  it('a control plane that is not running reaches the renderer as control-plane/unavailable', async () => {
    const down = makeFakeIpcMain();
    registerOperatorHandlers(
      down,
      () => Promise.reject(new ControlPlaneUnavailableError('socket did not appear')),
      logger,
      chooseFile,
    );

    const refused = await rejection(down.handlers.get(CHANNEL.operatorProjects)!(NULL_EVENT, {}));
    const attach = await rejection(
      down.handlers.get(CHANNEL.operatorSceneMedia)!(NULL_EVENT, {
        projectId: 'p1',
        sceneId: 's1',
        kind: 'audio',
      }),
    );

    expect(refused).toEqual({
      code: 'control-plane/unavailable',
      message: 'socket did not appear',
    });
    expect(attach).toEqual(refused);
    expect(chooseFile).not.toHaveBeenCalled();
  });

  it('produce forwards the project id and refuses a missing one before reaching the control plane', async () => {
    const produced = await invoke(CHANNEL.operatorProduction, { id: ' p1 ' });
    const missing = await rejection(invoke(CHANNEL.operatorProduction, {}));

    expect((produced as OperatorProjectDto).status).toBe('production_queued');
    expect(client.produceProject.mock.calls).toEqual([['p1']]);
    expect(missing).toEqual({
      code: 'operator/invalid-request',
      message: 'The project id is required.',
    });
  });

  it('a produce refused while claims are pending reaches the renderer as control-plane/invalid-argument naming how many', async () => {
    const reason = '2 claims are still pending; decide every claim before producing the video.';
    client.produceProject = mock(async () => {
      throw new ControlPlaneInvalidArgumentError(reason);
    });

    const refused = await rejection(invoke(CHANNEL.operatorProduction, { id: 'p1' }));

    expect(refused).toEqual({ code: 'control-plane/invalid-argument', message: reason });
  });

  it('scene order forwards the ids in the new order and refuses an empty, repeated or non-text list', async () => {
    await invoke(CHANNEL.operatorSceneOrder, { projectId: 'p1', sceneIds: ['s3', 's1', 's2'] });
    const refused = await Promise.all([
      rejection(invoke(CHANNEL.operatorSceneOrder, { projectId: 'p1', sceneIds: [] })),
      rejection(invoke(CHANNEL.operatorSceneOrder, { projectId: 'p1', sceneIds: ['s1', 's1'] })),
      rejection(invoke(CHANNEL.operatorSceneOrder, { projectId: 'p1', sceneIds: ['s1', 7] })),
      rejection(invoke(CHANNEL.operatorSceneOrder, { projectId: 'p1', sceneIds: 's1' })),
    ]);

    expect(client.reorderScenes.mock.calls).toEqual([['p1', ['s3', 's1', 's2']]]);
    expect(new Set(refused.map((r) => `${r.code}: ${r.message}`))).toEqual(
      new Set(['operator/invalid-request: The new order lists every scene id once.']),
    );
    expect(refused).toHaveLength(4);
  });

  it('scene lock forwards locked and unlocked, refuses a lock that is not true or false, and regeneration forwards the scene', async () => {
    await invoke(CHANNEL.operatorSceneLock, { projectId: 'p1', sceneId: 's1', locked: true });
    await invoke(CHANNEL.operatorSceneLock, { projectId: 'p1', sceneId: 's1', locked: false });
    const notBoolean = await rejection(
      invoke(CHANNEL.operatorSceneLock, { projectId: 'p1', sceneId: 's1', locked: 'yes' }),
    );
    await invoke(CHANNEL.operatorSceneRegeneration, { projectId: 'p1', sceneId: 's2' });
    const noScene = await rejection(invoke(CHANNEL.operatorSceneRegeneration, { projectId: 'p1' }));

    expect(client.setSceneLocked.mock.calls).toEqual([
      ['p1', 's1', true],
      ['p1', 's1', false],
    ]);
    expect(notBoolean.message).toBe('A scene is locked or unlocked.');
    expect(client.regenerateScene.mock.calls).toEqual([['p1', 's2']]);
    expect(noScene.message).toBe('The scene id is required.');
  });

  it('a regenerate refused on a locked scene reaches the renderer with the control plane reason', async () => {
    client.regenerateScene = mock(async () => {
      throw new ControlPlaneInvalidArgumentError('Scene s1 is locked; unlock it to regenerate.');
    });

    const refused = await rejection(
      invoke(CHANNEL.operatorSceneRegeneration, { projectId: 'p1', sceneId: 's1' }),
    );

    expect(refused).toEqual({
      code: 'control-plane/invalid-argument',
      message: 'Scene s1 is locked; unlock it to regenerate.',
    });
  });

  it('readiness answers what the worker last reported', async () => {
    expect(await invoke(CHANNEL.operatorReadiness)).toEqual(readiness);
  });

  it("attach asks the owner for a file of the kind, copies it into the project's media directory under a generated name and records that name, and no path reaches the renderer", async () => {
    const media = path.join(tempDir(), 'p1', 'media');
    const source = path.join(tempDir(), 'Holiday photo.PNG');
    writeFileSync(source, PNG);
    chosen = source;
    client.getMediaDirectory = mock(async () => media);

    const response = await invoke(CHANNEL.operatorSceneMedia, {
      projectId: 'p1',
      sceneId: 's1',
      kind: 'visual',
    });

    const [copied] = readdirSync(media);
    expect(copied).toMatch(/^owner-s1-[0-9a-f]{12}\.png$/);
    expect(readFileSync(path.join(media, copied!)).equals(PNG)).toBe(true);
    expect(chooseFile.mock.calls).toEqual([['visual']]);
    expect(client.getMediaDirectory.mock.calls).toEqual([['p1']]);
    expect(client.attachSceneMedia.mock.calls).toEqual([['p1', 's1', 'visual', copied]]);
    expect(response).toEqual({ project });
    expect(JSON.stringify(response)).not.toContain(media);
    expect(JSON.stringify(response)).not.toContain(source);
  });

  it('attach answers no project and copies nothing when the owner closes the dialog', async () => {
    const media = tempDir();
    client.getMediaDirectory = mock(async () => media);

    const response = await invoke(CHANNEL.operatorSceneMedia, {
      projectId: 'p1',
      sceneId: 's1',
      kind: 'audio',
    });

    expect(response).toEqual({ project: null });
    expect(chooseFile.mock.calls).toEqual([['audio']]);
    expect(client.attachSceneMedia).not.toHaveBeenCalled();
    expect(readdirSync(media)).toEqual([]);
  });

  it('attach deletes the copied file when the control plane refuses to record it, and the renderer gets the refusal', async () => {
    const media = tempDir();
    const source = path.join(tempDir(), 'voice.mp3');
    writeFileSync(source, 'ID3');
    chosen = source;
    client.getMediaDirectory = mock(async () => media);
    client.attachSceneMedia = mock(async () => {
      throw new ControlPlaneNotFoundError('Scene s9 not found in project p1');
    });

    const refused = await rejection(
      invoke(CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: 's9', kind: 'audio' }),
    );

    expect(refused).toEqual({
      code: 'control-plane/not-found',
      message: 'Scene s9 not found in project p1',
    });
    expect(client.attachSceneMedia).toHaveBeenCalledTimes(1);
    expect(readdirSync(media)).toEqual([]);
  });

  it('attach refuses ids with separators or dot segments and an unknown kind before asking the owner or the control plane, and ignores a path the renderer adds', async () => {
    const bad = [
      { projectId: '../p1', sceneId: 's1', kind: 'audio' },
      { projectId: 'p1', sceneId: 's1/../../x', kind: 'audio' },
      { projectId: 'p1', sceneId: 's1\\x', kind: 'visual' },
      { projectId: 'p1', sceneId: 's1', kind: 'video' },
    ];

    const refused = await Promise.all(
      bad.map((req) => rejection(invoke(CHANNEL.operatorSceneMedia, req))),
    );
    const media = tempDir();
    client.getMediaDirectory = mock(async () => media);
    await invoke(CHANNEL.operatorSceneMedia, {
      projectId: 'p1',
      sceneId: 's1',
      kind: 'visual',
      path: '/etc/passwd',
      fileName: '../../etc/passwd',
    });

    expect(refused.map((r) => r.code)).toEqual(bad.map(() => 'operator/invalid-request'));
    expect(chooseFile.mock.calls).toEqual([['visual']]);
    expect(client.attachSceneMedia).not.toHaveBeenCalled();
    expect(readdirSync(media)).toEqual([]);
  });

  it('attach refuses a file of the wrong type or over the cap without a path in the message, and records nothing', async () => {
    const media = tempDir();
    const source = path.join(tempDir(), 'notes.txt');
    writeFileSync(source, 'not media');
    chosen = source;
    client.getMediaDirectory = mock(async () => media);

    const refused = await rejection(
      invoke(CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: 's1', kind: 'visual' }),
    );

    expect(refused).toEqual({
      code: 'operator/invalid-request',
      message: 'A picture must be a png, jpg, jpeg or webp file.',
    });
    expect(client.attachSceneMedia).not.toHaveBeenCalled();
    expect(readdirSync(media)).toEqual([]);
  });

  it('assembly, media rights and approval forward the trimmed project id, and refuse a missing one before reaching the control plane', async () => {
    const assembled = await invoke(CHANNEL.operatorAssembly, { id: ' p1 ' });
    await invoke(CHANNEL.operatorMediaRights, { id: 'p1' });
    const approved = await invoke(CHANNEL.operatorPackageApproval, { id: 'p1 ' });
    const missing = await Promise.all(
      [CHANNEL.operatorAssembly, CHANNEL.operatorMediaRights, CHANNEL.operatorPackageApproval].map(
        (channel) => rejection(invoke(channel, { id: '  ' })),
      ),
    );

    expect((assembled as OperatorProjectDto).status).toBe('assembly_queued');
    expect((approved as OperatorProjectDto).status).toBe('approved');
    expect(client.assembleProject.mock.calls).toEqual([['p1']]);
    expect(client.confirmMediaRights.mock.calls).toEqual([['p1']]);
    expect(client.approvePackage.mock.calls).toEqual([['p1']]);
    expect(missing).toEqual(
      missing.map(() => ({
        code: 'operator/invalid-request',
        message: 'The project id is required.',
      })),
    );
  });

  it('package metadata forwards the trimmed title, description and tags without empty tags', async () => {
    await invoke(CHANNEL.operatorPackageMetadata, {
      projectId: ' p1 ',
      title: '  Span<T> explained ',
      description: ' A tour of Span<T>. ',
      tags: [' dotnet ', '', '  ', 'span'],
    });

    expect(client.editPackageMetadata.mock.calls).toEqual([
      ['p1', 'Span<T> explained', 'A tour of Span<T>.', ['dotnet', 'span']],
    ]);
  });

  it("package metadata outside YouTube's limits is refused before reaching the control plane, naming every limit it breaks", async () => {
    const at = (length: number): string => 'x'.repeat(length);
    const refused = await Promise.all([
      rejection(
        invoke(CHANNEL.operatorPackageMetadata, {
          projectId: 'p1',
          title: '  ',
          description: '',
          tags: [],
        }),
      ),
      rejection(
        invoke(CHANNEL.operatorPackageMetadata, {
          projectId: 'p1',
          title: at(101),
          description: at(5001),
          tags: [],
        }),
      ),
      rejection(
        invoke(CHANNEL.operatorPackageMetadata, {
          projectId: 'p1',
          title: 'Span',
          description: '',
          tags: [at(101)],
        }),
      ),
      rejection(
        invoke(CHANNEL.operatorPackageMetadata, {
          projectId: 'p1',
          title: 'Span',
          description: '',
          tags: [at(100), at(100), at(100), at(100), at(100), 'x'],
        }),
      ),
      rejection(
        invoke(CHANNEL.operatorPackageMetadata, {
          projectId: 'p1',
          title: 'Span',
          description: '',
          tags: [at(100), at(100), at(100), at(100), `${at(90)} bends`],
        }),
      ),
      rejection(
        invoke(CHANNEL.operatorPackageMetadata, {
          projectId: '',
          title: 'Span',
          description: '',
          tags: [],
        }),
      ),
    ]);
    const atTheLimits = await invoke(CHANNEL.operatorPackageMetadata, {
      projectId: 'p1',
      title: at(100),
      description: at(5000),
      tags: [at(100), at(100), at(100), at(100), at(96)],
    });

    expect(refused.map((r) => r.message)).toEqual([
      'The title is 1 to 100 characters.',
      'The title is 1 to 100 characters. The description is at most 5000 characters.',
      'Each tag is 1 to 100 characters.',
      'The tags are at most 500 characters together.',
      'The tags are at most 500 characters together.',
      'The project id is required.',
    ]);
    expect(refused.every((r) => r.code === 'operator/invalid-request')).toBe(true);
    expect(atTheLimits).toEqual(project);
    expect(client.editPackageMetadata.mock.calls.length).toBe(1);
  });

  it('an approval the control plane refuses reaches the renderer as control-plane/invalid-argument with what still blocks it', async () => {
    const reason = 'Confirm the media rights first. 2 checks failed.';
    client.approvePackage = mock(async () => {
      throw new ControlPlaneInvalidArgumentError(reason);
    });

    const refused = await rejection(invoke(CHANNEL.operatorPackageApproval, { id: 'p1' }));

    expect(refused).toEqual({ code: 'control-plane/invalid-argument', message: reason });
  });

  it('the returned unregister removes the sixteen operator handlers', () => {
    const own = makeFakeIpcMain();
    const unregister = registerOperatorHandlers(
      own,
      async () => client as unknown as OperatorClient,
      logger,
      chooseFile,
    );

    const registered = [...own.handlers.keys()].sort();
    unregister();

    expect(registered).toEqual(
      [
        CHANNEL.operatorMandate,
        CHANNEL.operatorSaveMandate,
        CHANNEL.operatorProjects,
        CHANNEL.operatorProject,
        CHANNEL.operatorStartProject,
        CHANNEL.operatorDecideClaim,
        CHANNEL.operatorProduction,
        CHANNEL.operatorSceneOrder,
        CHANNEL.operatorSceneLock,
        CHANNEL.operatorSceneRegeneration,
        CHANNEL.operatorSceneMedia,
        CHANNEL.operatorReadiness,
        CHANNEL.operatorAssembly,
        CHANNEL.operatorMediaRights,
        CHANNEL.operatorPackageMetadata,
        CHANNEL.operatorPackageApproval,
      ].sort(),
    );
    expect(own.handlers.size).toBe(0);
  });
});
