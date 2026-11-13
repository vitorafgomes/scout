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
  input,
  output,
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

import { CdkTrapFocus } from '@angular/cdk/a11y';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';

import { confirmAction } from '../core/confirm-dialog.component';
import { IconComponent } from '../core/icon.component';
import { NoticeService } from '../core/notice.service';
import { ScoutBridgeService } from '../core/scout-bridge';

import { CollapsibleListComponent } from './collapsible-list.component';
import { DIALOG_STYLES } from './credential-dialog.component';
import type { DockerCardStatus } from './docker-card.component';
import { FeaturePanelComponent } from './feature-panel.component';

export const DOCKER_HINT =
  "Turn on Docker to let Scout run a pull request's tests in an isolated container before it gives a verdict.";

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
  imports: [
    CollapsibleListComponent,
    forwardRef(() => GitHubTokenDialogComponent),
    IconComponent,
    MatButtonModule,
  ],
  template: `
    @if (status(); as s) {
      <section class="connect" data-test="github-connect-section">
        @if (s.connected) {
          <div class="row">
            <span class="note connected" data-test="github-connected"
              >Connected as <strong>{{ s.login }}</strong
              >{{ installedOn(s) }}</span
            >
            @if (s.method === 'browser' && !s.installed && s.installUrl !== null) {
              <button
                type="button"
                matButton="filled"
                data-test="github-install"
                (click)="open(s.installUrl)"
              >
                <sc-icon name="external" [size]="16" />Install Scout on your GitHub account
              </button>
            }
            <button
              type="button"
              matButton="outlined"
              class="danger"
              data-test="github-disconnect"
              [disabled]="busy()"
              (click)="askDisconnect()"
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
          @if (s.warning !== null) {
            <p class="warn" data-test="github-warning">{{ s.warning }}</p>
          }
          @if (showDockerHint(s.connected)) {
            <div class="docker-hint" role="note" data-test="github-docker-hint">
              <sc-icon name="info" [size]="16" />
              <span class="docker-hint-text">{{ dockerHint }}</span>
              <button
                type="button"
                matButton="outlined"
                class="hint-btn"
                data-test="github-setup-docker"
                (click)="setupDocker.emit()"
              >
                <sc-icon name="cube" [size]="16" />Set up Docker
              </button>
            </div>
          }
        } @else if (s.pending !== null) {
          <p class="note">Enter this code on GitHub:</p>
          <code class="code" data-test="github-user-code">{{ s.pending.userCode }}</code>
          <div class="row">
            <button
              type="button"
              matButton="filled"
              data-test="github-open"
              (click)="open(s.pending.verificationUri)"
            >
              <sc-icon name="external" [size]="16" />Open GitHub
            </button>
            <button
              type="button"
              matButton="outlined"
              data-test="github-copy"
              (click)="copy(s.pending.userCode)"
            >
              <sc-icon name="copy" [size]="16" />Copy code
            </button>
            <button type="button" matButton="outlined" data-test="github-cancel" (click)="cancel()">
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
          <div class="row">
            <button
              type="button"
              matButton="filled"
              data-test="github-connect"
              [disabled]="!s.configured || busy()"
              (click)="connect()"
            >
              <sc-icon name="plug" [size]="16" />Connect with GitHub
            </button>
            <button
              #useTokenButton
              type="button"
              matButton="outlined"
              data-test="github-use-token"
              (click)="tokenDialogOpen.set(true)"
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
              >Use a token
            </button>
          </div>
          <span class="help" data-test="github-connect-help"
            >Scout shows a one-time code; you enter it on GitHub and approve the Scout GitHub App
            there.</span
          >
        }
        @if (s.connected && bot(); as b) {
          <section class="bot" aria-labelledby="github-bot-title" data-test="github-bot-section">
            <h3 id="github-bot-title" class="bot-title">
              Use a Scout bot on GitHub <span class="optional">(optional)</span>
            </h3>
            <p class="lede" data-test="github-bot-help">
              Reviews, verdicts, comments and issues Scout posts show as the bot instead of you.
              GitHub creates an App that you own; Scout keeps its key on this computer.
            </p>
            @switch (b.state) {
              @case ('none') {
                <div class="row row--fields">
                  <label class="field"
                    >Create the bot on
                    <select
                      class="input"
                      data-test="github-bot-target"
                      [value]="botTarget()"
                      (change)="botTarget.set(targetOf($event))"
                    >
                      <option value="user">My account</option>
                      <option value="org">An organization</option>
                    </select>
                  </label>
                  @if (botTarget() === 'org') {
                    <label class="field"
                      >Organization
                      <input
                        #orgInput
                        type="text"
                        class="input input--org"
                        placeholder="acme"
                        data-test="github-bot-org"
                        [value]="botOrg()"
                        (input)="botOrg.set($any($event.target).value)"
                      />
                    </label>
                  }
                  <button
                    type="button"
                    matButton="outlined"
                    data-test="github-bot-create"
                    aria-describedby="github-bot-reason"
                    [disabled]="busy() || orgMissing()"
                    (click)="createBot(orgValue())"
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
                      <rect x="4" y="8" width="16" height="12" rx="3" />
                      <path d="M12 8V4M9 4h6M9 14h.01M15 14h.01" /></svg
                    >Create the bot on GitHub
                  </button>
                </div>
                <span id="github-bot-reason" class="help" data-test="github-bot-reason">{{
                  orgMissing()
                    ? 'Type the organization first.'
                    : 'GitHub opens in your browser to confirm the App.'
                }}</span>
              }
              @case ('creating') {
                <p class="note" data-test="github-bot-creating">
                  Finish creating the App on GitHub in your browser.
                </p>
                <div class="row">
                  <button
                    type="button"
                    matButton="outlined"
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
                      matButton="filled"
                      data-test="github-bot-install"
                      (click)="open(b.installUrl)"
                    >
                      <sc-icon name="external" [size]="16" />Install the bot
                    </button>
                  }
                  <button
                    type="button"
                    matButton="outlined"
                    data-test="github-bot-remove"
                    [disabled]="busy()"
                    class="danger"
                    (click)="askRemoveBot(b)"
                  >
                    <sc-icon name="trash" [size]="16" />Remove the bot
                  </button>
                </div>
              }
              @case ('installed') {
                <sc-collapsible-list
                  key="github-bot-repos"
                  filterLabel="Filter the repositories"
                  data-test="github-bot-installed"
                  [summary]="botReposSummary(b)"
                  [items]="b.repositories"
                >
                  <ng-template let-repo>{{ repo }}</ng-template>
                </sc-collapsible-list>
                <p class="help">Signed by the bot; an action switched off is signed as you.</p>
                <ul class="bot-writes" aria-label="What the bot signs">
                  @for (w of writes; track w) {
                    <li>
                      <label class="switch"
                        ><span class="switch-label">{{ writeLabels[w] }}</span
                        ><input
                          type="checkbox"
                          role="switch"
                          [attr.data-test]="'github-bot-write-' + w"
                          [checked]="b.writes.includes(w)"
                          [disabled]="busy()"
                          (change)="switchWrite(b, w, $any($event.target))"
                      /></label>
                    </li>
                  }
                </ul>
                <div class="row">
                  @if (b.installUrl !== null) {
                    <button
                      type="button"
                      matButton="outlined"
                      data-test="github-bot-install"
                      (click)="open(b.installUrl)"
                    >
                      Change repositories
                    </button>
                  }
                  <button
                    type="button"
                    matButton="outlined"
                    data-test="github-bot-remove"
                    [disabled]="busy()"
                    class="danger"
                    (click)="askRemoveBot(b)"
                  >
                    <sc-icon name="trash" [size]="16" />Remove the bot
                  </button>
                </div>
              }
            }
            @if (mentionHandle(b); as handle) {
              <div class="picture" data-test="github-bot-mention">
                <span data-test="github-bot-name">Name: {{ b.name ?? 'not read yet' }}</span>
                <span data-test="github-bot-handle"
                  >Handle: <code class="url">{{ handle }}</code></span
                >
                @if (b.nameError !== null) {
                  <span class="err" data-test="github-bot-name-error">{{ b.nameError }}</span>
                }
                <span data-test="github-bot-mention-line"
                  >Ask for a review with <code class="url">{{ handle }}</code></span
                >
                <button
                  type="button"
                  matButton="outlined"
                  data-test="github-bot-mention-copy"
                  [attr.aria-label]="'Copy ' + handle"
                  (click)="copy(handle)"
                >
                  <sc-icon name="copy" [size]="16" />Copy
                </button>
                <span class="help" data-test="github-bot-mention-help"
                  >Mention it in a pull request's description or a comment; the bell tells you who
                  asked.</span
                >
              </div>
            }
            @if (pictureUrl(b); as url) {
              <div class="picture" data-test="github-bot-picture">
                <button
                  type="button"
                  matButton="outlined"
                  data-test="github-bot-picture-open"
                  aria-describedby="github-bot-picture-help"
                  (click)="open(url)"
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
                    <rect x="3" y="5" width="18" height="14" rx="2" />
                    <circle cx="9" cy="10" r="2" />
                    <path d="M21 16l-5-5-8 8" /></svg
                  >Add a picture on GitHub
                </button>
                <div class="row">
                  <button
                    type="button"
                    matButton="outlined"
                    data-test="github-bot-rename"
                    aria-describedby="github-bot-rename-help"
                    (click)="open(url)"
                  >
                    Change the name on GitHub
                  </button>
                  <button
                    type="button"
                    matButton="outlined"
                    data-test="github-bot-refresh"
                    (click)="reread()"
                  >
                    Refresh
                  </button>
                </div>
                <span id="github-bot-rename-help" class="help" data-test="github-bot-rename-help"
                  >GitHub renames an App only on its settings page; Scout reads the new name when
                  this card opens again or when you click Refresh.</span
                >
                <span id="github-bot-picture-help" class="help" data-test="github-bot-picture-help"
                  >GitHub shows this picture on everything the bot signs. Actions you sign yourself
                  keep your own picture.</span
                >
                <code class="url" data-test="github-bot-picture-url">{{ url }}</code>
              </div>
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
        } @else if (s.connected && botError(); as reason) {
          <section class="bot" aria-labelledby="github-bot-title" data-test="github-bot-section">
            <h3 id="github-bot-title" class="bot-title">
              Use a Scout bot on GitHub <span class="optional">(optional)</span>
            </h3>
            <p class="err" data-test="github-bot-error">{{ reason }}</p>
          </section>
        } @else if (!s.connected) {
          <section class="bot" aria-labelledby="github-bot-title" data-test="github-bot-section">
            <h3 id="github-bot-title" class="bot-title">
              Use a Scout bot on GitHub <span class="optional">(optional)</span>
            </h3>
            <p class="lede" data-test="github-bot-help">
              Reviews, verdicts, comments and issues Scout posts show as the bot instead of you.
              GitHub creates an App that you own; Scout keeps its key on this computer.
            </p>
            <div class="row row--fields">
              <label class="field"
                >Create the bot on
                <select
                  class="input"
                  data-test="github-bot-target"
                  [value]="botTarget()"
                  (change)="botTarget.set(targetOf($event))"
                >
                  <option value="user">My account</option>
                  <option value="org">An organization</option>
                </select>
              </label>
              @if (botTarget() === 'org') {
                <label class="field"
                  >Organization
                  <input
                    type="text"
                    class="input input--org"
                    placeholder="acme"
                    data-test="github-bot-org"
                    [value]="botOrg()"
                    (input)="botOrg.set($any($event.target).value)"
                  />
                </label>
              }
              <button
                type="button"
                matButton="outlined"
                data-test="github-bot-create"
                aria-describedby="github-bot-reason"
                disabled
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
                  <rect x="4" y="8" width="16" height="12" rx="3" />
                  <path d="M12 8V4M9 4h6M9 14h.01M15 14h.01" /></svg
                >Create the bot on GitHub
              </button>
            </div>
            <span id="github-bot-reason" class="help" data-test="github-bot-reason"
              >Connect GitHub first to create the bot.</span
            >
          </section>
        }
      </section>
    }
    @if (actionError(); as message) {
      <p class="err" data-test="github-action-error">{{ message }}</p>
    }
    @if (tokenDialogOpen()) {
      <sc-github-token-dialog (saved)="tokenSaved($event)" (closed)="closeTokenDialog()" />
    }
  `,
  styles: [
    `
      .connect,
      .bot {
        display: flex;
        flex-direction: column;
        gap: 10px;
        align-items: flex-start;
      }
      .bot {
        margin-top: 4px;
        --sc-list-row-bg: var(--sc-surface);
        padding: 12px;
        border: 1px solid var(--sc-line);
        border-radius: 10px;
        background: var(--sc-bg);
        align-self: stretch;
      }
      .bot-title {
        margin: 0;
        font-size: 14px;
        font-weight: 600;
        color: var(--sc-fg);
      }
      .optional {
        font-weight: 400;
        color: var(--sc-muted);
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 4px;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .connected {
        margin-right: auto;
      }
      .lede {
        margin: 0;
        font-size: 13px;
        line-height: 1.5;
        color: var(--sc-fg-2);
      }
      .row--fields {
        align-items: flex-end;
        gap: 10px;
      }
      .bot-writes {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 4px;
        align-self: stretch;
      }
      .switch {
        display: flex;
        gap: 12px;
        align-items: center;
        padding: 6px 4px;
        font-size: 13px;
        color: var(--sc-fg);
        cursor: pointer;
      }
      .switch-label {
        margin-right: auto;
      }
      .switch input {
        appearance: none;
        position: relative;
        flex: none;
        width: 40px;
        height: 24px;
        margin: 0;
        box-sizing: border-box;
        border-radius: 12px;
        border: 1px solid var(--sc-line);
        background: var(--sc-surface-2);
        cursor: pointer;
      }
      .switch input::after {
        content: '';
        position: absolute;
        top: 3px;
        left: 3px;
        width: 16px;
        height: 16px;
        border-radius: 8px;
        background: var(--sc-muted);
      }
      .switch input:checked {
        border-color: var(--sc-accent-strong);
        background: var(--sc-accent-strong);
      }
      .switch input:checked::after {
        left: 19px;
        background: var(--sc-on-accent);
      }
      .switch input:disabled {
        opacity: 0.55;
        cursor: default;
      }
      .row {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        align-items: center;
        align-self: stretch;
      }
      .note {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg);
      }
      .code {
        font-family: var(--sc-mono);
        font-size: 28px;
        letter-spacing: 0.12em;
        color: var(--sc-fg);
        user-select: all;
      }
      .docker-hint {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
        align-self: stretch;
      }
      .docker-hint-text {
        flex: 1 1 240px;
        font-size: 13px;
        line-height: 1.45;
      }
      .hint-btn {
        --mat-button-outlined-label-text-color: var(--sc-on-info-container);
        --mat-button-outlined-outline-color: var(--sc-on-info-container);
        --mat-button-outlined-label-text-weight: 500;
      }
      .warn {
        margin: 0;
        font-size: 13px;
        padding: 8px 10px;
        border-radius: 10px;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
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
      .input {
        flex: 1;
        box-sizing: border-box;
        height: 36px;
        font: inherit;
        font-size: 14px;
        padding: 0 12px;
        border-radius: var(--sc-radius-sm);
        border: 1px solid var(--sc-line);
        background: var(--sc-surface);
        color: var(--sc-fg);
      }
      .err {
        margin: 0;
        font-size: 13px;
        color: var(--sc-error);
      }
      .danger {
        --mat-button-outlined-label-text-color: var(--sc-error);
      }
      .input--org {
        flex: none;
        width: 160px;
      }
      .picture {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: 6px;
        font-size: 13px;
        align-self: stretch;
        padding-top: 10px;
        border-top: 1px solid var(--sc-line);
      }
      .url {
        font-size: 12px;
        color: var(--sc-muted);
        overflow-wrap: anywhere;
      }
      .picture span > .url {
        color: inherit;
      }
      button sc-icon,
      button svg {
        margin-right: 8px;
        flex: none;
      }
    `,
  ],
})
export class GitHubConnectComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly dialog = inject(MatDialog);
  private readonly notice = inject(NoticeService);
  private readonly panel = inject(FeaturePanelComponent, { optional: true });
  private timer: ReturnType<typeof setTimeout> | null = null;
  private refreshing: Promise<void> | null = null;
  private destroyed = false;
  private stopListening: (() => void) | null = null;

  readonly status = signal<GitHubStatus | null>(null);
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly tokenDialogOpen = signal(false);
  readonly bot = signal<GitHubBotStatus | null>(null);
  /** Why the Scout bot's status could not be read while GitHub is connected; null otherwise. */
  readonly botError = signal<string | null>(null);
  readonly botTarget = signal<'user' | 'org'>('user');
  readonly botOrg = signal('');
  /** True while the bot is to be created on an organization whose name is still empty. */
  readonly orgMissing = computed(() => this.botTarget() === 'org' && this.botOrg().trim() === '');
  readonly removedSettingsUrl = signal<string | null>(null);
  readonly writes = GITHUB_BOT_WRITES;
  readonly writeLabels = BOT_WRITE_LABELS;
  private readonly orgInput = viewChild<ElementRef<HTMLInputElement>>('orgInput');
  private readonly useTokenButton = viewChild('useTokenButton', {
    read: ElementRef<HTMLButtonElement>,
  });

  @Output() readonly connectedChange = new EventEmitter<boolean>();
  /** Docker's state and the owner's switch, from the Docker card; null until the card has read them. */
  readonly docker = input<DockerCardStatus | null>(null);
  readonly setupDocker = output<void>();
  readonly dockerHint = DOCKER_HINT;

  /** The hint shows while GitHub is connected and Docker is not running or its runs are off. */
  showDockerHint(connected: boolean): boolean {
    const docker = this.docker();
    return connected && docker !== null && (!docker.running || !docker.enabled);
  }

  /** Reads the card, and reads it again whenever main says the features changed, as a dropped GitHub session does. */
  async ngOnInit(): Promise<void> {
    this.stopListening = this.bridge.onFeaturesChanged(() => void this.refresh());
    await this.refresh();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.stopListening?.();
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

  /** Disconnect forgets the sign-in, so it asks first with Cancel focused. */
  async askDisconnect(): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: 'Disconnect GitHub?',
      body: 'Scout stops reading and writing on GitHub and forgets the sign-in on this computer. The Scout GitHub App stays installed on your account until you remove it there.',
      confirmLabel: 'Disconnect',
      confirmIcon: 'plug-off',
    });
    if (confirmed) await this.disconnect();
  }

  async disconnect(): Promise<void> {
    await this.run(async () => {
      await this.bridge.githubSignOut();
    });
    if (this.actionError() === null && this.status()?.connected === false)
      this.notice.show('GitHub disconnected', 'success');
  }

  /** Cancel or Esc closes the token dialog with nothing saved and gives the focus back to Use a token. */
  closeTokenDialog(): void {
    this.tokenDialogOpen.set(false);
    this.useTokenButton()?.nativeElement.focus();
  }

  /**
   * Main kept the token: the card takes the status it answered, puts the keyboard on the panel's close button (never on
   * Disconnect), says so, and reads GitHub and the bot again.
   */
  async tokenSaved(status: GitHubStatus): Promise<void> {
    this.tokenDialogOpen.set(false);
    this.panel?.focusClose();
    this.status.set(status);
    this.notice.show('GitHub credentials saved', 'success');
    await this.refresh();
  }

  async createBot(org: string): Promise<void> {
    if (this.status()?.connected !== true) return;
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

  /** Removing the bot changes who signs Scout's posts, so it asks first with Cancel focused. */
  async askRemoveBot(current: GitHubBotStatus): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: 'Remove the Scout bot?',
      body: 'Scout stops signing as the bot and acts as you again. The App stays on GitHub until you delete it there.',
      confirmLabel: 'Remove',
    });
    if (confirmed) await this.removeBot(current);
  }

  async removeBot(current: GitHubBotStatus): Promise<void> {
    await this.run(async () => {
      await this.bridge.githubBotRemove();
      this.removedSettingsUrl.set(current.settingsUrl);
      this.notice.show('Scout no longer uses the bot', 'success');
    });
  }

  /** Sends the switched action; the box then shows what main holds, so a refused switch goes back. */
  async switchWrite(
    current: GitHubBotStatus,
    write: GitHubBotWrite,
    box?: HTMLInputElement,
  ): Promise<void> {
    const writes = current.writes.includes(write)
      ? current.writes.filter((w) => w !== write)
      : GITHUB_BOT_WRITES.filter((w) => w === write || current.writes.includes(w));
    await this.run(async () => {
      try {
        await this.bridge.githubBotSetWrites({ writes });
      } catch (err: unknown) {
        if (box !== undefined) box.checked = current.writes.includes(write);
        throw err;
      }
    });
    if (box !== undefined) box.checked = (this.bot() ?? current).writes.includes(write);
  }

  targetOf(event: Event): 'user' | 'org' {
    return (event.target as HTMLSelectElement).value === 'org' ? 'org' : 'user';
  }

  /** The bot's repositories in one line: how many, and that the bot can post in them. */
  botReposSummary(bot: GitHubBotStatus): string {
    return `Repositories: ${bot.repositories.length} (${bot.login ?? 'the bot'} can post in these)`;
  }

  /** The App's settings page, where GitHub takes the bot's picture; only once the bot exists, created or installed. */
  pictureUrl(bot: GitHubBotStatus): string | null {
    return bot.state === 'created' || bot.state === 'installed' ? bot.settingsUrl : null;
  }

  /** The handle that asks the bot for a review, `@<slug>`, once the bot exists, created or installed. */
  mentionHandle(bot: GitHubBotStatus): string | null {
    if ((bot.state !== 'created' && bot.state !== 'installed') || bot.login === null) return null;
    return `@${bot.login.replace(/\[bot\]$/, '')}`;
  }

  /** Reads the GitHub status and the bot again, so a name or handle changed on GitHub shows at once. */
  async reread(): Promise<void> {
    await this.refresh();
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
      const before = this.status();
      this.status.set(status);
      if (before !== null && !before.connected && status.connected)
        this.notice.show(
          status.login === null ? 'GitHub connected' : `GitHub connected as ${status.login}`,
          'success',
        );
      this.connectedChange.emit(status.connected);
      const bot = status.connected ? await this.loadBot() : null;
      if (!status.connected) this.botError.set(null);
      if (this.destroyed) return;
      this.announceBot(this.bot(), bot);
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

  private announceBot(before: GitHubBotStatus | null, after: GitHubBotStatus | null): void {
    if (before === null || after === null) return;
    const login = after.login ?? 'The bot';
    if (after.state === 'installed' && before.state !== 'installed') {
      const n = after.repositories.length;
      this.notice.show(
        `${login} is installed on ${n} ${n === 1 ? 'repository' : 'repositories'}`,
        'success',
      );
    } else if (
      after.state === 'created' &&
      (before.state === 'none' || before.state === 'creating')
    ) {
      this.notice.show(`${login} was created on GitHub`, 'success');
    }
  }

  private async loadBot(): Promise<GitHubBotStatus | null> {
    try {
      const bot = await this.bridge.githubBotStatus();
      this.botError.set(null);
      return bot;
    } catch (err: unknown) {
      this.botError.set(readIpcError(err).message);
      return null;
    }
  }

  private stopPolling(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }

  /** What follows "Connected as <login>": how many repositories the Scout GitHub App is installed on, when main counted any. */
  installedOn(status: GitHubStatus): string {
    const n = status.repositories;
    if (n === null || n === 0) return '';
    return ` · installed on ${n} ${n === 1 ? 'repository' : 'repositories'}`;
  }
}

const TOKEN_PERMISSIONS: readonly (readonly [string, string])[] = [
  ['Metadata', 'List the repositories Scout is installed on'],
  ['Contents', 'Read files and search code'],
  ['Pull requests', 'Read pull requests, diffs, checks and reviews'],
  ['Issues', 'Read issues'],
];

const TOKEN_STEPS: readonly string[] = [
  'Click Create a fine-grained token on GitHub and sign in there.',
  'Name the token, choose when it expires and pick the repositories Scout should see.',
  'Under Repository permissions, give read-only access to Metadata, Contents, Checks, Commit statuses, Pull requests and Issues.',
  'Click Generate token, copy the token, paste it here and click Save.',
];

/**
 * The credential dialog GitHub's Use a token opens: one required token field, checked by main before it is kept. Empty, it
 * saves nothing; refused, it stays open with main's reason and the field emptied; saved, it hands main's status back.
 */
@Component({
  selector: 'sc-github-token-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CdkTrapFocus, MatButtonModule],
  template: `
    <div
      class="backdrop"
      role="dialog"
      aria-modal="true"
      aria-labelledby="github-token-title"
      data-test="github-token-dialog"
      (keydown.escape)="onEscape($event)"
    >
      <div class="dialog" cdkTrapFocus>
        <header>
          <h2 id="github-token-title" data-test="github-token-title">Connect GitHub</h2>
          <p class="lede" data-test="github-token-lede">
            Fields marked * are required. Values stay in this computer's protected storage.
          </p>
        </header>
        <div class="body">
          <div class="field">
            <label class="label" for="github-token-input">Personal access token *</label>
            <input
              #tokenInput
              id="github-token-input"
              type="password"
              autocomplete="off"
              aria-describedby="github-token-help"
              data-test="github-token-input"
              [attr.aria-invalid]="empty() ? 'true' : null"
              (input)="empty.set(false)"
              (keydown.enter)="save()"
            />
            <p
              id="github-token-help"
              class="helper"
              [class.helper--err]="empty()"
              data-test="github-token-help"
            >
              {{
                empty()
                  ? 'Enter a GitHub token.'
                  : 'Read access to Metadata, Contents, Checks, Commit statuses, Pull requests and Issues, on the repositories Scout should see.'
              }}
            </p>
            <a
              class="link-btn"
              data-test="github-create-token"
              [href]="createTokenUrl"
              (click)="openCreateToken($event)"
              >Create a fine-grained token on GitHub<svg
                viewBox="0 0 24 24"
                width="13"
                height="13"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path d="M7 17L17 7M8 7h9v9" /></svg
            ></a>
          </div>
          <details class="scope-matrix" data-test="github-token-permissions">
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
          <section class="setup-steps" aria-labelledby="github-token-steps-title">
            <h3 id="github-token-steps-title">How to get this credential</h3>
            <ol data-test="github-token-steps">
              @for (step of steps; track $index) {
                <li>{{ step }}</li>
              }
            </ol>
          </section>
          @if (alert(); as message) {
            <p class="form-err" role="alert" data-test="github-token-alert">
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
            data-test="github-token-cancel"
            [disabled]="saving()"
            (click)="closed.emit()"
          >
            Cancel
          </button>
          <button
            type="button"
            matButton="filled"
            data-test="github-token-save"
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
      .link-btn {
        display: inline-flex;
        align-items: center;
        gap: 4px;
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
export class GitHubTokenDialogComponent implements AfterViewInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly cancelButton = viewChild.required('cancelButton', {
    read: ElementRef<HTMLButtonElement>,
  });
  private readonly tokenInput = viewChild.required<ElementRef<HTMLInputElement>>('tokenInput');
  private readonly message = signal<string | null>(null);

  readonly createTokenUrl = 'https://github.com/settings/personal-access-tokens/new';
  readonly permissions = TOKEN_PERMISSIONS;
  readonly steps = TOKEN_STEPS;
  /** True once Save was pressed with the field empty, until something is typed. */
  readonly empty = signal(false);
  readonly saving = signal(false);
  /** The line under the form: the empty field, or why main or the browser refused. */
  readonly alert = computed(() =>
    this.empty() ? 'One required field is empty. Nothing was saved.' : this.message(),
  );

  readonly saved = output<GitHubStatus>();
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

  async openCreateToken(event: Event): Promise<void> {
    event.preventDefault();
    try {
      await this.bridge.appOpenExternal({ url: this.createTokenUrl });
    } catch (err: unknown) {
      this.message.set(readIpcError(err).message);
    }
  }

  async save(): Promise<void> {
    if (this.saving()) return;
    const input = this.tokenInput().nativeElement;
    const token = input.value;
    this.message.set(null);
    if (token.trim() === '') {
      this.empty.set(true);
      return;
    }
    this.saving.set(true);
    let status: GitHubStatus;
    try {
      status = await this.bridge.githubSaveToken({ token });
    } catch (err: unknown) {
      input.value = '';
      this.message.set(readIpcError(err).message);
      this.saving.set(false);
      input.focus();
      return;
    }
    this.saved.emit(status);
  }
}
