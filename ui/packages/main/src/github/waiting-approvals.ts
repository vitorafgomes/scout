import {
  ScoutError,
  serializeError,
  type GitHubVerdictAction,
  type GitHubVerdictKey,
  type GitHubVerdictRequest,
  type GitHubWaitingApproval,
} from '@scout/shared';
import { toVerdictRequest } from '../ipc/github-verdict.js';
import type { NotificationInput } from '../notifications/notification-store.js';
import { waitingApprovalId, waitingApprovalNotification } from '../notifications/sources.js';
import type { Logger } from '../util/logger.js';

/** How long main waits between two reads of the checks. */
export const WAIT_POLL_MS = 60_000;
/** How long an approval waits for its checks before it ends as timed out. */
export const WAIT_MAX_MS = 2 * 60 * 60_000;
/** How many approvals may wait at once. */
export const MAX_WAITING = 20;

const API_URL = 'https://api.github.com';
const HTTP_TIMEOUT_MS = 15_000;
const PER_PAGE = 100;
const MAX_DETAIL_CHARS = 60;
const HEAD_SHA = /^[0-9a-f]{40}$/i;
const PASSED = new Set(['success', 'neutral', 'skipped']);

export interface WaitingApprovalsDeps {
  fetch: typeof globalThis.fetch;
  /** The owner's GitHub token, read for each read and never kept; null while GitHub is not connected. */
  token(): Promise<string | null>;
  /** Gives the verdict the owner approved, as the card's own click would. */
  give(request: GitHubVerdictRequest): Promise<GitHubVerdictAction>;
  /** Sends a waiting approval's state to every open window. */
  push(status: GitHubWaitingApproval): void;
  /** Adds or updates the bell's entry. */
  record(entry: NotificationInput): void;
  /** Removes the bell's entry with this id. */
  remove(id: string): void;
  /** The saved waiting approvals, as `save` wrote them; null when none were saved. */
  load(): Promise<string | null>;
  save(json: string): Promise<void>;
  now(): number;
  /** Runs `callback` once after `ms`; returns the cancel function. */
  setTimer(ms: number, callback: () => void): () => void;
  logger: Logger;
}

interface Waiting {
  readonly request: GitHubVerdictRequest;
  readonly askedAt: number;
  running: number;
  giveLogged: boolean;
  giving: boolean;
}

type Read =
  | { readonly kind: 'ok'; readonly body: Record<string, unknown> }
  | { readonly kind: 'missing' }
  | { readonly kind: 'wait'; readonly after: number };

type Checks =
  | { readonly kind: 'failed'; readonly name: string }
  | { readonly kind: 'running'; readonly running: number }
  | { readonly kind: 'passed' };

function keyOf(key: GitHubVerdictKey): string {
  return `${key.repository}#${key.number}@${key.commit}`;
}

function oneLine(name: string): string {
  return (name.split(/\r?\n/)[0] ?? '').slice(0, MAX_DETAIL_CHARS);
}

function listOf(value: unknown): Record<string, unknown>[] {
  return Array.isArray(value)
    ? value.filter(
        (item): item is Record<string, unknown> => typeof item === 'object' && item !== null,
      )
    : [];
}

/**
 * The commit's checks as the worker reads them: a check run not completed, or a status neither success, failure nor
 * error, is running; a completed run whose conclusion is not success, neutral or skipped, or a status failure or
 * error, failed; more runs than one page read, or a full page of 100 statuses, leaves checks unread, counted as
 * running. An answer that is not the shape GitHub documents, a list or a count missing or an item without its state,
 * is unread too: what Scout could not read never passes.
 */
function classify(runsBody: Record<string, unknown>, statusBody: Record<string, unknown>): Checks {
  const rawRuns = runsBody['check_runs'];
  const rawStatuses = statusBody['statuses'];
  const total = runsBody['total_count'];
  const runs = listOf(rawRuns);
  const statuses = listOf(rawStatuses);
  const failed: string[] = [];
  let running = 0;
  let malformed =
    !Array.isArray(rawRuns) ||
    !Array.isArray(rawStatuses) ||
    typeof total !== 'number' ||
    runs.length !== rawRuns.length ||
    statuses.length !== rawStatuses.length;
  for (const run of runs) {
    if (typeof run['status'] !== 'string') malformed = true;
    else if (run['status'] !== 'completed') running += 1;
    else if (!PASSED.has(String(run['conclusion']))) failed.push(String(run['name']));
  }
  for (const status of statuses) {
    const state = status['state'];
    if (typeof state !== 'string') malformed = true;
    else if (state === 'failure' || state === 'error') failed.push(String(status['context']));
    else if (state !== 'success') running += 1;
  }
  if (failed[0] !== undefined) return { kind: 'failed', name: oneLine(failed[0]) };
  const unread =
    malformed || (typeof total === 'number' && total > runs.length) || statuses.length >= PER_PAGE;
  return running > 0 || unread ? { kind: 'running', running } : { kind: 'passed' };
}

/**
 * The approvals the owner asked Scout to give once every check of the reviewed commit passed, at most 20 at once.
 * Every minute, oldest first, main reads the pull request and its checks with GETs to api.github.com: a moved head,
 * a closed pull request, a failed check or two hours end the approval and give nothing; once every check passed it
 * gives exactly the request the owner clicked. A read that fails keeps it waiting; the waiting approvals are saved
 * without any token and come back after a restart with the time the owner asked.
 */
export class WaitingApprovals {
  private readonly waiting = new Map<string, Waiting>();
  private readonly ended = new Map<string, GitHubWaitingApproval>();
  private cancelTimer: (() => void) | null = null;
  private ticking = false;
  private disposed = false;
  private saving: Promise<void> = Promise.resolve();

  constructor(private readonly deps: WaitingApprovalsDeps) {}

  /**
   * Starts waiting for the checks of an approval the owner clicked; a second click on a waiting one answers its
   * state and sends nothing, and a twenty-first is refused.
   */
  start(request: GitHubVerdictRequest): GitHubWaitingApproval {
    const key = keyOf(request);
    const current = this.waiting.get(key);
    if (current !== undefined) return this.statusOf(current);
    if (this.waiting.size >= MAX_WAITING) {
      throw new ScoutError(
        'github/waiting-approvals-full',
        `Scout already waits for the checks of ${MAX_WAITING} approvals. Cancel one or wait for one to end.`,
      );
    }
    const entry: Waiting = {
      request,
      askedAt: this.deps.now(),
      running: 0,
      giveLogged: false,
      giving: false,
    };
    this.waiting.set(key, entry);
    this.ended.delete(key);
    this.save();
    this.announce(entry);
    this.schedule(WAIT_POLL_MS);
    return this.statusOf(entry);
  }

  /**
   * Stops waiting and gives nothing; a key that is not waiting changes nothing, and neither does one whose approval
   * is being given at this moment, which ends as approved.
   */
  cancel(key: GitHubVerdictKey): void {
    if (this.waiting.get(keyOf(key))?.giving === true) return;
    this.end(keyOf(key), 'cancelled');
  }

  /** Ends every waiting approval and gives none: the GitHub connection they were asked under changed. */
  cancelAll(): void {
    for (const [key, entry] of [...this.waiting]) {
      if (!entry.giving) this.end(key, 'cancelled');
    }
  }

  /** The waiting approval for this pull request and commit, or the last one that ended, or null. */
  statusFor(key: GitHubVerdictKey): GitHubWaitingApproval | null {
    const current = this.waiting.get(keyOf(key));
    return current === undefined ? (this.ended.get(keyOf(key)) ?? null) : this.statusOf(current);
  }

  /** Reads the saved waiting approvals, drops any that is not a valid approval, and waits for them again. */
  async resume(): Promise<void> {
    let saved: unknown;
    try {
      saved = JSON.parse((await this.deps.load()) ?? '[]');
    } catch (err: unknown) {
      this.deps.logger.warn('github: the waiting approvals could not be read', {
        error: err instanceof Error ? err.name : typeof err,
      });
      return;
    }
    for (const item of listOf(saved).slice(0, MAX_WAITING)) {
      let request: GitHubVerdictRequest;
      try {
        request = toVerdictRequest(item['request']);
      } catch {
        continue;
      }
      const askedAt = item['askedAt'];
      if (request.verdict !== 'approve' || typeof askedAt !== 'number' || !Number.isFinite(askedAt))
        continue;
      const entry: Waiting = {
        request,
        askedAt: Math.min(askedAt, this.deps.now()),
        running: 0,
        giveLogged: false,
        giving: false,
      };
      this.waiting.set(keyOf(request), entry);
      this.announce(entry);
    }
    if (this.waiting.size > 0) this.schedule(WAIT_POLL_MS);
  }

  dispose(): void {
    this.disposed = true;
    this.cancelTimer?.();
    this.cancelTimer = null;
  }

  private statusOf(entry: Waiting): GitHubWaitingApproval {
    const { repository, number, commit } = entry.request;
    return {
      repository,
      number,
      commit,
      state: 'waiting',
      running: entry.running,
      reason: null,
      detail: null,
      askedAt: entry.askedAt,
    };
  }

  private announce(entry: Waiting): void {
    const status = this.statusOf(entry);
    this.deps.push(status);
    const notification = waitingApprovalNotification(status);
    if (notification !== null) this.deps.record(notification);
  }

  private end(
    key: string,
    reason: NonNullable<GitHubWaitingApproval['reason']>,
    detail: string | null = null,
  ): void {
    const entry = this.waiting.get(key);
    if (entry === undefined) return;
    this.waiting.delete(key);
    const status: GitHubWaitingApproval = {
      ...this.statusOf(entry),
      state: 'ended',
      reason,
      detail,
    };
    this.ended.set(key, status);
    this.save();
    this.deps.push(status);
    const notification = waitingApprovalNotification(status);
    if (notification === null) this.deps.remove(waitingApprovalId(status));
    else this.deps.record(notification);
    if (this.waiting.size === 0) {
      this.cancelTimer?.();
      this.cancelTimer = null;
    }
  }

  private save(): void {
    const json = JSON.stringify(
      [...this.waiting.values()].map(({ request, askedAt }) => ({ request, askedAt })),
    );
    this.saving = this.saving
      .then(() => this.deps.save(json))
      .catch((err: unknown) => {
        this.deps.logger.warn('github: the waiting approvals were not saved', {
          error: err instanceof Error ? err.name : typeof err,
        });
      });
  }

  private schedule(ms: number): void {
    if (this.disposed || this.ticking || this.cancelTimer !== null) return;
    this.cancelTimer = this.deps.setTimer(ms, () => {
      this.cancelTimer = null;
      void this.tick();
    });
  }

  private async tick(): Promise<void> {
    this.ticking = true;
    let wait = WAIT_POLL_MS;
    try {
      const token = await this.deps.token().catch(() => null);
      const oldestFirst = [...this.waiting.entries()].sort(([, a], [, b]) => a.askedAt - b.askedAt);
      for (const [key, entry] of oldestFirst) {
        if (this.disposed || this.waiting.get(key) !== entry) continue;
        if (this.deps.now() - entry.askedAt > WAIT_MAX_MS) {
          this.end(key, 'timed-out');
          continue;
        }
        if (token === null) continue;
        const after = await this.check(key, entry, token);
        if (after !== null) {
          wait = Math.max(WAIT_POLL_MS, after);
          break;
        }
      }
    } finally {
      this.ticking = false;
    }
    if (this.waiting.size > 0) this.schedule(wait);
  }

  /** Reads one approval's pull request and checks and acts on them; answers a longer wait GitHub asked for. */
  private async check(key: string, entry: Waiting, token: string): Promise<number | null> {
    const { repository, number, commit } = entry.request;
    const pull = await this.get(
      `${API_URL}/repos/${repository}/pulls/${number}`,
      token,
      'pull request',
    );
    if (pull.kind === 'wait') return pull.after;
    if (pull.kind === 'missing' || pull.body['state'] === 'closed') {
      this.end(key, 'closed');
      return null;
    }
    if (pull.body['state'] !== 'open') return null;
    const head = (pull.body['head'] as Record<string, unknown> | null | undefined)?.['sha'];
    if (typeof head !== 'string' || !HEAD_SHA.test(head)) return null;
    if (!head.toLowerCase().startsWith(commit.toLowerCase())) {
      this.end(key, 'new-commit');
      return null;
    }
    const commitUrl = `${API_URL}/repos/${repository}/commits/${head.toLowerCase()}`;
    const runs = await this.get(
      `${commitUrl}/check-runs?per_page=${PER_PAGE}`,
      token,
      'check runs',
    );
    if (runs.kind !== 'ok') return runs.kind === 'wait' ? runs.after : null;
    const statuses = await this.get(`${commitUrl}/status?per_page=${PER_PAGE}`, token, 'statuses');
    if (statuses.kind !== 'ok') return statuses.kind === 'wait' ? statuses.after : null;
    if (this.waiting.get(key) !== entry) return null;
    const checks = classify(runs.body, statuses.body);
    if (checks.kind === 'failed') {
      this.end(key, 'check-failed', checks.name);
    } else if (checks.kind === 'running') {
      if (checks.running !== entry.running) {
        entry.running = checks.running;
        this.announce(entry);
      }
    } else {
      await this.approve(key, entry);
    }
    return null;
  }

  private async approve(key: string, entry: Waiting): Promise<void> {
    entry.giving = true;
    try {
      await this.deps.give(entry.request);
    } catch (err: unknown) {
      entry.giving = false;
      if (!entry.giveLogged) {
        entry.giveLogged = true;
        this.deps.logger.warn('github: the approval was not given; will try again', {
          code: serializeError(err).code,
        });
      }
      return;
    }
    entry.giving = false;
    this.end(key, 'approved');
  }

  private async get(url: string, token: string, read: string): Promise<Read> {
    const { logger } = this.deps;
    let response: Response;
    try {
      response = await this.deps.fetch(url, {
        method: 'GET',
        headers: {
          Accept: 'application/vnd.github+json',
          Authorization: `Bearer ${token}`,
          'User-Agent': 'Scout',
          'X-GitHub-Api-Version': '2022-11-28',
        },
        signal: AbortSignal.timeout(HTTP_TIMEOUT_MS),
      });
    } catch (err: unknown) {
      logger.warn('github: the checks were not read; will try again', {
        read,
        error: err instanceof Error ? err.name : typeof err,
      });
      return { kind: 'wait', after: WAIT_POLL_MS };
    }
    if (response.status === 404 && read === 'pull request') return { kind: 'missing' };
    if (!response.ok) {
      const retryAfter = Number(response.headers.get('Retry-After'));
      const limited = response.status === 403 || response.status === 429;
      logger.warn('github: GitHub answered an error while Scout waits for the checks', {
        read,
        status: response.status,
      });
      return {
        kind: 'wait',
        after: limited && Number.isFinite(retryAfter) ? retryAfter * 1000 : WAIT_POLL_MS,
      };
    }
    const body: unknown = await response.json().catch(() => null);
    if (typeof body === 'object' && body !== null) {
      return { kind: 'ok', body: body as Record<string, unknown> };
    }
    logger.warn('github: GitHub answered something unreadable while Scout waits for the checks', {
      read,
    });
    return { kind: 'wait', after: WAIT_POLL_MS };
  }
}
