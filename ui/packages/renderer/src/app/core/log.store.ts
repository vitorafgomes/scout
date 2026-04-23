import { Injectable, OnDestroy, inject, signal } from '@angular/core';
import type { WorkerEvent, WorkerStatus } from '@scout/shared';
import { ScoutBridgeService } from './scout-bridge';

const MAX_EVENTS = 2000;

@Injectable({ providedIn: 'root' })
export class LogStore implements OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);

  readonly events = signal<readonly WorkerEvent[]>([]);
  readonly status = signal<WorkerStatus>({ state: 'idle', restarts: 0 });

  private readonly unsubscribe: () => void;

  constructor() {
    this.unsubscribe = this.bridge.onWorkerEvent((event) => {
      if (event.kind === 'state') {
        this.status.set(event.status);
      } else {
        this.events.update((prev) => {
          const next = [...prev, event];
          return next.length > MAX_EVENTS ? next.slice(next.length - MAX_EVENTS) : next;
        });
      }
    });
  }

  clear(): void {
    this.events.set([]);
  }

  copy(): void {
    const lines = this.events()
      .filter(
        (e): e is Extract<WorkerEvent, { kind: 'stdout' | 'stderr' }> =>
          e.kind === 'stdout' || e.kind === 'stderr',
      )
      .map((e) => `[${e.ts}] [${e.kind}] ${e.line}`)
      .join('\n');

    void navigator.clipboard.writeText(lines);
  }

  ngOnDestroy(): void {
    this.unsubscribe();
  }
}
