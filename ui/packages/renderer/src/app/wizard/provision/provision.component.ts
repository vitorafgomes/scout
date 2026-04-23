import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ScoutError } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

const DEVELOPER_PORTAL_URL = 'https://discord.com/developers/applications';

@Component({
  selector: 'sc-provision',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="provision">
      <h1>Provision Discord</h1>
      <p class="subtitle">Step 1 of 3 — Create the Discord app</p>
      <p class="copy">
        Scout will open the Discord Developer Portal in your browser. Create an application, add a
        bot user, and return here with the token.
      </p>

      @if (openError(); as err) {
        <div class="error-block" role="alert">
          <p class="error">Could not open the Developer Portal: {{ err }}</p>
          <p class="copy-fallback">
            Copy the URL below and open it in any browser:
            <code class="url">{{ portalUrl }}</code>
          </p>
          <button type="button" class="secondary" (click)="copyUrl()">
            {{ copied() ? 'Copied' : 'Copy URL' }}
          </button>
        </div>
      }

      <div class="actions">
        <button type="button" class="secondary" (click)="back()">Back</button>
        <button type="button" class="primary" (click)="openPortal()" [disabled]="opening()">
          {{ opening() ? 'Opening...' : 'Open Developer Portal' }}
        </button>
        <button
          type="button"
          class="primary"
          data-test="created-continue"
          [disabled]="!portalOpened()"
          (click)="continueToToken()"
        >
          I've created the app - Continue
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
      .provision {
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
      .copy {
        font-size: 14px;
        line-height: 1.6;
        max-width: 620px;
      }
      .actions {
        margin-top: 32px;
        display: flex;
        gap: 12px;
        flex-wrap: wrap;
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
      .error-block {
        border: 1px solid #d45a5a;
        background: #2b1010;
        padding: 16px;
        border-radius: 10px;
        margin-top: 16px;
      }
      .error {
        color: #d45a5a;
        margin: 0 0 12px;
      }
      .copy-fallback {
        font-size: 13px;
        color: #e4e8f0;
        margin: 0 0 12px;
      }
      .url {
        display: block;
        font-family: 'JetBrains Mono', ui-monospace, Menlo, Consolas, monospace;
        background: #0a0d13;
        border-radius: 3px;
        padding: 6px 8px;
        margin-top: 6px;
        word-break: break-all;
      }
    `,
  ],
})
export class ProvisionComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly router = inject(Router);
  private readonly wizard = inject(WizardStore);

  readonly portalUrl = DEVELOPER_PORTAL_URL;
  readonly opening = signal<boolean>(false);
  readonly portalOpened = signal<boolean>(false);
  readonly openError = signal<string | null>(null);
  readonly copied = signal<boolean>(false);

  ngOnInit(): void {
    this.wizard.goTo('provision');
  }

  async openPortal(): Promise<void> {
    this.opening.set(true);
    this.openError.set(null);
    try {
      await this.bridge.appOpenExternal({ url: DEVELOPER_PORTAL_URL });
      this.portalOpened.set(true);
    } catch (err: unknown) {
      this.openError.set(this.describeError(err));
    } finally {
      this.opening.set(false);
    }
  }

  continueToToken(): void {
    this.wizard.goTo('token');
    void this.router.navigateByUrl('/token');
  }

  back(): void {
    void this.router.navigateByUrl('/');
  }

  async copyUrl(): Promise<void> {
    try {
      if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(DEVELOPER_PORTAL_URL);
        this.copied.set(true);
      }
    } catch {
      // Clipboard API may be blocked by CSP or permissions in some
      // environments. The URL remains visible in the read-only code block
      // so the user can still copy manually.
      this.copied.set(false);
    }
  }

  private describeError(err: unknown): string {
    if (err instanceof ScoutError) {
      return `${err.code}: ${err.message}`;
    }
    if (err instanceof Error) {
      return err.message;
    }
    return 'Unknown error.';
  }
}
