import { ask, expect, test, useOnThisComputer } from '../fixtures/app.js';

test.use({
  script: { replies: [{ updates: ['Half of the answer'], holdUntilCancel: true }] },
});

test('Stop while the reply streams keeps its text, marks it Interrupted and leaves no stream cursor', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  await ask(page, 'Explain the last deployment');
  const reply = page.getByTestId('reply');
  await expect(reply).toHaveText('Half of the answer▌');

  await page.getByRole('button', { name: 'Stop the answer' }).click();

  await expect(page.getByTestId('failure')).toHaveText('Interrupted');
  await expect(reply).toHaveText('Half of the answer');
  await expect(page.getByTestId('stop')).toBeHidden();
  await expect(page.getByTestId('send')).toBeEnabled();
});
