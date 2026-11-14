import { expect, jest, setSystemTime } from 'bun:test';
import { EventEmitter } from 'node:events';
import { readIpcError, type LocalTurn, type WorkerEvent, type WorkerStatus } from '@scout/shared';
import type { BrowserWindow, Display, IpcMain, IpcMainInvokeEvent } from 'electron';
import { registerQuickAsk, type QuickAskDeps } from '../src/quick-ask/quick-ask.js';
import { windowBackground } from '../src/shell/theme.js';
import type { Supervisor } from '../src/worker/supervisor.js';
import { recordingLogger } from './kubernetes-fakes.js';

export type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

export class FakeWebContents extends EventEmitter {
  readonly sent: Array<{ channel: string; payload: unknown }> = [];
  destroyed = false;
  focused = false;
  focus(): void {
    this.focused = true;
  }
  isDestroyed(): boolean {
    return this.destroyed;
  }
  send(channel: string, payload: unknown): void {
    this.sent.push({ channel, payload });
  }
  setWindowOpenHandler(): void {}
}

export class FakeWindow extends EventEmitter {
  readonly webContents = new FakeWebContents();
  readonly calls: string[] = [];
  loaded: { path: string; options: { hash?: string } } | null = null;
  destroyed = false;
  visible = false;
  position: { x: number; y: number } | null = null;
  bounds = { x: 0, y: 0, width: 0, height: 0 };
  alwaysOnTopLevel: string | null = null;
  topMoves = 0;
  ignoringMouse: boolean | null = null;
  allWorkspaces: { visible: boolean; options: unknown } | null = null;
  constructor(
    readonly options: Record<string, unknown>,
    private readonly platform: NodeJS.Platform = 'linux',
  ) {
    super();
    this.alwaysOnTopLevel = options['alwaysOnTop'] === true ? 'floating' : null;
    this.bounds = {
      x: 0,
      y: 0,
      width: Number(options['width'] ?? 0),
      height: Number(options['height'] ?? 0),
    };
  }
  isDestroyed(): boolean {
    return this.destroyed;
  }
  setPosition(x: number, y: number): void {
    this.calls.push('setPosition');
    this.position = { x, y };
    this.bounds = { ...this.bounds, x, y };
  }
  setBounds(bounds: { x: number; y: number; width: number; height: number }): void {
    this.calls.push('setBounds');
    this.bounds = bounds;
  }
  getBounds(): { x: number; y: number; width: number; height: number } {
    return this.bounds;
  }
  setAlwaysOnTop(flag: boolean, level: string): void {
    this.alwaysOnTopLevel = flag ? level : null;
  }
  setIgnoreMouseEvents(ignore: boolean): void {
    this.ignoringMouse = ignore;
  }
  setVisibleOnAllWorkspaces(visible: boolean, options: unknown): void {
    this.allWorkspaces = { visible, options };
  }
  show(): void {
    this.calls.push('show');
    this.visible = true;
  }
  showInactive(): void {
    this.calls.push('showInactive');
    this.visible = true;
  }
  isVisible(): boolean {
    return this.visible;
  }
  focus(): void {
    this.calls.push('focus');
  }
  hide(): void {
    this.calls.push('hide');
    this.visible = false;
    if (this.platform === 'linux') this.alwaysOnTopLevel = null;
  }
  moveTop(): void {
    this.topMoves += 1;
  }
  destroy(): void {
    this.destroyed = true;
  }
  loadFile(path: string, options: { hash?: string }): Promise<void> {
    this.loaded = { path, options };
    return Promise.resolve();
  }
  loadURL(): Promise<void> {
    return Promise.resolve();
  }
}

export function sender(): FakeWebContents {
  return new FakeWebContents();
}

interface FakeDisplay {
  id: number;
  label: string;
  internal: boolean;
  bounds: { x: number; y: number; width: number; height: number };
  workArea: { x: number; y: number; width: number; height: number };
}

const PRIMARY: FakeDisplay = {
  id: 1,
  label: 'Built-in display',
  internal: true,
  bounds: { x: 0, y: 0, width: 1280, height: 800 },
  workArea: { x: 0, y: 32, width: 1280, height: 768 },
};
const SECONDARY: FakeDisplay = {
  id: 2,
  label: '',
  internal: false,
  bounds: { x: 1280, y: 0, width: 1920, height: 1080 },
  workArea: { x: 1280, y: 0, width: 1920, height: 1040 },
};
export const OWNER_LAPTOP: FakeDisplay = {
  id: 35,
  label: 'Built-in display',
  internal: true,
  bounds: { x: 0, y: 190, width: 1920, height: 1080 },
  workArea: { x: 0, y: 222, width: 1920, height: 1048 },
};
export const OWNER_HDMI: FakeDisplay = {
  id: 33,
  label: '',
  internal: false,
  bounds: { x: 1920, y: 0, width: 3440, height: 1440 },
  workArea: { x: 1920, y: 32, width: 3440, height: 1408 },
};
export const ON_OWNER_LAPTOP = { x: 400, y: 600 };
export const ON_OWNER_HDMI = { x: 2643, y: 1358 };
const ON_PRIMARY = { x: 100, y: 100 };
export const ON_SECONDARY = { x: 1500, y: 500 };

const teardowns: Array<() => Promise<void>> = [];

async function stopWithin(worker: Supervisor, ms: number): Promise<void> {
  let bound: ReturnType<typeof setTimeout> | undefined;
  await Promise.race([
    worker.stop(),
    new Promise<void>((resolve) => (bound = setTimeout(resolve, ms))),
  ]);
  clearTimeout(bound);
}

/** Restores real timers and the clock, then disposes every quick ask `setup()` registered since the last call and stops the worker it was given; each test file runs it in `afterEach`. */
export async function tearDown(): Promise<void> {
  jest.useRealTimers();
  setSystemTime();
  for (const teardown of teardowns.splice(0).reverse()) await teardown();
}

/** Registers a quick ask over fakes; `tearDown` disposes it after the test, whether or not the test passed. */
export async function setup(
  options: {
    state?: WorkerStatus['state'];
    taken?: string[];
    stored?: string;
    writable?: boolean;
    vaultFails?: 'get' | 'store';
    bubble?: string;
    windowBounds?: string;
    cursor?: { x: number; y: number };
    ownerLayout?: boolean;
    platform?: NodeJS.Platform;
    env?: Record<string, string>;
    execPath?: string;
    locale?: string;
    saveTo?: string | null;
    writeFails?: boolean;
    worker?: Supervisor;
    onListeningChanged?: (listening: boolean) => void;
    onAnsweringChanged?: (answering: boolean) => void;
    restarting?: () => boolean;
    onTurnDone?: (turn: LocalTurn) => void;
    supervisor?: Supervisor;
    createWindowFails?: (options: Record<string, unknown>) => boolean;
  } = {},
) {
  const theme = { shouldUseDarkColors: true };
  const handlers = new Map<string, IpcHandler>();
  const ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'> = {
    handle(channel: string, handler: IpcHandler) {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string) {
      handlers.delete(channel);
    },
  };
  const listeners = new Set<(e: WorkerEvent) => void>();
  const written: string[] = [];
  const writeErrors: Array<(err: Error) => void> = [];
  const supervisor = {
    status: { state: options.state ?? 'running', restarts: 0 } as WorkerStatus,
    writeStdin(line: string, onError: (err: Error) => void): boolean {
      if (supervisor.status.state !== 'running' || options.writable === false) return false;
      written.push(line);
      writeErrors.push(onError);
      return true;
    },
    onEvent(listener: (e: WorkerEvent) => void) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
  const registered = new Set<string>();
  const callbacks = new Map<string, () => void>();
  const taken = new Set(options.taken ?? []);
  const globalShortcut = {
    register(accelerator: string, callback: () => void): boolean {
      if (accelerator.endsWith('+')) throw new Error('conversion failed');
      if (taken.has(accelerator) || registered.has(accelerator)) return false;
      registered.add(accelerator);
      callbacks.set(accelerator, callback);
      return true;
    },
    unregister(accelerator: string): void {
      registered.delete(accelerator);
      callbacks.delete(accelerator);
    },
  };
  const entries = new Map<string, string>([
    ...(options.stored !== undefined ? [['quick-ask.shortcut', options.stored] as const] : []),
    ...(options.bubble !== undefined ? [['quick-ask.bubble', options.bubble] as const] : []),
    ...(options.windowBounds !== undefined
      ? [['quick-ask.window', options.windowBounds] as const]
      : []),
  ]);
  const screenCalls: string[] = [];
  const displays: FakeDisplay[] =
    options.ownerLayout === true ? [OWNER_LAPTOP, OWNER_HDMI] : [PRIMARY, SECONDARY];
  const primaryId = options.ownerLayout === true ? OWNER_HDMI.id : PRIMARY.id;
  let cursor = options.cursor ?? (options.ownerLayout === true ? ON_OWNER_HDMI : ON_PRIMARY);
  const distance = ({ bounds: b }: FakeDisplay, p: { x: number; y: number }) =>
    Math.hypot(
      Math.max(b.x - p.x, 0, p.x - (b.x + b.width - 1)),
      Math.max(b.y - p.y, 0, p.y - (b.y + b.height - 1)),
    );
  const screen = Object.assign(new EventEmitter(), {
    getCursorScreenPoint: () => {
      screenCalls.push('getCursorScreenPoint');
      return cursor;
    },
    getDisplayNearestPoint: (point: { x: number; y: number }) => {
      screenCalls.push('getDisplayNearestPoint');
      return displays.reduce((best, d) =>
        distance(d, point) < distance(best, point) ? d : best,
      ) as Display;
    },
    getPrimaryDisplay: () => {
      screenCalls.push('getPrimaryDisplay');
      return (displays.find((d) => d.id === primaryId) ?? displays[0]!) as Display;
    },
    getAllDisplays: () => [...displays] as Display[],
  });
  const timers: Array<{ ms: number; run: () => void }> = [];
  const setTimer = (ms: number, run: () => void) => {
    const timer = { ms, run };
    timers.push(timer);
    return () => {
      const index = timers.indexOf(timer);
      if (index !== -1) timers.splice(index, 1);
    };
  };
  function tick(times = 1): void {
    for (let i = 0; i < times; i += 1) {
      for (const timer of timers.splice(0)) timer.run();
    }
  }
  const windows: FakeWindow[] = [];
  const mainWindow = new FakeWindow({});
  const { logger, lines } = recordingLogger();
  const dialogs: Array<{ defaultPath: string; filters: unknown; properties: unknown }> = [];
  const files: Array<{ path: string; text: string }> = [];
  const deps: QuickAskDeps = {
    ipcMain,
    supervisor: options.supervisor ?? options.worker ?? supervisor,
    createWindow: (opts) => {
      if (options.createWindowFails?.(opts as Record<string, unknown>) === true) {
        throw new Error('the window could not be created');
      }
      const window = new FakeWindow(opts as Record<string, unknown>, options.platform);
      windows.push(window);
      return window as unknown as BrowserWindow;
    },
    preloadPath: '/app/preload.cjs',
    indexHtmlPath: '/app/renderer/index.html',
    globalShortcut,
    vault: {
      get: async (key) => {
        if (options.vaultFails === 'get') throw new Error('keyring locked');
        return entries.get(key) ?? null;
      },
      store: async (key, value) => {
        if (options.vaultFails === 'store') throw new Error('keyring locked');
        entries.set(key, value);
      },
    },
    logger,
    screen: screen as unknown as QuickAskDeps['screen'],
    setTimer,
    platform: options.platform ?? 'linux',
    env: options.env ?? {},
    execPath: options.execPath ?? '/opt/Scout/scout-desktop',
    windows: () => [mainWindow, ...windows] as unknown as BrowserWindow[],
    locale: () => options.locale ?? 'en-US',
    showSaveDialog: async (dialogOptions) => {
      dialogs.push(dialogOptions);
      const chosen = options.saveTo === undefined ? '/home/owner/Notes/standup.md' : options.saveTo;
      return chosen === null ? { canceled: true } : { canceled: false, filePath: chosen };
    },
    writeFile: async (file, text) => {
      if (options.writeFails === true) throw Object.assign(new Error('EACCES'), { code: 'EACCES' });
      files.push({ path: file, text });
    },
    documentsDir: () => '/home/owner/Documents',
    background: () => windowBackground(theme),
    ...(options.onListeningChanged === undefined
      ? {}
      : { onListeningChanged: options.onListeningChanged }),
    ...(options.onAnsweringChanged === undefined
      ? {}
      : { onAnsweringChanged: options.onAnsweringChanged }),
    ...(options.restarting === undefined ? {} : { restarting: options.restarting }),
    ...(options.onTurnDone === undefined ? {} : { onTurnDone: options.onTurnDone }),
  };
  const quickAsk = await registerQuickAsk(deps);
  const given = options.supervisor ?? options.worker;
  teardowns.push(async () => {
    quickAsk.dispose();
    if (given !== undefined) await stopWithin(given, 1_000);
  });

  async function invoke(channel: string, from: FakeWebContents, req?: unknown): Promise<unknown> {
    const handler = handlers.get(channel);
    if (handler === undefined) throw new Error(`no handler for ${channel}`);
    return handler({ sender: from } as unknown as IpcMainInvokeEvent, req);
  }

  async function rejection(promise: Promise<unknown>): Promise<string> {
    return (await rejectionError(promise)).message;
  }

  async function rejectionError(
    promise: Promise<unknown>,
  ): Promise<{ code: string | null; message: string }> {
    try {
      await promise;
    } catch (err: unknown) {
      return readIpcError(err);
    }
    throw new Error('expected a rejection');
  }

  const routed = (hash: string) => windows.filter((w) => w.loaded?.options.hash === hash);

  return {
    theme,
    quickAsk,
    handlers,
    supervisor,
    written,
    writeErrors,
    registered,
    pressShortcut: (accelerator: string) => callbacks.get(accelerator)?.(),
    globalShortcut,
    entries,
    windows,
    mainWindow,
    invoke,
    rejection,
    rejectionError,
    screenCalls,
    screen,
    displays,
    timers,
    tick,
    moveCursor: (point: { x: number; y: number }) => {
      cursor = point;
    },
    lines,
    dialogs,
    files,
    quickAskWindows: () => routed('/quick-ask'),
    quickAskWindow: () => routed('/quick-ask')[0]!,
    bubbles: () => routed('/floating-button'),
    bubble: () => routed('/floating-button').at(-1)!,
    cards: () => routed('/floating-card'),
    emitWorker: (e: WorkerEvent) => listeners.forEach((l) => l(e)),
  };
}

export function turn(
  id: string,
  question: string,
  changes: Partial<{
    reply: string;
    citations: unknown[];
    state: string;
    notRunning: boolean;
    activity: string;
    failure: string;
  }> = {},
) {
  return {
    id,
    question,
    reply: '',
    citations: [],
    state: 'running',
    askedAt: expect.any(Number),
    ...changes,
  };
}
