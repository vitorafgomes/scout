import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { WorkerEvent } from '@scout/shared';

import { LogStore } from '../../core/log.store';
import { LogsComponent } from './logs.component';

function makeLogStore() {
  const eventsSig = signal<readonly WorkerEvent[]>([]);
  return {
    events: eventsSig.asReadonly(),
    _set: (events: readonly WorkerEvent[]) => eventsSig.set(events),
    clear: () => {},
    copy: () => {},
  };
}

function stderr(line: string): WorkerEvent {
  return { kind: 'stderr', line, ts: '2026-05-29T16:00:00.000Z' };
}

async function setup(events: readonly WorkerEvent[]) {
  const store = makeLogStore();
  store._set(events);

  TestBed.configureTestingModule({
    imports: [LogsComponent],
    providers: [provideZonelessChangeDetection(), { provide: LogStore, useValue: store }],
  });

  const fixture = TestBed.createComponent(LogsComponent);
  fixture.detectChanges();
  return fixture.componentInstance;
}

describe('LogsComponent level classification', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('classifies info-prefixed stderr lines as info, not error', async () => {
    // The .NET worker host routes every level to stderr by design, so the
    // level must be read from the line content, not from the stream.
    const component = await setup([stderr('info: Microsoft.Hosting.Lifetime[0]')]);

    const levels = component.visibleLines().map((l) => l.level);
    expect(levels).toEqual(['info']);
  });

  it('still classifies fail/crit stderr lines as error', async () => {
    const component = await setup([
      stderr('fail: Scout.Host.Console.ConversationWorker[0]'),
      stderr('crit: Scout.Host.Console.ConversationWorker[0]'),
    ]);

    const levels = component.visibleLines().map((l) => l.level);
    expect(levels).toEqual(['error', 'error']);
  });

  it('classifies warn and debug stderr lines by content', async () => {
    const component = await setup([stderr('warn: some warning'), stderr('dbug: trace details')]);

    const levels = component.visibleLines().map((l) => l.level);
    expect(levels).toEqual(['warn', 'debug']);
  });
});
