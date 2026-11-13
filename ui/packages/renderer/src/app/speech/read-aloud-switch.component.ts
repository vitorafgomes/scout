import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';

import { IconComponent } from '../core/icon.component';
import { ReadAloud } from './read-aloud';

/** "Read answers aloud" as an icon toggle: every reply to a question asked in this window is read as it streams; the reason the voice failed shows under it. */
@Component({
  selector: 'sc-read-aloud-switch',
  standalone: true,
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      type="button"
      class="toggle"
      [class.bar]="look() === 'bar'"
      data-test="read-aloud-switch"
      aria-label="Read answers aloud"
      title="Read answers aloud"
      [attr.aria-pressed]="readAloud.enabled()"
      (click)="readAloud.setEnabled(!readAloud.enabled())"
    >
      <sc-icon
        [name]="readAloud.enabled() ? 'speaker' : 'speaker-off'"
        [size]="look() === 'bar' ? 18 : 16"
      />
    </button>
    @if (look() === 'tray' && readAloud.error(); as reason) {
      <p class="voice-error" role="alert" data-test="voice-error">{{ reason }}</p>
    }
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        flex-direction: column;
        gap: 2px;
      }
      .toggle {
        width: 32px;
        height: 32px;
        padding: 0;
        border-radius: var(--sc-radius-sm);
        border: 1px solid transparent;
        background: transparent;
        color: var(--sc-fg-2);
        display: inline-flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        flex: none;
        -webkit-app-region: no-drag;
      }
      .toggle:hover {
        background: var(--sc-surface-2);
      }
      .toggle[aria-pressed='true'] {
        background: var(--sc-info-container);
        color: var(--sc-accent);
        border-color: var(--sc-line);
      }
      .toggle.bar {
        width: 40px;
        height: 40px;
        border-radius: 50%;
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg);
      }
      .toggle.bar[aria-pressed='true'] {
        background: var(--sc-surface-2);
        color: var(--sc-fg);
      }
      .toggle:focus-visible {
        outline: 2px solid var(--sc-accent);
        outline-offset: 1px;
      }
      .voice-error {
        margin: 0;
        font-size: 12px;
        color: var(--sc-error);
      }
    `,
  ],
})
export class ReadAloudSwitchComponent {
  protected readonly readAloud = inject(ReadAloud);
  /** `tray` is the 32 px tool of a composer, with the voice's failure under it; `bar` is the round top-bar icon button, whose screen shows that failure itself. */
  readonly look = input<'tray' | 'bar'>('tray');
}
