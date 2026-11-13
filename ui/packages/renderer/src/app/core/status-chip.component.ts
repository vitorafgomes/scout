import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** The tone of a status chip; it picks the container pair and the leading glyph. */
export type ChipTone = 'ok' | 'warn' | 'error' | 'info' | 'neutral';

/** The leading glyph of each tone, one stroke path on a 24 by 24 grid: check, triangle, crossed circle, arc and dot. */
export const CHIP_GLYPHS: Readonly<Record<ChipTone, string>> = Object.freeze({
  ok: 'M20 6L9 17l-5-5',
  warn: 'M12 3l10 18H2zM12 10v4M12 17v.5',
  error: 'M15 9l-6 6M9 9l6 6M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z',
  info: 'M12 3a9 9 0 1 0 9 9',
  neutral: 'M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z',
});

/** The one status chip of every screen: a leading glyph and a sentence-case label on the tone's container pair. */
@Component({
  selector: 'sc-status-chip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[attr.data-tone]': 'tone()' },
  template: `
    <svg
      width="13"
      height="13"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="2.2"
      stroke-linecap="round"
      stroke-linejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path [attr.d]="glyph()" /></svg
    >{{ label() }}
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        gap: 5px;
        padding: 3px 10px;
        border-radius: 12px;
        font-size: 12px;
        font-weight: 600;
        white-space: nowrap;
        background: var(--sc-surface-2);
        color: var(--sc-muted);
      }
      svg {
        flex: none;
      }
      :host([data-tone='ok']) {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      :host([data-tone='warn']) {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      :host([data-tone='error']) {
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
      }
      :host([data-tone='info']) {
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
      }
    `,
  ],
})
export class StatusChipComponent {
  readonly tone = input.required<ChipTone>();
  readonly label = input.required<string>();

  readonly glyph = computed<string>(() => CHIP_GLYPHS[this.tone()]);
}
