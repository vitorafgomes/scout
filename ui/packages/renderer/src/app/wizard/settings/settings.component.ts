import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';

import { readIpcError } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

@Component({
  selector: 'sc-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="settings">
      <h1>Settings</h1>

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
        @if (accessError(); as error) {
          <p class="error" role="alert" data-test="access-error">{{ error }}</p>
        }
        <ul class="access-list">
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

      <section class="card">
        <h2>Provisioning</h2>
        <p class="description">
          Removes the stored Discord token and guild association. The bot will stop if currently
          running.
        </p>
        <button type="button" class="destructive" data-test="reprovision" (click)="reprovision()">
          Reprovision
        </button>
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

      <section class="card">
        <h2>Log verbosity</h2>
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
      .destructive {
        background: transparent;
        color: #d45a5a;
        border: 1px solid #2b1010;
        border-radius: 6px;
        padding: 8px 14px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .destructive:hover {
        background: #2b1010;
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
  private readonly wizard = inject(WizardStore);
  private readonly router = inject(Router);

  readonly version = signal<string | null>(null);
  readonly allowedUserIds = signal<readonly string[]>([]);
  readonly newUserId = signal('');
  readonly accessBusy = signal(false);
  readonly accessError = signal<string | null>(null);

  ngOnInit(): void {
    void this.bridge
      .appGetVersion()
      .then((res) => this.version.set(res.version))
      .catch(() => this.version.set(null));
    void this.bridge
      .accessGet()
      .then((res) => this.allowedUserIds.set(res.userIds))
      .catch((err: unknown) => this.accessError.set(readIpcError(err).message));
  }

  async addUser(): Promise<void> {
    if (await this.saveAllowedUsers([...this.allowedUserIds(), this.newUserId()])) {
      this.newUserId.set('');
    }
  }

  async removeUser(id: string): Promise<void> {
    await this.saveAllowedUsers(this.allowedUserIds().filter((existing) => existing !== id));
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

  async reprovision(): Promise<void> {
    const confirmed = window.confirm(
      'This will disconnect Scout and forget the current Discord app. Continue?',
    );
    if (!confirmed) return;
    try {
      await this.bridge.workerStop();
    } catch {
      // proceed regardless
    }
    await this.wizard.reprovision();
    void this.router.navigateByUrl('/');
  }
}
