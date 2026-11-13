import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type ThemeChoice,
  type ThemeSetRequest,
  type ThemeState,
} from '@scout/shared';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';

const THEME_KEY = 'app.theme';

/** The page background, the stylesheet's `--sc-bg`, in each scheme; a window opens painted with it so it never flashes. */
export const WINDOW_BACKGROUND = { dark: '#0f1623', light: '#f5f7fb' } as const;

/** The background a new window opens with: the scheme nativeTheme resolves from the stored choice and the system. */
export function windowBackground(
  nativeTheme: Pick<NativeThemeSubset, 'shouldUseDarkColors'>,
): string {
  return nativeTheme.shouldUseDarkColors ? WINDOW_BACKGROUND.dark : WINDOW_BACKGROUND.light;
}
const CHOICES: readonly ThemeChoice[] = ['light', 'dark', 'system'];

/** The part of Electron's nativeTheme the theme module drives; `themeSource` also sets every window's prefers-color-scheme. */
export interface NativeThemeSubset {
  themeSource: ThemeChoice;
  readonly shouldUseDarkColors: boolean;
  on(event: 'updated', listener: () => void): unknown;
  removeListener(event: 'updated', listener: () => void): unknown;
}

/** A window main can push to; Electron's BrowserWindow is one. */
export interface ThemeWindow {
  isDestroyed(): boolean;
  setBackgroundColor(color: string): void;
  readonly webContents: { send(channel: string, payload: unknown): void };
}

export interface ThemeDeps {
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>;
  vault: Pick<Vault, 'get' | 'store'>;
  nativeTheme: NativeThemeSubset;
  windows: () => readonly ThemeWindow[];
  /** True for a window drawn on a transparent background, such as the floating button; it keeps its background. */
  transparent: (window: ThemeWindow) => boolean;
  logger: Logger;
}

export interface ThemeHandle {
  dispose(): void;
}

function isChoice(value: unknown): value is ThemeChoice {
  return CHOICES.includes(value as ThemeChoice);
}

/**
 * The desktop's theme: the owner's choice of light, dark or system, kept in the vault under `app.theme` (system when
 * absent, unreadable or unknown), applied through nativeTheme and pushed to every window on each choice and on each
 * change of the system's preference; every window but a transparent one is repainted with the scheme's background.
 */
export async function registerTheme(deps: ThemeDeps): Promise<ThemeHandle> {
  const { ipcMain, vault, nativeTheme, windows, transparent, logger } = deps;

  const stored = await vault.get(THEME_KEY).catch((err: unknown) => {
    logger.warn('theme: stored choice not read, following the system', {
      error: err instanceof Error ? err.name : typeof err,
    });
    return null;
  });
  nativeTheme.themeSource = isChoice(stored) ? stored : 'system';

  function state(): ThemeState {
    return {
      choice: nativeTheme.themeSource,
      resolved: nativeTheme.shouldUseDarkColors ? 'dark' : 'light',
    };
  }

  function push(): void {
    const payload = state();
    const background = windowBackground(nativeTheme);
    for (const target of windows()) {
      try {
        if (target.isDestroyed()) continue;
        if (!transparent(target)) target.setBackgroundColor(background);
        target.webContents.send(CHANNEL.theme, payload);
      } catch {
        logger.debug('theme: a window closed before the change reached it');
      }
    }
  }

  async function choose(req: ThemeSetRequest | undefined): Promise<ThemeState> {
    const choice = req?.choice;
    if (!isChoice(choice)) {
      const serialized = serializeError(
        new ScoutError('theme/invalid-choice', 'Scout could not use that theme.'),
      );
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
    nativeTheme.themeSource = choice;
    push();
    await vault.store(THEME_KEY, choice).catch((err: unknown) => {
      logger.warn('theme: choice not remembered, it lasts for this session', {
        error: err instanceof Error ? err.name : typeof err,
      });
    });
    logger.info('theme: choice changed', { choice });
    return state();
  }

  nativeTheme.on('updated', push);
  ipcMain.handle(CHANNEL.themeGet, () => state());
  ipcMain.handle(CHANNEL.themeSet, (_event, req: ThemeSetRequest | undefined) => choose(req));

  return {
    dispose() {
      nativeTheme.removeListener('updated', push);
      ipcMain.removeHandler(CHANNEL.themeSet);
      ipcMain.removeHandler(CHANNEL.themeGet);
    },
  };
}
