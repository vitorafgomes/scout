import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';
import { ScoutError } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

const TOKEN_PATTERN = /^[A-Za-z0-9_\-.]{50,}$/;
const CLIENT_ID_MIN_LENGTH = 10;

@Component({
  selector: 'sc-token-paste',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="token-paste">
      <h1>Provision Discord</h1>
      <p class="subtitle">Step 2 of 3 - Bot token and intents</p>

      @if (vaultBlocked()) {
        <div class="blocking-banner" role="alert">
          Keyring unavailable. Install gnome-keyring / kwallet / libsecret to proceed.
        </div>
      }

      <form (submit)="submit($event)" autocomplete="off" novalidate>
        <fieldset [disabled]="submitting()">
          <label class="field">
            <span class="label">Discord bot token</span>
            <div class="token-row">
              <input
                [type]="reveal() ? 'text' : 'password'"
                class="input"
                autocomplete="off"
                spellcheck="false"
                data-test="token-input"
                [value]="tokenValue()"
                (input)="onTokenInput($event)"
                (blur)="onTokenBlur()"
                aria-label="Discord bot token"
              />
              <button
                type="button"
                class="reveal"
                (click)="toggleReveal()"
                [attr.aria-label]="reveal() ? 'Hide token' : 'Show token'"
              >
                {{ reveal() ? 'Hide' : 'Show' }}
              </button>
            </div>
            @if (tokenError(); as msg) {
              <span class="error" role="alert">{{ msg }}</span>
            }
          </label>

          <label class="field">
            <span class="label">Application (client) ID</span>
            <input
              type="text"
              class="input"
              autocomplete="off"
              spellcheck="false"
              data-test="client-id-input"
              [value]="clientIdValue()"
              (input)="onClientIdInput($event)"
              (blur)="onClientIdBlur()"
              aria-label="Discord application client id"
            />
            @if (clientIdError(); as msg) {
              <span class="error" role="alert">{{ msg }}</span>
            }
          </label>

          <fieldset class="intents">
            <legend>Required intents (enable in Portal then acknowledge here)</legend>
            <label class="checkbox">
              <input
                type="checkbox"
                data-test="intent-message-content"
                [checked]="intentMessageContent()"
                (change)="setIntent('messageContent', $event)"
              />
              Message Content Intent
            </label>
            <label class="checkbox">
              <input
                type="checkbox"
                data-test="intent-guild-members"
                [checked]="intentGuildMembers()"
                (change)="setIntent('guildMembers', $event)"
              />
              Server Members Intent
            </label>
            <label class="checkbox">
              <input
                type="checkbox"
                data-test="intent-presence"
                [checked]="intentPresence()"
                (change)="setIntent('presence', $event)"
              />
              Presence Intent
            </label>
          </fieldset>

          @if (submissionError(); as err) {
            <p class="submission-error" role="alert">{{ err }}</p>
          }

          <div class="actions">
            <button type="button" class="secondary" (click)="cancel()" [disabled]="submitting()">
              Cancel
            </button>
            <button
              type="submit"
              class="primary"
              data-test="submit"
              [disabled]="!canSubmit() || submitting()"
            >
              {{ submitting() ? 'Saving...' : 'Continue' }}
            </button>
          </div>
        </fieldset>
      </form>
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
      .token-paste {
        padding: 32px;
        max-width: 720px;
        margin: 0 auto;
      }
      h1 {
        font-size: 20px;
        margin: 0 0 4px;
      }
      .subtitle {
        color: var(--sc-text-muted);
        margin: 0 0 24px;
        font-size: 13px;
      }
      .blocking-banner {
        background: var(--sc-warning-bg);
        color: var(--sc-warning-text);
        padding: 12px 16px;
        border-radius: 6px;
        margin-bottom: 16px;
      }
      .field {
        display: flex;
        flex-direction: column;
        margin-bottom: 16px;
      }
      .label {
        font-size: 13px;
        font-weight: 500;
        margin-bottom: 8px;
      }
      .input {
        background: var(--sc-surface);
        color: var(--sc-text);
        border: 1px solid var(--sc-border);
        border-radius: 6px;
        padding: 10px 12px;
        font-size: 14px;
        font-family: 'JetBrains Mono', ui-monospace, Menlo, Consolas, monospace;
      }
      .input:focus {
        outline: 2px solid var(--sc-accent);
        outline-offset: 1px;
      }
      .token-row {
        display: flex;
        gap: 8px;
      }
      .token-row .input {
        flex: 1;
      }
      .reveal {
        background: transparent;
        color: var(--sc-text);
        border: 1px solid var(--sc-border);
        border-radius: 6px;
        padding: 0 14px;
        cursor: pointer;
        font-size: 13px;
      }
      .intents {
        border: 1px solid var(--sc-border);
        border-radius: 6px;
        padding: 12px 16px;
        margin: 16px 0;
      }
      .intents legend {
        font-size: 13px;
        color: var(--sc-text-muted);
        padding: 0 6px;
      }
      .checkbox {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 13px;
        padding: 4px 0;
      }
      .error {
        color: var(--sc-danger);
        font-size: 13px;
        margin-top: 6px;
      }
      .submission-error {
        color: var(--sc-danger);
        font-size: 13px;
      }
      .actions {
        display: flex;
        gap: 12px;
        justify-content: flex-end;
        margin-top: 16px;
      }
      .primary {
        background: var(--sc-accent);
        color: var(--sc-bg);
        border: none;
        border-radius: 6px;
        padding: 10px 16px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .primary:disabled {
        background: var(--sc-disabled-bg);
        color: var(--sc-text-muted);
        cursor: not-allowed;
      }
      .secondary {
        background: transparent;
        color: var(--sc-text);
        border: 1px solid var(--sc-border);
        border-radius: 6px;
        padding: 10px 16px;
        font-size: 14px;
        cursor: pointer;
      }
    `,
  ],
})
export class TokenPasteComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly router = inject(Router);
  private readonly wizard = inject(WizardStore);

  readonly tokenValue = signal<string>('');
  readonly clientIdValue = signal<string>('');
  readonly reveal = signal<boolean>(false);

  readonly intentMessageContent = signal<boolean>(false);
  readonly intentGuildMembers = signal<boolean>(false);
  readonly intentPresence = signal<boolean>(false);

  readonly tokenError = signal<string | null>(null);
  readonly clientIdError = signal<string | null>(null);
  readonly submissionError = signal<string | null>(null);
  readonly submitting = signal<boolean>(false);
  readonly vaultBlocked = signal<boolean>(false);

  readonly allIntentsChecked = computed<boolean>(
    () => this.intentMessageContent() && this.intentGuildMembers() && this.intentPresence(),
  );

  readonly tokenLooksValid = computed<boolean>(() => TOKEN_PATTERN.test(this.tokenValue().trim()));

  readonly clientIdLooksValid = computed<boolean>(
    () => this.clientIdValue().trim().length >= CLIENT_ID_MIN_LENGTH,
  );

  readonly canSubmit = computed<boolean>(
    () =>
      !this.vaultBlocked() &&
      this.tokenLooksValid() &&
      this.clientIdLooksValid() &&
      this.allIntentsChecked(),
  );

  // Probe used by tests to verify the local token buffer is cleared after
  // successful submit. Intentionally private to discourage external reads.
  private _lastSubmittedTokenLength: number | null = null;

  ngOnInit(): void {
    this.wizard.goTo('token');
  }

  onTokenInput(ev: Event): void {
    const value = (ev.target as HTMLInputElement).value;
    this.tokenValue.set(value);
    this.tokenError.set(null);
  }

  onTokenBlur(): void {
    const value = this.tokenValue();
    if (value.length === 0) {
      this.tokenError.set('Token is required.');
      return;
    }
    if (/\s/.test(value)) {
      this.tokenError.set('Token contains whitespace. Re-paste without surrounding spaces.');
      return;
    }
    if (!TOKEN_PATTERN.test(value)) {
      this.tokenError.set('Token does not match the expected Discord format.');
      return;
    }
    this.tokenError.set(null);
  }

  onClientIdInput(ev: Event): void {
    const value = (ev.target as HTMLInputElement).value;
    this.clientIdValue.set(value);
    this.clientIdError.set(null);
  }

  onClientIdBlur(): void {
    const value = this.clientIdValue().trim();
    if (value.length < CLIENT_ID_MIN_LENGTH) {
      this.clientIdError.set(`Client ID must be at least ${CLIENT_ID_MIN_LENGTH} characters.`);
      return;
    }
    this.clientIdError.set(null);
  }

  toggleReveal(): void {
    this.reveal.update((v) => !v);
  }

  setIntent(which: 'messageContent' | 'guildMembers' | 'presence', ev: Event): void {
    const checked = (ev.target as HTMLInputElement).checked;
    switch (which) {
      case 'messageContent':
        this.intentMessageContent.set(checked);
        return;
      case 'guildMembers':
        this.intentGuildMembers.set(checked);
        return;
      case 'presence':
        this.intentPresence.set(checked);
        return;
    }
  }

  async submit(ev: Event): Promise<void> {
    ev.preventDefault();
    if (!this.canSubmit() || this.submitting()) return;

    this.submitting.set(true);
    this.submissionError.set(null);

    // Hold the token in a local const only; clear the signal immediately
    // after the vault store resolves. The token value must never linger.
    const token = this.tokenValue().trim();
    const clientId = this.clientIdValue().trim();

    try {
      const availability = await this.bridge.vaultIsAvailable();
      if (!availability.available) {
        this.vaultBlocked.set(true);
        this.submitting.set(false);
        return;
      }

      await this.bridge.vaultStore({ key: 'discord.bot-token', value: token });
      await this.bridge.vaultStore({ key: 'discord.client-id', value: clientId });

      this._lastSubmittedTokenLength = token.length;
      this.tokenValue.set('');

      this.wizard.setClientId(clientId);
      this.wizard.markIntentsAcknowledged();
      this.wizard.goTo('oauth');
      void this.router.navigateByUrl('/oauth');
    } catch (err: unknown) {
      this.submissionError.set(this.describeError(err));
    } finally {
      this.submitting.set(false);
    }
  }

  cancel(): void {
    void this.wizard.reset();
    void this.router.navigateByUrl('/');
  }

  // Test probe: exposes only the length that was submitted, never the token.
  probeLastSubmittedTokenLength(): number | null {
    return this._lastSubmittedTokenLength;
  }

  private describeError(err: unknown): string {
    if (err instanceof ScoutError) {
      return err.message;
    }
    if (err instanceof Error) {
      return err.message;
    }
    return 'Unknown error while saving.';
  }
}
