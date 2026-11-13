import { ask, expect, test, useOnThisComputer } from '../fixtures/app.js';

const CLEAN_REVIEW = {
  repository: 'acme/shop',
  number: 5,
  commit: 'ec7f951',
  verdict: 'ready to merge',
  read: 'The whole diff and its tests.',
  findings: [],
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

test('a review that found nothing still shows its card: no finding card, the No findings line and Approve; no Post and no Request changes', async ({
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
  await expect(page.getByTestId('approve')).toBeVisible();
  await expect(page.getByTestId('finding')).toHaveCount(0);
  await expect(page.getByTestId('post')).toHaveCount(0);
  await expect(page.getByTestId('request-changes')).toHaveCount(0);
  await expect(page.getByTestId('all-dismissed')).toHaveCount(0);
  await expect(page.locator('pre code')).toHaveCount(0);
});
