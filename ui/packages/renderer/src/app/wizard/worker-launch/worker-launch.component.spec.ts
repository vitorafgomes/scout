import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkerEvent, WorkerStatus } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { LogStore } from '../../core/log.store';
import { WizardStore } from '../../core/wizard.store';
import { WorkerLaunchComponent } from './worker-launch.component';

type WorkerEventListener = (e: WorkerEvent) => void;

function makeRunningStatus(): WorkerStatus {
  return { state: 'running', restarts: 0 };
}

function makeCrashedStatus(): WorkerStatus {
  return { state: 'crashed', restarts: 1 };
}

interface BridgeFactory {
  workerStartImpl?: () => Promise<{ pid: number }>;
  onWorkerEventImpl?: (listener: WorkerEventListener) => () => void;
}

function makeBridge(opts: BridgeFactory = {}) {
  let capturedListener: WorkerEventListener | null = null;

  const startImpl = opts.workerStartImpl ?? (() => Promise.resolve({ pid: 42 }));
  const eventImpl = opts.onWorkerEventImpl;

  const bridge = {
    workerStart: vi.fn(startImpl),
    workerStop: vi.fn().mockResolvedValue({ exitCode: 0 }),
    workerStatus: vi.fn().mockResolvedValue({ state: 'idle', restarts: 0 }),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    onWorkerEvent: vi.fn((listener: WorkerEventListener) => {
      capturedListener = listener;
      if (eventImpl !== undefined) return eventImpl(listener);
      return () => {
        capturedListener = null;
      };
    }),
  } as unknown as ScoutBridgeService;

  return {
    bridge,
    fire: (e: WorkerEvent) => {
      if (capturedListener !== null) capturedListener(e);
    },
  };
}

function makeLogStore(): LogStore {
  return {
    events: () => [],
    status: () => ({ state: 'idle', restarts: 0 }),
    clear: vi.fn(),
    copy: vi.fn(),
  } as unknown as LogStore;
}

async function setup(
  opts: {
    guildName?: string;
    clientId?: string;
    bridgeOpts?: BridgeFactory;
  } = {},
) {
  const { bridge, fire } = makeBridge(opts.bridgeOpts ?? {});

  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: '', children: [] },
        { path: 'ready', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: LogStore, useValue: makeLogStore() },
    ],
  });

  const store = TestBed.inject(WizardStore);
  if (opts.clientId !== undefined) store.setClientId(opts.clientId);
  if (opts.guildName !== undefined) store.setGuild({ id: 'g1', name: opts.guildName });

  const fixture = TestBed.createComponent(WorkerLaunchComponent);
  fixture.detectChanges();
  await fixture.whenStable();

  return { fixture, bridge, fire, store };
}

describe('WorkerLaunchComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('calls workerStart on init', async () => {
    const { bridge } = await setup({ guildName: 'Scout Server', clientId: 'cid' });
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('renders the guild name', async () => {
    const { fixture } = await setup({ guildName: 'My Guild', clientId: 'cid' });
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('My Guild');
  });

  it('shows env summary tags', async () => {
    const { fixture } = await setup({ guildName: 'G', clientId: 'c' });
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('CHAT_ADAPTER=Discord');
    expect(text).toContain('LLM_PROVIDER=Ollama');
  });

  it('redirects to / when no guild is in the store', async () => {
    const { bridge } = makeBridge();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '', children: [] }]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: LogStore, useValue: makeLogStore() },
      ],
    });
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');
    const fixture = TestBed.createComponent(WorkerLaunchComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(nav).toHaveBeenCalledWith('/');
  });

  it('navigates to /ready after running state event (200ms delay)', async () => {
    vi.useFakeTimers();
    try {
      const { bridge, fire } = makeBridge();
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          provideRouter([
            { path: '', children: [] },
            { path: 'ready', children: [] },
          ]),
          { provide: ScoutBridgeService, useValue: bridge },
          { provide: LogStore, useValue: makeLogStore() },
        ],
      });
      const store = TestBed.inject(WizardStore);
      store.setGuild({ id: 'g1', name: 'G' });
      const router = TestBed.inject(Router);
      const nav = vi.spyOn(router, 'navigateByUrl');
      const fixture = TestBed.createComponent(WorkerLaunchComponent);
      fixture.detectChanges();
      await fixture.whenStable();

      fire({ kind: 'state', status: makeRunningStatus() });
      fixture.detectChanges();
      vi.advanceTimersByTime(300);
      await fixture.whenStable();

      expect(nav).toHaveBeenCalledWith('/ready');
    } finally {
      vi.useRealTimers();
    }
  });

  it('shows error badge on crashed state', async () => {
    const { bridge, fire } = makeBridge();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '', children: [] }]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: LogStore, useValue: makeLogStore() },
      ],
    });
    const store = TestBed.inject(WizardStore);
    store.setGuild({ id: 'g1', name: 'G' });
    const fixture = TestBed.createComponent(WorkerLaunchComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    fire({ kind: 'state', status: makeCrashedStatus() });
    fixture.detectChanges();
    await fixture.whenStable();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('error');
  });

  it('shows retry and cancel buttons on error', async () => {
    const startImpl = async (): Promise<{ pid: number }> => {
      throw new Error('binary missing');
    };
    const { bridge, fire } = makeBridge({ workerStartImpl: startImpl });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '', children: [] }]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: LogStore, useValue: makeLogStore() },
      ],
    });
    const store = TestBed.inject(WizardStore);
    store.setGuild({ id: 'g1', name: 'G' });
    const fixture = TestBed.createComponent(WorkerLaunchComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    fire({ kind: 'state', status: makeCrashedStatus() });
    fixture.detectChanges();
    await fixture.whenStable();

    const retry = fixture.nativeElement.querySelector('[data-test="retry"]');
    const cancel = fixture.nativeElement.querySelector('[data-test="cancel"]');
    expect(retry).not.toBeNull();
    expect(cancel).not.toBeNull();
  });

  it('cancel only navigates; worker state and wizard store are untouched', async () => {
    const { bridge, fire } = makeBridge();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '', children: [] }]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: LogStore, useValue: makeLogStore() },
      ],
    });
    const store = TestBed.inject(WizardStore);
    store.setGuild({ id: 'g1', name: 'G' });
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');
    const resetSpy = vi.spyOn(store, 'reset');
    const fixture = TestBed.createComponent(WorkerLaunchComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    fire({ kind: 'state', status: makeCrashedStatus() });
    fixture.detectChanges();
    await fixture.whenStable();

    const cancelBtn = fixture.nativeElement.querySelector(
      '[data-test="cancel"]',
    ) as HTMLButtonElement;
    cancelBtn.click();
    await fixture.whenStable();

    expect(bridge.workerStop).not.toHaveBeenCalled();
    expect(resetSpy).not.toHaveBeenCalled();
    expect(nav).toHaveBeenCalledWith('/');
  });
});
