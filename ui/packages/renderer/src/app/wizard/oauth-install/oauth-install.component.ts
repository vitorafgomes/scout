import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ScoutError, type DiscordOAuthResult } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

type InstallState = 'idle' | 'awaiting' | 'exchanging' | 'success' | 'error';

// Read-only Discord permissions Scout requires at install time. Keep the
// bit positions explicit so reviewers can map each flag to a docs entry.
const PERM_VIEW_CHANNEL = 1n << 10n;
const PERM_READ_MESSAGE_HISTORY = 1n << 16n;
const PERM_USE_APPLICATION_COMMANDS = 1n << 31n;

function computePermissions(): string {
  return (PERM_VIEW_CHANNEL | PERM_READ_MESSAGE_HISTORY | PERM_USE_APPLICATION_COMMANDS).toString();
}

const ERROR_COPY: Readonly<Record<string, string>> = Object.freeze({
  'oauth/state-mismatch': 'Authorization was rejected for security reasons. Try again.',
  'oauth/user-cancelled': 'Authorization cancelled. Try again or go back.',
  'oauth/loopback-port-in-use':
    'Cannot listen for the browser callback - close other Scout Desktop instances and retry.',
  'app/external-host-not-allowed':
    'An internal host allow-list check failed. This is a bug - please report it.',
});

@Component({
  selector: 'sc-oauth-install',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="oauth-install">
      <h1>Provision Discord</h1>
      <p class="subtitle">Step 3 of 3 - Install to your server</p>

      @switch (state()) {
        @case ('awaiting') {
          <section class="panel">
            <div class="spinner" aria-hidden="true"></div>
            <p>Waiting for authorization in your browser...</p>
            <p class="hint">Listening on 127.0.0.1:53682</p>
            <button type="button" class="ghost" data-test="cancel" (click)="cancel()">
              Cancel
            </button>
          </section>
        }
        @case ('exchanging') {
          <section class="panel">
            <div class="spinner" aria-hidden="true"></div>
            <p>Completing authorization...</p>
          </section>
        }
        @case ('success') {
          <section class="panel success">
            <p data-test="success-line">
              Scout was added to <strong>{{ guild()?.guildName }}</strong
              >.
            </p>
            <div class="actions">
              <button type="button" class="primary" data-test="continue" (click)="continueNext()">
                Continue
              </button>
            </div>
          </section>
        }
        @case ('error') {
          <section class="panel error-panel" role="alert">
            <p class="error" data-test="error-copy">{{ errorMessage() }}</p>
            <div class="actions">
              <button type="button" class="secondary" data-test="back" (click)="back()">
                Back
              </button>
              <button type="button" class="primary" data-test="retry" (click)="retry()">
                Retry
              </button>
            </div>
          </section>
        }
        @default {
          <section class="panel">
            <p>Preparing authorization...</p>
          </section>
        }
      }
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
      .oauth-install {
        padding: 32px;
        max-width: 720px;
        margin: 0 auto;
      }
      h1 {
        font-size: 20px;
        margin: 0 0 4px;
      }
      .subtitle {
        color: #7b8299;
        margin: 0 0 24px;
        font-size: 13px;
      }
      .panel {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 10px;
        padding: 32px;
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 16px;
        text-align: center;
      }
      .panel.success {
        border-color: #3aaf7b;
      }
      .panel.error-panel {
        border-color: #d45a5a;
      }
      .error {
        color: #d45a5a;
      }
      .hint {
        color: #7b8299;
        font-size: 13px;
        font-family: 'JetBrains Mono', ui-monospace, Menlo, Consolas, monospace;
      }
      .spinner {
        width: 28px;
        height: 28px;
        border-radius: 9999px;
        border: 3px solid #252b3b;
        border-top-color: #7c5cff;
        animation: sc-spin 1s linear infinite;
      }
      @keyframes sc-spin {
        to {
          transform: rotate(360deg);
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .spinner {
          animation-duration: 0ms;
        }
      }
      .actions {
        display: flex;
        gap: 12px;
        justify-content: center;
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
      .secondary {
        background: transparent;
        color: #e4e8f0;
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 10px 16px;
        font-size: 14px;
        cursor: pointer;
      }
      .ghost {
        background: transparent;
        color: #e4e8f0;
        border: none;
        padding: 8px 12px;
        font-size: 13px;
        cursor: pointer;
        text-decoration: underline;
      }
    `,
  ],
})
export class OAuthInstallComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly router = inject(Router);
  private readonly wizard = inject(WizardStore);

  readonly state = signal<InstallState>('idle');
  readonly errorMessage = signal<string | null>(null);
  readonly guild = signal<DiscordOAuthResult | null>(null);

  ngOnInit(): void {
    this.wizard.goTo('oauth');
    const id = this.wizard.clientId();
    if (id === null) {
      void this.router.navigateByUrl('/');
      return;
    }
    void this.runInstall(id);
  }

  retry(): void {
    const id = this.wizard.clientId();
    if (id === null) {
      void this.router.navigateByUrl('/');
      return;
    }
    void this.runInstall(id);
  }

  async cancel(): Promise<void> {
    try {
      await this.bridge.discordCancelOAuthInstall();
    } catch {
      // Even if the cancel IPC fails, still return the UI to a usable state.
    }
    this.state.set('idle');
    void this.router.navigateByUrl('/token');
  }

  continueNext(): void {
    this.wizard.goTo('launch');
    void this.router.navigateByUrl('/launch');
  }

  back(): void {
    void this.router.navigateByUrl('/token');
  }

  private async runInstall(clientId: string): Promise<void> {
    this.state.set('awaiting');
    this.errorMessage.set(null);
    this.guild.set(null);

    try {
      const permissions = computePermissions();
      const result = await this.bridge.discordStartOAuthInstall({ clientId, permissions });
      // The main process handled state verification + token exchange;
      // reaching here means the flow succeeded.
      this.state.set('exchanging');
      this.guild.set(result);
      this.wizard.setGuild({ id: result.guildId, name: result.guildName });
      this.state.set('success');
    } catch (err: unknown) {
      this.errorMessage.set(this.describeError(err));
      this.state.set('error');
    }
  }

  private describeError(err: unknown): string {
    if (err instanceof ScoutError) {
      const mapped = ERROR_COPY[err.code];
      if (mapped !== undefined) return mapped;
      if (err.code === 'discord/token-exchange-failed') {
        return `Token exchange failed: ${err.message}`;
      }
      return `Something went wrong (${err.code}). Check the logs.`;
    }
    if (err instanceof Error) {
      return `Something went wrong: ${err.message}. Check the logs.`;
    }
    return 'Something went wrong. Check the logs.';
  }
}

export const OAUTH_INSTALL_ERROR_COPY = ERROR_COPY;
export const OAUTH_INSTALL_PERMISSIONS = computePermissions();
