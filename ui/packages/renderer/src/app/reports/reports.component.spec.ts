import { readFileSync } from 'node:fs';

import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneInvalidArgumentError,
  encodeIpcErrorMessage,
  serializeError,
  type OperatorProjectDto,
  type ProposalDto,
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

function curve(values: number[]): SnapshotDto['retention'] {
  return values.map((v, i) => ({
    elapsedRatio: i / (values.length - 1),
    audienceWatchRatio: v / 100,
  }));
}

const DAY: SnapshotDto = {
  window: '24h',
  takenAt: '2026-11-05T18:00:00Z',
  views: 1284,
  minutesWatched: 6410,
  averageViewDurationSeconds: 299,
  averageViewPercentage: 60.8,
  subscribersGained: 37,
  subscribersLost: 2,
  likes: 96,
  comments: 14,
  shares: 9,
  impressions: 18920,
  impressionsCtr: 5.9,
  retention: curve([100, 78, 66, 61, 57, 54, 50, 47, 45, 41, 38]),
};

const WEEK: SnapshotDto = {
  window: '7d',
  takenAt: '2026-11-11T18:00:00Z',
  views: 6902,
  minutesWatched: 32115,
  averageViewDurationSeconds: 279,
  averageViewPercentage: 56.7,
  subscribersGained: 164,
  subscribersLost: 9,
  likes: 412,
  comments: 51,
  shares: 33,
  impressions: 121400,
  impressionsCtr: 4.8,
  retention: curve([100, 74, 63, 58, 54, 51, 48, 45, 42, 39, 35]),
};

const PROPOSALS: ProposalDto[] = [
  {
    id: 'pr1',
    kind: 'title',
    text: 'Docker volumes explained in 8 minutes',
    reason:
      'Click-through fell from 5.9% to 4.8% after day one; a title that names the payoff tested better on similar videos.',
    status: 'proposed',
    thumbnailFile: null,
  },
  {
    id: 'pr2',
    kind: 'thumbnail',
    text: 'Bind vs Volume',
    reason:
      'Viewers drop at 0:48, where the thumbnail promise is answered too late; this one matches the first scene.',
    status: 'proposed',
    thumbnailFile: 'thumbnail-proposal-pr2.png',
  },
  {
    id: 'pr3',
    kind: 'topic',
    text: 'Docker Compose volumes in practice',
    reason: 'Three comments asked how volumes work with Compose.',
    status: 'accepted',
    thumbnailFile: null,
  },
];

function publication(overrides: Partial<PublicationDto> = {}): PublicationDto {
  return {
    status: 'published',
    videoId: 'dQw4w9WgXcQ',
    privacyStatus: 'public',
    publishAt: null,
    madeForKids: false,
    syntheticMedia: true,
    uploadedAt: '2026-11-04T18:00:00Z',
    error: null,
    snapshots: [DAY, WEEK],
    proposals: PROPOSALS,
    experiment: null,
    ...overrides,
  };
}

function project(overrides: Partial<OperatorProjectDto> = {}): OperatorProjectDto {
  return {
    id: 'p1',
    topic: 'Docker volumes in 8 minutes',
    status: 'approved',
    origin: 'desktop',
    createdAt: '2026-11-03T09:00:00Z',
    updatedAt: '2026-11-04T18:00:00Z',
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
  publication: publication({
    uploadedAt: '2026-11-02T18:00:00Z',
    snapshots: [{ ...DAY, views: 412, retention: [] }],
    proposals: [{ ...PROPOSALS[0]!, id: 'pr9', text: 'Rebase without the fear' }],
  }),
});

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

async function mount(bridge: Bridge): Promise<ComponentFixture<ReportsComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(ReportsComponent);
  await settle(fixture);
  return fixture;
}

function el<T extends Element = HTMLElement>(
  fixture: ComponentFixture<ReportsComponent>,
  testId: string,
): T | null {
  return fixture.nativeElement.querySelector(`[data-test="${testId}"]`) as T | null;
}

async function click(fixture: ComponentFixture<ReportsComponent>, testId: string) {
  (el(fixture, testId) as HTMLElement).click();
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

/** What the owner sees, in order: headings, buttons with their state, links, labels, fields, images by their name, list items and text. */
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
    if (tag === 'select') {
      const options = Array.from(node.querySelectorAll('option')).map((o) => norm(o.textContent));
      return void lines.push(`select: ${options.join(' | ')}`);
    }
    const own = Array.from(node.childNodes)
      .filter((n) => n.nodeType === Node.TEXT_NODE)
      .map((n) => n.textContent)
      .join('');
    if (tag === 'li') {
      lines.push('item:' + (norm(own) === '' ? '' : ' ' + norm(own)));
    } else if (norm(own) !== '' && tag !== 'option' && tag !== 'text') {
      return void lines.push(`text: ${textOf(node)}`);
    }
    for (const child of Array.from(node.children)) walk(child);
  };
  if (root !== null) walk(root);
  return lines;
}

function seen(fixture: ComponentFixture<ReportsComponent>): string {
  return outline((fixture.nativeElement as HTMLElement).querySelector('main')).join('\n');
}

const HEADER = `h1: Reports
text: What each published video did, as YouTube reported it, and what Scout proposes.
button: Refresh`;

const PICKER = `label: Video
select: Docker volumes in 8 minutes · published 2026-11-04 18:00 | Git rebase without fear · published 2026-11-02 18:00
text: The newest published video is chosen for you.`;

function windowCard(title: string, s: SnapshotDto, last: number): string {
  const tiles: [string, number][] = [
    ['Views', s.views],
    ['Minutes watched', s.minutesWatched],
    ['Average view duration (seconds)', s.averageViewDurationSeconds],
    ['Average view percentage', s.averageViewPercentage],
    ['Subscribers gained', s.subscribersGained],
    ['Subscribers lost', s.subscribersLost],
    ['Likes', s.likes],
    ['Comments', s.comments],
    ['Shares', s.shares],
    ['Impressions', s.impressions!],
    ['Click-through rate', s.impressionsCtr!],
  ];
  return [
    `h2: ${title}`,
    `text: Taken ${s.takenAt.slice(0, 10)} 18:00 London time`,
    ...tiles.flatMap(([label, value]) => [`text: ${label}`, `text: ${value}`]),
    'h3: Audience retention',
    `img: Audience retention over the ${title.toLowerCase()}: starts at 100%, ends at ${last}%`,
    'text: Viewers still watching',
    'text: Scene starts',
  ].join('\n');
}

const WINDOWS = `${windowCard('First 24 hours', DAY, 38)}\n${windowCard('First 7 days', WEEK, 35)}`;

function proposals(pr1Buttons: string, pr2Buttons: string, ...before: string[]): string {
  return [
    'h2: What Scout proposes',
    ...before,
    'item:',
    'text: New title',
    `text: ${PROPOSALS[0]!.text}`,
    'text: Waiting for you',
    `text: ${PROPOSALS[0]!.reason}`,
    pr1Buttons,
    'item:',
    'img: Proposed thumbnail: Bind vs Volume',
    'text: New thumbnail',
    'text: Bind vs Volume',
    'text: Waiting for you',
    `text: ${PROPOSALS[1]!.reason}`,
    pr2Buttons,
    'item:',
    'text: New topic',
    `text: ${PROPOSALS[2]!.text}`,
    'text: Accepted',
    `text: ${PROPOSALS[2]!.reason}`,
  ].join('\n');
}

const IDLE = 'button: Accept\nbutton: Reject';
const OFF = 'button: Accept [disabled]\nbutton: Reject [disabled]';

function refusal(message: string) {
  return async () => {
    throw new Error(
      `Error invoking remote method 'scout:operator.proposalDecision': Error: ${encodeIpcErrorMessage(
        serializeError(new ControlPlaneInvalidArgumentError(message)),
      )}`,
    );
  };
}

describe('Reports as Reports.dc.html draws it, state by state', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('Loading: the loading card under the header until the first read answers', async () => {
    const fixture = await mount(
      makeBridge([], { operatorProjects: vi.fn(() => new Promise(() => undefined)) }),
    );

    expect(seen(fixture)).toBe(`${HEADER}\nstatus: Loading published videos`);
  });

  it('No published video: only videos on YouTube count; the sentence and Go to Assembly', async () => {
    const fixture = await mount(
      makeBridge([
        project({ publication: publication({ videoId: null }) }),
        project({ id: 'p3', publication: null }),
      ]),
    );

    expect(seen(fixture)).toBe(
      `${HEADER}
text: No video is on YouTube yet. Publish one from the Assembly page.
link: Go to Assembly`,
    );
    expect(el(fixture, 'reports-assembly')!.getAttribute('href')).toBe('/assembly');
  });

  it('Numbers and proposals: the newest video chosen, both windows as tiles with the numbers as returned, and what Scout proposes', async () => {
    const fixture = await mount(makeBridge([OLDER, project()]));

    expect(seen(fixture)).toBe(`${HEADER}\n${PICKER}\n${WINDOWS}\n${proposals(IDLE, IDLE)}`);
  });

  it('Saving a decision: every Accept and Reject off, the line on the proposal being decided', async () => {
    const fixture = await mount(
      makeBridge([project(), OLDER], {
        operatorDecideProposal: vi.fn(() => new Promise(() => undefined)),
      }),
    );

    await click(fixture, 'proposal-accept-pr1');

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${WINDOWS}\n${proposals(`${OFF}\ntext: Saving your decision...`, OFF)}`,
    );
  });

  it('First day only: the 7-day window says it has no numbers yet, and no proposals yet', async () => {
    const fixture = await mount(
      makeBridge([
        project({ publication: publication({ snapshots: [DAY], proposals: [] }) }),
        OLDER,
      ]),
    );

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${windowCard('First 24 hours', DAY, 38)}
h2: First 7 days
text: No 7d numbers yet.
h2: What Scout proposes
text: No proposals yet.`,
    );
  });

  it('Decision refused: the control plane reason under the heading, the proposal still open', async () => {
    const fixture = await mount(
      makeBridge([project(), OLDER], {
        operatorDecideProposal: vi.fn(refusal('The proposal was already decided.')),
      }),
    );

    await click(fixture, 'proposal-accept-pr1');

    expect(seen(fixture)).toBe(
      `${HEADER}\n${PICKER}\n${WINDOWS}\n${proposals(IDLE, IDLE, 'text: The proposal was already decided.')}`,
    );
  });

  it('Read failed: the control plane sentence over the last list, which stays', async () => {
    vi.useFakeTimers({ toFake: ['setInterval'] });
    const bridge = makeBridge([project(), OLDER]);
    const fixture = await mount(bridge);
    bridge.operatorProjects.mockImplementation(async () => {
      throw new Error('The Scout control plane did not answer.');
    });

    vi.advanceTimersByTime(10_000);
    await settle(fixture);

    expect(seen(fixture)).toBe(
      `${HEADER}\ntext: The Scout control plane did not answer.\n${PICKER}\n${WINDOWS}\n${proposals(IDLE, IDLE)}`,
    );
  });
});

describe('Reports: what a choice or a decision asks for', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('choosing another video shows its numbers and proposals, asks nothing new, keeps the choice through the 10 s refresh, and a decision there names that video', async () => {
    vi.useFakeTimers({ toFake: ['setInterval'] });
    const bridge = makeBridge([project(), OLDER]);
    const fixture = await mount(bridge);
    const picker = el<HTMLSelectElement>(fixture, 'reports-project')!;

    picker.value = 'p2';
    picker.dispatchEvent(new Event('change'));
    await settle(fixture);
    const reads = bridge.operatorProjects.mock.calls.length;
    vi.advanceTimersByTime(10_000);
    await settle(fixture);
    expect({
      reads,
      views: el(fixture, 'snapshot-24h')!.querySelector('.tile-value')!.textContent,
      chosen: el<HTMLSelectElement>(fixture, 'reports-project')!.value,
      proposals: Array.from(
        fixture.nativeElement.querySelectorAll('[data-test^="proposal-pr"]'),
      ).map((li) => (li as Element).getAttribute('data-test')),
    }).toEqual({ reads: 1, views: '412', chosen: 'p2', proposals: ['proposal-pr9'] });
    await click(fixture, 'proposal-accept-pr9');

    expect(bridge.operatorDecideProposal.mock.calls).toEqual([
      [{ projectId: 'p2', proposalId: 'pr9', accept: true }],
    ]);
  });

  it('Accept and Reject send the decision and show the project the control plane returns; a refusal shows its reason', async () => {
    const accepted = project({
      publication: publication({
        proposals: [{ ...PROPOSALS[0]!, status: 'accepted' }, PROPOSALS[1]!, PROPOSALS[2]!],
      }),
    });
    const bridge = makeBridge([project()], {
      operatorDecideProposal: vi.fn(async () => accepted),
    });
    const fixture = await mount(bridge);

    await click(fixture, 'proposal-accept-pr1');
    const after = norm(el(fixture, 'proposal-status-pr1')!.textContent);
    bridge.operatorDecideProposal.mockImplementation(refusal('The proposal was already decided.'));
    await click(fixture, 'proposal-reject-pr2');

    expect(bridge.operatorDecideProposal.mock.calls).toEqual([
      [{ projectId: 'p1', proposalId: 'pr1', accept: true }],
      [{ projectId: 'p1', proposalId: 'pr2', accept: false }],
    ]);
    expect([after, norm(el(fixture, 'proposal-error')!.textContent)]).toEqual([
      'Accepted',
      'The proposal was already decided.',
    ]);
  });

  it('Refresh asks for the projects again and shows what came back', async () => {
    const bridge = makeBridge([project()]);
    const fixture = await mount(bridge);
    bridge.operatorProjects.mockImplementation(async () => ({
      projects: [project({ publication: publication({ snapshots: [{ ...DAY, views: 2000 }] }) })],
    }));

    await click(fixture, 'reports-refresh');

    expect(bridge.operatorProjects.mock.calls).toEqual([[{ limit: 50 }], [{ limit: 50 }]]);
    expect(el(fixture, 'snapshot-24h')!.querySelector('.tile-value')!.textContent).toBe('2000');
  });

  it('the retention line is drawn from the points as returned on the plot area, with a dashed mark where each scene ends and the length under it', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          publication: publication({
            snapshots: [
              {
                ...DAY,
                retention: [
                  { elapsedRatio: 0, audienceWatchRatio: 1 },
                  { elapsedRatio: 0.5, audienceWatchRatio: 0.42 },
                  { elapsedRatio: 1, audienceWatchRatio: 0.1 },
                ],
              },
            ],
          }),
        }),
      ]),
    );

    const marks = Array.from(
      fixture.nativeElement.querySelectorAll('[data-test^="scene-mark-24h-"]'),
    ).map((m) => (m as Element).getAttribute('x1'));
    const labels = Array.from(fixture.nativeElement.querySelectorAll('svg text')).map((t) =>
      norm((t as Element).textContent),
    );
    expect(el(fixture, 'retention-line-24h')!.getAttribute('points')).toBe(
      '40,10 225,85.4 410,127',
    );
    expect(marks).toEqual(['114', '225']);
    expect(labels).toEqual(['100%', '50%', '0%', '0:00', '0:05', '0:10']);
    expect(el(fixture, 'retention-line-7d')).toBeNull();
  });

  it('a retention ratio above 1 raises the top of the chart instead of clipping it, and its label says so', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          scenes: [],
          publication: publication({
            snapshots: [
              {
                ...DAY,
                retention: [
                  { elapsedRatio: 0, audienceWatchRatio: 1.25 },
                  { elapsedRatio: 1, audienceWatchRatio: 0.5 },
                ],
              },
            ],
          }),
        }),
      ]),
    );

    const labels = Array.from(fixture.nativeElement.querySelectorAll('svg text')).map((t) =>
      norm((t as Element).textContent),
    );
    expect(el(fixture, 'retention-line-24h')!.getAttribute('points')).toBe('40,10 410,88');
    expect(labels).toEqual(['125%', '63%', '0%']);
    expect(fixture.nativeElement.querySelectorAll('[data-test^="scene-mark-"]').length).toBe(0);
  });

  it('a thumbnail proposal is previewed through scout-media, and text from YouTube renders as text', async () => {
    const markup = '<b>Span<T></b>';
    const fixture = await mount(
      makeBridge([
        project({
          publication: publication({ proposals: [{ ...PROPOSALS[1]!, text: markup }] }),
        }),
      ]),
    );

    expect(el<HTMLImageElement>(fixture, 'proposal-thumbnail-pr2')!.getAttribute('src')).toBe(
      'scout-media://p1/thumbnail-proposal-pr2.png?v=2026-11-04T18%3A00%3A00Z',
    );
    expect(norm(el(fixture, 'proposal-pr2')!.querySelector('strong')!.textContent)).toBe(markup);
    expect(el(fixture, 'proposal-pr2')!.querySelector('b')).toBeNull();
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

describe('Reports follows the theme and the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const file of ['src/app/reports/reports.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }

  it('Refresh, Accept and Reject show their icon before their text, as the prototype draws them', async () => {
    const fixture = await mount(makeBridge([project()]));

    expect(
      ['reports-refresh', 'proposal-accept-pr1', 'proposal-reject-pr1'].map((id) => {
        const icon = el(fixture, id)!.querySelector('sc-icon')!;
        return [
          icon.nextSibling?.textContent?.trim(),
          icon.previousSibling === null || norm(icon.previousSibling.textContent) === '',
        ];
      }),
    ).toEqual([
      ['Refresh', true],
      ['Accept', true],
      ['Reject', true],
    ]);
  });
});
