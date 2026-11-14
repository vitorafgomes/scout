import { readFileSync } from 'node:fs';

import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { AppNotification, JobControlState, NotificationKind } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';
import {
  NotificationBellComponent,
  timeAgo,
  type NotificationFilter,
} from './notification-bell.component';

const NOW = Date.now();

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(NOW);
});

afterEach(() => {
  vi.useRealTimers();
});

function note(id: string, extra: Partial<AppNotification> = {}): AppNotification {
  return {
    id,
    kind: 'done',
    title: `Title ${id}`,
    detail: `Detail ${id}`,
    at: NOW,
    read: false,
    source: 'worker',
    ...extra,
  };
}

function makeBridge(initial: readonly AppNotification[]) {
  const listeners: ((list: readonly AppNotification[]) => void)[] = [];
  return {
    push: (list: readonly AppNotification[]) => listeners.forEach((listener) => listener(list)),
    notificationsList: vi.fn(async () => initial),
    notificationsMarkRead: vi.fn(async (ids?: readonly string[]) =>
      initial.map((n) => (ids === undefined || ids.includes(n.id) ? { ...n, read: true } : n)),
    ),
    onNotifications: vi.fn((listener: (list: readonly AppNotification[]) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
    appOpenExternal: vi.fn(async (_req: { url: string }) => undefined),
    notificationsAct: vi.fn(async (_req: { id: string; action: string }) => undefined),
    notificationsControl: vi.fn(async (_req: { id: string; action: string }) => undefined),
    onLocalTurns: vi.fn(() => () => undefined),
    localGetTurns: vi.fn(async () => []),
    localAsk: vi.fn(async (_req: { id: string; text: string }) => undefined),
  };
}

async function settle(fixture: ComponentFixture<NotificationBellComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

async function render(
  bridge: ReturnType<typeof makeBridge>,
): Promise<ComponentFixture<NotificationBellComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(NotificationBellComponent);
  document.body.appendChild(fixture.nativeElement as HTMLElement);
  await settle(fixture);
  return fixture;
}

function el(
  fixture: ComponentFixture<NotificationBellComponent>,
  test: string,
): HTMLElement | null {
  return (fixture.nativeElement as HTMLElement).querySelector(`[data-test="${test}"]`);
}

function all(fixture: ComponentFixture<NotificationBellComponent>, test: string): HTMLElement[] {
  return Array.from(
    (fixture.nativeElement as HTMLElement).querySelectorAll(`[data-test="${test}"]`),
  );
}

async function click(fixture: ComponentFixture<NotificationBellComponent>, test: string) {
  el(fixture, test)!.click();
  await settle(fixture);
}

const KINDS: readonly NotificationKind[] = [
  'running',
  'paused',
  'done',
  'failed',
  'github',
  'mention',
];
const FILTERS: readonly NotificationFilter[] = ['all', 'running', 'github', 'done'];
const SHOWN_UNDER: Readonly<Record<NotificationFilter, readonly NotificationKind[]>> = {
  all: KINDS,
  running: ['running', 'paused'],
  github: ['github', 'mention'],
  done: ['done', 'failed'],
};

interface Row {
  readonly panel: 'closed' | 'open';
  readonly filter: NotificationFilter;
  readonly kind: NotificationKind;
  readonly read: boolean;
}

const MATRIX: readonly Row[] = (['closed', 'open'] as const).flatMap((panel) =>
  FILTERS.flatMap((filter) =>
    KINDS.flatMap((kind) => [false, true].map((read) => ({ panel, filter, kind, read }))),
  ),
);

describe('NotificationBellComponent matrix: panel state x filter x entry kind x read', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    document.body.innerHTML = '';
  });

  it('covers 2 panel states x 4 filters x 6 kinds x 2 read states', () => {
    expect(MATRIX.length).toBe(96);
  });

  it.each(MATRIX)('$panel panel, filter $filter, a $kind entry, read $read', async (row) => {
    const fixture = await render(makeBridge([note('n1', { kind: row.kind, read: row.read })]));
    if (row.panel === 'open') {
      await click(fixture, 'bell');
      await click(fixture, `filter-${row.filter}`);
    }

    const bell = el(fixture, 'bell')!;
    expect(bell.getAttribute('aria-label')).toBe(
      row.read ? 'Notifications' : 'Notifications, 1 unread',
    );
    expect(el(fixture, 'badge')?.textContent?.trim() ?? null).toBe(row.read ? null : '1');
    expect(bell.getAttribute('aria-expanded')).toBe(String(row.panel === 'open'));

    const entries = all(fixture, 'entry');
    const visible = row.panel === 'open' && SHOWN_UNDER[row.filter].includes(row.kind);
    expect(entries.length).toBe(visible ? 1 : 0);
    if (row.panel === 'open') {
      expect(el(fixture, `filter-${row.filter}`)!.getAttribute('aria-selected')).toBe('true');
      expect(el(fixture, 'empty') !== null).toBe(!visible);
    } else {
      expect(el(fixture, 'panel')).toBeNull();
    }
    if (visible) {
      expect(entries[0]!.getAttribute('data-kind')).toBe(row.kind);
      expect(entries[0]!.classList.contains('unread')).toBe(!row.read);
      expect(el(fixture, 'entry-title')!.textContent?.trim()).toBe('Title n1');
    }
  });
});

describe('NotificationBellComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    document.body.innerHTML = '';
  });

  it('the badge counts the unread entries and the label says how many', async () => {
    const fixture = await render(
      makeBridge([note('a'), note('b'), note('c', { read: true }), note('d', { kind: 'github' })]),
    );

    expect(el(fixture, 'badge')!.textContent?.trim()).toBe('3');
    expect(el(fixture, 'bell')!.getAttribute('aria-label')).toBe('Notifications, 3 unread');
  });

  for (const [unread, shown] of [
    [0, null],
    [1, '1'],
    [3, '3'],
    [12, '12'],
  ] as const) {
    it(`with ${unread} unread the Material badge on the bell ${shown === null ? 'is hidden' : `shows ${shown}`}`, async () => {
      const fixture = await render(
        makeBridge([
          ...Array.from({ length: unread }, (_, i) => note(`n${i}`)),
          note('seen', { read: true }),
        ]),
      );

      const drawn = el(fixture, 'bell')!.querySelector('.mat-badge-content');

      expect({
        count: drawn?.textContent?.trim() ?? null,
        insideTheBadge: drawn === null ? null : (el(fixture, 'badge')?.contains(drawn) ?? false),
      }).toEqual({ count: shown, insideTheBadge: shown === null ? null : true });
    });
  }

  it('the Material badge overlaps the top right corner of the bell and is hidden from screen readers, whose count is in the label', async () => {
    const fixture = await render(makeBridge([note('a'), note('b')]));

    const anchor = el(fixture, 'badge')!;
    const drawn = anchor.querySelector('.mat-badge-content')!;

    expect({
      position: ['mat-badge-above', 'mat-badge-after', 'mat-badge-overlap'].filter((name) =>
        anchor.classList.contains(name),
      ),
      hidden: [anchor.getAttribute('aria-hidden'), drawn.getAttribute('aria-hidden')],
      label: el(fixture, 'bell')!.getAttribute('aria-label'),
    }).toEqual({
      position: ['mat-badge-above', 'mat-badge-after', 'mat-badge-overlap'],
      hidden: ['true', 'true'],
      label: 'Notifications, 2 unread',
    });
  });

  for (const [unread, shown] of [
    [9, '9'],
    [12, '12'],
    [100, '100'],
  ] as const) {
    it(`with ${unread} unread the count "${shown}" sits on the bell's own box and keeps its right edge 4 px outside it, so a longer count grows to the left`, async () => {
      const fixture = await render(
        makeBridge(Array.from({ length: unread }, (_, i) => note(`n${i}`))),
      );

      const anchor = getComputedStyle(el(fixture, 'badge')!);
      const drawn = el(fixture, 'badge')!.querySelector('.mat-badge-content')!;
      const count = getComputedStyle(drawn);

      expect({
        count: drawn.textContent?.trim(),
        anchor: [anchor.position, anchor.top, anchor.right, anchor.bottom, anchor.left],
        left: count.left,
        right: count.right,
        marginRight: count.marginRight,
      }).toEqual({
        count: shown,
        anchor: ['absolute', '-1px', '-1px', '-1px', '-1px'],
        left: 'auto',
        right: '-4px',
        marginRight: '0px',
      });
    });
  }

  it('Mark all as read takes the Material badge off the bell', async () => {
    const fixture = await render(makeBridge([note('a'), note('b')]));
    const before = el(fixture, 'bell')!.querySelector('.mat-badge-content')?.textContent?.trim();

    await click(fixture, 'bell');
    await click(fixture, 'mark-all');

    expect(before).toBe('2');
    expect(el(fixture, 'bell')!.querySelector('.mat-badge-content')).toBeNull();
  });

  it('the panel shows the filters All, In progress, GitHub and Done, and Mark all as read', async () => {
    const fixture = await render(makeBridge([note('a')]));
    await click(fixture, 'bell');

    expect(
      Array.from(el(fixture, 'panel')!.querySelectorAll('[role="tab"]')).map((t) =>
        t.textContent?.trim(),
      ),
    ).toEqual(['All', 'In progress', 'GitHub', 'Done']);
    expect(el(fixture, 'mark-all')!.textContent?.trim()).toBe('Mark all as read');
    expect(el(fixture, 'panel')!.getAttribute('aria-label')).toBe('Notifications');
  });

  it('an entry shows its title, detail, time, progress bar and link', async () => {
    const fixture = await render(
      makeBridge([
        note('audit', {
          kind: 'running',
          title: 'Audit of acme/shop',
          detail: 'Area 3 of 8 done: src/orders',
          progress: 37,
          at: NOW - 12 * 60_000,
        }),
      ]),
    );
    await click(fixture, 'bell');

    expect(el(fixture, 'entry-title')!.textContent?.trim()).toBe('Audit of acme/shop');
    expect(el(fixture, 'entry-detail')!.textContent?.trim()).toBe('Area 3 of 8 done: src/orders');
    expect(el(fixture, 'entry-when')!.textContent?.trim()).toBe('12 min');
    expect(el(fixture, 'entry-progress')!.getAttribute('aria-valuenow')).toBe('37');
    expect(el(fixture, 'entry-link')).toBeNull();
  });

  it('a running test run shows "npm test in a container · running for 2 min 14 s" over an indeterminate bar that sweeps, with no value', async () => {
    const fixture = await render(
      makeBridge([
        note('tests:acme/api#41', {
          kind: 'running',
          title: 'Tests for acme/api#41',
          detail: 'npm test in a container · running for 2 min 14 s',
          indeterminate: true,
        }),
      ]),
    );
    await click(fixture, 'bell');
    const bar = el(fixture, 'entry-progress');

    expect(el(fixture, 'entry-detail')!.textContent?.trim()).toBe(
      'npm test in a container · running for 2 min 14 s',
    );
    expect(
      {
        role: bar?.getAttribute('role'),
        label: bar?.getAttribute('aria-label'),
        valuenow: bar?.getAttribute('aria-valuenow'),
        valuemin: bar?.getAttribute('aria-valuemin'),
        sweeps: bar?.querySelector('[data-test="entry-sweep"]') !== null,
      },
      'the bar is indeterminate: a progressbar with no value and a sweep inside',
    ).toEqual({
      role: 'progressbar',
      label: 'Tests for acme/api#41',
      valuenow: null,
      valuemin: null,
      sweeps: true,
    });
  });

  it('an entry that is no longer running draws no indeterminate bar, and a measured progress wins over it', async () => {
    const fixture = await render(
      makeBridge([
        note('tests:acme/api#41', { kind: 'failed', indeterminate: true }),
        note('audit:acme/shop', { kind: 'running', indeterminate: true, progress: 37 }),
      ]),
    );
    await click(fixture, 'bell');

    expect(
      all(fixture, 'entry-progress').map((bar) => bar.getAttribute('aria-valuenow')),
      'only the measured bar is drawn',
    ).toEqual(['37']);
    expect(el(fixture, 'entry-sweep')).toBeNull();
  });

  it('the sweep moves, and stops when the system asks for reduced motion', () => {
    const source = readFileSync('src/app/shell/notification-bell.component.ts', 'utf8');

    expect(source, 'the sweep animates').toMatch(/\.sweep \{[^}]*animation: sc-sweep 1\.4s/);
    expect(source).toMatch(/@keyframes sc-sweep \{\s*0% \{\s*transform: translateX\(-100%\)/);
    expect(source, 'reduced motion stops the sweep').toMatch(
      /@media \(prefers-reduced-motion: reduce\)\s*\{\s*\.sweep \{\s*animation: none;/,
    );
  });

  it('a link opens in the browser through the external-link bridge', async () => {
    const bridge = makeBridge([
      note('g', { kind: 'github', url: 'https://github.com/acme/api/pull/41' }),
    ]);
    const fixture = await render(bridge);
    await click(fixture, 'bell');

    expect(el(fixture, 'entry-link')!.textContent?.trim()).toBe('Open on GitHub');
    await click(fixture, 'entry-link');

    expect(bridge.appOpenExternal.mock.calls).toEqual([
      [{ url: 'https://github.com/acme/api/pull/41' }],
    ]);
  });

  it('a link main refuses shows why in the panel', async () => {
    const bridge = makeBridge([note('w', { url: 'https://example.com/report' })]);
    bridge.appOpenExternal.mockRejectedValueOnce(
      Object.assign(new Error('Opening example.com is not allowed.'), { code: 'x' }),
    );
    const fixture = await render(bridge);
    await click(fixture, 'bell');
    await click(fixture, 'entry-link');

    expect(el(fixture, 'error')!.textContent?.trim()).toContain('example.com');
  });

  it('Mark all as read asks main and shows the list it answers', async () => {
    const bridge = makeBridge([note('a'), note('b')]);
    const fixture = await render(bridge);
    await click(fixture, 'bell');

    await click(fixture, 'mark-all');

    expect(bridge.notificationsMarkRead.mock.calls).toEqual([[]]);
    expect(el(fixture, 'badge')).toBeNull();
    expect(all(fixture, 'entry').some((e) => e.classList.contains('unread'))).toBe(false);
  });

  it('a list main pushes replaces the shown one', async () => {
    const bridge = makeBridge([]);
    const fixture = await render(bridge);

    bridge.push([note('new', { kind: 'github' })]);
    await settle(fixture);

    expect(el(fixture, 'badge')!.textContent?.trim()).toBe('1');
  });

  it('Escape closes the panel and focus returns to the bell', async () => {
    const fixture = await render(makeBridge([note('a')]));
    await click(fixture, 'bell');
    el(fixture, 'filter-github')!.focus();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await settle(fixture);

    expect(el(fixture, 'panel')).toBeNull();
    expect(document.activeElement).toBe(el(fixture, 'bell'));
  });

  it('a click outside closes the panel without moving the focus to the bell; a click inside keeps it open', async () => {
    const fixture = await render(makeBridge([note('a')]));
    await click(fixture, 'bell');

    await click(fixture, 'filter-done');
    const openAfterInside = el(fixture, 'panel') !== null;
    const outside = document.createElement('div');
    document.body.appendChild(outside);
    outside.click();
    await settle(fixture);

    expect(openAfterInside).toBe(true);
    expect(el(fixture, 'panel')).toBeNull();
    expect(document.activeElement).not.toBe(el(fixture, 'bell'));
  });

  it('a click into a field outside the panel closes it and leaves the focus in that field', async () => {
    const fixture = await render(makeBridge([note('a')]));
    await click(fixture, 'bell');
    const field = document.createElement('input');
    document.body.appendChild(field);

    field.focus();
    field.click();
    await settle(fixture);

    expect(el(fixture, 'panel')).toBeNull();
    expect(document.activeElement).toBe(field);
  });

  it('the time beside an entry moves on while the panel stays open', async () => {
    vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
    vi.setSystemTime(NOW);
    try {
      const fixture = await render(makeBridge([note('a', { at: NOW })]));
      await click(fixture, 'bell');
      const atOpen = el(fixture, 'entry-when')!.textContent?.trim();

      vi.advanceTimersByTime(12 * 60_000);
      await settle(fixture);

      expect(atOpen).toBe('now');
      expect(el(fixture, 'entry-when')!.textContent?.trim()).toBe('12 min');
    } finally {
      vi.useRealTimers();
    }
  });

  it('opening an entry’s link marks that entry read and leaves the others unread', async () => {
    const bridge = makeBridge([
      note('g', { kind: 'github', url: 'https://github.com/acme/api/pull/41' }),
      note('other'),
    ]);
    const fixture = await render(bridge);
    await click(fixture, 'bell');

    await click(fixture, 'entry-link');

    expect(bridge.notificationsMarkRead.mock.calls).toEqual([[['g']]]);
    expect(el(fixture, 'badge')!.textContent?.trim()).toBe('1');
  });

  it('a link main refuses leaves the entry unread', async () => {
    const bridge = makeBridge([note('w', { url: 'https://example.com/report' })]);
    bridge.appOpenExternal.mockRejectedValueOnce(new Error('Opening example.com is not allowed.'));
    const fixture = await render(bridge);
    await click(fixture, 'bell');

    await click(fixture, 'entry-link');

    expect(bridge.notificationsMarkRead).not.toHaveBeenCalled();
    expect(el(fixture, 'badge')!.textContent?.trim()).toBe('1');
  });

  it('the bell toggles the panel', async () => {
    const fixture = await render(makeBridge([]));

    await click(fixture, 'bell');
    const opened = el(fixture, 'panel') !== null;
    await click(fixture, 'bell');

    expect(opened).toBe(true);
    expect(el(fixture, 'panel')).toBeNull();
  });
});

describe('NotificationBellComponent: the actions an entry offers', () => {
  const ACTIONS = [
    { id: 'whisper-download-base', label: 'Download the base model (142 MB)' },
    { id: 'open-models-transcription', label: 'Open Models, Transcription' },
  ] as const;
  const voice = (extra: Partial<AppNotification> = {}) =>
    note('voice-no-model:1', {
      kind: 'failed',
      title: 'A voice message could not be transcribed: no Whisper model is installed.',
      detail: '1 voice message not transcribed, the last from Discord.',
      actions: ACTIONS,
      ...extra,
    });

  beforeEach(() => {
    TestBed.resetTestingModule();
    document.body.innerHTML = '';
  });

  it('the bell shows each action as a button with its icon and its label, and an entry without actions shows none', async () => {
    const fixture = await render(makeBridge([voice(), note('plain')]));
    await click(fixture, 'bell');

    const [withActions, plain] = all(fixture, 'entry');
    const buttons = Array.from(withActions!.querySelectorAll('button'));
    expect(buttons.map((b) => b.textContent?.trim())).toEqual([
      'Download the base model (142 MB)',
      'Open Models, Transcription',
    ]);
    expect(buttons.every((b) => b.querySelector('svg path')?.getAttribute('d'))).toBe(true);
    expect(plain!.querySelectorAll('button')).toHaveLength(0);
  });

  it('Download the base model asks main to run that action on that entry', async () => {
    const bridge = makeBridge([voice()]);
    const fixture = await render(bridge);
    await click(fixture, 'bell');

    await click(fixture, 'entry-action-whisper-download-base');

    expect(bridge.notificationsAct.mock.calls).toEqual([
      [{ id: 'voice-no-model:1', action: 'whisper-download-base' }],
    ]);
  });

  it('Open Models, Transcription opens the Transcription tab of Models, closes the panel and asks main nothing', async () => {
    const bridge = makeBridge([voice()]);
    const fixture = await render(bridge);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    await click(fixture, 'bell');

    await click(fixture, 'entry-action-open-models-transcription');

    expect(navigate.mock.calls.map(([url]) => String(url))).toEqual(['/llm?tab=transcription']);
    expect(el(fixture, 'panel')).toBeNull();
    expect(bridge.notificationsAct).not.toHaveBeenCalled();
    expect(bridge.notificationsMarkRead.mock.calls).toEqual([[['voice-no-model:1']]]);
  });

  it('Open Approvals on a pending approval opens Approvals here, marks it read and asks main to run nothing', async () => {
    const bridge = makeBridge([
      note('approvals:pending', {
        kind: 'paused',
        title: 'An action waits for your approval',
        actions: [{ id: 'open-approvals', label: 'Open Approvals' }],
      }),
    ]);
    const fixture = await render(bridge);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    await click(fixture, 'bell');

    await click(fixture, 'entry-action-open-approvals');

    expect(navigate.mock.calls.map(([url]) => String(url))).toEqual(['/review']);
    expect(bridge.notificationsAct).not.toHaveBeenCalled();
    expect(bridge.notificationsMarkRead.mock.calls).toEqual([[['approvals:pending']]]);
    expect(el(fixture, 'badge')).toBeNull();
  });

  it('an action main runs marks the entry read; one main refuses leaves it unread', async () => {
    const accepted = makeBridge([voice()]);
    const refused = makeBridge([voice()]);
    refused.notificationsAct.mockRejectedValueOnce(new Error('Scout could not run that action.'));

    for (const bridge of [accepted, refused]) {
      TestBed.resetTestingModule();
      const fixture = await render(bridge);
      await click(fixture, 'bell');
      await click(fixture, 'entry-action-whisper-download-base');
    }

    expect(accepted.notificationsMarkRead.mock.calls).toEqual([[['voice-no-model:1']]]);
    expect(refused.notificationsMarkRead).not.toHaveBeenCalled();
  });

  it('an action main refuses shows why in the panel', async () => {
    const bridge = makeBridge([voice()]);
    bridge.notificationsAct.mockRejectedValueOnce(
      new Error('[scout:notifications/invalid-request] Scout could not run that action.'),
    );
    const fixture = await render(bridge);
    await click(fixture, 'bell');

    await click(fixture, 'entry-action-whisper-download-base');

    expect(el(fixture, 'error')!.textContent?.trim()).toBe('Scout could not run that action.');
  });

  it('a download main pushes as running shows its progress beside the actions, and the done entry drops them', async () => {
    const bridge = makeBridge([voice()]);
    const fixture = await render(bridge);
    await click(fixture, 'bell');

    bridge.push([voice({ kind: 'running', detail: 'Downloading the base model.', progress: 40 })]);
    await settle(fixture);
    const running = {
      progress: el(fixture, 'entry-progress')?.getAttribute('aria-valuenow'),
      actions: all(fixture, 'entry').flatMap((e) => Array.from(e.querySelectorAll('.action'))),
    };
    bridge.push([
      note('voice-no-model:1', { kind: 'done', title: 'A Whisper model is installed.' }),
    ]);
    await settle(fixture);

    expect(running.progress).toBe('40');
    expect(running.actions).toHaveLength(2);
    expect(el(fixture, 'entry-action-whisper-download-base')).toBeNull();
    expect(el(fixture, 'entry-title')!.textContent?.trim()).toBe('A Whisper model is installed.');
  });
});

describe('NotificationBellComponent: a mention of the Scout bot', () => {
  const mention = (extra: Partial<AppNotification> = {}) =>
    note('mention:issue-comment:9001', {
      kind: 'mention',
      title: '@alice mentioned @scoutbot on acme/shop#20: @ScoutBot review',
      detail: 'Pull request 20',
      url: 'https://github.com/acme/shop/pull/20#issuecomment-9001',
      actions: [{ id: 'review-pull-request', label: 'Review it' }],
      source: 'github',
      ...extra,
    });

  beforeEach(() => {
    TestBed.resetTestingModule();
    document.body.innerHTML = '';
  });

  it('shows who mentioned the bot where, unread, with Open on GitHub to the comment and Review it, under GitHub', async () => {
    const bridge = makeBridge([mention()]);
    const fixture = await render(bridge);
    await click(fixture, 'bell');
    await click(fixture, 'filter-github');

    const entry = all(fixture, 'entry')[0]!;
    expect(entry.classList.contains('unread')).toBe(true);
    expect(el(fixture, 'entry-title')!.textContent?.trim()).toBe(
      '@alice mentioned @scoutbot on acme/shop#20: @ScoutBot review',
    );
    expect(el(fixture, 'entry-link')!.textContent?.trim()).toBe('Open on GitHub');
    expect(el(fixture, 'entry-action-review-pull-request')!.textContent?.trim()).toBe('Review it');

    await click(fixture, 'entry-link');

    expect(bridge.appOpenExternal.mock.calls).toEqual([
      [{ url: 'https://github.com/acme/shop/pull/20#issuecomment-9001' }],
    ]);
    expect(bridge.notificationsMarkRead.mock.calls).toEqual([[['mention:issue-comment:9001']]]);
  });

  it('Review it asks Scout in the chat to review that pull request, opens the chat, marks the entry read and asks main to run nothing', async () => {
    const bridge = makeBridge([mention()]);
    const fixture = await render(bridge);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    await click(fixture, 'bell');

    await click(fixture, 'entry-action-review-pull-request');

    expect(bridge.localAsk.mock.calls.map(([req]) => req.text)).toEqual([
      'Review the pull request https://github.com/acme/shop/pull/20',
    ]);
    expect(navigate.mock.calls.map(([url]) => String(url))).toEqual(['/chat']);
    expect(el(fixture, 'panel')).toBeNull();
    expect(bridge.notificationsAct).not.toHaveBeenCalled();
    expect(bridge.notificationsMarkRead.mock.calls).toEqual([[['mention:issue-comment:9001']]]);
  });

  it('a question the chat refuses shows why in the panel, stays on this screen and leaves the entry unread', async () => {
    const bridge = makeBridge([mention()]);
    bridge.localAsk.mockRejectedValueOnce(
      new Error('[scout:local/busy] Scout is answering another question.'),
    );
    const fixture = await render(bridge);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    await click(fixture, 'bell');

    await click(fixture, 'entry-action-review-pull-request');

    expect(el(fixture, 'error')!.textContent?.trim()).toBe('Scout is answering another question.');
    expect(navigate).not.toHaveBeenCalled();
    expect(bridge.notificationsMarkRead).not.toHaveBeenCalled();
  });

  it('Review it on an entry without a pull request link asks nothing and says why', async () => {
    const { url: _url, ...withoutLink } = mention();
    const bridge = makeBridge([withoutLink]);
    const fixture = await render(bridge);
    await click(fixture, 'bell');

    await click(fixture, 'entry-action-review-pull-request');

    expect(bridge.localAsk).not.toHaveBeenCalled();
    expect(el(fixture, 'error')!.textContent?.trim()).toBe(
      'Scout could not tell which pull request this entry is about.',
    );
  });
});

describe('timeAgo', () => {
  it('now, minutes, hours and days', () => {
    expect(
      [0, 59_000, 5 * 60_000, 2 * 3_600_000, 3 * 86_400_000].map((d) => timeAgo(NOW - d, NOW)),
    ).toEqual(['now', 'now', '5 min', '2 h', '3 d']);
  });
});

const RUNNING_AUDIT = note('audit:acme/shop', {
  kind: 'running',
  title: 'Audit of acme/shop',
  detail: 'Area 3 of 8 done: src/orders',
  progress: 37,
});
const PAUSED_AUDIT = note('audit:acme/shop', {
  kind: 'paused',
  title: 'Audit of acme/shop paused',
  detail: 'Paused by you · area 3 of 8 done',
});
const RUNNING_TESTS = note('tests:acme/api#41', {
  kind: 'running',
  title: 'Tests for acme/api#41',
  detail: 'Running in a container on this computer.',
});

function controlsIn(entry: HTMLElement): string[] {
  return Array.from(entry.querySelectorAll('[data-test^="entry-control-"]')).map(
    (b) => b.getAttribute('aria-label') ?? '',
  );
}

describe('NotificationBellComponent: Pause, Continue and Stop on a running job', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    document.body.innerHTML = '';
  });

  const OFFERED: readonly [string, AppNotification, readonly string[]][] = [
    [
      'a running audit offers Pause and Stop',
      RUNNING_AUDIT,
      ['Pause the audit of acme/shop', 'Stop the audit of acme/shop'],
    ],
    [
      'a paused audit offers Continue and Stop',
      PAUSED_AUDIT,
      ['Continue the audit of acme/shop', 'Stop the audit of acme/shop'],
    ],
    ['a running test run offers Stop', RUNNING_TESTS, ['Stop the tests for acme/api#41']],
    [
      'a done audit offers nothing',
      note('audit:acme/shop', { kind: 'done', title: 'Audit of acme/shop is done' }),
      [],
    ],
    [
      'a stopped test run offers nothing',
      note('tests:acme/api#41', { kind: 'done', title: 'Tests for acme/api#41 stopped' }),
      [],
    ],
    [
      'a running notice that is not a job offers nothing',
      note('worker:1', { kind: 'running', title: 'Researching rust async...' }),
      [],
    ],
    [
      'a paused approval offers nothing',
      note('approvals:pending', { kind: 'paused', title: 'An action waits for your approval' }),
      [],
    ],
  ];
  for (const [name, entry, labels] of OFFERED) {
    it(`${name}, as icon buttons at the right of the title, before the time, each named for its job`, async () => {
      const fixture = await render(makeBridge([entry]));
      await click(fixture, 'bell');

      const shown = all(fixture, 'entry')[0]!;
      const line = shown.querySelector('.line')!;
      const order = Array.from(line.children).map((child) =>
        child.classList.contains('controls')
          ? `controls ${controlsIn(child as HTMLElement).length}`
          : (child.getAttribute('data-test') ?? ''),
      );

      expect(controlsIn(shown)).toEqual(labels);
      expect(order).toEqual(
        labels.length === 0
          ? ['entry-title', 'entry-when']
          : ['entry-title', `controls ${labels.length}`, 'entry-when'],
      );
      for (const button of Array.from(
        shown.querySelectorAll<HTMLButtonElement>('[data-test^="entry-control-"]'),
      )) {
        expect([
          button.tagName,
          button.type,
          button.title,
          button.disabled,
          button.tabIndex,
          button.getAttribute('aria-disabled'),
          button.querySelector('svg')?.getAttribute('aria-hidden'),
          button.textContent?.trim(),
        ]).toEqual([
          'BUTTON',
          'button',
          button.getAttribute('aria-label'),
          false,
          0,
          null,
          'true',
          '',
        ]);
      }
    });
  }

  const CLICKS: readonly [string, AppNotification, string, string][] = [
    ['Pause on a running audit', RUNNING_AUDIT, 'pause', 'Pause the audit of acme/shop'],
    ['Stop on a running audit', RUNNING_AUDIT, 'stop', 'Stop the audit of acme/shop'],
    ['Continue on a paused audit', PAUSED_AUDIT, 'continue', 'Continue the audit of acme/shop'],
    ['Stop on a paused audit', PAUSED_AUDIT, 'stop', 'Stop the audit of acme/shop'],
    ['Stop on a running test run', RUNNING_TESTS, 'stop', 'Stop the tests for acme/api#41'],
  ];
  for (const [name, entry, action] of CLICKS) {
    it(`${name} asks main with the entry and the action alone, once`, async () => {
      const bridge = makeBridge([entry]);
      const fixture = await render(bridge);
      await click(fixture, 'bell');

      await click(fixture, `entry-control-${action}`);

      expect(bridge.notificationsControl.mock.calls).toEqual([[{ id: entry.id, action }]]);
      expect(el(fixture, 'panel')).not.toBeNull();
    });
  }

  for (const [name, entry, action] of CLICKS) {
    it(`${name}: while main asks, the controls stay and are inactive, focus stays on the one pressed, and a second press sends nothing`, async () => {
      const bridge = makeBridge([entry]);
      const fixture = await render(bridge);
      await click(fixture, 'bell');
      const pressed = el(fixture, `entry-control-${action}`)!;
      pressed.focus();
      await click(fixture, `entry-control-${action}`);

      bridge.push([
        { ...entry, read: true, control: { action, phase: 'asking' } as JobControlState },
      ]);
      await settle(fixture);
      await click(fixture, `entry-control-${action}`);

      const buttons = all(fixture, 'entry')[0]!.querySelectorAll('[data-test^="entry-control-"]');
      expect(Array.from(buttons).map((b) => b.getAttribute('aria-disabled'))).toEqual(
        Array.from(buttons).map(() => 'true'),
      );
      expect(document.activeElement?.getAttribute('data-test')).toBe(`entry-control-${action}`);
      expect(bridge.notificationsControl).toHaveBeenCalledTimes(1);
      expect(el(fixture, 'entry-status')).toBeNull();
    });
  }

  const WAITING: readonly [string, AppNotification, JobControlState['action'], string][] = [
    ['a pause queued on a running audit', RUNNING_AUDIT, 'pause', 'Pausing after this area'],
    ['a stop queued on a running audit', RUNNING_AUDIT, 'stop', 'Stopping after this area'],
    ['a continue queued on a paused audit', PAUSED_AUDIT, 'continue', 'Continuing'],
    [
      'a stop queued on a paused audit, where no area is in progress',
      PAUSED_AUDIT,
      'stop',
      'Stopping',
    ],
    ['a stop queued on a running test run', RUNNING_TESTS, 'stop', 'Stopping'],
  ];
  for (const [name, entry, action, line] of WAITING) {
    it(`${name} replaces the controls and the time with "${line}", keeps the kind, the detail and the progress bar, and moves focus to that line`, async () => {
      const bridge = makeBridge([entry]);
      const fixture = await render(bridge);
      await click(fixture, 'bell');
      el(fixture, `entry-control-${action}`)!.focus();
      await click(fixture, `entry-control-${action}`);
      bridge.push([{ ...entry, read: true, control: { action, phase: 'asking' } }]);
      await settle(fixture);

      bridge.push([{ ...entry, read: true, control: { action, phase: 'waiting' } }]);
      await settle(fixture);

      const shown = all(fixture, 'entry')[0]!;
      const status = el(fixture, 'entry-status')!;
      expect(controlsIn(shown)).toEqual([]);
      expect(el(fixture, 'entry-when')).toBeNull();
      expect([status.textContent?.trim(), status.getAttribute('role')]).toEqual([line, 'status']);
      expect(shown.getAttribute('data-kind')).toBe(entry.kind);
      expect(el(fixture, 'entry-detail')!.textContent?.trim()).toBe(entry.detail);
      expect(el(fixture, 'entry-progress')?.getAttribute('aria-valuenow') ?? null).toBe(
        entry.progress === undefined ? null : String(entry.progress),
      );
      expect(document.activeElement).toBe(status);
    });
  }

  it('the line a queued click shows does not take the focus the owner moved elsewhere', async () => {
    const bridge = makeBridge([RUNNING_AUDIT, note('other')]);
    const fixture = await render(bridge);
    await click(fixture, 'bell');
    await click(fixture, 'entry-control-stop');
    el(fixture, 'mark-all')!.focus();

    bridge.push([
      { ...RUNNING_AUDIT, control: { action: 'stop', phase: 'waiting' } },
      note('other'),
    ]);
    await settle(fixture);

    expect(el(fixture, 'entry-status')!.textContent?.trim()).toBe('Stopping after this area');
    expect(document.activeElement?.getAttribute('data-test')).toBe('mark-all');
  });

  const NOTES: readonly [string, AppNotification, string, readonly string[]][] = [
    [
      'a refusal',
      RUNNING_AUDIT,
      'The repository must be given as owner and repo, each made of letters, digits, dots, hyphens or underscores.',
      ['Pause the audit of acme/shop', 'Stop the audit of acme/shop'],
    ],
    [
      'no answer',
      RUNNING_TESTS,
      'The Scout control plane did not answer, so this click may not have reached Scout. Try again.',
      ['Stop the tests for acme/api#41'],
    ],
    [
      'a click not confirmed',
      PAUSED_AUDIT,
      'Not confirmed: Scout did not say within two and a half minutes whether it applied this click; look at the job before clicking again.',
      ['Continue the audit of acme/shop', 'Stop the audit of acme/shop'],
    ],
  ];
  for (const [name, entry, sentence, labels] of NOTES) {
    it(`${name} shows main's sentence on the entry, said once to a screen reader, with the controls back and the entry as it was`, async () => {
      const fixture = await render(makeBridge([{ ...entry, note: sentence }]));
      await click(fixture, 'bell');

      const shown = all(fixture, 'entry')[0]!;
      const said = el(fixture, 'entry-note')!;
      expect([said.textContent?.trim(), said.getAttribute('role')]).toEqual([sentence, 'status']);
      expect(shown.querySelectorAll('[role="status"]')).toHaveLength(1);
      expect(controlsIn(shown)).toEqual(labels);
      expect([
        shown.getAttribute('data-kind'),
        el(fixture, 'entry-title')!.textContent?.trim(),
      ]).toEqual([entry.kind, entry.title]);
    });
  }

  it('a control main refuses shows its reason in the panel', async () => {
    const bridge = makeBridge([RUNNING_AUDIT]);
    bridge.notificationsControl.mockRejectedValueOnce(
      new Error('Scout could not send that control.'),
    );
    const fixture = await render(bridge);
    await click(fixture, 'bell');

    await click(fixture, 'entry-control-pause');

    expect(el(fixture, 'error')!.textContent?.trim()).toBe('Scout could not send that control.');
  });

  it('In progress lists the running audit, the paused audit and the running test run, each with its controls', async () => {
    const fixture = await render(
      makeBridge([
        RUNNING_TESTS,
        { ...PAUSED_AUDIT, id: 'audit:acme/web', title: 'Audit of acme/web paused' },
        RUNNING_AUDIT,
        note('audit:acme/old', { title: 'Audit of acme/old stopped' }),
      ]),
    );
    await click(fixture, 'bell');

    await click(fixture, 'filter-running');

    expect(all(fixture, 'entry').map((e) => controlsIn(e))).toEqual([
      ['Stop the tests for acme/api#41'],
      ['Continue the audit of acme/web', 'Stop the audit of acme/web'],
      ['Pause the audit of acme/shop', 'Stop the audit of acme/shop'],
    ]);
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
    if (found === undefined) continue;
    if (
      selector!.split(',').some((one) => {
        try {
          return node.matches(one.trim());
        } catch {
          return false;
        }
      })
    )
      value = found;
  }
  return value;
}

const CLOCK = 'M12 3a9 9 0 1 0 9 9M12 7v5l3 2';
const PAUSE_BARS = 'M9 5v14M15 5v14';
const BUBBLE = 'M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z';
const PAUSED_BY_THE_AI_SOURCE = note('audit:acme/web', {
  kind: 'paused',
  title: 'Audit of acme/web paused',
  detail:
    'The AI Source reached its usage limit. Ask Scout to continue when it has capacity again.',
});

interface Drawn {
  readonly glyph: string;
  readonly path: string | null;
  readonly tone: string | null;
  readonly title: string;
  readonly detail: string | null;
  readonly when: string | null;
  readonly bar: string | null;
  readonly controls: readonly string[];
  readonly inactive: boolean;
  readonly status: string | null;
  readonly note: string | null;
  readonly link: string | null;
}

function drawn(entry: HTMLElement): Drawn {
  const icon = entry.querySelector('.icon')!;
  const text = (test: string): string | null =>
    entry.querySelector(`[data-test="${test}"]`)?.textContent?.trim() ?? null;
  const buttons = Array.from(entry.querySelectorAll('[data-test^="entry-control-"]'));
  return {
    glyph: icon.textContent?.trim() ?? '',
    path: icon.querySelector('path')?.getAttribute('d') ?? null,
    tone: icon.getAttribute('data-tone'),
    title: text('entry-title') ?? '',
    detail: text('entry-detail'),
    when: text('entry-when'),
    bar: entry.querySelector('[data-test="entry-progress"]')?.getAttribute('aria-valuenow') ?? null,
    controls: buttons.map((b) => b.getAttribute('aria-label') ?? ''),
    inactive:
      buttons.length > 0 && buttons.every((b) => b.getAttribute('aria-disabled') === 'true'),
    status: text('entry-status'),
    note: text('entry-note'),
    link: text('entry-link'),
  };
}

const NONE: Pick<Drawn, 'path' | 'bar' | 'controls' | 'inactive' | 'status' | 'note' | 'link'> = {
  path: null,
  bar: null,
  controls: [],
  inactive: false,
  status: null,
  note: null,
  link: null,
};

const AUDIT_CONTROLS = ['Pause the audit of acme/shop', 'Stop the audit of acme/shop'];
const PAUSED_CONTROLS = ['Continue the audit of acme/shop', 'Stop the audit of acme/shop'];

const STATES: readonly [string, AppNotification, Drawn][] = [
  [
    'artboard: a running audit',
    { ...RUNNING_AUDIT, at: NOW - 3 * 60_000 },
    {
      ...NONE,
      glyph: 'A',
      tone: 'accent',
      title: 'Audit of acme/shop',
      detail: 'Area 3 of 8 done: src/orders',
      when: '3 min',
      bar: '37',
      controls: AUDIT_CONTROLS,
    },
  ],
  [
    'artboard: a running test run',
    RUNNING_TESTS,
    {
      ...NONE,
      glyph: 'T',
      tone: 'accent',
      title: 'Tests for acme/api#41',
      detail: 'Running in a container on this computer.',
      when: 'now',
      controls: ['Stop the tests for acme/api#41'],
    },
  ],
  [
    'artboard: a running audit stopping after this area',
    { ...RUNNING_AUDIT, control: { action: 'stop', phase: 'waiting' } },
    {
      ...NONE,
      glyph: 'A',
      tone: 'accent',
      title: 'Audit of acme/shop',
      detail: 'Area 3 of 8 done: src/orders',
      when: null,
      bar: '37',
      status: 'Stopping after this area',
    },
  ],
  [
    'artboard: an audit paused by you',
    PAUSED_AUDIT,
    {
      ...NONE,
      glyph: 'A',
      tone: 'warn',
      title: 'Audit of acme/shop paused',
      detail: 'Paused by you · area 3 of 8 done',
      when: 'now',
      controls: PAUSED_CONTROLS,
    },
  ],
  [
    'artboard: an audit paused by the AI Source',
    PAUSED_BY_THE_AI_SOURCE,
    {
      ...NONE,
      glyph: '!',
      tone: 'err',
      title: 'Audit of acme/web paused',
      detail:
        'The AI Source reached its usage limit. Ask Scout to continue when it has capacity again.',
      when: 'now',
      controls: ['Continue the audit of acme/web', 'Stop the audit of acme/web'],
    },
  ],
  [
    'artboard: an audit you stopped',
    note('audit:acme/shop', {
      title: 'Audit of acme/shop stopped',
      detail: '3 of 8 areas · 4 findings kept',
    }),
    {
      ...NONE,
      glyph: 'A',
      tone: 'muted',
      title: 'Audit of acme/shop stopped',
      detail: '3 of 8 areas · 4 findings kept',
      when: 'now',
    },
  ],
  [
    'artboard: a test run you stopped',
    note('tests:acme/api#41', {
      title: 'Tests for acme/api#41 stopped',
      detail: 'The tests gave no result.',
    }),
    {
      ...NONE,
      glyph: 'T',
      tone: 'muted',
      title: 'Tests for acme/api#41 stopped',
      detail: 'The tests gave no result.',
      when: 'now',
    },
  ],
  [
    'artboard: a mention of the Scout bot',
    note('mention:issue-comment:9001', {
      kind: 'mention',
      title: '@alice mentioned @scoutbot on acme/shop#20: @ScoutBot review',
      detail: 'Pull request 20',
      url: 'https://github.com/acme/shop/pull/20#issuecomment-9001',
      source: 'github',
    }),
    {
      ...NONE,
      glyph: '@',
      tone: 'warn',
      title: '@alice mentioned @scoutbot on acme/shop#20: @ScoutBot review',
      detail: 'Pull request 20',
      when: 'now',
      link: 'Open on GitHub',
    },
  ],
  [
    'artboard: a GitHub thread you were mentioned in',
    note('github:1', {
      kind: 'github',
      title: 'Retry logic for the order sync',
      detail: 'You were mentioned in acme/api#41',
      url: 'https://github.com/acme/api/pull/41',
      source: 'github',
    }),
    {
      ...NONE,
      glyph: '@',
      tone: 'warn',
      title: 'Retry logic for the order sync',
      detail: 'You were mentioned in acme/api#41',
      when: 'now',
      link: 'Open on GitHub',
    },
  ],
  [
    'artboard: new activity on your pull request',
    note('github:2', {
      kind: 'github',
      title: 'Apply discounts before the sum',
      detail: 'New activity on your pull request acme/shop#5',
      url: 'https://github.com/acme/shop/pull/5',
      source: 'github',
    }),
    {
      ...NONE,
      glyph: 'C',
      tone: 'warn',
      title: 'Apply discounts before the sum',
      detail: 'New activity on your pull request acme/shop#5',
      when: 'now',
      link: 'Open on GitHub',
    },
  ],
  [
    'artboard: a verdict given',
    note('verdict:act-1', {
      title: 'Verdict given: Approved acme/web#17',
      detail: '',
      url: 'https://github.com/acme/web/pull/17#pullrequestreview-1',
      source: 'verdict',
    }),
    {
      ...NONE,
      glyph: '✓',
      tone: 'ok',
      title: 'Verdict given: Approved acme/web#17',
      detail: null,
      when: 'now',
      link: 'See the review',
    },
  ],
  [
    'artboard: a voice message not transcribed',
    note('voice-no-model:1', {
      kind: 'failed',
      title: 'A voice message could not be transcribed: no Whisper model is installed.',
      detail: '1 voice message not transcribed, the last from Discord.',
    }),
    {
      ...NONE,
      glyph: '!',
      tone: 'err',
      title: 'A voice message could not be transcribed: no Whisper model is installed.',
      detail: '1 voice message not transcribed, the last from Discord.',
      when: 'now',
    },
  ],
  [
    'a notice whose title ends in stopped is not a job: a tick on the ok pair, never the stopped tone',
    note('worker:1', { title: 'Sync stopped', detail: '' }),
    {
      ...NONE,
      glyph: '✓',
      tone: 'ok',
      title: 'Sync stopped',
      detail: null,
      when: 'now',
    },
  ],
  [
    'app only: the asking step, the controls inactive',
    { ...RUNNING_AUDIT, read: true, control: { action: 'pause', phase: 'asking' } },
    {
      ...NONE,
      glyph: 'A',
      tone: 'accent',
      title: 'Audit of acme/shop',
      detail: 'Area 3 of 8 done: src/orders',
      when: 'now',
      bar: '37',
      controls: AUDIT_CONTROLS,
      inactive: true,
    },
  ],
  [
    'app only: pausing after this area',
    { ...RUNNING_AUDIT, control: { action: 'pause', phase: 'waiting' } },
    {
      ...NONE,
      glyph: 'A',
      tone: 'accent',
      title: 'Audit of acme/shop',
      detail: 'Area 3 of 8 done: src/orders',
      when: null,
      bar: '37',
      status: 'Pausing after this area',
    },
  ],
  [
    'app only: continuing',
    { ...PAUSED_AUDIT, control: { action: 'continue', phase: 'waiting' } },
    {
      ...NONE,
      glyph: 'A',
      tone: 'warn',
      title: 'Audit of acme/shop paused',
      detail: 'Paused by you · area 3 of 8 done',
      when: null,
      status: 'Continuing',
    },
  ],
  [
    'app only: a paused audit stopping, no area in progress',
    { ...PAUSED_AUDIT, control: { action: 'stop', phase: 'waiting' } },
    {
      ...NONE,
      glyph: 'A',
      tone: 'warn',
      title: 'Audit of acme/shop paused',
      detail: 'Paused by you · area 3 of 8 done',
      when: null,
      status: 'Stopping',
    },
  ],
  [
    'app only: a test run stopping',
    { ...RUNNING_TESTS, control: { action: 'stop', phase: 'waiting' } },
    {
      ...NONE,
      glyph: 'T',
      tone: 'accent',
      title: 'Tests for acme/api#41',
      detail: 'Running in a container on this computer.',
      when: null,
      status: 'Stopping',
    },
  ],
  [
    'app only: a note on the entry',
    {
      ...RUNNING_AUDIT,
      note: 'Not confirmed: Scout did not say within two and a half minutes whether it applied this click; look at the job before clicking again.',
    },
    {
      ...NONE,
      glyph: 'A',
      tone: 'accent',
      title: 'Audit of acme/shop',
      detail: 'Area 3 of 8 done: src/orders',
      when: 'now',
      bar: '37',
      controls: AUDIT_CONTROLS,
      note: 'Not confirmed: Scout did not say within two and a half minutes whether it applied this click; look at the job before clicking again.',
    },
  ],
  [
    'app only: a finished audit with its verdict',
    note('audit:acme/shop', {
      title: 'Audit of acme/shop is done',
      detail: 'Verdict: fix (0 High, 2 Medium, 1 Low).',
    }),
    {
      ...NONE,
      glyph: 'A',
      tone: 'ok',
      title: 'Audit of acme/shop is done',
      detail: 'Verdict: fix (0 High, 2 Medium, 1 Low).',
      when: 'now',
    },
  ],
  [
    'app only: tests finished',
    note('tests:acme/api#41', {
      title: 'Tests for acme/api#41 finished',
      detail: 'The result is in the conversation.',
    }),
    {
      ...NONE,
      glyph: 'T',
      tone: 'ok',
      title: 'Tests for acme/api#41 finished',
      detail: 'The result is in the conversation.',
      when: 'now',
    },
  ],
  [
    'app only: tests that did not finish, the failed kind',
    note('tests:acme/api#41', {
      kind: 'failed',
      title: 'Tests for acme/api#41 did not finish',
      detail: 'The answer ended before the tests reported a result.',
    }),
    {
      ...NONE,
      glyph: 'T',
      tone: 'err',
      title: 'Tests for acme/api#41 did not finish',
      detail: 'The answer ended before the tests reported a result.',
      when: 'now',
    },
  ],
  [
    'app only: Scout restarting after a crash',
    note('worker:health', {
      kind: 'running',
      title: 'Scout stopped unexpectedly and is restarting.',
      detail: 'Restart 1 since Scout was started.',
    }),
    {
      ...NONE,
      glyph: '',
      path: CLOCK,
      tone: 'accent',
      title: 'Scout stopped unexpectedly and is restarting.',
      detail: 'Restart 1 since Scout was started.',
      when: 'now',
    },
  ],
  [
    'app only: Scout stopped and not restarting',
    note('worker:health', {
      kind: 'failed',
      title: 'Scout stopped and does not restart by itself.',
      detail: 'Start it again from Logs or from Settings, Scout worker.',
    }),
    {
      ...NONE,
      glyph: '!',
      tone: 'err',
      title: 'Scout stopped and does not restart by itself.',
      detail: 'Start it again from Logs or from Settings, Scout worker.',
      when: 'now',
    },
  ],
  [
    'app only: a review requested on GitHub keeps its own icon',
    note('github:3', {
      kind: 'github',
      title: 'Retry logic for the order sync',
      detail: 'Review requested on acme/api#41',
      source: 'github',
    }),
    {
      ...NONE,
      glyph: '',
      path: BUBBLE,
      tone: 'warn',
      title: 'Retry logic for the order sync',
      detail: 'Review requested on acme/api#41',
      when: 'now',
    },
  ],
  [
    'app only: an action waiting for approval keeps its own icon',
    note('approvals:pending', {
      kind: 'paused',
      title: 'An action waits for your approval',
      detail: '',
    }),
    {
      ...NONE,
      glyph: '',
      path: PAUSE_BARS,
      tone: 'warn',
      title: 'An action waits for your approval',
      detail: null,
      when: 'now',
    },
  ],
];

describe('NotificationBellComponent: each entry as Main.dc.html draws it, and each state only the app has', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    document.body.innerHTML = '';
  });

  for (const [name, entry, expected] of STATES) {
    it(name, async () => {
      const fixture = await render(makeBridge([{ ...entry, at: Date.now() - (NOW - entry.at) }]));
      await click(fixture, 'bell');

      expect(drawn(all(fixture, 'entry')[0]!)).toEqual(expected);
    });
  }

  it('the control names the job in the case its title has, not its key', async () => {
    const fixture = await render(
      makeBridge([
        note('tests:acme/api#41', {
          kind: 'running',
          title: 'Tests for Acme/API#41',
          detail: 'Running in a container on this computer.',
        }),
      ]),
    );
    await click(fixture, 'bell');

    expect(controlsIn(all(fixture, 'entry')[0]!)).toEqual(['Stop the tests for Acme/API#41']);
  });

  const TONE_TOKENS: readonly [string, AppNotification, string, string][] = [
    ['accent', RUNNING_AUDIT, 'var(--sc-surface-2)', 'var(--sc-accent)'],
    [
      'ok',
      note('audit:acme/shop', { title: 'Audit of acme/shop is done' }),
      'var(--sc-ok-container)',
      'var(--sc-on-ok-container)',
    ],
    ['warn', PAUSED_AUDIT, 'var(--sc-warn-container)', 'var(--sc-on-warn-container)'],
    ['err', PAUSED_BY_THE_AI_SOURCE, 'var(--sc-error-container)', 'var(--sc-on-error-container)'],
    [
      'muted',
      note('audit:acme/shop', { title: 'Audit of acme/shop stopped' }),
      'var(--sc-surface-2)',
      'var(--sc-fg-2)',
    ],
  ];
  for (const [tone, entry, background, colour] of TONE_TOKENS) {
    it(`the ${tone} icon is the theme's ${background} under ${colour}, a 13 px bold glyph in a 32 px circle, in either theme`, async () => {
      const fixture = await render(makeBridge([entry]));
      await click(fixture, 'bell');

      const icon = all(fixture, 'entry')[0]!.querySelector('.icon')!;
      expect({
        tone: icon.getAttribute('data-tone'),
        background: declared(icon, 'background'),
        colour: declared(icon, 'color'),
        size: [declared(icon, 'width'), declared(icon, 'height'), declared(icon, 'border-radius')],
        glyph: [declared(icon, 'font-size'), declared(icon, 'font-weight')],
      }).toEqual({
        tone,
        background,
        colour,
        size: ['32px', '32px', '16px'],
        glyph: ['13px', '700'],
      });
    });
  }

  it("the panel is drawn as the artboard draws it: 4 px past the bell's right edge, the theme's shadow, a 14 px title, the unread row on the theme's unread colour, the error line at 13 px with 8 and 16 px padding", async () => {
    const bridge = makeBridge([note('w', { url: 'https://example.com/report' })]);
    bridge.appOpenExternal.mockRejectedValueOnce(new Error('Opening example.com is not allowed.'));
    const fixture = await render(bridge);
    await click(fixture, 'bell');
    await click(fixture, 'entry-link');

    const panel = el(fixture, 'panel')!;
    const entry = all(fixture, 'entry')[0]!;
    const error = el(fixture, 'error')!;
    expect({
      position: [declared(panel, 'top'), declared(panel, 'right')],
      shadow: declared(panel, 'box-shadow'),
      title: declared(el(fixture, 'entry-title')!, 'font-size'),
      unread: declared(entry, 'background'),
      error: [declared(error, 'font-size'), declared(error, 'padding')],
    }).toEqual({
      position: ['48px', '-4px'],
      shadow: '0 16px 40px var(--sc-shadow)',
      title: '14px',
      unread: 'var(--sc-unread)',
      error: ['13px', '8px 16px'],
    });
  });
});
