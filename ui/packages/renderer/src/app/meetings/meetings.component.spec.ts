import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalDeleteMeetingsRequest, LocalMeetingSummary } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { MeetingsComponent } from './meetings.component';

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
  };
}

async function render(
  bridge: ReturnType<typeof makeBridge>,
): Promise<ComponentFixture<MeetingsComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
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
    expect(meta).toMatch(/^2026-11-04 \d{2}:\d{2} · 25 min · 42 lines · 3 questions$/);
    expect(shortMeta).toMatch(/^2026-11-03 \d{2}:\d{2} · under a minute · 1 line · 0 questions$/);
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
    const dialogTitle = el(fixture, 'delete-dialog')?.querySelector('h2')?.textContent?.trim();
    const deletesBeforeConfirm = bridge.localDeleteMeetings.mock.calls.length;
    el(fixture, 'delete-cancel')!.click();
    await settle(fixture);
    const dialogAfterCancel = el(fixture, 'delete-dialog');
    within(el(fixture, `meeting-${NEWER.id}`), 'delete')!.click();
    await settle(fixture);
    el(fixture, 'delete-confirm')!.click();
    await settle(fixture);
    const afterOne = titles(fixture);

    el(fixture, 'delete-all')!.click();
    await settle(fixture);
    const allTitle = el(fixture, 'delete-dialog')?.querySelector('h2')?.textContent?.trim();
    el(fixture, 'delete-confirm')!.click();
    await settle(fixture);

    expect(dialogTitle).toBe('Delete Release sync');
    expect(deletesBeforeConfirm).toBe(0);
    expect(dialogAfterCancel).toBeNull();
    expect(afterOne).toEqual(['Standup']);
    expect(allTitle).toBe('Delete all meetings');
    expect(bridge.localDeleteMeetings.mock.calls.map(([req]) => req)).toEqual([
      { meetingId: NEWER.id },
      { all: true },
    ]);
    expect(bridge.localMeetings).toHaveBeenCalledTimes(3);
    expect(el(fixture, 'delete-dialog')).toBeNull();
    expect(el(fixture, 'meetings-empty')?.textContent?.trim()).toBe(
      'No meetings kept yet. Keep one from the quick-ask window after a meeting.',
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
    el(fixture, 'delete-confirm')!.click();
    await settle(fixture);

    expect(within(row(), 'export-result')?.textContent?.trim()).toBe(
      'Scout could not write the Markdown file.',
    );
    expect(el(fixture, 'delete-error')?.textContent?.trim()).toBe(
      'Scout has no meeting with that id.',
    );
    expect(titles(fixture)).toEqual(['Release sync']);
  });
});
