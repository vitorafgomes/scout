import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';

import { IconComponent } from '../core/icon.component';
import { ReadAloud } from './read-aloud';

/** The speaker on one of Scout's replies: reads that reply aloud, or stops it while it is being read. */
@Component({
  selector: 'sc-read-aloud-button',
  standalone: true,
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      type="button"
      class="speak"
      data-test="read-aloud"
      [attr.aria-pressed]="reading()"
      [attr.aria-label]="reading() ? 'Stop reading aloud' : 'Read this answer aloud'"
      [title]="reading() ? 'Stop reading aloud (Esc)' : 'Read this answer aloud'"
      (click)="readAloud.toggle(turnId())"
    >
      <sc-icon [name]="reading() ? 'stop' : 'speaker'" [size]="14" />
    </button>
  `,
  styles: [
    `
      .speak {
        width: 26px;
        height: 26px;
        padding: 0;
        border: 0;
        border-radius: 6px;
        background: transparent;
        color: var(--sc-muted);
        display: inline-flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        -webkit-app-region: no-drag;
      }
      .speak:hover,
      .speak[aria-pressed='true'] {
        color: var(--sc-accent);
        background: var(--sc-surface-2);
      }
      .speak:focus-visible {
        outline: 2px solid var(--sc-focus);
        outline-offset: 1px;
      }
    `,
  ],
})
export class ReadAloudButtonComponent {
  protected readonly readAloud = inject(ReadAloud);
  readonly turnId = input.required<string>();
  readonly reading = computed(() => this.readAloud.speakingId() === this.turnId());
}
