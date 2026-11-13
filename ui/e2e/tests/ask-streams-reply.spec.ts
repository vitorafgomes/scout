import { ask, expect, test, useOnThisComputer } from '../fixtures/app.js';

test.use({
  script: {
    replies: [
      {
        updates: ['You have ', 'two open ', 'pull requests'],
        holdUntilFile: 'finish',
        complete: 'You have two open pull requests.',
      },
    ],
  },
});

test('a question shows under You and the streamed reply under Scout, with no stream cursor at the end', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await ask(page, 'How many pull requests are open?');

  await expect(page.getByTestId('who-user')).toContainText('You');
  await expect(page.getByTestId('question')).toHaveText('How many pull requests are open?');
  const reply = page.getByTestId('reply');
  await expect(reply).toHaveText('You have two open pull requests▌');

  await desktop.release('finish');

  await expect(page.getByTestId('who-scout')).toContainText('Scout');
  await expect(reply).toHaveText('You have two open pull requests.');
  await expect(page.getByTestId('stop')).toBeHidden();
  await expect(page.getByTestId('send')).toBeEnabled();
  await expect(page.getByRole('textbox', { name: 'Ask Scout' })).toHaveValue('');
});
