import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  computed,
  inject,
  input,
} from '@angular/core';

import { IconComponent } from '../core/icon.component';
import { LlmConfigStore, providerLabel } from '../core/llm-config.store';
import { THEME_OPTIONS, ThemeStore } from '../core/theme.store';
import { NotificationBellComponent } from './notification-bell.component';

/** The label of the navigation entry whose path is the route's path or a parent of it; `fallback` when none is. */
export function screenTitle(
  items: readonly { readonly path: string; readonly label: string }[],
  url: string,
  fallback: string,
): string {
  const path = url.split(/[?#]/)[0] ?? '';
  return (
    items.find((item) => path === item.path || path.startsWith(`${item.path}/`))?.label ?? fallback
  );
}

/** The bar above every screen of a window: the screen's title, the AI Source and model in use, the screen's own actions, the theme switch and the bell. */
@Component({
  selector: 'sc-top-bar',
  standalone: true,
  imports: [IconComponent, NgTemplateOutlet, NotificationBellComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="bar" data-test="top-bar">
      <div class="heading">
        <span class="title" data-test="top-bar-title">{{ title() }}</span>
        <span class="source" data-test="top-bar-source">{{ source() }}</span>
      </div>
      @if (actions(); as pageActions) {
        <ng-container [ngTemplateOutlet]="pageActions" />
      }
      <div class="theme" role="group" aria-label="Theme">
        @for (option of options; track option.choice) {
          <button
            type="button"
            class="theme-option"
            [attr.data-test]="'theme-' + option.choice"
            [attr.aria-label]="option.ariaLabel"
            [attr.aria-pressed]="theme.choice() === option.choice"
            [title]="option.ariaLabel"
            (click)="theme.set(option.choice)"
          >
            <sc-icon [name]="option.icon" [size]="17" />
          </button>
        }
      </div>
      <sc-notification-bell />
    </header>
  `,
  styles: [
    `
      .bar {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 12px;
        padding: 10px 20px;
        background: var(--sc-surface);
        border-bottom: 1px solid var(--sc-line);
        color: var(--sc-fg);
      }
      .heading {
        display: flex;
        flex-direction: column;
        gap: 2px;
        margin-right: auto;
        min-width: 0;
      }
      .title {
        font-size: 16px;
        font-weight: 600;
      }
      .source {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .theme {
        display: flex;
        gap: 2px;
        padding: 3px;
        border: 1px solid var(--sc-line);
        border-radius: 20px;
        background: var(--sc-bg);
      }
      .theme-option {
        width: 34px;
        height: 30px;
        border: 0;
        border-radius: 16px;
        display: flex;
        align-items: center;
        justify-content: center;
        background: transparent;
        color: var(--sc-muted);
        cursor: pointer;
      }
      .theme-option[aria-pressed='true'] {
        background: var(--sc-pressed);
        color: var(--sc-fg);
      }
    `,
  ],
})
export class TopBarComponent {
  private readonly llm = inject(LlmConfigStore);
  protected readonly theme = inject(ThemeStore);
  protected readonly options = THEME_OPTIONS;

  readonly title = input.required<string>();
  /** The routed screen's own controls, drawn after the title; none on a screen that has none. */
  readonly actions = input<TemplateRef<unknown> | null>(null);

  readonly source = computed<string>(() => {
    const provider = this.llm.provider();
    if (provider === null || provider === 'None') return 'No AI Source';
    const model = this.llm.model();
    return model === null ? providerLabel(provider) : `${providerLabel(provider)} · ${model}`;
  });

  constructor() {
    void this.llm.loadFromVault();
  }
}
