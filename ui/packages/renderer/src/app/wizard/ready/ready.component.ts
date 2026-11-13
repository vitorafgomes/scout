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
import { MatDialog } from '@angular/material/dialog';
import { Router } from '@angular/router';
import {
  readIpcError,
  RESTART_DEFERRED_LINE,
  type WorkerEvent,
  type WorkerStatus,
} from '@scout/shared';

import { confirmAction } from '../../core/confirm-dialog.component';
import { IconComponent, type IconName } from '../../core/icon.component';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { LlmConfigStore, providerLabel } from '../../core/llm-config.store';
import { LogStore } from '../../core/log.store';
import { NoticeService } from '../../core/notice.service';
import { WizardStore } from '../../core/wizard.store';
import { formatUptime } from '../../core/uptime';
import { WIZARD_PAGE_STYLES } from '../wizard-footer.component';

const STATE_CHIPS: Readonly<
  Record<WorkerStatus['state'], { readonly label: string; readonly icon: IconName }>
> = Object.freeze({
  running: { label: 'Running', icon: 'check' },
  starting: { label: 'Starting', icon: 'refresh' },
  stopped: { label: 'Stopped', icon: 'stop' },
  crashed: { label: 'Crashed', icon: 'close' },
  idle: { label: 'Idle', icon: 'pause' },
});

@Component({
  selector: 'sc-ready',
  standalone: true,
  imports: [IconComponent, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <div class="heading">
        <h1>You are all set</h1>
        <p class="lede">Mention &#64;Scout in your server, or chat with it in this window.</p>
      </div>

      <section class="complete" data-test="setup-complete">
        <span class="complete-mark" aria-hidden="true"><sc-icon name="check" [size]="22" /></span>
        <div class="complete-text">
          <span class="complete-title">Setup is complete</span>
          <span class="complete-detail" data-test="setup-summary">{{ summary() }}</span>
        </div>
      </section>

      @if (!llmConfig.isConfigured()) {
        <div class="llm-banner" role="status" data-test="no-llm-banner">
          <span>No LLM model selected. Pick one to enable Scout's responses.</span>
          <button type="button" matButton="filled" data-test="choose-llm" (click)="goToLlm()">
            <sc-icon name="cube" [size]="16" />
            Choose model
          </button>
        </div>
      }

      @if (logStore.status().state === 'crashed') {
        <div class="alert crash-banner" role="alert">
          <p class="alert-title">Scout crashed.</p>
          <p class="alert-detail">
            @if (logStore.status().lastExitCode !== undefined) {
              Exit code: {{ logStore.status().lastExitCode }}.
            }
            Check the logs under Scout process and start over.
          </p>
        </div>
      }

      <details
        #process
        class="process"
        data-test="process"
        [open]="processOpen() ?? logStore.status().state !== 'running'"
      >
        <summary (click)="processOpen.set(!process.open)">Scout process and start over</summary>
        <div class="process-body">
          <div class="process-row">
            <span [class]="statusBadgeClass()" data-test="worker-chip"
              ><sc-icon [name]="chip().icon" [size]="12" />{{ chip().label }}</span
            >
            <span class="muted meta">
              @if (logStore.status().pid; as pid) {
                PID {{ pid }} ·
              }
              Uptime {{ uptime() }} · Restarts {{ logStore.status().restarts }}
            </span>
            @if (logStore.status().state === 'running') {
              <button type="button" matButton="outlined" data-test="stop" (click)="stopWorker()">
                <sc-icon name="stop" [size]="16" />
                Stop
              </button>
              <button
                type="button"
                matButton="outlined"
                data-test="restart"
                (click)="restartWorker()"
              >
                <sc-icon name="refresh" [size]="16" />
                Restart
              </button>
            } @else if (logStore.status().state === 'stopped') {
              <button type="button" matButton="filled" data-test="start" (click)="startWorker()">
                <sc-icon name="play" [size]="16" />
                Start
              </button>
            } @else if (logStore.status().state === 'crashed') {
              <button
                type="button"
                matButton="filled"
                data-test="restart"
                (click)="restartWorker()"
              >
                <sc-icon name="refresh" [size]="16" />
                Restart
              </button>
            }
          </div>
          @if (restartNote(); as note) {
            <p class="muted" role="status" data-test="restart-note">{{ note }}</p>
          }

          <div class="log-header">
            <span class="log-label">Logs (last 200 lines)</span>
            <button type="button" matButton="outlined" data-test="copy-logs" (click)="copyLogs()">
              <sc-icon name="copy" [size]="15" />Copy
            </button>
          </div>
          <div class="log-viewer" #logViewer>
            <pre class="log-pre">{{ logTail() }}</pre>
          </div>

          <div class="start-over">
            <div class="start-over-text">
              <span class="start-over-title">Start over with another bot</span>
              <span class="muted">Removes this bot's token from Scout and runs setup again.</span>
            </div>
            <button
              type="button"
              matButton="outlined"
              class="danger"
              data-test="reprovision"
              (click)="reprovision()"
            >
              <sc-icon name="trash" [size]="16" />
              Start over
            </button>
          </div>
        </div>
      </details>

      <footer class="footer">
        <button type="button" matButton="outlined" data-test="back-home" (click)="backHome()">
          Home
        </button>
      </footer>
    </main>
  `,
  styles: [
    WIZARD_PAGE_STYLES,
    `
      .complete {
        display: flex;
        align-items: center;
        gap: 14px;
        padding: 18px 20px;
        border-radius: 14px;
        background: var(--sc-ok-container);
      }
      .complete-mark {
        flex: 0 0 40px;
        display: flex;
        align-items: center;
        justify-content: center;
        width: 40px;
        height: 40px;
        border-radius: 20px;
        background: var(--sc-on-ok-container);
        color: var(--sc-ok-container);
      }
      .complete-text {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .complete-title {
        font-size: 16px;
        font-weight: 600;
        color: var(--sc-on-ok-container);
      }
      .complete-detail {
        color: var(--sc-fg-2);
      }
      .llm-banner {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
        font-size: 13px;
      }
      .process {
        padding: 12px 16px;
        border: 1px solid var(--sc-line);
        border-radius: 14px;
        background: var(--sc-surface);
      }
      .process summary {
        cursor: pointer;
        color: var(--sc-fg-2);
      }
      .process-body {
        display: flex;
        flex-direction: column;
        gap: 12px;
        padding-top: 12px;
      }
      .process-row,
      .start-over {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
      }
      .process-row button,
      .start-over button,
      .log-header button {
        --mat-button-outlined-container-height: 36px;
        --mat-button-filled-container-height: 36px;
      }
      .meta {
        margin-right: auto;
      }
      .badge {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 3px 10px;
        border-radius: 12px;
        font-size: 12px;
      }
      .badge--running {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .badge--crashed {
        background: var(--sc-error-container);
        color: var(--sc-error);
      }
      .badge--stopped,
      .badge--idle {
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .badge--starting {
        background: var(--sc-surface-2);
        color: var(--sc-accent);
      }
      .log-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
      }
      .log-label {
        font-size: 13px;
        color: var(--sc-muted);
      }
      .log-viewer {
        max-height: 320px;
        overflow-y: auto;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
      }
      .log-pre {
        margin: 0;
        padding: 12px;
        color: var(--sc-fg-2);
        font-family: var(--sc-mono);
        font-size: 12px;
        line-height: 1.6;
        white-space: pre-wrap;
        word-break: break-all;
      }
      .start-over {
        padding: 12px;
        border-radius: 10px;
        border: 1px solid var(--sc-error-container);
      }
      .start-over-text {
        display: flex;
        flex-direction: column;
        gap: 2px;
        margin-right: auto;
      }
      .start-over-title {
        font-weight: 600;
      }
      .footer {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        margin-top: 4px;
        padding-top: 18px;
        border-top: 1px solid var(--sc-line);
      }
    `,
  ],
})
export class ReadyComponent implements OnInit, OnDestroy {
  private readonly router = inject(Router);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly dialog = inject(MatDialog);
  private readonly notice = inject(NoticeService);
  readonly logStore = inject(LogStore);
  readonly wizard = inject(WizardStore);
  readonly llmConfig = inject(LlmConfigStore);

  readonly uptime = signal<string>('—');
  /** Main's answer to a Restart that did not restart at once: deferred, or failed and why. */
  readonly restartNote = signal<string | null>(null);
  /** The owner's own choice for the process section, open or closed; null until they toggle it, and until then it follows the worker. */
  readonly processOpen = signal<boolean | null>(null);

  private uptimeInterval: ReturnType<typeof setInterval> | null = null;
  private unsubscribeStatus: (() => void) | null = null;

  readonly statusBadgeClass = computed<string>(() => {
    const state = this.logStore.status().state;
    return `badge badge--${state}`;
  });

  /** The worker's state as its chip reads it: a sentence-case word and its glyph. */
  readonly chip = computed(() => STATE_CHIPS[this.logStore.status().state]);

  /** The line under "Setup is complete": whether Scout runs, the server it joined and, once a model is chosen, the AI Source it answers with. */
  readonly summary = computed<string>(() => {
    const running = this.logStore.status().state === 'running';
    const guild = this.wizard.guild();
    const provider = this.llmConfig.provider();
    const model = this.llmConfig.model();
    const where = guild === null ? '' : ` in ${guild.name}`;
    const source =
      !running || provider === null || provider === 'None' || model === null
        ? ''
        : ` and answers with ${providerLabel(provider)} · ${model}`;
    return `Scout ${running ? 'is running' : 'is set up'}${where}${source}.`;
  });

  readonly logTail = computed<string>(() => {
    const events = this.logStore.events();
    const lines = events
      .filter(
        (e): e is Extract<WorkerEvent, { kind: 'stdout' | 'stderr' }> =>
          e.kind === 'stdout' || e.kind === 'stderr',
      )
      .slice(-200)
      .map((e) => e.line);
    return lines.join('\n');
  });

  ngOnInit(): void {
    this.wizard.goTo('ready');
    void this.llmConfig.loadFromVault();

    this.uptimeInterval = setInterval(() => {
      this.uptime.set(formatUptime(this.logStore.status().startedAt));
    }, 1000);

    this.unsubscribeStatus = this.bridge.onWorkerEvent((event) => {
      if (event.kind === 'state') {
        this.uptime.set(formatUptime(event.status.startedAt));
      }
    });
  }

  ngOnDestroy(): void {
    if (this.uptimeInterval !== null) {
      clearInterval(this.uptimeInterval);
      this.uptimeInterval = null;
    }
    this.unsubscribeStatus?.();
    this.unsubscribeStatus = null;
  }

  async stopWorker(): Promise<void> {
    try {
      await this.bridge.workerStop();
    } catch {
      // status badge will reflect the state change via event
    }
  }

  async startWorker(): Promise<void> {
    try {
      await this.bridge.workerStart();
    } catch {
      // status badge will reflect crashed state via event
    }
  }

  async restartWorker(): Promise<void> {
    this.restartNote.set(null);
    if (this.logStore.status().state !== 'running') {
      await this.startWorker();
      return;
    }
    const result = await this.bridge
      .workerRestart({ reason: 'Ready, Restart' })
      .catch((err: unknown) => ({
        outcome: 'failed' as const,
        reason: err instanceof Error ? err.message : 'Could not restart Scout.',
      }));
    this.restartNote.set(
      result.outcome === 'deferred'
        ? RESTART_DEFERRED_LINE
        : result.outcome === 'failed'
          ? `Scout could not restart: ${result.reason}`
          : null,
    );
  }

  copyLogs(): void {
    void this.logStore.copy();
  }

  backHome(): void {
    void this.router.navigateByUrl('/');
  }

  goToLlm(): void {
    void this.router.navigateByUrl('/llm');
  }

  /**
   * Forgets Discord only, once the owner confirmed: with Slack or this computer still in use the worker restarts
   * through main's restart path, so it stops answering in Discord and keeps answering there; with nothing left it stops.
   */
  async reprovision(): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: 'Start over with another bot?',
      body: 'The bot token is removed from the keyring and setup starts again from the first step. Scout stops unless Slack or this computer still use it. The bot stays in your Discord server until you remove it there.',
      confirmLabel: 'Start over',
    });
    if (!confirmed) return;
    const state = this.logStore.status().state;
    const wasRunning = state === 'running' || state === 'starting';
    try {
      await this.wizard.reprovision();
    } catch (err: unknown) {
      this.notice.show(
        `Scout could not remove the bot token: ${readIpcError(err).message}`,
        'error',
      );
      return;
    }
    if (!this.wizard.hasProvisioning()) {
      if (wasRunning) await this.stopWorker();
      void this.router.navigateByUrl('/');
      return;
    }
    const result = await this.bridge
      .workerRestart({ reason: 'Discord disconnected' })
      .catch((err: unknown) => ({
        outcome: 'failed' as const,
        reason: err instanceof Error ? err.message : 'Could not restart Scout.',
      }));
    if (result.outcome === 'deferred') this.notice.show(RESTART_DEFERRED_LINE);
    else if (result.outcome === 'failed') {
      this.notice.show(`Scout could not restart: ${result.reason}`, 'error');
    }
    void this.router.navigateByUrl('/');
  }
}
