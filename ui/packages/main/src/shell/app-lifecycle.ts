import type { App } from 'electron';
import type { Supervisor } from '../worker/supervisor.js';
import type { Logger } from '../util/logger.js';
import { ASK_ARG, HIDDEN_ARG } from './login-item.js';

export interface LifecycleWindow {
  isDestroyed(): boolean;
  isMinimized(): boolean;
  restore(): void;
  show(): void;
  focus(): void;
  hide(): void;
}

export interface AppLifecycleDeps {
  app: Pick<App, 'on' | 'quit' | 'exit'>;
  platform: NodeJS.Platform;
  supervisor(): Pick<Supervisor, 'status' | 'stop'> | null;
  keepRunning(): boolean;
  window(): LifecycleWindow | null;
  createWindow(): void;
  onHiddenToTray(): void;
  /** Opens the quick-ask window; a second launch with `--ask` calls it instead of showing the main window. */
  onAsk(): void;
  /** Unregisters every IPC handler and releases the tray, the control plane and the sign-ins. */
  dispose(): void;
  logger: Logger;
}

export interface AppLifecycle {
  /** Shows the main window, creating it if it is gone, and brings it to the front. */
  showWindow(): void;
  /**
   * Attach to the main window's close event: while keep-running is on and Scout is not quitting, hides the window
   * instead; with keep-running off it quits (except on macOS), because a hidden quick-ask window would otherwise
   * stop window-all-closed from firing and keep Scout alive.
   */
  onWindowClose(event: { preventDefault(): void }): void;
}

export interface LaunchDeps {
  argv: readonly string[];
  platform: NodeJS.Platform;
  trayAvailable: boolean;
  notifier: { isSupported(): boolean; startedHidden(): void };
  openWindow(show: boolean): { once(event: 'show', listener: () => void): unknown };
  /** Opens the quick-ask window for a launch with `--ask`. */
  onAsk(): void;
}

/**
 * Opens the main window, hidden for a `--hidden` launch while a tray can bring it back. On Linux a hidden
 * start also needs notifications, because a tray icon may be invisible there; it announces itself with one.
 * A launch with `--ask` opens quick ask after the main window has shown, so showing it does not take the
 * focus from quick ask and hide it; a hidden launch opens quick ask at once.
 */
export function launchWindow(deps: LaunchDeps): void {
  const hidden =
    deps.trayAvailable &&
    deps.argv.includes(HIDDEN_ARG) &&
    (deps.platform !== 'linux' || deps.notifier.isSupported());
  const window = deps.openWindow(!hidden);
  if (hidden) deps.notifier.startedHidden();
  if (!deps.argv.includes(ASK_ARG)) return;
  if (hidden) deps.onAsk();
  else window.once('show', () => deps.onAsk());
}

export function installAppLifecycle(deps: AppLifecycleDeps): AppLifecycle {
  const { app, logger } = deps;
  let quitting = false;

  function showWindow(): void {
    const window = deps.window();
    if (window === null || window.isDestroyed()) {
      deps.createWindow();
      return;
    }
    if (window.isMinimized()) window.restore();
    window.show();
    window.focus();
  }

  app.on('window-all-closed', () => {
    if (!quitting && !deps.keepRunning() && deps.platform !== 'darwin') app.quit();
  });

  app.on('before-quit', (event) => {
    quitting = true;
    logger.info('before-quit');

    const worker = deps.supervisor();
    if (worker === null || worker.status.state === 'stopped' || worker.status.state === 'idle') {
      deps.dispose();
      return;
    }

    event.preventDefault();
    void worker
      .stop()
      .catch((err: unknown) => {
        logger.warn('before-quit: worker stop failed', { err: String(err) });
      })
      .finally(() => {
        deps.dispose();
        app.exit(0);
      });
  });

  app.on('second-instance', (_event, argv: readonly string[] = []) => {
    if (argv.includes(ASK_ARG)) deps.onAsk();
    else showWindow();
  });
  app.on('activate', showWindow);

  return {
    showWindow,
    onWindowClose(event): void {
      if (quitting) return;
      if (!deps.keepRunning()) {
        if (deps.platform !== 'darwin') app.quit();
        return;
      }
      event.preventDefault();
      deps.window()?.hide();
      deps.onHiddenToTray();
    },
  };
}
