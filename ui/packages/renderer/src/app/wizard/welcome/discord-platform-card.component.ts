import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

import { IconComponent } from '../../core/icon.component';

export type DiscordCardState =
  'not-installed' | 'idle' | 'provisioning' | 'connecting' | 'connected' | 'disconnected' | 'error';

export type PlatformKind = 'discord' | 'slack' | 'teams';

interface BadgeSpec {
  readonly label: string;
  readonly tone: 'neutral' | 'info' | 'success' | 'warn' | 'danger' | 'muted';
  readonly spinner: boolean;
}

const HELPERS: Readonly<Record<PlatformKind, string>> = Object.freeze({
  discord: 'Scout joins a server through a bot you create. About five minutes.',
  slack: 'Scout joins a workspace as a Slack app.',
  teams: 'Not available yet.',
});

const DISCORD_BADGES: Readonly<Record<DiscordCardState, BadgeSpec>> = Object.freeze({
  'not-installed': { label: 'Not installed', tone: 'muted', spinner: false },
  idle: { label: 'Installed', tone: 'success', spinner: false },
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
  imports: [IconComponent],
  template: `
    <article
      class="card"
      [class.card--chosen]="checked"
      [class.card--coming-soon]="kind === 'teams'"
      [attr.data-kind]="kind"
      [attr.data-state]="dataState"
      [attr.aria-disabled]="kind === 'teams' ? 'true' : null"
      [attr.data-test]="'platform-' + kind"
    >
      <header class="card-head">
        <h2>{{ displayName }}</h2>
        @switch (kind) {
          @case ('discord') {
            <span class="badge" [attr.data-tone]="badge.tone" data-test="discord-badge">
              @if (badge.spinner) {
                <span class="spinner" aria-hidden="true"></span>
              }
              <span>{{ badge.label }}</span>
            </span>
          }
          @case ('slack') {
            <span
              class="badge"
              [attr.data-tone]="slackConnected ? 'success' : 'neutral'"
              data-test="slack-badge"
            >
              <span>{{ slackConnected ? 'Connected' : 'Not connected' }}</span>
            </span>
          }
          @default {
            <span class="badge" data-tone="muted"><span>Coming soon</span></span>
          }
        }
      </header>
      <p class="helper" [attr.data-test]="'helper-' + kind">{{ helper }}</p>
      @if (kind === 'discord' && guildName !== null && state === 'connected') {
        <p class="caption" data-test="discord-caption">{{ guildName }}</p>
      }
      @if (kind === 'discord' && state === 'not-installed') {
        <button
          type="button"
          class="download-link"
          data-test="discord-download"
          (click)="download.emit()"
        >
          Download Discord
        </button>
      }
      @if (kind !== 'teams') {
        <label class="choose" [class.choose--on]="checked">
          <input
            type="radio"
            name="chat-app"
            class="choose-input"
            [value]="kind"
            [checked]="checked"
            [attr.aria-label]="ariaLabel"
            [attr.data-test]="'choose-' + kind"
            (change)="choose.emit()"
          />
          <sc-icon [name]="checked ? 'check' : 'plug'" [size]="16" />
          <span>{{ checked ? 'Chosen' : 'Choose ' + displayName }}</span>
        </label>
      }
    </article>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .card {
        height: 100%;
        box-sizing: border-box;
        display: flex;
        flex-direction: column;
        gap: 10px;
        border: 1px solid var(--sc-line);
        border-radius: 14px;
        padding: 16px;
        background: var(--sc-surface);
        color: var(--sc-fg);
      }
      .card--chosen {
        border: 2px solid var(--sc-accent);
        padding: 15px;
      }
      .card--coming-soon {
        opacity: 0.55;
      }
      .helper {
        margin: 0;
        font-size: 13px;
        line-height: 1.45;
        color: var(--sc-muted);
      }
      .choose {
        align-self: flex-start;
        display: inline-flex;
        align-items: center;
        gap: 8px;
        height: 36px;
        padding: 0 14px;
        border-radius: 18px;
        border: 1px solid var(--sc-line);
        color: var(--sc-fg);
        cursor: pointer;
        position: relative;
      }
      .choose--on {
        background: var(--sc-accent-strong);
        border-color: var(--sc-accent-strong);
        color: var(--sc-on-accent);
      }
      .choose:has(.choose-input:focus-visible) {
        outline: 2px solid var(--sc-focus);
        outline-offset: 2px;
      }
      .choose-input {
        position: absolute;
        opacity: 0;
        width: 1px;
        height: 1px;
        margin: 0;
      }
      .card-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
      }
      h2 {
        font-size: 15px;
        font-weight: 600;
        margin: 0;
      }
      .caption {
        font-size: 13px;
        margin: 0;
        color: var(--sc-fg-2);
      }
      .badge {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        border-radius: 12px;
        padding: 3px 10px;
        font-size: 12px;
        white-space: nowrap;
      }
      .badge[data-tone='neutral'] {
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .badge[data-tone='muted'] {
        background: var(--sc-surface-2);
        color: var(--sc-muted);
      }
      .badge[data-tone='info'] {
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
      }
      .badge[data-tone='success'] {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .badge[data-tone='warn'] {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .badge[data-tone='danger'] {
        background: var(--sc-error-container);
        color: var(--sc-error);
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
        align-self: flex-start;
        background: transparent;
        border: none;
        padding: 0;
        color: var(--sc-accent);
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
  @Input() slackConnected = false;

  /** True while this chat app is the one the owner chose on the step. */
  @Input() checked = false;

  @Output() readonly choose = new EventEmitter<void>();
  @Output() readonly download = new EventEmitter<void>();

  get badge(): BadgeSpec {
    return DISCORD_BADGES[this.state];
  }

  get displayName(): string {
    if (this.kind === 'slack') return 'Slack';
    if (this.kind === 'teams') return 'Microsoft Teams';
    return 'Discord';
  }

  get helper(): string {
    return HELPERS[this.kind];
  }

  /** The radio's name for a screen reader: the chat app and its state, and the server once connected. */
  get ariaLabel(): string {
    if (this.kind === 'slack')
      return `Slack, ${this.slackConnected ? 'Connected' : 'Not connected'}`;
    const base = `Discord, ${this.badge.label}`;
    if (this.state === 'connected' && this.guildName !== null) {
      return `${base}, connected to ${this.guildName}`;
    }
    return base;
  }

  /** The card's state for styling and tests. */
  get dataState(): string {
    if (this.kind === 'teams') return 'coming-soon';
    if (this.kind === 'slack') return this.slackConnected ? 'connected' : 'not-connected';
    return this.state;
  }
}

export const DISCORD_CARD_BADGES = DISCORD_BADGES;
