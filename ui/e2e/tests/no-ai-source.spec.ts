import { expect, test, useOnThisComputer, type Page } from '../fixtures/app.js';

async function chooseProvider(page: Page, provider: string): Promise<void> {
  await page.evaluate(
    (value) =>
      (
        window as unknown as {
          scout: { vaultStore(req: { key: string; value: string }): Promise<void> };
        }
      ).scout.vaultStore({ key: 'llm.provider', value }),
    provider,
  );
  await page.reload();
}

test('with no AI Source connected the field, the microphone, the voice conversation and Send are disabled', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  const field = page.getByRole('textbox', { name: 'Ask Scout' });
  await chooseProvider(page, 'Ollama');
  await expect(page.getByTestId('top-bar-source')).toHaveText('Ollama');
  await expect(field).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Start a voice conversation' })).toBeEnabled();

  await chooseProvider(page, 'None');

  await expect(page.getByTestId('top-bar-source')).toHaveText('No AI Source');
  await expect(field).toBeDisabled();
  await expect(field).toHaveAttribute('placeholder', 'Connect an AI Source first');
  await expect(page.getByRole('button', { name: 'Hold to talk' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Start a voice conversation' })).toBeDisabled();
  await expect(
    page.getByRole('button', { name: 'Start a voice conversation' }),
  ).toHaveAccessibleDescription('Connect an AI Source first');
  await expect(page.getByRole('button', { name: 'Send' })).toBeDisabled();
});
