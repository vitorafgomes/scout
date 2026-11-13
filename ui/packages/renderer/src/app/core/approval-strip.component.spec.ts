import { readFileSync } from 'node:fs';

import { provideZonelessChangeDetection, type Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneActionRejectedError,
  ControlPlaneAlreadyDecidedError,
  ControlPlaneUnavailableError,
  encodeIpcErrorMessage,
  serializeError,
  type ActionDecisionRequest,
  type LocalTurn,
  type PendingActionDto,
  type ScoutError,
  type WriteLogEntryDto,
} from '@scout/shared';

import { ChatComponent } from '../chat/chat.component';
import { QuickAskComponent } from '../quick-ask/quick-ask.component';
import { ReviewComponent } from '../review/review.component';
import { ICONS } from './icon.component';
import { PendingApprovals, describeAction } from './pending-approvals.store';
import { ScoutBridgeService } from './scout-bridge';

function ipcError(channel: string, error: ScoutError): Error {
  return new Error(
    `Error invoking remote method '${channel}': Error: ${encodeIpcErrorMessage(serializeError(error))}`,
  );
}

function github(
  id: string,
  action: string,
  summary: string,
  requestedAt = '2026-11-07T10:00:00Z',
): PendingActionDto {
  return {
    id,
    featureId: 'github',
    action,
    target: `GitHub.${summary.slice(0, summary.indexOf('('))}`,
    summary,
    requestedAt,
    status: 'pending',
    decidedAt: null,
  };
}

const KINDS = [
  {
    kind: 'verdict approve',
    action: github(
      'act-1',
      'Pull request verdicts',
      'give_pull_request_verdict(body, comments, commit, number=12, owner=acme, repo=api, verdict=approve)',
    ),
    title: 'Approve pull request #12 on acme/api',
  },
  {
    kind: 'verdict request changes',
    action: github(
      'act-1',
      'Pull request verdicts',
      'give_pull_request_verdict(body, comments, commit, number=12, owner=acme, repo=api, verdict=request_changes)',
    ),
    title: 'Request changes on pull request #12 on acme/api',
  },
  {
    kind: 'comment',
    action: github(
      'act-1',
      'Pull request comments',
      'comment_on_pull_request(body, number=12, owner=acme, repo=api)',
    ),
    title: 'Comment on pull request #12 on acme/api',
  },
  {
    kind: 'issue',
    action: github(
      'act-1',
      'New issues',
      'create_issue(body, owner=acme, repo=api, title=Login fails, twice, on Safari)',
    ),
    title: 'Open an issue on acme/api',
  },
] as const;

type Where = 'chat' | 'quick ask';

/** A control plane in memory: decisions push scout:actions.changed to every listener, as main does to every window. */
function makeBridge(actions: readonly PendingActionDto[]) {
  let listed: PendingActionDto[] = [...actions];
  const writes: WriteLogEntryDto[] = [];
  const changeListeners = new Set<() => void>();
  let turns: readonly LocalTurn[] = [];
  const turnListeners: ((turns: readonly LocalTurn[]) => void)[] = [];
  const changed = () => changeListeners.forEach((listener) => listener());
  const decide = (status: 'approved' | 'rejected', channel: string) =>
    vi.fn(async (req: ActionDecisionRequest): Promise<PendingActionDto> => {
      const action = listed.find((a) => a.id === req.id && a.status === 'pending');
      if (action === undefined) throw ipcError(channel, new ControlPlaneAlreadyDecidedError());
      listed =
        status === 'approved'
          ? listed.map((a) => (a.id === req.id ? { ...a, status: 'executing' } : a))
          : listed.filter((a) => a.id !== req.id);
      changed();
      return { ...action, status, decidedAt: '2026-11-07T10:01:00Z' };
    });
  const known = {
    changed,
    listed: () => listed,
    queue(action: PendingActionDto): void {
      listed = [...listed, action];
    },
    record(id: string, outcome: string): void {
      const action = listed.find((a) => a.id === id);
      listed = listed.filter((a) => a.id !== id);
      writes.unshift({
        at: '2026-11-07T10:02:00Z',
        featureId: action?.featureId ?? 'github',
        action: action?.action ?? '',
        target: action?.target ?? '',
        outcome,
        approvedBy: 'owner',
        actionId: id,
        signedAs: null,
        controlStatus: null,
      });
    },
    pushTurns(next: readonly LocalTurn[]): void {
      turns = next;
      turnListeners.forEach((listener) => listener(turns));
    },
    actionsList: vi.fn(async () => ({ actions: [...listed] })),
    writesList: vi.fn(async () => ({ entries: [...writes] })),
    actionsApprove: decide('approved', 'scout:actions.approve'),
    actionsReject: decide('rejected', 'scout:actions.reject'),
    onActionsChanged: vi.fn((listener: () => void) => {
      changeListeners.add(listener);
      return () => changeListeners.delete(listener);
    }),
    onLocalTurns: vi.fn((listener: (turns: readonly LocalTurn[]) => void) => {
      turnListeners.push(listener);
      return () => undefined;
    }),
    localGetTurns: vi.fn(async (): Promise<readonly LocalTurn[]> => turns),
    vaultHas: vi.fn(async () => ({ exists: false })),
    vaultGetPublic: vi.fn(async () => ({ value: null })),
    githubVerdictFor: vi.fn(async () => null),
  };
  return new Proxy(known, {
    get(target, name, receiver) {
      if (typeof name !== 'string' || name in target) return Reflect.get(target, name, receiver);
      return name.startsWith('on') ? () => () => undefined : async () => undefined;
    },
  }) as typeof known;
}

type Bridge = ReturnType<typeof makeBridge>;

function configure(bridge: Bridge, ownStoreForQuickAsk = false): void {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  if (ownStoreForQuickAsk) {
    TestBed.overrideComponent(QuickAskComponent, { add: { providers: [PendingApprovals] } });
  }
}

function create<T>(component: Type<T>): ComponentFixture<T> {
  const fixture = TestBed.createComponent(component);
  document.body.appendChild(fixture.nativeElement as HTMLElement);
  return fixture;
}

async function settle(...fixtures: ComponentFixture<unknown>[]): Promise<void> {
  for (let i = 0; i < 6; i++) {
    for (const fixture of fixtures) fixture.detectChanges();
    await Promise.all(fixtures.map((fixture) => fixture.whenStable()));
    await new Promise((resolve) => setTimeout(resolve));
  }
  for (const fixture of fixtures) fixture.detectChanges();
}

async function mount(bridge: Bridge, where: Where): Promise<ComponentFixture<unknown>> {
  configure(bridge);
  const fixture = create<unknown>(where === 'chat' ? ChatComponent : QuickAskComponent);
  await settle(fixture);
  return fixture;
}

function el(fixture: ComponentFixture<unknown>, testId: string): HTMLElement | null {
  return (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
    `[data-test="${testId}"]`,
  );
}

function text(fixture: ComponentFixture<unknown>, testId: string): string {
  return (el(fixture, testId)?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function approveButtons(fixture: ComponentFixture<unknown>): number {
  return (fixture.nativeElement as HTMLElement).querySelectorAll(
    '[data-test^="approval-approve-"], [data-test^="approve-"]',
  ).length;
}

async function reject(
  fixture: ComponentFixture<unknown>,
  id: string,
  reason: string | null,
): Promise<void> {
  el(fixture, `approval-reject-${id}`)!.click();
  await settle(fixture);
  if (reason !== null) (el(fixture, `approval-reason-${id}`) as HTMLInputElement).value = reason;
  el(fixture, `approval-confirm-reject-${id}`)!.click();
  await settle(fixture);
}

const STATUSES = [
  {
    status: 'pending',
    act: async () => undefined,
    outcome: null,
  },
  {
    status: 'approved',
    act: async (fixture: ComponentFixture<unknown>) => {
      el(fixture, 'approval-approve-act-1')!.click();
      await settle(fixture);
    },
    outcome: 'Approved, running',
  },
  {
    status: 'executed',
    act: async (fixture: ComponentFixture<unknown>, bridge: Bridge) => {
      el(fixture, 'approval-approve-act-1')!.click();
      await settle(fixture);
      bridge.record('act-1', 'succeeded');
      await TestBed.inject(PendingApprovals).load();
      await settle(fixture);
    },
    outcome: 'Done',
  },
  {
    status: 'rejected',
    act: async (fixture: ComponentFixture<unknown>) => reject(fixture, 'act-1', null),
    outcome: 'Rejected',
  },
  {
    status: 'failed',
    act: async (fixture: ComponentFixture<unknown>, bridge: Bridge) => {
      el(fixture, 'approval-approve-act-1')!.click();
      await settle(fixture);
      bridge.record('act-1', 'failed: GitHub answered 422, the pull request is closed');
      await TestBed.inject(PendingApprovals).load();
      await settle(fixture);
    },
    outcome: 'Failed: GitHub answered 422, the pull request is closed',
  },
] as const;

beforeAll(async () => {
  configure(makeBridge([]));
  const chat = create<unknown>(ChatComponent);
  const quick = create<unknown>(QuickAskComponent);
  await settle(chat, quick);
  chat.destroy();
  quick.destroy();
  TestBed.resetTestingModule();
  document.body.innerHTML = '';
});

describe('the approval shows in the chat and the quick-ask window, no other menu', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    document.body.innerHTML = '';
  });

  for (const where of ['chat', 'quick ask'] as const) {
    for (const { kind, action, title } of KINDS) {
      for (const { status, act, outcome } of STATUSES) {
        it(`${where}: a ${kind} that is ${status} shows "${title}" with ${
          outcome === null ? 'Approve and Reject' : `"${outcome}" and no Approve`
        }`, async () => {
          const bridge = makeBridge([action]);
          const fixture = await mount(bridge, where);

          await act(fixture, bridge);

          expect(text(fixture, 'approval-title')).toBe(title);
          expect(text(fixture, 'approval-feature')).toBe('GitHub');
          expect(text(fixture, 'approval-target')).toBe(action.target);
          expect(text(fixture, 'approval-summary')).toBe(action.summary);
          expect(text(fixture, 'approval-requested')).toMatch(/^Requested 2026-11-07 \d\d:00$/);
          if (outcome === null) {
            expect(text(fixture, 'approval-strip-heading')).toBe('Waiting for your approval');
            expect(text(fixture, 'approval-approve-act-1')).toBe('Approve');
            expect(text(fixture, 'approval-reject-act-1')).toBe('Reject');
            expect(el(fixture, 'approval-outcome-act-1')).toBeNull();
          } else {
            expect(text(fixture, 'approval-outcome-act-1')).toBe(outcome);
            expect(approveButtons(fixture)).toBe(0);
            expect(el(fixture, 'approval-reject-act-1')).toBeNull();
          }
        });
      }
    }
  }

  it('lists every pending action newest first and says nothing when none waits', async () => {
    const older = github(
      'act-1',
      'New issues',
      'create_issue(body, owner=acme, repo=api, title=x)',
    );
    const newer = github(
      'act-2',
      'Issue comments',
      'comment_on_issue(body, number=3, owner=acme, repo=web)',
      '2026-11-07T11:00:00Z',
    );
    const bridge = makeBridge([older, newer, { ...older, id: 'act-3', status: 'executing' }]);
    const fixture = await mount(bridge, 'chat');

    const titles = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('[data-test="approval-title"]'),
      (node) => (node.textContent ?? '').trim(),
    );

    expect(titles).toEqual(['Comment on issue #3 on acme/web', 'Open an issue on acme/api']);
    expect(el(fixture, 'approval-act-3')).toBeNull();

    TestBed.resetTestingModule();
    const none = await mount(makeBridge([]), 'chat');
    expect(el(none, 'approval-strip')).toBeNull();
  });

  it('a list that cannot be read shows no strip and leaves the chat as it was', async () => {
    const bridge = makeBridge([]);
    bridge.actionsList.mockImplementation(async () => {
      throw ipcError('scout:actions.list', new ControlPlaneUnavailableError('socket reset'));
    });
    const fixture = await mount(bridge, 'chat');

    expect(el(fixture, 'approval-strip')).toBeNull();
    expect(el(fixture, 'empty')).not.toBeNull();
  });
});

describe('the decision is the owner’s click', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    document.body.innerHTML = '';
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const verdict = KINDS[0].action;

  it('Approve calls scout:actions.approve once, and a double click stays one call', async () => {
    const bridge = makeBridge([verdict]);
    const fixture = await mount(bridge, 'chat');
    const approve = el(fixture, 'approval-approve-act-1')!;

    approve.click();
    approve.click();
    await settle(fixture);

    expect(bridge.actionsApprove.mock.calls).toEqual([[{ id: 'act-1' }]]);
    expect(bridge.actionsReject).not.toHaveBeenCalled();
  });

  it('Approve and Reject are disabled while that action’s decision is in flight', async () => {
    const bridge = makeBridge([verdict]);
    let answer: (value: PendingActionDto) => void = () => undefined;
    bridge.actionsApprove.mockImplementationOnce(
      () => new Promise<PendingActionDto>((resolve) => (answer = resolve)),
    );
    const fixture = await mount(bridge, 'quick ask');

    el(fixture, 'approval-approve-act-1')!.click();
    await settle(fixture);
    const during = [
      (el(fixture, 'approval-approve-act-1') as HTMLButtonElement).disabled,
      (el(fixture, 'approval-reject-act-1') as HTMLButtonElement).disabled,
    ];
    answer({ ...verdict, status: 'approved' });
    await settle(fixture);

    expect(during).toEqual([true, true]);
    expect(text(fixture, 'approval-outcome-act-1')).toBe('Approved, running');
  });

  for (const where of ['chat', 'quick ask'] as const) {
    it(`${where}: Approve is never the focused element: not when the card appears, and Reject moves focus to the reason`, async () => {
      const bridge = makeBridge([verdict]);
      const fixture = await mount(bridge, where);
      const approve = el(fixture, 'approval-approve-act-1');
      const onOpen = document.activeElement;

      el(fixture, 'approval-reject-act-1')!.click();
      await settle(fixture);

      expect(onOpen).not.toBe(approve);
      expect(approve?.hasAttribute('autofocus')).toBe(false);
      expect(document.activeElement).toBe(el(fixture, 'approval-reason-act-1'));
      expect(el(fixture, 'approval-approve-act-1')).toBeNull();
    });
  }

  it('the card is a region named by its heading, inside a region headed Waiting for your approval', async () => {
    const fixture = await mount(makeBridge([verdict]), 'chat');
    const strip = el(fixture, 'approval-strip')!;
    const card = el(fixture, 'approval-act-1')!;

    expect(strip.tagName).toBe('SECTION');
    expect(
      document.getElementById(strip.getAttribute('aria-labelledby')!)?.textContent?.trim(),
    ).toBe('Waiting for your approval');
    expect(card.tagName).toBe('SECTION');
    expect(document.getElementById(card.getAttribute('aria-labelledby')!)?.tagName).toBe('H3');
    expect(
      document.getElementById(card.getAttribute('aria-labelledby')!)?.textContent?.trim(),
    ).toBe('Approve pull request #12 on acme/api');
  });

  for (const [reason, sent, shown] of [
    [
      '  Wrong repository ',
      { id: 'act-1', reason: 'Wrong repository' },
      'Rejected: Wrong repository',
    ],
    [null, { id: 'act-1' }, 'Rejected'],
    ['   ', { id: 'act-1' }, 'Rejected'],
  ] as const) {
    it(`Reject with the reason ${JSON.stringify(reason)} sends ${JSON.stringify(sent)} and shows "${shown}"`, async () => {
      const bridge = makeBridge([verdict]);
      const fixture = await mount(bridge, 'quick ask');

      await reject(fixture, 'act-1', reason);

      expect(bridge.actionsReject.mock.calls).toEqual([[sent]]);
      expect(bridge.actionsApprove).not.toHaveBeenCalled();
      expect(text(fixture, 'approval-outcome-act-1')).toBe(shown);
    });
  }

  it('Cancel on the reason sends nothing and offers Approve and Reject again', async () => {
    const bridge = makeBridge([verdict]);
    const fixture = await mount(bridge, 'chat');

    el(fixture, 'approval-reject-act-1')!.click();
    await settle(fixture);
    el(fixture, 'approval-cancel-act-1')!.click();
    await settle(fixture);

    expect(bridge.actionsReject).not.toHaveBeenCalled();
    expect(el(fixture, 'approval-approve-act-1')).not.toBeNull();
    expect(document.activeElement).toBe(el(fixture, 'approval-reject-act-1'));
  });

  for (const [decision, channel] of [
    ['approve', 'scout:actions.approve'],
    ['reject', 'scout:actions.reject'],
  ] as const) {
    it(`a ${decision} the control plane fails says on the card that the control plane did not answer, never the socket failure, and leaves the action to decide again`, async () => {
      const bridge = makeBridge([verdict]);
      const failing = decision === 'approve' ? bridge.actionsApprove : bridge.actionsReject;
      failing.mockImplementationOnce(async () => {
        throw ipcError(channel, new ControlPlaneUnavailableError('socket reset'));
      });
      const fixture = await mount(bridge, 'chat');

      if (decision === 'approve') {
        el(fixture, 'approval-approve-act-1')!.click();
        await settle(fixture);
      } else {
        await reject(fixture, 'act-1', 'Wrong repository');
      }

      expect(text(fixture, 'approval-error-act-1')).toBe('The Scout control plane did not answer.');
      expect(el(fixture, 'approval-error-act-1')?.getAttribute('role')).toBe('alert');
      expect(el(fixture, 'approval-outcome-act-1')).toBeNull();
      expect(bridge.listed().map((a) => a.status)).toEqual(['pending']);
    });
  }

  for (const where of ['chat', 'quick ask'] as const) {
    it(`${where}: an approval refused as asked before the grant keeps its card as Rejected with that reason, not an error`, async () => {
      const bridge = makeBridge([verdict]);
      bridge.actionsApprove.mockImplementationOnce(async () => {
        throw ipcError('scout:actions.approve', new ControlPlaneActionRejectedError());
      });
      const fixture = await mount(bridge, where);

      el(fixture, 'approval-approve-act-1')!.click();
      await settle(fixture);

      expect(text(fixture, 'approval-outcome-act-1')).toBe(
        'Rejected: asked before writes were granted; ask again',
      );
      expect(el(fixture, 'approval-error-act-1')).toBeNull();
      expect(el(fixture, 'approval-approve-act-1')).toBeNull();
    });
  }

  it('a write log that cannot be read keeps the card at Approved, running and the strip working', async () => {
    const bridge = makeBridge([verdict]);
    bridge.writesList.mockImplementation(async () => {
      throw ipcError('scout:writes.list', new ControlPlaneUnavailableError('socket reset'));
    });
    const fixture = await mount(bridge, 'chat');

    el(fixture, 'approval-approve-act-1')!.click();
    await settle(fixture);
    bridge.record('act-1', 'succeeded');
    await TestBed.inject(PendingApprovals).load();
    await settle(fixture);

    expect(bridge.writesList).toHaveBeenCalled();
    expect(text(fixture, 'approval-outcome-act-1')).toBe('Approved, running');
    expect(el(fixture, 'approval-error-act-1')).toBeNull();
    expect(el(fixture, 'approval-dismiss-act-1')).not.toBeNull();
  });

  it('a dismissed outcome leaves the strip; the decision stays in the control plane', async () => {
    const bridge = makeBridge([verdict]);
    const fixture = await mount(bridge, 'chat');

    await reject(fixture, 'act-1', null);
    el(fixture, 'approval-dismiss-act-1')!.click();
    await settle(fixture);

    expect(el(fixture, 'approval-strip')).toBeNull();
    expect(bridge.actionsReject.mock.calls).toEqual([[{ id: 'act-1' }]]);
  });

  it('the list is read again when a chat turn ends, so an action queued during the turn shows at once', async () => {
    const bridge = makeBridge([]);
    const fixture = await mount(bridge, 'chat');
    const turn: LocalTurn = {
      id: 't1',
      question: 'Approve #12',
      reply: '',
      citations: [],
      state: 'running',
    };

    bridge.pushTurns([turn]);
    await settle(fixture);
    bridge.queue(verdict);
    bridge.pushTurns([{ ...turn, reply: 'Queued for your approval.', state: 'done' }]);
    await settle(fixture);

    expect(text(fixture, 'approval-title')).toBe('Approve pull request #12 on acme/api');
  });

  it("the owner's approval logged with a reason is not the outcome: the card stays at Approved, running", async () => {
    const bridge = makeBridge([verdict]);
    const fixture = await mount(bridge, 'chat');

    el(fixture, 'approval-approve-act-1')!.click();
    await settle(fixture);
    bridge.record('act-1', 'approved: Checked the diff');
    bridge.changed();
    await settle(fixture);

    expect(text(fixture, 'approval-outcome-act-1')).toBe('Approved, running');
  });

  it('every 15 seconds the list is read again, so a running action reaches Done without a push', async () => {
    vi.useFakeTimers({ toFake: ['setInterval'] });
    const bridge = makeBridge([verdict]);
    const fixture = await mount(bridge, 'chat');

    el(fixture, 'approval-approve-act-1')!.click();
    await settle(fixture);
    bridge.record('act-1', 'succeeded');
    vi.advanceTimersByTime(15_000);
    await settle(fixture);

    expect(text(fixture, 'approval-outcome-act-1')).toBe('Done');
  });
});

describe('once decided anywhere, no window offers Approve and the Approvals count drops', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    document.body.innerHTML = '';
  });

  const first = KINDS[0].action;
  const second = github(
    'act-2',
    'New issues',
    'create_issue(body, owner=acme, repo=web, title=Broken link)',
    '2026-11-07T09:00:00Z',
  );

  async function windows(bridge: Bridge) {
    configure(bridge, true);
    const chat = create<unknown>(ChatComponent);
    const quick = create<unknown>(QuickAskComponent);
    const approvals = create<unknown>(ReviewComponent);
    await settle(chat, quick, approvals);
    return { chat, quick, approvals, all: [chat, quick, approvals] as const };
  }

  for (const place of ['chat', 'quick ask', 'Approvals'] as const) {
    for (const decision of ['approve', 'reject'] as const) {
      it(`decided (${decision}) in ${place}: chat, quick ask and Approvals offer no Approve for it, and the count reads 1`, async () => {
        const bridge = makeBridge([first, second]);
        const w = await windows(bridge);
        const before = [w.chat, w.quick, w.approvals].map(approveButtons);
        const countBefore = text(w.approvals, 'pending-count');

        if (place === 'Approvals') {
          el(w.approvals, `${decision}-act-1`)!.click();
        } else {
          const fixture = place === 'chat' ? w.chat : w.quick;
          if (decision === 'approve') el(fixture, 'approval-approve-act-1')!.click();
          else {
            el(fixture, 'approval-reject-act-1')!.click();
            await settle(...w.all);
            el(fixture, 'approval-confirm-reject-act-1')!.click();
          }
        }
        await settle(...w.all);

        expect(before).toEqual([2, 2, 2]);
        expect(countBefore).toBe('2');
        for (const fixture of w.all) {
          expect(el(fixture, 'approval-approve-act-1')).toBeNull();
          expect(el(fixture, 'approve-act-1')).toBeNull();
          expect(approveButtons(fixture)).toBe(1);
        }
        expect(text(w.approvals, 'pending-count')).toBe('1');
        for (const [name, fixture] of [
          ['chat', w.chat],
          ['quick ask', w.quick],
        ] as const) {
          const outcome = el(fixture, 'approval-outcome-act-1');
          if (name === place) {
            expect(outcome?.textContent?.trim()).toBe(
              decision === 'approve' ? 'Approved, running' : 'Rejected',
            );
          } else {
            expect(el(fixture, 'approval-act-1')).toBeNull();
          }
        }
      });
    }
  }

  for (const [firstDecision, secondDecision] of [
    ['approve', 'approve'],
    ['approve', 'reject'],
    ['reject', 'approve'],
  ] as const) {
    it(`two windows at once (${firstDecision} in chat, ${secondDecision} in quick ask): the second says Already decided and changes nothing`, async () => {
      const bridge = makeBridge([first]);
      const w = await windows(bridge);

      const click = async (fixture: ComponentFixture<unknown>, decision: 'approve' | 'reject') => {
        if (decision === 'approve') el(fixture, 'approval-approve-act-1')!.click();
        else {
          el(fixture, 'approval-reject-act-1')!.click();
          await settle(fixture);
          el(fixture, 'approval-confirm-reject-act-1')!.click();
        }
      };
      if (secondDecision === 'reject') {
        el(w.quick, 'approval-reject-act-1')!.click();
        await settle(w.quick);
      }
      await click(w.chat, firstDecision);
      if (secondDecision === 'reject') el(w.quick, 'approval-confirm-reject-act-1')!.click();
      else el(w.quick, 'approval-approve-act-1')!.click();
      await settle(...w.all);

      const calls =
        bridge.actionsApprove.mock.calls.length + bridge.actionsReject.mock.calls.length;
      expect(calls).toBe(2);
      expect(text(w.quick, 'approval-outcome-act-1')).toBe('Already decided');
      expect(text(w.chat, 'approval-outcome-act-1')).toBe(
        firstDecision === 'approve' ? 'Approved, running' : 'Rejected',
      );
      expect(bridge.listed().map((a) => a.status)).toEqual(
        firstDecision === 'approve' ? ['executing'] : [],
      );
      for (const fixture of w.all) expect(approveButtons(fixture)).toBe(0);
    });
  }
});

describe('owner-reported bugs', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    document.body.innerHTML = '';
  });

  it('a code review approved by the bot no longer has to be approved in another menu: the chat approves it', async () => {
    const verdict = KINDS[0].action;
    const bridge = makeBridge([]);
    const fixture = await mount(bridge, 'chat');
    const turn: LocalTurn = {
      id: 't1',
      question: 'Review acme/api#12 and approve it if it is fine',
      reply: '',
      citations: [],
      state: 'running',
    };

    bridge.pushTurns([turn]);
    await settle(fixture);
    bridge.queue(verdict);
    bridge.pushTurns([{ ...turn, reply: 'The verdict waits for your approval.', state: 'done' }]);
    await settle(fixture);
    el(fixture, 'approval-approve-act-1')?.click();
    await settle(fixture);

    expect(bridge.actionsApprove.mock.calls).toEqual([[{ id: 'act-1' }]]);
    expect(text(fixture, 'approval-outcome-act-1')).toBe('Approved, running');
  });

  it('an action approved in the chat no longer shows in Approvals asking for approval', async () => {
    const bridge = makeBridge([KINDS[0].action]);
    configure(bridge);
    const chat = create<unknown>(ChatComponent);
    const approvals = create<unknown>(ReviewComponent);
    await settle(chat, approvals);

    el(chat, 'approval-approve-act-1')!.click();
    await settle(chat, approvals);

    expect(bridge.listed().map((a) => a.status)).toEqual(['executing']);
    expect(el(approvals, 'action-act-1')).toBeNull();
    expect(el(approvals, 'approve-act-1')).toBeNull();
    expect(el(approvals, 'pending-count')).toBeNull();
    expect(text(approvals, 'pending-empty')).toContain('Nothing is waiting.');
  });
});

describe('the action in words', () => {
  for (const [action, target, summary, words] of [
    [
      'Pull request verdicts',
      'acme/api#12',
      'Approve acme/api#12',
      'Approve pull request #12 on acme/api',
    ],
    [
      'Pull request verdicts',
      'acme/api#12',
      'Request changes on acme/api#12',
      'Request changes on pull request #12 on acme/api',
    ],
    [
      'Pull request reviews',
      'GitHub.submit_pull_request_review',
      'submit_pull_request_review(body, number=4, owner=acme, repo=api)',
      'Post a review on pull request #4 on acme/api',
    ],
    [
      'Issue comments',
      'GitHub.comment_on_issue',
      'comment_on_issue(body, number=9, owner=acme, repo=api)',
      'Comment on issue #9 on acme/api',
    ],
    ['Upload video', 'channel UC1', 'Upload "Intro" as private', 'Upload video'],
    [
      'Pull request verdicts',
      'GitHub.give_pull_request_verdict',
      'give_pull_request_verdict(body, number=12, owner=acme, repo=api, verdict=)',
      'Pull request verdicts',
    ],
  ] as const) {
    it(`${action} on ${target} reads "${words}"`, () => {
      const dto: PendingActionDto = {
        id: 'a',
        featureId: 'github',
        action,
        target,
        summary,
        requestedAt: '2026-11-07T10:00:00Z',
        status: 'pending',
        decidedAt: null,
      };

      expect(describeAction(dto)).toBe(words);
    });
  }
});

describe('the approval strip follows the theme', () => {
  it('its styles paint only with the theme tokens, no hex, rgb() or hsl() colour left', () => {
    const source = readFileSync('src/app/core/approval-strip.component.ts', 'utf8');
    const css = source.slice(source.indexOf('  styles:'), source.indexOf('\n})\n'));

    expect(css).toContain('var(--sc-');
    expect(css.match(/#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(/g) ?? []).toEqual([]);
  });

  it('Approve carries the check icon and the primary look, Reject the close icon and the danger look', async () => {
    TestBed.resetTestingModule();
    const fixture = await mount(makeBridge([KINDS[0].action]), 'chat');

    const approve = el(fixture, 'approval-approve-act-1')!;
    const reject = el(fixture, 'approval-reject-act-1')!;

    expect(approve.querySelector('sc-icon path')?.getAttribute('d')).toBe(ICONS.check);
    expect(approve.textContent?.trim()).toBe('Approve');
    expect(approve.classList.contains('primary')).toBe(true);
    expect(reject.querySelector('sc-icon path')?.getAttribute('d')).toBe(ICONS.close);
    expect(reject.textContent?.trim()).toBe('Reject');
    expect(reject.classList.contains('danger')).toBe(true);
  });
});

/** The value the last rule matching `node` in the page's style elements declares for `property`; null when none does. */
function declared(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found === undefined) continue;
    if (selector!.split(',').some((one) => matchesSafely(node, one.trim()))) value = found;
  }
  return value;
}

function matchesSafely(node: Element, selector: string): boolean {
  try {
    return node.matches(selector);
  } catch {
    return false;
  }
}

describe('the strip as Main.dc.html draws it, state by state', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    document.body.innerHTML = '';
  });

  const verdict = KINDS[0].action;

  function buttons(fixture: ComponentFixture<unknown>): string[] {
    const card = el(fixture, 'approval-act-1');
    return card === null
      ? []
      : Array.from(
          card.querySelectorAll('button'),
          (button) => `${(button.textContent ?? '').trim()}${button.disabled ? ' (off)' : ''}`,
        );
  }

  function outcome(fixture: ComponentFixture<unknown>) {
    const chip = el(fixture, 'approval-outcome-act-1');
    return chip === null
      ? null
      : {
          text: text(fixture, 'approval-outcome-act-1'),
          background: declared(chip, 'background'),
          color: declared(chip, 'color'),
          role: chip.getAttribute('role'),
        };
  }

  const STATES: readonly {
    readonly state: string;
    readonly setUp?: (bridge: Bridge) => void;
    readonly act: (fixture: ComponentFixture<unknown>, bridge: Bridge) => Promise<void>;
    readonly heading: string | null;
    readonly buttons: readonly string[];
    readonly outcome: ReturnType<typeof outcome>;
    readonly alert: string | null;
  }[] = [
    {
      state: 'Waiting',
      act: async () => undefined,
      heading: 'Waiting for your approval',
      buttons: ['Reject', 'Approve'],
      outcome: null,
      alert: null,
    },
    {
      state: 'Rejecting',
      act: async (fixture) => {
        el(fixture, 'approval-reject-act-1')!.click();
        await settle(fixture);
      },
      heading: 'Waiting for your approval',
      buttons: ['Cancel', 'Reject'],
      outcome: null,
      alert: null,
    },
    {
      state: 'Deciding',
      setUp: (bridge) =>
        bridge.actionsApprove.mockImplementationOnce(() => new Promise<PendingActionDto>(() => {})),
      act: async (fixture) => {
        el(fixture, 'approval-approve-act-1')!.click();
        await settle(fixture);
      },
      heading: 'Waiting for your approval',
      buttons: ['Reject (off)', 'Approve (off)'],
      outcome: null,
      alert: null,
    },
    {
      state: 'Approve failed',
      setUp: (bridge) =>
        bridge.actionsApprove.mockImplementationOnce(async () => {
          throw ipcError('scout:actions.approve', new ControlPlaneUnavailableError('reset'));
        }),
      act: async (fixture) => {
        el(fixture, 'approval-approve-act-1')!.click();
        await settle(fixture);
      },
      heading: 'Waiting for your approval',
      buttons: ['Reject', 'Approve'],
      outcome: null,
      alert: 'The Scout control plane did not answer.',
    },
    {
      state: 'Approved, running',
      act: async (fixture) => {
        el(fixture, 'approval-approve-act-1')!.click();
        await settle(fixture);
      },
      heading: 'Your decisions',
      buttons: ['Dismiss'],
      outcome: {
        text: 'Approved, running',
        background: 'var(--sc-surface-2)',
        color: 'var(--sc-accent)',
        role: 'status',
      },
      alert: null,
    },
    {
      state: 'Done',
      act: async (fixture, bridge) => {
        el(fixture, 'approval-approve-act-1')!.click();
        await settle(fixture);
        bridge.record('act-1', 'succeeded');
        await TestBed.inject(PendingApprovals).load();
        await settle(fixture);
      },
      heading: 'Your decisions',
      buttons: ['Dismiss'],
      outcome: {
        text: 'Done',
        background: 'var(--sc-ok-container)',
        color: 'var(--sc-on-ok-container)',
        role: 'status',
      },
      alert: null,
    },
    {
      state: 'Rejected',
      act: async (fixture) => reject(fixture, 'act-1', 'Wrong repository'),
      heading: 'Your decisions',
      buttons: ['Dismiss'],
      outcome: {
        text: 'Rejected: Wrong repository',
        background: 'var(--sc-surface-2)',
        color: 'var(--sc-fg-2)',
        role: 'status',
      },
      alert: null,
    },
    {
      state: 'Rejected before the grant',
      setUp: (bridge) =>
        bridge.actionsApprove.mockImplementationOnce(async () => {
          throw ipcError('scout:actions.approve', new ControlPlaneActionRejectedError());
        }),
      act: async (fixture) => {
        el(fixture, 'approval-approve-act-1')!.click();
        await settle(fixture);
      },
      heading: 'Your decisions',
      buttons: ['Dismiss'],
      outcome: {
        text: 'Rejected: asked before writes were granted; ask again',
        background: 'var(--sc-surface-2)',
        color: 'var(--sc-fg-2)',
        role: 'status',
      },
      alert: null,
    },
    {
      state: 'Failed',
      act: async (fixture, bridge) => {
        el(fixture, 'approval-approve-act-1')!.click();
        await settle(fixture);
        bridge.record('act-1', 'failed: GitHub answered 422, the pull request is closed');
        await TestBed.inject(PendingApprovals).load();
        await settle(fixture);
      },
      heading: 'Your decisions',
      buttons: ['Dismiss'],
      outcome: {
        text: 'Failed: GitHub answered 422, the pull request is closed',
        background: 'var(--sc-error-container)',
        color: 'var(--sc-on-error-container)',
        role: 'status',
      },
      alert: null,
    },
    {
      state: 'Already decided',
      setUp: (bridge) =>
        bridge.actionsApprove.mockImplementationOnce(async () => {
          throw ipcError('scout:actions.approve', new ControlPlaneAlreadyDecidedError());
        }),
      act: async (fixture) => {
        el(fixture, 'approval-approve-act-1')!.click();
        await settle(fixture);
      },
      heading: 'Your decisions',
      buttons: ['Dismiss'],
      outcome: {
        text: 'Already decided',
        background: 'var(--sc-surface-2)',
        color: 'var(--sc-fg-2)',
        role: 'status',
      },
      alert: null,
    },
    {
      state: 'None',
      act: async (fixture) => {
        await reject(fixture, 'act-1', null);
        el(fixture, 'approval-dismiss-act-1')!.click();
        await settle(fixture);
      },
      heading: null,
      buttons: [],
      outcome: null,
      alert: null,
    },
  ];

  for (const where of ['chat', 'quick ask'] as const) {
    for (const row of STATES) {
      it(`${where}: Approval "${row.state}" shows ${row.heading === null ? 'no strip' : `"${row.heading}"`}, the buttons ${JSON.stringify(row.buttons)} and ${row.outcome === null ? 'no outcome' : `"${row.outcome.text}"`}`, async () => {
        const bridge = makeBridge([verdict]);
        row.setUp?.(bridge);
        const fixture = await mount(bridge, where);

        await row.act(fixture, bridge);

        expect({
          heading:
            el(fixture, 'approval-strip-heading') === null
              ? null
              : text(fixture, 'approval-strip-heading'),
          buttons: buttons(fixture),
          outcome: outcome(fixture),
          alert:
            el(fixture, 'approval-error-act-1') === null
              ? null
              : text(fixture, 'approval-error-act-1'),
        }).toEqual({
          heading: row.heading,
          buttons: row.buttons,
          outcome: row.outcome,
          alert: row.alert,
        });
      });
    }
  }

  it('in the chat the strip sits between the conversation and the composer', async () => {
    const fixture = await mount(makeBridge([verdict]), 'chat');
    const strip = el(fixture, 'approval-strip')!.closest('sc-approval-strip')!;

    expect(strip.previousElementSibling?.getAttribute('data-test')).toBe('turns');
    expect(strip.nextElementSibling?.tagName).toBe('FORM');
  });

  it('the approval card keeps the 12 px corners Main.dc.html and QuickAsk.dc.html draw, not the 14 px card token', async () => {
    const fixture = await mount(makeBridge([verdict]), 'chat');
    const card = el(fixture, 'approval-act-1')!;

    expect(declared(card, 'border-radius')).toBe('12px');
  });

  for (const [name, testId, open] of [
    ['Reject', 'approval-reject-act-1', false],
    ['Reject with the reason open', 'approval-confirm-reject-act-1', true],
  ] as const) {
    it(`${name} is a Material outlined button drawn as the artboard's: 34 px, 14 px padding, 8 px corners, the error colour on its label and outline, dimmed while the decision is out`, async () => {
      const fixture = await mount(makeBridge([verdict]), 'chat');
      if (open) {
        el(fixture, 'approval-reject-act-1')!.click();
        await settle(fixture);
      }
      const button = el(fixture, testId)!;

      expect({
        material: button.classList.contains('mat-mdc-outlined-button'),
        icon: button.querySelector('sc-icon path')?.getAttribute('d'),
        label: button.textContent?.trim(),
        height: declared(button, '--mat-button-outlined-container-height'),
        padding: declared(button, '--mat-button-outlined-horizontal-padding'),
        shape: declared(button, '--mat-button-outlined-container-shape'),
        size: declared(button, '--mat-button-outlined-label-text-size'),
        weight: declared(button, '--mat-button-outlined-label-text-weight'),
        color: declared(button, '--mat-button-outlined-label-text-color'),
        outline: declared(button, '--mat-button-outlined-outline-color'),
        whileDeciding: [
          declared(button, '--mat-button-outlined-disabled-label-text-color'),
          declared(button, '--mat-button-outlined-disabled-outline-color'),
          declared(button, '--reject-dimmed'),
        ],
      }).toEqual({
        material: true,
        icon: ICONS.close,
        label: 'Reject',
        height: '34px',
        padding: '14px',
        shape: 'var(--sc-radius-sm)',
        size: '14px',
        weight: '400',
        color: 'var(--sc-error)',
        outline: 'var(--sc-error)',
        whileDeciding: [
          'var(--reject-dimmed)',
          'var(--reject-dimmed)',
          'color-mix(in srgb, var(--sc-error) 55%, transparent)',
        ],
      });
    });
  }

  it("Approve is a Material filled button drawn as the artboard's: 34 px, 14 px padding, 8 px corners, white on the strong accent", async () => {
    const fixture = await mount(makeBridge([verdict]), 'chat');
    const approve = el(fixture, 'approval-approve-act-1')!;

    expect({
      material: approve.classList.contains('mat-mdc-unelevated-button'),
      icon: approve.querySelector('sc-icon path')?.getAttribute('d'),
      label: approve.textContent?.trim(),
      height: declared(approve, '--mat-button-filled-container-height'),
      padding: declared(approve, '--mat-button-filled-horizontal-padding'),
      shape: declared(approve, '--mat-button-filled-container-shape'),
      size: declared(approve, '--mat-button-filled-label-text-size'),
      weight: declared(approve, '--mat-button-filled-label-text-weight'),
      container: declared(approve, '--mat-button-filled-container-color'),
      color: declared(approve, '--mat-button-filled-label-text-color'),
      disabled: [
        declared(approve, '--mat-button-filled-disabled-container-color'),
        declared(approve, '--mat-button-filled-disabled-label-text-color'),
      ],
    }).toEqual({
      material: true,
      icon: ICONS.check,
      label: 'Approve',
      height: '34px',
      padding: '14px',
      shape: 'var(--sc-radius-sm)',
      size: '14px',
      weight: '400',
      container: 'var(--sc-accent-strong)',
      color: 'var(--sc-on-accent)',
      disabled: ['var(--sc-surface-2)', 'var(--sc-muted)'],
    });
  });
});
