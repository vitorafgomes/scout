import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  OnDestroy,
  Output,
  OnInit,
  inject,
  signal,
} from '@angular/core';

import { readIpcError, type GitHubStatus } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

const POLL_MS = 3000;

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
              Recommended: a fine-grained token with read-only access (Metadata, Contents, Pull
              requests, Issues) to the repositories Scout should see.
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
      .connect {
        display: flex;
        flex-direction: column;
        gap: 8px;
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
      const waitingForInstall =
        status.method === 'browser' && !status.installed && status.installUrl !== null;
      if (status.pending !== null || waitingForInstall) {
        this.timer = setTimeout(() => void this.refresh(), POLL_MS);
      }
    } catch (err: unknown) {
      if (this.destroyed) return;
      this.actionError.set(readIpcError(err).message);
    }
  }

  private stopPolling(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }
}
