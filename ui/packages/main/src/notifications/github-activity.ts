import type { Logger } from '../util/logger.js';
import type { NotificationInput } from './notification-store.js';

export const SEARCH_URL = 'https://api.github.com/search/issues';
const API_URL = 'https://api.github.com';
export const POLL_MS = 5 * 60_000;
export const FIRST_LOOK_BACK_MS = 24 * 60 * 60_000;
const HTTP_TIMEOUT_MS = 15_000;
const PER_PAGE = 100;
const MAX_RESULTS = 1_000;
const MAX_SEARCHES_PER_MINUTE = 20;
const MINUTE_MS = 60_000;
const MAX_TITLE_CHARS = 200;
const REPOSITORY_URL = /^https:\/\/api\.github\.com\/repos\/([A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+)$/;
const HTML_URL = /^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/(pull|issues)\/\d+$/;
const COMMENT_URL =
  /^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/pull\/\d+(#(issuecomment-|discussion_r|pullrequestreview-)\d+)?$/;
const MAX_COMMENT_PAGES = 10;
const MAX_LINE_CHARS = 140;

/** The owner's Scout bot as the poll needs it: its slug and the account its installation is on. */
export interface GitHubBotMention {
  readonly slug: string;
  readonly account: string;
  readonly accountType: 'user' | 'org';
}

/** The owner's GitHub account as the poll needs it, or null while GitHub is not connected. */
export interface GitHubAccount {
  readonly token: string;
  readonly login: string;
}

export interface GitHubActivityDeps {
  fetch: typeof globalThis.fetch;
  account(): Promise<GitHubAccount | null>;
  /** The installed Scout bot whose mentions the bell watches, or null without one. */
  bot?(): Promise<GitHubBotMention | null>;
  record(entry: NotificationInput): void;
  /** Removes every GitHub entry; called when a connection change leaves GitHub disconnected. */
  clearEntries(): void;
  now(): number;
  /** Runs `callback` once after `ms`; returns the cancel function. */
  setTimer(ms: number, callback: () => void): () => void;
  logger: Logger;
}

interface Query {
  readonly q: (login: string) => string;
  readonly detail: (ref: string) => string;
}

/** The searches of one read, in priority order: an item found by an earlier one keeps that one's reason. */
export const QUERIES: readonly Query[] = [
  {
    q: (login) => `is:pr is:open review-requested:${login}`,
    detail: (ref) => `Review requested on ${ref}`,
  },
  { q: (login) => `is:pr mentions:${login}`, detail: (ref) => `You were mentioned in ${ref}` },
  { q: (login) => `is:issue mentions:${login}`, detail: (ref) => `You were mentioned in ${ref}` },
  {
    q: (login) => `is:pr author:${login} comments:>0`,
    detail: (ref) => `New activity on your pull request ${ref}`,
  },
  {
    q: (login) => `is:issue author:${login} comments:>0`,
    detail: (ref) => `New activity on your issue ${ref}`,
  },
];

interface Pass {
  readonly since: number;
  readonly startedAt: number;
  readonly queries: readonly string[];
  readonly bot: GitHubBotMention | null;
  readonly found: Map<string, NotificationInput>;
  readonly candidates: Map<number, Record<string, unknown>>;
  query: number;
  page: number;
  read: number;
}

type Outcome =
  | {
      readonly kind: 'ok';
      readonly items: readonly unknown[];
      readonly total: number;
      readonly body: unknown;
    }
  | { readonly kind: 'stop' }
  | { readonly kind: 'retry'; readonly after: number };

/**
 * Searches the owner's GitHub activity every five minutes while GitHub is connected: the pull requests waiting for
 * the owner's review, the pull requests and issues that mention the owner, and the owner's own ones with comments,
 * updated since the last complete read (the last 24 hours at first), every page of each search, each recorded as one
 * entry per item with its comment count. It sends at most 20 searches in any minute, under GitHub's 30: a read that
 * needs more stops there and goes on from the next page five minutes later, recording only once every page answered.
 * A rejected token stops it until GitHub is connected again or the token is refreshed; a rate limit waits what GitHub
 * asks; a timeout, a server error or any other refusal keeps the entries, drops the pages read so far and reads the
 * same window again from its first page on the next tick. A connection change that leaves GitHub disconnected removes
 * the GitHub entries, and a read still running from before the change records nothing. The token is never logged or
 * stored here. With an installed Scout bot one more search finds open pull requests on the bot's account that carry
 * the phrase `"@<slug>"`; each is confirmed by its body, the comments updated in the window and the reviews submitted
 * in it, matching `@<slug>` as a whole word, never in the bot's own text, and each confirmed comment or review becomes
 * one mention entry. A comment read that fails is handled as a failed search: nothing is recorded and the window
 * stays.
 */
export class GitHubActivityPoll {
  private cancelTimer: (() => void) | null = null;
  private since: number | null = null;
  private pass: Pass | null = null;
  private sentAt: number[] = [];
  private stopped = false;
  private disposed = false;
  private generation = 0;

  constructor(private readonly deps: GitHubActivityDeps) {}

  start(): void {
    void this.tick();
  }

  /**
   * GitHub was connected or disconnected: a stopped poll starts again over the last 24 hours, and without an account
   * the GitHub entries are removed.
   */
  reconnected(): void {
    this.generation += 1;
    this.stopped = false;
    this.since = null;
    this.pass = null;
    void this.deps.account().then(
      (account) => {
        if (account === null) this.deps.clearEntries();
        return this.tick();
      },
      () => this.tick(),
    );
  }

  /** The token was refreshed: a poll a rejected token stopped reads again from where it was. */
  tokenRefreshed(): void {
    if (!this.stopped) return;
    this.stopped = false;
    void this.tick();
  }

  dispose(): void {
    this.disposed = true;
    this.cancelTimer?.();
    this.cancelTimer = null;
  }

  /** One read; schedules the next unless the poll stopped. */
  async tick(): Promise<void> {
    this.cancelTimer?.();
    this.cancelTimer = null;
    if (this.stopped || this.disposed) return;
    const next = await this.read();
    if (next !== null && !this.stopped && !this.disposed) this.schedule(next);
  }

  private schedule(ms: number): void {
    this.cancelTimer?.();
    this.cancelTimer = this.deps.setTimer(ms, () => void this.tick());
  }

  private async read(): Promise<number | null> {
    const generation = this.generation;
    const account = await this.deps.account().catch(() => null);
    if (account === null) return POLL_MS;
    if (generation !== this.generation) return null;
    let pass = this.pass;
    this.pass = null;
    if (pass === null) {
      const bot = (await this.deps.bot?.().catch(() => null)) ?? null;
      if (generation !== this.generation) return null;
      const startedAt = this.deps.now();
      const queries = QUERIES.map((query) => query.q(account.login));
      if (bot !== null) {
        queries.push(
          `"@${bot.slug}" in:comments,body is:pr is:open ${bot.accountType}:${bot.account}`,
        );
      }
      pass = {
        since: this.since ?? startedAt - FIRST_LOOK_BACK_MS,
        startedAt,
        queries,
        bot,
        found: new Map(),
        candidates: new Map(),
        query: 0,
        page: 1,
        read: 0,
      };
    }
    while (pass.query < pass.queries.length) {
      const now = this.deps.now();
      this.sentAt = this.sentAt.filter((at) => now - at < MINUTE_MS);
      if (this.sentAt.length >= MAX_SEARCHES_PER_MINUTE) {
        this.pass = pass;
        return POLL_MS;
      }
      this.sentAt.push(now);
      const query = QUERIES[pass.query];
      const q = `${pass.queries[pass.query]!} updated:>=${isoSeconds(pass.since)}`;
      const outcome = await this.search(q, pass.page, account.token);
      if (generation !== this.generation) return null;
      if (outcome.kind === 'stop') return null;
      if (outcome.kind === 'retry') return outcome.after;
      for (const raw of outcome.items) {
        if (query === undefined) {
          const r = asRecord(raw);
          if (typeof r['id'] === 'number') pass.candidates.set(r['id'], r);
          continue;
        }
        const entry = toEntry(raw, query);
        if (entry !== null && !pass.found.has(entry.id)) pass.found.set(entry.id, entry);
      }
      pass.read += outcome.items.length;
      if (outcome.items.length === 0 || pass.read >= Math.min(outcome.total, MAX_RESULTS)) {
        pass.query += 1;
        pass.page = 1;
        pass.read = 0;
      } else {
        pass.page += 1;
      }
    }
    const mentions: NotificationInput[] = [];
    if (pass.bot !== null) {
      for (const pr of pass.candidates.values()) {
        const found = await this.confirm(pr, pass.bot, pass.since, account.token);
        if (generation !== this.generation) return null;
        if (found.kind === 'stop') return null;
        if (found.kind === 'retry') return found.after;
        mentions.push(...found.entries);
      }
    }
    for (const entry of pass.found.values()) this.deps.record(entry);
    for (const entry of mentions) this.deps.record(entry);
    this.since = pass.startedAt;
    return POLL_MS;
  }

  private async confirm(
    pr: Record<string, unknown>,
    bot: GitHubBotMention,
    since: number,
    token: string,
  ): Promise<
    | { readonly kind: 'ok'; readonly entries: readonly NotificationInput[] }
    | { readonly kind: 'stop' }
    | { readonly kind: 'retry'; readonly after: number }
  > {
    const repository = REPOSITORY_URL.exec(String(pr['repository_url']))?.[1];
    const { id, number } = pr;
    if (repository === undefined || typeof id !== 'number' || typeof number !== 'number') {
      return { kind: 'ok', entries: [] };
    }
    const ref = `${repository}#${number}`;
    const title = typeof pr['title'] === 'string' ? pr['title'] : '';
    const entries: NotificationInput[] = [];
    const body = mentionEntry(pr, `body:${id}`, ref, title, bot);
    if (body !== null) entries.push(body);
    const window = `since=${encodeURIComponent(new Date(since).toISOString())}&`;
    for (const [path, kind, params] of [
      [`/repos/${repository}/issues/${number}/comments`, 'issue-comment', window],
      [`/repos/${repository}/pulls/${number}/comments`, 'review-comment', window],
      [`/repos/${repository}/pulls/${number}/reviews`, 'review', ''],
    ] as const) {
      for (let page = 1; page <= MAX_COMMENT_PAGES; page++) {
        const url = `${API_URL}${path}?${params}per_page=${PER_PAGE}${page > 1 ? `&page=${page}` : ''}`;
        const outcome = await this.get(url, token, 'comments');
        if (outcome.kind !== 'ok') return outcome;
        const list = Array.isArray(outcome.body) ? (outcome.body as unknown[]) : [];
        for (const raw of list) {
          const comment = asRecord(raw);
          if (typeof comment['id'] !== 'number') continue;
          if (kind === 'review' && !(Date.parse(String(comment['submitted_at'])) >= since)) {
            continue;
          }
          const entry = mentionEntry(comment, `${kind}:${comment['id']}`, ref, title, bot);
          if (entry !== null) entries.push(entry);
        }
        if (list.length < PER_PAGE) break;
      }
    }
    return { kind: 'ok', entries };
  }

  private search(q: string, page: number, token: string): Promise<Outcome> {
    const params = new URLSearchParams({
      q,
      sort: 'updated',
      order: 'desc',
      per_page: String(PER_PAGE),
      ...(page > 1 ? { page: String(page) } : {}),
    });
    return this.get(`${SEARCH_URL}?${params.toString()}`, token, 'search');
  }

  private async get(url: string, token: string, what: 'search' | 'comments'): Promise<Outcome> {
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
      logger.warn('github activity: GitHub was not reached; will try again', {
        read: what,
        error: err instanceof Error ? err.name : typeof err,
      });
      return { kind: 'retry', after: POLL_MS };
    }
    if (response.status === 401) {
      this.stopped = true;
      logger.warn('github activity: GitHub rejected the token; waiting for a new connection');
      return { kind: 'stop' };
    }
    if (response.status === 403 || response.status === 429) {
      const after = this.backOff(response.headers);
      logger.warn('github activity: GitHub asked Scout to slow down; will try again', {
        read: what,
        status: response.status,
        afterSeconds: Math.round(after / 1000),
      });
      return { kind: 'retry', after };
    }
    if (!response.ok) {
      logger.warn('github activity: GitHub answered an error; will try again', {
        read: what,
        status: response.status,
      });
      return { kind: 'retry', after: POLL_MS };
    }
    let body: unknown;
    try {
      body = await response.json();
    } catch {
      logger.warn('github activity: GitHub answered something unreadable; will try again');
      return { kind: 'retry', after: POLL_MS };
    }
    const { items, total_count: total } = (body ?? {}) as {
      items?: unknown;
      total_count?: unknown;
    };
    const list = Array.isArray(items) ? items : [];
    return {
      kind: 'ok',
      items: list,
      total: typeof total === 'number' ? total : list.length,
      body,
    };
  }

  private backOff(headers: Headers): number {
    const retryAfter = Number(headers.get('Retry-After'));
    const reset = Number(headers.get('X-RateLimit-Reset'));
    const waits = [POLL_MS];
    if (headers.get('Retry-After') !== null && Number.isFinite(retryAfter)) {
      waits.push(retryAfter * 1000);
    }
    if (headers.get('X-RateLimit-Remaining') === '0' && Number.isFinite(reset)) {
      waits.push(reset * 1000 - this.deps.now());
    }
    return Math.max(...waits);
  }
}

function isoSeconds(at: number): string {
  return `${new Date(at).toISOString().slice(0, 19)}+00:00`;
}

function asRecord(raw: unknown): Record<string, unknown> {
  return (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** True when `text` names `@<slug>` as a whole handle, in any case: not inside an address or a longer handle. */
export function mentionsBot(text: string, slug: string): boolean {
  return new RegExp(`(^|[^A-Za-z0-9_-])@${escapeRegExp(slug)}(?![A-Za-z0-9_-])`, 'i').test(text);
}

function firstLine(text: string): string {
  const line =
    text
      .split(/\r?\n/)
      .map((l) => l.trim())
      .find((l) => l.length > 0) ?? '';
  return line.length <= MAX_LINE_CHARS ? line : `${line.slice(0, MAX_LINE_CHARS - 1)}…`;
}

function mentionEntry(
  source: Record<string, unknown>,
  key: string,
  ref: string,
  title: string,
  bot: GitHubBotMention,
): NotificationInput | null {
  const text = source['body'];
  const author = asRecord(source['user'])['login'];
  const written = source['created_at'] ?? source['submitted_at'];
  const createdAt = typeof written === 'string' ? Date.parse(written) : NaN;
  const url = source['html_url'];
  if (
    typeof text !== 'string' ||
    typeof author !== 'string' ||
    !Number.isFinite(createdAt) ||
    author.toLowerCase() === `${bot.slug}[bot]`.toLowerCase() ||
    !mentionsBot(text, bot.slug)
  ) {
    return null;
  }
  return {
    id: `mention:${key}`,
    kind: 'mention',
    title: `@${author} mentioned @${bot.slug} on ${ref}: ${firstLine(text)}`,
    detail: title.length <= MAX_TITLE_CHARS ? title : `${title.slice(0, MAX_TITLE_CHARS - 1)}…`,
    at: createdAt,
    ...(typeof url === 'string' && COMMENT_URL.test(url) ? { url } : {}),
    actions: [{ id: 'review-pull-request', label: 'Review it' }],
    source: 'github',
  };
}

function toEntry(raw: unknown, query: Query): NotificationInput | null {
  const r = asRecord(raw);
  const { id, number, title } = r;
  const repository = REPOSITORY_URL.exec(String(r['repository_url']))?.[1];
  const htmlUrl = r['html_url'];
  const updatedAt = typeof r['updated_at'] === 'string' ? Date.parse(r['updated_at']) : NaN;
  if (
    typeof id !== 'number' ||
    typeof number !== 'number' ||
    typeof title !== 'string' ||
    repository === undefined ||
    !Number.isFinite(updatedAt)
  ) {
    return null;
  }
  return {
    id: `github:${id}`,
    kind: 'github',
    title: title.length <= MAX_TITLE_CHARS ? title : `${title.slice(0, MAX_TITLE_CHARS - 1)}…`,
    detail: query.detail(`${repository}#${number}`),
    at: updatedAt,
    ...(typeof htmlUrl === 'string' && HTML_URL.test(htmlUrl) ? { url: htmlUrl } : {}),
    ...(typeof r['comments'] === 'number' ? { comments: r['comments'] } : {}),
    source: 'github',
  };
}
