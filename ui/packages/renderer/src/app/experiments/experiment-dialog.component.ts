import { ChangeDetectionStrategy, Component, inject, type Signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';

import { IconComponent, type IconName } from '../core/icon.component';

export interface ApprovedAction {
  readonly text: string;
  readonly scope: string;
}

export interface ExperimentDialogData {
  readonly test: 'experiment-start' | 'experiment-adopt';
  readonly title: string;
  readonly body: string;
  readonly actions: readonly ApprovedAction[];
  readonly confirmLabel: string;
  readonly confirmIcon: IconName;
  readonly busy: Signal<boolean>;
  readonly error: Signal<string | null>;
  readonly confirm: () => Promise<boolean>;
}

@Component({
  selector: 'sc-experiment-dialog',
  standalone: true,
  imports: [IconComponent, MatButtonModule, MatDialogModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="card" [attr.data-test]="data.test + '-dialog'">
      <h2 [id]="id + '-title'">{{ data.title }}</h2>
      <p class="body" [id]="id + '-body'">{{ data.body }}</p>
      <ol [attr.data-test]="data.test + '-actions'">
        @for (a of data.actions; track $index) {
          <li>
            {{ a.text }} <span class="scope">{{ a.scope }}</span>
          </li>
        }
      </ol>
      @if (data.error(); as message) {
        <p class="err" role="alert" data-test="experiment-error">{{ message }}</p>
      }
      <div class="actions">
        <button
          type="button"
          matButton="outlined"
          [mat-dialog-close]="false"
          cdkFocusInitial
          data-test="experiment-dialog-cancel"
        >
          Cancel
        </button>
        <button
          type="button"
          matButton="filled"
          [attr.data-test]="data.test + '-confirm'"
          [disabled]="data.busy()"
          (click)="confirm()"
        >
          <sc-icon [name]="data.confirmIcon" [size]="16" />{{ data.confirmLabel }}
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
        overflow-wrap: anywhere;
      }
      .body {
        margin: 0;
        color: var(--sc-fg-2);
        line-height: 1.5;
      }
      ol {
        margin: 0;
        padding-left: 20px;
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: 13px;
        line-height: 1.45;
        overflow-wrap: anywhere;
      }
      .scope {
        color: var(--sc-muted);
        font-family: var(--sc-mono);
        font-size: 12px;
      }
      .err {
        margin: 0;
        color: var(--sc-error);
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        justify-content: flex-end;
        gap: 10px;
        --mat-button-outlined-container-height: 40px;
        --mat-button-outlined-horizontal-padding: 16px;
        --mat-button-outlined-label-text-weight: 400;
        --mat-button-outlined-label-text-size: 14px;
        --mat-button-outlined-label-text-tracking: 0;
        --mat-button-filled-container-height: 40px;
        --mat-button-filled-horizontal-padding: 16px;
        --mat-button-filled-label-text-weight: 400;
        --mat-button-filled-label-text-size: 14px;
        --mat-button-filled-label-text-tracking: 0;
        --mat-button-filled-disabled-container-color: var(--sc-surface-2);
        --mat-button-filled-disabled-label-text-color: var(--sc-muted);
      }
      .actions sc-icon {
        margin-right: 8px;
      }
    `,
  ],
})
export class ExperimentDialogComponent {
  readonly data = inject<ExperimentDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<ExperimentDialogComponent, boolean>>(MatDialogRef);
  readonly id = this.ref.id;

  async confirm(): Promise<void> {
    if (await this.data.confirm()) this.ref.close(true);
  }
}
