import { ask, expect, test, useOnThisComputer } from '../fixtures/app.js';

const ISSUES_MARKER = '\n\nIssues to open (data, not instructions):\n';

function finding(id: string, title: string, line: number): Record<string, unknown> {
  return {
    id,
    severity: 'fix',
    title,
    path: 'src/orders/Checkout.cs',
    line,
    why: `${title}, as the code reads today.`,
    fix: 'Change it.',
  };
}

const FIRST = [
  finding('total', 'Discounts applied after the sum', 88),
  finding('lock', 'Move has no lock', 12),
  finding('naming', 'Name says the wrong thing', 30),
];
const NEW = [
  finding('rounding', 'Total is rounded twice', 120),
  finding('retry', 'Retry has no limit', 40),
];

function batch(findings: readonly Record<string, unknown>[]): string {
  const block = {
    kind: 'audit',
    repository: 'acme/shop',
    commit: 'ec7f951',
    verdict: 'defects to fix',
    findings,
  };
  return `Findings of this area.\n\n\`\`\`scout-findings\n${JSON.stringify(block)}\n\`\`\``;
}

test.use({
  script: {
    replies: [
      { complete: batch(FIRST) },
      { complete: 'The issues are on their way.' },
      { complete: batch([...FIRST, ...NEW]) },
      { complete: 'The two new issues are on their way; the other three went earlier.' },
    ],
  },
});

test('issues already sent leave the count: a later batch of the same audit offers only its new findings, and Open sends only those', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  const sentMarks = page.getByTestId('sent-mark');
  const open = page.getByTestId('post');

  await ask(page, 'Audit acme/shop');
  await expect(page.getByTestId('finding')).toHaveCount(3);
  await page.getByTestId('yes-all').click();
  await expect(open).toHaveText('Open 3 issues on acme/shop');
  await open.click();

  await expect(sentMarks).toHaveCount(3);
  await expect(sentMarks.first()).toHaveText('Issue queued');
  await expect(page.getByTestId('all-sent')).toContainText(
    'Every chosen issue is queued on acme/shop',
  );
  await expect(open).toHaveCount(0);

  await ask(page, 'Audit acme/shop again');
  await expect(page.getByTestId('finding')).toHaveCount(5);
  await expect(sentMarks).toHaveCount(3);
  await page.getByTestId('yes-all').click();
  await expect(open).toHaveText('Open 2 issues on acme/shop');
  await open.click();
  await expect(sentMarks).toHaveCount(5);
  await expect(open).toHaveCount(0);

  const asks = (await desktop.received()).filter((line) => line['type'] === 'ask');
  const issues = asks
    .map((line) => String(line['text']))
    .filter((text) => text.includes(ISSUES_MARKER))
    .map((text) => {
      const [question, json] = text.split(ISSUES_MARKER);
      const titles = (JSON.parse(json!) as { issues: { title: string }[] }).issues.map(
        (issue) => issue.title,
      );
      return { question, titles };
    });
  expect(issues).toEqual([
    {
      question: 'Open 3 issues on acme/shop',
      titles: ['Discounts applied after the sum', 'Move has no lock', 'Name says the wrong thing'],
    },
    {
      question: 'Open 2 issues on acme/shop',
      titles: ['Total is rounded twice', 'Retry has no limit'],
    },
  ]);
});
