import {
  CHANNEL,
  type LocalTurn,
  type ScoutBridge,
  type UpdateNotice,
  type UpdateProgress,
  type WorkerEvent,
} from '@scout/shared';

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

    discordCheckToken(req) {
      return ipc.invoke(CHANNEL.discordCheckToken, req) as ReturnType<
        ScoutBridge['discordCheckToken']
      >;
    },

    discordEnableIntent() {
      return ipc.invoke(CHANNEL.discordEnableIntent) as ReturnType<
        ScoutBridge['discordEnableIntent']
      >;
    },

    discordInviteUrl() {
      return ipc.invoke(CHANNEL.discordInviteUrl) as ReturnType<ScoutBridge['discordInviteUrl']>;
    },

    discordGuilds() {
      return ipc.invoke(CHANNEL.discordGuilds) as ReturnType<ScoutBridge['discordGuilds']>;
    },

    chatgptSignIn() {
      return ipc.invoke(CHANNEL.chatgptSignIn) as ReturnType<ScoutBridge['chatgptSignIn']>;
    },

    chatgptCancelSignIn() {
      return ipc.invoke(CHANNEL.chatgptCancelSignIn) as ReturnType<
        ScoutBridge['chatgptCancelSignIn']
      >;
    },

    chatgptListModels() {
      return ipc.invoke(CHANNEL.chatgptListModels) as ReturnType<ScoutBridge['chatgptListModels']>;
    },

    chatgptSignOut() {
      return ipc.invoke(CHANNEL.chatgptSignOut) as ReturnType<ScoutBridge['chatgptSignOut']>;
    },

    chatgptStatus() {
      return ipc.invoke(CHANNEL.chatgptStatus) as ReturnType<ScoutBridge['chatgptStatus']>;
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

    vaultGetPublic(req) {
      return ipc.invoke(CHANNEL.vaultGetPublic, req) as ReturnType<ScoutBridge['vaultGetPublic']>;
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

    validateProviderKey(req) {
      return ipc.invoke(CHANNEL.providersValidateKey, req) as ReturnType<
        ScoutBridge['validateProviderKey']
      >;
    },

    llmListModels(req) {
      return ipc.invoke(CHANNEL.llmListModels, req) as ReturnType<ScoutBridge['llmListModels']>;
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

    githubSignIn() {
      return ipc.invoke(CHANNEL.githubSignIn) as ReturnType<ScoutBridge['githubSignIn']>;
    },

    githubStatus() {
      return ipc.invoke(CHANNEL.githubStatus) as ReturnType<ScoutBridge['githubStatus']>;
    },

    githubCancel() {
      return ipc.invoke(CHANNEL.githubCancel) as ReturnType<ScoutBridge['githubCancel']>;
    },

    githubSignOut() {
      return ipc.invoke(CHANNEL.githubSignOut) as ReturnType<ScoutBridge['githubSignOut']>;
    },

    githubSaveToken(req) {
      return ipc.invoke(CHANNEL.githubSaveToken, req) as ReturnType<ScoutBridge['githubSaveToken']>;
    },

    youtubeSignIn() {
      return ipc.invoke(CHANNEL.youtubeSignIn) as ReturnType<ScoutBridge['youtubeSignIn']>;
    },

    youtubeStatus() {
      return ipc.invoke(CHANNEL.youtubeStatus) as ReturnType<ScoutBridge['youtubeStatus']>;
    },

    youtubeCancelSignIn() {
      return ipc.invoke(CHANNEL.youtubeCancelSignIn) as ReturnType<
        ScoutBridge['youtubeCancelSignIn']
      >;
    },

    youtubeSignOut() {
      return ipc.invoke(CHANNEL.youtubeSignOut) as ReturnType<ScoutBridge['youtubeSignOut']>;
    },

    youtubeSaveApiKey(req) {
      return ipc.invoke(CHANNEL.youtubeSaveApiKey, req) as ReturnType<
        ScoutBridge['youtubeSaveApiKey']
      >;
    },

    youtubeClearApiKey() {
      return ipc.invoke(CHANNEL.youtubeClearApiKey) as ReturnType<
        ScoutBridge['youtubeClearApiKey']
      >;
    },

    kubernetesDetect() {
      return ipc.invoke(CHANNEL.kubernetesDetect) as ReturnType<ScoutBridge['kubernetesDetect']>;
    },

    kubernetesUse(req) {
      return ipc.invoke(CHANNEL.kubernetesUse, req) as ReturnType<ScoutBridge['kubernetesUse']>;
    },

    kubernetesStatus() {
      return ipc.invoke(CHANNEL.kubernetesStatus) as ReturnType<ScoutBridge['kubernetesStatus']>;
    },

    kubernetesDisconnect() {
      return ipc.invoke(CHANNEL.kubernetesDisconnect) as ReturnType<
        ScoutBridge['kubernetesDisconnect']
      >;
    },

    accessGet() {
      return ipc.invoke(CHANNEL.accessGet) as ReturnType<ScoutBridge['accessGet']>;
    },

    accessSet(req) {
      return ipc.invoke(CHANNEL.accessSet, req) as ReturnType<ScoutBridge['accessSet']>;
    },

    accessStartPairing() {
      return ipc.invoke(CHANNEL.accessStartPairing) as ReturnType<
        ScoutBridge['accessStartPairing']
      >;
    },

    slackManifestUrl() {
      return ipc.invoke(CHANNEL.slackManifestUrl) as ReturnType<ScoutBridge['slackManifestUrl']>;
    },

    slackSave(req) {
      return ipc.invoke(CHANNEL.slackSave, req) as ReturnType<ScoutBridge['slackSave']>;
    },

    slackStatus() {
      return ipc.invoke(CHANNEL.slackStatus) as ReturnType<ScoutBridge['slackStatus']>;
    },

    slackDisconnect() {
      return ipc.invoke(CHANNEL.slackDisconnect) as ReturnType<ScoutBridge['slackDisconnect']>;
    },

    desktopGetSettings() {
      return ipc.invoke(CHANNEL.desktopGetSettings) as ReturnType<
        ScoutBridge['desktopGetSettings']
      >;
    },

    desktopSetKeepRunning(req) {
      return ipc.invoke(CHANNEL.desktopSetKeepRunning, req) as ReturnType<
        ScoutBridge['desktopSetKeepRunning']
      >;
    },

    desktopSetStartAtLogin(req) {
      return ipc.invoke(CHANNEL.desktopSetStartAtLogin, req) as ReturnType<
        ScoutBridge['desktopSetStartAtLogin']
      >;
    },

    localAsk(req) {
      return ipc.invoke(CHANNEL.localAsk, req) as ReturnType<ScoutBridge['localAsk']>;
    },

    localCancel(req) {
      return ipc.invoke(CHANNEL.localCancel, req) as ReturnType<ScoutBridge['localCancel']>;
    },

    localGetTurns() {
      return ipc.invoke(CHANNEL.localGetTurns) as ReturnType<ScoutBridge['localGetTurns']>;
    },

    localClearTurns() {
      return ipc.invoke(CHANNEL.localClearTurns) as ReturnType<ScoutBridge['localClearTurns']>;
    },

    onLocalTurns(listener) {
      const handler = (_event: unknown, ...args: unknown[]) =>
        listener(args[0] as readonly LocalTurn[]);
      ipc.on(CHANNEL.localTurns, handler);
      return () => {
        ipc.removeListener(CHANNEL.localTurns, handler);
      };
    },

    localHide() {
      return ipc.invoke(CHANNEL.localHide) as ReturnType<ScoutBridge['localHide']>;
    },

    localGetShortcut() {
      return ipc.invoke(CHANNEL.localGetShortcut) as ReturnType<ScoutBridge['localGetShortcut']>;
    },

    localSetShortcut(req) {
      return ipc.invoke(CHANNEL.localSetShortcut, req) as ReturnType<
        ScoutBridge['localSetShortcut']
      >;
    },

    localOpen() {
      return ipc.invoke(CHANNEL.localOpen) as ReturnType<ScoutBridge['localOpen']>;
    },

    localGetBubble() {
      return ipc.invoke(CHANNEL.localGetBubble) as ReturnType<ScoutBridge['localGetBubble']>;
    },

    localSetBubble(req) {
      return ipc.invoke(CHANNEL.localSetBubble, req) as ReturnType<ScoutBridge['localSetBubble']>;
    },

    updateGetState() {
      return ipc.invoke(CHANNEL.updateGetState) as ReturnType<ScoutBridge['updateGetState']>;
    },

    updateSetCheckEnabled(req) {
      return ipc.invoke(CHANNEL.updateSetCheckEnabled, req) as ReturnType<
        ScoutBridge['updateSetCheckEnabled']
      >;
    },

    updateCheckNow() {
      return ipc.invoke(CHANNEL.updateCheckNow) as ReturnType<ScoutBridge['updateCheckNow']>;
    },

    updateSkipVersion(req) {
      return ipc.invoke(CHANNEL.updateSkipVersion, req) as ReturnType<
        ScoutBridge['updateSkipVersion']
      >;
    },

    updateRemindLater() {
      return ipc.invoke(CHANNEL.updateRemindLater) as ReturnType<ScoutBridge['updateRemindLater']>;
    },

    onUpdateNotice(listener) {
      const handler = (_event: unknown, ...args: unknown[]) =>
        listener(args[0] as UpdateNotice | null);
      ipc.on(CHANNEL.updateNotice, handler);
      return () => {
        ipc.removeListener(CHANNEL.updateNotice, handler);
      };
    },

    updateInstall() {
      return ipc.invoke(CHANNEL.updateInstall) as ReturnType<ScoutBridge['updateInstall']>;
    },

    onUpdateProgress(listener) {
      const handler = (_event: unknown, ...args: unknown[]) => listener(args[0] as UpdateProgress);
      ipc.on(CHANNEL.updateProgress, handler);
      return () => {
        ipc.removeListener(CHANNEL.updateProgress, handler);
      };
    },
  };
}
