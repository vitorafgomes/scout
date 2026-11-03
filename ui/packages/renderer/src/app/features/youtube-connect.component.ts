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

import { readIpcError, type YouTubeStatus } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

const POLL_MS = 3000;

@Component({
  selector: 'sc-youtube-connect',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (status(); as s) {
      <section class="connect" data-test="youtube-connect-section">
        @if (s.connected) {
          @if (s.method === 'oauth') {
            <p class="note" data-test="youtube-connected">Connected to {{ s.channelTitle }}</p>
            <p class="help" data-test="youtube-method">Signed in with Google</p>
          } @else {
            <p class="note" data-test="youtube-connected">Connected with an API key</p>
            <p class="help" data-test="youtube-method">Public data only.</p>
          }
          <div class="row">
            <button
              type="button"
              class="btn btn--primary"
              data-test="youtube-open-studio"
              (click)="open(studioUrl(s.channelId))"
            >
              Open YouTube Studio
            </button>
            @if (s.channelId !== null) {
              <button
                type="button"
                class="btn btn--secondary"
                data-test="youtube-open-channel"
                (click)="open('https://www.youtube.com/channel/' + s.channelId)"
              >
                Open channel
              </button>
            }
            <button
              type="button"
              class="btn btn--secondary"
              data-test="youtube-disconnect"
              [disabled]="busy()"
              (click)="disconnect(s.method)"
            >
              Disconnect
            </button>
          </div>
        } @else if (s.pending) {
          <p class="note" data-test="youtube-pending">Waiting for the browser...</p>
          <div class="row">
            <button
              type="button"
              class="btn btn--secondary"
              data-test="youtube-cancel"
              (click)="cancel()"
            >
              Cancel
            </button>
          </div>
        } @else {
          @if (!s.configured) {
            <p class="note" data-test="youtube-not-configured">
              Sign in needs a Google OAuth client: enable the YouTube Data API, create an OAuth
              client of type Desktop app, then download its JSON as it is to
              {{ s.clientConfigPath }} (or write {{ clientFileExample }} there). Or use an API key
              below.
            </p>
            <button
              type="button"
              class="link"
              data-test="youtube-enable-api"
              (click)="open(enableApiUrl)"
            >
              Enable the YouTube Data API
            </button>
            <button
              type="button"
              class="link"
              data-test="youtube-open-credentials"
              (click)="open(createKeyUrl)"
            >
              Open Google Cloud Console credentials
            </button>
          }
          @if (s.error !== null) {
            <p class="err" data-test="youtube-error">{{ s.error }}</p>
          }
          <button
            type="button"
            class="btn btn--primary"
            data-test="youtube-sign-in"
            [disabled]="!s.configured || busy()"
            (click)="signIn()"
          >
            Sign in with Google
          </button>
          @if (keyFormOpen()) {
            <button
              type="button"
              class="link"
              data-test="youtube-create-key"
              (click)="open(createKeyUrl)"
            >
              Create a key in Google Cloud Console
            </button>
            <div class="row">
              <input
                #keyInput
                type="password"
                class="input"
                autocomplete="off"
                aria-label="YouTube Data API key"
                data-test="youtube-key-input"
              />
              <button
                type="button"
                class="btn btn--primary"
                data-test="youtube-key-save"
                [disabled]="busy()"
                (click)="saveKey(keyInput)"
              >
                Save
              </button>
            </div>
          } @else {
            <button
              type="button"
              class="btn btn--secondary"
              data-test="youtube-use-key"
              (click)="keyFormOpen.set(true)"
            >
              Use an API key
            </button>
          }
        }
        <button
          type="button"
          class="btn btn--secondary operator"
          data-test="youtube-open-operator"
          [disabled]="!s.connected"
          (click)="openOperator()"
        >
          Open YouTube operator
        </button>
      </section>
    }
    @if (actionError(); as message) {
      <p class="err" data-test="youtube-action-error">{{ message }}</p>
    }
  `,
  styles: [
    `
      .connect {
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .operator {
        align-self: flex-start;
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
export class YouTubeConnectComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private refreshing: Promise<void> | null = null;
  private destroyed = false;

  readonly status = signal<YouTubeStatus | null>(null);
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly keyFormOpen = signal(false);
  readonly createKeyUrl = 'https://console.cloud.google.com/apis/credentials';
  readonly enableApiUrl = 'https://console.cloud.google.com/apis/library/youtube.googleapis.com';
  readonly clientFileExample = '{"client_id": ..., "client_secret": ...}';

  @Output() readonly connectedChange = new EventEmitter<boolean>();

  async ngOnInit(): Promise<void> {
    await this.refresh();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.stopPolling();
  }

  async signIn(): Promise<void> {
    await this.run(async () => {
      await this.bridge.youtubeSignIn();
    });
  }

  async cancel(): Promise<void> {
    await this.run(() => this.bridge.youtubeCancelSignIn());
  }

  async disconnect(method: YouTubeStatus['method']): Promise<void> {
    await this.run(async () => {
      if (method === 'apiKey') await this.bridge.youtubeClearApiKey();
      else await this.bridge.youtubeSignOut();
    });
  }

  async saveKey(input: HTMLInputElement): Promise<void> {
    const apiKey = input.value;
    input.value = '';
    await this.run(async () => {
      await this.bridge.youtubeSaveApiKey({ apiKey });
      this.keyFormOpen.set(false);
    });
  }

  studioUrl(channelId: string | null): string {
    return channelId === null
      ? 'https://studio.youtube.com/'
      : `https://studio.youtube.com/channel/${channelId}`;
  }

  async open(url: string): Promise<void> {
    try {
      await this.bridge.appOpenExternal({ url });
    } catch (err: unknown) {
      this.actionError.set(readIpcError(err).message);
    }
  }

  async openOperator(): Promise<void> {
    try {
      await this.bridge.operatorOpenWindow();
    } catch (err: unknown) {
      this.actionError.set(readIpcError(err).message);
    }
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
      const status = await this.bridge.youtubeStatus();
      if (this.destroyed) return;
      this.status.set(status);
      this.connectedChange.emit(status.connected);
      if (status.pending) this.timer = setTimeout(() => void this.refresh(), POLL_MS);
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
