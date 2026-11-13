import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { RESTART_DEFERRED_LINE, readIpcError, type SlackStatus } from '@scout/shared';
import { firstValueFrom } from 'rxjs';

import { confirmAction } from '../../core/confirm-dialog.component';
import { IconComponent } from '../../core/icon.component';
import { LogStore } from '../../core/log.store';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { StatusChipComponent } from '../../core/status-chip.component';
import { WizardStore } from '../../core/wizard.store';
import {
  ChatConnectionDialogComponent,
  type ChatConnectionDialogData,
  type ChatConnectionSaved,
  type ChatPlatform,
} from './chat-connection-dialog.component';

/**
 * The Chat connections section of Settings: one card each for Discord, Slack and Microsoft Teams with
 * its state, Connect or Manage opening a modal that asks for the credentials, and Disconnect asking
 * first.
 */
@Component({
  selector: 'sc-chat-clients',
  standalone: true,
  imports: [IconComponent, StatusChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="clients" data-test="chat-clients" aria-labelledby="chat-clients-title">
      <h2 id="chat-clients-title" data-test="chat-clients-title">Chat connections</h2>
      <p class="lede">
        Where Scout answers. Credentials are entered here and kept on this computer.
      </p>

      <article class="client" data-test="client-discord" aria-labelledby="chat-discord">
        <div class="info">
          <div class="name">
            <h3 id="chat-discord">Discord</h3>
            <sc-status-chip
              data-test="discord-state"
              [tone]="wizard.hasDiscord() ? 'ok' : 'neutral'"
              [label]="
                wizard.hasDiscord()
                  ? 'Connected'
                  : wizard.discordNeedsServer()
                    ? 'Needs a server'
                    : 'Not connected'
              "
            />
          </div>
          @if (wizard.hasDiscord()) {
            <span class="detail" data-test="discord-detail"
              ><span data-test="discord-server"
                >Server: {{ wizard.guild()?.name ?? 'your server' }}</span
              ></span
            >
          } @else if (wizard.discordNeedsServer()) {
            <span class="detail" data-test="discord-detail"
              >Scout is in no server yet. Add it to the server it answers in.</span
            >
          } @else {
            <span class="detail" data-test="discord-detail">Answer in your Discord server.</span>
          }
        </div>
        @if (wizard.hasDiscord()) {
          <button
            type="button"
            class="btn"
            data-test="discord-manage"
            (click)="openConnection('discord', true)"
          >
            <sc-icon name="settings" [size]="17" />
            Manage
          </button>
          <button
            type="button"
            class="btn btn--danger"
            data-test="discord-disconnect"
            [disabled]="discordBusy()"
            (click)="disconnectDiscord()"
          >
            <sc-icon name="plug-off" [size]="17" />
            Disconnect
          </button>
        } @else if (wizard.discordNeedsServer()) {
          <button
            type="button"
            class="btn btn--primary"
            data-test="discord-add-server"
            (click)="addDiscordToServer()"
          >
            <sc-icon name="plug" [size]="17" />
            Add to server
          </button>
          <button
            type="button"
            class="btn btn--danger"
            data-test="discord-disconnect"
            [disabled]="discordBusy()"
            (click)="disconnectDiscord()"
          >
            <sc-icon name="plug-off" [size]="17" />
            Disconnect
          </button>
        } @else {
          <button
            type="button"
            class="btn btn--primary"
            data-test="discord-connect"
            (click)="openConnection('discord', false)"
          >
            <sc-icon name="plug" [size]="17" />
            Connect
          </button>
        }
      </article>

      <article class="client" data-test="client-slack" aria-labelledby="chat-slack">
        <div class="info">
          <div class="name">
            <h3 id="chat-slack">Slack</h3>
            <sc-status-chip
              data-test="slack-state"
              [tone]="wizard.slackConnected() ? 'ok' : 'neutral'"
              [label]="wizard.slackConnected() ? 'Connected' : 'Not connected'"
            />
          </div>
          <span class="detail" data-test="slack-detail">{{
            wizard.slackConnected()
              ? 'Workspace: ' + (slack()?.team ?? 'your workspace')
              : 'Answer in your Slack workspace.'
          }}</span>
        </div>
        @if (wizard.slackConnected()) {
          <button
            type="button"
            class="btn"
            data-test="slack-manage"
            (click)="openConnection('slack', true)"
          >
            <sc-icon name="settings" [size]="17" />
            Manage
          </button>
          <button
            type="button"
            class="btn btn--danger"
            data-test="slack-disconnect"
            [disabled]="slackBusy()"
            (click)="disconnectSlack()"
          >
            <sc-icon name="plug-off" [size]="17" />
            Disconnect
          </button>
        } @else {
          <button
            type="button"
            class="btn btn--primary"
            data-test="slack-connect"
            (click)="openConnection('slack', false)"
          >
            <sc-icon name="plug" [size]="17" />
            Connect
          </button>
        }
      </article>

      <article class="client" data-test="client-teams" aria-labelledby="chat-teams">
        <div class="info">
          <div class="name">
            <h3 id="chat-teams">Microsoft Teams</h3>
            <sc-status-chip data-test="teams-state" tone="neutral" label="Not connected" />
          </div>
          <span class="detail" data-test="teams-detail">Answer in Microsoft Teams.</span>
        </div>
        <button
          type="button"
          class="btn btn--primary"
          data-test="teams-connect"
          (click)="openConnection('teams', false)"
        >
          <sc-icon name="plug" [size]="17" />
          Connect
        </button>
      </article>
      @if (error(); as message) {
        <p class="error" role="alert" data-test="chat-clients-error">{{ message }}</p>
      }
      @if (restartNote(); as note) {
        <p class="note" role="status" data-test="chat-clients-restart">{{ note }}</p>
      }
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
        gap: 4px;
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 16px;
        color: var(--sc-fg);
      }
      h2 {
        margin: 0;
        font-size: 15px;
        font-weight: 600;
      }
      .lede {
        margin: 0 0 8px;
        font-size: 13px;
        color: var(--sc-muted);
      }
      .client {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        padding: 12px 0;
        border-top: 1px solid var(--sc-line);
      }
      .info {
        display: flex;
        flex-direction: column;
        gap: 4px;
        margin-right: auto;
        min-width: 0;
      }
      .name {
        display: flex;
        align-items: center;
        gap: 8px;
      }
      h3 {
        margin: 0;
        font-size: 14px;
        font-weight: 600;
      }
      .detail {
        font-size: 13px;
        color: var(--sc-muted);
      }
      .btn {
        height: 40px;
        padding: 0 16px;
        border-radius: 20px;
        display: inline-flex;
        align-items: center;
        gap: 8px;
        font: inherit;
        font-weight: 500;
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg);
        cursor: pointer;
      }
      .btn--primary {
        border-color: transparent;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
      }
      .btn--danger {
        color: var(--sc-error);
      }
      .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
      .error {
        margin: 8px 0 0;
        font-size: 13px;
        color: var(--sc-error);
      }
      .note {
        margin: 8px 0 0;
        font-size: 13px;
        color: var(--sc-muted);
      }
    `,
  ],
})
export class ChatClientsComponent implements OnInit {
  readonly wizard = inject(WizardStore);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly logStore = inject(LogStore);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);

  readonly slack = signal<SlackStatus | null>(null);
  readonly discordBusy = signal(false);
  readonly slackBusy = signal(false);
  readonly error = signal<string | null>(null);
  /** Main's answer to the restart a Disconnect asked for, when it did not restart at once. */
  readonly restartNote = signal<string | null>(null);

  ngOnInit(): void {
    void this.readSlack();
  }

  /**
   * Opens the credentials modal of one chat connection; a Slack save there refreshes its workspace, and a save's notice
   * shows under the cards.
   */
  async openConnection(platform: ChatPlatform, connected: boolean): Promise<void> {
    const ref = this.dialog.open<
      ChatConnectionDialogComponent,
      ChatConnectionDialogData,
      ChatConnectionSaved
    >(ChatConnectionDialogComponent, {
      data: { platform, connected },
      width: '480px',
      maxWidth: 'calc(100vw - 32px)',
      position: { top: '120px' },
      panelClass: 'sc-dialog',
      backdropClass: 'sc-dialog-backdrop',
    });
    const saved = await firstValueFrom(ref.afterClosed());
    if (saved === undefined) return;
    this.error.set(saved.notice);
    if (platform === 'slack') await this.readSlack();
  }

  /** Opens the wizard step that adds the bot to a server, for a bot token main kept without a server. */
  async addDiscordToServer(): Promise<void> {
    this.error.set(null);
    try {
      if (await this.wizard.prepareAddToServer()) void this.router.navigateByUrl('/oauth');
      else this.error.set('Scout could not read which Discord app to add. Connect Discord again.');
    } catch (err: unknown) {
      this.error.set(`Scout could not read which Discord app to add: ${readIpcError(err).message}`);
    }
  }

  /**
   * Forgets the Discord app only, once the owner confirmed. A running worker is restarted without
   * Discord so Slack and chat on this computer keep answering; with nothing left, the app goes back
   * to Welcome. A bot token that cannot be deleted keeps Discord connected and says why, and the
   * worker is started again with it; a restart that fails says why under the cards.
   */
  async disconnectDiscord(): Promise<void> {
    const server = this.wizard.guild()?.name ?? 'your server';
    const confirmed = await confirmAction(this.dialog, {
      title: 'Disconnect Discord?',
      body: `Scout forgets the Discord app and stops answering in ${server}. To use Discord again you paste the bot token and add the bot to the server again.`,
      confirmLabel: 'Disconnect',
      confirmIcon: 'plug-off',
    });
    if (!confirmed) return;
    this.discordBusy.set(true);
    this.error.set(null);
    this.restartNote.set(null);
    const state = this.logStore.status().state;
    const wasRunning = state === 'running' || state === 'starting';
    try {
      try {
        await this.wizard.reprovision();
      } catch (err: unknown) {
        this.error.set(`Discord is still connected: ${readIpcError(err).message}`);
        return;
      }
      if (!this.wizard.hasProvisioning()) {
        if (wasRunning) await this.bridge.workerStop().catch(() => undefined);
        void this.router.navigateByUrl('/');
        return;
      }
      const result = await this.bridge
        .workerRestart({ reason: 'Discord disconnected' })
        .catch((err: unknown) => ({
          outcome: 'failed' as const,
          reason: err instanceof Error ? err.message : 'Could not restart Scout.',
        }));
      this.restartNote.set(
        result.outcome === 'deferred'
          ? RESTART_DEFERRED_LINE
          : result.outcome === 'failed'
            ? `Scout could not restart: ${result.reason}`
            : null,
      );
    } finally {
      this.discordBusy.set(false);
    }
  }

  /**
   * Drops Slack only, once the owner confirmed. With no chat app left and Scout not kept for this
   * computer, it stops the worker and goes back to Welcome, as Disconnect Discord does.
   */
  async disconnectSlack(): Promise<void> {
    const team = this.slack()?.team;
    const confirmed = await confirmAction(this.dialog, {
      title: 'Disconnect Slack?',
      body: `Scout leaves ${team == null ? 'your workspace' : `the ${team} workspace`} and stops answering there. The tokens are removed from this computer; the Slack app stays in your workspace until you delete it.`,
      confirmLabel: 'Disconnect',
      confirmIcon: 'plug-off',
    });
    if (!confirmed) return;
    this.slackBusy.set(true);
    this.error.set(null);
    try {
      const status = await this.bridge.slackDisconnect();
      this.slack.set(status);
      this.wizard.setSlackConnected(status.connected);
      if (!this.wizard.hasProvisioning()) {
        const state = this.logStore.status().state;
        if (state === 'running' || state === 'starting') {
          await this.bridge.workerStop().catch(() => undefined);
        }
        void this.router.navigateByUrl('/');
      }
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    } finally {
      this.slackBusy.set(false);
    }
  }

  private async readSlack(): Promise<void> {
    try {
      this.slack.set(await this.bridge.slackStatus());
    } catch {
      this.slack.set(null);
    }
  }
}
