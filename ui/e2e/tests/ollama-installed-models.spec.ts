import { expect, test, useOnThisComputer } from '../fixtures/app.js';

test.use({
  ollama: {
    tags: [
      {
        name: 'sample-chat:3b',
        model: 'sample-chat:3b',
        modified_at: '2026-11-01T00:00:00Z',
        size: 2_019_393_189,
        digest: 'a80c4f17acd5',
        details: {
          format: 'gguf',
          family: 'llama',
          parameter_size: '3.2B',
          quantization_level: 'Q4_K_M',
        },
      },
      {
        name: 'sample-chat:1b',
        model: 'sample-chat:1b',
        modified_at: '2026-11-01T00:00:00Z',
        size: 838_860_800,
        digest: 'b91d5e2f7a03',
        details: {
          format: 'gguf',
          family: 'example',
          parameter_size: '1.2B',
          quantization_level: 'Q4_K_M',
        },
      },
    ],
    capabilities: {
      'sample-chat:3b': ['completion', 'tools'],
      'sample-chat:1b': ['completion'],
    },
  },
});

test('a model installed outside Scout is listed in Local models in Ollama and Use this puts it In use', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  await page.getByTestId('nav-sources').click();

  await page.getByTestId('manage-ollama-models').click();
  const dialog = page.getByRole('dialog');
  const row = dialog.getByTestId('row-sample-chat:3b');
  await expect(dialog.getByTestId('installed-models').locator('> li')).toHaveCount(2);
  await expect(row.locator('.model-meta')).toHaveText('llama · 3.2B · Q4_K_M · 1.9 GB on disk');
  await expect(dialog.getByTestId('metric-selected')).toHaveText('—');

  await row.getByTestId('select-sample-chat:3b').click();

  await expect(row.getByTestId('check-sample-chat:3b')).toHaveText('In use');
  await expect(dialog.getByTestId('metric-selected')).toHaveText('sample-chat:3b');
  await expect(row.getByTestId('remove-sample-chat:3b')).toBeDisabled();
});

test('a model whose capabilities lack tools is marked No tools, says why under its row, and can still be put In use', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  await page.getByTestId('nav-sources').click();

  await page.getByTestId('manage-ollama-models').click();
  const dialog = page.getByRole('dialog');
  const noTools = dialog.getByTestId('row-sample-chat:1b');
  const sentence =
    'This model cannot call tools, so Scout cannot read your repositories or cluster with it. Choose a model that supports tools.';
  await expect(noTools.getByTestId('no-tools-sample-chat:1b')).toHaveText('No tools');
  await expect(noTools.getByTestId('no-tools-note-sample-chat:1b')).toHaveText(sentence);
  await expect(dialog.getByTestId('no-tools-sample-chat:3b')).toHaveCount(0);
  await expect(dialog.getByTestId('in-use-no-tools')).toHaveCount(0);

  const useThis = noTools.getByTestId('select-sample-chat:1b');
  await expect(useThis).toHaveAccessibleDescription(sentence);
  await useThis.click();

  await expect(noTools.getByTestId('check-sample-chat:1b')).toHaveText('In use');
  await expect(dialog.getByTestId('in-use-no-tools')).toHaveText(sentence);
});
