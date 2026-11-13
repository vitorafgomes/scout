import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

import { IconComponent, type IconName } from './icon.component';

/** What a destructive confirmation says: its title, one line on what happens, the label of the action and its glyph (the trash when not given). */
export interface ConfirmDialogData {
  readonly title: string;
  readonly body: string;
  readonly confirmLabel: string;
  readonly confirmIcon?: IconName;
}

let opened = 0;

/** A confirmation for a destructive action, drawn as the artboards' dialog card; Cancel has the focus when it opens, so Enter never confirms by accident. */
@Component({
  selector: 'sc-confirm-dialog',
  standalone: true,
  imports: [IconComponent, MatButtonModule, MatDialogModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="card">
      <h2 [id]="id + '-title'">{{ data.title }}</h2>
      <p class="body" [id]="id + '-body'">{{ data.body }}</p>
      <div class="actions">
        <button
          type="button"
          matButton="outlined"
          class="cancel"
          [mat-dialog-close]="false"
          cdkFocusInitial
          data-test="confirm-cancel"
        >
          Cancel
        </button>
        <button
          type="button"
          matButton="filled"
          class="danger"
          [mat-dialog-close]="true"
          data-test="confirm-ok"
        >
          <sc-icon [name]="data.confirmIcon ?? 'trash'" [size]="16" />{{ data.confirmLabel }}
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
      .cancel {
        --mat-button-outlined-focus-state-layer-opacity: 0;
      }
      .danger {
        --mat-button-filled-container-color: var(--sc-danger);
        --mat-button-filled-label-text-color: var(--sc-on-danger);
      }
      .danger sc-icon {
        margin-right: 8px;
      }
    `,
  ],
})
export class ConfirmDialogComponent {
  readonly data = inject<ConfirmDialogData>(MAT_DIALOG_DATA);
  readonly id = inject(MatDialogRef).id;
}

/** Opens the confirmation and resolves true only when the owner chose the action. */
export async function confirmAction(dialog: MatDialog, data: ConfirmDialogData): Promise<boolean> {
  opened += 1;
  const id = `sc-confirm-${opened}`;
  const ref = dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(
    ConfirmDialogComponent,
    {
      id,
      data,
      role: 'alertdialog',
      restoreFocus: true,
      ariaLabelledBy: `${id}-title`,
      ariaDescribedBy: `${id}-body`,
      panelClass: 'sc-dialog',
      backdropClass: 'sc-dialog-backdrop',
      width: '440px',
      maxWidth: 'calc(100vw - 32px)',
      position: { top: '120px' },
    },
  );
  return (await firstValueFrom(ref.afterClosed())) === true;
}
