import { expect, test, useOnThisComputer } from '../fixtures/app.js';
import { SCOUT_DATA } from '../fixtures/loaded.js';

test.use({ scoutData: SCOUT_DATA });

test('Approvals shows the two actions waiting with the count 2, each with Approve and Reject, and the three writes in the history, newest first', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await page.getByTestId('nav-review').click();

  await expect(page.getByTestId('pending-count')).toHaveText('2');
  const waiting = page
    .getByRole('list', { name: 'Waiting for your approval' })
    .getByRole('listitem');
  await expect(waiting).toContainText([
    'Upload "Sourdough starter in five days" as private, to be published on 2026-11-12.',
    'Reply "Thank you, the hydration is 75%." to a question on "Sourdough starter in five days".',
  ]);
  await expect(waiting.getByTestId('action-feature')).toHaveText(['YouTube', 'YouTube']);
  await expect(waiting).toContainText(['Upload a video', 'Publish a reply to a comment']);
  await expect(waiting.getByRole('button', { name: 'Approve' })).toHaveCount(2);
  await expect(waiting.getByRole('button', { name: 'Reject' })).toHaveCount(2);

  await page.getByTestId('tab-history').click();

  await expect(page.getByRole('row')).toContainText([
    'Outcome',
    'Rejected: the title is not final',
    'Done',
    'Saved script_ready',
  ]);
});

test('Rejecting an action says so in a notice and writes it in the history', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  await page.getByTestId('nav-review').click();
  const notice = page.locator('.sc-notice');

  await page.getByTestId('reject-1b2c3d4e5f6041829304b5c6d7e8f9a0').click();

  await expect(notice).toHaveText(
    /^\s*Rejected: Publish a reply to a comment \(comment:UgxExampleComment01\)\s*OK\s*$/,
  );
  await expect(page.getByTestId('pending-count')).toHaveText('1');
  await notice.getByRole('button', { name: 'OK' }).click();
  await expect(notice).toHaveCount(0, { timeout: 2000 });

  await page.getByTestId('tab-history').click();
  const newest = page.getByTestId('write-row').first().getByRole('cell');
  await expect(newest.nth(2)).toHaveText('Publish a reply to a comment');
  await expect(newest.nth(3)).toHaveText('comment:UgxExampleComment01');
  await expect(newest.nth(4)).toHaveText('Rejected');
  await expect(newest.nth(5)).toHaveText('You');
});
