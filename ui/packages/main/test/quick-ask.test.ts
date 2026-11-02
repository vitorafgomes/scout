import { describe, expect, it } from 'bun:test';
import { EventEmitter } from 'node:events';
import { pathToFileURL } from 'node:url';
import { CHANNEL, readIpcError, type WorkerEvent, type WorkerStatus } from '@scout/shared';
import type { BrowserWindow, Display, IpcMain, IpcMainInvokeEvent, Session } from 'electron';
import {
  DEFAULT_QUICK_ASK_SHORTCUT,
  allowMicrophoneOnlyForApp,
  openQuickAsk,
  registerQuickAsk,
  type QuickAskDeps,
} from '../src/quick-ask/quick-ask.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

class FakeWebContents extends EventEmitter {
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

class FakeWindow extends EventEmitter {
  readonly webContents = new FakeWebContents();
  readonly calls: string[] = [];
  loaded: { path: string; options: { hash?: string } } | null = null;
  destroyed = false;
  position: { x: number; y: number } | null = null;
  bounds = { x: 0, y: 0, width: 0, height: 0 };
  alwaysOnTopLevel: string | null = null;
  allWorkspaces: { visible: boolean; options: unknown } | null = null;
  constructor(readonly options: Record<string, unknown>) {
    super();
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
  setVisibleOnAllWorkspaces(visible: boolean, options: unknown): void {
    this.allWorkspaces = { visible, options };
  }
  show(): void {
    this.calls.push('show');
  }
  showInactive(): void {
    this.calls.push('showInactive');
  }
  focus(): void {
    this.calls.push('focus');
  }
  hide(): void {
    this.calls.push('hide');
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

function sender(): FakeWebContents {
  return new FakeWebContents();
}

const PRIMARY = {
  bounds: { x: 0, y: 0, width: 1280, height: 800 },
  workArea: { x: 0, y: 32, width: 1280, height: 768 },
};
const SECONDARY = {
  bounds: { x: 1280, y: 0, width: 1920, height: 1080 },
  workArea: { x: 1280, y: 0, width: 1920, height: 1040 },
};
const ON_PRIMARY = { x: 100, y: 100 };
const ON_SECONDARY = { x: 1500, y: 500 };

async function setup(
  options: {
    state?: WorkerStatus['state'];
    taken?: string[];
    stored?: string;
    writable?: boolean;
    vaultFails?: 'get' | 'store';
    bubble?: string;
    windowBounds?: string;
    cursor?: { x: number; y: number };
    platform?: NodeJS.Platform;
    env?: Record<string, string>;
    execPath?: string;
  } = {},
) {
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
  const taken = new Set(options.taken ?? []);
  const globalShortcut = {
    register(accelerator: string): boolean {
      if (accelerator.endsWith('+')) throw new Error('conversion failed');
      if (taken.has(accelerator)) return false;
      registered.add(accelerator);
      return true;
    },
    unregister(accelerator: string): void {
      registered.delete(accelerator);
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
  const contains = (d: typeof PRIMARY, p: { x: number; y: number }) =>
    p.x >= d.bounds.x &&
    p.x < d.bounds.x + d.bounds.width &&
    p.y >= d.bounds.y &&
    p.y < d.bounds.y + d.bounds.height;
  const screen: QuickAskDeps['screen'] = {
    getCursorScreenPoint: () => {
      screenCalls.push('getCursorScreenPoint');
      return options.cursor ?? ON_PRIMARY;
    },
    getDisplayNearestPoint: (point) => {
      screenCalls.push('getDisplayNearestPoint');
      return (contains(SECONDARY, point) ? SECONDARY : PRIMARY) as Display;
    },
    getPrimaryDisplay: () => {
      screenCalls.push('getPrimaryDisplay');
      return PRIMARY as Display;
    },
    getAllDisplays: () => [PRIMARY, SECONDARY] as Display[],
  };
  const windows: FakeWindow[] = [];
  const mainWindow = new FakeWindow({});
  const { logger, lines } = recordingLogger();
  const deps: QuickAskDeps = {
    ipcMain,
    supervisor,
    createWindow: (opts) => {
      const window = new FakeWindow(opts as Record<string, unknown>);
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
    screen,
    platform: options.platform ?? 'linux',
    env: options.env ?? {},
    execPath: options.execPath ?? '/opt/Scout/scout-desktop',
    windows: () => [mainWindow, ...windows] as unknown as BrowserWindow[],
  };
  const quickAsk = await registerQuickAsk(deps);

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
    quickAsk,
    handlers,
    supervisor,
    written,
    writeErrors,
    registered,
    entries,
    windows,
    mainWindow,
    invoke,
    rejection,
    rejectionError,
    screenCalls,
    lines,
    quickAskWindows: () => routed('/quick-ask'),
    quickAskWindow: () => routed('/quick-ask')[0]!,
    bubbles: () => routed('/floating-button'),
    bubble: () => routed('/floating-button').at(-1)!,
    emitWorker: (e: WorkerEvent) => listeners.forEach((l) => l(e)),
  };
}

function pushedTurns(window: FakeWindow): unknown[] {
  return window.webContents.sent
    .filter((s) => s.channel === CHANNEL.localTurns)
    .map((s) => s.payload);
}

function turn(
  id: string,
  question: string,
  changes: Partial<{
    reply: string;
    citations: unknown[];
    state: string;
    notRunning: boolean;
  }> = {},
) {
  return { id, question, reply: '', citations: [], state: 'running', ...changes };
}

describe('quick ask: questions to the worker', () => {
  it('writes a typed question to the worker stdin as one ask line', async () => {
    const t = await setup();

    await t.invoke(CHANNEL.localAsk, sender(), {
      id: 'q-1',
      text: '  is the nightly build green?  ',
    });

    expect(t.written).toEqual(['{"type":"ask","id":"q-1","text":"is the nightly build green?"}']);
    t.quickAsk.dispose();
  });

  it('writes a recording to the worker stdin as one audio/wav ask line', async () => {
    const t = await setup();

    await t.invoke(CHANNEL.localAsk, sender(), {
      id: 'v-1',
      audio: { contentType: 'audio/wav', base64: 'UklGRg==' },
    });

    expect(t.written).toEqual([
      '{"type":"ask","id":"v-1","audio":{"contentType":"audio/wav","base64":"UklGRg=="}}',
    ]);
    t.quickAsk.dispose();
  });

  it('refuses with a clear message when the worker is not running, and writes nothing', async () => {
    const t = await setup({ state: 'stopped' });

    const message = await t.rejection(
      t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'hi' }),
    );

    expect(message).toBe('Scout is not running. Start it from the tray or the app.');
    expect(t.written).toEqual([]);
    expect(await t.invoke(CHANNEL.localGetTurns, sender())).toEqual([
      turn('q-1', 'hi', {
        reply: 'Scout is not running. Start it from the tray or the app.',
        state: 'failed',
        notRunning: true,
      }),
    ]);
    t.quickAsk.dispose();
  });

  it.each([
    [{ id: 'bad id!', text: 'hi' }],
    [{ id: 'q-1', text: '   ' }],
    [{ id: 'q-1', text: 'x'.repeat(4001) }],
    [{ id: 'q-1', audio: { contentType: 'audio/mpeg', base64: 'AAAA' } }],
    [{ id: 'q-1', audio: { contentType: 'audio/wav', base64: 'not base64!' } }],
    [{ id: 'q-1', audio: { contentType: 'audio/wav', base64: 'A'.repeat(14_000_004) } }],
    [undefined],
  ])('rejects a malformed question %p before it reaches the worker', async (req) => {
    const t = await setup();

    await t.rejection(t.invoke(CHANNEL.localAsk, sender(), req));

    expect(t.written).toEqual([]);
    t.quickAsk.dispose();
  });

  it('writes a cancel line only for a question that is still running', async () => {
    const t = await setup();
    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'hi' });

    await t.invoke(CHANNEL.localCancel, sender(), { id: 'q-1' });
    await t.invoke(CHANNEL.localCancel, sender(), { id: 'q-unknown' });

    expect(t.written.slice(1)).toEqual(['{"type":"cancel","id":"q-1"}']);
    t.quickAsk.dispose();
  });
});

describe('quick ask: one conversation for every window', () => {
  it('a question asked from one window reaches every window as the same conversation', async () => {
    const t = await setup();
    openQuickAsk();
    const quickAskWindow = t.quickAskWindow();

    await t.invoke(CHANNEL.localAsk, quickAskWindow.webContents, {
      id: 'q-1',
      text: 'is prod green?',
    });
    t.quickAsk.handleWorkerLine('{"id":"q-1","event":"update","text":"Look"}');

    const expected = [turn('q-1', 'is prod green?', { reply: 'Look' })];
    expect(pushedTurns(t.mainWindow).at(-1)).toEqual(expected);
    expect(pushedTurns(quickAskWindow).at(-1)).toEqual(expected);
    expect(await t.invoke(CHANNEL.localGetTurns, t.mainWindow.webContents)).toEqual(expected);
    t.quickAsk.dispose();
  });

  it('a recording becomes a "(voice message)" turn', async () => {
    const t = await setup();

    await t.invoke(CHANNEL.localAsk, sender(), {
      id: 'v-1',
      audio: { contentType: 'audio/wav', base64: 'UklGRg==' },
    });

    expect(pushedTurns(t.mainWindow).at(-1)).toEqual([turn('v-1', '(voice message)')]);
    t.quickAsk.dispose();
  });

  it('update streams the reply, complete adds the citations and ends the turn, fail ends it as failed', async () => {
    const t = await setup();
    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'first' });
    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-2', text: 'second' });

    t.quickAsk.handleWorkerLine('{"id":"q-1","event":"begin"}');
    t.quickAsk.handleWorkerLine('{"id":"q-1","event":"update","text":"Gr"}');
    const streaming = pushedTurns(t.mainWindow).at(-1);
    t.quickAsk.handleWorkerLine(
      '{"id":"q-1","event":"complete","text":"Green.","citations":[{"label":"run 42","url":"https://github.com/acme/ci/actions/runs/42"}]}',
    );
    t.quickAsk.handleWorkerLine('{"id":"q-2","event":"fail","text":"Cancelled."}');

    expect(streaming).toEqual([turn('q-1', 'first', { reply: 'Gr' }), turn('q-2', 'second')]);
    expect(pushedTurns(t.mainWindow).at(-1)).toEqual([
      turn('q-1', 'first', {
        reply: 'Green.',
        citations: [{ label: 'run 42', url: 'https://github.com/acme/ci/actions/runs/42' }],
        state: 'done',
      }),
      turn('q-2', 'second', { reply: 'Cancelled.', state: 'failed' }),
    ]);
    t.quickAsk.dispose();
  });

  it('drops events for unknown ids, for finished questions and lines that are not events', async () => {
    const t = await setup();
    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'hi' });
    t.quickAsk.handleWorkerLine('{"id":"q-1","event":"complete","text":"done"}');
    const pushes = pushedTurns(t.mainWindow).length;

    t.quickAsk.handleWorkerLine('{"id":"q-1","event":"update","text":"late"}');
    t.quickAsk.handleWorkerLine('{"id":"someone-else","event":"complete","text":"forged"}');
    t.quickAsk.handleWorkerLine('{"id":"q-1","event":"exec","text":"x"}');
    t.quickAsk.handleWorkerLine('not json');

    expect(pushedTurns(t.mainWindow)).toHaveLength(pushes);
    expect(await t.invoke(CHANNEL.localGetTurns, sender())).toEqual([
      turn('q-1', 'hi', { reply: 'done', state: 'done' }),
    ]);
    t.quickAsk.dispose();
  });

  it('refuses an id already in the conversation, even a finished one', async () => {
    const t = await setup();
    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'hi' });
    t.quickAsk.handleWorkerLine('{"id":"q-1","event":"complete","text":"done"}');

    const error = await t.rejectionError(
      t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'again' }),
    );

    expect(error.code).toBe('local/invalid-request');
    expect(t.written).toHaveLength(1);
    t.quickAsk.dispose();
  });

  it('a failed stdin write ends the turn with "Scout stopped before it finished answering."', async () => {
    const t = await setup();
    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'hi' });

    t.writeErrors[0]?.(new Error('write EPIPE'));

    expect(pushedTurns(t.mainWindow).at(-1)).toEqual([
      turn('q-1', 'hi', { reply: 'Scout stopped before it finished answering.', state: 'failed' }),
    ]);
    t.quickAsk.dispose();
  });

  it('a worker exit mid-answer ends every running turn and later events are dropped', async () => {
    const t = await setup();
    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'one' });
    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-2', text: 'two' });
    t.quickAsk.handleWorkerLine('{"id":"q-1","event":"update","text":"Half"}');

    t.emitWorker({ kind: 'exit', code: 137, signal: 'SIGKILL' });
    t.quickAsk.handleWorkerLine('{"id":"q-1","event":"complete","text":"too late"}');

    expect(await t.invoke(CHANNEL.localGetTurns, sender())).toEqual([
      turn('q-1', 'one', { reply: 'Scout stopped before it finished answering.', state: 'failed' }),
      turn('q-2', 'two', { reply: 'Scout stopped before it finished answering.', state: 'failed' }),
    ]);
    t.quickAsk.dispose();
  });

  it('clearTurns refuses while a turn is running, and empties the conversation in every window once none is', async () => {
    const t = await setup();
    openQuickAsk();
    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'hi' });

    await t.invoke(CHANNEL.localClearTurns, sender());
    const whileRunning = await t.invoke(CHANNEL.localGetTurns, sender());
    t.quickAsk.handleWorkerLine('{"id":"q-1","event":"complete","text":"done"}');
    await t.invoke(CHANNEL.localClearTurns, sender());

    expect(whileRunning).toEqual([turn('q-1', 'hi')]);
    expect(await t.invoke(CHANNEL.localGetTurns, sender())).toEqual([]);
    expect(pushedTurns(t.mainWindow).at(-1)).toEqual([]);
    expect(pushedTurns(t.quickAskWindow()).at(-1)).toEqual([]);
    t.quickAsk.dispose();
  });

  it('keeps the last 200 turns, dropping the oldest', async () => {
    const t = await setup();

    for (let i = 1; i <= 201; i++) {
      await t.invoke(CHANNEL.localAsk, sender(), { id: `q-${i}`, text: `question ${i}` });
      t.quickAsk.handleWorkerLine(`{"id":"q-${i}","event":"complete","text":"answer ${i}"}`);
    }

    const turns = (await t.invoke(CHANNEL.localGetTurns, sender())) as { id: string }[];
    expect(turns).toHaveLength(200);
    expect(turns[0]?.id).toBe('q-2');
    expect(turns.at(-1)?.id).toBe('q-201');
    t.quickAsk.dispose();
  });

  it('skips a window that has been destroyed', async () => {
    const t = await setup();
    t.mainWindow.destroyed = true;

    await t.invoke(CHANNEL.localAsk, sender(), { id: 'q-1', text: 'hi' });

    expect(pushedTurns(t.mainWindow)).toEqual([]);
    expect(pushedTurns(t.bubble()).at(-1)).toEqual([turn('q-1', 'hi')]);
    t.quickAsk.dispose();
  });
});

describe('quick ask: window and shortcut', () => {
  it('registers Ctrl/Cmd+Alt+Space at startup, and opens a frameless always-on-top window on the quick-ask route', async () => {
    const t = await setup();

    openQuickAsk();

    expect(t.registered.has(DEFAULT_QUICK_ASK_SHORTCUT)).toBe(true);
    const window = t.quickAskWindow();
    expect(window.options).toMatchObject({
      frame: false,
      alwaysOnTop: true,
      show: false,
      webPreferences: { contextIsolation: true, nodeIntegration: false, sandbox: true },
    });
    expect(window.loaded).toEqual({
      path: '/app/renderer/index.html',
      options: { hash: '/quick-ask' },
    });
    expect(window.calls).toEqual(['setPosition', 'show', 'focus']);
    t.quickAsk.dispose();
  });

  it('hides the window on blur and on scout:local.hide, and reuses it on the next open', async () => {
    const t = await setup();
    openQuickAsk();
    const window = t.quickAskWindow();

    window.emit('blur');
    openQuickAsk();
    await t.invoke(CHANNEL.localHide, sender());

    expect(t.quickAskWindows()).toHaveLength(1);
    expect(window.calls).toEqual(['setPosition', 'show', 'focus', 'hide', 'show', 'focus', 'hide']);
    t.quickAsk.dispose();
  });

  it.each(['linux', 'darwin', 'win32'] as const)(
    'on %s the quick-ask window keeps the default focusable window, so the window manager gives it the keyboard and the blur that hides it',
    async (platform) => {
      const t = await setup({ platform });

      openQuickAsk();

      const window = t.quickAskWindow();
      expect(window.options).not.toHaveProperty('focusable');
      expect(window.calls).toEqual(['setPosition', 'show', 'focus']);
      expect(window.webContents.focused).toBe(true);
      t.quickAsk.dispose();
    },
  );

  it('uses the stored shortcut at startup', async () => {
    const t = await setup({ stored: 'Super+K' });

    expect(t.registered).toEqual(new Set(['Super+K']));
    t.quickAsk.dispose();
  });

  it('keeps the old shortcut and says "That shortcut is taken" when the new one cannot be registered', async () => {
    const t = await setup({ taken: ['CommandOrControl+Shift+K'] });

    const message = await t.rejection(
      t.invoke(CHANNEL.localSetShortcut, sender(), { accelerator: 'CommandOrControl+Shift+K' }),
    );

    expect(message).toBe('That shortcut is taken');
    expect(t.registered).toEqual(new Set([DEFAULT_QUICK_ASK_SHORTCUT]));
    expect(await t.invoke(CHANNEL.localGetShortcut, sender())).toEqual({
      accelerator: DEFAULT_QUICK_ASK_SHORTCUT,
      registered: true,
      hint: null,
    });
    expect(t.entries.has('quick-ask.shortcut')).toBe(false);
    t.quickAsk.dispose();
  });

  it('keeps the old shortcut when the new one is not a valid accelerator', async () => {
    const t = await setup();

    await t.rejection(t.invoke(CHANNEL.localSetShortcut, sender(), { accelerator: 'Ctrl+' }));

    expect(t.registered).toEqual(new Set([DEFAULT_QUICK_ASK_SHORTCUT]));
    t.quickAsk.dispose();
  });

  it('moves to a free shortcut, releases the old one and remembers the new one', async () => {
    const t = await setup();

    const result = await t.invoke(CHANNEL.localSetShortcut, sender(), {
      accelerator: 'Alt+Shift+S',
    });

    expect(result).toEqual({ accelerator: 'Alt+Shift+S', registered: true, hint: null });
    expect(t.registered).toEqual(new Set(['Alt+Shift+S']));
    expect(t.entries.get('quick-ask.shortcut')).toBe('Alt+Shift+S');
    t.quickAsk.dispose();
  });

  it('reports the shortcut as not registered when another app holds it at startup', async () => {
    const t = await setup({ taken: [DEFAULT_QUICK_ASK_SHORTCUT] });

    expect(await t.invoke(CHANNEL.localGetShortcut, sender())).toEqual({
      accelerator: DEFAULT_QUICK_ASK_SHORTCUT,
      registered: false,
      hint: null,
    });
    t.quickAsk.dispose();
  });

  it('registers the default shortcut when the stored one cannot be read', async () => {
    const t = await setup({ vaultFails: 'get' });

    expect(t.registered).toEqual(new Set([DEFAULT_QUICK_ASK_SHORTCUT]));
    t.quickAsk.dispose();
  });

  it('still moves to the new shortcut when it cannot be saved', async () => {
    const t = await setup({ vaultFails: 'store' });

    const result = await t.invoke(CHANNEL.localSetShortcut, sender(), {
      accelerator: 'Alt+Shift+S',
    });

    expect(result).toEqual({ accelerator: 'Alt+Shift+S', registered: true, hint: null });
    expect(t.registered).toEqual(new Set(['Alt+Shift+S']));
    t.quickAsk.dispose();
  });

  it('unregisters the shortcut and its IPC handlers on dispose', async () => {
    const t = await setup();

    t.quickAsk.dispose();

    expect(t.registered.size).toBe(0);
    expect(t.handlers.has(CHANNEL.localAsk)).toBe(false);
    expect(t.handlers.has(CHANNEL.localGetTurns)).toBe(false);
    expect(t.handlers.has(CHANNEL.localClearTurns)).toBe(false);
    expect(t.handlers.has(CHANNEL.localSetShortcut)).toBe(false);
  });

  it('the quick-ask window can be resized and moved', async () => {
    const t = await setup();

    openQuickAsk();

    const options = t.quickAskWindow().options;
    expect(options).toMatchObject({ frame: false, resizable: true, minWidth: 360, minHeight: 240 });
    expect(options).not.toHaveProperty('movable');
    t.quickAsk.dispose();
  });

  it('a moved or resized quick-ask window reopens where it was left', async () => {
    const left = { x: 300, y: 200, width: 700, height: 500 };
    const first = await setup();
    openQuickAsk();
    first.quickAskWindow().bounds = left;

    first.quickAskWindow().emit('blur');
    await Promise.resolve();
    const stored = first.entries.get('quick-ask.window');
    first.quickAsk.dispose();

    expect(JSON.parse(stored ?? 'null')).toEqual(left);
    const next = await setup({ windowBounds: stored! });
    openQuickAsk();
    expect(next.quickAskWindow().bounds).toEqual(left);
    expect(next.quickAskWindow().calls).toEqual(['setBounds', 'show', 'focus']);
    next.quickAsk.dispose();
  });

  it('a quick-ask window closed by the window manager is remembered where it was left', async () => {
    const left = { x: 1400, y: 300, width: 800, height: 600 };
    const t = await setup();
    openQuickAsk();
    t.quickAskWindow().bounds = left;

    t.quickAskWindow().emit('close');
    await Promise.resolve();

    expect(JSON.parse(t.entries.get('quick-ask.window') ?? 'null')).toEqual(left);
    t.quickAsk.dispose();
  });

  it('stored bounds that lie on no current display fall back to the corner', async () => {
    const t = await setup({
      windowBounds: JSON.stringify({ x: -5000, y: 100, width: 700, height: 500 }),
    });

    openQuickAsk();

    const window = t.quickAskWindow();
    expect(window.calls).toEqual(['setPosition', 'show', 'focus']);
    expect(window.position).toEqual({ x: 624, y: 364 });
    t.quickAsk.dispose();
  });

  it.each(['not json', '{"x":10,"y":"top","width":700,"height":500}', '{"x":10,"y":40}'])(
    'ignores stored bounds %p and places the window in the corner',
    async (raw) => {
      const t = await setup({ windowBounds: raw });

      openQuickAsk();

      expect(t.quickAskWindow().calls).toEqual(['setPosition', 'show', 'focus']);
      expect(t.quickAskWindow().position).toEqual({ x: 624, y: 364 });
      t.quickAsk.dispose();
    },
  );

  it('still hides the window and warns when its place cannot be saved', async () => {
    const t = await setup({ vaultFails: 'store' });
    openQuickAsk();

    t.quickAskWindow().emit('blur');
    await Promise.resolve();
    await Promise.resolve();

    expect(t.quickAskWindow().calls.at(-1)).toBe('hide');
    expect(t.lines.some((l) => l.includes('quick-ask window place not saved'))).toBe(true);
    t.quickAsk.dispose();
  });
});

describe('quick ask: floating button', () => {
  const stored = (corner: string, display = 'cursor', enabled = true) =>
    JSON.stringify({ enabled, corner, display });

  it('floating button: creates a 64px transparent always-on-top non-focusable window at the stored corner', async () => {
    const t = await setup({ bubble: stored('top-left') });

    const bubble = t.bubble();
    expect(t.bubbles()).toHaveLength(1);
    expect(bubble.options).toMatchObject({
      width: 64,
      height: 64,
      show: false,
      frame: false,
      transparent: true,
      alwaysOnTop: true,
      focusable: false,
      skipTaskbar: true,
      resizable: false,
      hasShadow: false,
      acceptFirstMouse: true,
      webPreferences: { contextIsolation: true, nodeIntegration: false, sandbox: true },
    });
    expect(bubble.loaded).toEqual({
      path: '/app/renderer/index.html',
      options: { hash: '/floating-button' },
    });
    expect(bubble.position).toEqual({ x: 16, y: 48 });
    expect(bubble.calls).toEqual(['setPosition', 'showInactive']);
    t.quickAsk.dispose();
  });

  it('shows the floating button bottom-right when nothing is stored', async () => {
    const t = await setup();

    expect(t.bubbles()).toHaveLength(1);
    expect(t.bubble().position).toEqual({ x: 1200, y: 720 });
    t.quickAsk.dispose();
  });

  it('does not create the floating button when the stored settings turn it off', async () => {
    const t = await setup({ bubble: stored('bottom-right', 'cursor', false) });

    expect(t.bubbles()).toHaveLength(0);
    t.quickAsk.dispose();
  });

  it.each(['{"enabled":true,"corner":"middle","display":"cursor"}', 'not json'])(
    'falls back to the default and warns when the stored settings are %p',
    async (raw) => {
      const t = await setup({ bubble: raw });

      expect(t.bubble().position).toEqual({ x: 1200, y: 720 });
      expect(t.lines.some((l) => l.includes('stored floating button settings unreadable'))).toBe(
        true,
      );
      t.quickAsk.dispose();
    },
  );

  it('floating button: localOpen shows the quick-ask window in the same corner and hides the bubble', async () => {
    const t = await setup({ cursor: ON_SECONDARY });

    await t.invoke(CHANNEL.localOpen, sender());

    const window = t.quickAskWindow();
    expect(window.position).toEqual({ x: 2544, y: 604 });
    expect(window.calls).toEqual(['setPosition', 'show', 'focus']);
    expect(t.bubble().calls.at(-1)).toBe('hide');
    t.quickAsk.dispose();
  });

  it.each(['blur', 'scout:local.hide'])(
    'hiding the quick-ask window on %s shows the bubble again',
    async (how) => {
      const t = await setup();
      await t.invoke(CHANNEL.localOpen, sender());

      if (how === 'blur') t.quickAskWindow().emit('blur');
      else await t.invoke(CHANNEL.localHide, sender());

      expect(t.quickAskWindow().calls.at(-1)).toBe('hide');
      expect(t.bubble().calls).toEqual([
        'setPosition',
        'showInactive',
        'hide',
        'setPosition',
        'showInactive',
      ]);
      t.quickAsk.dispose();
    },
  );

  it('closing the quick-ask window from its frame shows the bubble again', async () => {
    const t = await setup();
    await t.invoke(CHANNEL.localOpen, sender());
    expect(t.bubble().calls.at(-1)).toBe('hide');

    t.quickAskWindow().emit('closed');

    expect(t.bubble().calls.slice(-2)).toEqual(['setPosition', 'showInactive']);
    t.quickAsk.dispose();
  });

  it('localSetBubble repositions the bubble to the new corner', async () => {
    const t = await setup();

    const result = await t.invoke(CHANNEL.localSetBubble, sender(), {
      enabled: true,
      corner: 'top-left',
      display: 'cursor',
    });

    expect(result).toEqual({ enabled: true, corner: 'top-left', display: 'cursor' });
    expect(t.bubbles()).toHaveLength(1);
    expect(t.bubble().position).toEqual({ x: 16, y: 48 });
    expect(JSON.parse(t.entries.get('quick-ask.bubble')!)).toEqual({
      enabled: true,
      corner: 'top-left',
      display: 'cursor',
    });
    expect(await t.invoke(CHANNEL.localGetBubble, sender())).toEqual({
      enabled: true,
      corner: 'top-left',
      display: 'cursor',
    });
    t.quickAsk.dispose();
  });

  it('still moves the bubble when the settings cannot be saved, and warns', async () => {
    const t = await setup({ vaultFails: 'store' });

    const result = await t.invoke(CHANNEL.localSetBubble, sender(), {
      enabled: true,
      corner: 'top-right',
      display: 'cursor',
    });

    expect(result).toEqual({ enabled: true, corner: 'top-right', display: 'cursor' });
    expect(t.bubble().position).toEqual({ x: 1200, y: 48 });
    expect(t.lines.some((l) => l.includes('floating button settings not saved'))).toBe(true);
    t.quickAsk.dispose();
  });

  it('shows the floating button bottom-right when the stored settings cannot be read', async () => {
    const t = await setup({ vaultFails: 'get' });

    expect(t.bubble().position).toEqual({ x: 1200, y: 720 });
    expect(t.lines.some((l) => l.includes('stored floating button settings unreadable'))).toBe(
      true,
    );
    t.quickAsk.dispose();
  });

  it('display primary places the bubble on the primary display even when the cursor is elsewhere', async () => {
    const t = await setup({ cursor: ON_SECONDARY, bubble: stored('bottom-right', 'primary') });

    await t.invoke(CHANNEL.localOpen, sender());

    expect(t.bubble().position).toEqual({ x: 1200, y: 720 });
    expect(t.quickAskWindow().position).toEqual({ x: 624, y: 364 });
    t.quickAsk.dispose();
  });

  it('display cursor places the bubble on the display under the cursor', async () => {
    const t = await setup({ cursor: ON_SECONDARY, bubble: stored('bottom-right', 'cursor') });

    expect(t.bubble().position).toEqual({ x: 3120, y: 960 });
    t.quickAsk.dispose();
  });

  it('enabled false destroys the bubble, and enabled true creates it again', async () => {
    const t = await setup();
    const first = t.bubble();

    await t.invoke(CHANNEL.localSetBubble, sender(), {
      enabled: false,
      corner: 'bottom-right',
      display: 'cursor',
    });
    const afterOff = t.bubbles().filter((b) => !b.destroyed);
    await t.invoke(CHANNEL.localSetBubble, sender(), {
      enabled: true,
      corner: 'bottom-right',
      display: 'cursor',
    });

    expect(first.destroyed).toBe(true);
    expect(afterOff).toHaveLength(0);
    expect(t.bubbles().filter((b) => !b.destroyed)).toHaveLength(1);
    expect(t.bubble().position).toEqual({ x: 1200, y: 720 });
    t.quickAsk.dispose();
  });

  it.each([
    [{ enabled: true, corner: 'middle', display: 'cursor' }],
    [{ enabled: true, corner: 'top-left', display: 'left' }],
    [{ enabled: 'yes', corner: 'top-left', display: 'cursor' }],
    [undefined],
  ])('refuses %p with quick-ask/invalid-bubble and keeps the bubble where it was', async (req) => {
    const t = await setup();

    const error = await t.rejectionError(t.invoke(CHANNEL.localSetBubble, sender(), req));

    expect(error.code).toBe('quick-ask/invalid-bubble');
    expect(t.entries.has('quick-ask.bubble')).toBe(false);
    expect(t.bubble().calls).toEqual(['setPosition', 'showInactive']);
    t.quickAsk.dispose();
  });

  it.each([{ XDG_SESSION_TYPE: 'wayland' }, { WAYLAND_DISPLAY: 'wayland-0' }])(
    'on Wayland the windows are still placed in the chosen corner (XWayland honours it) and the shortcut hint remains (%p)',
    async (env) => {
      const t = await setup({ platform: 'linux', env, bubble: stored('top-left') });

      await t.invoke(CHANNEL.localOpen, sender());
      const status = await t.invoke(CHANNEL.localGetShortcut, sender());

      expect(t.bubble().position).toEqual({ x: 16, y: 48 });
      expect(t.bubble().calls).toEqual(['setPosition', 'showInactive', 'hide']);
      expect(t.quickAskWindow().position).toEqual({ x: 16, y: 48 });
      expect(t.quickAskWindow().calls).toEqual(['setPosition', 'show', 'focus']);
      expect(status).toEqual({
        accelerator: DEFAULT_QUICK_ASK_SHORTCUT,
        registered: true,
        hint: 'Your desktop (Wayland) does not let apps register global shortcuts. Add a custom shortcut in your system settings that runs: /opt/Scout/scout-desktop --ask',
      });
      t.quickAsk.dispose();
    },
  );

  it('on Wayland the hint names the AppImage when Scout runs from one', async () => {
    const t = await setup({
      env: { XDG_SESSION_TYPE: 'wayland', APPIMAGE: '/home/owner/Apps/Scout.AppImage' },
    });

    const status = (await t.invoke(CHANNEL.localGetShortcut, sender())) as { hint: string };

    expect(status.hint).toBe(
      'Your desktop (Wayland) does not let apps register global shortcuts. Add a custom shortcut in your system settings that runs: /home/owner/Apps/Scout.AppImage --ask',
    );
    t.quickAsk.dispose();
  });

  it('on Wayland the hint quotes a program path that holds a space', async () => {
    const t = await setup({
      env: { WAYLAND_DISPLAY: 'wayland-0' },
      execPath: '/home/owner/My Apps/scout-desktop',
    });

    const status = (await t.invoke(CHANNEL.localGetShortcut, sender())) as { hint: string };

    expect(status.hint.endsWith('runs: "/home/owner/My Apps/scout-desktop" --ask')).toBe(true);
    t.quickAsk.dispose();
  });

  it('on macOS both windows are visible on all workspaces and above fullscreen apps', async () => {
    const t = await setup({ platform: 'darwin', env: { WAYLAND_DISPLAY: 'wayland-0' } });

    await t.invoke(CHANNEL.localOpen, sender());

    expect(t.bubble().alwaysOnTopLevel).toBe('screen-saver');
    expect(t.quickAskWindow().alwaysOnTopLevel).toBe('floating');
    for (const window of [t.bubble(), t.quickAskWindow()]) {
      expect(window.allWorkspaces).toEqual({
        visible: true,
        options: { visibleOnFullScreen: true },
      });
    }
    expect(t.bubble().position).toEqual({ x: 1200, y: 720 });
    t.quickAsk.dispose();
  });

  it('leaves the window level and workspaces alone outside macOS', async () => {
    const t = await setup({ platform: 'win32' });

    await t.invoke(CHANNEL.localOpen, sender());

    expect(t.bubble().alwaysOnTopLevel).toBeNull();
    expect(t.quickAskWindow().allWorkspaces).toBeNull();
    t.quickAsk.dispose();
  });

  it('dispose destroys the bubble and removes the floating button handlers', async () => {
    const t = await setup();
    const bubble = t.bubble();

    t.quickAsk.dispose();

    expect(bubble.destroyed).toBe(true);
    expect(t.handlers.has(CHANNEL.localOpen)).toBe(false);
    expect(t.handlers.has(CHANNEL.localGetBubble)).toBe(false);
    expect(t.handlers.has(CHANNEL.localSetBubble)).toBe(false);
  });
});

describe('quick ask: microphone permission', () => {
  type Handler = Parameters<Session['setPermissionRequestHandler']>[0];

  function decide(
    permission: string,
    details: { requestingUrl: string; mediaTypes?: string[] },
    devServerUrl?: string,
  ): boolean {
    let handler: Handler = null;
    allowMicrophoneOnlyForApp(
      {
        setPermissionRequestHandler(h: Handler) {
          handler = h;
        },
      },
      {
        indexHtmlPath: '/app/renderer/index.html',
        ...(devServerUrl !== undefined ? { devServerUrl } : {}),
      },
    );
    let granted: boolean | null = null;
    (handler as unknown as (...args: unknown[]) => void)(
      null,
      permission,
      (g: boolean) => {
        granted = g;
      },
      details,
    );
    if (granted === null) throw new Error('the handler never answered');
    return granted;
  }

  const appPage = pathToFileURL('/app/renderer/index.html').href + '#/quick-ask';

  it('grants the microphone to the app page', () => {
    expect(decide('media', { requestingUrl: appPage, mediaTypes: ['audio'] })).toBe(true);
  });

  it('grants the microphone to the dev server origin in development', () => {
    expect(
      decide(
        'media',
        { requestingUrl: 'http://localhost:4200/#/quick-ask', mediaTypes: ['audio'] },
        'http://localhost:4200',
      ),
    ).toBe(true);
  });

  it.each([
    ['https://evil.example/', ['audio']],
    [pathToFileURL('/tmp/other.html').href, ['audio']],
    [appPage, ['audio', 'video']],
    ['not a url', ['audio']],
  ])('refuses the microphone to %s for %p', (requestingUrl, mediaTypes) => {
    expect(decide('media', { requestingUrl, mediaTypes })).toBe(false);
  });

  it('leaves every other permission as Electron grants it today', () => {
    expect(decide('clipboard-sanitized-write', { requestingUrl: appPage })).toBe(true);
  });
});
