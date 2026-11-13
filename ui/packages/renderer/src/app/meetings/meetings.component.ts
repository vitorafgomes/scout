import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressBarModule } from '@angular/material/progress-bar';

import { readIpcError, type LocalMeetingSummary } from '@scout/shared';

import { confirmAction } from '../core/confirm-dialog.component';
import { IconComponent } from '../core/icon.component';
import { NoticeService } from '../core/notice.service';
import { ScoutBridgeService } from '../core/scout-bridge';

const LISTED = 100;
const OK = { label: 'OK', run: () => undefined } as const;

function counted(n: number, noun: string): string {
  return `${n} ${noun}${n === 1 ? '' : 's'}`;
}

/**
 * The meetings the owner kept from the quick-ask window, newest first: each one can be exported as Markdown
 * through the system's save dialog, or deleted; Delete all removes every one. A saved export and a delete are
 * also told in a notice with OK. Nothing here leaves the machine.
 */
@Component({
  selector: 'sc-meetings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, IconComponent, MatButtonModule, MatProgressBarModule],
  template: `
    <main class="page">
      <header class="header">
        <div class="title-block">
          <h1>Meetings</h1>
          <p class="lede" data-test="meetings-lede">
            The meetings you kept from the quick-ask window. They stay on this computer.
          </p>
        </div>
        @if (meetings().length > 0) {
          <button
            type="button"
            matButton="outlined"
            class="danger"
            data-test="delete-all"
            (click)="openDelete('all')"
          >
            <sc-icon name="trash" [size]="16" />Delete all
          </button>
        }
      </header>

      @if (error(); as message) {
        <div class="alert" role="alert">
          <sc-icon name="error" [size]="20" />
          <span class="alert-text" data-test="meetings-error">{{ message }}</span>
          <button type="button" matButton="outlined" data-test="meetings-retry" (click)="load()">
            <sc-icon name="refresh" [size]="16" />Try again
          </button>
        </div>
      }

      @if (deleteError(); as message) {
        <p class="err" role="alert" data-test="delete-error">{{ message }}</p>
      }

      @if (!loaded()) {
        <div
          class="loading"
          role="status"
          aria-label="Loading the meetings"
          data-test="meetings-loading"
        >
          <mat-progress-bar mode="indeterminate" aria-label="Loading the meetings" />
          @for (n of skeletons; track n) {
            <div class="skeleton" aria-hidden="true" data-test="meeting-skeleton">
              <i class="line line--title"></i>
              <i class="line"></i>
            </div>
          }
        </div>
      } @else if (error() === null && meetings().length === 0) {
        <div class="empty">
          <span class="empty-icon"><sc-icon name="calendar" [size]="24" /></span>
          <p data-test="meetings-empty">
            No meetings kept yet. Start one from Meeting in the quick-ask tools and tick Keep when
            you finish.
          </p>
        </div>
      }

      @if (meetings().length > 0) {
        <ul class="meetings" aria-label="Kept meetings" data-test="meetings-list">
          @for (m of meetings(); track m.id) {
            <li class="meeting" [attr.data-test]="'meeting-' + m.id">
              <div class="what">
                <span class="title" data-test="meeting-title">{{ m.title }}</span>
                <span class="meta" data-test="meeting-meta"
                  >{{ m.startedAt | date: 'yyyy-MM-dd HH:mm' }} · {{ duration(m) }} ·
                  {{ count(m.lineCount, 'line') }} · {{ count(m.turnCount, 'question') }}</span
                >
                @if (exported()[m.id]; as result) {
                  <span class="meta" data-test="export-result">{{ result }}</span>
                }
              </div>
              <div class="decide">
                <button
                  type="button"
                  matButton="outlined"
                  data-test="export"
                  [disabled]="busy().includes(m.id)"
                  (click)="export(m)"
                >
                  <sc-icon name="download" [size]="16" />Export as Markdown
                </button>
                <button
                  type="button"
                  matButton="outlined"
                  class="danger"
                  data-test="delete"
                  [disabled]="deletingBusy()"
                  (click)="openDelete(m)"
                >
                  <sc-icon name="trash" [size]="16" />Delete
                </button>
              </div>
            </li>
          }
        </ul>
      }
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .page {
        padding: 24px 32px;
        max-width: 1200px;
        margin: 0;
        display: flex;
        flex-direction: column;
        gap: 16px;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
        font-size: 14px;
        color: var(--sc-fg);
        --mat-button-outlined-container-height: 36px;
        --mat-button-outlined-horizontal-padding: 14px;
        --mat-button-outlined-label-text-weight: 400;
      }
      .header {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        gap: 12px;
      }
      .title-block {
        display: flex;
        flex-direction: column;
        gap: 4px;
        margin-right: auto;
      }
      h1 {
        margin: 0;
        font-size: 20px;
        font-weight: 600;
      }
      .lede {
        margin: 0;
        font-size: 14px;
        color: var(--sc-muted);
      }
      button sc-icon {
        margin-right: 8px;
        vertical-align: middle;
      }
      .danger {
        --mat-button-outlined-label-text-color: var(--sc-error);
      }
      .alert {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 12px;
        padding: 12px 16px;
        border-radius: var(--sc-radius);
        border: 1px solid var(--sc-error);
        background: var(--sc-error-container);
        color: var(--sc-error);
      }
      .alert-text {
        margin-right: auto;
        color: var(--sc-fg);
      }
      .alert button {
        background: var(--sc-surface);
      }
      .loading {
        display: flex;
        flex-direction: column;
        gap: 12px;
        --mat-progress-bar-track-color: var(--sc-surface-2);
        --mat-progress-bar-track-shape: 2px;
      }
      .skeleton {
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        background: var(--sc-surface);
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 10px;
        max-width: 380px;
      }
      .line {
        display: block;
        height: 12px;
        width: 85%;
        border-radius: 6px;
        background: var(--sc-surface-2);
      }
      .line--title {
        height: 14px;
        width: 60%;
        border-radius: 7px;
      }
      .err {
        margin: 0;
        font-size: 13px;
        color: var(--sc-error);
      }
      .empty {
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        background: var(--sc-surface);
        padding: 40px 24px;
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 12px;
        text-align: center;
      }
      .empty p {
        margin: 0;
        max-width: 460px;
        color: var(--sc-fg-2);
      }
      .empty-icon {
        width: 48px;
        height: 48px;
        border-radius: 24px;
        background: var(--sc-surface-2);
        color: var(--sc-accent);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .empty-icon sc-icon {
        margin-right: 0;
      }
      .meetings {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .meeting {
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 14px 16px;
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 12px;
      }
      .what {
        display: flex;
        flex-direction: column;
        gap: 6px;
        margin-right: auto;
        min-width: 0;
      }
      .title {
        font-weight: 600;
        overflow-wrap: anywhere;
      }
      .meta {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .decide {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        align-items: center;
      }
    `,
  ],
})
export class MeetingsComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly dialog = inject(MatDialog);
  private readonly notice = inject(NoticeService);

  readonly meetings = signal<readonly LocalMeetingSummary[]>([]);
  readonly loaded = signal(false);
  readonly error = signal<string | null>(null);
  readonly busy = signal<readonly string[]>([]);
  readonly exported = signal<Readonly<Record<string, string>>>({});
  readonly deletingBusy = signal(false);
  readonly deleteError = signal<string | null>(null);
  protected readonly count = counted;
  protected readonly skeletons = [1, 2, 3];

  private readonly destroyRef = inject(DestroyRef);

  async ngOnInit(): Promise<void> {
    this.destroyRef.onDestroy(this.bridge.onLocalMeetingsChanged(() => void this.load()));
    await this.load();
  }

  duration(meeting: LocalMeetingSummary): string {
    const minutes = Math.floor(
      (Date.parse(meeting.endedAt) - Date.parse(meeting.startedAt)) / 60_000,
    );
    return minutes < 1 ? 'under a minute' : `${minutes} min`;
  }

  /** Asks main to save the meeting's Markdown where the owner chooses, and says in its row whether it was saved; a saved one is also told in a notice. */
  async export(meeting: LocalMeetingSummary): Promise<void> {
    this.busy.update((ids) => [...ids, meeting.id]);
    let result: string;
    try {
      const { saved } = await this.bridge.localExportMeeting({ meetingId: meeting.id });
      result = saved ? 'Saved' : 'Not saved';
      if (saved) this.notice.show('Saved', 'success', undefined, OK);
    } catch (err: unknown) {
      result = readIpcError(err).message;
    } finally {
      this.busy.update((ids) => ids.filter((id) => id !== meeting.id));
    }
    this.exported.update((shown) => ({ ...shown, [meeting.id]: result }));
  }

  /** A delete cannot be undone, so it asks first with Cancel focused; only the owner's Delete removes anything. */
  async openDelete(target: LocalMeetingSummary | 'all'): Promise<void> {
    this.deleteError.set(null);
    const all = target === 'all';
    const confirmed = await confirmAction(this.dialog, {
      title: all ? 'Delete all meetings?' : 'Delete this meeting?',
      body: all
        ? 'This removes every kept meeting, their transcripts and their questions from this computer. It cannot be undone.'
        : `"${target.title}", its transcript and its questions are removed from this computer. It cannot be undone.`,
      confirmLabel: all ? 'Delete all' : 'Delete',
    });
    if (confirmed) await this.confirmDelete(target);
  }

  /** Deletes one meeting or all of them, says so in a notice once main has, and lists what is left. */
  async confirmDelete(target: LocalMeetingSummary | 'all'): Promise<void> {
    const all = target === 'all';
    this.deletingBusy.set(true);
    this.deleteError.set(null);
    try {
      await this.bridge.localDeleteMeetings(all ? { all: true } : { meetingId: target.id });
      this.notice.show(all ? 'All meetings deleted' : 'Meeting deleted', 'success', undefined, OK);
      await this.load();
    } catch (err: unknown) {
      this.deleteError.set(readIpcError(err).message);
      await this.load();
    } finally {
      this.deletingBusy.set(false);
    }
  }

  async load(): Promise<void> {
    try {
      const { items } = await this.bridge.localMeetings({ limit: LISTED });
      this.meetings.set(
        [...items].sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt)),
      );
      this.error.set(null);
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    } finally {
      this.loaded.set(true);
    }
  }
}
