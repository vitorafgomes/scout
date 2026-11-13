import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkerEvent, WorkerStatus } from '@scout/shared';

import { ScoutBridgeService } from './scout-bridge';
import { LogStore } from './log.store';

function makeBridgeStub(
  onWorkerEventImpl?: (listener: (e: WorkerEvent) => void) => () => void,
  snapshot: () => Promise<readonly WorkerEvent[]> = async () => [],
) {
  let captured: ((e: WorkerEvent) => void) | null = null;

  const stub = {
    workerLogSnapshot: vi.fn(snapshot),
    onWorkerEvent: vi.fn((listener: (e: WorkerEvent) => void) => {
      captured = listener;
      if (onWorkerEventImpl) return onWorkerEventImpl(listener);
      return () => {
        captured = null;
      };
    }),
    workerStart: vi.fn().mockResolvedValue({ pid: 1 }),
    workerStop: vi.fn().mockResolvedValue({ exitCode: 0 }),
    workerStatus: vi.fn().mockResolvedValue({ state: 'idle', restarts: 0 }),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    detectClients: vi.fn().mockResolvedValue({ clients: [] }),
    vaultIsAvailable: vi.fn().mockResolvedValue({ available: true }),
    vaultStore: vi.fn().mockResolvedValue(undefined),
    appOpenExternal: vi.fn(),
  } as unknown as ScoutBridgeService;

  return { stub, emit: (e: WorkerEvent) => captured?.(e) };
}

function setup(snapshot?: () => Promise<readonly WorkerEvent[]>) {
  const { stub, emit } = makeBridgeStub(undefined, snapshot);

  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), { provide: ScoutBridgeService, useValue: stub }],
  });

  const store = TestBed.inject(LogStore);
  return { store, emit };
}

describe('LogStore', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('a store created after the worker wrote lines shows them, then the lines that arrive after', async () => {
    const early = [
      { kind: 'stderr', line: 'fail: startup', ts: '2026-01-01T00:00:00Z' },
      { kind: 'stdout', line: 'SCOUT_READY', ts: '2026-01-01T00:00:01Z' },
    ] as const;
    let answer: (events: readonly WorkerEvent[]) => void = () => undefined;
    const { store, emit } = setup(
      () =>
        new Promise((resolve) => {
          answer = resolve;
        }),
    );
    await Promise.resolve();

    emit({ kind: 'stdout', line: 'SCOUT_READY', ts: '2026-01-01T00:00:01Z' });
    emit({ kind: 'stdout', line: 'after', ts: '2026-01-01T00:00:02Z' });
    answer(early);
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(store.events().map((e) => (e as { line: string }).line)).toEqual([
      'fail: startup',
      'SCOUT_READY',
      'after',
    ]);
  });

  it('when main cannot answer with the earlier lines, the store still follows the new ones', async () => {
    const { store, emit } = setup(async () => {
      throw new Error('[scout:bridge/not-available] no answer');
    });
    await new Promise((resolve) => setTimeout(resolve, 0));

    emit({ kind: 'stdout', line: 'still here', ts: '2026-01-01T00:00:03Z' });

    expect(store.events().map((e) => (e as { line: string }).line)).toEqual(['still here']);
  });

  it('starts with empty events and idle status', () => {
    const { store } = setup();
    expect(store.events()).toHaveLength(0);
    expect(store.status().state).toBe('idle');
  });

  it('accumulates stdout events', () => {
    const { store, emit } = setup();

    emit({ kind: 'stdout', line: 'line 1', ts: '2026-01-01T00:00:00Z' });
    emit({ kind: 'stdout', line: 'line 2', ts: '2026-01-01T00:00:01Z' });

    expect(store.events()).toHaveLength(2);
    expect(store.events()[0]).toMatchObject({ kind: 'stdout', line: 'line 1' });
  });

  it('accumulates stderr events', () => {
    const { store, emit } = setup();

    emit({ kind: 'stderr', line: 'err line', ts: '2026-01-01T00:00:00Z' });

    expect(store.events()).toHaveLength(1);
    expect(store.events()[0]).toMatchObject({ kind: 'stderr', line: 'err line' });
  });

  it('updates status on state events', () => {
    const { store, emit } = setup();

    const runningStatus: WorkerStatus = {
      state: 'running',
      pid: 77,
      restarts: 0,
      startedAt: '2026-01-01T00:00:00Z',
    };
    emit({ kind: 'state', status: runningStatus });

    expect(store.status().state).toBe('running');
    expect(store.status().pid).toBe(77);
  });

  it('does not add state events to the events array', () => {
    const { store, emit } = setup();

    emit({ kind: 'state', status: { state: 'running', restarts: 0 } });

    expect(store.events()).toHaveLength(0);
  });

  it('clear() resets events to empty', () => {
    const { store, emit } = setup();

    emit({ kind: 'stdout', line: 'hello', ts: '2026-01-01T00:00:00Z' });
    expect(store.events()).toHaveLength(1);

    store.clear();
    expect(store.events()).toHaveLength(0);
  });

  it('clear empties the log and does not throw when storage is unavailable', () => {
    const { store, emit } = setup();
    emit({ kind: 'stdout', line: 'hello', ts: '2026-01-01T00:00:00Z' });
    const denied = vi.spyOn(globalThis, 'localStorage', 'get').mockImplementation(() => {
      throw new DOMException('The operation is insecure.', 'SecurityError');
    });

    let thrown: unknown = null;
    try {
      store.clear();
    } catch (err: unknown) {
      thrown = err;
    }
    denied.mockRestore();

    expect(thrown).toBeNull();
    expect(store.events()).toEqual([]);
  });

  it('after Clear, a reloaded window pulling the earlier lines from main shows none written before the Clear, only the ones after it', async () => {
    const first = setup();
    first.emit({ kind: 'stdout', line: 'before clear', ts: '2026-01-01T00:00:04Z' });
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(Date.parse('2026-01-01T00:00:05Z'));
    first.store.clear();
    vi.useRealTimers();
    TestBed.resetTestingModule();

    const { store } = setup(async () => [
      { kind: 'stdout', line: 'before clear', ts: '2026-01-01T00:00:04Z' },
      { kind: 'stderr', line: 'as clear ran', ts: '2026-01-01T00:00:05.000Z' },
      { kind: 'stdout', line: 'after clear', ts: '2026-01-01T00:00:05.500Z' },
    ]);
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(store.events().map((e) => (e as { line: string }).line)).toEqual(['after clear']);
  });

  it('clear() does not reset status', () => {
    const { store, emit } = setup();

    emit({ kind: 'state', status: { state: 'running', restarts: 1 } });
    store.clear();

    expect(store.status().state).toBe('running');
  });

  it('caps events at 2000 entries', () => {
    const { store, emit } = setup();

    for (let i = 0; i < 2050; i++) {
      emit({ kind: 'stdout', line: `line ${i}`, ts: '2026-01-01T00:00:00Z' });
    }

    expect(store.events().length).toBeLessThanOrEqual(2000);
  });

  it('keeps the most recent events when over capacity', () => {
    const { store, emit } = setup();

    for (let i = 0; i < 2050; i++) {
      emit({ kind: 'stdout', line: `line-${i}`, ts: '2026-01-01T00:00:00Z' });
    }

    const lines = store.events().map((e) => (e as { line: string }).line);
    expect(lines).toContain('line-2049');
    expect(lines).not.toContain('line-0');
  });

  it('handles multiple state transitions correctly', () => {
    const { store, emit } = setup();

    emit({ kind: 'state', status: { state: 'starting', restarts: 0 } });
    emit({ kind: 'state', status: { state: 'running', restarts: 0, pid: 5 } });
    emit({ kind: 'state', status: { state: 'crashed', restarts: 1 } });

    expect(store.status().state).toBe('crashed');
    expect(store.status().restarts).toBe(1);
  });

  it('copy() labels lines by detected level, not by stream, and resolves with the number of lines copied', async () => {
    const { store, emit } = setup();
    const writeText = vi.fn();
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    emit({
      kind: 'stderr',
      line: 'info: Microsoft.Hosting.Lifetime[0]',
      ts: '2026-01-01T00:00:00Z',
    });
    emit({ kind: 'stderr', line: 'fail: Scout.Host.Console[0]', ts: '2026-01-01T00:00:01Z' });

    const copiedCount = await store.copy();

    expect(copiedCount).toBe(2);
    expect(writeText).toHaveBeenCalledOnce();
    const copied = writeText.mock.calls[0]?.[0] as string;
    expect(copied).toContain('[INFO] info: Microsoft.Hosting.Lifetime[0]');
    expect(copied).toContain('[ERROR] fail: Scout.Host.Console[0]');
    expect(copied).not.toContain('[stderr]');
    expect(copied).not.toContain('[stdout]');
  });

  it('copy() rejects with the clipboard error when the clipboard refuses the write', async () => {
    const { store, emit } = setup();
    const writeText = vi.fn().mockRejectedValue(new Error('Write permission denied'));
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    emit({ kind: 'stderr', line: 'info: one', ts: '2026-01-01T00:00:00Z' });

    await expect(store.copy()).rejects.toThrow('Write permission denied');
  });
});
