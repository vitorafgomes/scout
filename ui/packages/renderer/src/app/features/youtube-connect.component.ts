import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  EventEmitter,
  forwardRef,
  OnDestroy,
  Output,
  OnInit,
  inject,
  output,
  signal,
  viewChild,
} from '@angular/core';

import { readIpcError, type YouTubeStatus } from '@scout/shared';

import { CdkTrapFocus } from '@angular/cdk/a11y';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';

import { confirmAction } from '../core/confirm-dialog.component';
import { IconComponent } from '../core/icon.component';
import { NoticeService } from '../core/notice.service';
import { ScoutBridgeService } from '../core/scout-bridge';

import { DIALOG_STYLES } from './credential-dialog.component';
import { FeaturePanelComponent } from './feature-panel.component';

const POLL_MS = 3000;

@Component({
  selector: 'sc-youtube-connect',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [forwardRef(() => YouTubeKeyDialogComponent), IconComponent, MatButtonModule],
  template: `
    @if (status(); as s) {
      <section class="connect" data-test="youtube-connect-section">
        @if (s.connected) {
          @if (s.method === 'oauth') {
            <div class="who">
              <span class="note" data-test="youtube-connected"
                >Connected to <strong>{{ s.channelTitle }}</strong></span
              >
              <span class="help" data-test="youtube-method">Signed in with Google</span>
            </div>
            @if (s.scopes.length > 0) {
              <p class="help" data-test="youtube-scopes">
                Google access: {{ shortScopes(s.scopes) }}
              </p>
            }
          } @else {
            <p class="note" data-test="youtube-connected">Connected with an API key</p>
            <p class="help" data-test="youtube-method">Public data only.</p>
          }
          @if (s.pending) {
            <div class="row">
              <p class="note" data-test="youtube-pending-connected">Waiting for the browser...</p>
              <button
                type="button"
                matButton="outlined"
                data-test="youtube-cancel-connected"
                (click)="cancel()"
              >
                Cancel
              </button>
            </div>
          }
          <div class="row">
            <button
              type="button"
              matButton="filled"
              data-test="youtube-open-studio"
              (click)="open(studioUrl(s.channelId))"
            >
              <sc-icon name="external" [size]="16" />Open YouTube Studio
            </button>
            @if (s.channelId !== null) {
              <button
                type="button"
                matButton="outlined"
                data-test="youtube-open-channel"
                (click)="open('https://www.youtube.com/channel/' + s.channelId)"
              >
                <sc-icon name="external" [size]="16" />Open channel
              </button>
            }
            <button
              type="button"
              matButton="outlined"
              data-test="youtube-open-operator"
              (click)="openOperator()"
            >
              <sc-icon name="video" [size]="16" />Open YouTube operator
            </button>
            <button
              type="button"
              matButton="outlined"
              class="danger"
              data-test="youtube-disconnect"
              [disabled]="busy()"
              (click)="askDisconnect(s.method)"
            >
              <svg
                viewBox="0 0 24 24"
                width="16"
                height="16"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path d="M9 7V3M15 7V3M7 7h10v4a5 5 0 0 1-10 0zM12 16v5M3 3l18 18" /></svg
              >Disconnect
            </button>
          </div>
        } @else if (s.pending) {
          <p class="note" data-test="youtube-pending">Waiting for the browser...</p>
          <div class="row">
            <button
              type="button"
              matButton="outlined"
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
          <div class="row">
            <button
              type="button"
              matButton="filled"
              data-test="youtube-sign-in"
              [disabled]="!s.configured || busy()"
              (click)="signIn()"
            >
              <sc-icon name="plug" [size]="16" />Sign in with Google
            </button>
            <button
              #useKeyButton
              type="button"
              matButton="outlined"
              data-test="youtube-use-key"
              (click)="keyDialogOpen.set(true)"
            >
              <svg
                viewBox="0 0 24 24"
                width="16"
                height="16"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <circle cx="8" cy="15" r="4" />
                <path d="M11 12l9-9M17 6l3 3" /></svg
              >Add an API key
            </button>
          </div>
        }
      </section>
    }
    @if (actionError(); as message) {
      <p class="err" data-test="youtube-action-error">{{ message }}</p>
    }
    @if (keyDialogOpen()) {
      <sc-youtube-key-dialog (saved)="keySaved()" (closed)="closeKeyDialog()" />
    }
  `,
  styles: [
    `
      .connect {
        display: flex;
        flex-direction: column;
        gap: 10px;
        align-items: flex-start;
      }
      .who {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .row {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        align-items: center;
      }
      .note {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg);
      }
      .help {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .link {
        align-self: flex-start;
        padding: 0;
        border: none;
        background: none;
        color: var(--sc-accent);
        font: inherit;
        font-size: 13px;
        cursor: pointer;
      }
      .err {
        margin: 0;
        font-size: 13px;
        color: var(--sc-error);
      }
      .danger {
        --mat-button-outlined-label-text-color: var(--sc-error);
      }
      button sc-icon,
      button svg {
        margin-right: 8px;
        flex: none;
      }
    `,
  ],
})
export class YouTubeConnectComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly notice = inject(NoticeService);
  private readonly dialog = inject(MatDialog);
  private readonly panel = inject(FeaturePanelComponent, { optional: true });
  private readonly useKeyButton = viewChild('useKeyButton', {
    read: ElementRef<HTMLButtonElement>,
  });
  private timer: ReturnType<typeof setTimeout> | null = null;
  private refreshing: Promise<void> | null = null;
  private destroyed = false;
  private unsubscribe: (() => void) | null = null;

  readonly status = signal<YouTubeStatus | null>(null);
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly keyDialogOpen = signal(false);
  readonly createKeyUrl = 'https://console.cloud.google.com/apis/credentials';
  readonly enableApiUrl = 'https://console.cloud.google.com/apis/library/youtube.googleapis.com';
  readonly clientFileExample = '{"client_id": ..., "client_secret": ...}';

  @Output() readonly connectedChange = new EventEmitter<boolean>();

  async ngOnInit(): Promise<void> {
    this.unsubscribe = this.bridge.onFeaturesChanged(() => void this.refresh());
    await this.refresh();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.unsubscribe?.();
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

  /** Disconnect signs out or removes the key, so it asks first with Cancel focused. */
  async askDisconnect(method: YouTubeStatus['method']): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: 'Disconnect YouTube?',
      body:
        method === 'apiKey'
          ? 'Scout removes the YouTube API key from this computer and the YouTube operator leaves the menu. Drafts already on YouTube stay there.'
          : 'Scout signs out of the channel on this computer and the YouTube operator leaves the menu. Drafts already on YouTube stay there.',
      confirmLabel: 'Disconnect',
      confirmIcon: 'plug-off',
    });
    if (confirmed) await this.disconnect(method);
  }

  async disconnect(method: YouTubeStatus['method']): Promise<void> {
    await this.run(async () => {
      if (method === 'apiKey') await this.bridge.youtubeClearApiKey();
      else await this.bridge.youtubeSignOut();
    });
    if (this.actionError() === null && this.status()?.connected === false)
      this.notice.show('YouTube disconnected', 'success');
  }

  /** Cancel or Esc closes the Connect YouTube dialog with nothing saved and gives the focus back to Add an API key. */
  closeKeyDialog(): void {
    this.keyDialogOpen.set(false);
    this.useKeyButton()?.nativeElement.focus();
  }

  /** Main kept the key: the dialog closes, the keyboard goes to the panel's close button, never Disconnect, and the card reads its status again. */
  async keySaved(): Promise<void> {
    this.keyDialogOpen.set(false);
    this.panel?.focusClose();
    await this.refresh();
  }

  shortScopes(scopes: readonly string[]): string {
    return scopes.map((scope) => scope.replace('https://www.googleapis.com/auth/', '')).join(', ');
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
      const before = this.status();
      this.status.set(status);
      if (before !== null && !before.connected && status.connected)
        this.notice.show(
          status.method === 'apiKey'
            ? 'YouTube connected with an API key'
            : `YouTube connected as ${status.channelTitle ?? 'your channel'}`,
          'success',
        );
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

const KEY_PERMISSIONS: readonly (readonly [string, string])[] = [
  ['Channel overview', "Read a channel's title, handle and counts"],
  ['Comments', 'Read the comments on your videos'],
];

const KEY_STEPS: readonly string[] = [
  'Create a project in the Google Cloud Console.',
  'Enable the YouTube Data API v3 for that project.',
  'Create an API key, or sign in with Google from Scout to read your own channel.',
];

/**
 * The credential dialog Add an API key opens: one required key field, checked by main before it is kept. Empty, it saves
 * nothing; refused or failed, it stays open with main's sentence and the field emptied; kept, it says so to the card.
 */
@Component({
  selector: 'sc-youtube-key-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CdkTrapFocus, MatButtonModule],
  template: `
    <div
      class="backdrop"
      role="dialog"
      aria-modal="true"
      aria-labelledby="youtube-key-title"
      data-test="youtube-key-dialog"
      (keydown.escape)="onEscape($event)"
    >
      <div class="dialog" cdkTrapFocus>
        <header>
          <h2 id="youtube-key-title" data-test="youtube-key-title">Connect YouTube</h2>
          <p class="lede" data-test="youtube-key-lede">
            Fields marked * are required. Values stay in this computer's protected storage.
          </p>
        </header>
        <div class="body">
          <div class="field">
            <label class="label" for="youtube-key-input">YouTube Data API key *</label>
            <input
              #keyInput
              id="youtube-key-input"
              type="password"
              autocomplete="off"
              aria-describedby="youtube-key-help"
              data-test="youtube-key-input"
              [attr.aria-invalid]="empty() ? 'true' : null"
              (input)="empty.set(false)"
              (keydown.enter)="save()"
            />
            <p
              id="youtube-key-help"
              class="helper"
              [class.helper--err]="empty()"
              data-test="youtube-key-help"
            >
              {{
                empty()
                  ? 'Enter the API key.'
                  : 'Create it in the Google Cloud Console (APIs & Services, Credentials) for a project with the YouTube Data API v3 enabled. It reads public data only.'
              }}
            </p>
          </div>
          <details class="scope-matrix" data-test="youtube-key-permissions">
            <summary>Required permissions</summary>
            <table>
              <thead>
                <tr>
                  <th>Permission</th>
                  <th>What it enables</th>
                </tr>
              </thead>
              <tbody>
                @for (row of permissions; track row[0]) {
                  <tr>
                    <td class="perm">{{ row[0] }}</td>
                    <td>{{ row[1] }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </details>
          <section class="setup-steps" aria-labelledby="youtube-key-steps-title">
            <h3 id="youtube-key-steps-title">How to get this credential</h3>
            <ol data-test="youtube-key-steps">
              @for (step of steps; track $index) {
                <li>{{ step }}</li>
              }
            </ol>
          </section>
          @if (alert(); as message) {
            <p class="form-err" role="alert" data-test="youtube-key-alert">
              <svg
                viewBox="0 0 24 24"
                width="16"
                height="16"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                aria-hidden="true"
              >
                <circle cx="12" cy="12" r="9" />
                <path d="M12 7v6M12 16.5v.5" /></svg
              >{{ message }}
            </p>
          }
        </div>
        <footer>
          <button
            #cancelButton
            type="button"
            matButton="outlined"
            data-test="youtube-key-cancel"
            [disabled]="saving()"
            (click)="closed.emit()"
          >
            Cancel
          </button>
          <button
            type="button"
            matButton="filled"
            data-test="youtube-key-save"
            [disabled]="saving()"
            (click)="save()"
          >
            <svg
              viewBox="0 0 24 24"
              width="16"
              height="16"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path
                [attr.d]="
                  saving() ? 'M21 12a9 9 0 1 1-9-9' : 'M5 3h11l3 3v15H5zM8 3v6h8V3M8 21v-7h8v7'
                "
              /></svg
            >{{ saving() ? 'Saving…' : 'Save' }}
          </button>
        </footer>
      </div>
    </div>
  `,
  styles: [
    DIALOG_STYLES,
    `
      .helper--err {
        color: var(--sc-error);
      }
      .form-err {
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .form-err svg {
        flex: none;
      }
    `,
  ],
})
export class YouTubeKeyDialogComponent implements AfterViewInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly cancelButton = viewChild.required('cancelButton', {
    read: ElementRef<HTMLButtonElement>,
  });
  private readonly keyInput = viewChild.required<ElementRef<HTMLInputElement>>('keyInput');
  private readonly message = signal<string | null>(null);

  readonly permissions = KEY_PERMISSIONS;
  readonly steps = KEY_STEPS;
  /** True once Save was pressed with the field empty, until something is typed. */
  readonly empty = signal(false);
  readonly saving = signal(false);
  /** The line under the form: the empty field, or why main refused or could not keep the key. */
  readonly alert = computed(() =>
    this.empty() ? 'One required field is empty. Nothing was saved.' : this.message(),
  );

  readonly saved = output<void>();
  readonly closed = output<void>();

  /** Cancel takes the focus when the dialog opens, so Enter never saves by accident. */
  ngAfterViewInit(): void {
    this.cancelButton().nativeElement.focus();
  }

  /** Esc closes the dialog and not the side panel under it; while a save is on its way it does nothing, as Cancel. */
  onEscape(event: Event): void {
    event.stopPropagation();
    if (!this.saving()) this.closed.emit();
  }

  async save(): Promise<void> {
    if (this.saving()) return;
    const input = this.keyInput().nativeElement;
    const apiKey = input.value;
    this.message.set(null);
    if (apiKey.trim() === '') {
      this.empty.set(true);
      return;
    }
    this.saving.set(true);
    try {
      await this.bridge.youtubeSaveApiKey({ apiKey });
    } catch (err: unknown) {
      input.value = '';
      this.message.set(readIpcError(err).message);
      this.saving.set(false);
      input.focus();
      return;
    }
    input.value = '';
    this.saved.emit();
  }
}
