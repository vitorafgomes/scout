import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EventEmitter,
  OnDestroy,
  Output,
  OnInit,
  inject,
  signal,
  viewChild,
} from '@angular/core';

import {
  GITHUB_BOT_WRITES,
  readIpcError,
  type GitHubBotStatus,
  type GitHubBotWrite,
  type GitHubStatus,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

const POLL_MS = 3000;

const BOT_WRITE_LABELS: Readonly<Record<GitHubBotWrite, string>> = {
  review: 'Post reviews',
  verdict: 'Give verdicts (approve or request changes)',
  comment: 'Comment on pull requests and issues',
  issue: 'Open issues',
};

@Component({
  selector: 'sc-github-connect',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (status(); as s) {
      <section class="connect" data-test="github-connect-section">
        @if (s.connected) {
          <p class="note" data-test="github-connected">Connected as {{ s.login }}</p>
          @if (s.warning !== null) {
            <p class="warn" data-test="github-warning">{{ s.warning }}</p>
          }
          <div class="row">
            @if (s.method === 'browser' && !s.installed && s.installUrl !== null) {
              <button
                type="button"
                class="btn btn--primary"
                data-test="github-install"
                (click)="open(s.installUrl)"
              >
                Install Scout on your GitHub account
              </button>
            }
            <button
              type="button"
              class="btn btn--secondary"
              data-test="github-disconnect"
              [disabled]="busy()"
              (click)="disconnect()"
            >
              Disconnect
            </button>
          </div>
          @if (bot(); as b) {
            <section class="bot" data-test="github-bot-section">
              <p class="note"><strong>Use a Scout bot on GitHub (optional)</strong></p>
              @switch (b.state) {
                @case ('none') {
                  <p class="help" data-test="github-bot-help">
                    Reviews, verdicts, comments and issues Scout posts show as the bot instead of
                    you. GitHub creates an App that you own; Scout keeps its key on this computer.
                  </p>
                  <div class="row">
                    <select
                      class="input"
                      aria-label="Create the bot on"
                      data-test="github-bot-target"
                      (change)="botTarget.set(targetOf($event))"
                    >
                      <option value="user">My account</option>
                      <option value="org">An organization</option>
                    </select>
                    @if (botTarget() === 'org') {
                      <input
                        #orgInput
                        type="text"
                        class="input"
                        aria-label="Organization"
                        placeholder="organization"
                        data-test="github-bot-org"
                      />
                    }
                    <button
                      type="button"
                      class="btn btn--primary"
                      data-test="github-bot-create"
                      [disabled]="busy()"
                      (click)="createBot(orgValue())"
                    >
                      Create the bot on GitHub
                    </button>
                  </div>
                }
                @case ('creating') {
                  <p class="note" data-test="github-bot-creating">
                    Finish creating the App on GitHub in your browser.
                  </p>
                  <div class="row">
                    <button
                      type="button"
                      class="btn btn--secondary"
                      data-test="github-bot-cancel"
                      [disabled]="busy()"
                      (click)="cancelBot()"
                    >
                      Cancel
                    </button>
                  </div>
                }
                @case ('created') {
                  <p class="note" data-test="github-bot-created">
                    {{ b.login }} exists. Install it on the repositories it may act on; until then
                    Scout acts as you.
                  </p>
                  <div class="row">
                    @if (b.installUrl !== null) {
                      <button
                        type="button"
                        class="btn btn--primary"
                        data-test="github-bot-install"
                        (click)="open(b.installUrl)"
                      >
                        Install the bot
                      </button>
                    }
                    <button
                      type="button"
                      class="btn btn--secondary"
                      data-test="github-bot-remove"
                      [disabled]="busy()"
                      (click)="removeBot(b)"
                    >
                      Remove the bot
                    </button>
                  </div>
                }
                @case ('installed') {
                  <p class="note" data-test="github-bot-installed">{{ b.login }} can act on:</p>
                  <ul class="repos" data-test="github-bot-repos">
                    @for (repo of b.repositories; track repo) {
                      <li>{{ repo }}</li>
                    }
                  </ul>
                  <p class="help">Signed by the bot; an action switched off is signed as you.</p>
                  @for (w of writes; track w) {
                    <label class="switch">
                      <input
                        type="checkbox"
                        [attr.data-test]="'github-bot-write-' + w"
                        [checked]="b.writes.includes(w)"
                        [disabled]="busy()"
                        (change)="switchWrite(b, w)"
                      />
                      {{ writeLabels[w] }}
                    </label>
                  }
                  <div class="row">
                    @if (b.installUrl !== null) {
                      <button
                        type="button"
                        class="btn btn--secondary"
                        data-test="github-bot-install"
                        (click)="open(b.installUrl)"
                      >
                        Change repositories
                      </button>
                    }
                    <button
                      type="button"
                      class="btn btn--secondary"
                      data-test="github-bot-remove"
                      [disabled]="busy()"
                      (click)="removeBot(b)"
                    >
                      Remove the bot
                    </button>
                  </div>
                }
              }
              @if (b.error !== null) {
                <p class="err" data-test="github-bot-error">{{ b.error.message }}</p>
              }
              @if (removedSettingsUrl(); as url) {
                <p class="help" data-test="github-bot-removed">
                  Scout no longer uses the bot. The App stays on GitHub until you delete it there.
                </p>
                <button
                  type="button"
                  class="link"
                  data-test="github-bot-settings"
                  (click)="open(url)"
                >
                  Open the App's settings on GitHub
                </button>
              }
            </section>
          }
        } @else if (s.pending !== null) {
          <p class="note">Enter this code on GitHub:</p>
          <code class="code" data-test="github-user-code">{{ s.pending.userCode }}</code>
          <div class="row">
            <button
              type="button"
              class="btn btn--primary"
              data-test="github-open"
              (click)="open(s.pending.verificationUri)"
            >
              Open GitHub
            </button>
            <button
              type="button"
              class="btn btn--secondary"
              data-test="github-copy"
              (click)="copy(s.pending.userCode)"
            >
              Copy code
            </button>
            <button
              type="button"
              class="btn btn--secondary"
              data-test="github-cancel"
              (click)="cancel()"
            >
              Cancel
            </button>
          </div>
        } @else {
          @if (!s.configured) {
            <p class="note" data-test="github-not-configured">
              The Scout GitHub App is not configured yet.
            </p>
          } @else if (s.error !== null) {
            <p class="err" data-test="github-error">{{ s.error.message }}</p>
          }
          <button
            type="button"
            class="btn btn--primary"
            data-test="github-connect"
            [disabled]="!s.configured || busy()"
            (click)="connect()"
          >
            Connect with GitHub
          </button>
          @if (tokenFormOpen()) {
            <p class="help" data-test="github-token-help">
              Recommended: a fine-grained token with read access to Metadata and Contents and to
              Pull requests and Issues, read &amp; write on those two only if you will allow Scout
              to post reviews and open issues, for the repositories Scout should see.
            </p>
            <button
              type="button"
              class="link"
              data-test="github-create-token"
              (click)="open(createTokenUrl)"
            >
              Create a fine-grained token
            </button>
            <div class="row">
              <input
                #tokenInput
                type="password"
                class="input"
                autocomplete="off"
                aria-label="GitHub token"
                data-test="github-token-input"
              />
              <button
                type="button"
                class="btn btn--primary"
                data-test="github-token-save"
                [disabled]="busy()"
                (click)="saveToken(tokenInput)"
              >
                Save
              </button>
            </div>
          } @else {
            <button
              type="button"
              class="btn btn--secondary"
              data-test="github-use-token"
              (click)="tokenFormOpen.set(true)"
            >
              Use a token
            </button>
          }
        }
      </section>
    }
    @if (actionError(); as message) {
      <p class="err" data-test="github-action-error">{{ message }}</p>
    }
  `,
  styles: [
    `
      .connect,
      .bot {
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .bot {
        margin-top: 8px;
        padding-top: 8px;
        border-top: 1px solid #252b3b;
      }
      .repos {
        margin: 0;
        padding-left: 18px;
        font-size: 12px;
        color: #e4e8f0;
      }
      .switch {
        display: flex;
        gap: 6px;
        align-items: center;
        font-size: 12px;
        color: #e4e8f0;
      }
      .row {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
      }
      .note {
        margin: 0;
        font-size: 13px;
        color: #e4e8f0;
      }
      .code {
        font-size: 28px;
        letter-spacing: 0.12em;
        color: #e4e8f0;
        user-select: all;
      }
      .warn {
        margin: 0;
        font-size: 12px;
        color: #fbbf24;
      }
      .help {
        margin: 0;
        font-size: 12px;
        color: #7b8299;
      }
      .link {
        align-self: flex-start;
        padding: 0;
        border: none;
        background: none;
        color: #60a5fa;
        font-size: 12px;
        cursor: pointer;
      }
      .input {
        flex: 1;
        font-size: 13px;
        padding: 6px 8px;
        border-radius: 6px;
        border: 1px solid #252b3b;
        background: #11131a;
        color: #e4e8f0;
      }
      .err {
        margin: 0;
        font-size: 12px;
        color: #f87171;
      }
      .btn {
        font-size: 13px;
        padding: 6px 12px;
        border-radius: 6px;
        border: 1px solid transparent;
        cursor: pointer;
      }
      .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
      .btn--primary {
        background: #2563eb;
        color: white;
      }
      .btn--secondary {
        background: transparent;
        color: #e4e8f0;
        border-color: #252b3b;
      }
    `,
  ],
})
export class GitHubConnectComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private refreshing: Promise<void> | null = null;
  private destroyed = false;

  readonly status = signal<GitHubStatus | null>(null);
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly tokenFormOpen = signal(false);
  readonly createTokenUrl = 'https://github.com/settings/personal-access-tokens/new';
  readonly bot = signal<GitHubBotStatus | null>(null);
  readonly botTarget = signal<'user' | 'org'>('user');
  readonly removedSettingsUrl = signal<string | null>(null);
  readonly writes = GITHUB_BOT_WRITES;
  readonly writeLabels = BOT_WRITE_LABELS;
  private readonly orgInput = viewChild<ElementRef<HTMLInputElement>>('orgInput');

  @Output() readonly connectedChange = new EventEmitter<boolean>();

  async ngOnInit(): Promise<void> {
    await this.refresh();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.stopPolling();
  }

  async connect(): Promise<void> {
    await this.run(async () => {
      await this.bridge.githubSignIn();
    });
  }

  async cancel(): Promise<void> {
    await this.run(() => this.bridge.githubCancel());
  }

  async disconnect(): Promise<void> {
    await this.run(async () => {
      await this.bridge.githubSignOut();
    });
  }

  async saveToken(input: HTMLInputElement): Promise<void> {
    const token = input.value;
    input.value = '';
    await this.run(async () => {
      await this.bridge.githubSaveToken({ token });
      this.tokenFormOpen.set(false);
    });
  }

  async createBot(org: string): Promise<void> {
    this.removedSettingsUrl.set(null);
    await this.run(async () => {
      await this.bridge.githubBotCreate(
        this.botTarget() === 'org' ? { target: 'org', org } : { target: 'user' },
      );
    });
  }

  async cancelBot(): Promise<void> {
    await this.run(async () => {
      await this.bridge.githubBotCancel();
    });
  }

  async removeBot(current: GitHubBotStatus): Promise<void> {
    await this.run(async () => {
      await this.bridge.githubBotRemove();
      this.removedSettingsUrl.set(current.settingsUrl);
    });
  }

  async switchWrite(current: GitHubBotStatus, write: GitHubBotWrite): Promise<void> {
    const writes = current.writes.includes(write)
      ? current.writes.filter((w) => w !== write)
      : GITHUB_BOT_WRITES.filter((w) => w === write || current.writes.includes(w));
    await this.run(async () => {
      await this.bridge.githubBotSetWrites({ writes });
    });
  }

  targetOf(event: Event): 'user' | 'org' {
    return (event.target as HTMLSelectElement).value === 'org' ? 'org' : 'user';
  }

  orgValue(): string {
    return this.orgInput()?.nativeElement.value ?? '';
  }

  async open(url: string): Promise<void> {
    try {
      await this.bridge.appOpenExternal({ url });
    } catch (err: unknown) {
      this.actionError.set(readIpcError(err).message);
    }
  }

  async copy(code: string): Promise<void> {
    await navigator.clipboard.writeText(code).catch(() => undefined);
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    try {
      await action();
    } catch (err: unknown) {
      this.actionError.set(readIpcError(err).message);
    } finally {
      this.busy.set(false);
    }
    await this.refresh();
  }

  private async refresh(): Promise<void> {
    while (this.refreshing !== null) await this.refreshing;
    this.refreshing = this.load();
    try {
      await this.refreshing;
    } finally {
      this.refreshing = null;
    }
  }

  private async load(): Promise<void> {
    this.stopPolling();
    try {
      const status = await this.bridge.githubStatus();
      if (this.destroyed) return;
      this.status.set(status);
      this.connectedChange.emit(status.connected);
      const bot = status.connected ? await this.loadBot() : null;
      if (this.destroyed) return;
      this.bot.set(bot);
      const waitingForInstall =
        status.method === 'browser' && !status.installed && status.installUrl !== null;
      const waitingForBot = bot?.state === 'creating' || bot?.state === 'created';
      if (status.pending !== null || waitingForInstall || waitingForBot) {
        this.timer = setTimeout(() => void this.refresh(), POLL_MS);
      }
    } catch (err: unknown) {
      if (this.destroyed) return;
      this.actionError.set(readIpcError(err).message);
    }
  }

  private async loadBot(): Promise<GitHubBotStatus | null> {
    try {
      return await this.bridge.githubBotStatus();
    } catch (err: unknown) {
      this.actionError.set(readIpcError(err).message);
      return null;
    }
  }

  private stopPolling(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }
}
