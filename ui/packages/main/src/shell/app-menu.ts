import type { BrowserWindowConstructorOptions, Menu, MenuItemConstructorOptions } from 'electron';

export interface AppMenuDeps {
  readonly platform: NodeJS.Platform;
  setApplicationMenu(menu: Menu | null): void;
  buildFromTemplate(template: MenuItemConstructorOptions[]): Menu;
}

const MAC_MENU: MenuItemConstructorOptions[] = [
  { role: 'appMenu' },
  {
    label: 'Edit',
    submenu: [
      { role: 'undo' },
      { role: 'redo' },
      { type: 'separator' },
      { role: 'cut' },
      { role: 'copy' },
      { role: 'paste' },
      { role: 'selectAll' },
    ],
  },
  { role: 'windowMenu' },
];

/**
 * Replaces Electron's default File, Edit, View and Window menu: none on Linux and Windows, where
 * Chromium handles copy and paste in text fields without a menu; on macOS the app, Edit and Window
 * menus, because the copy and paste shortcuts there come from the Edit roles.
 */
export function installAppMenu(deps: AppMenuDeps): void {
  deps.setApplicationMenu(deps.platform === 'darwin' ? deps.buildFromTemplate(MAC_MENU) : null);
}

/** The window options with the menu bar hidden, so no bar shows even if a menu is set later. */
export function withoutMenuBar(
  options: BrowserWindowConstructorOptions,
): BrowserWindowConstructorOptions {
  return { ...options, autoHideMenuBar: true };
}
