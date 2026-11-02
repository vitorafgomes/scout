import { chmod, mkdir, readFile, rename, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { DesktopAutostartError } from '@scout/shared';

/** The argument a login launch carries: Scout starts with no window and the worker running. */
export const HIDDEN_ARG = '--hidden';

/** Characters a Desktop Entry Exec value would need escaped; a path holding one is refused instead. */
const UNSAFE_EXEC = /["`$\\%\r\n]/;

export interface LoginItemDeps {
  platform: NodeJS.Platform;
  /** The user's home directory; the Linux entry is `<home>/.config/autostart/scout.desktop`. */
  home: string;
  /** The program a login launch runs: the AppImage when Scout runs from one, else the app executable. */
  executable: string;
  app: {
    getLoginItemSettings(options?: { args?: string[] }): { openAtLogin: boolean };
    setLoginItemSettings(settings: { openAtLogin: boolean; args?: string[] }): void;
  };
}

/**
 * Scout's own start-at-login entry: turned on and off only by the owner's toggle in Settings; on Linux an entry that
 * exists is also rewritten at start to name Scout's own current executable (see refreshLoginEntry).
 */
export interface LoginItem {
  isEnabled(): Promise<boolean>;
  setEnabled(enabled: boolean): Promise<void>;
}

/** The exact Linux autostart entry Scout writes for this executable. */
export function autostartEntry(executable: string): string {
  return [
    '[Desktop Entry]',
    'Type=Application',
    'Name=Scout',
    `Exec="${executable}" ${HIDDEN_ARG}`,
    'X-GNOME-Autostart-enabled=true',
    '',
  ].join('\n');
}

export function createLoginItem(deps: LoginItemDeps): LoginItem {
  const file = path.join(deps.home, '.config', 'autostart', 'scout.desktop');

  async function writeEntry(): Promise<void> {
    if (UNSAFE_EXEC.test(deps.executable)) {
      throw new DesktopAutostartError(
        'Scout cannot start at login from a folder whose path has quotes, $, %, \\ or line breaks.',
      );
    }
    const tmp = `${file}.tmp`;
    try {
      await mkdir(path.dirname(file), { recursive: true });
      await writeFile(tmp, autostartEntry(deps.executable), { mode: 0o644 });
      await chmod(tmp, 0o644);
      await rename(tmp, file);
    } catch (err: unknown) {
      await rm(tmp, { force: true }).catch(() => undefined);
      throw new DesktopAutostartError(undefined, { cause: err });
    }
  }

  return {
    async isEnabled(): Promise<boolean> {
      if (deps.platform !== 'linux') {
        return deps.app.getLoginItemSettings({ args: [HIDDEN_ARG] }).openAtLogin;
      }
      return stat(file).then(
        (s) => s.isFile(),
        () => false,
      );
    },

    async setEnabled(enabled: boolean): Promise<void> {
      if (deps.platform !== 'linux') {
        try {
          deps.app.setLoginItemSettings({ openAtLogin: enabled, args: [HIDDEN_ARG] });
        } catch (err: unknown) {
          throw new DesktopAutostartError(undefined, { cause: err });
        }
        return;
      }
      if (enabled) {
        await writeEntry();
        return;
      }
      try {
        await rm(file, { force: true });
      } catch (err: unknown) {
        throw new DesktopAutostartError(undefined, { cause: err });
      }
    },
  };
}

/**
 * Rewrites the Linux start-at-login entry when it names another executable, such as an AppImage an update saved
 * under the new release's name; resolves true when it rewrote the entry. Start at login that is off stays off.
 */
export async function refreshLoginEntry(deps: LoginItemDeps): Promise<boolean> {
  if (deps.platform !== 'linux') return false;
  const file = path.join(deps.home, '.config', 'autostart', 'scout.desktop');
  const current = await readFile(file, 'utf8').catch(() => null);
  if (current === null || current === autostartEntry(deps.executable)) return false;
  await createLoginItem(deps).setEnabled(true);
  return true;
}

/** The argument that opens the quick-ask window, for a desktop shortcut where global shortcuts are refused. */
export const ASK_ARG = '--ask';
