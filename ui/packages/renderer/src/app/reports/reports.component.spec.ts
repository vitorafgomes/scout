import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneInvalidArgumentError,
  encodeIpcErrorMessage,
  serializeError,
  type OperatorProjectDto,
  type PublicationDto,
  type SceneDto,
  type SnapshotDto,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { ReportsComponent } from './reports.component';

function scene(id: string, order: number, durationMs: number): SceneDto {
  return {
    id,
    order,
    narration: '',
    visualPrompt: '',
    locked: false,
    status: 'ready',
    error: null,
    audioSource: 'generated',
    visualSource: 'generated',
    visualQuery: '',
    visualCredit: null,
    audioFile: null,
    visualFile: null,
    durationMs,
    clipStartMs: 0,
    clipEndMs: 0,
  };
}

function snapshot(overrides: Partial<SnapshotDto> = {}): SnapshotDto {
  return {
    window: '24h',
    takenAt: '2026-07-02T11:00:00Z',
    views: 1234,
    minutesWatched: 2050.5,
    averageViewDurationSeconds: 99.7,
    averageViewPercentage: 41.2,
    subscribersGained: 12,
    subscribersLost: 1,
    likes: 88,
    comments: 9,
    shares: 4,
    impressions: null,
    impressionsCtr: null,
    retention: [],
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
    syntheticMedia: true,
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
    scenes: [scene('s3', 3, 5000), scene('s1', 1, 2000), scene('s2', 2, 3000)],
    package: null,
    publication: publication(),
    narrationVoice: '',
    backgroundFile: null,
    delegated: false,
    direction: '',
    motion: 'gentle',
    format: 'generated_pictures',
    recordingFile: null,
    recordingMs: 0,
    ...overrides,
  };
}

function makeBridge(projects: OperatorProjectDto[], overrides: Record<string, unknown> = {}) {
  return {
    operatorProjects: vi.fn(async () => ({ projects })),
    operatorDecideProposal: vi.fn(async () => projects[0]!),
    ...overrides,
  };
}

type Bridge = ReturnType<typeof makeBridge>;

async function settle(fixture: ComponentFixture<ReportsComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

async function mount(
  bridge: Bridge,
  selectId?: string,
): Promise<ComponentFixture<ReportsComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(ReportsComponent);
  await settle(fixture);
  if (selectId !== undefined) {
    const picker = el<HTMLSelectElement>(fixture, 'reports-project')!;
    picker.value = selectId;
    picker.dispatchEvent(new Event('change'));
    await settle(fixture);
  }
  return fixture;
}

function el<T extends Element = HTMLElement>(
  fixture: ComponentFixture<ReportsComponent>,
  testId: string,
): T | null {
  return fixture.nativeElement.querySelector(`[data-test="${testId}"]`) as T | null;
}

function text(fixture: ComponentFixture<ReportsComponent>, testId: string): string {
  return (el(fixture, testId)?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function rows(fixture: ComponentFixture<ReportsComponent>, window: string): string[][] {
  return Array.from(
    fixture.nativeElement.querySelectorAll(`[data-test="snapshot-${window}"] tr`),
  ).map((tr) => Array.from((tr as HTMLElement).children).map((c) => (c.textContent ?? '').trim()));
}

describe('ReportsComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists only videos that are on YouTube, and says so when there is none', async () => {
    const fixture = await mount(
      makeBridge([
        project(),
        project({ id: 'p2', topic: 'Queued', publication: publication({ videoId: null }) }),
        project({ id: 'p3', topic: 'Not published', publication: null }),
      ]),
    );
    const options = Array.from(
      el<HTMLSelectElement>(fixture, 'reports-project')!.querySelectorAll('option'),
    ).map((o) => o.value);
    expect(options).toEqual(['', 'p1']);
    TestBed.resetTestingModule();
    const empty = await mount(makeBridge([project({ publication: null })]));
    expect(text(empty, 'reports-empty')).toBe(
      'No video is on YouTube yet. Publish one from the Assembly page.',
    );
  });

  it("shows the 24h and 7d numbers exactly as the API returned them, impressions and CTR only when present, and no metric of Scout's own", async () => {
    const fixture = await mount(
      makeBridge([
        project({
          publication: publication({
            snapshots: [
              snapshot(),
              snapshot({
                window: '7d',
                takenAt: '2026-07-08T11:00:00Z',
                views: 5000,
                impressions: 64000,
                impressionsCtr: 5.1,
              }),
            ],
          }),
        }),
      ]),
      'p1',
    );

    expect(rows(fixture, '24h')).toEqual([
      ['Taken', '2026-07-02 12:00 Europe/London'],
      ['Views', '1234'],
      ['Minutes watched', '2050.5'],
      ['Average view duration (seconds)', '99.7'],
      ['Average view percentage', '41.2'],
      ['Subscribers gained', '12'],
      ['Subscribers lost', '1'],
      ['Likes', '88'],
      ['Comments', '9'],
      ['Shares', '4'],
    ]);
    expect(rows(fixture, '7d').slice(-2)).toEqual([
      ['Impressions', '64000'],
      ['Impressions click-through rate', '5.1'],
    ]);
    expect(rows(fixture, '7d')[1]).toEqual(['Views', '5000']);
  });

  it('says when a window has no numbers yet', async () => {
    const fixture = await mount(
      makeBridge([project({ publication: publication({ snapshots: [snapshot()] }) })]),
      'p1',
    );

    expect(text(fixture, 'snapshot-empty-7d')).toBe('No 7d numbers yet.');
    expect(el(fixture, 'snapshot-empty-24h')).toBeNull();
  });

  it('draws the retention curve as an SVG line from the points as returned, with the scene boundaries marked where each scene ends', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          publication: publication({
            snapshots: [
              snapshot({
                retention: [
                  { elapsedRatio: 0, audienceWatchRatio: 1 },
                  { elapsedRatio: 0.5, audienceWatchRatio: 0.42 },
                  { elapsedRatio: 1, audienceWatchRatio: 0.1 },
                ],
              }),
            ],
          }),
        }),
      ]),
      'p1',
    );

    const line = el<SVGPolylineElement>(fixture, 'retention-line-24h')!;
    const marks = Array.from(
      fixture.nativeElement.querySelectorAll('[data-test^="scene-mark-24h-"]'),
    ).map((m) => [(m as Element).getAttribute('x1'), (m as Element).getAttribute('x2')]);
    expect(line.getAttribute('points')).toBe('0,0 200,92.8 400,144');
    expect(marks).toEqual([
      ['80', '80'],
      ['200', '200'],
    ]);
    expect(el(fixture, 'retention-line-7d')).toBeNull();
  });

  it('a retention ratio above 1 raises the top of the chart instead of clipping it', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          scenes: [],
          publication: publication({
            snapshots: [
              snapshot({
                retention: [
                  { elapsedRatio: 0, audienceWatchRatio: 1.25 },
                  { elapsedRatio: 1, audienceWatchRatio: 0.5 },
                ],
              }),
            ],
          }),
        }),
      ]),
      'p1',
    );

    expect(el(fixture, 'retention-line-24h')!.getAttribute('points')).toBe('0,0 400,96');
    expect(fixture.nativeElement.querySelectorAll('[data-test^="scene-mark-"]').length).toBe(0);
  });

  it('lists the proposals with Accept and Reject on the ones still proposed, and previews a thumbnail proposal through scout-media', async () => {
    const proposals = [
      {
        id: 'pr1',
        kind: 'title',
        text: 'Span<T> in 5 minutes',
        reason: 'CTR under median',
        status: 'proposed',
        thumbnailFile: null,
      },
      {
        id: 'pr2',
        kind: 'thumbnail',
        text: 'Bigger face',
        reason: 'Low CTR',
        status: 'proposed',
        thumbnailFile: 'thumbnail-proposal-pr2.png',
      },
      {
        id: 'pr3',
        kind: 'topic',
        text: 'Memory<T>',
        reason: 'Viewers asked',
        status: 'rejected',
        thumbnailFile: null,
      },
    ];
    const fixture = await mount(
      makeBridge([project({ publication: publication({ proposals }) })]),
      'p1',
    );

    expect(text(fixture, 'proposal-pr1')).toContain('title: Span<T> in 5 minutes');
    expect(text(fixture, 'proposal-pr1')).toContain('CTR under median');
    expect(el<HTMLImageElement>(fixture, 'proposal-thumbnail-pr2')!.getAttribute('src')).toBe(
      'scout-media://p1/thumbnail-proposal-pr2.png?v=2026-07-02T11%3A00%3A00Z',
    );
    expect(
      [el(fixture, 'proposal-accept-pr1'), el(fixture, 'proposal-reject-pr2')].every(
        (b) => b !== null,
      ),
    ).toBe(true);
    expect([el(fixture, 'proposal-accept-pr3'), el(fixture, 'proposal-reject-pr3')]).toEqual([
      null,
      null,
    ]);
    expect(text(fixture, 'proposal-status-pr3')).toBe('rejected');
  });

  it('Accept and Reject send the decision and show the project the control plane returns; a refusal shows its reason', async () => {
    const proposal = {
      id: 'pr1',
      kind: 'title',
      text: 'Short title',
      reason: 'r',
      status: 'proposed',
      thumbnailFile: null,
    };
    const accepted = project({
      publication: publication({ proposals: [{ ...proposal, status: 'accepted' }] }),
    });
    const bridge = makeBridge([project({ publication: publication({ proposals: [proposal] }) })], {
      operatorDecideProposal: vi.fn(async () => accepted),
    });
    const fixture = await mount(bridge, 'p1');

    el(fixture, 'proposal-accept-pr1')!.click();
    await settle(fixture);
    bridge.operatorDecideProposal.mockImplementation(async () => {
      throw new Error(
        `Error invoking remote method 'scout:operator.proposalDecision': Error: ${encodeIpcErrorMessage(
          serializeError(new ControlPlaneInvalidArgumentError('The proposal was already decided.')),
        )}`,
      );
    });
    await (fixture.componentInstance as ReportsComponent).decide(accepted, 'pr1', false);
    await settle(fixture);

    expect(bridge.operatorDecideProposal.mock.calls).toEqual([
      [{ projectId: 'p1', proposalId: 'pr1', accept: true }],
      [{ projectId: 'p1', proposalId: 'pr1', accept: false }],
    ]);
    expect(text(fixture, 'proposal-status-pr1')).toBe('accepted');
    expect(text(fixture, 'proposal-error')).toBe('The proposal was already decided.');
  });
});
