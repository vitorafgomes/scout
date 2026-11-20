import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type GitHubVerdictAction,
  type GitHubVerdictKey,
  type GitHubVerdictProgress,
  type GitHubVerdictRequest,
  type GitHubVerdictStatus,
  type ReviewCoverage,
  type ReviewFinding,
} from '@scout/shared';
import type { ControlPlaneClient } from '../control-plane/client.js';
import type { WaitingApprovals } from '../github/waiting-approvals.js';
import type { Logger } from '../util/logger.js';
import {
  guardChannels,
  senderCheck,
  VERDICT_WINDOW_GUARD,
  type RefuseUnlessFrom,
  type SenderHooks,
} from './sender-check.js';

export type VerdictClient = Pick<ControlPlaneClient, 'giveVerdict' | 'getVerdict'>;

export interface VerdictTimers {
  setTimeout(callback: () => void, ms: number): unknown;
  clearTimeout(handle: unknown): void;
}

const POLL_MS = 2_000;
const MAX_POLL_FAILURES = 5;
const MAX_FINDINGS = 50;
const MAX_TITLE_CHARS = 200;
const MAX_FINDING_TEXT_CHARS = 4_000;
const MAX_RULE_CHARS = 300;
const MAX_NOTES = 20;
const MAX_NOTE_CHARS = 300;
const MAX_TESTS_LINE_CHARS = 4_000;
const FINDING_ID = /^[A-Za-z0-9-]{1,40}$/;
const REPOSITORY_PATTERN = /^[A-Za-z0-9_.-]{1,100}\/[A-Za-z0-9_.-]{1,100}$/;
const COMMIT_PATTERN = /^[0-9a-f]{7,40}$/;
const ACTION_ID_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;
const REFUSED = 'Scout could not read that verdict.';
const ALREADY_SENT = 'A verdict or a review was already sent on this commit.';

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

function isFilled(value: unknown, max: number): value is string {
  return isText(value, max) && value.trim().length > 0;
}

/** One finding as the card's parser accepts it, with `why` and `fix` of at most 4,000 characters each. */
function toFinding(raw: unknown): ReviewFinding {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { id, severity, title, path, line, context, why, fix, rule } = r;
  if (
    typeof id !== 'string' ||
    !FINDING_ID.test(id) ||
    (severity !== 'block' && severity !== 'fix' && severity !== 'nit') ||
    !isFilled(title, MAX_TITLE_CHARS) ||
    !isFilled(path, 1_000) ||
    path.includes('..') ||
    typeof line !== 'number' ||
    !Number.isInteger(line) ||
    line < 1 ||
    (context !== undefined && !isText(context, MAX_FINDING_TEXT_CHARS)) ||
    !isFilled(why, MAX_FINDING_TEXT_CHARS) ||
    !isFilled(fix, MAX_FINDING_TEXT_CHARS) ||
    (rule !== undefined && !isFilled(rule, MAX_RULE_CHARS))
  ) {
    invalid();
  }
  return {
    id,
    severity,
    title,
    path,
    line,
    ...(context === undefined ? {} : { context }),
    why,
    fix,
    ...(rule === undefined ? {} : { rule }),
  };
}

function toCoverage(raw: unknown): ReviewCoverage {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { read, of } = r;
  if (
    !Number.isInteger(read) ||
    !Number.isInteger(of) ||
    (read as number) < 0 ||
    (read as number) > (of as number)
  ) {
    invalid();
  }
  return { read: read as number, of: of as number };
}

function toNotes(raw: unknown): readonly string[] {
  if (
    !Array.isArray(raw) ||
    raw.length > MAX_NOTES ||
    !raw.every((n) => isFilled(n, MAX_NOTE_CHARS))
  ) {
    invalid();
  }
  return raw as string[];
}

/** Checks a verdict from the renderer field by field and keeps only the known fields; it carries findings, never a body. */
export function toVerdictRequest(raw: unknown): GitHubVerdictRequest {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const { repository, number, verdict, findings, coverage, notes, commit, testsLine } = r;
  if (
    typeof repository !== 'string' ||
    !REPOSITORY_PATTERN.test(repository) ||
    repository.split('/').some((part) => part === '.' || part === '..') ||
    typeof number !== 'number' ||
    !Number.isInteger(number) ||
    number < 1 ||
    (verdict !== 'approve' && verdict !== 'request_changes' && verdict !== 'comment') ||
    !Array.isArray(findings) ||
    findings.length > MAX_FINDINGS ||
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
    findings: findings.map(toFinding),
    ...(coverage === undefined ? {} : { coverage: toCoverage(coverage) }),
    ...(notes === undefined ? {} : { notes: toNotes(notes) }),
    commit,
    testsLine,
  };
}

/** Refuses a call that acts on a verdict or a waiting approval unless it comes from the Scout window or quick ask. */
export function refuseUnlessVerdictWindow(
  refuseUnlessFrom: RefuseUnlessFrom,
  event: unknown,
  channel: string,
): void {
  refuseUnlessFrom(VERDICT_WINDOW_GUARD.allowed, event, channel, VERDICT_WINDOW_GUARD.refusal);
}

/** A window main can push to; Electron's BrowserWindow is one. */
export interface VerdictWindow {
  isDestroyed(): boolean;
  readonly webContents: { send(channel: string, payload: unknown): void };
}

/**
 * Sends a verdict's status, or on `channel` a waiting approval's, to every open window, so the card shows it whichever
 * window asked.
 */
export function pushToWindows<T = GitHubVerdictStatus>(
  windows: () => readonly VerdictWindow[],
  channel: string = CHANNEL.githubVerdict,
): (status: T) => void {
  return (status) => {
    for (const target of windows()) {
      if (!target.isDestroyed()) target.webContents.send(channel, status);
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

/** What main keeps of the verdict handlers: the verdict as the card gives it, and their removal. */
export interface GitHubVerdictHandlers {
  /** Gives a verdict as the card's click does; `afterChecks` marks every status of that action as given by Scout once every check passed. */
  give(request: GitHubVerdictRequest, afterChecks?: boolean): Promise<GitHubVerdictAction>;
  dispose(): void;
}

/**
 * The card's verdict, or its Post as the verdict `comment`: the owner's click goes to the control plane as an already-approved action, and main follows
 * that action every 2 seconds, pushing each change to every window, until it is given or failed. The control plane
 * decides what is identical: every click is sent, a different verdict or other findings at a commit that has one is a
 * new action, and an identical one answers the action already there; two identical clicks in flight are sent once.
 * Approve when the checks pass hands an approval to `waiting`, refused only while an approval of the commit stands,
 * and only the windows that give verdicts may ask for it or cancel it.
 */
export function registerGitHubVerdictHandlers(
  ownIpcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  getClient: () => Promise<VerdictClient>,
  push: (status: GitHubVerdictStatus) => void,
  logger: Logger,
  senders: SenderHooks,
  waiting: Pick<WaitingApprovals, 'start' | 'cancel' | 'statusFor'>,
  timers: VerdictTimers = { setTimeout, clearTimeout },
): GitHubVerdictHandlers {
  const ipcMain = guardChannels(ownIpcMain, senderCheck(senders, logger), {
    [CHANNEL.githubGiveVerdict]: VERDICT_WINDOW_GUARD,
    [CHANNEL.githubApproveWhenChecksPass]: VERDICT_WINDOW_GUARD,
    [CHANNEL.githubCancelWaitingApproval]: VERDICT_WINDOW_GUARD,
  });
  const following = new Map<string, unknown>();
  const verdicts = new Map<string, GitHubVerdictStatus>();
  const byKey = new Map<string, string>();
  const giving = new Map<string, Promise<GitHubVerdictAction>>();

  /** The known status updated by `progress`, keeping its verdict when the control plane reports none. */
  function merged(
    known: GitHubVerdictStatus,
    progress: GitHubVerdictProgress,
  ): GitHubVerdictStatus {
    const { verdict, ...rest } = progress;
    return { ...known, ...rest, verdict: verdict ?? known.verdict, actionId: known.actionId };
  }

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
            record(merged(known, progress));
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

  async function give(
    request: GitHubVerdictRequest,
    afterChecks = false,
  ): Promise<GitHubVerdictAction> {
    const key = `${keyOf(request)} ${request.verdict} ${JSON.stringify(request.findings)}`;
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
          signedAs: null,
          testsLine: null,
          repository: request.repository,
          number: request.number,
          commit: request.commit,
          verdict: request.verdict,
          ...(afterChecks ? { afterChecks: true as const } : {}),
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
      const request = toVerdictRequest(raw);
      waiting.cancel({
        repository: request.repository,
        number: request.number,
        commit: request.commit,
      });
      return await give(request);
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
      record(merged(known, await (await getClient()).getVerdict(actionId)));
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

  ipcMain.handle(CHANNEL.githubApproveWhenChecksPass, (_event, raw: unknown) => {
    try {
      const request = toVerdictRequest(raw);
      if (request.verdict !== 'approve') invalid();
      const sent = verdicts.get(byKey.get(keyOf(request)) ?? '');
      if (sent !== undefined && sent.verdict === 'approve' && sent.state !== 'failed') {
        throw new ScoutError('github/verdict-exists', ALREADY_SENT);
      }
      return waiting.start(request);
    } catch (err: unknown) {
      logger.warn('github: approval when the checks pass not started', {
        code: serializeError(err).code,
      });
      relayError(err);
    }
  });

  ipcMain.handle(CHANNEL.githubCancelWaitingApproval, (_event, raw: unknown) => {
    try {
      waiting.cancel(toVerdictKey(raw));
    } catch (err: unknown) {
      relayError(err);
    }
  });

  ipcMain.handle(CHANNEL.githubWaitingApprovalFor, (_event, raw: unknown) => {
    try {
      return waiting.statusFor(toVerdictKey(raw));
    } catch (err: unknown) {
      relayError(err);
    }
  });

  return {
    give,
    dispose() {
      for (const handle of following.values()) timers.clearTimeout(handle);
      following.clear();
      ipcMain.removeHandler(CHANNEL.githubWaitingApprovalFor);
      ipcMain.removeHandler(CHANNEL.githubCancelWaitingApproval);
      ipcMain.removeHandler(CHANNEL.githubApproveWhenChecksPass);
      ipcMain.removeHandler(CHANNEL.githubVerdictFor);
      ipcMain.removeHandler(CHANNEL.githubVerdictStatus);
      ipcMain.removeHandler(CHANNEL.githubGiveVerdict);
    },
  };
}
