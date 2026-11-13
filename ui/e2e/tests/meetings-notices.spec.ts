import { expect, test, useOnThisComputer } from '../fixtures/app.js';
import { KEPT_MEETINGS } from '../fixtures/loaded.js';

test.use({ script: { replies: [], meetings: KEPT_MEETINGS } });

test('deleting a meeting says Meeting deleted, and Delete all says All meetings deleted over the empty state', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  await page.getByTestId('nav-meetings').click();
  const rows = page.getByTestId('meetings-list').getByRole('listitem');
  const notice = page.locator('.sc-notice');

  await rows.filter({ hasText: 'Quick sync' }).getByTestId('delete').click();
  await page.getByTestId('confirm-ok').click();

  await expect(notice).toHaveText(/^\s*Meeting deleted\s*OK\s*$/);
  await expect(rows.getByTestId('meeting-title')).toHaveText(['Supplier call', 'Weekly planning']);
  await notice.getByRole('button', { name: 'OK' }).click();
  await expect(notice).toHaveCount(0, { timeout: 2000 });

  await page.getByTestId('delete-all').click();
  await page.getByTestId('confirm-ok').click();

  await expect(notice).toHaveText(/^\s*All meetings deleted\s*OK\s*$/);
  await expect(page.getByTestId('meetings-empty')).toBeVisible();
  await expect(page.getByTestId('delete-all')).toHaveCount(0);
  const deletes = (await desktop.received()).filter((line) => line['type'] === 'delete');
  expect(deletes.map(({ meetingId, all }) => ({ meetingId, all }))).toEqual([
    { meetingId: 'kept-meeting-sync-0003', all: undefined },
    { meetingId: undefined, all: true },
  ]);
});
