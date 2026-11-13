import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { Router } from '@angular/router';
import { CLIENT_KINDS, ScoutError, type DetectedClient } from '@scout/shared';

import { IconComponent } from '../../core/icon.component';
import { LogStore } from '../../core/log.store';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore, type WizardStep } from '../../core/wizard.store';
import { WIZARD_PAGE_STYLES, WizardFooterComponent } from '../wizard-footer.component';
import {
  DiscordPlatformCardComponent,
  type DiscordCardState,
} from './discord-platform-card.component';

const DISCORD_DOWNLOAD_URL = 'https://discord.com/download';
const MAX_WORKER_FAILURES = 5;

const MID_WIZARD_STEPS: ReadonlySet<WizardStep> = new Set<WizardStep>([
  'provision',
  'token',
  'oauth',
  'launch',
]);

@Component({
  selector: 'sc-welcome',
  standalone: true,
  imports: [DiscordPlatformCardComponent, IconComponent, MatButtonModule, WizardFooterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <div class="heading">
        <h1>Choose where you chat with Scout</h1>
        <p class="lede">
          Pick the chat app Scout joins. You can add another one later in Settings.
        </p>
      </div>
      @if (error(); as errorMessage) {
        <p class="error">{{ errorMessage }}</p>
      }
      @if (loading()) {
        <p class="muted loading">Detecting clients...</p>
      } @else {
        <div class="platforms" role="radiogroup" aria-label="Chat app" data-test="chat-apps">
          <sc-discord-platform-card
            kind="discord"
            [state]="discordCardState()"
            [guildName]="guildName()"
            [checked]="chosen() === 'discord'"
            (choose)="chosen.set('discord')"
            (download)="onDiscordDownload()"
          />
          <sc-discord-platform-card
            kind="slack"
            [slackConnected]="slackConnected()"
            [checked]="chosen() === 'slack'"
            (choose)="chosen.set('slack')"
          />
          <sc-discord-platform-card kind="teams" />
        </div>
      }
      @if (!loading() && !hasProvisioning()) {
        <section class="local-only" data-test="no-chat-app">
          <div class="local-text">
            <span class="local-title">No chat app?</span>
            <span class="muted"
              >Chat with Scout in its own window. You can connect Discord or Slack later in
              Settings.</span
            >
          </div>
          <button type="button" matButton="outlined" data-test="use-local" (click)="useLocalOnly()">
            <sc-icon name="monitor" [size]="16" />Use on this computer
          </button>
        </section>
      }
      <sc-wizard-footer
        [showBack]="false"
        [blockedReason]="loading() ? 'Detecting the chat apps on this computer.' : null"
        (continue)="continueWithChosen()"
      />
    </main>
  `,
  styles: [
    WIZARD_PAGE_STYLES,
    `
      .platforms {
        display: flex;
        flex-wrap: wrap;
        gap: 12px;
      }
      .platforms sc-discord-platform-card {
        flex: 1 1 200px;
      }
      .error {
        margin: 0;
        color: var(--sc-error);
      }
      .local-only {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: 12px;
        padding: 14px 16px;
        border: 1px dashed var(--sc-line);
        border-radius: 14px;
      }
      .local-only button {
        --mat-button-outlined-container-height: 36px;
      }
      .local-text {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .local-title {
        font-weight: 600;
      }
    `,
  ],
})
export class WelcomeComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly router = inject(Router);
  private readonly wizard = inject(WizardStore);
  private readonly logStore = inject(LogStore);

  /** The chat app the owner chose on this step; Continue sets it up. */
  readonly chosen = signal<'discord' | 'slack'>('discord');
  readonly loading = signal<boolean>(true);
  readonly clients = signal<readonly DetectedClient[]>([]);
  readonly error = signal<string | null>(null);

  private readonly discordClient = computed<DetectedClient | null>(() => {
    const found = this.clients().find((c) => c.kind === 'discord');
    return found ?? null;
  });

  readonly guildName = computed<string | null>(() => this.wizard.guild()?.name ?? null);
  readonly slackConnected = computed<boolean>(() => this.wizard.slackConnected());
  readonly hasProvisioning = computed<boolean>(() => this.wizard.hasProvisioning());

  readonly discordCardState = computed<DiscordCardState>(() => {
    const provisioned = this.wizard.hasDiscord();
    const step = this.wizard.currentStep();
    const worker = this.logStore.status();
    const discord = this.discordClient();
    const installed = discord?.installed ?? false;

    // Mid-wizard flow trumps everything: reflect that provisioning is in
    // progress even when worker status is idle.
    if (MID_WIZARD_STEPS.has(step)) {
      return 'provisioning';
    }

    if (!provisioned) {
      return installed ? 'idle' : 'not-installed';
    }

    // Provisioned branch: let the supervisor state drive the card badge.
    const state = worker.state;
    if (state === 'starting') return 'connecting';
    if (state === 'running') return 'connected';
    if (state === 'crashed' && worker.restarts >= MAX_WORKER_FAILURES) return 'error';
    if (state === 'stopped' || state === 'crashed') return 'disconnected';
    // state === 'idle' while provisioned: worker will be kicked; show connecting.
    return 'connecting';
  });

  ngOnInit(): void {
    this.wizard.goTo('welcome');
    void this.bootstrap();
  }

  /** Continue sets up the chosen chat app: Discord through its card's state, Slack through its setup. */
  continueWithChosen(): void {
    if (this.chosen() === 'slack') this.onSlackCardActivate();
    else this.onDiscordCardActivate();
  }

  onDiscordCardActivate(): void {
    const state = this.discordCardState();
    if (state === 'connected' || state === 'error') {
      void this.router.navigateByUrl('/settings');
      return;
    }
    if (state === 'disconnected') {
      void this.bridge.workerStart().catch(() => {
        // Supervisor events drive the badge; nothing to do here.
      });
      return;
    }
    if (state === 'provisioning') {
      void this.router.navigateByUrl(resumeUrlFor(this.wizard.currentStep()));
      return;
    }
    if (this.wizard.discordNeedsServer()) {
      void this.addToServer();
      return;
    }
    this.wizard.goTo('provision');
    void this.router.navigateByUrl('/provision');
  }

  /** A bot token main kept without a server goes on at Add to server; with no application id known, setup starts over. */
  private async addToServer(): Promise<void> {
    const ready = await this.wizard.prepareAddToServer().catch(() => false);
    if (ready) {
      void this.router.navigateByUrl('/oauth');
      return;
    }
    this.wizard.goTo('provision');
    void this.router.navigateByUrl('/provision');
  }

  useLocalOnly(): void {
    this.wizard.useLocalOnly();
    void this.bridge.workerStart().catch(() => undefined);
    void this.router.navigateByUrl('/chat');
  }

  onSlackCardActivate(): void {
    void this.router.navigateByUrl('/slack');
  }

  onDiscordDownload(): void {
    void this.bridge.appOpenExternal({ url: DISCORD_DOWNLOAD_URL }).catch(() => {
      // Silent: the renderer cannot do better than hand off to the main process.
    });
  }

  private async bootstrap(): Promise<void> {
    // Detection and vault hydration run in parallel; neither gates the other.
    void this.runDetection();
    try {
      const result = await this.wizard.loadFromVault();
      if (result.hasProvisioning && this.logStore.status().state === 'idle') {
        void this.bridge.workerStart().catch(() => {
          // Worker status events will drive the card; nothing more to do.
        });
      }
      const landing = this.wizard.takeLandingUrl();
      if (landing !== null) void this.router.navigateByUrl(landing);
    } catch {
      // loadFromVault already swallows internal errors; this is a belt-and-braces.
    }
  }

  private async runDetection(): Promise<void> {
    try {
      const response = await this.bridge.detectClients();
      this.clients.set(response.clients);
      this.error.set(null);
    } catch (err: unknown) {
      this.clients.set(
        CLIENT_KINDS.map((kind) => ({ kind, installed: false, method: 'none' as const })),
      );
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

function resumeUrlFor(step: WizardStep): string {
  switch (step) {
    case 'provision':
      return '/provision';
    case 'token':
      return '/token';
    case 'oauth':
      return '/oauth';
    case 'launch':
      return '/launch';
    case 'ready':
      return '/ready';
    case 'welcome':
    default:
      return '/provision';
  }
}
