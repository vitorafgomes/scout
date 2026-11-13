import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import type { LocalBubbleSettings } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';
import { BubbleStateStore } from './bubble-state';

/**
 * The card beside the floating button, in a transparent window of its own that main shows while the pointer or the
 * keyboard focus is on the button and that never takes a click: a title and a line for the button's state, drawn on
 * the side of the button that faces the middle of the work area, as the floating button's corner says.
 */
@Component({
  selector: 'sc-floating-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[attr.data-corner]': 'corner()' },
  template: `
    <div class="card" role="status" data-test="floating-card">
      <span class="title" data-test="floating-card-title">{{ store.card().title }}</span>
      <span class="line" data-test="floating-card-line">{{ store.card().line }}</span>
    </div>
  `,
  styles: [
    `
      :host {
        position: fixed;
        inset: 0;
        box-sizing: border-box;
        display: flex;
        flex-direction: column;
        justify-content: flex-end;
        align-items: flex-end;
        padding: 0 12px 10px;
        background: transparent;
        font-size: 13px;
        --card-shadow: color-mix(in srgb, var(--sc-scrim) 45%, transparent);
      }
      :host([data-corner^='top']) {
        justify-content: flex-start;
        padding: 10px 12px 0;
      }
      :host([data-corner$='left']) {
        align-items: flex-start;
      }
      :host-context(html[data-theme='dark']) {
        --card-shadow: color-mix(in srgb, var(--sc-scrim) 83%, transparent);
      }
      @media (prefers-color-scheme: dark) {
        :host-context(html:not([data-theme])) {
          --card-shadow: color-mix(in srgb, var(--sc-scrim) 83%, transparent);
        }
      }
      .card {
        max-width: 220px;
        box-sizing: border-box;
        padding: 8px 10px;
        border-radius: 10px;
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        box-shadow: 0 6px 18px var(--card-shadow);
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .title {
        font-weight: 600;
        color: var(--sc-fg);
      }
      .line {
        font-size: 12px;
        color: var(--sc-fg-2);
        line-height: 1.4;
      }
    `,
  ],
})
export class FloatingCardComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly store = inject(BubbleStateStore);

  /** The floating button's corner, which decides the side of the button the card is drawn on. */
  readonly corner = signal<LocalBubbleSettings['corner']>('bottom-right');

  ngOnInit(): void {
    document.documentElement.style.background = 'transparent';
    document.body.style.background = 'transparent';
    document.body.style.margin = '0';
    document.body.style.overflow = 'hidden';
    this.store.start(this.destroyRef);
    let pushed = false;
    this.destroyRef.onDestroy(
      this.bridge.onLocalBubble(({ corner }) => {
        pushed = true;
        this.corner.set(corner);
      }),
    );
    this.bridge
      .localGetBubble()
      .then(({ corner }) => {
        if (!pushed) this.corner.set(corner);
      })
      .catch(() => undefined);
  }
}
