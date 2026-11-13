import { describe, expect, it } from 'bun:test';
import { readFileSync } from 'node:fs';
import type { Menu, MenuItemConstructorOptions } from 'electron';

import { installAppMenu, withoutMenuBar } from '../src/shell/app-menu.js';

function fakeMenu(platform: NodeJS.Platform) {
  const set: (Menu | null)[] = [];
  const built: MenuItemConstructorOptions[][] = [];
  installAppMenu({
    platform,
    setApplicationMenu: (menu) => set.push(menu),
    buildFromTemplate: (template) => {
      built.push(template);
      return { template } as unknown as Menu;
    },
  });
  return { set, built };
}

function roles(items: readonly MenuItemConstructorOptions[]): string[] {
  return items.flatMap((item) => (item.role === undefined ? [] : [item.role]));
}

describe('the application menu', () => {
  for (const platform of ['linux', 'win32'] as const) {
    it(`on ${platform} there is no application menu: File, Edit, View and Window are gone`, () => {
      const { set, built } = fakeMenu(platform);

      expect(set).toEqual([null]);
      expect(built).toEqual([]);
    });
  }

  it('on darwin a minimal menu stays: the app menu, Edit with undo, redo, cut, copy, paste and select all, and Window', () => {
    const { set, built } = fakeMenu('darwin');

    expect(set).toHaveLength(1);
    expect(built).toHaveLength(1);
    const template = built[0]!;
    expect(template.map((item) => item.role ?? item.label)).toEqual([
      'appMenu',
      'Edit',
      'windowMenu',
    ]);
    const edit = template.find((item) => item.label === 'Edit')!;
    expect(roles(edit.submenu as MenuItemConstructorOptions[])).toEqual([
      'undo',
      'redo',
      'cut',
      'copy',
      'paste',
      'selectAll',
    ]);
  });

  it('a window built through withoutMenuBar hides the menu bar and keeps every other option', () => {
    const options = withoutMenuBar({ width: 420, height: 560, frame: false });

    expect(options).toEqual({ width: 420, height: 560, frame: false, autoHideMenuBar: true });
  });

  it('main.ts removes the menu before it creates any window, and every window it creates goes through withoutMenuBar', () => {
    const source = readFileSync(new URL('../src/main.ts', import.meta.url), 'utf8');
    const install = source.indexOf('installAppMenu({');
    const windows = [...source.matchAll(/new BrowserWindow\(/g)].map((match) => match.index!);
    const opened = [...source.matchAll(/(?<!function )\bnewWindow\b/g)].map(
      (match) => match.index!,
    );
    const firstWindow = Math.min(source.indexOf('createMainWindow({'), ...opened);

    expect(install).toBeGreaterThan(-1);
    expect(install).toBeLessThan(firstWindow);
    expect(windows.length).toBeGreaterThan(0);
    expect(opened.length).toBeGreaterThan(0);
    expect([...source.matchAll(/new BrowserWindow\(withoutMenuBar\(/g)]).toHaveLength(
      windows.length,
    );
  });
});
