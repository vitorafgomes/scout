import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { readIpcError, type McpPendingStart } from '@scout/shared';

import { McpStore } from '../core/mcp.store';

/** A pull request URL as the dialog names it, `owner/repo#number`; any other text as it is. */
export function pullRequestName(url: string): string {
  const named = /^https:\/\/github\.com\/([^/]+)\/([^/]+)\/pull\/(\d+)/.exec(url);
  return named === null ? url : `${named[1]}/${named[2]}#${named[3]}`;
}

/**
 * The start an assistant asked for, answered only by Refuse or Start the review: Refuse has the focus, Escape and a click
 * outside do nothing, each button answers once and a refused answer says why here and leaves the dialog open.
 */
@Component({
  selector: 'sc-mcp-start-dialog',
  standalone: true,
  imports: [MatButtonModule, MatDialogModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="card" data-test="mcp-start-dialog">
      <h2 id="mcp-ask-title">An assistant asks to start a review</h2>
      <p class="body" id="mcp-ask-body">
        Review of <code data-test="mcp-start-pr">{{ name }}</code
        >. Scout reads GitHub and uses your AI Source; it writes nothing on GitHub.
      </p>
      <p class="body">
        Follow it in the bell. The request waits 2 minutes for your answer, then it is refused.
      </p>
      @if (error(); as message) {
        <p class="error" role="alert" data-test="mcp-start-error">{{ message }}</p>
      }
      <div class="actions">
        <button
          type="button"
          matButton="outlined"
          cdkFocusInitial
          data-test="mcp-start-refuse"
          [disabled]="busy()"
          (click)="answer(false)"
        >
          Refuse
        </button>
        <button
          type="button"
          matButton="filled"
          data-test="mcp-start-start"
          [disabled]="busy()"
          (click)="answer(true)"
        >
          Start the review
        </button>
      </div>
    </div>
  `,
  styles: [
    `
      .card {
        padding: 20px;
        display: flex;
        flex-direction: column;
        gap: 12px;
        color: var(--sc-fg);
        font-size: 14px;
      }
      h2 {
        margin: 0;
        font-size: 17px;
        font-weight: 600;
      }
      .body {
        margin: 0;
        color: var(--sc-fg-2);
        line-height: 1.5;
      }
      code {
        padding: 2px 6px;
        border-radius: 6px;
        background: var(--sc-surface-2);
        color: var(--sc-fg);
        overflow-wrap: anywhere;
      }
      .error {
        margin: 0;
        color: var(--sc-error);
        font-size: 13px;
      }
      .actions {
        display: flex;
        justify-content: flex-end;
        gap: 8px;
        margin-top: 4px;
        --mat-button-outlined-container-height: 36px;
        --mat-button-outlined-horizontal-padding: 16px;
        --mat-button-outlined-label-text-weight: 400;
        --mat-button-outlined-label-text-size: 14px;
        --mat-button-outlined-label-text-tracking: 0;
        --mat-button-filled-container-height: 36px;
        --mat-button-filled-horizontal-padding: 16px;
        --mat-button-filled-label-text-weight: 400;
        --mat-button-filled-label-text-size: 14px;
        --mat-button-filled-label-text-tracking: 0;
      }
    `,
  ],
})
export class McpStartDialogComponent {
  private readonly pending = inject<McpPendingStart>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef);
  private readonly mcp = inject(McpStore);

  readonly name = pullRequestName(this.pending.pullRequest);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  /** Sends the owner's answer once; the dialog leaves when main took it, or says why it did not. */
  async answer(start: boolean): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.mcp.answerStart(this.pending.id, start);
      this.ref.close();
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
      this.busy.set(false);
    }
  }
}

/** Opens the dialog for `pending`, as the renderer's confirmations open, with no way out but its two buttons. */
export function openMcpStartDialog(
  dialog: MatDialog,
  pending: McpPendingStart,
): MatDialogRef<McpStartDialogComponent> {
  return dialog.open(McpStartDialogComponent, {
    data: pending,
    disableClose: true,
    ariaModal: true,
    restoreFocus: true,
    ariaLabelledBy: 'mcp-ask-title',
    ariaDescribedBy: 'mcp-ask-body',
    panelClass: 'sc-dialog',
    backdropClass: 'sc-dialog-backdrop',
    maxWidth: 'calc(100vw - 32px)',
    width: '480px',
  });
}
