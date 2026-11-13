import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  effect,
  inject,
  signal,
} from '@angular/core';

import { MatButtonModule } from '@angular/material/button';

import { IconComponent } from './icon.component';
import { LocalConversation } from './local-conversation';
import { PendingApprovals, type ApprovalCard } from './pending-approvals.store';

const POLL_MS = 15_000;

let strips = 0;

/**
 * "Waiting for your approval": the actions that cannot be undone and wait for the owner, as cards pinned above the
 * composer of the chat and of the quick-ask window. Approve and Reject are the owner's click, on the same channels
 * as the Approvals screen; a decided card shows how the action ended until it is dismissed.
 */
@Component({
  selector: 'sc-approval-strip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, IconComponent, MatButtonModule],
  template: `
    @if (store.cards().length > 0) {
      <section class="strip" data-test="approval-strip" [attr.aria-labelledby]="headingId">
        <h2 class="heading" [id]="headingId" data-test="approval-strip-heading">
          {{ store.waiting() > 0 ? 'Waiting for your approval' : 'Your decisions' }}
        </h2>
        <ul class="cards" aria-live="polite">
          @for (card of store.cards(); track card.action.id) {
            <li>
              <section
                class="card"
                [attr.aria-labelledby]="headingId + '-' + card.action.id"
                [attr.data-test]="'approval-' + card.action.id"
                [attr.data-state]="card.state"
              >
                <div class="chips">
                  <span class="chip" data-test="approval-feature">{{ card.feature }}</span>
                  <span class="chip chip--warn"
                    ><sc-icon name="warning" [size]="14" />Cannot be undone</span
                  >
                  <span class="requested" data-test="approval-requested"
                    >Requested {{ card.action.requestedAt | date: 'yyyy-MM-dd HH:mm' }}</span
                  >
                </div>
                <h3
                  class="title"
                  [id]="headingId + '-' + card.action.id"
                  data-test="approval-title"
                >
                  {{ card.title }}
                </h3>
                <p class="target">
                  Target: <code data-test="approval-target">{{ card.action.target }}</code>
                </p>
                <p class="summary" data-test="approval-summary">{{ card.action.summary }}</p>
                @switch (card.state) {
                  @case ('pending') {
                    @if (rejecting() === card.action.id) {
                      <div class="decide">
                        <label class="reason">
                          Reason (optional)
                          <input
                            type="text"
                            placeholder="Saved with the decision in the history"
                            [attr.data-test]="'approval-reason-' + card.action.id"
                            #reason
                          />
                        </label>
                        <button
                          type="button"
                          class="quiet"
                          [attr.data-test]="'approval-cancel-' + card.action.id"
                          (click)="cancelReject(card)"
                        >
                          Cancel
                        </button>
                        <button
                          type="button"
                          matButton="outlined"
                          class="danger"
                          [attr.data-test]="'approval-confirm-reject-' + card.action.id"
                          [disabled]="card.deciding"
                          (click)="reject(card, reason.value)"
                        >
                          <sc-icon name="close" [size]="16" />Reject
                        </button>
                      </div>
                    } @else {
                      <div class="decide">
                        <button
                          type="button"
                          matButton="outlined"
                          class="danger"
                          [attr.data-test]="'approval-reject-' + card.action.id"
                          [disabled]="card.deciding"
                          (click)="startReject(card)"
                        >
                          <sc-icon name="close" [size]="16" />Reject
                        </button>
                        <button
                          type="button"
                          matButton="filled"
                          class="primary"
                          [attr.data-test]="'approval-approve-' + card.action.id"
                          [disabled]="card.deciding"
                          (click)="store.decide(card.action, 'approve')"
                        >
                          <sc-icon name="check" [size]="16" />Approve
                        </button>
                      </div>
                    }
                  }
                  @default {
                    <div class="outcome">
                      <span
                        class="chip"
                        role="status"
                        [class.chip--running]="card.state === 'running'"
                        [class.chip--ok]="card.state === 'done'"
                        [class.chip--error]="card.state === 'failed'"
                        [attr.data-test]="'approval-outcome-' + card.action.id"
                        >{{ outcomeText(card) }}</span
                      >
                      <button
                        type="button"
                        class="quiet"
                        [attr.data-test]="'approval-dismiss-' + card.action.id"
                        (click)="store.dismiss(card.action.id)"
                      >
                        <sc-icon name="close" [size]="16" />Dismiss
                      </button>
                    </div>
                  }
                }
                @if (card.error; as message) {
                  <p
                    class="error"
                    role="alert"
                    [attr.data-test]="'approval-error-' + card.action.id"
                  >
                    {{ message }}
                  </p>
                }
              </section>
            </li>
          }
        </ul>
      </section>
    }
  `,
  styles: [
    `
      :host {
        display: contents;
      }
      .strip {
        flex: none;
        max-height: 40vh;
        overflow-y: auto;
        padding: 8px var(--approval-inset, 0);
        display: flex;
        flex-direction: column;
        gap: 8px;
        color: var(--sc-fg);
      }
      .heading {
        margin: 0;
        font-size: 13px;
        font-weight: 600;
        color: var(--sc-muted);
      }
      .cards {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .card {
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-left: 3px solid var(--sc-accent);
        border-radius: 12px;
        padding: 12px 14px;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .chips,
      .decide,
      .outcome {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .decide {
        align-items: flex-end;
        justify-content: flex-end;
      }
      .outcome {
        justify-content: space-between;
      }
      .chip {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 3px 10px;
        border-radius: 12px;
        font-size: 12px;
        font-weight: 500;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .chip--warn {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .chip--running {
        color: var(--sc-accent);
      }
      .chip--ok {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .chip--error {
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
      }
      .requested {
        margin-left: auto;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .title {
        margin: 0;
        font-size: 15px;
        font-weight: 600;
      }
      .target,
      .summary {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg-2);
        overflow-wrap: anywhere;
      }
      code {
        font-family: var(--sc-mono);
        font-size: 12px;
      }
      .reason {
        display: flex;
        flex-direction: column;
        gap: 4px;
        flex: 1 1 220px;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .reason input {
        height: 34px;
        padding: 0 10px;
        border-radius: var(--sc-radius-sm);
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        color: var(--sc-fg);
        font: inherit;
        font-size: 14px;
      }
      .quiet {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        height: 34px;
        padding: 0 14px;
        border-radius: var(--sc-radius-sm);
        font: inherit;
        font-size: 14px;
        cursor: pointer;
      }
      .quiet:focus-visible {
        outline: 2px solid var(--sc-focus);
        outline-offset: 2px;
      }
      .primary {
        --mat-button-filled-container-height: 34px;
        --mat-button-filled-horizontal-padding: 14px;
        --mat-button-filled-container-shape: var(--sc-radius-sm);
        --mat-button-filled-label-text-size: 14px;
        --mat-button-filled-label-text-weight: 400;
        --mat-button-filled-container-color: var(--sc-accent-strong);
        --mat-button-filled-label-text-color: var(--sc-on-accent);
        --mat-button-filled-disabled-container-color: var(--sc-surface-2);
        --mat-button-filled-disabled-label-text-color: var(--sc-muted);
      }
      .danger {
        --mat-button-outlined-container-height: 34px;
        --mat-button-outlined-horizontal-padding: 14px;
        --mat-button-outlined-container-shape: var(--sc-radius-sm);
        --mat-button-outlined-label-text-size: 14px;
        --mat-button-outlined-label-text-weight: 400;
        --mat-button-outlined-label-text-color: var(--sc-error);
        --mat-button-outlined-outline-color: var(--sc-error);
        --reject-dimmed: color-mix(in srgb, var(--sc-error) 55%, transparent);
        --mat-button-outlined-disabled-label-text-color: var(--reject-dimmed);
        --mat-button-outlined-disabled-outline-color: var(--reject-dimmed);
      }
      .primary sc-icon,
      .danger sc-icon {
        margin-right: 6px;
      }
      .quiet {
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg-2);
      }
      .error {
        margin: 0;
        padding: 8px 10px;
        border-radius: var(--sc-radius-sm);
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
        font-size: 13px;
      }
    `,
  ],
})
export class ApprovalStripComponent implements OnInit, OnDestroy {
  readonly store = inject(PendingApprovals);
  private readonly conversation = inject(LocalConversation);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private timer: ReturnType<typeof setInterval> | null = null;
  private wasRunning = false;

  readonly headingId = `approval-strip-${++strips}`;
  readonly rejecting = signal<string | null>(null);

  constructor() {
    effect(() => {
      const running = this.conversation.running() !== null;
      if (this.wasRunning && !running) void this.store.load();
      this.wasRunning = running;
    });
  }

  ngOnInit(): void {
    this.timer = setInterval(() => void this.store.load(), POLL_MS);
    void this.store.load();
  }

  ngOnDestroy(): void {
    if (this.timer !== null) clearInterval(this.timer);
  }

  outcomeText(card: ApprovalCard): string {
    switch (card.state) {
      case 'running':
        return 'Approved, running';
      case 'done':
        return 'Done';
      case 'rejected':
        return card.detail === null ? 'Rejected' : `Rejected: ${card.detail}`;
      case 'failed':
        return card.detail === null ? 'Failed' : `Failed: ${card.detail}`;
      case 'already':
        return 'Already decided';
      default:
        return '';
    }
  }

  startReject(card: ApprovalCard): void {
    this.rejecting.set(card.action.id);
    setTimeout(() => this.find(`approval-reason-${card.action.id}`)?.focus());
  }

  cancelReject(card: ApprovalCard): void {
    this.rejecting.set(null);
    setTimeout(() => this.find(`approval-reject-${card.action.id}`)?.focus());
  }

  async reject(card: ApprovalCard, reason: string): Promise<void> {
    await this.store.decide(card.action, 'reject', reason);
    const still = this.store.cards().find((c) => c.action.id === card.action.id);
    if (still?.state !== 'pending') this.rejecting.set(null);
  }

  private find(testId: string): HTMLElement | null {
    return this.host.nativeElement.querySelector<HTMLElement>(`[data-test="${testId}"]`);
  }
}
