import { formatDate } from '@angular/common';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlanePermissionDeniedError,
  ControlPlaneUnavailableError,
  encodeIpcErrorMessage,
  serializeError,
  ControlPlaneInvalidArgumentError,
  type MandateDto,
  type OperatorProjectDto,
  type ReadinessDto,
  type SceneDto,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { ProductionComponent } from './production.component';

const mandate: MandateDto = {
  objective: 'Grow a .NET channel',
  audience: 'Backend developers',
  pillars: ['Performance', 'Testing'],
  cadence: 'weekly',
  language: 'en',
  voiceAndStyle: 'calm',
  blockedTopics: ['politics', 'crypto'],
  targetMetric: 'watch_hours',
  targetValue: '500',
  updatedAt: '2026-11-03T09:00:00Z',
};

function project(overrides: Partial<OperatorProjectDto> = {}): OperatorProjectDto {
  return {
    id: 'p1',
    topic: 'Span<T> in C#',
    status: 'script_ready',
    origin: 'desktop',
    createdAt: '2026-11-02T09:00:00Z',
    updatedAt: '2026-11-03T09:05:00Z',
    candidates: [{ title: 'Span<T> without fear', reason: 'Fits Performance', fit: 0.82 }],
    script: {
      hook: 'Your allocations are lying to you.',
      sections: [{ heading: 'Why Span', narration: 'Span is a view over memory.' }],
      callToAction: 'Subscribe for more.',
      titleVariants: ['Span<T> explained', 'Stop allocating'],
      description: 'A tour of Span<T>.',
      tags: ['dotnet', 'span'],
      chapters: [{ at: '00:00', title: 'Intro' }],
    },
    claims: [
      {
        id: 'c1',
        text: 'Span<T> is a ref struct.',
        risk: 'low',
        sourceUrls: ['https://learn.microsoft.com/dotnet/api/system.span-1'],
        status: 'pending',
        note: null,
      },
    ],
    error: null,
    scenes: [],
    package: null,
    ...overrides,
  };
}

function scene(overrides: Partial<SceneDto> = {}): SceneDto {
  return {
    id: 's1',
    order: 1,
    narration: 'Span is a view over memory.',
    visualPrompt: 'A ruler laid over a row of bytes',
    locked: false,
    status: 'ready',
    error: null,
    audioSource: 'generated',
    visualSource: 'owner',
    audioFile: 'scene-s1.mp3',
    visualFile: 'owner-s1-0a1b2c3d4e5f.png',
    durationMs: 4200,
    ...overrides,
  };
}

const readiness: ReadinessDto = {
  speechAvailable: false,
  imagesAvailable: true,
  provider: 'openai',
  ffmpegAvailable: true,
  youtubeSignedIn: false,
  notes: ['Narration comes from your own audio files with this provider.'],
  reportedAt: '2026-11-03T09:00:00Z',
};

function refusedWith(channel: string, message: string): Error {
  return new Error(
    `Error invoking remote method '${channel}': Error: ${encodeIpcErrorMessage(
      serializeError(new ControlPlaneInvalidArgumentError(message)),
    )}`,
  );
}

function ipcFailure(channel: string, message: string): Error {
  return new Error(
    `Error invoking remote method '${channel}': Error: ${encodeIpcErrorMessage(
      serializeError(new ControlPlaneUnavailableError(message)),
    )}`,
  );
}

function makeBridge(overrides: Record<string, unknown> = {}) {
  return {
    operatorMandate: vi.fn(async () => ({ mandate: null as MandateDto | null })),
    operatorSaveMandate: vi.fn(async () => mandate),
    operatorProjects: vi.fn(async () => ({ projects: [] as OperatorProjectDto[] })),
    operatorProject: vi.fn(async () => project()),
    operatorStartProject: vi.fn(async () =>
      project({ id: 'p9', topic: 'Records in C#', status: 'queued', script: null, claims: [] }),
    ),
    operatorDecideClaim: vi.fn(async () => project()),
    operatorReadiness: vi.fn(async () => readiness),
    operatorProduce: vi.fn(async () => project({ status: 'production_queued' })),
    operatorReorderScenes: vi.fn(async () => project()),
    operatorSetSceneLocked: vi.fn(async () => project()),
    operatorRegenerateScene: vi.fn(async () => project()),
    operatorAttachSceneMedia: vi.fn(async () => ({
      project: project() as OperatorProjectDto | null,
    })),
    ...overrides,
  };
}

type Bridge = ReturnType<typeof makeBridge>;

function listing(...projects: OperatorProjectDto[]) {
  return {
    operatorProjects: vi.fn(async () => ({ projects })),
    operatorProject: vi.fn(async ({ id }: { id: string }) => projects.find((p) => p.id === id)!),
  };
}

async function mount(bridge: Bridge): Promise<ComponentFixture<ProductionComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(ProductionComponent);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<ProductionComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el<T extends HTMLElement = HTMLElement>(
  fixture: ComponentFixture<ProductionComponent>,
  testId: string,
): T | null {
  return fixture.nativeElement.querySelector(`[data-test="${testId}"]`) as T | null;
}

function text(fixture: ComponentFixture<ProductionComponent>, testId: string): string {
  return (el(fixture, testId)?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function type(fixture: ComponentFixture<ProductionComponent>, testId: string, value: string): void {
  const input = el<HTMLInputElement>(fixture, testId)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

async function click(
  fixture: ComponentFixture<ProductionComponent>,
  testId: string,
): Promise<void> {
  el(fixture, testId)!.click();
  await settle(fixture);
}

describe('ProductionComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('explains that the operator needs a mandate while none is saved, and says there are no videos yet', async () => {
    const fixture = await mount(makeBridge());

    expect(text(fixture, 'mandate-empty')).toBe(
      'The operator needs a mandate before it makes a video: what the channel is for, who it is for and its language. Fill it in and save it.',
    );
    expect(text(fixture, 'projects-empty')).toBe(
      'No videos yet. Ask for one above, or ask Scout in chat.',
    );
    expect(el(fixture, 'mandate-saved')).toBeNull();
  });

  it('save mandate sends the trimmed fields, the pillars filled in and the blocked topics split on commas, and shows the saved time', async () => {
    const bridge = makeBridge();
    const fixture = await mount(bridge);

    type(fixture, 'mandate-objective', '  Grow a .NET channel ');
    type(fixture, 'mandate-audience', ' Backend developers ');
    type(fixture, 'mandate-pillar-0', ' Performance ');
    type(fixture, 'mandate-pillar-2', 'Testing');
    type(fixture, 'mandate-cadence', ' weekly');
    type(fixture, 'mandate-language', 'en ');
    type(fixture, 'mandate-voice', ' calm ');
    type(fixture, 'mandate-blocked', ' politics, , crypto ');
    const metric = el<HTMLSelectElement>(fixture, 'mandate-metric')!;
    metric.value = 'watch_hours';
    metric.dispatchEvent(new Event('change'));
    type(fixture, 'mandate-target', ' 500 ');
    await click(fixture, 'mandate-save');

    const { updatedAt: _stamp, ...fields } = mandate;
    expect(bridge.operatorSaveMandate.mock.calls).toEqual([[fields]]);
    expect(text(fixture, 'mandate-saved')).toBe(
      `Saved ${formatDate(mandate.updatedAt!, 'yyyy-MM-dd HH:mm', 'en-US')}`,
    );
    expect(el(fixture, 'mandate-empty')).toBeNull();
  });

  it('a saved mandate fills the form and shows when it was saved', async () => {
    const fixture = await mount(makeBridge({ operatorMandate: vi.fn(async () => ({ mandate })) }));

    expect(el<HTMLInputElement>(fixture, 'mandate-objective')!.value).toBe('Grow a .NET channel');
    expect(el<HTMLInputElement>(fixture, 'mandate-pillar-1')!.value).toBe('Testing');
    expect(el<HTMLInputElement>(fixture, 'mandate-blocked')!.value).toBe('politics, crypto');
    expect(el<HTMLSelectElement>(fixture, 'mandate-metric')!.value).toBe('watch_hours');
    expect(text(fixture, 'mandate-saved')).toBe(
      `Saved ${formatDate(mandate.updatedAt!, 'yyyy-MM-dd HH:mm', 'en-US')}`,
    );
    expect(el(fixture, 'mandate-empty')).toBeNull();
  });

  it('a refused mandate shows the control plane reason', async () => {
    const bridge = makeBridge({
      operatorSaveMandate: vi.fn(async () => {
        throw new Error(
          `Error invoking remote method 'scout:operator.saveMandate': Error: [scout:operator/invalid-request] The objective is required.`,
        );
      }),
    });
    const fixture = await mount(bridge);

    await click(fixture, 'mandate-save');

    expect(text(fixture, 'mandate-error')).toBe('The objective is required.');
    expect(el(fixture, 'mandate-saved')).toBeNull();
  });

  it('start project sends the trimmed topic and shows the queued project, which joins the list', async () => {
    const queued = project({
      id: 'p9',
      topic: 'Records in C#',
      status: 'queued',
      createdAt: '2026-11-03T10:00:00Z',
      script: null,
      claims: [],
    });
    let listed: OperatorProjectDto[] = [];
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: listed })),
      operatorStartProject: vi.fn(async () => {
        listed = [queued];
        return queued;
      }),
    });
    const fixture = await mount(bridge);

    type(fixture, 'topic', '  Records in C# ');
    await settle(fixture);
    await click(fixture, 'start-project');

    expect(bridge.operatorStartProject.mock.calls).toEqual([[{ topic: 'Records in C#' }]]);
    expect(text(fixture, 'queued')).toBe('Records in C# queued');
    expect(text(fixture, 'project-p9')).toContain('queued');
    expect(el<HTMLInputElement>(fixture, 'topic')!.value).toBe('');
  });

  it('a start the owner has not granted shows the write gate reason in the Make a video area', async () => {
    const reason =
      'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.';
    const bridge = makeBridge({
      operatorStartProject: vi.fn(async () => {
        throw new Error(
          `Error invoking remote method 'scout:operator.startProject': Error: ${encodeIpcErrorMessage(
            serializeError(new ControlPlanePermissionDeniedError(reason)),
          )}`,
        );
      }),
    });
    const fixture = await mount(bridge);

    type(fixture, 'topic', 'Records in C#');
    await settle(fixture);
    await click(fixture, 'start-project');

    expect(text(fixture, 'start-error')).toBe(reason);
    expect(el(fixture, 'queued')).toBeNull();
    expect(el(fixture, 'production-error')).toBeNull();
  });

  it('Make a video stays disabled until the topic has 3 characters', async () => {
    const fixture = await mount(makeBridge());

    type(fixture, 'topic', ' ab ');
    await settle(fixture);
    const short = el<HTMLButtonElement>(fixture, 'start-project')!.disabled;
    type(fixture, 'topic', 'abc');
    await settle(fixture);

    expect([short, el<HTMLButtonElement>(fixture, 'start-project')!.disabled]).toEqual([
      true,
      false,
    ]);
  });

  it('lists projects newest first with topic, status and updated time, and a selection shows candidates, script and claims', async () => {
    const older = project({ id: 'p0', topic: 'Older video', createdAt: '2026-11-01T09:00:00Z' });
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: [older, project()] })),
    });
    const fixture = await mount(bridge);

    const order = Array.from(
      fixture.nativeElement.querySelectorAll('.project strong') as NodeListOf<HTMLElement>,
    ).map((n) => n.textContent?.trim());
    const row = Array.from(el(fixture, 'project-p1')!.children).map((n) => n.textContent?.trim());
    await click(fixture, 'project-p1');
    const detail = text(fixture, 'project-detail');

    expect(order).toEqual(['Span<T> in C#', 'Older video']);
    expect(row).toEqual([
      'Span<T> in C#',
      'script_ready',
      formatDate('2026-11-03T09:05:00Z', 'yyyy-MM-dd HH:mm', 'en-US'),
    ]);
    expect(bridge.operatorProject.mock.calls).toEqual([[{ id: 'p1' }]]);
    for (const part of [
      'Span<T> without fear',
      '82%',
      'Fits Performance',
      'Hook: Your allocations are lying to you.',
      'Why Span',
      'Span is a view over memory.',
      'Call to action: Subscribe for more.',
      'Span<T> explained',
      'Stop allocating',
      'Description: A tour of Span<T>.',
      'Tags: dotnet, span',
      '00:00 Intro',
      'Span<T> is a ref struct.',
      'Risk: low',
      'https://learn.microsoft.com/dotnet/api/system.span-1',
    ]) {
      expect(detail).toContain(part);
    }
    expect(text(fixture, 'claim-status-c1')).toBe('pending');
  });

  it('a selected project with nothing yet shows the empty candidates, script and claims states', async () => {
    const bare = project({ candidates: [], script: null, claims: [] });
    const fixture = await mount(
      makeBridge({
        operatorProjects: vi.fn(async () => ({ projects: [bare] })),
        operatorProject: vi.fn(async () => bare),
      }),
    );

    await click(fixture, 'project-p1');

    expect([
      text(fixture, 'candidates-empty'),
      text(fixture, 'script-empty'),
      text(fixture, 'claims-empty'),
    ]).toEqual([
      'No topic candidates yet.',
      'The script is not written yet.',
      'No claims to check.',
    ]);
  });

  it('a source link is shown as text with a copy button, never as a link the window could follow', async () => {
    const writeText = vi.fn(async () => undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const fixture = await mount(
      makeBridge({ operatorProjects: vi.fn(async () => ({ projects: [project()] })) }),
    );
    await click(fixture, 'project-p1');

    const claim = el(fixture, 'claim-c1')!;
    (Array.from(claim.querySelectorAll('button')) as HTMLButtonElement[])
      .find((b) => b.textContent?.trim() === 'Copy')!
      .click();

    expect(claim.querySelector('a')).toBeNull();
    expect(claim.querySelector('code')?.textContent).toBe(
      'https://learn.microsoft.com/dotnet/api/system.span-1',
    );
    expect(writeText.mock.calls).toEqual([
      ['https://learn.microsoft.com/dotnet/api/system.span-1'],
    ]);
  });

  it('Supported and Unsupported send the claim status and show the decided claim', async () => {
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: [project()] })),
      operatorDecideClaim: vi.fn(async (req: { status: string }) =>
        project({ claims: [{ ...project().claims[0]!, status: req.status }] }),
      ),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'supported-c1');
    const afterSupported = text(fixture, 'claim-status-c1');
    await click(fixture, 'unsupported-c1');

    expect(bridge.operatorDecideClaim.mock.calls).toEqual([
      [{ projectId: 'p1', claimId: 'c1', status: 'supported' }],
      [{ projectId: 'p1', claimId: 'c1', status: 'unsupported' }],
    ]);
    expect([afterSupported, text(fixture, 'claim-status-c1')]).toEqual([
      'supported',
      'unsupported',
    ]);
  });

  it('Waive needs a note: the confirm stays disabled while the note is empty, and the trimmed note is sent', async () => {
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: [project()] })),
      operatorDecideClaim: vi.fn(async () =>
        project({
          claims: [{ ...project().claims[0]!, status: 'waived', note: 'Common knowledge' }],
        }),
      ),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'waive-c1');
    const emptyDisabled = el<HTMLButtonElement>(fixture, 'waive-confirm-c1')!.disabled;
    type(fixture, 'waive-note-c1', '   ');
    await settle(fixture);
    const blankDisabled = el<HTMLButtonElement>(fixture, 'waive-confirm-c1')!.disabled;
    type(fixture, 'waive-note-c1', '  Common knowledge ');
    await settle(fixture);
    await click(fixture, 'waive-confirm-c1');

    expect([emptyDisabled, blankDisabled]).toEqual([true, true]);
    expect(bridge.operatorDecideClaim.mock.calls).toEqual([
      [{ projectId: 'p1', claimId: 'c1', status: 'waived', note: 'Common knowledge' }],
    ]);
    expect(text(fixture, 'claim-c1')).toContain('waived · Common knowledge');
    expect(el(fixture, 'waive-note-c1')).toBeNull();
  });

  it('refreshes the list every 10 seconds while open and takes the selected project from it, with one list call and no project call per tick', async () => {
    vi.useFakeTimers();
    let listed = project({ status: 'researching' });
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: [listed] })),
      operatorProject: vi.fn(async () => project({ status: 'researching' })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    listed = project({ status: 'failed', error: 'The AI model did not answer.' });
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);

    expect(text(fixture, 'project-p1')).toContain('failed');
    expect(text(fixture, 'project-failure')).toBe('The AI model did not answer.');
    expect(bridge.operatorProjects).toHaveBeenCalledTimes(2);
    expect(bridge.operatorProject).toHaveBeenCalledTimes(1);
  });

  it('a selected project that left the list is read on its own at the next tick', async () => {
    vi.useFakeTimers();
    let listed = [project()];
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: listed })),
      operatorProject: vi.fn(async () => project({ status: 'failed', error: 'Read on its own.' })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    listed = [project({ id: 'p2', topic: 'Newer video' })];
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);

    expect(bridge.operatorProject.mock.calls).toEqual([[{ id: 'p1' }], [{ id: 'p1' }]]);
    expect(text(fixture, 'project-failure')).toBe('Read on its own.');
  });

  it('a control plane that goes down shows the error and keeps the last list and project', async () => {
    vi.useFakeTimers();
    let down = false;
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => {
        if (down) throw ipcFailure('scout:operator.projects', 'socket reset');
        return { projects: [project()] };
      }),
      operatorProject: vi.fn(async () => {
        if (down) throw ipcFailure('scout:operator.project', 'socket reset');
        return project();
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    down = true;
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);

    expect(text(fixture, 'production-error')).toBe('socket reset');
    expect(el(fixture, 'project-p1')).not.toBeNull();
    expect(text(fixture, 'project-detail')).toContain('Span<T> is a ref struct.');
  });
  it('the readiness banner says whether narration and pictures can be generated, the provider, the YouTube sign-in and the notes', async () => {
    const fixture = await mount(makeBridge());

    expect(text(fixture, 'readiness-speech')).toBe('Narration: not available');
    expect(text(fixture, 'readiness-images')).toBe('Generated pictures: available');
    expect(text(fixture, 'readiness-provider')).toBe('Provider: openai');
    expect(text(fixture, 'readiness-youtube')).toBe('YouTube: not signed in');
    expect(text(fixture, 'readiness-note')).toBe(
      'Narration comes from your own audio files with this provider.',
    );
  });

  it('Produce video media is disabled with the reason while claims are pending', async () => {
    const pending = project({
      claims: [
        { ...project().claims[0]!, id: 'c1' },
        { ...project().claims[0]!, id: 'c2' },
      ],
    });
    const bridge = makeBridge(listing(pending));
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    expect(el<HTMLButtonElement>(fixture, 'produce')!.disabled).toBe(true);
    expect(text(fixture, 'produce-blocked')).toBe(
      '2 claims are still pending; decide every claim before producing the video.',
    );
  });

  it('Produce video media is enabled on a script_ready or media_ready project with every claim decided, sends the project id and shows the new status', async () => {
    const decided = project({
      claims: [{ ...project().claims[0]!, status: 'supported' }],
    });
    const bridge = makeBridge(
      listing(decided, project({ id: 'p2', status: 'media_ready', claims: [] })),
    );
    const fixture = await mount(bridge);
    await click(fixture, 'project-p2');
    const onMediaReady = el<HTMLButtonElement>(fixture, 'produce')?.disabled;
    await click(fixture, 'project-p1');

    await click(fixture, 'produce');

    expect(onMediaReady).toBe(false);
    expect(el(fixture, 'produce-blocked')).toBeNull();
    expect(bridge.operatorProduce.mock.calls).toEqual([[{ id: 'p1' }]]);
    expect(text(fixture, 'project-p1')).toContain('production_queued');
  });

  it('a project still being scripted offers no Produce video media button', async () => {
    const bridge = makeBridge({
      ...listing(project({ status: 'scripting', claims: [] })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    expect(el(fixture, 'produce')).toBeNull();
  });

  it('a failed project offers Produce video media again when it has a script, and not without one', async () => {
    const failed = project({ status: 'failed', error: 'Speech timed out.', claims: [] });
    const unscripted = project({ id: 'p2', status: 'failed', script: null, claims: [] });
    const bridge = makeBridge({
      ...listing(failed, unscripted),
      operatorProduce: vi.fn(async () => project({ status: 'production_queued', claims: [] })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p2');
    const withoutScript = el(fixture, 'produce');
    await click(fixture, 'project-p1');

    await click(fixture, 'produce');

    expect(withoutScript).toBeNull();
    expect(bridge.operatorProduce.mock.calls).toEqual([[{ id: 'p1' }]]);
  });

  it('a refused produce shows the control plane reason', async () => {
    const reason = 'The owner has not allowed youtube to write.';
    const bridge = makeBridge({
      ...listing(project({ claims: [] })),
      operatorProduce: vi.fn(async () => {
        throw refusedWith('scout:operator.production', reason);
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'produce');

    expect(text(fixture, 'produce-error')).toBe(reason);
  });

  it('lists the scenes in their order with narration, picture prompt, status and where the audio and picture come from', async () => {
    const scenes = [
      scene({
        id: 's2',
        order: 2,
        narration: 'It never allocates.',
        status: 'failed',
        error: 'Speech is not available.',
        audioSource: 'none',
        visualSource: 'text_card',
        audioFile: null,
        visualFile: null,
      }),
      scene(),
    ];
    const bridge = makeBridge({
      ...listing(project({ scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    const order = Array.from(
      fixture.nativeElement.querySelectorAll(
        '[data-test="scene-narration"]',
      ) as NodeListOf<HTMLElement>,
    ).map((n) => n.textContent?.trim());

    expect(order).toEqual(['Span is a view over memory.', 'It never allocates.']);
    expect(text(fixture, 'scene-s1')).toContain('Picture prompt: A ruler laid over a row of bytes');
    expect(text(fixture, 'scene-status-s1')).toBe('ready');
    expect(text(fixture, 'scene-sources-s1')).toBe('Audio: generated · Picture: your file');
    expect(text(fixture, 'scene-status-s2')).toBe('failed');
    expect(text(fixture, 'scene-sources-s2')).toBe('Audio: none · Picture: text card');
    expect(text(fixture, 'scene-failure-s2')).toBe('Speech is not available.');
  });

  it('shows the error of a failed scene as a failure and the note of any other scene as a plain note', async () => {
    const scenes = [
      scene({ error: 'No picture: the connected AI Source has no image service in Scout.' }),
      scene({ id: 's2', order: 2, status: 'failed', error: 'Speech timed out.' }),
    ];
    const bridge = makeBridge({
      ...listing(project({ scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    expect(text(fixture, 'scene-note-s1')).toBe(
      'No picture: the connected AI Source has no image service in Scout.',
    );
    expect(el(fixture, 'scene-note-s1')!.classList.contains('err')).toBe(false);
    expect(el(fixture, 'scene-failure-s1')).toBeNull();
    expect(text(fixture, 'scene-failure-s2')).toBe('Speech timed out.');
    expect(el(fixture, 'scene-failure-s2')!.classList.contains('err')).toBe(true);
    expect(el(fixture, 'scene-note-s2')).toBeNull();
  });

  it('previews a scene image and audio through scout-media:// with the project id and the file name, never a file path', async () => {
    const scenes = [scene(), scene({ id: 's2', order: 2, audioFile: null, visualFile: null })];
    const bridge = makeBridge({
      ...listing(project({ scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    expect(el(fixture, 'scene-image-s1')!.getAttribute('src')).toBe(
      'scout-media://p1/owner-s1-0a1b2c3d4e5f.png?v=2026-11-03T09%3A05%3A00Z',
    );
    expect(el(fixture, 'scene-image-s1')!.getAttribute('alt')).toBe('Picture of scene 1');
    expect(el(fixture, 'scene-audio-s1')!.getAttribute('src')).toBe(
      'scout-media://p1/scene-s1.mp3?v=2026-11-03T09%3A05%3A00Z',
    );
    expect(el<HTMLAudioElement>(fixture, 'scene-audio-s1')!.controls).toBe(true);
    expect([el(fixture, 'scene-image-s2'), el(fixture, 'scene-audio-s2')]).toEqual([null, null]);
  });

  it('move up and move down send the scene ids in the new order', async () => {
    const scenes = [
      scene({ id: 's1', order: 1 }),
      scene({ id: 's2', order: 2 }),
      scene({ id: 's3', order: 3 }),
    ];
    const bridge = makeBridge({
      ...listing(project({ scenes })),
      operatorReorderScenes: vi.fn(async () => project({ scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const edges = [
      el<HTMLButtonElement>(fixture, 'scene-up-s1')!.disabled,
      el<HTMLButtonElement>(fixture, 'scene-down-s3')!.disabled,
    ];

    await click(fixture, 'scene-down-s1');
    await click(fixture, 'scene-up-s3');

    expect(edges).toEqual([true, true]);
    expect(bridge.operatorReorderScenes.mock.calls).toEqual([
      [{ projectId: 'p1', sceneIds: ['s2', 's1', 's3'] }],
      [{ projectId: 'p1', sceneIds: ['s1', 's3', 's2'] }],
    ]);
  });

  it('the lock toggle sends locked and unlocked, and Regenerate is disabled on a locked scene and sends the scene otherwise', async () => {
    const scenes = [scene({ id: 's1', locked: true }), scene({ id: 's2', order: 2 })];
    const bridge = makeBridge({
      ...listing(project({ scenes })),
      operatorSetSceneLocked: vi.fn(async () => project({ scenes })),
      operatorRegenerateScene: vi.fn(async () => project({ scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const labels = [text(fixture, 'scene-lock-s1'), text(fixture, 'scene-lock-s2')];

    await click(fixture, 'scene-lock-s1');
    await click(fixture, 'scene-lock-s2');
    await click(fixture, 'scene-regenerate-s2');

    expect(labels).toEqual(['Unlock', 'Lock']);
    expect(el<HTMLButtonElement>(fixture, 'scene-regenerate-s1')!.disabled).toBe(true);
    expect(bridge.operatorSetSceneLocked.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1', locked: false }],
      [{ projectId: 'p1', sceneId: 's2', locked: true }],
    ]);
    expect(bridge.operatorRegenerateScene.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's2' }],
    ]);
  });

  it('Attach audio and Attach image send the project, the scene and the kind, and show the attached file', async () => {
    const before = [scene({ audioFile: null, visualFile: null, audioSource: 'none' })];
    const after = [scene({ audioFile: 'owner-s1-aaaaaaaaaaaa.mp3', audioSource: 'owner' })];
    const bridge = makeBridge({
      ...listing(project({ scenes: before })),
      operatorAttachSceneMedia: vi.fn(async () => ({
        project: project({ scenes: after }) as OperatorProjectDto | null,
      })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'scene-attach-audio-s1');
    await click(fixture, 'scene-attach-visual-s1');

    expect(bridge.operatorAttachSceneMedia.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1', kind: 'audio' }],
      [{ projectId: 'p1', sceneId: 's1', kind: 'visual' }],
    ]);
    expect(el(fixture, 'scene-audio-s1')!.getAttribute('src')).toBe(
      'scout-media://p1/owner-s1-aaaaaaaaaaaa.mp3?v=2026-11-03T09%3A05%3A00Z',
    );
  });

  it('a refused attach shows the reason, and a closed file dialog changes nothing', async () => {
    const scenes = [scene({ audioFile: null })];
    let refuse = true;
    const bridge = makeBridge({
      ...listing(project({ scenes })),
      operatorAttachSceneMedia: vi.fn(async () => {
        if (refuse)
          throw refusedWith('scout:operator.sceneMedia', 'Narration files are at most 100 MB.');
        return { project: null as OperatorProjectDto | null };
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'scene-attach-audio-s1');
    const refused = text(fixture, 'scene-error');
    refuse = false;
    await click(fixture, 'scene-attach-audio-s1');

    expect(refused).toBe('Narration files are at most 100 MB.');
    expect(el(fixture, 'scene-error')).toBeNull();
    expect(el(fixture, 'scene-audio-s1')).toBeNull();
    expect(text(fixture, 'scene-s1')).toContain('Span is a view over memory.');
  });

  it('a producing project shows how many of its scenes are ready', async () => {
    const scenes = [
      scene({ id: 's1' }),
      scene({ id: 's2', order: 2, status: 'generating' }),
      scene({ id: 's3', order: 3, status: 'planned' }),
    ];
    const bridge = makeBridge({
      ...listing(project({ status: 'producing', scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    expect(text(fixture, 'production-progress')).toBe('1 of 3 scenes ready');
  });
});
