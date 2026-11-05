import { describe, it, expect, mock } from 'bun:test';
import { buildBridge, type IpcSubset } from '../src/build-bridge.js';
import {
  CHANNEL,
  type FeatureDto,
  type ScoutBridge,
  type WorkerEvent,
  type YouTubeStatus,
} from '@scout/shared';

function makeFakeIpc(): IpcSubset {
  return {
    invoke: mock((_channel: string, ..._args: unknown[]) => Promise.resolve(undefined as unknown)),
    on: mock((_channel: string, _listener: (event: unknown, ...args: unknown[]) => void) => {}),
    removeListener: mock(
      (_channel: string, _listener: (event: unknown, ...args: unknown[]) => void) => {},
    ),
  };
}

describe('buildBridge', () => {
  it('returns an object with exactly the keys of ScoutBridge', () => {
    const bridge = buildBridge(makeFakeIpc());

    const expectedKeys: (keyof ScoutBridge)[] = [
      'detectClients',
      'discordCheckToken',
      'discordEnableIntent',
      'discordInviteUrl',
      'discordGuilds',
      'chatgptSignIn',
      'chatgptCancelSignIn',
      'chatgptListModels',
      'chatgptSignOut',
      'chatgptStatus',
      'vaultStore',
      'vaultHas',
      'vaultDelete',
      'vaultGetPublic',
      'vaultIsAvailable',
      'workerStart',
      'workerStop',
      'workerStatus',
      'onWorkerEvent',
      'appOpenExternal',
      'appGetVersion',
      'appQuit',
      'diagnosticsExport',
      'llmListOllamaModels',
      'llmListLmStudioModels',
      'llmPullOllamaModel',
      'onLlmPullOllamaProgress',
      'llmDeleteOllamaModel',
      'whisperListInstalled',
      'whisperDownloadModel',
      'onWhisperDownloadProgress',
      'whisperDeleteModel',
      'providersList',
      'providerStatus',
      'providerStart',
      'providerStop',
      'providerRestart',
      'providerSetAutostart',
      'validateProviderKey',
      'llmListModels',
      'featuresList',
      'featureSetCredentials',
      'featureSetup',
      'featureDeleteCredentials',
      'featureEnable',
      'featureDisable',
      'featureWriteGrant',
      'onFeaturesChanged',
      'actionsList',
      'actionsApprove',
      'actionsReject',
      'onActionsChanged',
      'writesList',
      'operatorMandate',
      'operatorSaveMandate',
      'operatorProjects',
      'operatorProject',
      'operatorStartProject',
      'operatorDecideClaim',
      'operatorProduce',
      'operatorDelegate',
      'operatorDeleteProject',
      'operatorReorderScenes',
      'operatorSetSceneLocked',
      'operatorRegenerateScene',
      'operatorEditSceneText',
      'operatorRegenerateNarration',
      'operatorSetMouthBox',
      'operatorRegeneratePresenter',
      'operatorRequestPresenter',
      'operatorUsePresenterVersion',
      'operatorRemovePresenterVersion',
      'operatorEditScenePicturePrompt',
      'operatorRegenerateScenePicture',
      'operatorAttachSceneMedia',
      'operatorReadiness',
      'operatorOpenWindow',
      'operatorAssemble',
      'operatorConfirmMediaRights',
      'operatorEditPackageMetadata',
      'operatorApprovePackage',
      'operatorPublishPackage',
      'operatorDecideProposal',
      'operatorStartExperiment',
      'operatorAdoptExperimentWinner',
      'operatorComments',
      'operatorRequestCommentSync',
      'operatorDecideComment',
      'operatorApproveReply',
      'operatorDecideIdea',
      'githubSignIn',
      'githubStatus',
      'githubCancel',
      'githubSignOut',
      'githubSaveToken',
      'youtubeSignIn',
      'youtubeStatus',
      'youtubeCancelSignIn',
      'youtubeSignOut',
      'youtubeSaveApiKey',
      'youtubeClearApiKey',
      'kubernetesDetect',
      'kubernetesUse',
      'kubernetesStatus',
      'kubernetesDisconnect',
      'accessGet',
      'accessSet',
      'accessStartPairing',
      'slackManifestUrl',
      'slackSave',
      'slackStatus',
      'slackDisconnect',
      'desktopGetSettings',
      'desktopSetKeepRunning',
      'desktopSetStartAtLogin',
      'localAsk',
      'localCancel',
      'localGetTurns',
      'localClearTurns',
      'onLocalTurns',
      'localHide',
      'localGetShortcut',
      'localSetShortcut',
      'localOpen',
      'localGetBubble',
      'localSetBubble',
      'localListen',
      'localAudio',
      'localStopListening',
      'onLocalTranscript',
      'localReadText',
      'localKeepMeeting',
      'localMeetings',
      'localExportMeeting',
      'localDeleteMeetings',
      'updateGetState',
      'updateSetCheckEnabled',
      'updateCheckNow',
      'updateSkipVersion',
      'updateRemindLater',
      'onUpdateNotice',
      'updateInstall',
      'onUpdateProgress',
    ];

    const actualKeys = Object.keys(bridge).sort();
    expect(actualKeys).toEqual(expectedKeys.slice().sort());
    expect(actualKeys.length).toBe(expectedKeys.length);
  });

  it('detectClients invokes the correct channel exactly once and returns resolved value', async () => {
    const ipc = makeFakeIpc();
    const expected = { clients: [] };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.detectClients();

    expect(ipc.invoke).toHaveBeenCalledTimes(1);
    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.detectClients);
    expect(result).toBe(expected);
  });

  it('the ChatGPT methods reach the renderer only through their own channels, with no payload', async () => {
    const ipc = makeFakeIpc();
    const status = { signedIn: true, email: 'owner@example.com', planUsageGranted: true };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation((channel: string) =>
      Promise.resolve(channel === CHANNEL.chatgptSignOut ? { revocationConfirmed: true } : status),
    );

    const bridge = buildBridge(ipc);

    expect(await bridge.chatgptSignIn()).toEqual(status);
    expect(await bridge.chatgptStatus()).toEqual(status);
    expect(await bridge.chatgptSignOut()).toEqual({ revocationConfirmed: true });
    await bridge.chatgptCancelSignIn();
    await bridge.chatgptListModels();
    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.chatgptSignIn],
      [CHANNEL.chatgptStatus],
      [CHANNEL.chatgptSignOut],
      [CHANNEL.chatgptCancelSignIn],
      [CHANNEL.chatgptListModels],
    ]);
  });

  it('discordCheckToken passes the pasted token to main once and returns the bot info', async () => {
    const ipc = makeFakeIpc();
    const req = { token: 'pasted-token' };
    const expected = {
      applicationId: '1',
      applicationName: 'Scout',
      botUsername: 'scout',
      messageContent: 'on' as const,
    };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const result = await buildBridge(ipc).discordCheckToken(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.discordCheckToken, req);
    expect(result).toBe(expected);
  });

  it('discordEnableIntent and discordGuilds send no token from the renderer', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    await bridge.discordEnableIntent();
    await bridge.discordGuilds();
    await bridge.discordInviteUrl();

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.discordEnableIntent],
      [CHANNEL.discordGuilds],
      [CHANNEL.discordInviteUrl],
    ]);
  });

  it('onWorkerEvent registers a handler via ipc.on and forwards events stripping the ipc event arg', () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    const received: WorkerEvent[] = [];
    bridge.onWorkerEvent((ev) => received.push(ev));

    expect(ipc.on).toHaveBeenCalledTimes(1);
    expect(ipc.on).toHaveBeenCalledWith(CHANNEL.workerEvents, expect.any(Function));

    const calls = (ipc.on as ReturnType<typeof mock>).mock.calls as [
      string,
      (event: unknown, payload: WorkerEvent) => void,
    ][];
    const registeredHandler = calls[0]![1];

    const fakeEvent = {};
    const payload: WorkerEvent = { kind: 'stdout', line: 'hello', ts: '2026-01-01T00:00:00Z' };
    registeredHandler(fakeEvent, payload);

    expect(received).toHaveLength(1);
    expect(received[0]).toBe(payload);
  });

  it('onWorkerEvent returns an unsubscribe function that calls removeListener', () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    const unsubscribe = bridge.onWorkerEvent(() => {});
    expect(ipc.removeListener).toHaveBeenCalledTimes(0);

    unsubscribe();

    expect(ipc.removeListener).toHaveBeenCalledTimes(1);
    expect(ipc.removeListener).toHaveBeenCalledWith(CHANNEL.workerEvents, expect.any(Function));
  });

  it('onWorkerEvent passes the same handler reference to on and removeListener', () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    const unsubscribe = bridge.onWorkerEvent(() => {});
    unsubscribe();

    const onCalls = (ipc.on as ReturnType<typeof mock>).mock.calls as [string, unknown][];
    const removeCalls = (ipc.removeListener as ReturnType<typeof mock>).mock.calls as [
      string,
      unknown,
    ][];

    expect(onCalls[0]![1]).toBe(removeCalls[0]![1]);
  });

  it('propagates rejection from ipc.invoke unchanged', async () => {
    const ipc = makeFakeIpc();
    const error = new Error('No handler registered for scout:detect.clients');
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.reject(error));

    const bridge = buildBridge(ipc);

    await expect(bridge.detectClients()).rejects.toBe(error);
  });

  it('appGetVersion takes no arguments (length === 0)', () => {
    const bridge = buildBridge(makeFakeIpc());
    expect(bridge.appGetVersion.length).toBe(0);
  });

  it('appGetVersion invokes the correct channel', async () => {
    const ipc = makeFakeIpc();
    const expected = { version: '1.0.0' };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.appGetVersion();

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.appGetVersion);
    expect(result).toBe(expected);
  });

  it('vaultHas passes key request through and returns response', async () => {
    const ipc = makeFakeIpc();
    const req = { key: 'discord.bot-token' as const };
    const expected = { exists: true };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.vaultHas(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.vaultHas, req);
    expect(result).toBe(expected);
  });

  it('validateProviderKey sends the request on the validateKey channel and returns the verdict', async () => {
    const ipc = makeFakeIpc();
    const req = { provider: 'OpenAI' as const, apiKey: 'sk-test' };
    const expected = { ok: false as const, reason: 'invalid-key' as const };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.validateProviderKey(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.providersValidateKey, req);
    expect(result).toBe(expected);
  });

  it('llmListModels sends the provider on the llm.listModels channel and returns the models', async () => {
    const ipc = makeFakeIpc();
    const req = { provider: 'Gemini' as const };
    const expected = { models: [{ id: 'gemini-2.5-flash', displayName: 'Gemini 2.5 Flash' }] };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const result = await buildBridge(ipc).llmListModels(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.llmListModels, req);
    expect(result).toBe(expected);
  });

  it('vaultGetPublic sends the key on the getPublic channel and returns the value', async () => {
    const ipc = makeFakeIpc();
    const req = { key: 'llm.provider' as const };
    const expected = { value: 'OpenAI' };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.vaultGetPublic(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.vaultGetPublic, req);
    expect(result).toBe(expected);
  });

  it('workerStart invokes the correct channel with no extra args', async () => {
    const ipc = makeFakeIpc();
    const expected = { pid: 12345 };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.workerStart();

    expect(ipc.invoke).toHaveBeenCalledTimes(1);
    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.workerStart);
    expect(result).toBe(expected);
  });

  it('diagnosticsExport invokes the correct channel and returns bundle', async () => {
    const ipc = makeFakeIpc();
    const expected = { bundle: '{"version":"1.0.0"}' };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.diagnosticsExport();

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.diagnosticsExport);
    expect(result).toBe(expected);
  });

  it('appQuit propagates rejection from invoke', async () => {
    const ipc = makeFakeIpc();
    const error = new Error('No handler registered for scout:app.quit');
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.reject(error));

    const bridge = buildBridge(ipc);

    await expect(bridge.appQuit()).rejects.toBe(error);
  });

  it('featuresList invokes the correct channel with no arguments', async () => {
    const ipc = makeFakeIpc();
    const expected = { features: [] };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.featuresList();

    expect(ipc.invoke).toHaveBeenCalledTimes(1);
    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featuresList);
    expect(result).toBe(expected);
  });

  it('featureSetCredentials forwards the payload to the matching channel', async () => {
    const ipc = makeFakeIpc();
    const req = { id: 'github' as const, body: { pat: 'ghp_xxx' } };
    const expected: FeatureDto = {
      id: 'github',
      displayName: 'GitHub',
      description: '',
      requiresCredentials: true,
      hasCredentials: true,
      enabled: false,
      status: 'disabled',
      lastError: null,
      credentialFields: [],
      settingsFields: [],
      settings: {},
      scopeMatrix: null,
      setupSteps: null,
      writeClass: 'none',
      granted: false,
      grantedAt: null,
    };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.featureSetCredentials(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featureSetCredentials, req);
    expect(result).toBe(expected);
  });

  it('featureSetup forwards the credentials and settings to the matching channel', async () => {
    const ipc = makeFakeIpc();
    const req = {
      id: 'github' as const,
      credentials: { pat: 'ghp_xxx' },
      settings: { default_repo_url: 'https://github.com/acme/repo' },
    };
    const expected: FeatureDto = {
      id: 'github',
      displayName: 'GitHub',
      description: '',
      requiresCredentials: true,
      hasCredentials: true,
      enabled: false,
      status: 'disabled',
      lastError: null,
      credentialFields: [],
      settingsFields: [],
      settings: {},
      scopeMatrix: null,
      setupSteps: null,
      writeClass: 'none',
      granted: false,
      grantedAt: null,
    };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.featureSetup(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featureSetup, req);
    expect(result).toBe(expected);
  });

  it('featureWriteGrant, actionsList, actionsApprove, actionsReject and writesList invoke their channels with the request', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);
    const grant = { id: 'youtube' as const, granted: true, confirmWrites: true };

    await bridge.featureWriteGrant(grant);
    await bridge.actionsList();
    await bridge.actionsApprove({ id: 'act-1' });
    await bridge.actionsReject({ id: 'act-1', reason: 'Wrong thumbnail' });
    await bridge.writesList({ limit: 50 });

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.featureWriteGrant, grant],
      [CHANNEL.actionsList],
      [CHANNEL.actionsApprove, { id: 'act-1' }],
      [CHANNEL.actionsReject, { id: 'act-1', reason: 'Wrong thumbnail' }],
      [CHANNEL.writesList, { limit: 50 }],
    ]);
  });

  it('the operator methods reach main only through their own scout:operator channels with their requests', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);
    const mandate = {
      objective: 'Grow the channel',
      audience: 'Developers',
      pillars: ['.NET'],
      cadence: 'weekly',
      language: 'en',
      voiceAndStyle: 'calm',
      blockedTopics: [],
      targetMetric: 'views',
      targetValue: '10000',
      defaultFormat: 'animated_character' as const,
    };
    const decision = {
      projectId: 'p1',
      claimId: 'c1',
      status: 'waived' as const,
      note: 'Common knowledge',
    };

    await bridge.operatorMandate();
    await bridge.operatorSaveMandate(mandate);
    await bridge.operatorProjects({ limit: 50 });
    await bridge.operatorProject({ id: 'p1' });
    await bridge.operatorStartProject({
      topic: 'Span<T> in C#',
      format: 'own_pictures',
      direction: 'Explain it to a junior developer',
      presenter: '',
    });
    await bridge.operatorDecideClaim(decision);

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.operatorMandate],
      [CHANNEL.operatorSaveMandate, mandate],
      [CHANNEL.operatorProjects, { limit: 50 }],
      [CHANNEL.operatorProject, { id: 'p1' }],
      [
        CHANNEL.operatorStartProject,
        {
          topic: 'Span<T> in C#',
          format: 'own_pictures',
          direction: 'Explain it to a junior developer',
          presenter: '',
        },
      ],
      [CHANNEL.operatorDecideClaim, decision],
    ]);
  });

  it('produce, delegation, project deletion, scene order, lock, regeneration, attach of a scene file and of a background, and readiness reach main on their own scout:operator channels, and attach carries the ids and the kind only', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    await bridge.operatorProduce({
      id: 'p1',
      narrationVoice: 'piper:en_GB-cori-medium',
      direction: 'Chalk on slate',
      motion: 'still',
      presenter: '',
    });
    await bridge.operatorDelegate({
      id: 'p1',
      narrationVoice: '',
      direction: '',
      motion: 'gentle',
      presenter: 'A friendly robot in a workshop',
    });
    await bridge.operatorDeleteProject({ id: 'p1' });
    await bridge.operatorReorderScenes({ projectId: 'p1', sceneIds: ['s2', 's1'] });
    await bridge.operatorSetSceneLocked({ projectId: 'p1', sceneId: 's1', locked: true });
    await bridge.operatorRegenerateScene({ projectId: 'p1', sceneId: 's2' });
    await bridge.operatorAttachSceneMedia({ projectId: 'p1', sceneId: 's1', kind: 'audio' });
    await bridge.operatorAttachSceneMedia({ projectId: 'p1', sceneId: '', kind: 'background' });
    await bridge.operatorReadiness();

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [
        CHANNEL.operatorProduction,
        {
          id: 'p1',
          narrationVoice: 'piper:en_GB-cori-medium',
          direction: 'Chalk on slate',
          motion: 'still',
          presenter: '',
        },
      ],
      [
        CHANNEL.operatorDelegation,
        {
          id: 'p1',
          narrationVoice: '',
          direction: '',
          motion: 'gentle',
          presenter: 'A friendly robot in a workshop',
        },
      ],
      [CHANNEL.operatorProjectDeletion, { id: 'p1' }],
      [CHANNEL.operatorSceneOrder, { projectId: 'p1', sceneIds: ['s2', 's1'] }],
      [CHANNEL.operatorSceneLock, { projectId: 'p1', sceneId: 's1', locked: true }],
      [CHANNEL.operatorSceneRegeneration, { projectId: 'p1', sceneId: 's2' }],
      [CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: 's1', kind: 'audio' }],
      [CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: '', kind: 'background' }],
      [CHANNEL.operatorReadiness],
    ]);
  });

  it('operatorEditSceneText and operatorRegenerateNarration invoke their channels', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    await bridge.operatorEditSceneText({ projectId: 'p1', sceneId: 's1', text: 'New text.' });
    await bridge.operatorRegenerateNarration({ projectId: 'p1', sceneId: 's2' });

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.operatorSceneText, { projectId: 'p1', sceneId: 's1', text: 'New text.' }],
      [CHANNEL.operatorNarrationRegeneration, { projectId: 'p1', sceneId: 's2' }],
    ]);
  });

  it('the mouth box, the presenter regeneration and a presenter attach reach main on their own scout:operator channels with their requests', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);
    const mouthBox = { x: 0.4, y: 0.6, width: 0.2, height: 0.1 };

    await bridge.operatorSetMouthBox({ projectId: 'p1', mouthBox });
    await bridge.operatorRegeneratePresenter({ id: 'p1' });
    await bridge.operatorAttachSceneMedia({ projectId: 'p1', sceneId: '', kind: 'presenter' });

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.operatorMouthBox, { projectId: 'p1', mouthBox }],
      [CHANNEL.operatorPresenterRegeneration, { id: 'p1' }],
      [CHANNEL.operatorSceneMedia, { projectId: 'p1', sceneId: '', kind: 'presenter' }],
    ]);
  });

  it('a presenter request, a presenter version, a presenter version removal, a scene picture prompt and a scene picture regeneration reach main on their own scout:operator channels with their requests', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    await bridge.operatorRequestPresenter({
      projectId: 'p1',
      feedback: 'Older',
      prompt: 'A robot',
    });
    await bridge.operatorUsePresenterVersion({ projectId: 'p1', index: 2 });
    await bridge.operatorRemovePresenterVersion({ projectId: 'p1', index: 0 });
    await bridge.operatorEditScenePicturePrompt({
      projectId: 'p1',
      sceneId: 's1',
      prompt: 'A river',
    });
    await bridge.operatorRegenerateScenePicture({ projectId: 'p1', sceneId: 's1' });

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.operatorPresenterRequest, { projectId: 'p1', feedback: 'Older', prompt: 'A robot' }],
      [CHANNEL.operatorPresenterVersion, { projectId: 'p1', index: 2 }],
      [CHANNEL.operatorPresenterVersionRemoval, { projectId: 'p1', index: 0 }],
      [CHANNEL.operatorScenePicturePrompt, { projectId: 'p1', sceneId: 's1', prompt: 'A river' }],
      [CHANNEL.operatorScenePictureRegeneration, { projectId: 'p1', sceneId: 's1' }],
    ]);
  });

  it('assembly, media rights, package metadata and package approval reach main on their own scout:operator channels with their requests', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);
    const metadata = {
      projectId: 'p1',
      title: 'Span<T> explained',
      description: 'A tour of Span<T>.',
      tags: ['dotnet', 'span'],
    };

    await bridge.operatorAssemble({ id: 'p1' });
    await bridge.operatorConfirmMediaRights({ id: 'p1' });
    await bridge.operatorEditPackageMetadata(metadata);
    await bridge.operatorApprovePackage({ id: 'p1' });

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.operatorAssembly, { id: 'p1' }],
      [CHANNEL.operatorMediaRights, { id: 'p1' }],
      [CHANNEL.operatorPackageMetadata, metadata],
      [CHANNEL.operatorPackageApproval, { id: 'p1' }],
    ]);
  });

  it('publishing, deciding a proposal, starting an experiment and adopting its winner reach main on their own scout:operator channels with their requests', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);
    const publish = {
      projectId: 'p1',
      privacyStatus: 'private' as const,
      publishAt: '2026-12-01T10:00:00.000Z',
      madeForKids: false,
      syntheticMedia: true,
    };

    await bridge.operatorPublishPackage(publish);
    await bridge.operatorDecideProposal({ projectId: 'p1', proposalId: 'pr1', accept: true });
    await bridge.operatorStartExperiment({ projectId: 'p1', armHours: 48 });
    await bridge.operatorAdoptExperimentWinner({ id: 'p1' });

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.operatorPublication, publish],
      [CHANNEL.operatorProposalDecision, { projectId: 'p1', proposalId: 'pr1', accept: true }],
      [CHANNEL.operatorExperiment, { projectId: 'p1', armHours: 48 }],
      [CHANNEL.operatorExperimentAdoption, { id: 'p1' }],
    ]);
  });

  it('reading the comments, asking for a sync, deciding a comment, approving a reply and deciding an idea reach main on their own scout:operator channels with their requests', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    await bridge.operatorComments();
    await bridge.operatorRequestCommentSync();
    await bridge.operatorDecideComment({ commentId: 'c1', decision: 'hold' });
    await bridge.operatorApproveReply({ commentId: 'c1', text: 'Thanks for asking.' });
    await bridge.operatorDecideIdea({ ideaId: 'idea-c1', use: true });

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.operatorComments],
      [CHANNEL.operatorCommentSync],
      [CHANNEL.operatorCommentDecision, { commentId: 'c1', decision: 'hold' }],
      [CHANNEL.operatorReplyApproval, { commentId: 'c1', text: 'Thanks for asking.' }],
      [CHANNEL.operatorIdeaDecision, { ideaId: 'idea-c1', use: true }],
    ]);
  });

  it('operatorOpenWindow asks main for the operator window on scout:operator.window with no payload', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    await bridge.operatorOpenWindow();

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([[CHANNEL.operatorWindow]]);
    expect(CHANNEL.operatorWindow).toBe('scout:operator.window');
  });

  it('onActionsChanged calls the listener each time main announces a change and unsubscribes the same handler', () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);
    let changes = 0;

    const unsubscribe = bridge.onActionsChanged(() => (changes += 1));
    const [channel, handler] = (
      (ipc.on as ReturnType<typeof mock>).mock.calls as [string, (event: unknown) => void][]
    )[0]!;
    handler({});
    handler({});
    unsubscribe();

    expect(channel).toBe(CHANNEL.actionsChanged);
    expect(changes).toBe(2);
    expect(ipc.removeListener).toHaveBeenCalledWith(CHANNEL.actionsChanged, handler);
  });

  it('onFeaturesChanged calls the listener each time main announces the features changed and unsubscribes the same handler', () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);
    let changes = 0;

    const unsubscribe = bridge.onFeaturesChanged(() => (changes += 1));
    const [channel, handler] = (
      (ipc.on as ReturnType<typeof mock>).mock.calls as [string, (event: unknown) => void][]
    )[0]!;
    handler({});
    handler({});
    unsubscribe();

    expect(channel).toBe(CHANNEL.featuresChanged);
    expect(changes).toBe(2);
    expect(ipc.removeListener).toHaveBeenCalledWith(CHANNEL.featuresChanged, handler);
  });

  it('featureEnable invokes the correct channel with the id request', async () => {
    const ipc = makeFakeIpc();
    const req = { id: 'kubernetes' as const, confirmWrites: false };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve({}));

    const bridge = buildBridge(ipc);
    await bridge.featureEnable(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featureEnable, req);
  });

  it('featureDisable invokes the correct channel with the id request', async () => {
    const ipc = makeFakeIpc();
    const req = { id: 'github' as const };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve({}));

    const bridge = buildBridge(ipc);
    await bridge.featureDisable(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featureDisable, req);
  });

  it('featureDeleteCredentials invokes the correct channel with the id request', async () => {
    const ipc = makeFakeIpc();
    const req = { id: 'github' as const };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve({}));

    const bridge = buildBridge(ipc);
    await bridge.featureDeleteCredentials(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featureDeleteCredentials, req);
  });

  it('kubernetesUse sends the selected contexts on the use channel and returns the status', async () => {
    const ipc = makeFakeIpc();
    const status = {
      path: '/home/me/.kube/config',
      contexts: [{ name: 'dev', label: 'dev' }],
      connected: true,
    };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(status));

    const result = await buildBridge(ipc).kubernetesUse({
      contexts: [{ name: 'dev', label: 'dev' }],
    });

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.kubernetesUse, {
      contexts: [{ name: 'dev', label: 'dev' }],
    });
    expect(result).toBe(status);
  });

  it('accessSet sends the allowed users on the access.set channel and returns the stored list', async () => {
    const ipc = makeFakeIpc();
    const stored = { userIds: ['111'] };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(stored));

    const result = await buildBridge(ipc).accessSet({ userIds: [' 111 '] });

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.accessSet, { userIds: [' 111 '] });
    expect(result).toBe(stored);
  });

  it('accessGet invokes the access.get channel', async () => {
    const ipc = makeFakeIpc();
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() =>
      Promise.resolve({ userIds: [] }),
    );

    await buildBridge(ipc).accessGet();

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.accessGet);
  });

  it('accessStartPairing invokes the access.startPairing channel and returns the code', async () => {
    const ipc = makeFakeIpc();
    const pairing = { code: '482913', expiresAt: '2026-10-28T12:10:00.000Z' };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(pairing));

    const result = await buildBridge(ipc).accessStartPairing();

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.accessStartPairing);
    expect(result).toBe(pairing);
  });

  it('the Slack methods reach main only through their own channels, and only save carries the tokens', async () => {
    const ipc = makeFakeIpc();
    const status = { connected: true, team: 'Acme', botUser: 'scout' };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(status));
    const bridge = buildBridge(ipc);

    await bridge.slackManifestUrl();
    await bridge.slackSave({ botToken: 'xoxb-1', appToken: 'xapp-1' });
    await bridge.slackStatus();
    await bridge.slackDisconnect();

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.slackManifestUrl],
      [CHANNEL.slackSave, { botToken: 'xoxb-1', appToken: 'xapp-1' }],
      [CHANNEL.slackStatus],
      [CHANNEL.slackDisconnect],
    ]);
  });

  it('the YouTube methods reach main only through their own channels, and only saveApiKey carries the key', async () => {
    const ipc = makeFakeIpc();
    const status: YouTubeStatus = {
      configured: false,
      connected: true,
      method: 'apiKey',
      channelId: null,
      channelTitle: null,
      pending: false,
      error: null,
      clientConfigPath: '/user-data/google-oauth-client.json',
      source: 'file',
      scopes: [],
    };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(status));
    const bridge = buildBridge(ipc);

    await bridge.youtubeSignIn();
    await bridge.youtubeStatus();
    await bridge.youtubeCancelSignIn();
    await bridge.youtubeSaveApiKey({ apiKey: 'AIzaSyPreloadKey' });
    await bridge.youtubeClearApiKey();
    const signedOut = await bridge.youtubeSignOut();

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.youtubeSignIn],
      [CHANNEL.youtubeStatus],
      [CHANNEL.youtubeCancelSignIn],
      [CHANNEL.youtubeSaveApiKey, { apiKey: 'AIzaSyPreloadKey' }],
      [CHANNEL.youtubeClearApiKey],
      [CHANNEL.youtubeSignOut],
    ]);
    expect(signedOut).toEqual(status);
  });

  it('the Desktop settings methods reach main only through their own channels', async () => {
    const ipc = makeFakeIpc();
    const settings = { keepRunning: true, startAtLogin: false, trayAvailable: true };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(settings));
    const bridge = buildBridge(ipc);

    await bridge.desktopGetSettings();
    await bridge.desktopSetKeepRunning({ enabled: false });
    await bridge.desktopSetStartAtLogin({ enabled: true });

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.desktopGetSettings],
      [CHANNEL.desktopSetKeepRunning, { enabled: false }],
      [CHANNEL.desktopSetStartAtLogin, { enabled: true }],
    ]);
  });

  it('the quick-ask methods reach main only through their own channels with their payloads', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    await bridge.localAsk({ id: 'q-1', text: 'is prod green?' });
    await bridge.localCancel({ id: 'q-1' });
    await bridge.localGetTurns();
    await bridge.localClearTurns();
    await bridge.localHide();
    await bridge.localGetShortcut();
    await bridge.localSetShortcut({ accelerator: 'Alt+Shift+S' });
    await bridge.localOpen();
    await bridge.localGetBubble();
    await bridge.localSetBubble({ enabled: true, corner: 'top-left', display: 'primary' });

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.localAsk, { id: 'q-1', text: 'is prod green?' }],
      [CHANNEL.localCancel, { id: 'q-1' }],
      [CHANNEL.localGetTurns],
      [CHANNEL.localClearTurns],
      [CHANNEL.localHide],
      [CHANNEL.localGetShortcut],
      [CHANNEL.localSetShortcut, { accelerator: 'Alt+Shift+S' }],
      [CHANNEL.localOpen],
      [CHANNEL.localGetBubble],
      [CHANNEL.localSetBubble, { enabled: true, corner: 'top-left', display: 'primary' }],
    ]);
  });

  it('onLocalTurns forwards the conversation main pushes and unsubscribes the same handler', () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);
    const received: unknown[] = [];
    const turns = [
      { id: 'q-1', question: 'is prod green?', reply: 'Green', citations: [], state: 'running' },
    ];

    const unsubscribe = bridge.onLocalTurns((t) => received.push(t));
    const [channel, handler] = (
      (ipc.on as ReturnType<typeof mock>).mock.calls as [
        string,
        (event: unknown, payload: unknown) => void,
      ][]
    )[0]!;
    handler({}, turns);
    unsubscribe();

    expect(channel).toBe(CHANNEL.localTurns);
    expect(received).toEqual([turns]);
    expect(ipc.removeListener).toHaveBeenCalledWith(CHANNEL.localTurns, handler);
  });

  it('live listening reaches main only through its own channels, and onLocalTranscript forwards what main pushes and unsubscribes the same handler', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);
    const received: unknown[] = [];
    const transcript = { id: 'l-1', text: 'is prod green', final: true };

    await bridge.localListen();
    await bridge.localAudio({ id: 'l-1', pcm16: 'AAAA' });
    await bridge.localStopListening({ id: 'l-1' });
    const unsubscribe = bridge.onLocalTranscript((e) => received.push(e));
    const [channel, handler] = (
      (ipc.on as ReturnType<typeof mock>).mock.calls as [
        string,
        (event: unknown, payload: unknown) => void,
      ][]
    )[0]!;
    handler({}, transcript);
    unsubscribe();

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.localListen],
      [CHANNEL.localAudio, { id: 'l-1', pcm16: 'AAAA' }],
      [CHANNEL.localStopListening, { id: 'l-1' }],
    ]);
    expect(channel).toBe(CHANNEL.localTranscript);
    expect(received).toEqual([transcript]);
    expect(ipc.removeListener).toHaveBeenCalledWith(CHANNEL.localTranscript, handler);
  });

  it('localReadText carries the screenshot to main on scout:local.readText and answers what main answers', async () => {
    const ipc = makeFakeIpc();
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() =>
      Promise.resolve({ text: 'Error 502' }),
    );
    const bridge = buildBridge(ipc);
    const image = { contentType: 'image/jpeg', base64: 'U0hPVA==' } as const;

    const answer = await bridge.localReadText({ image });

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.localReadText, { image }],
    ]);
    expect(answer).toEqual({ text: 'Error 502' });
  });

  it('the four meeting calls go to main on their own scout:local channels and answer what main answers', async () => {
    const ipc = makeFakeIpc();
    const answers = [{ meetingId: 'm1' }, { items: [] }, { saved: true }, { deleted: 2 }];
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() =>
      Promise.resolve(answers.shift()),
    );
    const bridge = buildBridge(ipc);
    const meeting = { startedAt: 's', endedAt: 'e', title: 'Standup', lines: [], turns: [] };

    const replies = [
      await bridge.localKeepMeeting({ meeting }),
      await bridge.localMeetings({ limit: 100 }),
      await bridge.localExportMeeting({ meetingId: 'm1' }),
      await bridge.localDeleteMeetings({ all: true }),
    ];

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.localKeepMeeting, { meeting }],
      [CHANNEL.localMeetings, { limit: 100 }],
      [CHANNEL.localExportMeeting, { meetingId: 'm1' }],
      [CHANNEL.localDeleteMeetings, { all: true }],
    ]);
    expect(replies).toEqual([{ meetingId: 'm1' }, { items: [] }, { saved: true }, { deleted: 2 }]);
  });

  it('localListen carries the lane to main on scout:local.listen', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    await bridge.localListen({ lane: 'others' });

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.localListen, { lane: 'others' }],
    ]);
  });

  it('the update methods reach main only through their own channels with their payloads', async () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    await bridge.updateGetState();
    await bridge.updateSetCheckEnabled({ enabled: false });
    await bridge.updateCheckNow();
    await bridge.updateSkipVersion({ version: '0.2.0' });
    await bridge.updateRemindLater();
    await bridge.updateInstall();

    expect((ipc.invoke as ReturnType<typeof mock>).mock.calls).toEqual([
      [CHANNEL.updateGetState],
      [CHANNEL.updateSetCheckEnabled, { enabled: false }],
      [CHANNEL.updateCheckNow],
      [CHANNEL.updateSkipVersion, { version: '0.2.0' }],
      [CHANNEL.updateRemindLater],
      [CHANNEL.updateInstall],
    ]);
  });

  it('onUpdateNotice forwards the notice main pushes and unsubscribes the same handler', () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);
    const received: unknown[] = [];
    const notice = {
      currentVersion: '0.1.0',
      latestVersion: '0.2.0',
      notes: '* item',
      releaseUrl: 'https://github.com/vitorafgomes/scout/releases/tag/v0.2.0',
      downloadUrl: 'https://github.com/vitorafgomes/scout/releases/tag/v0.2.0',
      publishedAt: '2026-11-01T18:00:00Z',
    };

    const unsubscribe = bridge.onUpdateNotice((n) => received.push(n));
    const [channel, handler] = (
      (ipc.on as ReturnType<typeof mock>).mock.calls as [
        string,
        (event: unknown, payload: unknown) => void,
      ][]
    )[0]!;
    handler({}, notice);
    handler({}, null);
    unsubscribe();

    expect(channel).toBe(CHANNEL.updateNotice);
    expect(received).toEqual([notice, null]);
    expect(ipc.removeListener).toHaveBeenCalledWith(CHANNEL.updateNotice, handler);
  });

  it('onUpdateProgress forwards the progress main pushes and unsubscribes the same handler', () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);
    const received: unknown[] = [];
    const progress = {
      phase: 'downloading',
      percent: 42,
      transferred: 42,
      total: 100,
      message: null,
    };

    const unsubscribe = bridge.onUpdateProgress((p) => received.push(p));
    const [channel, handler] = (
      (ipc.on as ReturnType<typeof mock>).mock.calls as [
        string,
        (event: unknown, payload: unknown) => void,
      ][]
    )[0]!;
    handler({}, progress);
    unsubscribe();

    expect(channel).toBe(CHANNEL.updateProgress);
    expect(received).toEqual([progress]);
    expect(ipc.removeListener).toHaveBeenCalledWith(CHANNEL.updateProgress, handler);
  });
});
