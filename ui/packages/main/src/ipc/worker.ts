import type { IpcMain, BrowserWindow, WebContents } from 'electron';
import { CHANNEL, serializeError } from '@scout/shared';
import type { WorkerStartResponse, WorkerStopResponse, WorkerStatusResponse } from '@scout/shared';
import type { Supervisor } from '../worker/supervisor.js';
import type { Logger } from '../util/logger.js';

export interface WorkerIpcDeps {
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>;
  supervisor: Supervisor;
  logger: Logger;
}

let _attachedWindow: BrowserWindow | null = null;
let _unsubscribeEvents: (() => void) | null = null;

export function attachBrowserWindow(window: BrowserWindow, supervisor: Supervisor): void {
  _attachedWindow = window;

  _unsubscribeEvents?.();

  // Replay the snapshot so a freshly-opened renderer catches up
  const snapshot = supervisor.getSnapshot();
  for (const event of snapshot) {
    safeWebContentsSend(window.webContents, event);
  }

  // Forward all future events
  _unsubscribeEvents = supervisor.onEvent((event) => {
    safeWebContentsSend(window.webContents, event);
  });
}

function safeWebContentsSend(wc: WebContents, event: unknown): void {
  try {
    if (!wc.isDestroyed()) {
      wc.send(CHANNEL.workerEvents, event);
    }
  } catch {
    // renderer may have been destroyed between the check and the send
  }
}

export function registerWorkerHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  supervisor: Supervisor,
  logger: Logger,
): () => void {
  ipcMain.handle(CHANNEL.workerStart, async (): Promise<WorkerStartResponse> => {
    logger.info('worker: start requested');
    try {
      return await supervisor.start();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      throw Object.assign(new Error(serialized.message), { code: serialized.code });
    }
  });

  ipcMain.handle(CHANNEL.workerStop, async (): Promise<WorkerStopResponse> => {
    logger.info('worker: stop requested');
    try {
      return await supervisor.stop();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      throw Object.assign(new Error(serialized.message), { code: serialized.code });
    }
  });

  ipcMain.handle(CHANNEL.workerStatus, (): WorkerStatusResponse => {
    return { ...supervisor.status };
  });

  return () => {
    ipcMain.removeHandler(CHANNEL.workerStart);
    ipcMain.removeHandler(CHANNEL.workerStop);
    ipcMain.removeHandler(CHANNEL.workerStatus);

    _unsubscribeEvents?.();
    _unsubscribeEvents = null;
    _attachedWindow = null;

    logger.info('worker: IPC handlers unregistered');
  };
}
