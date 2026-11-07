import type { IpcMain } from 'electron';
import {
  CHANNEL,
  encodeIpcErrorMessage,
  serializeError,
  type GitHubPendingSignIn,
  type GitHubSaveTokenRequest,
  type GitHubBotCreateRequest,
  type GitHubBotSetWritesRequest,
  type GitHubBotStatus,
  type GitHubStatus,
} from '@scout/shared';
import type { GitHubAuth } from '../oauth/github-auth.js';
import type { GitHubBot } from '../oauth/github-bot.js';
import type { Supervisor } from '../worker/supervisor.js';
import type { Logger } from '../util/logger.js';

/** Restarts the worker when it is running, so it picks up a connection change. */
export async function restartWorkerIfRunning(
  worker: Pick<Supervisor, 'status' | 'stop' | 'start'>,
  logger: Logger,
): Promise<void> {
  const { state } = worker.status;
  if (state !== 'running' && state !== 'starting') return;
  try {
    logger.info('restarting the worker to apply a connection change');
    await worker.stop();
    await worker.start();
  } catch (err: unknown) {
    logger.warn('worker restart failed', { code: serializeError(err).code });
  }
}

/** The Scout bot's channels; the renderer gets its status only, never the App's private key or a token. */
export function registerGitHubBotHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  bot: Pick<GitHubBot, 'status' | 'create' | 'cancel' | 'remove' | 'setWrites'>,
  logger: Logger,
): () => void {
  async function relay(
    name: string,
    call: () => Promise<GitHubBotStatus>,
  ): Promise<GitHubBotStatus> {
    try {
      return await call();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      logger.warn(`github-bot: ${name} failed`, { code: serialized.code });
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
  }

  ipcMain.handle(CHANNEL.githubBotStatus, () => relay('status', () => bot.status()));
  ipcMain.handle(CHANNEL.githubBotCreate, (_event, req: GitHubBotCreateRequest) =>
    relay('create', () =>
      bot.create(
        req?.target === 'org'
          ? { target: 'org', org: typeof req.org === 'string' ? req.org : '' }
          : { target: 'user' },
      ),
    ),
  );
  ipcMain.handle(CHANNEL.githubBotCancel, () => relay('cancel', () => bot.cancel()));
  ipcMain.handle(CHANNEL.githubBotRemove, () => relay('remove', () => bot.remove()));
  ipcMain.handle(CHANNEL.githubBotSetWrites, (_event, req: GitHubBotSetWritesRequest) =>
    relay('set writes', () => bot.setWrites(Array.isArray(req?.writes) ? req.writes : [null])),
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.githubBotSetWrites);
    ipcMain.removeHandler(CHANNEL.githubBotRemove);
    ipcMain.removeHandler(CHANNEL.githubBotCancel);
    ipcMain.removeHandler(CHANNEL.githubBotCreate);
    ipcMain.removeHandler(CHANNEL.githubBotStatus);
  };
}

export function registerGitHubHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  auth: Pick<GitHubAuth, 'signIn' | 'status' | 'cancel' | 'signOut' | 'saveToken'>,
  logger: Logger,
): () => void {
  async function relay<T>(name: string, call: () => Promise<T>): Promise<T> {
    try {
      return await call();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      logger.warn(`github: ${name} failed`, { code: serialized.code });
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
  }

  ipcMain.handle(CHANNEL.githubSignIn, (): Promise<GitHubPendingSignIn> =>
    relay('sign-in', () => auth.signIn()),
  );
  ipcMain.handle(CHANNEL.githubStatus, (): Promise<GitHubStatus> =>
    relay('status', () => auth.status()),
  );
  ipcMain.handle(CHANNEL.githubCancel, (): void => auth.cancel());
  ipcMain.handle(CHANNEL.githubSignOut, (): Promise<GitHubStatus> =>
    relay('sign-out', () => auth.signOut()),
  );
  ipcMain.handle(
    CHANNEL.githubSaveToken,
    (_event, req: GitHubSaveTokenRequest): Promise<GitHubStatus> =>
      relay('save token', () => auth.saveToken(typeof req?.token === 'string' ? req.token : '')),
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.githubSaveToken);
    ipcMain.removeHandler(CHANNEL.githubSignOut);
    ipcMain.removeHandler(CHANNEL.githubCancel);
    ipcMain.removeHandler(CHANNEL.githubStatus);
    ipcMain.removeHandler(CHANNEL.githubSignIn);
  };
}
