import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  OnInit,
  Output,
  computed,
  inject,
  signal,
} from '@angular/core';

import type { LlmProviderDescriptor, LlmProviderId } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

export interface LlmProviderDialogSubmit {
  readonly id: LlmProviderId;
  readonly values: Readonly<Record<string, string>>;
}

// Schema-driven credential dialog for cloud LLM providers, following the same
// shape as CredentialDialogComponent (features/credential-dialog.component.ts)
// but typed against LlmProviderDescriptor rather than FeatureDto. Non-secret
// fields are pre-filled from existingValues; secret fields always start
// blank — secretPresent drives a "key already set" hint instead of ever
// echoing a stored secret back into the form.
@Component({
  selector: 'sc-llm-provider-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="backdrop"
      role="dialog"
      aria-modal="true"
      [attr.aria-labelledby]="'llm-dlg-title-' + descriptor.id"
      data-test="llm-provider-dialog"
    >
      <div class="dialog">
        <header>
          <h2 [id]="'llm-dlg-title-' + descriptor.id" data-test="llm-dialog-title">
            Connect {{ descriptor.displayName }}
          </h2>
          <a
            class="docs-link"
            target="_blank"
            rel="noopener"
            [attr.href]="descriptor.docsUrl"
            data-test="llm-docs-link"
            (click)="onDocsLinkClick($event)"
          >
            Get an API key
          </a>
        </header>

        @for (field of descriptor.fields; track field.key) {
          <label class="field" [attr.data-test]="'llm-field-' + field.key">
            <span class="label">{{ field.label }}</span>

            @if (field.kind === 'password') {
              <input
                type="password"
                autocomplete="off"
                [attr.data-test]="'llm-input-' + field.key"
                [value]="valueOf(field.key)"
                [placeholder]="field.placeholder ?? ''"
                (input)="onFieldInput(field.key, $any($event.target).value)"
              />
            } @else {
              <input
                type="text"
                [attr.data-test]="'llm-input-' + field.key"
                [value]="valueOf(field.key)"
                [placeholder]="field.placeholder ?? ''"
                (input)="onFieldInput(field.key, $any($event.target).value)"
              />
            }

            @if (field.secret && secretPresent && valueOf(field.key).length === 0) {
              <p class="hint" [attr.data-test]="'llm-secret-hint-' + field.key">
                Key already set. Leave blank to keep it.
              </p>
            }

            @if (field.helperText !== undefined) {
              <p class="helper" [attr.data-test]="'llm-helper-' + field.key">
                {{ field.helperText }}
              </p>
            }
          </label>
        }

        <footer>
          <button
            type="button"
            class="btn btn--secondary"
            data-test="llm-dialog-cancel"
            (click)="onCancel()"
          >
            Cancel
          </button>
          <button
            type="button"
            class="btn btn--primary"
            data-test="llm-dialog-submit"
            [disabled]="!canSubmit()"
            (click)="onSubmit()"
          >
            Save &amp; use
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
        width: min(520px, 92vw);
        max-height: 90vh;
        overflow-y: auto;
        display: flex;
        flex-direction: column;
        gap: 14px;
        color: #e4e8f0;
      }
      header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
      }
      h2 {
        margin: 0;
        font-size: 18px;
      }
      .docs-link {
        font-size: 12px;
        color: #93c5fd;
        text-decoration: none;
        white-space: nowrap;
      }
      .docs-link:hover {
        text-decoration: underline;
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
      .helper,
      .hint {
        margin: 0;
        font-size: 12px;
        color: #7b8299;
      }
      .hint {
        color: #d4a24a;
      }
      input[type='text'],
      input[type='password'] {
        background: #0f1117;
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 8px;
        color: #e4e8f0;
        font-family: inherit;
        font-size: 13px;
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
export class LlmProviderDialogComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);

  @Input({ required: true }) descriptor!: LlmProviderDescriptor;
  // Non-secret field values from a previous save, keyed by field.key.
  @Input() existingValues: Readonly<Record<string, string>> = {};
  // Whether every required secret for this provider is already in the
  // vault. Drives the "key already set" hint and lets required secrets be
  // left blank on edit without failing validation.
  @Input() secretPresent = false;

  @Output() readonly submit = new EventEmitter<LlmProviderDialogSubmit>();
  @Output() readonly cancel = new EventEmitter<void>();

  private readonly _values = signal<Readonly<Record<string, string>>>({});

  readonly canSubmit = computed<boolean>(() => {
    const values = this._values();
    for (const field of this.descriptor.fields) {
      if (!field.required) continue;
      const v = values[field.key];
      const hasValue = v !== undefined && v.trim().length > 0;
      if (hasValue) continue;
      if (field.secret && this.secretPresent) continue;
      return false;
    }
    return true;
  });

  ngOnInit(): void {
    const seed: Record<string, string> = {};
    for (const field of this.descriptor.fields) {
      if (field.secret) continue; // never prefill a secret into the form
      const existing = this.existingValues[field.key];
      if (existing !== undefined) {
        seed[field.key] = existing;
      } else if (field.defaultValue !== undefined) {
        seed[field.key] = field.defaultValue;
      }
    }
    this._values.set(seed);
  }

  valueOf(key: string): string {
    return this._values()[key] ?? '';
  }

  onFieldInput(key: string, next: string): void {
    this._values.set({ ...this._values(), [key]: next });
  }

  onDocsLinkClick(event: MouseEvent): void {
    event.preventDefault();
    void this.bridge.appOpenExternal({ url: this.descriptor.docsUrl }).catch(() => {
      // Renderer cannot do better than hand off to main; window.open below
      // covers packaged builds without the shell-open rewrite.
    });
    window.open(this.descriptor.docsUrl, '_blank', 'noopener');
  }

  onCancel(): void {
    this.cancel.emit();
  }

  onSubmit(): void {
    if (!this.canSubmit()) return;
    const values = this._values();
    const out: Record<string, string> = {};
    for (const field of this.descriptor.fields) {
      const v = values[field.key];
      if (v === undefined) continue;
      const trimmed = v.trim();
      if (trimmed.length === 0) continue;
      out[field.key] = trimmed;
    }
    this.submit.emit({ id: this.descriptor.id, values: out });
  }
}
