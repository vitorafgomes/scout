import { describe, expect, it } from 'bun:test';
import { readFile } from 'node:fs/promises';
import * as path from 'node:path';
import type { PendingActionDto } from '@scout/shared';
import { NotificationStore } from '../src/notifications/notification-store.js';
import {
  APPROVALS_POLL_MS,
  watchPendingApprovals,
} from '../src/notifications/pending-approvals.js';
import { recordingLogger } from './kubernetes-fakes.js';

function action(id: string, status = 'pending', at = '2026-11-08T09:00:00Z'): PendingActionDto {
  return {
    id,
    featureId: 'github',
    action: 'Pull request verdicts',
    target: `acme/api#${id}`,
    summary: 'Approve pull request',
    requestedAt: at,
    status,
    decidedAt: null,
  };
}

function setup(answer: () => Promise<readonly PendingActionDto[]>) {
  const clock = { now: 1_000 };
  const store = new NotificationStore({
    vault: { get: async () => null, store: async () => undefined },
    push: () => undefined,
    now: () => clock.now,
    setTimer: () => () => undefined,
    logger: recordingLogger().logger,
  });
  const timers: { ms: number; callback: () => void; cancelled: boolean }[] = [];
  const { logger, lines } = recordingLogger();
  let reads = 0;
  const approvals = watchPendingApprovals({
    list: () => {
      reads += 1;
      return answer();
    },
    store,
    setTimer: (ms, callback) => {
      const timer = { ms, callback, cancelled: false };
      timers.push(timer);
      return () => {
        timer.cancelled = true;
      };
    },
    logger,
  });
  const entry = () => store.list().find((n) => n.id === 'approvals:pending');
  const pending = () => timers.filter((t) => !t.cancelled).map((t) => t.ms);
  return { store, approvals, entry, pending, lines, clock, reads: () => reads };
}

describe('the pending approvals entry in the notification center', () => {
  it('one action waiting becomes one entry with its name, its target and Open Approvals', async () => {
    const t = setup(async () => [action('12'), action('9', 'executed')]);

    await t.approvals.check();

    expect(t.entry()).toMatchObject({
      kind: 'paused',
      title: 'An action waits for your approval',
      detail: 'Pull request verdicts on acme/api#12',
      at: Date.parse('2026-11-08T09:00:00Z'),
      read: false,
      actions: [{ id: 'open-approvals', label: 'Open Approvals' }],
      source: 'worker',
    });
  });

  it('more actions replace the entry rather than stacking another, naming the first three', async () => {
    let queue = [action('1')];
    const t = setup(async () => queue);
    await t.approvals.check();

    queue = ['1', '2', '3', '4', '5'].map((id) =>
      action(id, 'pending', `2026-11-08T09:0${id}:00Z`),
    );
    await t.approvals.check();

    expect(t.store.list().filter((n) => n.id.startsWith('approvals:'))).toHaveLength(1);
    expect(t.entry()).toMatchObject({
      title: '5 actions wait for your approval',
      detail:
        'Pull request verdicts on acme/api#1; Pull request verdicts on acme/api#2; Pull request verdicts on acme/api#3; and 2 more',
    });
  });

  it('the same queue read again leaves a read entry read', async () => {
    const t = setup(async () => [action('12')]);
    await t.approvals.check();
    t.store.markRead();
    t.clock.now += 15_000;

    await t.approvals.check();

    expect(t.entry()!.read).toBe(true);
  });

  it('the entry is removed once nothing waits', async () => {
    let queue = [action('12')];
    const t = setup(async () => queue);
    await t.approvals.check();

    queue = [action('12', 'approved')];
    await t.approvals.check();

    expect(t.entry()).toBeUndefined();
  });

  it('a queue the control plane cannot answer keeps the entry and reads again later', async () => {
    let fail = false;
    const t = setup(async () => {
      if (fail) throw new Error('control plane unavailable');
      return [action('12')];
    });
    await t.approvals.check();

    fail = true;
    await t.approvals.check();

    expect(t.entry()!.title).toBe('An action waits for your approval');
    expect(t.pending()).toEqual([APPROVALS_POLL_MS]);
    expect(t.lines.some((l) => l.includes('the queue could not be read'))).toBe(true);
  });

  it('a check while a read runs does not read twice', async () => {
    let release: () => void = () => undefined;
    const t = setup(
      () =>
        new Promise((resolve) => {
          release = () => resolve([action('12')]);
        }),
    );

    const first = t.approvals.check();
    const second = t.approvals.check();
    release();
    await Promise.all([first, second]);

    expect(t.reads()).toBe(1);
  });

  it('reads every 15 seconds until disposed', async () => {
    const t = setup(async () => []);
    await t.approvals.check();
    const scheduled = t.pending();

    t.approvals.dispose();

    expect(scheduled).toEqual([15_000]);
    expect(t.pending()).toEqual([]);
  });
});

describe('main.ts wiring of the bell sources', () => {
  it('the approvals queue, the worker health, the GitHub disconnect and the token refresh reach the bell', async () => {
    const main = await readFile(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');
    const actionsChanged = main.slice(main.indexOf('onActionsChanged: () => {'));

    expect(main).toMatch(
      /watchPendingApprovals\(\{\s*list: async \(\) => \(await connection\.client\)\.listPendingActions\(\),\s*store: notifications,/,
    );
    expect(main).toContain('void pendingApprovals.check();');
    expect(actionsChanged.slice(0, actionsChanged.indexOf('},'))).toContain(
      'void pendingApprovals?.check();',
    );
    expect(main).toContain(
      'unwatchWorkerHealth = watchWorkerHealth(workerSupervisor, notifications);',
    );
    expect(main).toContain(
      "clearEntries: () => notifications.remove((n) => n.source === 'github'),",
    );
    expect(main).toContain('onTokenRefreshed: () => gitHubActivity?.tokenRefreshed(),');
    expect(main).toContain('pendingApprovals?.dispose();');
    expect(main).toContain('unwatchWorkerHealth?.();');
  });
});
