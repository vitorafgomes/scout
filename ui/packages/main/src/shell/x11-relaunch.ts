import type { Logger } from '../util/logger.js';

export const OZONE_X11_FLAG = '--ozone-platform=x11';

/**
 * How to start Scout again through XWayland, or null when no restart is needed: only on Linux in a
 * Wayland session that also offers an X server (`DISPLAY`), and only when no ozone platform was chosen
 * yet, so the restarted process never restarts again. A packaged app started from an AppImage restarts
 * the AppImage, because its mount goes away when this process exits; outside a packaged app an inherited
 * `APPIMAGE` belongs to another program and is ignored.
 */
export function x11RelaunchArgs(
  platform: string,
  env: Readonly<Record<string, string | undefined>>,
  argv: readonly string[],
  execPath: string,
  packaged: boolean,
): { execPath: string; args: string[] } | null {
  if (platform !== 'linux') return null;
  const wayland = env['XDG_SESSION_TYPE'] === 'wayland' || env['WAYLAND_DISPLAY'] != null;
  if (!wayland || env['DISPLAY'] == null) return null;
  if (argv.some((arg) => arg.startsWith('--ozone-platform'))) return null;
  const appImage = packaged ? env['APPIMAGE'] : undefined;
  return { execPath: appImage ?? execPath, args: [...argv, OZONE_X11_FLAG] };
}

export interface X11RestartDeps {
  spawn: (
    command: string,
    args: string[],
    options: { detached: true; stdio: 'ignore' },
  ) => Pick<NodeJS.EventEmitter, 'once'> & { unref(): void };
  exit: (code: number) => void;
  logger: Pick<Logger, 'info' | 'warn'>;
}

/**
 * Starts the new process detached and exits once it is running; resolves false, without exiting, when it
 * cannot be started, so Scout carries on as a native Wayland client.
 */
export function restartThroughX11(
  decision: { execPath: string; args: string[] },
  deps: X11RestartDeps,
): Promise<boolean> {
  return new Promise((resolve) => {
    const child = deps.spawn(decision.execPath, decision.args, { detached: true, stdio: 'ignore' });
    child.once('spawn', () => {
      child.unref();
      deps.logger.info('relaunching through XWayland', { execPath: decision.execPath });
      deps.exit(0);
      resolve(true);
    });
    child.once('error', (err: Error) => {
      deps.logger.warn('relaunch through XWayland failed; staying on native Wayland', {
        execPath: decision.execPath,
        error: err.message,
      });
      resolve(false);
    });
  });
}
