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
  findings: [],
  offer: {
    verdict: 'wait',
    reason: 'Approve when the checks pass: 1 check is still running.',
    commentInstead: false,
  },
};

test.use({
  script: {
    replies: [
      {
        complete: `Nothing to report on acme/shop#5.\n\n\`\`\`scout-findings\n${JSON.stringify(REVIEW_WITH_A_CHECK_RUNNING)}\n\`\`\``,
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
  const approveWhenGreen = page.getByTestId('offer');
  await expect(checkLine).toHaveText(
    '1 check is still running. Scout does not approve on its own; a person approves on GitHub.',
  );
  await expect(approveWhenGreen).toHaveText('Approve when the checks pass');
  await expect(page.getByTestId('offer-reason')).toHaveText(
    REVIEW_WITH_A_CHECK_RUNNING.offer.reason,
  );
  await expect(page.getByTestId('comment-instead')).toHaveCount(0);

  await approveWhenGreen.click();

  const waiting = page.getByTestId('waiting-line');
  await expect(waiting).toHaveText(
    'Waiting for the checks: 1 still running. Your approval is sent when they pass.',
  );
  await expect(waiting).toHaveAttribute('role', 'status');
  await expect(page.getByTestId('cancel-waiting')).toHaveAccessibleName(
    'Cancel the approval of acme/shop#5',
  );
  await expect(approveWhenGreen, 'the offered action gives way to the waiting line').toHaveCount(0);
  await expect(checkLine).toHaveCount(0);

  await page.getByTestId('cancel-waiting').click();

  await expect(waiting).toHaveCount(0);
  await expect(approveWhenGreen).toHaveText('Approve when the checks pass');
  await expect(checkLine).toHaveText(
    '1 check is still running. Scout does not approve on its own; a person approves on GitHub.',
  );
  await expect(page.getByTestId('offer-reason')).toHaveText(
    REVIEW_WITH_A_CHECK_RUNNING.offer.reason,
  );
});
