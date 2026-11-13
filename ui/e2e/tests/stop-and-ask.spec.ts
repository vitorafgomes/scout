import { ask, expect, test, useOnThisComputer } from '../fixtures/app.js';

test.use({
  script: {
    replies: [
      { updates: ['Opening issues for everything'], holdUntilCancel: true },
      { complete: 'Pull request 5 reviewed.' },
    ],
  },
});

test('a new question while Scout answers stops that answer and is asked instead', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  await ask(page, 'Analyze the project');
  await expect(page.getByTestId('reply')).toHaveText('Opening issues for everything▌');
  await expect(page.getByTestId('send')).toBeEnabled();

  await ask(page, 'Stop and review pull request 5');

  await expect(page.getByTestId('question')).toHaveText([
    'Analyze the project',
    'Stop and review pull request 5',
  ]);
  await expect(page.getByTestId('failure')).toHaveText('Interrupted');
  await expect(page.getByTestId('reply')).toHaveText([
    'Opening issues for everything',
    'Pull request 5 reviewed.',
  ]);
  await expect(page.getByTestId('stop')).toBeHidden();
  const received = await desktop.received();
  const first = received.find((line) => line['type'] === 'ask')?.['id'];
  expect(
    received
      .filter((line) => line['type'] === 'ask' || line['type'] === 'cancel')
      .map((line) => (line['type'] === 'ask' ? line['text'] : `cancel ${String(line['id'])}`)),
  ).toEqual(['Analyze the project', `cancel ${String(first)}`, 'Stop and review pull request 5']);
});

test('Clear, in the top bar on the Chat screen, while Scout answers stops that answer, empties the conversation, and the next question is answered', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  const clear = page.getByTestId('top-bar').getByTestId('clear');
  await expect(clear).toBeDisabled();
  await ask(page, 'Analyze the project');
  await expect(page.getByTestId('reply')).toHaveText('Opening issues for everything▌');
  await expect(clear).toBeEnabled();

  await clear.click();

  await expect(page.getByTestId('question')).toHaveCount(0);
  await expect(page.getByTestId('stop')).toBeHidden();
  await expect(clear).toBeDisabled();
  await ask(page, 'Review pull request 5');
  await expect(page.getByTestId('reply')).toHaveText(['Pull request 5 reviewed.']);
  const received = await desktop.received();
  const first = received.find((line) => line['type'] === 'ask')?.['id'];
  expect(
    received
      .filter((line) => ['ask', 'cancel', 'reset'].includes(String(line['type'])))
      .map((line) =>
        line['type'] === 'ask'
          ? line['text']
          : line['type'] === 'cancel'
            ? `cancel ${String(line['id'])}`
            : 'reset',
      ),
  ).toEqual(['Analyze the project', `cancel ${String(first)}`, 'reset', 'Review pull request 5']);
});
