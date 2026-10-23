import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';

import type { FeatureId } from '@scout/shared';

import { FeaturesStore, type FeatureViewModel } from '../core/features.store';

import { CredentialDialogComponent } from './credential-dialog.component';
import { FeatureCardComponent } from './feature-card.component';
import { GitHubConnectComponent } from './github-connect.component';
import { KubernetesConnectComponent } from './kubernetes-connect.component';

type Connection = 'connected' | 'not-connected' | 'unknown';

@Component({
  selector: 'sc-features',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FeatureCardComponent,
    CredentialDialogComponent,
    GitHubConnectComponent,
    KubernetesConnectComponent,
  ],
  template: `
    <main class="page">
      <header class="header">
        <h1>Features</h1>
        <p class="lede">Connect Scout to external tools.</p>
      </header>

      @if (store.backendUnavailable()) {
        <div class="unavailable-notice" data-test="backend-unavailable">
          The Scout control plane is not running, so features cannot be shown.
        </div>
      } @else {
        <div class="features-grid" data-test="features-grid">
          @for (f of store.features(); track f.id) {
            <sc-feature-card
              [feature]="f"
              [connection]="connectionOf(f.id)"
              (toggle)="store.toggle(f.id)"
              (openCredentialDialog)="openDialog(f)"
              (clearCredentials)="store.clearCredentials(f.id)"
            >
              @if (f.id === 'github') {
                <sc-github-connect
                  (connectedChange)="githubConnection.set($event ? 'connected' : 'not-connected')"
                />
              }
              @if (f.id === 'kubernetes') {
                <sc-kubernetes-connect
                  (connectedChange)="
                    kubernetesConnection.set($event ? 'connected' : 'not-connected')
                  "
                />
              }
            </sc-feature-card>
          }
        </div>
        <div class="notes" data-test="features-notes">
          <p>GitHub and Kubernetes are used by the chat bot once connected.</p>
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
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .header {
        margin-bottom: 24px;
      }
      h1 {
        margin: 0 0 8px;
        font-size: 24px;
        color: #e4e8f0;
      }
      .lede {
        margin: 0;
        font-size: 14px;
        color: #7b8299;
        max-width: 640px;
      }
      .features-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
        gap: 16px;
      }
      .notes {
        margin-top: 16px;
        font-size: 13px;
        color: #7b8299;
      }
      .notes p {
        margin: 0 0 4px;
      }
      .unavailable-notice {
        padding: 16px 20px;
        border: 1px solid #2d3348;
        border-radius: 8px;
        background: #11131a;
        font-size: 13px;
        color: #7b8299;
      }
    `,
  ],
})
export class FeaturesComponent implements OnInit {
  readonly store = inject(FeaturesStore);
  readonly activeDialog = signal<FeatureViewModel | null>(null);
  readonly githubConnection = signal<Connection>('unknown');
  readonly kubernetesConnection = signal<Connection>('unknown');

  connectionOf(id: FeatureId): Connection | null {
    switch (id) {
      case 'github':
        return this.githubConnection();
      case 'kubernetes':
        return this.kubernetesConnection();
      default:
        return null;
    }
  }

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
