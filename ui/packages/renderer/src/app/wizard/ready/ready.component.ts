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

function formatUptime(startedAt: string | undefined): string {
  if (startedAt === undefined) return '—';
  const ms = Date.now() - new Date(startedAt).getTime();
  if (ms < 0) return '0s';
  const totalSeconds = Math.floor(ms / 1000);
  const h = Math.floor(totalSeconds / 3600);
  const m = Math.floor((totalSeconds % 3600) / 60);
  const s = totalSeconds % 60;
  if (h > 0) return `${h}h ${m}m ${s}s`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

@Component({
  selector: 'sc-ready',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="ready">
      <div class="header">
        <h1>Scout Worker</h1>
        <span [class]="statusBadgeClass()">{{ logStore.status().state }}</span>
      </div>

      @if (wizard.guild(); as guild) {
        <p class="guild">
          Server: <strong>{{ guild.name }}</strong>
        </p>
      }

      <div class="meta-row">
        @if (logStore.status().pid; as pid) {
          <span class="meta-item">PID {{ pid }}</span>
        }
        <span class="meta-item">Uptime: {{ uptime() }}</span>
        <span class="meta-item">Restarts: {{ logStore.status().restarts }}</span>
      </div>

      @if (logStore.status().state === 'crashed') {
        <div class="crash-banner">
          Worker crashed.
          @if (logStore.status().lastExitCode !== undefined) {
            Exit code: {{ logStore.status().lastExitCode }}.
          }
          Check logs below.
        </div>
      }

      <div class="log-header">
        <span class="log-label">Logs (last 200 lines)</span>
        <button type="button" class="copy-btn" data-test="copy-logs" (click)="copyLogs()">
          Copy
        </button>
      </div>

      <div class="log-viewer" #logViewer>
        <pre class="log-pre">{{ logTail() }}</pre>
      </div>

      <div class="actions">
        @if (logStore.status().state === 'running') {
          <button type="button" class="secondary" data-test="stop" (click)="stopWorker()">
            Stop
          </button>
          <button type="button" class="primary" data-test="restart" (click)="restartWorker()">
            Restart
          </button>
        } @else if (logStore.status().state === 'stopped') {
          <p class="stopped-msg">Worker stopped.</p>
          <button type="button" class="primary" data-test="start" (click)="startWorker()">
            Start
          </button>
        } @else if (logStore.status().state === 'crashed') {
          <button type="button" class="primary" data-test="restart" (click)="restartWorker()">
            Restart
          </button>
        }
        <button type="button" class="ghost" data-test="back-home" (click)="backHome()">Home</button>
        <button type="button" class="destructive" data-test="reprovision" (click)="reprovision()">
          Reprovision
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
      .ready {
        padding: 32px;
        max-width: 720px;
        margin: 0 auto;
      }
      .header {
        display: flex;
        align-items: center;
        gap: 12px;
        margin-bottom: 8px;
      }
      h1 {
        font-size: 20px;
        margin: 0;
      }
      .guild {
        font-size: 14px;
        margin: 0 0 16px;
        color: #a0aab8;
      }
      .meta-row {
        display: flex;
        gap: 16px;
        flex-wrap: wrap;
        margin-bottom: 16px;
      }
      .meta-item {
        font-size: 13px;
        color: #8fa3c0;
      }
      .badge {
        display: inline-block;
        border-radius: 4px;
        padding: 3px 10px;
        font-size: 12px;
        font-weight: 600;
        letter-spacing: 0.03em;
      }
      .badge--running {
        background: #0f3325;
        color: #38d27a;
      }
      .badge--crashed {
        background: #3d1a1a;
        color: #f07070;
      }
      .badge--stopped {
        background: #1e2230;
        color: #8fa3c0;
      }
      .badge--starting {
        background: #1e3a5f;
        color: #5eb3f0;
      }
      .badge--idle {
        background: #1e2230;
        color: #8fa3c0;
      }
      .crash-banner {
        background: #1e0f0f;
        border: 1px solid #3d1a1a;
        border-radius: 6px;
        padding: 10px 14px;
        font-size: 13px;
        color: #f07070;
        margin-bottom: 16px;
      }
      .log-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        margin-bottom: 6px;
      }
      .log-label {
        font-size: 12px;
        color: #6a7a90;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.05em;
      }
      .copy-btn {
        background: transparent;
        border: 1px solid #2d3348;
        border-radius: 4px;
        color: #8fa3c0;
        font-size: 12px;
        padding: 2px 8px;
        cursor: pointer;
      }
      .log-viewer {
        max-height: 320px;
        overflow-y: auto;
        background: #0c0e14;
        border: 1px solid #1e2230;
        border-radius: 6px;
        margin-bottom: 20px;
      }
      .log-pre {
        margin: 0;
        padding: 12px 14px;
        font-family: 'JetBrains Mono', 'Fira Code', monospace;
        font-size: 12px;
        line-height: 1.55;
        color: #8fa3c0;
        white-space: pre-wrap;
        word-break: break-all;
      }
      .actions {
        display: flex;
        gap: 10px;
        align-items: center;
        flex-wrap: wrap;
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
        color: #e4e8f0;
        border: 1px solid #2d3348;
        border-radius: 6px;
        padding: 10px 16px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .ghost {
        background: transparent;
        color: #6a7a90;
        border: none;
        font-size: 13px;
        cursor: pointer;
        padding: 10px 8px;
      }
      .stopped-msg {
        font-size: 13px;
        color: #8fa3c0;
        margin: 0;
      }
      .destructive {
        background: transparent;
        color: #f07070;
        border: 1px solid #3d1a1a;
        border-radius: 6px;
        padding: 10px 16px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .destructive:hover {
        background: #1e0f0f;
      }
    `,
  ],
})
export class ReadyComponent implements OnInit, OnDestroy {
  private readonly router = inject(Router);
  private readonly bridge = inject(ScoutBridgeService);
  readonly logStore = inject(LogStore);
  readonly wizard = inject(WizardStore);

  readonly uptime = signal<string>('—');

  private uptimeInterval: ReturnType<typeof setInterval> | null = null;
  private unsubscribeStatus: (() => void) | null = null;

  readonly statusBadgeClass = computed<string>(() => {
    const state = this.logStore.status().state;
    return `badge badge--${state}`;
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
    await this.stopWorker();
    await this.startWorker();
  }

  copyLogs(): void {
    this.logStore.copy();
  }

  backHome(): void {
    void this.router.navigateByUrl('/');
  }

  async reprovision(): Promise<void> {
    const confirmed = window.confirm(
      'This will disconnect Scout and forget the current Discord app. Continue?',
    );
    if (!confirmed) return;
    await this.stopWorker();
    await this.wizard.reprovision();
    void this.router.navigateByUrl('/');
  }
}
