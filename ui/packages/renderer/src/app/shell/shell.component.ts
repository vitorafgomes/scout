import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { StepIndicatorComponent } from './step-indicator.component';
import { VaultAvailabilityBannerComponent } from './vault-availability-banner.component';

@Component({
  selector: 'sc-shell',
  standalone: true,
  imports: [RouterOutlet, VaultAvailabilityBannerComponent, StepIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sc-vault-availability-banner />
    <sc-step-indicator />
    <router-outlet />
  `,
  styles: [
    `
      :host {
        display: block;
        min-height: 100vh;
        background: #0f1117;
        color: #e4e8f0;
      }
    `,
  ],
})
export class ShellComponent {}
