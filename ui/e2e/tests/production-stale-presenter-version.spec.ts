import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { expect, test, useOnThisComputer, type Page } from '../fixtures/app.js';

const PROJECT = '7b5e1c3a9d2f4e6a8b0c1d2e3f4a5b6c';
const STEP_MS = 50;
const POLL_MS = 10_000;

interface StoredProject {
  presenterVersions: { file: string }[];
}

test.use({ scoutData: path.join(import.meta.dirname, '..', 'fixtures', 'presenter-data') });

/** Moves the page's paused clock forward in small steps until `ready` holds, and answers the time it moved. */
async function advanceUntil(page: Page, ready: () => Promise<boolean>): Promise<number> {
  let advanced = 0;
  await expect
    .poll(
      async () => {
        await page.clock.runFor(STEP_MS);
        advanced += STEP_MS;
        return ready();
      },
      { timeout: 20_000, intervals: [50] },
    )
    .toBe(true);
  return advanced;
}

test('Remove on a presenter version that is no longer there says the list was refreshed, shows the versions left and removes nothing', async ({
  desktop,
}) => {
  const { app, page } = await desktop.launch();
  await useOnThisComputer(page);
  const projectsFile = path.join(
    await app.evaluate(() => process.env['XDG_DATA_HOME'] ?? ''),
    'Scout',
    'projects.json',
  );
  const versions = page.getByTestId('presenter-versions').getByRole('listitem');
  await page.clock.install();
  await page.clock.pauseAt(Date.now() + STEP_MS);

  await page.evaluate(() => (location.hash = '#/operator/production'));
  let advanced = await advanceUntil(page, () => page.getByTestId(`project-${PROJECT}`).isVisible());
  await page.getByTestId(`project-${PROJECT}`).click();
  advanced += await advanceUntil(page, async () => (await versions.count()) === 3);
  const stored = JSON.parse(await readFile(projectsFile, 'utf8')) as StoredProject[];
  stored[0]!.presenterVersions.shift();
  await writeFile(projectsFile, JSON.stringify(stored, null, 2));
  expect(advanced, 'the screen has not polled since it opened').toBeLessThan(POLL_MS);
  await page.getByTestId('presenter-version-remove-0').click();
  advanced += await advanceUntil(page, () => page.getByTestId('presenter-error').isVisible());

  expect(advanced, 'the screen did not poll before the answer').toBeLessThan(POLL_MS);
  expect(await page.getByTestId('presenter-error').textContent()).toBe(
    'That version is no longer there; the list was refreshed.',
  );
  expect(await versions.count()).toBe(2);
  const left = JSON.parse(await readFile(projectsFile, 'utf8')) as StoredProject[];
  expect(left[0]!.presenterVersions.map((v) => v.file)).toEqual([
    'presenter-0002.png',
    'presenter-0003.png',
  ]);
});
