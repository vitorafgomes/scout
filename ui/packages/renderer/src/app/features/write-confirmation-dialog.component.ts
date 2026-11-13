import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EventEmitter,
  Injector,
  Input,
  OnChanges,
  Output,
  afterNextRender,
  inject,
  signal,
  viewChild,
} from '@angular/core';

import { CdkTrapFocus } from '@angular/cdk/a11y';
import { MatButtonModule } from '@angular/material/button';

import type { FeatureDto, FeatureScopeRowDto } from '@scout/shared';

import { scopeLabel } from './feature-details.component';

export const WRITE_CONFIRMATION_TEXT =
  'Scout will be allowed to perform these writes on your behalf. Actions marked irreversible will still wait for your approval, one by one, in Approvals.';

/** Lists the writes a feature declares and asks the owner to allow them before the feature is turned on or granted. */
@Component({
  selector: 'sc-write-confirmation-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CdkTrapFocus, MatButtonModule],
  template: `
    <div class="scrim">
      <div
        class="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="write-confirmation-title"
        aria-describedby="write-confirmation-text"
        data-test="write-confirmation-dialog"
        cdkTrapFocus
        (keydown.escape)="onEscape($event)"
      >
        <h2 id="write-confirmation-title" class="title">
          Allow {{ feature.displayName }} to write?
        </h2>

        <div
          #list
          class="body"
          tabindex="0"
          role="region"
          aria-label="The writes to allow"
          data-test="write-confirmation-list"
          (scroll)="checkSeen()"
        >
          <h3 class="subtitle">Writes</h3>
          <ul class="rows">
            @for (row of writeRows(); track row.scope) {
              <li class="row" [attr.data-test]="'write-row-' + row.scope">
                <span class="head"
                  ><span class="label">{{ label(row.scope) }}</span
                  ><span
                    class="access"
                    [class.access--irreversible]="row.access === 'irreversible'"
                    >{{ row.access === 'irreversible' ? 'Cannot be undone' : 'Write' }}</span
                  ></span
                >
                <ul class="what">
                  @for (action of row.actions; track $index) {
                    <li>{{ action }}</li>
                  }
                </ul>
              </li>
            }
          </ul>
          <p
            id="write-confirmation-text"
            class="helper"
            data-test="write-confirmation-text"
            [textContent]="text"
          ></p>
        </div>

        @if (!seenAll()) {
          <p
            id="write-confirmation-scroll-hint"
            class="hint"
            data-test="write-confirmation-scroll-hint"
          >
            Scroll to the end of the list to allow these writes.
          </p>
        }
        <footer class="foot">
          <button
            type="button"
            #cancelButton
            matButton="outlined"
            data-test="write-confirmation-cancel"
            (click)="cancel.emit()"
          >
            <svg
              viewBox="0 0 24 24"
              width="16"
              height="16"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              aria-hidden="true"
            >
              <path d="M6 6l12 12M18 6L6 18" /></svg
            >Cancel
          </button>
          <button
            type="button"
            matButton="filled"
            data-test="write-confirmation-allow"
            aria-describedby="write-confirmation-scroll-hint"
            [disabled]="!seenAll()"
            (click)="allow.emit()"
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
              <path d="M20 6L9 17l-5-5" /></svg
            >Allow these writes
          </button>
        </footer>
      </div>
    </div>
  `,
  styles: [
    `
      .scrim {
        position: fixed;
        inset: 0;
        z-index: 100;
        display: flex;
        align-items: flex-start;
        justify-content: center;
        padding: 64px 16px 16px;
        box-sizing: border-box;
        background: var(--sc-scrim);
        font-size: 14px;
        color: var(--sc-fg);
      }
      .dialog {
        width: 520px;
        max-width: 100%;
        max-height: min(760px, 100%);
        display: flex;
        flex-direction: column;
        box-sizing: border-box;
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        box-shadow: 0 20px 48px var(--sc-shadow);
      }
      .title {
        margin: 0;
        padding: 20px 20px 8px;
        font-size: 17px;
        font-weight: 600;
      }
      .body {
        padding: 4px 20px 12px;
        overflow-y: auto;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .subtitle {
        margin: 0;
        font-size: 13px;
        font-weight: 600;
      }
      .rows {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
      }
      .row {
        display: flex;
        flex-direction: column;
        gap: 3px;
        padding: 8px 0;
        border-top: 1px solid var(--sc-line);
        font-size: 13px;
      }
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .label {
        font-weight: 500;
      }
      .what {
        margin: 0;
        padding: 0;
        list-style: none;
        display: flex;
        flex-direction: column;
        gap: 4px;
        color: var(--sc-muted);
      }
      .access {
        padding: 1px 8px;
        border-radius: 10px;
        font-size: 11px;
        font-weight: 600;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .access--irreversible {
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
      }
      .helper {
        margin: 0;
        font-size: 13px;
        line-height: 1.5;
        color: var(--sc-fg-2);
      }
      .hint {
        margin: 0;
        padding: 8px 20px 0;
        border-top: 1px solid var(--sc-line);
        font-size: 12px;
        color: var(--sc-muted);
      }
      .body:focus-visible {
        outline: 2px solid var(--sc-focus);
        outline-offset: -2px;
      }
      .foot {
        display: flex;
        flex-wrap: wrap;
        justify-content: flex-end;
        gap: 8px;
        padding: 12px 20px 20px;
        border-top: 1px solid var(--sc-line);
        --mat-button-outlined-horizontal-padding: 16px;
      }
      .foot svg {
        margin-right: 8px;
        flex: none;
      }
    `,
  ],
})
export class WriteConfirmationDialogComponent implements AfterViewInit, OnChanges {
  @Input({ required: true }) feature!: FeatureDto;

  @Output() readonly allow = new EventEmitter<void>();
  @Output() readonly cancel = new EventEmitter<void>();

  readonly text = WRITE_CONFIRMATION_TEXT;
  readonly label = scopeLabel;
  private readonly cancelButton = viewChild('cancelButton', {
    read: ElementRef<HTMLButtonElement>,
  });
  private readonly list = viewChild('list', { read: ElementRef<HTMLElement> });
  private readonly injector = inject(Injector);

  /** True once every write row has been in view: the list fits, or it was scrolled to its end. */
  readonly seenAll = signal(false);

  /** A change of the rows asks for the end of the list to be seen again. */
  ngOnChanges(): void {
    this.seenAll.set(false);
    afterNextRender(() => this.checkSeen(), { injector: this.injector });
  }

  /** Cancel takes the focus when the dialog opens, so Enter never allows writes by accident. */
  ngAfterViewInit(): void {
    this.cancelButton()?.nativeElement.focus();
    this.checkSeen();
  }

  /** Marks the list as seen when its last row is in view. */
  checkSeen(): void {
    const el = this.list()?.nativeElement;
    if (el === undefined) return;
    if (el.scrollTop + el.clientHeight >= el.scrollHeight - 1) this.seenAll.set(true);
  }

  /** Esc cancels, as Cancel does; it never allows. */
  onEscape(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.cancel.emit();
  }

  writeRows(): readonly FeatureScopeRowDto[] {
    return (this.feature.scopeMatrix ?? []).filter((row) => row.access !== 'read');
  }
}
