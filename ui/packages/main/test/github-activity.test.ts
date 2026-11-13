import { describe, expect, it } from 'bun:test';
import {
  FIRST_LOOK_BACK_MS,
  GitHubActivityPoll,
  POLL_MS,
  QUERIES,
  SEARCH_URL,
  type GitHubBotMention,
} from '../src/notifications/github-activity.js';
import {
  NotificationStore,
  type NotificationInput,
} from '../src/notifications/notification-store.js';
import { recordingLogger } from './kubernetes-fakes.js';

const TOKEN = 'ghu_ownerTokenNeverLogged';
const LOGIN = 'vitorafgomes';
const START = Date.parse('2026-11-07T12:00:00Z');

type Reply = Response | Error;

interface Sent {
  url: string;
  q: string;
  headers: Record<string, string>;
}

function item(id: number, repo: string, number: number, kind: 'pull' | 'issues' = 'pull') {
  return {
    id,
    number,
    title: `Title ${id}`,
    repository_url: `https://api.github.com/repos/${repo}`,
    html_url: `https://github.com/${repo}/${kind}/${number}`,
    updated_at: '2026-11-07T11:50:00Z',
  };
}

function ok(items: unknown[] = []): Response {
  return new Response(JSON.stringify({ total_count: items.length, items }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function status(code: number, headers: Record<string, string> = {}): Response {
  return new Response('{}', { status: code, headers });
}

function fiveOk(...first: unknown[][]): Response[] {
  return [0, 1, 2, 3, 4].map((i) => ok(first[i] ?? []));
}

function setup(replies: Reply[], connected = true) {
  const sent: Sent[] = [];
  const recorded: NotificationInput[] = [];
  const account = { connected };
  let cleared = 0;
  const timers: { ms: number; callback: () => void; cancelled: boolean }[] = [];
  const clock = { now: START };
  const { logger, lines } = recordingLogger();
  const poll = new GitHubActivityPoll({
    fetch: (async (url: string, init: RequestInit) => {
      sent.push({
        url,
        q: new URL(url).searchParams.get('q') ?? '',
        headers: init.headers as Record<string, string>,
      });
      const reply = replies.shift();
      if (reply === undefined) throw new Error('no scripted reply');
      if (reply instanceof Error) throw reply;
      return reply;
    }) as unknown as typeof fetch,
    account: async () => (account.connected ? { token: TOKEN, login: LOGIN } : null),
    record: (entry) => recorded.push(entry),
    clearEntries: () => {
      cleared += 1;
    },
    now: () => clock.now,
    setTimer: (ms, callback) => {
      const timer = { ms, callback, cancelled: false };
      timers.push(timer);
      return () => {
        timer.cancelled = true;
      };
    },
    logger,
  });
  const pending = () => timers.filter((t) => !t.cancelled).map((t) => t.ms);
  return { poll, sent, recorded, pending, lines, clock, account, cleared: () => cleared };
}

async function settleMicrotasks(): Promise<void> {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

describe('GitHub activity poll', () => {
  it('searches review requests, mentions and the owner’s own pull requests and issues for the signed-in login, each naming is:pr or is:issue', async () => {
    const { poll, sent } = setup(fiveOk());

    await poll.tick();

    expect(sent.map((s) => s.q)).toEqual([
      `is:pr is:open review-requested:${LOGIN} updated:>=2026-11-06T12:00:00+00:00`,
      `is:pr mentions:${LOGIN} updated:>=2026-11-06T12:00:00+00:00`,
      `is:issue mentions:${LOGIN} updated:>=2026-11-06T12:00:00+00:00`,
      `is:pr author:${LOGIN} comments:>0 updated:>=2026-11-06T12:00:00+00:00`,
      `is:issue author:${LOGIN} comments:>0 updated:>=2026-11-06T12:00:00+00:00`,
    ]);
    expect(sent.every((s) => /\bis:(pr|issue)\b/.test(s.q))).toBe(true);
    expect(new URL(sent[0]!.url).origin + new URL(sent[0]!.url).pathname).toBe(SEARCH_URL);
    expect(SEARCH_URL).toBe('https://api.github.com/search/issues');
    expect(sent[0]!.headers).toEqual({
      Accept: 'application/vnd.github+json',
      Authorization: `Bearer ${TOKEN}`,
      'User-Agent': 'Scout',
      'X-GitHub-Api-Version': '2022-11-28',
    });
  });

  it('reads the last 24 hours first, then from the start of the last complete read, every five minutes', async () => {
    const { poll, sent, pending, clock } = setup([...fiveOk(), ...fiveOk()]);

    await poll.tick();
    const firstWait = pending();
    clock.now = START + POLL_MS;
    await poll.tick();

    expect(FIRST_LOOK_BACK_MS).toBe(86_400_000);
    expect(firstWait).toEqual([300_000]);
    expect(sent[5]!.q.endsWith('updated:>=2026-11-07T12:00:00+00:00')).toBe(true);
  });

  it('records one github entry per item with its title, why, time and page, the first search’s reason winning', async () => {
    const { poll, recorded } = setup(
      fiveOk(
        [item(1, 'acme/api', 41)],
        [item(1, 'acme/api', 41), item(2, 'acme/api', 42)],
        [item(3, 'acme/api', 7, 'issues')],
        [item(4, 'acme/shop', 5)],
        [item(5, 'acme/shop', 9, 'issues')],
      ),
    );

    await poll.tick();

    const at = Date.parse('2026-11-07T11:50:00Z');
    expect(recorded).toEqual([
      {
        id: 'github:1',
        kind: 'github',
        title: 'Title 1',
        detail: 'Review requested on acme/api#41',
        at,
        url: 'https://github.com/acme/api/pull/41',
        source: 'github',
      },
      {
        id: 'github:2',
        kind: 'github',
        title: 'Title 2',
        detail: 'You were mentioned in acme/api#42',
        at,
        url: 'https://github.com/acme/api/pull/42',
        source: 'github',
      },
      {
        id: 'github:3',
        kind: 'github',
        title: 'Title 3',
        detail: 'You were mentioned in acme/api#7',
        at,
        url: 'https://github.com/acme/api/issues/7',
        source: 'github',
      },
      {
        id: 'github:4',
        kind: 'github',
        title: 'Title 4',
        detail: 'New activity on your pull request acme/shop#5',
        at,
        url: 'https://github.com/acme/shop/pull/5',
        source: 'github',
      },
      {
        id: 'github:5',
        kind: 'github',
        title: 'Title 5',
        detail: 'New activity on your issue acme/shop#9',
        at,
        url: 'https://github.com/acme/shop/issues/9',
        source: 'github',
      },
    ]);
  });

  it('an item whose page is not a github.com pull request or issue has no link', async () => {
    const odd = { ...item(6, 'acme/api', 1), html_url: 'https://evil.example/acme/api/pull/1' };
    const { poll, recorded } = setup(fiveOk([odd]));

    await poll.tick();

    expect(recorded.map((e) => [e.id, e.url])).toEqual([['github:6', undefined]]);
  });

  it('makes no request while GitHub is not connected, and checks again later', async () => {
    const { poll, sent, pending } = setup([], false);

    await poll.tick();

    expect(sent).toEqual([]);
    expect(pending()).toEqual([POLL_MS]);
  });

  it('a 401 stops the poll and waits for GitHub to be connected again, then reads the last 24 hours', async () => {
    const { poll, sent, recorded, pending, lines } = setup([status(401), ...fiveOk()]);

    await poll.tick();
    await poll.tick();

    expect(sent.length).toBe(1);
    expect(recorded).toEqual([]);
    expect(pending()).toEqual([]);
    expect(lines.some((l) => l.includes('waiting for a new connection'))).toBe(true);

    poll.reconnected();
    await settleMicrotasks();

    expect(sent.length).toBe(6);
    expect(sent[1]!.q.endsWith('updated:>=2026-11-06T12:00:00+00:00')).toBe(true);
  });

  it('a secondary rate limit with Retry-After waits that long and records nothing until a read completes', async () => {
    const { poll, sent, recorded, pending, clock } = setup([
      ok([item(1, 'acme/api', 41)]),
      status(403, { 'Retry-After': '900' }),
      ...fiveOk(),
    ]);

    await poll.tick();
    const waited = pending();
    clock.now = START + 900_000;
    await poll.tick();

    expect(waited).toEqual([900_000]);
    expect(recorded.map((e) => e.id)).toEqual([]);
    expect(sent.length).toBe(7);
    expect(sent[2]!.q.endsWith('updated:>=2026-11-06T12:15:00+00:00')).toBe(true);
  });

  it('a primary rate limit waits until X-RateLimit-Reset', async () => {
    const reset = String((START + 20 * 60_000) / 1000);
    const { poll, pending } = setup([
      status(403, { 'X-RateLimit-Remaining': '0', 'X-RateLimit-Reset': reset }),
    ]);

    await poll.tick();

    expect(pending()).toEqual([20 * 60_000]);
  });

  it('a 429 without headers backs off at least five minutes', async () => {
    const { poll, pending } = setup([status(429)]);

    await poll.tick();

    expect(pending()).toEqual([POLL_MS]);
  });

  it('a timeout keeps the entries, logs without the token and reads the same window on the next tick', async () => {
    const timeout = new DOMException('The operation timed out.', 'TimeoutError');
    const { poll, sent, recorded, pending, lines } = setup([timeout, ...fiveOk()]);

    await poll.tick();
    const afterTimeout = { recorded: recorded.length, pending: pending() };
    await poll.tick();

    expect(afterTimeout).toEqual({ recorded: 0, pending: [POLL_MS] });
    expect(
      lines.some((l) => l.includes('GitHub was not reached') && l.includes('TimeoutError')),
    ).toBe(true);
    expect(lines.join('\n')).not.toContain(TOKEN);
    expect(sent[1]!.q.endsWith('updated:>=2026-11-06T12:00:00+00:00')).toBe(true);
  });

  it('a 5xx keeps the entries and retries on the next tick', async () => {
    const { poll, recorded, pending, lines } = setup([
      ok([item(1, 'acme/api', 41)]),
      status(502),
      ...fiveOk([item(1, 'acme/api', 41)]),
    ]);

    await poll.tick();
    const afterFailure = { recorded: recorded.length, pending: pending() };
    await poll.tick();

    expect(afterFailure).toEqual({ recorded: 0, pending: [POLL_MS] });
    expect(lines.some((l) => l.includes('answered an error') && l.includes('502'))).toBe(true);
    expect(recorded.map((e) => e.id)).toEqual(['github:1']);
  });

  it('never logs the token on any outcome', async () => {
    const all: string[] = [];
    for (const reply of [status(401), status(403), status(422), status(500)]) {
      const { poll, lines } = setup([reply]);
      await poll.tick();
      all.push(...lines);
    }

    expect(all.length).toBeGreaterThan(0);
    expect(all.join('\n')).not.toContain(TOKEN);
  });

  it('dispose cancels the next tick', async () => {
    const { poll, pending } = setup(fiveOk());
    await poll.tick();

    poll.dispose();

    expect(pending()).toEqual([]);
  });
});

function busySetup(perQuery: number, clock: { now: number }) {
  const firstWindow = 'updated:>=2026-11-06T12:00:00+00:00';
  const sent: { query: number; page: number; window: string }[] = [];
  const recorded = new Set<string>();
  const timers: number[] = [];
  const poll = new GitHubActivityPoll({
    fetch: (async (url: string) => {
      const params = new URL(url).searchParams;
      const q = params.get('q') ?? '';
      const perPage = Number(params.get('per_page'));
      const page = Number(params.get('page') ?? '1');
      const query = QUERIES.findIndex((each) => q.startsWith(`${each.q(LOGIN)} updated:>=`));
      sent.push({ query, page, window: q.slice(q.indexOf('updated:>=')) });
      const total = q.endsWith(firstWindow) ? perQuery : 0;
      const first = (page - 1) * perPage;
      const items = Array.from({ length: Math.max(0, Math.min(perPage, total - first)) }, (_, i) =>
        item(100_000 * (query + 1) + first + i, 'acme/api', first + i + 1),
      );
      return new Response(JSON.stringify({ total_count: total, items }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }) as unknown as typeof fetch,
    account: async () => ({ token: TOKEN, login: LOGIN }),
    record: (entry) => recorded.add(entry.id),
    clearEntries: () => undefined,
    now: () => clock.now,
    setTimer: (ms) => {
      timers.push(ms);
      return () => undefined;
    },
    logger: recordingLogger().logger,
  });
  return { poll, sent, recorded, timers };
}

describe('GitHub activity poll on a busy account', () => {
  it('a read never sends more than GitHub’s 30 searches a minute, even when every search has 1,000 results and GitHub is reconnected at once', async () => {
    const clock = { now: START };
    const { poll, sent, timers } = busySetup(1_000, clock);

    await poll.tick();
    const firstRead = sent.length;
    poll.reconnected();
    for (let i = 0; i < 100 && timers.length < 2; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
    }

    expect(firstRead).toBeGreaterThan(QUERIES.length);
    expect(sent.length).toBeLessThanOrEqual(30);
    expect(timers).toEqual([POLL_MS, POLL_MS]);
  });

  it('a read that needs more searches than a minute allows goes on from the next page on the following ticks and records every item once it completes', async () => {
    const clock = { now: START };
    const { poll, sent, recorded } = busySetup(650, clock);

    await poll.tick();
    const afterFirst = { sent: sent.length, recorded: recorded.size };
    clock.now = START + POLL_MS;
    await poll.tick();
    const afterSecond = recorded.size;
    clock.now = START + 2 * POLL_MS;
    await poll.tick();

    const firstPass = sent.filter((s) => s.window === 'updated:>=2026-11-06T12:00:00+00:00');
    const everyPage = QUERIES.flatMap((_, query) =>
      Array.from({ length: 7 }, (_, i) => ({ query, page: i + 1 })),
    );
    expect(afterFirst.recorded).toBe(0);
    expect(afterFirst.sent).toBeLessThan(everyPage.length);
    expect(firstPass.map(({ query, page }) => ({ query, page }))).toEqual(everyPage);
    expect(afterSecond).toBe(5 * 650);
    expect(sent.slice(firstPass.length).map((s) => s.window)).toEqual(
      QUERIES.map(() => 'updated:>=2026-11-07T12:00:00+00:00'),
    );
  });

  it('a search that fails on its second page after the first answered records nothing from that read and reads the same window from the first page on the next tick', async () => {
    const pageOne = Array.from({ length: 100 }, (_, i) => item(2000 + i, 'acme/api', i + 1));
    const { poll, sent, recorded, pending } = setup([
      new Response(JSON.stringify({ total_count: 150, items: pageOne }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
      status(502),
      ...fiveOk(),
    ]);

    await poll.tick();
    const afterFailure = { recorded: recorded.length, pending: pending(), sent: sent.length };
    await poll.tick();

    expect(afterFailure).toEqual({ recorded: 0, pending: [POLL_MS], sent: 2 });
    expect(new URL(sent[1]!.url).searchParams.get('page')).toBe('2');
    expect(sent[2]!.q).toBe(sent[0]!.q);
    expect(new URL(sent[2]!.url).searchParams.get('page')).toBeNull();
    expect(recorded).toEqual([]);
  });

  it('records every item a search matches, not only the first page of results', async () => {
    const matches = Array.from({ length: 130 }, (_, i) => item(1000 + i, 'acme/api', i + 1));
    const firstWindow = 'updated:>=2026-11-06T12:00:00+00:00';
    const recorded = new Set<string>();
    const clock = { now: START };
    const poll = new GitHubActivityPoll({
      fetch: (async (url: string) => {
        const params = new URL(url).searchParams;
        const q = params.get('q') ?? '';
        const perPage = Number(params.get('per_page') ?? '30');
        const page = Number(params.get('page') ?? '1');
        const all =
          q.startsWith('is:pr is:open review-requested:') && q.endsWith(firstWindow) ? matches : [];
        const items = all.slice((page - 1) * perPage, page * perPage);
        return new Response(JSON.stringify({ total_count: all.length, items }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }) as unknown as typeof fetch,
      account: async () => ({ token: TOKEN, login: LOGIN }),
      record: (entry) => recorded.add(entry.id),
      clearEntries: () => undefined,
      now: () => clock.now,
      setTimer: () => () => undefined,
      logger: recordingLogger().logger,
    });

    await poll.tick();
    clock.now = START + POLL_MS;
    await poll.tick();

    expect(recorded.size).toBe(130);
  });

  const updates: [string, number, number, boolean][] = [
    [
      'a mention the owner already read stays read when the thread is only updated again',
      2,
      2,
      true,
    ],
    [
      'a mention the owner already read becomes unread when its thread got a new comment',
      2,
      3,
      false,
    ],
  ];

  for (const [name, before, after, staysRead] of updates) {
    it(name, async () => {
      const clock = { now: START };
      const store = new NotificationStore({
        vault: { get: async () => null, store: async () => undefined },
        push: () => undefined,
        now: () => clock.now,
        setTimer: () => () => undefined,
        logger: recordingLogger().logger,
      });
      const mention = (updatedAt: string, comments: number) => [
        { ...item(7, 'acme/api', 7), updated_at: updatedAt, comments },
      ];
      const replies = [
        ...fiveOk([], mention('2026-11-07T11:50:00Z', before)),
        ...fiveOk([], mention('2026-11-07T12:03:00Z', after)),
      ];
      const poll = new GitHubActivityPoll({
        fetch: (async () => replies.shift()!) as unknown as typeof fetch,
        account: async () => ({ token: TOKEN, login: LOGIN }),
        record: (entry) => store.record(entry),
        clearEntries: () => store.remove((n) => n.source === 'github'),
        now: () => clock.now,
        setTimer: () => () => undefined,
        logger: recordingLogger().logger,
      });

      await poll.tick();
      store.markRead();
      clock.now = START + POLL_MS;
      await poll.tick();

      const entry = store.list().find((n) => n.id === 'github:7');
      expect(entry?.detail).toBe('You were mentioned in acme/api#7');
      expect(entry?.comments).toBe(after);
      expect(entry?.read).toBe(staysRead);
    });
  }
});

describe('GitHub activity poll and the connection', () => {
  it('a poll a rejected token stopped reads again after the token is refreshed, from where it was', async () => {
    const { poll, sent, pending, clock } = setup([...fiveOk(), status(401), ...fiveOk()]);
    await poll.tick();
    clock.now = START + POLL_MS;
    await poll.tick();
    const stoppedWith = pending();

    poll.tokenRefreshed();
    await settleMicrotasks();

    expect(stoppedWith).toEqual([]);
    expect(sent.length).toBe(11);
    expect(sent[6]!.q.endsWith('updated:>=2026-11-07T12:00:00+00:00')).toBe(true);
    expect(pending()).toEqual([POLL_MS]);
  });

  it('a refresh while the poll runs starts no extra read', async () => {
    const { poll, sent } = setup(fiveOk());
    await poll.tick();

    poll.tokenRefreshed();
    await settleMicrotasks();

    expect(sent.length).toBe(5);
  });

  it('a connection change that leaves GitHub disconnected removes the GitHub entries', async () => {
    const { poll, account, cleared } = setup([]);
    account.connected = false;

    poll.reconnected();
    await settleMicrotasks();

    expect(cleared()).toBe(1);
  });

  it('a connection change with an account keeps the GitHub entries', async () => {
    const { poll, cleared } = setup(fiveOk());

    poll.reconnected();
    await settleMicrotasks();

    expect(cleared()).toBe(0);
  });

  it('a read still running when GitHub is disconnected records nothing afterwards', async () => {
    let release: () => void = () => undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    const replies = fiveOk([item(1, 'acme/api', 41)]);
    const recorded: NotificationInput[] = [];
    const connection = { connected: true };
    const poll = new GitHubActivityPoll({
      fetch: (async () => {
        await held;
        return replies.shift()!;
      }) as unknown as typeof fetch,
      account: async () => (connection.connected ? { token: TOKEN, login: LOGIN } : null),
      record: (entry) => recorded.push(entry),
      clearEntries: () => undefined,
      now: () => START,
      setTimer: () => () => undefined,
      logger: recordingLogger().logger,
    });
    const inFlight = poll.tick();

    connection.connected = false;
    poll.reconnected();
    release();
    await inFlight;
    await settleMicrotasks();

    expect(recorded).toEqual([]);
  });
});

const BOT: GitHubBotMention = { slug: 'scoutbot', account: LOGIN, accountType: 'user' };
const FIRST_SINCE = '2026-11-06T12:00:00.000Z';
const COMMENTS_ORIGIN = 'https://api.github.com/repos/';

function pr(id: number, repo: string, number: number, extra: Record<string, unknown> = {}) {
  return {
    ...item(id, repo, number),
    title: `Pull request ${number}`,
    body: 'No mention here.',
    user: { login: 'alice' },
    created_at: '2026-11-07T09:00:00Z',
    ...extra,
  };
}

function comment(
  id: number,
  repo: string,
  number: number,
  body: string,
  author = LOGIN,
  kind: 'issue' | 'review' = 'issue',
) {
  return {
    id,
    body,
    user: { login: author },
    created_at: '2026-11-07T11:40:00Z',
    updated_at: '2026-11-07T11:45:00Z',
    html_url: `https://github.com/${repo}/pull/${number}#${kind === 'issue' ? 'issuecomment-' : 'discussion_r'}${id}`,
  };
}

interface MentionWorld {
  candidates: unknown[];
  issueComments: Map<string, unknown[] | Reply>;
  reviewComments: Map<string, unknown[] | Reply>;
  reviews: Map<string, unknown[] | Reply>;
  searchFailure: Reply | null;
}

function mentionSetup(bot: GitHubBotMention | null = BOT) {
  const world: MentionWorld = {
    candidates: [],
    issueComments: new Map(),
    reviewComments: new Map(),
    reviews: new Map(),
    searchFailure: null,
  };
  const sent: { url: string; authorization: string }[] = [];
  const clock = { now: START };
  const { logger, lines } = recordingLogger();
  const store = new NotificationStore({
    vault: { get: async () => null, store: async () => undefined },
    push: () => undefined,
    now: () => clock.now,
    setTimer: () => () => undefined,
    logger,
  });
  const account = { connected: true };
  const timers: number[] = [];
  const json = (body: unknown) =>
    new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    });
  const answer = (reply: unknown[] | Reply | undefined): Response => {
    if (reply instanceof Error) throw reply;
    if (reply instanceof Response) return reply;
    return json(reply ?? []);
  };
  const poll = new GitHubActivityPoll({
    fetch: (async (url: string, init: RequestInit) => {
      sent.push({
        url,
        authorization: (init.headers as Record<string, string>)['Authorization'] ?? '',
      });
      const parsed = new URL(url);
      if (url.startsWith(SEARCH_URL)) {
        const q = parsed.searchParams.get('q') ?? '';
        if (!q.startsWith('"@')) return json({ total_count: 0, items: [] });
        if (world.searchFailure !== null) return answer(world.searchFailure);
        return json({ total_count: world.candidates.length, items: world.candidates });
      }
      const match = /^\/repos\/([^/]+\/[^/]+)\/(issues|pulls)\/(\d+)\/(comments|reviews)$/.exec(
        parsed.pathname,
      );
      if (match === null) throw new Error(`unexpected request to ${url}`);
      const key = `${match[1]}#${match[3]}`;
      const source =
        match[4] === 'reviews'
          ? world.reviews
          : match[2] === 'issues'
            ? world.issueComments
            : world.reviewComments;
      return answer(source.get(key));
    }) as unknown as typeof fetch,
    account: async () => (account.connected ? { token: TOKEN, login: LOGIN } : null),
    bot: async () => bot,
    record: (entry) => store.record(entry),
    clearEntries: () => store.remove((n) => n.source === 'github'),
    now: () => clock.now,
    setTimer: (ms) => {
      timers.push(ms);
      return () => undefined;
    },
    logger,
  });
  const mentions = () => store.list().filter((n) => n.kind === 'mention');
  const searches = () =>
    sent.filter((s) => s.url.startsWith(SEARCH_URL)).map((s) => new URL(s.url).searchParams);
  const commentReads = () => sent.filter((s) => s.url.startsWith(COMMENTS_ORIGIN));
  return {
    poll,
    world,
    store,
    clock,
    lines,
    account,
    timers,
    mentions,
    searches,
    commentReads,
  };
}

describe('GitHub activity poll: mentions of the Scout bot', () => {
  it('with an installed bot, a read adds one search for the phrase "@<slug>" in the comments and body of open pull requests on the bot’s account, in the same window', async () => {
    const user = mentionSetup();
    const org = mentionSetup({ slug: 'scout-review', account: 'acme', accountType: 'org' });
    const none = mentionSetup(null);

    await user.poll.tick();
    await org.poll.tick();
    await none.poll.tick();

    expect(user.searches().map((p) => p.get('q'))).toHaveLength(6);
    expect(user.searches()[5]!.get('q')).toBe(
      `"@scoutbot" in:comments,body is:pr is:open user:${LOGIN} updated:>=2026-11-06T12:00:00+00:00`,
    );
    expect(org.searches()[5]!.get('q')).toBe(
      '"@scout-review" in:comments,body is:pr is:open org:acme updated:>=2026-11-06T12:00:00+00:00',
    );
    expect(none.searches()).toHaveLength(5);
    expect(none.searches().some((p) => (p.get('q') ?? '').includes('"@'))).toBe(false);
  });

  it('the owner’s own comment "@ScoutBot review" on a lowercase slug becomes one unread mention entry: who, the bot, the pull request, the first line, the comment’s link and Review it', async () => {
    const { poll, world, mentions, commentReads } = mentionSetup();
    world.candidates = [pr(501, 'acme/shop', 20)];
    world.issueComments.set('acme/shop#20', [
      comment(9001, 'acme/shop', 20, '  @ScoutBot review  \r\nthe second line is not shown'),
    ]);

    await poll.tick();

    expect(mentions()).toEqual([
      {
        id: 'mention:issue-comment:9001',
        kind: 'mention',
        title: `@${LOGIN} mentioned @scoutbot on acme/shop#20: @ScoutBot review`,
        detail: 'Pull request 20',
        at: Date.parse('2026-11-07T11:40:00Z'),
        url: 'https://github.com/acme/shop/pull/20#issuecomment-9001',
        actions: [{ id: 'review-pull-request', label: 'Review it' }],
        source: 'github',
        read: false,
      },
    ]);
    expect(commentReads().map((r) => [r.url, r.authorization])).toEqual([
      [
        `https://api.github.com/repos/acme/shop/issues/20/comments?since=${encodeURIComponent(FIRST_SINCE)}&per_page=100`,
        `Bearer ${TOKEN}`,
      ],
      [
        `https://api.github.com/repos/acme/shop/pulls/20/comments?since=${encodeURIComponent(FIRST_SINCE)}&per_page=100`,
        `Bearer ${TOKEN}`,
      ],
      ['https://api.github.com/repos/acme/shop/pulls/20/reviews?per_page=100', `Bearer ${TOKEN}`],
    ]);
  });

  it('a review comment and the pull request’s body that mention the bot are entries too, linking to the comment and to the pull request', async () => {
    const { poll, world, mentions } = mentionSetup();
    world.candidates = [
      pr(502, 'acme/shop', 21, { body: 'Opened for @scoutbot to look at', user: { login: 'bob' } }),
    ];
    world.reviewComments.set('acme/shop#21', [
      comment(7001, 'acme/shop', 21, 'What does @scoutbot think of this line?', 'carol', 'review'),
    ]);

    await poll.tick();

    expect(mentions().map((n) => [n.id, n.title, n.url])).toEqual([
      [
        'mention:review-comment:7001',
        '@carol mentioned @scoutbot on acme/shop#21: What does @scoutbot think of this line?',
        'https://github.com/acme/shop/pull/21#discussion_r7001',
      ],
      [
        'mention:body:502',
        '@bob mentioned @scoutbot on acme/shop#21: Opened for @scoutbot to look at',
        'https://github.com/acme/shop/pull/21',
      ],
    ]);
  });

  const phrases: [string, boolean][] = [
    ['@scoutbot review', true],
    ['@SCOUTBOT please review', true],
    ['please review (@scoutbot)', true],
    ['first line\n@scoutbot, now', true],
    ['scoutbot review this', false],
    ['@scoutbot-two review', false],
    ['@scoutbot_two review', false],
    ['@scoutbots review', false],
    ['write to me@scoutbot today', false],
    ['@scout bot review', false],
    ['@other review, scoutbot', false],
  ];

  it('a phrase hit becomes an entry only when the comment names @<slug> as a whole handle, in any case', async () => {
    const { poll, world, mentions } = mentionSetup();
    world.candidates = [pr(503, 'acme/shop', 22)];
    world.issueComments.set(
      'acme/shop#22',
      phrases.map(([text], i) => comment(8000 + i, 'acme/shop', 22, text)),
    );

    await poll.tick();

    const entered = new Set(mentions().map((n) => n.id));
    expect(
      phrases.map(([text], i) => [text, entered.has(`mention:issue-comment:${8000 + i}`)]),
    ).toEqual(phrases);
  });

  it('the bot’s own comment is never an entry, in any case of its login, while the owner’s mention is', async () => {
    const { poll, world, mentions } = mentionSetup();
    world.candidates = [
      pr(504, 'acme/shop', 23, { body: '@scoutbot hi', user: { login: 'scoutbot[bot]' } }),
    ];
    world.issueComments.set('acme/shop#23', [
      comment(9101, 'acme/shop', 23, '@scoutbot is preparing the review', 'scoutbot[bot]'),
      comment(9102, 'acme/shop', 23, '@scoutbot done', 'ScoutBot[bot]'),
      comment(9103, 'acme/shop', 23, '@scoutbot review'),
    ]);

    await poll.tick();

    expect(mentions().map((n) => n.id)).toEqual(['mention:issue-comment:9103']);
  });

  it('one entry per comment: an edit updates it in place and keeps it read, and a new mention in another comment is a new unread entry', async () => {
    const { poll, world, store, clock, mentions } = mentionSetup();
    world.candidates = [pr(505, 'acme/shop', 24)];
    world.issueComments.set('acme/shop#24', [comment(9201, 'acme/shop', 24, '@scoutbot review')]);
    await poll.tick();
    store.markRead();

    world.issueComments.set('acme/shop#24', [
      {
        ...comment(9201, 'acme/shop', 24, '@scoutbot review again'),
        updated_at: '2026-11-07T12:02:00Z',
      },
      {
        ...comment(9202, 'acme/shop', 24, '@scoutbot and this one'),
        created_at: '2026-11-07T12:01:00Z',
      },
    ]);
    clock.now = START + POLL_MS;
    await poll.tick();

    expect(mentions().map((n) => [n.id, n.title, n.read])).toEqual([
      [
        'mention:issue-comment:9202',
        `@${LOGIN} mentioned @scoutbot on acme/shop#24: @scoutbot and this one`,
        false,
      ],
      [
        'mention:issue-comment:9201',
        `@${LOGIN} mentioned @scoutbot on acme/shop#24: @scoutbot review again`,
        true,
      ],
    ]);
  });

  it('GitHub Disconnect clears the mention entries with the other GitHub entries', async () => {
    const { poll, world, store, account, mentions } = mentionSetup();
    world.candidates = [pr(506, 'acme/shop', 25)];
    world.issueComments.set('acme/shop#25', [comment(9301, 'acme/shop', 25, '@scoutbot review')]);
    store.record({
      id: 'worker:1',
      kind: 'done',
      title: 'Audit done',
      detail: '',
      source: 'worker',
    });
    await poll.tick();
    const before = mentions().length;

    account.connected = false;
    poll.reconnected();
    await settleMicrotasks();

    expect(before).toBe(1);
    expect(mentions()).toEqual([]);
    expect(store.list().map((n) => n.id)).toEqual(['worker:1']);
  });

  it('when the mention search fails, nothing is recorded and the next tick reads the same window again', async () => {
    const { poll, world, store, clock, searches, timers } = mentionSetup();
    await poll.tick();
    world.candidates = [pr(507, 'acme/shop', 26)];
    world.issueComments.set('acme/shop#26', [comment(9401, 'acme/shop', 26, '@scoutbot review')]);
    world.searchFailure = status(502);
    clock.now = START + POLL_MS;

    await poll.tick();
    const afterFailure = { entries: store.list().length, waits: timers.slice(1) };
    world.searchFailure = null;
    clock.now = START + 2 * POLL_MS;
    await poll.tick();

    expect(afterFailure).toEqual({ entries: 0, waits: [POLL_MS] });
    expect(searches()[17]!.get('q')).toBe(
      `"@scoutbot" in:comments,body is:pr is:open user:${LOGIN} updated:>=2026-11-07T12:00:00+00:00`,
    );
    expect(store.list().map((n) => n.id)).toEqual(['mention:issue-comment:9401']);
  });

  it.each([
    [
      'times out',
      new DOMException('The operation timed out.', 'TimeoutError') as Reply,
      'TimeoutError',
    ],
    ['answers 502', status(502) as Reply, '502'],
  ] as const)(
    'when a comment read %s, no entry is recorded, the window is kept and the next tick confirms it',
    async (_case, failure, logged) => {
      const { poll, world, store, clock, searches, commentReads, lines } = mentionSetup();
      await poll.tick();
      world.candidates = [pr(508, 'acme/shop', 27)];
      world.issueComments.set('acme/shop#27', failure);
      clock.now = START + POLL_MS;

      await poll.tick();
      const afterFailure = store.list().length;
      world.issueComments.set('acme/shop#27', [comment(9501, 'acme/shop', 27, '@scoutbot review')]);
      clock.now = START + 2 * POLL_MS;
      await poll.tick();

      const kept = encodeURIComponent('2026-11-07T12:00:00.000Z');
      expect(afterFailure).toBe(0);
      expect(lines.some((l) => l.includes('"read":"comments"') && l.includes(logged))).toBe(true);
      expect(lines.join('\n')).not.toContain(TOKEN);
      expect(searches()[17]!.get('q')!.endsWith('updated:>=2026-11-07T12:00:00+00:00')).toBe(true);
      expect(
        commentReads()
          .filter((r) => r.url.includes('/issues/27/comments'))
          .at(-1)!.url,
      ).toContain(`since=${kept}&`);
      expect(store.list().map((n) => n.id)).toEqual(['mention:issue-comment:9501']);
    },
  );

  it('a comment read GitHub answers with 401 stops the poll and records nothing', async () => {
    const { poll, world, store, timers } = mentionSetup();
    world.candidates = [pr(509, 'acme/shop', 28)];
    world.issueComments.set('acme/shop#28', status(401));

    await poll.tick();

    expect(store.list()).toEqual([]);
    expect(timers).toEqual([]);
  });
});

describe('GitHub activity poll on a busy account with the bot’s mention search', () => {
  it('the mention search counts in the 20 searches a minute, goes on from its page on the following tick, and the comment reads are not searches', async () => {
    const clock = { now: START };
    const perQuery = 400;
    const firstWindow = 'updated:>=2026-11-06T12:00:00+00:00';
    const searchesAt: number[] = [];
    const mentionPages: number[] = [];
    let commentReads = 0;
    const recorded = new Set<string>();
    const poll = new GitHubActivityPoll({
      fetch: (async (url: string) => {
        if (!url.startsWith(SEARCH_URL)) {
          commentReads += 1;
          return new Response('[]', { status: 200 });
        }
        searchesAt.push(clock.now);
        const params = new URL(url).searchParams;
        const q = params.get('q') ?? '';
        const page = Number(params.get('page') ?? '1');
        const mention = q.startsWith('"@');
        if (mention) mentionPages.push(page);
        const query = mention ? 5 : QUERIES.findIndex((each) => q.startsWith(each.q(LOGIN)));
        const total = q.endsWith(firstWindow) ? perQuery : 0;
        const first = (page - 1) * 100;
        const items = Array.from({ length: Math.max(0, Math.min(100, total - first)) }, (_, i) =>
          item(100_000 * (query + 1) + first + i, 'acme/api', first + i + 1),
        );
        return new Response(JSON.stringify({ total_count: total, items }), { status: 200 });
      }) as unknown as typeof fetch,
      account: async () => ({ token: TOKEN, login: LOGIN }),
      bot: async () => BOT,
      record: (entry) => recorded.add(entry.id),
      clearEntries: () => undefined,
      now: () => clock.now,
      setTimer: () => () => undefined,
      logger: recordingLogger().logger,
    });

    await poll.tick();
    const afterFirst = { searches: searchesAt.length, recorded: recorded.size, commentReads };
    clock.now = START + POLL_MS;
    await poll.tick();

    const perMinute = searchesAt.map(
      (at) => searchesAt.filter((t) => t > at - 60_000 && t <= at).length,
    );
    expect(afterFirst).toEqual({ searches: 20, recorded: 0, commentReads: 0 });
    expect(Math.max(...perMinute)).toBeLessThanOrEqual(20);
    expect(mentionPages.slice(0, 4)).toEqual([1, 2, 3, 4]);
    expect(searchesAt.length).toBe(24);
    expect(commentReads).toBe(3 * perQuery);
    expect(recorded.size).toBe(5 * perQuery);
  });
});

describe('GitHub activity poll: mentions of the Scout bot in review summaries', () => {
  const review = (id: number, body: string, submittedAt: string, author = LOGIN) => ({
    id,
    body,
    user: { login: author },
    submitted_at: submittedAt,
    html_url: `https://github.com/acme/shop/pull/30#pullrequestreview-${id}`,
  });

  it('a review summary submitted in the window that names @<slug> is one entry linking to the review; an older review, the bot’s own and a phrase without the handle are not', async () => {
    const { poll, world, mentions } = mentionSetup();
    world.candidates = [pr(510, 'acme/shop', 30)];
    world.reviews.set('acme/shop#30', [
      review(6001, 'Looks fine, but @ScoutBot please check the tests', '2026-11-07T11:30:00Z'),
      review(6002, '@scoutbot review this', '2026-11-05T10:00:00Z'),
      review(6003, '@scoutbot has prepared its review', '2026-11-07T11:31:00Z', 'scoutbot[bot]'),
      review(6004, 'scoutbot review this', '2026-11-07T11:32:00Z'),
      review(6005, '', '2026-11-07T11:33:00Z'),
    ]);

    await poll.tick();

    expect(mentions().map((n) => [n.id, n.title, n.url, n.at])).toEqual([
      [
        'mention:review:6001',
        `@${LOGIN} mentioned @scoutbot on acme/shop#30: Looks fine, but @ScoutBot please check the tests`,
        'https://github.com/acme/shop/pull/30#pullrequestreview-6001',
        Date.parse('2026-11-07T11:30:00Z'),
      ],
    ]);
  });

  it('a review read again in the next window stays one entry and keeps its read flag, and a new review is a new entry', async () => {
    const { poll, world, store, clock, mentions } = mentionSetup();
    world.candidates = [pr(511, 'acme/shop', 30)];
    world.reviews.set('acme/shop#30', [review(6101, '@scoutbot review', '2026-11-07T12:00:30Z')]);
    await poll.tick();
    store.markRead();

    world.reviews.set('acme/shop#30', [
      review(6101, '@scoutbot review', '2026-11-07T12:00:30Z'),
      review(6102, '@scoutbot again', '2026-11-07T12:01:00Z'),
    ]);
    clock.now = START + POLL_MS;
    await poll.tick();

    expect(mentions().map((n) => [n.id, n.read])).toEqual([
      ['mention:review:6102', false],
      ['mention:review:6101', true],
    ]);
  });
});
