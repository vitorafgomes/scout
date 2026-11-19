import { describe, expect, it } from 'bun:test';
import type {
  GitHubVerdictStatus,
  GitHubWaitingApproval,
  LocalTurn,
  NotificationAction,
} from '@scout/shared';
import type { NotificationInput } from '../src/notifications/notification-store.js';
import {
  isBellOnlyReviewLine,
  issuesNotification,
  verdictNotification,
  waitingApprovalNotification,
  workerNotice,
} from '../src/notifications/sources.js';

function line(text: string): string {
  return JSON.stringify({ id: null, event: 'notice', text });
}

const nextId = (): string => 'notice:1';

const BATCH = [
  'Audit of acme/shop, src/orders (3 of 8, 4 findings so far):',
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
  const RUNNING_3_OF_8: NotificationInput = {
    id: 'audit:acme/shop',
    kind: 'running',
    title: 'Audit of acme/shop',
    detail: 'Area 3 of 8 done · 4 findings so far · cards arrive in the chat as each area ends',
    progress: 37,
    source: 'worker',
  };

  it('an audit batch is the audit entry in progress, with the areas done, the findings so far, the share of areas done and no link', () => {
    expect(workerNotice(line(BATCH), nextId)).toEqual(RUNNING_3_OF_8);
  });

  it('an area that ended with no new findings moves the running entry and its bar like a batch with findings', () => {
    expect(
      workerNotice(
        line('Audit of acme/shop, src/billing (4 of 8, 1 finding so far): no new findings.'),
        nextId,
      ),
    ).toEqual({
      id: 'audit:acme/shop',
      kind: 'running',
      title: 'Audit of acme/shop',
      detail: 'Area 4 of 8 done · 1 finding so far · cards arrive in the chat as each area ends',
      progress: 50,
      source: 'worker',
    });
  });

  it('the resume line turns the audit entry running at once, with the areas done and the findings so far', () => {
    expect(
      workerNotice(
        line('Audit of acme/shop resumed: area 3 of 8 done, 4 findings so far.'),
        nextId,
      ),
    ).toEqual(RUNNING_3_OF_8);
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
        workerNotice(
          line(`Audit of acme/shop, src (${done} of ${total}, 0 findings so far):`),
          nextId,
        )?.progress,
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
    signedAs: null,
    testsLine: null,
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

  const signed: readonly [
    GitHubVerdictStatus['signedAs'],
    GitHubVerdictStatus['testsLine'],
    string,
  ][] = [
    ['scout[bot]', 'posted', 'Signed as Bot (scout[bot]) · the tests line was posted as a comment'],
    ['octocat', 'notPosted', 'Signed as Owner (octocat) · the tests line was not posted'],
    ['scout[bot]', 'none', 'Signed as Bot (scout[bot])'],
    [null, 'posted', 'The tests line was posted as a comment'],
    [null, 'notPosted', 'The tests line was not posted'],
    [null, 'none', ''],
  ];
  for (const [signedAs, testsLine, detail] of signed) {
    it(`a given verdict signed as ${signedAs ?? 'nobody known'} with the tests line ${testsLine ?? 'unknown'} reads "${detail}"`, () => {
      const review = 'https://github.com/acme/shop/pull/5#pullrequestreview-1';

      expect(
        verdictNotification({ ...verdict('given', review), signedAs, testsLine }),
        `the given entry's detail says who signed the verdict and what became of the tests line: "${detail}"`,
      ).toEqual({
        id: 'verdict:act-1',
        kind: 'done',
        title: 'Verdict given: Approved acme/shop#5',
        detail,
        url: review,
        source: 'verdict',
      });
    });
  }
});

describe('an approval that waits for the checks', () => {
  const ID = 'approval:acme/shop#5@3f2a9c1';
  const CANCEL: readonly NotificationAction[] = [
    { id: 'cancel-waiting-approval', label: 'Cancel' },
  ];
  function waiting(
    state: GitHubWaitingApproval['state'],
    reason: GitHubWaitingApproval['reason'] = null,
    running = 0,
    detail: string | null = null,
  ): GitHubWaitingApproval {
    return {
      repository: 'acme/shop',
      number: 5,
      commit: '3f2a9c1',
      state,
      running,
      reason,
      detail,
      askedAt: 1_000,
    };
  }

  const rows: readonly [string, GitHubWaitingApproval, NotificationInput | null][] = [
    [
      'waiting with two checks running is a running entry with a sweeping bar, the count and Cancel',
      waiting('waiting', null, 2),
      {
        id: ID,
        kind: 'running',
        title: 'Waiting to approve acme/shop#5',
        detail: '2 checks still running',
        indeterminate: true,
        actions: CANCEL,
        source: 'verdict',
      },
    ],
    [
      'waiting with one check running counts it in the singular',
      waiting('waiting', null, 1),
      {
        id: ID,
        kind: 'running',
        title: 'Waiting to approve acme/shop#5',
        detail: '1 check still running',
        indeterminate: true,
        actions: CANCEL,
        source: 'verdict',
      },
    ],
    [
      'waiting before any check was read says it waits for the checks',
      waiting('waiting'),
      {
        id: ID,
        kind: 'running',
        title: 'Waiting to approve acme/shop#5',
        detail: 'Waiting for the checks',
        indeterminate: true,
        actions: CANCEL,
        source: 'verdict',
      },
    ],
    [
      'a failed check ends it as failed, naming the check',
      waiting('ended', 'check-failed', 0, 'lint'),
      {
        id: ID,
        kind: 'failed',
        title: 'Not approved: a check failed on acme/shop#5',
        detail: 'lint did not pass',
        source: 'verdict',
      },
    ],
    [
      'a new commit ends it as failed, saying the pull request changed',
      waiting('ended', 'new-commit'),
      {
        id: ID,
        kind: 'failed',
        title: 'Not approved: acme/shop#5 changed after the review',
        detail: '',
        source: 'verdict',
      },
    ],
    [
      'two hours ends it as failed, naming the pull request',
      waiting('ended', 'timed-out'),
      {
        id: ID,
        kind: 'failed',
        title: 'Not approved: the checks did not finish within 2 hours',
        detail: 'acme/shop#5',
        source: 'verdict',
      },
    ],
    [
      'a closed pull request ends it as failed',
      waiting('ended', 'closed'),
      {
        id: ID,
        kind: 'failed',
        title: 'Not approved: acme/shop#5 was closed',
        detail: '',
        source: 'verdict',
      },
    ],
    ['a cancel removes the entry', waiting('ended', 'cancelled'), null],
    [
      'an approval handed to the verdict removes the entry, the verdict tells the rest',
      waiting('ended', 'approved'),
      null,
    ],
  ];
  for (const [name, status, entry] of rows) {
    it(name, () => {
      expect(
        waitingApprovalNotification(status),
        `the bell tells the outcome of the approval that waits for the checks: ${name}`,
      ).toEqual(entry);
    });
  }

  it('a verdict Scout gave after the checks passed reads "Approved acme/shop#5 after its checks passed"', () => {
    const review = 'https://github.com/acme/shop/pull/5#pullrequestreview-1';

    expect(
      verdictNotification({ ...verdict('given', review), afterChecks: true }),
      'the bell tells the approval was given once every check passed',
    ).toEqual({
      id: 'verdict:act-1',
      kind: 'done',
      title: 'Approved acme/shop#5 after its checks passed',
      detail: '',
      url: review,
      source: 'verdict',
    });
  });
});

describe('issues opened from the audit findings', () => {
  function issuesTurn(reply: string, extra: Partial<LocalTurn> = {}): LocalTurn {
    return {
      id: 'q-7',
      question: 'Open 3 issues on acme/shop',
      reply,
      citations: [],
      state: 'done',
      issues: true,
      issueTitles: [
        'Discounts applied after the sum',
        'Move has no lock',
        'Coupon read per request',
      ],
      ...extra,
    };
  }
  const issue = (n: number, repository = 'acme/shop'): string =>
    `https://github.com/${repository}/issues/${n}`;

  it('three issues opened read "3 issues opened on acme/shop", "From the audit findings you chose", and See the issues opens the repository\'s issues', () => {
    const reply = [
      `Opened ${issue(12)}, ${issue(13)} and ${issue(14)}.`,
      `The first again: ${issue(12)}; one elsewhere: ${issue(3, 'acme/web')}.`,
    ].join('\n');

    expect(
      issuesNotification(issuesTurn(reply)),
      'three distinct issue links of acme/shop make one done entry that opens the repository issues',
    ).toEqual({
      id: 'issues:q-7',
      kind: 'done',
      title: '3 issues opened on acme/shop',
      detail: 'From the audit findings you chose',
      url: 'https://github.com/acme/shop/issues',
      source: 'worker',
    });
  });

  it('one issue opened reads "1 issue opened on acme/shop" and See the issues opens that issue', () => {
    expect(
      issuesNotification(issuesTurn(`Opened ${issue(12)}.`)),
      'one issue link makes "1 issue opened on acme/shop" whose link is that issue',
    ).toEqual({
      id: 'issues:q-7',
      kind: 'done',
      title: '1 issue opened on acme/shop',
      detail: 'From the audit findings you chose',
      url: issue(12),
      source: 'worker',
    });
  });

  it('a failed issues turn, one with no issue link, one with links of another repository only and a turn that carried no issues record no entry', () => {
    const { issues: _carried, ...plain } = issuesTurn(`See ${issue(12)}.`);
    const turns: readonly [string, LocalTurn][] = [
      ['failed', issuesTurn(`Opened ${issue(12)}.`, { state: 'failed' })],
      ['no link', issuesTurn('GitHub refused the issues.')],
      ['another repository', issuesTurn(`Opened ${issue(3, 'acme/web')}.`)],
      ['not an issues turn', plain],
    ];

    expect(
      turns.map(([name, turn]) => [name, issuesNotification(turn)]),
      'a failed issues turn, or one with no issue link, records no entry',
    ).toEqual(turns.map(([name]) => [name, null]));
  });
});

describe('every review sentence, mapped whole', () => {
  const ID = 'reviewjob:acme/api#52';
  const TAIL = 'Ask Scout to review acme/api#52 again to resume it from the first batch not done.';
  const block = (findings: number): string =>
    `\n\n\`\`\`scout-findings\n${JSON.stringify({ coverage: { read: 51, of: 133 }, findings: Array.from({ length: findings }, () => ({})) })}\n\`\`\``;
  const paused = (detail: string): NotificationInput => ({
    id: ID,
    kind: 'paused',
    title: 'Review of acme/api#52 paused',
    detail,
    source: 'worker',
  });
  const done = (title: string, detail: string): NotificationInput => ({
    id: ID,
    kind: 'done',
    title,
    detail,
    actions: [{ id: 'open-chat', label: 'Open in the chat' }],
    source: 'worker',
  });

  const rows: readonly [string, string, NotificationInput | null][] = [
    [
      'the progress line with the area being read',
      'Review of acme/api#52: 6 of 17 batches done, 51 of 133 files read, 3 findings so far, reading src/orders.',
      {
        id: ID,
        kind: 'running',
        title: 'Review of acme/api#52',
        detail:
          '6 of 17 batches done · reading src/orders · 51 of 133 files read · 3 findings so far · the findings arrive in the chat when the review ends',
        progress: 35,
        source: 'worker',
      },
    ],
    [
      'the last progress line, without an area',
      'Review of acme/api#52: 17 of 17 batches done, 133 of 133 files read, 1 finding so far.',
      {
        id: ID,
        kind: 'running',
        title: 'Review of acme/api#52',
        detail:
          '17 of 17 batches done · 133 of 133 files read · 1 finding so far · the findings arrive in the chat when the review ends',
        progress: 100,
        source: 'worker',
      },
    ],
    [
      'the resumed line',
      'Review of acme/api#52 resumed: 6 of 17 batches done, 51 of 133 files read, 3 findings so far.',
      {
        id: ID,
        kind: 'running',
        title: 'Review of acme/api#52',
        detail:
          '6 of 17 batches done · 51 of 133 files read · 3 findings so far · the findings arrive in the chat when the review ends',
        progress: 35,
        source: 'worker',
      },
    ],
    [
      "the owner's pause",
      'The review of acme/api#52 paused: you paused it. Ask Scout to continue the review of acme/api#52 to resume it from the first batch not done.',
      paused('Paused by you'),
    ],
    [
      'the pause at 60 minutes',
      'The review of acme/api#52 paused: it ran for 60 minutes with 11 batches left. Ask Scout to continue the review of acme/api#52 to give it 60 more minutes.',
      paused('It ran for 60 minutes with 11 batches left. Continue to give it 60 more minutes.'),
    ],
    [
      'the GitHub pause',
      `The review of acme/api#52 paused: GitHub could not be read. ${TAIL}`,
      paused('GitHub could not be read. Continue to try again.'),
    ],
    [
      'the usage limit pause',
      `The review of acme/api#52 paused: the AI provider's usage limit was reached. ${TAIL}`,
      paused(
        'The AI Source reached its usage limit. Ask Scout to continue when it has capacity again.',
      ),
    ],
    [
      'the credit pause',
      `The review of acme/api#52 paused: the AI provider's account has run out of credit. ${TAIL}`,
      paused(
        "The AI Source's account has run out of credit. Ask Scout to continue once it has credit again.",
      ),
    ],
    [
      'the rate limit pause',
      `The review of acme/api#52 paused: the AI provider's rate limit was reached. ${TAIL}`,
      paused(
        'The AI Source reached its rate limit. Ask Scout to continue when it has capacity again.',
      ),
    ],
    [
      'the sign-in pause',
      `The review of acme/api#52 paused: the AI provider needs the owner to sign in again. ${TAIL}`,
      paused(
        'The AI Source needs you to sign in again. Sign in from AI Sources, then ask Scout to continue.',
      ),
    ],
    [
      'the rejected key pause',
      `The review of acme/api#52 paused: the AI Source rejected its key. ${TAIL}`,
      paused(
        'The AI Source rejected its key. Give it a working key in AI Sources, then ask Scout to continue.',
      ),
    ],
    [
      'the missing model pause',
      `The review of acme/api#52 paused: the AI Source does not have the model llama3.1. ${TAIL}`,
      paused(
        'The AI Source does not have the model llama3.1. Choose a model it has in Models, then ask Scout to continue.',
      ),
    ],
    [
      'the card',
      `Review of acme/api#52 at 4cf6972:${block(3)}`,
      done(
        'Review of acme/api#52 is done',
        '51 of 133 files read · 3 findings, on the card in the chat',
      ),
    ],
    [
      'the card with one finding',
      `Review of acme/api#52 at 4cf6972:${block(1)}`,
      done(
        'Review of acme/api#52 is done',
        '51 of 133 files read · 1 finding, on the card in the chat',
      ),
    ],
    [
      'the plain "is done" sentence',
      'The review of acme/api#52 at 4cf6972 is done: ready to merge, 133 of 133 files read, 0 findings kept.',
      done(
        'Review of acme/api#52 is done',
        '133 of 133 files read · 0 findings, on the card in the chat',
      ),
    ],
    [
      'the plain "is done" sentence with one finding',
      'The review of acme/api#52 at 4cf6972 is done: ready after the blocking findings, 133 of 133 files read, 1 findings kept.',
      done(
        'Review of acme/api#52 is done',
        '133 of 133 files read · 1 finding, on the card in the chat',
      ),
    ],
    [
      'the stopped card',
      `Review of acme/api#52 at 4cf6972, stopped at your request:${block(3)}`,
      done(
        'Review of acme/api#52 stopped',
        '51 of 133 files read · 3 findings, on the card in the chat',
      ),
    ],
    [
      'nothing was read',
      'The review of acme/api#52 ended at your request: nothing was read.',
      done('Review of acme/api#52 stopped', 'Nothing was read.'),
    ],
    [
      'hostile: an area named like another sentence stays the running entry, its name data',
      'Review of acme/api#52: 6 of 17 batches done, 51 of 133 files read, 3 findings so far, reading x. The review of a/b#1 paused: you paused it.',
      {
        id: ID,
        kind: 'running',
        title: 'Review of acme/api#52',
        detail:
          '6 of 17 batches done · reading x. The review of a/b#1 paused: you paused it · 51 of 133 files read · 3 findings so far · the findings arrive in the chat when the review ends',
        progress: 35,
        source: 'worker',
      },
    ],
    [
      'hostile: a notice that only quotes a progress line after other text is its own entry',
      'Scout says\nReview of acme/api#52: 6 of 17 batches done, 51 of 133 files read, 3 findings so far.',
      {
        id: 'notice:1',
        kind: 'done',
        title: 'Scout says',
        detail:
          'Review of acme/api#52: 6 of 17 batches done, 51 of 133 files read, 3 findings so far.',
        source: 'worker',
      },
    ],
    [
      "hostile: an audit's batch line stays the audit's entry",
      'Audit of acme/shop, src/orders (3 of 8, 4 findings so far):',
      {
        id: 'audit:acme/shop',
        kind: 'running',
        title: 'Audit of acme/shop',
        detail: 'Area 3 of 8 done · 4 findings so far · cards arrive in the chat as each area ends',
        progress: 37,
        source: 'worker',
      },
    ],
  ];

  it.each(rows)('%s', (_name, text, entry) => {
    expect(workerNotice(line(text), nextId)).toEqual(entry);
  });

  it('a hostile area name is still a progress line the chat does not show', () => {
    expect(isBellOnlyReviewLine(rows[18]![1])).toBe(true);
    expect(isBellOnlyReviewLine(rows[19]![1])).toBe(false);
  });

  it("only the progress, the resume and the owner's pause stay out of the chat", () => {
    expect(rows.map(([name, text]) => [name, isBellOnlyReviewLine(text)])).toEqual(
      rows.map(([name]) => [
        name,
        name.startsWith('the progress') ||
          name.startsWith('the last progress') ||
          name === 'the resumed line' ||
          name === "the owner's pause" ||
          name.startsWith('hostile: an area'),
      ]),
    );
  });
});
