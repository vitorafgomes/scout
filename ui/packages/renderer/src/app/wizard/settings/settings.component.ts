import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { readIpcError, type DesktopSettings, type LocalBubbleSettings } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { ChatClientsComponent } from './chat-clients.component';
import { WorkerSettingsComponent } from './worker-settings.component';

const CORNERS: readonly { value: LocalBubbleSettings['corner']; label: string }[] = [
  { value: 'top-left', label: 'Top left' },
  { value: 'top-right', label: 'Top right' },
  { value: 'bottom-left', label: 'Bottom left' },
  { value: 'bottom-right', label: 'Bottom right' },
];

const DISPLAYS: readonly { value: LocalBubbleSettings['display']; label: string }[] = [
  { value: 'cursor', label: 'Display with the mouse cursor' },
  { value: 'primary', label: 'Primary display' },
];

@Component({
  selector: 'sc-settings',
  standalone: true,
  imports: [ChatClientsComponent, WorkerSettingsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="settings">
      <h1>Settings</h1>

      <sc-chat-clients />

      <sc-worker-settings />

      <section class="card" data-test="access-section">
        <h2>Who can use Scout</h2>
        <p class="description" data-test="access-help">
          Discord: turn on Developer Mode, right-click a user, Copy User ID. Slack: profile, More,
          Copy member ID. Teams: the user's Bot Framework user ID, which starts with 29:.
        </p>
        <form class="access-add" (submit)="$event.preventDefault(); addUser()">
          <input
            type="text"
            aria-label="User ID"
            placeholder="User ID"
            data-test="access-input"
            [value]="newUserId()"
            (input)="newUserId.set($any($event.target).value)"
          />
          <button
            type="submit"
            data-test="access-add"
            [disabled]="accessBusy() || newUserId().trim() === ''"
          >
            Add
          </button>
        </form>
        <button
          type="button"
          data-test="access-pair"
          [disabled]="accessBusy()"
          (click)="startPairing()"
        >
          Pair from chat
        </button>
        @if (pairingCode(); as code) {
          <p class="description" data-test="access-pair-instructions">
            Send <code>{{ code }}</code> to Scout in a direct message on Discord.
          </p>
        }
        @if (pairingExpired()) {
          <p class="error" role="alert" data-test="access-pair-expired">
            Pairing expired. Start again.
          </p>
        }
        @if (accessError(); as error) {
          <p class="error" role="alert" data-test="access-error">{{ error }}</p>
        }
        <ul class="access-list">
          @if (allowedUserIds().length === 0) {
            <li class="description" data-test="access-empty">
              No one is listed yet. The list only matters once Discord or Slack is connected; chat
              on this computer never needs it.
            </li>
          }
          @for (id of allowedUserIds(); track id) {
            <li>
              <code [attr.data-test]="'access-user-' + id">{{ id }}</code>
              <button
                type="button"
                [attr.data-test]="'access-remove-' + id"
                [disabled]="accessBusy()"
                (click)="removeUser(id)"
              >
                Remove
              </button>
            </li>
          }
        </ul>
      </section>

      <section class="card" data-test="shortcut-section">
        <h2>Floating button and quick ask</h2>
        <p class="description">
          A small round Scout button stays above your windows without covering anything else. Click
          it to open the quick-ask box in the same corner.
        </p>
        @if (bubble(); as settings) {
          <button
            type="button"
            role="switch"
            class="switch"
            data-test="bubble-toggle"
            [attr.aria-checked]="settings.enabled"
            [disabled]="bubbleBusy()"
            (click)="saveBubble({ enabled: !settings.enabled })"
          >
            Show the floating button
          </button>
          <div class="choices" role="group" aria-label="Corner">
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
          <div class="choices" role="group" aria-label="Display">
            @for (display of displays; track display.value) {
              <button
                type="button"
                [attr.data-test]="'bubble-display-' + display.value"
                [attr.aria-pressed]="settings.display === display.value"
                [disabled]="bubbleBusy()"
                (click)="saveBubble({ display: display.value })"
              >
                {{ display.label }}
              </button>
            }
          </div>
        }
        @if (bubbleError(); as error) {
          <p class="error" role="alert" data-test="bubble-error">{{ error }}</p>
        }
        <h3>Shortcut</h3>
        <p class="description">
          Press it anywhere to open a small box and ask Scout, typed or spoken. Use Electron's key
          names, for example CommandOrControl+Alt+Space.
        </p>
        <form class="access-add" (submit)="$event.preventDefault(); saveShortcut()">
          <input
            type="text"
            aria-label="Quick ask shortcut"
            data-test="shortcut-input"
            [value]="shortcut()"
            (input)="shortcut.set($any($event.target).value)"
          />
          <button
            type="submit"
            data-test="shortcut-save"
            [disabled]="shortcutBusy() || shortcut().trim() === ''"
          >
            Save
          </button>
        </form>
        @if (shortcutHint(); as hint) {
          <p class="description hint" data-test="shortcut-hint">{{ hint }}</p>
        }
        @if (shortcutError(); as error) {
          <p class="error" role="alert" data-test="shortcut-error">{{ error }}</p>
        }
      </section>

      <section class="card" data-test="desktop-section">
        <h2>Desktop</h2>
        @if (desktop(); as settings) {
          <label class="toggle">
            <input
              type="checkbox"
              data-test="desktop-keep-running"
              [checked]="settings.keepRunning"
              [disabled]="desktopBusy() || !settings.trayAvailable"
              (change)="setKeepRunning($any($event.target))"
            />
            Keep Scout running when the window is closed
          </label>
          @if (!settings.trayAvailable) {
            <p class="description" data-test="desktop-no-tray">
              This desktop has no system tray, so Scout quits when its window is closed.
            </p>
          }
          <label class="toggle">
            <input
              type="checkbox"
              data-test="desktop-start-at-login"
              [checked]="settings.startAtLogin"
              [disabled]="desktopBusy()"
              (change)="setStartAtLogin($any($event.target))"
            />
            Start Scout when I log in
          </label>
        }
        @if (desktopError(); as error) {
          <p class="error" role="alert" data-test="desktop-error">{{ error }}</p>
        }
      </section>

      <section class="card">
        <h2>About</h2>
        <p class="kv">
          Version: <code>{{ version() ?? 'unknown' }}</code>
        </p>
      </section>

      <section class="card">
        <h2>Theme</h2>
        <p class="muted">Coming soon.</p>
      </section>
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        color: #e4e8f0;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .settings {
        padding: 32px;
        max-width: 720px;
        margin: 0 auto;
        display: flex;
        flex-direction: column;
        gap: 24px;
      }
      h1 {
        font-size: 20px;
        margin: 0;
      }
      .card {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 10px;
        padding: 16px;
      }
      h2 {
        margin: 0 0 8px;
        font-size: 14px;
        font-weight: 600;
      }
      .description {
        font-size: 13px;
        color: #7b8299;
        margin: 0 0 12px;
      }
      .kv {
        font-size: 13px;
        color: #e4e8f0;
        margin: 0;
      }
      .kv code {
        font-family: 'JetBrains Mono', ui-monospace, monospace;
        background: #0f1117;
        border: 1px solid #252b3b;
        padding: 2px 6px;
        border-radius: 4px;
      }
      .muted {
        color: #7b8299;
        font-size: 13px;
        margin: 0;
      }
      .access-add {
        display: flex;
        gap: 8px;
      }
      .access-add input {
        flex: 1;
        background: #0f1117;
        border: 1px solid #252b3b;
        border-radius: 6px;
        color: #e4e8f0;
        padding: 6px 10px;
      }
      .access-list {
        list-style: none;
        margin: 12px 0 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .access-list li {
        display: flex;
        justify-content: space-between;
        align-items: center;
        font-size: 13px;
      }
      h3 {
        margin: 16px 0 8px;
        font-size: 13px;
        font-weight: 600;
      }
      .hint {
        margin: 8px 0 0;
      }
      .choices {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        margin: 0 0 12px;
      }
      .choices button[aria-pressed='true'] {
        background: #2563eb;
        border-color: #2563eb;
        color: #ffffff;
      }
      .switch {
        display: inline-flex;
        align-items: center;
        gap: 8px;
        margin: 0 0 12px;
      }
      .switch::before {
        content: '';
        width: 28px;
        height: 16px;
        border-radius: 8px;
        background: #252b3b;
      }
      .switch[aria-checked='true']::before {
        background: #2563eb;
      }
      .toggle {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 13px;
        margin: 0 0 8px;
      }
      .error {
        color: #d45a5a;
        font-size: 13px;
        margin: 8px 0 0;
      }
    `,
  ],
})
export class SettingsComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);

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
  readonly shortcut = signal('');
  readonly shortcutBusy = signal(false);
  readonly shortcutError = signal<string | null>(null);
  readonly shortcutHint = signal<string | null>(null);
  readonly bubble = signal<LocalBubbleSettings | null>(null);
  readonly bubbleBusy = signal(false);
  readonly bubbleError = signal<string | null>(null);
  readonly corners = CORNERS;
  readonly displays = DISPLAYS;

  constructor() {
    const unsubscribe = this.bridge.onWorkerEvent((event) => {
      if (event.kind === 'stdout' && event.line.trim() === 'SCOUT_PAIRING_BURNED') {
        this.pairingCode.set(null);
        this.pairingExpired.set(true);
      }
    });
    inject(DestroyRef).onDestroy(unsubscribe);
  }

  ngOnInit(): void {
    void this.bridge
      .appGetVersion()
      .then((res) => this.version.set(res.version))
      .catch(() => this.version.set(null));
    void this.bridge
      .accessGet()
      .then((res) => this.allowedUserIds.set(res.userIds))
      .catch((err: unknown) => this.accessError.set(readIpcError(err).message));
    void this.bridge
      .desktopGetSettings()
      .then((settings) => this.desktop.set(settings))
      .catch((err: unknown) => this.desktopError.set(readIpcError(err).message));
    void this.bridge
      .localGetShortcut()
      .then((res) => {
        this.shortcut.set(res.accelerator);
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
      this.bubble.set(await this.bridge.localSetBubble({ ...current, ...change }));
    } catch (err: unknown) {
      this.bubbleError.set(readIpcError(err).message);
    } finally {
      this.bubbleBusy.set(false);
    }
  }

  async saveShortcut(): Promise<void> {
    this.shortcutBusy.set(true);
    this.shortcutError.set(null);
    try {
      const status = await this.bridge.localSetShortcut({ accelerator: this.shortcut().trim() });
      this.shortcut.set(status.accelerator);
      this.shortcutHint.set(status.hint);
    } catch (err: unknown) {
      this.shortcutError.set(readIpcError(err).message);
    } finally {
      this.shortcutBusy.set(false);
    }
  }

  async setKeepRunning(input: HTMLInputElement): Promise<void> {
    await this.saveDesktop(input, (enabled) => this.bridge.desktopSetKeepRunning({ enabled }));
  }

  async setStartAtLogin(input: HTMLInputElement): Promise<void> {
    await this.saveDesktop(input, (enabled) => this.bridge.desktopSetStartAtLogin({ enabled }));
  }

  private async saveDesktop(
    input: HTMLInputElement,
    save: (enabled: boolean) => Promise<DesktopSettings>,
  ): Promise<void> {
    const enabled = input.checked;
    this.desktopBusy.set(true);
    this.desktopError.set(null);
    try {
      this.desktop.set(await save(enabled));
    } catch (err: unknown) {
      input.checked = !enabled;
      this.desktopError.set(readIpcError(err).message);
    } finally {
      this.desktopBusy.set(false);
    }
  }

  async addUser(): Promise<void> {
    if (await this.saveAllowedUsers([...this.allowedUserIds(), this.newUserId()])) {
      this.newUserId.set('');
    }
  }

  async removeUser(id: string): Promise<void> {
    await this.saveAllowedUsers(this.allowedUserIds().filter((existing) => existing !== id));
  }

  async startPairing(): Promise<void> {
    this.accessError.set(null);
    this.pairingExpired.set(false);
    try {
      this.pairingCode.set((await this.bridge.accessStartPairing()).code);
    } catch (err: unknown) {
      this.accessError.set(readIpcError(err).message);
    }
  }

  private async saveAllowedUsers(userIds: readonly string[]): Promise<boolean> {
    this.accessBusy.set(true);
    this.accessError.set(null);
    try {
      this.allowedUserIds.set((await this.bridge.accessSet({ userIds })).userIds);
      return true;
    } catch (err: unknown) {
      this.accessError.set(readIpcError(err).message);
      return false;
    } finally {
      this.accessBusy.set(false);
    }
  }
}
