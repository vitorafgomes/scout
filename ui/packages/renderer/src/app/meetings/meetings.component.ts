import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';

import { readIpcError, type LocalMeetingSummary } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

const LISTED = 100;

function counted(n: number, noun: string): string {
  return `${n} ${noun}${n === 1 ? '' : 's'}`;
}

/**
 * The meetings the owner kept from the quick-ask window, newest first: each one can be exported as Markdown
 * through the system's save dialog, or deleted; Delete all removes every one. Nothing here leaves the machine.
 */
@Component({
  selector: 'sc-meetings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  template: `
    <main class="page">
      <header class="header">
        <h1>Meetings</h1>
        <p class="lede">The meetings you kept. They stay on this computer.</p>
      </header>

      @if (error(); as message) {
        <p class="err" data-test="meetings-error">{{ message }}</p>
      } @else if (loaded() && meetings().length === 0) {
        <p class="empty" data-test="meetings-empty">
          No meetings kept yet. Keep one from the quick-ask window after a meeting.
        </p>
      }

      @if (meetings().length > 0) {
        <div class="toolbar">
          <button
            type="button"
            class="btn btn--secondary"
            data-test="delete-all"
            (click)="openDelete('all')"
          >
            Delete all
          </button>
        </div>
        <ul class="actions" data-test="meetings-list">
          @for (m of meetings(); track m.id) {
            <li class="action" [attr.data-test]="'meeting-' + m.id">
              <div class="what">
                <strong data-test="meeting-title">{{ m.title }}</strong>
                <span class="meta" data-test="meeting-meta"
                  >{{ m.startedAt | date: 'yyyy-MM-dd HH:mm' }} · {{ duration(m) }} ·
                  {{ count(m.lineCount, 'line') }} · {{ count(m.turnCount, 'question') }}</span
                >
              </div>
              <div class="decide">
                <button
                  type="button"
                  class="btn btn--primary"
                  data-test="export"
                  [disabled]="busy() === m.id"
                  (click)="export(m)"
                >
                  Export
                </button>
                <button
                  type="button"
                  class="btn btn--secondary"
                  data-test="delete"
                  (click)="openDelete(m)"
                >
                  Delete
                </button>
                @if (exported()[m.id]; as result) {
                  <span class="meta" data-test="export-result">{{ result }}</span>
                }
              </div>
            </li>
          }
        </ul>
      }

      @if (deleting(); as target) {
        <div
          class="backdrop"
          role="dialog"
          aria-modal="true"
          aria-labelledby="delete-title"
          data-test="delete-dialog"
        >
          <div class="dialog">
            <h2 id="delete-title">
              {{ target === 'all' ? 'Delete all meetings' : 'Delete ' + target.title }}
            </h2>
            <p>
              {{
                target === 'all'
                  ? 'This removes every kept meeting, its transcript and its questions from this computer.'
                  : 'This removes the meeting, its transcript and its questions from this computer.'
              }}
            </p>
            @if (deleteError(); as message) {
              <p class="err" data-test="delete-error">{{ message }}</p>
            }
            <div class="decide">
              <button
                type="button"
                class="btn btn--secondary"
                data-test="delete-cancel"
                (click)="deleting.set(null)"
              >
                Cancel
              </button>
              <button
                type="button"
                class="btn btn--primary"
                data-test="delete-confirm"
                [disabled]="deletingBusy()"
                (click)="confirmDelete(target)"
              >
                Confirm and delete
              </button>
            </div>
          </div>
        </div>
      }
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .page {
        padding: 32px;
        max-width: 1080px;
        margin: 0 auto;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
        color: #e4e8f0;
      }
      .header {
        margin-bottom: 24px;
      }
      h1 {
        margin: 0 0 8px;
        font-size: 24px;
      }
      h2 {
        margin: 0;
        font-size: 16px;
      }
      .lede,
      .empty,
      .meta {
        margin: 0;
        font-size: 13px;
        color: #7b8299;
      }
      .meta {
        margin-left: 8px;
      }
      .err {
        font-size: 13px;
        color: #f87171;
      }
      .toolbar {
        display: flex;
        justify-content: flex-end;
        margin-bottom: 12px;
      }
      .actions {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .action {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 12px 16px;
        display: flex;
        flex-direction: column;
        gap: 8px;
        font-size: 13px;
      }
      .decide {
        display: flex;
        gap: 8px;
        align-items: center;
      }
      .backdrop {
        position: fixed;
        inset: 0;
        background: rgba(0, 0, 0, 0.6);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .dialog {
        background: #11131a;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 20px;
        max-width: 560px;
        width: 100%;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .btn {
        font-size: 13px;
        padding: 6px 12px;
        border-radius: 6px;
        border: 1px solid transparent;
        cursor: pointer;
      }
      .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
      .btn--primary {
        background: #2563eb;
        color: white;
      }
      .btn--secondary {
        background: transparent;
        color: #e4e8f0;
        border-color: #252b3b;
      }
    `,
  ],
})
export class MeetingsComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);

  readonly meetings = signal<readonly LocalMeetingSummary[]>([]);
  readonly loaded = signal(false);
  readonly error = signal<string | null>(null);
  readonly busy = signal<string | null>(null);
  readonly exported = signal<Readonly<Record<string, string>>>({});
  readonly deleting = signal<LocalMeetingSummary | 'all' | null>(null);
  readonly deletingBusy = signal(false);
  readonly deleteError = signal<string | null>(null);
  protected readonly count = counted;

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  duration(meeting: LocalMeetingSummary): string {
    const minutes = Math.floor(
      (Date.parse(meeting.endedAt) - Date.parse(meeting.startedAt)) / 60_000,
    );
    return minutes < 1 ? 'under a minute' : `${minutes} min`;
  }

  /** Asks main to save the meeting's Markdown where the owner chooses, and says whether it was saved. */
  async export(meeting: LocalMeetingSummary): Promise<void> {
    this.busy.set(meeting.id);
    let result: string;
    try {
      const { saved } = await this.bridge.localExportMeeting({ meetingId: meeting.id });
      result = saved ? 'Saved' : 'Not saved';
    } catch (err: unknown) {
      result = readIpcError(err).message;
    } finally {
      this.busy.set(null);
    }
    this.exported.update((shown) => ({ ...shown, [meeting.id]: result }));
  }

  openDelete(target: LocalMeetingSummary | 'all'): void {
    this.deleteError.set(null);
    this.deleting.set(target);
  }

  async confirmDelete(target: LocalMeetingSummary | 'all'): Promise<void> {
    this.deletingBusy.set(true);
    this.deleteError.set(null);
    try {
      await this.bridge.localDeleteMeetings(
        target === 'all' ? { all: true } : { meetingId: target.id },
      );
      this.deleting.set(null);
      await this.load();
    } catch (err: unknown) {
      this.deleteError.set(readIpcError(err).message);
    } finally {
      this.deletingBusy.set(false);
    }
  }

  private async load(): Promise<void> {
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
