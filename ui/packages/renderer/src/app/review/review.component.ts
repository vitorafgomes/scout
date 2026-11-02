import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  inject,
  signal,
} from '@angular/core';

import { readIpcError, type PendingActionDto, type WriteLogEntryDto } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

const POLL_MS = 15_000;
const WRITES_SHOWN = 50;

/** The owner's approval surface: irreversible actions waiting for a decision, and the writes Scout made. */
@Component({
  selector: 'sc-review',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  template: `
    <main class="page">
      <header class="header">
        <h1>Review</h1>
        <p class="lede">What Scout is waiting to do, and what it did.</p>
      </header>

      @if (error(); as message) {
        <p class="err" data-test="review-error">{{ message }}</p>
      }

      <section class="block">
        <h2>Waiting for your approval</h2>
        @if (actions().length === 0) {
          <p class="empty" data-test="pending-empty">
            Nothing is waiting. Actions that cannot be undone will appear here before Scout performs
            them.
          </p>
        } @else {
          <ul class="actions">
            @for (a of actions(); track a.id) {
              <li class="action" [attr.data-test]="'action-' + a.id">
                <div class="what">
                  <strong>{{ a.action }}</strong> on <code>{{ a.target }}</code>
                  <span class="meta"
                    >{{ a.featureId }} · {{ a.requestedAt | date: 'yyyy-MM-dd HH:mm' }}</span
                  >
                </div>
                <p class="summary">{{ a.summary }}</p>
                <div class="decide">
                  <input
                    #reason
                    type="text"
                    placeholder="Reason (optional)"
                    [attr.data-test]="'reason-' + a.id"
                  />
                  <button
                    type="button"
                    class="btn btn--primary"
                    [attr.data-test]="'approve-' + a.id"
                    [disabled]="deciding() === a.id"
                    (click)="decide(a, 'approve', reason)"
                  >
                    Approve
                  </button>
                  <button
                    type="button"
                    class="btn btn--secondary"
                    [attr.data-test]="'reject-' + a.id"
                    [disabled]="deciding() === a.id"
                    (click)="decide(a, 'reject', reason)"
                  >
                    Reject
                  </button>
                </div>
              </li>
            }
          </ul>
        }
      </section>

      <section class="block">
        <h2>Recent writes</h2>
        @if (writes().length === 0) {
          <p class="empty" data-test="writes-empty">Scout has not written anything yet.</p>
        } @else {
          <table>
            <thead>
              <tr>
                <th>Time</th>
                <th>Feature</th>
                <th>Action</th>
                <th>Target</th>
                <th>Outcome</th>
                <th>Approved by</th>
              </tr>
            </thead>
            <tbody>
              @for (w of writes(); track $index) {
                <tr data-test="write-row">
                  <td>{{ w.at | date: 'yyyy-MM-dd HH:mm' }}</td>
                  <td>{{ w.featureId }}</td>
                  <td data-test="write-action">{{ w.action }}</td>
                  <td>
                    <code>{{ w.target }}</code>
                  </td>
                  <td>{{ w.outcome }}</td>
                  <td>{{ w.approvedBy ?? '-' }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
      </section>
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
        margin: 0 0 12px;
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
      .block {
        margin-bottom: 32px;
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
      .summary {
        margin: 0;
      }
      .decide {
        display: flex;
        gap: 8px;
      }
      .decide input {
        flex: 1;
        background: #0f1117;
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 6px 8px;
        color: #e4e8f0;
        font-size: 13px;
      }
      code {
        font-family: 'JetBrains Mono', ui-monospace, monospace;
        font-size: 12px;
      }
      table {
        width: 100%;
        border-collapse: collapse;
        font-size: 13px;
      }
      th,
      td {
        text-align: left;
        padding: 6px 8px;
        border-bottom: 1px solid #1e2333;
      }
      th {
        color: #7b8299;
        font-weight: 500;
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
export class ReviewComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private timer: ReturnType<typeof setInterval> | null = null;
  private unsubscribe: (() => void) | null = null;

  readonly actions = signal<readonly PendingActionDto[]>([]);
  readonly writes = signal<readonly WriteLogEntryDto[]>([]);
  readonly error = signal<string | null>(null);
  readonly deciding = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    this.unsubscribe = this.bridge.onActionsChanged(() => void this.load());
    this.timer = setInterval(() => void this.load(), POLL_MS);
    await this.load();
  }

  ngOnDestroy(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.unsubscribe?.();
  }

  async decide(
    action: PendingActionDto,
    decision: 'approve' | 'reject',
    reasonInput: HTMLInputElement,
  ): Promise<void> {
    const reason = reasonInput.value.trim();
    const req = reason.length > 0 ? { id: action.id, reason } : { id: action.id };
    this.deciding.set(action.id);
    try {
      if (decision === 'approve') await this.bridge.actionsApprove(req);
      else await this.bridge.actionsReject(req);
      await this.load();
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    } finally {
      this.deciding.set(null);
    }
  }

  private async load(): Promise<void> {
    try {
      const [pending, log] = await Promise.all([
        this.bridge.actionsList(),
        this.bridge.writesList({ limit: WRITES_SHOWN }),
      ]);
      this.actions.set(pending.actions);
      this.writes.set([...log.entries].sort((a, b) => Date.parse(b.at) - Date.parse(a.at)));
      this.error.set(null);
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    }
  }
}
