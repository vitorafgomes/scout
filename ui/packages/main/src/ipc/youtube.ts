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
import type { Logger } from '../util/logger.js';

/**
 * Revokes the youtube write grant on the control plane and then calls `onRevoked`, so the renderer reloads the
 * features; a failure is logged with its code and never thrown.
 */
export async function revokeYouTubeWriteGrant(
  getControlPlane: () => Promise<Pick<ControlPlaneClient, 'setWriteGrant'>>,
  logger: Logger,
  onRevoked?: () => void,
): Promise<void> {
  try {
    await (await getControlPlane()).setWriteGrant('youtube', false, false);
  } catch (err: unknown) {
    logger.warn('youtube: write grant not revoked', { code: serializeError(err).code });
    return;
  }
  onRevoked?.();
}

export function registerYouTubeHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  auth: Pick<
    GoogleAuth,
    'signIn' | 'status' | 'cancelSignIn' | 'signOut' | 'saveApiKey' | 'clearApiKey'
  >,
  logger: Logger,
  getControlPlane: () => Promise<Pick<ControlPlaneClient, 'setWriteGrant'>>,
  onWriteGrantRevoked?: () => void,
): () => void {
  const revokeWriteGrant = (): Promise<void> =>
    revokeYouTubeWriteGrant(getControlPlane, logger, onWriteGrantRevoked);

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
    return relay('sign-out', () => auth.signOut());
  });
  ipcMain.handle(
    CHANNEL.youtubeSaveApiKey,
    (_event, req: YouTubeSaveApiKeyRequest): Promise<YouTubeStatus> =>
      relay('save key', () => auth.saveApiKey(typeof req?.apiKey === 'string' ? req.apiKey : '')),
  );
  ipcMain.handle(CHANNEL.youtubeClearApiKey, async (): Promise<YouTubeStatus> => {
    const before = await auth.status().catch(() => null);
    if (before?.method !== 'oauth') await revokeWriteGrant();
    return relay('clear key', () => auth.clearApiKey());
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
