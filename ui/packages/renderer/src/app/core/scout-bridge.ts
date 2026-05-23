import { Injectable } from '@angular/core';
import {
  ScoutError,
  type AppGetVersionResponse,
  type AppOpenExternalRequest,
  type DetectClientsResponse,
  type DiagnosticsExportResponse,
  type DiscordBuildOAuthUrlRequest,
  type DiscordBuildOAuthUrlResponse,
  type DiscordStartOAuthInstallRequest,
  type DiscordStartOAuthInstallResponse,
  type LlmListOllamaModelsRequest,
  type LlmListOllamaModelsResponse,
  type LlmPullOllamaModelRequest,
  type LlmPullOllamaModelResponse,
  type LlmPullOllamaProgressEvent,
  type LlmDeleteOllamaModelRequest,
  type LlmDeleteOllamaModelResponse,
  type LlmListLmStudioModelsRequest,
  type LlmListLmStudioModelsResponse,
  type WhisperListInstalledResponse,
  type WhisperDownloadModelRequest,
  type WhisperDownloadModelResponse,
  type WhisperDownloadProgressEvent,
  type WhisperDeleteModelRequest,
  type WhisperDeleteModelResponse,
  type ScoutBridge,
  type VaultDeleteRequest,
  type VaultDeleteResponse,
  type VaultHasRequest,
  type VaultHasResponse,
  type VaultIsAvailableResponse,
  type VaultStoreRequest,
  type WorkerEvent,
  type WorkerStartResponse,
  type WorkerStatusResponse,
  type WorkerStopResponse,
  type ProvidersListResponse,
  type ProviderStatusDto,
  type ProviderStatusRequest,
  type ProviderStartRequest,
  type ProviderStopRequest,
  type ProviderRestartRequest,
  type ProviderAutostartRequest,
} from '@scout/shared';

const BRIDGE_NOT_AVAILABLE_CODE = 'bridge/not-available';
const BRIDGE_NOT_AVAILABLE_MESSAGE =
  'window.scout is not available. The renderer is running outside of Electron or the preload bridge failed to load.';

type WindowWithBridge = Window & { readonly scout?: ScoutBridge };

function resolveBridge(): ScoutBridge {
  const globalWindow = (globalThis as { window?: WindowWithBridge }).window;
  const fromWindow = globalWindow?.scout;
  if (fromWindow !== undefined) {
    return fromWindow;
  }
  return createFakeBridge();
}

function rejectNotAvailable<T>(): Promise<T> {
  return Promise.reject(new ScoutError(BRIDGE_NOT_AVAILABLE_CODE, BRIDGE_NOT_AVAILABLE_MESSAGE));
}

function createFakeBridge(): ScoutBridge {
  return {
    detectClients: (): Promise<DetectClientsResponse> => rejectNotAvailable(),
    discordBuildOAuthUrl: (
      _req: DiscordBuildOAuthUrlRequest,
    ): Promise<DiscordBuildOAuthUrlResponse> => rejectNotAvailable(),
    discordStartOAuthInstall: (
      _req: DiscordStartOAuthInstallRequest,
    ): Promise<DiscordStartOAuthInstallResponse> => rejectNotAvailable(),
    discordCancelOAuthInstall: (): Promise<void> => rejectNotAvailable(),
    vaultStore: (_req: VaultStoreRequest): Promise<void> => rejectNotAvailable(),
    vaultHas: (_req: VaultHasRequest): Promise<VaultHasResponse> => rejectNotAvailable(),
    vaultDelete: (_req: VaultDeleteRequest): Promise<VaultDeleteResponse> => rejectNotAvailable(),
    vaultIsAvailable: (): Promise<VaultIsAvailableResponse> => rejectNotAvailable(),
    workerStart: (): Promise<WorkerStartResponse> => rejectNotAvailable(),
    workerStop: (): Promise<WorkerStopResponse> => rejectNotAvailable(),
    workerStatus: (): Promise<WorkerStatusResponse> => rejectNotAvailable(),
    onWorkerEvent: (_listener: (event: WorkerEvent) => void): (() => void) => {
      return (): void => {
        // no-op unsubscribe
      };
    },
    appOpenExternal: (_req: AppOpenExternalRequest): Promise<void> => rejectNotAvailable(),
    appGetVersion: (): Promise<AppGetVersionResponse> => rejectNotAvailable(),
    appQuit: (): Promise<void> => rejectNotAvailable(),
    diagnosticsExport: (): Promise<DiagnosticsExportResponse> => rejectNotAvailable(),
    llmListOllamaModels: (_req: LlmListOllamaModelsRequest): Promise<LlmListOllamaModelsResponse> =>
      rejectNotAvailable(),
    llmListLmStudioModels: (
      _req: LlmListLmStudioModelsRequest,
    ): Promise<LlmListLmStudioModelsResponse> => rejectNotAvailable(),
    llmPullOllamaModel: (
      _req: LlmPullOllamaModelRequest,
    ): Promise<LlmPullOllamaModelResponse> => rejectNotAvailable(),
    onLlmPullOllamaProgress: (_listener: (event: LlmPullOllamaProgressEvent) => void): (() => void) => {
      return (): void => {
        // no-op
      };
    },
    llmDeleteOllamaModel: (
      _req: LlmDeleteOllamaModelRequest,
    ): Promise<LlmDeleteOllamaModelResponse> => rejectNotAvailable(),
    whisperListInstalled: (): Promise<WhisperListInstalledResponse> => rejectNotAvailable(),
    whisperDownloadModel: (
      _req: WhisperDownloadModelRequest,
    ): Promise<WhisperDownloadModelResponse> => rejectNotAvailable(),
    onWhisperDownloadProgress: (
      _listener: (event: WhisperDownloadProgressEvent) => void,
    ): (() => void) => {
      return (): void => {
        // no-op
      };
    },
    whisperDeleteModel: (
      _req: WhisperDeleteModelRequest,
    ): Promise<WhisperDeleteModelResponse> => rejectNotAvailable(),
    providersList: (): Promise<ProvidersListResponse> => rejectNotAvailable(),
    providerStatus: (_req: ProviderStatusRequest): Promise<ProviderStatusDto> =>
      rejectNotAvailable(),
    providerStart: (_req: ProviderStartRequest): Promise<ProviderStatusDto> =>
      rejectNotAvailable(),
    providerStop: (_req: ProviderStopRequest): Promise<ProviderStatusDto> =>
      rejectNotAvailable(),
    providerRestart: (_req: ProviderRestartRequest): Promise<ProviderStatusDto> =>
      rejectNotAvailable(),
    providerSetAutostart: (_req: ProviderAutostartRequest): Promise<void> =>
      rejectNotAvailable(),
  };
}

@Injectable({ providedIn: 'root' })
export class ScoutBridgeService {
  private readonly bridge: ScoutBridge = resolveBridge();

  async detectClients(): Promise<DetectClientsResponse> {
    return this.bridge.detectClients();
  }

  async vaultIsAvailable(): Promise<VaultIsAvailableResponse> {
    return this.bridge.vaultIsAvailable();
  }

  async vaultStore(req: VaultStoreRequest): Promise<void> {
    return this.bridge.vaultStore(req);
  }

  async vaultHas(req: VaultHasRequest): Promise<VaultHasResponse> {
    return this.bridge.vaultHas(req);
  }

  async vaultDelete(req: VaultDeleteRequest): Promise<VaultDeleteResponse> {
    return this.bridge.vaultDelete(req);
  }

  async appOpenExternal(req: AppOpenExternalRequest): Promise<void> {
    return this.bridge.appOpenExternal(req);
  }

  async discordStartOAuthInstall(
    req: DiscordStartOAuthInstallRequest,
  ): Promise<DiscordStartOAuthInstallResponse> {
    return this.bridge.discordStartOAuthInstall(req);
  }

  async discordCancelOAuthInstall(): Promise<void> {
    return this.bridge.discordCancelOAuthInstall();
  }

  async workerStart(): Promise<WorkerStartResponse> {
    return this.bridge.workerStart();
  }

  async workerStop(): Promise<WorkerStopResponse> {
    return this.bridge.workerStop();
  }

  async workerStatus(): Promise<WorkerStatusResponse> {
    return this.bridge.workerStatus();
  }

  onWorkerEvent(listener: (event: WorkerEvent) => void): () => void {
    return this.bridge.onWorkerEvent(listener);
  }

  async llmListOllamaModels(req: LlmListOllamaModelsRequest): Promise<LlmListOllamaModelsResponse> {
    return this.bridge.llmListOllamaModels(req);
  }

  async llmListLmStudioModels(
    req: LlmListLmStudioModelsRequest,
  ): Promise<LlmListLmStudioModelsResponse> {
    return this.bridge.llmListLmStudioModels(req);
  }

  async llmPullOllamaModel(
    req: LlmPullOllamaModelRequest,
  ): Promise<LlmPullOllamaModelResponse> {
    return this.bridge.llmPullOllamaModel(req);
  }

  onLlmPullOllamaProgress(listener: (event: LlmPullOllamaProgressEvent) => void): () => void {
    return this.bridge.onLlmPullOllamaProgress(listener);
  }

  async llmDeleteOllamaModel(
    req: LlmDeleteOllamaModelRequest,
  ): Promise<LlmDeleteOllamaModelResponse> {
    return this.bridge.llmDeleteOllamaModel(req);
  }

  async whisperListInstalled(): Promise<WhisperListInstalledResponse> {
    return this.bridge.whisperListInstalled();
  }

  async whisperDownloadModel(
    req: WhisperDownloadModelRequest,
  ): Promise<WhisperDownloadModelResponse> {
    return this.bridge.whisperDownloadModel(req);
  }

  onWhisperDownloadProgress(
    listener: (event: WhisperDownloadProgressEvent) => void,
  ): () => void {
    return this.bridge.onWhisperDownloadProgress(listener);
  }

  async whisperDeleteModel(
    req: WhisperDeleteModelRequest,
  ): Promise<WhisperDeleteModelResponse> {
    return this.bridge.whisperDeleteModel(req);
  }

  async appGetVersion(): Promise<AppGetVersionResponse> {
    return this.bridge.appGetVersion();
  }

  async providerStatus(req: ProviderStatusRequest): Promise<ProviderStatusDto> {
    return this.bridge.providerStatus(req);
  }

  async providerStart(req: ProviderStartRequest): Promise<ProviderStatusDto> {
    return this.bridge.providerStart(req);
  }

  async providerStop(req: ProviderStopRequest): Promise<ProviderStatusDto> {
    return this.bridge.providerStop(req);
  }

  async providerRestart(req: ProviderRestartRequest): Promise<ProviderStatusDto> {
    return this.bridge.providerRestart(req);
  }

  async providerSetAutostart(req: ProviderAutostartRequest): Promise<void> {
    return this.bridge.providerSetAutostart(req);
  }
}

export const SCOUT_BRIDGE_NOT_AVAILABLE_CODE = BRIDGE_NOT_AVAILABLE_CODE;
