import type { Tray, MenuItemConstructorOptions } from 'electron';
import type { WorkerStatus } from '@scout/shared';
import type { Logger } from '../util/logger.js';

export interface TrayDeps {
  createTray: (iconPath: string) => Tray;
  buildContextMenu: (template: MenuItemConstructorOptions[]) => Electron.Menu;
  iconPath: string;
  onShowWindow: () => void;
  onStopWorker: () => Promise<void>;
  onQuit: () => void;
  logger: Logger;
  initialStatus?: WorkerStatus;
}

export interface TrayController {
  setStatus(status: WorkerStatus): void;
  destroy(): void;
}

function prettyState(state: WorkerStatus['state']): string {
  switch (state) {
    case 'idle':
      return 'Idle';
    case 'starting':
      return 'Starting';
    case 'running':
      return 'Running';
    case 'stopped':
      return 'Stopped';
    case 'crashed':
      return 'Crashed';
  }
}

function isWorkerActive(state: WorkerStatus['state']): boolean {
  return state === 'running' || state === 'starting';
}

export function createTrayController(deps: TrayDeps): TrayController {
  const { createTray, buildContextMenu, iconPath, onShowWindow, onStopWorker, onQuit, logger } =
    deps;

  let currentStatus: WorkerStatus = deps.initialStatus ?? { state: 'idle', restarts: 0 };
  let tray: Tray | null = createTray(iconPath);

  function buildMenu(status: WorkerStatus): Electron.Menu {
    const template: MenuItemConstructorOptions[] = [
      {
        label: 'Show Scout Desktop',
        type: 'normal',
        id: 'show-window',
        click: onShowWindow,
      },
      { type: 'separator' },
      {
        label: `Status: ${prettyState(status.state)}`,
        type: 'normal',
        id: 'status-label',
        enabled: false,
      },
      { type: 'separator' },
    ];

    if (isWorkerActive(status.state)) {
      template.push({
        label: 'Stop worker',
        type: 'normal',
        id: 'stop-worker',
        click: () => {
          onStopWorker().catch((err: unknown) => {
            logger.error('tray: stop worker failed', err);
          });
        },
      });
    }

    template.push({
      label: 'Quit Scout Desktop',
      type: 'normal',
      id: 'quit',
      click: onQuit,
    });

    return buildContextMenu(template);
  }

  function attachTrayListeners(t: Tray): void {
    // Left-click on Windows/Linux shows window; macOS only emits 'click'.
    t.on('click', onShowWindow);

    // Windows/Linux emit distinct right-click for context menu popup.
    t.on('right-click', () => {
      t.popUpContextMenu();
    });
  }

  tray.setContextMenu(buildMenu(currentStatus));
  tray.setToolTip('Scout Desktop');
  attachTrayListeners(tray);

  return {
    setStatus(status: WorkerStatus): void {
      currentStatus = status;

      if (tray == null || tray.isDestroyed()) return;

      tray.setContextMenu(buildMenu(status));
      tray.setToolTip(`Scout Desktop — ${prettyState(status.state)}`);
    },

    destroy(): void {
      if (tray != null && !tray.isDestroyed()) {
        tray.destroy();
      }
      tray = null;
    },
  };
}
