import { UpperCasePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { ScoutError, type DetectedClient } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';

@Component({
  selector: 'sc-welcome',
  standalone: true,
  imports: [UpperCasePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="welcome">
      <h1>Scout Desktop</h1>
      <p>Provision your Scout bot.</p>
      <section class="platforms">
        @if (loading()) {
          <p class="loading">Detecting clients...</p>
        } @else if (error(); as errorMessage) {
          <p class="error">{{ errorMessage }}</p>
        } @else {
          @for (client of clients(); track client.kind) {
            <article class="platform-card" [attr.data-kind]="client.kind">
              <h2>{{ client.kind | uppercase }}</h2>
              <p>{{ client.installed ? 'Installed' : 'Not installed' }}</p>
            </article>
          }
        }
      </section>
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        min-height: 100vh;
        background: #111111;
        color: #ffffff;
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
    `,
  ],
})
export class WelcomeComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);

  readonly loading = signal<boolean>(true);
  readonly clients = signal<readonly DetectedClient[]>([]);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.runDetection();
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
