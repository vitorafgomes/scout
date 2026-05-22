import type { DetectedClient, VaultKey } from './client-kinds.js';

export type { VaultKey };

export const CHANNEL = {
  detectClients: 'scout:detect.clients',
  discordBuildOAuthUrl: 'scout:discord.buildOAuthUrl',
  discordStartOAuthInstall: 'scout:discord.startOAuthInstall',
  discordCancelOAuthInstall: 'scout:discord.cancelOAuthInstall',
  vaultStore: 'scout:vault.store',
  vaultHas: 'scout:vault.has',
  vaultDelete: 'scout:vault.delete',
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
  whisperListInstalled: 'scout:whisper.listInstalled',
  whisperDownloadModel: 'scout:whisper.downloadModel',
  whisperDownloadProgress: 'scout:whisper.downloadProgress',
  whisperDeleteModel: 'scout:whisper.deleteModel',
  llmDetectEnvironment: 'scout:llm.detectEnvironment',
  shellOpenExternal: 'scout:shell.openExternal',
  providersList: 'scout:providers.list',
  providerStatus: 'scout:providers.status',
  providerStart: 'scout:providers.start',
  providerRestart: 'scout:providers.restart',
  providerSetAutostart: 'scout:providers.setAutostart',
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

export interface ScoutBridge {
  detectClients(): Promise<DetectClientsResponse>;
  discordBuildOAuthUrl(req: DiscordBuildOAuthUrlRequest): Promise<DiscordBuildOAuthUrlResponse>;
  discordStartOAuthInstall(
    req: DiscordStartOAuthInstallRequest,
  ): Promise<DiscordStartOAuthInstallResponse>;
  discordCancelOAuthInstall(): Promise<void>;
  vaultStore(req: VaultStoreRequest): Promise<void>;
  vaultHas(req: VaultHasRequest): Promise<VaultHasResponse>;
  vaultDelete(req: VaultDeleteRequest): Promise<VaultDeleteResponse>;
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
  llmDeleteOllamaModel(
    req: LlmDeleteOllamaModelRequest,
  ): Promise<LlmDeleteOllamaModelResponse>;
  llmListLmStudioModels(
    req: LlmListLmStudioModelsRequest,
  ): Promise<LlmListLmStudioModelsResponse>;
  whisperListInstalled(): Promise<WhisperListInstalledResponse>;
  whisperDownloadModel(req: WhisperDownloadModelRequest): Promise<WhisperDownloadModelResponse>;
  onWhisperDownloadProgress(
    listener: (event: WhisperDownloadProgressEvent) => void,
  ): () => void;
  whisperDeleteModel(req: WhisperDeleteModelRequest): Promise<WhisperDeleteModelResponse>;
  providersList(): Promise<ProvidersListResponse>;
  providerStatus(req: ProviderStatusRequest): Promise<ProviderStatusDto>;
  providerStart(req: ProviderStartRequest): Promise<ProviderStatusDto>;
  providerRestart(req: ProviderRestartRequest): Promise<ProviderStatusDto>;
  providerSetAutostart(req: ProviderAutostartRequest): Promise<void>;
}
