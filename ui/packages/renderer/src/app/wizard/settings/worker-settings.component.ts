import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  signal,
} from '@angular/core';

import { readIpcError, RESTART_DEFERRED_LINE, type WorkerStatus } from '@scout/shared';

import { MatSlideToggleModule } from '@angular/material/slide-toggle';

import { IconComponent } from '../../core/icon.component';
import { LogStore } from '../../core/log.store';
import { MenuPreferences } from '../../core/menu-preferences';
import { NoticeService } from '../../core/notice.service';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { StatusChipComponent, type ChipTone } from '../../core/status-chip.component';
import { SETTINGS_SWITCH_STYLES } from './settings-switch.styles';

type WorkerState = WorkerStatus['state'];

const STOP_FAILED_NOTICE = 'Could not stop the Scout worker. Try again.';

const WORKER_CHIPS: Readonly<
  Record<WorkerState, { readonly tone: ChipTone; readonly label: string }>
> = {
  idle: { tone: 'neutral', label: 'Not started' },
  starting: { tone: 'info', label: 'Starting' },
  running: { tone: 'ok', label: 'Running' },
  crashed: { tone: 'error', label: 'Stopped unexpectedly' },
  stopped: { tone: 'neutral', label: 'Stopped' },
};

/** How long the worker has run, as the Settings artboard writes it: `5 s` under a minute, `14 min` under an hour, then `2 h 14 min`. */
export function settingsUptime(startedAt: string | undefined, now: number): string {
  if (startedAt === undefined) return '—';
  const seconds = Math.max(0, Math.floor((now - Date.parse(startedAt)) / 1000));
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} min`;
  return `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

/** The Settings section for the Scout worker: its state, Start, Stop, Restart and the Logs entry. */
@Component({
  selector: 'sc-worker-settings',
  standalone: true,
  imports: [IconComponent, MatSlideToggleModule, StatusChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card" data-test="worker-section" aria-labelledby="worker-title">
      <header class="head">
        <h2 id="worker-title">Scout worker</h2>
        <sc-status-chip data-test="worker-state" [tone]="chip().tone" [label]="chip().label" />
      </header>
      <p class="meta">
        @if (logStore.status().pid; as pid) {
          <span data-test="worker-pid">PID {{ pid }}</span> ·
        }
        <span data-test="worker-uptime">Uptime {{ uptime() }}</span> ·
        <span data-test="worker-restarts">Restarts {{ logStore.status().restarts }}</span>
      </p>
      <div class="actions">
        @if (logStore.status().state === 'running') {
          <button type="button" class="btn" data-test="worker-stop" (click)="stopWorker()">
            <sc-icon name="stop" [size]="16" />
            Stop
          </button>
          <button type="button" class="btn" data-test="worker-restart" (click)="restartWorker()">
            <sc-icon name="refresh" [size]="16" />
            Restart
          </button>
        } @else if (logStore.status().state === 'starting') {
          <button type="button" class="btn" data-test="worker-stop" (click)="stopWorker()">
            <sc-icon name="stop" [size]="16" />
            Stop
          </button>
        } @else {
          <button
            type="button"
            class="btn btn--primary"
            data-test="worker-start"
            (click)="startWorker()"
          >
            <sc-icon name="play" [size]="16" />
            Start
          </button>
        }
      </div>
      @if (restartNote(); as note) {
        <p class="muted" role="status" data-test="worker-restart-note">{{ note }}</p>
      }
      @if (error(); as message) {
        <p class="error" role="alert" data-test="worker-error">
          <sc-icon name="alert" [size]="15" />{{ message }}
        </p>
      }
      <div class="switch-row logs-row">
        <span id="lbl-logs" class="switch-label">Show Logs in the menu</span>
        <mat-slide-toggle
          data-test="show-logs"
          aria-labelledby="lbl-logs"
          hideIcon
          [checked]="menu.showLogs()"
          (change)="menu.setShowLogs($event.checked)"
        />
      </div>
      <p class="muted" data-test="log-verbosity">Log verbosity: coming soon.</p>
    </section>
  `,
  styles: [
    SETTINGS_SWITCH_STYLES,
    `
      :host {
        display: block;
      }
      .card {
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 16px;
        color: var(--sc-fg);
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .head {
        display: flex;
        align-items: center;
        gap: 8px;
      }
      h2 {
        margin: 0;
        margin-right: auto;
        font-size: 15px;
        font-weight: 600;
      }
      .meta {
        font-size: 13px;
        color: var(--sc-muted);
        margin: 0;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      .btn {
        height: 40px;
        padding: 0 16px;
        border-radius: 20px;
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg);
        display: inline-flex;
        align-items: center;
        gap: 8px;
        font: inherit;
        font-weight: 500;
        cursor: pointer;
      }
      .btn--primary {
        border-color: transparent;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
      }
      .logs-row {
        padding-top: 12px;
        border-top: 1px solid var(--sc-line);
      }
      .muted {
        color: var(--sc-muted);
        font-size: 13px;
        margin: 0;
      }
      .error {
        display: flex;
        align-items: center;
        gap: 6px;
        color: var(--sc-error);
        font-size: 13px;
        margin: 0;
      }
    `,
  ],
})
export class WorkerSettingsComponent {
  readonly logStore = inject(LogStore);
  readonly menu = inject(MenuPreferences);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly notice = inject(NoticeService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly now = signal(Date.now());
  /** Main's answer to a Restart that did not restart at once: deferred, or failed and why. */
  readonly restartNote = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  /** The worker's state as the owner reads it: a sentence-case label on its tone, never the raw state. */
  readonly chip = computed(() => WORKER_CHIPS[this.logStore.status().state]);

  readonly uptime = computed<string>(() => {
    return settingsUptime(this.logStore.status().startedAt, this.now());
  });

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), 1000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  async startWorker(): Promise<void> {
    this.error.set(null);
    this.restartNote.set(null);
    await this.bridge.workerStart().catch((err: unknown) => {
      this.error.set(`Scout did not start: ${readIpcError(err).message}`);
    });
  }

  async stopWorker(): Promise<void> {
    this.error.set(null);
    this.restartNote.set(null);
    await this.bridge.workerStop().catch((err: unknown) => {
      this.error.set(
        `Scout could not stop the worker. It is still running. ${readIpcError(err).message}`,
      );
      this.notice.show(STOP_FAILED_NOTICE, 'error', 8000, {
        label: 'Retry',
        run: () => {
          this.host.nativeElement.querySelector<HTMLElement>('[data-test="worker-stop"]')?.focus();
          void this.stopWorker();
        },
      });
    });
  }

  async restartWorker(): Promise<void> {
    this.error.set(null);
    this.restartNote.set(null);
    const result = await this.bridge
      .workerRestart({ reason: 'Settings, Restart' })
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
}
