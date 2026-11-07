import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import type {
  BrowserWindow,
  BrowserWindowConstructorOptions,
  DesktopCapturer,
  IpcMain,
  Rectangle,
  Screen,
  Session,
} from 'electron';
import {
  CHANNEL,
  LAST_ANSWER_MARKER,
  MEETING_WINDOWS,
  REVIEW_MARKER,
  ISSUES_MARKER,
  SCREEN_TEXT_MARKER,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  transcriptMarker,
  type LocalAskRequest,
  type LocalBubbleSettings,
  type LocalCitation,
  type LocalEvent,
  type LocalListenLane,
  type LocalListenResponse,
  type LocalDeleteMeetingsResponse,
  type LocalExportMeetingResponse,
  type LocalKeepMeetingResponse,
  type LocalMeeting,
  type LocalMeetingLine,
  type LocalMeetingSummary,
  type LocalMeetingTurn,
  type LocalMeetingsResponse,
  type LocalReadTextResponse,
  type LocalShortcut,
  type LocalTranscriptEvent,
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
export const BUSY_REPLY = 'Scout is still answering; wait or press Stop.';
export const LISTENING_STOPPED_REPLY = 'Scout stopped listening.';
export const SYSTEM_AUDIO_UNAVAILABLE =
  'System audio capture is not available on this platform yet.';
export const READ_TIMEOUT_REPLY = 'Scout did not answer the text read in time.';
export const READ_FAILED_REPLY = 'Scout could not read the text on the screen; see Scout logs.';
export const MEETING_LIMITS_REPLY =
  'A meeting holds at most 5000 lines and 500 turns, with a title of 1 to 200 characters.';
export const MEETING_UNREADABLE_REPLY = 'Scout could not read that meeting.';
export const NO_MEETING_REPLY = 'Scout has no meeting with that id.';
export const MEETING_TIMEOUT_REPLY = 'Scout did not answer about the meetings in time.';
export const MEETING_FAILED_REPLY = 'Scout could not save or read the meeting; see Scout logs.';
export const EXPORT_WRITE_FAILED = 'Scout could not write the Markdown file.';
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
const MAX_TRANSCRIPT_ASK_CHARS = MAX_TEXT_CHARS + 24_200;
const MAX_SCREEN_TEXT_EXTRA_CHARS = 5_200;
const MAX_LAST_ANSWER_EXTRA_CHARS = 4_200;
const MAX_REVIEW_EXTRA_CHARS = 120_000;
const TRANSCRIPT_MARKERS = MEETING_WINDOWS.map(transcriptMarker);
const MEETING_TIMEOUT_MS = 30_000;
const MAX_MEETING_LINES = 5_000;
const MAX_MEETING_LINE_CHARS = 2_000;
const MAX_MEETING_TURNS = 500;
const MAX_MEETING_TITLE_CHARS = 200;
const MAX_MEETINGS_LIMIT = 1_000;
const MEETING_ID_PATTERN = /^[A-Za-z0-9_-]{22}$/;
const ISO_TIME_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$/;
const MAX_EXPORT_NAME_CHARS = 120;
const READ_TIMEOUT_MS = 15_000;
const MAX_AUDIO_BASE64_CHARS = 14_000_000;
const MAX_IMAGE_BASE64_CHARS = 4_000_000;
const MAX_PCM16_BASE64_CHARS = 48_000;
const SYSTEM_AUDIO_ARMED_MS = 10_000;
const SYSTEM_AUDIO_FEATURES = ['PulseaudioLoopbackForScreenShare', 'WebRtcPipeWireCapturer'];
const EVENT_KINDS: ReadonlySet<string> = new Set([
  'begin',
  'progress',
  'update',
  'complete',
  'fail',
]);
const MAX_ACTIVITY_CHARS = 200;
const TRANSCRIPT_KINDS: ReadonlySet<string> = new Set(['partial', 'final', 'complete', 'fail']);
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
  /** The app's locale, the target of a live translation in mode `system`; an empty one means `en`. */
  locale: () => string;
  /** Asks the owner where to save a kept meeting's Markdown; Electron's save dialog. */
  showSaveDialog: (options: {
    defaultPath: string;
    filters: { name: string; extensions: string[] }[];
  }) => Promise<{ canceled: boolean; filePath?: string }>;
  /** Writes the exported Markdown to the file the owner chose. */
  writeFile: (file: string, text: string) => Promise<void>;
  /** The owner's Documents folder, where the save dialog opens. */
  documentsDir: () => string;
}

/** Brackets one pending display-media request, so the quick-ask window can stay up while the system picker is open. */
export interface ScreenCaptureWatch {
  begin(): void;
  end(): void;
  /** True once, while a listen on lane `others` armed the system audio no more than 10 s ago. */
  takeSystemAudio(): boolean;
}

export interface QuickAsk {
  screenCapture: ScreenCaptureWatch;
  /**
   * Applies the JSON of one `SCOUT_LOCAL ` stdout line: a transcript of a live listening session is pushed to every
   * window, a reply goes to its running turn, a worker notice becomes its own turn; anything else is dropped.
   */
  handleWorkerLine(json: string): void;
  dispose(): void;
}

let openCurrent: (() => void) | null = null;

/** Shows the quick-ask window, creating it on first use; does nothing before `registerQuickAsk`. */
export function openQuickAsk(): void {
  openCurrent?.();
}

interface AppPage {
  indexHtmlPath: string;
  devServerUrl?: string;
}

function appPageCheck(app: AppPage): (raw: string | undefined) => boolean {
  const pagePath = pathToFileURL(app.indexHtmlPath).pathname;
  const devOrigin = app.devServerUrl != null ? new URL(app.devServerUrl).origin : null;
  return (raw) => {
    if (raw == null) return false;
    try {
      const url = new URL(raw);
      return url.protocol === 'file:' ? url.pathname === pagePath : url.origin === devOrigin;
    } catch {
      return false;
    }
  };
}

/** Grants the microphone only to the app's own page, and audio only; every other permission keeps Electron's default. */
export function allowMicrophoneOnlyForApp(
  session: Pick<Session, 'setPermissionRequestHandler'>,
  app: AppPage,
): void {
  const isAppPage = appPageCheck(app);
  session.setPermissionRequestHandler((_webContents, permission, callback, details) => {
    if (permission !== 'media') {
      callback(true);
      return;
    }
    const mediaTypes = 'mediaTypes' in details ? (details.mediaTypes ?? []) : [];
    callback(isAppPage(details.requestingUrl) && mediaTypes.every((type) => type === 'audio'));
  });
}

/** Adds the switches Chromium needs on Linux to capture the system audio to an `enable-features` value. */
export function withSystemAudioFeatures(existing: string): string {
  const features = existing.split(',').filter((feature) => feature.length > 0);
  return [...features, ...SYSTEM_AUDIO_FEATURES.filter((f) => !features.includes(f))].join(',');
}

/**
 * Answers a screen capture request only from the app's own page and only for video, with the source the system
 * offers: the one picked in the desktop portal on Wayland, else the first screen. A request that also asks for
 * audio gets the system audio loopback only while a listen on lane `others` armed it. Anything else, a cancelled
 * picker or a failure, answers nothing, so the page's capture is refused.
 */
export function allowScreenCaptureOnlyForApp(
  session: Pick<Session, 'setDisplayMediaRequestHandler'>,
  app: AppPage,
  capturer: Pick<DesktopCapturer, 'getSources'>,
  capture: ScreenCaptureWatch = {
    begin: () => undefined,
    end: () => undefined,
    takeSystemAudio: () => false,
  },
): void {
  const isAppPage = appPageCheck(app);
  session.setDisplayMediaRequestHandler((request, answer) => {
    capture.begin();
    const callback: typeof answer = (streams) => {
      answer(streams);
      capture.end();
    };
    const fromApp = isAppPage(request.frame?.url);
    const systemAudio = fromApp && request.audioRequested && capture.takeSystemAudio();
    void (async () => {
      try {
        if (!fromApp || !request.videoRequested || (request.audioRequested && !systemAudio)) {
          callback(null);
          return;
        }
        const sources = await capturer.getSources({
          types: ['screen', 'window'],
          thumbnailSize: { width: 0, height: 0 },
        });
        const source = sources.find((s) => s.id.startsWith('screen:')) ?? sources[0];
        callback(
          source === undefined
            ? null
            : systemAudio
              ? { video: source, audio: 'loopback' }
              : { video: source },
        );
      } catch {
        callback(null);
      }
    })();
  });
}

function ipcError(code: string, message: string): Error {
  const serialized = serializeError(new ScoutError(code, message));
  return Object.assign(new Error(encodeIpcErrorMessage(serialized)), { code: serialized.code });
}

function fail(code: string, message: string): never {
  throw ipcError(code, message);
}

function toImage(raw: unknown): { image?: { contentType: 'image/jpeg'; base64: string } } {
  if (raw === undefined) return {};
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const base64 = typeof r['base64'] === 'string' ? r['base64'] : '';
  if (
    r['contentType'] !== 'image/jpeg' ||
    base64.length === 0 ||
    base64.length > MAX_IMAGE_BASE64_CHARS ||
    !BASE64_PATTERN.test(base64)
  ) {
    fail('local/invalid-request', 'The screenshot must be a JPEG of at most 3 MB.');
  }
  return { image: { contentType: 'image/jpeg', base64 } };
}

function toWorkerRequest(req: unknown): {
  id: string;
  question: string;
  line: string;
  screenshot: boolean;
  transcript: boolean;
  screenText: boolean;
  meeting: boolean;
  review: boolean;
  issues: boolean;
} {
  const r = (typeof req === 'object' && req !== null ? req : {}) as Record<string, unknown>;
  const id = typeof r['id'] === 'string' ? r['id'] : '';
  if (!ID_PATTERN.test(id) || id.startsWith('r-') || id.startsWith('m-')) {
    fail('local/invalid-request', 'The question has no valid id.');
  }
  const image = toImage(r['image']);
  const screenshot = image.image !== undefined;

  if (typeof r['text'] === 'string') {
    const text = r['text'].trim();
    const marker = firstAt(text, TRANSCRIPT_MARKERS);
    const screenMarker = text.indexOf(SCREEN_TEXT_MARKER);
    const lastAnswer = text.indexOf(LAST_ANSWER_MARKER);
    const reviewMarker = text.indexOf(REVIEW_MARKER);
    const issuesMarker = text.indexOf(ISSUES_MARKER);
    const markers = [marker, screenMarker, lastAnswer, reviewMarker, issuesMarker].filter(
      (at) => at !== -1,
    );
    const question = markers.length === 0 ? text : text.slice(0, Math.min(...markers));
    const limit =
      (marker === -1 ? MAX_TEXT_CHARS : MAX_TRANSCRIPT_ASK_CHARS) +
      (screenMarker === -1 ? 0 : MAX_SCREEN_TEXT_EXTRA_CHARS) +
      (lastAnswer === -1 ? 0 : MAX_LAST_ANSWER_EXTRA_CHARS) +
      (reviewMarker === -1 && issuesMarker === -1 ? 0 : MAX_REVIEW_EXTRA_CHARS);
    if (question.length === 0 || question.length > MAX_TEXT_CHARS || text.length > limit) {
      fail('local/invalid-request', `Type a question of 1 to ${MAX_TEXT_CHARS} characters.`);
    }
    return {
      id,
      question: question.trim(),
      line: JSON.stringify({ type: 'ask', id, text, ...image }),
      screenshot,
      transcript: marker !== -1,
      screenText: screenMarker !== -1,
      meeting: r['meeting'] === true,
      review: reviewMarker !== -1,
      issues: issuesMarker !== -1,
    };
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
    line: JSON.stringify({
      type: 'ask',
      id,
      audio: { contentType: 'audio/wav', base64 },
      ...image,
    }),
    screenshot,
    transcript: false,
    screenText: false,
    meeting: false,
    review: false,
    issues: false,
  };
}

function firstAt(text: string, markers: readonly string[]): number {
  const found = markers.map((marker) => text.indexOf(marker)).filter((at) => at !== -1);
  return found.length === 0 ? -1 : Math.min(...found);
}

function isTime(value: unknown): value is string {
  return (
    typeof value === 'string' && ISO_TIME_PATTERN.test(value) && !Number.isNaN(Date.parse(value))
  );
}

function optionalText(value: unknown): string | undefined {
  if (value === undefined) return undefined;
  if (typeof value !== 'string') fail('local/invalid-request', MEETING_UNREADABLE_REPLY);
  return value;
}

function toMeetingLine(raw: unknown): LocalMeetingLine {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { at, lane, text } = r;
  if (!isTime(at) || (lane !== 'me' && lane !== 'others') || typeof text !== 'string') {
    fail('local/invalid-request', MEETING_UNREADABLE_REPLY);
  }
  if (text.length > MAX_MEETING_LINE_CHARS) fail('local/invalid-request', MEETING_LIMITS_REPLY);
  const translation = optionalText(r['translation']);
  const language = optionalText(r['language']);
  return {
    at,
    lane,
    text,
    ...(translation !== undefined ? { translation } : {}),
    ...(language !== undefined ? { language } : {}),
  };
}

function toMeetingTurn(raw: unknown): LocalMeetingTurn {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { at, question, answer } = r;
  if (!isTime(at) || typeof question !== 'string' || typeof answer !== 'string') {
    fail('local/invalid-request', MEETING_UNREADABLE_REPLY);
  }
  return {
    at,
    question,
    answer,
    screenshot: r['screenshot'] === true,
    transcript: r['transcript'] === true,
    screenText: r['screenText'] === true,
  };
}

function toMeeting(raw: unknown): LocalMeeting {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { startedAt, endedAt, lines, turns } = r;
  if (!isTime(startedAt) || !isTime(endedAt) || !Array.isArray(lines) || !Array.isArray(turns)) {
    fail('local/invalid-request', MEETING_UNREADABLE_REPLY);
  }
  const title = typeof r['title'] === 'string' ? r['title'].trim() : '';
  if (
    title.length === 0 ||
    title.length > MAX_MEETING_TITLE_CHARS ||
    lines.length > MAX_MEETING_LINES ||
    turns.length > MAX_MEETING_TURNS
  ) {
    fail('local/invalid-request', MEETING_LIMITS_REPLY);
  }
  return {
    startedAt,
    endedAt,
    title,
    lines: lines.map(toMeetingLine),
    turns: turns.map(toMeetingTurn),
  };
}

function toMeetingId(raw: unknown): string {
  const id = (raw as Record<string, unknown> | null | undefined)?.['meetingId'];
  if (typeof id !== 'string' || !MEETING_ID_PATTERN.test(id)) {
    fail('local/invalid-request', NO_MEETING_REPLY);
  }
  return id;
}

function toSummary(raw: unknown): LocalMeetingSummary[] {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { id, startedAt, endedAt, title, lineCount, turnCount } = r;
  return typeof id === 'string' &&
    typeof startedAt === 'string' &&
    typeof endedAt === 'string' &&
    typeof title === 'string' &&
    Number.isInteger(lineCount) &&
    Number.isInteger(turnCount)
    ? [
        {
          id,
          startedAt,
          endedAt,
          title,
          lineCount: lineCount as number,
          turnCount: turnCount as number,
        },
      ]
    : [];
}

/** The file name the save dialog offers: the Markdown's `# ` title made safe for a file system, else Meeting. */
function exportName(markdown: string): string {
  const first = markdown.split('\n', 1)[0] ?? '';
  const title = first.startsWith('# ') ? first.slice(2) : '';
  const safe = title
    .replace(/[\\/:*?"<>|\p{Cc}]/gu, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/^\.+/, '')
    .slice(0, MAX_EXPORT_NAME_CHARS)
    .trim();
  return `${safe.length > 0 ? safe : 'Meeting'}.md`;
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

function isSeq(value: unknown): value is number {
  return Number.isInteger(value) && (value as number) >= 0;
}

function parseLine(json: string): Record<string, unknown> | null {
  try {
    const parsed: unknown = JSON.parse(json);
    return typeof parsed === 'object' && parsed !== null
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

function toLocalEvent(p: Record<string, unknown>): LocalEvent | { readonly notice: string } | null {
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
  let pendingCaptures = 0;
  const sessions = new Map<string, LocalListenLane>();
  const reads = new Map<string, (outcome: LocalReadTextResponse | Error) => void>();
  let readCount = 0;
  const meetingRequests = new Map<string, (outcome: Record<string, unknown> | Error) => void>();
  let meetingCount = 0;
  let systemAudio: { id: string; at: number } | null = null;
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

  function sendToWindows(channel: string, payload: unknown): void {
    for (const target of deps.windows()) {
      try {
        if (!target.isDestroyed()) target.webContents.send(channel, payload);
      } catch {
        // the window may close between the check and the send
      }
    }
  }

  function broadcast(): void {
    sendToWindows(CHANNEL.localTurns, [...turns]);
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

  function failed(turn: LocalTurn, text: string): LocalTurn {
    return turn.reply.length > 0
      ? { ...turn, failure: text, state: 'failed' }
      : { ...turn, reply: text, state: 'failed' };
  }

  function failRunning(text: string): void {
    for (const turn of turns.filter((t) => t.state === 'running')) {
      reshape(turn.id, (t) => failed(t, text));
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
        if (!created.isDestroyed() && pendingCaptures === 0 && sessions.size === 0) hide();
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
    const { id, question, line, screenshot, transcript, screenText, meeting, review, issues } =
      toWorkerRequest(req);
    if (turns.some((turn) => turn.id === id)) {
      fail('local/invalid-request', 'That question is already running.');
    }
    if (turns.some((turn) => turn.state === 'running')) fail('local/busy', BUSY_REPLY);

    append({
      id,
      question,
      reply: '',
      citations: [],
      state: 'running',
      askedAt: Date.now(),
      ...(screenshot ? { screenshot: true as const } : {}),
      ...(transcript ? { transcript: true as const } : {}),
      ...(screenText ? { screenText: true as const } : {}),
      ...(meeting ? { meeting: true as const } : {}),
      ...(review ? { review: true as const } : {}),
      ...(issues ? { issues: true as const } : {}),
    });
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

  function pushTranscript(event: LocalTranscriptEvent): void {
    sendToWindows(CHANNEL.localTranscript, event);
  }

  function disarm(id: string): void {
    if (systemAudio?.id === id) systemAudio = null;
  }

  function endListening(id: string, error?: string): void {
    const lane = sessions.get(id);
    if (lane === undefined) return;
    sessions.delete(id);
    disarm(id);
    pushTranscript({
      id,
      lane,
      text: '',
      final: true,
      ended: true,
      ...(error != null ? { error } : {}),
    });
  }

  function listen(req: unknown): LocalListenResponse {
    const lane = (req as Record<string, unknown> | null | undefined)?.['lane'] ?? 'me';
    if (lane !== 'me' && lane !== 'others') {
      fail('local/invalid-request', 'Listen to the microphone or to the system audio.');
    }
    const mode = (req as Record<string, unknown> | null | undefined)?.['translate'] ?? 'off';
    if (mode !== 'off' && mode !== 'system' && mode !== 'channel') {
      fail(
        'local/invalid-request',
        'Translate to nothing, the system language or the channel language.',
      );
    }
    if (lane === 'others' && deps.platform !== 'linux') {
      fail('local/invalid-request', SYSTEM_AUDIO_UNAVAILABLE);
    }
    for (const [open, openLane] of sessions) {
      if (openLane !== lane) continue;
      supervisor.writeStdin(JSON.stringify({ type: 'stop', id: open }), () => {});
      sessions.delete(open);
      disarm(open);
    }
    const id = randomUUID();
    const translate =
      mode === 'system'
        ? { translate: deps.locale() || 'en' }
        : mode === 'channel'
          ? { translate: 'auto' }
          : {};
    const line = JSON.stringify({ type: 'listen', id, ...translate });
    const accepted = supervisor.writeStdin(line, (err) => {
      logger.warn('quick-ask: listening not delivered to the worker', { error: err.message });
      endListening(id, LISTENING_STOPPED_REPLY);
    });
    if (!accepted) fail('local/not-running', NOT_RUNNING_MESSAGE);
    sessions.set(id, lane);
    if (lane === 'others') systemAudio = { id, at: Date.now() };
    return { id };
  }

  function audio(req: unknown): void {
    const r = (typeof req === 'object' && req !== null ? req : {}) as Record<string, unknown>;
    const id = r['id'];
    if (typeof id !== 'string' || !sessions.has(id)) {
      fail('local/invalid-request', 'Scout is not listening.');
    }
    const pcm16 = typeof r['pcm16'] === 'string' ? r['pcm16'] : '';
    if (
      pcm16.length === 0 ||
      pcm16.length > MAX_PCM16_BASE64_CHARS ||
      !BASE64_PATTERN.test(pcm16)
    ) {
      fail(
        'local/invalid-request',
        `An audio chunk is base64 PCM16 of 1 to ${MAX_PCM16_BASE64_CHARS} characters.`,
      );
    }
    const accepted = supervisor.writeStdin(JSON.stringify({ type: 'audio', id, pcm16 }), (err) => {
      logger.warn('quick-ask: audio not delivered to the worker', { error: err.message });
      endListening(id, LISTENING_STOPPED_REPLY);
    });
    if (!accepted) {
      endListening(id, NOT_RUNNING_MESSAGE);
      fail('local/not-running', NOT_RUNNING_MESSAGE);
    }
  }

  function stopListening(req: unknown): void {
    const id = (req as Record<string, unknown> | null)?.['id'];
    if (typeof id !== 'string' || !sessions.has(id)) return;
    disarm(id);
    const accepted = supervisor.writeStdin(JSON.stringify({ type: 'stop', id }), () =>
      endListening(id, LISTENING_STOPPED_REPLY),
    );
    if (!accepted) endListening(id, LISTENING_STOPPED_REPLY);
  }

  function applyTranscript(p: Record<string, unknown>): boolean {
    const id = p['id'];
    const event = p['event'];
    const lane = typeof id === 'string' ? sessions.get(id) : undefined;
    if (lane === undefined || typeof id !== 'string' || typeof event !== 'string') return false;
    if (event === 'translation') {
      pushTranslation(id, lane, p);
      return true;
    }
    if (!TRANSCRIPT_KINDS.has(event)) return true;
    const text = typeof p['text'] === 'string' ? p['text'] : '';
    const seq = p['seq'];
    if (event === 'complete') endListening(id);
    else if (event === 'fail') endListening(id, text.length > 0 ? text : LISTENING_STOPPED_REPLY);
    else if (text.length > 0) {
      pushTranscript({
        id,
        lane,
        text,
        final: event === 'final',
        ...(event === 'final' && isSeq(seq) ? { seq } : {}),
      });
    }
    return true;
  }

  function pushTranslation(id: string, lane: LocalListenLane, p: Record<string, unknown>): void {
    const { seq, language, text, error } = p;
    if (!isSeq(seq) || typeof language !== 'string') return;
    if (typeof text !== 'string' && typeof error !== 'string') return;
    pushTranscript({
      kind: 'translation',
      id,
      lane,
      seq,
      language,
      ...(typeof error === 'string' ? { error } : { text: text as string }),
    });
  }

  function readText(req: unknown): Promise<LocalReadTextResponse> {
    const image = toImage(
      (req as Record<string, unknown> | null | undefined)?.['image'] ?? null,
    ).image!;
    readCount += 1;
    const id = `r-${readCount}`;
    return new Promise((resolve, reject) => {
      const settle = (outcome: LocalReadTextResponse | Error): void => {
        clearTimeout(timer);
        reads.delete(id);
        if (outcome instanceof Error) reject(outcome);
        else resolve(outcome);
      };
      const timer = setTimeout(() => {
        logger.warn('quick-ask: the worker did not answer a text read in time', { id });
        settle(ipcError('local/read-failed', READ_TIMEOUT_REPLY));
      }, READ_TIMEOUT_MS);
      reads.set(id, settle);
      const accepted = supervisor.writeStdin(JSON.stringify({ type: 'read', id, image }), (err) => {
        logger.warn('quick-ask: text read not delivered to the worker', { id, error: err.message });
        settle(ipcError('local/read-failed', STOPPED_REPLY));
      });
      if (!accepted) {
        settle(ipcError('local/not-running', NOT_RUNNING_MESSAGE));
        return;
      }
      logger.info('quick-ask: text read sent to the worker', {
        id,
        bytes: Buffer.byteLength(image.base64, 'base64'),
      });
    });
  }

  function applyRead(p: Record<string, unknown>): boolean {
    const id = p['id'];
    const settle = typeof id === 'string' ? reads.get(id) : undefined;
    if (settle === undefined) return false;
    const text = typeof p['text'] === 'string' ? p['text'] : '';
    if (p['event'] === 'text') settle({ text });
    else if (p['event'] === 'fail') {
      settle(ipcError('local/read-failed', text.length > 0 ? text : READ_FAILED_REPLY));
    }
    return true;
  }

  function askMeetings(
    type: string,
    fields: Record<string, unknown>,
    answer: string,
  ): Promise<Record<string, unknown>> {
    meetingCount += 1;
    const id = `m-${meetingCount}`;
    return new Promise((resolve, reject) => {
      const settle = (outcome: Record<string, unknown> | Error): void => {
        clearTimeout(timer);
        meetingRequests.delete(id);
        if (outcome instanceof Error) reject(outcome);
        else resolve(outcome);
      };
      const timer = setTimeout(() => {
        logger.warn('quick-ask: the worker did not answer a meeting request in time', { id, type });
        settle(ipcError('local/meeting-failed', MEETING_TIMEOUT_REPLY));
      }, MEETING_TIMEOUT_MS);
      meetingRequests.set(id, (outcome) => {
        if (outcome instanceof Error) settle(outcome);
        else if (outcome['event'] === answer) settle(outcome);
        else if (outcome['event'] === 'fail') {
          const text = typeof outcome['text'] === 'string' ? outcome['text'] : '';
          settle(ipcError('local/meeting-failed', text.length > 0 ? text : MEETING_FAILED_REPLY));
        }
      });
      const accepted = supervisor.writeStdin(JSON.stringify({ type, id, ...fields }), (err) => {
        logger.warn('quick-ask: meeting request not delivered to the worker', {
          id,
          type,
          error: err.message,
        });
        settle(ipcError('local/meeting-failed', STOPPED_REPLY));
      });
      if (!accepted) {
        settle(ipcError('local/not-running', NOT_RUNNING_MESSAGE));
        return;
      }
      logger.info('quick-ask: meeting request sent to the worker', { id, type });
    });
  }

  function applyMeeting(p: Record<string, unknown>): boolean {
    const id = p['id'];
    const handle = typeof id === 'string' ? meetingRequests.get(id) : undefined;
    if (handle === undefined) return false;
    handle(p);
    return true;
  }

  function endMeetingRequests(): void {
    for (const handle of [...meetingRequests.values()]) {
      handle(ipcError('local/meeting-failed', STOPPED_REPLY));
    }
  }

  async function keepMeeting(req: unknown): Promise<LocalKeepMeetingResponse> {
    const meeting = toMeeting((req as Record<string, unknown> | null | undefined)?.['meeting']);
    logger.info('quick-ask: keeping a meeting', {
      lines: meeting.lines.length,
      turns: meeting.turns.length,
    });
    const reply = await askMeetings('keep', { meeting }, 'kept');
    const meetingId = reply['meetingId'];
    if (typeof meetingId !== 'string') fail('local/meeting-failed', MEETING_FAILED_REPLY);
    return { meetingId };
  }

  async function listMeetings(req: unknown): Promise<LocalMeetingsResponse> {
    const limit = (req as Record<string, unknown> | null | undefined)?.['limit'];
    if (
      limit !== undefined &&
      (!Number.isInteger(limit) || (limit as number) < 1 || (limit as number) > MAX_MEETINGS_LIMIT)
    ) {
      fail('local/invalid-request', `List from 1 to ${MAX_MEETINGS_LIMIT} meetings.`);
    }
    const reply = await askMeetings('meetings', limit !== undefined ? { limit } : {}, 'meetings');
    const items = Array.isArray(reply['items']) ? reply['items'].flatMap(toSummary) : [];
    return { items };
  }

  async function exportMeeting(req: unknown): Promise<LocalExportMeetingResponse> {
    const meetingId = toMeetingId(req);
    const reply = await askMeetings('export', { meetingId }, 'markdown');
    const markdown = typeof reply['text'] === 'string' ? reply['text'] : '';
    const choice = await deps.showSaveDialog({
      defaultPath: path.join(deps.documentsDir(), exportName(markdown)),
      filters: [{ name: 'Markdown', extensions: ['md'] }],
    });
    if (choice.canceled || choice.filePath === undefined || choice.filePath.length === 0) {
      return { saved: false };
    }
    try {
      await deps.writeFile(choice.filePath, markdown);
    } catch (err: unknown) {
      logger.warn('quick-ask: a meeting export was not written', {
        meetingId,
        code: (err as { code?: unknown } | null)?.code ?? null,
      });
      fail('local/export-failed', EXPORT_WRITE_FAILED);
    }
    logger.info('quick-ask: a meeting export was written', {
      meetingId,
      chars: markdown.length,
    });
    return { saved: true };
  }

  async function deleteMeetings(req: unknown): Promise<LocalDeleteMeetingsResponse> {
    const all = (req as Record<string, unknown> | null | undefined)?.['all'] === true;
    const reply = await askMeetings(
      'delete',
      all ? { all: true } : { meetingId: toMeetingId(req) },
      'deleted',
    );
    const count = reply['count'];
    return { deleted: Number.isInteger(count) ? (count as number) : 0 };
  }

  const unsubscribe = supervisor.onEvent((event: WorkerEvent) => {
    if (event.kind !== 'exit') return;
    failRunning(STOPPED_REPLY);
    for (const id of [...sessions.keys()]) endListening(id, LISTENING_STOPPED_REPLY);
    for (const settle of [...reads.values()]) settle(ipcError('local/read-failed', STOPPED_REPLY));
    endMeetingRequests();
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
  ipcMain.handle(CHANNEL.localListen, (_event, req: unknown) => listen(req));
  ipcMain.handle(CHANNEL.localAudio, (_event, req: unknown) => audio(req));
  ipcMain.handle(CHANNEL.localStopListening, (_event, req: unknown) => stopListening(req));
  ipcMain.handle(CHANNEL.localReadText, (_event, req: unknown) => readText(req));
  ipcMain.handle(CHANNEL.localKeepMeeting, (_event, req: unknown) => keepMeeting(req));
  ipcMain.handle(CHANNEL.localMeetings, (_event, req: unknown) => listMeetings(req));
  ipcMain.handle(CHANNEL.localExportMeeting, (_event, req: unknown) => exportMeeting(req));
  ipcMain.handle(CHANNEL.localDeleteMeetings, (_event, req: unknown) => deleteMeetings(req));
  openCurrent = open;

  return {
    screenCapture: {
      begin(): void {
        pendingCaptures += 1;
      },
      end(): void {
        pendingCaptures = Math.max(0, pendingCaptures - 1);
        if (
          pendingCaptures === 0 &&
          window !== null &&
          !window.isDestroyed() &&
          !window.isVisible()
        ) {
          window.showInactive();
        }
      },
      takeSystemAudio(): boolean {
        const armed = systemAudio !== null && Date.now() - systemAudio.at <= SYSTEM_AUDIO_ARMED_MS;
        systemAudio = null;
        return armed;
      },
    },
    handleWorkerLine(json: string): void {
      const parsed = parseLine(json);
      if (parsed === null || applyMeeting(parsed) || applyRead(parsed) || applyTranscript(parsed))
        return;
      const event = toLocalEvent(parsed);
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
      if (event.event === 'progress') {
        const activity = (event.text ?? '').trim().slice(0, MAX_ACTIVITY_CHARS);
        if (activity.length > 0) reshape(event.id, (turn) => ({ ...turn, activity }));
        return;
      }
      reshape(event.id, ({ activity: _cleared, ...turn }) =>
        event.event === 'fail'
          ? failed(turn, event.text ?? turn.reply)
          : {
              ...turn,
              reply: event.text ?? turn.reply,
              citations: event.event === 'complete' ? (event.citations ?? []) : turn.citations,
              state: event.event === 'complete' ? 'done' : 'running',
            },
      );
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
        CHANNEL.localListen,
        CHANNEL.localAudio,
        CHANNEL.localStopListening,
        CHANNEL.localReadText,
        CHANNEL.localKeepMeeting,
        CHANNEL.localMeetings,
        CHANNEL.localExportMeeting,
        CHANNEL.localDeleteMeetings,
      ]) {
        ipcMain.removeHandler(channel);
      }
      for (const settle of [...reads.values()])
        settle(ipcError('local/read-failed', STOPPED_REPLY));
      endMeetingRequests();
      sessions.clear();
      systemAudio = null;
      if (window !== null && !window.isDestroyed()) window.destroy();
      window = null;
      destroyBubble();
      turns.length = 0;
    },
  };
}
