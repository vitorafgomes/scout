import { Injectable, OnDestroy, inject, signal } from '@angular/core';
import type { WorkerEvent, WorkerStatus } from '@scout/shared';
import { ScoutBridgeService } from './scout-bridge';
import { detectLevels, type LogLevel } from './log-level';

/** One line as the Logs screen shows it, ready to copy. */
export interface CopiedLine {
  readonly ts: string;
  readonly level: LogLevel;
  readonly text: string;
}

const MAX_EVENTS = 2000;
const CLEARED_AT_KEY = 'scout:logs-cleared-at';

function clearedAt(): number {
  return Number(globalThis.localStorage?.getItem(CLEARED_AT_KEY) ?? '-Infinity');
}

function capped(events: readonly WorkerEvent[]): readonly WorkerEvent[] {
  return events.length > MAX_EVENTS ? events.slice(events.length - MAX_EVENTS) : events;
}

function key(event: WorkerEvent): string {
  return JSON.stringify(event);
}

@Injectable({ providedIn: 'root' })
export class LogStore implements OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);

  readonly events = signal<readonly WorkerEvent[]>([]);
  readonly status = signal<WorkerStatus>({ state: 'idle', restarts: 0 });

  private readonly unsubscribe: () => void;

  /**
   * Follows the worker's pushed events, then asks main for the lines written before this store existed, leaving out
   * the ones written before the owner last cleared the log.
   */
  constructor() {
    this.unsubscribe = this.bridge.onWorkerEvent((event) => {
      if (event.kind === 'state') {
        this.status.set(event.status);
      } else {
        this.events.update((prev) => capped([...prev, event]));
      }
    });
    Promise.resolve()
      .then(() => this.bridge.workerLogSnapshot())
      .then((all) => {
        const since = clearedAt();
        const snapshot = all.filter((e) => !('ts' in e && Date.parse(e.ts) <= since));
        const held = new Set(snapshot.map(key));
        this.events.update((live) =>
          capped([...snapshot, ...live.filter((e) => !held.has(key(e)))]),
        );
      })
      .catch(() => undefined);
  }

  /** Empties the lines on screen and, while storage is available, keeps them out of what a reloaded window pulls from main. */
  clear(): void {
    this.events.set([]);
    try {
      globalThis.localStorage?.setItem(CLEARED_AT_KEY, String(Date.now()));
    } catch {
      return;
    }
  }

  appendDiagnostic(line: string, kind: 'stdout' | 'stderr' = 'stderr'): void {
    const event: WorkerEvent = {
      kind,
      line,
      ts: new Date().toISOString(),
    };
    this.events.update((prev) => capped([...prev, event]));
  }

  /** Copies `shown`, or every output line without it, to the clipboard and resolves with how many it copied. */
  async copy(shown?: readonly CopiedLine[]): Promise<number> {
    const lines = (shown ?? this.everyLine()).map(
      ({ ts, level, text }) => `[${ts}] [${level.toUpperCase()}] ${text}`,
    );

    await navigator.clipboard.writeText(lines.join('\n'));
    return lines.length;
  }

  private everyLine(): CopiedLine[] {
    const events = this.events().filter(
      (e): e is Extract<WorkerEvent, { kind: 'stdout' | 'stderr' }> =>
        e.kind === 'stdout' || e.kind === 'stderr',
    );
    return detectLevels(events).map(({ event, level }) => ({
      ts: event.ts,
      level,
      text: event.line,
    }));
  }

  ngOnDestroy(): void {
    this.unsubscribe();
  }
}
