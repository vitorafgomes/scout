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
}

export const SCOUT_BRIDGE_NOT_AVAILABLE_CODE = BRIDGE_NOT_AVAILABLE_CODE;
