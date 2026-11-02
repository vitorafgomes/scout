import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { readIpcError, type DiscordBotInfo } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

type IntentState = 'checking' | 'on' | 'needs-portal' | 'failed';

@Component({
  selector: 'sc-token-paste',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="token-paste">
      <h1>Paste the bot token</h1>
      <p class="subtitle">Step 2 of 3</p>

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
                (paste)="onTokenPaste($event)"
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
              <button
                type="submit"
                class="secondary"
                data-test="submit"
                [disabled]="tokenValue().trim().length === 0 || submitting()"
              >
                {{ submitting() ? 'Checking...' : 'Save' }}
              </button>
            </div>
          </label>
        </fieldset>
      </form>

      @if (submissionError(); as err) {
        <p class="submission-error" role="alert" data-test="token-error">{{ err }}</p>
      }

      @if (bot(); as found) {
        <section class="found" aria-live="polite">
          <p class="ok" data-test="bot-found">
            &#10003; Found {{ found.applicationName }} (bot {{ found.botUsername }})
          </p>
          @switch (intent()) {
            @case ('checking') {
              <p data-test="intent-checking">Turning on Message Content Intent...</p>
            }
            @case ('on') {
              <p class="ok" data-test="intent-on">&#10003; Message Content Intent is on</p>
            }
            @case ('needs-portal') {
              <p class="fix" data-test="intent-fix">{{ intentFix()?.message }}</p>
              <div class="fix-actions">
                <button type="button" class="secondary" (click)="openPortal()">
                  Open Developer Portal
                </button>
                <button
                  type="button"
                  class="primary"
                  data-test="check-again"
                  (click)="enableIntent()"
                >
                  Check again
                </button>
              </div>
            }
            @case ('failed') {
              <p class="submission-error" role="alert">{{ intentFix()?.message }}</p>
              <div class="fix-actions">
                <button
                  type="button"
                  class="primary"
                  data-test="check-again"
                  (click)="enableIntent()"
                >
                  Check again
                </button>
              </div>
            }
          }
        </section>
      }

      <div class="actions">
        <button type="button" class="secondary" (click)="cancel()" [disabled]="submitting()">
          Cancel
        </button>
        <button
          type="button"
          class="primary"
          data-test="continue"
          [disabled]="intent() !== 'on'"
          (click)="continueNext()"
        >
          Continue
        </button>
      </div>
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
        color: #7b8299;
        margin: 0 0 24px;
        font-size: 13px;
      }
      .blocking-banner {
        background: #7a5c00;
        color: #fff3cd;
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
        background: #171b26;
        color: #e4e8f0;
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 10px 12px;
        font-size: 14px;
        font-family: 'JetBrains Mono', ui-monospace, Menlo, Consolas, monospace;
      }
      .input:focus {
        outline: 2px solid #7c5cff;
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
        color: #e4e8f0;
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 0 14px;
        cursor: pointer;
        font-size: 13px;
      }
      .error {
        color: #d45a5a;
        font-size: 13px;
        margin-top: 6px;
      }
      .submission-error {
        color: #d45a5a;
        font-size: 13px;
      }
      .found {
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 12px 16px;
        margin: 16px 0;
        font-size: 14px;
        line-height: 1.6;
      }
      .found p {
        margin: 0 0 6px;
      }
      .ok {
        color: #3aaf7b;
      }
      .fix {
        color: #f0c060;
      }
      .fix-actions {
        display: flex;
        gap: 12px;
        margin-top: 8px;
      }
      .actions {
        display: flex;
        gap: 12px;
        justify-content: flex-end;
        margin-top: 16px;
      }
      .primary {
        background: #7c5cff;
        color: #0f1117;
        border: none;
        border-radius: 6px;
        padding: 10px 16px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .primary:disabled {
        background: #3d4258;
        color: #7b8299;
        cursor: not-allowed;
      }
      .secondary {
        background: transparent;
        color: #e4e8f0;
        border: 1px solid #252b3b;
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
  readonly reveal = signal<boolean>(false);
  readonly submitting = signal<boolean>(false);
  readonly submissionError = signal<string | null>(null);
  readonly vaultBlocked = signal<boolean>(false);
  readonly bot = signal<DiscordBotInfo | null>(null);
  readonly intent = signal<IntentState | null>(null);
  readonly intentFix = signal<{ message: string; portalUrl: string | null } | null>(null);

  ngOnInit(): void {
    this.wizard.goTo('token');
  }

  onTokenInput(ev: Event): void {
    this.tokenValue.set((ev.target as HTMLInputElement).value);
    this.submissionError.set(null);
  }

  onTokenPaste(ev: ClipboardEvent): void {
    const pasted = ev.clipboardData?.getData('text') ?? '';
    if (pasted.trim().length === 0) return;
    ev.preventDefault();
    this.tokenValue.set(pasted);
    void this.submit();
  }

  toggleReveal(): void {
    this.reveal.update((v) => !v);
  }

  async submit(ev?: Event): Promise<void> {
    ev?.preventDefault();
    const token = this.tokenValue().trim();
    if (token.length === 0 || this.submitting()) return;

    this.submitting.set(true);
    this.submissionError.set(null);
    this.bot.set(null);
    this.intent.set(null);
    try {
      const availability = await this.bridge.vaultIsAvailable();
      if (!availability.available) {
        this.vaultBlocked.set(true);
        return;
      }
      const info = await this.bridge.discordCheckToken({ token });
      this.tokenValue.set('');
      this.bot.set(info);
      this.wizard.setClientId(info.applicationId);
      if (info.messageContent === 'on') {
        this.intent.set('on');
      } else {
        await this.enableIntent();
      }
    } catch (err: unknown) {
      this.submissionError.set(readIpcError(err).message);
    } finally {
      this.submitting.set(false);
    }
  }

  async enableIntent(): Promise<void> {
    this.intent.set('checking');
    this.intentFix.set(null);
    try {
      const result = await this.bridge.discordEnableIntent();
      if (result.enabled) {
        this.intent.set('on');
        return;
      }
      this.intentFix.set({ message: result.message, portalUrl: result.portalUrl });
      this.intent.set('needs-portal');
    } catch (err: unknown) {
      this.intentFix.set({ message: readIpcError(err).message, portalUrl: null });
      this.intent.set('failed');
    }
  }

  openPortal(): void {
    const url = this.intentFix()?.portalUrl;
    if (url == null) return;
    void this.bridge.appOpenExternal({ url }).catch((err: unknown) => {
      this.submissionError.set(readIpcError(err).message);
    });
  }

  continueNext(): void {
    if (this.intent() !== 'on') return;
    this.wizard.goTo('oauth');
    void this.router.navigateByUrl('/oauth');
  }

  cancel(): void {
    void this.wizard.reset();
    void this.router.navigateByUrl('/');
  }
}
