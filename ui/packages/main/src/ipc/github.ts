import type { IpcMain } from 'electron';
import {
  CHANNEL,
  encodeIpcErrorMessage,
  serializeError,
  type GitHubPendingSignIn,
  type GitHubSaveTokenRequest,
  type GitHubStatus,
} from '@scout/shared';
import type { GitHubAuth } from '../oauth/github-auth.js';
import type { Supervisor } from '../worker/supervisor.js';
import type { Logger } from '../util/logger.js';

/** Restarts the worker when it is running, so it picks up a GitHub connect or disconnect. */
export async function restartWorkerIfRunning(
  worker: Pick<Supervisor, 'status' | 'stop' | 'start'>,
  logger: Logger,
): Promise<void> {
  const { state } = worker.status;
  if (state !== 'running' && state !== 'starting') return;
  try {
    logger.info('github: restarting the worker to apply the GitHub connection');
    await worker.stop();
    await worker.start();
  } catch (err: unknown) {
    logger.warn('github: worker restart failed', { code: serializeError(err).code });
  }
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
