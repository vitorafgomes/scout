import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type AppNotification,
  type NotificationActionId,
  type NotificationKind,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import { noticeNotification } from './sources.js';

/** An entry as a source records it; the store sets `read`, and `at` when the source gives none. */
export type NotificationInput = Omit<AppNotification, 'read' | 'at'> & { readonly at?: number };

export interface NotificationStoreDeps {
  vault: Pick<Vault, 'get' | 'store'>;
  /** Sends the whole list to every open window. */
  push(list: readonly AppNotification[]): void;
  now(): number;
  /** Runs `callback` once after `ms`; returns the cancel function. */
  setTimer(ms: number, callback: () => void): () => void;
  logger: Logger;
}

export const MAX_NOTIFICATIONS = 200;
export const SAVE_DELAY_MS = 1_000;
const VAULT_KEY = 'app.notifications';
const KINDS = new Set<NotificationKind>([
  'running',
  'done',
  'failed',
  'paused',
  'github',
  'mention',
]);
const SOURCES = new Set<AppNotification['source']>(['worker', 'verdict', 'notice', 'github']);
const NOTICE_KINDS = new Set(['info', 'success', 'error']);
const ACTIONS = new Set<NotificationActionId>([
  'whisper-download-base',
  'open-models-transcription',
  'open-approvals',
  'review-pull-request',
]);
export const CLOSED_BEFORE_DONE = 'Scout closed before this finished.';
const MAX_NOTICE_CHARS = 1_000;
const REFUSED = 'Scout could not read that request.';

/**
 * The desktop's notification center: at most 200 entries, newest first, kept in memory and in the vault under
 * `app.notifications`, written a second after the last change; every change is pushed to every window.
 */
export class NotificationStore {
  private items: readonly AppNotification[] = [];
  private cancelSave: (() => void) | null = null;
  private sequence = 0;
  private readFailed = false;

  constructor(private readonly deps: NotificationStoreDeps) {}

  /**
   * Reads the saved list: an entry main does not recognise is dropped alone, a running audit comes back in progress as
   * it was, because the worker resumes it on start, any other entry saved as running comes back as failed because Scout
   * closed before it finished, a job control still unanswered is dropped from its entry because the run that sent it is
   * gone, and a list that could not be read is never saved over.
   */
  async load(): Promise<void> {
    let raw: string | null;
    try {
      raw = await this.deps.vault.get(VAULT_KEY);
    } catch (err: unknown) {
      this.readFailed = true;
      this.deps.logger.warn(
        'notifications: the saved list could not be read; changes stay in memory and the saved list is kept',
        { error: err instanceof Error ? err.name : typeof err },
      );
      return;
    }
    if (raw === null) return;
    const parsed = parseList(raw);
    if (parsed === null) {
      this.deps.logger.warn('notifications: the saved list is corrupt; starting empty');
      return;
    }
    if (parsed.dropped > 0) {
      this.deps.logger.warn('notifications: dropped saved entries Scout does not recognise', {
        dropped: parsed.dropped,
      });
    }
    const saved = parsed.entries.map(({ control: _control, ...n }): AppNotification => {
      if (n.kind !== 'running' || n.id.startsWith('audit:')) return n;
      const { progress: _progress, ...rest } = n;
      return { ...rest, kind: 'failed', detail: CLOSED_BEFORE_DONE };
    });
    const recorded = new Set(this.items.map((n) => n.id));
    this.replace([...this.items, ...saved.filter((n) => !recorded.has(n.id))], false);
  }

  list(): readonly AppNotification[] {
    return this.items;
  }

  /** An id no other entry of this run or a saved one carries. */
  nextId(prefix: string): string {
    this.sequence += 1;
    return `${prefix}:${this.deps.now().toString(36)}-${this.sequence}`;
  }

  /**
   * Adds the entry, or updates the entry with its id as unread; an update that changes nothing is ignored, a read job
   * entry stays read until its kind changes or a new note comes, so the next area of a running audit does not raise the
   * badge, and a read GitHub entry stays read unless its reason changed or its thread got more comments; one saved
   * before comment counts were kept stays read and takes the count.
   */
  record(input: NotificationInput): void {
    const existing = this.items.find((n) => n.id === input.id);
    const keepsRead = existing !== undefined && existing.read && sameActivity(existing, input);
    const next: AppNotification = { ...input, at: input.at ?? this.deps.now(), read: keepsRead };
    if (existing !== undefined && sameContent(existing, next)) return;
    this.replace([next, ...this.items.filter((n) => n.id !== next.id)]);
  }

  /** Removes every entry `matches` selects. */
  remove(matches: (n: AppNotification) => boolean): void {
    if (this.items.some(matches)) this.replace(this.items.filter((n) => !matches(n)));
  }

  /** Marks the entries with these ids as read, or every entry without ids. */
  markRead(ids?: readonly string[]): readonly AppNotification[] {
    const wanted = ids === undefined ? null : new Set(ids);
    const marks = (n: AppNotification): boolean => !n.read && (wanted === null || wanted.has(n.id));
    if (this.items.some(marks)) {
      this.replace(this.items.map((n) => (marks(n) ? { ...n, read: true } : n)));
    }
    return this.items;
  }

  /** Writes a pending change now. */
  async flush(): Promise<void> {
    if (this.cancelSave === null) return;
    this.cancelSave();
    this.cancelSave = null;
    await this.save();
  }

  private replace(list: readonly AppNotification[], save = true): void {
    this.items = [...list].sort((a, b) => b.at - a.at).slice(0, MAX_NOTIFICATIONS);
    this.deps.push(this.items);
    if (!save) return;
    this.cancelSave?.();
    this.cancelSave = this.deps.setTimer(SAVE_DELAY_MS, () => {
      this.cancelSave = null;
      void this.save();
    });
  }

  private async save(): Promise<void> {
    if (this.readFailed) return;
    try {
      await this.deps.vault.store(VAULT_KEY, JSON.stringify(this.items));
    } catch (err: unknown) {
      this.deps.logger.warn('notifications: the list was not saved', {
        error: err instanceof Error ? err.name : typeof err,
      });
    }
  }
}

function sameContent(a: AppNotification, b: AppNotification): boolean {
  return (
    a.kind === b.kind &&
    a.title === b.title &&
    a.detail === b.detail &&
    a.at === b.at &&
    a.progress === b.progress &&
    a.url === b.url &&
    JSON.stringify(a.actions) === JSON.stringify(b.actions) &&
    a.comments === b.comments &&
    a.control?.action === b.control?.action &&
    a.control?.phase === b.control?.phase &&
    a.note === b.note &&
    a.source === b.source
  );
}

function sameActivity(existing: AppNotification, next: NotificationInput): boolean {
  if (existing.id.startsWith('audit:') || existing.id.startsWith('tests:')) {
    return existing.kind === next.kind && (next.note === undefined || next.note === existing.note);
  }
  return (
    existing.source === 'github' &&
    next.source === 'github' &&
    existing.detail === next.detail &&
    (existing.comments === undefined || (next.comments ?? 0) <= existing.comments)
  );
}

function isActions(value: unknown): boolean {
  return (
    Array.isArray(value) &&
    value.every(
      (a: unknown) =>
        typeof a === 'object' &&
        a !== null &&
        ACTIONS.has((a as Record<string, unknown>)['id'] as NotificationActionId) &&
        typeof (a as Record<string, unknown>)['label'] === 'string',
    )
  );
}

function parseList(raw: string): { entries: AppNotification[]; dropped: number } | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!Array.isArray(parsed)) return null;
  const entries = parsed.filter(isNotification);
  return { entries, dropped: parsed.length - entries.length };
}

function isNotification(value: unknown): value is AppNotification {
  if (typeof value !== 'object' || value === null) return false;
  const n = value as Record<string, unknown>;
  return (
    typeof n['id'] === 'string' &&
    KINDS.has(n['kind'] as NotificationKind) &&
    typeof n['title'] === 'string' &&
    typeof n['detail'] === 'string' &&
    typeof n['at'] === 'number' &&
    Number.isFinite(n['at']) &&
    typeof n['read'] === 'boolean' &&
    (n['progress'] === undefined || typeof n['progress'] === 'number') &&
    (n['url'] === undefined || typeof n['url'] === 'string') &&
    (n['actions'] === undefined || isActions(n['actions'])) &&
    (n['comments'] === undefined || typeof n['comments'] === 'number') &&
    (n['note'] === undefined || typeof n['note'] === 'string') &&
    SOURCES.has(n['source'] as AppNotification['source'])
  );
}

/** A window main can push to; Electron's BrowserWindow is one. */
export interface NotificationWindow {
  isDestroyed(): boolean;
  readonly webContents: { send(channel: string, payload: unknown): void };
}

/** Sends the list to every open window. */
export function pushToWindows(
  windows: () => readonly NotificationWindow[],
): (list: readonly AppNotification[]) => void {
  return (list) => {
    for (const target of windows()) {
      if (!target.isDestroyed()) target.webContents.send(CHANNEL.notifications, list);
    }
  };
}

function relayError(err: unknown): never {
  const serialized = serializeError(err);
  throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), { code: serialized.code });
}

function refuse(): never {
  throw new ScoutError('notifications/invalid-request', REFUSED);
}

function toIds(raw: unknown): readonly string[] | undefined {
  const ids = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const value = ids['ids'];
  if (value === undefined) return undefined;
  if (
    !Array.isArray(value) ||
    value.length > MAX_NOTIFICATIONS ||
    !value.every((id) => typeof id === 'string')
  ) {
    refuse();
  }
  return value as string[];
}

/** The list, mark read, and the renderer's notices kept as entries. */
export function registerNotificationHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  store: NotificationStore,
): () => void {
  ipcMain.handle(CHANNEL.notificationsList, () => store.list());

  ipcMain.handle(CHANNEL.notificationsMarkRead, (_event, raw: unknown) => {
    try {
      return store.markRead(toIds(raw));
    } catch (err: unknown) {
      relayError(err);
    }
  });

  ipcMain.handle(CHANNEL.noticeRecord, (_event, raw: unknown) => {
    try {
      const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
      const { message, kind } = r;
      if (
        typeof message !== 'string' ||
        message.trim().length === 0 ||
        message.length > MAX_NOTICE_CHARS ||
        typeof kind !== 'string' ||
        !NOTICE_KINDS.has(kind)
      ) {
        refuse();
      }
      store.record(
        noticeNotification(
          { message, kind: kind as 'info' | 'success' | 'error' },
          store.nextId('notice'),
        ),
      );
    } catch (err: unknown) {
      relayError(err);
    }
  });

  return () => {
    ipcMain.removeHandler(CHANNEL.noticeRecord);
    ipcMain.removeHandler(CHANNEL.notificationsMarkRead);
    ipcMain.removeHandler(CHANNEL.notificationsList);
  };
}
