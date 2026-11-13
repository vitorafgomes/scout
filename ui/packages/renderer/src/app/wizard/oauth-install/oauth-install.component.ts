import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { Router } from '@angular/router';
import { readIpcError, type DiscordGuild } from '@scout/shared';

import { PersistentStateService } from '../../core/persistent-state';
import { IconComponent } from '../../core/icon.component';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { WIZARD_PAGE_STYLES, WizardFooterComponent } from '../wizard-footer.component';

const POLL_INTERVAL_MS = 3_000;
const MAX_POLLS = 100;

@Component({
  selector: 'sc-oauth-install',
  standalone: true,
  imports: [IconComponent, MatButtonModule, WizardFooterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <div class="heading">
        <h1>Add Scout to your server</h1>
        <p class="lede">Invite the bot to the server where your team will ask it questions.</p>
      </div>

      <section class="card">
        @if (joined(); as name) {
          <p class="joined" role="status">
            <sc-icon name="check" [size]="18" /><span data-test="joined"
              >Scout joined {{ name }}</span
            >
          </p>
        } @else if (waiting()) {
          <div class="waiting" role="status">
            <span class="spinner" aria-hidden="true"></span>
            <span data-test="waiting"
              >Pick your server in the browser and click Authorize. Scout continues on its
              own.</span
            >
          </div>
        } @else {
          <p class="intro">Discord opens in your browser so you can pick the server.</p>
          <button
            type="button"
            matButton="filled"
            class="start"
            data-test="invite"
            (click)="openInvite()"
          >
            <sc-icon name="external" [size]="16" />
            Add Scout to a server
          </button>
        }

        @if (error(); as message) {
          <p class="error" role="alert" data-test="error-copy">{{ message }}</p>
        }

        @if (guilds().length > 0 && joined() === null) {
          <div class="already" data-test="already-in">
            <span class="already-title">Scout is already in these servers</span>
            <span class="muted"
              >Picking one uses it right away; you can change it later in Settings.</span
            >
            <ul class="guilds">
              @for (guild of guilds(); track guild.id) {
                <li>
                  <button
                    type="button"
                    class="guild"
                    data-test="guild-option"
                    (click)="choose(guild)"
                  >
                    <span
                      class="guild-mark"
                      aria-hidden="true"
                      [attr.data-initial]="guild.name.charAt(0).toUpperCase()"
                    ></span>
                    <span class="guild-name">{{ guild.name }}</span>
                    <sc-icon name="forward" [size]="16" />
                  </button>
                </li>
              }
            </ul>
          </div>
        }
      </section>

      <sc-wizard-footer
        [blockedReason]="
          joined() === null ? 'Add Scout to a server, or pick one it is already in.' : null
        "
        (back)="back()"
        (continue)="continueToLaunch()"
      />
    </main>
  `,
  styles: [
    WIZARD_PAGE_STYLES,
    `
      .intro {
        margin: 0;
        color: var(--sc-fg-2);
      }
      .start {
        align-self: flex-start;
      }
      .joined {
        display: flex;
        align-items: center;
        gap: 10px;
        margin: 0;
        padding: 12px;
        border-radius: 10px;
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
        font-weight: 600;
      }
      .waiting {
        display: flex;
        align-items: center;
        gap: 12px;
      }
      .spinner {
        flex: 0 0 20px;
        width: 20px;
        height: 20px;
        box-sizing: border-box;
        border-radius: 10px;
        border: 2px solid var(--sc-surface-2);
        border-top-color: var(--sc-accent);
        animation: sc-spin 0.9s linear infinite;
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
      .error {
        margin: 0;
        color: var(--sc-error);
      }
      .already {
        display: flex;
        flex-direction: column;
        gap: 8px;
        padding-top: 14px;
        border-top: 1px solid var(--sc-line);
      }
      .already-title {
        font-weight: 600;
      }
      .guilds {
        display: flex;
        flex-direction: column;
        gap: 8px;
        margin: 0;
        padding: 0;
        list-style: none;
      }
      .guild {
        display: flex;
        align-items: center;
        gap: 10px;
        width: 100%;
        padding: 10px 12px;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        color: var(--sc-fg);
        font: inherit;
        text-align: left;
        cursor: pointer;
      }
      .guild sc-icon {
        margin: 0;
      }
      .guild-mark {
        display: flex;
        align-items: center;
        justify-content: center;
        width: 28px;
        height: 28px;
        border-radius: 8px;
        background: var(--sc-surface-2);
        color: var(--sc-accent);
        font-size: 13px;
        font-weight: 700;
      }
      .guild-mark::before {
        content: attr(data-initial);
      }
      .guild-name {
        margin-right: auto;
        font-weight: 500;
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
  private opening = false;

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

  /** Opens the invite once and starts one poll chain; a second click while it opens or waits does nothing. */
  async openInvite(): Promise<void> {
    if (this.opening || this.waiting()) return;
    this.opening = true;
    try {
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
    } finally {
      this.opening = false;
    }
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

  continueToLaunch(): void {
    if (this.joined() === null) return;
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
