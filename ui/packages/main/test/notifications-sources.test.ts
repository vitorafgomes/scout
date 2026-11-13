import { describe, expect, it } from 'bun:test';
import type { GitHubVerdictStatus } from '@scout/shared';
import type { NotificationInput } from '../src/notifications/notification-store.js';
import { verdictNotification, workerNotice } from '../src/notifications/sources.js';

function line(text: string): string {
  return JSON.stringify({ id: null, event: 'notice', text });
}

const nextId = (): string => 'notice:1';

const BATCH = [
  'Audit of acme/shop, src/orders (3 of 8):',
  '',
  '```scout-findings',
  '{"repository":"acme/shop","link":"https://github.com/acme/shop/blob/x"}',
  '```',
].join('\n');
const PAUSED =
  "The audit of acme/shop paused: the AI provider's usage limit was reached. Ask Scout to continue the audit of acme/shop when the AI Source has capacity again.";
const DONE = [
  'Audit of acme/shop at 3f2a9c1 is done. Verdict: fix (0 High, 2 Medium, 1 Low).',
  'Scope: 8 of 8 areas covered; 120 of 120 files read.',
  'Every area was covered.',
].join('\n');

describe('worker notices', () => {
  it('an audit batch is the audit entry in progress, with the share of areas done and no link', () => {
    expect(workerNotice(line(BATCH), nextId)).toEqual({
      id: 'audit:acme/shop',
      kind: 'running',
      title: 'Audit of acme/shop',
      detail: 'Area 3 of 8 done: src/orders',
      progress: 37,
      source: 'worker',
    });
  });

  const shares: readonly [number, number, number][] = [
    [3, 8, 37],
    [1, 3, 33],
    [2, 3, 66],
    [5, 9, 55],
    [7, 8, 87],
    [8, 8, 100],
    [9, 8, 100],
  ];
  for (const [done, total, progress] of shares) {
    it(`the share of ${done} of ${total} areas done is ${progress}, rounded down as the artboard draws it`, () => {
      expect(
        workerNotice(line(`Audit of acme/shop, src (${done} of ${total}):`), nextId)?.progress,
      ).toBe(progress);
    });
  }

  it('the pause and the end update the same audit entry as paused and done', () => {
    expect(workerNotice(line(PAUSED), nextId)).toEqual({
      id: 'audit:acme/shop',
      kind: 'paused',
      title: 'Audit of acme/shop paused',
      detail:
        'The AI Source reached its usage limit. Ask Scout to continue when it has capacity again.',
      source: 'worker',
    });
    expect(workerNotice(line(DONE), nextId)).toEqual({
      id: 'audit:acme/shop',
      kind: 'done',
      title: 'Audit of acme/shop is done',
      detail: 'Verdict: fix (0 High, 2 Medium, 1 Low).',
      source: 'worker',
    });
  });

  const jobNotices: readonly [string, string, unknown][] = [
    [
      "the owner's own pause is the audit entry paused by you",
      'The audit of acme/shop paused: you paused it. Ask Scout to continue the audit of acme/shop to resume it from the first area not done.',
      {
        id: 'audit:acme/shop',
        kind: 'paused',
        title: 'Audit of acme/shop paused',
        detail: 'Paused by you',
        source: 'worker',
      },
    ],
    [
      "the owner's stop ends the audit's own entry as stopped, with the areas done and the findings kept",
      'The audit of acme/shop ended at your request: 3 of 8 areas done, 4 findings kept on the cards.',
      {
        id: 'audit:acme/shop',
        kind: 'done',
        title: 'Audit of acme/shop stopped',
        detail: '3 of 8 areas · 4 findings kept',
        source: 'worker',
      },
    ],
    [
      "a stop whose counts Scout does not read keeps the worker's words",
      'The audit of acme/shop ended at your request: early.',
      {
        id: 'audit:acme/shop',
        kind: 'done',
        title: 'Audit of acme/shop stopped',
        detail: 'early.',
        source: 'worker',
      },
    ],
    [
      "the owner's stop of a test run ends that pull request's entry, its key without case",
      'The tests of Acme/API#41 ended at your request; no result was kept.',
      {
        id: 'tests:acme/api#41',
        kind: 'done',
        title: 'Tests for Acme/API#41 stopped',
        detail: 'The tests gave no result.',
        source: 'worker',
      },
    ],
    [
      'a notice that only looks like a stop is its own entry',
      'The tests of the release ended at your request.',
      {
        id: 'notice:1',
        kind: 'done',
        title: 'The tests of the release ended at your request.',
        detail: '',
        source: 'worker',
      },
    ],
  ];
  for (const [name, text, entry] of jobNotices) {
    it(name, () => {
      expect(workerNotice(line(text), nextId)).toEqual(entry as NotificationInput);
    });
  }

  const ASK = 'Ask Scout to continue the audit of acme/shop when the AI Source has capacity again.';
  const pausedByTheAiSource: readonly [string, string, string][] = [
    [
      'the usage limit',
      "the AI provider's usage limit was reached",
      'The AI Source reached its usage limit. Ask Scout to continue when it has capacity again.',
    ],
    [
      'an account out of credit',
      "the AI provider's account has run out of credit",
      "The AI Source's account has run out of credit. Ask Scout to continue once it has credit again.",
    ],
    [
      'the rate limit',
      "the AI provider's rate limit was reached",
      'The AI Source reached its rate limit. Ask Scout to continue when it has capacity again.',
    ],
    [
      'a sign-in the AI Source needs again',
      'the AI provider needs the owner to sign in again',
      'The AI Source needs you to sign in again. Sign in from AI Sources, then ask Scout to continue.',
    ],
    [
      'a key the AI Source rejected',
      'the AI Source rejected its key',
      'The AI Source rejected its key. Give it a working key in AI Sources, then ask Scout to continue.',
    ],
    [
      'a model the AI Source does not have, named',
      'the AI Source does not have the model llama3.2:3b',
      'The AI Source does not have the model llama3.2:3b. Choose a model it has in Models, then ask Scout to continue.',
    ],
    [
      'a reason Scout has no sentence for, the worker’s own words with a capital first letter',
      'the AI Source is resting',
      'The AI Source is resting. Ask Scout to continue the audit of acme/shop when the AI Source has capacity again.',
    ],
  ];
  for (const [name, reason, detail] of pausedByTheAiSource) {
    it(`an audit the AI Source paused for ${name} says so in the artboard's words`, () => {
      expect(
        workerNotice(line(`The audit of acme/shop paused: ${reason}. ${ASK}`), nextId),
      ).toEqual({
        id: 'audit:acme/shop',
        kind: 'paused',
        title: 'Audit of acme/shop paused',
        detail,
        source: 'worker',
      });
    });
  }

  it('any other notice is its own entry, its kind read from the text', () => {
    const kinds = [
      'Researching rust async...',
      'The video project on rust async stopped: the AI Source failed',
      'Something paused for a while',
      'Assembled rust async. Review it in the operator window.',
    ].map((text) => workerNotice(line(text), nextId)?.kind);

    expect(kinds).toEqual(['running', 'failed', 'paused', 'done']);
  });

  it('a notice carries a link only when its text has one', () => {
    const uploaded = workerNotice(
      line('Uploaded rust async as private: https://youtu.be/abc123.'),
      nextId,
    );
    const plain = workerNotice(line('Assembled rust async.'), nextId);

    expect(uploaded).toMatchObject({
      id: 'notice:1',
      title: 'Uploaded rust async as private: https://youtu.be/abc123.',
      url: 'https://youtu.be/abc123',
    });
    expect(plain).not.toHaveProperty('url');
  });

  it('a reply event, a notice with an id, an empty notice and a broken line are not notices', () => {
    expect(
      [
        JSON.stringify({ id: 'q1', event: 'complete', text: 'Hi' }),
        JSON.stringify({ id: 'q1', event: 'notice', text: 'Hi' }),
        line('   '),
        '{broken',
      ].map((json) => workerNotice(json, nextId)),
    ).toEqual([null, null, null, null]);
  });
});

function verdict(
  state: GitHubVerdictStatus['state'],
  url: string | null = null,
  reason: string | null = null,
  kind: GitHubVerdictStatus['verdict'] = 'approve',
): GitHubVerdictStatus {
  return {
    actionId: 'act-1',
    state,
    url,
    reason,
    repository: 'acme/shop',
    number: 5,
    commit: '3f2a9c1',
    verdict: kind,
  };
}

describe('verdict follow', () => {
  it('queued and running are one running entry, given is done with the verdict in its title and the review link, failed carries the reason', () => {
    const review = 'https://github.com/acme/shop/pull/5#pullrequestreview-1';

    expect(
      [
        verdict('queued'),
        verdict('running'),
        verdict('given', review),
        verdict('failed', null, 'GitHub refused the review.'),
      ].map(verdictNotification),
    ).toEqual([
      {
        id: 'verdict:act-1',
        kind: 'running',
        title: 'Verdict on acme/shop#5',
        detail: 'Approve: queued',
        source: 'verdict',
      },
      {
        id: 'verdict:act-1',
        kind: 'running',
        title: 'Verdict on acme/shop#5',
        detail: 'Approve: running',
        source: 'verdict',
      },
      {
        id: 'verdict:act-1',
        kind: 'done',
        title: 'Verdict given: Approved acme/shop#5',
        detail: '',
        url: review,
        source: 'verdict',
      },
      {
        id: 'verdict:act-1',
        kind: 'failed',
        title: 'Verdict not given on acme/shop#5',
        detail: 'GitHub refused the review.',
        source: 'verdict',
      },
    ]);
  });

  const verdicts: readonly [GitHubVerdictStatus['verdict'], string, string][] = [
    ['approve', 'Approve: queued', 'Verdict given: Approved acme/shop#5'],
    ['request_changes', 'Request changes: queued', 'Verdict given: Changes requested acme/shop#5'],
    ['comment', 'Comment: queued', 'Verdict given: Commented acme/shop#5'],
  ];
  for (const [kind, queued, given] of verdicts) {
    it(`a ${kind} verdict reads "${queued}" while queued and "${given}" once given`, () => {
      expect(
        [verdict('queued', null, null, kind), verdict('given', null, null, kind)]
          .map(verdictNotification)
          .map((n) => (n.kind === 'running' ? n.detail : n.title)),
      ).toEqual([queued, given]);
    });
  }
});
