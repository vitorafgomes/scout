import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

const TIMEZONE_STORAGE_KEY = 'scout:settings.timezone';

function detectMachineZone(): string {
  try {
    const zone = new Intl.DateTimeFormat().resolvedOptions().timeZone;
    return zone && zone.trim() !== '' ? zone : 'UTC';
  } catch {
    return 'UTC';
  }
}

function loadSupportedZones(): readonly string[] {
  const intl = Intl as typeof Intl & {
    supportedValuesOf?: (key: string) => string[];
  };
  if (typeof intl.supportedValuesOf !== 'function') return [];
  try {
    return intl.supportedValuesOf('timeZone');
  } catch {
    return [];
  }
}

function isValidTimeZone(value: string): boolean {
  try {
    new Intl.DateTimeFormat('en', { timeZone: value });
    return true;
  } catch {
    return false;
  }
}

@Component({
  selector: 'sc-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="settings">
      <h1>Settings</h1>

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
        <h2>Time zone</h2>
        <p class="description">
          The daily-assistant plugins (dates, weather, holidays) use this zone. Leave it as the
          machine default or override it with any IANA identifier.
        </p>
        <p class="kv">
          Detected on this machine: <code data-test="tz-detected">{{ detectedZone() }}</code>
        </p>

        <label class="tz-label" for="tz-input">Effective time zone</label>
        @if (supportedZones().length > 0) {
          <select
            id="tz-input"
            class="tz-input"
            data-test="tz-input"
            [value]="tzValue()"
            (change)="onTzInput($event)"
          >
            @for (zone of supportedZones(); track zone) {
              <option [value]="zone" [selected]="zone === tzValue()">{{ zone }}</option>
            }
          </select>
        } @else {
          <input
            id="tz-input"
            class="tz-input"
            type="text"
            data-test="tz-input"
            placeholder="e.g. Europe/London"
            [value]="tzValue()"
            (input)="onTzInput($event)"
          />
        }

        @if (tzError(); as error) {
          <p class="tz-error" data-test="tz-error">{{ error }}</p>
        }
        @if (tzSaved()) {
          <p class="tz-saved" data-test="tz-saved">Saved — restart the worker to apply.</p>
        }

        <div class="tz-actions">
          <button type="button" class="primary" data-test="tz-save" (click)="saveTimezone()">
            Save
          </button>
          <button type="button" class="ghost" data-test="tz-reset" (click)="useMachineDefault()">
            Use machine default
          </button>
        </div>
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
        color: var(--sc-text);
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
        background: var(--sc-surface);
        border: 1px solid var(--sc-border);
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
        color: var(--sc-text-muted);
        margin: 0 0 12px;
      }
      .kv {
        font-size: 13px;
        color: var(--sc-text);
        margin: 0;
      }
      .kv code {
        font-family: 'JetBrains Mono', ui-monospace, monospace;
        background: var(--sc-bg);
        border: 1px solid var(--sc-border);
        padding: 2px 6px;
        border-radius: 4px;
      }
      .muted {
        color: var(--sc-text-muted);
        font-size: 13px;
        margin: 0;
      }
      .destructive {
        background: transparent;
        color: var(--sc-danger);
        border: 1px solid var(--sc-danger-bg);
        border-radius: 6px;
        padding: 8px 14px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .destructive:hover {
        background: var(--sc-danger-bg);
      }
      .tz-label {
        display: block;
        font-size: 12px;
        color: var(--sc-text-muted);
        margin: 12px 0 6px;
      }
      .tz-input {
        width: 100%;
        box-sizing: border-box;
        background: var(--sc-bg);
        color: var(--sc-text);
        border: 1px solid var(--sc-border);
        border-radius: 6px;
        padding: 8px 10px;
        font-size: 13px;
        font-family: 'JetBrains Mono', ui-monospace, monospace;
      }
      .tz-input:focus {
        outline: none;
        border-color: var(--sc-accent);
      }
      .tz-error {
        color: var(--sc-danger);
        font-size: 12px;
        margin: 8px 0 0;
      }
      .tz-saved {
        color: var(--sc-success-text);
        font-size: 12px;
        margin: 8px 0 0;
      }
      .tz-actions {
        display: flex;
        gap: 10px;
        margin-top: 12px;
      }
      .primary {
        background: var(--sc-accent);
        color: var(--sc-on-accent);
        border: 1px solid var(--sc-accent);
        border-radius: 6px;
        padding: 8px 14px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .primary:hover {
        background: var(--sc-accent-hover);
      }
      .ghost {
        background: transparent;
        color: var(--sc-text-muted);
        border: 1px solid var(--sc-border);
        border-radius: 6px;
        padding: 8px 14px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .ghost:hover {
        background: var(--sc-surface-2);
      }
    `,
  ],
})
export class SettingsComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly wizard = inject(WizardStore);
  private readonly router = inject(Router);

  readonly version = signal<string | null>(null);

  readonly detectedZone = signal<string>(detectMachineZone());
  readonly supportedZones = signal<readonly string[]>(loadSupportedZones());
  readonly tzValue = signal<string>(this.readEffectiveZone());
  readonly tzError = signal<string | null>(null);
  readonly tzSaved = signal<boolean>(false);

  ngOnInit(): void {
    void this.bridge
      .appGetVersion()
      .then((res) => this.version.set(res.version))
      .catch(() => this.version.set(null));
  }

  onTzInput(event: Event): void {
    const target = event.target as HTMLInputElement | HTMLSelectElement | null;
    this.tzValue.set(target?.value ?? '');
    this.tzError.set(null);
    this.tzSaved.set(false);
  }

  saveTimezone(): void {
    const value = this.tzValue().trim();
    if (value === '') {
      this.tzError.set('Enter a time zone identifier.');
      this.tzSaved.set(false);
      return;
    }
    if (!isValidTimeZone(value)) {
      this.tzError.set(`"${value}" is not a valid IANA time zone.`);
      this.tzSaved.set(false);
      return;
    }
    this.tzError.set(null);
    void this.bridge
      .vaultStore({ key: 'settings.timezone', value })
      .then(() => {
        window.localStorage.setItem(TIMEZONE_STORAGE_KEY, value);
        this.tzValue.set(value);
        this.tzSaved.set(true);
      })
      .catch(() => {
        this.tzError.set('Could not save the time zone. Try again.');
        this.tzSaved.set(false);
      });
  }

  useMachineDefault(): void {
    void this.bridge
      .vaultDelete({ key: 'settings.timezone' })
      .catch(() => {
        // ignore not-found — the override may never have been stored
      })
      .finally(() => {
        window.localStorage.removeItem(TIMEZONE_STORAGE_KEY);
        this.tzValue.set(this.detectedZone());
        this.tzError.set(null);
        this.tzSaved.set(false);
      });
  }

  private readEffectiveZone(): string {
    const override = window.localStorage.getItem(TIMEZONE_STORAGE_KEY);
    if (override !== null && override.trim() !== '') return override;
    return detectMachineZone();
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
