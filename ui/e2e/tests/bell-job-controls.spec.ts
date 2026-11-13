import { ask, expect, test, useOnThisComputer, type Page } from '../fixtures/app.js';
import { SCOUT_DATA } from '../fixtures/loaded.js';

const TESTS_RUN = { event: 'progress', text: 'Run pull request tests: acme api 41' };

function notice(text: string): Readonly<Record<string, unknown>> {
  return { id: null, event: 'notice', text };
}

async function openInProgress(page: Page): Promise<void> {
  await page.getByTestId('bell').click();
  await page.getByTestId('filter-running').click();
}

test.describe('a test run the chat started', () => {
  test.use({
    scoutData: SCOUT_DATA,
    script: {
      replies: [
        {
          events: [TESTS_RUN],
          updates: ['Running the tests of acme/api#41 in a container.'],
          holdUntilFile: 'tests-stopped',
          endEvents: [
            notice('The tests of acme/api#41 ended at your request; no result was kept.'),
          ],
          complete: 'You stopped the tests of acme/api#41 before they finished.',
        },
      ],
    },
  });

  test('Stop in the bell reaches the control plane, says Stopping with the focus on it until the worker reports, then the run is in Done as stopped', async ({
    desktop,
  }) => {
    const { page } = await desktop.launch();
    await useOnThisComputer(page);
    await ask(page, 'Run the tests of acme/api#41');
    await openInProgress(page);
    const stop = page.getByRole('button', { name: 'Stop the tests for acme/api#41' });

    await stop.click();

    const status = page.getByTestId('entry-status');
    await expect(status).toHaveText('Stopping');
    await expect(status).toBeFocused();
    await expect(stop).toHaveCount(0);
    await desktop.release('tests-stopped');
    await page.getByTestId('filter-done').click();
    await expect(
      page.getByTestId('entry').filter({ hasText: 'Tests for acme/api#41 stopped' }),
    ).toContainText('The tests gave no result.');
    await expect(page.getByTestId('entry-status')).toHaveCount(0);
  });
});

test.describe('an audit the control plane cannot name', () => {
  test.use({
    scoutData: SCOUT_DATA,
    script: {
      replies: [
        {
          events: [notice('Audit of ./.., src (1 of 2):')],
          updates: ['Auditing.'],
          holdUntilCancel: true,
        },
      ],
    },
  });

  test('Pause is refused by the control plane, whose reason shows on the entry, and the controls come back', async ({
    desktop,
  }) => {
    const { page } = await desktop.launch();
    await useOnThisComputer(page);
    await ask(page, 'Audit the repository');
    await openInProgress(page);

    await page.getByRole('button', { name: 'Pause the audit of ./..' }).click();

    await expect(page.getByTestId('entry-note')).toHaveText(
      'The repository must be given as owner and repo, each made of letters, digits, dots, hyphens or underscores.',
    );
    await expect(page.getByRole('button', { name: 'Pause the audit of ./..' })).toBeEnabled();
    await expect(page.getByTestId('entry-status')).toHaveCount(0);
    await expect(page.getByTestId('entry').filter({ hasText: 'Audit of ./..' })).toHaveAttribute(
      'data-kind',
      'running',
    );
  });
});

test.describe('a test run while the control plane is down', () => {
  test.use({
    scoutData: SCOUT_DATA,
    script: { replies: [{ events: [TESTS_RUN], updates: ['Running.'], holdUntilCancel: true }] },
  });

  test('Stop says the control plane did not answer and offers Stop again, never Stopping', async ({
    desktop,
  }) => {
    const { page } = await desktop.launch();
    await useOnThisComputer(page);
    await ask(page, 'Run the tests of acme/api#41');
    await openInProgress(page);
    await expect(
      page.getByRole('button', { name: 'Stop the tests for acme/api#41' }),
    ).toBeVisible();
    desktop.killControlPlane();

    await page.getByRole('button', { name: 'Stop the tests for acme/api#41' }).click();

    await expect(page.getByTestId('entry-note')).toHaveText(
      'The Scout control plane did not answer, so this click may not have reached Scout. Try again.',
      { timeout: 30_000 },
    );
    await expect(
      page.getByRole('button', { name: 'Stop the tests for acme/api#41' }),
    ).toBeVisible();
    await expect(page.getByTestId('entry-status')).toHaveCount(0);
  });
});

test.describe('an audit in progress', () => {
  test.use({
    scoutData: SCOUT_DATA,
    script: {
      replies: [
        {
          events: [notice('Audit of acme/shop, src/orders (3 of 8):')],
          updates: ['Auditing.'],
          holdUntilCancel: true,
        },
      ],
    },
  });

  test('its entry shows A, the areas done and the share of them rounded down, with Pause and Stop', async ({
    desktop,
  }) => {
    const { page } = await desktop.launch();
    await useOnThisComputer(page);
    await ask(page, 'Audit acme/shop');
    await openInProgress(page);

    const entry = page.getByTestId('entry').filter({ hasText: 'Audit of acme/shop' });
    await expect(entry.locator('.icon')).toHaveText('A');
    await expect(entry.getByTestId('entry-detail')).toHaveText('Area 3 of 8 done: src/orders');
    await expect(entry.getByTestId('entry-progress')).toHaveAttribute('aria-valuenow', '37');
    await expect(entry.getByRole('button', { name: 'Pause the audit of acme/shop' })).toBeVisible();
    await expect(entry.getByRole('button', { name: 'Stop the audit of acme/shop' })).toBeVisible();
  });
});

test.describe('an audit you paused', () => {
  test.use({
    scoutData: SCOUT_DATA,
    script: {
      replies: [
        {
          events: [
            notice('Audit of acme/shop, src/orders (3 of 8):'),
            notice(
              'The audit of acme/shop paused: you paused it. Ask Scout to continue the audit of acme/shop to resume it from the first area not done.',
            ),
          ],
          updates: ['Paused.'],
          holdUntilCancel: true,
        },
      ],
    },
  });

  test('Stop says Stopping, not after this area, because no area is in progress', async ({
    desktop,
  }) => {
    const { page } = await desktop.launch();
    await useOnThisComputer(page);
    await ask(page, 'Audit acme/shop');
    await openInProgress(page);
    const entry = page.getByTestId('entry').filter({ hasText: 'Audit of acme/shop paused' });
    await expect(entry.getByTestId('entry-detail')).toHaveText('Paused by you · area 3 of 8 done');

    await entry.getByRole('button', { name: 'Stop the audit of acme/shop' }).click();

    await expect(entry.getByTestId('entry-status')).toHaveText('Stopping');
    await expect(
      entry.getByRole('button', { name: 'Continue the audit of acme/shop' }),
    ).toHaveCount(0);
  });
});
