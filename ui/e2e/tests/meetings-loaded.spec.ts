import { expect, test, useOnThisComputer } from '../fixtures/app.js';
import { KEPT_MEETINGS } from '../fixtures/loaded.js';

test.use({ script: { replies: [], meetings: KEPT_MEETINGS } });

test('Meetings lists the three kept meetings, newest first, each with its length, lines and questions', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await page.getByTestId('nav-meetings').click();

  const rows = page.getByTestId('meetings-list').getByRole('listitem');
  await expect(rows.getByTestId('meeting-title')).toHaveText([
    'Quick sync',
    'Supplier call',
    'Weekly planning',
  ]);
  await expect(rows.getByTestId('meeting-meta')).toContainText([
    'under a minute · 1 line · 0 questions',
    '20 min · 48 lines · 1 question',
    '45 min · 120 lines · 3 questions',
  ]);
  await expect(rows.getByRole('button', { name: 'Export as Markdown' })).toHaveCount(3);
  await expect(page.getByTestId('delete-all')).toBeVisible();
});
