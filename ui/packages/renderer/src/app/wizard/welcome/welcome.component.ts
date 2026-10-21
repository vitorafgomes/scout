import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';
import { CLIENT_KINDS, ScoutError, type DetectedClient } from '@scout/shared';

import { LogStore } from '../../core/log.store';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore, type WizardStep } from '../../core/wizard.store';
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
  imports: [DiscordPlatformCardComponent],
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
          <sc-discord-platform-card
            kind="discord"
            [state]="discordCardState()"
            [guildName]="guildName()"
            (activate)="onDiscordCardActivate()"
            (download)="onDiscordDownload()"
          />
          <sc-discord-platform-card kind="slack" />
          <sc-discord-platform-card kind="teams" />
        }
      </section>
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        background: var(--sc-bg);
        color: var(--sc-text);
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
      .error {
        color: var(--sc-danger);
      }
      .loading {
        color: var(--sc-text-muted);
      }
    `,
  ],
})
export class WelcomeComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly router = inject(Router);
  private readonly wizard = inject(WizardStore);
  private readonly logStore = inject(LogStore);

  readonly loading = signal<boolean>(true);
  readonly clients = signal<readonly DetectedClient[]>([]);
  readonly error = signal<string | null>(null);

  private readonly discordClient = computed<DetectedClient | null>(() => {
    const found = this.clients().find((c) => c.kind === 'discord');
    return found ?? null;
  });

  readonly guildName = computed<string | null>(() => this.wizard.guild()?.name ?? null);

  readonly discordCardState = computed<DiscordCardState>(() => {
    const provisioned = this.wizard.hasProvisioning();
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

  onDiscordCardActivate(): void {
    const state = this.discordCardState();
    if (state === 'connected' || state === 'error') {
      void this.router.navigateByUrl('/ready');
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
    this.wizard.goTo('provision');
    void this.router.navigateByUrl('/provision');
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
