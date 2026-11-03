import { pathToFileURL } from 'node:url';
import type {
  BrowserWindow,
  BrowserWindowConstructorOptions,
  IpcMain,
  Rectangle,
  Screen,
  Session,
} from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type LocalAskRequest,
  type LocalBubbleSettings,
  type LocalCitation,
  type LocalEvent,
  type LocalShortcut,
  type LocalTurn,
  type WorkerEvent,
} from '@scout/shared';
import type { Supervisor } from '../worker/supervisor.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';
import { ASK_ARG } from '../shell/login-item.js';
import { placeInCorner } from './placement.js';

export const DEFAULT_QUICK_ASK_SHORTCUT = 'CommandOrControl+Alt+Space';
export const NOT_RUNNING_MESSAGE = 'Scout is not running. Start it from the tray or the app.';
export const STOPPED_REPLY = 'Scout stopped before it finished answering.';
export const SHORTCUT_TAKEN_MESSAGE = 'That shortcut is taken';
export const WAYLAND_SHORTCUT_HINT =
  'Your desktop (Wayland) does not let apps register global shortcuts. Add a custom shortcut in your system settings that runs:';
export const DEFAULT_BUBBLE: LocalBubbleSettings = {
  enabled: true,
  corner: 'bottom-right',
  display: 'cursor',
};

const SHORTCUT_VAULT_KEY = 'quick-ask.shortcut';
const BUBBLE_VAULT_KEY = 'quick-ask.bubble';
const WINDOW_VAULT_KEY = 'quick-ask.window';
const BUBBLE_SIZE = { width: 64, height: 64 };
const QUICK_ASK_SIZE = { width: 640, height: 420 };
const QUICK_ASK_MIN_SIZE = { minWidth: 360, minHeight: 240 };
const CORNERS: ReadonlySet<unknown> = new Set([
  'bottom-right',
  'bottom-left',
  'top-right',
  'top-left',
]);
const DISPLAYS: ReadonlySet<unknown> = new Set(['cursor', 'primary']);
const ID_PATTERN = /^[A-Za-z0-9-]{1,64}$/;
const BASE64_PATTERN = /^[A-Za-z0-9+/]*={0,2}$/;
const MAX_TEXT_CHARS = 4000;
const MAX_AUDIO_BASE64_CHARS = 14_000_000;
const EVENT_KINDS: ReadonlySet<string> = new Set(['begin', 'update', 'complete', 'fail']);
// ponytail: the shared conversation keeps the last 200 turns in memory only; persist or page it if the owner wants more.
const MAX_TURNS = 200;
const MAX_NOTICE_CHARS = 32_000;
const VOICE_QUESTION = '(voice message)';

export interface QuickAskDeps {
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>;
  supervisor: Pick<Supervisor, 'writeStdin' | 'onEvent'>;
  createWindow: (opts: BrowserWindowConstructorOptions) => BrowserWindow;
  preloadPath: string | undefined;
  indexHtmlPath: string;
  devServerUrl?: string;
  globalShortcut: {
    register(accelerator: string, callback: () => void): boolean;
    unregister(accelerator: string): void;
  };
  vault: Pick<Vault, 'get' | 'store'>;
  logger: Logger;
  screen: Pick<
    Screen,
    'getDisplayNearestPoint' | 'getCursorScreenPoint' | 'getPrimaryDisplay' | 'getAllDisplays'
  >;
  platform: NodeJS.Platform;
  env: Readonly<Record<string, string | undefined>>;
  /** The running executable; the Wayland hint names it, or the AppImage when Scout runs from one. */
  execPath: string;
  /** Every open window; each one is sent the whole conversation whenever it changes. */
  windows: () => readonly BrowserWindow[];
}

export interface QuickAsk {
  /** Applies the JSON of one `SCOUT_LOCAL ` stdout line to its running turn, or appends a worker notice as its own turn; anything else is dropped. */
  handleWorkerLine(json: string): void;
  dispose(): void;
}

let openCurrent: (() => void) | null = null;

/** Shows the quick-ask window, creating it on first use; does nothing before `registerQuickAsk`. */
export function openQuickAsk(): void {
  openCurrent?.();
}

/** Grants the microphone only to the app's own page, and audio only; every other permission keeps Electron's default. */
export function allowMicrophoneOnlyForApp(
  session: Pick<Session, 'setPermissionRequestHandler'>,
  app: { indexHtmlPath: string; devServerUrl?: string },
): void {
  const pagePath = pathToFileURL(app.indexHtmlPath).pathname;
  const devOrigin = app.devServerUrl != null ? new URL(app.devServerUrl).origin : null;

  function isAppPage(raw: string | undefined): boolean {
    if (raw == null) return false;
    try {
      const url = new URL(raw);
      return url.protocol === 'file:' ? url.pathname === pagePath : url.origin === devOrigin;
    } catch {
      return false;
    }
  }

  session.setPermissionRequestHandler((_webContents, permission, callback, details) => {
    if (permission !== 'media') {
      callback(true);
      return;
    }
    const mediaTypes = 'mediaTypes' in details ? (details.mediaTypes ?? []) : [];
    callback(isAppPage(details.requestingUrl) && mediaTypes.every((type) => type === 'audio'));
  });
}

function fail(code: string, message: string): never {
  const serialized = serializeError(new ScoutError(code, message));
  throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), { code: serialized.code });
}

function toWorkerRequest(req: unknown): { id: string; question: string; line: string } {
  const r = (typeof req === 'object' && req !== null ? req : {}) as Record<string, unknown>;
  const id = typeof r['id'] === 'string' ? r['id'] : '';
  if (!ID_PATTERN.test(id)) fail('local/invalid-request', 'The question has no valid id.');

  if (typeof r['text'] === 'string') {
    const text = r['text'].trim();
    if (text.length === 0 || text.length > MAX_TEXT_CHARS) {
      fail('local/invalid-request', `Type a question of 1 to ${MAX_TEXT_CHARS} characters.`);
    }
    return { id, question: text, line: JSON.stringify({ type: 'ask', id, text }) };
  }

  const audio = (typeof r['audio'] === 'object' && r['audio'] !== null ? r['audio'] : {}) as Record<
    string,
    unknown
  >;
  const base64 = typeof audio['base64'] === 'string' ? audio['base64'] : '';
  if (
    audio['contentType'] !== 'audio/wav' ||
    base64.length === 0 ||
    base64.length > MAX_AUDIO_BASE64_CHARS ||
    !BASE64_PATTERN.test(base64)
  ) {
    fail('local/invalid-request', 'The recording must be a WAV of at most 5 minutes.');
  }
  return {
    id,
    question: VOICE_QUESTION,
    line: JSON.stringify({ type: 'ask', id, audio: { contentType: 'audio/wav', base64 } }),
  };
}

function toBubbleSettings(raw: unknown): LocalBubbleSettings | null {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { enabled, corner, display } = r;
  if (typeof enabled !== 'boolean' || !CORNERS.has(corner) || !DISPLAYS.has(display)) return null;
  return {
    enabled,
    corner: corner as LocalBubbleSettings['corner'],
    display: display as LocalBubbleSettings['display'],
  };
}

export function toBounds(raw: string | null): Rectangle | null {
  if (raw === null) return null;
  try {
    const r = JSON.parse(raw) as Record<string, unknown>;
    const { x, y, width, height } = r;
    return [x, y, width, height].every((n) => Number.isInteger(n))
      ? ({ x, y, width, height } as Rectangle)
      : null;
  } catch {
    return null;
  }
}

function toLocalEvent(json: string): LocalEvent | { readonly notice: string } | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return null;
  }
  if (typeof parsed !== 'object' || parsed === null) return null;
  const p = parsed as Record<string, unknown>;
  const id = p['id'];
  const event = p['event'];
  if (event === 'notice') {
    const text = p['text'];
    return id === null && typeof text === 'string' && text.length > 0
      ? { notice: text.slice(0, MAX_NOTICE_CHARS) }
      : null;
  }
  if (typeof id !== 'string' || typeof event !== 'string' || !EVENT_KINDS.has(event)) return null;

  const citations: LocalCitation[] = Array.isArray(p['citations'])
    ? p['citations'].flatMap((c: unknown) => {
        const label = (c as Record<string, unknown> | null)?.['label'];
        const url = (c as Record<string, unknown> | null)?.['url'];
        if (typeof label !== 'string') return [];
        return typeof url === 'string' ? [{ label, url }] : [{ label }];
      })
    : [];
  return {
    id,
    event: event as LocalEvent['event'],
    ...(typeof p['text'] === 'string' ? { text: p['text'] } : {}),
    ...(citations.length > 0 ? { citations } : {}),
  };
}

export async function registerQuickAsk(deps: QuickAskDeps): Promise<QuickAsk> {
  const { ipcMain, supervisor, globalShortcut, vault, logger } = deps;
  const turns: LocalTurn[] = [];
  let notices = 0;
  let window: BrowserWindow | null = null;
  let bubbleWindow: BrowserWindow | null = null;
  let bubble = DEFAULT_BUBBLE;
  let savedBounds: Rectangle | null = null;
  const wayland =
    deps.platform === 'linux' &&
    (deps.env['XDG_SESSION_TYPE'] === 'wayland' || deps.env['WAYLAND_DISPLAY'] != null);
  const webPreferences = {
    contextIsolation: true,
    nodeIntegration: false,
    sandbox: true,
    webSecurity: true,
    allowRunningInsecureContent: false,
    experimentalFeatures: false,
    ...(deps.preloadPath != null ? { preload: deps.preloadPath } : {}),
  };

  function broadcast(): void {
    const snapshot = [...turns];
    for (const target of deps.windows()) {
      try {
        if (!target.isDestroyed()) target.webContents.send(CHANNEL.localTurns, snapshot);
      } catch {
        // the window may close between the check and the send
      }
    }
  }

  function isRunning(id: string): boolean {
    return turns.some((turn) => turn.id === id && turn.state === 'running');
  }

  function reshape(id: string, change: (turn: LocalTurn) => LocalTurn): void {
    const index = turns.findIndex((turn) => turn.id === id && turn.state === 'running');
    if (index === -1) return;
    turns[index] = change(turns[index]!);
    broadcast();
  }

  function failRunning(text: string): void {
    for (const turn of turns.filter((t) => t.state === 'running')) {
      reshape(turn.id, (t) => ({ ...t, reply: text, state: 'failed' }));
    }
  }

  function prepare(target: BrowserWindow, route: string, level: 'screen-saver' | 'floating'): void {
    target.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    target.webContents.on('will-navigate', (e) => e.preventDefault());
    if (deps.platform === 'darwin') {
      target.setAlwaysOnTop(true, level);
      target.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
    }
    if (deps.devServerUrl != null) void target.loadURL(`${deps.devServerUrl}/#/${route}`);
    else void target.loadFile(deps.indexHtmlPath, { hash: `/${route}` });
  }

  function place(target: BrowserWindow, size: { width: number; height: number }): void {
    const { screen } = deps;
    const display =
      bubble.display === 'primary'
        ? screen.getPrimaryDisplay()
        : screen.getDisplayNearestPoint(screen.getCursorScreenPoint());
    const { x, y } = placeInCorner(size, display.workArea, bubble.corner);
    target.setPosition(x, y);
  }

  function showBubble(): void {
    if (!bubble.enabled) return;
    if (bubbleWindow === null || bubbleWindow.isDestroyed()) {
      bubbleWindow = deps.createWindow({
        ...BUBBLE_SIZE,
        show: false,
        frame: false,
        transparent: true,
        backgroundColor: '#00000000',
        alwaysOnTop: true,
        skipTaskbar: true,
        resizable: false,
        focusable: false,
        hasShadow: false,
        acceptFirstMouse: true,
        webPreferences,
      });
      prepare(bubbleWindow, 'floating-button', 'screen-saver');
    }
    place(bubbleWindow, BUBBLE_SIZE);
    bubbleWindow.showInactive();
    bubbleWindow.setAlwaysOnTop(true, 'screen-saver');
  }

  function destroyBubble(): void {
    if (bubbleWindow !== null && !bubbleWindow.isDestroyed()) bubbleWindow.destroy();
    bubbleWindow = null;
  }

  function rememberBounds(target: BrowserWindow): void {
    savedBounds = target.getBounds();
    vault.store(WINDOW_VAULT_KEY, JSON.stringify(savedBounds)).catch((err: unknown) => {
      logger.warn('quick-ask: quick-ask window place not saved; it lasts until Scout quits', {
        code: serializeError(err).code,
      });
    });
  }

  function onSomeDisplay(point: { x: number; y: number }): boolean {
    return deps.screen
      .getAllDisplays()
      .some(
        ({ workArea: a }) =>
          point.x >= a.x && point.x < a.x + a.width && point.y >= a.y && point.y < a.y + a.height,
      );
  }

  function hide(): void {
    if (window !== null && !window.isDestroyed()) {
      rememberBounds(window);
      window.hide();
    }
    showBubble();
  }

  function open(): void {
    if (window === null || window.isDestroyed()) {
      const created = deps.createWindow({
        ...QUICK_ASK_SIZE,
        ...QUICK_ASK_MIN_SIZE,
        show: false,
        frame: false,
        alwaysOnTop: true,
        resizable: true,
        skipTaskbar: true,
        backgroundColor: '#111111',
        webPreferences,
      });
      created.on('blur', () => {
        if (!created.isDestroyed()) hide();
      });
      created.on('close', () => rememberBounds(created));
      created.on('closed', () => showBubble());
      prepare(created, 'quick-ask', 'floating');
      if (savedBounds !== null && onSomeDisplay(savedBounds)) created.setBounds(savedBounds);
      else place(created, QUICK_ASK_SIZE);
      window = created;
    }
    if (bubbleWindow !== null && !bubbleWindow.isDestroyed()) bubbleWindow.hide();
    window.show();
    window.setAlwaysOnTop(true, 'floating');
    window.moveTop();
    window.focus();
    window.webContents.focus();
  }

  try {
    const raw = await vault.get(BUBBLE_VAULT_KEY);
    const stored = raw === null ? DEFAULT_BUBBLE : toBubbleSettings(JSON.parse(raw));
    if (stored === null) fail('quick-ask/invalid-bubble', 'The stored settings are malformed.');
    bubble = stored;
  } catch (err: unknown) {
    logger.warn('quick-ask: stored floating button settings unreadable; using the default', {
      code: serializeError(err).code,
    });
  }
  showBubble();

  try {
    savedBounds = toBounds(await vault.get(WINDOW_VAULT_KEY));
  } catch (err: unknown) {
    logger.warn('quick-ask: stored quick-ask window place unreadable; using the corner', {
      code: serializeError(err).code,
    });
  }

  let shortcut = DEFAULT_QUICK_ASK_SHORTCUT;
  try {
    shortcut = (await vault.get(SHORTCUT_VAULT_KEY)) ?? DEFAULT_QUICK_ASK_SHORTCUT;
  } catch (err: unknown) {
    logger.warn('quick-ask: stored shortcut unreadable; using the default', {
      code: serializeError(err).code,
    });
  }
  let registered = false;
  try {
    registered = globalShortcut.register(shortcut, open);
  } catch {
    registered = false;
  }
  if (!registered) logger.warn('quick-ask: shortcut not registered', { shortcut });

  const program = deps.env['APPIMAGE'] ?? deps.execPath;
  const askCommand = `${program.includes(' ') ? `"${program}"` : program} ${ASK_ARG}`;

  function shortcutStatus(): LocalShortcut {
    return {
      accelerator: shortcut,
      registered,
      hint: wayland ? `${WAYLAND_SHORTCUT_HINT} ${askCommand}` : null,
    };
  }

  async function setBubble(req: unknown): Promise<LocalBubbleSettings> {
    const next = toBubbleSettings(req);
    if (next === null) {
      fail('quick-ask/invalid-bubble', 'Choose a corner and a display for the floating button.');
    }
    bubble = next;
    try {
      await vault.store(BUBBLE_VAULT_KEY, JSON.stringify(next));
    } catch (err: unknown) {
      logger.warn('quick-ask: floating button settings not saved; they last until Scout quits', {
        code: serializeError(err).code,
      });
    }
    if (next.enabled) showBubble();
    else destroyBubble();
    logger.info('quick-ask: floating button changed', { ...next });
    return bubble;
  }

  async function setShortcut(req: unknown): Promise<LocalShortcut> {
    const raw = (req as Record<string, unknown> | null)?.['accelerator'];
    const next = typeof raw === 'string' ? raw.trim() : '';
    if (next.length === 0 || next.length > 64)
      fail('quick-ask/invalid-shortcut', 'Type a shortcut.');
    if (next === shortcut && registered) return shortcutStatus();

    let ok: boolean;
    try {
      ok = globalShortcut.register(next, open);
    } catch {
      fail('quick-ask/invalid-shortcut', `"${next}" is not a shortcut Scout understands.`);
    }
    if (!ok) fail('quick-ask/shortcut-taken', SHORTCUT_TAKEN_MESSAGE);

    if (registered) globalShortcut.unregister(shortcut);
    shortcut = next;
    registered = true;
    try {
      await vault.store(SHORTCUT_VAULT_KEY, next);
    } catch (err: unknown) {
      logger.warn('quick-ask: shortcut not saved; it lasts until Scout quits', {
        code: serializeError(err).code,
      });
    }
    logger.info('quick-ask: shortcut changed', { shortcut });
    return shortcutStatus();
  }

  function append(turn: LocalTurn): void {
    turns.push(turn);
    if (turns.length > MAX_TURNS) turns.splice(0, turns.length - MAX_TURNS);
    broadcast();
  }

  function ask(req: LocalAskRequest | undefined): void {
    const { id, question, line } = toWorkerRequest(req);
    if (turns.some((turn) => turn.id === id)) {
      fail('local/invalid-request', 'That question is already running.');
    }

    append({ id, question, reply: '', citations: [], state: 'running' });
    const accepted = supervisor.writeStdin(line, (err) => {
      logger.warn('quick-ask: question not delivered to the worker', { error: err.message });
      reshape(id, (turn) => ({ ...turn, reply: STOPPED_REPLY, state: 'failed' }));
    });
    if (!accepted) {
      reshape(id, (turn) => ({
        ...turn,
        reply: NOT_RUNNING_MESSAGE,
        state: 'failed',
        notRunning: true,
      }));
      fail('local/not-running', NOT_RUNNING_MESSAGE);
    }
  }

  function cancel(req: unknown): void {
    const id = (req as Record<string, unknown> | null)?.['id'];
    if (typeof id !== 'string' || !isRunning(id)) return;
    supervisor.writeStdin(JSON.stringify({ type: 'cancel', id }), () => {});
  }

  function clearTurns(): void {
    if (turns.some((turn) => turn.state === 'running')) return;
    turns.length = 0;
    broadcast();
  }

  const unsubscribe = supervisor.onEvent((event: WorkerEvent) => {
    if (event.kind === 'exit') failRunning(STOPPED_REPLY);
  });

  ipcMain.handle(CHANNEL.localAsk, (_event, req: LocalAskRequest | undefined) => ask(req));
  ipcMain.handle(CHANNEL.localCancel, (_event, req: unknown) => cancel(req));
  ipcMain.handle(CHANNEL.localGetTurns, () => [...turns]);
  ipcMain.handle(CHANNEL.localClearTurns, () => clearTurns());
  ipcMain.handle(CHANNEL.localHide, () => hide());
  ipcMain.handle(CHANNEL.localGetShortcut, () => shortcutStatus());
  ipcMain.handle(CHANNEL.localSetShortcut, (_event, req: unknown) => setShortcut(req));
  ipcMain.handle(CHANNEL.localOpen, () => open());
  ipcMain.handle(CHANNEL.localGetBubble, () => bubble);
  ipcMain.handle(CHANNEL.localSetBubble, (_event, req: unknown) => setBubble(req));
  openCurrent = open;

  return {
    handleWorkerLine(json: string): void {
      const event = toLocalEvent(json);
      if (event === null) return;
      if ('notice' in event) {
        notices += 1;
        append({
          id: `notice:${notices}`,
          question: '',
          reply: event.notice,
          citations: [],
          state: 'done',
          notice: true,
        });
        return;
      }
      if (event.event === 'begin') return;
      reshape(event.id, (turn) => ({
        ...turn,
        reply: event.text ?? turn.reply,
        citations: event.event === 'complete' ? (event.citations ?? []) : turn.citations,
        state: event.event === 'complete' ? 'done' : event.event === 'fail' ? 'failed' : 'running',
      }));
    },
    dispose(): void {
      openCurrent = null;
      unsubscribe();
      if (registered) globalShortcut.unregister(shortcut);
      registered = false;
      for (const channel of [
        CHANNEL.localAsk,
        CHANNEL.localCancel,
        CHANNEL.localGetTurns,
        CHANNEL.localClearTurns,
        CHANNEL.localHide,
        CHANNEL.localGetShortcut,
        CHANNEL.localSetShortcut,
        CHANNEL.localOpen,
        CHANNEL.localGetBubble,
        CHANNEL.localSetBubble,
      ]) {
        ipcMain.removeHandler(channel);
      }
      if (window !== null && !window.isDestroyed()) window.destroy();
      window = null;
      destroyBubble();
      turns.length = 0;
    },
  };
}
