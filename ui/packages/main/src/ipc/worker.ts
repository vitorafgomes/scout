import type { IpcMain, BrowserWindow, WebContents } from 'electron';
import { CHANNEL, serializeError } from '@scout/shared';
import type {
  WorkerEvent,
  WorkerRestartResponse,
  WorkerStartResponse,
  WorkerStopResponse,
  WorkerStatusResponse,
} from '@scout/shared';
import type { Supervisor } from '../worker/supervisor.js';
import type { WorkerRestart } from '../worker/worker-restart.js';
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

const MAX_RESTART_REASON = 80;

/** The renderer's restart channel; it goes through main's one restart path and answers with what that did. */
export function registerWorkerRestartHandler(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  restart: Pick<WorkerRestart, 'restart'>,
  logger: Logger,
): () => void {
  ipcMain.handle(CHANNEL.workerRestart, (_event, req: unknown): Promise<WorkerRestartResponse> => {
    const raw = (req as Record<string, unknown> | null | undefined)?.['reason'];
    const reason =
      typeof raw === 'string' && raw.trim().length > 0
        ? raw.trim().slice(0, MAX_RESTART_REASON)
        : 'a change in the app';
    logger.info('worker: restart requested', { reason });
    return restart.restart(reason);
  });
  return () => ipcMain.removeHandler(CHANNEL.workerRestart);
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

  ipcMain.handle(CHANNEL.workerLogSnapshot, (): readonly WorkerEvent[] => supervisor.getSnapshot());

  return () => {
    ipcMain.removeHandler(CHANNEL.workerStart);
    ipcMain.removeHandler(CHANNEL.workerStop);
    ipcMain.removeHandler(CHANNEL.workerStatus);
    ipcMain.removeHandler(CHANNEL.workerLogSnapshot);

    _unsubscribeEvents?.();
    _unsubscribeEvents = null;
    _attachedWindow = null;

    logger.info('worker: IPC handlers unregistered');
  };
}
