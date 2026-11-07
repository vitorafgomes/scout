import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type GitHubVerdictAction,
  type GitHubVerdictComment,
  type GitHubVerdictRequest,
  type GitHubVerdictStatus,
} from '@scout/shared';
import type { ControlPlaneClient } from '../control-plane/client.js';
import type { Logger } from '../util/logger.js';

export type VerdictClient = Pick<ControlPlaneClient, 'giveVerdict' | 'getVerdict'>;

export interface VerdictTimers {
  setTimeout(callback: () => void, ms: number): unknown;
  clearTimeout(handle: unknown): void;
}

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
    (verdict !== 'approve' && verdict !== 'request_changes') ||
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

/**
 * The card's verdict: the owner's click goes to the control plane as an already-approved action, and main follows
 * that action every 2 seconds, pushing each change to every window, until it is given or failed.
 */
export function registerGitHubVerdictHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  getClient: () => Promise<VerdictClient>,
  push: (status: GitHubVerdictStatus) => void,
  logger: Logger,
  timers: VerdictTimers = { setTimeout, clearTimeout },
): () => void {
  const following = new Map<string, unknown>();

  function follow(actionId: string, last: string | null, failures: number): void {
    following.set(
      actionId,
      timers.setTimeout(() => {
        void (async () => {
          try {
            const status = await (await getClient()).getVerdict(actionId);
            if (!following.has(actionId)) return;
            const seen = JSON.stringify(status);
            if (seen !== last) push(status);
            if (status.state === 'given' || status.state === 'failed') {
              following.delete(actionId);
              return;
            }
            follow(actionId, seen, 0);
          } catch (err: unknown) {
            if (!following.has(actionId)) return;
            logger.warn('github: verdict status not read', { code: serializeError(err).code });
            if (failures + 1 >= MAX_POLL_FAILURES) {
              following.delete(actionId);
              return;
            }
            follow(actionId, last, failures + 1);
          }
        })();
      }, POLL_MS),
    );
  }

  ipcMain.handle(CHANNEL.githubGiveVerdict, async (_event, raw: unknown) => {
    try {
      const request = toVerdictRequest(raw);
      const action = await (await getClient()).giveVerdict(request);
      logger.info('github: verdict sent as an approved action', {
        repository: request.repository,
        number: request.number,
        verdict: request.verdict,
      });
      if (!following.has(action.actionId)) follow(action.actionId, null, 0);
      return action;
    } catch (err: unknown) {
      logger.warn('github: verdict not sent', { code: serializeError(err).code });
      relayError(err);
    }
  });

  ipcMain.handle(CHANNEL.githubVerdictStatus, async (_event, raw: unknown) => {
    try {
      const actionId = (raw as Partial<GitHubVerdictAction> | null)?.actionId;
      if (typeof actionId !== 'string' || !ACTION_ID_PATTERN.test(actionId)) invalid();
      return await (await getClient()).getVerdict(actionId);
    } catch (err: unknown) {
      relayError(err);
    }
  });

  return () => {
    for (const handle of following.values()) timers.clearTimeout(handle);
    following.clear();
    ipcMain.removeHandler(CHANNEL.githubVerdictStatus);
    ipcMain.removeHandler(CHANNEL.githubGiveVerdict);
  };
}
