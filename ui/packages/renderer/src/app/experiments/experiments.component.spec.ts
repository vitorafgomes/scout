import { readFileSync } from 'node:fs';

import { InteractivityChecker } from '@angular/cdk/a11y';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneInvalidArgumentError,
  encodeIpcErrorMessage,
  serializeError,
  type ExperimentArmDto,
  type ExperimentDto,
  type OperatorProjectDto,
  type ProposalDto,
  type PublicationDto,
  type VideoPackageDto,
} from '@scout/shared';

import { LAID_OUT } from '../core/laid-out.testing';
import { ScoutBridgeService } from '../core/scout-bridge';

import { ExperimentsComponent } from './experiments.component';

const CURRENT = 'Docker volumes in 8 minutes';
const VARIANT = 'Docker volumes explained in 8 minutes';

const pkg: VideoPackageDto = {
  status: 'approved',
  videoFile: 'video.mp4',
  shorts: [],
  thumbnailFile: 'thumbnail.png',
  checks: [],
  rightsConfirmed: true,
  approvedAt: '2026-11-04T10:00:00Z',
  title: CURRENT,
  description: '',
  tags: [],
  durationMs: 492000,
  error: null,
  metadataEdited: false,
  assembledAt: null,
  metadataSavedAt: null,
  rightsConfirmedAt: null,
  thumbnailDescription: null,
};

function proposal(overrides: Partial<ProposalDto> = {}): ProposalDto {
  return {
    id: 'pr1',
    kind: 'title',
    text: VARIANT,
    reason: 'Click-through fell after day one.',
    status: 'accepted',
    thumbnailFile: null,
    ...overrides,
  };
}

function arm(overrides: Partial<ExperimentArmDto> = {}): ExperimentArmDto {
  return {
    id: 'control',
    label: 'Control',
    value: CURRENT,
    startedAt: '2026-11-06T18:00:00Z',
    endedAt: '2026-11-08T18:00:00Z',
    impressions: 2140,
    clicks: 98,
    averageViewPercentage: 61.2,
    ...overrides,
  };
}

const WON =
  'Variant A has a 5.8% click-through rate against 4.5% for Control (z = 2.75): Variant A wins.';

function experiment(overrides: Partial<ExperimentDto> = {}): ExperimentDto {
  return {
    id: 'e1',
    kind: 'title',
    status: 'running',
    arms: [
      arm(),
      arm({
        id: 'variant-a',
        label: 'Variant A',
        value: VARIANT,
        startedAt: null,
        endedAt: null,
        impressions: 0,
        clicks: 0,
        averageViewPercentage: 0,
      }),
    ],
    winnerArmId: null,
    result: null,
    ...overrides,
  };
}

function ended(
  status: string,
  control: [number, number, number],
  variant: [number, number, number],
) {
  return {
    status,
    arms: [
      arm({ impressions: control[0], clicks: control[1], averageViewPercentage: control[2] }),
      arm({
        id: 'variant-a',
        label: 'Variant A',
        value: VARIANT,
        startedAt: '2026-11-08T18:00:00Z',
        endedAt: '2026-11-10T18:00:00Z',
        impressions: variant[0],
        clicks: variant[1],
        averageViewPercentage: variant[2],
      }),
    ],
  };
}

const CONCLUDED = experiment({
  ...ended('concluded', [4310, 194, 61.2], [4520, 262, 59.8]),
  winnerArmId: 'variant-a',
  result: WON,
});

function publication(overrides: Partial<PublicationDto> = {}): PublicationDto {
  return {
    status: 'published',
    videoId: 'dQw4w9WgXcQ',
    privacyStatus: 'public',
    publishAt: null,
    madeForKids: false,
    syntheticMedia: false,
    uploadedAt: '2026-11-04T18:00:00Z',
    error: null,
    snapshots: [],
    proposals: [proposal()],
    experiment: null,
    ...overrides,
  };
}

function project(overrides: Partial<OperatorProjectDto> = {}): OperatorProjectDto {
  return {
    id: 'p1',
    topic: CURRENT,
    status: 'approved',
    origin: 'desktop',
    createdAt: '2026-11-03T09:00:00Z',
    updatedAt: '2026-11-04T18:00:00Z',
    candidates: [],
    script: null,
    claims: [],
    error: null,
    scenes: [],
    package: pkg,
    publication: publication(),
    narrationVoice: '',
    backgroundFile: null,
    delegated: false,
    direction: '',
    motion: 'gentle',
    format: 'generated_pictures',
    recordingFile: null,
    recordingMs: 0,
    presenter: '',
    presenterFile: null,
    mouthBox: null,
    presenterVersions: [],
    presenterPreviewFile: null,
    presenterRequest: '',
    presenterSource: '',
    ...overrides,
  };
}

const OLDER = project({
  id: 'p2',
  topic: 'Git rebase without fear',
  createdAt: '2026-11-01T09:00:00Z',
  publication: publication({ uploadedAt: '2026-11-02T18:00:00Z', proposals: [] }),
});

function withPublication(overrides: Partial<PublicationDto>): OperatorProjectDto[] {
  return [project({ publication: publication(overrides) }), OLDER];
}

function makeBridge(projects: OperatorProjectDto[], overrides: Record<string, unknown> = {}) {
  return {
    operatorProjects: vi.fn(async () => ({ projects })),
    operatorStartExperiment: vi.fn(async () =>
      project({ publication: publication({ experiment: experiment() }) }),
    ),
    operatorAdoptExperimentWinner: vi.fn(async () => projects[0]!),
    ...overrides,
  };
}

type Bridge = ReturnType<typeof makeBridge>;

async function settle(fixture: ComponentFixture<ExperimentsComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

async function mount(bridge: Bridge): Promise<ComponentFixture<ExperimentsComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(ExperimentsComponent);
  await settle(fixture);
  return fixture;
}

function el<T extends HTMLElement = HTMLElement>(testId: string): T | null {
  return document.querySelector(`[data-test="${testId}"]`) as T | null;
}

function items(testId: string): string[] {
  return Array.from(document.querySelectorAll(`[data-test="${testId}"] li`)).map((li) =>
    ((li as HTMLElement).textContent ?? '').replace(/\s+/g, ' ').trim(),
  );
}

async function click(fixture: ComponentFixture<ExperimentsComponent>, testId: string) {
  el(testId)!.click();
  await settle(fixture);
}

async function hours(fixture: ComponentFixture<ExperimentsComponent>, value: string) {
  const input = el<HTMLInputElement>('experiment-hours')!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

const norm = (s: string | null | undefined): string => (s ?? '').replace(/\s+/g, ' ').trim();

function textOf(node: Element): string {
  const parts: string[] = [];
  const visit = (n: Node): void => {
    if (n.nodeType === Node.TEXT_NODE) parts.push(n.textContent ?? '');
    else if (
      n.nodeType === Node.ELEMENT_NODE &&
      (n as Element).getAttribute('aria-hidden') !== 'true'
    ) {
      parts.push(' ');
      n.childNodes.forEach(visit);
      parts.push(' ');
    }
  };
  node.childNodes.forEach(visit);
  return norm(parts.join(''));
}

/** What the owner sees, in order: headings, buttons with their state, links, labels, fields, table cells, list items and text. */
function outline(root: Element | null): string[] {
  const lines: string[] = [];
  const walk = (node: Element): void => {
    if (node.getAttribute('aria-hidden') === 'true') return;
    const tag = node.tagName.toLowerCase();
    if (node.getAttribute('role') === 'status' && node.hasAttribute('aria-label')) {
      lines.push(`status: ${node.getAttribute('aria-label')}`);
      return;
    }
    if (tag === 'img' || node.getAttribute('role') === 'img') {
      lines.push(`img: ${node.getAttribute('alt') ?? node.getAttribute('aria-label') ?? ''}`);
      return;
    }
    if (/^h[1-6]$/.test(tag)) return void lines.push(`${tag}: ${textOf(node)}`);
    if (tag === 'button') {
      return void lines.push(
        `button: ${textOf(node)}${node.hasAttribute('disabled') ? ' [disabled]' : ''}`,
      );
    }
    if (tag === 'a') return void lines.push(`link: ${textOf(node)}`);
    if (tag === 'label') return void lines.push(`label: ${textOf(node)}`);
    if (tag === 'input') return void lines.push(`input: ${node.getAttribute('type') ?? 'text'}`);
    if (tag === 'select') {
      const options = Array.from(node.querySelectorAll('option')).map((o) => norm(o.textContent));
      return void lines.push(`select: ${options.join(' | ')}`);
    }
    if (tag === 'th' || tag === 'td') return void lines.push(`cell: ${textOf(node)}`);
    const own = Array.from(node.childNodes)
      .filter((n) => n.nodeType === Node.TEXT_NODE)
      .map((n) => n.textContent)
      .join('');
    if (tag === 'li') {
      lines.push('item:' + (norm(own) === '' ? '' : ' ' + norm(own)));
    } else if (norm(own) !== '' && tag !== 'option') {
      return void lines.push(`text: ${textOf(node)}`);
    }
    for (const child of Array.from(node.children)) walk(child);
  };
  if (root !== null) walk(root);
  return lines;
}

function seen(fixture: ComponentFixture<ExperimentsComponent>): string {
  return [
    ...outline((fixture.nativeElement as HTMLElement).querySelector('main')),
    ...outline(document.querySelector('.cdk-overlay-container')),
  ].join('\n');
}

const HEADER = `h1: Experiments
text: Title and thumbnail tests on a published video, and their results.
link: Proposals in Reports`;

const PICKER = `label: Video
select: Docker volumes in 8 minutes · published 2026-11-04 18:00 | Git rebase without fear · published 2026-11-02 18:00
text: The newest published video is chosen for you.`;

function start(disabled: boolean, ...lines: string[]): string {
  return [
    'h2: Start a test',
    'label: Hours per variant',
    'input: number',
    'text: 24 to 168 hours',
    `button: Start experiment${disabled ? ' [disabled]' : ''}`,
    ...lines.map((l) => `text: ${l}`),
  ].join('\n');
}

const PLAN = `Tests the accepted title "${VARIANT}" against the current one.`;
const ENDED =
  'The last test has ended. Accept another title or thumbnail proposal in Reports to test it.';
const RULE =
  'Each variant, the current title included, needs at least 1,000 impressions and 10 clicks. The best click-through rate must beat the next one by a clear margin (a z-score of at least 1.96), and viewers must not watch the winner more than 5 points less than the current title. Otherwise the test is inconclusive. When the test ends the current title is put back; the winner goes on YouTube only when you adopt it.';

function test(status: string, rows: string[][], ...after: string[]): string {
  return [
    'h2: Title test',
    `text: ${status}`,
    'text: Times in London',
    ...[
      'Variant',
      'Title shown',
      'Started',
      'Ended',
      'Impressions',
      'Clicks',
      'Click-through',
      'Average view percentage',
      ...rows.flat(),
    ].map((c) => `cell: ${c}`),
    'text: How the winner is chosen',
    `text: ${RULE}`,
    ...after,
  ].join('\n');
}

const START_DIALOG = `h2: Start the title test?
text: Starting it approves these changes on YouTube; each runs when its variant goes live.
item: Set the title to "${CURRENT}", the current one, for 48 hours
text: videos.update
item: Change the title to "${VARIANT}" for 48 hours
text: videos.update
item: Put back "${CURRENT}" when the test ends
text: videos.update`;

const CONTROL_ENDED = ['Current title', CURRENT, '2026-11-06 18:00', '2026-11-08 18:00'];
const VARIANT_ENDED = ['Variant A', VARIANT, '2026-11-08 18:00', '2026-11-10 18:00'];
const WINNER_ROWS = [
  [...CONTROL_ENDED, '4310', '194', '4.5%', '61.2'],
  [
    'Variant A Winner',
    VARIANT,
    '2026-11-08 18:00',
    '2026-11-10 18:00',
    '4520',
    '262',
    '5.8%',
    '59.8',
  ],
];

function refused(message: string) {
  return async () => {
    throw new Error(
      `Error invoking remote method 'scout:operator.experiment': Error: ${encodeIpcErrorMessage(
        serializeError(new ControlPlaneInvalidArgumentError(message)),
      )}`,
    );
  };
}

describe('Experiments as Experiments.dc.html draws it, state by state', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(async () => {
    TestBed.inject(MatDialog).closeAll();
    vi.useRealTimers();
  });

  it('Loading: the loading card under the header until the first read answers', async () => {
    const fixture = await mount(
      makeBridge([], { operatorProjects: vi.fn(() => new Promise(() => undefined)) }),
    );

    expect(seen(fixture)).toBe(`${HEADER}\nstatus: Loading published videos`);
  });

  it('No published video: the sentence and Go to Assembly', async () => {
    const fixture = await mount(makeBridge([project({ publication: null })]));

    expect(seen(fixture)).toBe(
      `${HEADER}
text: No video is on YouTube yet. Publish one from the Assembly page.
link: Go to Assembly`,
    );
    expect(el('experiments-assembly')!.getAttribute('href')).toBe('/assembly');
  });

  it('Ready to start: the newest video chosen, Start experiment on, and what the test compares', async () => {
    const fixture = await mount(makeBridge(withPublication({})));

    expect(seen(fixture)).toBe(`${HEADER}\n${PICKER}\n${start(false, PLAN)}`);
  });

  it('Running: Start off with its reason, the arms in London, an arm not ended yet reading Not yet', async () => {
    const fixture = await mount(
      makeBridge(
        withPublication({ proposals: [proposal({ status: 'tested' })], experiment: experiment() }),
      ),
    );

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${start(true, 'A test is already running on this video. Start another when it ends.')}\n${test(
        'Running',
        [
          [...CONTROL_ENDED, '2140', '98', '4.6%', '61.2'],
          ['Variant A', VARIANT, 'Not yet', 'Not yet', 'Not yet', 'Not yet', 'Not yet', 'Not yet'],
        ],
      )}`,
    );
    expect(el('experiment-start')!.getAttribute('aria-describedby')).toBe('experiments-why');
  });

  it('Winner: the winning row marked, the result as returned, and Adopt the winner', async () => {
    const fixture = await mount(
      makeBridge(
        withPublication({ proposals: [proposal({ status: 'tested' })], experiment: CONCLUDED }),
      ),
    );

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${start(true, ENDED)}\n${test('Concluded', WINNER_ROWS, `text: ${WON}`, 'button: Adopt the winner')}`,
    );
  });

  it('Inconclusive: the result as returned and no Adopt', async () => {
    const result = 'Inconclusive: every arm needs at least 1,000 impressions and 10 clicks.';
    const fixture = await mount(
      makeBridge(
        withPublication({
          proposals: [proposal({ status: 'tested' })],
          experiment: experiment({
            ...ended('inconclusive', [1210, 61, 61.2], [860, 47, 58.4]),
            result,
          }),
        }),
      ),
    );

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${start(true, ENDED)}\n${test(
        'Inconclusive',
        [
          [...CONTROL_ENDED, '1210', '61', '5.0%', '61.2'],
          [...VARIANT_ENDED, '860', '47', '5.5%', '58.4'],
        ],
        `text: ${result}`,
      )}`,
    );
  });

  it('Winner adopted: the chip reads Adopted and Adopt is gone', async () => {
    const fixture = await mount(
      makeBridge(
        withPublication({
          proposals: [proposal({ status: 'tested' })],
          experiment: { ...CONCLUDED, status: 'adopted' },
        }),
      ),
    );

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${start(true, ENDED)}\n${test('Adopted', WINNER_ROWS, `text: ${WON}`)}`,
    );
  });

  it('Hours out of range: Start off and the range in the error colour, the plan line kept', async () => {
    const fixture = await mount(makeBridge(withPublication({})));

    await hours(fixture, '200');

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${start(true, 'Each variant runs 24 to 168 hours.', PLAN)}`,
    );
  });

  it('Not published: Start off with the control plane reason', async () => {
    const fixture = await mount(makeBridge(withPublication({ status: 'uploaded' })));

    expect(seen(fixture)).toBe(
      `${HEADER}
label: Video
select: Docker volumes in 8 minutes · uploaded 2026-11-04 18:00 | Git rebase without fear · published 2026-11-02 18:00
text: The newest published video is chosen for you.
${start(true, 'Only a published or scheduled video is tested; this one is uploaded.')}`,
    );
  });

  it('No accepted proposal: Start off, sending the owner to Reports', async () => {
    const fixture = await mount(makeBridge(withPublication({ proposals: [] })));

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${start(true, 'Accept a title or thumbnail proposal on the Reports page first.')}`,
    );
    expect(el('experiments-reports')!.getAttribute('href')).toBe('/reports');
  });

  it('Read failed: the control plane sentence over the last list, which stays', async () => {
    vi.useFakeTimers({ toFake: ['setInterval'] });
    const bridge = makeBridge(withPublication({}));
    const fixture = await mount(bridge);
    bridge.operatorProjects.mockImplementation(async () => {
      throw new Error('The Scout control plane did not answer.');
    });

    vi.advanceTimersByTime(10_000);
    await settle(fixture);

    expect(seen(fixture)).toBe(
      `${HEADER}\ntext: The Scout control plane did not answer.\n${PICKER}\n${start(false, PLAN)}`,
    );
  });

  it('Start dialog: every write it approves, the current title first and put back at the end, Cancel focused', async () => {
    const fixture = await mount(makeBridge(withPublication({})));

    await click(fixture, 'experiment-start');

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${start(false, PLAN)}\n${START_DIALOG}\nbutton: Cancel\nbutton: Start experiment`,
    );
    expect(document.activeElement).toBe(el('experiment-dialog-cancel'));
    const dialog = document.querySelector('[role="dialog"]')!;
    expect(
      ['aria-labelledby', 'aria-describedby'].map((name) =>
        norm(document.getElementById(dialog.getAttribute(name) ?? '')?.textContent),
      ),
    ).toEqual([
      'Start the title test?',
      'Starting it approves these changes on YouTube; each runs when its variant goes live.',
    ]);
    expect(document.querySelector('.cdk-overlay-pane')!.classList.contains('sc-dialog')).toBe(true);
  });

  it('Start refused: the dialog stays open with the reason and sends nothing more', async () => {
    const bridge = makeBridge(withPublication({}), {
      operatorStartExperiment: vi.fn(refused('The video is not public yet.')),
    });
    const fixture = await mount(bridge);

    await click(fixture, 'experiment-start');
    await click(fixture, 'experiment-start-confirm');

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${start(false, PLAN)}\n${START_DIALOG}\ntext: The video is not public yet.\nbutton: Cancel\nbutton: Start experiment`,
    );
    expect(bridge.operatorStartExperiment).toHaveBeenCalledTimes(1);
  });

  it('Adopt dialog: what it changes for good and the one write it approves', async () => {
    const fixture = await mount(
      makeBridge(
        withPublication({ proposals: [proposal({ status: 'tested' })], experiment: CONCLUDED }),
      ),
    );

    await click(fixture, 'experiment-adopt');

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${start(true, ENDED)}\n${test('Concluded', WINNER_ROWS, `text: ${WON}`, 'button: Adopt the winner')}
h2: Adopt the winner?
text: Scout changes the title on YouTube for good to "${VARIANT}".
item: Set the title to "${VARIANT}"
text: videos.update
button: Cancel
button: Adopt the winner`,
    );
  });
});

describe('Experiments: what each write sends and its confirmation path', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(async () => {
    TestBed.inject(MatDialog).closeAll();
    vi.useRealTimers();
  });

  it('Start experiment sends the project and the hours only from the confirmation, then closes it', async () => {
    const bridge = makeBridge(withPublication({}));
    const fixture = await mount(bridge);

    await hours(fixture, '72');
    await click(fixture, 'experiment-start');
    expect({
      sent: bridge.operatorStartExperiment.mock.calls.length,
      asking: el('experiment-start-dialog') !== null,
    }).toEqual({ sent: 0, asking: true });
    const actions = items('experiment-start-actions');
    await click(fixture, 'experiment-start-confirm');

    expect(actions).toEqual([
      `Set the title to "${CURRENT}", the current one, for 72 hours videos.update`,
      `Change the title to "${VARIANT}" for 72 hours videos.update`,
      `Put back "${CURRENT}" when the test ends videos.update`,
    ]);
    expect(bridge.operatorStartExperiment.mock.calls).toEqual([
      [{ projectId: 'p1', armHours: 72 }],
    ]);
    expect(el('experiment-start-dialog')).toBeNull();
  });

  it('Cancel, Esc and the scrim close the start confirmation and send nothing', async () => {
    const bridge = makeBridge(withPublication({}));
    const fixture = await mount(bridge);

    await click(fixture, 'experiment-start');
    await click(fixture, 'experiment-dialog-cancel');
    expect(bridge.operatorStartExperiment).not.toHaveBeenCalled();
    const afterCancel = el('experiment-start-dialog');
    await click(fixture, 'experiment-start');
    el('experiment-start-dialog')!.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'Escape',
        keyCode: 27,
        bubbles: true,
      } as KeyboardEventInit),
    );
    await settle(fixture);
    const afterEscape = el('experiment-start-dialog');
    await click(fixture, 'experiment-start');
    (document.querySelector('.cdk-overlay-backdrop') as HTMLElement).click();
    await settle(fixture);

    expect([afterCancel, afterEscape, el('experiment-start-dialog')]).toEqual([null, null, null]);
    expect(bridge.operatorStartExperiment).not.toHaveBeenCalled();
  });

  it('a thumbnail experiment approves thumbnails.set for each arm with the proposed file', async () => {
    const fixture = await mount(
      makeBridge(
        withPublication({
          proposals: [
            proposal({
              kind: 'thumbnail',
              text: 'Bind vs Volume',
              thumbnailFile: 'thumbnail-proposal-pr1.png',
            }),
          ],
        }),
      ),
    );

    await click(fixture, 'experiment-start');

    expect(items('experiment-start-actions')).toEqual([
      'Set the thumbnail to "thumbnail.png", the current one, for 48 hours thumbnails.set',
      'Change the thumbnail to "thumbnail-proposal-pr1.png" for 48 hours thumbnails.set',
      'Put back "thumbnail.png" when the test ends thumbnails.set',
    ]);
  });

  it('the experiment tests the first two accepted proposals of the first accepted kind', async () => {
    const fixture = await mount(
      makeBridge(
        withPublication({
          proposals: [
            proposal({ id: 'pr1', text: 'First' }),
            proposal({ id: 'pr2', kind: 'thumbnail', thumbnailFile: 'thumbnail-proposal-pr2.png' }),
            proposal({ id: 'pr3', text: 'Second' }),
            proposal({ id: 'pr4', text: 'Third' }),
          ],
        }),
      ),
    );

    await click(fixture, 'experiment-start');

    expect(items('experiment-start-actions')).toEqual([
      `Set the title to "${CURRENT}", the current one, for 48 hours videos.update`,
      'Change the title to "First" for 48 hours videos.update',
      'Change the title to "Second" for 48 hours videos.update',
      `Put back "${CURRENT}" when the test ends videos.update`,
    ]);
  });

  it('Adopt the winner sends the project id only from its confirmation, then closes it', async () => {
    const bridge = makeBridge(
      withPublication({ proposals: [proposal({ status: 'tested' })], experiment: CONCLUDED }),
    );
    const fixture = await mount(bridge);

    await click(fixture, 'experiment-adopt');
    expect({
      sent: bridge.operatorAdoptExperimentWinner.mock.calls.length,
      asking: el('experiment-adopt-dialog') !== null,
    }).toEqual({ sent: 0, asking: true });
    await click(fixture, 'experiment-dialog-cancel');
    expect(bridge.operatorAdoptExperimentWinner).not.toHaveBeenCalled();
    await click(fixture, 'experiment-adopt');
    await click(fixture, 'experiment-adopt-confirm');

    expect(bridge.operatorAdoptExperimentWinner.mock.calls).toEqual([[{ id: 'p1' }]]);
    expect(el('experiment-adopt-dialog')).toBeNull();
  });

  it('arm hours outside 24 to 168 keep Start off; 24 and 168 turn it on', async () => {
    const fixture = await mount(makeBridge(withPublication({})));

    const states: boolean[] = [];
    for (const value of ['23', '169', '24', '168']) {
      await hours(fixture, value);
      states.push(el<HTMLButtonElement>('experiment-start')!.disabled);
    }

    expect(states).toEqual([true, true, false, false]);
  });

  it('values from YouTube render as text, never as markup', async () => {
    const markup = '<img src=x onerror="alert(1)">';
    const fixture = await mount(
      makeBridge(withPublication({ proposals: [proposal({ text: markup })] })),
    );

    await click(fixture, 'experiment-start');

    expect(items('experiment-start-actions')[1]).toBe(
      `Change the title to "${markup}" for 48 hours videos.update`,
    );
    expect(document.querySelector('.cdk-overlay-container img')).toBeNull();
  });
});

/** The colour literals left in a component's styles, read from the renderer root: hex, rgb(), hsl() and the named white and black. */
function colourLiterals(relative: string): string[] {
  const source = readFileSync(relative, 'utf8');
  const start = source.indexOf('  styles:');
  const css = source.slice(start, source.indexOf('\n})\n', start));
  expect(start, `${relative} declares its styles`).toBeGreaterThanOrEqual(0);
  expect(css, `${relative} paints with the theme tokens`).toContain('var(--sc-');
  return Array.from(
    css.matchAll(
      /#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(|(?<![\w-])(?:white|black)(?![\w-])/g,
    ),
    (match) => match[0],
  );
}

describe('Experiments follows the theme and the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const file of [
    'src/app/experiments/experiments.component.ts',
    'src/app/experiments/experiment-dialog.component.ts',
  ]) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }

  it('Start experiment and Adopt the winner show their icon before their text, as the prototype draws them', async () => {
    await mount(
      makeBridge(
        withPublication({ proposals: [proposal({ status: 'tested' })], experiment: CONCLUDED }),
      ),
    );

    expect(
      ['experiment-start', 'experiment-adopt'].map((id) => {
        const icon = el(id)!.querySelector('sc-icon')!;
        return [
          icon.nextSibling?.textContent?.trim(),
          icon.previousSibling === null || norm(icon.previousSibling.textContent) === '',
        ];
      }),
    ).toEqual([
      ['Start experiment', true],
      ['Adopt the winner', true],
    ]);
  });
});
