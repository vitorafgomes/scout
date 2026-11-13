import { ask, expect, test, useOnThisComputer } from '../fixtures/app.js';

const lines = Array.from({ length: 200 }, (_, index) => `Line ${index + 1} of the answer.`);
const parts = Array.from({ length: 20 }, (_, index) =>
  lines.slice(index * 10, index * 10 + 10).join('\n\n'),
).map((part, index) => (index === 0 ? part : `\n\n${part}`));

test.use({
  script: { replies: [{ updates: parts, delayMs: 30, complete: lines.join('\n\n') }] },
});

test('a long streamed reply keeps the conversation scrolled to its last line', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await ask(page, 'List every service and its owner');

  await expect(page.getByTestId('stop')).toBeHidden();
  await expect(page.getByTestId('send')).toBeEnabled();
  await expect(page.getByText('Line 200 of the answer.')).toBeInViewport();
  await expect(page.getByText('Line 1 of the answer.', { exact: true })).not.toBeInViewport();
  const turns = page.getByTestId('turns');
  await expect
    .poll(() =>
      turns.evaluate((list) => ({
        overflows: list.scrollHeight > list.clientHeight * 2,
        atBottom: list.scrollHeight - list.scrollTop - list.clientHeight <= 1,
      })),
    )
    .toEqual({ overflows: true, atBottom: true });
});
