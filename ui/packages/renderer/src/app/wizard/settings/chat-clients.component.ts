import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';

import { LogStore } from '../../core/log.store';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { SlackSetupComponent } from '../slack/slack-setup.component';

/** One card per chat client in Settings: connect or disconnect Discord and Slack in place. */
@Component({
  selector: 'sc-chat-clients',
  standalone: true,
  imports: [SlackSetupComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="clients" data-test="chat-clients" aria-labelledby="chat-clients-title">
      <h2 id="chat-clients-title">Chat clients</h2>

      <article class="client" data-test="client-discord">
        <header>
          <h3>Discord</h3>
          <span class="state" data-test="discord-state">{{
            wizard.hasDiscord() ? 'Connected' : 'Not connected'
          }}</span>
        </header>
        @if (wizard.hasDiscord()) {
          <p class="detail" data-test="discord-server">
            Server: {{ wizard.guild()?.name ?? 'your server' }}
          </p>
          <button
            type="button"
            class="secondary"
            data-test="discord-disconnect"
            [disabled]="discordBusy()"
            (click)="disconnectDiscord()"
          >
            Disconnect
          </button>
        } @else {
          <button type="button" data-test="discord-connect" (click)="connectDiscord()">
            Connect
          </button>
        }
      </article>

      @if (wizard.slackConnected() || slackOpen()) {
        <sc-slack-setup />
      } @else {
        <article class="client" data-test="client-slack">
          <header>
            <h3>Slack</h3>
            <span class="state" data-test="slack-state">Not connected</span>
          </header>
          <button type="button" data-test="slack-connect" (click)="slackOpen.set(true)">
            Connect
          </button>
        </article>
      }

      <article class="client client--soon" data-test="client-teams" aria-disabled="true">
        <header>
          <h3>Teams</h3>
          <span class="state" data-test="teams-state">Coming soon</span>
        </header>
        <button type="button" data-test="teams-connect" disabled>Connect</button>
      </article>
    </section>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .clients {
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      h2 {
        margin: 0;
        font-size: 14px;
        font-weight: 600;
      }
      .client {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 10px;
        padding: 16px;
      }
      .client--soon {
        opacity: 0.6;
      }
      header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        margin-bottom: 8px;
      }
      h3 {
        margin: 0;
        font-size: 14px;
        font-weight: 600;
      }
      .state {
        font-size: 12px;
        color: #8fa3c0;
      }
      .detail {
        font-size: 13px;
        color: #a0aab8;
        margin: 0 0 12px;
      }
    `,
  ],
})
export class ChatClientsComponent {
  readonly wizard = inject(WizardStore);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly logStore = inject(LogStore);
  private readonly router = inject(Router);

  readonly slackOpen = signal(false);
  readonly discordBusy = signal(false);

  connectDiscord(): void {
    this.wizard.goTo('provision');
    void this.router.navigateByUrl('/provision');
  }

  /**
   * Forgets the Discord app only. A running worker is restarted without Discord so Slack and
   * chat on this computer keep answering; with nothing left, the app goes back to Welcome.
   */
  async disconnectDiscord(): Promise<void> {
    const confirmed = window.confirm(
      'This will disconnect Scout from Discord and forget the current Discord app. Continue?',
    );
    if (!confirmed) return;
    this.discordBusy.set(true);
    const state = this.logStore.status().state;
    const wasRunning = state === 'running' || state === 'starting';
    try {
      if (wasRunning) await this.bridge.workerStop().catch(() => undefined);
      await this.wizard.reprovision();
      if (!this.wizard.hasProvisioning()) {
        void this.router.navigateByUrl('/');
        return;
      }
      if (wasRunning) await this.bridge.workerStart().catch(() => undefined);
    } finally {
      this.discordBusy.set(false);
    }
  }
}
