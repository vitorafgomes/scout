import { CHANNEL, type ScoutBridge, type WorkerEvent } from '@scout/shared';

export interface IpcSubset {
  invoke(channel: string, ...args: unknown[]): Promise<unknown>;
  on(channel: string, listener: (event: unknown, ...args: unknown[]) => void): unknown;
  removeListener(channel: string, listener: (event: unknown, ...args: unknown[]) => void): unknown;
}

export function buildBridge(ipc: IpcSubset): ScoutBridge {
  return {
    detectClients() {
      return ipc.invoke(CHANNEL.detectClients) as ReturnType<ScoutBridge['detectClients']>;
    },

    discordBuildOAuthUrl(req) {
      return ipc.invoke(CHANNEL.discordBuildOAuthUrl, req) as ReturnType<
        ScoutBridge['discordBuildOAuthUrl']
      >;
    },

    discordStartOAuthInstall(req) {
      return ipc.invoke(CHANNEL.discordStartOAuthInstall, req) as ReturnType<
        ScoutBridge['discordStartOAuthInstall']
      >;
    },

    discordCancelOAuthInstall() {
      return ipc.invoke(CHANNEL.discordCancelOAuthInstall) as ReturnType<
        ScoutBridge['discordCancelOAuthInstall']
      >;
    },

    vaultStore(req) {
      return ipc.invoke(CHANNEL.vaultStore, req) as ReturnType<ScoutBridge['vaultStore']>;
    },

    vaultHas(req) {
      return ipc.invoke(CHANNEL.vaultHas, req) as ReturnType<ScoutBridge['vaultHas']>;
    },

    vaultDelete(req) {
      return ipc.invoke(CHANNEL.vaultDelete, req) as ReturnType<ScoutBridge['vaultDelete']>;
    },

    vaultIsAvailable() {
      return ipc.invoke(CHANNEL.vaultIsAvailable) as ReturnType<ScoutBridge['vaultIsAvailable']>;
    },

    workerStart() {
      return ipc.invoke(CHANNEL.workerStart) as ReturnType<ScoutBridge['workerStart']>;
    },

    workerStop() {
      return ipc.invoke(CHANNEL.workerStop) as ReturnType<ScoutBridge['workerStop']>;
    },

    workerStatus() {
      return ipc.invoke(CHANNEL.workerStatus) as ReturnType<ScoutBridge['workerStatus']>;
    },

    onWorkerEvent(listener) {
      const handler = (_event: unknown, ...args: unknown[]) => listener(args[0] as WorkerEvent);
      ipc.on(CHANNEL.workerEvents, handler);
      return () => {
        ipc.removeListener(CHANNEL.workerEvents, handler);
      };
    },

    appOpenExternal(req) {
      return ipc.invoke(CHANNEL.appOpenExternal, req) as ReturnType<ScoutBridge['appOpenExternal']>;
    },

    appGetVersion() {
      return ipc.invoke(CHANNEL.appGetVersion) as ReturnType<ScoutBridge['appGetVersion']>;
    },

    appQuit() {
      return ipc.invoke(CHANNEL.appQuit) as ReturnType<ScoutBridge['appQuit']>;
    },

    diagnosticsExport() {
      return ipc.invoke(CHANNEL.diagnosticsExport) as ReturnType<ScoutBridge['diagnosticsExport']>;
    },

    llmListOllamaModels(req) {
      return ipc.invoke(CHANNEL.llmListOllamaModels, req) as ReturnType<
        ScoutBridge['llmListOllamaModels']
      >;
    },

    llmListLmStudioModels(req) {
      return ipc.invoke(CHANNEL.llmListLmStudioModels, req) as ReturnType<
        ScoutBridge['llmListLmStudioModels']
      >;
    },

    llmPullOllamaModel(req) {
      return ipc.invoke(CHANNEL.llmPullOllamaModel, req) as ReturnType<
        ScoutBridge['llmPullOllamaModel']
      >;
    },

    onLlmPullOllamaProgress(listener) {
      const handler = (_event: unknown, payload: unknown): void => {
        listener(payload as Parameters<typeof listener>[0]);
      };
      ipc.on(CHANNEL.llmPullOllamaProgress, handler);
      return (): void => {
        ipc.removeListener(CHANNEL.llmPullOllamaProgress, handler);
      };
    },

    llmDeleteOllamaModel(req) {
      return ipc.invoke(CHANNEL.llmDeleteOllamaModel, req) as ReturnType<
        ScoutBridge['llmDeleteOllamaModel']
      >;
    },

    whisperListInstalled() {
      return ipc.invoke(CHANNEL.whisperListInstalled) as ReturnType<
        ScoutBridge['whisperListInstalled']
      >;
    },

    whisperDownloadModel(req) {
      return ipc.invoke(CHANNEL.whisperDownloadModel, req) as ReturnType<
        ScoutBridge['whisperDownloadModel']
      >;
    },

    onWhisperDownloadProgress(listener) {
      const handler = (_event: unknown, payload: unknown): void => {
        listener(payload as Parameters<typeof listener>[0]);
      };
      ipc.on(CHANNEL.whisperDownloadProgress, handler);
      return (): void => {
        ipc.removeListener(CHANNEL.whisperDownloadProgress, handler);
      };
    },

    whisperDeleteModel(req) {
      return ipc.invoke(CHANNEL.whisperDeleteModel, req) as ReturnType<
        ScoutBridge['whisperDeleteModel']
      >;
    },

    providersList() {
      return ipc.invoke(CHANNEL.providersList) as ReturnType<ScoutBridge['providersList']>;
    },

    providerStatus(req) {
      return ipc.invoke(CHANNEL.providerStatus, req) as ReturnType<ScoutBridge['providerStatus']>;
    },

    providerStart(req) {
      return ipc.invoke(CHANNEL.providerStart, req) as ReturnType<ScoutBridge['providerStart']>;
    },

    providerStop(req) {
      return ipc.invoke(CHANNEL.providerStop, req) as ReturnType<ScoutBridge['providerStop']>;
    },

    providerRestart(req) {
      return ipc.invoke(CHANNEL.providerRestart, req) as ReturnType<ScoutBridge['providerRestart']>;
    },

    providerSetAutostart(req) {
      return ipc.invoke(CHANNEL.providerSetAutostart, req) as ReturnType<
        ScoutBridge['providerSetAutostart']
      >;
    },

    featuresList() {
      return ipc.invoke(CHANNEL.featuresList) as ReturnType<ScoutBridge['featuresList']>;
    },

    featureSetCredentials(req) {
      return ipc.invoke(CHANNEL.featureSetCredentials, req) as ReturnType<
        ScoutBridge['featureSetCredentials']
      >;
    },

    featureSetup(req) {
      return ipc.invoke(CHANNEL.featureSetup, req) as ReturnType<ScoutBridge['featureSetup']>;
    },

    featureDeleteCredentials(req) {
      return ipc.invoke(CHANNEL.featureDeleteCredentials, req) as ReturnType<
        ScoutBridge['featureDeleteCredentials']
      >;
    },

    featureEnable(req) {
      return ipc.invoke(CHANNEL.featureEnable, req) as ReturnType<ScoutBridge['featureEnable']>;
    },

    featureDisable(req) {
      return ipc.invoke(CHANNEL.featureDisable, req) as ReturnType<ScoutBridge['featureDisable']>;
    },
  };
}
