import type { CloudLlmProvider, DetectedClient, PublicVaultKey, VaultKey } from './client-kinds.js';

export type { VaultKey };

export const CHANNEL = {
  detectClients: 'scout:detect.clients',
  discordCheckToken: 'scout:discord.checkToken',
  discordEnableIntent: 'scout:discord.enableIntent',
  discordInviteUrl: 'scout:discord.inviteUrl',
  discordGuilds: 'scout:discord.guilds',
  chatgptSignIn: 'scout:chatgpt.signIn',
  chatgptListModels: 'scout:chatgpt.listModels',
  chatgptCancelSignIn: 'scout:chatgpt.cancelSignIn',
  chatgptSignOut: 'scout:chatgpt.signOut',
  chatgptStatus: 'scout:chatgpt.status',
  vaultStore: 'scout:vault.store',
  vaultHas: 'scout:vault.has',
  vaultDelete: 'scout:vault.delete',
  vaultGetPublic: 'scout:vault.getPublic',
  vaultIsAvailable: 'scout:vault.isAvailable',
  workerStart: 'scout:worker.start',
  workerStop: 'scout:worker.stop',
  workerStatus: 'scout:worker.status',
  workerEvents: 'scout:worker.events',
  appOpenExternal: 'scout:app.openExternal',
  appGetVersion: 'scout:app.getVersion',
  appQuit: 'scout:app.quit',
  diagnosticsExport: 'scout:diagnostics.export',
  llmListOllamaModels: 'scout:llm.listOllamaModels',
  llmPullOllamaModel: 'scout:llm.pullOllamaModel',
  llmPullOllamaProgress: 'scout:llm.pullOllamaProgress',
  llmDeleteOllamaModel: 'scout:llm.deleteOllamaModel',
  llmListLmStudioModels: 'scout:llm.listLmStudioModels',
  llmListModels: 'scout:llm.listModels',
  whisperListInstalled: 'scout:whisper.listInstalled',
  whisperDownloadModel: 'scout:whisper.downloadModel',
  whisperDownloadProgress: 'scout:whisper.downloadProgress',
  whisperDeleteModel: 'scout:whisper.deleteModel',
  llmDetectEnvironment: 'scout:llm.detectEnvironment',
  shellOpenExternal: 'scout:shell.openExternal',
  providersList: 'scout:providers.list',
  providerStatus: 'scout:providers.status',
  providerStart: 'scout:providers.start',
  providerStop: 'scout:providers.stop',
  providerRestart: 'scout:providers.restart',
  providerSetAutostart: 'scout:providers.setAutostart',
  providersValidateKey: 'scout:providers.validateKey',
  featuresList: 'scout:features.list',
  featureSetCredentials: 'scout:features.setCredentials',
  featureSetup: 'scout:features.setup',
  featureDeleteCredentials: 'scout:features.deleteCredentials',
  featureEnable: 'scout:features.enable',
  featureDisable: 'scout:features.disable',
  featureWriteGrant: 'scout:features.writeGrant',
  featuresChanged: 'scout:features.changed',
  actionsList: 'scout:actions.list',
  actionsApprove: 'scout:actions.approve',
  actionsReject: 'scout:actions.reject',
  actionsChanged: 'scout:actions.changed',
  writesList: 'scout:writes.list',
  operatorMandate: 'scout:operator.mandate',
  operatorSaveMandate: 'scout:operator.saveMandate',
  operatorProjects: 'scout:operator.projects',
  operatorProject: 'scout:operator.project',
  operatorStartProject: 'scout:operator.startProject',
  operatorDecideClaim: 'scout:operator.decideClaim',
  operatorProduction: 'scout:operator.production',
  operatorDelegation: 'scout:operator.delegation',
  operatorProjectDeletion: 'scout:operator.projectDeletion',
  operatorSceneOrder: 'scout:operator.sceneOrder',
  operatorSceneLock: 'scout:operator.sceneLock',
  operatorSceneRegeneration: 'scout:operator.sceneRegeneration',
  operatorSceneMedia: 'scout:operator.sceneMedia',
  operatorReadiness: 'scout:operator.readiness',
  operatorWindow: 'scout:operator.window',
  operatorAssembly: 'scout:operator.assembly',
  operatorMediaRights: 'scout:operator.mediaRights',
  operatorPackageMetadata: 'scout:operator.packageMetadata',
  operatorPackageApproval: 'scout:operator.packageApproval',
  operatorPublication: 'scout:operator.publication',
  operatorProposalDecision: 'scout:operator.proposalDecision',
  operatorExperiment: 'scout:operator.experiment',
  operatorExperimentAdoption: 'scout:operator.experimentAdoption',
  operatorComments: 'scout:operator.comments',
  operatorCommentSync: 'scout:operator.commentSync',
  operatorCommentDecision: 'scout:operator.commentDecision',
  operatorReplyApproval: 'scout:operator.replyApproval',
  operatorIdeaDecision: 'scout:operator.ideaDecision',
  githubSignIn: 'scout:github.signIn',
  githubStatus: 'scout:github.status',
  githubCancel: 'scout:github.cancel',
  githubSignOut: 'scout:github.signOut',
  githubSaveToken: 'scout:github.saveToken',
  youtubeSignIn: 'scout:youtube.signIn',
  youtubeStatus: 'scout:youtube.status',
  youtubeCancelSignIn: 'scout:youtube.cancelSignIn',
  youtubeSignOut: 'scout:youtube.signOut',
  youtubeSaveApiKey: 'scout:youtube.saveApiKey',
  youtubeClearApiKey: 'scout:youtube.clearApiKey',
  kubernetesDetect: 'scout:kubernetes.detect',
  kubernetesUse: 'scout:kubernetes.use',
  kubernetesStatus: 'scout:kubernetes.status',
  kubernetesDisconnect: 'scout:kubernetes.disconnect',
  accessGet: 'scout:access.get',
  accessSet: 'scout:access.set',
  accessStartPairing: 'scout:access.startPairing',
  slackManifestUrl: 'scout:slack.manifestUrl',
  slackSave: 'scout:slack.save',
  slackStatus: 'scout:slack.status',
  slackDisconnect: 'scout:slack.disconnect',
  desktopGetSettings: 'scout:desktop.getSettings',
  desktopSetKeepRunning: 'scout:desktop.setKeepRunning',
  desktopSetStartAtLogin: 'scout:desktop.setStartAtLogin',
  localAsk: 'scout:local.ask',
  localCancel: 'scout:local.cancel',
  localGetTurns: 'scout:local.getTurns',
  localClearTurns: 'scout:local.clearTurns',
  localTurns: 'scout:local.turns',
  localHide: 'scout:local.hide',
  localGetShortcut: 'scout:local.getShortcut',
  localSetShortcut: 'scout:local.setShortcut',
  localOpen: 'scout:local.open',
  localGetBubble: 'scout:local.getBubble',
  localSetBubble: 'scout:local.setBubble',
  updateGetState: 'scout:update.getState',
  updateSetCheckEnabled: 'scout:update.setCheckEnabled',
  updateCheckNow: 'scout:update.checkNow',
  updateSkipVersion: 'scout:update.skipVersion',
  updateRemindLater: 'scout:update.remindLater',
  updateNotice: 'scout:update.notice',
  updateInstall: 'scout:update.install',
  updateProgress: 'scout:update.progress',
} as const satisfies Readonly<Record<string, string>>;

export type ChannelName = (typeof CHANNEL)[keyof typeof CHANNEL];

export interface DetectClientsResponse {
  readonly clients: readonly DetectedClient[];
}

/** A bot token the owner pastes; travels renderer to main once and is never sent back. */
export interface DiscordCheckTokenRequest {
  readonly token: string;
}

/** Whether the bot may read message text: on, off, or only switchable in the portal (100+ servers). */
export type DiscordMessageContent = 'on' | 'off' | 'privileged-100plus';

/** What the bot token says about the Discord application behind it; never carries the token. */
export interface DiscordBotInfo {
  readonly applicationId: string;
  readonly applicationName: string;
  readonly botUsername: string;
  readonly messageContent: DiscordMessageContent;
}

export type DiscordEnableIntentResult =
  | { readonly enabled: true }
  | { readonly enabled: false; readonly message: string; readonly portalUrl: string };

export interface DiscordInviteUrlResponse {
  readonly url: string;
}

export interface DiscordGuild {
  readonly id: string;
  readonly name: string;
}

export interface DiscordGuildsResponse {
  readonly guilds: readonly DiscordGuild[];
}

/** The only ChatGPT sign-in state the renderer ever sees; tokens stay in the main process. */
export interface ChatGptStatus {
  readonly signedIn: boolean;
  readonly email: string | null;
  readonly planUsageGranted: boolean;
}

export interface ChatGptSignOutResponse {
  readonly revocationConfirmed: boolean;
}

export interface ChatGptModel {
  readonly slug: string;
  readonly displayName: string;
}

export interface ChatGptListModelsResponse {
  readonly models: readonly ChatGptModel[];
}

export interface VaultStoreRequest {
  readonly key: VaultKey;
  readonly value: string;
}

export interface VaultHasRequest {
  readonly key: VaultKey;
}

export interface VaultHasResponse {
  readonly exists: boolean;
}

export interface VaultDeleteRequest {
  readonly key: VaultKey;
}

export interface VaultDeleteResponse {
  readonly deleted: boolean;
}

export interface VaultGetPublicRequest {
  readonly key: PublicVaultKey;
}

export interface VaultGetPublicResponse {
  readonly value: string | null;
}

export interface VaultIsAvailableResponse {
  readonly available: boolean;
  readonly reason?: 'no-keyring' | 'unknown';
}

export type WorkerStartRequest = Record<string, never>;

export interface WorkerStartResponse {
  readonly pid: number;
}

export type WorkerStopRequest = Record<string, never>;

export interface WorkerStopResponse {
  readonly exitCode: number | null;
}

export interface WorkerStatus {
  readonly state: 'idle' | 'starting' | 'running' | 'crashed' | 'stopped';
  readonly pid?: number;
  readonly startedAt?: string;
  readonly restarts: number;
  readonly lastExitCode?: number;
  /** Set on a crash after which the supervisor will not restart the worker by itself. */
  readonly gaveUp?: true;
}

export type WorkerStatusResponse = WorkerStatus;

export type WorkerEvent =
  | { readonly kind: 'stdout'; readonly line: string; readonly ts: string }
  | { readonly kind: 'stderr'; readonly line: string; readonly ts: string }
  | { readonly kind: 'state'; readonly status: WorkerStatus }
  | { readonly kind: 'exit'; readonly code: number | null; readonly signal: string | null };

// The URL is validated against an allow-list in main before `shell.openExternal`.
export interface AppOpenExternalRequest {
  readonly url: string;
}

export interface AppGetVersionResponse {
  readonly version: string;
}

export type AppQuitRequest = Record<string, never>;
export type AppQuitResponse = Record<string, never>;

// Serialised, token-redacted JSON diagnostics bundle.
export interface DiagnosticsExportResponse {
  readonly bundle: string;
}

export interface LlmListOllamaModelsRequest {
  readonly endpoint: string;
}

export interface OllamaModelInfo {
  readonly name: string;
  readonly modifiedAt: string;
  readonly sizeBytes: number;
}

export interface LlmListOllamaModelsResponse {
  readonly models: readonly OllamaModelInfo[];
}

export interface LlmPullOllamaModelRequest {
  readonly endpoint: string;
  readonly tag: string;
  readonly requestId: string;
}

export interface LlmPullOllamaModelResponse {
  readonly tag: string;
}

export interface LlmPullOllamaProgressEvent {
  readonly requestId: string;
  readonly tag: string;
  readonly status: string;
  readonly percent: number;
  readonly completedBytes: number;
  readonly totalBytes: number;
}

export interface LlmDeleteOllamaModelRequest {
  readonly endpoint: string;
  readonly tag: string;
}

export interface LlmDeleteOllamaModelResponse {
  readonly tag: string;
}

export type WhisperVariant = 'large-v3' | 'large-v3-turbo';

export interface WhisperListInstalledResponse {
  readonly installed: readonly WhisperVariant[];
}

export interface WhisperDownloadModelRequest {
  readonly variant: WhisperVariant;
  readonly requestId: string;
}

export interface WhisperDownloadModelResponse {
  readonly variant: WhisperVariant;
  readonly path: string;
}

export interface WhisperDownloadProgressEvent {
  readonly requestId: string;
  readonly variant: WhisperVariant;
  readonly percent: number;
  readonly completedBytes: number;
  readonly totalBytes: number;
}

export interface WhisperDeleteModelRequest {
  readonly variant: WhisperVariant;
}

export interface WhisperDeleteModelResponse {
  readonly variant: WhisperVariant;
}

export interface LlmListLmStudioModelsRequest {
  readonly endpoint: string;
}

export interface LmStudioModelInfo {
  readonly id: string;
}

export interface LlmListLmStudioModelsResponse {
  readonly models: readonly LmStudioModelInfo[];
}

export type ProviderId = 'ollama' | 'lm-studio';

export interface ProviderModelDto {
  readonly name: string;
  readonly sizeBytes: number;
  readonly modifiedAt: string;
}

export interface ProviderErrorDto {
  readonly code: string;
  readonly message: string;
}

export interface ProviderStatusDto {
  readonly id: ProviderId;
  readonly installed: boolean;
  readonly running: boolean;
  readonly endpoint: string | null;
  readonly version: string | null;
  readonly managedByWorker: boolean;
  readonly autostart: boolean;
  readonly supportsLifecycle: boolean;
  readonly lastError: ProviderErrorDto | null;
  readonly models: readonly ProviderModelDto[];
}

export interface ProviderStatusRequest {
  readonly id: ProviderId;
}

export interface ProviderStartRequest {
  readonly id: ProviderId;
}

export interface ProviderStopRequest {
  readonly id: ProviderId;
}

export interface ProviderRestartRequest {
  readonly id: ProviderId;
}

export interface ProviderAutostartRequest {
  readonly id: ProviderId;
  readonly enabled: boolean;
}

export interface ProvidersListResponse {
  readonly providers: readonly ProviderStatusDto[];
}

export interface ProviderKeyValidateRequest {
  readonly provider: CloudLlmProvider;
  readonly apiKey: string;
}

export type ProviderKeyValidateResponse =
  | { readonly ok: true }
  | {
      readonly ok: false;
      readonly reason: 'invalid-key' | 'unreachable' | 'unexpected';
      readonly status?: number;
    };

export interface LlmListModelsRequest {
  readonly provider: CloudLlmProvider;
}

/** A chat model the connected provider offers; `id` is what Scout writes to `llm.cloud-model`. */
export interface LlmModel {
  readonly id: string;
  readonly displayName: string;
}

export interface LlmListModelsResponse {
  readonly models: readonly LlmModel[];
}

export type FeatureId = 'github' | 'kubernetes' | 'youtube';
export const FEATURE_IDS: readonly FeatureId[] = Object.freeze([
  'github',
  'kubernetes',
  'youtube',
] as const);
export type FeatureStatus = 'enabled' | 'needs-credentials' | 'disabled' | 'error';

export interface FeatureCredentialFieldDto {
  readonly key: string;
  readonly kind: 'text' | 'password' | 'textarea';
  readonly label: string;
  readonly helperText: string | null;
  readonly createLinkUrl: string | null;
  readonly createLinkLabel: string | null;
}

export interface FeatureSettingFieldDto {
  readonly key: string;
  readonly kind: 'text' | 'password' | 'textarea';
  readonly label: string;
  readonly helperText: string | null;
  readonly createLinkUrl: string | null;
  readonly createLinkLabel: string | null;
  readonly required: boolean;
}

/** What a scope row lets Scout do: read, write, or a write that cannot be undone and waits for an approval per action. */
export type FeatureScopeAccess = 'read' | 'write' | 'irreversible';

export interface FeatureScopeRowDto {
  readonly scope: string;
  readonly actions: readonly string[];
  readonly access: FeatureScopeAccess;
}

/** Whether a feature writes nothing, writes content a person reads, or changes something outside Scout. */
export type FeatureWriteClass = 'none' | 'descriptive' | 'executive';

export interface FeatureDto {
  readonly id: FeatureId;
  readonly displayName: string;
  readonly description: string;
  readonly requiresCredentials: boolean;
  readonly hasCredentials: boolean;
  readonly enabled: boolean;
  readonly status: FeatureStatus;
  readonly lastError: string | null;
  readonly credentialFields: readonly FeatureCredentialFieldDto[];
  readonly settingsFields: readonly FeatureSettingFieldDto[];
  readonly settings: Readonly<Record<string, string | null>>;
  readonly scopeMatrix: readonly FeatureScopeRowDto[] | null;
  readonly setupSteps: readonly string[] | null;
  readonly writeClass: FeatureWriteClass;
  /** True once the owner allowed this feature's writes on the Features screen. */
  readonly granted: boolean;
  readonly grantedAt: string | null;
}

export interface FeaturesListResponse {
  readonly features: readonly FeatureDto[];
}

export interface FeatureIdRequest {
  readonly id: FeatureId;
}

/** Turns a feature on; `confirmWrites` is true only after the owner allowed the feature's write rows. */
export interface FeatureEnableRequest {
  readonly id: FeatureId;
  readonly confirmWrites: boolean;
}

/** Grants or revokes a feature's writes; granting needs `confirmWrites` true, after the owner allowed the write rows. */
export interface FeatureWriteGrantRequest {
  readonly id: FeatureId;
  readonly granted: boolean;
  readonly confirmWrites: boolean;
}

/** An action a granted feature asked to perform that cannot be undone; it runs only after the owner approves it. */
export interface PendingActionDto {
  readonly id: string;
  readonly featureId: string;
  readonly action: string;
  readonly target: string;
  readonly summary: string;
  readonly requestedAt: string;
  /** `pending`, `approved`, `rejected`, or `executed` once the worker recorded the approved write. */
  readonly status: string;
  readonly decidedAt: string | null;
}

export interface PendingActionsResponse {
  readonly actions: readonly PendingActionDto[];
}

/** The owner's decision on one pending action; `reason` is optional and kept with the decision. */
export interface ActionDecisionRequest {
  readonly id: string;
  readonly reason?: string;
}

/** One write Scout performed: when, for which feature, what, on what, how it ended and who approved it. */
export interface WriteLogEntryDto {
  readonly at: string;
  readonly featureId: string;
  readonly action: string;
  readonly target: string;
  readonly outcome: string;
  readonly approvedBy: string | null;
  readonly actionId: string | null;
}

export interface WritesListRequest {
  readonly limit: number;
}

export interface WritesListResponse {
  readonly entries: readonly WriteLogEntryDto[];
}

/** The channel mandate the operator works to; `updatedAt` is set once the owner saved it. */
export interface MandateDto {
  readonly objective: string;
  readonly audience: string;
  readonly pillars: readonly string[];
  readonly cadence: string;
  readonly language: string;
  readonly voiceAndStyle: string;
  readonly blockedTopics: readonly string[];
  readonly targetMetric: string;
  readonly targetValue: string;
  readonly updatedAt: string | null;
}

/** The mandate fields the owner saves; the control plane stamps the time. */
export type MandateSaveRequest = Omit<MandateDto, 'updatedAt'>;

/** The saved mandate, or null while the owner has not saved one. */
export interface MandateResponse {
  readonly mandate: MandateDto | null;
}

/** A topic the operator proposed, why, and how well it fits the mandate (0 to 1). */
export interface TopicCandidateDto {
  readonly title: string;
  readonly reason: string;
  readonly fit: number;
}

export interface ScriptSectionDto {
  readonly heading: string;
  readonly narration: string;
}

export interface ChapterDto {
  readonly at: string;
  readonly title: string;
}

/** The script draft of a video project, with the metadata the upload would carry. */
export interface ScriptDto {
  readonly hook: string;
  readonly sections: readonly ScriptSectionDto[];
  readonly callToAction: string;
  readonly titleVariants: readonly string[];
  readonly description: string;
  readonly tags: readonly string[];
  readonly chapters: readonly ChapterDto[];
}

/** A factual claim in the script, the sources the operator found for it, and the owner's verdict. */
export interface ClaimDto {
  readonly id: string;
  readonly text: string;
  readonly risk: string;
  readonly sourceUrls: readonly string[];
  readonly status: string;
  readonly note: string | null;
}

/** One video the operator works on, from the topic to the script and its claims. */
export interface OperatorProjectDto {
  readonly id: string;
  readonly topic: string;
  readonly status: string;
  readonly origin: string;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly candidates: readonly TopicCandidateDto[];
  readonly script: ScriptDto | null;
  readonly claims: readonly ClaimDto[];
  readonly error: string | null;
  readonly scenes: readonly SceneDto[];
  /** The assembled video and its upload metadata; null until the owner first asks for assembly. */
  readonly package: VideoPackageDto | null;
  readonly publication: PublicationDto | null;
  /** The voice id the owner produced this video in; empty means Scout's choice. */
  readonly narrationVoice: string;
  /** The picture behind every scene without its own, named inside the project's media directory. */
  readonly backgroundFile: string | null;
  /** True once the owner delegated the project: Scout produces and assembles it and stops before the approval. */
  readonly delegated: boolean;
  /** The owner's words for the pictures and animation of this video, at most 500 characters; empty for none. */
  readonly visualBrief: string;
  readonly motion: ProjectMotion;
}

/** How much the assembler moves each picture. */
export type ProjectMotion = 'still' | 'gentle' | 'dynamic';

export type PrivacyStatus = 'public' | 'private' | 'unlisted';

export interface PublicationDto {
  readonly status: string;
  readonly videoId: string | null;
  readonly privacyStatus: string;
  readonly publishAt: string | null;
  readonly madeForKids: boolean;
  readonly syntheticMedia: boolean;
  readonly uploadedAt: string | null;
  readonly error: string | null;
  readonly snapshots: readonly SnapshotDto[];
  readonly proposals: readonly ProposalDto[];
  readonly experiment: ExperimentDto | null;
}

export interface SnapshotDto {
  readonly window: string;
  readonly takenAt: string;
  readonly views: number;
  readonly minutesWatched: number;
  readonly averageViewDurationSeconds: number;
  readonly averageViewPercentage: number;
  readonly subscribersGained: number;
  readonly subscribersLost: number;
  readonly likes: number;
  readonly comments: number;
  readonly shares: number;
  readonly impressions: number | null;
  readonly impressionsCtr: number | null;
  readonly retention: readonly RetentionPointDto[];
}

export interface RetentionPointDto {
  readonly elapsedRatio: number;
  readonly audienceWatchRatio: number;
}

export interface ProposalDto {
  readonly id: string;
  readonly kind: string;
  readonly text: string;
  readonly reason: string;
  readonly status: string;
  readonly thumbnailFile: string | null;
}

export interface ExperimentDto {
  readonly id: string;
  readonly kind: string;
  readonly status: string;
  readonly arms: readonly ExperimentArmDto[];
  readonly winnerArmId: string | null;
  readonly result: string | null;
}

export interface ExperimentArmDto {
  readonly id: string;
  readonly label: string;
  readonly value: string;
  readonly startedAt: string | null;
  readonly endedAt: string | null;
  readonly impressions: number;
  readonly clicks: number;
  readonly averageViewPercentage: number;
}

export interface PublishPackageRequest {
  readonly projectId: string;
  readonly privacyStatus: PrivacyStatus;
  readonly publishAt: string | null;
  readonly madeForKids: boolean;
  readonly syntheticMedia: boolean;
}

export interface ProposalDecisionRequest {
  readonly projectId: string;
  readonly proposalId: string;
  readonly accept: boolean;
}

export interface ExperimentStartRequest {
  readonly projectId: string;
  readonly armHours: number;
}

/** One top-level comment on the owner's channel, as Scout stored it, with the model's kind and theme and the owner's reply. */
export interface AudienceCommentDto {
  readonly id: string;
  readonly threadId: string;
  readonly videoId: string;
  readonly videoTitle: string;
  readonly author: string;
  readonly text: string;
  readonly publishedAt: string;
  readonly likeCount: number;
  readonly kind: string;
  readonly theme: string;
  readonly status: string;
  readonly draft: string;
  readonly approvedText: string | null;
  readonly replyId: string | null;
  readonly error: string | null;
  readonly decidedAt: string | null;
  readonly fetchedAt: string;
}

/** A video idea taken from an audience request; `status` is proposed, used or dismissed. */
export interface AudienceIdeaDto {
  readonly id: string;
  readonly text: string;
  readonly commentId: string;
  readonly status: string;
  readonly projectId: string | null;
}

/** The stored comments, newest first, the audience ideas and the state of the comment sync. */
export interface CommentsViewDto {
  readonly comments: readonly AudienceCommentDto[];
  readonly ideas: readonly AudienceIdeaDto[];
  readonly syncedAt: string | null;
  readonly syncRequestedAt: string | null;
  readonly syncError: string | null;
}

export type CommentDecision = 'draft' | 'dismiss' | 'not_spam' | 'hold';

export interface CommentDecisionRequest {
  readonly commentId: string;
  readonly decision: CommentDecision;
}

/** The owner's reply to one comment, 1 to 1000 characters after trimming. */
export interface ReplyApprovalRequest {
  readonly commentId: string;
  readonly text: string;
}

export interface IdeaDecisionRequest {
  readonly ideaId: string;
  readonly use: boolean;
}

/**
 * The assembled video of a project: the MP4, its Shorts and thumbnail named inside the project's media directory,
 * the checks the worker ran, the owner's media-rights confirmation and approval, and the upload metadata.
 */
export interface VideoPackageDto {
  readonly status: string;
  readonly videoFile: string | null;
  readonly shorts: readonly ShortClipDto[];
  readonly thumbnailFile: string | null;
  readonly checks: readonly PackageCheckDto[];
  readonly rightsConfirmed: boolean;
  readonly approvedAt: string | null;
  readonly title: string;
  readonly description: string;
  readonly tags: readonly string[];
  readonly durationMs: number;
  readonly error: string | null;
  readonly metadataEdited: boolean;
}

/** One vertical Shorts cut, `startMs` into the video, with its hook line. */
export interface ShortClipDto {
  readonly file: string;
  readonly startMs: number;
  readonly durationMs: number;
  readonly hook: string;
}

/** One check the worker ran on the package; `result` is pass, warn or fail. */
export interface PackageCheckDto {
  readonly name: string;
  readonly result: string;
  readonly detail: string;
}

/** The owner's upload metadata for a package, within YouTube's limits. */
export interface PackageMetadataRequest {
  readonly projectId: string;
  readonly title: string;
  readonly description: string;
  readonly tags: readonly string[];
}

/** One scene of a video: its narration, the picture it needs, and the media files made for it, named inside the project's media directory. */
export interface SceneDto {
  readonly id: string;
  readonly order: number;
  readonly narration: string;
  readonly visualPrompt: string;
  readonly locked: boolean;
  readonly status: string;
  readonly error: string | null;
  readonly audioSource: string;
  readonly visualSource: string;
  readonly visualQuery: string;
  readonly visualCredit: string | null;
  readonly audioFile: string | null;
  readonly visualFile: string | null;
  readonly durationMs: number;
}

export interface OperatorProjectsRequest {
  readonly limit: number;
}

export interface OperatorProjectsResponse {
  readonly projects: readonly OperatorProjectDto[];
}

export interface OperatorProjectRequest {
  readonly id: string;
}

/** The answer to deleting a project: it is gone from Scout's data folder. */
export interface OperatorProjectDeletionResponse {
  readonly deleted: true;
}

/** The project to produce and the voice to narrate it in, a readiness voice id or empty for Scout's choice. */
export interface OperatorProduceRequest {
  readonly id: string;
  readonly narrationVoice: string;
  readonly visualBrief: string;
  readonly motion: ProjectMotion;
}

/** The project the owner delegates and its narration voice; Scout produces and assembles it and stops before the approval. */
export interface OperatorDelegateRequest {
  readonly id: string;
  readonly narrationVoice: string;
  readonly visualBrief: string;
  readonly motion: ProjectMotion;
}

/** The topic of a video the owner asks the operator to make; 3 to 200 characters. */
export interface StartProjectRequest {
  readonly topic: string;
}

export type ClaimStatus = 'supported' | 'unsupported' | 'waived';

/** The owner's verdict on one claim; a waived claim needs the note saying why. */
export interface ClaimDecisionRequest {
  readonly projectId: string;
  readonly claimId: string;
  readonly status: ClaimStatus;
  readonly note?: string;
}

/** The scenes of a project in the order the owner wants them; every scene id exactly once. */
export interface SceneOrderRequest {
  readonly projectId: string;
  readonly sceneIds: readonly string[];
}

export interface SceneLockRequest {
  readonly projectId: string;
  readonly sceneId: string;
  readonly locked: boolean;
}

export interface SceneRequest {
  readonly projectId: string;
  readonly sceneId: string;
}

export type SceneMediaKind = 'audio' | 'visual';

/**
 * Which scene gets an owner's file and of which kind, or the project's background picture with an empty scene id;
 * main asks the owner for the file, the renderer never sends a path.
 */
export interface SceneMediaRequest {
  readonly projectId: string;
  readonly sceneId: string;
  readonly kind: SceneMediaKind | 'background';
}

/** The project after the owner's file was attached, or null when the owner closed the file dialog. */
export interface SceneMediaResponse {
  readonly project: OperatorProjectDto | null;
}

/** One narration voice this machine offers; `id` is what a project stores, `source` groups the list. */
export interface VoiceDto {
  readonly id: string;
  readonly label: string;
  readonly source: string;
  /** A short sample of the voice, named inside the voice-samples directory; null while none is made. */
  readonly sampleFile: string | null;
}

/** What the operator can make on this machine right now, as the worker last reported it. */
export interface ReadinessDto {
  readonly speechAvailable: boolean;
  readonly imagesAvailable: boolean;
  readonly stockPictures: boolean;
  readonly provider: string;
  readonly ffmpegAvailable: boolean;
  readonly youtubeSignedIn: boolean;
  readonly notes: readonly string[];
  readonly voices: readonly VoiceDto[];
  readonly reportedAt: string | null;
}

export interface FeatureSetCredentialsRequest {
  readonly id: FeatureId;
  readonly body: Readonly<Record<string, string>>;
}

export interface FeatureSetupRequest {
  readonly id: FeatureId;
  readonly credentials: Readonly<Record<string, string>>;
  readonly settings: Readonly<Record<string, string | null>>;
}

/** The one-time code the owner types on GitHub's device page; never a token. */
export interface GitHubPendingSignIn {
  readonly userCode: string;
  readonly verificationUri: string;
}

export interface GitHubStatus {
  readonly configured: boolean;
  readonly connected: boolean;
  readonly method: 'browser' | 'token' | null;
  readonly login: string | null;
  readonly warning: string | null;
  readonly installed: boolean;
  readonly installUrl: string | null;
  readonly pending: GitHubPendingSignIn | null;
  readonly error: { readonly code: string; readonly message: string } | null;
}

/** A token the owner pastes; travels renderer to main once and is never sent back. */
export interface GitHubSaveTokenRequest {
  readonly token: string;
}

/** The YouTube connection as the renderer sees it; never carries a token, the key or the OAuth client. */
export interface YouTubeStatus {
  readonly configured: boolean;
  readonly connected: boolean;
  readonly method: 'oauth' | 'apiKey' | null;
  readonly channelId: string | null;
  readonly channelTitle: string | null;
  readonly pending: boolean;
  readonly error: string | null;
  readonly clientConfigPath: string;
  readonly source: 'env' | 'file' | 'bundled' | 'embedded' | null;
  readonly scopes: readonly string[];
}

/** A YouTube Data API key the owner pastes; travels renderer to main once and is never sent back. */
export interface YouTubeSaveApiKeyRequest {
  readonly apiKey: string;
}

export type KubernetesAuthKind =
  'token' | 'client-certificate' | 'exec' | 'auth-provider' | 'basic' | 'unknown';

/** One context of the kubeconfig on this computer; never carries a credential. */
export interface KubernetesContextInfo {
  readonly name: string;
  readonly cluster: string;
  readonly server: string;
  readonly namespace?: string;
  readonly auth: KubernetesAuthKind;
  /** The command and arguments an exec context runs to get a token, shown before consent. */
  readonly execCommand?: string;
}

export interface KubernetesDetection {
  readonly path: string | null;
  readonly contexts: readonly KubernetesContextInfo[];
  readonly error: string | null;
}

/** An allowed kubeconfig context and the label the model and chat users see instead of its name. */
export interface KubernetesLabelledContext {
  readonly name: string;
  readonly label: string;
}

/** The contexts the owner allowed; main validates each against the file it located itself. */
export interface KubernetesUseRequest {
  readonly contexts: readonly KubernetesLabelledContext[];
}

export interface KubernetesStatus {
  readonly path: string | null;
  readonly contexts: readonly KubernetesLabelledContext[];
  readonly connected: boolean;
}

/** The platform user ids allowed to use Scout from chat; main validates and stores them. */
export interface AccessAllowedUsers {
  readonly userIds: readonly string[];
}

/** A one-time pairing code main issued; the owner sends `pair <code>` to Scout before `expiresAt`. */
export interface AccessPairing {
  readonly code: string;
  readonly expiresAt: string;
}

/** The link that opens Slack's app creation flow prefilled with Scout's manifest. */
export interface SlackManifestUrlResponse {
  readonly url: string;
}

/** The two tokens the owner pastes; travel renderer to main once and are never sent back. */
export interface SlackSaveRequest {
  readonly botToken: string;
  readonly appToken: string;
}

/** The workspace and bot user Slack reported when the tokens were saved; never a token. */
export interface SlackStatus {
  readonly connected: boolean;
  readonly team: string | null;
  readonly botUser: string | null;
}

/** How Scout stays on the desktop; keepRunning is always false while the system tray is unavailable. */
export interface DesktopSettings {
  readonly keepRunning: boolean;
  readonly startAtLogin: boolean;
  readonly trayAvailable: boolean;
}

export interface DesktopToggleRequest {
  readonly enabled: boolean;
}

/** A recording made in the quick-ask window: 16 kHz mono 16-bit PCM WAV, base64-encoded. */
export interface LocalAudio {
  readonly contentType: 'audio/wav';
  readonly base64: string;
}

/** One question from the quick-ask window; the renderer picks the id, main checks it. */
export type LocalAskRequest =
  | { readonly id: string; readonly text: string }
  | { readonly id: string; readonly audio: LocalAudio };

export interface LocalCancelRequest {
  readonly id: string;
}

export interface LocalCitation {
  readonly label: string;
  readonly url?: string;
}

/** A step of the worker's reply to one quick-ask question; `text` is always the full reply so far. */
export interface LocalEvent {
  readonly id: string;
  readonly event: 'begin' | 'update' | 'complete' | 'fail';
  readonly text?: string;
  readonly citations?: readonly LocalCitation[];
}

/**
 * One question and its reply in the local conversation main keeps for every window: the question text,
 * or "(voice message)" for a recording, and the reply so far.
 */
export interface LocalTurn {
  readonly id: string;
  readonly question: string;
  readonly reply: string;
  readonly citations: readonly LocalCitation[];
  readonly state: 'running' | 'done' | 'failed';
  /** True when the question failed because the worker is not running. */
  readonly notRunning?: boolean;
  /** True for a message Scout posted on its own, such as operator progress; it has no question. */
  readonly notice?: true;
}

/**
 * The global shortcut that opens the quick-ask window, in Electron accelerator syntax; `registered` is whether
 * the OS took it, and `hint` says what to do instead where the desktop refuses global shortcuts.
 */
export interface LocalShortcut {
  readonly accelerator: string;
  readonly registered: boolean;
  readonly hint: string | null;
}

/** The floating Scout button: whether it shows, which corner it sits in, and on which display. */
export interface LocalBubbleSettings {
  readonly enabled: boolean;
  readonly corner: 'bottom-right' | 'bottom-left' | 'top-right' | 'top-left';
  readonly display: 'cursor' | 'primary';
}

/** A newer Scout Desktop on GitHub Releases: its version, its notes as plain text, and the links the owner may open. */
export interface UpdateNotice {
  readonly currentVersion: string;
  readonly latestVersion: string;
  readonly notes: string;
  readonly releaseUrl: string;
  readonly downloadUrl: string;
  readonly publishedAt: string;
  /** True when this package can replace itself: a packaged AppImage or the Windows installer. */
  readonly canInstall: boolean;
}

/** Where an install the user started stands; `percent` and the byte counts are set only while downloading. */
export interface UpdateProgress {
  readonly phase: 'checking' | 'downloading' | 'ready' | 'installing' | 'failed';
  readonly percent: number | null;
  readonly transferred: number | null;
  readonly total: number | null;
  readonly message: string | null;
}

/** The update check as Settings shows it; `packaged` is false in a development build, which never checks. */
export interface UpdateState {
  readonly checkEnabled: boolean;
  readonly lastCheckAt: string | null;
  readonly lastResult: string | null;
  readonly notice: UpdateNotice | null;
  readonly packaged: boolean;
}

export interface UpdateSkipVersionRequest {
  readonly version: string;
}

export interface ScoutBridge {
  detectClients(): Promise<DetectClientsResponse>;
  discordCheckToken(req: DiscordCheckTokenRequest): Promise<DiscordBotInfo>;
  discordEnableIntent(): Promise<DiscordEnableIntentResult>;
  discordInviteUrl(): Promise<DiscordInviteUrlResponse>;
  discordGuilds(): Promise<DiscordGuildsResponse>;
  chatgptSignIn(): Promise<ChatGptStatus>;
  chatgptCancelSignIn(): Promise<void>;
  chatgptListModels(): Promise<ChatGptListModelsResponse>;
  chatgptSignOut(): Promise<ChatGptSignOutResponse>;
  chatgptStatus(): Promise<ChatGptStatus>;
  vaultStore(req: VaultStoreRequest): Promise<void>;
  vaultHas(req: VaultHasRequest): Promise<VaultHasResponse>;
  vaultDelete(req: VaultDeleteRequest): Promise<VaultDeleteResponse>;
  vaultGetPublic(req: VaultGetPublicRequest): Promise<VaultGetPublicResponse>;
  vaultIsAvailable(): Promise<VaultIsAvailableResponse>;
  workerStart(): Promise<WorkerStartResponse>;
  workerStop(): Promise<WorkerStopResponse>;
  workerStatus(): Promise<WorkerStatusResponse>;
  onWorkerEvent(listener: (event: WorkerEvent) => void): () => void;
  appOpenExternal(req: AppOpenExternalRequest): Promise<void>;
  appGetVersion(): Promise<AppGetVersionResponse>;
  appQuit(): Promise<void>;
  diagnosticsExport(): Promise<DiagnosticsExportResponse>;
  llmListOllamaModels(req: LlmListOllamaModelsRequest): Promise<LlmListOllamaModelsResponse>;
  llmPullOllamaModel(req: LlmPullOllamaModelRequest): Promise<LlmPullOllamaModelResponse>;
  onLlmPullOllamaProgress(listener: (event: LlmPullOllamaProgressEvent) => void): () => void;
  llmDeleteOllamaModel(req: LlmDeleteOllamaModelRequest): Promise<LlmDeleteOllamaModelResponse>;
  llmListLmStudioModels(req: LlmListLmStudioModelsRequest): Promise<LlmListLmStudioModelsResponse>;
  whisperListInstalled(): Promise<WhisperListInstalledResponse>;
  whisperDownloadModel(req: WhisperDownloadModelRequest): Promise<WhisperDownloadModelResponse>;
  onWhisperDownloadProgress(listener: (event: WhisperDownloadProgressEvent) => void): () => void;
  whisperDeleteModel(req: WhisperDeleteModelRequest): Promise<WhisperDeleteModelResponse>;
  providersList(): Promise<ProvidersListResponse>;
  providerStatus(req: ProviderStatusRequest): Promise<ProviderStatusDto>;
  providerStart(req: ProviderStartRequest): Promise<ProviderStatusDto>;
  providerStop(req: ProviderStopRequest): Promise<ProviderStatusDto>;
  providerRestart(req: ProviderRestartRequest): Promise<ProviderStatusDto>;
  providerSetAutostart(req: ProviderAutostartRequest): Promise<void>;
  validateProviderKey(req: ProviderKeyValidateRequest): Promise<ProviderKeyValidateResponse>;
  llmListModels(req: LlmListModelsRequest): Promise<LlmListModelsResponse>;
  featuresList(): Promise<FeaturesListResponse>;
  featureSetCredentials(req: FeatureSetCredentialsRequest): Promise<FeatureDto>;
  featureSetup(req: FeatureSetupRequest): Promise<FeatureDto>;
  featureDeleteCredentials(req: FeatureIdRequest): Promise<FeatureDto>;
  featureEnable(req: FeatureEnableRequest): Promise<FeatureDto>;
  featureDisable(req: FeatureIdRequest): Promise<FeatureDto>;
  featureWriteGrant(req: FeatureWriteGrantRequest): Promise<FeatureDto>;
  /** Calls `listener` each time main announces that a feature's state changed outside the Features screen. */
  onFeaturesChanged(listener: () => void): () => void;
  actionsList(): Promise<PendingActionsResponse>;
  actionsApprove(req: ActionDecisionRequest): Promise<PendingActionDto>;
  actionsReject(req: ActionDecisionRequest): Promise<PendingActionDto>;
  onActionsChanged(listener: () => void): () => void;
  writesList(req: WritesListRequest): Promise<WritesListResponse>;
  operatorMandate(): Promise<MandateResponse>;
  operatorSaveMandate(req: MandateSaveRequest): Promise<MandateDto>;
  operatorProjects(req: OperatorProjectsRequest): Promise<OperatorProjectsResponse>;
  operatorProject(req: OperatorProjectRequest): Promise<OperatorProjectDto>;
  operatorStartProject(req: StartProjectRequest): Promise<OperatorProjectDto>;
  operatorDecideClaim(req: ClaimDecisionRequest): Promise<OperatorProjectDto>;
  operatorProduce(req: OperatorProduceRequest): Promise<OperatorProjectDto>;
  operatorDelegate(req: OperatorDelegateRequest): Promise<OperatorProjectDto>;
  operatorDeleteProject(req: OperatorProjectRequest): Promise<OperatorProjectDeletionResponse>;
  operatorReorderScenes(req: SceneOrderRequest): Promise<OperatorProjectDto>;
  operatorSetSceneLocked(req: SceneLockRequest): Promise<OperatorProjectDto>;
  operatorRegenerateScene(req: SceneRequest): Promise<OperatorProjectDto>;
  operatorAttachSceneMedia(req: SceneMediaRequest): Promise<SceneMediaResponse>;
  operatorReadiness(): Promise<ReadinessDto>;
  /** Opens the YouTube operator window, or brings it to the front when it is already open. */
  operatorOpenWindow(): Promise<void>;
  operatorAssemble(req: OperatorProjectRequest): Promise<OperatorProjectDto>;
  operatorConfirmMediaRights(req: OperatorProjectRequest): Promise<OperatorProjectDto>;
  operatorEditPackageMetadata(req: PackageMetadataRequest): Promise<OperatorProjectDto>;
  operatorApprovePackage(req: OperatorProjectRequest): Promise<OperatorProjectDto>;
  operatorPublishPackage(req: PublishPackageRequest): Promise<OperatorProjectDto>;
  operatorDecideProposal(req: ProposalDecisionRequest): Promise<OperatorProjectDto>;
  operatorStartExperiment(req: ExperimentStartRequest): Promise<OperatorProjectDto>;
  operatorAdoptExperimentWinner(req: OperatorProjectRequest): Promise<OperatorProjectDto>;
  operatorComments(): Promise<CommentsViewDto>;
  operatorRequestCommentSync(): Promise<CommentsViewDto>;
  operatorDecideComment(req: CommentDecisionRequest): Promise<CommentsViewDto>;
  operatorApproveReply(req: ReplyApprovalRequest): Promise<CommentsViewDto>;
  operatorDecideIdea(req: IdeaDecisionRequest): Promise<CommentsViewDto>;
  githubSignIn(): Promise<GitHubPendingSignIn>;
  githubStatus(): Promise<GitHubStatus>;
  githubCancel(): Promise<void>;
  githubSignOut(): Promise<GitHubStatus>;
  githubSaveToken(req: GitHubSaveTokenRequest): Promise<GitHubStatus>;
  youtubeSignIn(): Promise<YouTubeStatus>;
  youtubeStatus(): Promise<YouTubeStatus>;
  youtubeCancelSignIn(): Promise<void>;
  youtubeSignOut(): Promise<YouTubeStatus>;
  youtubeSaveApiKey(req: YouTubeSaveApiKeyRequest): Promise<YouTubeStatus>;
  youtubeClearApiKey(): Promise<YouTubeStatus>;
  kubernetesDetect(): Promise<KubernetesDetection>;
  kubernetesUse(req: KubernetesUseRequest): Promise<KubernetesStatus>;
  kubernetesStatus(): Promise<KubernetesStatus>;
  kubernetesDisconnect(): Promise<KubernetesStatus>;
  accessGet(): Promise<AccessAllowedUsers>;
  accessSet(req: AccessAllowedUsers): Promise<AccessAllowedUsers>;
  accessStartPairing(): Promise<AccessPairing>;
  slackManifestUrl(): Promise<SlackManifestUrlResponse>;
  slackSave(req: SlackSaveRequest): Promise<SlackStatus>;
  slackStatus(): Promise<SlackStatus>;
  slackDisconnect(): Promise<SlackStatus>;
  desktopGetSettings(): Promise<DesktopSettings>;
  desktopSetKeepRunning(req: DesktopToggleRequest): Promise<DesktopSettings>;
  desktopSetStartAtLogin(req: DesktopToggleRequest): Promise<DesktopSettings>;
  localAsk(req: LocalAskRequest): Promise<void>;
  localCancel(req: LocalCancelRequest): Promise<void>;
  localGetTurns(): Promise<readonly LocalTurn[]>;
  localClearTurns(): Promise<void>;
  onLocalTurns(listener: (turns: readonly LocalTurn[]) => void): () => void;
  localHide(): Promise<void>;
  localGetShortcut(): Promise<LocalShortcut>;
  localSetShortcut(req: Pick<LocalShortcut, 'accelerator'>): Promise<LocalShortcut>;
  localOpen(): Promise<void>;
  localGetBubble(): Promise<LocalBubbleSettings>;
  localSetBubble(req: LocalBubbleSettings): Promise<LocalBubbleSettings>;
  updateGetState(): Promise<UpdateState>;
  updateSetCheckEnabled(req: DesktopToggleRequest): Promise<UpdateState>;
  updateCheckNow(): Promise<UpdateState>;
  updateSkipVersion(req: UpdateSkipVersionRequest): Promise<UpdateState>;
  updateRemindLater(): Promise<UpdateState>;
  onUpdateNotice(listener: (notice: UpdateNotice | null) => void): () => void;
  updateInstall(): Promise<UpdateState>;
  onUpdateProgress(listener: (progress: UpdateProgress) => void): () => void;
}
