import { expect, test, useOnThisComputer } from '../fixtures/app.js';
import { SCOUT_DATA } from '../fixtures/loaded.js';

test.use({ scoutData: SCOUT_DATA });

test('Production shows the saved mandate and the two projects, newest first, each with its status', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await page.evaluate(() => (location.hash = '#/operator/production'));

  await expect(page.getByTestId('channel-summary')).toContainText('Mandate saved 2 Nov,');
  await expect(page.getByTestId('channel-summary')).toContainText(
    'Default format: Generated pictures',
  );
  await expect(page.getByTestId('projects').getByRole('listitem')).toHaveText([
    /No-knead bread for a weeknight\s*Queued/,
    /Sourdough starter in five days\s*Script ready for review/,
  ]);
  await expect(page.getByTestId('production-error')).toHaveCount(0);

  await page.getByTestId('channel').locator('summary').click();

  await expect(page.getByTestId('mandate-objective')).toHaveValue(
    'Teach home bakers to make bread they are proud of.',
  );
  await expect(page.getByTestId('mandate-audience')).toHaveValue(
    'Beginners who bake at the weekend',
  );
});
