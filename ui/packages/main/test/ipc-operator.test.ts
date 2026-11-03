import { describe, it, expect, beforeEach, mock } from 'bun:test';
import {
  CHANNEL,
  ControlPlaneInvalidArgumentError,
  ControlPlanePermissionDeniedError,
  ControlPlaneUnavailableError,
  readIpcError,
  type MandateDto,
  type OperatorProjectDto,
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
};

type StubClient = { [K in keyof OperatorClient]: ReturnType<typeof mock> };

function makeClient(overrides: Partial<StubClient> = {}): StubClient {
  return {
    getMandate: mock(async () => mandate),
    saveMandate: mock(async () => mandate),
    listProjects: mock(async () => [project]),
    getProject: mock(async () => project),
    startProject: mock(async () => project),
    decideClaim: mock(async () => project),
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

  beforeEach(() => {
    ipcMain = makeFakeIpcMain();
    client = makeClient();
    registerOperatorHandlers(ipcMain, async () => client as unknown as OperatorClient, logger);
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
    );

    const refused = await rejection(down.handlers.get(CHANNEL.operatorProjects)!(NULL_EVENT, {}));

    expect(refused).toEqual({
      code: 'control-plane/unavailable',
      message: 'socket did not appear',
    });
  });

  it('the returned unregister removes the six operator handlers', () => {
    const own = makeFakeIpcMain();
    const unregister = registerOperatorHandlers(
      own,
      async () => client as unknown as OperatorClient,
      logger,
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
      ].sort(),
    );
    expect(own.handlers.size).toBe(0);
  });
});
