import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';

import type { FeatureId } from '@scout/shared';

import { FeaturesStore, type FeatureViewModel } from '../core/features.store';
import { ScoutBridgeService } from '../core/scout-bridge';

import { CredentialDialogComponent } from './credential-dialog.component';
import { FeatureCardComponent } from './feature-card.component';
import { GitHubConnectComponent } from './github-connect.component';
import { KubernetesConnectComponent } from './kubernetes-connect.component';
import { WriteConfirmationDialogComponent } from './write-confirmation-dialog.component';
import { YouTubeConnectComponent } from './youtube-connect.component';

type Connection = 'connected' | 'not-connected' | 'unknown';

interface WriteConfirmation {
  readonly feature: FeatureViewModel;
  readonly purpose: 'enable' | 'grant';
}

export const NO_ALLOWED_USERS_WARNING =
  'Nobody can use this connection from chat until you add allowed users in Settings.';

@Component({
  selector: 'sc-features',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FeatureCardComponent,
    CredentialDialogComponent,
    GitHubConnectComponent,
    KubernetesConnectComponent,
    YouTubeConnectComponent,
    WriteConfirmationDialogComponent,
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
              (toggle)="onToggle(f)"
              (allowWrites)="confirming.set({ feature: f, purpose: 'grant' })"
              (revokeWrites)="store.setWriteGrant(f.id, false, false)"
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
              @if (f.id === 'youtube') {
                <sc-youtube-connect
                  (connectedChange)="youtubeConnection.set($event ? 'connected' : 'not-connected')"
                />
              }
              @if (f.id !== 'desktop' && connectionOf(f.id) === 'connected' && noAllowedUsers()) {
                <p class="access-warning" [attr.data-test]="'access-warning-' + f.id">
                  {{ noAllowedUsersWarning }}
                </p>
              }
            </sc-feature-card>
          }
        </div>
        <div class="notes" data-test="features-notes">
          <p>GitHub, Kubernetes and YouTube are used by the chat bot once connected.</p>
          <p>
            A feature that writes lists its write permissions on its card and writes only after you
            allow them; actions that cannot be undone wait for you on the Review screen.
          </p>
        </div>
      }

      @if (activeDialog(); as dlg) {
        <sc-credential-dialog
          [feature]="dlg"
          (submit)="onDialogSubmit(dlg.id, $event)"
          (cancel)="closeDialog()"
        />
      }

      @if (confirming(); as c) {
        <sc-write-confirmation-dialog
          [feature]="c.feature"
          (allow)="onAllowWrites(c)"
          (cancel)="confirming.set(null)"
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
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 460px), 1fr));
        gap: 16px;
        align-items: start;
      }
      .notes {
        margin-top: 16px;
        font-size: 13px;
        color: #7b8299;
      }
      .notes p {
        margin: 0 0 4px;
      }
      .access-warning {
        margin: 8px 0 0;
        font-size: 13px;
        color: #e0b04a;
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
  private readonly bridge = inject(ScoutBridgeService);
  readonly noAllowedUsers = signal(false);
  readonly noAllowedUsersWarning = NO_ALLOWED_USERS_WARNING;
  readonly activeDialog = signal<FeatureViewModel | null>(null);
  readonly confirming = signal<WriteConfirmation | null>(null);
  readonly githubConnection = signal<Connection>('unknown');
  readonly kubernetesConnection = signal<Connection>('unknown');
  readonly youtubeConnection = signal<Connection>('unknown');

  connectionOf(id: FeatureId): Connection | null {
    switch (id) {
      case 'github':
        return this.githubConnection();
      case 'kubernetes':
        return this.kubernetesConnection();
      case 'youtube':
        return this.youtubeConnection();
      case 'desktop':
        return 'connected';
      default:
        return null;
    }
  }

  async ngOnInit(): Promise<void> {
    void this.bridge.accessGet().then(
      (res) => this.noAllowedUsers.set(res.userIds.length === 0),
      () => this.noAllowedUsers.set(false),
    );
    await this.store.load();
  }

  onToggle(feature: FeatureViewModel): void {
    if (!feature.enabled && feature.writeClass !== 'none') {
      this.confirming.set({ feature, purpose: 'enable' });
      return;
    }
    void this.store.toggle(feature.id);
  }

  async onAllowWrites(confirmation: WriteConfirmation): Promise<void> {
    this.confirming.set(null);
    const { id } = confirmation.feature;
    if (confirmation.purpose === 'enable') await this.store.toggle(id, true);
    else await this.store.setWriteGrant(id, true, true);
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
