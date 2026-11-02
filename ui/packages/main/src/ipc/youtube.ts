import type { IpcMain } from 'electron';
import {
  CHANNEL,
  encodeIpcErrorMessage,
  serializeError,
  type YouTubeSaveApiKeyRequest,
  type YouTubeStatus,
} from '@scout/shared';
import type { GoogleAuth } from '../oauth/google-auth.js';
import type { Logger } from '../util/logger.js';

export function registerYouTubeHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  auth: Pick<
    GoogleAuth,
    'signIn' | 'status' | 'cancelSignIn' | 'signOut' | 'saveApiKey' | 'clearApiKey'
  >,
  logger: Logger,
): () => void {
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
  ipcMain.handle(CHANNEL.youtubeSignOut, (): Promise<YouTubeStatus> =>
    relay('sign-out', () => auth.signOut()),
  );
  ipcMain.handle(
    CHANNEL.youtubeSaveApiKey,
    (_event, req: YouTubeSaveApiKeyRequest): Promise<YouTubeStatus> =>
      relay('save key', () => auth.saveApiKey(typeof req?.apiKey === 'string' ? req.apiKey : '')),
  );
  ipcMain.handle(CHANNEL.youtubeClearApiKey, (): Promise<YouTubeStatus> =>
    relay('clear key', () => auth.clearApiKey()),
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.youtubeClearApiKey);
    ipcMain.removeHandler(CHANNEL.youtubeSaveApiKey);
    ipcMain.removeHandler(CHANNEL.youtubeSignOut);
    ipcMain.removeHandler(CHANNEL.youtubeCancelSignIn);
    ipcMain.removeHandler(CHANNEL.youtubeStatus);
    ipcMain.removeHandler(CHANNEL.youtubeSignIn);
  };
}
