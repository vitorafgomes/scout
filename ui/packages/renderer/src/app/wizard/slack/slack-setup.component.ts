import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { readIpcError, type SlackStatus } from '@scout/shared';

import { LogStore } from '../../core/log.store';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

/** Sets up the Slack app from Scout's manifest and keeps its two tokens in main. */
@Component({
  selector: 'sc-slack-setup',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="slack" data-test="slack-setup">
      <h2>Slack</h2>
      @if (status(); as current) {
        @if (current.connected) {
          <p data-test="slack-connected">
            Connected to {{ current.team ?? 'your workspace' }} as {{ current.botUser ?? 'scout' }}
          </p>
          <button
            type="button"
            class="secondary"
            data-test="slack-disconnect"
            [disabled]="busy()"
            (click)="disconnect()"
          >
            Disconnect
          </button>
        } @else {
          <h3>Step 1. Create the Slack app</h3>
          <button type="button" data-test="slack-create" [disabled]="busy()" (click)="createApp()">
            Create the Slack app
          </button>
          <ol class="steps" data-test="slack-steps">
            <li>Pick the workspace and click Create.</li>
            <li>Click Install to Workspace, then copy the Bot User OAuth Token.</li>
            <li>
              Open Basic Information, App-Level Tokens, Generate Token and Scopes, add the scope
              connections:write, Generate, then copy the token.
            </li>
            <li>In Slack, invite @scout to each channel where it should answer.</li>
          </ol>
          <h3>Step 2. Paste the two tokens</h3>
          <form (submit)="$event.preventDefault(); save()">
            <label>
              Bot User OAuth Token (xoxb-)
              <input
                type="password"
                autocomplete="off"
                data-test="slack-bot-token"
                [value]="botToken()"
                (input)="botToken.set($any($event.target).value)"
              />
            </label>
            <label>
              App-level token (xapp-)
              <input
                type="password"
                autocomplete="off"
                data-test="slack-app-token"
                [value]="appToken()"
                (input)="appToken.set($any($event.target).value)"
              />
            </label>
            <button
              type="submit"
              data-test="slack-save"
              [disabled]="busy() || botToken().trim() === '' || appToken().trim() === ''"
            >
              Save
            </button>
          </form>
        }
      }
      @if (error(); as message) {
        <p class="error" role="alert" data-test="slack-error">{{ message }}</p>
      }
    </section>
  `,
  styles: [
    `
      :host {
        display: block;
        width: 100%;
        max-width: 720px;
        margin: 0 auto;
        box-sizing: border-box;
      }
      .slack {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 10px;
        padding: 16px;
        color: #e4e8f0;
      }
      h2 {
        margin: 0 0 8px;
        font-size: 14px;
        font-weight: 600;
      }
      h3 {
        margin: 16px 0 8px;
        font-size: 13px;
        font-weight: 600;
      }
      .steps {
        font-size: 13px;
        color: #a0aab8;
        padding-left: 20px;
      }
      form {
        display: flex;
        flex-direction: column;
        gap: 8px;
        max-width: 420px;
      }
      label {
        display: flex;
        flex-direction: column;
        gap: 4px;
        font-size: 13px;
      }
      input {
        background: #0f1117;
        border: 1px solid #252b3b;
        border-radius: 6px;
        color: #e4e8f0;
        padding: 6px 10px;
      }
      .error {
        color: #d45a5a;
        font-size: 13px;
        margin: 8px 0 0;
      }
    `,
  ],
})
export class SlackSetupComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly wizard = inject(WizardStore);
  private readonly logStore = inject(LogStore);

  readonly status = signal<SlackStatus | null>(null);
  readonly botToken = signal('');
  readonly appToken = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.bridge
      .slackStatus()
      .then((status) => this.status.set(status))
      .catch((err: unknown) => {
        this.status.set({ connected: false, team: null, botUser: null });
        this.error.set(readIpcError(err).message);
      });
  }

  async createApp(): Promise<void> {
    this.error.set(null);
    try {
      const { url } = await this.bridge.slackManifestUrl();
      await this.bridge.appOpenExternal({ url });
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    }
  }

  async save(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const status = await this.bridge.slackSave({
        botToken: this.botToken(),
        appToken: this.appToken(),
      });
      this.botToken.set('');
      this.appToken.set('');
      this.status.set(status);
      this.wizard.setSlackConnected(status.connected);
      if (this.logStore.status().state === 'idle') await this.bridge.workerStart();
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    } finally {
      this.busy.set(false);
    }
  }

  async disconnect(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const status = await this.bridge.slackDisconnect();
      this.status.set(status);
      this.wizard.setSlackConnected(status.connected);
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    } finally {
      this.busy.set(false);
    }
  }
}
