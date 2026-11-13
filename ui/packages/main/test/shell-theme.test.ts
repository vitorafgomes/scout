import { describe, expect, it } from 'bun:test';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { CHANNEL, VaultUnavailableError, readIpcError, type ThemeState } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import {
  WINDOW_BACKGROUND,
  registerTheme,
  windowBackground,
  type NativeThemeSubset,
  type ThemeWindow,
} from '../src/shell/theme.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

function fakeNativeTheme(systemDark: boolean) {
  const listeners = new Set<() => void>();
  const native = {
    systemDark,
    themeSource: 'system' as NativeThemeSubset['themeSource'],
    get shouldUseDarkColors(): boolean {
      return this.themeSource === 'system' ? this.systemDark : this.themeSource === 'dark';
    },
    on(_event: 'updated', listener: () => void) {
      listeners.add(listener);
    },
    removeListener(_event: 'updated', listener: () => void) {
      listeners.delete(listener);
    },
    listeners,
    systemChanges(dark: boolean) {
      this.systemDark = dark;
      if (this.themeSource === 'system') for (const listener of listeners) listener();
    },
  };
  return native;
}

function fakeWindow(
  options: { destroyed?: boolean; throws?: boolean; transparent?: boolean } = {},
) {
  const received: unknown[] = [];
  const painted: string[] = [];
  const window: ThemeWindow & { received: unknown[]; painted: string[]; transparent: boolean } = {
    received,
    painted,
    transparent: options.transparent ?? false,
    isDestroyed: () => options.destroyed ?? false,
    setBackgroundColor(color: string) {
      painted.push(color);
    },
    webContents: {
      send(channel: string, payload: unknown) {
        if (options.throws === true) throw new Error('Object has been destroyed');
        received.push([channel, payload]);
      },
    },
  };
  return window;
}

async function setup(
  options: {
    stored?: string;
    systemDark?: boolean;
    get?: (key: string) => Promise<string | null>;
    store?: (key: string, value: string) => Promise<void>;
    windows?: ReturnType<typeof fakeWindow>[];
  } = {},
) {
  const handlers = new Map<string, IpcHandler>();
  const ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'> = {
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
  const entries = new Map<string, string>();
  if (options.stored !== undefined) entries.set('app.theme', options.stored);
  const vault = {
    get: options.get ?? (async (key: string) => entries.get(key) ?? null),
    store:
      options.store ??
      (async (key: string, value: string) => {
        entries.set(key, value);
      }),
  };
  const nativeTheme = fakeNativeTheme(options.systemDark ?? true);
  const windows = options.windows ?? [fakeWindow(), fakeWindow()];
  const { logger, lines } = recordingLogger();
  const handle = await registerTheme({
    ipcMain,
    vault,
    nativeTheme,
    windows: () => windows,
    transparent: (window) => (window as ReturnType<typeof fakeWindow>).transparent,
    logger,
  });
  const invoke = (channel: string, req?: unknown) =>
    Promise.resolve(handlers.get(channel)!(NULL_EVENT, req)) as Promise<ThemeState>;
  return { handle, handlers, invoke, entries, nativeTheme, windows, lines };
}

describe('the theme choice stored in the vault under app.theme', () => {
  const stored: [string | undefined, boolean, ThemeState][] = [
    [undefined, true, { choice: 'system', resolved: 'dark' }],
    [undefined, false, { choice: 'system', resolved: 'light' }],
    ['system', false, { choice: 'system', resolved: 'light' }],
    ['light', true, { choice: 'light', resolved: 'light' }],
    ['dark', false, { choice: 'dark', resolved: 'dark' }],
    ['sepia', true, { choice: 'system', resolved: 'dark' }],
  ];

  for (const [value, systemDark, expected] of stored) {
    it(`stored ${String(value)} with a ${systemDark ? 'dark' : 'light'} system starts as ${expected.choice}, painted ${expected.resolved}`, async () => {
      const { invoke, nativeTheme } = await setup({
        ...(value === undefined ? {} : { stored: value }),
        systemDark,
      });

      expect(await invoke(CHANNEL.themeGet)).toEqual(expected);
      expect(nativeTheme.themeSource).toBe(expected.choice);
    });
  }

  it('an unreadable vault defaults to system and says so in the log', async () => {
    const { invoke, nativeTheme, lines } = await setup({
      get: async () => {
        throw new VaultUnavailableError();
      },
      systemDark: false,
    });

    expect(await invoke(CHANNEL.themeGet)).toEqual({ choice: 'system', resolved: 'light' });
    expect(nativeTheme.themeSource).toBe('system');
    expect(lines.some((line) => line.includes('theme: stored choice not read'))).toBe(true);
  });
});

describe('choosing a theme', () => {
  const choices: ['light' | 'dark' | 'system', boolean, ThemeState][] = [
    ['light', true, { choice: 'light', resolved: 'light' }],
    ['dark', false, { choice: 'dark', resolved: 'dark' }],
    ['system', true, { choice: 'system', resolved: 'dark' }],
    ['system', false, { choice: 'system', resolved: 'light' }],
  ];

  for (const [choice, systemDark, expected] of choices) {
    it(`${choice} on a ${systemDark ? 'dark' : 'light'} system is stored, applied through nativeTheme and pushed to every window`, async () => {
      const { invoke, entries, nativeTheme, windows } = await setup({
        stored: choice === 'system' ? 'light' : 'system',
        systemDark,
      });

      const answer = await invoke(CHANNEL.themeSet, { choice });

      expect(answer).toEqual(expected);
      expect(entries.get('app.theme')).toBe(choice);
      expect(nativeTheme.themeSource).toBe(choice);
      for (const window of windows) expect(window.received).toEqual([[CHANNEL.theme, expected]]);
    });
  }

  it('the choice is the one the next app run starts with', async () => {
    const first = await setup({ systemDark: true });
    await first.invoke(CHANNEL.themeSet, { choice: 'light' });

    const next = await setup({ stored: first.entries.get('app.theme')!, systemDark: true });

    expect(await next.invoke(CHANNEL.themeGet)).toEqual({ choice: 'light', resolved: 'light' });
  });

  for (const req of [undefined, {}, { choice: 'sepia' }, { choice: 1 }]) {
    it(`refuses ${JSON.stringify(req)} and keeps the current theme`, async () => {
      const { invoke, entries, nativeTheme, windows } = await setup({ stored: 'dark' });

      const error = await invoke(CHANNEL.themeSet, req).catch((err: unknown) => err);

      expect(readIpcError(error)).toEqual({
        code: 'theme/invalid-choice',
        message: 'Scout could not use that theme.',
      });
      expect(nativeTheme.themeSource).toBe('dark');
      expect(entries.get('app.theme')).toBe('dark');
      for (const window of windows) expect(window.received).toEqual([]);
    });
  }

  it('a vault that cannot store the choice still applies it for this session and says so in the log', async () => {
    const { invoke, nativeTheme, windows, lines } = await setup({
      systemDark: true,
      store: async () => {
        throw new VaultUnavailableError();
      },
    });

    const answer = await invoke(CHANNEL.themeSet, { choice: 'light' });

    expect(answer).toEqual({ choice: 'light', resolved: 'light' });
    expect(nativeTheme.themeSource).toBe('light');
    expect(windows[0]!.received).toEqual([[CHANNEL.theme, answer]]);
    expect(lines.some((line) => line.includes('theme: choice not remembered'))).toBe(true);
  });

  it('a closed or closing window is skipped and the others still get the change', async () => {
    const open = fakeWindow();
    const { invoke } = await setup({
      windows: [fakeWindow({ destroyed: true }), fakeWindow({ throws: true }), open],
    });

    await invoke(CHANNEL.themeSet, { choice: 'dark' });

    expect(open.received).toEqual([[CHANNEL.theme, { choice: 'dark', resolved: 'dark' }]]);
  });
});

describe('following the system', () => {
  it('a change of the system preference reaches every window while the choice is system', async () => {
    const { nativeTheme, windows } = await setup({ systemDark: true });

    nativeTheme.systemChanges(false);

    for (const window of windows)
      expect(window.received).toEqual([[CHANNEL.theme, { choice: 'system', resolved: 'light' }]]);
  });

  it('dispose stops following the system and removes the channels', async () => {
    const { handle, handlers, nativeTheme, windows } = await setup({ systemDark: true });

    handle.dispose();
    nativeTheme.systemChanges(false);

    expect(nativeTheme.listeners.size).toBe(0);
    expect(handlers.has(CHANNEL.themeGet)).toBe(false);
    expect(handlers.has(CHANNEL.themeSet)).toBe(false);
    for (const window of windows) expect(window.received).toEqual([]);
  });
});

describe('the window background, so the light theme does not flash dark on open', () => {
  const stored: [string | undefined, boolean, 'light' | 'dark'][] = [
    [undefined, true, 'dark'],
    [undefined, false, 'light'],
    ['light', true, 'light'],
    ['dark', false, 'dark'],
    ['system', false, 'light'],
  ];

  for (const [value, systemDark, scheme] of stored) {
    it(`stored ${String(value)} with a ${systemDark ? 'dark' : 'light'} system: a new window opens painted with the ${scheme} page background`, async () => {
      const { nativeTheme } = await setup({
        ...(value === undefined ? {} : { stored: value }),
        systemDark,
      });

      expect(windowBackground(nativeTheme)).toBe(WINDOW_BACKGROUND[scheme]);
    });
  }

  it("main's two backgrounds are the stylesheet's --sc-bg in each scheme", () => {
    const scss = readFileSync(
      path.join(import.meta.dirname, '../../renderer/src/styles.scss'),
      'utf8',
    );
    const surface = (scheme: string) =>
      new RegExp(`\\$_${scheme}: \\(\\s*material: \\(\\s*surface: (#[0-9a-f]{6}),`).exec(scss)?.[1];

    expect(scss).toMatch(/:root,\n\.sc-dark-canvas \{\n  --sc-bg: var\(--mat-sys-surface\);/);
    expect({ dark: surface('dark'), light: surface('light') }).toEqual(WINDOW_BACKGROUND);
  });

  const changes: ['light' | 'dark' | 'system', boolean, 'light' | 'dark'][] = [
    ['light', true, 'light'],
    ['dark', false, 'dark'],
    ['system', false, 'light'],
  ];

  for (const [choice, systemDark, scheme] of changes) {
    it(`choosing ${choice} on a ${systemDark ? 'dark' : 'light'} system repaints every open window ${scheme}, and the floating button stays transparent`, async () => {
      const button = fakeWindow({ transparent: true });
      const open = [fakeWindow(), fakeWindow()];
      const { invoke } = await setup({
        stored: choice === 'system' ? 'dark' : 'system',
        systemDark,
        windows: [...open, button],
      });

      await invoke(CHANNEL.themeSet, { choice });

      for (const window of open) expect(window.painted).toEqual([WINDOW_BACKGROUND[scheme]]);
      expect(button.painted).toEqual([]);
      expect(button.received).toHaveLength(1);
    });
  }

  it('a change of the system preference repaints every open window while the choice is system', async () => {
    const { nativeTheme, windows } = await setup({ systemDark: true });

    nativeTheme.systemChanges(false);

    for (const window of windows) expect(window.painted).toEqual([WINDOW_BACKGROUND.light]);
  });
});

describe('main.ts wiring', () => {
  it('main registers the theme with nativeTheme and every window before any window opens, and disposes it on quit', () => {
    const main = readFileSync(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');
    const start = main.indexOf('theme = await registerTheme({');
    const wiring = main.slice(start, main.indexOf('});', start));
    const dispose = main.slice(
      main.indexOf('function disposeAll(): void {'),
      main.indexOf('const notifierLogger'),
    );

    expect(start).toBeGreaterThan(-1);
    expect(wiring).toContain('nativeTheme,');
    expect(wiring).toContain('windows: () => BrowserWindow.getAllWindows(),');
    expect(wiring).toContain('transparent: (target) => transparentWindows.has(target),');
    expect(start).toBeLessThan(main.indexOf('quickAsk = await registerQuickAsk({'));
    expect(start).toBeLessThan(main.indexOf('operatorWindow = await registerOperatorWindow({'));
    expect(start).toBeLessThan(main.indexOf('launchWindow({'));
    expect(dispose).toContain('theme?.dispose();');
  });

  it('every window main opens goes through newWindow, which marks a transparent one, and opens with the resolved theme background', () => {
    const main = readFileSync(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');

    expect(main).toContain(
      'const created = new BrowserWindow(withoutMenuBar(opts));\n  if (opts.transparent === true) transparentWindows.add(created);',
    );
    expect([...main.matchAll(/createWindow: newWindow,/g)]).toHaveLength(3);
    expect([...main.matchAll(/new BrowserWindow\(/g)]).toHaveLength(1);
    expect([
      ...main.matchAll(/background: \(\) => windowBackground\(nativeTheme\),/g),
    ]).toHaveLength(2);
    expect(main).toContain('background: windowBackground(nativeTheme),');
  });
});
