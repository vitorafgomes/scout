import { describe, expect, it } from 'bun:test';
import { EventEmitter } from 'node:events';
import { CHANNEL } from '@scout/shared';
import type { BrowserWindow, Display, IpcMain } from 'electron';
import {
  OPERATOR_WINDOW_TITLE,
  registerOperatorWindow,
  type OperatorWindowDeps,
} from '../src/operator/operator-window.js';
import { WINDOW_BACKGROUND, windowBackground } from '../src/shell/theme.js';
import { recordingLogger } from './kubernetes-fakes.js';

type Bounds = { x: number; y: number; width: number; height: number };

class FakeWebContents extends EventEmitter {
  openHandler: ((details: { url: string }) => { action: string }) | null = null;
  setWindowOpenHandler(handler: (details: { url: string }) => { action: string }): void {
    this.openHandler = handler;
  }
}

class FakeWindow extends EventEmitter {
  readonly webContents = new FakeWebContents();
  readonly calls: string[] = [];
  loaded: { file?: string; url?: string; hash?: string } | null = null;
  destroyed = false;
  minimized = false;
  bounds: Bounds;
  constructor(readonly options: Record<string, unknown>) {
    super();
    this.bounds = {
      x: 0,
      y: 0,
      width: Number(options['width']),
      height: Number(options['height']),
    };
  }
  isDestroyed(): boolean {
    return this.destroyed;
  }
  isMinimized(): boolean {
    return this.minimized;
  }
  restore(): void {
    this.calls.push('restore');
  }
  show(): void {
    this.calls.push('show');
  }
  focus(): void {
    this.calls.push('focus');
  }
  setBounds(bounds: Bounds): void {
    this.calls.push('setBounds');
    this.bounds = bounds;
  }
  getBounds(): Bounds {
    return this.bounds;
  }
  destroy(): void {
    this.destroyed = true;
  }
  loadFile(file: string, options: { hash?: string }): Promise<void> {
    this.loaded = { file, ...options };
    return Promise.resolve();
  }
  loadURL(url: string): Promise<void> {
    this.loaded = { url };
    return Promise.resolve();
  }
}

const DISPLAY = { workArea: { x: 0, y: 0, width: 1920, height: 1080 } } as Display;

function setup(
  options: {
    stored?: string;
    getFails?: boolean;
    storeFails?: boolean;
    devServerUrl?: string;
  } = {},
) {
  const theme = { shouldUseDarkColors: true };
  const handlers = new Map<string, () => unknown>();
  const ipcMain = {
    handle: (channel: string, handler: () => unknown) => handlers.set(channel, handler),
    removeHandler: (channel: string) => handlers.delete(channel),
  } as unknown as Pick<IpcMain, 'handle' | 'removeHandler'>;
  const windows: FakeWindow[] = [];
  const stored: Array<[string, string]> = [];
  const { logger, lines } = recordingLogger();
  const deps: OperatorWindowDeps = {
    ipcMain,
    createWindow: (opts) => {
      const created = new FakeWindow(opts as Record<string, unknown>);
      windows.push(created);
      return created as unknown as BrowserWindow;
    },
    preloadPath: '/app/preload.cjs',
    indexHtmlPath: '/app/index.html',
    ...(options.devServerUrl !== undefined ? { devServerUrl: options.devServerUrl } : {}),
    vault: {
      get: async (key) => {
        if (options.getFails === true) throw new Error('keychain locked');
        return key === 'operator.window' ? (options.stored ?? null) : null;
      },
      store: async (key, value) => {
        if (options.storeFails === true) throw new Error('disk full');
        stored.push([key, value]);
      },
    },
    screen: { getAllDisplays: () => [DISPLAY] },
    background: () => windowBackground(theme),
    logger,
  };
  return { deps, handlers, windows, stored, lines, theme };
}

async function flush(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('the operator window', () => {
  for (const scheme of ['light', 'dark'] as const) {
    it(`the ${scheme} theme does not flash on open: the operator window opens painted with the ${scheme} page background chosen after Scout started`, async () => {
      const { deps, handlers, windows, theme } = setup();
      await registerOperatorWindow(deps);
      theme.shouldUseDarkColors = scheme === 'dark';

      await handlers.get(CHANNEL.operatorWindow)!();

      expect(windows[0]!.options['backgroundColor']).toBe(WINDOW_BACKGROUND[scheme]);
    });
  }

  it('scout:operator.window opens one window on #/operator with the hardened webPreferences, the preload and a deny handler', async () => {
    const { deps, handlers, windows } = setup();
    await registerOperatorWindow(deps);

    await handlers.get(CHANNEL.operatorWindow)!();

    expect(windows).toHaveLength(1);
    const created = windows[0]!;
    expect(created.options['webPreferences']).toEqual({
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
      allowRunningInsecureContent: false,
      experimentalFeatures: false,
      preload: '/app/preload.cjs',
    });
    expect(created.options['width']).toBe(1400);
    expect(created.options['height']).toBe(900);
    expect(created.options['resizable']).toBe(true);
    expect(created.options['title']).toBe('Scout - YouTube operator');
    expect(created.loaded).toEqual({ file: '/app/index.html', hash: '/operator' });
    expect(created.webContents.openHandler!({ url: 'https://example.com' })).toEqual({
      action: 'deny',
    });
  });

  it('loads #/operator from the dev server in development', async () => {
    const { deps, windows } = setup({ devServerUrl: 'http://localhost:4200' });
    const operator = await registerOperatorWindow(deps);

    operator.open();

    expect(windows[0]!.loaded).toEqual({ url: 'http://localhost:4200/#/operator' });
  });

  it('blocks navigation away from the app and keeps its own title when the page sets one', async () => {
    const { deps, windows } = setup();
    const operator = await registerOperatorWindow(deps);
    operator.open();
    const created = windows[0]!;
    let navigationPrevented = false;
    let titlePrevented = false;

    created.webContents.emit(
      'will-navigate',
      { preventDefault: () => (navigationPrevented = true) },
      'https://evil.example',
    );
    created.emit('page-title-updated', { preventDefault: () => (titlePrevented = true) }, 'Scout');

    expect(navigationPrevented).toBe(true);
    expect(titlePrevented).toBe(true);
    expect(OPERATOR_WINDOW_TITLE).toBe('Scout - YouTube operator');
  });

  it('a second open focuses the window it already has without creating another', async () => {
    const { deps, handlers, windows } = setup();
    await registerOperatorWindow(deps);
    const open = handlers.get(CHANNEL.operatorWindow)!;

    await open();
    windows[0]!.minimized = true;
    await open();

    expect(windows).toHaveLength(1);
    expect(windows[0]!.calls).toEqual(['restore', 'show', 'focus']);
  });

  it('opens a new window after the owner closed the first one', async () => {
    const { deps, windows } = setup();
    const operator = await registerOperatorWindow(deps);

    operator.open();
    windows[0]!.emit('closed');
    operator.open();

    expect(windows).toHaveLength(2);
  });

  it('saves its place under operator.window when it is moved, resized or closed', async () => {
    const { deps, windows, stored } = setup();
    const operator = await registerOperatorWindow(deps);
    operator.open();
    const created = windows[0]!;

    created.bounds = { x: 10, y: 20, width: 1200, height: 800 };
    created.emit('moved');
    created.bounds = { x: 10, y: 20, width: 1300, height: 850 };
    created.emit('resized');
    created.bounds = { x: 30, y: 40, width: 1300, height: 850 };
    created.emit('close');
    await flush();

    expect(stored).toEqual([
      ['operator.window', JSON.stringify({ x: 10, y: 20, width: 1200, height: 800 })],
      ['operator.window', JSON.stringify({ x: 10, y: 20, width: 1300, height: 850 })],
      ['operator.window', JSON.stringify({ x: 30, y: 40, width: 1300, height: 850 })],
    ]);
  });

  it('restores the saved place when it lies on a current display', async () => {
    const saved = { x: 100, y: 50, width: 1200, height: 800 };
    const { deps, windows } = setup({ stored: JSON.stringify(saved) });
    const operator = await registerOperatorWindow(deps);

    operator.open();

    expect(windows[0]!.calls).toContain('setBounds');
    expect(windows[0]!.bounds).toEqual(saved);
  });

  it('keeps the default size when the saved place is on a display that is gone', async () => {
    const { deps, windows } = setup({
      stored: JSON.stringify({ x: 3000, y: 50, width: 1200, height: 800 }),
    });
    const operator = await registerOperatorWindow(deps);

    operator.open();

    expect(windows[0]!.calls).not.toContain('setBounds');
    expect(windows[0]!.bounds).toEqual({ x: 0, y: 0, width: 1400, height: 900 });
  });

  it('closes with the app: dispose saves the place, destroys the window and unregisters the channel', async () => {
    const { deps, handlers, windows, stored } = setup();
    const operator = await registerOperatorWindow(deps);
    operator.open();
    windows[0]!.bounds = { x: 5, y: 6, width: 1000, height: 700 };

    operator.dispose();
    await flush();

    expect(windows[0]!.destroyed).toBe(true);
    expect(handlers.has(CHANNEL.operatorWindow)).toBe(false);
    expect(stored).toEqual([
      ['operator.window', JSON.stringify({ x: 5, y: 6, width: 1000, height: 700 })],
    ]);
  });

  it('registers scout:operator.window and nothing else', async () => {
    const { deps, handlers } = setup();

    await registerOperatorWindow(deps);

    expect([...handlers.keys()]).toEqual(['scout:operator.window']);
  });

  it('opens at the default size and logs the code only when the stored place cannot be read', async () => {
    const { deps, windows, lines } = setup({ getFails: true });
    const operator = await registerOperatorWindow(deps);

    operator.open();

    expect(windows[0]!.bounds).toEqual({ x: 0, y: 0, width: 1400, height: 900 });
    expect(lines.some((line) => line.includes('operator window place unreadable'))).toBe(true);
    expect(lines.join('\n')).not.toContain('keychain locked');
  });

  it('keeps working and logs the code only when the place cannot be saved', async () => {
    const { deps, windows, lines } = setup({ storeFails: true });
    const operator = await registerOperatorWindow(deps);
    operator.open();

    windows[0]!.emit('close');
    await flush();

    expect(lines.some((line) => line.includes('operator window place not saved'))).toBe(true);
    expect(lines.join('\n')).not.toContain('disk full');
  });

  it("tells the operator window's renderer apart from any other, so a grant from it is refused and a decision from its Approvals page is taken, and stops owning it once the window is gone", async () => {
    const { deps, windows } = setup();
    const operator = await registerOperatorWindow(deps);
    const other = new FakeWebContents();

    expect(operator.owns(other)).toBe(false);
    operator.open();
    const contents = windows[0]!.webContents;

    expect([operator.owns(contents), operator.owns(other), operator.owns(null)]).toEqual([
      true,
      false,
      false,
    ]);
    windows[0]!.destroyed = true;
    expect(operator.owns(contents)).toBe(false);
  });
});
