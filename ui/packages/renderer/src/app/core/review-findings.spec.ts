import { describe, expect, it } from 'vitest';

import { REVIEW_MARKER, type ReviewFinding, type ReviewFindings } from '@scout/shared';

import { commentBody, parseReviewFindings, postingAsk, reviewBody } from './review-findings';

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
});
