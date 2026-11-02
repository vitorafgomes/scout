import type { Menu, MenuItemConstructorOptions, Tray } from 'electron';
import type { WorkerStatus } from '@scout/shared';
import type { Logger } from '../util/logger.js';

export interface TrayDeps {
  createTray: (iconPath: string) => Tray;
  buildContextMenu: (template: MenuItemConstructorOptions[]) => Menu;
  iconPath: string;
  onOpen: () => void;
  onAskScout: () => void;
  onStart: () => Promise<unknown>;
  onStop: () => Promise<unknown>;
  /** Must go through app.quit() so the normal before-quit path stops the worker. */
  onQuit: () => void;
  logger: Logger;
  initialStatus?: WorkerStatus;
}

export interface TrayController {
  setStatus(status: WorkerStatus): void;
  destroy(): void;
}

const STATE_LABEL: Record<WorkerStatus['state'], string> = {
  idle: 'Idle',
  starting: 'Starting',
  running: 'Running',
  stopped: 'Stopped',
  crashed: 'Crashed',
};

/** The tray icon and its menu, or null when this desktop could not create a tray. */
export function createTrayController(deps: TrayDeps): TrayController | null {
  const { logger } = deps;

  let tray: Tray;
  try {
    tray = deps.createTray(deps.iconPath);
  } catch (err: unknown) {
    logger.warn('tray: not available on this desktop', {
      error: err instanceof Error ? err.name : typeof err,
    });
    return null;
  }

  let status: WorkerStatus = deps.initialStatus ?? { state: 'idle', restarts: 0 };
  let pending: 'start' | 'stop' | null = null;

  function run(action: 'start' | 'stop'): void {
    pending = action;
    refresh();
    (action === 'start' ? deps.onStart() : deps.onStop())
      .catch((err: unknown) => {
        logger.error(`tray: ${action} failed`, err);
      })
      .finally(() => {
        pending = null;
        refresh();
      });
  }

  function workerItem(): MenuItemConstructorOptions {
    if (pending === 'stop') return { id: 'worker', label: 'Stop Scout', enabled: false };
    if (pending === 'start' || status.state === 'starting') {
      return { id: 'worker', label: 'Start Scout', enabled: false };
    }
    if (status.state === 'running') {
      return { id: 'worker', label: 'Stop Scout', enabled: true, click: () => run('stop') };
    }
    return { id: 'worker', label: 'Start Scout', enabled: true, click: () => run('start') };
  }

  function refresh(): void {
    if (tray.isDestroyed()) return;
    tray.setToolTip(`Scout - ${STATE_LABEL[status.state]}`);
    tray.setContextMenu(
      deps.buildContextMenu([
        { id: 'open', label: 'Open Scout', click: deps.onOpen },
        { id: 'ask', label: 'Ask Scout...', click: deps.onAskScout },
        { type: 'separator' },
        workerItem(),
        { type: 'separator' },
        { id: 'quit', label: 'Quit Scout', click: deps.onQuit },
      ]),
    );
  }

  tray.on('click', deps.onOpen);
  refresh();

  return {
    setStatus(next: WorkerStatus): void {
      status = next;
      refresh();
    },

    destroy(): void {
      if (!tray.isDestroyed()) tray.destroy();
    },
  };
}
