import type { IpcMain } from 'electron';
import {
  CHANNEL,
  serializeError,
  type ProviderStatusDto,
  type ProvidersListResponse,
  type ProviderStatusRequest,
  type ProviderStartRequest,
  type ProviderStopRequest,
  type ProviderRestartRequest,
  type ProviderAutostartRequest,
} from '@scout/shared';
import type { WorkerHttpClient } from '../worker/http-client.js';
import type { Logger } from '../util/logger.js';

const START_TIMEOUT_MS = 35_000;

function throwSerialized(err: unknown): never {
  const serialized = serializeError(err);
  throw Object.assign(new Error(serialized.message), { code: serialized.code });
}

export function registerProvidersHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  client: WorkerHttpClient,
  logger: Logger,
): () => void {
  ipcMain.handle(CHANNEL.providersList, async (): Promise<ProvidersListResponse> => {
    try {
      const providers = await client.get<ProviderStatusDto[]>('/providers');
      return { providers };
    } catch (err: unknown) {
      logger.warn('providers: list failed', { err: String(err) });
      throwSerialized(err);
    }
  });

  ipcMain.handle(
    CHANNEL.providerStatus,
    async (_event, req: ProviderStatusRequest): Promise<ProviderStatusDto> => {
      try {
        return await client.get<ProviderStatusDto>(`/providers/${req.id}/status`);
      } catch (err: unknown) {
        logger.warn('providers: status failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.providerStart,
    async (_event, req: ProviderStartRequest): Promise<ProviderStatusDto> => {
      try {
        return await client.post<ProviderStatusDto>(`/providers/${req.id}/start`, undefined, {
          timeoutMs: START_TIMEOUT_MS,
        });
      } catch (err: unknown) {
        logger.warn('providers: start failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.providerStop,
    async (_event, req: ProviderStopRequest): Promise<ProviderStatusDto> => {
      try {
        return await client.post<ProviderStatusDto>(`/providers/${req.id}/stop`);
      } catch (err: unknown) {
        logger.warn('providers: stop failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.providerRestart,
    async (_event, req: ProviderRestartRequest): Promise<ProviderStatusDto> => {
      try {
        return await client.post<ProviderStatusDto>(`/providers/${req.id}/restart`);
      } catch (err: unknown) {
        logger.warn('providers: restart failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.providerSetAutostart,
    async (_event, req: ProviderAutostartRequest): Promise<void> => {
      try {
        await client.put<void>(`/providers/${req.id}/autostart`, { enabled: req.enabled });
      } catch (err: unknown) {
        logger.warn('providers: setAutostart failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.providersList);
    ipcMain.removeHandler(CHANNEL.providerStatus);
    ipcMain.removeHandler(CHANNEL.providerStart);
    ipcMain.removeHandler(CHANNEL.providerStop);
    ipcMain.removeHandler(CHANNEL.providerRestart);
    ipcMain.removeHandler(CHANNEL.providerSetAutostart);
    logger.info('providers: IPC handlers unregistered');
  };
}
