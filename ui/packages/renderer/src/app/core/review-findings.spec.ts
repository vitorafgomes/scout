import { describe, expect, it } from 'vitest';

import {
  ISSUES_MARKER,
  REVIEW_MARKER,
  VERDICT_MARKER,
  type LocalTurn,
  type ReviewCheck,
  type ReviewFinding,
  type ReviewFindings,
} from '@scout/shared';

import {
  commentBody,
  issueBody,
  issuesAsk,
  parseReviewFindings,
  postingAsk,
  reviewBody,
  verdictAsk,
  verdictOf,
} from './review-findings';

const RED = '\u{1F534}';
const YELLOW = '\u{1F7E1}';
const GREEN = '\u{1F7E2}';

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
      reply({ ...review, findings: [] }),
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
    expect(refused.map(parseReviewFindings)).toEqual(refused.map(() => null));
    expect(
      parseReviewFindings(
        reply({
          ...review,
          findings: Array.from({ length: 50 }, (_, i) => ({ ...total, id: `f-${i}` })),
        }),
      )?.review.findings,
    ).toHaveLength(50);
  });

  it('commentBody and reviewBody follow the posted-comment shape with the severity mark', () => {
    expect(commentBody(total)).toBe(
      `${RED} Discounts applied after the sum\n\nNot from this PR, but this is where it shows.\n\n\`total\` is summed before the discount loop runs.\n\n**Fix:** Apply the discounts before the sum.`,
    );
    expect(commentBody(naming)).toBe(
      `${GREEN} Name says the wrong thing\n\n\`items\` holds ids.\n\n**Fix:** Rename it \`itemIds\`.`,
    );
    expect(commentBody({ ...naming, severity: 'fix' }).startsWith(`${YELLOW} `)).toBe(true);
    expect(reviewBody(review, [total, naming])).toBe(
      `**Verdict:** merge after the blocking finding\n\n- ${RED} Discounts applied after the sum · \`src/orders/Checkout.cs:88\`\n- ${GREEN} Name says the wrong thing · \`src/orders/Cart.cs:12\`\n\n6 of 6 files read`,
    );
    const { read: _read, ...unread } = review;
    expect(reviewBody(unread, [naming])).toBe(
      `**Verdict:** merge after the blocking finding\n\n- ${GREEN} Name says the wrong thing · \`src/orders/Cart.cs:12\``,
    );
  });

  it('postingAsk is the bare question, the review marker and the JSON of the selected comments in order', () => {
    const ask = postingAsk(review, [
      { finding: total, body: 'edited body' },
      { finding: naming, body: commentBody(naming) },
    ]);
    const [question, json] = ask.split(REVIEW_MARKER);

    expect(question).toBe('Post 2 findings on acme/shop#5');
    expect(JSON.parse(json!)).toEqual({
      repository: 'acme/shop',
      number: 5,
      body: reviewBody(review, [total, naming]),
      comments: [
        { path: 'src/orders/Checkout.cs', line: 88, body: 'edited body' },
        { path: 'src/orders/Cart.cs', line: 12, body: commentBody(naming) },
      ],
    });
    expect(postingAsk(review, [{ finding: naming, body: 'x' }]).split(REVIEW_MARKER)[0]).toBe(
      'Post 1 finding on acme/shop#5',
    );
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
    expect(refused.map(parseReviewFindings)).toEqual(refused.map(() => null));
  });

  it('issueBody follows the Problem, Evidence, Proposed change shape with the rule and the drafted-by line', () => {
    expect(issueBody(audit, audit.findings[0]!)).toBe(
      `## Problem\n\n${RED} Discounts applied after the sum\n\n\`total\` is summed before the discount loop runs.\n\n## Evidence\n\n\`src/orders/Checkout.cs:88\`\n\nNot from this PR, but this is where it shows.\n\n## Proposed change\n\nApply the discounts before the sum.\n\n**Rule:** ADR 0007: totals are computed after discounts\n\n---\nDrafted by Scout from an audit of acme/shop at ec7f951.`,
    );
    const { commit: _commit, ...atHead } = audit;
    expect(issueBody(atHead, naming)).toBe(
      `## Problem\n\n${GREEN} Name says the wrong thing\n\n\`items\` holds ids.\n\n## Evidence\n\n\`src/orders/Cart.cs:12\`\n\n## Proposed change\n\nRename it \`itemIds\`.\n\n---\nDrafted by Scout from an audit of acme/shop.`,
    );
    expect(issueBody(audit, naming, 'The owner rewrote this issue.')).toBe(
      'The owner rewrote this issue.',
    );
  });

  it('issuesAsk is the bare question, the issues marker and one issue per chosen finding in order with its title and body', () => {
    const ask = issuesAsk(audit, [
      { finding: naming, body: 'edited body' },
      { finding: total, body: issueBody(audit, total) },
    ]);
    const [question, json] = ask.split(ISSUES_MARKER);

    expect(question).toBe('Open 2 issues on acme/shop');
    expect(JSON.parse(json!)).toEqual({
      repository: 'acme/shop',
      issues: [
        { title: 'Name says the wrong thing', body: 'edited body' },
        { title: 'Discounts applied after the sum', body: issueBody(audit, total) },
      ],
    });
    expect(issuesAsk(audit, [{ finding: naming, body: 'x' }]).split(ISSUES_MARKER)[0]).toBe(
      'Open 1 issue on acme/shop',
    );
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

  it('verdictAsk is the verdict sentence, the verdict marker and the JSON of the verdict with the review body and the chosen comments in order', () => {
    const chosen = [
      { finding: naming, body: 'Edited by the owner' },
      { finding: total, body: commentBody(total) },
    ];

    const [approveQuestion, approveJson] = verdictAsk(review, chosen, 'approve').split(
      VERDICT_MARKER,
    );
    const [changesQuestion, changesJson] = verdictAsk(review, [], 'request_changes').split(
      VERDICT_MARKER,
    );

    expect(approveQuestion).toBe('Approve acme/shop#5');
    expect(JSON.parse(approveJson!)).toEqual({
      repository: 'acme/shop',
      number: 5,
      verdict: 'approve',
      body: reviewBody(review, [naming, total]),
      comments: [
        { path: 'src/orders/Cart.cs', line: 12, body: 'Edited by the owner' },
        { path: 'src/orders/Checkout.cs', line: 88, body: commentBody(total) },
      ],
    });
    expect(changesQuestion).toBe('Request changes on acme/shop#5');
    expect(JSON.parse(changesJson!)).toEqual({
      repository: 'acme/shop',
      number: 5,
      verdict: 'request_changes',
      body: reviewBody(review, []),
      comments: [],
    });
  });

  it('verdictOf reads the verdict from the shared turns: giving while it runs, given with the link, failed after a failure or an answer without a link, nothing for another pull request, an earlier turn or an audit', () => {
    const turn = (
      id: string,
      question: string,
      state: LocalTurn['state'],
      reply = '',
    ): LocalTurn => ({ id, question, reply, citations: [], state });
    const findings = turn('f-1', 'review acme/shop#5', 'done');
    const link = 'https://github.com/acme/shop/pull/5#pullrequestreview-9';

    expect({
      none: verdictOf([findings], 'f-1', review),
      giving: verdictOf([findings, turn('v', 'Approve acme/shop#5', 'running')], 'f-1', review),
      given: verdictOf(
        [findings, turn('v', 'Request changes on acme/shop#5', 'done', `Done: ${link}.`)],
        'f-1',
        review,
      ),
      failed: verdictOf(
        [findings, turn('v', 'Approve acme/shop#5', 'failed', `GitHub refused ${link}`)],
        'f-1',
        review,
      ),
      withoutLink: verdictOf(
        [findings, turn('v', 'Approve acme/shop#5', 'done', 'Writes are not allowed.')],
        'f-1',
        review,
      ),
      givenThenFailedAgain: verdictOf(
        [
          findings,
          turn('v', 'Approve acme/shop#5', 'done', link),
          turn('w', 'Approve acme/shop#5', 'failed'),
        ],
        'f-1',
        review,
      ),
      otherPullRequest: verdictOf(
        [findings, turn('v', 'Approve acme/shop#6', 'done', link)],
        'f-1',
        review,
      ),
      beforeTheFindings: verdictOf(
        [turn('v', 'Approve acme/shop#5', 'done', link), findings],
        'f-1',
        review,
      ),
      audit: verdictOf([findings, turn('v', 'Approve acme/shop#5', 'done', link)], 'f-1', audit),
    }).toEqual({
      none: null,
      giving: { verdict: 'approve', phase: 'giving' },
      given: { verdict: 'request_changes', phase: 'given', url: link },
      failed: { verdict: 'approve', phase: 'failed' },
      withoutLink: { verdict: 'approve', phase: 'failed' },
      givenThenFailedAgain: { verdict: 'approve', phase: 'given', url: link },
      otherPullRequest: null,
      beforeTheFindings: null,
      audit: null,
    });
  });
});
