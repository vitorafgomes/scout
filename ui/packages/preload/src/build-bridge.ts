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
  };
}
