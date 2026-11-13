import type { IpcMain } from 'electron';
import {
  CHANNEL,
  encodeIpcErrorMessage,
  serializeError,
  type YouTubeSaveApiKeyRequest,
  type YouTubeStatus,
} from '@scout/shared';
import type { ControlPlaneClient } from '../control-plane/client.js';
import type { GoogleAuth } from '../oauth/google-auth.js';
import type { FeaturesHooks } from './features.js';
import { revokeFeatureWriteGrant, type PendingRevokes } from './write-grant-revoke.js';
import type { Logger } from '../util/logger.js';

/**
 * Revokes the youtube write grant on the control plane and then calls `onRevoked`, so the renderer reloads the
 * features; a failure is logged with its code, kept in `pending` to send again, and never thrown.
 */
export function revokeYouTubeWriteGrant(
  getControlPlane: () => Promise<Pick<ControlPlaneClient, 'setWriteGrant'>>,
  pending: PendingRevokes,
  logger: Logger,
  onRevoked?: () => void,
): Promise<void> {
  return revokeFeatureWriteGrant('youtube', getControlPlane, pending, logger, onRevoked);
}

export async function deleteYouTubeData(
  getControlPlane: () => Promise<Pick<ControlPlaneClient, 'deleteFeatureCredentials'>>,
  logger: Logger,
): Promise<void> {
  try {
    await (await getControlPlane()).deleteFeatureCredentials('youtube');
  } catch (err: unknown) {
    logger.warn('youtube: data not deleted', { code: serializeError(err).code });
  }
}

export function youTubeWriteGrantHooks(
  auth: Pick<GoogleAuth, 'ensureWriteScopes' | 'dropWriteScopes' | 'settled' | 'status'>,
  deleteData: () => Promise<void>,
): Pick<FeaturesHooks, 'prepareWriteGrant' | 'afterWriteRevoke'> {
  const deleteIfSignedOut = async (): Promise<void> => {
    const status = await auth.status().catch(() => null);
    if (status?.connected === false) await deleteData();
  };
  return {
    prepareWriteGrant: async (id) => {
      if (id === 'youtube') await auth.ensureWriteScopes();
    },
    afterWriteRevoke: async (id) => {
      if (id !== 'youtube') return;
      let dropped: boolean;
      try {
        dropped = await auth.dropWriteScopes();
      } catch (err: unknown) {
        await deleteIfSignedOut();
        throw err;
      }
      if (dropped) void auth.settled().then(deleteIfSignedOut);
    },
  };
}

export function registerYouTubeHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  auth: Pick<
    GoogleAuth,
    'signIn' | 'status' | 'cancelSignIn' | 'signOut' | 'saveApiKey' | 'clearApiKey'
  >,
  logger: Logger,
  getControlPlane: () => Promise<
    Pick<ControlPlaneClient, 'setWriteGrant' | 'deleteFeatureCredentials'>
  >,
  pending: PendingRevokes,
  onWriteGrantRevoked?: () => void,
): () => void {
  const revokeWriteGrant = (): Promise<void> =>
    revokeYouTubeWriteGrant(getControlPlane, pending, logger, onWriteGrantRevoked);
  const deleteData = (): Promise<void> => deleteYouTubeData(getControlPlane, logger);

  async function relay<T>(name: string, call: () => Promise<T>): Promise<T> {
    try {
      return await call();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      logger.warn(`youtube: ${name} failed`, { code: serialized.code });
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
  }

  ipcMain.handle(CHANNEL.youtubeSignIn, (): Promise<YouTubeStatus> =>
    relay('sign-in', () => auth.signIn()),
  );
  ipcMain.handle(CHANNEL.youtubeStatus, (): Promise<YouTubeStatus> =>
    relay('status', () => auth.status()),
  );
  ipcMain.handle(CHANNEL.youtubeCancelSignIn, (): void => auth.cancelSignIn());
  ipcMain.handle(CHANNEL.youtubeSignOut, async (): Promise<YouTubeStatus> => {
    await revokeWriteGrant();
    const status = await relay('sign-out', () => auth.signOut());
    await deleteData();
    return status;
  });
  ipcMain.handle(
    CHANNEL.youtubeSaveApiKey,
    (_event, req: YouTubeSaveApiKeyRequest): Promise<YouTubeStatus> =>
      relay('save key', () => auth.saveApiKey(typeof req?.apiKey === 'string' ? req.apiKey : '')),
  );
  ipcMain.handle(CHANNEL.youtubeClearApiKey, async (): Promise<YouTubeStatus> => {
    const before = await auth.status().catch(() => null);
    const only = before?.method !== 'oauth';
    if (only) await revokeWriteGrant();
    const status = await relay('clear key', () => auth.clearApiKey());
    if (only) await deleteData();
    return status;
  });

  return () => {
    ipcMain.removeHandler(CHANNEL.youtubeClearApiKey);
    ipcMain.removeHandler(CHANNEL.youtubeSaveApiKey);
    ipcMain.removeHandler(CHANNEL.youtubeSignOut);
    ipcMain.removeHandler(CHANNEL.youtubeCancelSignIn);
    ipcMain.removeHandler(CHANNEL.youtubeStatus);
    ipcMain.removeHandler(CHANNEL.youtubeSignIn);
  };
}
