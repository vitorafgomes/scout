import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { WizardStore } from '../core/wizard.store';
import { PostConnectShellComponent } from './post-connect-shell.component';
import { StepIndicatorComponent } from './step-indicator.component';
import { VaultAvailabilityBannerComponent } from './vault-availability-banner.component';

@Component({
  selector: 'sc-shell',
  standalone: true,
  imports: [
    RouterOutlet,
    VaultAvailabilityBannerComponent,
    StepIndicatorComponent,
    PostConnectShellComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sc-vault-availability-banner />
    @if (showPostConnectShell()) {
      <sc-post-connect-shell />
    } @else {
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
        color: var(--sc-text);
      }
    `,
  ],
})
export class ShellComponent {
  private readonly wizard = inject(WizardStore);
  readonly showPostConnectShell = computed<boolean>(() => this.wizard.hasProvisioning());
}
