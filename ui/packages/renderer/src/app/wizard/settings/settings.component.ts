import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { MatSlideToggleModule, type MatSlideToggleChange } from '@angular/material/slide-toggle';
import {
  readIpcError,
  type DesktopSettings,
  type LocalBubbleSettings,
  type LocalBubbleStatus,
  type LocalMonitor,
  type UpdateChannel,
  type UpdateNotice,
  type UpdateProgress,
  type UpdateState,
} from '@scout/shared';

import { confirmAction } from '../../core/confirm-dialog.component';
import { IconComponent } from '../../core/icon.component';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { THEME_OPTIONS, ThemeStore } from '../../core/theme.store';
import { AudioSettingsComponent } from './audio-settings.component';
import { ChatClientsComponent } from './chat-clients.component';
import { captureShortcut, keycapsOf } from './quick-ask-shortcut';
import { SETTINGS_SWITCH_STYLES } from './settings-switch.styles';
import { WorkerSettingsComponent } from './worker-settings.component';

const REPOSITORY_URL = 'https://github.com/vitorafgomes/scout';
const LICENCE_URL = 'https://github.com/vitorafgomes/scout/blob/main/LICENSE';
const CHANGELOG_URL = 'https://github.com/vitorafgomes/scout/releases';
const SECURITY_URL = 'https://github.com/vitorafgomes/scout/security/advisories/new';

const CHANNEL_OPTIONS: readonly { value: UpdateChannel; label: string }[] = [
  { value: 'stable', label: 'Stable' },
  { value: 'beta', label: 'Beta' },
];

const CHANNEL_HINTS: Readonly<Record<UpdateChannel, string>> = {
  stable: 'Stable gets released versions only.',
  beta: 'Beta also gets pre-releases, to try changes before they are released. A pre-release can have bugs; keep Stable for daily work.',
};

const CHECK_FAILURES: Readonly<Record<string, string>> = {
  network: 'no connection to GitHub',
  timeout: 'GitHub did not answer in time',
  parse: 'GitHub sent an unexpected answer',
  incomplete: 'the download did not finish',
};

const CORNERS: readonly { value: LocalBubbleSettings['corner']; label: string }[] = [
  { value: 'top-left', label: 'Top left' },
  { value: 'top-right', label: 'Top right' },
  { value: 'bottom-left', label: 'Bottom left' },
  { value: 'bottom-right', label: 'Bottom right' },
];

const ABOUT_REGION = 'about-more';

const LAST_CHECK_DATE = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
});

const RECORDING_LINE = 'Recording. Press the new shortcut, or Esc to cancel.';

@Component({
  selector: 'sc-settings',
  standalone: true,
  imports: [
    AudioSettingsComponent,
    ChatClientsComponent,
    IconComponent,
    MatSlideToggleModule,
    WorkerSettingsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="settings">
      <header class="page-head">
        <h1>Settings</h1>
        <p class="description">
          Chat connections, the Scout worker, who can use Scout, the quick-ask button, the
          microphone and speaker, and how this app looks.
        </p>
      </header>

      <div class="columns">
        <div class="column">
          <section class="card" aria-labelledby="s-appearance">
            <h2 id="s-appearance">Appearance</h2>
            <div class="segmented segmented--theme" role="group" aria-label="Theme">
              @for (option of themeOptions; track option.choice) {
                <button
                  type="button"
                  class="theme-choice"
                  [attr.data-test]="'settings-theme-' + option.choice"
                  [attr.aria-label]="option.ariaLabel"
                  [attr.aria-pressed]="theme.choice() === option.choice"
                  [title]="option.ariaLabel"
                  (click)="theme.set(option.choice)"
                >
                  <sc-icon [name]="option.icon" [size]="16" />
                  {{ option.label }}
                </button>
              }
            </div>
            <p class="hint">
              The same choice as the switch in the top bar. System follows your desktop's light or
              dark setting, in every Scout window.
            </p>
            @if (theme.error(); as error) {
              <p class="error" role="alert" data-test="theme-error">{{ error }}</p>
            }
          </section>

          <sc-chat-clients />

          <sc-worker-settings />

          <section class="card about" aria-labelledby="s-about" data-test="about-section">
            <h2 id="s-about">About</h2>
            <div class="about-name">
              <span class="name">Scout</span>
              <span class="description" data-test="about-description">
                A chat assistant for Discord, Slack and Teams. It reads your repositories and cloud,
                reports problems as issues and reviews pull requests. It never writes code.
              </span>
            </div>
            <dl class="facts">
              <dt>Version</dt>
              <dd>
                <code data-test="about-version">{{ version() ?? 'unknown' }}</code>
              </dd>
              <dt>Licence</dt>
              <dd data-test="about-licence-text">MIT, open source</dd>
              <dt>Built with</dt>
              <dd data-test="about-built-with">Angular, Electron and .NET</dd>
            </dl>
            <div class="about-line">
              <span class="about-label">Project</span>
              <button
                type="button"
                class="link"
                data-test="about-repository"
                (click)="openAbout(repositoryUrl)"
              >
                <sc-icon name="external" [size]="14" />
                Repository
              </button>
              <button
                type="button"
                class="link"
                data-test="about-changelog"
                (click)="openAbout(changelogUrl)"
              >
                <sc-icon name="external" [size]="14" />
                Changelog
              </button>
              <button
                type="button"
                class="link"
                data-test="about-licence"
                (click)="openAbout(licenceUrl)"
              >
                <sc-icon name="external" [size]="14" />
                MIT licence
              </button>
            </div>
            <div class="about-line">
              <span class="about-label">Security</span>
              <button
                type="button"
                class="link"
                data-test="about-security"
                (click)="openAbout(securityUrl)"
              >
                <sc-icon name="external" [size]="14" />
                Report a security problem privately
              </button>
              <span class="about-note">
                A private report to the maintainers on GitHub, never a public issue.
              </span>
            </div>
            @if (aboutError(); as error) {
              <p class="error" role="alert" data-test="about-error">{{ error }}</p>
            }
            <p class="hint">Links open in your browser.</p>
            <div class="about-notices">
              <span class="notices-label">Trademark notices</span>
              <button
                type="button"
                class="icon-btn"
                data-test="about-toggle"
                [attr.aria-expanded]="aboutOpen()"
                [attr.aria-controls]="aboutOpen() ? aboutRegion : null"
                [attr.aria-label]="
                  aboutOpen() ? 'Hide the trademark notices' : 'Show the trademark notices'
                "
                [title]="aboutOpen() ? 'Hide the trademark notices' : 'Show the trademark notices'"
                (click)="aboutOpen.set(!aboutOpen())"
              >
                <sc-icon name="chevron-down" [size]="18" />
              </button>
            </div>
            @if (aboutOpen()) {
              <ul class="notices" [id]="aboutRegion" data-test="trademark-notices">
                <li>OpenAI and ChatGPT are trademarks of OpenAI.</li>
                <li>Google and Gemini are trademarks of Google.</li>
                <li>Anthropic and Claude are trademarks of Anthropic.</li>
                <li>OpenRouter is a trademark of OpenRouter, Inc.</li>
                <li>Ollama is a trademark of Ollama, Inc.</li>
                <li>LM Studio is a trademark of Element Labs, Inc.</li>
                <li>
                  Third-party logos are not covered by Scout's MIT licence and remain their owners'
                  trademarks.
                </li>
                <li>
                  All other product names and logos are trademarks of their respective owners. Their
                  use identifies the service you connect and does not imply endorsement.
                </li>
              </ul>
            }
          </section>
        </div>

        <div class="column">
          <section class="card" data-test="access-section" aria-labelledby="s-access">
            <h2 id="s-access">Who can use Scout</h2>
            <p class="lede" data-test="access-lede">
              People listed here can ask Scout from Discord or Slack. Chat on this computer never
              needs the list.
            </p>
            <ul class="steps" data-test="access-help">
              <li>Discord: turn on Developer Mode, right-click a person, Copy User ID.</li>
              <li>Slack: open their profile, More, Copy member ID.</li>
              <li>Teams: the user's Bot Framework user ID, which starts with 29:.</li>
            </ul>
            <form class="access-add" (submit)="$event.preventDefault(); addUser()">
              <div class="field">
                <label for="access-user-id" class="field-label">User ID</label>
                <input
                  id="access-user-id"
                  type="text"
                  placeholder="412398765432109876"
                  aria-describedby="access-user-id-help"
                  data-test="access-input"
                  [value]="newUserId()"
                  (input)="newUserId.set($any($event.target).value)"
                />
                <span id="access-user-id-help" class="hint">
                  A Discord user ID or a Slack member ID.
                </span>
              </div>
              <button
                type="submit"
                class="btn btn--primary add"
                data-test="access-add"
                [disabled]="accessBusy() || newUserId().trim() === ''"
              >
                <sc-icon name="user-plus" [size]="17" />
                Add
              </button>
            </form>
            <div class="pair-row">
              <button
                type="button"
                class="btn"
                data-test="access-pair"
                [disabled]="accessBusy()"
                (click)="startPairing()"
              >
                <sc-icon name="link" [size]="17" />
                Pair from chat
              </button>
              @if (pairingCode(); as code) {
                <span class="pair-text" data-test="access-pair-instructions">
                  Send <code>pair {{ code }}</code> to Scout in a direct message on Discord.
                </span>
              }
            </div>
            @if (pairingExpired()) {
              <p class="error" role="alert" data-test="access-pair-expired">
                Pairing expired. Start again.
              </p>
            }
            @if (accessError(); as error) {
              <p class="error" role="alert" data-test="access-error">{{ error }}</p>
            }
            <ul class="access-list" aria-label="Allowed people">
              @if (allowedUserIds().length === 0) {
                <li class="muted" data-test="access-empty">
                  No one is listed yet. The list only matters once Discord or Slack is connected;
                  chat on this computer never needs it.
                </li>
              }
              @for (id of allowedUserIds(); track id) {
                <li>
                  <code [attr.data-test]="'access-user-' + id">{{ id }}</code>
                  <button
                    type="button"
                    class="btn btn--danger"
                    [attr.data-test]="'access-remove-' + id"
                    [attr.aria-label]="'Remove ' + id"
                    [disabled]="accessBusy()"
                    (click)="removeUser(id)"
                  >
                    <sc-icon name="trash" [size]="15" />
                    Remove
                  </button>
                </li>
              }
            </ul>
          </section>

          <section class="card" data-test="shortcut-section" aria-labelledby="s-bubble">
            <h2 id="s-bubble">Floating button and quick ask</h2>
            <p class="lede">
              A small round Scout button stays above your windows. Click it to open the quick-ask
              box in the same corner.
            </p>
            @if (bubble(); as settings) {
              <div class="switch-row">
                <span id="lbl-bubble" class="switch-label">Show the floating button</span>
                <mat-slide-toggle
                  data-test="bubble-toggle"
                  aria-labelledby="lbl-bubble"
                  hideIcon
                  [checked]="settings.enabled"
                  [disabled]="bubbleBusy()"
                  (change)="toggleBubble($event)"
                />
              </div>
              <span id="lbl-corner" class="field-label">Corner</span>
              <div class="segmented" role="group" aria-labelledby="lbl-corner">
                @for (corner of corners; track corner.value) {
                  <button
                    type="button"
                    [attr.data-test]="'bubble-corner-' + corner.value"
                    [attr.aria-pressed]="settings.corner === corner.value"
                    [disabled]="bubbleBusy()"
                    (click)="saveBubble({ corner: corner.value })"
                  >
                    {{ corner.label }}
                  </button>
                }
              </div>
              @if (settings.monitors.length > 1) {
                <span id="lbl-display" class="field-label">Display</span>
                <div
                  class="segmented"
                  role="group"
                  aria-labelledby="lbl-display"
                  data-test="bubble-display"
                >
                  <button
                    type="button"
                    data-test="bubble-display-cursor"
                    [attr.aria-pressed]="settings.display === 'cursor'"
                    [disabled]="bubbleBusy()"
                    (click)="chooseDisplay(settings, 'cursor')"
                  >
                    Follow the mouse cursor
                  </button>
                  @for (monitor of settings.monitors; track monitor.id) {
                    <button
                      type="button"
                      [attr.data-test]="'bubble-monitor-' + monitor.id"
                      [attr.aria-pressed]="onMonitor(monitor, settings.display)"
                      [disabled]="bubbleBusy()"
                      (click)="
                        chooseDisplay(settings, monitor.primary ? 'primary' : monitor.id, monitor)
                      "
                    >
                      {{ monitor.primary ? monitor.name + ' (primary)' : monitor.name }}
                    </button>
                  }
                </div>
              }
              @if (settings.hint; as hint) {
                <p class="hint" data-test="bubble-hint">{{ hint }}</p>
              }
            }
            @if (bubbleError(); as error) {
              <p class="error" role="alert" data-test="bubble-error">{{ error }}</p>
            }
            <div class="shortcut">
              <span id="lbl-shortcut" class="field-label">Quick-ask shortcut</span>
              <div class="shortcut-row">
                <div
                  class="keys"
                  role="group"
                  aria-labelledby="lbl-shortcut"
                  aria-describedby="shortcut-help"
                  data-test="shortcut-input"
                  [class.keys--recording]="recording()"
                >
                  @if (recording()) {
                    <span class="keys-waiting">Press the new shortcut</span>
                  } @else {
                    @for (key of keys(); track $index) {
                      <kbd>{{ key }}</kbd>
                    }
                  }
                </div>
                <button
                  type="button"
                  class="btn"
                  data-test="shortcut-record"
                  [attr.aria-pressed]="recording()"
                  [disabled]="shortcutBusy()"
                  (click)="toggleRecording($any($event.currentTarget))"
                  (keydown)="captureKey($event)"
                  (keyup)="releaseKey($event)"
                  (blur)="endRecording()"
                >
                  <sc-icon name="keyboard" [size]="17" />
                  Record shortcut
                </button>
                <button
                  type="button"
                  class="btn btn--primary"
                  data-test="shortcut-save"
                  [disabled]="shortcutBusy() || recording() || pending() === ''"
                  (click)="saveShortcut()"
                >
                  <sc-icon name="save" [size]="17" />
                  Save
                </button>
              </div>
              <span id="shortcut-help" class="hint">
                Click Record shortcut, then press the keys. Press it anywhere to ask Scout, typed or
                spoken.
              </span>
              <p class="visually-hidden" role="status" data-test="shortcut-status">
                {{ shortcutStatus() }}
              </p>
              @if (shortcutRefused(); as refusal) {
                <p class="error" role="alert" data-test="shortcut-refused">{{ refusal }}</p>
              }
              @if (shortcutHint(); as hint) {
                <p class="hint" data-test="shortcut-hint">{{ hint }}</p>
              }
              @if (shortcutError(); as error) {
                <p class="error" role="alert" data-test="shortcut-error">{{ error }}</p>
              }
            </div>
          </section>

          <sc-audio-settings />

          <section class="card" data-test="desktop-section" aria-labelledby="s-desktop">
            <h2 id="s-desktop">Desktop</h2>
            @if (desktop(); as settings) {
              <div class="switch-row">
                <span id="lbl-keep" class="switch-label">
                  Keep Scout running when the window is closed
                </span>
                <mat-slide-toggle
                  data-test="desktop-keep-running"
                  aria-labelledby="lbl-keep"
                  hideIcon
                  [checked]="settings.keepRunning"
                  [disabled]="desktopBusy() || !settings.trayAvailable"
                  (change)="setKeepRunning($event)"
                />
              </div>
              @if (!settings.trayAvailable) {
                <p class="hint" data-test="desktop-no-tray">
                  This desktop has no system tray, so Scout quits when its window is closed.
                </p>
              }
              <div class="switch-row">
                <span id="lbl-login" class="switch-label">Start Scout when I log in</span>
                <mat-slide-toggle
                  data-test="desktop-start-at-login"
                  aria-labelledby="lbl-login"
                  hideIcon
                  [checked]="settings.startAtLogin"
                  [disabled]="desktopBusy()"
                  (change)="setStartAtLogin($event)"
                />
              </div>
            }
            @if (desktopError(); as error) {
              <p class="error" role="alert" data-test="desktop-error">{{ error }}</p>
            }
          </section>
          <section class="card" data-test="updates-section" aria-labelledby="s-updates">
            <h2 id="s-updates">Updates</h2>
            <p class="lede">Nothing downloads or installs without a click.</p>
            <div class="version-row">
              <span class="muted">Current version</span>
              @if (onPrerelease()) {
                <span class="chip-pre" data-test="update-current-prerelease">Pre-release</span>
              }
              <code data-test="update-current-version">{{ version() ?? 'unknown' }}</code>
            </div>
            @if (update(); as state) {
              @if (state.packaged) {
                <div class="switch-row">
                  <span id="lbl-updates" class="switch-label">Check for new versions</span>
                  <mat-slide-toggle
                    data-test="update-check-enabled"
                    aria-labelledby="lbl-updates"
                    hideIcon
                    [checked]="state.checkEnabled"
                    [disabled]="updateBusy()"
                    (change)="setCheckEnabled($event)"
                  />
                </div>
                <p class="hint" data-test="update-schedule">
                  Scout checks GitHub Releases 10 seconds after it starts, then once a day.
                </p>
                <div class="channel-row">
                  <span id="lbl-channel" class="switch-label">Channel</span>
                  <div class="segmented" role="group" aria-labelledby="lbl-channel">
                    @for (option of channelOptions; track option.value) {
                      <button
                        type="button"
                        class="theme-choice"
                        [attr.data-test]="'update-channel-' + option.value"
                        [attr.aria-pressed]="state.channel === option.value"
                        [disabled]="updateBusy()"
                        (click)="setChannel(option.value)"
                      >
                        {{ option.label }}
                      </button>
                    }
                  </div>
                </div>
                <p class="hint" data-test="update-channel-hint">
                  {{ channelHints[state.channel] }}
                </p>
                @if (state.channel === 'stable' && onPrerelease()) {
                  <p class="back-to-stable" role="status" data-test="update-back-to-stable">
                    You are on the pre-release {{ version() }}. Stable never installs an older
                    version: Scout offers the next stable version newer than it.
                  </p>
                }
                <div class="update-row">
                  <span class="muted last-check" data-test="update-last-check">
                    Last check:
                    {{ state.lastCheckAt === null ? 'never' : formatDate(state.lastCheckAt) }}
                    @if (state.lastResult !== null) {
                      · {{ lastResultText(state.lastResult) }}
                    }
                  </span>
                  <button
                    type="button"
                    class="btn"
                    data-test="update-check-now"
                    [disabled]="updateBusy()"
                    (click)="checkNow()"
                  >
                    <sc-icon name="refresh" [size]="17" />
                    Check now
                  </button>
                </div>
                @if (state.notice; as n) {
                  <div class="update-box" role="status" data-test="settings-update-available">
                    <div class="update-headline">
                      <h3 data-test="settings-update-headline">
                        Scout {{ n.latestVersion }} is available. You have {{ n.currentVersion }}.
                      </h3>
                      @if (n.latestVersion.includes('-')) {
                        <span class="chip-pre" data-test="settings-update-prerelease"
                          >Pre-release</span
                        >
                      }
                    </div>
                    @if (!n.canInstall) {
                      <p data-test="settings-update-cannot">
                        This package cannot update itself. Download the new version and replace this
                        copy; your settings stay on this computer.
                      </p>
                    }
                    @if (updateProgress(); as p) {
                      @switch (p.phase) {
                        @case ('downloading') {
                          <div class="update-progress" data-test="settings-update-progress">
                            <span
                              >Downloading{{
                                p.percent === null ? '' : ' ' + p.percent + '%'
                              }}</span
                            >
                            @if (p.percent !== null) {
                              <progress max="100" [value]="p.percent"></progress>
                            }
                          </div>
                        }
                        @case ('failed') {
                          <p class="error" role="alert" data-test="settings-update-failed">
                            {{ p.message }}
                          </p>
                        }
                        @case ('checking') {
                          <p data-test="settings-update-progress">Checking the release</p>
                        }
                        @default {
                          <p data-test="settings-update-progress">Restarting to install</p>
                        }
                      }
                    }
                    <div class="update-actions">
                      @if (n.canInstall && !installing()) {
                        <button
                          type="button"
                          class="btn btn--primary"
                          data-test="settings-update-install"
                          [disabled]="updateBusy()"
                          (click)="installUpdate()"
                        >
                          Update and restart
                        </button>
                      }
                      @if (!n.canInstall || updateProgress()?.phase === 'failed') {
                        <button
                          type="button"
                          class="btn"
                          [class.btn--primary]="!n.canInstall"
                          data-test="settings-update-download"
                          (click)="openUpdateLink(n.downloadUrl)"
                        >
                          Download
                        </button>
                      }
                      <button
                        type="button"
                        class="link"
                        data-test="settings-update-release-notes"
                        (click)="openUpdateLink(n.releaseUrl)"
                      >
                        <sc-icon name="external" [size]="14" />
                        Release notes
                      </button>
                    </div>
                    <p class="hint">
                      Later and Skip this version are on the bar at the top of the window.
                    </p>
                  </div>
                } @else if (
                  state.lastResult === 'up-to-date' &&
                  !(state.channel === 'stable' && onPrerelease())
                ) {
                  <div class="update-ok" role="status" data-test="settings-update-up-to-date">
                    <span class="ok-title">You are up to date</span>
                    <span
                      >Scout {{ version() ?? 'unknown' }} is the newest version on the
                      {{ state.channel === 'beta' ? 'Beta' : 'Stable' }} channel.</span
                    >
                  </div>
                }
              } @else {
                <p class="hint" data-test="update-dev-build">
                  Update checks run only in the installed app.
                </p>
              }
            }
            @if (updateError(); as error) {
              <p class="error" role="alert" data-test="update-error">{{ error }}</p>
            }
          </section>
        </div>
      </div>
    </main>
  `,
  styles: [
    SETTINGS_SWITCH_STYLES,
    `
      :host {
        display: block;
        color: var(--sc-fg);
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .settings {
        padding: 24px 32px 40px;
        max-width: 1200px;
        font-size: 14px;
        display: flex;
        flex-direction: column;
        gap: 16px;
        box-sizing: border-box;
      }
      .page-head {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      h1 {
        font-size: 20px;
        font-weight: 600;
        margin: 0;
      }
      .page-head .description {
        font-size: 13px;
        color: var(--sc-muted);
        margin: 0;
      }
      .columns {
        display: flex;
        flex-wrap: wrap;
        gap: 16px;
        align-items: flex-start;
      }
      .column {
        flex: 1 1 440px;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .card {
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      h2 {
        margin: 0;
        font-size: 15px;
        font-weight: 600;
      }
      .lede {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg-2);
        line-height: 1.5;
      }
      .hint {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
        line-height: 1.5;
      }
      .field-label {
        font-size: 13px;
        font-weight: 500;
      }
      .muted {
        color: var(--sc-muted);
        font-size: 13px;
        margin: 0;
      }
      code {
        font-family: var(--sc-mono);
        background: var(--sc-surface-2);
        color: var(--sc-fg);
        padding: 2px 6px;
        border-radius: 6px;
      }
      .about-name {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .about-name .name {
        font-weight: 600;
      }
      .about-name .description {
        font-size: 13px;
        color: var(--sc-fg-2);
        line-height: 1.5;
      }
      .facts {
        margin: 0;
        display: grid;
        grid-template-columns: 88px 1fr;
        gap: 6px 0;
        font-size: 13px;
      }
      .facts dt {
        color: var(--sc-muted);
      }
      .facts dd {
        margin: 0;
      }
      .facts code,
      .version-row code {
        font-size: 13px;
        padding: 2px 8px;
      }
      .about-line {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px 12px;
      }
      .about-label {
        flex: 0 0 88px;
        font-size: 13px;
        color: var(--sc-muted);
      }
      .about-note {
        flex-basis: 100%;
        padding-left: 100px;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .about-notices {
        display: flex;
        align-items: center;
        gap: 8px;
        padding-top: 8px;
        border-top: 1px solid var(--sc-line);
      }
      .notices-label {
        margin-right: auto;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .version-row {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 13px;
      }
      .version-row .muted {
        margin-right: auto;
      }
      .update-ok {
        display: flex;
        flex-direction: column;
        gap: 2px;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .update-ok .ok-title {
        font-weight: 600;
        color: var(--sc-ok);
      }
      .update-box {
        display: flex;
        flex-direction: column;
        gap: 10px;
        padding: 12px;
        border-radius: 10px;
        background: var(--sc-info-container);
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .update-box h3 {
        margin: 0;
        font-size: 14px;
        font-weight: 600;
        color: var(--sc-fg);
      }
      .update-box p {
        margin: 0;
        line-height: 1.5;
      }
      .update-progress {
        display: flex;
        align-items: center;
        gap: 10px;
      }
      .update-progress progress {
        flex: 1;
      }
      .channel-row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px 12px;
      }
      .channel-row .switch-label {
        margin-right: auto;
      }
      .back-to-stable {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg-2);
        line-height: 1.5;
      }
      .chip-pre {
        display: inline-flex;
        align-items: center;
        height: 22px;
        padding: 0 8px;
        border-radius: 11px;
        font-size: 12px;
        font-weight: 500;
        background: var(--sc-warn-container);
        color: var(--sc-warn);
      }
      .update-headline {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .update-actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
      }
      .icon-btn {
        width: 32px;
        height: 32px;
        border-radius: 16px;
        border: 0;
        background: transparent;
        color: var(--sc-fg-2);
        display: inline-flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
      }
      .icon-btn[aria-expanded='true'] sc-icon {
        transform: rotate(180deg);
      }
      .link {
        display: inline-flex;
        align-items: center;
        gap: 4px;
        padding: 0;
        border: none;
        background: none;
        font: inherit;
        font-size: 13px;
        color: var(--sc-accent);
        cursor: pointer;
      }
      .notices,
      .steps {
        margin: 0;
        padding-left: 18px;
        font-size: 12px;
        color: var(--sc-muted);
        line-height: 1.6;
      }
      .access-add {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        align-items: flex-start;
        margin: 0;
      }
      .field {
        flex: 1 1 220px;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .access-add input {
        height: 40px;
        padding: 0 12px;
        box-sizing: border-box;
        background: var(--sc-bg);
        border: 1px solid var(--sc-line);
        border-radius: 10px;
        color: var(--sc-fg);
        font: inherit;
      }
      .add {
        margin-top: 25px;
      }
      .pair-row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
      }
      .pair-text {
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .access-list {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
      }
      .access-list li {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 8px 0;
        border-top: 1px solid var(--sc-line);
        font-size: 13px;
      }
      .access-list li code {
        margin-right: auto;
      }
      .btn {
        height: 40px;
        padding: 0 16px;
        border-radius: 20px;
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg);
        display: inline-flex;
        align-items: center;
        gap: 8px;
        font: inherit;
        font-weight: 500;
        cursor: pointer;
      }
      .btn--primary {
        border-color: transparent;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
      }
      .btn--danger {
        height: 32px;
        padding: 0 10px;
        border: 0;
        border-radius: 16px;
        gap: 6px;
        color: var(--sc-error);
      }
      .btn[aria-pressed='true'] {
        border-color: var(--sc-accent-strong);
        background: var(--sc-surface-2);
      }
      .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
      .segmented {
        display: flex;
        flex-wrap: wrap;
        gap: 2px;
        align-self: flex-start;
        padding: 3px;
        border: 1px solid var(--sc-line);
        border-radius: 20px;
        background: var(--sc-bg);
      }
      .segmented button {
        height: 30px;
        padding: 0 12px;
        border: 0;
        border-radius: 16px;
        background: transparent;
        color: var(--sc-muted);
        font: inherit;
        font-size: 13px;
        font-weight: 500;
        cursor: pointer;
      }
      .segmented button[aria-pressed='true'] {
        background: var(--sc-pressed);
        color: var(--sc-fg);
        font-weight: 600;
      }
      .segmented--theme {
        border-radius: 22px;
      }
      .segmented--theme .theme-choice {
        height: 34px;
        padding: 0 14px;
        border-radius: 18px;
        display: inline-flex;
        align-items: center;
        gap: 8px;
        font-size: 14px;
      }
      .shortcut {
        display: flex;
        flex-direction: column;
        gap: 6px;
        padding-top: 12px;
        border-top: 1px solid var(--sc-line);
      }
      .shortcut-row {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        align-items: center;
      }
      .keys {
        flex: 1 1 200px;
        height: 40px;
        padding: 0 8px;
        box-sizing: border-box;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        display: flex;
        align-items: center;
        gap: 6px;
      }
      .keys--recording {
        border-color: var(--sc-accent-strong);
      }
      .keys-waiting {
        font-size: 13px;
        color: var(--sc-muted);
      }
      kbd {
        font-family: inherit;
        font-size: 12px;
        padding: 3px 8px;
        border-radius: 6px;
        border: 1px solid var(--sc-line);
        background: var(--sc-surface-2);
        color: var(--sc-fg);
      }
      .visually-hidden {
        position: absolute;
        width: 1px;
        height: 1px;
        margin: 0;
        overflow: hidden;
        clip-path: inset(50%);
        white-space: nowrap;
      }
      .update-row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        padding-top: 8px;
        border-top: 1px solid var(--sc-line);
      }
      .last-check {
        margin-right: auto;
      }
      .error {
        color: var(--sc-error);
        font-size: 13px;
        margin: 0;
      }
    `,
  ],
})
export class SettingsComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly dialog = inject(MatDialog);
  protected readonly theme = inject(ThemeStore);
  protected readonly themeOptions = THEME_OPTIONS;

  readonly version = signal<string | null>(null);
  readonly allowedUserIds = signal<readonly string[]>([]);
  readonly newUserId = signal('');
  readonly accessBusy = signal(false);
  readonly accessError = signal<string | null>(null);
  readonly pairingCode = signal<string | null>(null);
  readonly pairingExpired = signal(false);
  readonly desktop = signal<DesktopSettings | null>(null);
  readonly desktopBusy = signal(false);
  readonly desktopError = signal<string | null>(null);
  readonly update = signal<UpdateState | null>(null);
  readonly updateBusy = signal(false);
  readonly updateError = signal<string | null>(null);
  readonly aboutError = signal<string | null>(null);
  readonly aboutOpen = signal(false);
  readonly aboutRegion = ABOUT_REGION;
  readonly repositoryUrl = REPOSITORY_URL;
  readonly licenceUrl = LICENCE_URL;
  readonly changelogUrl = CHANGELOG_URL;
  readonly securityUrl = SECURITY_URL;
  readonly updateProgress = signal<UpdateProgress | null>(null);
  readonly channelOptions = CHANNEL_OPTIONS;
  readonly channelHints = CHANNEL_HINTS;
  readonly onPrerelease = computed(() => (this.version() ?? '').includes('-'));
  readonly installing = computed(() => {
    const phase = this.updateProgress()?.phase;
    return (
      phase === 'checking' || phase === 'downloading' || phase === 'ready' || phase === 'installing'
    );
  });
  private pairingTimer: ReturnType<typeof setTimeout> | null = null;
  /** The shortcut main registered and saved. */
  readonly savedShortcut = signal('');
  /** The shortcut the keycaps show and Save sends: the saved one, or the one just recorded. */
  readonly pending = signal('');
  readonly recording = signal(false);
  private readonly shortcutRegistered = signal(false);
  readonly shortcutRefused = signal<string | null>(null);
  readonly shortcutStatus = signal('');
  private readonly mac = /^Mac/.test(navigator.platform);
  private heldKey: string | null = null;
  readonly keys = computed(() => keycapsOf(this.pending(), this.mac));
  readonly shortcutBusy = signal(false);
  readonly shortcutError = signal<string | null>(null);
  readonly shortcutHint = signal<string | null>(null);
  readonly bubble = signal<LocalBubbleStatus | null>(null);
  readonly bubbleBusy = signal(false);
  readonly bubbleError = signal<string | null>(null);
  readonly corners = CORNERS;

  constructor() {
    const unsubscribe = this.bridge.onWorkerEvent((event) => {
      if (event.kind !== 'stdout') return;
      const line = event.line.trim();
      if (line === 'SCOUT_PAIRING_BURNED') this.endPairing(true);
      else if (line.startsWith('SCOUT_PAIRED=')) {
        this.endPairing(false);
        void this.readAllowedUsers();
      }
    });
    const unsubscribeBubble = this.bridge.onLocalBubble((status) => this.bubble.set(status));
    const unsubscribeNotice = this.bridge.onUpdateNotice((notice) => this.setNotice(notice));
    const unsubscribeProgress = this.bridge.onUpdateProgress((next) =>
      this.updateProgress.set(next),
    );
    inject(DestroyRef).onDestroy(() => {
      unsubscribe();
      unsubscribeBubble();
      unsubscribeNotice();
      unsubscribeProgress();
      this.clearPairingTimer();
      this.endRecording();
    });
  }

  ngOnInit(): void {
    void this.bridge
      .appGetVersion()
      .then((res) => this.version.set(res.version))
      .catch(() => this.version.set(null));
    void this.readAllowedUsers();
    void this.bridge
      .desktopGetSettings()
      .then((settings) => this.desktop.set(settings))
      .catch((err: unknown) => this.desktopError.set(readIpcError(err).message));
    void this.bridge
      .updateGetState()
      .then((state) => this.update.set(state))
      .catch((err: unknown) => this.updateError.set(readIpcError(err).message));
    void this.bridge
      .localGetShortcut()
      .then((res) => {
        this.savedShortcut.set(res.accelerator);
        this.shortcutRegistered.set(res.registered);
        this.pending.set(res.accelerator);
        this.shortcutHint.set(res.hint);
      })
      .catch((err: unknown) => this.shortcutError.set(readIpcError(err).message));
    void this.bridge
      .localGetBubble()
      .then((settings) => this.bubble.set(settings))
      .catch((err: unknown) => this.bubbleError.set(readIpcError(err).message));
  }

  async saveBubble(change: Partial<LocalBubbleSettings>): Promise<void> {
    const current = this.bubble();
    if (current === null) return;
    this.bubbleBusy.set(true);
    this.bubbleError.set(null);
    try {
      const { enabled, corner, display } = { ...current, ...change };
      this.bubble.set(await this.bridge.localSetBubble({ enabled, corner, display }));
    } catch (err: unknown) {
      this.bubbleError.set(readIpcError(err).message);
    } finally {
      this.bubbleBusy.set(false);
    }
  }

  /** True when the floating button sits on `monitor` by this choice: its id, or `primary` for the primary monitor. */
  onMonitor(monitor: LocalMonitor, display: LocalBubbleSettings['display']): boolean {
    return display === monitor.id || (display === 'primary' && monitor.primary);
  }

  /** A display button: stores its choice, unless it is pressed already, so a stored id is never rewritten. */
  async chooseDisplay(
    settings: LocalBubbleStatus,
    display: LocalBubbleSettings['display'],
    monitor?: LocalMonitor,
  ): Promise<void> {
    const pressed =
      monitor === undefined
        ? settings.display === 'cursor'
        : this.onMonitor(monitor, settings.display);
    if (!pressed) await this.saveBubble({ display });
  }

  async toggleBubble(change: MatSlideToggleChange): Promise<void> {
    await this.saveBubble({ enabled: change.checked });
    change.source.checked = this.bubble()?.enabled ?? !change.checked;
  }

  /** Record shortcut: starts listening for the next key combination, or stops with the shortcut unchanged. */
  toggleRecording(button?: HTMLElement): void {
    if (this.recording()) {
      this.endRecording();
      return;
    }
    button?.focus();
    this.recording.set(true);
    this.shortcutRefused.set(null);
    this.shortcutError.set(null);
    this.shortcutStatus.set(RECORDING_LINE);
    void this.bridge.localSuspendShortcut({ suspended: true }).catch(() => undefined);
  }

  /**
   * One key press while recording: Tab leaves the button as usual and ends the recording unchanged, Esc cancels it
   * back to the saved shortcut, a modifier alone keeps waiting, a refused key says why, and a whole combination
   * becomes the shortcut Save sends. No key reaches the page while recording, and none is touched otherwise.
   */
  captureKey(event: KeyboardEvent): void {
    if (!this.recording()) return;
    if (event.key === 'Tab') {
      this.endRecording();
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    this.heldKey = event.code;
    if (event.key === 'Escape') {
      this.pending.set(this.savedShortcut());
      this.endRecording();
      this.shortcutStatus.set('Recording cancelled. The shortcut is unchanged.');
      return;
    }
    const captured = captureShortcut(event, this.mac);
    if (captured === null) return;
    if ('refusal' in captured) {
      this.shortcutRefused.set(captured.refusal);
      return;
    }
    this.pending.set(captured.accelerator);
    this.endRecording();
    this.shortcutStatus.set(`New shortcut: ${this.keys().join(' ')}. Press Save to keep it.`);
  }

  /** The release of a key the recording took never reaches the button, so Space does not press Record shortcut again. */
  releaseKey(event: KeyboardEvent): void {
    if (event.code !== this.heldKey) return;
    this.heldKey = null;
    event.preventDefault();
    event.stopPropagation();
  }

  /** Ends a recording, leaving the shortcut as it was before it, and gives main its shortcut back. */
  endRecording(): void {
    if (!this.recording()) return;
    this.recording.set(false);
    this.shortcutRefused.set(null);
    this.shortcutStatus.set('Recording ended. The shortcut is unchanged.');
    void this.bridge
      .localSuspendShortcut({ suspended: false })
      .then((status) => this.shortcutTakenBack(status.registered))
      .catch(() => undefined);
  }

  private shortcutTakenBack(registered: boolean): void {
    const lost = this.shortcutRegistered() && !registered;
    this.shortcutRegistered.set(registered);
    if (!lost) return;
    this.pending.set(this.savedShortcut());
    this.shortcutError.set(
      `Scout could not register ${keycapsOf(this.savedShortcut(), this.mac).join('+')} again: another application may hold it now. Choose another shortcut.`,
    );
  }

  async saveShortcut(): Promise<void> {
    this.shortcutBusy.set(true);
    this.shortcutError.set(null);
    try {
      const status = await this.bridge.localSetShortcut({ accelerator: this.pending() });
      this.savedShortcut.set(status.accelerator);
      this.shortcutRegistered.set(status.registered);
      this.pending.set(status.accelerator);
      this.shortcutHint.set(status.hint);
    } catch (err: unknown) {
      this.pending.set(this.savedShortcut());
      this.shortcutError.set(readIpcError(err).message);
    } finally {
      this.shortcutBusy.set(false);
    }
  }

  async setKeepRunning(change: MatSlideToggleChange): Promise<void> {
    await this.saveDesktop(change, (enabled) => this.bridge.desktopSetKeepRunning({ enabled }));
  }

  async setStartAtLogin(change: MatSlideToggleChange): Promise<void> {
    await this.saveDesktop(change, (enabled) => this.bridge.desktopSetStartAtLogin({ enabled }));
  }

  /** A check's time as the artboard writes it, in local time: `7 Nov 2026, 14:02`. */
  formatDate(iso: string): string {
    return LAST_CHECK_DATE.format(new Date(iso));
  }

  async setCheckEnabled(change: MatSlideToggleChange): Promise<void> {
    const enabled = change.checked;
    const saved = await this.saveUpdate(() => this.bridge.updateSetCheckEnabled({ enabled }));
    if (!saved) change.source.checked = !enabled;
  }

  async checkNow(): Promise<void> {
    await this.saveUpdate(() => this.bridge.updateCheckNow());
  }

  /** Moves the update check to Stable or Beta; main stores it and checks again on that channel. */
  async setChannel(channel: UpdateChannel): Promise<void> {
    await this.saveUpdate(() => this.bridge.updateSetChannel({ channel }));
  }

  /** Starts the same install as the bar's Update and restart; the progress arrives on `scout:update.progress`. */
  async installUpdate(): Promise<void> {
    this.updateProgress.set(null);
    await this.saveUpdate(() => this.bridge.updateInstall());
  }

  private setNotice(notice: UpdateNotice | null): void {
    const state = this.update();
    if (state !== null) this.update.set({ ...state, notice });
  }

  private async saveUpdate(call: () => Promise<UpdateState>): Promise<boolean> {
    this.updateBusy.set(true);
    this.updateError.set(null);
    try {
      this.update.set(await call());
      return true;
    } catch (err: unknown) {
      this.updateError.set(readIpcError(err).message);
      return false;
    } finally {
      this.updateBusy.set(false);
    }
  }

  private async saveDesktop(
    change: MatSlideToggleChange,
    save: (enabled: boolean) => Promise<DesktopSettings>,
  ): Promise<void> {
    const enabled = change.checked;
    this.desktopBusy.set(true);
    this.desktopError.set(null);
    try {
      this.desktop.set(await save(enabled));
    } catch (err: unknown) {
      change.source.checked = !enabled;
      this.desktopError.set(readIpcError(err).message);
    } finally {
      this.desktopBusy.set(false);
    }
  }

  /** The last update check's result in words: up to date, a version available, or why the check failed. */
  lastResultText(result: string): string {
    if (result === 'up-to-date') return 'You have the latest version';
    const [kind, ...rest] = result.split(' ');
    const detail = rest.join(' ');
    if (kind === 'available') return `Version ${detail} is available`;
    if (kind === 'skipped') return `Version ${detail} is available (skipped)`;
    if (kind === 'error') {
      const reason = detail.startsWith('http ')
        ? `GitHub answered HTTP ${detail.slice(5)}`
        : (CHECK_FAILURES[detail] ?? detail);
      return `Could not check for updates (${reason})`;
    }
    return result;
  }

  /** Opens a link of the Updates card; a link main refuses says why in that card. */
  async openUpdateLink(url: string): Promise<void> {
    this.updateError.set(null);
    try {
      await this.bridge.appOpenExternal({ url });
    } catch (err: unknown) {
      this.updateError.set(readIpcError(err).message);
    }
  }

  /** Opens a link of the About card; a link main refuses says why under the links. */
  async openAbout(url: string): Promise<void> {
    this.aboutError.set(null);
    try {
      await this.bridge.appOpenExternal({ url });
    } catch (err: unknown) {
      this.aboutError.set(readIpcError(err).message);
    }
  }

  async addUser(): Promise<void> {
    const id = this.newUserId();
    if (await this.saveAllowedUsers((current) => [...current, id])) {
      this.newUserId.set('');
    }
  }

  /** Asks first, with Cancel focused; only the owner's Remove takes the id off the allowed list. */
  async removeUser(id: string): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: `Remove ${id}?`,
      body: 'This user can no longer ask Scout anything. You can add them again later.',
      confirmLabel: 'Remove',
    });
    if (!confirmed) return;
    await this.saveAllowedUsers((current) => current.filter((existing) => existing !== id));
  }

  async startPairing(): Promise<void> {
    this.accessError.set(null);
    this.pairingExpired.set(false);
    this.clearPairingTimer();
    try {
      const pairing = await this.bridge.accessStartPairing();
      this.pairingCode.set(pairing.code);
      this.pairingTimer = setTimeout(
        () => this.endPairing(true),
        Math.max(0, Date.parse(pairing.expiresAt) - Date.now()),
      );
    } catch (err: unknown) {
      this.accessError.set(readIpcError(err).message);
    }
  }

  private endPairing(expired: boolean): void {
    this.clearPairingTimer();
    if (this.pairingCode() === null && !expired) return;
    this.pairingCode.set(null);
    this.pairingExpired.set(expired);
  }

  private clearPairingTimer(): void {
    if (this.pairingTimer !== null) clearTimeout(this.pairingTimer);
    this.pairingTimer = null;
  }

  private async readAllowedUsers(): Promise<void> {
    try {
      this.allowedUserIds.set((await this.bridge.accessGet()).userIds);
    } catch (err: unknown) {
      this.accessError.set(readIpcError(err).message);
    }
  }

  /** Re-reads the stored list first, so an id paired from chat or added elsewhere is never dropped. */
  private async saveAllowedUsers(
    change: (current: readonly string[]) => readonly string[],
  ): Promise<boolean> {
    this.accessBusy.set(true);
    this.accessError.set(null);
    try {
      const current = (await this.bridge.accessGet()).userIds;
      this.allowedUserIds.set((await this.bridge.accessSet({ userIds: change(current) })).userIds);
      return true;
    } catch (err: unknown) {
      this.accessError.set(readIpcError(err).message);
      return false;
    } finally {
      this.accessBusy.set(false);
    }
  }
}
