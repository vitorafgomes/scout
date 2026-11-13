import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

import { expect, test, useOnThisComputer } from '../fixtures/app.js';
import { SCOUT_DATA } from '../fixtures/loaded.js';

test.use({ scoutData: SCOUT_DATA });

test('Features shows a card per feature with its state, YouTube with writes allowed since the day they were granted and GitHub with writes not allowed', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await page.getByTestId('nav-features').click();

  await expect(page.getByTestId('features-grid').getByRole('heading', { level: 2 })).toHaveText([
    'GitHub',
    'Docker',
    'Kubernetes',
    'YouTube',
    'Desktop',
  ]);
  await expect(page.getByTestId('badge-github')).toHaveText('Not connected');
  await expect(page.getByTestId('badge-kubernetes')).toHaveText('Not connected');
  await expect(page.getByTestId('badge-youtube')).toHaveText('Not connected');
  await expect(page.getByTestId('badge-desktop')).toHaveText('On');
  await expect(page.getByTestId('perms-youtube')).toContainText('Cannot be undone');

  await page.getByTestId('details-youtube').click();
  await expect(page.getByTestId('grant-youtube')).toHaveText(
    'Write permission: granted on 2026-11-01',
  );
  await page.getByTestId('feature-panel-close').click();

  await page.getByTestId('details-github').click();
  await expect(page.getByTestId('grant-github')).toHaveText('Write permission: not granted');
});

test("the Kubernetes card shows Read 4 and its panel lists the control plane's four reads as the artboard draws them, with no grant box, and shows the contexts it found open with no click", async ({
  desktop,
}) => {
  const { app, page } = await desktop.launch();
  const home = await app.evaluate(() => process.env['HOME'] ?? '');
  expect(home).toContain('scout-e2e-');
  await mkdir(path.join(home, '.kube'), { recursive: true });
  await writeFile(
    path.join(home, '.kube', 'config'),
    [
      'apiVersion: v1',
      'kind: Config',
      'clusters:',
      '  - name: shop-prod',
      '    cluster: { server: "https://shop-prod.example.test" }',
      '  - name: staging',
      '    cluster: { server: "https://staging.example.test" }',
      'users:',
      '  - name: reader',
      '    user: { tokenFile: reader-token }',
      'contexts:',
      '  - name: shop-prod',
      '    context: { cluster: shop-prod, user: reader }',
      '  - name: staging',
      '    context: { cluster: staging, user: reader }',
      'current-context: staging',
      '',
    ].join('\n'),
  );
  await useOnThisComputer(page);

  await page.getByTestId('nav-features').click();

  await expect(page.getByTestId('perms-kubernetes').getByRole('listitem')).toHaveText(['Read 4']);

  await page.getByTestId('details-kubernetes').click();
  const reads = page.getByTestId('reads-kubernetes');
  await expect(page.getByTestId('reads-summary-kubernetes')).toHaveText('Reads 4');
  await expect(reads.locator('.label')).toHaveText(['Namespaces', 'Pods', 'Deployments', 'Events']);
  await expect(reads.locator('.what')).toHaveText([
    'List the namespaces of each context you chose',
    'Read pod status and restarts; never logs or secrets',
    'Read replicas and rollout status',
    'Read recent events',
  ]);
  await expect(page.getByTestId('permissions-kubernetes')).toHaveCount(0);
  await expect(page.getByTestId('list-toggle-kubernetes-contexts')).toHaveAttribute(
    'aria-expanded',
    'true',
  );
  await expect(page.getByTestId('list-row-kubernetes-contexts')).toHaveText([
    'shop-prod · https://shop-prod.example.test · token',
    'staging · https://staging.example.test · token',
  ]);
});

for (const panel of [
  {
    id: 'github',
    chips: ['Read 5', 'Write 3', 'Cannot be undone 5'],
    reads: [
      'Reads 5',
      ['Metadata', 'List the repositories the Scout GitHub App is installed on'],
      [
        'Notifications',
        "Search every five minutes for your mentions, review requests and replies; read the pull requests that mention your Scout bot and the bot's own name",
      ],
    ],
    multi: [
      'Contents',
      'Read files, directories and languages; list and search every file to audit a project',
    ],
    permissions: [
      'Permissions 8',
      [
        "Scout's data folder",
        'Write',
        'Keep conventions profiles, repository snapshots and audit jobs on this computer',
      ],
      [
        'Scout bot on GitHub (optional)',
        'Write',
        "Sign what you switched to the bot with its token, on the repositories you installed it on; you create the bot on GitHub's page and its key stays on this computer",
      ],
    ],
  },
  {
    id: 'youtube',
    chips: ['Read 8', 'Write 2', 'Cannot be undone 6'],
    reads: [
      'Reads 8',
      ['Channel overview', 'Title, handle, subscribers, total views and video count'],
      [
        'Free-licence pictures',
        'Find a CC0, public domain or CC BY picture for a scene on Openverse and download it',
      ],
    ],
    multi: null,
    permissions: [
      'Permissions 8',
      [
        "Scout's data folder",
        'Write',
        'Save scripts, narration, images, the scene list, the video and thumbnails, and the recording you attach with its transcript',
      ],
      ['Moderate a comment', 'Cannot be undone', 'Hold a comment for your review on YouTube'],
    ],
  },
  {
    id: 'desktop',
    chips: ['Read 3'],
    reads: [
      'Reads 3',
      ['Screen', 'One screenshot you attach to a question, and the text read from it'],
      ['System audio', "The computer's sound while you keep System audio on or record a meeting"],
    ],
    multi: ['Microphone', 'Your voice while you hold to talk, listen or record a meeting'],
    permissions: null,
  },
] as const) {
  test(`the ${panel.id} card's chips and its panel's first and last Reads and Permissions rows are the control plane's catalog in the artboard's words, one sentence per row`, async ({
    desktop,
  }) => {
    const { page } = await desktop.launch();
    await useOnThisComputer(page);

    await page.getByTestId('nav-features').click();

    await expect(page.getByTestId(`perms-${panel.id}`).getByRole('listitem')).toHaveText([
      ...panel.chips,
    ]);

    await page.getByTestId(`details-${panel.id}`).click();
    const [readsHeading, firstRead, lastRead] = panel.reads;
    const reads = page.getByTestId(`reads-${panel.id}`).getByRole('listitem');
    await expect(page.getByTestId(`reads-summary-${panel.id}`)).toHaveText(readsHeading);
    await expect(reads.first().locator('.label')).toHaveText(firstRead[0]);
    await expect(reads.first().locator('.what')).toHaveText(firstRead[1]);
    await expect(reads.last().locator('.label')).toHaveText(lastRead[0]);
    await expect(reads.last().locator('.what')).toHaveText(lastRead[1]);
    if (panel.multi !== null) {
      await expect(page.getByTestId(`what-${panel.multi[0]}`)).toHaveText(panel.multi[1]);
    }
    if (panel.permissions === null) {
      await expect(page.getByTestId(`permissions-${panel.id}`)).toHaveCount(0);
      return;
    }
    const [permissionsHeading, firstWrite, lastWrite] = panel.permissions;
    const writes = page.getByTestId(`permissions-${panel.id}`).getByRole('listitem');
    await expect(page.getByTestId(`permissions-summary-${panel.id}`)).toHaveText(
      permissionsHeading,
    );
    for (const [row, [label, access, what]] of [
      [writes.first(), firstWrite],
      [writes.last(), lastWrite],
    ] as const) {
      await expect(row.locator('.label')).toHaveText(label);
      await expect(row.locator('.access')).toHaveText(access);
      await expect(row.locator('.what')).toHaveText(what);
    }
  });
}

test('YouTube: Add an API key opens the Connect YouTube dialog with its one masked key field and three steps, and Cancel closes it and gives the focus back to Add an API key', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await page.getByTestId('nav-features').click();
  await page.getByTestId('details-youtube').click();
  await page.getByTestId('youtube-use-key').click();

  const dialog = page.getByRole('dialog', { name: 'Connect YouTube' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByLabel('YouTube Data API key *')).toHaveAttribute('type', 'password');
  await expect(dialog.getByLabel('YouTube Data API key *')).toHaveValue('');
  await expect(page.getByTestId('youtube-key-steps').getByRole('listitem')).toHaveText([
    'Create a project in the Google Cloud Console.',
    'Enable the YouTube Data API v3 for that project.',
    'Create an API key, or sign in with Google from Scout to read your own channel.',
  ]);
  await expect(page.getByTestId('youtube-key-cancel')).toBeFocused();

  await page.getByTestId('youtube-key-cancel').click();

  await expect(page.getByTestId('youtube-key-dialog')).toHaveCount(0);
  await expect(page.getByTestId('youtube-use-key')).toBeFocused();
});

test('GitHub not connected: the bot select "Create the bot on" is on and choosing An organization shows its field, while Create the bot on GitHub stays off with its reason', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await page.getByTestId('nav-features').click();
  await page.getByTestId('details-github').click();
  const target = page.getByLabel('Create the bot on');
  await expect(target).toBeEnabled();

  await target.selectOption('org');

  await expect(page.getByLabel('Organization', { exact: true })).toBeVisible();
  await expect(page.getByTestId('github-bot-create')).toBeDisabled();
  await expect(page.getByTestId('github-bot-reason')).toHaveText(
    'Connect GitHub first to create the bot.',
  );
});

test('Docker: Refresh in its panel asks Docker again and says in one toast what Docker answered, here a Docker host that does not answer', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await page.getByTestId('nav-features').click();
  await page.getByTestId('details-docker').click();
  const line = page.getByTestId('docker-status-line');
  await expect(line).not.toHaveText('Scout is checking Docker on this computer.');
  const answered = (await line.textContent())?.trim() ?? '';
  const expected = answered.startsWith('Docker is installed but not running')
    ? 'Docker is still not running'
    : 'Docker is still not installed';
  await expect(page.locator('.sc-notice')).toHaveCount(0);

  await page.getByTestId('docker-refresh').click();

  await expect(page.locator('.sc-notice')).toHaveText(expected);
  await expect(line).toHaveText(answered);
});
