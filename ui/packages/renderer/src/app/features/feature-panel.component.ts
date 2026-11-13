import { CdkTrapFocus } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterRenderEffect,
  effect,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';

import { CHIP_GLYPHS, type CardChip } from './feature-card.component';
import { FeatureLogoComponent } from './feature-logo.component';

/**
 * The side panel a feature's Details opens: it slides in from the right over a scrim, holds the focus while open, and
 * closes on its close button, on Esc, or on a click on the scrim. The sections it shows stay mounted while it is closed.
 * While it is open the page behind it does not scroll.
 */
@Component({
  selector: 'sc-feature-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CdkTrapFocus, FeatureLogoComponent, MatButtonModule],
  template: `
    <div
      class="scrim"
      aria-hidden="true"
      data-test="feature-panel-scrim"
      [hidden]="!open()"
      (click)="closed.emit()"
    ></div>
    <aside
      class="panel"
      role="dialog"
      aria-modal="true"
      aria-labelledby="feature-panel-title"
      aria-describedby="feature-panel-desc"
      data-test="feature-panel"
      [hidden]="!open()"
      [cdkTrapFocus]="open()"
      (keydown.escape)="onEscape($event)"
    >
      <header class="head">
        @if (logo(); as id) {
          <sc-feature-logo [feature]="id" />
        }
        <div class="title-block">
          <h2 id="feature-panel-title" class="title" data-test="feature-panel-title">
            {{ name() }}
          </h2>
          @if (chip(); as c) {
            <span
              class="badge"
              [class.badge--ok]="c.tone === 'ok'"
              [class.badge--warn]="c.tone === 'warn'"
              [class.badge--err]="c.tone === 'err'"
              data-test="feature-panel-chip"
              ><svg
                viewBox="0 0 24 24"
                width="13"
                height="13"
                fill="none"
                stroke="currentColor"
                stroke-width="2.4"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path [attr.d]="glyphs[c.tone]" /></svg
              >{{ c.label }}</span
            >
          }
        </div>
        <button
          #closeButton
          type="button"
          matIconButton
          class="close"
          title="Close (Esc)"
          data-test="feature-panel-close"
          [attr.aria-label]="'Close the ' + name() + ' details'"
          (click)="closed.emit()"
        >
          <svg
            viewBox="0 0 24 24"
            width="18"
            height="18"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            aria-hidden="true"
          >
            <path d="M6 6l12 12M18 6L6 18" />
          </svg>
        </button>
      </header>
      <div class="body">
        <p id="feature-panel-desc" class="desc" data-test="feature-panel-desc">
          {{ description() }}
        </p>
        <ng-content />
      </div>
    </aside>
  `,
  styles: [
    `
      :host {
        display: contents;
      }
      .scrim {
        position: fixed;
        inset: 0;
        background: var(--sc-scrim);
        z-index: 90;
      }
      .panel {
        position: fixed;
        top: 0;
        right: 0;
        height: 100vh;
        width: 560px;
        max-width: 100%;
        z-index: 91;
        display: flex;
        flex-direction: column;
        box-sizing: border-box;
        background: var(--sc-surface);
        color: var(--sc-fg);
        border-left: 1px solid var(--sc-line);
        box-shadow: -16px 0 40px var(--sc-shadow);
        animation: sc-panel-in 0.2s ease-out;
      }
      .scrim[hidden],
      .panel[hidden] {
        display: none;
      }
      @keyframes sc-panel-in {
        from {
          transform: translateX(100%);
        }
        to {
          transform: translateX(0);
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .panel {
          animation: none;
        }
      }
      .head {
        display: flex;
        align-items: flex-start;
        gap: 12px;
        padding: 16px 16px 12px 20px;
        border-bottom: 1px solid var(--sc-line);
      }
      .title-block {
        flex: 1;
        min-width: 0;
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: 4px;
      }
      .title {
        margin: 0;
        font-size: 18px;
        font-weight: 600;
      }
      .close {
        flex: 0 0 36px;
        --mat-icon-button-state-layer-size: 36px;
        --mat-icon-button-icon-color: var(--sc-fg);
        border: 1px solid var(--sc-line);
        box-sizing: border-box;
      }
      .badge {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 2px 10px;
        border-radius: 12px;
        font-size: 12px;
        font-weight: 500;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .badge--ok {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .badge--warn {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .badge--err {
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
      }
      .body {
        flex: 1;
        min-height: 0;
        overflow-y: auto;
        padding: 16px 20px 20px;
        display: flex;
        flex-direction: column;
        gap: 14px;
      }
      .desc {
        margin: 0;
        font-size: 13px;
        line-height: 1.5;
        color: var(--sc-fg-2);
      }
    `,
  ],
})
export class FeaturePanelComponent {
  readonly open = input(false);
  /** The feature whose mark the header shows. */
  readonly logo = input('');
  readonly name = input('');
  readonly chip = input<CardChip | null>(null);
  readonly description = input('');

  readonly closed = output<void>();
  readonly glyphs = CHIP_GLYPHS;

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private locked: { readonly element: HTMLElement; readonly overflowY: string }[] = [];

  private readonly closeButton = viewChild.required('closeButton', {
    read: ElementRef<HTMLButtonElement>,
  });

  constructor() {
    afterRenderEffect(() => {
      const name = this.name();
      if (this.open() && name !== '') this.focusClose();
    });
    effect(() => {
      if (this.open()) this.lockScroll();
      else this.unlockScroll();
    });
    inject(DestroyRef).onDestroy(() => this.unlockScroll());
  }

  /** Puts the keyboard on the close button, the panel's first stop. */
  focusClose(): void {
    this.closeButton().nativeElement.focus();
  }

  private lockScroll(): void {
    if (this.locked.length > 0) return;
    for (let el = this.host.nativeElement.parentElement; el !== null; el = el.parentElement) {
      const overflowY = getComputedStyle(el).overflowY;
      if (overflowY !== 'auto' && overflowY !== 'scroll') continue;
      this.locked.push({ element: el, overflowY: el.style.overflowY });
      el.style.overflowY = 'hidden';
    }
  }

  private unlockScroll(): void {
    for (const { element, overflowY } of this.locked) element.style.overflowY = overflowY;
    this.locked = [];
  }

  onEscape(event: Event): void {
    event.preventDefault();
    this.closed.emit();
  }
}
