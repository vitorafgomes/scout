import { ask, expect, test, useOnThisComputer } from '../fixtures/app.js';

const REVIEW_WITH_A_CHECK_RUNNING = {
  repository: 'acme/shop',
  number: 5,
  commit: 'ec7f951',
  verdict: 'merge once the checks pass',
  read: 'The whole diff and its tests.',
  checks: [
    {
      name: 'build',
      status: 'completed',
      conclusion: 'success',
      url: 'https://github.com/acme/shop/runs/1',
    },
    {
      name: 'e2e',
      status: 'in_progress',
      conclusion: null,
      url: 'https://github.com/acme/shop/runs/2',
    },
  ],
  findings: [
    {
      id: 'lock',
      severity: 'fix',
      title: 'Move has no lock',
      path: 'src/orders/Cart.cs',
      line: 12,
      why: 'Two moves at once lose an item.',
      fix: 'Take the cart lock.',
    },
  ],
};

test.use({
  script: {
    replies: [
      {
        complete: `One thing to fix on acme/shop#5.\n\n\`\`\`scout-findings\n${JSON.stringify(REVIEW_WITH_A_CHECK_RUNNING)}\n\`\`\``,
      },
    ],
  },
});

test('a review whose checks still run offers Approve when the checks pass; the click makes the card wait for them with Cancel, and Cancel brings the offer back', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await ask(page, 'Review acme/shop#5');

  const checkLine = page.getByTestId('check-line');
  const approveWhenGreen = page.getByTestId('approve-when-green');
  await expect(checkLine).toHaveText(
    '1 check is still running. Scout approves only when every check passed.',
  );
  await expect(approveWhenGreen).toHaveText('Approve when the checks pass');
  await expect(page.getByTestId('approve')).toHaveCount(0);
  await expect(page.getByTestId('request-changes')).toBeVisible();

  await approveWhenGreen.click();

  const waiting = page.getByTestId('waiting-line');
  await expect(waiting).toHaveText(
    'Waiting for the checks: 1 still running. Scout approves when they pass.',
  );
  await expect(waiting).toHaveAttribute('role', 'status');
  await expect(page.getByTestId('cancel-waiting')).toHaveAccessibleName(
    'Cancel the approval of acme/shop#5',
  );
  await expect(approveWhenGreen).toHaveCount(0);
  await expect(page.getByTestId('request-changes')).toHaveCount(0);
  await expect(page.getByTestId('post')).toBeVisible();
  await expect(checkLine).toHaveCount(0);

  await page.getByTestId('cancel-waiting').click();

  await expect(waiting).toHaveCount(0);
  await expect(approveWhenGreen).toHaveText('Approve when the checks pass');
  await expect(checkLine).toHaveText(
    '1 check is still running. Scout approves only when every check passed.',
  );
  await expect(page.getByTestId('approve')).toHaveCount(0);
});
