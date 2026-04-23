import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { Router } from '@angular/router';

import { WizardStore } from '../../core/wizard.store';

@Component({
  selector: 'sc-ready',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="ready">
      <h1>Ready</h1>
      <p class="copy">Ready (placeholder - PR 10/11 finishes this).</p>
      <button type="button" class="primary" (click)="backHome()">Back to home</button>
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
      .ready {
        padding: 32px;
        max-width: 720px;
        margin: 0 auto;
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
export class ReadyComponent implements OnInit {
  private readonly router = inject(Router);
  readonly wizard = inject(WizardStore);

  ngOnInit(): void {
    this.wizard.goTo('ready');
  }

  async backHome(): Promise<void> {
    await this.wizard.reset();
    void this.router.navigateByUrl('/');
  }
}
