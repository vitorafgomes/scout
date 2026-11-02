import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';
import { readIpcError, type DiscordGuild } from '@scout/shared';

import { PersistentStateService } from '../../core/persistent-state';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

const POLL_INTERVAL_MS = 3_000;
const MAX_POLLS = 100;

@Component({
  selector: 'sc-oauth-install',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="oauth-install">
      <h1>Add Scout to your server</h1>
      <p class="subtitle">Step 3 of 3</p>

      <section class="panel">
        @if (joined(); as name) {
          <p class="ok" data-test="joined">Scout joined {{ name }}</p>
        } @else if (waiting()) {
          <div class="spinner" aria-hidden="true"></div>
          <p data-test="waiting">
            Pick your server in the browser and click Authorize. Scout continues on its own.
          </p>
        } @else {
          <p>Discord opens in your browser so you can pick the server.</p>
          <button type="button" class="primary" data-test="invite" (click)="openInvite()">
            Add Scout to a server
          </button>
        }

        @if (error(); as message) {
          <p class="error" role="alert" data-test="error-copy">{{ message }}</p>
        }

        @if (guilds().length > 0 && joined() === null) {
          <p class="hint">Scout is already in these servers. Pick one to use:</p>
          <ul class="guilds">
            @for (guild of guilds(); track guild.id) {
              <li>
                <button
                  type="button"
                  class="secondary"
                  data-test="guild-option"
                  (click)="choose(guild)"
                >
                  {{ guild.name }}
                </button>
              </li>
            }
          </ul>
        }

        <div class="actions">
          <button type="button" class="ghost" data-test="back" (click)="back()">Back</button>
        </div>
      </section>
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
      .ok {
        color: #3aaf7b;
      }
      .hint {
        color: #7b8299;
        font-size: 13px;
      }
      .guilds {
        list-style: none;
        padding: 0;
        margin: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
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
export class OAuthInstallComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly router = inject(Router);
  private readonly wizard = inject(WizardStore);
  private readonly persistent = inject(PersistentStateService);

  readonly guilds = signal<readonly DiscordGuild[]>([]);
  readonly waiting = signal<boolean>(false);
  readonly joined = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  private known = new Set<string>();
  private baseline: Promise<boolean> = Promise.resolve(false);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private polls = 0;
  private destroyed = false;

  ngOnInit(): void {
    this.wizard.goTo('oauth');
    if (this.wizard.clientId() === null) {
      void this.router.navigateByUrl('/');
      return;
    }
    this.baseline = this.loadGuilds();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.stopPolling();
  }

  async openInvite(): Promise<void> {
    this.error.set(null);
    if (!(await this.baseline)) {
      this.baseline = this.loadGuilds();
      if (!(await this.baseline)) return;
    }
    try {
      const { url } = await this.bridge.discordInviteUrl();
      await this.bridge.appOpenExternal({ url });
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
      return;
    }
    this.polls = 0;
    this.waiting.set(true);
    this.schedulePoll();
  }

  async choose(guild: DiscordGuild): Promise<void> {
    this.stopPolling();
    const clientId = this.wizard.clientId();
    if (clientId === null) return;
    try {
      await this.bridge.vaultStore({ key: 'discord.guild-id', value: guild.id });
      await this.bridge.vaultStore({ key: 'discord.guild-name', value: guild.name });
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
      return;
    }
    this.wizard.setGuild(guild);
    this.persistent.set({ clientId, guildId: guild.id, guildName: guild.name });
    this.joined.set(guild.name);
    this.wizard.goTo('launch');
    void this.router.navigateByUrl('/launch');
  }

  back(): void {
    this.stopPolling();
    void this.router.navigateByUrl('/token');
  }

  private async loadGuilds(): Promise<boolean> {
    try {
      const { guilds } = await this.bridge.discordGuilds();
      this.known = new Set(guilds.map((g) => g.id));
      this.guilds.set(guilds);
      return true;
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
      return false;
    }
  }

  private schedulePoll(): void {
    if (this.destroyed) return;
    this.timer = setTimeout(() => void this.poll(), POLL_INTERVAL_MS);
  }

  private stopPolling(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
    this.waiting.set(false);
  }

  private async poll(): Promise<void> {
    this.timer = null;
    if (this.destroyed) return;
    this.polls += 1;
    try {
      const { guilds } = await this.bridge.discordGuilds();
      if (this.destroyed || !this.waiting()) return;
      const added = guilds.find((g) => !this.known.has(g.id));
      if (added !== undefined) {
        await this.choose(added);
        return;
      }
    } catch (err: unknown) {
      const { code, message } = readIpcError(err);
      if (code === 'discord/token-rejected') {
        this.stopPolling();
        this.error.set(message);
        return;
      }
    }
    if (this.destroyed || !this.waiting()) return;
    if (this.polls >= MAX_POLLS) {
      this.stopPolling();
      this.error.set(
        'Scout has not joined a server yet. Click Add Scout to a server to try again.',
      );
      return;
    }
    this.schedulePoll();
  }
}
