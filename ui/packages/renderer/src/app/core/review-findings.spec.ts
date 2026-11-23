import { describe, expect, it } from 'vitest';

import { ISSUES_MARKER, type ReviewFinding, type ReviewFindings } from '@scout/shared';

import { issueBody, issuesAsk } from './review-findings';

const RED = ':no_entry:';
const GREEN = ':bulb:';

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

describe('review findings', () => {
  it('an issue marks its severity with a shortcode, never an emoji character', () => {
    const marks = (['block', 'fix', 'nit'] as const).map(
      (severity) => /## Problem\n\n(\S+) /.exec(issueBody(audit, { ...naming, severity }))?.[1],
    );

    expect(marks, 'each severity opens the issue with its GitHub shortcode').toEqual([
      ':no_entry:',
      ':wrench:',
      ':bulb:',
    ]);
    expect(
      /\p{Extended_Pictographic}/u.test(issueBody(audit, total)),
      'no emoji character is in the issue body',
    ).toBe(false);
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
});
