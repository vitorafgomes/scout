import type { IpcMain } from 'electron';
import { CHANNEL, serializeError } from '@scout/shared';
import type {
  ChatGptListModelsResponse,
  ChatGptSignOutResponse,
  ChatGptStatus,
} from '@scout/shared';
import type { ChatGptAuth } from '../oauth/chatgpt-auth.js';
import type { Logger } from '../util/logger.js';

export function registerChatGptHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  auth: Pick<ChatGptAuth, 'signIn' | 'signOut' | 'status' | 'cancelSignIn' | 'listModels'>,
  logger: Logger,
): () => void {
  async function relay<T>(name: string, call: () => Promise<T>): Promise<T> {
    try {
      return await call();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      logger.warn(`chatgpt: ${name} failed`, { code: serialized.code });
      throw Object.assign(new Error(serialized.message), { code: serialized.code });
    }
  }

  ipcMain.handle(CHANNEL.chatgptSignIn, (): Promise<ChatGptStatus> =>
    relay('sign-in', () => auth.signIn()),
  );
  ipcMain.handle(CHANNEL.chatgptSignOut, (): Promise<ChatGptSignOutResponse> =>
    relay('sign-out', () => auth.signOut()),
  );
  ipcMain.handle(CHANNEL.chatgptListModels, async (): Promise<ChatGptListModelsResponse> => ({
    models: await relay('model list', () => auth.listModels()),
  }));
  ipcMain.handle(CHANNEL.chatgptCancelSignIn, (): void => auth.cancelSignIn());
  ipcMain.handle(CHANNEL.chatgptStatus, (): Promise<ChatGptStatus> =>
    relay('status', () => auth.status()),
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.chatgptStatus);
    ipcMain.removeHandler(CHANNEL.chatgptCancelSignIn);
    ipcMain.removeHandler(CHANNEL.chatgptListModels);
    ipcMain.removeHandler(CHANNEL.chatgptSignOut);
    ipcMain.removeHandler(CHANNEL.chatgptSignIn);
  };
}
