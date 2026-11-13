import { expect, test, type Page } from '../fixtures/app.js';

test('Back to Scout brings the hidden Scout window back and leaves the operator window open', async ({
  desktop,
}) => {
  const { app, page } = await desktop.launch();
  await expect(page.getByTestId('use-local')).toBeVisible();

  await page.evaluate(() =>
    (
      window as unknown as { scout: { operatorOpenWindow(): Promise<void> } }
    ).scout.operatorOpenWindow(),
  );
  const find = (): Page | undefined =>
    app.windows().find((window) => window.url().includes('#/operator'));
  await expect.poll(() => find() !== undefined, { timeout: 30_000 }).toBe(true);
  const operator = find()!;
  const back = operator.getByRole('button', { name: 'Back to Scout' });
  await expect(back).toBeVisible();

  const main = await app.browserWindow(page);
  await main.evaluate((window) => window.hide());
  await expect.poll(() => main.evaluate((window) => window.isVisible())).toBe(false);

  await back.click();

  await expect
    .poll(() => main.evaluate((window) => window.isVisible() && !window.isMinimized()))
    .toBe(true);
  const operatorWindow = await app.browserWindow(operator);
  expect(await operatorWindow.evaluate((window) => window.isVisible())).toBe(true);
});
