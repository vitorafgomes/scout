import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { InteractivityChecker } from '@angular/cdk/a11y';
import { formatDate } from '@angular/common';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalDeleteMeetingsRequest, LocalMeetingSummary } from '@scout/shared';

import { LAID_OUT } from '../core/laid-out.testing';
import { ScoutBridgeService } from '../core/scout-bridge';

import { MeetingsComponent } from './meetings.component';

const COMPONENT_FILE = './meetings.component.ts';

const OLDER: LocalMeetingSummary = {
  id: 'AAAAAAAAAAAAAAAAAAAAAA',
  startedAt: '2026-11-03T09:00:00.000+00:00',
  endedAt: '2026-11-03T09:00:40.000+00:00',
  title: 'Standup',
  lineCount: 1,
  turnCount: 0,
};
const NEWER: LocalMeetingSummary = {
  id: 'BBBBBBBBBBBBBBBBBBBBBB',
  startedAt: '2026-11-04T10:00:00.000+00:00',
  endedAt: '2026-11-04T10:25:30.000+00:00',
  title: 'Release sync',
  lineCount: 42,
  turnCount: 3,
};

function makeBridge(lists: readonly (readonly LocalMeetingSummary[])[]) {
  const pending = [...lists];
  return {
    localMeetings: vi.fn(async (_req: { limit?: number }) => ({
      items: pending.length > 1 ? pending.shift()! : (pending[0] ?? []),
    })),
    localExportMeeting: vi.fn(async (_req: { meetingId: string }) => ({ saved: true })),
    localDeleteMeetings: vi.fn(async (_req: LocalDeleteMeetingsRequest) => ({ deleted: 1 })),
    onLocalMeetingsChanged: vi.fn((_listener: () => void) => () => undefined),
  };
}

async function render(
  bridge: ReturnType<typeof makeBridge>,
): Promise<ComponentFixture<MeetingsComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
    ],
  });
  const fixture = TestBed.createComponent(MeetingsComponent);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<MeetingsComponent>): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

async function answerConfirm(
  fixture: ComponentFixture<MeetingsComponent>,
  answer: 'confirm-ok' | 'confirm-cancel',
): Promise<void> {
  const button = document.querySelector<HTMLButtonElement>(`[data-test="${answer}"]`);
  if (button === null) throw new Error('the confirmation dialog did not open');
  button.click();
  for (let i = 0; i < 3; i += 1) {
    await TestBed.inject(ApplicationRef).whenStable();
    await settle(fixture);
  }
}

function dialogTitle(): string | null {
  return document.querySelector('sc-confirm-dialog h2')?.textContent?.trim() ?? null;
}

function el(fixture: ComponentFixture<MeetingsComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function within(row: HTMLElement | null, test: string): HTMLElement | null {
  return row?.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function titles(fixture: ComponentFixture<MeetingsComponent>): string[] {
  return Array.from(
    fixture.nativeElement.querySelectorAll(
      '[data-test="meeting-title"]',
    ) as NodeListOf<HTMLElement>,
  ).map((node) => (node.textContent ?? '').trim());
}

describe('Meetings', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists kept meetings newest first and offers Export and Delete', async () => {
    const bridge = makeBridge([[OLDER, NEWER]]);
    bridge.localExportMeeting
      .mockResolvedValueOnce({ saved: true })
      .mockResolvedValueOnce({ saved: false });
    const fixture = await render(bridge);
    const newer = el(fixture, `meeting-${NEWER.id}`);
    const older = el(fixture, `meeting-${OLDER.id}`);
    const shown = titles(fixture);
    const meta = within(newer, 'meeting-meta')?.textContent?.replace(/\s+/g, ' ').trim();
    const shortMeta = within(older, 'meeting-meta')?.textContent?.replace(/\s+/g, ' ').trim();

    within(newer, 'export')!.click();
    await settle(fixture);
    within(older, 'export')!.click();
    await settle(fixture);

    expect(bridge.localMeetings).toHaveBeenCalledWith({ limit: 100 });
    expect(el(fixture, 'meetings-list')).not.toBeNull();
    expect(shown).toEqual(['Release sync', 'Standup']);
    expect(meta).toBe(
      `${formatDate(NEWER.startedAt, 'yyyy-MM-dd HH:mm', 'en-US')} · 25 min · 42 lines · 3 questions`,
    );
    expect(shortMeta).toBe(
      `${formatDate(OLDER.startedAt, 'yyyy-MM-dd HH:mm', 'en-US')} · under a minute · 1 line · 0 questions`,
    );
    expect(within(newer, 'delete')).not.toBeNull();
    expect(bridge.localExportMeeting.mock.calls.map(([req]) => req)).toEqual([
      { meetingId: NEWER.id },
      { meetingId: OLDER.id },
    ]);
    expect(within(newer, 'export-result')?.textContent?.trim()).toBe('Saved');
    expect(within(older, 'export-result')?.textContent?.trim()).toBe('Not saved');
  });

  it('Delete and Delete all ask first and refresh the list', async () => {
    const bridge = makeBridge([[NEWER, OLDER], [OLDER], []]);
    const fixture = await render(bridge);

    within(el(fixture, `meeting-${NEWER.id}`), 'delete')!.click();
    await settle(fixture);
    const oneTitle = dialogTitle();
    const oneBody = document.querySelector('sc-confirm-dialog .body')?.textContent?.trim();
    const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
    const deletesBeforeConfirm = bridge.localDeleteMeetings.mock.calls.length;
    await answerConfirm(fixture, 'confirm-cancel');
    const dialogAfterCancel = dialogTitle();
    within(el(fixture, `meeting-${NEWER.id}`), 'delete')!.click();
    await settle(fixture);
    await answerConfirm(fixture, 'confirm-ok');
    const afterOne = titles(fixture);

    el(fixture, 'delete-all')!.click();
    await settle(fixture);
    const allTitle = dialogTitle();
    const allBody = document.querySelector('sc-confirm-dialog .body')?.textContent?.trim();
    await answerConfirm(fixture, 'confirm-ok');

    expect(oneTitle).toBe('Delete this meeting?');
    expect(focused).toBe('confirm-cancel');
    expect(deletesBeforeConfirm).toBe(0);
    expect(dialogAfterCancel).toBeNull();
    expect(afterOne).toEqual(['Standup']);
    expect(allTitle).toBe('Delete all meetings?');
    expect(oneBody).toBe(
      '"Release sync", its transcript and its questions are removed from this computer. It cannot be undone.',
    );
    expect(allBody).toBe(
      'This removes every kept meeting, their transcripts and their questions from this computer. It cannot be undone.',
    );
    expect(bridge.localDeleteMeetings.mock.calls.map(([req]) => req)).toEqual([
      { meetingId: NEWER.id },
      { all: true },
    ]);
    expect(bridge.localMeetings).toHaveBeenCalledTimes(3);
    expect(dialogTitle()).toBeNull();
    expect(el(fixture, 'meetings-empty')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'No meetings kept yet. Start one from Meeting in the quick-ask tools and tick Keep when you finish.',
    );
  });

  it("shows the worker's sentence when the meetings cannot be listed, and no empty state", async () => {
    const sentence =
      'Scout keeps no meetings on this machine; the meetings database is not configured.';
    const bridge = makeBridge([]);
    bridge.localMeetings.mockRejectedValueOnce(
      new Error(`[scout:local/meeting-failed] ${sentence}`),
    );
    const fixture = await render(bridge);

    expect(el(fixture, 'meetings-error')?.textContent?.trim()).toBe(sentence);
    expect(el(fixture, 'meetings-empty')).toBeNull();
  });

  it('an export or a delete that fails shows the sentence and keeps the list', async () => {
    const bridge = makeBridge([[NEWER]]);
    bridge.localExportMeeting.mockRejectedValueOnce(
      new Error('[scout:local/export-failed] Scout could not write the Markdown file.'),
    );
    bridge.localDeleteMeetings.mockRejectedValueOnce(
      new Error('[scout:local/meeting-failed] Scout has no meeting with that id.'),
    );
    const fixture = await render(bridge);
    const row = (): HTMLElement | null => el(fixture, `meeting-${NEWER.id}`);

    within(row(), 'export')!.click();
    await settle(fixture);
    within(row(), 'delete')!.click();
    await settle(fixture);
    await answerConfirm(fixture, 'confirm-ok');

    expect(within(row(), 'export-result')?.textContent?.trim()).toBe(
      'Scout could not write the Markdown file.',
    );
    expect(el(fixture, 'delete-error')?.textContent?.trim()).toBe(
      'Scout has no meeting with that id.',
    );
    expect(titles(fixture)).toEqual(['Release sync']);
  });
  it('a meeting kept from the quick-ask window while this screen is open is listed when main pushes that the meetings changed', async () => {
    const bridge = makeBridge([[OLDER], [NEWER, OLDER]]);
    const fixture = await render(bridge);
    const before = titles(fixture);

    bridge.onLocalMeetingsChanged.mock.calls.forEach(([listener]) => listener());
    await settle(fixture);

    expect(before).toEqual(['Standup']);
    expect(titles(fixture)).toEqual(['Release sync', 'Standup']);
  });

  it('with a full first page listed, the Delete all confirm does not claim that page is all the kept meetings', async () => {
    const page = Array.from({ length: 100 }, (_, i) => ({
      ...OLDER,
      id: `M${String(i).padStart(21, '0')}`,
      title: `Meeting ${i}`,
    }));
    const fixture = await render(makeBridge([page]));

    el(fixture, 'delete-all')!.click();
    await settle(fixture);
    const body = document.querySelector('sc-confirm-dialog .body')?.textContent?.trim();
    await answerConfirm(fixture, 'confirm-cancel');

    expect(body).toBe(
      'This removes every kept meeting, their transcripts and their questions from this computer. It cannot be undone.',
    );
  });

  it("one meeting's export finishing leaves another meeting's export still in progress disabled", async () => {
    const bridge = makeBridge([[NEWER, OLDER]]);
    const answers: ((value: { saved: boolean }) => void)[] = [];
    bridge.localExportMeeting.mockImplementation(
      () => new Promise<{ saved: boolean }>((resolve) => answers.push(resolve)),
    );
    const fixture = await render(bridge);
    const exportOf = (id: string) =>
      within(el(fixture, `meeting-${id}`), 'export') as HTMLButtonElement;

    exportOf(NEWER.id).click();
    await settle(fixture);
    exportOf(OLDER.id).click();
    await settle(fixture);
    answers[0]!({ saved: true });
    await settle(fixture);
    const olderStillBusy = exportOf(OLDER.id).disabled;
    answers[1]!({ saved: true });
    await settle(fixture);

    expect(answers).toHaveLength(2);
    expect(olderStillBusy).toBe(true);
  });

  it('a delete that fails because the meeting is already gone re-reads the list, and the gone meeting leaves it', async () => {
    const bridge = makeBridge([[NEWER], []]);
    bridge.localDeleteMeetings.mockRejectedValueOnce(
      new Error('[scout:local/meeting-failed] Scout has no meeting with that id.'),
    );
    const fixture = await render(bridge);

    within(el(fixture, `meeting-${NEWER.id}`), 'delete')!.click();
    await settle(fixture);
    await answerConfirm(fixture, 'confirm-ok');

    expect(titles(fixture)).toEqual([]);
  });
});

describe('Meetings follows the theme and the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('its styles paint only with the theme tokens, no hex, rgb() or hsl() colour left', () => {
    const source = readFileSync(fileURLToPath(new URL(COMPONENT_FILE, import.meta.url)), 'utf8');
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

  it('opens with the lede, and Delete all, Export as Markdown and Delete each carry an icon and their text', async () => {
    const fixture = await render(makeBridge([[NEWER]]));
    const row = el(fixture, `meeting-${NEWER.id}`);

    expect(el(fixture, 'meetings-lede')?.textContent?.trim()).toBe(
      'The meetings you kept from the quick-ask window. They stay on this computer.',
    );
    expect(
      [el(fixture, 'delete-all'), within(row, 'export'), within(row, 'delete')].map((b) => [
        b?.querySelector('sc-icon') !== null,
        b?.textContent?.trim(),
      ]),
    ).toEqual([
      [true, 'Delete all'],
      [true, 'Export as Markdown'],
      [true, 'Delete'],
    ]);
  });

  it('Delete all is absent while nothing is kept', async () => {
    const fixture = await render(makeBridge([[]]));

    expect(el(fixture, 'delete-all')).toBeNull();
    expect(el(fixture, 'meetings-empty')).not.toBeNull();
  });

  it('Cancel on Delete all keeps every meeting and deletes nothing', async () => {
    const bridge = makeBridge([[NEWER, OLDER]]);
    const fixture = await render(bridge);

    el(fixture, 'delete-all')!.click();
    await settle(fixture);
    const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
    const body = document.querySelector('sc-confirm-dialog .body')?.textContent?.trim();
    await answerConfirm(fixture, 'confirm-cancel');

    expect(focused).toBe('confirm-cancel');
    expect(bridge.localDeleteMeetings).not.toHaveBeenCalled();
    expect(titles(fixture)).toEqual(['Release sync', 'Standup']);
    expect(body).toBe(
      'This removes every kept meeting, their transcripts and their questions from this computer. It cannot be undone.',
    );
  });

  it('a list that fails offers Try again, which reads the meetings once more', async () => {
    const bridge = makeBridge([[NEWER]]);
    bridge.localMeetings.mockRejectedValueOnce(
      new Error('[scout:local/meeting-failed] The worker is not running.'),
    );
    const fixture = await render(bridge);
    const shown = el(fixture, 'meetings-error')?.textContent?.trim();

    el(fixture, 'meetings-retry')!.click();
    await settle(fixture);

    expect(shown).toBe('The worker is not running.');
    expect(el(fixture, 'meetings-error')).toBeNull();
    expect(titles(fixture)).toEqual(['Release sync']);
  });
});

const OLDEST: LocalMeetingSummary = {
  id: 'CCCCCCCCCCCCCCCCCCCCCC',
  startedAt: '2026-11-02T14:00:00.000+00:00',
  endedAt: '2026-11-02T14:55:00.000+00:00',
  title: 'Release planning',
  lineCount: 402,
  turnCount: 0,
};
const AT_TEN_LOCAL: LocalMeetingSummary = {
  ...NEWER,
  startedAt: new Date(2026, 10, 4, 10, 0).toISOString(),
  endedAt: new Date(2026, 10, 4, 10, 25, 30).toISOString(),
};
const EMPTY_SENTENCE =
  'No meetings kept yet. Start one from Meeting in the quick-ask tools and tick Keep when you finish.';
const NOT_CONFIGURED =
  'Scout keeps no meetings on this machine; the meetings database is not configured.';
const NO_ANSWER = 'Scout did not answer about the meetings in time.';
const NO_SUCH_MEETING = 'Scout has no meeting with that id.';
const NOT_WRITTEN = 'Scout could not write the Markdown file.';
const DELETE_ONE_BODY =
  '"Release sync", its transcript and its questions are removed from this computer. It cannot be undone.';
const DELETE_ALL_BODY =
  'This removes every kept meeting, their transcripts and their questions from this computer. It cannot be undone.';

function declared(node: Element, property: string): string | null {
  const scope = node.getAttributeNames().find((name) => name.startsWith('_ngcontent-'));
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    const own = scope !== undefined && selector!.includes(scope);
    if (found !== undefined && own && node.matches(selector!.trim())) value = found;
  }
  return value;
}

function styled(node: Element | null, properties: readonly string[]): (string | null)[] {
  if (node === null) throw new Error('the element to read the style of is not rendered');
  return properties.map((property) => declared(node, property));
}

function text(node: Element | null | undefined): string | null {
  return node?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
}

function notices(): { text: string; action: string | null; classes: string[] }[] {
  return Array.from(
    document.querySelectorAll<HTMLElement>('.cdk-overlay-container .sc-notice'),
  ).map((el) => {
    const label = el.querySelector('.mat-mdc-snack-bar-label [aria-hidden="true"]') ?? el;
    const copy = label.cloneNode(true) as HTMLElement;
    copy.querySelectorAll('button').forEach((button) => button.remove());
    return {
      text: (copy.textContent ?? '').replace(/\s+/g, ' ').trim(),
      action: el.querySelector('button')?.textContent?.trim() ?? null,
      classes: Array.from(el.classList).filter((c) => c.startsWith('sc-notice--')),
    };
  });
}

function failed(code: string, sentence: string): Error {
  return new Error(`[scout:local/${code}] ${sentence}`);
}

function unanswered<T>(): Promise<T> {
  return new Promise<T>(() => undefined);
}

type Row = readonly [
  title: string | null,
  exportResult: string | null,
  exportOff: boolean,
  deleteOff: boolean,
];

interface Seen {
  readonly skeletons: number;
  readonly error: string | null;
  readonly tryAgain: boolean;
  readonly deleteError: string | null;
  readonly empty: string | null;
  readonly deleteAll: readonly [present: boolean, off: boolean];
  readonly rows: readonly Row[];
  readonly dialog: readonly (string | null)[] | null;
  readonly notice: readonly [text: string, action: string | null] | null;
}

const NO_DELETE_ALL = [false, false] as const;
const DELETE_ALL_READY = [true, false] as const;

const NOTHING: Seen = {
  skeletons: 0,
  error: null,
  tryAgain: false,
  deleteError: null,
  empty: null,
  deleteAll: NO_DELETE_ALL,
  rows: [],
  dialog: null,
  notice: null,
};

function seen(fixture: ComponentFixture<MeetingsComponent>): Seen {
  const host = fixture.nativeElement as HTMLElement;
  const dialog = document.querySelector('sc-confirm-dialog');
  const deleteAll = el(fixture, 'delete-all') as HTMLButtonElement | null;
  const [notice] = notices();
  return {
    skeletons: host.querySelectorAll(
      '[data-test="meetings-loading"] [data-test="meeting-skeleton"]',
    ).length,
    error: text(el(fixture, 'meetings-error')),
    tryAgain: el(fixture, 'meetings-retry') !== null,
    deleteError: text(el(fixture, 'delete-error')),
    empty: text(el(fixture, 'meetings-empty')),
    deleteAll: [deleteAll !== null, deleteAll?.disabled === true],
    rows: Array.from(host.querySelectorAll<HTMLElement>('[data-test="meetings-list"] > li')).map(
      (row) => [
        text(within(row, 'meeting-title')),
        text(within(row, 'export-result')),
        (within(row, 'export') as HTMLButtonElement).disabled,
        (within(row, 'delete') as HTMLButtonElement).disabled,
      ],
    ),
    dialog:
      dialog === null
        ? null
        : [
            dialogTitle(),
            text(dialog.querySelector('.body')),
            text(dialog.querySelector('[data-test="confirm-cancel"]')),
            text(dialog.querySelector('[data-test="confirm-ok"]')),
          ],
    notice: notice === undefined ? null : [notice.text, notice.action],
  };
}

async function click(
  fixture: ComponentFixture<MeetingsComponent>,
  button: HTMLElement | null,
): Promise<void> {
  button!.click();
  await settle(fixture);
}

function rowButton(
  fixture: ComponentFixture<MeetingsComponent>,
  meeting: LocalMeetingSummary,
  button: 'export' | 'delete',
): HTMLElement | null {
  return within(el(fixture, `meeting-${meeting.id}`), button);
}

async function reading(): Promise<ComponentFixture<MeetingsComponent>> {
  const bridge = makeBridge([[NEWER]]);
  bridge.localMeetings.mockImplementationOnce(() => unanswered());
  return render(bridge);
}

async function refused(): Promise<ComponentFixture<MeetingsComponent>> {
  const bridge = makeBridge([[NEWER]]);
  bridge.localMeetings.mockRejectedValueOnce(failed('meeting-failed', NOT_CONFIGURED));
  return render(bridge);
}

async function refusedWithMeetingsListed(): Promise<ComponentFixture<MeetingsComponent>> {
  const bridge = makeBridge([[OLDEST, NEWER, OLDER]]);
  const fixture = await render(bridge);
  bridge.localMeetings.mockRejectedValueOnce(failed('meeting-failed', NO_ANSWER));
  bridge.onLocalMeetingsChanged.mock.calls.forEach(([listener]) => listener());
  await settle(fixture);
  return fixture;
}

const IDLE: readonly Row[] = [
  ['Release sync', null, false, false],
  ['Standup', null, false, false],
  ['Release planning', null, false, false],
];
const DELETES_OFF: readonly Row[] = IDLE.map(([title]) => [title, null, false, true] as const);

interface Case {
  readonly state: string;
  readonly reach: () => Promise<ComponentFixture<MeetingsComponent>>;
  readonly shows: Seen;
}

const PREVIEW_STATES: readonly Case[] = [
  {
    state: 'Loaded',
    reach: () => render(makeBridge([[OLDEST, NEWER, OLDER]])),
    shows: { ...NOTHING, deleteAll: DELETE_ALL_READY, rows: IDLE },
  },
  {
    state: 'Loading',
    reach: reading,
    shows: { ...NOTHING, skeletons: 3 },
  },
  {
    state: 'Empty',
    reach: () => render(makeBridge([[]])),
    shows: { ...NOTHING, empty: EMPTY_SENTENCE },
  },
  {
    state: 'Error',
    reach: refused,
    shows: { ...NOTHING, error: NOT_CONFIGURED, tryAgain: true },
  },
  {
    state: 'Error, meetings kept',
    reach: refusedWithMeetingsListed,
    shows: {
      ...NOTHING,
      error: NO_ANSWER,
      tryAgain: true,
      deleteAll: DELETE_ALL_READY,
      rows: IDLE,
    },
  },
  {
    state: 'Delete dialog',
    reach: async () => {
      const fixture = await render(makeBridge([[OLDEST, NEWER, OLDER]]));
      await click(fixture, rowButton(fixture, NEWER, 'delete'));
      return fixture;
    },
    shows: {
      ...NOTHING,
      deleteAll: DELETE_ALL_READY,
      rows: IDLE,
      dialog: ['Delete this meeting?', DELETE_ONE_BODY, 'Cancel', 'Delete'],
    },
  },
  {
    state: 'Delete all dialog',
    reach: async () => {
      const fixture = await render(makeBridge([[OLDEST, NEWER, OLDER]]));
      await click(fixture, el(fixture, 'delete-all'));
      return fixture;
    },
    shows: {
      ...NOTHING,
      deleteAll: DELETE_ALL_READY,
      rows: IDLE,
      dialog: ['Delete all meetings?', DELETE_ALL_BODY, 'Cancel', 'Delete all'],
    },
  },
  {
    state: 'Export results',
    reach: async () => {
      const bridge = makeBridge([[OLDEST, NEWER, OLDER]]);
      bridge.localExportMeeting
        .mockResolvedValueOnce({ saved: true })
        .mockResolvedValueOnce({ saved: false })
        .mockRejectedValueOnce(failed('export-failed', NOT_WRITTEN));
      const fixture = await render(bridge);
      for (const meeting of [NEWER, OLDER, OLDEST]) {
        await click(fixture, rowButton(fixture, meeting, 'export'));
      }
      return fixture;
    },
    shows: {
      ...NOTHING,
      deleteAll: DELETE_ALL_READY,
      rows: [
        ['Release sync', 'Saved', false, false],
        ['Standup', 'Not saved', false, false],
        ['Release planning', NOT_WRITTEN, false, false],
      ],
      notice: ['Saved', 'OK'],
    },
  },
  {
    state: 'Exporting',
    reach: async () => {
      const bridge = makeBridge([[OLDEST, NEWER, OLDER]]);
      bridge.localExportMeeting.mockImplementationOnce(() => unanswered());
      const fixture = await render(bridge);
      await click(fixture, rowButton(fixture, NEWER, 'export'));
      return fixture;
    },
    shows: {
      ...NOTHING,
      deleteAll: DELETE_ALL_READY,
      rows: [['Release sync', null, true, false], IDLE[1]!, IDLE[2]!],
    },
  },
  {
    state: 'Deleting',
    reach: async () => {
      const bridge = makeBridge([[OLDEST, NEWER, OLDER]]);
      bridge.localDeleteMeetings.mockImplementationOnce(() => unanswered());
      const fixture = await render(bridge);
      await click(fixture, rowButton(fixture, NEWER, 'delete'));
      await answerConfirm(fixture, 'confirm-ok');
      return fixture;
    },
    shows: { ...NOTHING, deleteAll: DELETE_ALL_READY, rows: DELETES_OFF },
  },
  {
    state: 'Delete failed',
    reach: async () => {
      const bridge = makeBridge([[OLDEST, NEWER, OLDER]]);
      bridge.localDeleteMeetings.mockRejectedValueOnce(failed('meeting-failed', NO_SUCH_MEETING));
      const fixture = await render(bridge);
      await click(fixture, rowButton(fixture, NEWER, 'delete'));
      await answerConfirm(fixture, 'confirm-ok');
      return fixture;
    },
    shows: {
      ...NOTHING,
      deleteError: NO_SUCH_MEETING,
      deleteAll: DELETE_ALL_READY,
      rows: IDLE,
    },
  },
];

const KEPT_AS_TODAY: readonly Case[] = [
  {
    state: 'one meeting kept',
    reach: () => render(makeBridge([[NEWER]])),
    shows: { ...NOTHING, deleteAll: DELETE_ALL_READY, rows: [IDLE[0]!] },
  },
  {
    state: 'Delete all running',
    reach: async () => {
      const bridge = makeBridge([[OLDEST, NEWER, OLDER]]);
      bridge.localDeleteMeetings.mockImplementationOnce(() => unanswered());
      const fixture = await render(bridge);
      await click(fixture, el(fixture, 'delete-all'));
      await answerConfirm(fixture, 'confirm-ok');
      return fixture;
    },
    shows: { ...NOTHING, deleteAll: DELETE_ALL_READY, rows: DELETES_OFF },
  },
  {
    state: 'Try again answered while the meetings are listed',
    reach: async () => {
      const fixture = await refusedWithMeetingsListed();
      await click(fixture, el(fixture, 'meetings-retry'));
      return fixture;
    },
    shows: { ...NOTHING, deleteAll: DELETE_ALL_READY, rows: IDLE },
  },
  {
    state: 'an export saved while a delete runs',
    reach: async () => {
      const bridge = makeBridge([[OLDEST, NEWER, OLDER]]);
      bridge.localDeleteMeetings.mockImplementationOnce(() => unanswered());
      const fixture = await render(bridge);
      await click(fixture, rowButton(fixture, NEWER, 'delete'));
      await answerConfirm(fixture, 'confirm-ok');
      await click(fixture, rowButton(fixture, OLDER, 'export'));
      return fixture;
    },
    shows: {
      ...NOTHING,
      deleteAll: DELETE_ALL_READY,
      rows: [DELETES_OFF[0]!, ['Standup', 'Saved', false, true], DELETES_OFF[2]!],
      notice: ['Saved', 'OK'],
    },
  },
  {
    state: 'a meeting deleted while its export runs',
    reach: async () => {
      const bridge = makeBridge([
        [OLDEST, NEWER, OLDER],
        [OLDEST, OLDER],
      ]);
      bridge.localExportMeeting.mockImplementationOnce(() => unanswered());
      const fixture = await render(bridge);
      await click(fixture, rowButton(fixture, NEWER, 'export'));
      await click(fixture, rowButton(fixture, NEWER, 'delete'));
      await answerConfirm(fixture, 'confirm-ok');
      return fixture;
    },
    shows: {
      ...NOTHING,
      deleteAll: DELETE_ALL_READY,
      rows: [IDLE[1]!, IDLE[2]!],
      notice: ['Meeting deleted', 'OK'],
    },
  },
];

describe('Meetings as the artboard draws it', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('while the meetings are being read, the progress bar sits over three skeleton rows, with no list, empty state, error or Delete all', async () => {
    const bridge = makeBridge([[NEWER]]);
    let answer: (value: { items: readonly LocalMeetingSummary[] }) => void = () => undefined;
    bridge.localMeetings.mockImplementationOnce(
      () => new Promise<{ items: readonly LocalMeetingSummary[] }>((resolve) => (answer = resolve)),
    );
    const fixture = await render(bridge);
    const loading = el(fixture, 'meetings-loading');
    const whileReading = {
      announced: [loading?.getAttribute('role'), loading?.getAttribute('aria-label')],
      children: Array.from(loading?.children ?? []).map((child) => child.tagName.toLowerCase()),
      skeletonLines: Array.from(
        loading?.querySelectorAll('[data-test="meeting-skeleton"]') ?? [],
      ).map((skeleton) => [
        skeleton.getAttribute('aria-hidden'),
        skeleton.querySelectorAll('i').length,
      ]),
      rest: seen(fixture),
    };

    answer({ items: [NEWER] });
    await settle(fixture);

    expect(whileReading).toEqual({
      announced: ['status', 'Loading the meetings'],
      children: ['mat-progress-bar', 'div', 'div', 'div'],
      skeletonLines: [
        ['true', 2],
        ['true', 2],
        ['true', 2],
      ],
      rest: { ...NOTHING, skeletons: 3 },
    });
    expect(el(fixture, 'meetings-loading')).toBeNull();
    expect(titles(fixture)).toEqual(['Release sync']);
  });

  it('a skeleton row is a card on the surface, at most 380 px wide, with a title bar and a longer bar on the second surface, under a progress bar with the same track', async () => {
    const fixture = await reading();
    const loading = el(fixture, 'meetings-loading');
    const skeleton = within(loading, 'meeting-skeleton');
    const [titleBar, bar] = Array.from(skeleton?.querySelectorAll('i') ?? []);

    expect(styled(loading, ['flex-direction', 'gap', '--mat-progress-bar-track-color'])).toEqual([
      'column',
      '12px',
      'var(--sc-surface-2)',
    ]);
    expect(styled(skeleton, ['border', 'background', 'padding', 'max-width'])).toEqual([
      '1px solid var(--sc-line)',
      'var(--sc-surface)',
      '16px',
      '380px',
    ]);
    expect(styled(titleBar ?? null, ['height', 'width', 'background'])).toEqual([
      '14px',
      '60%',
      'var(--sc-surface-2)',
    ]);
    expect(styled(bar ?? null, ['height', 'width', 'background'])).toEqual([
      '12px',
      '85%',
      'var(--sc-surface-2)',
    ]);
  });

  it('a meeting is a row card: its title over the date, length, lines and questions, then Export as Markdown and Delete as outlined buttons', async () => {
    const fixture = await render(makeBridge([[AT_TEN_LOCAL]]));
    const row = el(fixture, `meeting-${AT_TEN_LOCAL.id}`);
    const columns = Array.from(row?.children ?? []).map((column) =>
      Array.from(column.children).map((part) => part.getAttribute('data-test')),
    );
    const buttons = Array.from(row?.querySelectorAll('button') ?? []).map((button) => [
      button.classList.contains('mat-mdc-outlined-button'),
      button.querySelector('.mdc-button__label')?.firstElementChild?.tagName.toLowerCase(),
      text(button),
      declared(button, '--mat-button-outlined-label-text-color'),
    ]);

    expect(columns).toEqual([
      ['meeting-title', 'meeting-meta'],
      ['export', 'delete'],
    ]);
    expect(text(within(row, 'meeting-meta'))).toBe(
      '2026-11-04 10:00 · 25 min · 42 lines · 3 questions',
    );
    expect(buttons).toEqual([
      [true, 'sc-icon', 'Export as Markdown', null],
      [true, 'sc-icon', 'Delete', 'var(--sc-error)'],
    ]);
    expect(styled(row, ['background', 'border', 'padding', 'align-items', 'gap'])).toEqual([
      'var(--sc-surface)',
      '1px solid var(--sc-line)',
      '14px 16px',
      'center',
      '12px',
    ]);
    expect(styled(within(row, 'meeting-title'), ['font-weight'])).toEqual(['600']);
    expect(styled(within(row, 'meeting-meta'), ['font-size', 'color'])).toEqual([
      '12px',
      'var(--sc-muted)',
    ]);
    expect(
      styled(within(row, 'export')?.querySelector('sc-icon') ?? null, [
        'margin-right',
        'vertical-align',
      ]),
    ).toEqual(['8px', 'middle']);
  });

  it('the page sets 14 px text from its left edge, and its outlined buttons 36 px high with 14 px of padding and a regular-weight label', async () => {
    const fixture = await render(makeBridge([[NEWER]]));
    const page = (fixture.nativeElement as HTMLElement).querySelector('main');

    expect(
      styled(page, [
        'font-size',
        'margin',
        'max-width',
        '--mat-button-outlined-container-height',
        '--mat-button-outlined-horizontal-padding',
        '--mat-button-outlined-label-text-weight',
      ]),
    ).toEqual(['14px', '0', '1200px', '36px', '14px', '400']);
    expect(page?.contains(el(fixture, 'delete-all'))).toBe(true);
    expect(page?.contains(el(fixture, 'export'))).toBe(true);
  });

  it('a refused list reads in the text colour on the error container, its icon in the error colour, with Try again on the surface', async () => {
    const fixture = await refused();
    const sentence = el(fixture, 'meetings-error');
    const alert = sentence?.parentElement ?? null;

    expect(alert?.firstElementChild?.tagName.toLowerCase()).toBe('sc-icon');
    expect(styled(alert, ['color', 'background', 'border'])).toEqual([
      'var(--sc-error)',
      'var(--sc-error-container)',
      '1px solid var(--sc-error)',
    ]);
    expect(styled(sentence, ['color'])).toEqual(['var(--sc-fg)']);
    expect(styled(el(fixture, 'meetings-retry'), ['background'])).toEqual(['var(--sc-surface)']);
  });

  it.each(PREVIEW_STATES)(
    'the $state preview state shows the owner what the artboard draws for it',
    async ({ reach, shows }) => {
      const fixture = await reach();
      const shown = seen(fixture);
      document.querySelector<HTMLButtonElement>('[data-test="confirm-cancel"]')?.click();
      await settle(fixture);

      expect(shown).toEqual(shows);
    },
  );
});

describe('Meetings keeps what it does today', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it.each(KEPT_AS_TODAY)('with $state, the screen shows what it showed before', async (kept) => {
    const fixture = await kept.reach();

    expect(seen(fixture)).toEqual(kept.shows);
  });
});

type Notice = ReturnType<typeof notices>[number];

interface Told {
  readonly notices: readonly Notice[];
  readonly rows: readonly (readonly [title: string | null, exportResult: string | null])[];
  readonly deleteError: string | null;
  readonly empty: string | null;
  readonly deleteAll: boolean;
  readonly exports: readonly { meetingId: string }[];
  readonly deletes: readonly LocalDeleteMeetingsRequest[];
  readonly reads: number;
}

interface Happened {
  readonly case: string;
  readonly lists: readonly (readonly LocalMeetingSummary[])[];
  readonly answer: (bridge: ReturnType<typeof makeBridge>) => void;
  readonly act: (fixture: ComponentFixture<MeetingsComponent>) => Promise<void>;
  readonly tells: Told;
}

function success(text: string): Notice {
  return { text, action: 'OK', classes: ['sc-notice--success'] };
}

const BOTH_LISTED = [
  ['Release sync', null],
  ['Standup', null],
] as const;
const QUIET: Omit<Told, 'notices' | 'exports' | 'deletes' | 'reads'> = {
  rows: BOTH_LISTED,
  deleteError: null,
  empty: null,
  deleteAll: true,
};
const MAIN_AGREES = (): void => undefined;
const EXPORT_ONE = [{ meetingId: NEWER.id }];
const DELETE_ONE = [{ meetingId: NEWER.id }];
const DELETE_ALL = [{ all: true }] as const;

async function exportNewer(fixture: ComponentFixture<MeetingsComponent>): Promise<void> {
  await click(fixture, rowButton(fixture, NEWER, 'export'));
}

function deleteNewer(answer: 'confirm-ok' | 'confirm-cancel') {
  return async (fixture: ComponentFixture<MeetingsComponent>): Promise<void> => {
    await click(fixture, rowButton(fixture, NEWER, 'delete'));
    await answerConfirm(fixture, answer);
  };
}

async function deleteEvery(fixture: ComponentFixture<MeetingsComponent>): Promise<void> {
  await click(fixture, el(fixture, 'delete-all'));
  await answerConfirm(fixture, 'confirm-ok');
}

const NOTICES: readonly Happened[] = [
  {
    case: 'export saved',
    lists: [[NEWER, OLDER]],
    answer: MAIN_AGREES,
    act: exportNewer,
    tells: {
      ...QUIET,
      notices: [success('Saved')],
      rows: [
        ['Release sync', 'Saved'],
        ['Standup', null],
      ],
      exports: EXPORT_ONE,
      deletes: [],
      reads: 1,
    },
  },
  {
    case: 'export cancelled in the save dialog',
    lists: [[NEWER, OLDER]],
    answer: (bridge) => bridge.localExportMeeting.mockResolvedValueOnce({ saved: false }),
    act: exportNewer,
    tells: {
      ...QUIET,
      notices: [],
      rows: [
        ['Release sync', 'Not saved'],
        ['Standup', null],
      ],
      exports: EXPORT_ONE,
      deletes: [],
      reads: 1,
    },
  },
  {
    case: 'export failed',
    lists: [[NEWER, OLDER]],
    answer: (bridge) =>
      bridge.localExportMeeting.mockRejectedValueOnce(failed('export-failed', NOT_WRITTEN)),
    act: exportNewer,
    tells: {
      ...QUIET,
      notices: [],
      rows: [
        ['Release sync', NOT_WRITTEN],
        ['Standup', null],
      ],
      exports: EXPORT_ONE,
      deletes: [],
      reads: 1,
    },
  },
  {
    case: 'delete confirmed',
    lists: [[NEWER, OLDER], [OLDER]],
    answer: MAIN_AGREES,
    act: deleteNewer('confirm-ok'),
    tells: {
      ...QUIET,
      notices: [success('Meeting deleted')],
      rows: [['Standup', null]],
      exports: [],
      deletes: DELETE_ONE,
      reads: 2,
    },
  },
  {
    case: 'delete all confirmed',
    lists: [[NEWER, OLDER], []],
    answer: MAIN_AGREES,
    act: deleteEvery,
    tells: {
      notices: [success('All meetings deleted')],
      rows: [],
      deleteError: null,
      empty: EMPTY_SENTENCE,
      deleteAll: false,
      exports: [],
      deletes: DELETE_ALL,
      reads: 2,
    },
  },
  {
    case: 'delete refused',
    lists: [[NEWER, OLDER]],
    answer: (bridge) =>
      bridge.localDeleteMeetings.mockRejectedValueOnce(failed('meeting-failed', NO_SUCH_MEETING)),
    act: deleteNewer('confirm-ok'),
    tells: {
      ...QUIET,
      notices: [],
      deleteError: NO_SUCH_MEETING,
      exports: [],
      deletes: DELETE_ONE,
      reads: 2,
    },
  },
  {
    case: 'delete all refused',
    lists: [[NEWER, OLDER]],
    answer: (bridge) =>
      bridge.localDeleteMeetings.mockRejectedValueOnce(failed('meeting-failed', NO_ANSWER)),
    act: deleteEvery,
    tells: {
      ...QUIET,
      notices: [],
      deleteError: NO_ANSWER,
      exports: [],
      deletes: DELETE_ALL,
      reads: 2,
    },
  },
  {
    case: 'delete cancelled',
    lists: [[NEWER, OLDER]],
    answer: MAIN_AGREES,
    act: deleteNewer('confirm-cancel'),
    tells: { ...QUIET, notices: [], exports: [], deletes: [], reads: 1 },
  },
];

describe('Meetings says what happened', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it.each(NOTICES)(
    '$case: the notice the artboard draws for it, with OK, or none, over the rows the screen keeps',
    async ({ lists, answer, act, tells }) => {
      const bridge = makeBridge(lists);
      answer(bridge);
      const fixture = await render(bridge);

      await act(fixture);

      expect({
        notices: notices(),
        rows: seen(fixture).rows.map(([title, exportResult]) => [title, exportResult]),
        deleteError: text(el(fixture, 'delete-error')),
        empty: text(el(fixture, 'meetings-empty')),
        deleteAll: el(fixture, 'delete-all') !== null,
        exports: bridge.localExportMeeting.mock.calls.map(([req]) => req),
        deletes: bridge.localDeleteMeetings.mock.calls.map(([req]) => req),
        reads: bridge.localMeetings.mock.calls.length,
      }).toEqual(tells);
    },
  );
});
