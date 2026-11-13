import { describe, expect, it } from 'vitest';

import {
  ISSUES_MARKER,
  type ReviewCheck,
  type ReviewFinding,
  type ReviewFindings,
} from '@scout/shared';

import {
  commentBody,
  issueBody,
  issuesAsk,
  parseReviewFindings,
  reviewBody,
  verdictRequest,
  verdictTestsLine,
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

  it('the request a click on Post sends is the verdict comment: the review body, the chosen findings as comments in order, the reviewed commit and no tests line; none without a commit', () => {
    const reviewed = { ...review, commit: 'ec7f951', tests: { ran: true, passed: true } };
    const chosen = [
      { finding: total, body: 'edited body' },
      { finding: naming, body: commentBody(naming) },
    ];

    expect(verdictRequest(reviewed, chosen, 'comment')).toEqual({
      repository: 'acme/shop',
      number: 5,
      verdict: 'comment',
      body: reviewBody(reviewed, [total, naming]),
      comments: [
        { path: 'src/orders/Checkout.cs', line: 88, body: 'edited body' },
        { path: 'src/orders/Cart.cs', line: 12, body: commentBody(naming) },
      ],
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
    expect(
      issueBody(audit, { ...naming, why: 'The owner rewrote why.', fix: 'And the fix.' }),
    ).toBe(
      `## Problem\n\n${GREEN} Name says the wrong thing\n\nThe owner rewrote why.\n\n## Evidence\n\n\`src/orders/Cart.cs:12\`\n\n## Proposed change\n\nAnd the fix.\n\n---\nDrafted by Scout from an audit of acme/shop at ec7f951.`,
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

  it('verdictRequest is the verdict pinned to the reviewed commit, with the review body, the chosen findings as comments in order and the tests line; none on an audit or a review without a commit', () => {
    const chosen = [
      { finding: naming, body: 'Edited by the owner' },
      { finding: total, body: commentBody(total) },
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
      body: reviewBody(checked, [naming, total]),
      comments: [
        { path: 'src/orders/Cart.cs', line: 12, body: 'Edited by the owner' },
        { path: 'src/orders/Checkout.cs', line: 88, body: commentBody(total) },
      ],
      commit: 'ec7f951',
      testsLine:
        'Checks: build passed, lint failed, e2e pending\nTests passed in 42s (dotnet test)',
    });
    expect(verdictRequest(review, [], 'request_changes')).toEqual({
      repository: 'acme/shop',
      number: 5,
      verdict: 'request_changes',
      body: reviewBody(review, []),
      comments: [],
      commit: 'ec7f951',
      testsLine: '',
    });
    expect(verdictRequest(withoutCommit, chosen, 'approve')).toBeNull();
    expect(verdictRequest(audit, chosen, 'approve')).toBeNull();
  });

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

  it('a review with no finding parses and an audit with none is refused; a clean review posts its verdict, No findings. and what was read, with no blank gap, and the all-dismissed body is unchanged', () => {
    const clean: ReviewFindings = { ...review, verdict: 'ready to merge', findings: [] };

    expect(parseReviewFindings(reply(clean))).toEqual({
      review: clean,
      markdown: '## Review of acme/shop\n\nOne blocker.\n\nAsk me to post it.',
    });
    expect(parseReviewFindings(reply({ ...audit, findings: [] }))).toBeNull();
    expect(reviewBody(clean, [])).toBe(
      '**Verdict:** ready to merge\n\nNo findings.\n\n6 of 6 files read',
    );
    const { read: _read, ...unread } = clean;
    expect(reviewBody(unread, [])).toBe('**Verdict:** ready to merge\n\nNo findings.');
    expect(reviewBody(review, [])).toBe(
      '**Verdict:** merge after the blocking finding\n\n\n\n6 of 6 files read',
    );
  });
});
