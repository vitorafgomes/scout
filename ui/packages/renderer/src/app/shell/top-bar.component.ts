import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  computed,
  inject,
  input,
} from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

import { IconComponent } from '../core/icon.component';
import { LlmConfigStore, providerLabel } from '../core/llm-config.store';
import { McpStore } from '../core/mcp.store';
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

/** The bar above every screen of a window: the screen's title, the AI Source and model in use, the screen's own actions, the theme switch, the bell and, in the main window, the Settings button. */
@Component({
  selector: 'sc-top-bar',
  standalone: true,
  imports: [
    IconComponent,
    NgTemplateOutlet,
    NotificationBellComponent,
    RouterLink,
    RouterLinkActive,
  ],
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
      @if (mcpActive()) {
        <a
          class="settings mcp"
          routerLink="/settings"
          aria-label="MCP server is active. Open its settings"
          title="MCP server is active. Open its settings"
          data-test="mcp-mark"
        >
          <sc-icon name="plug" [size]="19" />
          <span class="mcp-dot" aria-hidden="true"></span>
        </a>
      }
      <sc-notification-bell />
      @if (settings()) {
        <a
          class="settings"
          routerLink="/settings"
          routerLinkActive="settings--active"
          ariaCurrentWhenActive="page"
          aria-label="Settings"
          title="Settings"
          data-test="nav-settings"
        >
          <sc-icon name="sliders" [size]="19" />
        </a>
      }
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
      .settings {
        width: 40px;
        height: 40px;
        box-sizing: border-box;
        border-radius: 20px;
        border: 1px solid var(--sc-line);
        display: flex;
        align-items: center;
        justify-content: center;
        color: var(--sc-fg);
      }
      .mcp {
        position: relative;
      }
      .mcp-dot {
        position: absolute;
        top: 4px;
        right: 4px;
        width: 11px;
        height: 11px;
        box-sizing: border-box;
        border: 2px solid var(--sc-surface);
        border-radius: 6px;
        background: var(--sc-ok);
      }
      .settings--active {
        background: var(--sc-surface-2);
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
  private readonly mcp = inject(McpStore);
  protected readonly theme = inject(ThemeStore);
  protected readonly options = THEME_OPTIONS;

  readonly title = input.required<string>();
  /** The routed screen's own controls, drawn after the title; none on a screen that has none. */
  readonly actions = input<TemplateRef<unknown> | null>(null);
  /** True in the window that has the Settings screen: the bar then ends with the button that opens it. */
  readonly settings = input(false);

  /** True in the window that has the Settings screen while the MCP server listens: the bar then shows its mark. */
  readonly mcpActive = computed(
    () => this.settings() && this.mcp.status()?.listener.state === 'listening',
  );

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
