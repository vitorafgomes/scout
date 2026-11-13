import { expect, test, useOnThisComputer } from '../fixtures/app.js';

test.use({
  ollama: {
    tags: [
      {
        name: 'nomic-embed-text:latest',
        model: 'nomic-embed-text:latest',
        modified_at: '2026-11-01T00:00:00Z',
        size: 274_302_450,
        digest: 'b1c2d3e4f5a6',
        details: { format: 'gguf', family: 'nomic-bert', parameter_size: '137M' },
      },
    ],
    capabilities: { 'nomic-embed-text:latest': ['embedding'] },
    missing: ['sample-missing:1b'],
  },
});

test('a typed name is pulled through the local server only and appears under Installed, and a name the library lacks says so', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  await page.getByTestId('nav-sources').click();
  await page.getByTestId('manage-ollama-models').click();
  const dialog = page.getByRole('dialog');
  const field = dialog.getByLabel('Any model of the Ollama library, as name or name:tag');

  await field.fill('sample-missing:1b');
  await dialog.getByTestId('pull-submit').click();
  await expect(dialog.getByTestId('pull-error-reason')).toHaveText(
    'The Ollama library has no model named sample-missing:1b.',
  );
  await expect(field).toHaveValue('sample-missing:1b');

  await field.fill('sample-pulled:1b');
  await field.press('Enter');

  await expect(dialog.getByTestId('row-sample-pulled:1b')).toBeVisible();
  await expect(dialog.getByTestId('pull-done')).toHaveText(
    'Pulled sample-pulled:1b. It is under Installed: Use this makes it the model Scout uses.',
  );
  await expect(field).toHaveValue('');
  await expect(dialog.getByTestId('metric-selected')).toHaveText('—');
});
