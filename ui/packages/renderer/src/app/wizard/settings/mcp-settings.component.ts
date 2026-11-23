import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatSlideToggleModule, type MatSlideToggleChange } from '@angular/material/slide-toggle';
import { readIpcError, type McpActivityRow, type McpStartMode } from '@scout/shared';

import { IconComponent } from '../../core/icon.component';
import { McpStore } from '../../core/mcp.store';
import { StatusChipComponent, type ChipTone } from '../../core/status-chip.component';
import { SETTINGS_SWITCH_STYLES } from './settings-switch.styles';

/** What an assistant can do, as the group lists it. */
interface McpAbility {
  readonly title: string;
  readonly offered: boolean;
  readonly what: string;
  readonly asks: string;
}

const START_MODES: readonly { readonly mode: McpStartMode; readonly label: string }[] = [
  { mode: 'ask', label: 'Ask me each time' },
  { mode: 'free', label: 'Start without asking' },
];

const TOOL_WORDS: Readonly<Record<string, string>> = {
  list_reviews: 'List the reviews',
  start_review: 'Start a review',
  get_review: 'Read the review card',
  preview_note: 'Read the note preview',
  request: 'A request',
};

const RESULT_WORDS: Readonly<Record<string, string>> = {
  listed: 'Listed the reviews and audits.',
  read: 'Read the card.',
  previewed: 'Read the note.',
  'no card': 'No review card of it was held.',
  'refused: unknown finding': 'Refused: the card has no such finding.',
  'refused: a preview is running': 'Refused: a preview of it was being composed.',
  'refused: the worker could not compose it': 'Refused: Scout could not compose the note.',
  'refused: invalid argument': 'Refused: an argument was not valid.',
  started: 'Started.',
  'refused: by the owner': 'Refused by you.',
  'refused: no answer': 'Refused: you did not answer within 2 minutes.',
  'refused: Scout is busy': 'Refused: Scout was answering another question.',
  'refused: another start is waiting': 'Refused: another start was waiting for you.',
  'refused: not a pull request link': 'Refused: not a pull request link.',
  'refused: no such tool': 'Refused: Scout offers no such tool to an assistant.',
  'refused 401': 'Refused: no token, or a wrong one.',
  'refused 403': 'Refused: from a web page, or to another host name.',
};

const clock = (at: number): string =>
  new Date(at).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

/** When a token was created, as the token line says it: today at 11:40, or on 18 Nov 2026 at 11:40. */
export function mcpCreatedAt(at: number, now: number): string {
  const day = (time: number) => new Date(time).toDateString();
  if (day(at) === day(now)) return `today at ${clock(at)}`;
  const date = new Date(at).toLocaleDateString('en-GB', {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
  });
  return `on ${date} at ${clock(at)}`;
}

/** The settings an assistant needs, with the token as a bearer header, in the form most assistants read. */
export function mcpAssistantSettings(port: number, token: string): string {
  return JSON.stringify(
    {
      mcpServers: {
        scout: {
          type: 'http',
          url: `http://127.0.0.1:${port}/mcp`,
          headers: { Authorization: `Bearer ${token}` },
        },
      },
    },
    null,
    2,
  );
}

/**
 * The Settings group of the MCP server, as the Settings artboard draws it (section `s-mcp`). The token a new one answers
 * lives only in this component's `shown` signal: it is gone when the owner copies it or this component is destroyed.
 */
@Component({
  selector: 'sc-mcp-settings',
  standalone: true,
  imports: [IconComponent, MatSlideToggleModule, StatusChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card" data-test="mcp-section" aria-labelledby="s-mcp">
      <header class="head">
        <h2 id="s-mcp">MCP server</h2>
        <sc-status-chip data-test="mcp-chip" [tone]="chip().tone" [label]="chip().label" />
      </header>
      <p class="intro">
        A coding assistant on this computer asks Scout for a review and follows it, through the
        Model Context Protocol. It never writes on GitHub: a verdict, a comment and an issue are
        given only from this app, with your approval.
      </p>
      <div class="switch-row">
        <span id="mcp-on" class="switch-label">Let an assistant on this computer use Scout</span>
        <mat-slide-toggle
          data-test="mcp-switch"
          aria-labelledby="mcp-on"
          hideIcon
          [checked]="checked()"
          (change)="setEnabled($event)"
        />
      </div>
      @if (failedPort(); as port) {
        <p class="error" role="alert" data-test="mcp-failed">
          The MCP server did not start: port {{ port }} on this computer is in use. Close what uses
          it, or let Scout pick another port.
          <button type="button" class="btn" data-test="mcp-pick-port" (click)="pickPort()">
            Pick another port and start
          </button>
        </p>
      }
      @if (error(); as message) {
        <p class="error" role="alert" data-test="mcp-error">
          <sc-icon name="alert" [size]="15" />{{ message }}
        </p>
      }
      @if (listeningPort(); as port) {
        <dl class="facts">
          <dt>Address</dt>
          <dd>
            <code data-test="mcp-address">http://127.0.0.1:{{ port }}/mcp</code>
          </dd>
          <dt>Reachable from</dt>
          <dd>This computer only</dd>
          <dt>Token</dt>
          <dd data-test="mcp-token-line">{{ tokenLine() }}</dd>
        </dl>
        @if (shown(); as token) {
          <div class="token" role="status" data-test="mcp-token">
            <span>Copy the token now. Scout does not show it again.</span>
            <code>{{ token }}</code>
            <div class="actions">
              <button type="button" class="btn" data-test="mcp-copy-token" (click)="copy(token)">
                Copy the token
              </button>
              <button
                type="button"
                class="btn"
                data-test="mcp-copy-settings"
                (click)="copy(settings(port, token))"
              >
                Copy the settings for an assistant
              </button>
            </div>
          </div>
        }
        <div class="actions">
          <button type="button" class="btn" data-test="mcp-create-token" (click)="createToken()">
            {{ hasToken() ? 'Create a new token' : 'Create a token' }}
          </button>
          @if (hasToken()) {
            <button type="button" class="btn" data-test="mcp-revoke" (click)="revokeToken()">
              Revoke the token
            </button>
          }
        </div>
        <p class="muted">
          A new token replaces the one before it. An assistant with no token, or an old one, is
          refused.
        </p>
        <h3>What an assistant can do</h3>
        @for (ability of abilities(); track ability.title) {
          <div class="ability" data-test="mcp-ability">
            <div class="ability-head">
              <span class="ability-title">{{ ability.title }}</span>
              <sc-status-chip
                [tone]="ability.offered ? 'ok' : 'neutral'"
                [label]="ability.offered ? 'Offered' : 'Not offered'"
              />
            </div>
            <span class="muted">{{ ability.what }}</span>
            <span class="muted">{{ ability.asks }}</span>
          </div>
        }
        <div class="mode-row">
          <span id="mcp-start">When an assistant starts a review</span>
          <div class="modes" role="group" aria-labelledby="mcp-start">
            @for (option of modes; track option.mode) {
              <button
                type="button"
                class="mode"
                [attr.data-test]="'mcp-mode-' + option.mode"
                [attr.aria-pressed]="startMode() === option.mode"
                (click)="setStartMode(option.mode)"
              >
                {{ option.label }}
              </button>
            }
          </div>
        </div>
        <h3>What assistants did</h3>
        @if (activity().length === 0) {
          <p class="muted" data-test="mcp-nothing-yet">
            Nothing yet. Each request of an assistant is listed here, the refused ones too.
          </p>
        } @else {
          <div class="activity" role="table" aria-label="What assistants did">
            @for (row of activity(); track $index) {
              <div class="activity-row" role="row" data-test="mcp-activity-row">
                <span role="cell">{{ row.when }}</span>
                <span role="cell">{{ row.what }}</span>
                <code role="cell">{{ row.target }}</code>
                <span role="cell" [class.refused]="row.refused">{{ row.result }}</span>
              </div>
            }
          </div>
        }
      }
    </section>
  `,
  styles: [
    SETTINGS_SWITCH_STYLES,
    `
      :host {
        display: block;
      }
      .card {
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 16px;
        color: var(--sc-fg);
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .head {
        display: flex;
        align-items: center;
        gap: 8px;
      }
      h2 {
        margin: 0;
        margin-right: auto;
        font-size: 15px;
        font-weight: 600;
      }
      h3 {
        margin: 4px 0 0;
        font-size: 13px;
        font-weight: 600;
      }
      .intro,
      .muted {
        color: var(--sc-muted);
        font-size: 13px;
        line-height: 1.5;
        margin: 0;
      }
      .facts {
        display: grid;
        grid-template-columns: max-content 1fr;
        gap: 6px 16px;
        margin: 0;
        font-size: 13px;
      }
      .facts dt {
        color: var(--sc-muted);
      }
      .facts dd {
        margin: 0;
      }
      code {
        font-family: var(--sc-font-mono, monospace);
        font-size: 12px;
        overflow-wrap: anywhere;
      }
      .token {
        display: flex;
        flex-direction: column;
        gap: 8px;
        padding: 12px;
        border-radius: var(--sc-radius);
        background: var(--sc-surface-2);
        font-size: 13px;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      .btn,
      .mode {
        height: 40px;
        padding: 0 16px;
        border-radius: 20px;
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg);
        font: inherit;
        font-weight: 500;
        cursor: pointer;
      }
      .mode[aria-pressed='true'] {
        border-color: transparent;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
      }
      .ability {
        display: flex;
        flex-direction: column;
        gap: 4px;
        padding-top: 8px;
        border-top: 1px solid var(--sc-line);
      }
      .ability-head {
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .ability-title {
        font-weight: 500;
        margin-right: auto;
      }
      .mode-row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px 12px;
      }
      .modes {
        display: flex;
        gap: 8px;
      }
      .activity {
        display: flex;
        flex-direction: column;
        font-size: 13px;
      }
      .activity-row {
        display: grid;
        grid-template-columns: 48px 140px minmax(0, 1fr) minmax(0, 2fr);
        gap: 8px;
        padding: 6px 0;
        border-top: 1px solid var(--sc-line);
      }
      .refused {
        color: var(--sc-error);
      }
      .error {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 6px;
        color: var(--sc-error);
        font-size: 13px;
        margin: 0;
      }
    `,
  ],
})
export class McpSettingsComponent {
  private readonly mcp = inject(McpStore);
  private readonly status = this.mcp.status;

  /** The token a new one answered, until the owner copies it or this component goes. */
  readonly shown = signal<string | null>(null);
  /** Why the last call failed, in main's words; null when it did not. */
  readonly error = signal<string | null>(null);
  readonly modes = START_MODES;
  readonly settings = mcpAssistantSettings;

  readonly checked = computed(() => {
    const status = this.status();
    return status !== null && status.enabled && status.listener.state !== 'failed';
  });
  readonly listeningPort = computed(() => {
    const listener = this.status()?.listener;
    return listener?.state === 'listening' ? listener.port : null;
  });
  readonly failedPort = computed(() => {
    const listener = this.status()?.listener;
    return listener?.state === 'failed' ? listener.port : null;
  });
  readonly chip = computed<{ readonly tone: ChipTone; readonly label: string }>(() =>
    this.failedPort() !== null
      ? { tone: 'error', label: 'Did not start' }
      : this.listeningPort() !== null
        ? { tone: 'ok', label: 'Active' }
        : { tone: 'neutral', label: 'Off' },
  );
  readonly hasToken = computed(() => this.status()?.hasToken === true);
  readonly startMode = computed<McpStartMode>(() => this.status()?.startMode ?? 'ask');
  readonly tokenLine = computed(() => {
    const created = this.status()?.tokenCreatedAt;
    return this.hasToken() && created != null
      ? `Created ${mcpCreatedAt(created, Date.now())}. Scout keeps only a fingerprint of it, never the token.`
      : 'None yet. An assistant cannot connect until you create one.';
  });
  readonly abilities = computed<readonly McpAbility[]>(() => [
    {
      title: 'Read',
      offered: true,
      what: 'The reviews and audits Scout ran and how far each one is, the finished review card with its findings and what Scout offers, and the note Scout would post on the pull request.',
      asks: 'Never asks you.',
    },
    {
      title: 'Start a review',
      offered: true,
      what: 'Start a review of a pull request that the GitHub Feature already reads. Scout reads GitHub and uses your AI Source.',
      asks:
        this.startMode() === 'ask'
          ? 'Asks you here before each start.'
          : 'Starts without asking; each start shows in the bell.',
    },
    {
      title: 'Write on GitHub',
      offered: false,
      what: 'Give a verdict, post a comment, open an issue, merge.',
      asks: 'An assistant that asks is refused, and the refusal is listed below.',
    },
  ]);
  /** The calls main listed, newest first, in the group's words. */
  readonly activity = computed(() =>
    [...(this.status()?.activity ?? [])].reverse().map((row: McpActivityRow) => ({
      when: clock(row.at),
      what: TOOL_WORDS[row.tool] ?? row.tool,
      target: row.target,
      result:
        RESULT_WORDS[row.result] ?? `${row.result.charAt(0).toUpperCase()}${row.result.slice(1)}.`,
      refused: row.result.startsWith('refused'),
    })),
  );

  constructor() {
    this.mcp.start();
  }

  async setEnabled(change: MatSlideToggleChange): Promise<void> {
    await this.run(() => this.mcp.setEnabled(change.checked));
    change.source.checked = this.checked();
  }

  async createToken(): Promise<void> {
    await this.run(async () => this.shown.set(await this.mcp.createToken()));
  }

  async revokeToken(): Promise<void> {
    this.shown.set(null);
    await this.run(() => this.mcp.revokeToken());
  }

  async setStartMode(mode: McpStartMode): Promise<void> {
    await this.run(() => this.mcp.setStartMode(mode));
  }

  async pickPort(): Promise<void> {
    await this.run(() => this.mcp.pickPort());
  }

  /** Copies `text` and forgets the token; a copy that fails keeps the token shown and says so. */
  async copy(text: string): Promise<void> {
    this.error.set(null);
    try {
      await navigator.clipboard.writeText(text);
      this.shown.set(null);
    } catch {
      this.error.set('Scout could not copy to the clipboard. Try again.');
    }
  }

  private async run(call: () => Promise<unknown>): Promise<void> {
    this.error.set(null);
    try {
      await call();
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    }
  }
}
