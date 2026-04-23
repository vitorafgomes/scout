import type { DetectedClient } from './client-kinds.js';

export const CHANNEL = {
  detectClients: 'scout:detect.clients',
  discordBuildOAuthUrl: 'scout:discord.buildOAuthUrl',
  discordStartOAuthInstall: 'scout:discord.startOAuthInstall',
  discordCancelOAuthInstall: 'scout:discord.cancelOAuthInstall',
  vaultStore: 'scout:vault.store',
  vaultHas: 'scout:vault.has',
  vaultDelete: 'scout:vault.delete',
  workerStart: 'scout:worker.start',
  workerStop: 'scout:worker.stop',
  workerStatus: 'scout:worker.status',
  workerEvents: 'scout:worker.events',
  appOpenExternal: 'scout:app.openExternal',
  appGetVersion: 'scout:app.getVersion',
  appQuit: 'scout:app.quit',
  diagnosticsExport: 'scout:diagnostics.export',
} as const satisfies Readonly<Record<string, string>>;

export type ChannelName = (typeof CHANNEL)[keyof typeof CHANNEL];

export type VaultKey = 'discord.bot-token' | 'discord.client-id';

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
  workerStart(): Promise<WorkerStartResponse>;
  workerStop(): Promise<WorkerStopResponse>;
  workerStatus(): Promise<WorkerStatusResponse>;
  onWorkerEvent(listener: (event: WorkerEvent) => void): () => void;
  appOpenExternal(req: AppOpenExternalRequest): Promise<void>;
  appGetVersion(): Promise<AppGetVersionResponse>;
  appQuit(): Promise<void>;
  diagnosticsExport(): Promise<DiagnosticsExportResponse>;
}
