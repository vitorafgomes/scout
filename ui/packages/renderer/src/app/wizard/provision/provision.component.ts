import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { Router } from '@angular/router';

import { IconComponent } from '../../core/icon.component';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { WIZARD_PAGE_STYLES, WizardFooterComponent } from '../wizard-footer.component';

const DEVELOPER_PORTAL_URL = 'https://discord.com/developers/applications';

@Component({
  selector: 'sc-provision',
  standalone: true,
  imports: [IconComponent, MatButtonModule, WizardFooterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <div class="heading">
        <h1>Create your Discord bot</h1>
        <p class="lede">
          Scout talks in Discord through a bot you own. Create it in the Discord Developer Portal.
        </p>
      </div>

      <section class="card">
        <ol class="steps" data-test="portal-steps">
          <li>Click <strong>New Application</strong> and name it Scout.</li>
          <li>Open <strong>Bot</strong> and click <strong>Reset Token</strong>.</li>
          <li>Click <strong>Copy</strong>, then come back here.</li>
        </ol>
        <p class="warn" data-test="reset-warning">
          <sc-icon name="warning" [size]="16" />
          <span
            >Reset Token stops the old token working. If this bot already runs somewhere else, that
            copy goes offline.</span
          >
        </p>
        <div class="portal">
          <button
            type="button"
            [matButton]="portalOpened() ? 'outlined' : 'filled'"
            data-test="open-portal"
            [disabled]="opening()"
            (click)="openPortal()"
          >
            <sc-icon name="external" [size]="16" />
            {{
              opening()
                ? 'Opening...'
                : portalOpened()
                  ? 'Open the portal again'
                  : 'Open Discord Developer Portal'
            }}
          </button>
          <span class="muted">Already have a bot token? Continue straight away.</span>
        </div>

        @if (openFailed()) {
          <div class="alert error-block" role="alert" data-test="portal-failed">
            <p class="alert-title">The Developer Portal did not open.</p>
            <p class="alert-detail">
              Open this address in any browser, then come back and continue:
            </p>
            <div class="address">
              <code class="url">{{ portalUrl }}</code>
              <button type="button" matButton="outlined" data-test="copy-url" (click)="copyUrl()">
                <sc-icon name="copy" [size]="15" />{{ copied() ? 'Copied' : 'Copy address' }}
              </button>
            </div>
          </div>
        }
      </section>

      <sc-wizard-footer
        hint="Continue works whether or not the portal opened here."
        [primary]="portalOpened()"
        (back)="back()"
        (continue)="continueToToken()"
      />
    </main>
  `,
  styles: [
    WIZARD_PAGE_STYLES,
    `
      .steps {
        display: flex;
        flex-direction: column;
        gap: 8px;
        margin: 0;
        padding-left: 20px;
        line-height: 1.5;
        color: var(--sc-fg-2);
      }
      .steps strong {
        color: var(--sc-fg);
      }
      .warn {
        display: flex;
        align-items: flex-start;
        gap: 10px;
        margin: 0;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
        font-size: 13px;
        line-height: 1.45;
      }
      .warn sc-icon {
        margin-top: 1px;
      }
      .portal,
      .address {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
      }
      .address button {
        --mat-button-outlined-container-height: 34px;
      }
      .url {
        padding: 6px 10px;
        border-radius: 8px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        font-family: var(--sc-mono);
        font-size: 13px;
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
  readonly openFailed = signal<boolean>(false);
  readonly copied = signal<boolean>(false);

  ngOnInit(): void {
    this.wizard.goTo('provision');
  }

  async openPortal(): Promise<void> {
    this.opening.set(true);
    this.openFailed.set(false);
    try {
      await this.bridge.appOpenExternal({ url: DEVELOPER_PORTAL_URL });
      this.portalOpened.set(true);
    } catch {
      this.openFailed.set(true);
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
      this.copied.set(false);
    }
  }
}
