import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';

import { IconComponent } from '../core/icon.component';

/**
 * What every setup step shares: the centred 760 px column, the title with its lede, the card, the alert box and the
 * icon beside a button's label.
 */
export const WIZARD_PAGE_STYLES = `
  :host {
    display: block;
    color: var(--sc-fg);
    font-size: 14px;
  }
  .page {
    display: flex;
    flex-direction: column;
    gap: 18px;
    max-width: 760px;
    margin: 0 auto;
    padding: 28px 24px;
  }
  .heading {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  h1 {
    margin: 0;
    font-size: 24px;
    font-weight: 600;
    letter-spacing: -0.01em;
  }
  .lede {
    margin: 0;
    color: var(--sc-fg-2);
    line-height: 1.5;
  }
  .card {
    display: flex;
    flex-direction: column;
    gap: 14px;
    padding: 20px;
    background: var(--sc-surface);
    border: 1px solid var(--sc-line);
    border-radius: 14px;
  }
  .alert {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 12px;
    border-radius: 10px;
    background: var(--sc-error-container);
  }
  .alert-title {
    margin: 0;
    font-weight: 600;
    color: var(--sc-error);
  }
  .alert-detail {
    margin: 0;
    font-size: 13px;
    color: var(--sc-fg-2);
    overflow-wrap: anywhere;
  }
  .muted {
    margin: 0;
    font-size: 13px;
    color: var(--sc-muted);
  }
  button {
    --mat-button-filled-container-color: var(--sc-accent-strong);
    --mat-button-filled-label-text-color: var(--sc-on-accent);
  }
  button sc-icon {
    margin-right: 8px;
    vertical-align: middle;
  }
  .danger {
    --mat-button-outlined-label-text-color: var(--sc-error);
    --mat-button-outlined-outline-color: var(--sc-error);
  }
`;

/**
 * The footer of a setup step: Back on the left, an optional hint, and Continue on the right. While the step
 * cannot continue, Continue is disabled and the reason under it describes the button.
 */
@Component({
  selector: 'sc-wizard-footer',
  standalone: true,
  imports: [IconComponent, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <footer class="footer">
      @if (showBack()) {
        <button
          type="button"
          matButton="outlined"
          data-test="wizard-back"
          [disabled]="backDisabled()"
          (click)="back.emit()"
        >
          <sc-icon class="before" name="back" [size]="16" />Back
        </button>
      }
      <span class="hint" data-test="wizard-hint">{{ hint() }}</span>
      <button
        type="button"
        [matButton]="primary() || blockedReason() !== null ? 'filled' : 'outlined'"
        [class.btn--primary]="primary() && blockedReason() === null"
        [class.btn--secondary]="!primary() && blockedReason() === null"
        [class.btn--blocked]="blockedReason() !== null"
        data-test="wizard-continue"
        [disabled]="blockedReason() !== null"
        [attr.aria-describedby]="blockedReason() === null ? null : 'wizard-blocked'"
        (click)="continue.emit()"
      >
        {{ continueLabel() }}<sc-icon class="after" name="forward" [size]="16" />
      </button>
      @if (blockedReason(); as reason) {
        <span id="wizard-blocked" class="blocked" data-test="wizard-blocked">{{ reason }}</span>
      }
    </footer>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .footer {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        border-top: 1px solid var(--sc-line);
        padding-top: 18px;
        margin-top: 4px;
      }
      .hint {
        margin-right: auto;
        font-size: 12px;
        color: var(--sc-muted);
      }
      button {
        --mat-button-filled-container-color: var(--sc-accent-strong);
        --mat-button-filled-label-text-color: var(--sc-on-accent);
      }
      sc-icon {
        vertical-align: middle;
      }
      .before {
        margin-right: 8px;
      }
      .after {
        margin-left: 8px;
      }
      .btn--blocked {
        --mat-button-filled-disabled-container-color: var(--sc-surface-2);
        --mat-button-filled-disabled-label-text-color: var(--sc-muted);
      }
      .blocked {
        flex-basis: 100%;
        text-align: right;
        font-size: 12px;
        color: var(--sc-muted);
      }
    `,
  ],
})
export class WizardFooterComponent {
  readonly showBack = input<boolean>(true);
  /** True while the step is busy and leaving it would cut a request short. */
  readonly backDisabled = input<boolean>(false);
  readonly hint = input<string>('');
  readonly continueLabel = input<string>('Continue');
  /** Whether Continue is the step's main action; when it is not, it is drawn as a secondary button. */
  readonly primary = input<boolean>(true);
  /** Why the step cannot continue yet; null while it can. */
  readonly blockedReason = input<string | null>(null);

  readonly back = output<void>();
  readonly continue = output<void>();
}
