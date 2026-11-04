import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneInvalidArgumentError,
  encodeIpcErrorMessage,
  serializeError,
  type ExperimentDto,
  type OperatorProjectDto,
  type ProposalDto,
  type PublicationDto,
  type VideoPackageDto,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { ExperimentsComponent } from './experiments.component';

const pkg: VideoPackageDto = {
  status: 'approved',
  videoFile: 'video.mp4',
  shorts: [],
  thumbnailFile: 'thumbnail.png',
  checks: [],
  rightsConfirmed: true,
  approvedAt: '2026-07-01T10:00:00Z',
  title: 'Span<T> explained',
  description: '',
  tags: [],
  durationMs: 98000,
  error: null,
  metadataEdited: false,
};

function proposal(overrides: Partial<ProposalDto> = {}): ProposalDto {
  return {
    id: 'pr1',
    kind: 'title',
    text: 'Span<T> in 5 minutes',
    reason: 'CTR under the median',
    status: 'accepted',
    thumbnailFile: null,
    ...overrides,
  };
}

function experiment(overrides: Partial<ExperimentDto> = {}): ExperimentDto {
  return {
    id: 'e1',
    kind: 'title',
    status: 'running',
    arms: [
      {
        id: 'control',
        label: 'Control',
        value: 'Span<T> explained',
        startedAt: '2026-07-02T09:00:00Z',
        endedAt: '2026-07-03T09:00:00Z',
        impressions: 2000,
        clicks: 80,
        averageViewPercentage: 40,
      },
      {
        id: 'a1',
        label: 'Variant',
        value: 'Span<T> in 5 minutes',
        startedAt: '2026-07-03T09:00:00Z',
        endedAt: null,
        impressions: 0,
        clicks: 0,
        averageViewPercentage: 0,
      },
    ],
    winnerArmId: null,
    result: null,
    ...overrides,
  };
}

function publication(overrides: Partial<PublicationDto> = {}): PublicationDto {
  return {
    status: 'published',
    videoId: 'dQw4w9WgXcQ',
    privacyStatus: 'public',
    publishAt: null,
    madeForKids: false,
    syntheticMedia: false,
    uploadedAt: '2026-07-01T11:00:00Z',
    error: null,
    snapshots: [],
    proposals: [],
    experiment: null,
    ...overrides,
  };
}

function project(overrides: Partial<OperatorProjectDto> = {}): OperatorProjectDto {
  return {
    id: 'p1',
    topic: 'Span<T> in C#',
    status: 'approved',
    origin: 'desktop',
    createdAt: '2026-07-01T09:00:00Z',
    updatedAt: '2026-07-02T11:00:00Z',
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
    visualBrief: '',
    motion: 'gentle',
    ...overrides,
  };
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
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(ExperimentsComponent);
  await settle(fixture);
  const picker = el<HTMLSelectElement>(fixture, 'experiments-project');
  if (picker !== null) {
    picker.value = 'p1';
    picker.dispatchEvent(new Event('change'));
    await settle(fixture);
  }
  return fixture;
}

function el<T extends HTMLElement = HTMLElement>(
  fixture: ComponentFixture<ExperimentsComponent>,
  testId: string,
): T | null {
  return fixture.nativeElement.querySelector(`[data-test="${testId}"]`) as T | null;
}

function text(fixture: ComponentFixture<ExperimentsComponent>, testId: string): string {
  return (el(fixture, testId)?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function items(fixture: ComponentFixture<ExperimentsComponent>, testId: string): string[] {
  return Array.from(fixture.nativeElement.querySelectorAll(`[data-test="${testId}"] li`)).map(
    (li) => ((li as HTMLElement).textContent ?? '').replace(/\s+/g, ' ').trim(),
  );
}

async function click(fixture: ComponentFixture<ExperimentsComponent>, testId: string) {
  el(fixture, testId)!.click();
  await settle(fixture);
}

async function hours(fixture: ComponentFixture<ExperimentsComponent>, value: string) {
  const input = el<HTMLInputElement>(fixture, 'experiment-hours')!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

describe('ExperimentsComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('Start opens a dialog listing every approved action the experiment will create, one per arm, for the hours chosen, and confirming sends the arm hours', async () => {
    const bridge = makeBridge([
      project({
        publication: publication({
          proposals: [
            proposal(),
            proposal({ id: 'pr2', status: 'rejected', text: 'Rejected title' }),
            proposal({ id: 'pr3', kind: 'topic', text: 'Memory<T>' }),
          ],
        }),
      }),
    ]);
    const fixture = await mount(bridge);

    await hours(fixture, '72');
    await click(fixture, 'experiment-start');
    const actions = items(fixture, 'experiment-start-actions');
    await click(fixture, 'experiment-start-confirm');

    expect(actions).toEqual([
      'videos.update: Control, "Span<T> explained", for 72 hours',
      'videos.update: Variant A, "Span<T> in 5 minutes", for 72 hours',
      'videos.update: Control, "Span<T> explained", again when the test ends',
    ]);
    expect(bridge.operatorStartExperiment.mock.calls).toEqual([
      [{ projectId: 'p1', armHours: 72 }],
    ]);
    expect(el(fixture, 'experiment-start-dialog')).toBeNull();
  });

  it('a thumbnail experiment lists thumbnails.set actions with the proposed file', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          publication: publication({
            proposals: [
              proposal({
                kind: 'thumbnail',
                text: 'Bigger face',
                thumbnailFile: 'thumbnail-proposal-pr1.png',
              }),
            ],
          }),
        }),
      ]),
    );

    await click(fixture, 'experiment-start');

    expect(items(fixture, 'experiment-start-actions')).toEqual([
      'thumbnails.set: Control, "thumbnail.png", for 48 hours',
      'thumbnails.set: Variant A, "thumbnail-proposal-pr1.png", for 48 hours',
      'thumbnails.set: Control, "thumbnail.png", again when the test ends',
    ]);
  });

  it('the experiment tests the first two accepted proposals of the first accepted kind, as the control plane does', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          publication: publication({
            proposals: [
              proposal({ id: 'pr1', text: 'First' }),
              proposal({
                id: 'pr2',
                kind: 'thumbnail',
                thumbnailFile: 'thumbnail-proposal-pr2.png',
              }),
              proposal({ id: 'pr3', text: 'Second' }),
              proposal({ id: 'pr4', text: 'Third' }),
            ],
          }),
        }),
      ]),
    );

    await click(fixture, 'experiment-start');

    expect(items(fixture, 'experiment-start-actions')).toEqual([
      'videos.update: Control, "Span<T> explained", for 48 hours',
      'videos.update: Variant A, "First", for 48 hours',
      'videos.update: Variant B, "Second", for 48 hours',
      'videos.update: Control, "Span<T> explained", again when the test ends',
    ]);
  });

  it('a video that is not published or scheduled cannot start an experiment', async () => {
    const fixture = await mount(
      makeBridge([
        project({ publication: publication({ status: 'uploaded', proposals: [proposal()] }) }),
      ]),
    );

    expect(el<HTMLButtonElement>(fixture, 'experiment-start')!.disabled).toBe(true);
    expect(text(fixture, 'experiment-start-blocked')).toBe(
      'Only a published or scheduled video is tested; this one is uploaded.',
    );
  });

  it('arm hours outside 24 to 168, or no accepted title or thumbnail proposal, keep Start disabled and say why', async () => {
    const fixture = await mount(
      makeBridge([project({ publication: publication({ proposals: [proposal()] }) })]),
    );

    await hours(fixture, '23');
    const low = [
      el<HTMLButtonElement>(fixture, 'experiment-start')!.disabled,
      text(fixture, 'experiment-hours-error'),
    ];
    await hours(fixture, '169');
    const high = el<HTMLButtonElement>(fixture, 'experiment-start')!.disabled;
    await hours(fixture, '24');
    const lowest = el<HTMLButtonElement>(fixture, 'experiment-start')!.disabled;
    await hours(fixture, '168');
    const highest = el<HTMLButtonElement>(fixture, 'experiment-start')!.disabled;

    expect(low).toEqual([true, 'Each arm runs 24 to 168 hours.']);
    expect([high, lowest, highest]).toEqual([true, false, false]);
    TestBed.resetTestingModule();
    const none = await mount(makeBridge([project()]));
    expect(el<HTMLButtonElement>(none, 'experiment-start')!.disabled).toBe(true);
    expect(text(none, 'experiment-start-blocked')).toBe(
      'Accept a title or thumbnail proposal on the Reports page first.',
    );
  });

  it('a running experiment shows each arm with its start and end in London and its metrics, and cannot be started again', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          publication: publication({ proposals: [proposal()], experiment: experiment() }),
        }),
      ]),
    );

    const rows = Array.from(
      fixture.nativeElement.querySelectorAll('[data-test="experiment-arms"] tbody tr'),
    ).map((tr) =>
      Array.from((tr as HTMLElement).children).map((c) => (c.textContent ?? '').trim()),
    );
    expect(text(fixture, 'experiment-status')).toBe('running');
    expect(rows).toEqual([
      ['Control', 'Span<T> explained', '2026-07-02 10:00', '2026-07-03 10:00', '2000', '80', '40'],
      ['Variant', 'Span<T> in 5 minutes', '2026-07-03 10:00', '-', '0', '0', '0'],
    ]);
    expect(el<HTMLButtonElement>(fixture, 'experiment-start')!.disabled).toBe(true);
    expect(el(fixture, 'experiment-adopt')).toBeNull();
  });

  it('a concluded experiment shows the winner and the result as returned, and Adopt winner confirms the one action it will create', async () => {
    const decided = experiment({
      status: 'concluded',
      winnerArmId: 'a1',
      result: 'Variant wins: z = 2.31 on 2000 and 2100 impressions.',
    });
    const bridge = makeBridge([project({ publication: publication({ experiment: decided }) })]);
    const fixture = await mount(bridge);

    const result = [text(fixture, 'experiment-winner'), text(fixture, 'experiment-result-text')];
    await click(fixture, 'experiment-adopt');
    const actions = items(fixture, 'experiment-adopt-actions');
    await click(fixture, 'experiment-adopt-confirm');

    expect(result).toEqual([
      'Winner: Variant',
      'Variant wins: z = 2.31 on 2000 and 2100 impressions.',
    ]);
    expect(actions).toEqual(['videos.update: set the title to "Span<T> in 5 minutes"']);
    expect(bridge.operatorAdoptExperimentWinner.mock.calls).toEqual([[{ id: 'p1' }]]);
    expect(el(fixture, 'experiment-adopt-dialog')).toBeNull();
  });

  it('an inconclusive experiment says so with the result as returned and offers no Adopt', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          publication: publication({
            experiment: experiment({
              status: 'inconclusive',
              result: 'Inconclusive: z = 0.84 on 1200 and 1150 impressions.',
            }),
          }),
        }),
      ]),
    );

    expect(text(fixture, 'experiment-winner')).toBe('Inconclusive');
    expect(text(fixture, 'experiment-result-text')).toBe(
      'Inconclusive: z = 0.84 on 1200 and 1150 impressions.',
    );
    expect(el(fixture, 'experiment-adopt')).toBeNull();
  });

  it('a refused start keeps the dialog open with the reason', async () => {
    const bridge = makeBridge(
      [project({ publication: publication({ proposals: [proposal()] }) })],
      {
        operatorStartExperiment: vi.fn(async () => {
          throw new Error(
            `Error invoking remote method 'scout:operator.experiment': Error: ${encodeIpcErrorMessage(
              serializeError(new ControlPlaneInvalidArgumentError('The video is not public yet.')),
            )}`,
          );
        }),
      },
    );
    const fixture = await mount(bridge);

    await click(fixture, 'experiment-start');
    await click(fixture, 'experiment-start-confirm');

    expect(text(fixture, 'experiment-error')).toBe('The video is not public yet.');
    expect(el(fixture, 'experiment-start-dialog')).not.toBeNull();
  });
});
