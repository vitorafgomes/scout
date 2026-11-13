import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';

import { expect, test, useOnThisComputer } from '../fixtures/app.js';

const DESIGN_FONT = 'Inter, -apple-system, "Segoe UI", sans-serif';

test('the app opens on the shell with Scout running, the Chat screen empty, the top bar in the design font and no window-height scrollbar', async ({
  desktop,
}) => {
  const first = await desktop.launch();
  await useOnThisComputer(first.page);
  await first.app.close();

  const { page } = await desktop.launch();

  await expect(page.getByTestId('nav-chat')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('top-bar-title')).toHaveText('Chat');
  await expect(page.getByTestId('top-bar-title')).toHaveCSS('font-family', DESIGN_FONT);
  await expect(page.getByTestId('top-bar-source')).toHaveCSS('font-family', DESIGN_FONT);
  await expect(page.getByTestId('empty-text')).toHaveText(
    'Ask Scout anything, or hold the microphone button to talk.',
  );

  await page.getByTestId('nav-settings').click();
  await expect(page.getByTestId('worker-state')).toHaveText('Running');
  expect(
    await page.evaluate(() => {
      const root = document.documentElement;
      return [root.scrollHeight - root.clientHeight, window.innerWidth - root.clientWidth];
    }),
  ).toEqual([0, 0]);
});

test('with the control plane host missing, Approvals and Production say the control plane did not answer and show no spawn failure', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await page.getByTestId('nav-review').click();

  await expect(page.getByTestId('review-error')).toHaveText(
    'Could not load the approvals: the Scout control plane did not answer. Nothing was approved or rejected.',
  );
  await expect(page.locator('body')).not.toContainText(/ENOENT|spawn|\/tmp\//);

  await page.evaluate(() => (location.hash = '#/operator/production'));

  await expect(page.getByTestId('production-error')).toHaveText(
    'The Scout control plane did not answer.',
  );
  await expect(page.getByTestId('mandate-error')).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText(/ENOENT|spawn|\/tmp\//);
});

test('Inter is loaded from the app bundle for the top bar and the bell, never a fallback, and its licence ships beside the font file', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);
  await page.getByTestId('bell').click();
  await expect(page.getByTestId('panel')).toBeVisible();
  await page.evaluate(() => document.fonts.ready);

  const fonts = await page.evaluate(() => ({
    faces: Array.from(document.fonts, (face) => [face.family, face.status]),
    firstFamily: ['top-bar-title', 'top-bar-source', 'panel'].map((id) =>
      getComputedStyle(document.querySelector(`[data-test="${id}"]`)!)
        .fontFamily.split(',')[0]
        ?.trim(),
    ),
    sources: Array.from(document.styleSheets)
      .flatMap((sheet) => Array.from(sheet.cssRules))
      .filter((rule) => rule instanceof CSSFontFaceRule)
      .map((rule) => rule.style.getPropertyValue('src')),
  }));
  const media = path.resolve(
    import.meta.dirname,
    '..',
    '..',
    'packages',
    'renderer',
    'dist',
    'browser',
    'media',
  );

  expect(fonts).toEqual({
    faces: [['Inter', 'loaded']],
    firstFamily: ['Inter', 'Inter', 'Inter'],
    sources: [
      expect.stringMatching(
        /^url\("\.\/media\/inter-latin-wght-normal-[^/"]+\.woff2"\) format\("woff2"\)$/,
      ),
    ],
  });
  expect((await readdir(media)).filter((name) => name.endsWith('.woff2'))).toEqual([
    expect.stringMatching(/^inter-latin-wght-normal-.+\.woff2$/),
  ]);
  expect(await readFile(path.join(media, 'inter', 'LICENSE'), 'utf8')).toContain(
    'SIL OPEN FONT LICENSE Version 1.1',
  );
});
