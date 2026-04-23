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
        <span class="env-tag">CHAT_ADAPTER=Discord</span>
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
        color: #e4e8f0;
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
        color: #a0aab8;
      }
      .env-summary {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        margin-bottom: 20px;
      }
      .env-tag {
        background: #1e2230;
        border: 1px solid #2d3348;
        border-radius: 4px;
        padding: 2px 8px;
        font-size: 12px;
        font-family: monospace;
        color: #8fa3c0;
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
        background: #1e3a5f;
        color: #5eb3f0;
      }
      .badge--running {
        background: #0f3325;
        color: #38d27a;
      }
      .badge--crashed {
        background: #3d1a1a;
        color: #f07070;
      }
      .error-msg {
        font-size: 13px;
        color: #f07070;
        margin: 0 0 16px;
        background: #1e0f0f;
        border: 1px solid #3d1a1a;
        border-radius: 6px;
        padding: 10px 14px;
        word-break: break-word;
      }
      .log-tail {
        background: #0c0e14;
        border: 1px solid #1e2230;
        border-radius: 6px;
        padding: 12px 14px;
        font-family: 'JetBrains Mono', 'Fira Code', monospace;
        font-size: 12px;
        line-height: 1.55;
        color: #8fa3c0;
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
        background: #7c5cff;
        color: #ffffff;
        border: none;
        border-radius: 6px;
        padding: 10px 16px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .secondary {
        background: transparent;
        color: #a0aab8;
        border: 1px solid #2d3348;
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

  ngOnInit(): void {
    this.wizard.goTo('launch');

    if (this.wizard.guild() === null) {
      void this.router.navigateByUrl('/');
      return;
    }

    this.unsubscribeStatus = this.bridge.onWorkerEvent((event) => {
      if (event.kind === 'state') {
        if (event.status.state === 'running') {
          this.bootState.set('ready');
          setTimeout(() => {
            void this.router.navigateByUrl('/ready');
          }, 200);
        } else if (event.status.state === 'crashed') {
          this.bootState.set('error');
          this.errorMessage.set('Worker crashed before becoming ready. Check logs below.');
        }
      }
    });

    void this.startWorker();
  }

  ngOnDestroy(): void {
    this.unsubscribeStatus?.();
    this.unsubscribeStatus = null;
  }

  private async startWorker(): Promise<void> {
    this.bootState.set('booting');
    this.errorMessage.set(null);
    try {
      await this.bridge.workerStart();
    } catch (err: unknown) {
      this.bootState.set('error');
      const msg = err instanceof Error ? err.message : String(err);
      this.errorMessage.set(msg);
    }
  }

  async retry(): Promise<void> {
    this.bootState.set('booting');
    this.errorMessage.set(null);
    await this.startWorker();
  }

  async cancel(): Promise<void> {
    try {
      await this.bridge.workerStop();
    } catch {
      // best-effort
    }
    await this.wizard.reset();
    void this.router.navigateByUrl('/');
  }
}
