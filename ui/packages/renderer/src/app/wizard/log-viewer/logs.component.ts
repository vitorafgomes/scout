import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { UpperCasePipe } from '@angular/common';
import type { WorkerEvent } from '@scout/shared';

import { LogStore } from '../../core/log.store';
import { type LogLevel, detectLevel } from '../../core/log-level';

const LEVELS: readonly LogLevel[] = Object.freeze(['info', 'warn', 'error', 'debug'] as const);

// The worker intentionally writes ALL of its logs to stderr so stdout stays
// clean for the SCOUT_READY/SCOUT_HTTP markers. Labelling by stream therefore
// paints healthy startups as errors. Parse the .NET console logger prefix
// (info:/warn:/fail:/crit:/dbug:/trce:) instead; indented continuation lines
// inherit the level of the header line above them.
const DOTNET_LEVEL_PREFIX = /^(trce|dbug|info|warn|fail|crit):/;

const DOTNET_LEVEL_MAP: Readonly<Record<string, LogLevel>> = Object.freeze({
  trce: 'debug',
  dbug: 'debug',
  info: 'info',
  warn: 'warn',
  fail: 'error',
  crit: 'error',
});

export function classifyWorkerLine(
  kind: 'stdout' | 'stderr',
  line: string,
  lastStderrLevel: LogLevel,
): LogLevel {
  if (kind === 'stdout') {
    return detectLevel(line);
  }

  const prefix = DOTNET_LEVEL_PREFIX.exec(line);
  if (prefix?.[1] !== undefined) {
    return DOTNET_LEVEL_MAP[prefix[1]] ?? 'info';
  }
  if (line.startsWith('[scout]')) {
    return 'info';
  }
  if (/^\s/.test(line)) {
    // Continuation of the previous .NET log entry (message body, stack frame).
    return lastStderrLevel;
  }
  // Raw unformatted stderr (e.g. an unhandled-exception dump): keep it loud.
  return 'error';
}

@Component({
  selector: 'sc-logs',
  standalone: true,
  imports: [UpperCasePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="logs">
      <header class="header">
        <h1>Logs</h1>
        <div class="actions">
          <button type="button" class="ghost" data-test="toggle-pause" (click)="togglePause()">
            {{ paused() ? 'Resume' : 'Pause' }}
          </button>
          <button type="button" class="ghost" data-test="copy" (click)="copy()">Copy all</button>
          <button type="button" class="ghost" data-test="clear" (click)="clear()">Clear</button>
        </div>
      </header>

      <div class="filters" role="group" aria-label="Filter by log level">
        @for (level of levels; track level) {
          <label class="filter">
            <input
              type="checkbox"
              [checked]="enabledLevels()[level]"
              [attr.data-test]="'filter-' + level"
              (change)="toggleLevel(level, $event)"
            />
            <span>{{ level }}</span>
          </label>
        }
      </div>

      <div class="viewer" role="log" aria-live="polite" aria-label="Worker log output">
        @if (visibleLines().length === 0) {
          <p class="empty">No log lines match the current filter.</p>
        } @else {
          @for (line of visibleLines(); track line.id) {
            <div class="line" [attr.data-level]="line.level">
              <span class="ts">{{ line.ts }}</span>
              <span class="lvl">{{ line.level | uppercase }}</span>
              <span class="msg">{{ line.text }}</span>
            </div>
          }
        }
      </div>
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
      .logs {
        padding: 24px 32px;
        display: flex;
        flex-direction: column;
        gap: 12px;
        height: calc(100vh - 0px);
        box-sizing: border-box;
      }
      .header {
        display: flex;
        align-items: center;
        justify-content: space-between;
      }
      h1 {
        margin: 0;
        font-size: 20px;
      }
      .actions {
        display: flex;
        gap: 8px;
      }
      .ghost {
        background: transparent;
        color: var(--sc-text);
        border: 1px solid var(--sc-border);
        border-radius: 6px;
        padding: 6px 12px;
        font-size: 13px;
        cursor: pointer;
      }
      .ghost:hover {
        background: var(--sc-surface-3);
      }
      .filters {
        display: flex;
        gap: 12px;
        font-size: 13px;
        color: var(--sc-text-muted);
      }
      .filter {
        display: inline-flex;
        align-items: center;
        gap: 4px;
        text-transform: capitalize;
      }
      .viewer {
        flex: 1;
        background: var(--sc-bg-deep);
        border: 1px solid var(--sc-border);
        border-radius: 6px;
        padding: 12px;
        overflow-y: auto;
        font-family: 'JetBrains Mono', ui-monospace, monospace;
        font-size: 12px;
        line-height: 1.5;
      }
      .line {
        display: grid;
        grid-template-columns: 180px 60px 1fr;
        gap: 8px;
        padding: 1px 0;
      }
      .ts {
        color: var(--sc-text-muted);
      }
      .lvl {
        font-weight: 600;
      }
      .line[data-level='info'] .lvl {
        color: var(--sc-text);
      }
      .line[data-level='warn'] .lvl,
      .line[data-level='warn'] .msg {
        color: var(--sc-warning);
      }
      .line[data-level='error'] .lvl,
      .line[data-level='error'] .msg {
        color: var(--sc-danger);
      }
      .line[data-level='debug'] .lvl,
      .line[data-level='debug'] .msg {
        color: var(--sc-text-muted);
      }
      .msg {
        white-space: pre-wrap;
        word-break: break-all;
      }
      .empty {
        color: var(--sc-text-muted);
        font-size: 13px;
      }
    `,
  ],
})
export class LogsComponent {
  readonly logStore = inject(LogStore);

  readonly levels: readonly LogLevel[] = LEVELS;
  readonly paused = signal<boolean>(false);
  private readonly _frozenSnapshot = signal<readonly WorkerEvent[] | null>(null);

  readonly enabledLevels = signal<Readonly<Record<LogLevel, boolean>>>({
    info: true,
    warn: true,
    error: true,
    debug: true,
  });

  readonly visibleLines = computed<
    readonly {
      readonly id: string;
      readonly ts: string;
      readonly level: LogLevel;
      readonly text: string;
    }[]
  >(() => {
    const source = this._frozenSnapshot() ?? this.logStore.events();
    const filters = this.enabledLevels();
    const out: { id: string; ts: string; level: LogLevel; text: string }[] = [];
    let counter = 0;
    let lastStderrLevel: LogLevel = 'info';
    for (const ev of source) {
      if (ev.kind !== 'stdout' && ev.kind !== 'stderr') continue;
      const level = classifyWorkerLine(ev.kind, ev.line, lastStderrLevel);
      if (ev.kind === 'stderr') lastStderrLevel = level;
      if (!filters[level]) continue;
      out.push({ id: `${ev.ts}-${counter++}`, ts: ev.ts, level, text: ev.line });
    }
    return out;
  });

  togglePause(): void {
    if (this.paused()) {
      this._frozenSnapshot.set(null);
      this.paused.set(false);
    } else {
      this._frozenSnapshot.set(this.logStore.events().slice());
      this.paused.set(true);
    }
  }

  toggleLevel(level: LogLevel, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.enabledLevels.update((prev) => ({ ...prev, [level]: checked }));
  }

  copy(): void {
    this.logStore.copy();
  }

  clear(): void {
    this.logStore.clear();
    this._frozenSnapshot.set(null);
    this.paused.set(false);
  }
}
