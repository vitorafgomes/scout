import { expect, test, useOnThisComputer } from '../fixtures/app.js';
import { SCOUT_DATA } from '../fixtures/loaded.js';

test.use({ scoutData: SCOUT_DATA });

test('when the control plane dies while Approvals is open, Refresh says it did not answer and that nothing was approved or rejected', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  await page.getByTestId('nav-review').click();
  await expect(page.getByTestId('pending-count')).toHaveText('2');

  desktop.killControlPlane();
  await page.getByTestId('review-refresh').click();

  await expect(page.getByTestId('review-error')).toHaveText(
    'Could not load the approvals: the Scout control plane did not answer. Nothing was approved or rejected.',
    { timeout: 30_000 },
  );
});
