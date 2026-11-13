import { readFileSync } from 'node:fs';

import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { ActivatedRoute } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneActionRejectedError,
  ControlPlaneAlreadyDecidedError,
  ControlPlaneUnavailableError,
  encodeIpcErrorMessage,
  serializeError,
  type PendingActionDto,
  type WorkerEvent,
  type WorkerStatus,
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
    signedAs: null,
    controlStatus: null,
  };
}

function makeBridge(overrides: Record<string, unknown> = {}) {
  const changeListeners: (() => void)[] = [];
  const workerListeners: ((event: WorkerEvent) => void)[] = [];
  const bridge = {
    actionsList: vi.fn(async () => ({ actions: [] as PendingActionDto[] })),
    writesList: vi.fn(async () => ({ entries: [] as WriteLogEntryDto[] })),
    actionsApprove: vi.fn(async () => ({ ...upload, status: 'approved' })),
    actionsReject: vi.fn(async () => ({ ...upload, status: 'rejected' })),
    onActionsChanged: vi.fn((listener: () => void) => {
      changeListeners.push(listener);
      return () => undefined;
    }),
    workerStatus: vi.fn(async (): Promise<WorkerStatus> => ({ state: 'running', restarts: 0 })),
    workerStart: vi.fn(async () => ({ pid: 42 })),
    onWorkerEvent: vi.fn((listener: (event: WorkerEvent) => void) => {
      workerListeners.push(listener);
      return () => undefined;
    }),
    ...overrides,
  };
  return {
    bridge,
    changed: () => changeListeners.forEach((listener) => listener()),
    workerState: (status: WorkerStatus) =>
      workerListeners.forEach((listener) => listener({ kind: 'state', status })),
  };
}

async function mount(
  bridge: ReturnType<typeof makeBridge>['bridge'],
  routeData?: Record<string, unknown>,
): Promise<ComponentFixture<ReviewComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      ...(routeData === undefined
        ? []
        : [{ provide: ActivatedRoute, useValue: { snapshot: { data: routeData } } }]),
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

async function openTab(
  fixture: ComponentFixture<ReviewComponent>,
  tab: 'pending' | 'history',
): Promise<void> {
  (fixture.nativeElement.querySelector(`[data-test="tab-${tab}"]`) as HTMLElement).click();
  await settle(fixture);
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

    const pending = text(fixture, 'pending-empty');
    await openTab(fixture, 'history');

    expect(pending).toBe(
      'Nothing is waiting. Actions that cannot be undone appear here before Scout performs them.',
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

    for (const part of ['YouTube', 'Upload video', 'channel UC1', 'Upload "Intro" as private']) {
      expect(row).toContain(part);
    }
    expect(bridge.actionsApprove.mock.calls).toEqual([[{ id: 'act-1' }]]);
    expect(fixture.nativeElement.querySelector('[data-test="action-act-1"]')).toBeNull();
    expect(text(fixture, 'pending-empty')).toContain('Nothing is waiting.');
  });

  it('an action the worker is executing has left Waiting for you and its count, and shows in History as Running now with no Approve or Reject', async () => {
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({
        actions: [upload, { ...upload, id: 'act-2', status: 'executing' }],
      })),
    });
    const fixture = await mount(bridge);
    const q = (id: string) => fixture.nativeElement.querySelector(`[data-test="${id}"]`);
    const waiting = [q('action-act-2'), q('approve-act-2'), q('reject-act-2')];
    const count = text(fixture, 'pending-count');
    const firstOffered = [q('approve-act-1'), q('reject-act-1')].every((b) => b !== null);

    await openTab(fixture, 'history');

    expect(waiting).toEqual([null, null, null]);
    expect(count).toBe('1');
    expect(firstOffered).toBe(true);
    expect(text(fixture, 'executing-act-2')).toBe('Running now');
    expect([q('approve-act-2'), q('reject-act-2')]).toEqual([null, null]);
    expect(q('executing-act-1')).toBeNull();
  });

  it('a click that comes after another window decided says Already decided and drops the action from the list', async () => {
    let pending = [upload];
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: pending })),
      actionsApprove: vi.fn(async () => {
        pending = [];
        throw new Error(
          `Error invoking remote method 'scout:actions.approve': Error: ${encodeIpcErrorMessage(
            serializeError(new ControlPlaneAlreadyDecidedError()),
          )}`,
        );
      }),
    });
    const fixture = await mount(bridge);

    (fixture.nativeElement.querySelector('[data-test="approve-act-1"]') as HTMLElement).click();
    await settle(fixture);
    await settle(fixture);

    expect(text(fixture, 'review-error')).toBe('Already decided');
    expect(fixture.nativeElement.querySelector('[data-test="approve-act-1"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="pending-count"]')).toBeNull();
  });

  it('an approval the control plane fails shows its reason and keeps the action to decide again', async () => {
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: [upload] })),
      actionsApprove: vi.fn(async () => {
        throw new Error(
          `Error invoking remote method 'scout:actions.approve': Error: ${encodeIpcErrorMessage(
            serializeError(new ControlPlaneUnavailableError('socket reset')),
          )}`,
        );
      }),
    });
    const fixture = await mount(bridge);

    (fixture.nativeElement.querySelector('[data-test="approve-act-1"]') as HTMLElement).click();
    await settle(fixture);
    await settle(fixture);

    expect(text(fixture, 'review-error')).toBe('The Scout control plane did not answer.');
    expect(
      (fixture.nativeElement.querySelector('[data-test="approve-act-1"]') as HTMLButtonElement)
        .disabled,
    ).toBe(false);
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
    await openTab(fixture, 'history');

    const rows = Array.from(
      fixture.nativeElement.querySelectorAll('[data-test="write-row"]') as NodeListOf<HTMLElement>,
    ).map((row) => row.querySelector('[data-test="write-action"]')?.textContent?.trim());
    const newest = text(fixture, 'write-row');

    expect(rows).toEqual(['Upload video', 'Set thumbnail', 'Save script']);
    for (const part of ['YouTube', 'channel UC1', 'Done', 'You']) {
      expect(newest).toContain(part);
    }
  });

  it('the Approvals page of the operator window lists only the pending actions and writes of youtube', async () => {
    const deploy: PendingActionDto = { ...upload, id: 'act-gh', featureId: 'github' };
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: [upload, deploy] })),
      writesList: vi.fn(async () => ({
        entries: [
          write('2026-11-02T09:00:00Z', 'Upload video', 'owner'),
          { ...write('2026-11-02T10:00:00Z', 'Comment on issue', null), featureId: 'github' },
        ],
      })),
    });
    const fixture = await mount(bridge, { feature: 'youtube' });
    const pending = [
      fixture.nativeElement.querySelector('[data-test="action-act-1"]'),
      fixture.nativeElement.querySelector('[data-test="action-act-gh"]'),
    ];
    await openTab(fixture, 'history');

    const writes = Array.from(
      fixture.nativeElement.querySelectorAll(
        '[data-test="write-action"]',
      ) as NodeListOf<HTMLElement>,
    ).map((cell) => cell.textContent?.trim());

    expect(pending[0]).not.toBeNull();
    expect(pending[1]).toBeNull();
    expect(writes).toEqual(['Upload video']);
    expect(fixture.nativeElement.querySelector('h1')?.textContent?.trim()).toBe('Approvals');
  });

  it('the Approvals screen of the main window keeps the pending actions and writes of every feature', async () => {
    const deploy: PendingActionDto = { ...upload, id: 'act-gh', featureId: 'github' };
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: [upload, deploy] })),
      writesList: vi.fn(async () => ({
        entries: [
          write('2026-11-02T09:00:00Z', 'Upload video', 'owner'),
          { ...write('2026-11-02T10:00:00Z', 'Comment on issue', null), featureId: 'github' },
        ],
      })),
    });
    const fixture = await mount(bridge);
    const pending = [
      fixture.nativeElement.querySelector('[data-test="action-act-1"]'),
      fixture.nativeElement.querySelector('[data-test="action-act-gh"]'),
    ];
    await openTab(fixture, 'history');

    const writes = Array.from(
      fixture.nativeElement.querySelectorAll(
        '[data-test="write-action"]',
      ) as NodeListOf<HTMLElement>,
    ).map((cell) => cell.textContent?.trim());

    expect(pending.every((row) => row !== null)).toBe(true);
    expect(writes).toEqual(['Comment on issue', 'Upload video']);
    expect(fixture.nativeElement.querySelector('h1')?.textContent?.trim()).toBe('Approvals');
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

  it('a control plane that goes down says the approvals could not be loaded, never the spawn failure, and keeps the last list', async () => {
    let down = false;
    const { bridge, changed } = makeBridge({
      actionsList: vi.fn(async () => {
        if (down) {
          throw new Error(
            `Error invoking remote method 'scout:actions.list': Error: ${encodeIpcErrorMessage(
              serializeError(
                new ControlPlaneUnavailableError(
                  'The control-plane host did not start: spawn /tmp/scout-e2e/no-control-plane ENOENT',
                ),
              ),
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

    expect(text(fixture, 'review-error')).toBe(
      'Could not load the approvals: the Scout control plane did not answer. Nothing was approved or rejected.',
    );
    expect(text(fixture, 'review-error')).not.toMatch(/\/tmp|spawn|ENOENT/);
    expect(fixture.nativeElement.querySelector('[data-test="action-act-1"]')).not.toBeNull();
  });
});

describe('Approvals follows the theme and the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('its styles paint only with the theme tokens, no hex, rgb() or hsl() colour left', () => {
    const source = readFileSync('src/app/review/review.component.ts', 'utf8');
    const start = source.indexOf('  styles:');
    const css = source.slice(start, source.indexOf('\n})\n', start));

    expect(start).toBeGreaterThanOrEqual(0);
    expect(css).toContain('var(--sc-');
    expect(
      Array.from(
        css.matchAll(
          /#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(|(?<![\w-])(?:white|black)(?![\w-])/g,
        ),
        (match) => match[0],
      ),
    ).toEqual([]);
  });

  it('opens with the lede and a Refresh that reads both lists again', async () => {
    const { bridge } = makeBridge();
    const fixture = await mount(bridge);

    (fixture.nativeElement.querySelector('[data-test="review-refresh"]') as HTMLElement).click();
    await settle(fixture);

    expect(text(fixture, 'review-lede')).toBe(
      'What Scout is waiting to do, and what it did. Actions that cannot be undone wait here for you.',
    );
    expect(text(fixture, 'review-refresh')).toBe('Refresh');
    expect(bridge.actionsList).toHaveBeenCalledTimes(2);
    expect(bridge.writesList).toHaveBeenCalledTimes(2);
  });

  function tabs(fixture: ComponentFixture<ReviewComponent>): [string, string | null][] {
    return Array.from(
      fixture.nativeElement.querySelectorAll('[role="tab"]') as NodeListOf<HTMLElement>,
      (tab) => [tab.textContent!.replace(/\s+/g, ' ').trim(), tab.getAttribute('aria-selected')],
    );
  }

  for (const [waiting, picked, expected, panel] of [
    [
      0,
      'pending',
      [
        ['Waiting for you', 'true'],
        ['History', 'false'],
      ],
      'pending-empty',
    ],
    [
      2,
      'pending',
      [
        ['Waiting for you 2', 'true'],
        ['History', 'false'],
      ],
      'action-act-1',
    ],
    [
      0,
      'history',
      [
        ['Waiting for you', 'false'],
        ['History', 'true'],
      ],
      'write-row',
    ],
    [
      2,
      'history',
      [
        ['Waiting for you 2', 'false'],
        ['History', 'true'],
      ],
      'write-row',
    ],
  ] as const) {
    it(`with ${waiting} waiting, the ${picked} tab shows ${panel} and the tabs read ${JSON.stringify(expected)}`, async () => {
      const { bridge } = makeBridge({
        actionsList: vi.fn(async () => ({
          actions: [upload, { ...upload, id: 'act-2' }].slice(0, waiting),
        })),
        writesList: vi.fn(async () => ({
          entries: [write('2026-11-02T09:00:00Z', 'Upload video', 'owner')],
        })),
      });
      const fixture = await mount(bridge);

      await openTab(fixture, picked);

      expect(tabs(fixture)).toEqual(expected);
      expect(fixture.nativeElement.querySelector(`[data-test="${panel}"]`)).not.toBeNull();
      expect(
        fixture.nativeElement.querySelector(
          `[data-test="${picked === 'pending' ? 'write-row' : 'action-act-1'}"]`,
        ),
      ).toBeNull();
    });
  }

  it('See the history on the empty list opens the History tab', async () => {
    const { bridge } = makeBridge({
      writesList: vi.fn(async () => ({
        entries: [write('2026-11-02T09:00:00Z', 'Upload video', 'owner')],
      })),
    });
    const fixture = await mount(bridge);

    (fixture.nativeElement.querySelector('[data-test="see-history"]') as HTMLElement).click();
    await settle(fixture);

    expect(tabs(fixture)).toEqual([
      ['Waiting for you', 'false'],
      ['History', 'true'],
    ]);
    expect(text(fixture, 'write-action')).toBe('Upload video');
  });

  for (const [outcome, shown, tone] of [
    ['succeeded', 'Done', 'chip--ok'],
    ['approved', 'Approved', 'chip--ok'],
    ['rejected', 'Rejected', null],
    ['failed: the head commit changed', 'Failed: the head commit changed', 'chip--error'],
    ['started', 'Started', null],
    [
      'not confirmed: Scout stopped before it recorded how this ended, so it may have run',
      'Not confirmed: Scout stopped before it recorded how this ended, so it may have run',
      'chip--warn',
    ],
  ] as const) {
    it(`the outcome "${outcome}" reads "${shown}" and is ${tone ?? 'a neutral chip'}`, async () => {
      const { bridge } = makeBridge({
        writesList: vi.fn(async () => ({
          entries: [{ ...write('2026-11-02T09:00:00Z', 'Upload video', 'owner'), outcome }],
        })),
      });
      const fixture = await mount(bridge);
      await openTab(fixture, 'history');

      const chip = fixture.nativeElement.querySelector(
        '[data-test="write-outcome"]',
      ) as HTMLElement;

      expect(chip.textContent?.trim()).toBe(shown);
      expect(Array.from(chip.classList).filter((c) => c.startsWith('chip--'))).toEqual(
        tone === null ? [] : [tone],
      );
    });
  }

  const CONTROL_ROWS: readonly [
    WriteLogEntryDto['controlStatus'],
    string,
    'chip--ok' | 'chip--warn' | null,
  ][] = [
    [
      'applied',
      'Paused the audit of acme/shop; an area in progress finishes first. The findings so far stay on the cards.',
      'chip--ok',
    ],
    ['not applied', 'Not applied: no test run of acme/api#41 is running.', null],
    ['not applied', 'No audit of acme/shop is running.', null],
    [
      'expired',
      'Not applied: the click waited more than two minutes for Scout, so it was dropped; click again if it still applies.',
      null,
    ],
    [
      'not confirmed',
      'Not confirmed: Scout took this click but did not say how it ended; look at the job before clicking again.',
      'chip--warn',
    ],
    ['not applied', 'Not applied: it failed in Scout (IOException).', null],
  ];
  for (const [status, outcome, tone] of CONTROL_ROWS) {
    it(`a click on a running job recorded ${status}, "${outcome.slice(0, 40)}", is ${tone ?? 'a neutral chip'} with its text unchanged`, async () => {
      const { bridge } = makeBridge({
        writesList: vi.fn(async () => ({
          entries: [
            {
              ...write('2026-11-02T09:00:00Z', 'Pause audit', 'owner'),
              featureId: 'github',
              target: 'acme/shop',
              outcome,
              actionId: 'c1',
              controlStatus: status,
            },
          ],
        })),
      });
      const fixture = await mount(bridge);
      await openTab(fixture, 'history');

      const chip = fixture.nativeElement.querySelector(
        '[data-test="write-outcome"]',
      ) as HTMLElement;

      expect(chip.textContent?.trim()).toBe(outcome);
      expect(Array.from(chip.classList).filter((c) => c.startsWith('chip--'))).toEqual(
        tone === null ? [] : [tone],
      );
    });
  }

  it('an approval list that fails says why with Try again, and Try again reads it once more', async () => {
    let down = true;
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => {
        if (down) throw new Error('[scout:controlplane/unavailable] socket reset');
        return { actions: [upload] };
      }),
    });
    const fixture = await mount(bridge);
    const shown = text(fixture, 'review-error');

    down = false;
    (fixture.nativeElement.querySelector('[data-test="review-retry"]') as HTMLElement).click();
    await settle(fixture);

    expect(shown).toBe('socket reset');
    expect(fixture.nativeElement.querySelector('[data-test="review-error"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="action-act-1"]')).not.toBeNull();
  });
});

describe('Approvals keeps every decision and its outcome visible', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  function approveButton(
    fixture: ComponentFixture<ReviewComponent>,
    id: string,
  ): HTMLButtonElement {
    return fixture.nativeElement.querySelector(`[data-test="approve-${id}"]`) as HTMLButtonElement;
  }

  it('an action approved and not yet claimed by a worker shows in History as Approved, waiting for Scout to run it', async () => {
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({
        actions: [{ ...upload, status: 'approved', decidedAt: '2026-11-02T10:05:00Z' }],
      })),
    });
    const fixture = await mount(bridge);
    await openTab(fixture, 'history');

    const history = (
      fixture.nativeElement.querySelector('#panel-history') as HTMLElement
    ).textContent!.replace(/\s+/g, ' ');

    expect(history).toContain('Approved, waiting for Scout to run it');
  });

  it('approving a second action while the first is still being decided keeps the first one disabled', async () => {
    const second: PendingActionDto = { ...upload, id: 'act-2', summary: 'Upload "Outro"' };
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: [upload, second] })),
      actionsApprove: vi.fn(() => new Promise<PendingActionDto>(() => undefined)),
    });
    const fixture = await mount(bridge);
    approveButton(fixture, 'act-1').click();
    await settle(fixture);
    expect(approveButton(fixture, 'act-1').disabled).toBe(true);

    approveButton(fixture, 'act-2').click();
    await settle(fixture);

    expect(approveButton(fixture, 'act-1').disabled).toBe(true);
  });

  it('a second click on Approve while the first is answered sends one decision and shows no error', async () => {
    let answer: (value: PendingActionDto) => void = () => undefined;
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: [upload] })),
      actionsApprove: vi.fn(() => new Promise<PendingActionDto>((resolve) => (answer = resolve))),
    });
    const fixture = await mount(bridge);
    approveButton(fixture, 'act-1').click();
    await settle(fixture);

    approveButton(fixture, 'act-1').click();
    await settle(fixture);
    answer({ ...upload, status: 'approved' });
    await settle(fixture);

    expect(bridge.actionsApprove).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.querySelector('[data-test="review-error"]')).toBeNull();
  });

  it('a rejection logged with its reason is a neutral chip, not a success', async () => {
    const outcome = "rejected: Replaced by the owner's verdict from the review card.";
    const { bridge } = makeBridge({
      writesList: vi.fn(async () => ({
        entries: [{ ...write('2026-11-02T09:00:00Z', 'Pull request verdicts', 'owner'), outcome }],
      })),
    });
    const fixture = await mount(bridge);
    await openTab(fixture, 'history');

    const chip = fixture.nativeElement.querySelector('[data-test="write-outcome"]') as HTMLElement;

    expect(Array.from(chip.classList).filter((c) => c.startsWith('chip--'))).toEqual([]);
  });

  it('History says which identity signed each write', async () => {
    const { bridge } = makeBridge({
      writesList: vi.fn(async () => ({
        entries: [write('2026-11-02T09:00:00Z', 'Pull request verdicts', 'owner')],
      })),
    });
    const fixture = await mount(bridge);
    await openTab(fixture, 'history');

    const headers = Array.from(
      fixture.nativeElement.querySelectorAll('#panel-history th') as NodeListOf<HTMLElement>,
      (th) => th.textContent!.trim(),
    );

    expect(headers).toContain('Signed as');
  });

  for (const [state, offered] of [
    ['stopped', true],
    ['crashed', true],
    ['idle', true],
    ['starting', false],
    ['running', false],
  ] as const) {
    it(`an approved action waiting to run ${offered ? 'offers' : 'does not offer'} Start Scout while the worker is ${state}`, async () => {
      const { bridge } = makeBridge({
        actionsList: vi.fn(async () => ({
          actions: [{ ...upload, status: 'approved', decidedAt: '2026-11-02T10:05:00Z' }],
        })),
        workerStatus: vi.fn(async (): Promise<WorkerStatus> => ({ state, restarts: 0 })),
      });
      const fixture = await mount(bridge);
      await openTab(fixture, 'history');

      const start = fixture.nativeElement.querySelector('[data-test="review-start-scout"]');

      expect(start !== null).toBe(offered);
    });
  }

  it('Start Scout starts the worker, and the button leaves once main says the worker runs', async () => {
    const { bridge, workerState } = makeBridge({
      actionsList: vi.fn(async () => ({
        actions: [{ ...upload, status: 'approved', decidedAt: '2026-11-02T10:05:00Z' }],
      })),
      workerStatus: vi.fn(async (): Promise<WorkerStatus> => ({ state: 'stopped', restarts: 0 })),
    });
    const fixture = await mount(bridge);
    await openTab(fixture, 'history');

    (
      fixture.nativeElement.querySelector('[data-test="review-start-scout"]') as HTMLElement
    ).click();
    await settle(fixture);
    workerState({ state: 'running', restarts: 0 });
    await settle(fixture);

    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.querySelector('[data-test="review-start-scout"]')).toBeNull();
  });

  it('Start Scout in a window main sends no worker states to reads the state again, and leaves once the worker starts', async () => {
    let state: WorkerStatus['state'] = 'stopped';
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({
        actions: [{ ...upload, status: 'approved', decidedAt: '2026-11-02T10:05:00Z' }],
      })),
      workerStatus: vi.fn(async (): Promise<WorkerStatus> => ({ state, restarts: 0 })),
      workerStart: vi.fn(async () => {
        state = 'starting';
        return { pid: 42 };
      }),
    });
    const fixture = await mount(bridge, { feature: 'youtube' });
    await openTab(fixture, 'history');

    (
      fixture.nativeElement.querySelector('[data-test="review-start-scout"]') as HTMLElement
    ).click();
    await settle(fixture);

    expect(fixture.nativeElement.querySelector('[data-test="review-start-scout"]')).toBeNull();
  });

  it('Start Scout without an approved action waiting is never offered', async () => {
    const { bridge } = makeBridge({
      workerStatus: vi.fn(async (): Promise<WorkerStatus> => ({ state: 'stopped', restarts: 0 })),
    });
    const fixture = await mount(bridge);
    await openTab(fixture, 'history');

    expect(fixture.nativeElement.querySelector('[data-test="review-start-scout"]')).toBeNull();
  });

  for (const [signer, shown] of [
    ['scout-octocat[bot]', 'Bot (scout-octocat[bot])'],
    ['octocat', 'Owner (octocat)'],
    [null, '-'],
  ] as const) {
    it(`a write signed by ${signer ?? 'nobody known'} reads ${shown} under Signed as`, async () => {
      const { bridge } = makeBridge({
        writesList: vi.fn(async () => ({
          entries: [
            {
              ...write('2026-11-02T09:00:00Z', 'Pull request verdicts', 'owner'),
              signedAs: signer,
            },
          ],
        })),
      });
      const fixture = await mount(bridge);
      await openTab(fixture, 'history');

      expect(text(fixture, 'write-signed-as')).toBe(shown);
    });
  }

  it('an approval the control plane turned into a rejection, asked before the grant, says Rejected with that reason', async () => {
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: [upload] })),
      actionsApprove: vi.fn(async () => {
        throw new Error(
          `Error invoking remote method 'scout:actions.approve': Error: ${encodeIpcErrorMessage(serializeError(new ControlPlaneActionRejectedError()))}`,
        );
      }),
    });
    const fixture = await mount(bridge);

    (fixture.nativeElement.querySelector('[data-test="approve-act-1"]') as HTMLElement).click();
    await settle(fixture);
    await settle(fixture);

    expect(text(fixture, 'review-error')).toBe(
      'Rejected: asked before writes were granted; ask again',
    );
  });

  it('an approval list that fails on the first load shows the error and never says nothing is waiting', async () => {
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => {
        throw new Error('[scout:controlplane/unavailable] socket reset');
      }),
    });
    const fixture = await mount(bridge);

    expect(text(fixture, 'review-error')).toBe('socket reset');
    expect(fixture.nativeElement.querySelector('[data-test="pending-empty"]')).toBeNull();
  });
});

function declared(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found !== undefined && selector!.split(',').some((one) => matches(node, one.trim()))) {
      value = found;
    }
  }
  return value;
}

function matches(node: Element, selector: string): boolean {
  try {
    return node.matches(selector);
  } catch {
    return false;
  }
}

function local(iso: string): string {
  const at = new Date(iso);
  const two = (part: number): string => String(part).padStart(2, '0');
  return `${at.getFullYear()}-${two(at.getMonth() + 1)}-${two(at.getDate())} ${two(at.getHours())}:${two(at.getMinutes())}`;
}

describe('Approvals shows each state of the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  const second: PendingActionDto = { ...upload, id: 'act-2', summary: 'Upload "Outro"' };
  const approved: PendingActionDto = {
    ...upload,
    status: 'approved',
    decidedAt: '2026-11-02T10:05:00Z',
  };
  const FRAME = ['review-lede', 'review-refresh', 'tab-history', 'tab-pending'];
  const REQUESTED = local(upload.requestedAt);

  function failing(channel: string, error: Error): never {
    throw new Error(
      `Error invoking remote method '${channel}': Error: ${encodeIpcErrorMessage(serializeError(error))}`,
    );
  }

  function controlPlaneDown(): never {
    return failing('scout:actions.list', new ControlPlaneUnavailableError('socket reset'));
  }

  function words(el: HTMLElement): string {
    const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
    const parts: string[] = [];
    while (walker.nextNode() !== null) {
      const part = walker.currentNode.textContent!.replace(/\s+/g, ' ').trim();
      if (part.length > 0) parts.push(part);
    }
    return parts.join(' ');
  }

  function seen(fixture: ComponentFixture<ReviewComponent>, selector: string): string[] {
    return Array.from(
      fixture.nativeElement.querySelectorAll(selector) as NodeListOf<HTMLElement>,
      words,
    );
  }

  function id(testId: string): string {
    return `[data-test="${testId}"]`;
  }

  function one(fixture: ComponentFixture<ReviewComponent>, selector: string): HTMLElement {
    const found = fixture.nativeElement.querySelectorAll(selector) as NodeListOf<HTMLElement>;
    expect([selector, found.length > 0]).toEqual([selector, true]);
    return found[0]!;
  }

  function rendered(fixture: ComponentFixture<ReviewComponent>): string[] {
    return Array.from(
      new Set(
        Array.from(
          fixture.nativeElement.querySelectorAll('[data-test]') as NodeListOf<HTMLElement>,
          (el) => el.getAttribute('data-test')!,
        ),
      ),
    ).sort();
  }

  function drawn(node: Element, properties: readonly string[]): Record<string, string | null> {
    return Object.fromEntries(properties.map((property) => [property, declared(node, property)]));
  }

  function click(fixture: ComponentFixture<ReviewComponent>, testId: string): void {
    one(fixture, id(testId)).click();
  }

  const cards = '[aria-label="Waiting for your approval"] > li';

  for (const [waiting, others, shown] of [
    [0, [], null],
    [1, [], '1'],
    [2, [], '2'],
    [2, ['executing', 'approved'], '2'],
    [0, ['executing', 'approved'], null],
  ] as const) {
    it(`the count on Waiting for you reads ${shown ?? 'nothing'} over ${waiting} cards with ${waiting} waiting and ${others.length} decided still listed, once, in the tab's own name`, async () => {
      const { bridge } = makeBridge({
        actionsList: vi.fn(async () => ({
          actions: [
            ...[upload, second].slice(0, waiting),
            ...others.map((status, i) => ({ ...upload, id: `other-${i}`, status })),
          ],
        })),
      });
      const fixture = await mount(bridge);
      const tab = one(fixture, id('tab-pending'));
      const counts = Array.from(tab.querySelectorAll('span'));

      expect(seen(fixture, id('pending-count'))).toEqual(shown === null ? [] : [shown]);
      expect(seen(fixture, cards).length).toBe(waiting);
      expect(seen(fixture, id('tab-pending'))).toEqual([
        shown === null ? 'Waiting for you' : `Waiting for you ${shown}`,
      ]);
      expect(seen(fixture, id('tab-history'))).toEqual(['History']);
      expect(
        counts.map((c) => [c.className, c.getAttribute('aria-hidden'), c.getAttribute('title')]),
      ).toEqual(shown === null ? [] : [['count', null, null]]);
      expect([tab.getAttribute('aria-label'), tab.getAttribute('aria-describedby')]).toEqual([
        null,
        null,
      ]);
    });
  }

  for (const [window, route, shown] of [
    ['the main window', undefined, '3'],
    ['the operator window, which lists youtube only', { feature: 'youtube' }, '2'],
  ] as const) {
    it(`the count on Waiting for you equals the cards shown in ${window}: ${shown}`, async () => {
      const { bridge } = makeBridge({
        actionsList: vi.fn(async () => ({
          actions: [
            upload,
            second,
            { ...upload, id: 'act-gh', featureId: 'github' },
            { ...approved, id: 'act-done' },
          ],
        })),
      });
      const fixture = await mount(bridge, route);

      expect(seen(fixture, id('pending-count'))).toEqual([shown]);
      expect(seen(fixture, cards).length).toBe(Number(shown));
    });
  }

  it('the count on Waiting for you is the pill on the warn container', async () => {
    const { bridge } = makeBridge({ actionsList: vi.fn(async () => ({ actions: [upload] })) });
    const fixture = await mount(bridge);

    expect(
      drawn(one(fixture, id('pending-count')), [
        'min-width',
        'height',
        'padding',
        'border-radius',
        'background',
        'color',
        'font-size',
        'font-weight',
      ]),
    ).toEqual({
      'min-width': '20px',
      height: '20px',
      padding: '0 6px',
      'border-radius': '10px',
      background: 'var(--sc-warn-container)',
      color: 'var(--sc-on-warn-container)',
      'font-size': '12px',
      'font-weight': '600',
    });
  });

  it('the count on Waiting for you stays on the last list while a later read fails, and is absent while the first read fails', async () => {
    let down = false;
    const { bridge, changed } = makeBridge({
      actionsList: vi.fn(async () => {
        if (down) controlPlaneDown();
        return { actions: [upload, second] };
      }),
    });
    const kept = await mount(bridge);
    down = true;
    changed();
    await settle(kept);
    const afterLaterFailure = [seen(kept, id('pending-count')), seen(kept, cards).length];

    TestBed.resetTestingModule();
    const never = await mount(makeBridge({ actionsList: vi.fn(controlPlaneDown) }).bridge);

    expect(afterLaterFailure).toEqual([['2'], 2]);
    expect([seen(never, id('pending-count')), seen(never, cards).length]).toEqual([[], 0]);
  });

  it('the screen draws its text at 14 px, the alert on the error container with its sentence in the text colour, as the prototype does', async () => {
    const fixture = await mount(makeBridge({ actionsList: vi.fn(controlPlaneDown) }).bridge);

    expect({
      page: drawn(one(fixture, 'main'), ['font-size']),
      alert: drawn(one(fixture, '[role="alert"]'), ['background', 'border']),
      sentence: drawn(one(fixture, id('review-error')), ['color']),
    }).toEqual({
      page: { 'font-size': '14px' },
      alert: { background: 'var(--sc-error-container)', border: '1px solid var(--sc-error)' },
      sentence: { color: 'var(--sc-fg)' },
    });
  });

  it('the screen draws the history table at least 900 px wide, with its headers, the time of a write and nothing else on one line, and code at 13 px, as the prototype does', async () => {
    const { bridge } = makeBridge({
      writesList: vi.fn(async () => ({
        entries: [write('2026-11-02T09:00:00Z', 'Upload video', 'owner')],
      })),
    });
    const fixture = await mount(bridge);
    await openTab(fixture, 'history');
    const cells = Array.from(one(fixture, id('write-row')).querySelectorAll('td'), (cell) =>
      declared(cell, 'white-space'),
    );

    expect(drawn(one(fixture, 'table'), ['min-width', 'font-size'])).toEqual({
      'min-width': '900px',
      'font-size': '13px',
    });
    expect(
      Array.from(fixture.nativeElement.querySelectorAll('th') as NodeListOf<HTMLElement>, (th) =>
        declared(th, 'white-space'),
      ),
    ).toEqual(Array(7).fill('nowrap'));
    expect(cells).toEqual(['nowrap', null, null, null, null, null, null]);
    expect(drawn(one(fixture, `${id('write-row')} code`), ['font-size'])).toEqual({
      'font-size': '13px',
    });
  });

  it('the chips that say an approved action waits or runs are 4 px tall-padded on the info container, the other chips 3 px, as the prototype does', async () => {
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({
        actions: [
          upload,
          { ...approved, id: 'act-2' },
          { ...upload, id: 'act-3', status: 'executing' },
        ],
      })),
    });
    const fixture = await mount(bridge);
    const waiting = drawn(one(fixture, id('action-feature')), ['padding', 'background']);
    await openTab(fixture, 'history');

    expect({
      waiting,
      approved: drawn(one(fixture, id('waiting-to-run-act-2')), ['padding', 'background']),
      running: drawn(one(fixture, id('executing-act-3')), ['padding', 'background']),
    }).toEqual({
      waiting: { padding: '3px 10px', background: 'var(--sc-surface-2)' },
      approved: { padding: '4px 10px', background: 'var(--sc-info-container)' },
      running: { padding: '4px 10px', background: 'var(--sc-info-container)' },
    });
  });

  it('the loading state shows the progress bar over three skeleton cards until the first read answers', async () => {
    let answer: (value: { actions: PendingActionDto[] }) => void = () => undefined;
    const { bridge } = makeBridge({
      actionsList: vi.fn(
        () => new Promise<{ actions: PendingActionDto[] }>((resolve) => (answer = resolve)),
      ),
    });
    const fixture = await mount(bridge);
    const q = (selector: string) => fixture.nativeElement.querySelectorAll(selector);
    const loading = one(fixture, id('review-loading'));
    const whileLoading = {
      status: [loading.getAttribute('role'), loading.getAttribute('aria-label')],
      bars: Array.from(
        loading.querySelectorAll('mat-progress-bar') as NodeListOf<HTMLElement>,
        (bar) => [bar.getAttribute('aria-label'), bar.getAttribute('mode')],
      ),
      firstChild: loading.firstElementChild!.tagName.toLowerCase(),
      skeletons: Array.from(
        loading.querySelectorAll(id('review-skeleton')) as NodeListOf<HTMLElement>,
        (card) => [
          card.getAttribute('aria-hidden'),
          Array.from(card.children, (bar) => bar.className),
        ],
      ),
      rest: [
        q('[role="tabpanel"]').length,
        q(id('pending-count')).length,
        q(id('pending-empty')).length,
        q(id('review-error')).length,
      ],
    };

    answer({ actions: [upload] });
    await settle(fixture);

    expect(whileLoading).toEqual({
      status: ['status', 'Loading the approvals'],
      bars: [['Loading the approvals', 'indeterminate']],
      firstChild: 'mat-progress-bar',
      skeletons: [1, 2, 3].map(() => [
        'true',
        ['bar bar--title', 'bar bar--line', 'bar bar--meta'],
      ]),
      rest: [0, 0, 0, 0],
    });
    expect([q(id('review-loading')).length, q(id('review-skeleton')).length]).toEqual([0, 0]);
    expect(q(id('action-act-1')).length).toBe(1);
  });

  it('the loading state draws each skeleton as a card with a title, a line and a meta bar on the theme tokens', async () => {
    const { bridge } = makeBridge({
      actionsList: vi.fn(() => new Promise<never>(() => undefined)),
    });
    const fixture = await mount(bridge);
    const card = one(fixture, id('review-skeleton'));
    const bar = ['height', 'width', 'border-radius', 'background'];

    expect({
      area: drawn(one(fixture, id('review-loading')), ['display', 'flex-direction', 'gap']),
      card: drawn(card, ['border', 'border-radius', 'background', 'padding', 'gap']),
      bars: Array.from(card.children, (line) => drawn(line, bar)),
    }).toEqual({
      area: { display: 'flex', 'flex-direction': 'column', gap: '12px' },
      card: {
        border: '1px solid var(--sc-line)',
        'border-radius': 'var(--sc-radius)',
        background: 'var(--sc-surface)',
        padding: '16px',
        gap: '10px',
      },
      bars: [
        {
          height: '14px',
          width: '45%',
          'border-radius': '7px',
          background: 'var(--sc-surface-2)',
        },
        {
          height: '12px',
          width: '70%',
          'border-radius': '6px',
          background: 'var(--sc-surface-2)',
        },
        {
          height: '12px',
          width: '30%',
          'border-radius': '6px',
          background: 'var(--sc-surface-2)',
        },
      ],
    });
  });

  it('the loading state ends without a skeleton when the first read fails', async () => {
    const fixture = await mount(makeBridge({ actionsList: vi.fn(controlPlaneDown) }).bridge);

    expect(seen(fixture, id('review-loading'))).toEqual([]);
    expect(seen(fixture, id('review-skeleton'))).toEqual([]);
    expect(seen(fixture, id('review-retry'))).toEqual(['Try again']);
  });

  it('the reason field is one labelled text field per action, before Reject and Approve, as the prototype draws it', async () => {
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: [upload, second] })),
    });
    const fixture = await mount(bridge);

    const fields = ['act-1', 'act-2'].map((action) => {
      const card = one(fixture, id(`action-${action}`));
      const input = card.querySelector(id(`reason-${action}`)) as HTMLInputElement;
      const label = input.labels![0]!;
      return {
        tag: input.tagName.toLowerCase(),
        type: input.type,
        value: input.value,
        placeholder: input.placeholder,
        fields: card.querySelectorAll('input, textarea, select').length,
        label: words(label),
        order: Array.from(card.querySelectorAll('.decide > *') as NodeListOf<HTMLElement>, words),
        labelDrawn: drawn(label, ['flex', 'font-size', 'color']),
        inputDrawn: drawn(input, [
          'height',
          'padding',
          'border-radius',
          'border',
          'background',
          'color',
          'font-size',
        ]),
      };
    });

    expect(fields).toEqual(
      [1, 2].map(() => ({
        tag: 'input',
        type: 'text',
        value: '',
        placeholder: 'Saved with the decision in the history',
        fields: 1,
        label: 'Reason (optional)',
        order: ['Reason (optional)', 'Reject', 'Approve'],
        labelDrawn: { flex: '1 1 260px', 'font-size': '12px', color: 'var(--sc-muted)' },
        inputDrawn: {
          height: '36px',
          padding: '0 12px',
          'border-radius': 'var(--sc-radius-sm)',
          border: '1px solid var(--sc-line)',
          background: 'var(--sc-bg)',
          color: 'var(--sc-fg)',
          'font-size': '14px',
        },
      })),
    );
  });

  for (const [decision, typed, sent] of [
    ['approve', '', { id: 'act-1' }],
    ['approve', '   ', { id: 'act-1' }],
    ['approve', ' Checked the title ', { id: 'act-1', reason: 'Checked the title' }],
    ['reject', '', { id: 'act-1' }],
    ['reject', '   ', { id: 'act-1' }],
    ['reject', ' Wrong thumbnail ', { id: 'act-1', reason: 'Wrong thumbnail' }],
  ] as const) {
    it(`${decision} with the reason field holding ${JSON.stringify(typed)} sends ${JSON.stringify(sent)} once, and nothing to the other decision`, async () => {
      const { bridge } = makeBridge({
        actionsList: vi.fn(async () => ({ actions: [upload, second] })),
      });
      const fixture = await mount(bridge);
      (one(fixture, id('reason-act-1')) as HTMLInputElement).value = typed;
      (one(fixture, id('reason-act-2')) as HTMLInputElement).value = 'For the other action';

      click(fixture, `${decision}-act-1`);
      await settle(fixture);

      expect({
        approve: bridge.actionsApprove.mock.calls,
        reject: bridge.actionsReject.mock.calls,
      }).toEqual({
        approve: decision === 'approve' ? [[sent]] : [],
        reject: decision === 'reject' ? [[sent]] : [],
      });
    });
  }

  for (const decision of ['approve', 'reject'] as const) {
    it(`while ${decision} of one action is in flight its Approve and Reject are disabled and the other action's stay enabled`, async () => {
      const { bridge } = makeBridge({
        actionsList: vi.fn(async () => ({ actions: [upload, second] })),
        actionsApprove: vi.fn(() => new Promise<PendingActionDto>(() => undefined)),
        actionsReject: vi.fn(() => new Promise<PendingActionDto>(() => undefined)),
      });
      const fixture = await mount(bridge);
      const disabled = () =>
        ['approve-act-1', 'reject-act-1', 'approve-act-2', 'reject-act-2'].map(
          (button) => (one(fixture, id(button)) as HTMLButtonElement).disabled,
        );
      const before = disabled();

      click(fixture, `${decision}-act-1`);
      await settle(fixture);

      expect(before).toEqual([false, false, false, false]);
      expect(disabled()).toEqual([true, true, false, false]);
    });
  }

  for (const [first, then] of [
    ['reject', 'reject'],
    ['reject', 'approve'],
    ['approve', 'reject'],
  ] as const) {
    it(`${then} clicked while ${first} of the same action is still running sends that one ${first} and nothing else`, async () => {
      let answer: (value: PendingActionDto) => void = () => undefined;
      const running = () => new Promise<PendingActionDto>((resolve) => (answer = resolve));
      const { bridge } = makeBridge({
        actionsList: vi.fn(async () => ({ actions: [upload] })),
        actionsApprove: vi.fn(running),
        actionsReject: vi.fn(running),
      });
      const fixture = await mount(bridge);
      click(fixture, `${first}-act-1`);
      await settle(fixture);

      click(fixture, `${then}-act-1`);
      await settle(fixture);
      answer({ ...upload, status: first === 'approve' ? 'approved' : 'rejected' });
      await settle(fixture);

      expect({
        approve: bridge.actionsApprove.mock.calls,
        reject: bridge.actionsReject.mock.calls,
        error: seen(fixture, id('review-error')),
      }).toEqual({
        approve: first === 'approve' ? [[{ id: 'act-1' }]] : [],
        reject: first === 'reject' ? [[{ id: 'act-1' }]] : [],
        error: [],
      });
    });
  }

  for (const decision of ['reject', 'approve'] as const) {
    it(`a ${decision} the control plane fails is not shown as done: its reason shows, the action is still offered with its count, and History stays as it was`, async () => {
      const refuse = vi.fn(async () =>
        failing(`scout:actions.${decision}`, new ControlPlaneUnavailableError('socket reset')),
      );
      const { bridge } = makeBridge({
        actionsList: vi.fn(async () => ({ actions: [upload] })),
        writesList: vi.fn(async () => ({
          entries: [write('2026-11-01T09:00:00Z', 'Set thumbnail', 'owner')],
        })),
        actionsApprove: refuse,
        actionsReject: refuse,
      });
      const fixture = await mount(bridge);
      await openTab(fixture, 'history');
      const historyBefore = [rendered(fixture), seen(fixture, id('write-row'))];
      await openTab(fixture, 'pending');

      click(fixture, `${decision}-act-1`);
      await settle(fixture);
      await settle(fixture);
      const waiting = {
        error: seen(fixture, id('review-error')),
        count: seen(fixture, id('pending-count')),
        cards: seen(fixture, cards).length,
        disabled: ['approve-act-1', 'reject-act-1'].map(
          (button) => (one(fixture, id(button)) as HTMLButtonElement).disabled,
        ),
        empty: seen(fixture, id('pending-empty')),
      };
      await openTab(fixture, 'history');

      expect(waiting).toEqual({
        error: ['The Scout control plane did not answer.'],
        count: ['1'],
        cards: 1,
        disabled: [false, false],
        empty: [],
      });
      expect([
        rendered(fixture).filter((testId) => !testId.startsWith('review-')),
        seen(fixture, id('write-row')),
      ]).toEqual([
        historyBefore[0]!.filter((testId) => !testId.startsWith('review-')),
        [`${local('2026-11-01T09:00:00Z')} YouTube Set thumbnail channel UC1 Done You -`],
      ]);
      expect(refuse).toHaveBeenCalledTimes(1);
    });
  }

  for (const [status, offered] of [
    ['pending', true],
    ['approved', false],
    ['executing', false],
  ] as const) {
    it(`an action whose status is ${status} ${offered ? 'offers' : 'offers no'} Approve, Reject or reason field on the Waiting tab`, async () => {
      const { bridge } = makeBridge({
        actionsList: vi.fn(async () => ({ actions: [{ ...upload, status }] })),
      });
      const fixture = await mount(bridge);

      expect(
        ['action-act-1', 'approve-act-1', 'reject-act-1', 'reason-act-1'].map(
          (testId) => seen(fixture, id(testId)).length,
        ),
      ).toEqual(offered ? [1, 1, 1, 1] : [0, 0, 0, 0]);
      expect(seen(fixture, id('pending-count'))).toEqual(offered ? ['1'] : []);
      expect(seen(fixture, id('pending-empty')).length).toBe(offered ? 0 : 1);
    });
  }

  it('the time an action was requested is printed in the zone of this computer, never the time it was decided, and a write shows its own time', async () => {
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: [upload, { ...approved, id: 'act-2' }] })),
      writesList: vi.fn(async () => ({
        entries: [write('2026-11-01T09:30:00Z', 'Set thumbnail', 'owner')],
      })),
    });
    const fixture = await mount(bridge);
    const waiting = seen(fixture, `${id('action-act-1')} .requested`);
    await openTab(fixture, 'history');

    expect(REQUESTED).toMatch(/^2026-11-0[123] \d\d:\d\d$/);
    expect(local(approved.decidedAt!)).not.toBe(REQUESTED);
    expect(waiting).toEqual([`Requested ${REQUESTED}`]);
    expect(seen(fixture, `${id('approved-act-2')} .requested`)).toEqual([`Requested ${REQUESTED}`]);
    expect(seen(fixture, `${id('write-row')} td:first-child`)).toEqual([
      local('2026-11-01T09:30:00Z'),
    ]);
  });

  const card = (summary: string) =>
    `YouTube Cannot be undone Requested ${REQUESTED} Upload video Target: channel UC1 ${summary} Reason (optional) Reject Approve`;
  const WAITING = ['action-feature', 'pending-count'];
  const ROWS = ['write-action', 'write-outcome', 'write-row', 'write-signed-as'];
  const NOTHING_WAITING = ['pending-empty', 'see-history'];
  const nothingWaiting: readonly (readonly [string, readonly string[]])[] = [
    ['[role="tab"]', ['Waiting for you', 'History']],
    [
      id('pending-empty'),
      ['Nothing is waiting. Actions that cannot be undone appear here before Scout performs them.'],
    ],
    [id('see-history'), ['See the history']],
  ];
  const couldNotLoad: readonly (readonly [string, readonly string[]])[] = [
    ['[role="tab"]', ['Waiting for you', 'History']],
    [
      '[role="alert"]',
      [
        'Could not load the approvals: the Scout control plane did not answer. Nothing was approved or rejected. Try again',
      ],
    ],
  ];
  const waitingToRun = `YouTube Approved, waiting for Scout to run it Requested ${REQUESTED} Upload video Target: channel UC1`;

  for (const [state, overrides, waiting, history] of [
    [
      'Loaded',
      {
        actionsList: vi.fn(async () => ({ actions: [upload, second] })),
        writesList: vi.fn(async () => ({
          entries: [write('2026-11-02T09:00:00Z', 'Upload video', 'owner')],
        })),
      },
      {
        ids: [
          ...WAITING,
          ...['action', 'approve', 'reason', 'reject'].flatMap((part) => [
            `${part}-act-1`,
            `${part}-act-2`,
          ]),
        ],
        sees: [
          ['h1', ['Approvals']],
          [
            id('review-lede'),
            [
              'What Scout is waiting to do, and what it did. Actions that cannot be undone wait here for you.',
            ],
          ],
          [id('review-refresh'), ['Refresh']],
          ['[role="tab"]', ['Waiting for you 2', 'History']],
          [cards, [card('Upload "Intro" as private'), card('Upload "Outro"')]],
        ],
      },
      {
        ids: ['pending-count', ...ROWS],
        sees: [
          ['[role="tab"]', ['Waiting for you 2', 'History']],
          ['#panel-history > .note', ['The last 50 writes Scout made, newest first.']],
          [
            id('write-row'),
            [`${local('2026-11-02T09:00:00Z')} YouTube Upload video channel UC1 Done You -`],
          ],
        ],
      },
    ],
    [
      'Loading',
      { actionsList: vi.fn(() => new Promise<never>(() => undefined)) },
      {
        ids: ['review-loading', 'review-skeleton'],
        sees: [
          ['[role="tab"]', ['Waiting for you', 'History']],
          ['[role="status"][aria-label="Loading the approvals"]', ['']],
          [id('review-skeleton'), ['', '', '']],
        ],
      },
      {
        ids: ['review-loading', 'review-skeleton'],
        sees: [[id('review-skeleton'), ['', '', '']]],
      },
    ],
    [
      'Empty',
      {},
      { ids: NOTHING_WAITING, sees: nothingWaiting },
      {
        ids: ['writes-empty'],
        sees: [['#panel-history > .note', ['Scout has not written anything yet.']]],
      },
    ],
    [
      'Error',
      { actionsList: vi.fn(controlPlaneDown) },
      {
        ids: ['review-error', 'review-retry'],
        sees: [...couldNotLoad, [id('review-retry'), ['Try again']]],
      },
      { ids: ['review-error', 'review-retry', 'writes-empty'], sees: couldNotLoad },
    ],
    [
      'Approved, waiting to run',
      { actionsList: vi.fn(async () => ({ actions: [approved] })) },
      { ids: NOTHING_WAITING, sees: nothingWaiting },
      {
        ids: ['approved-act-1', 'waiting-to-run-act-1', 'writes-empty'],
        sees: [['[aria-label="Approved, waiting to run"] > li', [waitingToRun]]],
      },
    ],
    [
      'Approved, Scout not running',
      {
        actionsList: vi.fn(async () => ({ actions: [approved] })),
        workerStatus: vi.fn(async (): Promise<WorkerStatus> => ({ state: 'stopped', restarts: 0 })),
      },
      { ids: NOTHING_WAITING, sees: nothingWaiting },
      {
        ids: [
          'approved-act-1',
          'review-start-scout',
          'waiting-to-run-act-1',
          'worker-stopped',
          'writes-empty',
        ],
        sees: [
          ['[aria-label="Approved, waiting to run"] > li', [waitingToRun]],
          [id('worker-stopped'), ['Scout is not running, so nothing approved can run yet.']],
          [id('review-start-scout'), ['Start Scout']],
        ],
      },
    ],
    [
      'History',
      {
        actionsList: vi.fn(async () => ({ actions: [{ ...upload, status: 'executing' }] })),
        writesList: vi.fn(async () => ({
          entries: [
            {
              ...write('2026-11-01T09:00:00Z', 'Reply to a comment', 'owner'),
              outcome: 'rejected: Wrong thumbnail',
            },
            {
              ...write('2026-10-31T09:00:00Z', 'Set thumbnail', null),
              outcome: 'failed: the head commit changed',
            },
            { ...write('2026-11-02T09:00:00Z', 'Upload video', 'owner'), signedAs: 'octocat' },
          ],
        })),
      },
      { ids: NOTHING_WAITING, sees: nothingWaiting },
      {
        ids: ['executing-act-1', 'running-act-1', ...ROWS],
        sees: [
          [
            '[aria-label="Approved and running"] > li',
            [`YouTube Running now Requested ${REQUESTED} Upload video Target: channel UC1`],
          ],
          ['#panel-history > .note', ['The last 50 writes Scout made, newest first.']],
          [
            '#panel-history th',
            ['Time', 'Feature', 'Action', 'Target', 'Outcome', 'Approved by', 'Signed as'],
          ],
          [
            id('write-row'),
            [
              `${local('2026-11-02T09:00:00Z')} YouTube Upload video channel UC1 Done You Owner (octocat)`,
              `${local('2026-11-01T09:00:00Z')} YouTube Reply to a comment channel UC1 Rejected: Wrong thumbnail You -`,
              `${local('2026-10-31T09:00:00Z')} YouTube Set thumbnail channel UC1 Failed: the head commit changed Automatic (writes allowed) -`,
            ],
          ],
        ],
      },
    ],
  ] as const) {
    it(`the ${state} state shows what the prototype shows on each tab, and renders nothing else`, async () => {
      const fixture = await mount(makeBridge(overrides).bridge);
      const onWaiting = {
        ids: rendered(fixture),
        sees: waiting.sees.map(([selector]) => [selector, seen(fixture, selector)]),
      };
      await openTab(fixture, 'history');

      expect(onWaiting).toEqual({
        ids: [...FRAME, ...waiting.ids].sort(),
        sees: waiting.sees.map(([selector, texts]) => [selector, texts]),
      });
      expect({
        ids: rendered(fixture),
        sees: history.sees.map(([selector]) => [selector, seen(fixture, selector)]),
      }).toEqual({
        ids: [...FRAME, ...history.ids].sort(),
        sees: history.sees.map(([selector, texts]) => [selector, texts]),
      });
    });
  }

  it('a rejected action leaves Waiting for you and its count, and reads rejected with its reason in History', async () => {
    let pending = [upload];
    let log: WriteLogEntryDto[] = [];
    const { bridge } = makeBridge({
      actionsList: vi.fn(async () => ({ actions: pending })),
      writesList: vi.fn(async () => ({ entries: log })),
      actionsReject: vi.fn(async () => {
        pending = [];
        log = [
          {
            ...write(upload.requestedAt, upload.action, 'owner'),
            outcome: 'rejected: Wrong title',
          },
        ];
        return { ...upload, status: 'rejected' };
      }),
    });
    const fixture = await mount(bridge);
    (one(fixture, id('reason-act-1')) as HTMLInputElement).value = 'Wrong title';

    click(fixture, 'reject-act-1');
    await settle(fixture);
    const waiting = [
      seen(fixture, id('action-act-1')),
      seen(fixture, id('pending-count')),
      seen(fixture, id('pending-empty')).length,
    ];
    await openTab(fixture, 'history');

    expect(waiting).toEqual([[], [], 1]);
    expect(seen(fixture, id('write-row'))).toEqual([
      `${local('2026-11-02T10:00:00Z')} YouTube Upload video channel UC1 Rejected: Wrong title You -`,
    ]);
    expect(Array.from(one(fixture, id('write-outcome')).classList)).toEqual(['chip']);
    expect(bridge.actionsApprove).not.toHaveBeenCalled();
  });
});

describe('Approvals says things as the artboard draws them', () => {
  const NOW = new Date(2026, 10, 12, 15, 0);
  const localIso = (day: number, hour: number, minute: number): string =>
    new Date(2026, 10, day, hour, minute).toISOString();
  const verdict: PendingActionDto = {
    id: 'act-v',
    featureId: 'github',
    action: 'Pull request verdicts',
    target: 'acme/shop#5',
    summary: 'Approve after the 2 review comments were posted.',
    requestedAt: localIso(12, 14, 2),
    status: 'pending',
    decidedAt: null,
  };

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [{ provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } }],
    });
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(NOW);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function words(el: HTMLElement): string {
    const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
    const parts: string[] = [];
    while (walker.nextNode() !== null) {
      const part = walker.currentNode.textContent!.replace(/\s+/g, ' ').trim();
      if (part.length > 0) parts.push(part);
    }
    return parts.join(' ');
  }

  function seen(fixture: ComponentFixture<ReviewComponent>, selector: string): string[] {
    return Array.from(
      fixture.nativeElement.querySelectorAll(selector) as NodeListOf<HTMLElement>,
      words,
    );
  }

  function notices(): { text: string; kind: string[]; action: string | null }[] {
    return Array.from(
      document.querySelectorAll<HTMLElement>('.cdk-overlay-container .sc-notice'),
      (el) => {
        const label = el.querySelector('.mat-mdc-snack-bar-label [aria-hidden="true"]') ?? el;
        const copy = label.cloneNode(true) as HTMLElement;
        copy.querySelectorAll('button').forEach((button) => button.remove());
        return {
          text: (copy.textContent ?? '').replace(/\s+/g, ' ').trim(),
          kind: Array.from(el.classList).filter((c) => c.startsWith('sc-notice--')),
          action: el.querySelector('button')?.textContent?.trim() ?? null,
        };
      },
    );
  }

  for (const [state, listed, entries, selector, shown] of [
    [
      'a card waiting for you',
      [verdict],
      [],
      '[aria-label="Waiting for your approval"] > li',
      [
        'GitHub Cannot be undone Requested today 14:02 Approve a pull request Target: acme/shop#5 Approve after the 2 review comments were posted. Reason (optional) Reject Approve',
      ],
    ],
    [
      'an action approved and waiting to run',
      [
        {
          ...upload,
          action: 'videos.insert',
          requestedAt: localIso(11, 17, 55),
          status: 'approved',
        },
      ],
      [],
      '[aria-label="Approved, waiting to run"] > li',
      [
        'YouTube Approved, waiting for Scout to run it Requested yesterday 17:55 Upload a video Target: channel UC1',
      ],
    ],
    [
      'an action running',
      [
        {
          ...upload,
          action: 'comments.insert',
          requestedAt: localIso(10, 9, 5),
          status: 'executing',
        },
      ],
      [],
      '[aria-label="Approved and running"] > li',
      [
        'YouTube Running now Requested 2026-11-10 09:05 Publish a reply to a comment Target: channel UC1',
      ],
    ],
    [
      'the writes in History',
      [],
      [
        { ...write(localIso(12, 11, 5), 'thumbnails.set', 'owner'), signedAs: 'octocat' },
        {
          ...write(localIso(11, 16, 20), 'Pull request verdicts', 'owner'),
          outcome: 'failed: the head commit changed',
        },
        {
          ...write(localIso(11, 15, 2), 'comments.insert', 'owner'),
          outcome: 'rejected: asked before writes were granted; ask again',
        },
        {
          ...write(localIso(9, 8, 0), "Scout's data folder", null),
          outcome: 'saved script_ready',
        },
      ],
      '[data-test="write-row"]',
      [
        'Today 11:05 YouTube Set a video thumbnail channel UC1 Done You Owner (octocat)',
        'Yesterday 16:20 YouTube Give a verdict on a pull request channel UC1 Failed: the head commit changed You -',
        'Yesterday 15:02 YouTube Publish a reply to a comment channel UC1 Rejected: asked before writes were granted; ask again You -',
        "2026-11-09 08:00 YouTube Scout's data folder channel UC1 Saved script_ready Automatic (writes allowed) -",
      ],
    ],
  ] as const) {
    it(`${state} reads in words, with the time on this computer's days`, async () => {
      const { bridge } = makeBridge({
        actionsList: vi.fn(async () => ({ actions: listed })),
        writesList: vi.fn(async () => ({ entries: entries })),
      });
      const fixture = await mount(bridge);
      if (!selector.includes('Waiting for your approval')) await openTab(fixture, 'history');

      expect(seen(fixture, selector)).toEqual(shown);
    });
  }

  it('History keeps the tone of the outcome the log recorded, under its words', async () => {
    const { bridge } = makeBridge({
      writesList: vi.fn(async () => ({
        entries: ['succeeded', 'rejected: wrong title', 'failed: the head commit changed'].map(
          (outcome, i) => ({
            ...write(localIso(12, 11 - i, 0), 'videos.insert', 'owner'),
            outcome,
          }),
        ),
      })),
    });
    const fixture = await mount(bridge);
    await openTab(fixture, 'history');

    expect(
      Array.from(
        fixture.nativeElement.querySelectorAll(
          '[data-test="write-outcome"]',
        ) as NodeListOf<HTMLElement>,
        (chip) => [words(chip), Array.from(chip.classList).filter((c) => c.startsWith('chip--'))],
      ),
    ).toEqual([
      ['Done', ['chip--ok']],
      ['Rejected: wrong title', []],
      ['Failed: the head commit changed', ['chip--error']],
    ]);
  });

  for (const decision of ['approve', 'reject'] as const) {
    for (const [reason, typed, sent] of [
      ['no reason', '', { id: 'act-v' }],
      ['a reason', ' Checked the diff ', { id: 'act-v', reason: 'Checked the diff' }],
    ] as const) {
      for (const result of ['ok', 'failed'] as const) {
        const notice =
          result === 'failed'
            ? []
            : decision === 'approve'
              ? [
                  {
                    text: 'Approved: Approve a pull request (acme/shop#5)',
                    kind: ['sc-notice--success'],
                    action: 'OK',
                  },
                ]
              : [
                  {
                    text: 'Rejected: Approve a pull request (acme/shop#5)',
                    kind: ['sc-notice--info'],
                    action: 'OK',
                  },
                ];
        it(`${decision} with ${reason} that ${result === 'ok' ? 'the control plane takes' : 'the control plane fails'} ${notice.length > 0 ? `says "${notice[0]!.text}" in a notice with OK` : 'shows no notice and keeps the alert'}`, async () => {
          const answer = vi.fn(async (): Promise<PendingActionDto> => {
            if (result === 'failed') {
              throw new Error(
                `Error invoking remote method 'scout:actions.${decision}': Error: ${encodeIpcErrorMessage(
                  serializeError(new ControlPlaneUnavailableError('socket reset')),
                )}`,
              );
            }
            return { ...verdict, status: decision === 'approve' ? 'approved' : 'rejected' };
          });
          const { bridge } = makeBridge({
            actionsList: vi.fn(async () => ({ actions: [verdict] })),
            actionsApprove: decision === 'approve' ? answer : vi.fn(),
            actionsReject: decision === 'reject' ? answer : vi.fn(),
          });
          const fixture = await mount(bridge);
          (
            fixture.nativeElement.querySelector('[data-test="reason-act-v"]') as HTMLInputElement
          ).value = typed;

          (
            fixture.nativeElement.querySelector(`[data-test="${decision}-act-v"]`) as HTMLElement
          ).click();
          await settle(fixture);
          await settle(fixture);

          expect({
            notice: notices(),
            alert: seen(fixture, '[data-test="review-error"]'),
            approve: bridge.actionsApprove.mock.calls,
            reject: bridge.actionsReject.mock.calls,
          }).toEqual({
            notice,
            alert: result === 'failed' ? ['The Scout control plane did not answer.'] : [],
            approve: decision === 'approve' ? [[sent]] : [],
            reject: decision === 'reject' ? [[sent]] : [],
          });
        });
      }
    }
  }
});
