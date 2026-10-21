import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';
import type { WorkerEvent } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { LogStore } from '../../core/log.store';
import { WizardStore } from '../../core/wizard.store';

type BootState = 'booting' | 'ready' | 'error';

// If the worker never reports 'running' or 'crashed' (gateway stall, port race,
// a config that never reaches ready), a bare "booting" screen would strand the
// user with no on-screen control. Fail over to the error state after this bound
// so Retry/Cancel become reachable.
const READY_TIMEOUT_MS = 25_000;

@Component({
  selector: 'sc-worker-launch',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="launch">
      <h1>Starting Scout Worker</h1>

      @if (wizard.guild(); as guild) {
        <p class="guild">
          Server: <strong>{{ guild.name }}</strong>
        </p>
      }

      <div class="env-summary">
        <span class="env-tag">DISCORD_BOT_TOKEN=&#x2022;&#x2022;&#x2022;</span>
        <span class="env-tag">LLM_PROVIDER=Ollama</span>
        <span class="env-tag">ENV=Production</span>
      </div>

      <div class="status-row">
        @switch (bootState()) {
          @case ('booting') {
            <span class="badge badge--starting">starting</span>
          }
          @case ('ready') {
            <span class="badge badge--running">running</span>
          }
          @case ('error') {
            <span class="badge badge--crashed">error</span>
          }
        }
      </div>

      @if (errorMessage(); as msg) {
        <p class="error-msg">{{ msg }}</p>
      }

      <pre class="log-tail">{{ logTail() }}</pre>

      @if (bootState() === 'booting') {
        <div class="actions">
          <button type="button" class="secondary" data-test="cancel-booting" (click)="cancel()">
            Cancel
          </button>
        </div>
      }

      @if (bootState() === 'error') {
        <div class="actions">
          <button type="button" class="primary" data-test="retry" (click)="retry()">Retry</button>
          <button type="button" class="secondary" data-test="cancel" (click)="cancel()">
            Cancel
          </button>
        </div>
      }
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
      .launch {
        padding: 32px;
        max-width: 720px;
        margin: 0 auto;
      }
      h1 {
        font-size: 20px;
        margin: 0 0 12px;
      }
      .guild {
        font-size: 14px;
        margin: 0 0 16px;
        color: var(--sc-text-muted);
      }
      .env-summary {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        margin-bottom: 20px;
      }
      .env-tag {
        background: var(--sc-surface-3);
        border: 1px solid var(--sc-border-strong);
        border-radius: 4px;
        padding: 2px 8px;
        font-size: 12px;
        font-family: monospace;
        color: var(--sc-text-muted);
      }
      .status-row {
        margin-bottom: 16px;
      }
      .badge {
        display: inline-block;
        border-radius: 4px;
        padding: 3px 10px;
        font-size: 12px;
        font-weight: 600;
        letter-spacing: 0.03em;
      }
      .badge--starting {
        background: var(--sc-info-bg);
        color: var(--sc-info);
      }
      .badge--running {
        background: var(--sc-success-bg);
        color: var(--sc-success-text);
      }
      .badge--crashed {
        background: var(--sc-danger-border);
        color: var(--sc-danger-text);
      }
      .error-msg {
        font-size: 13px;
        color: var(--sc-danger-text);
        margin: 0 0 16px;
        background: var(--sc-danger-bg);
        border: 1px solid var(--sc-danger-border);
        border-radius: 6px;
        padding: 10px 14px;
        word-break: break-word;
      }
      .log-tail {
        background: var(--sc-bg-deep);
        border: 1px solid var(--sc-surface-3);
        border-radius: 6px;
        padding: 12px 14px;
        font-family: 'JetBrains Mono', 'Fira Code', monospace;
        font-size: 12px;
        line-height: 1.55;
        color: var(--sc-text-muted);
        white-space: pre-wrap;
        word-break: break-all;
        max-height: 240px;
        overflow-y: auto;
        min-height: 80px;
        margin-bottom: 20px;
      }
      .actions {
        display: flex;
        gap: 10px;
      }
      .primary {
        background: var(--sc-accent);
        color: var(--sc-on-accent);
        border: none;
        border-radius: 6px;
        padding: 10px 16px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .secondary {
        background: transparent;
        color: var(--sc-text-muted);
        border: 1px solid var(--sc-border-strong);
        border-radius: 6px;
        padding: 10px 16px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
    `,
  ],
})
export class WorkerLaunchComponent implements OnInit, OnDestroy {
  private readonly router = inject(Router);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly logStore = inject(LogStore);
  readonly wizard = inject(WizardStore);

  readonly bootState = signal<BootState>('booting');
  readonly errorMessage = signal<string | null>(null);

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

  ngOnInit(): void {
    this.wizard.goTo('launch');

    if (!this.wizard.hasProvisioning()) {
      void this.router.navigateByUrl('/');
      return;
    }

    this.unsubscribeStatus = this.bridge.onWorkerEvent((event) => {
      if (event.kind === 'state') {
        if (event.status.state === 'running') {
          this.clearReadyTimer();
          this.bootState.set('ready');
          setTimeout(() => {
            void this.router.navigateByUrl('/ready');
          }, 200);
        } else if (event.status.state === 'crashed') {
          this.clearReadyTimer();
          this.bootState.set('error');
          this.errorMessage.set('Worker crashed before becoming ready. Check logs below.');
        }
      }
    });

    void this.startWorker();
  }

  ngOnDestroy(): void {
    this.clearReadyTimer();
    this.unsubscribeStatus?.();
    this.unsubscribeStatus = null;
  }

  private async startWorker(): Promise<void> {
    this.bootState.set('booting');
    this.errorMessage.set(null);
    this.armReadyTimer();
    try {
      await this.bridge.workerStart();
    } catch (err: unknown) {
      this.clearReadyTimer();
      this.bootState.set('error');
      const msg = err instanceof Error ? err.message : String(err);
      this.errorMessage.set(msg);
    }
  }

  private armReadyTimer(): void {
    this.clearReadyTimer();
    this.readyTimer = setTimeout(() => {
      this.readyTimer = null;
      if (this.bootState() === 'booting') {
        this.bootState.set('error');
        this.errorMessage.set(
          'Worker did not report ready in time. Check the logs below or retry.',
        );
      }
    }, READY_TIMEOUT_MS);
  }

  private clearReadyTimer(): void {
    if (this.readyTimer !== null) {
      clearTimeout(this.readyTimer);
      this.readyTimer = null;
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
