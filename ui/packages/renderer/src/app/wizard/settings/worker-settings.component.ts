import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';

import { LogStore } from '../../core/log.store';
import { MenuPreferences } from '../../core/menu-preferences';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { formatUptime } from '../../core/uptime';

/** The Settings section for the Scout worker: its state, Start, Stop, Restart and the Logs entry. */
@Component({
  selector: 'sc-worker-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card" data-test="worker-section" aria-labelledby="worker-title">
      <header class="head">
        <h2 id="worker-title">Scout worker</h2>
        <span [class]="'badge badge--' + logStore.status().state" data-test="worker-state">{{
          logStore.status().state
        }}</span>
      </header>
      <p class="meta">
        @if (logStore.status().pid; as pid) {
          <span data-test="worker-pid">PID {{ pid }}</span>
        }
        <span data-test="worker-uptime">Uptime: {{ uptime() }}</span>
        <span data-test="worker-restarts">Restarts: {{ logStore.status().restarts }}</span>
      </p>
      <div class="actions">
        @if (logStore.status().state === 'running') {
          <button type="button" data-test="worker-stop" (click)="stopWorker()">Stop</button>
          <button type="button" data-test="worker-restart" (click)="restartWorker()">
            Restart
          </button>
        } @else if (logStore.status().state === 'starting') {
          <button type="button" data-test="worker-stop" (click)="stopWorker()">Stop</button>
        } @else {
          <button type="button" data-test="worker-start" (click)="startWorker()">Start</button>
        }
      </div>
      <label class="toggle">
        <input
          type="checkbox"
          data-test="show-logs"
          [checked]="menu.showLogs()"
          (change)="menu.setShowLogs($any($event.target).checked)"
        />
        Show Logs in the menu
      </label>
      <p class="muted" data-test="log-verbosity">Log verbosity: coming soon.</p>
    </section>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .card {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 10px;
        padding: 16px;
      }
      .head {
        display: flex;
        align-items: center;
        gap: 12px;
        margin-bottom: 8px;
      }
      h2 {
        margin: 0;
        font-size: 14px;
        font-weight: 600;
      }
      .meta {
        display: flex;
        gap: 16px;
        flex-wrap: wrap;
        font-size: 13px;
        color: #8fa3c0;
        margin: 0 0 12px;
      }
      .actions {
        display: flex;
        gap: 8px;
        margin-bottom: 12px;
      }
      .toggle {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 13px;
        margin: 0 0 8px;
      }
      .muted {
        color: #7b8299;
        font-size: 13px;
        margin: 0;
      }
      .badge {
        border-radius: 4px;
        padding: 3px 10px;
        font-size: 12px;
        font-weight: 600;
        background: #1e2230;
        color: #8fa3c0;
      }
      .badge--running {
        background: #0f3325;
        color: #38d27a;
      }
      .badge--crashed {
        background: #3d1a1a;
        color: #f07070;
      }
      .badge--starting {
        background: #1e3a5f;
        color: #5eb3f0;
      }
    `,
  ],
})
export class WorkerSettingsComponent {
  readonly logStore = inject(LogStore);
  readonly menu = inject(MenuPreferences);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly now = signal(Date.now());

  readonly uptime = computed<string>(() => {
    this.now();
    return formatUptime(this.logStore.status().startedAt);
  });

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), 1000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  async startWorker(): Promise<void> {
    await this.bridge.workerStart().catch(() => undefined);
  }

  async stopWorker(): Promise<void> {
    await this.bridge.workerStop().catch(() => undefined);
  }

  async restartWorker(): Promise<void> {
    await this.stopWorker();
    await this.startWorker();
  }
}
