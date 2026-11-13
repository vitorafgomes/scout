import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import { readIpcError, type WorkerEvent } from '@scout/shared';

import { IconComponent } from '../../core/icon.component';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { LlmConfigStore, providerLabel } from '../../core/llm-config.store';
import { LogStore } from '../../core/log.store';
import { WizardStore } from '../../core/wizard.store';
import { WIZARD_PAGE_STYLES } from '../wizard-footer.component';

type BootState = 'booting' | 'ready' | 'error';

@Component({
  selector: 'sc-worker-launch',
  standalone: true,
  imports: [IconComponent, MatButtonModule, MatProgressBarModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <div class="heading">
        <h1>Start Scout</h1>
        <p class="lede">Scout starts on this computer with the AI Source you connected.</p>
      </div>

      <section class="card">
        <div class="status-row" role="status">
          @switch (bootState()) {
            @case ('booting') {
              <span class="badge badge--starting"
                ><sc-icon name="refresh" [size]="14" />Starting Scout</span
              >
              <span class="muted">This usually takes a few seconds.</span>
            }
            @case ('ready') {
              <span class="badge badge--running"
                ><sc-icon name="check" [size]="14" />Scout is running</span
              >
            }
            @case ('error') {
              <span class="badge badge--crashed"
                ><sc-icon name="close" [size]="14" />Could not start</span
              >
            }
          }
        </div>

        @if (bootState() === 'booting') {
          <mat-progress-bar
            mode="indeterminate"
            aria-label="Starting Scout"
            data-test="launch-progress"
          />
        }

        <dl class="facts" data-test="launch-facts">
          <dt>Chat app</dt>
          <dd>
            Discord
            @if (wizard.guild(); as guild) {
              · server <strong>{{ guild.name }}</strong>
            }
          </dd>
          <dt>AI Source</dt>
          <dd>{{ aiSource() }}</dd>
          <dt>Bot token</dt>
          <dd>Saved, encrypted with this computer's keyring</dd>
        </dl>

        @if (errorMessage(); as msg) {
          <div class="alert" role="alert" data-test="launch-error">
            <p class="alert-title">Scout stopped before it was ready.</p>
            <p class="alert-detail">{{ msg }}</p>
          </div>
        }

        <details class="details" [open]="bootState() === 'error'">
          <summary>Show details</summary>
          @if (errorDetail(); as raw) {
            <p class="raw" data-test="launch-raw">{{ raw }}</p>
          }
          <pre class="log-tail">{{ logTail() }}</pre>
        </details>

        @if (bootState() === 'error') {
          <div class="actions">
            <button type="button" matButton="filled" data-test="retry" (click)="retry()">
              <sc-icon name="refresh" [size]="16" />Retry
            </button>
            <button type="button" matButton="outlined" data-test="cancel" (click)="cancel()">
              Cancel
            </button>
          </div>
        }
      </section>
    </main>
  `,
  styles: [
    WIZARD_PAGE_STYLES,
    `
      .status-row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
      }
      .badge {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 4px 12px;
        border-radius: 14px;
        font-size: 13px;
        font-weight: 600;
      }
      .badge--starting {
        background: var(--sc-surface-2);
        color: var(--sc-accent);
      }
      .badge--running {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .badge--crashed {
        background: var(--sc-error-container);
        color: var(--sc-error);
      }
      mat-progress-bar {
        border-radius: 3px;
      }
      .facts {
        display: grid;
        grid-template-columns: max-content 1fr;
        gap: 10px 20px;
        align-items: baseline;
        margin: 0;
      }
      .facts dt {
        font-size: 13px;
        color: var(--sc-muted);
      }
      .facts dd {
        margin: 0;
      }
      .details {
        padding-top: 12px;
        border-top: 1px solid var(--sc-line);
      }
      .details summary {
        cursor: pointer;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .raw {
        margin: 10px 0 0;
        font-family: var(--sc-mono);
        font-size: 12px;
        line-height: 1.6;
        color: var(--sc-fg-2);
        overflow-wrap: anywhere;
      }
      .log-tail {
        margin: 10px 0 0;
        padding: 12px;
        min-height: 40px;
        max-height: 240px;
        overflow-y: auto;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        color: var(--sc-fg-2);
        font-family: var(--sc-mono);
        font-size: 12px;
        line-height: 1.6;
        white-space: pre-wrap;
        word-break: break-all;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
      }
    `,
  ],
})
export class WorkerLaunchComponent implements OnInit, OnDestroy {
  private readonly router = inject(Router);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly logStore = inject(LogStore);
  readonly wizard = inject(WizardStore);
  readonly llmConfig = inject(LlmConfigStore);

  readonly bootState = signal<BootState>('booting');
  readonly errorMessage = signal<string | null>(null);
  /** What a refused start answered, as main sent it: shown under Show details, never in the failure box. */
  readonly errorDetail = signal<string | null>(null);

  /** The AI Source the worker is given, by its card's name; Ollama while none is chosen, the one the worker falls back to. */
  readonly aiSource = computed<string>(() => {
    const provider = this.llmConfig.provider();
    if (provider === null) return providerLabel('Ollama');
    return provider === 'None' ? 'No AI Source' : providerLabel(provider);
  });

  readonly logTail = computed<string>(() => {
    const events = this.logStore.events();
    const lines = events
      .filter(
        (e): e is Extract<WorkerEvent, { kind: 'stdout' | 'stderr' }> =>
          e.kind === 'stdout' || e.kind === 'stderr',
      )
      .slice(-30)
      .map((e) => e.line);
    return lines.join('\n');
  });

  private unsubscribeStatus: (() => void) | null = null;
  private readyTimer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  ngOnInit(): void {
    this.wizard.goTo('launch');

    if (!this.wizard.hasProvisioning()) {
      void this.router.navigateByUrl('/');
      return;
    }

    void this.llmConfig.loadFromVault();

    this.unsubscribeStatus = this.bridge.onWorkerEvent((event) => {
      if (event.kind === 'state') {
        if (event.status.state === 'running') {
          this.running();
        } else if (event.status.state === 'crashed') {
          this.bootState.set('error');
          this.errorMessage.set(
            'Scout crashed while starting. Its last lines are under Show details.',
          );
        }
      }
    });

    if (this.logStore.status().state === 'running') this.running();
    else void this.startWorker();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    if (this.readyTimer !== null) clearTimeout(this.readyTimer);
    this.readyTimer = null;
    this.unsubscribeStatus?.();
    this.unsubscribeStatus = null;
  }

  /**
   * Shows the worker running and moves on to Ready once, whether the running event or the start's answer comes first;
   * a Launch the shell replaced when it handed over leaves that to the one now shown.
   */
  private running(): void {
    if (this.destroyed || this.bootState() === 'ready') return;
    this.bootState.set('ready');
    this.readyTimer = setTimeout(() => {
      this.readyTimer = null;
      void this.router.navigateByUrl('/ready');
    }, 200);
  }

  private async startWorker(): Promise<void> {
    this.bootState.set('booting');
    this.errorMessage.set(null);
    this.errorDetail.set(null);
    try {
      await this.bridge.workerStart();
      this.running();
    } catch (err: unknown) {
      this.bootState.set('error');
      const { code, message } = readIpcError(err);
      this.errorMessage.set(
        code === null ? 'Scout could not start. The reason is under Show details.' : message,
      );
      this.errorDetail.set(err instanceof Error ? err.message : String(err));
    }
  }

  async retry(): Promise<void> {
    this.bootState.set('booting');
    this.errorMessage.set(null);
    await this.startWorker();
  }

  cancel(): void {
    // Non-destructive: keep the worker running and the wizard state intact.
    // If the user returns to Welcome the Discord card will reflect the real
    // worker status. They can decide to stop/reprovision explicitly later.
    void this.router.navigateByUrl('/');
  }
}
