import { ChangeDetectionStrategy, Component, input } from '@angular/core';

import { IconComponent } from '../core/icon.component';

/** The stroke mark each feature's tile draws, by feature id. */
export const FEATURE_MARKS: Readonly<Record<string, string>> = {
  github:
    'M9 19c-4 1.5-4-2-6-2.5M15 21v-3.5c0-1 .1-1.4-.5-2 2.8-.3 5.5-1.4 5.5-6a4.6 4.6 0 0 0-1.3-3.2 4.2 4.2 0 0 0-.1-3.2s-1-.3-3.4 1.3a11.6 11.6 0 0 0-6 0C6.8 2.8 5.8 3.1 5.8 3.1a4.2 4.2 0 0 0-.1 3.2A4.6 4.6 0 0 0 4.4 9.5c0 4.6 2.7 5.7 5.5 6-.6.6-.6 1.2-.5 2V21',
  docker: 'M21 8l-9-5-9 5 9 5zM3 8v8l9 5 9-5V8M12 13v8',
  kubernetes: 'M12 2l8.5 4.5v9L12 20l-8.5-4.5v-9zM12 7v5l4.3 2.5M12 12l-4.3 2.5',
  youtube: 'M3 6h18v12H3zM10 9l5 3-5 3z',
  desktop: 'M3 4h18v12H3zM8 20h8M12 16v4',
};

/** The 40 px tile with a feature's mark, as its card and its side panel show it. */
@Component({
  selector: 'sc-feature-logo',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  host: { 'aria-hidden': 'true', '[attr.data-test]': "'feature-logo-' + feature()" },
  template: `
    @if (paths[feature()]; as d) {
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="1.8"
        stroke-linecap="round"
        stroke-linejoin="round"
      >
        <path [attr.d]="d" />
      </svg>
    } @else {
      <sc-icon name="plug" [size]="20" />
    }
  `,
  styles: [
    `
      :host {
        flex: 0 0 40px;
        height: 40px;
        border-radius: 10px;
        background: var(--sc-surface-2);
        display: flex;
        align-items: center;
        justify-content: center;
        color: var(--sc-fg);
      }
      svg {
        width: 20px;
        height: 20px;
      }
    `,
  ],
})
export class FeatureLogoComponent {
  readonly feature = input.required<string>();
  readonly paths = FEATURE_MARKS;
}
