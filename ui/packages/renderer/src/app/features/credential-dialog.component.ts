import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
  computed,
  signal,
} from '@angular/core';

import type { FeatureDto, FeatureSettingFieldDto } from '@scout/shared';

export interface CredentialDialogSubmit {
  readonly credentials: Record<string, string>;
  readonly settings: Record<string, string>;
}

// Schema-driven credential dialog. The shape of the form is driven entirely by
// `feature.credentialFields` and `feature.scopeMatrix` returned by the worker.
//
// Backwards compatibility: when `credentialFields` is missing or empty (a
// worker that has not yet been redeployed with the Phase 2 schema), the
// component falls back to the legacy single-textarea behaviour and emits
// `{ raw: <text> }`. Drop that path once the worker rollout is complete.
@Component({
  selector: 'sc-credential-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="backdrop"
      role="dialog"
      aria-modal="true"
      [attr.aria-labelledby]="'dlg-title-' + feature.id"
      data-test="credential-dialog"
    >
      <div class="dialog">
        <header>
          <h2 [id]="'dlg-title-' + feature.id" data-test="dialog-title">
            Connect {{ feature.displayName }}
          </h2>
        </header>

        @if (useSchemaForm()) {
          @for (field of settingsFields(); track field.key) {
            <label class="field" [attr.data-test]="'setting-field-' + field.key">
              <span class="label">{{ field.label }}</span>

              @switch (field.kind) {
                @case ('textarea') {
                  <textarea
                    rows="6"
                    [attr.data-test]="'setting-input-' + field.key"
                    [value]="settingValueOf(field.key)"
                    (input)="onSettingInput(field.key, $any($event.target).value)"
                  ></textarea>
                }
                @case ('password') {
                  <input
                    type="password"
                    autocomplete="off"
                    [attr.data-test]="'setting-input-' + field.key"
                    [value]="settingValueOf(field.key)"
                    (input)="onSettingInput(field.key, $any($event.target).value)"
                  />
                }
                @default {
                  <input
                    type="text"
                    [attr.data-test]="'setting-input-' + field.key"
                    [value]="settingValueOf(field.key)"
                    (input)="onSettingInput(field.key, $any($event.target).value)"
                  />
                }
              }

              @if (field.helperText !== null) {
                <p class="helper" [attr.data-test]="'setting-helper-' + field.key">
                  {{ field.helperText }}
                </p>
              }

              @if (field.createLinkUrl !== null) {
                <a
                  class="link-btn"
                  target="_blank"
                  rel="noopener"
                  [attr.href]="field.createLinkUrl"
                  [attr.data-test]="'setting-link-' + field.key"
                  (click)="onCreateLinkClick(field.createLinkUrl, $event)"
                >
                  {{ field.createLinkLabel ?? 'Open' }}
                </a>
              }
            </label>
          }

          @for (field of feature.credentialFields; track field.key) {
            <label class="field" [attr.data-test]="'field-' + field.key">
              <span class="label">{{ field.label }}</span>

              @switch (field.kind) {
                @case ('textarea') {
                  <textarea
                    rows="6"
                    [attr.data-test]="'input-' + field.key"
                    [value]="valueOf(field.key)"
                    (input)="onFieldInput(field.key, $any($event.target).value)"
                  ></textarea>
                }
                @case ('password') {
                  <input
                    type="password"
                    autocomplete="off"
                    [attr.data-test]="'input-' + field.key"
                    [value]="valueOf(field.key)"
                    (input)="onFieldInput(field.key, $any($event.target).value)"
                  />
                }
                @default {
                  <input
                    type="text"
                    [attr.data-test]="'input-' + field.key"
                    [value]="valueOf(field.key)"
                    (input)="onFieldInput(field.key, $any($event.target).value)"
                  />
                }
              }

              @if (field.helperText !== null) {
                <p class="helper" [attr.data-test]="'helper-' + field.key">
                  {{ field.helperText }}
                </p>
              }

              @if (field.createLinkUrl !== null) {
                <a
                  class="link-btn"
                  target="_blank"
                  rel="noopener"
                  [attr.href]="field.createLinkUrl"
                  [attr.data-test]="'link-' + field.key"
                  (click)="onCreateLinkClick(field.createLinkUrl, $event)"
                >
                  {{ field.createLinkLabel ?? 'Open' }}
                </a>
              }
            </label>
          }

          @if (feature.scopeMatrix !== null && feature.scopeMatrix.length > 0) {
            <section class="scope-matrix" data-test="scope-matrix">
              <h3>Required permissions</h3>
              <table>
                <thead>
                  <tr>
                    <th>Scope</th>
                    <th>What it enables</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of feature.scopeMatrix; track row.scope) {
                    <tr [attr.data-test]="'scope-row-' + row.scope">
                      <td><code>{{ row.scope }}</code></td>
                      <td>
                        <ul>
                          @for (action of row.actions; track action) {
                            <li>{{ action }}</li>
                          }
                        </ul>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </section>
          }

          @if (feature.setupSteps !== null && feature.setupSteps.length > 0) {
            <section class="setup-steps">
              <h3>How to get this credential</h3>
              <ol data-test="setup-steps">
                @for (step of feature.setupSteps; track $index) {
                  <li>{{ step }}</li>
                }
              </ol>
            </section>
          }
        } @else {
          <label class="field">
            <span class="label">Credentials JSON</span>
            <textarea
              rows="8"
              data-test="credential-textarea"
              [value]="legacyValue()"
              (input)="onLegacyInput($any($event.target).value)"
            ></textarea>
          </label>
        }

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
            [disabled]="!canSubmit()"
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
        width: min(600px, 92vw);
        max-height: 90vh;
        overflow-y: auto;
        display: flex;
        flex-direction: column;
        gap: 14px;
        color: #e4e8f0;
      }
      h2 {
        margin: 0;
        font-size: 18px;
      }
      h3 {
        margin: 0 0 8px;
        font-size: 13px;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: #7b8299;
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
      .helper {
        margin: 0;
        font-size: 12px;
        color: #7b8299;
      }
      input[type='text'],
      input[type='password'],
      textarea {
        background: #0f1117;
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 8px;
        color: #e4e8f0;
        font-family: inherit;
        font-size: 13px;
      }
      textarea {
        font-family: 'JetBrains Mono', ui-monospace, monospace;
        font-size: 12px;
        resize: vertical;
      }
      .link-btn {
        align-self: flex-start;
        font-size: 12px;
        padding: 4px 10px;
        border-radius: 4px;
        border: 1px solid #252b3b;
        color: #93c5fd;
        text-decoration: none;
        background: transparent;
      }
      .link-btn:hover {
        background: #1e2333;
      }
      .scope-matrix {
        border-top: 1px solid #252b3b;
        padding-top: 12px;
      }
      .scope-matrix table {
        width: 100%;
        border-collapse: collapse;
        font-size: 12px;
      }
      .scope-matrix th,
      .scope-matrix td {
        text-align: left;
        padding: 6px 8px;
        border-bottom: 1px solid #1e2333;
        vertical-align: top;
      }
      .scope-matrix th {
        color: #7b8299;
        font-weight: 500;
      }
      .scope-matrix code {
        font-family: 'JetBrains Mono', ui-monospace, monospace;
        color: #fbbf24;
        font-size: 11px;
      }
      .scope-matrix ul {
        margin: 0;
        padding-left: 16px;
      }
      .setup-steps {
        border-top: 1px solid #252b3b;
        padding-top: 12px;
      }
      .setup-steps ol {
        margin: 0;
        padding-left: 20px;
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: 12px;
        color: #e4e8f0;
      }
      .setup-steps li {
        line-height: 1.45;
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
  @Input({ required: true }) feature!: FeatureDto;

  @Output() readonly submit = new EventEmitter<CredentialDialogSubmit>();
  @Output() readonly cancel = new EventEmitter<void>();

  // Schema form state: one entry per credential field key.
  private readonly _values = signal<Readonly<Record<string, string>>>({});

  // Separate state for settings field values; keys never collide with credentials.
  private readonly _settingValues = signal<Readonly<Record<string, string>>>({});

  // Legacy single-textarea fallback (when worker has no schema yet).
  readonly legacyValue = signal<string>('');

  readonly useSchemaForm = computed<boolean>(() => {
    const fields = this.feature?.credentialFields;
    return Array.isArray(fields) && fields.length > 0;
  });

  // Guard against a stale worker that predates settingsFields on the DTO.
  readonly settingsFields = computed<readonly FeatureSettingFieldDto[]>(() => {
    return this.feature?.settingsFields ?? [];
  });

  readonly canSubmit = computed<boolean>(() => {
    if (!this.useSchemaForm()) {
      return this.legacyValue().trim().length > 0;
    }
    const values = this._values();
    for (const field of this.feature.credentialFields) {
      if (isOptionalField(field.label)) continue;
      const v = values[field.key];
      if (v === undefined || v.trim().length === 0) return false;
    }
    const settingValues = this._settingValues();
    for (const field of this.settingsFields()) {
      if (!field.required) continue;
      const v = settingValues[field.key];
      if (v === undefined || v.trim().length === 0) return false;
    }
    return true;
  });

  valueOf(key: string): string {
    return this._values()[key] ?? '';
  }

  onFieldInput(key: string, next: string): void {
    this._values.set({ ...this._values(), [key]: next });
  }

  settingValueOf(key: string): string {
    return this._settingValues()[key] ?? '';
  }

  onSettingInput(key: string, next: string): void {
    this._settingValues.set({ ...this._settingValues(), [key]: next });
  }

  onLegacyInput(next: string): void {
    this.legacyValue.set(next);
  }

  onCreateLinkClick(url: string, event: MouseEvent): void {
    // Renderer is wired so that target="_blank" links are routed through
    // shell.openExternal by main. If that wiring is absent, the anchor still
    // works as a plain link. We call window.open as an explicit fallback so
    // packaged builds without the rewrite still escape the renderer.
    event.preventDefault();
    window.open(url, '_blank', 'noopener');
  }

  onSubmit(): void {
    if (!this.canSubmit()) return;
    if (!this.useSchemaForm()) {
      this.submit.emit({ credentials: { raw: this.legacyValue().trim() }, settings: {} });
      return;
    }
    const values = this._values();
    const credentials: Record<string, string> = {};
    for (const field of this.feature.credentialFields) {
      const v = values[field.key];
      if (v === undefined) continue;
      const trimmed = v.trim();
      if (trimmed.length === 0) continue;
      credentials[field.key] = trimmed;
    }
    const settingValues = this._settingValues();
    const settings: Record<string, string> = {};
    for (const field of this.settingsFields()) {
      const v = settingValues[field.key];
      if (v === undefined) continue;
      const trimmed = v.trim();
      if (trimmed.length === 0) continue;
      settings[field.key] = trimmed;
    }
    this.submit.emit({ credentials, settings });
  }
}

// Convention: labels ending with "(optional)" are not required. Used by the
// kubernetes context field; avoids needing a separate required flag on the DTO.
function isOptionalField(label: string): boolean {
  return /\(optional\)\s*$/i.test(label);
}
