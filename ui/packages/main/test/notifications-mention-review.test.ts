import { describe, expect, it } from 'bun:test';
import {
  MENTION_MAX_AGE_MS,
  MENTION_RETRY_MS,
  MentionReviews,
  STARTED_DETAIL,
} from '../src/notifications/mention-review.js';
import type { NotificationInput } from '../src/notifications/notification-store.js';

const NOW = Date.parse('2026-11-15T12:00:00Z');
const QUESTION = 'Review the pull request https://github.com/acme/api/pull/44';
const ID = 'mention:issue-comment:7';
const REVIEW_ID = `review:${ID}`;

function mention(over: Partial<NotificationInput> = {}): NotificationInput {
  return {
    id: ID,
    kind: 'mention',
    title: '@owner mentioned @scout-acme on acme/api#44: @scout-acme please review',
    detail: 'Retry logic for the order sync',
    at: NOW - 60_000,
    url: 'https://github.com/acme/api/pull/44#issuecomment-7',
    actions: [{ id: 'review-pull-request', label: 'Review it' }],
    source: 'github',
    ...over,
  };
}

function harness(
  over: { login?: string | null | Error; answers?: boolean[]; askedByOwner?: boolean } = {},
) {
  const asked: string[] = [];
  const kept = new Map<string, NotificationInput>();
  const timers: { ms: number; run: () => void; cancelled: boolean }[] = [];
  const answers = over.answers ?? [];
  let now = NOW;
  const reviews = new MentionReviews({
    login: () =>
      over.login instanceof Error
        ? Promise.reject(over.login)
        : Promise.resolve(over.login === undefined ? 'owner' : over.login),
    ask: (question) => {
      if (!(answers.shift() ?? true)) return null;
      asked.push(question);
      return `turn-${asked.length}`;
    },
    asked: () => over.askedByOwner === true,
    kept: (id) => kept.get(id),
    record: (entry) => kept.set(entry.id, entry),
    now: () => now,
    setTimer: (ms, run) => {
      const timer = { ms, run, cancelled: false };
      timers.push(timer);
      return () => {
        timer.cancelled = true;
      };
    },
    logger: { info: () => undefined, warn: () => undefined } as never,
  });
  return {
    reviews,
    asked,
    kept,
    timers,
    advance: (ms: number) => {
      now += ms;
    },
    /** Runs the timers `idle` set for the end of the caller's stack. */
    settle: () => {
      for (const timer of timers.filter((t) => t.ms === 0 && !t.cancelled)) {
        timer.cancelled = true;
        timer.run();
      }
    },
  };
}

describe('a mention of the Scout bot starts the review by itself', () => {
  it("asks the chat to review the pull request of the owner's fresh mention, its entry says it started and the review shows in progress", async () => {
    const h = harness();

    await h.reviews.record(mention());

    expect(h.asked).toEqual([QUESTION]);
    const { actions: _dropped, ...started } = mention();
    expect(h.kept.get(ID)).toEqual({ ...started, detail: STARTED_DETAIL });
    expect(h.kept.get(REVIEW_ID)).toEqual({
      id: REVIEW_ID,
      kind: 'running',
      title: 'Reviewing acme/api#44',
      detail: 'Started by your mention of the Scout bot',
      indeterminate: true,
      url: 'https://github.com/acme/api/pull/44#issuecomment-7',
      source: 'github',
    });
  });

  it('asks once for a mention, however often GitHub returns it, and after a restart', async () => {
    const h = harness();
    await h.reviews.record(mention());
    await h.reviews.record(
      mention({ title: '@owner mentioned @scout-acme on acme/api#44: edited' }),
    );

    const restarted = harness();
    restarted.kept.set(ID, h.kept.get(ID)!);
    await restarted.reviews.record(mention());

    expect(h.asked).toEqual([QUESTION]);
    expect(h.kept.get(ID)!.detail).toBe(STARTED_DETAIL);
    expect(restarted.asked).toEqual([]);
    expect(restarted.kept.get(ID)!.detail).toBe(STARTED_DETAIL);
  });

  it('keeps Review it and asks nothing for a mention by someone else', async () => {
    const h = harness();
    const theirs = mention({ title: '@acme-dev mentioned @scout-acme on acme/api#44: review' });

    await h.reviews.record(theirs);

    expect(h.asked).toEqual([]);
    expect(h.kept.get(theirs.id)).toEqual(theirs);
  });

  it('keeps Review it for a mention older than an hour, one with no pull request link, and with GitHub disconnected', async () => {
    const old = harness();
    await old.reviews.record(mention({ at: NOW - MENTION_MAX_AGE_MS - 1 }));
    const noLink = harness();
    await noLink.reviews.record(mention({ url: 'https://github.com/acme/api/issues/9' }));
    const disconnected = harness({ login: null });
    await disconnected.reviews.record(mention());

    expect([old.asked, noLink.asked, disconnected.asked]).toEqual([[], [], []]);
    expect(old.kept.get(ID)!.actions).toEqual([{ id: 'review-pull-request', label: 'Review it' }]);
  });

  it('waits while the chat answers, then asks when it goes idle, one review at a time', async () => {
    const h = harness({ answers: [false, false] });
    await h.reviews.record(mention());
    await h.reviews.record(
      mention({ id: 'mention:body:9', url: 'https://github.com/acme/api/pull/45' }),
    );
    expect(h.asked).toEqual([]);
    expect(h.kept.get(ID)!.detail).toBe('Retry logic for the order sync');

    h.reviews.idle();
    h.settle();
    expect(h.asked).toEqual([QUESTION]);

    h.reviews.idle();
    h.reviews.turnDone('turn-1');
    h.settle();
    expect(h.asked).toEqual([
      QUESTION,
      'Review the pull request https://github.com/acme/api/pull/45',
    ]);
    expect(h.kept.get(REVIEW_ID)!.kind).toBe('done');
    expect(h.kept.get('review:mention:body:9')!.kind).toBe('running');
    expect(h.kept.get('mention:body:9')!.detail).toBe(STARTED_DETAIL);
  });

  it('a question the chat refuses while ending a turn of its own is asked again only on the retry, never in a loop', async () => {
    const h = harness();
    let refusals = 0;
    const refusing = new MentionReviews({
      login: () => Promise.resolve('owner'),
      ask: () => {
        refusals += 1;
        refusing.idle();
        return null;
      },
      asked: () => false,
      kept: (id) => h.kept.get(id),
      record: (entry) => h.kept.set(entry.id, entry),
      now: () => NOW,
      setTimer: (ms, run) => {
        h.timers.push({ ms, run, cancelled: false });
        return () => undefined;
      },
      logger: { info: () => undefined, warn: () => undefined } as never,
    });

    await refusing.record(mention());
    h.settle();

    expect(refusals).toBe(1);
    expect(h.timers.map((t) => t.ms)).toEqual([MENTION_RETRY_MS]);
  });

  it('shows the review ready when its turn completes', async () => {
    const h = harness();
    await h.reviews.record(mention());

    h.reviews.idle();
    h.reviews.turnDone('turn-1');
    h.settle();

    expect(h.kept.get(REVIEW_ID)).toEqual({
      id: REVIEW_ID,
      kind: 'done',
      title: 'Review of acme/api#44 is ready',
      detail: 'The answer is in the chat.',
      url: 'https://github.com/acme/api/pull/44#issuecomment-7',
      source: 'github',
    });
    expect(h.kept.get(ID)!.detail).toBe(STARTED_DETAIL);
  });

  it('shows a review whose turn stopped or failed as not finished, and gives Review it back', async () => {
    const h = harness();
    await h.reviews.record(mention());

    h.reviews.idle();
    h.settle();

    expect(h.kept.get(REVIEW_ID)).toEqual({
      id: REVIEW_ID,
      kind: 'failed',
      title: 'Review of acme/api#44 did not finish',
      detail: 'It stopped before the answer. Review it asks again.',
      url: 'https://github.com/acme/api/pull/44#issuecomment-7',
      source: 'github',
    });
    expect(h.kept.get(ID)).toEqual(mention());
    expect(h.asked).toEqual([QUESTION]);
  });

  it('leaves the entries alone when a turn that is not a review ends', async () => {
    const h = harness();
    await h.reviews.record(mention());

    h.reviews.turnDone('someone-elses-turn');

    expect(h.asked).toEqual([QUESTION]);
    expect(h.kept.get(REVIEW_ID)!.kind).toBe('running');
  });

  it('asks again a minute after a refusal, and gives up once the mention is an hour old', async () => {
    const h = harness({ answers: [false, false] });
    await h.reviews.record(mention());
    const retry = h.timers.at(-1)!;
    expect(retry.ms).toBe(MENTION_RETRY_MS);

    retry.run();
    expect(h.asked).toEqual([]);

    h.advance(MENTION_MAX_AGE_MS);
    h.timers.at(-1)!.run();
    expect(h.asked).toEqual([]);
    expect(h.timers.filter((t) => t.ms === MENTION_RETRY_MS).length).toBe(2);
    expect(h.kept.get(ID)!.actions).toEqual([{ id: 'review-pull-request', label: 'Review it' }]);
  });

  it('asks nothing for a waiting mention once GitHub is disconnected', async () => {
    const h = harness({ answers: [false] });
    await h.reviews.record(mention());

    h.reviews.clear();
    h.reviews.pump();
    h.timers.forEach((timer) => {
      if (!timer.cancelled) timer.run();
    });

    expect(h.asked).toEqual([]);
    expect(h.kept.get(ID)).toEqual(mention());
  });

  it('keeps Review it and asks nothing when the GitHub login cannot be read', async () => {
    const h = harness({ login: new Error('keychain locked') });

    await h.reviews.record(mention());

    expect(h.asked).toEqual([]);
    expect(h.kept.get(ID)).toEqual(mention());
  });

  it('asks nothing for a mention the owner already asked about with Review it while it waited', async () => {
    const h = harness({ askedByOwner: true });

    await h.reviews.record(mention());

    expect(h.asked).toEqual([]);
    expect(h.kept.get(ID)!.detail).toBe(STARTED_DETAIL);
    expect(h.kept.has(REVIEW_ID)).toBe(false);
  });

  it('asks nothing for a mention that lost Review it, its pull request merged, closed or approved by the bot', async () => {
    const settled = harness();
    const { actions: _dropped, ...merged } = mention({ detail: 'Merged. Nothing to review.' });
    await settled.reviews.record(merged);
    const waiting = harness({ answers: [false] });
    await waiting.reviews.record(mention());
    waiting.kept.set(ID, merged);
    waiting.reviews.pump();

    expect([settled.asked, waiting.asked]).toEqual([[], []]);
    expect(settled.kept.get(ID)).toEqual(merged);
  });

  it('records every other entry as it came', async () => {
    const h = harness();
    const other: NotificationInput = {
      id: 'github:1',
      kind: 'github',
      title: 'Retry logic',
      detail: 'You were mentioned in acme/api#41',
      source: 'github',
    };

    await h.reviews.record(other);

    expect(h.kept.get('github:1')).toEqual(other);
    expect(h.asked).toEqual([]);
  });
});
