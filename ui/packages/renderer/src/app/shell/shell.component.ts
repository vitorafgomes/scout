import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';

import { IconComponent } from '../core/icon.component';
import { LogStore } from '../core/log.store';
import { THEME_OPTIONS, ThemeStore } from '../core/theme.store';
import { WizardStore, wizardStepCounter } from '../core/wizard.store';
import { PostConnectShellComponent } from './post-connect-shell.component';
import { StepIndicatorComponent } from './step-indicator.component';
import { VaultAvailabilityBannerComponent } from './vault-availability-banner.component';

/** The setup wizard's pages, which paint with the theme tokens; any other page shown before setup keeps the dark canvas until it migrates. */
const WIZARD_ROUTES: readonly string[] = Object.freeze([
  '/',
  '/settings',
  '/provision',
  '/token',
  '/oauth',
  '/launch',
  '/ready',
]);

@Component({
  selector: 'sc-shell',
  standalone: true,
  imports: [
    IconComponent,
    RouterOutlet,
    VaultAvailabilityBannerComponent,
    StepIndicatorComponent,
    PostConnectShellComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.sc-dark-canvas]': 'pinnedDark()' },
  template: `
    <sc-vault-availability-banner />
    @if (showPostConnectShell()) {
      <sc-post-connect-shell />
    } @else {
      <header class="bar" data-test="wizard-header">
        <div class="brand">
          <span class="mark" aria-hidden="true">S</span>
          <div class="heading">
            <span class="title" data-test="wizard-title">Scout setup</span>
            <span class="counter" data-test="step-counter">{{ stepCounter() }}</span>
          </div>
        </div>
        <div class="theme" role="group" aria-label="Theme">
          @for (option of themeOptions; track option.choice) {
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
      </header>
      <sc-step-indicator />
      <router-outlet />
    }
  `,
  styles: [
    `
      :host {
        display: block;
        min-height: 100vh;
        background: var(--sc-bg);
        color: var(--sc-fg);
      }
      :host.sc-dark-canvas {
        background: #0f1117;
        color: #e4e8f0;
      }
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
      .brand {
        display: flex;
        align-items: center;
        gap: 10px;
        margin-right: auto;
      }
      .mark {
        display: flex;
        align-items: center;
        justify-content: center;
        width: 28px;
        height: 28px;
        border-radius: 8px;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
        font-weight: 700;
      }
      .heading {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .title {
        font-size: 15px;
        font-weight: 600;
      }
      .counter {
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
export class ShellComponent {
  private readonly wizard = inject(WizardStore);
  private readonly logStore = inject(LogStore);
  private readonly url = toSignal(
    inject(Router).events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
    ),
    { initialValue: inject(Router).url },
  );

  protected readonly theme = inject(ThemeStore);
  protected readonly themeOptions = THEME_OPTIONS;

  /** The "Step n of N · name" line of the setup header, counted over the steps the stepper lists. */
  readonly stepCounter = computed<string>(() => wizardStepCounter(this.wizard.currentStep()));

  /**
   * The main shell once Scout can run; the wizard keeps its frame and stepper through Launch and hands over when the
   * worker reports running.
   */
  readonly showPostConnectShell = computed<boolean>(
    () =>
      this.wizard.hasProvisioning() &&
      !(this.wizard.currentStep() === 'launch' && this.logStore.status().state !== 'running'),
  );

  /** True before setup on a page that is not one of the wizard's, such as Slack or Settings, which still paints dark. */
  readonly pinnedDark = computed<boolean>(
    () =>
      !this.showPostConnectShell() && !WIZARD_ROUTES.includes(this.url().split(/[?#]/)[0] ?? ''),
  );
}
