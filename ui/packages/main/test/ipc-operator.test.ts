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
  type CommentsViewDto,
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

const readiness: ReadinessDto = {
  speechAvailable: false,
  imagesAvailable: false,
  stockPictures: false,
  provider: 'google',
  ffmpegAvailable: true,
  youtubeSignedIn: true,
  notes: ['Speech and images need OpenAI or Azure OpenAI.'],
  voices: [
    {
      id: 'piper:en_GB-cori-medium',
      label: 'Cori (medium)',
      source: 'piper',
      sampleFile: 'piper-en_GB-cori-medium.wav',
    },
  ],
  reportedAt: '2026-11-03T09:00:00Z',
  transcriptionAvailable: false,
  lipSyncAvailable: false,
};

const view: CommentsViewDto = {
  comments: [],
  ideas: [],
  syncedAt: null,
  syncRequestedAt: '2026-11-03T09:30:00Z',
  syncError: null,
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
    delegateProject: mock(async () => ({
      ...project,
      status: 'production_queued',
      delegated: true,
    })),
    deleteProject: mock(async () => undefined),
    reorderScenes: mock(async () => project),
    setSceneLocked: mock(async () => project),
    regenerateScene: mock(async () => project),
    editSceneText: mock(async () => ({ ...project, status: 'production_queued' })),
    regenerateNarration: mock(async () => ({ ...project, status: 'production_queued' })),
    setMouthBox: mock(async () => project),
    regeneratePresenter: mock(async () => project),
    attachSceneMedia: mock(async () => project),
    getMediaDirectory: mock(async () => '/nowhere'),
    getReadiness: mock(async () => readiness),
    assembleProject: mock(async () => ({ ...project, status: 'assembly_queued' })),
    confirmMediaRights: mock(async () => project),
    editPackageMetadata: mock(async () => project),
    approvePackage: mock(async () => ({ ...project, status: 'approved' })),
    publishPackage: mock(async () => project),
    decideProposal: mock(async () => project),
    startExperiment: mock(async () => project),
    adoptExperimentWinner: mock(async () => project),
    listComments: mock(async () => view),
    requestCommentSync: mock(async () => view),
    decideComment: mock(async () => view),
    approveReply: mock(async () => view),
    decideIdea: mock(async () => view),
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
      narrationVoice: 'piper:en_GB-aru-medium#3',
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
    expect(client.startProject.mock.calls).toEqual([
      ['Span<T> in C#', 'generated_pictures', '', ''],
      ['x'.repeat(200), 'generated_pictures', '', ''],
    ]);
    expect([short, long]).toEqual([
      { code: 'operator/invalid-request', message: 'The topic must be 3 to 200 characters.' },
      { code: 'operator/invalid-request', message: 'The topic must be 3 to 200 characters.' },
    ]);
  });

  it('starting a project forwards the topic, the format and the trimmed direction, and refuses an unknown format or a direction over 1000 characters with the control plane sentence', async () => {
    await invoke(CHANNEL.operatorStartProject, {
      topic: 'Span<T> in C#',
      format: ' own_pictures ',
      direction: '  Explain it to a junior developer  ',
    });
    await invoke(CHANNEL.operatorStartProject, {
      topic: 'Records in C#',
      format: 'generated_pictures',
      direction: ` ${'d'.repeat(1000)} `,
    });
    await invoke(CHANNEL.operatorStartProject, { topic: 'Pattern matching', format: '' });
    const refused = await Promise.all(
      [
        { format: 'ai_presenter' },
        { format: 'Own_Pictures' },
        { format: 'own_pictures', direction: 'd'.repeat(1001) },
      ].map((req) => rejection(invoke(CHANNEL.operatorStartProject, { topic: 'Span', ...req }))),
    );

    expect(client.startProject.mock.calls).toEqual([
      ['Span<T> in C#', 'own_pictures', 'Explain it to a junior developer', ''],
      ['Records in C#', 'generated_pictures', 'd'.repeat(1000), ''],
      ['Pattern matching', 'generated_pictures', '', ''],
    ]);
    const format = {
      code: 'operator/invalid-request',
      message: 'The format is generated_pictures, own_pictures, own_video or animated_character.',
    };
    expect(refused).toEqual([
      format,
      format,
      { code: 'operator/invalid-request', message: 'A direction is at most 1000 characters.' },
    ]);
  });

  it('starts a project in own_video and refuses ai_presenter with the new sentence', async () => {
    await invoke(CHANNEL.operatorStartProject, {
      topic: 'My talk at the meetup',
      format: ' own_video ',
      direction: '',
    });
    const refused = await rejection(
      invoke(CHANNEL.operatorStartProject, { topic: 'Span', format: 'ai_presenter' }),
    );

    expect(client.startProject.mock.calls).toEqual([
      ['My talk at the meetup', 'own_video', '', ''],
    ]);
    expect(refused).toEqual({
      code: 'operator/invalid-request',
      message: 'The format is generated_pictures, own_pictures, own_video or animated_character.',
    });
  });

  it('a presenter description over 300 characters is refused with the sentence', async () => {
    await invoke(CHANNEL.operatorStartProject, {
      topic: 'Span<T> in C#',
      format: 'animated_character',
      direction: '',
      presenter: '  A friendly robot in a workshop  ',
    });
    await invoke(CHANNEL.operatorProduction, {
      id: 'p1',
      narrationVoice: '',
      presenter: ` ${'r'.repeat(300)} `,
    });
    await invoke(CHANNEL.operatorDelegation, {
      id: 'p1',
      narrationVoice: '',
      presenter: ' A cartoon owl on a bookshelf ',
    });
    const tooLong = 'r'.repeat(301);
    const refused = await Promise.all([
      rejection(
        invoke(CHANNEL.operatorStartProject, {
          topic: 'Span<T> in C#',
          format: 'animated_character',
          presenter: tooLong,
        }),
      ),
      rejection(invoke(CHANNEL.operatorProduction, { id: 'p1', presenter: tooLong })),
      rejection(invoke(CHANNEL.operatorDelegation, { id: 'p1', presenter: tooLong })),
    ]);

    expect(client.startProject.mock.calls).toEqual([
      ['Span<T> in C#', 'animated_character', '', 'A friendly robot in a workshop'],
    ]);
    expect(client.produceProject.mock.calls).toEqual([['p1', '', '', 'gentle', 'r'.repeat(300)]]);
    expect(client.delegateProject.mock.calls).toEqual([
      ['p1', '', '', 'gentle', 'A cartoon owl on a bookshelf'],
    ]);
    const sentence = {
      code: 'operator/invalid-request',
      message: 'A presenter description is at most 300 characters.',
    };
    expect(refused).toEqual([sentence, sentence, sentence]);
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

  it('produce and delegation forward the trimmed project id, narration voice and direction with the motion, an empty motion as gentle, and refuse a missing id before reaching the control plane', async () => {
    const produced = await invoke(CHANNEL.operatorProduction, {
      id: ' p1 ',
      narrationVoice: ' piper:en_GB-aru-medium#3 ',
      direction: '  Chalk on slate  ',
      motion: ' dynamic ',
    });
    const delegated = await invoke(CHANNEL.operatorDelegation, {
      id: 'p1 ',
      narrationVoice: '',
      direction: '',
      motion: '',
    });
    const missing = await Promise.all([
      rejection(invoke(CHANNEL.operatorProduction, { narrationVoice: '' })),
      rejection(invoke(CHANNEL.operatorDelegation, { narrationVoice: '' })),
    ]);

    expect((produced as OperatorProjectDto).status).toBe('production_queued');
    expect((delegated as OperatorProjectDto).delegated).toBe(true);
    expect(client.produceProject.mock.calls).toEqual([
      ['p1', 'piper:en_GB-aru-medium#3', 'Chalk on slate', 'dynamic', ''],
    ]);
    expect(client.delegateProject.mock.calls).toEqual([['p1', '', '', 'gentle', '']]);
    expect(missing).toEqual([
      { code: 'operator/invalid-request', message: 'The project id is required.' },
      { code: 'operator/invalid-request', message: 'The project id is required.' },
    ]);
  });

  it('deleting a project forwards the trimmed id and answers deleted, refuses a missing id before the control plane, and passes a control plane refusal on', async () => {
    const answer = await invoke(CHANNEL.operatorProjectDeletion, { id: ' p1 ' });
    const missing = await Promise.all([
      rejection(invoke(CHANNEL.operatorProjectDeletion, { id: '  ' })),
      rejection(invoke(CHANNEL.operatorProjectDeletion, undefined)),
    ]);
    const callsBeforeRefusal = client.deleteProject.mock.calls.length;
    const reason = 'The project has pending YouTube actions; finish or cancel them first.';
    client.deleteProject = mock(async () => {
      throw new ControlPlaneInvalidArgumentError(reason);
    });

    const refused = await rejection(invoke(CHANNEL.operatorProjectDeletion, { id: 'p1' }));

    expect(answer).toEqual({ deleted: true });
    expect(callsBeforeRefusal).toBe(1);
    expect(missing).toEqual([
      { code: 'operator/invalid-request', message: 'The project id is required.' },
      { code: 'operator/invalid-request', message: 'The project id is required.' },
    ]);
    expect(refused).toEqual({ code: 'control-plane/invalid-argument', message: reason });
  });

  it('production and delegation refuse a malformed narration voice before the control plane, with the control plane sentence', async () => {
    const malformed = ['Cori medium', 'x'.repeat(81), 'piper:en_GB/../cori'];

    const refused = await Promise.all(
      malformed.flatMap((narrationVoice) => [
        rejection(invoke(CHANNEL.operatorProduction, { id: 'p1', narrationVoice })),
        rejection(invoke(CHANNEL.operatorDelegation, { id: 'p1', narrationVoice })),
      ]),
    );
    await invoke(CHANNEL.operatorProduction, { id: 'p1', narrationVoice: 'x'.repeat(80) });

    const sentence = 'A narration voice is an id of at most 80 letters, digits, _ : . # or -.';
    expect(refused).toEqual(
      malformed.flatMap(() => [
        { code: 'operator/invalid-request', message: sentence },
        { code: 'operator/invalid-request', message: sentence },
      ]),
    );
    expect(client.produceProject.mock.calls).toEqual([['p1', 'x'.repeat(80), '', 'gentle', '']]);
    expect(client.delegateProject).not.toHaveBeenCalled();
  });

  it('a direction over 1000 characters or an unknown motion is refused with the sentence', async () => {
    const tooLong = { id: 'p1', narrationVoice: '', direction: 'b'.repeat(1001), motion: 'still' };
    const unknownMotion = { id: 'p1', narrationVoice: '', direction: 'calm', motion: 'wild' };

    const refused = await Promise.all(
      [tooLong, unknownMotion].flatMap((req) => [
        rejection(invoke(CHANNEL.operatorProduction, req)),
        rejection(invoke(CHANNEL.operatorDelegation, req)),
      ]),
    );
    await invoke(CHANNEL.operatorProduction, {
      ...tooLong,
      direction: ` ${'b'.repeat(1000)} `,
    });

    const brief = {
      code: 'operator/invalid-request',
      message: 'A direction is at most 1000 characters.',
    };
    const motion = {
      code: 'operator/invalid-request',
      message: 'Motion is still, gentle or dynamic.',
    };
    expect(refused).toEqual([brief, brief, motion, motion]);
    expect(client.produceProject.mock.calls).toEqual([['p1', '', 'b'.repeat(1000), 'still', '']]);
    expect(client.delegateProject).not.toHaveBeenCalled();
  });

  it('a delegation the owner has not granted reaches the renderer as control-plane/permission-denied with the gate reason', async () => {
    const reason =
      'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.';
    client.delegateProject = mock(async () => {
      throw new ControlPlanePermissionDeniedError(reason);
    });

    const refused = await rejection(
      invoke(CHANNEL.operatorDelegation, { id: 'p1', narrationVoice: '' }),
    );

    expect(refused).toEqual({ code: 'control-plane/permission-denied', message: reason });
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

  it('scene text forwards the trimmed text and refuses one empty or over 700 characters before the control plane; narration regeneration forwards the scene', async () => {
    const edited = await invoke(CHANNEL.operatorSceneText, {
      projectId: ' p1 ',
      sceneId: ' s1 ',
      text: '  Span is a window over memory.  ',
    });
    await invoke(CHANNEL.operatorSceneText, {
      projectId: 'p1',
      sceneId: 's2',
      text: ` ${'t'.repeat(700)} `,
    });
    const refused = await Promise.all(
      ['', '   ', 't'.repeat(701), undefined].map((sceneText) =>
        rejection(
          invoke(CHANNEL.operatorSceneText, { projectId: 'p1', sceneId: 's1', text: sceneText }),
        ),
      ),
    );
    const noScene = await rejection(
      invoke(CHANNEL.operatorSceneText, { projectId: 'p1', text: 'New text.' }),
    );
    const renarrated = await invoke(CHANNEL.operatorNarrationRegeneration, {
      projectId: ' p1',
      sceneId: 's2 ',
    });
    const narrationRefused = await Promise.all([
      rejection(invoke(CHANNEL.operatorNarrationRegeneration, { projectId: 'p1' })),
      rejection(invoke(CHANNEL.operatorNarrationRegeneration, { sceneId: 's1' })),
    ]);

    expect((edited as OperatorProjectDto).status).toBe('production_queued');
    expect(client.editSceneText.mock.calls).toEqual([
      ['p1', 's1', 'Span is a window over memory.'],
      ['p1', 's2', 't'.repeat(700)],
    ]);
    const sentence = {
      code: 'operator/invalid-request',
      message: "A scene's text is 1 to 700 characters.",
    };
    expect(refused).toEqual([sentence, sentence, sentence, sentence]);
    expect(noScene).toEqual({
      code: 'operator/invalid-request',
      message: 'The scene id is required.',
    });
    expect((renarrated as OperatorProjectDto).status).toBe('production_queued');
    expect(client.regenerateNarration.mock.calls).toEqual([['p1', 's2']]);
    expect(narrationRefused).toEqual([
      { code: 'operator/invalid-request', message: 'The scene id is required.' },
      { code: 'operator/invalid-request', message: 'The project id is required.' },
    ]);
  });

  it('a mouth box outside the picture or too small is refused with the sentence', async () => {
    const box = { x: 0.4, y: 0.6, width: 0.2, height: 0.1 };
    const marked = await invoke(CHANNEL.operatorMouthBox, { projectId: ' p1 ', mouthBox: box });
    await invoke(CHANNEL.operatorMouthBox, {
      projectId: 'p1',
      mouthBox: { x: 0, y: 0.98, width: 1, height: 0.02 },
    });
    const outside = [
      { ...box, x: 0.9 },
      { ...box, y: 0.95 },
      { ...box, width: 0.01 },
      { ...box, height: 0.019 },
      { ...box, x: -0.1 },
      { ...box, y: 1.2 },
      { ...box, width: Number.NaN },
      { ...box, height: Number.POSITIVE_INFINITY },
      { ...box, x: '0.4' },
      { y: 0.6, width: 0.2, height: 0.1 },
      null,
    ];
    const refused = await Promise.all(
      outside.map((mouthBox) =>
        rejection(invoke(CHANNEL.operatorMouthBox, { projectId: 'p1', mouthBox })),
      ),
    );
    const noProject = await rejection(invoke(CHANNEL.operatorMouthBox, { mouthBox: box }));

    expect(marked).toEqual(project);
    expect(client.setMouthBox.mock.calls).toEqual([
      ['p1', box],
      ['p1', { x: 0, y: 0.98, width: 1, height: 0.02 }],
    ]);
    const sentence = {
      code: 'operator/invalid-request',
      message:
        'A mouth box is four fractions of the picture, each between 0 and 1, at least 0.02 wide and high and inside the picture.',
    };
    expect(refused).toEqual(outside.map(() => sentence));
    expect(noProject).toEqual({
      code: 'operator/invalid-request',
      message: 'The project id is required.',
    });
  });

  it('a presenter regeneration forwards the trimmed project id and refuses a missing one before the control plane', async () => {
    const regenerated = await invoke(CHANNEL.operatorPresenterRegeneration, { id: ' p1 ' });
    const missing = await rejection(invoke(CHANNEL.operatorPresenterRegeneration, { id: ' ' }));

    expect(regenerated).toEqual(project);
    expect(client.regeneratePresenter.mock.calls).toEqual([['p1']]);
    expect(missing).toEqual({
      code: 'operator/invalid-request',
      message: 'The project id is required.',
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

  it("a background attach needs no scene id and only a picture: it copies the owner's picture as owner-background-<hex> and records it with the kind background and an empty scene id", async () => {
    const media = tempDir();
    const picture = path.join(tempDir(), 'Studio wall.JPG');
    writeFileSync(picture, PNG);
    client.getMediaDirectory = mock(async () => media);
    chosen = picture;

    const response = await invoke(CHANNEL.operatorSceneMedia, {
      projectId: 'p1',
      sceneId: '',
      kind: 'background',
    });
    const [copied] = readdirSync(media);
    const withScene = await rejection(
      invoke(CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: 's1', kind: 'background' }),
    );
    const audio = path.join(tempDir(), 'voice.mp3');
    writeFileSync(audio, 'ID3');
    chosen = audio;
    const notAPicture = await rejection(
      invoke(CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: '', kind: 'background' }),
    );

    expect(copied).toMatch(/^owner-background-[0-9a-f]{12}\.jpg$/);
    expect(chooseFile.mock.calls).toEqual([['visual'], ['visual']]);
    expect(client.attachSceneMedia.mock.calls).toEqual([['p1', '', 'background', copied]]);
    expect(response).toEqual({ project });
    expect(withScene).toEqual({
      code: 'operator/invalid-request',
      message: 'A background is attached to the project, not to a scene.',
    });
    expect(notAPicture).toEqual({
      code: 'operator/invalid-request',
      message: 'A picture must be a png, jpg, jpeg or webp file.',
    });
    expect(readdirSync(media)).toEqual([copied!]);
  });

  it("a presenter attach needs no scene id and only a picture: it copies the owner's picture as owner-presenter-<hex> and records it with the kind presenter and an empty scene id", async () => {
    const media = tempDir();
    const picture = path.join(tempDir(), 'My robot.WEBP');
    writeFileSync(picture, PNG);
    client.getMediaDirectory = mock(async () => media);
    chosen = picture;

    const response = await invoke(CHANNEL.operatorSceneMedia, {
      projectId: 'p1',
      sceneId: '',
      kind: 'presenter',
    });
    const [copied] = readdirSync(media);
    const withScene = await rejection(
      invoke(CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: 's1', kind: 'presenter' }),
    );
    const audio = path.join(tempDir(), 'voice.mp3');
    writeFileSync(audio, 'ID3');
    chosen = audio;
    const notAPicture = await rejection(
      invoke(CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: '', kind: 'presenter' }),
    );

    expect(copied).toMatch(/^owner-presenter-[0-9a-f]{12}\.webp$/);
    expect(chooseFile.mock.calls).toEqual([['visual'], ['visual']]);
    expect(client.attachSceneMedia.mock.calls).toEqual([['p1', '', 'presenter', copied]]);
    expect(response).toEqual({ project });
    expect(withScene).toEqual({
      code: 'operator/invalid-request',
      message: 'A presenter picture is attached to the project, not to a scene.',
    });
    expect(notAPicture).toEqual({
      code: 'operator/invalid-request',
      message: 'A picture must be a png, jpg, jpeg or webp file.',
    });
    expect(readdirSync(media)).toEqual([copied!]);
  });

  it('a background the control plane refuses is deleted from the media directory, and the renderer gets the refusal', async () => {
    const media = tempDir();
    const picture = path.join(tempDir(), 'wall.png');
    writeFileSync(picture, PNG);
    chosen = picture;
    client.getMediaDirectory = mock(async () => media);
    const reason =
      'A background is attached to a project whose script or media is ready, an assembled or approved one, one queued for assembly or assembling, or a failed one; this one is queued.';
    client.attachSceneMedia = mock(async () => {
      throw new ControlPlaneInvalidArgumentError(reason);
    });

    const refused = await rejection(
      invoke(CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: '', kind: 'background' }),
    );

    expect(refused).toEqual({ code: 'control-plane/invalid-argument', message: reason });
    expect(readdirSync(media)).toEqual([]);
  });

  it('attaches a recording to the project with an empty scene id and removes the copy when the control plane refuses', async () => {
    const media = tempDir();
    const recording = path.join(tempDir(), 'Meetup talk.MP4');
    writeFileSync(recording, 'ftyp recording bytes');
    client.getMediaDirectory = mock(async () => media);
    chosen = recording;
    const recorded = client.attachSceneMedia;

    const response = await invoke(CHANNEL.operatorSceneMedia, {
      projectId: 'p1',
      sceneId: '',
      kind: 'recording',
    });
    const [copied] = readdirSync(media);
    const withScene = await rejection(
      invoke(CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: 's1', kind: 'recording' }),
    );
    const unknownKind = await rejection(
      invoke(CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: 's1', kind: 'video' }),
    );
    const reason = 'Only a project of your own video takes a recording.';
    client.attachSceneMedia = mock(async () => {
      throw new ControlPlaneInvalidArgumentError(reason);
    });
    const refused = await rejection(
      invoke(CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: '', kind: 'recording' }),
    );

    expect(copied).toMatch(/^owner-recording-[0-9a-f]{12}\.mp4$/);
    expect(chooseFile.mock.calls).toEqual([['recording'], ['recording']]);
    expect(recorded.mock.calls).toEqual([['p1', '', 'recording', copied]]);
    expect(response).toEqual({ project });
    expect(withScene).toEqual({
      code: 'operator/invalid-request',
      message: 'A recording is attached to the project, not to a scene.',
    });
    expect(unknownKind).toEqual({
      code: 'operator/invalid-request',
      message: 'Media is attached as audio, visual, background or recording.',
    });
    expect(refused).toEqual({ code: 'control-plane/invalid-argument', message: reason });
    expect(readdirSync(media)).toEqual([copied!]);
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

  it('publication forwards the privacy, made for kids, synthetic media and a future schedule with private', async () => {
    const future = '2099-01-01T09:30:00.000Z';

    await invoke(CHANNEL.operatorPublication, {
      projectId: ' p1 ',
      privacyStatus: 'private',
      publishAt: future,
      madeForKids: false,
      syntheticMedia: true,
    });
    await invoke(CHANNEL.operatorPublication, {
      projectId: 'p1',
      privacyStatus: 'unlisted',
      publishAt: null,
      madeForKids: true,
      syntheticMedia: false,
    });

    expect(client.publishPackage.mock.calls).toEqual([
      ['p1', 'private', future, false, true],
      ['p1', 'unlisted', null, true, false],
    ]);
  });

  it('a publication without a privacy choice or a made for kids answer, a schedule with public or unlisted, a schedule that is not an ISO UTC time or is in the past is refused before reaching the control plane', async () => {
    const base = {
      projectId: 'p1',
      privacyStatus: 'private',
      publishAt: null,
      madeForKids: false,
      syntheticMedia: false,
    };

    const refused = [
      await rejection(invoke(CHANNEL.operatorPublication, { ...base, privacyStatus: undefined })),
      await rejection(invoke(CHANNEL.operatorPublication, { ...base, privacyStatus: 'friends' })),
      await rejection(invoke(CHANNEL.operatorPublication, { ...base, madeForKids: undefined })),
      await rejection(invoke(CHANNEL.operatorPublication, { ...base, madeForKids: 'no' })),
      await rejection(invoke(CHANNEL.operatorPublication, { ...base, syntheticMedia: undefined })),
      await rejection(
        invoke(CHANNEL.operatorPublication, {
          ...base,
          privacyStatus: 'public',
          publishAt: '2099-01-01T09:30:00.000Z',
        }),
      ),
      await rejection(
        invoke(CHANNEL.operatorPublication, { ...base, publishAt: '2099-01-01 09:30' }),
      ),
      await rejection(
        invoke(CHANNEL.operatorPublication, { ...base, publishAt: '2020-01-01T09:30:00.000Z' }),
      ),
      await rejection(invoke(CHANNEL.operatorPublication, { ...base, projectId: ' ' })),
    ];

    expect(refused.map((r) => r.message)).toEqual([
      'Choose public, private or unlisted.',
      'Choose public, private or unlisted.',
      'Say whether the video is made for kids.',
      'Say whether the video is made for kids.',
      'Say whether the video contains realistic synthetic or altered media.',
      'A scheduled time needs private; YouTube makes the video public at that time.',
      'The scheduled time is not an ISO UTC time.',
      'The scheduled time must be in the future.',
      'The project id is required.',
    ]);
    expect(refused.every((r) => r.code === 'operator/invalid-request')).toBe(true);
    expect(client.publishPackage).not.toHaveBeenCalled();
  });

  it('a proposal decision forwards accept or reject, and refuses a decision that is not a boolean', async () => {
    await invoke(CHANNEL.operatorProposalDecision, {
      projectId: 'p1',
      proposalId: ' pr1 ',
      accept: true,
    });
    await invoke(CHANNEL.operatorProposalDecision, {
      projectId: 'p1',
      proposalId: 'pr2',
      accept: false,
    });
    const refused = await rejection(
      invoke(CHANNEL.operatorProposalDecision, {
        projectId: 'p1',
        proposalId: 'pr3',
        accept: 'yes',
      }),
    );

    expect(client.decideProposal.mock.calls).toEqual([
      ['p1', 'pr1', true],
      ['p1', 'pr2', false],
    ]);
    expect(refused).toEqual({
      code: 'operator/invalid-request',
      message: 'A proposal is accepted or rejected.',
    });
  });

  it('an experiment starts with arm hours from 24 to 168 only, and the winner is adopted by project id', async () => {
    await invoke(CHANNEL.operatorExperiment, { projectId: 'p1', armHours: 24 });
    await invoke(CHANNEL.operatorExperiment, { projectId: 'p1', armHours: 168 });
    const refused = await Promise.all(
      [23, 169, 48.5, '48', undefined].map((armHours) =>
        rejection(invoke(CHANNEL.operatorExperiment, { projectId: 'p1', armHours })),
      ),
    );
    await invoke(CHANNEL.operatorExperimentAdoption, { id: ' p1 ' });

    expect(client.startExperiment.mock.calls).toEqual([
      ['p1', 24],
      ['p1', 168],
    ]);
    expect(refused).toEqual(
      refused.map(() => ({
        code: 'operator/invalid-request',
        message: 'Each arm runs 24 to 168 hours.',
      })),
    );
    expect(client.adoptExperimentWinner.mock.calls).toEqual([['p1']]);
  });

  it('the comments, a sync request, a comment decision, a reply approval and an idea decision reach the control plane with the trimmed ids and the trimmed reply', async () => {
    const listed = await invoke(CHANNEL.operatorComments);
    const requested = await invoke(CHANNEL.operatorCommentSync);
    for (const decision of ['draft', 'dismiss', 'not_spam', 'hold']) {
      await invoke(CHANNEL.operatorCommentDecision, { commentId: ' Ugx-1_a.b ', decision });
    }
    await invoke(CHANNEL.operatorReplyApproval, {
      commentId: 'Ugx1',
      text: '  Thanks!\nNext week.  ',
    });
    await invoke(CHANNEL.operatorReplyApproval, { commentId: 'Ugx1', text: 'x'.repeat(1000) });
    await invoke(CHANNEL.operatorIdeaDecision, { ideaId: ' idea-Ugx1 ', use: true });
    await invoke(CHANNEL.operatorIdeaDecision, { ideaId: 'idea-Ugx2', use: false });

    expect(listed).toEqual(view);
    expect(requested).toEqual(view);
    expect(client.listComments.mock.calls).toEqual([[]]);
    expect(client.requestCommentSync.mock.calls).toEqual([[]]);
    expect(client.decideComment.mock.calls).toEqual([
      ['Ugx-1_a.b', 'draft'],
      ['Ugx-1_a.b', 'dismiss'],
      ['Ugx-1_a.b', 'not_spam'],
      ['Ugx-1_a.b', 'hold'],
    ]);
    expect(client.approveReply.mock.calls).toEqual([
      ['Ugx1', 'Thanks!\nNext week.'],
      ['Ugx1', 'x'.repeat(1000)],
    ]);
    expect(client.decideIdea.mock.calls).toEqual([
      ['idea-Ugx1', true],
      ['idea-Ugx2', false],
    ]);
  });

  it('comment handlers refusals: a decision outside draft, dismiss, not_spam and hold, an id that is not a comment id, a reply that is not 1 to 1000 characters after trimming and an idea decision that is not a boolean are refused in the control plane words before reaching it', async () => {
    const refused = [
      await rejection(
        invoke(CHANNEL.operatorCommentDecision, { commentId: 'Ugx1', decision: 'rejected' }),
      ),
      await rejection(
        invoke(CHANNEL.operatorCommentDecision, { commentId: 'Ugx1', decision: 'banAuthor' }),
      ),
      await rejection(
        invoke(CHANNEL.operatorCommentDecision, { commentId: 'Ugx/1', decision: 'hold' }),
      ),
      await rejection(
        invoke(CHANNEL.operatorCommentDecision, { commentId: 'c'.repeat(101), decision: 'hold' }),
      ),
      await rejection(invoke(CHANNEL.operatorReplyApproval, { commentId: 'Ugx1', text: '   ' })),
      await rejection(
        invoke(CHANNEL.operatorReplyApproval, { commentId: 'Ugx1', text: 'x'.repeat(1001) }),
      ),
      await rejection(invoke(CHANNEL.operatorReplyApproval, { commentId: '', text: 'Thanks' })),
      await rejection(invoke(CHANNEL.operatorIdeaDecision, { ideaId: 'idea-Ugx1', use: 'yes' })),
      await rejection(invoke(CHANNEL.operatorIdeaDecision, { ideaId: ' ', use: true })),
    ];

    expect(refused.map((r) => r.message)).toEqual([
      'A comment is decided as draft, dismiss, not_spam or hold.',
      'A comment is decided as draft, dismiss, not_spam or hold.',
      "'Ugx/1' is not a comment id.",
      `'${'c'.repeat(101)}' is not a comment id.`,
      'A reply is 1 to 1000 characters.',
      'A reply is 1 to 1000 characters.',
      "'' is not a comment id.",
      'An idea is used or dismissed.',
      'The idea id is required.',
    ]);
    expect(refused.every((r) => r.code === 'operator/invalid-request')).toBe(true);
    expect(client.decideComment).not.toHaveBeenCalled();
    expect(client.approveReply).not.toHaveBeenCalled();
    expect(client.decideIdea).not.toHaveBeenCalled();
  });

  it('a reply the control plane refuses reaches the renderer as control-plane/invalid-argument with its words, and a hold without the grant as control-plane/permission-denied', async () => {
    client.approveReply.mockImplementation(async () => {
      throw new ControlPlaneInvalidArgumentError(
        'A quarantined comment gets no reply; mark it not spam first.',
      );
    });
    client.decideComment.mockImplementation(async () => {
      throw new ControlPlanePermissionDeniedError(
        'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.',
      );
    });

    const reply = await rejection(
      invoke(CHANNEL.operatorReplyApproval, { commentId: 'Ugx2', text: 'Thanks' }),
    );
    const hold = await rejection(
      invoke(CHANNEL.operatorCommentDecision, { commentId: 'Ugx2', decision: 'hold' }),
    );

    expect(reply).toEqual({
      code: 'control-plane/invalid-argument',
      message: 'A quarantined comment gets no reply; mark it not spam first.',
    });
    expect(hold).toEqual({
      code: 'control-plane/permission-denied',
      message:
        'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.',
    });
  });

  it('the returned unregister removes the thirty-one operator handlers', () => {
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
        CHANNEL.operatorDelegation,
        CHANNEL.operatorProjectDeletion,
        CHANNEL.operatorSceneOrder,
        CHANNEL.operatorSceneLock,
        CHANNEL.operatorSceneRegeneration,
        CHANNEL.operatorSceneText,
        CHANNEL.operatorNarrationRegeneration,
        CHANNEL.operatorMouthBox,
        CHANNEL.operatorPresenterRegeneration,
        CHANNEL.operatorSceneMedia,
        CHANNEL.operatorReadiness,
        CHANNEL.operatorAssembly,
        CHANNEL.operatorMediaRights,
        CHANNEL.operatorPackageMetadata,
        CHANNEL.operatorPackageApproval,
        CHANNEL.operatorPublication,
        CHANNEL.operatorProposalDecision,
        CHANNEL.operatorExperiment,
        CHANNEL.operatorExperimentAdoption,
        CHANNEL.operatorComments,
        CHANNEL.operatorCommentSync,
        CHANNEL.operatorCommentDecision,
        CHANNEL.operatorReplyApproval,
        CHANNEL.operatorIdeaDecision,
      ].sort(),
    );
    expect(own.handlers.size).toBe(0);
  });
});
