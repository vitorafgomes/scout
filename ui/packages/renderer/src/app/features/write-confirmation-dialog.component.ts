import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

import type { FeatureDto, FeatureScopeRowDto } from '@scout/shared';

import { DIALOG_STYLES } from './credential-dialog.component';

export const WRITE_CONFIRMATION_TEXT =
  'Scout will be allowed to perform these writes on your behalf. Actions marked irreversible will still wait for your approval, one by one, on the Review screen.';

/** Lists the writes a feature declares and asks the owner to allow them before the feature is turned on or granted. */
@Component({
  selector: 'sc-write-confirmation-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="backdrop"
      role="dialog"
      aria-modal="true"
      aria-labelledby="write-confirmation-title"
      data-test="write-confirmation-dialog"
    >
      <div class="dialog">
        <header>
          <h2 id="write-confirmation-title">Allow {{ feature.displayName }} to write?</h2>
        </header>

        <section class="scope-matrix">
          <h3>Writes</h3>
          <table>
            <tbody>
              @for (row of writeRows(); track row.scope) {
                <tr [attr.data-test]="'write-row-' + row.scope">
                  <td>
                    <code>{{ row.scope }}</code>
                  </td>
                  <td>
                    <ul>
                      @for (action of row.actions; track action) {
                        <li>{{ action }}</li>
                      }
                    </ul>
                  </td>
                  <td>
                    <span
                      class="access"
                      [class.access--irreversible]="row.access === 'irreversible'"
                    >
                      {{ row.access }}
                    </span>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </section>

        <p class="helper" data-test="write-confirmation-text">{{ text }}</p>

        <footer>
          <button
            type="button"
            class="btn btn--secondary"
            data-test="write-confirmation-cancel"
            (click)="cancel.emit()"
          >
            Cancel
          </button>
          <button
            type="button"
            class="btn btn--primary"
            data-test="write-confirmation-allow"
            (click)="allow.emit()"
          >
            Allow these writes
          </button>
        </footer>
      </div>
    </div>
  `,
  styles: [
    DIALOG_STYLES,
    `
      .helper {
        font-size: 13px;
        color: #e4e8f0;
      }
      .access {
        font-size: 11px;
        text-transform: uppercase;
        color: #fbbf24;
      }
      .access--irreversible {
        color: #f87171;
      }
    `,
  ],
})
export class WriteConfirmationDialogComponent {
  @Input({ required: true }) feature!: FeatureDto;

  @Output() readonly allow = new EventEmitter<void>();
  @Output() readonly cancel = new EventEmitter<void>();

  readonly text = WRITE_CONFIRMATION_TEXT;

  writeRows(): readonly FeatureScopeRowDto[] {
    return (this.feature.scopeMatrix ?? []).filter((row) => row.access !== 'read');
  }
}
