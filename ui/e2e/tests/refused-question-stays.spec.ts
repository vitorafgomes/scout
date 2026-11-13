import { ask, expect, test, useOnThisComputer } from '../fixtures/app.js';

test.use({ script: { replies: [{ complete: 'Green since this morning.' }] } });

test('a question main refuses for being over 4000 characters stays in the field and never reaches Scout', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  await ask(page, 'Is the build green?');
  await expect(page.getByTestId('reply')).toHaveText('Green since this morning.');
  const question = 'Why is the build red? '.repeat(200).slice(0, 4001);

  await ask(page, question);

  await expect(page.getByRole('alert')).toHaveText('Type a question of 1 to 4000 characters.');
  await expect(page.getByRole('textbox', { name: 'Ask Scout' })).toHaveValue(question);
  await expect(page.getByTestId('question')).toHaveText(['Is the build green?']);
  const asked = (await desktop.received()).filter((line) => line['type'] === 'ask');
  expect(asked.map((line) => line['text'])).toEqual(['Is the build green?']);
});
