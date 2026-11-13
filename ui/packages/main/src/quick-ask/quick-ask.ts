import { createHash, randomUUID } from 'node:crypto';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import type {
  BrowserWindow,
  BrowserWindowConstructorOptions,
  DesktopCapturer,
  Display,
  IpcMain,
  Point,
  Rectangle,
  Screen,
  Session,
} from 'electron';
import {
  CHANNEL,
  LAST_ANSWER_MARKER,
  LOCAL_CANCELLED_REPLY,
  MEETING_WINDOWS,
  ISSUES_MARKER,
  SCREEN_TEXT_MARKER,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  transcriptMarker,
  type LocalAskRequest,
  type LocalBubbleCardResponse,
  type LocalBubbleSettings,
  type LocalBubbleStatus,
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
  type LocalFindingDecision,
  type LocalTurn,
  type WorkerEvent,
  type WorkerStatus,
} from '@scout/shared';
import type { Supervisor } from '../worker/supervisor.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';
import { ASK_ARG } from '../shell/login-item.js';
import { fitInWorkArea, placeInCorner } from './placement.js';
import { registerSpeech } from './speech.js';

export const DEFAULT_QUICK_ASK_SHORTCUT = 'CommandOrControl+Alt+Space';
export const NOT_RUNNING_MESSAGE = 'Scout is not running. Start it from the tray or the app.';
export const STOPPED_REPLY = 'Scout stopped before it finished answering.';
export const BUSY_REPLY = 'Scout is queuing cards for your approval; wait until it finishes.';
export const LISTENING_STOPPED_REPLY = 'Scout stopped listening.';
export const LISTEN_TAKEN_REPLY = 'Another listen took the microphone.';
export const SYSTEM_AUDIO_TAKEN_REPLY = 'Another listen took the system audio.';
export const STARTING_ACTIVITY = 'Scout is starting…';
export const SYSTEM_AUDIO_UNAVAILABLE =
  'System audio capture is not available on this platform yet.';
export const READ_TIMEOUT_REPLY = 'Scout did not answer the text read in time.';
export const READ_FAILED_REPLY = 'Scout could not read the text on the screen; see Scout logs.';
export const MEETING_LIMITS_REPLY =
  'A meeting holds at most 5000 lines and 500 turns, with a title of 1 to 200 characters.';
export const MEETING_UNREADABLE_REPLY = 'Scout could not read that meeting.';
export const NO_MEETING_REPLY = 'Scout has no meeting with that id.';
export const MEETING_TIMEOUT_REPLY = 'Scout did not answer about the meetings in time.';
export const MEETING_HOLDS_LANE_REPLY =
  'Scout is listening to a meeting; use the quick-ask window.';
export const MEETING_FAILED_REPLY = 'Scout could not save or read the meeting; see Scout logs.';
export const EXPORT_WRITE_FAILED = 'Scout could not write the Markdown file.';
export const SHORTCUT_TAKEN_MESSAGE = 'That shortcut is taken';
export const WAYLAND_SHORTCUT_HINT =
  'Your desktop (Wayland) does not let apps register global shortcuts. Add a custom shortcut in your system settings that runs:';
export const STALE_CURSOR_HINT =
  "Your desktop (Wayland) does not tell Scout where the pointer is outside Scout's own windows, so the button follows the cursor only when the pointer passes over one of them.";
export const MISSING_MONITOR_HINT =
  'The monitor chosen for the floating button is not connected; the button is on the primary display until it is back.';
export const DEFAULT_BUBBLE: LocalBubbleSettings = {
  enabled: true,
  corner: 'bottom-right',
  display: 'cursor',
};

const SHORTCUT_VAULT_KEY = 'quick-ask.shortcut';
const CONTROL_MODIFIERS = new Set([
  'commandorcontrol',
  'cmdorctrl',
  'command',
  'cmd',
  'control',
  'ctrl',
]);
const RESERVED_WITH_CONTROL = new Map([
  ['c', 'copy'],
  ['v', 'paste'],
  ['x', 'cut'],
  ['z', 'undo'],
  ['a', 'select all'],
  ['q', 'quitting an app'],
  ['w', 'closing a tab or window'],
  ['tab', 'switching tabs or windows'],
]);
const RESERVED_WITH_ALT = new Map([
  ['f4', 'closing a window'],
  ['tab', 'switching windows'],
]);
const BUBBLE_VAULT_KEY = 'quick-ask.bubble';
const WINDOW_VAULT_KEY = 'quick-ask.window';
const BUBBLE_SIZE = { width: 64, height: 64 };
const QUICK_ASK_SIZE = { width: 440, height: 700 };
const QUICK_ASK_MIN_SIZE = { minWidth: 360, minHeight: 240 };
const CARD_SIZE = { width: 244, height: 200 };
const CARD_OVERHANG = 12;
const CORNERS: ReadonlySet<unknown> = new Set([
  'bottom-right',
  'bottom-left',
  'top-right',
  'top-left',
]);
const DISPLAYS: ReadonlySet<unknown> = new Set(['cursor', 'primary']);
const CURSOR_POLL_MS = 500;
const STALE_CURSOR_POLLS = 60;
const ID_PATTERN = /^[A-Za-z0-9-]{1,64}$/;
const BASE64_PATTERN = /^[A-Za-z0-9+/]*={0,2}$/;
const MAX_TEXT_CHARS = 4000;
const MAX_TRANSCRIPT_ASK_CHARS = MAX_TEXT_CHARS + 24_200;
const MAX_SCREEN_TEXT_EXTRA_CHARS = 5_200;
const MAX_LAST_ANSWER_EXTRA_CHARS = 4_200;
const MAX_REVIEW_EXTRA_CHARS = 120_000;
const TRANSCRIPT_MARKERS = MEETING_WINDOWS.map(transcriptMarker);
const CONTEXT_MARKERS = [...TRANSCRIPT_MARKERS, SCREEN_TEXT_MARKER, LAST_ANSWER_MARKER];
const FINDINGS_FENCE = '```scout-findings';
const FINDING_ID_PATTERN = /^[A-Za-z0-9-]{1,40}$/;
const MAX_DECISIONS = 1_000;
const MAX_DECISION_TEXT_CHARS = 8_000;
const DECISIONS_REFUSED = 'Scout could not read those decisions.';
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
const STREAM_CURSOR = '▌';
const TRANSCRIPT_KINDS: ReadonlySet<string> = new Set(['partial', 'final', 'complete', 'fail']);
// ponytail: the shared conversation keeps the last 200 turns in memory only; persist or page it if the owner wants more.
const MAX_TURNS = 200;
const MAX_NOTICE_CHARS = 32_000;
const RECORDER_GONE = ['destroyed', 'render-process-gone', 'did-start-navigation'] as const;

/** The page that asked to record a shortcut; main takes the shortcut back if it is closed, crashes or reloads. */
type RecorderPage = Pick<NodeJS.EventEmitter, 'once' | 'removeListener'>;

export interface QuickAskDeps {
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>;
  supervisor: Pick<Supervisor, 'writeStdin' | 'onEvent' | 'status'>;
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
    | 'getDisplayNearestPoint'
    | 'getCursorScreenPoint'
    | 'getPrimaryDisplay'
    | 'getAllDisplays'
    | 'on'
    | 'removeListener'
  >;
  /** Runs `callback` once after `ms`; the returned function cancels it. The floating button's cursor check re-arms it every 500 ms. */
  setTimer: (ms: number, callback: () => void) => () => void;
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
    properties: 'showOverwriteConfirmation'[];
  }) => Promise<{ canceled: boolean; filePath?: string }>;
  /** Writes the exported Markdown to the file the owner chose. */
  writeFile: (file: string, text: string) => Promise<void>;
  /** The owner's Documents folder, where the save dialog opens. */
  documentsDir: () => string;
  /** The resolved theme's page background, read each time the quick-ask window is created; the floating button stays transparent. */
  background: () => string;
  /** Called when the first live listen starts and when the last one ends. */
  onListeningChanged?: (listening: boolean) => void;
  /** Called when a turn starts running and when the last running turn ends. */
  onAnsweringChanged?: (answering: boolean) => void;
  /** True while main restarts the worker; a question asked or held meanwhile waits for the worker to run. */
  restarting?: () => boolean;
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
  /** True while a live listen is open, until the worker completes its last one. */
  isListening(): boolean;
  /** True while a turn runs, a question held for the worker included. */
  isAnswering(): boolean;
  /** Main's restart settled: a question held through it is sent if the worker runs, else refused. */
  restartSettled(): void;
  /** Which of this module's windows `sender`, an IPC call's webContents, belongs to, or null for neither. */
  windowOf(sender: unknown): 'quick-ask' | 'floating-button' | null;
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

/** True for a URL of Scout's own page: the renderer's index file, or the dev server's origin. */
export function appPageCheck(app: AppPage): (raw: string | undefined) => boolean {
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
  voice: boolean;
  issues: boolean;
  issueTitles?: readonly string[];
} {
  const r = (typeof req === 'object' && req !== null ? req : {}) as Record<string, unknown>;
  const id = typeof r['id'] === 'string' ? r['id'] : '';
  if (!ID_PATTERN.test(id) || id.startsWith('r-') || id.startsWith('m-')) {
    fail('local/invalid-request', 'The question has no valid id.');
  }
  const image = toImage(r['image']);
  const screenshot = image.image !== undefined;

  if (typeof r['text'] === 'string') {
    const sent = r['context'];
    if (sent !== undefined && typeof sent !== 'string') {
      fail('local/invalid-request', 'The context sent with the question is not text.');
    }
    if (
      typeof sent === 'string' &&
      sent.length > 0 &&
      !CONTEXT_MARKERS.some((contextMarker) => sent.startsWith(contextMarker))
    ) {
      fail(
        'local/invalid-request',
        'The context must start with a transcript, screen text or last answer block.',
      );
    }
    const text = (r['text'].trim() + (sent ?? '')).trim();
    const marker = firstAt(text, TRANSCRIPT_MARKERS);
    const screenMarker = text.indexOf(SCREEN_TEXT_MARKER);
    const lastAnswer = text.indexOf(LAST_ANSWER_MARKER);
    const issuesMarker = text.indexOf(ISSUES_MARKER);
    const markers = [marker, screenMarker, lastAnswer, issuesMarker].filter((at) => at !== -1);
    const question = markers.length === 0 ? text : text.slice(0, Math.min(...markers));
    const contextAt = [marker, screenMarker, lastAnswer].filter((at) => at !== -1);
    const split =
      contextAt.length === 0
        ? { text }
        : {
            text: text.slice(0, Math.min(...contextAt)),
            context: text.slice(Math.min(...contextAt)),
          };
    const limit =
      (marker === -1 ? MAX_TEXT_CHARS : MAX_TRANSCRIPT_ASK_CHARS) +
      (screenMarker === -1 ? 0 : MAX_SCREEN_TEXT_EXTRA_CHARS) +
      (lastAnswer === -1 ? 0 : MAX_LAST_ANSWER_EXTRA_CHARS) +
      (issuesMarker === -1 ? 0 : MAX_REVIEW_EXTRA_CHARS);
    if (question.length === 0 || question.length > MAX_TEXT_CHARS || text.length > limit) {
      fail('local/invalid-request', `Type a question of 1 to ${MAX_TEXT_CHARS} characters.`);
    }
    return {
      id,
      question: question.trim(),
      line: JSON.stringify({ type: 'ask', id, ...split, ...image }),
      screenshot,
      transcript: marker !== -1,
      screenText: screenMarker !== -1,
      meeting: r['meeting'] === true,
      voice: r['voice'] === true,
      issues: issuesMarker !== -1,
      ...(issuesMarker === -1 ? {} : titlesOf(text.slice(issuesMarker + ISSUES_MARKER.length))),
    };
  }

  fail('local/invalid-request', `Type a question of 1 to ${MAX_TEXT_CHARS} characters.`);
}

function titlesOf(json: string): { issueTitles?: readonly string[] } {
  let data: unknown;
  try {
    data = JSON.parse(json);
  } catch {
    return {};
  }
  const issues = (data as { issues?: unknown } | null)?.issues;
  if (!Array.isArray(issues)) return {};
  const titles = issues.flatMap((issue: unknown) => {
    const title = (issue as { title?: unknown } | null)?.title;
    return typeof title === 'string' && title.trim().length > 0 ? [title.trim()] : [];
  });
  return titles.length === 0 ? {} : { issueTitles: titles };
}

function toDecision(raw: unknown): LocalFindingDecision {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const pick = r['pick'];
  const dismissed = r['dismissed'];
  if ((pick !== 'yes' && pick !== 'no' && pick !== null) || typeof dismissed !== 'boolean') {
    fail('local/invalid-request', DECISIONS_REFUSED);
  }
  if (r['edited'] === undefined) return { pick, dismissed };
  const edited = (
    typeof r['edited'] === 'object' && r['edited'] !== null ? r['edited'] : {}
  ) as Record<string, unknown>;
  const { why, fix } = edited;
  if (
    typeof why !== 'string' ||
    typeof fix !== 'string' ||
    why.length > MAX_DECISION_TEXT_CHARS ||
    fix.length > MAX_DECISION_TEXT_CHARS
  ) {
    fail('local/invalid-request', DECISIONS_REFUSED);
  }
  return { pick, dismissed, edited: { why, fix } };
}

function toDecisions(raw: unknown): Record<string, LocalFindingDecision> {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) {
    fail('local/invalid-request', DECISIONS_REFUSED);
  }
  const entries = Object.entries(raw);
  if (entries.length > MAX_DECISIONS || entries.some(([id]) => !FINDING_ID_PATTERN.test(id))) {
    fail('local/invalid-request', DECISIONS_REFUSED);
  }
  return Object.fromEntries(entries.map(([id, decision]) => [id, toDecision(decision)]));
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

/**
 * Why an accelerator cannot be the quick-ask shortcut: it has no modifier, so it would fire while typing, or
 * the system reserves it; null when it can be registered.
 */
function shortcutRefusal(accelerator: string): string | null {
  const parts = accelerator.toLowerCase().split('+');
  const key = parts.pop() ?? '';
  let use: string | undefined;
  if (parts.length === 0 && (key === 'super' || key === 'meta')) use = 'opening its launcher';
  else if (parts.length === 0) {
    return `"${accelerator}" has no modifier, so it would fire whenever you type it; add Ctrl, Alt, Super or Cmd.`;
  } else if (parts.every((part) => part === 'shift')) {
    return `"${accelerator}" has no modifier but Shift, so it would fire whenever you type it; add Ctrl, Alt, Super or Cmd.`;
  } else if (parts.length === 1 && CONTROL_MODIFIERS.has(parts[0]!)) {
    use = RESERVED_WITH_CONTROL.get(key);
  } else if (parts.length === 1 && parts[0] === 'alt') {
    use = RESERVED_WITH_ALT.get(key);
  }
  return use === undefined
    ? null
    : `"${accelerator}" is reserved by the system for ${use}; choose another.`;
}

function toBubbleSettings(raw: unknown): LocalBubbleSettings | null {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { enabled, corner, display } = r;
  const known = DISPLAYS.has(display) || Number.isInteger(display);
  if (typeof enabled !== 'boolean' || !CORNERS.has(corner) || !known) return null;
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
  let cardWindow: BrowserWindow | null = null;
  let bubble = DEFAULT_BUBBLE;
  let savedBounds: Rectangle | null = null;
  let pendingCaptures = 0;
  const sessions = new Map<string, LocalListenLane>();
  const meetingSessions = new Set<string>();
  const stoppingSessions = new Set<string>();
  const sessionSenders = new Map<string, unknown>();
  let listeningPushed = false;
  const reads = new Map<string, (outcome: LocalReadTextResponse | Error) => void>();
  let readCount = 0;
  const meetingRequests = new Map<string, (outcome: Record<string, unknown> | Error) => void>();
  let meetingCount = 0;
  let systemAudio: { id: string; at: number } | null = null;
  let held: { id: string; line: string } | null = null;
  let answeringPushed = false;
  let cancelCursorCheck: (() => void) | null = null;
  let bubbleDisplayId: number | null = null;
  let lastCursor: Point | null = null;
  let stillChecks = 0;
  let staleCursor = false;
  let staleLogged = false;
  let missingNoticed = false;
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
    const answering = turns.some((turn) => turn.state === 'running');
    if (answering === answeringPushed) return;
    answeringPushed = answering;
    deps.onAnsweringChanged?.(answering);
  }

  function isRunning(id: string): boolean {
    return turns.some((turn) => turn.id === id && turn.state === 'running');
  }

  function setDecisions(req: unknown): void {
    const r = (typeof req === 'object' && req !== null ? req : {}) as Record<string, unknown>;
    const index = turns.findIndex((turn) => turn.id === r['turnId']);
    const turn = turns[index];
    if (turn === undefined || turn.state !== 'done' || !turn.reply.includes(FINDINGS_FENCE)) {
      fail('local/invalid-request', 'That turn has no findings to decide on.');
    }
    turns[index] = { ...turn, decisions: toDecisions(r['decisions']) };
    broadcast();
  }

  function reshape(id: string, change: (turn: LocalTurn) => LocalTurn): void {
    const index = turns.findIndex((turn) => turn.id === id && turn.state === 'running');
    if (index === -1) return;
    turns[index] = change(turns[index]!);
    broadcast();
  }

  function failed(turn: LocalTurn, text: string): LocalTurn {
    const reply = turn.reply.endsWith(STREAM_CURSOR) ? turn.reply.slice(0, -1) : turn.reply;
    return reply.length > 0
      ? { ...turn, reply, failure: text, state: 'failed' }
      : { ...turn, reply: text, state: 'failed' };
  }

  function failRunning(text: string): void {
    for (const turn of turns.filter((t) => t.state === 'running' && t.id !== held?.id)) {
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

  function chosenDisplay(cursor: Point): Display {
    const { screen } = deps;
    if (bubble.display === 'cursor') return screen.getDisplayNearestPoint(cursor);
    if (bubble.display === 'primary') return screen.getPrimaryDisplay();
    const id = bubble.display;
    const chosen = screen.getAllDisplays().find((d) => d.id === id);
    if (chosen !== undefined) {
      missingNoticed = false;
      return chosen;
    }
    if (!missingNoticed) {
      missingNoticed = true;
      logger.warn('quick-ask: the floating button monitor is not connected; using the primary', {
        displayId: id,
      });
      notices += 1;
      append({
        id: `notice:${notices}`,
        question: '',
        reply: MISSING_MONITOR_HINT,
        citations: [],
        state: 'done',
        askedAt: Date.now(),
        notice: true,
      });
    }
    return screen.getPrimaryDisplay();
  }

  function inCorner(size: { width: number; height: number }): {
    bounds: Rectangle;
    display: Display;
    cursor: Point;
  } {
    const cursor = deps.screen.getCursorScreenPoint();
    const display = chosenDisplay(cursor);
    const width = Math.min(size.width, display.workArea.width);
    const height = Math.min(size.height, display.workArea.height);
    const { x, y } = placeInCorner({ width, height }, display.workArea, bubble.corner);
    return { bounds: { x, y, width, height }, display, cursor };
  }

  function place(target: BrowserWindow, size: { width: number; height: number }): void {
    const { bounds, display, cursor } = inCorner(size);
    if (target === bubbleWindow) hideCard();
    target.setPosition(bounds.x, bounds.y);
    if (target === bubbleWindow) {
      bubbleDisplayId = display.id;
      logger.info('quick-ask: floating button placed', {
        displayId: display.id,
        workArea: display.workArea,
        displays: deps.screen.getAllDisplays().length,
        cursor,
        setting: bubble.display,
      });
    }
  }

  function monitorName(display: Display): string {
    const fallback = display.internal ? 'Built-in display' : 'External display';
    const label = display.label.trim();
    return `${label === '' ? fallback : label} ${display.bounds.width}×${display.bounds.height}`;
  }

  function bubbleStatus(): LocalBubbleStatus {
    const primaryId = deps.screen.getPrimaryDisplay().id;
    const displays = deps.screen.getAllDisplays();
    const names = displays.map(monitorName);
    const monitors = displays.map((d, i) => ({
      id: d.id,
      name:
        names.indexOf(names[i]!) === names.lastIndexOf(names[i]!)
          ? names[i]!
          : `${names[i]} at ${d.bounds.x}, ${d.bounds.y}`,
      primary: d.id === primaryId,
    }));
    const chosen = bubble.display;
    const missing = typeof chosen === 'number' && !monitors.some((m) => m.id === chosen);
    const hint =
      chosen === 'cursor' && staleCursor
        ? STALE_CURSOR_HINT
        : missing
          ? MISSING_MONITOR_HINT
          : null;
    return { ...bubble, monitors, hint };
  }

  function pushBubble(): void {
    sendToWindows(CHANNEL.localBubbleChanged, bubbleStatus());
  }

  function bubbleShown(): boolean {
    return bubbleWindow !== null && !bubbleWindow.isDestroyed() && bubbleWindow.isVisible();
  }

  function watchCursor(cursor: Point): void {
    if (!wayland) return;
    const still = lastCursor !== null && lastCursor.x === cursor.x && lastCursor.y === cursor.y;
    lastCursor = cursor;
    stillChecks = still ? stillChecks + 1 : 0;
    if (!still && staleCursor) {
      staleCursor = false;
      pushBubble();
      return;
    }
    const displays = deps.screen.getAllDisplays().length;
    if (staleCursor || stillChecks < STALE_CURSOR_POLLS || displays < 2) return;
    staleCursor = true;
    pushBubble();
    if (staleLogged) return;
    staleLogged = true;
    logger.info(
      'quick-ask: the cursor has not moved for 30 s; the system may not report the pointer outside Scout windows (Wayland/XWayland), so the floating button cannot follow it',
      { cursor, displays },
    );
  }

  function checkCursor(): void {
    if (!bubbleShown()) return;
    const cursor = deps.screen.getCursorScreenPoint();
    if (deps.screen.getDisplayNearestPoint(cursor).id !== bubbleDisplayId) {
      place(bubbleWindow!, BUBBLE_SIZE);
    }
    watchCursor(cursor);
  }

  function followCursor(): void {
    if (cancelCursorCheck !== null || bubble.display !== 'cursor') return;
    stillChecks = 0;
    const arm = (): void => {
      cancelCursorCheck = deps.setTimer(CURSOR_POLL_MS, () => {
        checkCursor();
        if (cancelCursorCheck !== null) arm();
      });
    };
    arm();
  }

  function stopFollowingCursor(): void {
    cancelCursorCheck?.();
    cancelCursorCheck = null;
  }

  function displaysChanged(): void {
    if (bubbleShown()) place(bubbleWindow!, BUBBLE_SIZE);
    pushBubble();
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
    followCursor();
  }

  function destroyBubble(): void {
    stopFollowingCursor();
    if (bubbleWindow !== null && !bubbleWindow.isDestroyed()) bubbleWindow.destroy();
    bubbleWindow = null;
    if (cardWindow !== null && !cardWindow.isDestroyed()) cardWindow.destroy();
    cardWindow = null;
  }

  function hideCard(): void {
    if (cardWindow !== null && !cardWindow.isDestroyed()) cardWindow.hide();
  }

  function cardBounds(button: Rectangle): Rectangle | null {
    const center = { x: button.x + button.width / 2, y: button.y + button.height / 2 };
    const area = deps.screen.getDisplayNearestPoint(center).workArea;
    const x = bubble.corner.endsWith('right')
      ? button.x + button.width + CARD_OVERHANG - CARD_SIZE.width
      : button.x - CARD_OVERHANG;
    const y = bubble.corner.startsWith('bottom')
      ? button.y - CARD_SIZE.height
      : button.y + button.height;
    const inside =
      x >= area.x &&
      y >= area.y &&
      x + CARD_SIZE.width <= area.x + area.width &&
      y + CARD_SIZE.height <= area.y + area.height;
    return inside ? { x, y, ...CARD_SIZE } : null;
  }

  function bubbleCard(req: unknown): LocalBubbleCardResponse {
    const shown = (req as { shown?: unknown } | null | undefined)?.shown;
    if (typeof shown !== 'boolean') fail('local/invalid-request', 'Say whether the card shows.');
    if (!shown || !bubbleShown()) {
      hideCard();
      return { shown: false };
    }
    try {
      const bounds = cardBounds(bubbleWindow!.getBounds());
      if (bounds === null) {
        hideCard();
        return { shown: false };
      }
      if (cardWindow === null || cardWindow.isDestroyed()) {
        cardWindow = deps.createWindow({
          ...CARD_SIZE,
          show: false,
          frame: false,
          transparent: true,
          backgroundColor: '#00000000',
          alwaysOnTop: true,
          skipTaskbar: true,
          resizable: false,
          focusable: false,
          hasShadow: false,
          webPreferences,
        });
        cardWindow.setIgnoreMouseEvents(true);
        prepare(cardWindow, 'floating-card', 'screen-saver');
      }
      cardWindow.setBounds(bounds);
      cardWindow.showInactive();
      cardWindow.setAlwaysOnTop(true, 'screen-saver');
      return { shown: true };
    } catch (err: unknown) {
      logger.warn('quick-ask: the floating button card was not shown', {
        code: serializeError(err).code,
      });
      hideCard();
      return { shown: false };
    }
  }

  function rememberBounds(target: BrowserWindow): void {
    savedBounds = target.getBounds();
    vault.store(WINDOW_VAULT_KEY, JSON.stringify(savedBounds)).catch((err: unknown) => {
      logger.warn('quick-ask: quick-ask window place not saved; it lasts until Scout quits', {
        code: serializeError(err).code,
      });
    });
  }

  function fitOnDisplay(bounds: Rectangle | null): Rectangle | null {
    if (bounds === null) return null;
    const overlap = ({ workArea: a }: Display): number =>
      Math.max(0, Math.min(bounds.x + bounds.width, a.x + a.width) - Math.max(bounds.x, a.x)) *
      Math.max(0, Math.min(bounds.y + bounds.height, a.y + a.height) - Math.max(bounds.y, a.y));
    const best = deps.screen
      .getAllDisplays()
      .reduce<Display | null>(
        (most, d) => (overlap(d) > (most ? overlap(most) : 0) ? d : most),
        null,
      );
    return best === null ? null : fitInWorkArea(bounds, best.workArea);
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
      const kept = fitOnDisplay(savedBounds);
      const bounds = kept ?? inCorner(QUICK_ASK_SIZE).bounds;
      const created = deps.createWindow({
        width: bounds.width,
        height: bounds.height,
        ...QUICK_ASK_MIN_SIZE,
        show: false,
        frame: false,
        alwaysOnTop: true,
        resizable: true,
        skipTaskbar: true,
        backgroundColor: deps.background(),
        webPreferences,
      });
      created.on('blur', () => {
        if (!created.isDestroyed() && pendingCaptures === 0 && sessions.size === 0) hide();
      });
      const contents = created.webContents;
      created.on('close', () => rememberBounds(created));
      created.on('closed', () => {
        stopSessionsOf(contents);
        showBubble();
      });
      contents.on('render-process-gone', () => stopSessionsOf(contents));
      prepare(created, 'quick-ask', 'floating');
      if (kept !== null) created.setBounds(kept);
      else created.setPosition(bounds.x, bounds.y);
      window = created;
    } else {
      const now = window.getBounds();
      const kept = fitOnDisplay(now) ?? inCorner(QUICK_ASK_SIZE).bounds;
      const moved = (['x', 'y', 'width', 'height'] as const).some((k) => kept[k] !== now[k]);
      if (moved) window.setBounds(kept);
    }
    stopFollowingCursor();
    hideCard();
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
  if (shortcut !== DEFAULT_QUICK_ASK_SHORTCUT && shortcutRefusal(shortcut) !== null) {
    const refused = shortcut;
    shortcut = DEFAULT_QUICK_ASK_SHORTCUT;
    logger.warn('quick-ask: stored shortcut no longer allowed; reset to the default', {
      refused,
    });
    notices += 1;
    append({
      id: `notice:${notices}`,
      question: '',
      reply: `Your quick-ask shortcut ${refused} is no longer allowed; it was reset to ${shortcut}.`,
      citations: [],
      state: 'done',
      askedAt: Date.now(),
      notice: true,
    });
    try {
      await vault.store(SHORTCUT_VAULT_KEY, shortcut);
    } catch (err: unknown) {
      logger.warn('quick-ask: reset shortcut not saved', { code: serializeError(err).code });
    }
  }
  let registered = false;
  let suspended = false;
  let unwatchRecorder: (() => void) | null = null;
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

  async function setBubble(req: unknown): Promise<LocalBubbleStatus> {
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
    if (next.display !== 'cursor') stopFollowingCursor();
    if (next.enabled) showBubble();
    else destroyBubble();
    logger.info('quick-ask: floating button changed', { ...next });
    pushBubble();
    return bubbleStatus();
  }

  function stopWatchingRecorder(): void {
    unwatchRecorder?.();
    unwatchRecorder = null;
  }

  function takeShortcutBack(): void {
    stopWatchingRecorder();
    if (!suspended) return;
    suspended = false;
    try {
      registered = globalShortcut.register(shortcut, open);
    } catch {
      registered = false;
    }
    if (!registered) logger.warn('quick-ask: shortcut not registered again', { shortcut });
  }

  function suspendShortcut(req: unknown, page: RecorderPage): LocalShortcut {
    const flag = (req as Record<string, unknown> | null)?.['suspended'];
    if (typeof flag !== 'boolean') {
      fail('quick-ask/invalid-shortcut', 'Say whether the quick-ask shortcut is suspended.');
    }
    if (!flag) {
      takeShortcutBack();
      return shortcutStatus();
    }
    if (!registered) return shortcutStatus();
    globalShortcut.unregister(shortcut);
    registered = false;
    suspended = true;
    const back = (): void => takeShortcutBack();
    for (const gone of RECORDER_GONE) page.once(gone, back);
    unwatchRecorder = () => {
      for (const gone of RECORDER_GONE) page.removeListener(gone, back);
    };
    logger.info('quick-ask: shortcut suspended while Settings records one');
    return shortcutStatus();
  }

  async function setShortcut(req: unknown): Promise<LocalShortcut> {
    const raw = (req as Record<string, unknown> | null)?.['accelerator'];
    const next = typeof raw === 'string' ? raw.trim() : '';
    if (next.length === 0 || next.length > 64)
      fail('quick-ask/invalid-shortcut', 'Type a shortcut.');
    const refusal = shortcutRefusal(next);
    if (refusal !== null) fail('quick-ask/invalid-shortcut', refusal);
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
    suspended = false;
    stopWatchingRecorder();
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
    const {
      id,
      question,
      line,
      screenshot,
      transcript,
      screenText,
      meeting,
      voice,
      issues,
      issueTitles,
    } = toWorkerRequest(req);
    if (turns.some((turn) => turn.id === id)) {
      fail('local/invalid-request', 'That question is already running.');
    }
    stopRunning();

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
      ...(voice ? { voice: true as const } : {}),
      ...(issues ? { issues: true as const } : {}),
      ...(issueTitles === undefined ? {} : { issueTitles }),
    });
    if (deliver(id, line)) return;
    if (willRun(supervisor.status)) {
      held = { id, line };
      reshape(id, (turn) => ({ ...turn, activity: STARTING_ACTIVITY }));
      return;
    }
    refuse(id);
    fail('local/not-running', NOT_RUNNING_MESSAGE);
  }

  function deliver(id: string, line: string): boolean {
    return supervisor.writeStdin(line, (err) => {
      logger.warn('quick-ask: question not delivered to the worker', { error: err.message });
      reshape(id, (turn) => ({ ...turn, reply: STOPPED_REPLY, state: 'failed' }));
    });
  }

  function refuse(id: string): void {
    reshape(id, ({ activity: _cleared, ...turn }) => ({
      ...turn,
      reply: NOT_RUNNING_MESSAGE,
      state: 'failed',
      notRunning: true,
    }));
  }

  /**
   * True while the worker is on its way to running: starting, between a restart's stop and start, or crashed with an
   * automatic restart still to come.
   */
  function willRun(status: WorkerStatus): boolean {
    if (status.state === 'starting') return true;
    if (status.state === 'crashed') return status.gaveUp !== true;
    return status.state === 'stopped' && deps.restarting?.() === true;
  }

  /** Sends the question held while Scout started once it runs, or refuses it once the start ended any other way. */
  function releaseHeld(status: WorkerStatus): void {
    if (held === null || willRun(status)) return;
    const { id, line } = held;
    held = null;
    if (status.state === 'running') {
      reshape(id, ({ activity: _cleared, ...turn }) => turn);
      if (deliver(id, line)) return;
    }
    refuse(id);
  }

  function cancel(req: unknown): void {
    const id = (req as Record<string, unknown> | null)?.['id'];
    if (typeof id !== 'string' || !isRunning(id)) return;
    if (turns.some((turn) => turn.id === id && turn.issues === true))
      fail('local/busy', BUSY_REPLY);
    if (held?.id === id) {
      held = null;
      reshape(id, ({ activity: _cleared, ...turn }) => failed(turn, LOCAL_CANCELLED_REPLY));
      return;
    }
    supervisor.writeStdin(JSON.stringify({ type: 'cancel', id }), () => {});
  }

  /** Cancels every running turn, refusing with `local/busy` while one carries a card's issues, which Scout queues for the owner's approval. */
  function stopRunning(): void {
    const running = turns.filter((turn) => turn.state === 'running');
    if (running.some((turn) => turn.issues === true)) {
      fail('local/busy', BUSY_REPLY);
    }
    for (const turn of running) cancel({ id: turn.id });
  }

  function clearTurns(): void {
    stopRunning();
    turns.length = 0;
    broadcast();
    supervisor.writeStdin(JSON.stringify({ type: 'reset' }), () => {});
  }

  function pushTranscript(event: LocalTranscriptEvent): void {
    sendToWindows(CHANNEL.localTranscript, event);
  }

  function disarm(id: string): void {
    if (systemAudio?.id === id) systemAudio = null;
  }

  function forget(id: string): void {
    sessions.delete(id);
    meetingSessions.delete(id);
    stoppingSessions.delete(id);
    sessionSenders.delete(id);
    disarm(id);
  }

  function stopSessionsOf(sender: unknown): void {
    for (const [id, from] of [...sessionSenders]) if (from === sender) stopListening({ id });
  }

  function pushListening(): void {
    const listening = sessions.size > 0;
    if (listening === listeningPushed) return;
    listeningPushed = listening;
    sendToWindows(CHANNEL.localListeningChanged, { listening });
    deps.onListeningChanged?.(listening);
  }

  function endListening(id: string, error?: string): void {
    const lane = sessions.get(id);
    if (lane === undefined) return;
    forget(id);
    pushListening();
    pushTranscript({
      id,
      lane,
      text: '',
      final: true,
      ended: true,
      ...(error != null ? { error } : {}),
    });
  }

  function listen(req: unknown, sender: unknown): LocalListenResponse {
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
      if (openLane === lane && meetingSessions.has(open))
        fail('local/busy', MEETING_HOLDS_LANE_REPLY);
    }
    for (const [open, openLane] of sessions) {
      if (openLane !== lane) continue;
      const stopped = stoppingSessions.has(open);
      if (!stopped) supervisor.writeStdin(JSON.stringify({ type: 'stop', id: open }), () => {});
      forget(open);
      pushTranscript({
        id: open,
        lane: openLane,
        text: '',
        final: true,
        ended: true,
        ...(stopped
          ? {}
          : { error: openLane === 'me' ? LISTEN_TAKEN_REPLY : SYSTEM_AUDIO_TAKEN_REPLY }),
      });
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
    if (!accepted) {
      pushListening();
      fail('local/not-running', NOT_RUNNING_MESSAGE);
    }
    sessions.set(id, lane);
    sessionSenders.set(id, sender);
    if ((req as Record<string, unknown> | null | undefined)?.['meeting'] === true) {
      meetingSessions.add(id);
    }
    pushListening();
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
    meetingSessions.delete(id);
    stoppingSessions.add(id);
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
    const keepKey = createHash('sha256').update(meeting.startedAt).digest('base64url');
    const reply = await askMeetings('keep', { keepKey, meeting }, 'kept');
    const meetingId = reply['meetingId'];
    if (typeof meetingId !== 'string') fail('local/meeting-failed', MEETING_FAILED_REPLY);
    sendToWindows(CHANNEL.localMeetingsChanged, {});
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
      properties: ['showOverwriteConfirmation'],
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
    sendToWindows(CHANNEL.localMeetingsChanged, {});
    return { deleted: Number.isInteger(count) ? (count as number) : 0 };
  }

  const unsubscribe = supervisor.onEvent((event: WorkerEvent) => {
    if (event.kind === 'state') releaseHeld(event.status);
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
  ipcMain.handle(CHANNEL.localSetDecisions, (_event, req: unknown) => setDecisions(req));
  ipcMain.handle(CHANNEL.localHide, () => hide());
  ipcMain.handle(CHANNEL.localGetShortcut, () => shortcutStatus());
  ipcMain.handle(CHANNEL.localSetShortcut, (_event, req: unknown) => setShortcut(req));
  ipcMain.handle(CHANNEL.localSuspendShortcut, (event, req: unknown) =>
    suspendShortcut(req, event.sender),
  );
  ipcMain.handle(CHANNEL.localOpen, () => open());
  ipcMain.handle(CHANNEL.localGetBubble, () => bubbleStatus());
  ipcMain.handle(CHANNEL.localSetBubble, (_event, req: unknown) => setBubble(req));
  ipcMain.handle(CHANNEL.localBubbleCard, (_event, req: unknown) => bubbleCard(req));
  ipcMain.handle(CHANNEL.localListen, (event, req: unknown) => listen(req, event.sender));
  ipcMain.handle(CHANNEL.localAudio, (_event, req: unknown) => audio(req));
  ipcMain.handle(CHANNEL.localStopListening, (_event, req: unknown) => stopListening(req));
  ipcMain.handle(CHANNEL.localListening, () => ({ listening: sessions.size > 0 }));
  ipcMain.handle(CHANNEL.localReadText, (_event, req: unknown) => readText(req));
  const speech = registerSpeech({ ipcMain, supervisor, vault, logger });
  ipcMain.handle(CHANNEL.localKeepMeeting, (_event, req: unknown) => keepMeeting(req));
  ipcMain.handle(CHANNEL.localMeetings, (_event, req: unknown) => listMeetings(req));
  ipcMain.handle(CHANNEL.localExportMeeting, (_event, req: unknown) => exportMeeting(req));
  ipcMain.handle(CHANNEL.localDeleteMeetings, (_event, req: unknown) => deleteMeetings(req));
  openCurrent = open;
  deps.screen.on('display-added', displaysChanged);
  deps.screen.on('display-removed', displaysChanged);
  deps.screen.on('display-metrics-changed', displaysChanged);

  const contentsOf = (target: BrowserWindow | null): unknown =>
    target !== null && !target.isDestroyed() ? target.webContents : undefined;

  return {
    windowOf: (sender) => {
      if (sender == null) return null;
      if (sender === contentsOf(window)) return 'quick-ask';
      return sender === contentsOf(bubbleWindow) ? 'floating-button' : null;
    },
    isListening: () => sessions.size > 0,
    isAnswering: () => turns.some((turn) => turn.state === 'running'),
    restartSettled: () => releaseHeld(supervisor.status),
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
      if (
        parsed === null ||
        applyMeeting(parsed) ||
        applyRead(parsed) ||
        speech.apply(parsed) ||
        applyTranscript(parsed)
      )
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
          askedAt: Date.now(),
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
      stopWatchingRecorder();
      deps.screen.removeListener('display-added', displaysChanged);
      deps.screen.removeListener('display-removed', displaysChanged);
      deps.screen.removeListener('display-metrics-changed', displaysChanged);
      unsubscribe();
      if (registered) globalShortcut.unregister(shortcut);
      registered = false;
      for (const channel of [
        CHANNEL.localAsk,
        CHANNEL.localCancel,
        CHANNEL.localGetTurns,
        CHANNEL.localClearTurns,
        CHANNEL.localSetDecisions,
        CHANNEL.localHide,
        CHANNEL.localGetShortcut,
        CHANNEL.localSetShortcut,
        CHANNEL.localSuspendShortcut,
        CHANNEL.localOpen,
        CHANNEL.localGetBubble,
        CHANNEL.localSetBubble,
        CHANNEL.localBubbleCard,
        CHANNEL.localListen,
        CHANNEL.localAudio,
        CHANNEL.localStopListening,
        CHANNEL.localListening,
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
      speech.dispose();
      sessions.clear();
      meetingSessions.clear();
      stoppingSessions.clear();
      sessionSenders.clear();
      systemAudio = null;
      if (window !== null && !window.isDestroyed()) window.destroy();
      window = null;
      destroyBubble();
      turns.length = 0;
    },
  };
}
