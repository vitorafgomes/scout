import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { readIpcError, type DiscordBotInfo } from '@scout/shared';

import { IconComponent } from '../../core/icon.component';
import { LogStore } from '../../core/log.store';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

export type ChatPlatform = 'discord' | 'slack' | 'teams';

/** Which chat connection the modal is for, and whether it is connected already. */
export interface ChatConnectionDialogData {
  readonly platform: ChatPlatform;
  readonly connected: boolean;
}

/** What a save that went through tells the cards: a notice to show under them, or none. */
export interface ChatConnectionSaved {
  readonly notice: string | null;
}

/** One secret field of a chat connection: what it asks for and how a wrong value is told. */
interface SecretField {
  readonly key: 'discord-token' | 'slack-bot-token' | 'slack-app-token';
  readonly label: string;
  readonly placeholder: string;
  readonly prefix: string;
  readonly help: string;
  readonly invalid: string;
}

const DEVELOPER_PORTAL_URL = 'https://discord.com/developers/applications';

const FIELDS: Readonly<Record<ChatPlatform, readonly SecretField[]>> = {
  discord: [
    {
      key: 'discord-token',
      label: 'Discord bot token',
      placeholder: 'Paste the bot token',
      prefix: '',
      help: 'In the Discord Developer Portal: your application, Bot, Reset Token. Discord shows it once.',
      invalid: 'Enter the bot token.',
    },
  ],
  slack: [
    {
      key: 'slack-bot-token',
      label: 'Bot User OAuth Token (xoxb-)',
      placeholder: 'xoxb-...',
      prefix: 'xoxb-',
      help: 'Step 2. Paste the two tokens. This one is under OAuth & Permissions after you install the app.',
      invalid: 'This is not a bot token: it starts with xoxb-.',
    },
    {
      key: 'slack-app-token',
      label: 'App-level token (xapp-)',
      placeholder: 'xapp-...',
      prefix: 'xapp-',
      help: 'Under Basic Information, App-Level Tokens, with the connections:write scope.',
      invalid: 'This is not an app-level token: it starts with xapp-.',
    },
  ],
  teams: [],
};

/** The Slack app steps, the same the first-run Slack setup shows. */
export const SLACK_STEPS: readonly string[] = Object.freeze([
  'Pick the workspace and click Create.',
  'Click Install to Workspace, then copy the Bot User OAuth Token.',
  'Open Basic Information, App-Level Tokens, Generate Token and Scopes, add the scope connections:write, Generate, then copy the token.',
  'In Slack, invite @scout to each channel where it should answer.',
]);

/**
 * The credentials of one chat connection, in a modal opened from its card in Settings. Discord takes
 * the bot token through the same check the wizard uses, and a new Discord app continues on the
 * wizard step that adds the bot to a server; Slack takes its two tokens through the same calls as
 * the Slack setup; Teams says it cannot be connected yet. A field left empty or with the wrong
 * prefix is marked when Save is pressed and nothing is sent. Secrets live only in password fields
 * and are emptied once sent; the modal closes with `saved` when main kept them.
 */
@Component({
  selector: 'sc-chat-connection-dialog',
  standalone: true,
  imports: [IconComponent, MatButtonModule, MatDialogModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ title() }}</h2>
    <mat-dialog-content class="body">
      @switch (data.platform) {
        @case ('discord') {
          <p class="desc">
            Scout answers as your Discord bot. Paste the bot token from the Discord Developer
            Portal.
            @if (!data.connected) {
              <span data-test="dialog-next"
                >After the token, Scout opens the step that adds the bot to your server.</span
              >
            }
          </p>
          <button
            type="button"
            matButton="outlined"
            class="inline"
            data-test="dialog-discord-portal"
            (click)="open(portalUrl)"
          >
            <sc-icon name="external" [size]="16" />
            Open the Developer Portal
          </button>
        }
        @case ('slack') {
          <p class="desc">
            Scout joins your workspace as a Slack app, so it needs the two tokens of that app.
          </p>
          <section class="steps" aria-labelledby="dialog-steps-title">
            <h3 id="dialog-steps-title">Step 1. Create the Slack app</h3>
            <button
              type="button"
              matButton="outlined"
              class="inline create"
              data-test="dialog-slack-create"
              [disabled]="busy()"
              (click)="createSlackApp()"
            >
              <sc-icon name="external" [size]="16" />
              Create the Slack app
            </button>
            <ol data-test="dialog-steps">
              @for (step of slackSteps; track step) {
                <li>{{ step }}</li>
              }
            </ol>
          </section>
        }
        @case ('teams') {
          <p class="desc">Scout answers in Teams as a Bot Framework bot.</p>
          <p class="note" role="note" data-test="dialog-note">
            <sc-icon name="warning" [size]="16" />
            <span
              >Teams cannot be connected yet. Scout has no place on this computer to keep a Teams
              bot registration, and Teams sends its messages to a public HTTPS endpoint that Scout
              does not open.</span
            >
          </p>
        }
      }
      @for (field of fields; track field.key) {
        <div class="field">
          <label [for]="'dialog-' + field.key">{{ field.label }}</label>
          <div class="row">
            <input
              [id]="'dialog-' + field.key"
              [type]="reveal() ? 'text' : 'password'"
              autocomplete="off"
              spellcheck="false"
              [attr.data-test]="'dialog-' + field.key"
              [attr.aria-describedby]="'dialog-help-' + field.key"
              [attr.aria-invalid]="marked(field)"
              [class.invalid]="marked(field)"
              [placeholder]="field.placeholder"
              [value]="values()[field.key] ?? ''"
              (input)="setValue(field.key, $any($event.target).value)"
            />
            <button
              type="button"
              matIconButton
              class="reveal"
              [attr.data-test]="'dialog-reveal-' + field.key"
              [attr.aria-pressed]="reveal()"
              [attr.aria-label]="(reveal() ? 'Hide ' : 'Show ') + field.label"
              [title]="(reveal() ? 'Hide ' : 'Show ') + field.label"
              (click)="reveal.set(!reveal())"
            >
              <svg
                viewBox="0 0 24 24"
                width="18"
                height="18"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                @if (reveal()) {
                  <path
                    d="M3 3l18 18M10.6 5.1A10 10 0 0 1 12 5c6.4 0 10 7 10 7a17 17 0 0 1-3.2 4.1M6.6 6.6A17 17 0 0 0 2 12s3.6 7 10 7a9.7 9.7 0 0 0 5.4-1.6M9.9 9.9a3 3 0 0 0 4.2 4.2"
                  />
                } @else {
                  <path
                    d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12zM12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z"
                  />
                }
              </svg>
            </button>
          </div>
          <span
            class="help"
            [class.help--invalid]="marked(field)"
            [id]="'dialog-help-' + field.key"
            [attr.data-test]="'dialog-help-' + field.key"
            >{{ marked(field) ? field.invalid : field.help }}</span
          >
        </div>
      }
      @if (tried() && invalid()) {
        <p class="error" role="alert" data-test="dialog-invalid">
          Check the highlighted fields. Nothing was saved.
        </p>
      }
      @if (error(); as message) {
        <p class="error" role="alert" data-test="dialog-error">{{ message }}</p>
        @if (intentPortal(); as url) {
          <div class="row">
            <button type="button" matButton="outlined" (click)="open(url)">
              <sc-icon name="external" [size]="16" />
              Open the Developer Portal
            </button>
            <button
              type="button"
              matButton="outlined"
              data-test="dialog-check-again"
              [disabled]="busy()"
              (click)="checkIntent()"
            >
              <sc-icon name="refresh" [size]="16" />
              Check again
            </button>
          </div>
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (data.platform === 'teams') {
        <button
          type="button"
          matButton="outlined"
          mat-dialog-close
          cdkFocusInitial
          data-test="dialog-close"
        >
          Close
        </button>
      } @else {
        <button
          type="button"
          matButton="outlined"
          mat-dialog-close
          cdkFocusInitial
          data-test="dialog-cancel"
          [disabled]="busy()"
        >
          Cancel
        </button>
        <button
          type="button"
          matButton="filled"
          class="save"
          data-test="dialog-save"
          [disabled]="busy()"
          (click)="save()"
        >
          <sc-icon [name]="data.connected ? 'save' : 'plug'" [size]="16" />
          {{ saveLabel() }}
        </button>
      }
    </mat-dialog-actions>
  `,
  styles: [
    `
      :host {
        --mat-dialog-subhead-size: 17px;
        --mat-dialog-subhead-weight: 600;
        --mat-dialog-headline-padding: 20px 20px 0;
        --mat-dialog-with-actions-content-padding: 12px 20px 0;
        --mat-dialog-content-padding: 12px 20px 0;
        --mat-dialog-actions-padding: 16px 20px 20px;
      }
      .body {
        display: flex;
        flex-direction: column;
        gap: 12px;
        color: var(--sc-fg);
      }
      .desc {
        margin: 0;
        color: var(--sc-fg-2);
        font-size: 13px;
        line-height: 1.5;
      }
      .inline {
        align-self: flex-start;
      }
      .steps {
        display: flex;
        flex-direction: column;
        gap: 8px;
        padding: 12px;
        border-radius: 10px;
        background: var(--sc-bg);
        border: 1px solid var(--sc-line);
      }
      .steps h3 {
        margin: 0;
        font-size: 13px;
        font-weight: 600;
      }
      .steps ol {
        margin: 0;
        padding-left: 20px;
        font-size: 13px;
        color: var(--sc-fg-2);
        line-height: 1.45;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .field label {
        font-size: 13px;
        font-weight: 500;
      }
      .row {
        display: flex;
        gap: 8px;
        align-items: center;
      }
      input {
        flex: 1;
        min-width: 0;
        height: 40px;
        padding: 0 12px;
        border-radius: var(--sc-radius-sm);
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        color: var(--sc-fg);
        font: inherit;
        font-family: var(--sc-mono);
        font-size: 13px;
        box-sizing: border-box;
      }
      input.invalid {
        border-color: var(--sc-error);
      }
      .help {
        font-size: 12px;
        color: var(--sc-muted);
        line-height: 1.4;
      }
      .help--invalid {
        color: var(--sc-error);
      }
      .note {
        display: flex;
        gap: 8px;
        align-items: flex-start;
        margin: 0;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
        font-size: 13px;
        line-height: 1.45;
      }
      .error {
        margin: 0;
        font-size: 13px;
        color: var(--sc-error);
      }
      .create {
        --mat-button-outlined-container-height: 36px;
        --mat-button-outlined-horizontal-padding: 14px;
      }
      .reveal {
        --mat-icon-button-icon-color: var(--sc-fg-2);
        border: 1px solid var(--sc-line);
      }
      .save {
        --mat-button-filled-container-color: var(--sc-accent-strong);
        --mat-button-filled-label-text-color: var(--sc-on-accent);
        --mat-button-filled-horizontal-padding: 16px;
      }
      [matButton] sc-icon {
        margin-right: 6px;
      }
    `,
  ],
})
export class ChatConnectionDialogComponent {
  readonly data = inject<ChatConnectionDialogData>(MAT_DIALOG_DATA);
  private readonly ref =
    inject<MatDialogRef<ChatConnectionDialogComponent, ChatConnectionSaved>>(MatDialogRef);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly wizard = inject(WizardStore);
  private readonly logStore = inject(LogStore);
  private readonly router = inject(Router);

  protected readonly portalUrl = DEVELOPER_PORTAL_URL;
  protected readonly slackSteps = SLACK_STEPS;
  protected readonly fields = FIELDS[this.data.platform];
  readonly values = signal<Readonly<Partial<Record<SecretField['key'], string>>>>({});
  readonly reveal = signal(false);
  readonly tried = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly intentPortal = signal<string | null>(null);
  private bot: DiscordBotInfo | null = null;

  readonly title = computed(() => {
    const name = { discord: 'Discord', slack: 'Slack', teams: 'Microsoft Teams' }[
      this.data.platform
    ];
    return `${this.data.connected && this.data.platform !== 'teams' ? 'Manage' : 'Connect'} ${name}`;
  });
  readonly saveLabel = computed(() => {
    if (!this.data.connected) return 'Connect';
    return this.data.platform === 'slack' ? 'Save tokens' : 'Save token';
  });
  /** True while any field is empty or does not start with its prefix. */
  readonly invalid = computed(() => this.fields.some((field) => this.wrong(field)));

  setValue(key: SecretField['key'], value: string): void {
    this.values.update((current) => ({ ...current, [key]: value }));
  }

  /** True when a Save was tried and this field is empty or does not start with its prefix. */
  marked(field: SecretField): boolean {
    return this.tried() && this.wrong(field);
  }

  async save(): Promise<void> {
    this.tried.set(true);
    if (this.invalid()) return;
    if (this.data.platform === 'discord') await this.saveDiscord();
    else if (this.data.platform === 'slack') await this.saveSlack();
  }

  /** Asks Discord again whether Message Content Intent is on, after the owner changed it in the portal. */
  async checkIntent(): Promise<void> {
    await this.run(async () => {
      if (this.bot !== null) await this.enableIntentThenFinish(this.bot);
    });
  }

  async createSlackApp(): Promise<void> {
    this.error.set(null);
    try {
      const { url } = await this.bridge.slackManifestUrl();
      await this.bridge.appOpenExternal({ url });
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    }
  }

  open(url: string): void {
    this.bridge.appOpenExternal({ url }).catch((err: unknown) => {
      this.error.set(readIpcError(err).message);
    });
  }

  private wrong(field: SecretField): boolean {
    const value = (this.values()[field.key] ?? '').trim();
    return value === '' || !value.startsWith(field.prefix);
  }

  private value(key: SecretField['key']): string {
    return (this.values()[key] ?? '').trim();
  }

  private async saveDiscord(): Promise<void> {
    const token = this.value('discord-token');
    await this.run(async () => {
      const bot = await this.bridge.discordCheckToken({ token });
      this.values.set({});
      this.tried.set(false);
      this.bot = bot;
      if (bot.messageContent === 'on') this.finishDiscord(bot);
      else await this.enableIntentThenFinish(bot);
    });
  }

  private async enableIntentThenFinish(bot: DiscordBotInfo): Promise<void> {
    const result = await this.bridge.discordEnableIntent();
    if (!result.enabled) {
      this.error.set(result.message);
      this.intentPortal.set(result.portalUrl);
      return;
    }
    this.intentPortal.set(null);
    this.finishDiscord(bot);
  }

  /**
   * Closes on a token of the connected app, or moves on to the wizard step that adds the bot to a server. Main restarts
   * a running worker once it stores a token the worker does not have, so the dialog asks for no restart of its own.
   */
  private finishDiscord(bot: DiscordBotInfo): void {
    const known = this.wizard.clientId();
    const sameApp = this.data.connected && (known === null || known === bot.applicationId);
    this.ref.close({ notice: null });
    if (sameApp) return;
    this.wizard.setClientId(bot.applicationId);
    this.wizard.goTo('oauth');
    void this.router.navigateByUrl('/oauth');
  }

  private async saveSlack(): Promise<void> {
    const request = {
      botToken: this.value('slack-bot-token'),
      appToken: this.value('slack-app-token'),
    };
    await this.run(async () => {
      const status = await this.bridge.slackSave(request);
      this.values.set({});
      this.tried.set(false);
      this.wizard.setSlackConnected(status.connected);
      const worker = this.logStore.status();
      let notice: string | null = null;
      if (worker.state === 'idle' || (worker.state === 'crashed' && worker.gaveUp === true)) {
        await this.bridge.workerStart().catch((err: unknown) => {
          notice = `Slack saved. Scout did not start: ${readIpcError(err).message.replace(/\.$/, '')}.`;
        });
      }
      this.ref.close({ notice });
    });
  }

  /** Runs one save; while it runs, Cancel, Escape and a click on the backdrop cannot close the modal. */
  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.ref.disableClose = true;
    this.error.set(null);
    try {
      await action();
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    } finally {
      this.busy.set(false);
      this.ref.disableClose = false;
    }
  }
}
