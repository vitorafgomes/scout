import { ask, expect, test, useOnThisComputer } from '../fixtures/app.js';

const UNRUNNABLE =
  '122 test(s) did not pass; the 3 whose failure is in the kept output could not run: they need a Docker daemon, which the container does not have.';

const REVIEW_WITH_UNRUNNABLE_TESTS = {
  repository: 'acme/shop',
  number: 5,
  commit: 'ec7f951',
  verdict: 'ready to merge',
  read: 'The whole diff and its tests.',
  checks: [
    {
      name: 'build',
      status: 'completed',
      conclusion: 'success',
      url: 'https://github.com/acme/shop/runs/1',
    },
  ],
  tests: {
    ran: true,
    command: 'dotnet test',
    exitCode: 1,
    durationSeconds: 95,
    output: 'Cannot connect to the Docker daemon',
    unrunnable: UNRUNNABLE,
  },
  findings: [],
  offer: {
    verdict: 'approve',
    reason:
      'Approve: every changed file was read, nothing needs fixing, and every check passed, which covers the tests that could not run here.',
    commentInstead: false,
  },
};

test.use({
  script: {
    replies: [
      {
        complete: `Nothing to report on acme/shop#5.\n\n\`\`\`scout-findings\n${JSON.stringify(REVIEW_WITH_UNRUNNABLE_TESTS)}\n\`\`\``,
      },
    ],
  },
});

test('a review whose container run could not run its tests shows tests could not run, not tests failed, and offers Approve', async ({
  desktop,
}) => {
  const { page } = await desktop.launch();
  await useOnThisComputer(page);

  await ask(page, 'Review acme/shop#5');

  const run = page.getByTestId('tests-run');
  await expect(run, 'the chip says the tests could not run, never tests failed').toHaveText(
    'Tests could not run',
  );
  await expect(run, 'the chip has the neutral not-run tone').toHaveClass('chip notrun');
  await expect(
    page.getByTestId('tests-unrunnable'),
    'the worker sentence sits under the chips',
  ).toHaveText(UNRUNNABLE);
  await expect(page.getByTestId('tests-output').locator('summary')).toHaveText('Last output');
  await expect(
    page.getByTestId('offer'),
    'the card shows the Approve the worker offers for a run that could not run when every check passed',
  ).toHaveText('Approve');
  await expect(page.getByTestId('offer-reason')).toHaveText(
    REVIEW_WITH_UNRUNNABLE_TESTS.offer.reason,
  );
  await expect(page.getByTestId('check-line')).toHaveCount(0);
});
