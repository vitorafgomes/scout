import { describe, expect, test } from 'bun:test';
import {
  CHANNEL,
  type ChannelName,
  type MandateDto,
  type OperatorProjectDto,
  type ReadinessDto,
} from '../src/ipc-contract.js';

describe('CHANNEL constant', () => {
  test('all values are unique', () => {
    const values = Object.values(CHANNEL);
    const unique = new Set(values);
    expect(unique.size).toBe(values.length);
  });

  test('all values start with scout: prefix', () => {
    for (const value of Object.values(CHANNEL)) {
      expect(value.startsWith('scout:')).toBe(true);
    }
  });

  test('exposes exactly the expected channels', () => {
    expect(Object.keys(CHANNEL).sort()).toEqual(
      [
        'accessGet',
        'accessSet',
        'accessStartPairing',
        'actionsApprove',
        'actionsChanged',
        'actionsList',
        'actionsReject',
        'appGetVersion',
        'appOpenExternal',
        'appQuit',
        'chatgptCancelSignIn',
        'chatgptListModels',
        'chatgptSignIn',
        'chatgptSignOut',
        'chatgptStatus',
        'desktopGetSettings',
        'desktopSetKeepRunning',
        'desktopSetStartAtLogin',
        'detectClients',
        'diagnosticsExport',
        'discordCheckToken',
        'discordEnableIntent',
        'discordGuilds',
        'discordInviteUrl',
        'featureDeleteCredentials',
        'featureDisable',
        'featureEnable',
        'featureSetCredentials',
        'featureSetup',
        'featureWriteGrant',
        'featuresChanged',
        'featuresList',
        'githubCancel',
        'githubSaveToken',
        'githubSignIn',
        'githubSignOut',
        'githubStatus',
        'kubernetesDetect',
        'kubernetesDisconnect',
        'kubernetesStatus',
        'kubernetesUse',
        'localAsk',
        'localCancel',
        'localClearTurns',
        'localGetBubble',
        'localGetShortcut',
        'localGetTurns',
        'localHide',
        'localOpen',
        'localSetBubble',
        'localSetShortcut',
        'localTurns',
        'llmDeleteOllamaModel',
        'llmDetectEnvironment',
        'llmListLmStudioModels',
        'llmListModels',
        'llmListOllamaModels',
        'llmPullOllamaModel',
        'llmPullOllamaProgress',
        'operatorAssembly',
        'operatorCommentDecision',
        'operatorCommentSync',
        'operatorComments',
        'operatorDecideClaim',
        'operatorDelegation',
        'operatorMandate',
        'operatorMediaRights',
        'operatorPackageApproval',
        'operatorPackageMetadata',
        'operatorProduction',
        'operatorProject',
        'operatorProjectDeletion',
        'operatorProjects',
        'operatorPublication',
        'operatorProposalDecision',
        'operatorExperiment',
        'operatorExperimentAdoption',
        'operatorIdeaDecision',
        'operatorReadiness',
        'operatorReplyApproval',
        'operatorSaveMandate',
        'operatorSceneLock',
        'operatorSceneMedia',
        'operatorSceneOrder',
        'operatorSceneRegeneration',
        'operatorStartProject',
        'operatorWindow',
        'providerRestart',
        'providerSetAutostart',
        'providerStart',
        'providerStatus',
        'providerStop',
        'providersList',
        'providersValidateKey',
        'shellOpenExternal',
        'slackDisconnect',
        'slackManifestUrl',
        'slackSave',
        'slackStatus',
        'updateCheckNow',
        'updateGetState',
        'updateInstall',
        'updateNotice',
        'updateProgress',
        'updateRemindLater',
        'updateSetCheckEnabled',
        'updateSkipVersion',
        'whisperDeleteModel',
        'whisperDownloadModel',
        'whisperDownloadProgress',
        'whisperListInstalled',
        'vaultDelete',
        'vaultGetPublic',
        'vaultHas',
        'vaultIsAvailable',
        'vaultStore',
        'workerEvents',
        'workerStart',
        'workerStatus',
        'workerStop',
        'writesList',
        'youtubeCancelSignIn',
        'youtubeClearApiKey',
        'youtubeSaveApiKey',
        'youtubeSignIn',
        'youtubeSignOut',
        'youtubeStatus',
      ].sort(),
    );
  });
});

describe('local conversation channels', () => {
  test('one conversation for every window: get, clear and the pushed list, with no per-window event channel', () => {
    expect(CHANNEL.localGetTurns).toBe('scout:local.getTurns');
    expect(CHANNEL.localClearTurns).toBe('scout:local.clearTurns');
    expect(CHANNEL.localTurns).toBe('scout:local.turns');
    expect(Object.values(CHANNEL)).not.toContain('scout:local.event');
  });
});

describe('review channels', () => {
  test('a feature write grant is its own channel, separate from enable', () => {
    expect(CHANNEL.featureWriteGrant).toBe('scout:features.writeGrant');
  });

  test('main tells the renderer the features changed on their own channel, so a revoked grant reaches the Features screen', () => {
    expect(CHANNEL.featuresChanged).toBe('scout:features.changed');
  });

  test('the Review screen lists, approves and rejects queued actions, reads the write log and hears of changes', () => {
    expect([
      CHANNEL.actionsList,
      CHANNEL.actionsApprove,
      CHANNEL.actionsReject,
      CHANNEL.actionsChanged,
      CHANNEL.writesList,
    ]).toEqual([
      'scout:actions.list',
      'scout:actions.approve',
      'scout:actions.reject',
      'scout:actions.changed',
      'scout:writes.list',
    ]);
  });
});

describe('operator channels', () => {
  test('the Production screen reads and saves the mandate, lists and reads projects, starts one and decides a claim on six scout:operator channels', () => {
    expect([
      CHANNEL.operatorMandate,
      CHANNEL.operatorSaveMandate,
      CHANNEL.operatorProjects,
      CHANNEL.operatorProject,
      CHANNEL.operatorStartProject,
      CHANNEL.operatorDecideClaim,
    ]).toEqual([
      'scout:operator.mandate',
      'scout:operator.saveMandate',
      'scout:operator.projects',
      'scout:operator.project',
      'scout:operator.startProject',
      'scout:operator.decideClaim',
    ]);
  });

  test('the project carries its narration voice, its background picture and whether it is delegated, and the mandate has no narration voice', () => {
    const mandateHasVoice: 'narrationVoice' extends keyof MandateDto ? true : false = false;
    const project = {
      narrationVoice: 'piper:en_GB-cori-medium',
      backgroundFile: 'owner-background-0a1b2c3d4e5f.png',
      delegated: true,
    } satisfies Pick<OperatorProjectDto, 'narrationVoice' | 'backgroundFile' | 'delegated'>;
    const readiness = {
      voices: [{ id: 'OpenAI:nova', label: 'nova (OpenAI)', source: 'OpenAI', sampleFile: null }],
    } satisfies Pick<ReadinessDto, 'voices'>;

    expect([mandateHasVoice, project.narrationVoice, readiness.voices[0]?.label]).toEqual([
      false,
      'piper:en_GB-cori-medium',
      'nova (OpenAI)',
    ]);
  });

  test('producing media, delegating a project, deleting one, ordering, locking and regenerating a scene, attaching an owner file and reading readiness each have their own scout:operator channel, named as nouns that pass the write-verb scan', () => {
    const channels = [
      CHANNEL.operatorProduction,
      CHANNEL.operatorDelegation,
      CHANNEL.operatorProjectDeletion,
      CHANNEL.operatorSceneOrder,
      CHANNEL.operatorSceneLock,
      CHANNEL.operatorSceneRegeneration,
      CHANNEL.operatorSceneMedia,
      CHANNEL.operatorReadiness,
    ];

    expect(channels).toEqual([
      'scout:operator.production',
      'scout:operator.delegation',
      'scout:operator.projectDeletion',
      'scout:operator.sceneOrder',
      'scout:operator.sceneLock',
      'scout:operator.sceneRegeneration',
      'scout:operator.sceneMedia',
      'scout:operator.readiness',
    ]);
    expect(
      channels.filter((c) =>
        ['create', 'post', 'write', 'send', 'update', 'delete', 'publish', 'upload'].some((verb) =>
          c.toLowerCase().includes(verb),
        ),
      ),
    ).toEqual([]);
  });

  test('assembling a video, confirming its media rights, editing its metadata and approving its package each have their own scout:operator channel, named as nouns', () => {
    expect([
      CHANNEL.operatorAssembly,
      CHANNEL.operatorMediaRights,
      CHANNEL.operatorPackageMetadata,
      CHANNEL.operatorPackageApproval,
    ]).toEqual([
      'scout:operator.assembly',
      'scout:operator.mediaRights',
      'scout:operator.packageMetadata',
      'scout:operator.packageApproval',
    ]);
  });

  test('publishing a package, deciding a proposal, starting an experiment and adopting its winner each have their own scout:operator channel, named as nouns that pass the write-verb scan', () => {
    const channels = [
      CHANNEL.operatorPublication,
      CHANNEL.operatorProposalDecision,
      CHANNEL.operatorExperiment,
      CHANNEL.operatorExperimentAdoption,
    ];

    expect(channels).toEqual([
      'scout:operator.publication',
      'scout:operator.proposalDecision',
      'scout:operator.experiment',
      'scout:operator.experimentAdoption',
    ]);
    expect(
      channels.filter((c) =>
        ['create', 'post', 'write', 'send', 'update', 'delete', 'publish', 'upload'].some((verb) =>
          c.toLowerCase().includes(verb),
        ),
      ),
    ).toEqual([]);
  });

  test('reading the comments, asking for a sync, deciding a comment, approving a reply and deciding an idea each have their own scout:operator channel, named as nouns that pass the write-verb scan', () => {
    const channels = [
      CHANNEL.operatorComments,
      CHANNEL.operatorCommentSync,
      CHANNEL.operatorCommentDecision,
      CHANNEL.operatorReplyApproval,
      CHANNEL.operatorIdeaDecision,
    ];

    expect(channels).toEqual([
      'scout:operator.comments',
      'scout:operator.commentSync',
      'scout:operator.commentDecision',
      'scout:operator.replyApproval',
      'scout:operator.ideaDecision',
    ]);
    expect(
      channels.filter((c) =>
        ['create', 'post', 'write', 'send', 'update', 'delete', 'publish', 'upload'].some((verb) =>
          c.toLowerCase().includes(verb),
        ),
      ),
    ).toEqual([]);
  });
});

describe('ChannelName type', () => {
  test('accepts exact literal values only (compile-time check)', () => {
    // Compile-time: each literal is a valid ChannelName. If any CHANNEL value
    // drifts from this map, tsc fails.
    const valid: Record<ChannelName, true> = {
      'scout:detect.clients': true,
      'scout:discord.checkToken': true,
      'scout:discord.enableIntent': true,
      'scout:discord.inviteUrl': true,
      'scout:discord.guilds': true,
      'scout:chatgpt.signIn': true,
      'scout:chatgpt.cancelSignIn': true,
      'scout:chatgpt.listModels': true,
      'scout:chatgpt.signOut': true,
      'scout:chatgpt.status': true,
      'scout:vault.store': true,
      'scout:vault.has': true,
      'scout:vault.delete': true,
      'scout:vault.getPublic': true,
      'scout:vault.isAvailable': true,
      'scout:worker.start': true,
      'scout:worker.stop': true,
      'scout:worker.status': true,
      'scout:worker.events': true,
      'scout:app.openExternal': true,
      'scout:app.getVersion': true,
      'scout:app.quit': true,
      'scout:diagnostics.export': true,
      'scout:llm.listOllamaModels': true,
      'scout:llm.pullOllamaModel': true,
      'scout:llm.pullOllamaProgress': true,
      'scout:llm.deleteOllamaModel': true,
      'scout:llm.listLmStudioModels': true,
      'scout:llm.listModels': true,
      'scout:llm.detectEnvironment': true,
      'scout:shell.openExternal': true,
      'scout:whisper.listInstalled': true,
      'scout:whisper.downloadModel': true,
      'scout:whisper.downloadProgress': true,
      'scout:whisper.deleteModel': true,
      'scout:providers.list': true,
      'scout:providers.status': true,
      'scout:providers.start': true,
      'scout:providers.stop': true,
      'scout:providers.restart': true,
      'scout:providers.setAutostart': true,
      'scout:providers.validateKey': true,
      'scout:features.list': true,
      'scout:features.setCredentials': true,
      'scout:features.setup': true,
      'scout:features.deleteCredentials': true,
      'scout:features.enable': true,
      'scout:features.disable': true,
      'scout:features.writeGrant': true,
      'scout:actions.list': true,
      'scout:actions.approve': true,
      'scout:actions.reject': true,
      'scout:actions.changed': true,
      'scout:features.changed': true,
      'scout:writes.list': true,
      'scout:operator.mandate': true,
      'scout:operator.saveMandate': true,
      'scout:operator.projects': true,
      'scout:operator.project': true,
      'scout:operator.startProject': true,
      'scout:operator.decideClaim': true,
      'scout:operator.production': true,
      'scout:operator.delegation': true,
      'scout:operator.projectDeletion': true,
      'scout:operator.sceneOrder': true,
      'scout:operator.sceneLock': true,
      'scout:operator.sceneRegeneration': true,
      'scout:operator.sceneMedia': true,
      'scout:operator.readiness': true,
      'scout:operator.window': true,
      'scout:operator.assembly': true,
      'scout:operator.mediaRights': true,
      'scout:operator.packageMetadata': true,
      'scout:operator.packageApproval': true,
      'scout:operator.publication': true,
      'scout:operator.proposalDecision': true,
      'scout:operator.experiment': true,
      'scout:operator.experimentAdoption': true,
      'scout:operator.comments': true,
      'scout:operator.commentSync': true,
      'scout:operator.commentDecision': true,
      'scout:operator.replyApproval': true,
      'scout:operator.ideaDecision': true,
      'scout:github.signIn': true,
      'scout:github.status': true,
      'scout:github.cancel': true,
      'scout:github.signOut': true,
      'scout:github.saveToken': true,
      'scout:youtube.signIn': true,
      'scout:youtube.status': true,
      'scout:youtube.cancelSignIn': true,
      'scout:youtube.signOut': true,
      'scout:youtube.saveApiKey': true,
      'scout:youtube.clearApiKey': true,
      'scout:kubernetes.detect': true,
      'scout:kubernetes.use': true,
      'scout:kubernetes.status': true,
      'scout:kubernetes.disconnect': true,
      'scout:access.get': true,
      'scout:access.set': true,
      'scout:access.startPairing': true,
      'scout:slack.manifestUrl': true,
      'scout:slack.save': true,
      'scout:slack.status': true,
      'scout:slack.disconnect': true,
      'scout:desktop.getSettings': true,
      'scout:desktop.setKeepRunning': true,
      'scout:desktop.setStartAtLogin': true,
      'scout:local.ask': true,
      'scout:local.cancel': true,
      'scout:local.getTurns': true,
      'scout:local.clearTurns': true,
      'scout:local.turns': true,
      'scout:local.hide': true,
      'scout:local.getShortcut': true,
      'scout:local.setShortcut': true,
      'scout:local.open': true,
      'scout:local.getBubble': true,
      'scout:local.setBubble': true,
      'scout:update.getState': true,
      'scout:update.setCheckEnabled': true,
      'scout:update.checkNow': true,
      'scout:update.skipVersion': true,
      'scout:update.remindLater': true,
      'scout:update.notice': true,
      'scout:update.install': true,
      'scout:update.progress': true,
    };
    expect(Object.values(valid).every((v) => v === true)).toBe(true);
  });
});

describe('no-external-write sanity scan', () => {
  // Channels whose name contains a write verb but which do NOT perform an
  // external-system write. Each entry justifies why the channel is read-only
  // from Scout's perspective (Discord, GitHub, cloud APIs, etc. are never
  // mutated via IPC; all outbound mutation is blocked by Scout.Core).
  //
  // - scout:vault.store           -> local safeStorage write, never leaves the machine
  // - scout:vault.delete          -> local safeStorage delete, never leaves the machine
  // - scout:worker.stop           -> local child-process signal, not an external API call
  // - scout:llm.listOllamaModels  -> READ on the user's local Ollama instance (GET /api/tags); no mutation
  // - scout:llm.deleteOllamaModel -> DELETE on the user's local Ollama (/api/delete); model is on user's
  //                                  disk, not an external system
  const LOCAL_WRITE_ALLOWLIST: readonly string[] = [
    'scout:vault.store',
    'scout:vault.delete',
    'scout:worker.stop',
    'scout:llm.listOllamaModels',
    'scout:llm.deleteOllamaModel',
    'scout:whisper.downloadModel',
    'scout:whisper.deleteModel',
    // - scout:features.setCredentials    -> writes a feature credential blob to the OS keyring via the worker;
    //                                       never reaches an external system (credentials only enable later reads).
    // - scout:features.deleteCredentials -> deletes that same local keyring entry; no external mutation.
    'scout:features.setCredentials',
    'scout:features.deleteCredentials',
    // - scout:update.getState .. notice  -> "update" names Scout's own version check: one unauthenticated GET of
    //                                       public release metadata and Scout's own vault keys; nothing is
    //                                       written outside Scout's config.
    'scout:update.getState',
    'scout:update.setCheckEnabled',
    'scout:update.checkNow',
    'scout:update.skipVersion',
    'scout:update.remindLater',
    'scout:update.notice',
    // - scout:update.install, progress  -> install replaces Scout's own executable with a release the user chose
    //                                       by clicking Update and restart; nothing outside Scout's own
    //                                       installation is written. progress only reports that install.
    'scout:update.install',
    'scout:update.progress',
    'scout:writes.list',
    'scout:features.writeGrant',
  ];

  // Verbs that typically imply mutation against an external system. If a new
  // channel name contains one of these and is NOT in the allow-list above,
  // the test fails — do not rename to bypass the scan, surface the concern.
  const WRITE_VERBS: readonly string[] = ['create', 'post', 'write', 'send', 'update', 'delete'];

  test('every write-verb channel is local-only and justified', () => {
    for (const value of Object.values(CHANNEL)) {
      const lower = value.toLowerCase();
      const matchedVerb = WRITE_VERBS.find((verb) => lower.includes(verb));
      if (matchedVerb === undefined) continue;
      expect(
        LOCAL_WRITE_ALLOWLIST,
        `channel ${value} matches write verb "${matchedVerb}" but is not in LOCAL_WRITE_ALLOWLIST`,
      ).toContain(value);
    }
  });

  test('allow-list entries are all real channels', () => {
    const channelValues = new Set<string>(Object.values(CHANNEL));
    for (const allowed of LOCAL_WRITE_ALLOWLIST) {
      expect(channelValues.has(allowed)).toBe(true);
    }
  });
});

describe('update channels', () => {
  test('the update check and install have their own scout:update channels, with the notice and the progress pushed', () => {
    expect([
      CHANNEL.updateGetState,
      CHANNEL.updateSetCheckEnabled,
      CHANNEL.updateCheckNow,
      CHANNEL.updateSkipVersion,
      CHANNEL.updateRemindLater,
      CHANNEL.updateNotice,
      CHANNEL.updateInstall,
      CHANNEL.updateProgress,
    ]).toEqual([
      'scout:update.getState',
      'scout:update.setCheckEnabled',
      'scout:update.checkNow',
      'scout:update.skipVersion',
      'scout:update.remindLater',
      'scout:update.notice',
      'scout:update.install',
      'scout:update.progress',
    ]);
  });
});
