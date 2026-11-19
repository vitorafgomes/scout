import { describe, expect, it } from 'bun:test';
import { readFile } from 'node:fs/promises';
import * as path from 'node:path';
import { ScoutError, type GitHubVerdictRequest, type GitHubWaitingApproval } from '@scout/shared';
import {
  MAX_WAITING,
  WAIT_MAX_MS,
  WAIT_POLL_MS,
  WaitingApprovals,
} from '../src/github/waiting-approvals.js';
import type { NotificationInput } from '../src/notifications/notification-store.js';
import { recordingLogger } from './kubernetes-fakes.js';

const HEAD = '3f2a9c1d4b5e6f708192a3b4c5d6e7f809a1b2c3';
const TOKEN = 'gho_waiting-approvals-secret';
const REQUEST: GitHubVerdictRequest = {
  repository: 'acme/shop',
  number: 5,
  verdict: 'approve',
  body: '**Verdict:** merge',
  comments: [{ path: 'src/Cart.cs', line: 12, body: 'Lock the move' }],
  commit: HEAD,
  testsLine: 'Tests: 42 passed in the container (dotnet test).',
};
const KEY = { repository: REQUEST.repository, number: REQUEST.number, commit: REQUEST.commit };
const ENTRY_ID = `approval:acme/shop#5@${HEAD}`;
const PULL_URL = 'https://api.github.com/repos/acme/shop/pulls/5';
const RUNS_URL = `https://api.github.com/repos/acme/shop/commits/${HEAD}/check-runs?per_page=100`;
const STATUS_URL = `https://api.github.com/repos/acme/shop/commits/${HEAD}/status?per_page=100`;

type Reply = () => Response;
type Check = readonly [name: string, status: string, conclusion: string | null];

function json(body: unknown, status = 200, headers: Record<string, string> = {}): Reply {
  return () => new Response(JSON.stringify(body), { status, headers });
}

const MERGEABLE = { mergeable: true, mergeable_state: 'clean' } as const;

function pull(
  sha = HEAD,
  state = 'open',
  mergeability: Record<string, unknown> = MERGEABLE,
): Reply {
  return json({ state, head: { sha }, ...mergeability });
}

function runs(checks: readonly Check[], total = checks.length): Reply {
  return json({
    total_count: total,
    check_runs: checks.map(([name, status, conclusion]) => ({ name, status, conclusion })),
  });
}

function statuses(list: readonly (readonly [context: string, state: string])[]): Reply {
  return json({ state: 'pending', statuses: list.map(([context, state]) => ({ context, state })) });
}

const PASSED: Reply = runs([['build', 'completed', 'success']]);
const NO_STATUSES: Reply = statuses([]);

function setup(saved: string | null = null, vault: { loadFails?: Error; saveFails?: Error } = {}) {
  const github: { pull: Reply | Error; runs: Reply | Error; statuses: Reply | Error } = {
    pull: pull(),
    runs: PASSED,
    statuses: NO_STATUSES,
  };
  const fetched: { url: string; method: string | undefined; authorization: string | null }[] = [];
  const fetchFn = (async (input: string | URL | Request, init?: RequestInit) => {
    const url = String(input);
    fetched.push({
      url,
      method: init?.method,
      authorization: new Headers(init?.headers).get('Authorization'),
    });
    const replies: Record<string, Reply | Error> = {
      [PULL_URL]: github.pull,
      [RUNS_URL]: github.runs,
      [STATUS_URL]: github.statuses,
    };
    const reply = replies[url] ?? new Error(`unexpected read of ${url}`);
    if (reply instanceof Error) throw reply;
    return reply();
  }) as typeof globalThis.fetch;
  let clock = 1_000;
  let token: string | null = TOKEN;
  let giveFailure: Error | null = null;
  let heldGive: Promise<void> | null = null;
  const given: GitHubVerdictRequest[] = [];
  const pushed: GitHubWaitingApproval[] = [];
  const recorded: NotificationInput[] = [];
  const removed: string[] = [];
  const saves: string[] = [];
  const timers: { ms: number; callback: () => void; cancelled: boolean }[] = [];
  const { logger, lines } = recordingLogger();
  const waiting = new WaitingApprovals({
    fetch: fetchFn,
    token: async () => token,
    give: async (request) => {
      if (heldGive !== null) await heldGive;
      if (giveFailure !== null) throw giveFailure;
      given.push(request);
      return { actionId: 'act-1' };
    },
    push: (status) => pushed.push(status),
    record: (entry) => recorded.push(entry),
    remove: (id) => removed.push(id),
    load: async () => {
      if (vault.loadFails !== undefined) throw vault.loadFails;
      return saved;
    },
    save: async (text) => {
      if (vault.saveFails !== undefined) throw vault.saveFails;
      saves.push(text);
    },
    now: () => clock,
    setTimer: (ms, callback) => {
      const timer = { ms, callback, cancelled: false };
      timers.push(timer);
      return () => {
        timer.cancelled = true;
      };
    },
    logger,
  });

  async function settled(): Promise<void> {
    for (let i = 0; i < 30; i += 1) await new Promise((resolve) => setTimeout(resolve, 0));
  }

  /** Runs the next scheduled read and answers the wait main set for it. */
  async function tick(): Promise<number | null> {
    const next = timers.shift();
    if (next === undefined) return null;
    if (!next.cancelled) next.callback();
    await settled();
    return next.ms;
  }

  return {
    github,
    fetched,
    given,
    pushed,
    recorded,
    removed,
    saves,
    timers,
    lines,
    waiting,
    tick,
    settled,
    advance(ms: number): void {
      clock += ms;
    },
    disconnect(): void {
      token = null;
    },
    failGive(err: Error | null): void {
      giveFailure = err;
    },
    /** Holds the next give until the returned function is called. */
    holdGive(): () => void {
      let release: () => void = () => undefined;
      heldGive = new Promise<void>((resolve) => {
        release = resolve;
      });
      return () => {
        heldGive = null;
        release();
      };
    },
  };
}

function state(
  t: ReturnType<typeof setup>,
): Pick<GitHubWaitingApproval, 'state' | 'reason'> | null {
  const status = t.waiting.statusFor(KEY);
  return status === null ? null : { state: status.state, reason: status.reason };
}

describe('an approval that waits for the checks', () => {
  it('gives the stored approval once every check passed, and not before', async () => {
    const t = setup();
    t.github.runs = runs([
      ['build', 'in_progress', null],
      ['lint', 'completed', 'success'],
    ]);
    t.github.statuses = statuses([['ci/deploy', 'pending']]);

    const asked = t.waiting.start(REQUEST);
    await t.tick();
    await t.tick();
    const whileRunning = { given: [...t.given], status: t.waiting.statusFor(KEY) };
    t.github.runs = runs([
      ['build', 'completed', 'success'],
      ['lint', 'completed', 'neutral'],
      ['docs', 'completed', 'skipped'],
    ]);
    t.github.statuses = statuses([['ci/deploy', 'success']]);
    await t.tick();

    expect(asked).toEqual({
      ...KEY,
      state: 'waiting',
      running: 0,
      reason: null,
      detail: null,
      askedAt: 1_000,
    });
    expect(
      whileRunning,
      'Scout gives nothing while a check still runs, and counts the running ones',
    ).toEqual({
      given: [],
      status: { ...KEY, state: 'waiting', running: 2, reason: null, detail: null, askedAt: 1_000 },
    });
    expect(t.given, 'Scout gives the approval by itself once every check passed').toEqual([
      REQUEST,
    ]);
    expect(t.pushed.map((s) => [s.state, s.running, s.reason])).toEqual([
      ['waiting', 0, null],
      ['waiting', 2, null],
      ['ended', 2, 'approved'],
    ]);
    expect(t.removed, 'the waiting entry leaves the bell once the verdict takes over').toEqual([
      ENTRY_ID,
    ]);
    expect(t.timers, 'nothing is read after the approval was given').toEqual([]);
    expect(
      t.fetched.every(
        (f) =>
          f.method === 'GET' &&
          f.url.startsWith('https://api.github.com/') &&
          f.authorization === `Bearer ${TOKEN}`,
      ),
      'main sends only GETs to api.github.com while it waits',
    ).toBe(true);
  });

  it('gives exactly the request the owner approved, also after Scout restarted', async () => {
    const first = setup();
    first.github.runs = runs([['build', 'queued', null]]);
    first.waiting.start(REQUEST);
    await first.tick();
    const kept = first.saves.at(-1) ?? null;
    const restarted = setup(kept);
    await restarted.waiting.resume();
    await restarted.tick();

    expect(
      restarted.given.map((request) => JSON.stringify(request)),
      'the approval sent is the one the owner clicked, byte for byte',
    ).toEqual([JSON.stringify(REQUEST)]);
    expect(kept, 'the saved approval never carries the token').not.toContain(TOKEN);
  });

  it('ends as check-failed and gives nothing', async () => {
    const t = setup();
    t.github.runs = runs([
      ['build', 'completed', 'success'],
      ['lint', 'completed', 'failure'],
      ['e2e', 'in_progress', null],
    ]);

    t.waiting.start(REQUEST);
    await t.tick();

    expect(t.given, 'a failed check cancels the approval').toEqual([]);
    expect(t.waiting.statusFor(KEY), 'the approval ends as check-failed, naming the check').toEqual(
      {
        ...KEY,
        state: 'ended',
        running: 0,
        reason: 'check-failed',
        detail: 'lint',
        askedAt: 1_000,
      },
    );
    expect(t.recorded.at(-1), 'the bell names the check that failed').toEqual({
      id: ENTRY_ID,
      kind: 'failed',
      title: 'Not approved: a check failed on acme/shop#5',
      detail: 'lint did not pass',
      source: 'verdict',
    });
    expect(t.saves.at(-1)).toBe('[]');
  });

  it('names a failed status by its first line and at most 60 characters', async () => {
    const t = setup();
    t.github.statuses = statuses([[`ci/${'x'.repeat(80)}\nsecond line`, 'error']]);

    t.waiting.start(REQUEST);
    await t.tick();

    expect(
      t.waiting.statusFor(KEY)?.detail,
      'the failed check is named on one line of at most 60 characters',
    ).toBe(`ci/${'x'.repeat(57)}`);
    expect(t.given).toEqual([]);
  });

  it('ends as new-commit when the head moved', async () => {
    const t = setup();
    t.github.pull = pull('9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d');

    t.waiting.start(REQUEST);
    await t.tick();

    expect(t.given, 'a new commit cancels the approval').toEqual([]);
    expect(state(t), 'the approval ends as new-commit once the head moved').toEqual({
      state: 'ended',
      reason: 'new-commit',
    });
    expect(
      t.fetched.map((f) => f.url),
      'no check of another commit is read',
    ).toEqual([PULL_URL]);
  });

  it('a short reviewed commit is the head it starts, whatever its case', async () => {
    const t = setup();
    t.github.pull = pull(HEAD.toUpperCase());
    const short = { ...REQUEST, commit: HEAD.slice(0, 7) };

    t.waiting.start(short);
    await t.tick();

    expect(t.given, 'the head that starts with the reviewed commit is the same commit').toEqual([
      short,
    ]);
  });

  it('ends as closed when the pull request was closed or GitHub no longer finds it', async () => {
    const closed = setup();
    closed.github.pull = pull(HEAD, 'closed');
    const gone = setup();
    gone.github.pull = json({ message: 'Not Found' }, 404);

    closed.waiting.start(REQUEST);
    gone.waiting.start(REQUEST);
    await closed.tick();
    await gone.tick();

    expect([state(closed), state(gone)]).toEqual([
      { state: 'ended', reason: 'closed' },
      { state: 'ended', reason: 'closed' },
    ]);
    expect([...closed.given, ...gone.given]).toEqual([]);
  });

  it.each([
    ['GitHub says it cannot be merged', { mergeable: false }, PASSED],
    ['only its mergeable state is dirty', { mergeable: true, mergeable_state: 'dirty' }, PASSED],
    [
      'it conflicts while its checks still run',
      { mergeable: false, mergeable_state: 'dirty' },
      runs([['build', 'in_progress', null]]),
    ],
  ] as const)(
    'a pull request with conflicts with its base branch (%s) ends as conflict at once and gives nothing',
    async (_name, mergeability, checks) => {
      const t = setup();
      t.github.pull = pull(HEAD, 'open', mergeability);
      t.github.runs = checks;

      t.waiting.start(REQUEST);
      await t.tick();

      expect(t.given, 'a conflict cancels the approval').toEqual([]);
      expect(
        t.waiting.statusFor(KEY),
        'the approval ends as conflict at the first read that shows it',
      ).toEqual({
        ...KEY,
        state: 'ended',
        running: 0,
        reason: 'conflict',
        detail: null,
        askedAt: 1_000,
      });
      expect(t.recorded.at(-1), 'the bell says the approval ended for the conflict').toEqual({
        id: ENTRY_ID,
        kind: 'failed',
        title: 'Not approved: acme/shop#5 has conflicts with its base branch',
        detail: 'Scout approves only a pull request that can be merged',
        source: 'verdict',
      });
      expect(
        t.fetched.map((f) => f.url),
        'no check is read once the pull request conflicts',
      ).toEqual([PULL_URL]);
      expect(t.timers, 'nothing is read after the approval ended').toEqual([]);
      expect(t.saves.at(-1)).toBe('[]');
    },
  );

  it.each([
    ['null', { mergeable: null, mergeable_state: 'unknown' }],
    ['absent', {}],
    ['not a boolean', { mergeable: 'true', mergeable_state: 'clean' }],
  ] as const)(
    'keeps waiting while GitHub has not said whether the pull request can be merged (mergeable %s), gives at the next read once it has, and ends after two hours',
    async (_name, unknown) => {
      const t = setup();
      t.github.pull = pull(HEAD, 'open', unknown);
      const late = setup();
      late.github.pull = pull(HEAD, 'open', unknown);

      t.waiting.start(REQUEST);
      late.waiting.start(REQUEST);
      await t.tick();
      await late.tick();
      const unsaid = { given: [...t.given], state: state(t), next: t.timers.map((x) => x.ms) };
      t.github.pull = pull();
      await t.tick();
      late.advance(WAIT_MAX_MS + 1);
      await late.tick();

      expect(
        unsaid,
        'Scout gives nothing while GitHub has not said the pull request can be merged, and reads again',
      ).toEqual({
        given: [],
        state: { state: 'waiting', reason: null },
        next: [WAIT_POLL_MS],
      });
      expect(
        t.given,
        'the approval is given at the next read once GitHub says it can be merged',
      ).toEqual([REQUEST]);
      expect(state(late), 'an answer that never comes ends the wait after two hours').toEqual({
        state: 'ended',
        reason: 'timed-out',
      });
      expect(late.given).toEqual([]);
    },
  );

  it('ends as timed-out after two hours', async () => {
    const t = setup();
    t.github.runs = runs([['build', 'in_progress', null]]);

    t.waiting.start(REQUEST);
    t.advance(WAIT_MAX_MS);
    await t.tick();
    const atTwoHours = state(t);
    t.advance(1);
    await t.tick();

    expect(atTwoHours).toEqual({ state: 'waiting', reason: null });
    expect(state(t), 'the approval ends once it waited more than two hours').toEqual({
      state: 'ended',
      reason: 'timed-out',
    });
    expect(t.given).toEqual([]);
    expect(t.timers).toEqual([]);
  });

  it('cancel ends it and gives nothing', async () => {
    const t = setup();
    t.waiting.start(REQUEST);

    t.waiting.cancel(KEY);
    t.waiting.cancel({ ...KEY, number: 6 });
    await t.tick();

    expect(t.given, 'a cancelled approval is never given').toEqual([]);
    expect(t.fetched).toEqual([]);
    expect(t.pushed.at(-1)).toEqual({
      ...KEY,
      state: 'ended',
      running: 0,
      reason: 'cancelled',
      detail: null,
      askedAt: 1_000,
    });
    expect(t.removed).toEqual([ENTRY_ID]);
    expect(t.saves.at(-1)).toBe('[]');
  });

  it('resume keeps the waiting approvals and their time', async () => {
    const other = { ...REQUEST, number: 6 };
    const saved = JSON.stringify([
      { request: REQUEST, askedAt: 1_000 },
      { request: { ...REQUEST, number: 7, commit: 'main' }, askedAt: 1_000 },
      { request: { ...REQUEST, number: 8, verdict: 'request_changes' }, askedAt: 1_000 },
      { request: other, askedAt: 'yesterday' },
      'not an approval',
    ]);
    const t = setup(saved);
    t.github.runs = runs([['build', 'in_progress', null]]);

    t.advance(WAIT_MAX_MS);
    await t.waiting.resume();
    const resumed = t.waiting.statusFor(KEY);
    const dropped = [7, 8, 6].map((number) => t.waiting.statusFor({ ...KEY, number }));
    t.advance(1);
    await t.tick();

    expect(resumed, 'a restart keeps the waiting approval and when the owner asked').toEqual({
      ...KEY,
      state: 'waiting',
      running: 0,
      reason: null,
      detail: null,
      askedAt: 1_000,
    });
    expect(t.recorded[0]?.id, 'the bell shows the approval waiting again').toBe(ENTRY_ID);
    expect(dropped).toEqual([null, null, null]);
    expect(state(t), 'the two hours count from the click before the restart').toEqual({
      state: 'ended',
      reason: 'timed-out',
    });
  });

  const failures: readonly [string, Reply | Error, number][] = [
    ['a rejected token (401)', json({ message: 'Bad credentials' }, 401), WAIT_POLL_MS],
    [
      'a rate limit (403) with Retry-After',
      json({ message: 'rate limit' }, 403, { 'Retry-After': '300' }),
      300_000,
    ],
    [
      'a rate limit (429) with a short Retry-After',
      json({}, 429, { 'Retry-After': '5' }),
      WAIT_POLL_MS,
    ],
    ['a server error (502)', json({ message: 'Bad Gateway' }, 502), WAIT_POLL_MS],
    ['a timeout', new DOMException('The operation timed out.', 'TimeoutError'), WAIT_POLL_MS],
    [
      'a reset connection',
      Object.assign(new Error('socket hang up'), { code: 'ECONNRESET' }),
      WAIT_POLL_MS,
    ],
    ['an unreadable body', () => new Response('<html>', { status: 200 }), WAIT_POLL_MS],
  ];
  for (const read of ['pull', 'runs', 'statuses'] as const) {
    for (const [name, reply, wait] of failures) {
      it(`the ${read} read failing with ${name} keeps waiting, sends nothing and reads again after ${wait / 1000} s`, async () => {
        const t = setup();
        t.github[read] = reply;

        t.waiting.start(REQUEST);
        await t.tick();

        expect(t.given, `${name} never gives the approval`).toEqual([]);
        expect(state(t), `${name} leaves the approval waiting`).toEqual({
          state: 'waiting',
          reason: null,
        });
        expect(
          t.timers.map((timer) => timer.ms),
          `${name} reads again after ${wait} ms`,
        ).toEqual([wait]);
        expect(t.lines.join('\n')).not.toContain(TOKEN);
      });
    }
  }

  it('a Cancel that arrives while the approval is being given does not say it was cancelled: it ends as approved', async () => {
    const t = setup();
    t.waiting.start(REQUEST);
    const release = t.holdGive();
    const reading = t.tick();
    await t.settled();

    t.waiting.cancel(KEY);
    const whileGiving = state(t);
    release();
    await reading;
    await t.settled();

    expect(whileGiving, 'the approval being given is still waiting, not cancelled').toEqual({
      state: 'waiting',
      reason: null,
    });
    expect(t.given).toEqual([REQUEST]);
    expect(state(t)).toEqual({ state: 'ended', reason: 'approved' });
  });

  it('a change of the GitHub connection ends every waiting approval and gives none', async () => {
    const t = setup();
    t.waiting.start(REQUEST);
    t.waiting.start({ ...REQUEST, number: 6 });

    t.waiting.cancelAll();
    await t.tick();

    expect(t.given, 'nothing is given under another connection').toEqual([]);
    expect(state(t)).toEqual({ state: 'ended', reason: 'cancelled' });
    expect(JSON.parse(t.saves.at(-1) ?? 'null'), 'nothing waits in the vault').toEqual([]);
  });

  it('a saved time in the future counts from now, so the two hours still end it', async () => {
    const t = setup(JSON.stringify([{ request: REQUEST, askedAt: 9_999_999_999_999 }]));
    await t.waiting.resume();

    t.advance(WAIT_MAX_MS + 1);
    await t.tick();

    expect(state(t)).toEqual({ state: 'ended', reason: 'timed-out' });
    expect(t.given).toEqual([]);
  });

  it.each([
    ['cannot be read', { loadFails: new Error('keychain locked') }, null],
    ['is not JSON', {}, '<html>'],
  ] as const)(
    'a vault whose waiting approvals %s resumes none, reads nothing and says so once without the token',
    async (_name, vault, saved) => {
      const t = setup(saved, vault);

      await t.waiting.resume();

      expect(t.waiting.statusFor(KEY)).toBeNull();
      expect(t.timers).toEqual([]);
      expect(t.fetched).toEqual([]);
      expect(t.lines.filter((line) => line.includes('could not be read')).length).toBe(1);
      expect(t.lines.join('\n')).not.toContain(TOKEN);
    },
  );

  it('a vault that refuses the save keeps the approval waiting and still gives it once the checks pass', async () => {
    const t = setup(null, { saveFails: new Error('disk full') });

    t.waiting.start(REQUEST);
    await t.tick();

    expect(t.given).toEqual([REQUEST]);
    expect(t.lines.some((line) => line.includes('were not saved'))).toBe(true);
    expect(t.lines.join('\n')).not.toContain(TOKEN);
  });

  it('without a GitHub connection it keeps waiting and sends nothing', async () => {
    const t = setup();
    t.disconnect();

    t.waiting.start(REQUEST);
    await t.tick();

    expect(t.fetched, 'nothing is sent without a token').toEqual([]);
    expect(state(t)).toEqual({ state: 'waiting', reason: null });
  });

  it('stays waiting while checks are unread', async () => {
    const manyRuns = setup();
    manyRuns.github.runs = runs(
      Array.from({ length: 100 }, (_, i): Check => [`job-${i}`, 'completed', 'success']),
      101,
    );
    const fullPage = setup();
    fullPage.github.statuses = statuses(
      Array.from({ length: 100 }, (_, i) => [`ci/${i}`, 'success'] as const),
    );
    const almostFull = setup();
    almostFull.github.statuses = statuses(
      Array.from({ length: 99 }, (_, i) => [`ci/${i}`, 'success'] as const),
    );

    for (const t of [manyRuns, fullPage, almostFull]) {
      t.waiting.start(REQUEST);
      await t.tick();
    }

    expect(
      [manyRuns.given, fullPage.given],
      'a commit with more runs than read, or a full page of 100 statuses, is never approved',
    ).toEqual([[], []]);
    expect([state(manyRuns), state(fullPage)]).toEqual([
      { state: 'waiting', reason: null },
      { state: 'waiting', reason: null },
    ]);
    expect(almostFull.given, '99 statuses are every status of the commit').toEqual([REQUEST]);
  });

  it.each([
    ['check runs without their list', { runs: json({ total_count: 0 }) }],
    ['check runs without their count', { runs: json({ check_runs: [] }) }],
    ['check runs whose list is not a list', { runs: json({ total_count: 0, check_runs: 'none' }) }],
    [
      'a check run that is not an object',
      { runs: json({ total_count: 1, check_runs: ['build'] }) },
    ],
    [
      'a check run without its status',
      { runs: json({ total_count: 1, check_runs: [{ name: 'build' }] }) },
    ],
    ['statuses without their list', { statuses: json({ state: 'success' }) }],
    ['a status without its state', { statuses: json({ statuses: [{ context: 'ci/status' }] }) }],
    ['an empty object for both', { runs: json({}), statuses: json({}) }],
    ['a pull request without its state', { pull: json({ head: { sha: HEAD } }) }],
  ] as const)(
    'an answer GitHub gives with 200 but not in its shape (%s) is unread: it keeps waiting and never approves',
    async (_name, replies) => {
      const t = setup();
      Object.assign(t.github, replies);

      t.waiting.start(REQUEST);
      await t.tick();

      expect(t.given, 'what Scout could not read never passes').toEqual([]);
      expect(state(t)).toEqual({ state: 'waiting', reason: null });
    },
  );

  it('refuses the twenty-first', () => {
    const t = setup();
    for (let number = 1; number <= MAX_WAITING; number += 1) {
      t.waiting.start({ ...REQUEST, number });
    }
    const pushedBefore = t.pushed.length;

    const again = t.waiting.start({ ...REQUEST, number: 1 });
    let refused: unknown = null;
    try {
      t.waiting.start({ ...REQUEST, number: MAX_WAITING + 1 });
    } catch (err: unknown) {
      refused = err;
    }

    expect(refused, 'at most 20 approvals wait at once').toBeInstanceOf(ScoutError);
    expect((refused as ScoutError).code).toBe('github/waiting-approvals-full');
    expect(again.state, 'a second click on a waiting approval answers it').toBe('waiting');
    expect(t.pushed.length, 'a second click sends nothing').toBe(pushedBefore);
    expect(t.waiting.statusFor({ ...KEY, number: MAX_WAITING + 1 })).toBeNull();
  });

  it('a give that fails keeps waiting, tries again at the next read and logs its code once', async () => {
    const t = setup();
    t.failGive(new ScoutError('control-plane/unavailable', 'The control plane is down.'));

    t.waiting.start(REQUEST);
    await t.tick();
    await t.tick();
    const whileFailing = state(t);
    t.failGive(null);
    await t.tick();

    expect(whileFailing).toEqual({ state: 'waiting', reason: null });
    expect(t.given, 'the approval is given once the verdict can be sent again').toEqual([REQUEST]);
    expect(t.lines.filter((line) => line.includes('control-plane/unavailable'))).toHaveLength(1);
  });

  it('dispose cancels the next read', async () => {
    const t = setup();
    t.waiting.start(REQUEST);

    t.waiting.dispose();
    await t.tick();

    expect(t.fetched).toEqual([]);
  });
});

describe('main.ts wiring of the waiting approvals', () => {
  it('gives through the verdict handlers as an approval after the checks, keeps them in the vault, resumes them and stops them on quit', async () => {
    const main = await readFile(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');

    expect(main).toContain('give: (request) => verdicts.give(request, true),');
    expect(main).toContain("load: () => vault.get('github.waiting-approvals'),");
    expect(main).toContain("save: (json) => vault.store('github.waiting-approvals', json),");
    expect(main).toContain('token: () => auth.accessToken(),');
    expect(main).toContain('void waiting.resume();');
    expect(main).toContain('waitingApprovals?.dispose();');
    expect(main, 'a change of the GitHub connection ends the waiting approvals').toMatch(
      /onConnectionChanged: \(\) => \{\s*waitingApprovals\?\.cancelAll\(\);/,
    );
    expect(main, "the bell's cancel is refused unless it comes from a verdict window").toContain(
      'refuseUnlessVerdictWindow(',
    );
  });
});
