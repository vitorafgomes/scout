import type { IpcMain } from 'electron';
import {
  CHANNEL,
  FEATURE_IDS,
  ScoutError,
  serializeError,
  type FeatureDto,
  type FeatureId,
  type FeatureIdRequest,
  type FeatureSetCredentialsRequest,
  type FeatureSetupRequest,
  type FeaturesListResponse,
} from '@scout/shared';
import type { WorkerHttpClient } from '../worker/http-client.js';
import type { Logger } from '../util/logger.js';

function throwSerialized(err: unknown): never {
  const serialized = serializeError(err);
  throw Object.assign(new Error(serialized.message), { code: serialized.code });
}

function assertFeatureId(id: string): asserts id is FeatureId {
  if (!FEATURE_IDS.includes(id as FeatureId)) {
    throw new ScoutError('features/invalid-id', `unknown feature "${id}"`);
  }
}

export function registerFeaturesHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  client: WorkerHttpClient,
  logger: Logger,
): () => void {
  ipcMain.handle(CHANNEL.featuresList, async (): Promise<FeaturesListResponse> => {
    try {
      const features = await client.get<FeatureDto[]>('/features');
      return { features };
    } catch (err: unknown) {
      logger.warn('features: list failed', { err: String(err) });
      throwSerialized(err);
    }
  });

  ipcMain.handle(
    CHANNEL.featureSetCredentials,
    async (_event, req: FeatureSetCredentialsRequest): Promise<FeatureDto> => {
      try {
        assertFeatureId(req.id);
        return await client.post<FeatureDto>(`/features/${req.id}/credentials`, req.body);
      } catch (err: unknown) {
        logger.warn('features: setCredentials failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.featureSetup,
    async (_event, req: FeatureSetupRequest): Promise<FeatureDto> => {
      try {
        assertFeatureId(req.id);
        return await client.post<FeatureDto>(`/features/${req.id}/setup`, {
          credentials: req.credentials,
          settings: req.settings,
        });
      } catch (err: unknown) {
        logger.warn('features: setup failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.featureDeleteCredentials,
    async (_event, req: FeatureIdRequest): Promise<FeatureDto> => {
      try {
        assertFeatureId(req.id);
        return await client.delete<FeatureDto>(`/features/${req.id}/credentials`);
      } catch (err: unknown) {
        logger.warn('features: deleteCredentials failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.featureEnable,
    async (_event, req: FeatureIdRequest): Promise<FeatureDto> => {
      try {
        assertFeatureId(req.id);
        return await client.post<FeatureDto>(`/features/${req.id}/enable`);
      } catch (err: unknown) {
        logger.warn('features: enable failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.featureDisable,
    async (_event, req: FeatureIdRequest): Promise<FeatureDto> => {
      try {
        assertFeatureId(req.id);
        return await client.post<FeatureDto>(`/features/${req.id}/disable`);
      } catch (err: unknown) {
        logger.warn('features: disable failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.featuresList);
    ipcMain.removeHandler(CHANNEL.featureSetCredentials);
    ipcMain.removeHandler(CHANNEL.featureSetup);
    ipcMain.removeHandler(CHANNEL.featureDeleteCredentials);
    ipcMain.removeHandler(CHANNEL.featureEnable);
    ipcMain.removeHandler(CHANNEL.featureDisable);
    logger.info('features: IPC handlers unregistered');
  };
}
