import { describe, expect, it } from 'bun:test';

import {
  checksOutcome,
  parseReviewFindings,
  testsLine,
  verdictRequest,
  verdictTestsLine,
  type ReviewCheck,
  type ReviewFinding,
  type ReviewFindings,
  MAX_OFFER_REASON,
  OLD_CARD_REASON,
  offerOf,
  toOffer,
  cardOfTurn,
  latestReviewCard,
} from '../src/index.js';

const UNRUNNABLE =
  '122 test(s) did not pass; the 3 whose failure is in the kept output could not run: they need a Docker daemon, which the container does not have.';

const total: ReviewFinding = {
  id: 'total',
  severity: 'block',
  title: 'Discounts applied after the sum',
  path: 'src/orders/Checkout.cs',
  line: 88,
  context: 'Not from this PR, but this is where it shows.',
  why: '`total` is summed before the discount loop runs.',
  fix: 'Apply the discounts before the sum.',
};
const naming: ReviewFinding = {
  id: 'naming',
  severity: 'nit',
  title: 'Name says the wrong thing',
  path: 'src/orders/Cart.cs',
  line: 12,
  why: '`items` holds ids.',
  fix: 'Rename it `itemIds`.',
};
const review: ReviewFindings = {
  repository: 'acme/shop',
  number: 5,
  commit: 'ec7f951',
  verdict: 'merge after the blocking finding',
  read: '6 of 6 files read',
  findings: [total, naming],
};

const audit: ReviewFindings = {
  kind: 'audit',
  repository: 'acme/shop',
  commit: 'ec7f951',
  verdict: 'two defects to fix before the release',
  read: '41 of 41 files read',
  known: ['Refunds are manual until v2, as the README says.'],
  findings: [
    { ...total, rule: 'ADR 0007: totals are computed after discounts' },
    { ...naming, rule: 'Defect' },
  ],
};

function reply(block: unknown): string {
  const json = typeof block === 'string' ? block : JSON.stringify(block);
  return `## Review of acme/shop\n\nOne blocker.\n\n\`\`\`scout-findings\n${json}\n\`\`\`\n\nAsk me to post it.`;
}

const fence = (body: Record<string, unknown>): string =>
  '```scout-findings\n' + JSON.stringify(body) + '\n```';
const JOB = { repository: 'acme/api', number: 52, verdict: 'ready to merge', findings: [] };

describe('review findings', () => {
  it('parseReviewFindings takes a valid block and leaves the surrounding text, and refuses invalid JSON, a missing field, a bad severity, a bad repository and more than 50 findings', () => {
    expect(parseReviewFindings(reply(review))).toEqual({
      review,
      markdown: '## Review of acme/shop\n\nOne blocker.\n\nAsk me to post it.',
    });
    expect(
      parseReviewFindings(reply({ ...review, findings: [{ ...total, context: '' }] })),
    ).toEqual(
      expect.objectContaining({
        review: expect.objectContaining({ findings: [{ ...total, context: '' }] }),
      }),
    );

    const { why: _why, ...withoutWhy } = total;
    const refused = [
      reply('{"repository": "acme/shop",'),
      reply({ ...review, findings: [withoutWhy] }),
      reply({ ...review, findings: [{ ...total, severity: 'major' }] }),
      reply({ ...review, repository: 'acme/shop/extra' }),
      reply({ ...review, repository: 'acme' }),
      reply({ ...review, number: 0 }),
      reply({ ...review, verdict: '' }),
      reply({ ...review, findings: [total, { ...naming, id: 'total' }] }),
      reply({ ...review, findings: [{ ...total, path: '../secrets' }] }),
      reply({ ...review, findings: [{ ...total, line: 1.5 }] }),
      reply({ ...review, findings: [{ ...total, title: 't'.repeat(201) }] }),
      reply({
        ...review,
        findings: Array.from({ length: 51 }, (_, i) => ({ ...total, id: `f-${i}` })),
      }),
      `${reply(review)}\n\n${reply(review)}`,
      'No findings block at all.',
    ];
    expect(refused.map((reply) => parseReviewFindings(reply))).toEqual(refused.map(() => null));
    expect(
      parseReviewFindings(
        reply({
          ...review,
          findings: Array.from({ length: 50 }, (_, i) => ({ ...total, id: `f-${i}` })),
        }),
      )?.review.findings,
    ).toHaveLength(50);
  });

  it('the request a click on Post sends is the verdict comment: the chosen findings in order, the reviewed commit and no tests line; none without a commit', () => {
    const reviewed = { ...review, commit: 'ec7f951', tests: { ran: true, passed: true } };
    const chosen = [
      { finding: total, body: 'edited body' },
      { finding: naming, body: 'body' },
    ];

    expect(verdictRequest(reviewed, chosen, 'comment')).toEqual({
      repository: 'acme/shop',
      number: 5,
      verdict: 'comment',
      findings: [total, naming],
      commit: 'ec7f951',
      testsLine: '',
    });
    const { commit: _commit, ...unpinned } = reviewed;
    expect(verdictRequest(unpinned, chosen, 'comment')).toBeNull();
  });

  it('parseReviewFindings takes an audit block without a number, with rule and known, and refuses an audit with a malformed known list', () => {
    expect(parseReviewFindings(reply(audit))).toEqual({
      review: audit,
      markdown: '## Review of acme/shop\n\nOne blocker.\n\nAsk me to post it.',
    });
    const { known: _known, ...withoutKnown } = audit;
    expect(parseReviewFindings(reply(withoutKnown))?.review).toEqual(withoutKnown);
    expect(parseReviewFindings(reply({ ...audit, known: [] }))?.review.known).toEqual([]);
    expect(
      parseReviewFindings(reply({ ...review, findings: [{ ...total, rule: 'Defect' }] }))?.review
        .findings,
    ).toEqual([{ ...total, rule: 'Defect' }]);
    expect(
      parseReviewFindings(reply({ ...audit, known: Array.from({ length: 20 }, () => 'k') }))?.review
        .known,
    ).toHaveLength(20);

    const refused = [
      reply({ ...audit, known: 'Refunds are manual.' }),
      reply({ ...audit, known: [''] }),
      reply({ ...audit, known: ['   '] }),
      reply({ ...audit, known: [42] }),
      reply({ ...audit, known: ['k'.repeat(301)] }),
      reply({ ...audit, known: Array.from({ length: 21 }, () => 'k') }),
      reply({ ...audit, number: 5 }),
      reply({ ...audit, kind: 'scan' }),
      reply({ ...audit, findings: [{ ...total, rule: '' }] }),
      reply({ ...audit, findings: [{ ...total, rule: 'r'.repeat(301) }] }),
      reply({ ...review, findings: [{ ...total, rule: 7 }] }),
      reply({ ...withoutKnown, kind: 'review' }),
    ];
    expect(refused.map((reply) => parseReviewFindings(reply))).toEqual(refused.map(() => null));
  });

  it('parseReviewFindings keeps the checks, the container run and selfAuthored of a review, keeps a check without an https link but not the link, and drops a malformed value without refusing the review', () => {
    const passed: ReviewCheck = {
      name: 'build',
      status: 'completed',
      conclusion: 'success',
      url: 'https://github.com/acme/shop/runs/1',
    };
    const pending: ReviewCheck = {
      name: 'deploy-preview',
      status: 'in_progress',
      conclusion: null,
      url: 'https://ci.example.com/7',
    };
    const ran = {
      ran: true,
      image: 'mcr.microsoft.com/dotnet/sdk@sha256:ab',
      command: 'dotnet test',
      exitCode: 1,
      durationSeconds: 42.4,
      output: 'x'.repeat(4_100),
    };
    const kept = parseReviewFindings(
      reply({
        ...review,
        checks: [
          passed,
          { ...passed, name: 'status-a', conclusion: 'failure', url: 'javascript:alert(1)' },
          { ...passed, name: 'status-b', url: null },
          { ...passed, name: '' },
          { ...passed, conclusion: 7 },
          'build',
          pending,
        ],
        tests: ran,
        selfAuthored: true,
      }),
    )?.review;
    const dropped = [
      { checks: 'all green' },
      { checks: [{ ...passed, status: '' }] },
      { tests: { ran: true, command: 'dotnet test', durationSeconds: 3 } },
      { tests: { ran: true, command: '', exitCode: 0, durationSeconds: 3 } },
      { tests: { ran: true, command: 'dotnet test', exitCode: 0, durationSeconds: -1 } },
      { tests: { ran: false } },
      { tests: { ran: 'yes', reason: 'Docker is not installed.' } },
      { selfAuthored: 'true' },
    ].map((extra) => parseReviewFindings(reply({ ...review, ...extra }))?.review);

    expect(kept).toEqual({
      ...review,
      checks: [
        passed,
        { ...passed, name: 'status-a', conclusion: 'failure', url: '' },
        { ...passed, name: 'status-b', url: '' },
        pending,
      ],
      tests: { ...ran, output: 'x'.repeat(4_000) },
      selfAuthored: true,
    });
    expect(dropped).toEqual(dropped.map(() => review));
    expect(
      parseReviewFindings(
        reply({
          ...review,
          tests: { ran: false, reason: 'Docker is not installed.', exitCode: 3 },
        }),
      )?.review.tests,
    ).toEqual({ ran: false, reason: 'Docker is not installed.' });
  });

  it('parseReviewFindings keeps unrunnable of a failed container run, and drops it on a passing run and when it is not text', () => {
    const run = { ran: true, command: 'dotnet test', exitCode: 1, durationSeconds: 12 };
    const tests = (extra: Record<string, unknown>) =>
      parseReviewFindings(reply({ ...review, tests: { ...run, ...extra } }))?.review.tests;

    expect(
      tests({ unrunnable: UNRUNNABLE }),
      'a failed run keeps the sentence that says its failed tests could not run',
    ).toEqual({ ...run, unrunnable: UNRUNNABLE });
    expect(
      [
        tests({ exitCode: 0, unrunnable: UNRUNNABLE }),
        tests({ unrunnable: 3 }),
        tests({ unrunnable: '  ' }),
        parseReviewFindings(
          reply({
            ...review,
            tests: { ran: false, reason: 'Docker is not installed.', unrunnable: UNRUNNABLE },
          }),
        )?.review.tests,
      ],
      'unrunnable is read only on a failed run and only as text',
    ).toEqual([
      { ...run, exitCode: 0 },
      run,
      run,
      { ran: false, reason: 'Docker is not installed.' },
    ]);
  });

  it("a verdict carries the findings with the owner's edited text, a review job's coverage and notes, and never a body", () => {
    const edited = { ...naming, why: 'Edited by the owner.', fix: 'Rename it as the owner says.' };
    const job: ReviewFindings = {
      ...review,
      coverage: { read: 51, of: 133 },
      notes: ['82 files not read: the review was stopped.'],
    };

    const request = verdictRequest(job, [{ finding: edited, body: 'body' }], 'request_changes');

    expect(request, 'the findings as the owner left them, with what code measured').toEqual({
      repository: 'acme/shop',
      number: 5,
      verdict: 'request_changes',
      findings: [edited],
      coverage: { read: 51, of: 133 },
      notes: ['82 files not read: the review was stopped.'],
      commit: 'ec7f951',
      testsLine: '',
    });
    expect(request).not.toHaveProperty('body');
    expect(request).not.toHaveProperty('comments');
  });

  it('verdictRequest is the verdict pinned to the reviewed commit, with the chosen findings in order and the tests line; none on an audit or a review without a commit', () => {
    const chosen = [
      { finding: naming, body: 'Edited by the owner' },
      { finding: total, body: 'body' },
    ];
    const checked: ReviewFindings = {
      ...review,
      checks: [
        { name: 'build', status: 'completed', conclusion: 'success', url: '' },
        { name: 'lint', status: 'completed', conclusion: 'failure', url: '' },
        { name: 'e2e', status: 'queued', conclusion: null, url: '' },
      ],
      tests: { ran: true, command: 'dotnet test', exitCode: 0, durationSeconds: 41.6 },
    };
    const { commit: _commit, ...withoutCommit } = review;

    expect(verdictRequest(checked, chosen, 'approve')).toEqual({
      repository: 'acme/shop',
      number: 5,
      verdict: 'approve',
      findings: [naming, total],
      commit: 'ec7f951',
      testsLine:
        'Checks: build passed, lint failed, e2e pending\nTests passed in 42s (dotnet test)',
    });
    expect(verdictRequest(review, [], 'request_changes')).toEqual({
      repository: 'acme/shop',
      number: 5,
      verdict: 'request_changes',
      findings: [],
      commit: 'ec7f951',
      testsLine: '',
    });
    expect(verdictRequest(withoutCommit, chosen, 'approve')).toBeNull();
    expect(verdictRequest(audit, chosen, 'approve')).toBeNull();
  });

  it.each<{
    name: string;
    checks: readonly ReviewCheck[] | undefined;
    running: number;
    failed: string | null;
  }>([
    { name: 'no checks', checks: undefined, running: 0, failed: null },
    {
      name: 'success, neutral and skipped all passed',
      checks: [
        { name: 'build', status: 'completed', conclusion: 'success', url: '' },
        { name: 'docs', status: 'completed', conclusion: 'neutral', url: '' },
        { name: 'e2e', status: 'completed', conclusion: 'skipped', url: '' },
      ],
      running: 0,
      failed: null,
    },
    {
      name: 'queued and in progress are running',
      checks: [
        { name: 'build', status: 'completed', conclusion: 'success', url: '' },
        { name: 'e2e', status: 'queued', conclusion: null, url: '' },
        { name: 'lint', status: 'in_progress', conclusion: null, url: '' },
      ],
      running: 2,
      failed: null,
    },
    {
      name: 'a completed check that did not succeed failed, the first one named, beside one running',
      checks: [
        { name: 'e2e', status: 'in_progress', conclusion: null, url: '' },
        { name: 'lint', status: 'completed', conclusion: 'failure', url: '' },
        { name: 'deploy', status: 'completed', conclusion: 'cancelled', url: '' },
      ],
      running: 1,
      failed: 'lint',
    },
    {
      name: 'a completed check with no conclusion failed, as the worker reads it',
      checks: [{ name: 'build', status: 'completed', conclusion: null, url: '' }],
      running: 0,
      failed: 'build',
    },
  ])(
    'checksOutcome reads the checks as the worker judges an approval: $name',
    ({ checks, running, failed }) => {
      expect(
        checksOutcome(checks),
        'running counts the checks not completed; failed names the first completed one that is not success, neutral or skipped',
      ).toEqual({ running, failed });
    },
  );

  it('verdictTestsLine is the checks with their marks, then the container run, each on its line; empty with neither', () => {
    expect([
      verdictTestsLine({ ...review, tests: { ran: false, reason: 'Docker is not installed.' } }),
      verdictTestsLine({
        ...review,
        checks: [{ name: 'build', status: 'completed', conclusion: 'skipped', url: '' }],
      }),
      verdictTestsLine(review),
    ]).toEqual(['Tests did not run: Docker is not installed.', 'Checks: build passed', '']);
  });

  it('testsLine and verdictTestsLine say the tests could not run with the sentence, never Tests failed', () => {
    const tests = {
      ran: true,
      command: 'dotnet test',
      exitCode: 1,
      durationSeconds: 12,
      unrunnable: UNRUNNABLE,
    };
    const passed: ReviewCheck = {
      name: 'build',
      status: 'completed',
      conclusion: 'success',
      url: '',
    };
    const lines = [testsLine(tests), verdictTestsLine({ ...review, checks: [passed], tests })];

    expect(
      lines,
      'a run whose failed tests could not run says so with the worker sentence, after the checks',
    ).toEqual([
      `Tests could not run: ${UNRUNNABLE}`,
      `Checks: build passed\nTests could not run: ${UNRUNNABLE}`,
    ]);
    expect(lines.join('\n'), 'an unrunnable run is never called a failure').not.toContain(
      'Tests failed',
    );
  });

  it('a review with no finding parses and an audit with none is refused; the worker composes what a clean review posts', () => {
    const clean: ReviewFindings = { ...review, verdict: 'ready to merge', findings: [] };

    expect(parseReviewFindings(reply(clean))).toEqual({
      review: clean,
      markdown: '## Review of acme/shop\n\nOne blocker.\n\nAsk me to post it.',
    });
    expect(parseReviewFindings(reply({ ...audit, findings: [] }))).toBeNull();
  });

  it("parseReviewFindings keeps a review job's coverage and notes from a notice, and drops a malformed coverage or notes without refusing the review", () => {
    const kept = parseReviewFindings(
      fence({
        ...JOB,
        coverage: { read: 51, of: 133 },
        notes: ['82 files not read: the review was stopped.'],
      }),
      true,
    );
    const badCoverage = parseReviewFindings(
      fence({ ...JOB, coverage: { read: 140, of: 133 }, notes: ['x'] }),
      true,
    );
    const badNotes = parseReviewFindings(
      fence({ ...JOB, coverage: { read: 1, of: 2 }, notes: [42] }),
      true,
    );
    const none = parseReviewFindings(fence(JOB), true);

    expect([kept?.review.coverage, kept?.review.notes]).toEqual([
      { read: 51, of: 133 },
      ['82 files not read: the review was stopped.'],
    ]);
    expect([badCoverage?.review.coverage, badCoverage?.review.notes]).toEqual([
      undefined,
      undefined,
    ]);
    expect([badNotes?.review.coverage, badNotes?.review.notes]).toEqual([
      { read: 1, of: 2 },
      undefined,
    ]);
    expect([none?.review.coverage, none?.review.notes]).toEqual([undefined, undefined]);
  });

  it('coverage and notes are kept from a worker notice and dropped from a reply', () => {
    const block = fence({ ...JOB, coverage: { read: 50, of: 50 }, notes: ['A note.'] });

    const notice = parseReviewFindings(block, true);
    const reply = parseReviewFindings(block);

    expect([notice?.review.coverage, notice?.review.notes], 'a notice keeps them').toEqual([
      { read: 50, of: 50 },
      ['A note.'],
    ]);
    expect(
      [reply?.review.coverage, reply?.review.notes, reply?.review.repository],
      'a reply drops them and keeps the review',
    ).toEqual([undefined, undefined, 'acme/api']);
  });

  it('a review with 20 notes of 300 characters each keeps them all', () => {
    const notes = Array.from({ length: 20 }, (_, i) => String(i).padEnd(300, 'n'));

    const parsed = parseReviewFindings(
      fence({ ...JOB, coverage: { read: 3, of: 4 }, notes }),
      true,
    );

    expect(parsed?.review.notes, 'notes at the bound are kept').toEqual(notes);
  });

  it('an audit block never carries coverage or notes, even in a notice', () => {
    const parsed = parseReviewFindings(
      fence({
        kind: 'audit',
        repository: 'acme/api',
        verdict: 'sound',
        findings: [
          { id: 'a', severity: 'fix', title: 'T', path: 'src/A.cs', line: 1, why: 'W', fix: 'F' },
        ],
        coverage: { read: 1, of: 2 },
        notes: ['A note.'],
      }),
      true,
    );

    expect(
      [
        parsed?.review.kind,
        parsed?.review.findings.length,
        parsed?.review.coverage,
        parsed?.review.notes,
      ],
      'an audit parses without coverage or notes',
    ).toEqual(['audit', 1, undefined, undefined]);
  });

  it.each([
    ['21 notes', Array.from({ length: 21 }, (_, i) => `Note ${i}.`)],
    ['a note of 301 characters', ['n'.repeat(301)]],
  ])('a review with %s keeps its coverage and drops its notes', (_case, notes) => {
    const parsed = parseReviewFindings(
      fence({ ...JOB, coverage: { read: 3, of: 4 }, notes }),
      true,
    );

    expect(
      [parsed?.review.coverage, parsed?.review.notes],
      'notes past the bound are dropped',
    ).toEqual([{ read: 3, of: 4 }, undefined]);
  });

  it.each([
    ['read above of', { read: 5, of: 4 }],
    ['a negative count', { read: -1, of: 4 }],
    ['a count that is not whole', { read: 1.5, of: 4 }],
  ])('coverage with %s is dropped, and its notes with it', (_case, coverage) => {
    const parsed = parseReviewFindings(fence({ ...JOB, coverage, notes: ['A note.'] }), true);

    expect(
      [parsed?.review.coverage, parsed?.review.notes, parsed?.review.number],
      'a bad coverage is dropped and the review kept',
    ).toEqual([undefined, undefined, 52]);
  });
});

describe('the offer of a review', () => {
  const offered = {
    verdict: 'request_changes' as const,
    reason: 'Request changes: 1 finding needs fixing.',
    commentInstead: true,
  };

  it('keeps the worker offer of a review card as data', () => {
    const parsed = parseReviewFindings(reply({ ...review, offer: offered }));
    expect(parsed?.review.offer).toEqual(offered);
  });

  it('ignores an offer that is not one, and a card from before the offer still parses', () => {
    for (const offer of [
      { verdict: 'merge', reason: 'r', commentInstead: false },
      { verdict: 'comment', reason: 'r', commentInstead: true },
      { verdict: 'approve', reason: '', commentInstead: false },
      { verdict: 'approve', reason: 'x'.repeat(MAX_OFFER_REASON + 1), commentInstead: false },
    ]) {
      const parsed = parseReviewFindings(reply({ ...review, offer }));
      expect(parsed?.review.offer).toBeUndefined();
    }
    expect(parseReviewFindings(reply(review))?.review.offer).toBeUndefined();
  });

  it('offers Comment only, never Approve, on a card from before the offer, and says why', () => {
    expect(offerOf({})).toEqual({
      verdict: 'comment',
      reason: OLD_CARD_REASON,
      commentInstead: false,
    });
    expect(OLD_CARD_REASON).toBe(
      'This review was made before Scout decided the verdict; review it again to see what Scout offers.',
    );
    expect(toOffer({ ...offered, extra: 1 })).toEqual(offered);
  });
});

describe('the review card of a turn', () => {
  const turn = (id: string, body: unknown, state: 'done' | 'running' = 'done') => ({
    id,
    state,
    reply: reply(body),
  });

  it('is read only from a done turn, and the newest review of the pull request asked wins, never another one or an audit', () => {
    const turns = [
      turn('a', { ...review, verdict: 'older' }),
      turn('b', review),
      turn('c', { ...review, number: (review.number ?? 0) + 1, verdict: 'other' }),
      turn('d', { ...review, verdict: 'still running' }, 'running'),
      turn('e', audit),
    ];

    expect(cardOfTurn(turns[3]!)).toBeUndefined();
    expect(latestReviewCard(turns, review.repository.toUpperCase(), review.number!)?.verdict).toBe(
      review.verdict,
    );
    expect(latestReviewCard(turns, review.repository, 999)).toBeNull();
  });
});
