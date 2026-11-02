import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneUnavailableError,
  encodeIpcErrorMessage,
  serializeError,
  type PendingActionDto,
  type WriteLogEntryDto,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { ReviewComponent } from './review.component';

const upload: PendingActionDto = {
  id: 'act-1',
  featureId: 'youtube',
  action: 'Upload video',
  target: 'channel UC1',
  summary: 'Upload "Intro" as private',
  requestedAt: '2026-11-02T10:00:00Z',
  status: 'pending',
  decidedAt: null,
};

function write(at: string, action: string, approvedBy: string | null): WriteLogEntryDto {
  return {
    at,
    featureId: 'youtube',
    action,
    target: 'channel UC1',
    outcome: 'succeeded',
    approvedBy,
    actionId: null,
  };
}

function makeBridge(overrides: Record<string, unknown> = {}) {
  const changeListeners: (() => void)[] = [];
  const bridge = {
    actionsList: vi.fn(async () => ({ actions: [] as PendingActionDto[] })),
    writesList: vi.fn(async () => ({ entries: [] as WriteLogEntryDto[] })),
    actionsApprove: vi.fn(async () => ({ ...upload, status: 'approved' })),
    actionsReject: vi.fn(async () => ({ ...upload, status: 'rejected' })),
    onActionsChanged: vi.fn((listener: () => void) => {
      changeListeners.push(listener);
      return () => undefined;
    }),
    ...overrides,
  };
  return { bridge, changed: () => changeListeners.forEach((listener) => listener()) };
}

async function mount(
  bridge: ReturnType<typeof makeBridge>['bridge'],
): Promise<ComponentFixture<ReviewComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(ReviewComponent);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<ReviewComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function text(fixture: ComponentFixture<ReviewComponent>, testId: string): string {
  const el = fixture.nativeElement.querySelector(`[data-test="${testId}"]`) as HTMLElement | null;
  return (el?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

describe('ReviewComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('says nothing is waiting and nothing was written when both lists are empty', async () => {
    const { bridge } = makeBridge();
    const fixture = await mount(bridge);

    expect(text(fixture, 'pending-empty')).toBe(
      'Nothing is waiting. Actions that cannot be undone will appear here before Scout performs them.',
    );
    expect(text(fixture, 'writes-empty')).toBe('Scout has not written anything yet.');
    expect(bridge.writesList.mock.calls).toEqual([[{ limit: 50 }]]);
  });

  it('pending actions render and Approve calls the control plane then refreshes', async () => {
    let pending = [upload];
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: pending })),
      actionsApprove: vi.fn(async () => {
        pending = [];
        return { ...upload, status: 'approved' };
      }),
    });
    const fixture = await mount(bridge);
    const row = text(fixture, 'action-act-1');

    (fixture.nativeElement.querySelector('[data-test="approve-act-1"]') as HTMLElement).click();
    await settle(fixture);

    for (const part of ['youtube', 'Upload video', 'channel UC1', 'Upload "Intro" as private']) {
      expect(row).toContain(part);
    }
    expect(bridge.actionsApprove.mock.calls).toEqual([[{ id: 'act-1' }]]);
    expect(fixture.nativeElement.querySelector('[data-test="action-act-1"]')).toBeNull();
    expect(text(fixture, 'pending-empty')).toContain('Nothing is waiting.');
  });

  it('Reject sends the reason', async () => {
    const { bridge } = makeBridge({ actionsList: vi.fn(async () => ({ actions: [upload] })) });
    const fixture = await mount(bridge);

    const reason = fixture.nativeElement.querySelector(
      '[data-test="reason-act-1"]',
    ) as HTMLInputElement;
    reason.value = '  Wrong thumbnail ';
    (fixture.nativeElement.querySelector('[data-test="reject-act-1"]') as HTMLElement).click();
    await settle(fixture);

    expect(bridge.actionsReject.mock.calls).toEqual([[{ id: 'act-1', reason: 'Wrong thumbnail' }]]);
    expect(bridge.actionsApprove).not.toHaveBeenCalled();
  });

  it('recent writes render newest first', async () => {
    const { bridge } = makeBridge({
      writesList: vi.fn(async () => ({
        entries: [
          write('2026-11-01T08:00:00Z', 'Save script', null),
          write('2026-11-02T09:00:00Z', 'Upload video', 'owner'),
          write('2026-11-01T20:00:00Z', 'Set thumbnail', 'owner'),
        ],
      })),
    });
    const fixture = await mount(bridge);

    const rows = Array.from(
      fixture.nativeElement.querySelectorAll('[data-test="write-row"]') as NodeListOf<HTMLElement>,
    ).map((row) => row.querySelector('[data-test="write-action"]')?.textContent?.trim());
    const newest = text(fixture, 'write-row');

    expect(rows).toEqual(['Upload video', 'Set thumbnail', 'Save script']);
    for (const part of ['youtube', 'channel UC1', 'succeeded', 'owner']) {
      expect(newest).toContain(part);
    }
  });

  it('shows an action main announces at once, and one queued silently within 15 seconds', async () => {
    vi.useFakeTimers();
    let queue: PendingActionDto[] = [];
    const { bridge, changed } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: queue })),
    });
    const fixture = await mount(bridge);

    queue = [upload];
    changed();
    await settle(fixture);
    const afterAnnounce = fixture.nativeElement.querySelector('[data-test="action-act-1"]');
    queue = [upload, { ...upload, id: 'act-2' }];
    await vi.advanceTimersByTimeAsync(15_000);
    await settle(fixture);

    expect(afterAnnounce).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="action-act-2"]')).not.toBeNull();
  });

  it('a control plane that goes down shows the error and keeps the last list', async () => {
    let down = false;
    const { bridge, changed } = makeBridge({
      actionsList: vi.fn(async () => {
        if (down) {
          throw new Error(
            `Error invoking remote method 'scout:actions.list': Error: ${encodeIpcErrorMessage(
              serializeError(new ControlPlaneUnavailableError('socket reset')),
            )}`,
          );
        }
        return { actions: [upload] };
      }),
    });
    const fixture = await mount(bridge);

    down = true;
    changed();
    await settle(fixture);

    expect(text(fixture, 'review-error')).toBe('socket reset');
    expect(fixture.nativeElement.querySelector('[data-test="action-act-1"]')).not.toBeNull();
  });
});
