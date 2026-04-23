import { UpperCasePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  OnInit,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';
import { CLIENT_KINDS, ScoutError, type DetectedClient } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

@Component({
  selector: 'sc-welcome',
  standalone: true,
  imports: [UpperCasePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="welcome">
      <h1>Scout Desktop</h1>
      <p>Provision your Scout bot.</p>
      @if (error(); as errorMessage) {
        <p class="error">{{ errorMessage }}</p>
      }
      <section class="platforms">
        @if (loading()) {
          <p class="loading">Detecting clients...</p>
        } @else {
          @for (client of visibleClients(); track client.kind) {
            <article class="platform-card" [attr.data-kind]="client.kind">
              <h2>{{ client.kind | uppercase }}</h2>
              <p>{{ client.installed ? 'Installed' : 'Not installed' }}</p>
            </article>
          }
        }
      </section>
      <div class="cta-row">
        <button
          type="button"
          class="primary-cta"
          data-test="provision-discord"
          [disabled]="loading()"
          (click)="startDiscordProvisioning()"
        >
          Provision Discord
        </button>
      </div>
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        background: #0f1117;
        color: #e4e8f0;
        font-family:
          Inter,
          -apple-system,
          BlinkMacSystemFont,
          'Segoe UI',
          Helvetica,
          Arial,
          sans-serif;
      }
      .welcome {
        padding: 48px 32px;
        max-width: 960px;
        margin: 0 auto;
      }
      h1 {
        font-size: 26px;
        margin: 0 0 8px;
      }
      .platforms {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
        gap: 16px;
        margin-top: 32px;
      }
      .platform-card {
        border: 1px solid #252b3b;
        border-radius: 10px;
        padding: 16px;
        background: #171b26;
      }
      .platform-card h2 {
        font-size: 16px;
        margin: 0 0 4px;
      }
      .error {
        color: #d45a5a;
      }
      .loading {
        color: #7b8299;
      }
      .cta-row {
        margin-top: 32px;
        display: flex;
        justify-content: flex-start;
      }
      .primary-cta {
        background: #7c5cff;
        color: #0f1117;
        border: none;
        border-radius: 6px;
        padding: 10px 20px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .primary-cta:hover:not(:disabled) {
        background: #9378ff;
      }
      .primary-cta:disabled {
        background: #3d4258;
        color: #7b8299;
        cursor: not-allowed;
      }
    `,
  ],
})
export class WelcomeComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly router = inject(Router);
  private readonly wizard = inject(WizardStore);

  readonly loading = signal<boolean>(true);
  readonly clients = signal<readonly DetectedClient[]>([]);
  readonly error = signal<string | null>(null);

  readonly visibleClients = computed<readonly DetectedClient[]>(() => {
    const detected = this.clients();
    if (detected.length > 0) return detected;
    return CLIENT_KINDS.map((kind) => ({ kind, installed: false, method: 'none' as const }));
  });

  ngOnInit(): void {
    this.wizard.goTo('welcome');
    void this.runDetection();
  }

  startDiscordProvisioning(): void {
    this.wizard.goTo('provision');
    void this.router.navigateByUrl('/provision');
  }

  private async runDetection(): Promise<void> {
    try {
      const response = await this.bridge.detectClients();
      this.clients.set(response.clients);
      this.error.set(null);
    } catch (err: unknown) {
      this.error.set(this.describeError(err));
    } finally {
      this.loading.set(false);
    }
  }

  private describeError(err: unknown): string {
    if (err instanceof ScoutError) {
      return `${err.code}: ${err.message}`;
    }
    if (err instanceof Error) {
      return err.message;
    }
    return 'Unknown error while detecting clients.';
  }
}
