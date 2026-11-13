import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  Output,
  computed,
  signal,
  viewChild,
} from '@angular/core';

import { CdkTrapFocus } from '@angular/cdk/a11y';
import { MatButtonModule } from '@angular/material/button';

import type { FeatureDto, FeatureSettingFieldDto } from '@scout/shared';

import { scopeLabel } from './feature-details.component';

/** The dialog look shared by the credential dialog and the write confirmation dialog. */
export const DIALOG_STYLES = `
      .backdrop {
        position: fixed;
        inset: 0;
        z-index: 100;
        display: flex;
        align-items: flex-start;
        justify-content: center;
        padding: 48px 16px;
        box-sizing: border-box;
        background: var(--sc-scrim);
        font-size: 14px;
        color: var(--sc-fg);
      }
      .dialog {
        width: 560px;
        max-width: 100%;
        max-height: min(820px, 100%);
        display: flex;
        flex-direction: column;
        box-sizing: border-box;
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        box-shadow: 0 20px 48px var(--sc-shadow);
      }
      header {
        padding: 20px 20px 8px;
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      h2 {
        margin: 0;
        font-size: 18px;
        font-weight: 600;
      }
      .lede {
        margin: 0;
        font-size: 13px;
        color: var(--sc-muted);
      }
      .body {
        padding: 8px 20px 16px;
        display: flex;
        flex-direction: column;
        gap: 16px;
        overflow-y: auto;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .label {
        font-size: 13px;
        font-weight: 500;
        color: var(--sc-fg);
      }
      .helper {
        margin: 0;
        font-size: 12px;
        line-height: 1.45;
        color: var(--sc-muted);
      }
      input[type='text'],
      input[type='password'],
      textarea {
        box-sizing: border-box;
        background: var(--sc-bg);
        border: 1px solid var(--sc-line);
        border-radius: 8px;
        padding: 0 12px;
        color: var(--sc-fg);
        font-family: inherit;
        font-size: 14px;
      }
      input[type='text'],
      input[type='password'] {
        height: 40px;
      }
      textarea {
        padding: 8px 12px;
        font-family: var(--sc-mono);
        font-size: 12px;
        resize: vertical;
      }
      [aria-invalid='true'] {
        border-color: var(--sc-error);
      }
      .link-btn {
        align-self: flex-start;
        font-size: 13px;
        color: var(--sc-accent);
        background: transparent;
      }
      .scope-matrix {
        border: 1px solid var(--sc-line);
        border-radius: 10px;
        padding: 10px 12px;
      }
      .scope-matrix summary {
        cursor: pointer;
        font-size: 13px;
        font-weight: 600;
      }
      .scope-matrix table {
        width: 100%;
        margin-top: 8px;
        border-collapse: collapse;
        font-size: 13px;
      }
      .scope-matrix th,
      .scope-matrix td {
        text-align: left;
        padding: 6px 8px 6px 0;
        vertical-align: top;
      }
      .scope-matrix th {
        color: var(--sc-muted);
        font-weight: 600;
      }
      .scope-matrix td {
        border-top: 1px solid var(--sc-line);
        color: var(--sc-fg-2);
      }
      .scope-matrix td.perm {
        width: 130px;
        font-weight: 500;
        color: var(--sc-fg);
      }
      .scope-matrix ul {
        margin: 0;
        padding-left: 16px;
      }
      .setup-steps {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .setup-steps h3 {
        margin: 0;
        font-size: 13px;
        font-weight: 600;
      }
      .setup-steps ol {
        margin: 0;
        padding-left: 20px;
        display: flex;
        flex-direction: column;
        gap: 4px;
        font-size: 13px;
        line-height: 1.45;
        color: var(--sc-fg-2);
      }
      .form-err {
        margin: 0;
        font-size: 13px;
        color: var(--sc-error);
      }
      footer {
        display: flex;
        justify-content: flex-end;
        gap: 8px;
        padding: 12px 20px 20px;
        border-top: 1px solid var(--sc-line);
        --mat-button-outlined-horizontal-padding: 16px;
      }
      footer svg {
        margin-right: 8px;
        flex: none;
      }
`;

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
  imports: [CdkTrapFocus, MatButtonModule],
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="backdrop"
      role="dialog"
      aria-modal="true"
      [attr.aria-labelledby]="'dlg-title-' + feature.id"
      data-test="credential-dialog"
      (keydown.escape)="cancel.emit()"
    >
      <div class="dialog" cdkTrapFocus>
        <header>
          <h2 [id]="'dlg-title-' + feature.id" data-test="dialog-title">
            Connect {{ feature.displayName }}
          </h2>
          <p class="lede" data-test="dialog-lede">
            Fields marked * are required. Values stay in this computer's protected storage.
          </p>
        </header>

        <div class="body">
          @if (useSchemaForm()) {
            @for (field of settingsFields(); track field.key) {
              <label class="field" [attr.data-test]="'setting-field-' + field.key">
                <span class="label">{{ field.label }}{{ field.required ? ' *' : '' }}</span>

                @switch (field.kind) {
                  @case ('textarea') {
                    <textarea
                      rows="6"
                      [attr.data-test]="'setting-input-' + field.key"
                      [attr.aria-invalid]="emptySetting(field.key, field.required)"
                      [value]="settingValueOf(field.key)"
                      (input)="onSettingInput(field.key, $any($event.target).value)"
                    ></textarea>
                  }
                  @case ('password') {
                    <input
                      type="password"
                      autocomplete="off"
                      [attr.data-test]="'setting-input-' + field.key"
                      [attr.aria-invalid]="emptySetting(field.key, field.required)"
                      [value]="settingValueOf(field.key)"
                      (input)="onSettingInput(field.key, $any($event.target).value)"
                    />
                  }
                  @default {
                    <input
                      type="text"
                      [attr.data-test]="'setting-input-' + field.key"
                      [attr.aria-invalid]="emptySetting(field.key, field.required)"
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
                <span class="label"
                  >{{ field.label }}{{ isRequired(field.label) ? ' *' : '' }}</span
                >

                @switch (field.kind) {
                  @case ('textarea') {
                    <textarea
                      rows="6"
                      [attr.data-test]="'input-' + field.key"
                      [attr.aria-invalid]="emptyCredential(field.key, field.label)"
                      [value]="valueOf(field.key)"
                      (input)="onFieldInput(field.key, $any($event.target).value)"
                    ></textarea>
                  }
                  @case ('password') {
                    <input
                      type="password"
                      autocomplete="off"
                      [attr.data-test]="'input-' + field.key"
                      [attr.aria-invalid]="emptyCredential(field.key, field.label)"
                      [value]="valueOf(field.key)"
                      (input)="onFieldInput(field.key, $any($event.target).value)"
                    />
                  }
                  @default {
                    <input
                      type="text"
                      [attr.data-test]="'input-' + field.key"
                      [attr.aria-invalid]="emptyCredential(field.key, field.label)"
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
              <details class="scope-matrix" data-test="scope-matrix">
                <summary>Required permissions</summary>
                <table>
                  <thead>
                    <tr>
                      <th>Permission</th>
                      <th>What it enables</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (row of feature.scopeMatrix; track row.scope) {
                      <tr [attr.data-test]="'scope-row-' + row.scope">
                        <td class="perm">{{ label(row.scope) }}</td>
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
              </details>
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

          @if (tried() && missing() > 0) {
            <p class="form-err" role="alert" data-test="credential-missing">
              {{
                missing() === 1
                  ? 'One required field is empty. Nothing was saved.'
                  : missing() + ' required fields are empty. Nothing was saved.'
              }}
            </p>
          }
        </div>

        <footer>
          <button
            #cancelButton
            type="button"
            matButton="outlined"
            data-test="credential-cancel"
            (click)="cancel.emit()"
          >
            Cancel
          </button>
          <button
            type="button"
            matButton="filled"
            data-test="credential-submit"
            (click)="onSubmit()"
          >
            <svg
              viewBox="0 0 24 24"
              width="16"
              height="16"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="M5 3h11l3 3v15H5zM8 3v6h8V3M8 21v-7h8v7" /></svg
            >Save
          </button>
        </footer>
      </div>
    </div>
  `,
  styles: [DIALOG_STYLES],
})
export class CredentialDialogComponent implements AfterViewInit {
  private readonly cancelButton = viewChild('cancelButton', {
    read: ElementRef<HTMLButtonElement>,
  });

  /** Cancel takes the focus when the dialog opens, so Enter never saves by accident. */
  ngAfterViewInit(): void {
    this.cancelButton()?.nativeElement.focus();
  }

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

  readonly label = scopeLabel;
  /** True once Save was pressed with a required field empty; the empty fields and the line under them then say so. */
  readonly tried = signal(false);

  /** How many required fields are still empty. */
  readonly missing = computed<number>(() => {
    if (!this.useSchemaForm()) return this.legacyValue().trim().length > 0 ? 0 : 1;
    const values = this._values();
    const settings = this._settingValues();
    const credentials = this.feature.credentialFields.filter(
      (f) => this.isRequired(f.label) && (values[f.key] ?? '').trim() === '',
    ).length;
    const required = this.settingsFields().filter(
      (f) => f.required && (settings[f.key] ?? '').trim() === '',
    ).length;
    return credentials + required;
  });

  isRequired(label: string): boolean {
    return !isOptionalField(label);
  }

  emptyCredential(key: string, label: string): 'true' | null {
    return this.tried() && this.isRequired(label) && (this._values()[key] ?? '').trim() === ''
      ? 'true'
      : null;
  }

  emptySetting(key: string, required: boolean): 'true' | null {
    return this.tried() && required && (this._settingValues()[key] ?? '').trim() === ''
      ? 'true'
      : null;
  }

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
    if (!this.canSubmit()) {
      this.tried.set(true);
      return;
    }
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
