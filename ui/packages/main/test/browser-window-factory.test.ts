import { describe, it, expect } from 'bun:test';
import { createMainWindow } from '../src/browser-window-factory.js';
import type { CreateMainWindowDeps } from '../src/browser-window-factory.js';
import type { BrowserWindowConstructorOptions } from 'electron';
import type { Logger } from '../src/util/logger.js';

type ListenerMap = Map<string, ((...args: unknown[]) => unknown)[]>;

class FakeBrowserWindow {
  constructorOpts: BrowserWindowConstructorOptions;
  shownCount = 0;
  loadedUrl: string | null = null;
  loadedFile: string | null = null;
  windowOpenHandlerResult: { action: string } | null = null;
  navigateBlockedUrls: string[] = [];

  private listeners: ListenerMap = new Map();
  private contentsListeners: ListenerMap = new Map();

  webContents: {
    setWindowOpenHandler: (handler: (details: { url: string }) => { action: string }) => void;
    on: (event: string, listener: (...args: unknown[]) => void) => void;
  };

  constructor(opts: BrowserWindowConstructorOptions) {
    this.constructorOpts = opts;

    this.webContents = {
      setWindowOpenHandler: (handler) => {
        this.windowOpenHandlerResult = handler({ url: 'https://example.com' });
      },
      on: (event, listener) => {
        const arr = this.contentsListeners.get(event) ?? [];
        arr.push(listener);
        this.contentsListeners.set(event, arr);
      },
    };
  }

  show() {
    this.shownCount++;
  }

  once(event: string, listener: (...args: unknown[]) => unknown) {
    const arr = this.listeners.get(event) ?? [];
    arr.push(listener);
    this.listeners.set(event, arr);
  }

  loadURL(url: string): Promise<void> {
    this.loadedUrl = url;
    return Promise.resolve();
  }

  loadFile(filePath: string): Promise<void> {
    this.loadedFile = filePath;
    return Promise.resolve();
  }

  triggerReadyToShow() {
    const listeners = this.listeners.get('ready-to-show') ?? [];
    for (const l of listeners) l();
  }

  triggerWillNavigate(url: string) {
    const fakeEvent = {
      prevented: false,
      preventDefault() {
        this.prevented = true;
      },
    };
    const listeners = this.contentsListeners.get('will-navigate') ?? [];
    for (const l of listeners) l(fakeEvent, url);
    if (fakeEvent.prevented) this.navigateBlockedUrls.push(url);
  }
}

function makeNullLogger(): Logger {
  return {
    debug() {},
    info() {},
    warn() {},
    error() {},
    child() {
      return makeNullLogger();
    },
  };
}

interface SetupResult {
  deps: CreateMainWindowDeps;
  ref: { fake: FakeBrowserWindow };
}

function setup(overrides?: Partial<CreateMainWindowDeps>): SetupResult {
  const ref = { fake: null as unknown as FakeBrowserWindow };

  const deps: CreateMainWindowDeps = {
    createWindow: (opts) => {
      ref.fake = new FakeBrowserWindow(opts);
      return ref.fake as unknown as import('electron').BrowserWindow;
    },
    logger: makeNullLogger(),
    preloadPath: '/fake/preload.cjs',
    indexHtmlPath: '/fake/index.html',
    ...overrides,
  };

  return { deps, ref };
}

describe('createMainWindow', () => {
  it('constructs window with correct hardened webPreferences', () => {
    const { deps, ref } = setup();
    createMainWindow(deps);

    const wp = ref.fake.constructorOpts.webPreferences!;
    expect(wp.contextIsolation).toBe(true);
    expect(wp.nodeIntegration).toBe(false);
    expect(wp.sandbox).toBe(true);
    expect(wp.webSecurity).toBe(true);
    expect(wp.allowRunningInsecureContent).toBe(false);
    expect(wp.experimentalFeatures).toBe(false);
  });

  it('constructs window with show:false and correct dimensions', () => {
    const { deps, ref } = setup();
    createMainWindow(deps);

    expect(ref.fake.constructorOpts.show).toBe(false);
    expect(ref.fake.constructorOpts.width).toBe(1080);
    expect(ref.fake.constructorOpts.height).toBe(720);
    expect(ref.fake.constructorOpts.minWidth).toBe(960);
    expect(ref.fake.constructorOpts.minHeight).toBe(640);
  });

  it('sets preload path in webPreferences when provided', () => {
    const { deps, ref } = setup({ preloadPath: '/my/preload.cjs' });
    createMainWindow(deps);

    expect(ref.fake.constructorOpts.webPreferences?.preload).toBe('/my/preload.cjs');
  });

  it('omits preload from webPreferences when preloadPath is undefined', () => {
    const { deps, ref } = setup({ preloadPath: undefined });
    createMainWindow(deps);

    expect(ref.fake.constructorOpts.webPreferences?.preload).toBeUndefined();
  });

  it('ready-to-show handler calls show()', () => {
    const { deps, ref } = setup();
    createMainWindow(deps);

    expect(ref.fake.shownCount).toBe(0);
    ref.fake.triggerReadyToShow();
    expect(ref.fake.shownCount).toBe(1);
  });

  it('setWindowOpenHandler returns { action: deny } for any URL', () => {
    const { deps, ref } = setup();
    createMainWindow(deps);

    expect(ref.fake.windowOpenHandlerResult).toEqual({ action: 'deny' });
  });

  it('will-navigate handler prevents default and logs a warning', () => {
    const warnedUrls: string[] = [];
    const logger = makeNullLogger();
    logger.warn = (_msg: string, ctx?: Record<string, unknown>) => {
      if (ctx?.['url'] != null) warnedUrls.push(ctx['url'] as string);
    };

    const { deps, ref } = setup({ logger });
    createMainWindow(deps);

    ref.fake.triggerWillNavigate('https://evil.example.com');

    expect(ref.fake.navigateBlockedUrls).toContain('https://evil.example.com');
    expect(warnedUrls).toContain('https://evil.example.com');
  });

  it('dev mode calls loadURL with devServerUrl', () => {
    const { deps, ref } = setup({ devServerUrl: 'http://localhost:4200' });
    createMainWindow(deps);

    expect(ref.fake.loadedUrl).toBe('http://localhost:4200');
    expect(ref.fake.loadedFile).toBeNull();
  });

  it('prod mode calls loadFile with indexHtmlPath', () => {
    const { deps, ref } = setup({ indexHtmlPath: '/app/index.html' });
    createMainWindow(deps);

    expect(ref.fake.loadedFile).toBe('/app/index.html');
    expect(ref.fake.loadedUrl).toBeNull();
  });

  it('backgroundColor is set to dark default', () => {
    const { deps, ref } = setup();
    createMainWindow(deps);

    expect(ref.fake.constructorOpts.backgroundColor).toBe('#111111');
  });
});
