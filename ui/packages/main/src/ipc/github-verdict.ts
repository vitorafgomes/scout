import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type GitHubVerdictAction,
  type GitHubVerdictComment,
  type GitHubVerdictKey,
  type GitHubVerdictRequest,
  type GitHubVerdictStatus,
} from '@scout/shared';
import type { ControlPlaneClient } from '../control-plane/client.js';
import type { Logger } from '../util/logger.js';
import { guardChannels, senderCheck, type SenderHooks, type SenderKind } from './sender-check.js';

export type VerdictClient = Pick<ControlPlaneClient, 'giveVerdict' | 'getVerdict'>;

export interface VerdictTimers {
  setTimeout(callback: () => void, ms: number): unknown;
  clearTimeout(handle: unknown): void;
}

/** The windows that show a review's verdict buttons: the Chat screen of the main window, and quick ask. */
const VERDICT_WINDOWS: readonly SenderKind[] = ['main', 'quick-ask'];
const POLL_MS = 2_000;
const MAX_POLL_FAILURES = 5;
const MAX_BODY_CHARS = 65_000;
const MAX_COMMENTS = 50;
const MAX_TESTS_LINE_CHARS = 4_000;
const REPOSITORY_PATTERN = /^[A-Za-z0-9_.-]{1,100}\/[A-Za-z0-9_.-]{1,100}$/;
const COMMIT_PATTERN = /^[0-9a-f]{7,40}$/;
const ACTION_ID_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;
const REFUSED = 'Scout could not read that verdict.';

function invalid(): never {
  throw new ScoutError('github/invalid-verdict', REFUSED);
}

function relayError(err: unknown): never {
  const serialized = serializeError(err);
  throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), { code: serialized.code });
}

function isText(value: unknown, max: number, min = 0): value is string {
  return typeof value === 'string' && value.length >= min && value.length <= max;
}

function toComment(raw: unknown): GitHubVerdictComment {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { path, line, body } = r;
  if (
    !isText(path, 1_000, 1) ||
    typeof line !== 'number' ||
    !Number.isInteger(line) ||
    line < 1 ||
    !isText(body, MAX_BODY_CHARS, 1)
  ) {
    invalid();
  }
  return { path, line, body };
}

/** Checks a verdict from the renderer field by field and keeps only the known fields. */
export function toVerdictRequest(raw: unknown): GitHubVerdictRequest {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { repository, number, verdict, body, comments, commit, testsLine } = r;
  if (
    typeof repository !== 'string' ||
    !REPOSITORY_PATTERN.test(repository) ||
    typeof number !== 'number' ||
    !Number.isInteger(number) ||
    number < 1 ||
    (verdict !== 'approve' && verdict !== 'request_changes' && verdict !== 'comment') ||
    !isText(body, MAX_BODY_CHARS) ||
    !Array.isArray(comments) ||
    comments.length > MAX_COMMENTS ||
    typeof commit !== 'string' ||
    !COMMIT_PATTERN.test(commit) ||
    !isText(testsLine, MAX_TESTS_LINE_CHARS)
  ) {
    invalid();
  }
  return {
    repository,
    number,
    verdict,
    body,
    comments: comments.map(toComment),
    commit,
    testsLine,
  };
}

/** A window main can push to; Electron's BrowserWindow is one. */
export interface VerdictWindow {
  isDestroyed(): boolean;
  readonly webContents: { send(channel: string, payload: unknown): void };
}

/** Sends a verdict's status to every open window, so the card shows it whichever window asked. */
export function pushToWindows(
  windows: () => readonly VerdictWindow[],
): (status: GitHubVerdictStatus) => void {
  return (status) => {
    for (const target of windows()) {
      if (!target.isDestroyed()) target.webContents.send(CHANNEL.githubVerdict, status);
    }
  };
}

function keyOf(key: GitHubVerdictKey): string {
  return `${key.repository}#${key.number}@${key.commit}`;
}

function toVerdictKey(raw: unknown): GitHubVerdictKey {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { repository, number, commit } = r;
  if (
    typeof repository !== 'string' ||
    !REPOSITORY_PATTERN.test(repository) ||
    typeof number !== 'number' ||
    !Number.isInteger(number) ||
    number < 1 ||
    typeof commit !== 'string' ||
    !COMMIT_PATTERN.test(commit)
  ) {
    invalid();
  }
  return { repository, number, commit };
}

/**
 * The card's verdict, or its Post as the verdict `comment`: the owner's click goes to the control plane as an already-approved action, and main follows
 * that action every 2 seconds, pushing each change to every window, until it is given or failed. One pull request
 * at one reviewed commit gets one action: a click from either window while it is queued, running or given answers
 * that action instead of asking again; a failed one allows a new click.
 */
export function registerGitHubVerdictHandlers(
  ownIpcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  getClient: () => Promise<VerdictClient>,
  push: (status: GitHubVerdictStatus) => void,
  logger: Logger,
  senders: SenderHooks,
  timers: VerdictTimers = { setTimeout, clearTimeout },
): () => void {
  const ipcMain = guardChannels(ownIpcMain, senderCheck(senders, logger), {
    [CHANNEL.githubGiveVerdict]: {
      allowed: VERDICT_WINDOWS,
      refusal: () =>
        new ScoutError(
          'github/not-verdict-window',
          'A verdict can be given only from the Scout window or quick ask.',
        ),
    },
  });
  const following = new Map<string, unknown>();
  const verdicts = new Map<string, GitHubVerdictStatus>();
  const byKey = new Map<string, string>();
  const giving = new Map<string, Promise<GitHubVerdictAction>>();

  function record(status: GitHubVerdictStatus): void {
    const previous = verdicts.get(status.actionId);
    verdicts.set(status.actionId, status);
    byKey.set(keyOf(status), status.actionId);
    if (JSON.stringify(previous) !== JSON.stringify(status)) push(status);
  }

  function follow(actionId: string, failures: number): void {
    following.set(
      actionId,
      timers.setTimeout(() => {
        void (async () => {
          try {
            const progress = await (await getClient()).getVerdict(actionId);
            const known = verdicts.get(actionId);
            if (!following.has(actionId) || known === undefined) return;
            record({ ...known, ...progress, actionId });
            if (progress.state === 'given' || progress.state === 'failed') {
              following.delete(actionId);
              return;
            }
            follow(actionId, 0);
          } catch (err: unknown) {
            if (!following.has(actionId)) return;
            logger.warn('github: verdict status not read', { code: serializeError(err).code });
            if (failures + 1 >= MAX_POLL_FAILURES) {
              following.delete(actionId);
              return;
            }
            follow(actionId, failures + 1);
          }
        })();
      }, POLL_MS),
    );
  }

  async function give(request: GitHubVerdictRequest): Promise<GitHubVerdictAction> {
    const key = keyOf(request);
    const existing = verdicts.get(byKey.get(key) ?? '');
    if (existing !== undefined && existing.state !== 'failed') {
      return { actionId: existing.actionId };
    }
    const inFlight = giving.get(key);
    if (inFlight !== undefined) return inFlight;
    const sending = (async () => {
      const action = await (await getClient()).giveVerdict(request);
      logger.info('github: verdict sent as an approved action', {
        repository: request.repository,
        number: request.number,
        verdict: request.verdict,
      });
      if (!verdicts.has(action.actionId)) {
        record({
          actionId: action.actionId,
          state: 'queued',
          url: null,
          reason: null,
          repository: request.repository,
          number: request.number,
          commit: request.commit,
          verdict: request.verdict,
        });
      }
      if (!following.has(action.actionId)) follow(action.actionId, 0);
      return action;
    })();
    giving.set(key, sending);
    try {
      return await sending;
    } finally {
      giving.delete(key);
    }
  }

  ipcMain.handle(CHANNEL.githubGiveVerdict, async (_event, raw: unknown) => {
    try {
      return await give(toVerdictRequest(raw));
    } catch (err: unknown) {
      logger.warn('github: verdict not sent', { code: serializeError(err).code, err: String(err) });
      relayError(err);
    }
  });

  ipcMain.handle(CHANNEL.githubVerdictStatus, async (_event, raw: unknown) => {
    try {
      const actionId = (raw as Partial<GitHubVerdictAction> | null)?.actionId;
      if (typeof actionId !== 'string' || !ACTION_ID_PATTERN.test(actionId)) invalid();
      const known = verdicts.get(actionId);
      if (known === undefined) {
        throw new ScoutError('github/unknown-verdict', 'Scout has no verdict with that id.');
      }
      record({ ...known, ...(await (await getClient()).getVerdict(actionId)), actionId });
      return verdicts.get(actionId);
    } catch (err: unknown) {
      logger.warn('github: verdict status not answered', {
        code: serializeError(err).code,
        err: String(err),
      });
      relayError(err);
    }
  });

  ipcMain.handle(CHANNEL.githubVerdictFor, async (_event, raw: unknown) => {
    try {
      return verdicts.get(byKey.get(keyOf(toVerdictKey(raw))) ?? '') ?? null;
    } catch (err: unknown) {
      relayError(err);
    }
  });

  return () => {
    for (const handle of following.values()) timers.clearTimeout(handle);
    following.clear();
    ipcMain.removeHandler(CHANNEL.githubVerdictFor);
    ipcMain.removeHandler(CHANNEL.githubVerdictStatus);
    ipcMain.removeHandler(CHANNEL.githubGiveVerdict);
  };
}
