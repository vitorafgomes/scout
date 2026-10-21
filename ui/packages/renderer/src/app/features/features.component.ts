import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';

import type { FeatureId } from '@scout/shared';

import { FeaturesStore, type FeatureViewModel } from '../core/features.store';

import { CredentialDialogComponent } from './credential-dialog.component';
import { FeatureCardComponent } from './feature-card.component';

@Component({
  selector: 'sc-features',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FeatureCardComponent, CredentialDialogComponent],
  template: `
    <main class="page">
      <header class="header">
        <h1>Features</h1>
        <p class="lede">
          Connect Scout to external tools. Each feature stores its credentials in the OS keyring and
          can be toggled independently.
        </p>
      </header>

      @if (store.backendUnavailable()) {
        <div class="unavailable-notice" data-test="backend-unavailable">
          Backend control plane arrives in the next step. Features will appear once the worker is running.
        </div>
      } @else {
        <div class="features-grid" data-test="features-grid">
          @for (f of store.features(); track f.id) {
            <sc-feature-card
              [feature]="f"
              (toggle)="store.toggle(f.id)"
              (openCredentialDialog)="openDialog(f)"
              (clearCredentials)="store.clearCredentials(f.id)"
            />
          }
        </div>
      }

      @if (activeDialog(); as dlg) {
        <sc-credential-dialog
          [feature]="dlg"
          (submit)="onDialogSubmit(dlg.id, $event)"
          (cancel)="closeDialog()"
        />
      }
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .page {
        padding: 32px;
        max-width: 1080px;
        margin: 0 auto;
        font-family: Inter, -apple-system, 'Segoe UI', sans-serif;
      }
      .header {
        margin-bottom: 24px;
      }
      h1 {
        margin: 0 0 8px;
        font-size: 24px;
        color: var(--sc-text);
      }
      .lede {
        margin: 0;
        font-size: 14px;
        color: var(--sc-text-muted);
        max-width: 640px;
      }
      .features-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
        gap: 16px;
      }
      .unavailable-notice {
        padding: 16px 20px;
        border: 1px solid var(--sc-border-strong);
        border-radius: 8px;
        background: var(--sc-bg);
        font-size: 13px;
        color: var(--sc-text-muted);
      }
    `,
  ],
})
export class FeaturesComponent implements OnInit {
  readonly store = inject(FeaturesStore);
  readonly activeDialog = signal<FeatureViewModel | null>(null);

  async ngOnInit(): Promise<void> {
    await this.store.load();
  }

  openDialog(feature: FeatureViewModel): void {
    this.activeDialog.set(feature);
  }

  closeDialog(): void {
    this.activeDialog.set(null);
  }

  async onDialogSubmit(
    id: FeatureId,
    payload: { credentials: Record<string, string>; settings: Record<string, string> },
  ): Promise<void> {
    this.closeDialog();
    await this.store.setup(id, payload.credentials, payload.settings);
  }
}
