import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { Router } from '@angular/router';

import { WizardStore } from '../../core/wizard.store';

@Component({
  selector: 'sc-worker-launch',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="launch">
      <h1>Almost there</h1>
      @if (wizard.guild(); as guild) {
        <p class="copy">
          Scout is ready to start in <strong>{{ guild.name }}</strong
          >. The worker supervisor ships in the next release - come back soon to finish
          provisioning.
        </p>
      } @else {
        <p class="copy">
          Scout is ready. The worker supervisor ships in the next release - come back soon to finish
          provisioning.
        </p>
      }
      <div class="actions">
        <button type="button" class="primary" data-test="back-home" (click)="backHome()">
          Back to home
        </button>
      </div>
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        color: #e4e8f0;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .launch {
        padding: 32px;
        max-width: 720px;
        margin: 0 auto;
      }
      h1 {
        font-size: 20px;
        margin: 0 0 16px;
      }
      .copy {
        font-size: 14px;
        line-height: 1.6;
      }
      .actions {
        margin-top: 32px;
      }
      .primary {
        background: #7c5cff;
        color: #0f1117;
        border: none;
        border-radius: 6px;
        padding: 10px 16px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
    `,
  ],
})
export class WorkerLaunchComponent implements OnInit {
  private readonly router = inject(Router);
  readonly wizard = inject(WizardStore);

  ngOnInit(): void {
    this.wizard.goTo('launch');
    if (this.wizard.guild() === null) {
      void this.router.navigateByUrl('/');
    }
  }

  async backHome(): Promise<void> {
    await this.wizard.reset();
    void this.router.navigateByUrl('/');
  }
}
