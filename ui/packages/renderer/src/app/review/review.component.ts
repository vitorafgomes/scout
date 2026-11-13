import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute } from '@angular/router';

import {
  readIpcError,
  type PendingActionDto,
  type WorkerStatus,
  type WriteLogEntryDto,
} from '@scout/shared';

import { IconComponent } from '../core/icon.component';
import { NoticeService } from '../core/notice.service';
import {
  actionInWords,
  approverInWords,
  decisionError,
  featureName,
  outcomeInWords,
  whenInWords,
} from '../core/pending-approvals.store';
import { ScoutBridgeService } from '../core/scout-bridge';

const POLL_MS = 15_000;
const WRITES_SHOWN = 50;
const OK = { label: 'OK', run: () => undefined } as const;

type ReviewTab = 'pending' | 'history';

/**
 * The owner's approval surface: irreversible actions waiting for a decision, and the writes Scout made. History lists the
 * actions approved and not yet run, with Start Scout while the worker is not running, the ones running, and the write
 * log with who signed each write. A route with `data.feature` shows that feature's items only; the main window's
 * Approvals shows every feature. Actions, outcomes, approvers and times read in words, and a decision the control plane
 * takes is also told in a notice with OK.
 */
@Component({
  selector: 'sc-review',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, MatButtonModule, MatProgressBarModule],
  template: `
    <main class="page">
      <header class="header">
        <div class="title-block">
          <h1>Approvals</h1>
          <p class="lede" data-test="review-lede">
            What Scout is waiting to do, and what it did. Actions that cannot be undone wait here
            for you.
          </p>
        </div>
        <button type="button" matButton="outlined" data-test="review-refresh" (click)="load()">
          <sc-icon name="refresh" [size]="16" />Refresh
        </button>
      </header>

      <div class="tabs" role="tablist" aria-label="Approvals">
        <button
          type="button"
          role="tab"
          id="tab-pending"
          class="tab"
          aria-controls="panel-pending"
          data-test="tab-pending"
          [attr.aria-selected]="tab() === 'pending'"
          (click)="tab.set('pending')"
        >
          Waiting for you
          @if (loaded() && actions().length > 0) {
            <span class="count" data-test="pending-count">{{ actions().length }}</span>
          }
        </button>
        <button
          type="button"
          role="tab"
          id="tab-history"
          class="tab"
          aria-controls="panel-history"
          data-test="tab-history"
          [attr.aria-selected]="tab() === 'history'"
          (click)="tab.set('history')"
        >
          History
        </button>
      </div>

      @if (error(); as message) {
        <div class="alert" role="alert">
          <sc-icon name="error" [size]="20" />
          <span class="alert-text" data-test="review-error">{{ message }}</span>
          <button type="button" matButton="outlined" data-test="review-retry" (click)="load()">
            <sc-icon name="refresh" [size]="16" />Try again
          </button>
        </div>
      }

      @if (!loaded()) {
        <div
          class="loading"
          role="status"
          aria-label="Loading the approvals"
          data-test="review-loading"
        >
          <mat-progress-bar mode="indeterminate" aria-label="Loading the approvals" />
          @for (card of [1, 2, 3]; track card) {
            <div class="skeleton" aria-hidden="true" data-test="review-skeleton">
              <span class="bar bar--title"></span>
              <span class="bar bar--line"></span>
              <span class="bar bar--meta"></span>
            </div>
          }
        </div>
      } @else if (tab() === 'pending') {
        <section id="panel-pending" role="tabpanel" aria-labelledby="tab-pending" class="panel">
          @if (actions().length === 0 && error() === null) {
            <div class="empty">
              <span class="empty-icon"><sc-icon name="check" [size]="24" /></span>
              <p data-test="pending-empty">
                Nothing is waiting. Actions that cannot be undone appear here before Scout performs
                them.
              </p>
              <button
                type="button"
                matButton="outlined"
                data-test="see-history"
                (click)="tab.set('history')"
              >
                <sc-icon name="list" [size]="16" />See the history
              </button>
            </div>
          } @else if (actions().length > 0) {
            <ul class="actions" aria-live="polite" aria-label="Waiting for your approval">
              @for (a of actions(); track a.id) {
                <li class="action" [attr.data-test]="'action-' + a.id">
                  <div class="chips">
                    <span class="chip" data-test="action-feature">{{
                      featureName(a.featureId)
                    }}</span>
                    <span class="chip chip--warn"
                      ><sc-icon name="warning" [size]="14" />Cannot be undone</span
                    >
                    <span class="requested">{{ whenInWords(a.requestedAt, 'requested') }}</span>
                  </div>
                  <div class="what">
                    <span class="name">{{ actionInWords(a.action, a.summary) }}</span>
                    <span class="target"
                      >Target: <code>{{ a.target }}</code></span
                    >
                  </div>
                  <p class="summary">{{ a.summary }}</p>
                  <div class="decide">
                    <label class="reason">
                      Reason (optional)
                      <input
                        #reason
                        type="text"
                        placeholder="Saved with the decision in the history"
                        [attr.data-test]="'reason-' + a.id"
                      />
                    </label>
                    <button
                      type="button"
                      matButton="outlined"
                      [attr.data-test]="'reject-' + a.id"
                      [disabled]="deciding().has(a.id)"
                      (click)="decide(a, 'reject', reason)"
                    >
                      <sc-icon name="close" [size]="16" />Reject
                    </button>
                    <button
                      type="button"
                      matButton="filled"
                      [attr.data-test]="'approve-' + a.id"
                      [disabled]="deciding().has(a.id)"
                      (click)="decide(a, 'approve', reason)"
                    >
                      <sc-icon name="check" [size]="16" />Approve
                    </button>
                  </div>
                </li>
              }
            </ul>
          }
        </section>
      } @else {
        <section id="panel-history" role="tabpanel" aria-labelledby="tab-history" class="panel">
          @if (approved().length > 0) {
            <ul class="actions" aria-label="Approved, waiting to run">
              @for (a of approved(); track a.id) {
                <li class="action" [attr.data-test]="'approved-' + a.id">
                  <div class="chips">
                    <span class="chip">{{ featureName(a.featureId) }}</span>
                    <span class="chip chip--info" [attr.data-test]="'waiting-to-run-' + a.id"
                      ><sc-icon name="check" [size]="14" />Approved, waiting for Scout to run
                      it</span
                    >
                    <span class="requested">{{ whenInWords(a.requestedAt, 'requested') }}</span>
                  </div>
                  <div class="what">
                    <span class="name">{{ actionInWords(a.action, a.summary) }}</span>
                    <span class="target"
                      >Target: <code>{{ a.target }}</code></span
                    >
                  </div>
                </li>
              }
            </ul>
            @if (workerStopped()) {
              <div class="start">
                <p class="note" data-test="worker-stopped">
                  Scout is not running, so nothing approved can run yet.
                </p>
                <button
                  type="button"
                  matButton="filled"
                  data-test="review-start-scout"
                  [disabled]="starting()"
                  (click)="startScout()"
                >
                  <sc-icon name="play" [size]="16" />Start Scout
                </button>
              </div>
            }
          }
          @if (running().length > 0) {
            <ul class="actions" aria-label="Approved and running">
              @for (a of running(); track a.id) {
                <li class="action" [attr.data-test]="'running-' + a.id">
                  <div class="chips">
                    <span class="chip">{{ featureName(a.featureId) }}</span>
                    <span
                      class="chip chip--info"
                      role="status"
                      [attr.data-test]="'executing-' + a.id"
                      ><sc-icon name="refresh" [size]="14" />Running now</span
                    >
                    <span class="requested">{{ whenInWords(a.requestedAt, 'requested') }}</span>
                  </div>
                  <div class="what">
                    <span class="name">{{ actionInWords(a.action, a.summary) }}</span>
                    <span class="target"
                      >Target: <code>{{ a.target }}</code></span
                    >
                  </div>
                </li>
              }
            </ul>
          }
          @if (writes().length === 0) {
            <p class="note" data-test="writes-empty">Scout has not written anything yet.</p>
          } @else {
            <p class="note">The last 50 writes Scout made, newest first.</p>
            <div class="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Time</th>
                    <th>Feature</th>
                    <th>Action</th>
                    <th>Target</th>
                    <th>Outcome</th>
                    <th>Approved by</th>
                    <th>Signed as</th>
                  </tr>
                </thead>
                <tbody>
                  @for (w of writes(); track $index) {
                    <tr data-test="write-row">
                      <td class="quiet">{{ whenInWords(w.at, 'history') }}</td>
                      <td>{{ featureName(w.featureId) }}</td>
                      <td data-test="write-action">{{ actionInWords(w.action) }}</td>
                      <td>
                        <code>{{ w.target }}</code>
                      </td>
                      <td>
                        <span
                          class="chip"
                          [class.chip--ok]="toneOf(w) === 'ok'"
                          [class.chip--warn]="toneOf(w) === 'warn'"
                          [class.chip--error]="toneOf(w) === 'error'"
                          data-test="write-outcome"
                          >{{ outcomeInWords(w.outcome) }}</span
                        >
                      </td>
                      <td class="quiet">{{ approverInWords(w.approvedBy) }}</td>
                      <td class="quiet" data-test="write-signed-as">{{ signedAs(w) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
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
        margin: 0 auto;
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
      .lede,
      .note {
        margin: 0;
        font-size: 14px;
        color: var(--sc-muted);
      }
      .note {
        font-size: 13px;
      }
      button sc-icon {
        margin-right: 6px;
      }
      .tabs {
        display: flex;
        gap: 4px;
        border-bottom: 1px solid var(--sc-line);
      }
      .tab {
        border: 0;
        border-bottom: 2px solid transparent;
        background: transparent;
        color: var(--sc-muted);
        padding: 10px 14px;
        font: inherit;
        font-weight: 500;
        display: flex;
        align-items: center;
        gap: 8px;
        cursor: pointer;
      }
      .tab[aria-selected='true'] {
        color: var(--sc-fg);
        border-bottom-color: var(--sc-accent);
      }
      .count {
        min-width: 20px;
        height: 20px;
        padding: 0 6px;
        box-sizing: border-box;
        border-radius: 10px;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
        font-size: 12px;
        font-weight: 600;
        display: inline-flex;
        align-items: center;
        justify-content: center;
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
        color: var(--sc-on-error-container);
      }
      .alert-text {
        margin-right: auto;
        color: var(--sc-fg);
      }
      .panel,
      .loading {
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .loading mat-progress-bar {
        border-radius: 2px;
      }
      .skeleton {
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        background: var(--sc-surface);
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .bar {
        height: 12px;
        border-radius: 6px;
        background: var(--sc-surface-2);
      }
      .bar--title {
        height: 14px;
        width: 45%;
        border-radius: 7px;
      }
      .bar--line {
        width: 70%;
      }
      .bar--meta {
        width: 30%;
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
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
        display: flex;
        align-items: center;
        justify-content: center;
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
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .chips {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .chip {
        align-self: flex-start;
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
      .chip sc-icon {
        margin-right: 0;
      }
      .chip--warn {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .start {
        display: flex;
        align-items: center;
        gap: 12px;
        flex-wrap: wrap;
      }
      .chip--info {
        padding: 4px 10px;
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
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
      .what {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .name {
        font-weight: 600;
        font-size: 15px;
      }
      .target {
        font-size: 13px;
        color: var(--sc-muted);
      }
      .summary {
        margin: 0;
        color: var(--sc-fg-2);
        line-height: 1.45;
      }
      .decide {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        gap: 10px;
      }
      .reason {
        display: flex;
        flex-direction: column;
        gap: 4px;
        flex: 1 1 260px;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .reason input {
        height: 36px;
        padding: 0 12px;
        border-radius: var(--sc-radius-sm);
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        color: var(--sc-fg);
        font: inherit;
        font-size: 14px;
      }
      code {
        font-family: var(--sc-mono);
        font-size: 13px;
        color: var(--sc-fg-2);
        overflow-wrap: anywhere;
      }
      .table-wrap {
        overflow-x: auto;
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        background: var(--sc-surface);
      }
      table {
        width: 100%;
        min-width: 900px;
        border-collapse: collapse;
        font-size: 13px;
      }
      th,
      td {
        text-align: left;
        padding: 10px 14px;
        border-bottom: 1px solid var(--sc-line);
      }
      th {
        color: var(--sc-muted);
        font-weight: 600;
      }
      th,
      td:first-child {
        white-space: nowrap;
      }
      .quiet {
        color: var(--sc-fg-2);
      }
    `,
  ],
})
export class ReviewComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly notice = inject(NoticeService);
  private timer: ReturnType<typeof setInterval> | null = null;
  private unsubscribe: (() => void) | null = null;

  readonly feature: string | null =
    (inject(ActivatedRoute, { optional: true })?.snapshot.data['feature'] as string | undefined) ??
    null;
  readonly actions = signal<readonly PendingActionDto[]>([]);
  readonly approved = signal<readonly PendingActionDto[]>([]);
  readonly running = signal<readonly PendingActionDto[]>([]);
  readonly writes = signal<readonly WriteLogEntryDto[]>([]);
  readonly error = signal<string | null>(null);
  readonly deciding = signal<ReadonlySet<string>>(new Set());
  readonly worker = signal<WorkerStatus | null>(null);
  readonly starting = signal(false);
  readonly loaded = signal(false);
  readonly tab = signal<ReviewTab>('pending');
  readonly actionInWords = actionInWords;
  readonly outcomeInWords = outcomeInWords;
  readonly approverInWords = approverInWords;
  readonly whenInWords = whenInWords;

  async ngOnInit(): Promise<void> {
    const unsubscribeActions = this.bridge.onActionsChanged(() => void this.load());
    const unsubscribeWorker = this.bridge.onWorkerEvent((event) => {
      if (event.kind === 'state') this.worker.set(event.status);
    });
    this.unsubscribe = () => {
      unsubscribeActions();
      unsubscribeWorker();
    };
    this.bridge.workerStatus().then(
      (status) => this.worker.set(status),
      () => undefined,
    );
    this.timer = setInterval(() => void this.load(), POLL_MS);
    await this.load();
  }

  ngOnDestroy(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.unsubscribe?.();
  }

  featureName(id: string): string {
    return featureName(id);
  }

  /** True once main reported a worker that is neither running nor starting. */
  workerStopped(): boolean {
    const state = this.worker()?.state;
    return state !== undefined && state !== 'running' && state !== 'starting';
  }

  /** Who signed a write: the Scout bot when its login is a bot's, else the owner; a dash when the worker did not say. */
  signedAs(entry: WriteLogEntryDto): string {
    if (entry.signedAs === null) return '-';
    return entry.signedAs.endsWith('[bot]')
      ? `Bot (${entry.signedAs})`
      : `Owner (${entry.signedAs})`;
  }

  /**
   * Starts the worker so the approved actions can run, then reads its state again, because main pushes worker states to the
   * main window only; a start main refuses shows its reason.
   */
  async startScout(): Promise<void> {
    this.starting.set(true);
    try {
      await this.bridge.workerStart();
      this.worker.set(await this.bridge.workerStatus());
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    } finally {
      this.starting.set(false);
    }
  }

  /**
   * The owner's click on a running job reads by how it ended: applied as done, not applied and expired as neutral, not
   * confirmed as a warning. Any other write that failed reads as an error, a rejected one, with or without its reason,
   * as neutral, a write that started and has no record yet as neutral, one Scout stopped before recording as a warning,
   * every other outcome as done.
   */
  toneOf(w: WriteLogEntryDto): 'ok' | 'neutral' | 'warn' | 'error' {
    if (w.controlStatus === 'applied') return 'ok';
    if (w.controlStatus === 'not confirmed') return 'warn';
    if (w.controlStatus !== null) return 'neutral';
    if (w.outcome.startsWith('failed')) return 'error';
    if (w.outcome.startsWith('rejected')) return 'neutral';
    if (w.outcome === 'started') return 'neutral';
    if (w.outcome.startsWith('not confirmed')) return 'warn';
    return 'ok';
  }

  async decide(
    action: PendingActionDto,
    decision: 'approve' | 'reject',
    reasonInput: HTMLInputElement,
  ): Promise<void> {
    const reason = reasonInput.value.trim();
    const req = reason.length > 0 ? { id: action.id, reason } : { id: action.id };
    this.deciding.set(new Set(this.deciding()).add(action.id));
    try {
      if (decision === 'approve') await this.bridge.actionsApprove(req);
      else await this.bridge.actionsReject(req);
      const what = `${actionInWords(action.action, action.summary)} (${action.target})`;
      if (decision === 'approve') this.notice.show(`Approved: ${what}`, 'success', undefined, OK);
      else this.notice.show(`Rejected: ${what}`, 'info', undefined, OK);
      await this.load();
    } catch (err: unknown) {
      await this.load();
      this.error.set(decisionError(err));
    } finally {
      const next = new Set(this.deciding());
      next.delete(action.id);
      this.deciding.set(next);
    }
  }

  async load(): Promise<void> {
    try {
      const [pending, log] = await Promise.all([
        this.bridge.actionsList(),
        this.bridge.writesList({ limit: WRITES_SHOWN }),
      ]);
      const shown = (featureId: string): boolean =>
        this.feature === null || featureId === this.feature;
      const listed = pending.actions.filter((a) => shown(a.featureId));
      this.actions.set(listed.filter((a) => a.status === 'pending'));
      this.approved.set(listed.filter((a) => a.status === 'approved'));
      this.running.set(listed.filter((a) => a.status === 'executing'));
      this.writes.set(
        log.entries
          .filter((w) => shown(w.featureId))
          .sort((a, b) => Date.parse(b.at) - Date.parse(a.at)),
      );
      this.error.set(null);
    } catch (err: unknown) {
      const { code, message } = readIpcError(err);
      this.error.set(
        code === 'control-plane/unavailable'
          ? 'Could not load the approvals: the Scout control plane did not answer. Nothing was approved or rejected.'
          : message,
      );
    } finally {
      this.loaded.set(true);
    }
  }
}
