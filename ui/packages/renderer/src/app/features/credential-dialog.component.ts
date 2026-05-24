import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
  signal,
} from '@angular/core';

import type { FeatureId } from '@scout/shared';

// Phase 1 stub: per-feature schemas land in Phase 2 (GitHub PAT) and Phase 3
// (kubeconfig). For now, a single textarea captures opaque JSON the worker
// stores verbatim under the "raw" key.
@Component({
  selector: 'sc-credential-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="backdrop"
      role="dialog"
      aria-modal="true"
      [attr.aria-labelledby]="'dlg-title-' + featureId"
      data-test="credential-dialog"
    >
      <div class="dialog">
        <header>
          <h2 [id]="'dlg-title-' + featureId" data-test="dialog-title">
            {{ displayName }} credentials
          </h2>
        </header>
        <label class="field">
          <span class="label">Credentials JSON</span>
          <textarea
            rows="8"
            data-test="credential-textarea"
            [value]="value()"
            (input)="onInput($any($event.target).value)"
          ></textarea>
        </label>
        <footer>
          <button
            type="button"
            class="btn btn--secondary"
            data-test="credential-cancel"
            (click)="cancel.emit()"
          >
            Cancel
          </button>
          <button
            type="button"
            class="btn btn--primary"
            data-test="credential-submit"
            [disabled]="value().trim().length === 0"
            (click)="onSubmit()"
          >
            Save
          </button>
        </footer>
      </div>
    </div>
  `,
  styles: [
    `
      .backdrop {
        position: fixed;
        inset: 0;
        background: rgba(0, 0, 0, 0.6);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 100;
      }
      .dialog {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 20px;
        width: min(520px, 90vw);
        display: flex;
        flex-direction: column;
        gap: 16px;
        color: #e4e8f0;
      }
      h2 {
        margin: 0;
        font-size: 18px;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .label {
        font-size: 13px;
        color: #7b8299;
      }
      textarea {
        background: #0f1117;
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 8px;
        color: #e4e8f0;
        font-family: 'JetBrains Mono', ui-monospace, monospace;
        font-size: 12px;
        resize: vertical;
      }
      footer {
        display: flex;
        justify-content: flex-end;
        gap: 8px;
      }
      .btn {
        font-size: 13px;
        padding: 6px 14px;
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
export class CredentialDialogComponent {
  @Input({ required: true }) featureId!: FeatureId;
  @Input({ required: true }) displayName!: string;

  @Output() readonly submit = new EventEmitter<Record<string, string>>();
  @Output() readonly cancel = new EventEmitter<void>();

  readonly value = signal<string>('');

  onInput(next: string): void {
    this.value.set(next);
  }

  onSubmit(): void {
    const raw = this.value().trim();
    if (raw.length === 0) return;
    this.submit.emit({ raw });
  }
}
