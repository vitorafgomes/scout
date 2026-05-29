import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkerEvent, WorkerStatus } from '@scout/shared';

import { ScoutBridgeService } from './scout-bridge';
import { LogStore } from './log.store';

function makeBridgeStub(onWorkerEventImpl?: (listener: (e: WorkerEvent) => void) => () => void) {
  let captured: ((e: WorkerEvent) => void) | null = null;

  const stub = {
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
    discordCancelOAuthInstall: vi.fn().mockResolvedValue(undefined),
    detectClients: vi.fn().mockResolvedValue({ clients: [] }),
    vaultIsAvailable: vi.fn().mockResolvedValue({ available: true }),
    vaultStore: vi.fn().mockResolvedValue(undefined),
    discordStartOAuthInstall: vi.fn(),
    appOpenExternal: vi.fn(),
  } as unknown as ScoutBridgeService;

  return { stub, emit: (e: WorkerEvent) => captured?.(e) };
}

function setup() {
  const { stub, emit } = makeBridgeStub();

  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), { provide: ScoutBridgeService, useValue: stub }],
  });

  const store = TestBed.inject(LogStore);
  return { store, emit };
}

describe('LogStore', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
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

  it('copy() labels lines by detected level, not by stream', () => {
    const { store, emit } = setup();
    const writeText = vi.fn();
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    emit({
      kind: 'stderr',
      line: 'info: Microsoft.Hosting.Lifetime[0]',
      ts: '2026-01-01T00:00:00Z',
    });
    emit({ kind: 'stderr', line: 'fail: Scout.Host.Console[0]', ts: '2026-01-01T00:00:01Z' });

    store.copy();

    expect(writeText).toHaveBeenCalledOnce();
    const copied = writeText.mock.calls[0]?.[0] as string;
    expect(copied).toContain('[INFO] info: Microsoft.Hosting.Lifetime[0]');
    expect(copied).toContain('[ERROR] fail: Scout.Host.Console[0]');
    expect(copied).not.toContain('[stderr]');
    expect(copied).not.toContain('[stdout]');
  });
});
