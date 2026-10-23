import type { CloudLlmProvider, DetectedClient, PublicVaultKey, VaultKey } from './client-kinds.js';

export type { VaultKey };

export const CHANNEL = {
  detectClients: 'scout:detect.clients',
  discordBuildOAuthUrl: 'scout:discord.buildOAuthUrl',
  discordStartOAuthInstall: 'scout:discord.startOAuthInstall',
  discordCancelOAuthInstall: 'scout:discord.cancelOAuthInstall',
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
  githubSignIn: 'scout:github.signIn',
  githubStatus: 'scout:github.status',
  githubCancel: 'scout:github.cancel',
  githubSignOut: 'scout:github.signOut',
  githubSaveToken: 'scout:github.saveToken',
  kubernetesDetect: 'scout:kubernetes.detect',
  kubernetesUse: 'scout:kubernetes.use',
  kubernetesStatus: 'scout:kubernetes.status',
  kubernetesDisconnect: 'scout:kubernetes.disconnect',
  accessGet: 'scout:access.get',
  accessSet: 'scout:access.set',
} as const satisfies Readonly<Record<string, string>>;

export type ChannelName = (typeof CHANNEL)[keyof typeof CHANNEL];

export interface DetectClientsResponse {
  readonly clients: readonly DetectedClient[];
}

export interface DiscordBuildOAuthUrlRequest {
  readonly clientId: string;
  readonly permissions: string;
  readonly scopes: readonly ['bot', 'applications.commands'];
}

export interface DiscordBuildOAuthUrlResponse {
  readonly url: string;
  readonly state: string;
}

export interface DiscordStartOAuthInstallRequest {
  readonly clientId: string;
  readonly permissions: string;
}

export interface DiscordOAuthResult {
  readonly guildId: string;
  readonly guildName: string;
}

export type DiscordStartOAuthInstallResponse = DiscordOAuthResult;

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

export type FeatureId = 'github' | 'kubernetes';
export const FEATURE_IDS: readonly FeatureId[] = Object.freeze(['github', 'kubernetes'] as const);
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

export interface FeatureScopeRowDto {
  readonly scope: string;
  readonly actions: readonly string[];
}

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
}

export interface FeaturesListResponse {
  readonly features: readonly FeatureDto[];
}

export interface FeatureIdRequest {
  readonly id: FeatureId;
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

export interface ScoutBridge {
  detectClients(): Promise<DetectClientsResponse>;
  discordBuildOAuthUrl(req: DiscordBuildOAuthUrlRequest): Promise<DiscordBuildOAuthUrlResponse>;
  discordStartOAuthInstall(
    req: DiscordStartOAuthInstallRequest,
  ): Promise<DiscordStartOAuthInstallResponse>;
  discordCancelOAuthInstall(): Promise<void>;
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
  featureEnable(req: FeatureIdRequest): Promise<FeatureDto>;
  featureDisable(req: FeatureIdRequest): Promise<FeatureDto>;
  githubSignIn(): Promise<GitHubPendingSignIn>;
  githubStatus(): Promise<GitHubStatus>;
  githubCancel(): Promise<void>;
  githubSignOut(): Promise<GitHubStatus>;
  githubSaveToken(req: GitHubSaveTokenRequest): Promise<GitHubStatus>;
  kubernetesDetect(): Promise<KubernetesDetection>;
  kubernetesUse(req: KubernetesUseRequest): Promise<KubernetesStatus>;
  kubernetesStatus(): Promise<KubernetesStatus>;
  kubernetesDisconnect(): Promise<KubernetesStatus>;
  accessGet(): Promise<AccessAllowedUsers>;
  accessSet(req: AccessAllowedUsers): Promise<AccessAllowedUsers>;
}
