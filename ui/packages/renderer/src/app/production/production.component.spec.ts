import { formatDate } from '@angular/common';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlanePermissionDeniedError,
  ControlPlaneUnavailableError,
  encodeIpcErrorMessage,
  serializeError,
  type MandateDto,
  type OperatorProjectDto,
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
    ...overrides,
  };
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
    ...overrides,
  };
}

type Bridge = ReturnType<typeof makeBridge>;

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
});
