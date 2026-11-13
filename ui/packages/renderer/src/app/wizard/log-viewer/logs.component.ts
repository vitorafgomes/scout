import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnInit,
  afterRenderEffect,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { RouterLink } from '@angular/router';
import { readIpcError, type WorkerEvent } from '@scout/shared';

import { confirmAction } from '../../core/confirm-dialog.component';
import { ICONS, IconComponent } from '../../core/icon.component';
import { LogStore } from '../../core/log.store';
import { type LogLevel, detectLevels } from '../../core/log-level';
import { NoticeService } from '../../core/notice.service';
import { ScoutBridgeService } from '../../core/scout-bridge';

const LEVELS: readonly LogLevel[] = Object.freeze(['info', 'warn', 'error', 'debug'] as const);

const LEVEL_NAMES: Readonly<Record<LogLevel, string>> = {
  info: 'Info',
  warn: 'Warning',
  error: 'Error',
  debug: 'Debug',
};

const LEVEL_GLYPHS: Readonly<Record<LogLevel, string>> = {
  info: 'M12 8v.5M12 11v5',
  warn: 'M12 3l9 16H3zM12 10v4',
  error: 'M6 6l12 12M18 6L6 18',
  debug: 'M8 6h8M6 10h12M8 14h8M10 18h4',
};

const OK = { label: 'OK', run: () => undefined } as const;

type OutputEvent = Extract<WorkerEvent, { kind: 'stdout' | 'stderr' }>;

const STAMPS = new WeakMap<OutputEvent, Stamp>();

interface Stamp {
  readonly time: string;
  readonly full: string;
}

function lines(n: number): string {
  return `${n} ${n === 1 ? 'line' : 'lines'}`;
}

function two(n: number): string {
  return String(n).padStart(2, '0');
}

/** The local time of day a line was written, `14:02:11.384`, and its whole local date and time; worked out once per line and kept while the line is. */
function stampOf(event: OutputEvent): Stamp {
  let stamp = STAMPS.get(event);
  if (stamp === undefined) {
    const at = new Date(event.ts);
    if (Number.isNaN(at.getTime())) {
      stamp = { time: event.ts, full: event.ts };
    } else {
      const time = `${two(at.getHours())}:${two(at.getMinutes())}:${two(at.getSeconds())}.${String(at.getMilliseconds()).padStart(3, '0')}`;
      stamp = {
        time,
        full: `${at.getFullYear()}-${two(at.getMonth() + 1)}-${two(at.getDate())} ${time}`,
      };
    }
    STAMPS.set(event, stamp);
  }
  return stamp;
}

@Component({
  selector: 'sc-logs',
  standalone: true,
  imports: [IconComponent, MatButtonModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="logs">
      <header class="header">
        <div class="title-block">
          <h1>Logs</h1>
          <p class="lede" data-test="logs-lede">
            What the Scout worker writes while it runs. Lines stay in this window until you clear
            them.
          </p>
        </div>
        <div class="actions">
          <button
            type="button"
            matButton="outlined"
            data-test="toggle-pause"
            [attr.aria-pressed]="paused()"
            (click)="togglePause()"
          >
            <sc-icon [name]="paused() ? 'play' : 'pause'" [size]="16" />{{
              paused() ? 'Resume' : 'Pause'
            }}
          </button>
          <button type="button" matButton="outlined" data-test="copy" (click)="copy()">
            <sc-icon name="copy" [size]="16" />Copy all
          </button>
          <button
            type="button"
            matButton="outlined"
            class="danger"
            data-test="clear"
            (click)="askClear()"
          >
            <sc-icon name="trash" [size]="16" />Clear
          </button>
        </div>
      </header>

      <div class="filters">
        <div class="levels" role="group" aria-label="Show levels">
          @for (level of levels; track level) {
            <button
              type="button"
              class="level"
              [class.level--on]="enabledLevels()[level]"
              [attr.data-level]="level"
              [attr.aria-pressed]="enabledLevels()[level]"
              [attr.data-test]="'filter-' + level"
              (click)="toggleLevel(level)"
            >
              <svg
                width="14"
                height="14"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2.4"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
                focusable="false"
              >
                <path [attr.d]="levelGlyphs[level]" /></svg
              >{{ levelNames[level] }}
              <span class="count" [attr.data-test]="'count-' + level">{{ counts()[level] }}</span>
            </button>
          }
        </div>
        <label class="search">
          <sc-icon name="search" [size]="16" />
          <input
            type="search"
            aria-label="Search the log"
            placeholder="Search the log"
            data-test="search"
            [value]="query()"
            (input)="query.set($any($event.target).value)"
          />
        </label>
      </div>

      <div class="status-row">
        <span class="chip" [attr.data-state]="workerChip().state" data-test="worker-chip"
          ><svg
            width="13"
            height="13"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2.4"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
            focusable="false"
          >
            <path [attr.d]="workerChip().glyph" /></svg
          >{{ workerChip().label }}</span
        >
        <span data-test="follow-line">{{ followLine() }}</span>
        <span class="errors" role="status" aria-live="polite" data-test="error-count">{{
          errorCount()
        }}</span>
      </div>

      @if (visibleLines().length > 0) {
        <div
          #viewer
          class="viewer"
          role="log"
          aria-live="off"
          aria-label="Worker log output"
          (scroll)="onViewerScroll()"
        >
          @for (line of visibleLines(); track line.event) {
            <div class="line" [attr.data-level]="line.level">
              <time
                class="ts"
                [attr.datetime]="line.ts"
                [attr.title]="line.full"
                [attr.aria-description]="line.full"
                >{{ line.time }}</time
              >
              <span class="lvl">{{ line.level.toUpperCase() }}</span>
              <span class="msg">{{ line.text }}</span>
            </div>
          }
        </div>
      } @else {
        <div class="empty">
          @if (outputLines().length > 0) {
            <span class="empty-icon empty-icon--warn"><sc-icon name="funnel" [size]="24" /></span>
            <p data-test="empty-filtered">
              No line matches the search and the levels you chose.
              {{ hiddenLines(outputLines().length) }} hidden.
            </p>
            <button
              type="button"
              matButton="outlined"
              data-test="show-every-line"
              (click)="showEveryLine()"
            >
              <sc-icon name="close" [size]="16" />Show every line
            </button>
          } @else if (workerAlive()) {
            <span class="empty-icon empty-icon--accent"><sc-icon name="list" [size]="24" /></span>
            <p data-test="empty-none">
              Scout is running and has not written anything yet. New lines appear here as it works.
            </p>
            <a matButton="outlined" routerLink="/settings" data-test="open-settings">
              <sc-icon name="settings" [size]="16" />Open Settings
            </a>
          } @else {
            <span class="empty-icon"><sc-icon name="stop" [size]="24" /></span>
            <p data-test="empty-stopped">
              Scout is not running, so it writes nothing. Start it to see its log here.
            </p>
            <button type="button" matButton="filled" data-test="start-worker" (click)="start()">
              <sc-icon name="play" [size]="16" />Start Scout
            </button>
          }
        </div>
      }
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        height: 100%;
        color: var(--sc-fg);
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .logs {
        padding: 24px 32px;
        display: flex;
        flex-direction: column;
        gap: 16px;
        height: 100%;
        width: 100%;
        max-width: 1200px;
        overflow: hidden;
        box-sizing: border-box;
        font-size: 14px;
        --mat-button-outlined-container-height: 36px;
        --mat-button-outlined-horizontal-padding: 14px;
        --mat-button-outlined-label-text-weight: 400;
        --mat-button-outlined-label-text-size: 14px;
        --mat-button-outlined-label-text-tracking: 0;
        --mat-button-filled-container-height: 36px;
        --mat-button-filled-horizontal-padding: 14px;
        --mat-button-filled-label-text-weight: 400;
        --mat-button-filled-label-text-size: 14px;
        --mat-button-filled-label-text-tracking: 0;
      }
      .header {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        gap: 12px;
      }
      .title-block {
        display: flex;
        flex-direction: column;
        gap: 4px;
        margin-right: auto;
      }
      h1 {
        margin: 0;
        font-size: 20px;
        font-weight: 600;
      }
      .lede {
        margin: 0;
        font-size: 14px;
        color: var(--sc-muted);
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      button sc-icon,
      a sc-icon {
        margin-right: 8px;
      }
      .danger {
        --mat-button-outlined-label-text-color: var(--sc-error);
      }
      .filters {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
      }
      .levels {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
      }
      .level {
        height: 32px;
        padding: 0 12px;
        border-radius: 16px;
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-muted);
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font: inherit;
        font-size: 13px;
        cursor: pointer;
      }
      .level--on {
        background: var(--sc-surface-2);
        color: var(--sc-fg);
      }
      .count {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .level--on[data-level='info'] .count {
        color: var(--sc-accent);
      }
      .level--on[data-level='warn'] .count {
        color: var(--sc-on-warn-container);
      }
      .level--on[data-level='error'] .count {
        color: var(--sc-error);
      }
      .search {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 0 12px;
        height: 36px;
        border: 1px solid var(--sc-line);
        border-radius: 18px;
        background: var(--sc-surface);
        color: var(--sc-muted);
        flex: 1 1 220px;
        max-width: 360px;
        margin-left: auto;
      }
      .search input {
        border: 0;
        outline: 0;
        background: transparent;
        color: var(--sc-fg);
        font: inherit;
        width: 100%;
      }
      .search:focus-within {
        outline: 2px solid var(--sc-focus);
        outline-offset: 2px;
      }
      .status-row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        font-size: 13px;
        color: var(--sc-muted);
      }
      .chip {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 3px 10px;
        border-radius: 12px;
        font-size: 12px;
        font-weight: 500;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .chip[data-state='running'] {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .chip[data-state='starting'] {
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
      }
      .errors {
        margin-left: auto;
        color: var(--sc-error);
      }
      .viewer {
        flex: 0 1 auto;
        min-height: 0;
        background: var(--sc-code-bg);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 8px 0;
        overflow-y: auto;
        font-family: var(--sc-mono);
        font-size: 12.5px;
        line-height: 1.55;
      }
      .line {
        display: flex;
        flex-wrap: wrap;
        column-gap: 12px;
        padding: 3px 16px;
      }
      .line[data-level='error'] {
        background: var(--sc-error-container);
      }
      .ts {
        color: var(--sc-muted);
        font-variant-numeric: tabular-nums;
      }
      .lvl {
        flex: 0 0 64px;
        font-weight: 600;
      }
      .line[data-level='info'] .lvl {
        color: var(--sc-accent);
      }
      .line[data-level='warn'] .lvl {
        color: var(--sc-on-warn-container);
      }
      .line[data-level='error'] .lvl {
        color: var(--sc-error);
      }
      .line[data-level='debug'] .lvl {
        color: var(--sc-muted);
      }
      .msg {
        flex: 1 1 360px;
        min-width: 0;
        color: var(--sc-fg-2);
        white-space: pre-wrap;
        word-break: break-word;
        overflow-wrap: anywhere;
      }
      .empty {
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        background: var(--sc-surface);
        padding: 40px 24px;
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 12px;
        text-align: center;
      }
      .empty p {
        margin: 0;
        max-width: 480px;
        color: var(--sc-fg-2);
      }
      .empty-icon {
        width: 48px;
        height: 48px;
        border-radius: 24px;
        background: var(--sc-surface-2);
        color: var(--sc-muted);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .empty-icon--accent {
        color: var(--sc-accent);
      }
      .empty-icon--warn {
        color: var(--sc-on-warn-container);
      }
    `,
  ],
})
export class LogsComponent implements OnInit {
  readonly logStore = inject(LogStore);
  private readonly notice = inject(NoticeService);
  private readonly dialog = inject(MatDialog);
  private readonly bridge = inject(ScoutBridgeService);

  readonly levels: readonly LogLevel[] = LEVELS;
  readonly levelNames = LEVEL_NAMES;
  readonly levelGlyphs = LEVEL_GLYPHS;
  readonly paused = signal<boolean>(false);
  readonly query = signal<string>('');
  private readonly _frozenSnapshot = signal<readonly WorkerEvent[] | null>(null);
  private readonly viewer = viewChild<ElementRef<HTMLElement>>('viewer');
  private atBottom = true;

  readonly enabledLevels = signal<Readonly<Record<LogLevel, boolean>>>({
    info: true,
    warn: true,
    error: true,
    debug: true,
  });

  readonly outputLines = computed<readonly OutputEvent[]>(() =>
    (this._frozenSnapshot() ?? this.logStore.events()).filter(
      (e): e is OutputEvent => e.kind === 'stdout' || e.kind === 'stderr',
    ),
  );

  private readonly classified = computed(() => detectLevels(this.outputLines()));

  /** How many output lines each level holds, before the level switches and the search apply. */
  readonly counts = computed<Readonly<Record<LogLevel, number>>>(() => {
    const counts: Record<LogLevel, number> = { info: 0, warn: 0, error: 0, debug: 0 };
    for (const { level } of this.classified()) counts[level] += 1;
    return counts;
  });

  /** The lines the level switches and the search let through; each row is tracked by its event, which never changes. */
  readonly visibleLines = computed<
    readonly {
      readonly event: WorkerEvent;
      readonly ts: string;
      readonly time: string;
      readonly full: string;
      readonly level: LogLevel;
      readonly text: string;
    }[]
  >(() => {
    const filters = this.enabledLevels();
    const needle = this.query().trim().toLowerCase();
    const out: {
      event: WorkerEvent;
      ts: string;
      time: string;
      full: string;
      level: LogLevel;
      text: string;
    }[] = [];
    for (const { event: ev, level } of this.classified()) {
      if (!filters[level]) continue;
      if (needle !== '' && !ev.line.toLowerCase().includes(needle)) continue;
      out.push({ event: ev, ts: ev.ts, ...stampOf(ev), level, text: ev.line });
    }
    return out;
  });

  /** While the owner was at the newest line, every render that changes the lines keeps the newest line in view; a pause freezes the lines. */
  constructor() {
    afterRenderEffect(() => {
      this.visibleLines();
      const viewer = this.viewer()?.nativeElement;
      if (viewer === undefined || !this.atBottom) return;
      viewer.scrollTop = viewer.scrollHeight;
    });
  }

  /** Running and starting count as alive; idle, stopped and crashed write nothing new. */
  readonly workerAlive = computed<boolean>(() => {
    const state = this.logStore.status().state;
    return state === 'running' || state === 'starting';
  });

  readonly workerChip = computed<{
    readonly state: string;
    readonly label: string;
    readonly glyph: string;
  }>(() => {
    switch (this.logStore.status().state) {
      case 'running':
        return { state: 'running', label: 'Worker running', glyph: 'M20 6L9 17l-5-5' };
      case 'starting':
        return { state: 'starting', label: 'Worker starting', glyph: ICONS.refresh };
      default:
        return { state: 'stopped', label: 'Worker stopped', glyph: ICONS.stop };
    }
  });

  readonly followLine = computed<string>(() => {
    if (!this.workerAlive()) return 'No new lines while the worker is stopped';
    return this.paused()
      ? 'Paused: new lines are kept and shown when you resume'
      : 'Following new lines';
  });

  readonly errorCount = computed<string>(() => {
    const errors = this.counts().error;
    if (errors === 0) return '';
    return `${errors} ${errors === 1 ? 'error' : 'errors'} in this session`;
  });

  /** The log store hears the worker's state only from events after it was created; until one arrives, main's answer seeds it. */
  async ngOnInit(): Promise<void> {
    try {
      const status = await this.bridge.workerStatus();
      if (this.logStore.status().state === 'idle') this.logStore.status.set(status);
    } catch {
      return;
    }
  }

  /** The owner scrolled the viewer: following resumes only once the newest line is in view again. */
  onViewerScroll(): void {
    const viewer = this.viewer()?.nativeElement;
    if (viewer === undefined) return;
    this.atBottom = viewer.scrollHeight - viewer.scrollTop - viewer.clientHeight < 4;
  }

  hiddenLines(total: number): string {
    return `${lines(total)} ${total === 1 ? 'is' : 'are'}`;
  }

  togglePause(): void {
    if (this.paused()) {
      this._frozenSnapshot.set(null);
      this.paused.set(false);
    } else {
      this._frozenSnapshot.set(this.logStore.events().slice());
      this.paused.set(true);
    }
  }

  toggleLevel(level: LogLevel): void {
    this.enabledLevels.update((prev) => ({ ...prev, [level]: !prev[level] }));
  }

  showEveryLine(): void {
    this.query.set('');
    this.enabledLevels.set({ info: true, warn: true, error: true, debug: true });
  }

  /** Copies the lines on screen, as the level switches, the search and a pause leave them, and says how many of the lines held. */
  async copy(): Promise<void> {
    try {
      const shown = this.visibleLines();
      if (shown.length === 0) {
        this.notice.show('Nothing to copy', 'info', undefined, OK);
        return;
      }
      const count = await this.logStore.copy(shown);
      const held = this.outputLines().length;
      this.notice.show(
        count < held ? `Copied ${count} of ${lines(held)}` : `Copied ${lines(count)}`,
        'success',
        undefined,
        OK,
      );
    } catch (err: unknown) {
      const reason = err instanceof Error ? err.message : String(err);
      this.notice.show(`Could not copy the logs: ${reason}`, 'error', undefined, OK);
    }
  }

  /** Clear cannot be undone, so it asks first; Cancel keeps every line. */
  async askClear(): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: 'Clear the log?',
      body: `Removes the ${lines(this.outputLines().length)} from this window. The worker keeps running and new lines keep arriving. It cannot be undone.`,
      confirmLabel: 'Clear',
      confirmIcon: 'trash',
    });
    if (confirmed) this.clear();
  }

  clear(): void {
    this.logStore.clear();
    this._frozenSnapshot.set(null);
    this.paused.set(false);
    this.notice.show('Log cleared', 'info', undefined, OK);
  }

  async start(): Promise<void> {
    try {
      await this.bridge.workerStart();
    } catch (err: unknown) {
      this.notice.show(
        `Could not start Scout: ${readIpcError(err).message}`,
        'error',
        undefined,
        OK,
      );
    }
  }
}
