import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

export type DiscordCardState =
  'not-installed' | 'idle' | 'provisioning' | 'connecting' | 'connected' | 'disconnected' | 'error';

export type PlatformKind = 'discord' | 'slack' | 'teams';

interface BadgeSpec {
  readonly label: string;
  readonly tone: 'neutral' | 'info' | 'success' | 'warn' | 'danger' | 'muted';
  readonly spinner: boolean;
}

const DISCORD_BADGES: Readonly<Record<DiscordCardState, BadgeSpec>> = Object.freeze({
  'not-installed': { label: 'Not installed', tone: 'muted', spinner: false },
  idle: { label: 'Installed', tone: 'neutral', spinner: false },
  provisioning: { label: 'Provisioning', tone: 'info', spinner: false },
  connecting: { label: 'Connecting', tone: 'info', spinner: true },
  connected: { label: 'Connected', tone: 'success', spinner: false },
  disconnected: { label: 'Disconnected', tone: 'warn', spinner: false },
  error: { label: 'Error', tone: 'danger', spinner: false },
});

@Component({
  selector: 'sc-discord-platform-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (kind === 'discord') {
      <article
        class="card card--discord"
        [attr.data-kind]="'discord'"
        [attr.data-state]="state"
        role="button"
        tabindex="0"
        [attr.aria-label]="ariaLabel"
        (click)="onActivate()"
        (keydown)="onKeydown($event)"
      >
        <header class="card-head">
          <h2>Discord</h2>
          <span class="badge" [attr.data-tone]="badge.tone" data-test="discord-badge">
            @if (badge.spinner) {
              <span class="spinner" aria-hidden="true"></span>
            }
            <span>{{ badge.label }}</span>
          </span>
        </header>
        @if (guildName !== null && state === 'connected') {
          <p class="caption" data-test="discord-caption">{{ guildName }}</p>
        }
        @if (state === 'not-installed') {
          <button
            type="button"
            class="download-link"
            data-test="discord-download"
            (click)="onDownloadClick($event)"
          >
            Download Discord
          </button>
        }
      </article>
    } @else {
      <article
        class="card card--coming-soon"
        [attr.data-kind]="kind"
        [attr.data-state]="'coming-soon'"
        aria-disabled="true"
      >
        <header class="card-head">
          <h2>{{ displayName }}</h2>
          <span class="badge" data-tone="muted">
            <span>Coming soon</span>
          </span>
        </header>
      </article>
    }
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .card {
        border: 1px solid #252b3b;
        border-radius: 10px;
        padding: 16px;
        background: #171b26;
        color: #e4e8f0;
        cursor: pointer;
        outline: none;
        transition:
          border-color 120ms ease,
          transform 120ms ease;
      }
      .card:hover,
      .card:focus-visible {
        border-color: #7c5cff;
      }
      .card:focus-visible {
        box-shadow: 0 0 0 2px #7c5cff44;
      }
      .card--coming-soon {
        cursor: default;
        opacity: 0.55;
        pointer-events: none;
      }
      .card-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
      }
      h2 {
        font-size: 16px;
        margin: 0;
      }
      .caption {
        font-size: 13px;
        margin: 8px 0 0;
        color: #a0aab8;
      }
      .badge {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        border-radius: 999px;
        padding: 2px 10px;
        font-size: 12px;
        font-weight: 600;
        letter-spacing: 0.02em;
      }
      .badge[data-tone='neutral'] {
        background: #1e2230;
        color: #a0aab8;
      }
      .badge[data-tone='muted'] {
        background: #141821;
        color: #6a7a90;
      }
      .badge[data-tone='info'] {
        background: #1e3a5f;
        color: #5eb3f0;
      }
      .badge[data-tone='success'] {
        background: #0f3325;
        color: #38d27a;
      }
      .badge[data-tone='warn'] {
        background: #3a2e0f;
        color: #f0c05e;
      }
      .badge[data-tone='danger'] {
        background: #3d1a1a;
        color: #f07070;
      }
      .spinner {
        width: 10px;
        height: 10px;
        border-radius: 9999px;
        border: 2px solid transparent;
        border-top-color: currentColor;
        animation: sc-card-spin 1s linear infinite;
      }
      @keyframes sc-card-spin {
        to {
          transform: rotate(360deg);
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .spinner {
          animation-duration: 0ms;
        }
      }
      .download-link {
        margin-top: 12px;
        background: transparent;
        border: none;
        padding: 0;
        color: #7c5cff;
        font-size: 13px;
        cursor: pointer;
        text-decoration: underline;
      }
    `,
  ],
})
export class DiscordPlatformCardComponent {
  @Input({ required: true }) kind!: PlatformKind;
  @Input() state: DiscordCardState = 'idle';
  @Input() guildName: string | null = null;

  @Output() readonly activate = new EventEmitter<void>();
  @Output() readonly download = new EventEmitter<void>();

  get badge(): BadgeSpec {
    return DISCORD_BADGES[this.state];
  }

  get displayName(): string {
    if (this.kind === 'slack') return 'Slack';
    if (this.kind === 'teams') return 'Teams';
    return 'Discord';
  }

  get ariaLabel(): string {
    const base = `Discord platform, ${this.badge.label}`;
    if (this.state === 'connected' && this.guildName !== null) {
      return `${base}, connected to ${this.guildName}`;
    }
    return base;
  }

  onActivate(): void {
    if (this.kind !== 'discord') return;
    this.activate.emit();
  }

  onKeydown(event: KeyboardEvent): void {
    if (this.kind !== 'discord') return;
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.activate.emit();
    }
  }

  onDownloadClick(event: Event): void {
    event.stopPropagation();
    this.download.emit();
  }
}

export const DISCORD_CARD_BADGES = DISCORD_BADGES;
