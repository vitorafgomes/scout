import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { Router } from '@angular/router';
import {
  INSECURE_STORE_MESSAGE,
  readIpcError,
  type DiscordBotInfo,
  type VaultUnavailableReason,
} from '@scout/shared';

import { IconComponent } from '../../core/icon.component';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { WIZARD_PAGE_STYLES, WizardFooterComponent } from '../wizard-footer.component';

type IntentState = 'checking' | 'on' | 'needs-portal' | 'failed';

@Component({
  selector: 'sc-token-paste',
  standalone: true,
  imports: [IconComponent, MatButtonModule, WizardFooterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <div class="heading">
        <h1>Paste the bot token</h1>
        <p class="lede">
          Scout checks the token with Discord and turns on what it needs to read your messages.
        </p>
      </div>

      @if (vaultBlocked(); as reason) {
        <div class="blocking-banner" role="alert">
          @if (reason === 'insecure-store') {
            {{ insecureStoreMessage }}
          } @else {
            Keyring unavailable. Install gnome-keyring / kwallet / libsecret to proceed.
          }
        </div>
      }

      <section class="card">
        <form (submit)="submit($event)" autocomplete="off" novalidate>
          <fieldset [disabled]="submitting()">
            <label class="field">
              <span class="label">Discord bot token</span>
              <div class="token-row">
                <div class="box" [class.box--error]="submissionError() !== null">
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
                    data-test="reveal"
                    (click)="toggleReveal()"
                    [attr.aria-label]="reveal() ? 'Hide token' : 'Show token'"
                    [title]="reveal() ? 'Hide token' : 'Show token'"
                  >
                    <svg
                      viewBox="0 0 24 24"
                      width="17"
                      height="17"
                      fill="none"
                      stroke="currentColor"
                      stroke-width="2"
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      aria-hidden="true"
                      focusable="false"
                    >
                      <path
                        d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z"
                      />
                    </svg>
                  </button>
                </div>
                <button
                  type="submit"
                  [matButton]="saved() ? 'outlined' : 'filled'"
                  data-test="submit"
                  [disabled]="tokenValue().trim().length === 0 || submitting()"
                >
                  <sc-icon name="save" [size]="16" />
                  {{ saveLabel() }}
                </button>
              </div>
            </label>
          </fieldset>
        </form>

        @if (submissionError(); as err) {
          <p class="help help--error" role="alert" data-test="token-error">{{ err }}</p>
        } @else {
          <p class="help" data-test="token-help">
            Paste it here; Scout saves and checks it as soon as you paste.
          </p>
        }

        @if (bot(); as found) {
          <div class="checks" aria-live="polite">
            <div class="check" data-tone="ok">
              <span class="tick" aria-hidden="true"><sc-icon name="check" [size]="13" /></span>
              <div class="check-text">
                <span class="check-title" data-test="bot-found"
                  >Found {{ found.applicationName }} (bot {{ found.botUsername }})</span
                >
                <span class="muted">The token belongs to the application you created.</span>
              </div>
            </div>
            @switch (intent()) {
              @case ('checking') {
                <div class="check" data-tone="busy">
                  <span class="tick" aria-hidden="true"
                    ><sc-icon name="refresh" [size]="13"
                  /></span>
                  <div class="check-text">
                    <span class="check-title" data-test="intent-checking"
                      >Turning on Message Content Intent...</span
                    >
                  </div>
                </div>
              }
              @case ('on') {
                <div class="check" data-tone="ok">
                  <span class="tick" aria-hidden="true"><sc-icon name="check" [size]="13" /></span>
                  <div class="check-text">
                    <span class="check-title" data-test="intent-on"
                      >Message Content Intent is on</span
                    >
                    <span class="muted">Lets Scout read the messages that mention it.</span>
                  </div>
                </div>
              }
              @case ('needs-portal') {
                <div class="check" data-tone="warn">
                  <span class="tick" aria-hidden="true"
                    ><sc-icon name="warning" [size]="13"
                  /></span>
                  <div class="check-text">
                    <span class="check-title" data-test="intent-fix">{{
                      intentFix()?.message
                    }}</span>
                    <div class="fix-actions">
                      <button type="button" matButton="outlined" (click)="openPortal()">
                        Open Developer Portal
                      </button>
                      <button
                        type="button"
                        matButton="filled"
                        data-test="check-again"
                        (click)="enableIntent()"
                      >
                        <sc-icon name="refresh" [size]="16" />
                        Check again
                      </button>
                    </div>
                    @if (portalFailed()) {
                      <p class="help help--error" role="alert" data-test="portal-failed">
                        The Developer Portal did not open. Open this address in any browser, then
                        come back and check again:
                        <span class="address">{{ intentFix()?.portalUrl }}</span>
                      </p>
                    }
                  </div>
                </div>
              }
              @case ('failed') {
                <div class="check" data-tone="error">
                  <span class="tick" aria-hidden="true"><sc-icon name="close" [size]="13" /></span>
                  <div class="check-text">
                    <span class="check-title" role="alert">{{ intentFix()?.message }}</span>
                    <div class="fix-actions">
                      <button
                        type="button"
                        matButton="filled"
                        data-test="check-again"
                        (click)="enableIntent()"
                      >
                        <sc-icon name="refresh" [size]="16" />
                        Check again
                      </button>
                    </div>
                  </div>
                </div>
              }
            }
          </div>
        } @else if (intent() === 'on') {
          <div class="checks" aria-live="polite">
            <div class="check" data-tone="ok">
              <span class="tick" aria-hidden="true"><sc-icon name="check" [size]="13" /></span>
              <div class="check-text">
                <span class="check-title" data-test="token-saved"
                  >The bot token is saved and Message Content Intent is on</span
                >
              </div>
            </div>
          </div>
        }

        <p class="help" data-test="token-kept">
          The token is kept encrypted on this computer, under a key from its keyring.
        </p>
      </section>

      <sc-wizard-footer
        [blockedReason]="intent() === 'on' ? null : 'Save a token Discord accepts to continue.'"
        [backDisabled]="submitting()"
        (back)="back()"
        (continue)="continueNext()"
      />
    </main>
  `,
  styles: [
    WIZARD_PAGE_STYLES,
    `
      .blocking-banner {
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
        font-size: 13px;
      }
      fieldset {
        margin: 0;
        padding: 0;
        border: 0;
        min-width: 0;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .label {
        font-size: 13px;
        font-weight: 600;
      }
      .token-row {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      .box {
        flex: 1 1 280px;
        display: flex;
        align-items: center;
        height: 40px;
        box-sizing: border-box;
        padding: 0 4px 0 12px;
        border: 1px solid var(--sc-line);
        border-radius: 10px;
        background: var(--sc-bg);
      }
      .box--error {
        border-color: var(--sc-error);
      }
      .box:focus-within {
        outline: 2px solid var(--sc-focus);
        outline-offset: 1px;
      }
      .input {
        flex: 1;
        min-width: 0;
        border: 0;
        outline: 0;
        background: transparent;
        color: var(--sc-fg);
        font: inherit;
      }
      .reveal {
        display: flex;
        align-items: center;
        justify-content: center;
        width: 32px;
        height: 32px;
        border: 0;
        border-radius: 8px;
        background: transparent;
        color: var(--sc-muted);
        cursor: pointer;
      }
      .help {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .help--error {
        color: var(--sc-error);
      }
      .checks {
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .check {
        display: flex;
        align-items: flex-start;
        gap: 10px;
      }
      .tick {
        flex: 0 0 22px;
        display: flex;
        align-items: center;
        justify-content: center;
        width: 22px;
        height: 22px;
        border-radius: 11px;
        background: var(--sc-surface-2);
        color: var(--sc-accent);
      }
      .check[data-tone='ok'] .tick {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .check[data-tone='warn'] .tick {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .check[data-tone='error'] .tick {
        background: var(--sc-error-container);
        color: var(--sc-error);
      }
      .check-text {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .check-title {
        font-weight: 500;
      }
      .address {
        overflow-wrap: anywhere;
        font-family: var(--sc-mono);
      }
      .fix-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
        margin-top: 8px;
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
  readonly vaultBlocked = signal<VaultUnavailableReason | null>(null);
  readonly insecureStoreMessage = INSECURE_STORE_MESSAGE;
  readonly bot = signal<DiscordBotInfo | null>(null);
  readonly intent = signal<IntentState | null>(null);
  readonly intentFix = signal<{ message: string; portalUrl: string | null } | null>(null);
  /** True once Open Developer Portal could not open the browser; the reason stays in main's log. */
  readonly portalFailed = signal<boolean>(false);

  /** True once the token is saved and the field is empty again, so Save has nothing new to send. */
  readonly saved = computed<boolean>(
    () => this.intent() === 'on' && this.tokenValue().trim().length === 0,
  );

  /** What the save button reads: the check in flight, a rejected token to send again, the saved token, or Save. */
  readonly saveLabel = computed<string>(() => {
    if (this.submitting()) return 'Checking...';
    if (this.submissionError() !== null) return 'Try again';
    return this.saved() ? 'Saved' : 'Save';
  });

  ngOnInit(): void {
    this.wizard.goTo('token');
    void this.showSavedBot();
  }

  /**
   * Coming back to this step with the bot already set up, the saved token counts: main stores it only once Message
   * Content Intent is on, so Continue opens without a new token, which Discord shows only once.
   */
  private async showSavedBot(): Promise<void> {
    if (this.wizard.clientId() === null) return;
    try {
      const { exists } = await this.bridge.vaultHas({ key: 'discord.bot-token' });
      if (exists && this.intent() === null && !this.submitting()) this.intent.set('on');
    } catch {
      return;
    }
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
    this.reveal.set(false);
    this.submissionError.set(null);
    this.bot.set(null);
    this.intent.set(null);
    try {
      const availability = await this.bridge.vaultIsAvailable();
      if (!availability.available) {
        this.vaultBlocked.set(availability.reason ?? 'no-keyring');
        return;
      }
      this.vaultBlocked.set(null);
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
    this.portalFailed.set(false);
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
    this.portalFailed.set(false);
    void this.bridge.appOpenExternal({ url }).catch(() => this.portalFailed.set(true));
  }

  continueNext(): void {
    if (this.intent() !== 'on') return;
    this.wizard.goTo('oauth');
    void this.router.navigateByUrl('/oauth');
  }

  back(): void {
    this.wizard.goTo('provision');
    void this.router.navigateByUrl('/provision');
  }
}
