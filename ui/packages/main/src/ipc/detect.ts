import type { IpcMain } from 'electron';
import { serializeError, type DetectClientsResponse } from '@scout/shared';
import { detectAll } from '../detect/index.js';
import type { DetectDeps } from '../detect/types.js';
import type { Logger } from '../util/logger.js';
import { CHANNEL } from '@scout/shared';

export function registerDetectHandler(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  deps: DetectDeps,
  logger: Logger,
): () => void {
  ipcMain.handle(CHANNEL.detectClients, async (): Promise<DetectClientsResponse> => {
    try {
      const clients = await detectAll(deps, logger);
      return { clients };
    } catch (err: unknown) {
      const serialized = serializeError(err);
      throw Object.assign(new Error(serialized.message), { code: serialized.code });
    }
  });

  return () => {
    ipcMain.removeHandler(CHANNEL.detectClients);
  };
}
