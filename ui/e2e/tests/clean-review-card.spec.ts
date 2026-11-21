import { ask, expect, test, useOnThisComputer } from '../fixtures/app.js';

const CLEAN_REVIEW = {
  repository: 'acme/shop',
  number: 5,
  commit: 'ec7f951',
  verdict: 'ready to merge',
  read: 'The whole diff and its tests.',
  findings: [],
  offer: {
    verdict: 'approve',
    reason:
      'Approve: every changed file was read, nothing needs fixing, the tests passed and every check passed.',
    commentInstead: false,
  },
};

test.use({
  script: {
    replies: [
      {
        complete: `Nothing to report on acme/shop#5.\n\n\`\`\`scout-findings\n${JSON.stringify(CLEAN_REVIEW)}\n\`\`\``,
      },
    ],
  },
});

test('a review that found nothing still shows its card: no finding card, the No findings line and the Approve the worker offers with its reason, and nothing else', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await ask(page, 'Review acme/shop#5');

  await expect(page.getByTestId('clean')).toHaveText('No findings.');
  await expect(page.getByTestId('clean-read')).toHaveText(
    'What Scout says it read: The whole diff and its tests.',
  );
  await expect(page.getByTestId('target')).toHaveText('acme/shop · PR #5 · commit ec7f951');
  await expect(page.getByTestId('offer')).toHaveText('Approve');
  await expect(page.getByTestId('offer-reason')).toHaveText(CLEAN_REVIEW.offer.reason);
  await expect(page.getByTestId('finding')).toHaveCount(0);
  await expect(page.getByTestId('post')).toHaveCount(0);
  await expect(page.getByTestId('comment-instead')).toHaveCount(0);
  await expect(page.getByTestId('all-dismissed')).toHaveCount(0);
  await expect(page.locator('pre code')).toHaveCount(0);
});
