import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkerEvent, WorkerStatus } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { LogStore } from '../../core/log.store';
import { WizardStore } from '../../core/wizard.store';
import { PersistentStateService } from '../../core/persistent-state';
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
  // Controls what vaultHas returns for discord.bot-token (default: exists=false)
  tokenExists?: boolean;
}

function makeBridge(opts: BridgeFactory = {}) {
  let capturedListener: WorkerEventListener | null = null;

  const startImpl = opts.workerStartImpl ?? (() => Promise.resolve({ pid: 42 }));
  const eventImpl = opts.onWorkerEventImpl;
  const tokenExists = opts.tokenExists ?? false;

  const bridge = {
    workerStart: vi.fn(startImpl),
    workerStop: vi.fn().mockResolvedValue({ exitCode: 0 }),
    workerStatus: vi.fn().mockResolvedValue({ state: 'idle', restarts: 0 }),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    vaultHas: vi.fn().mockResolvedValue({ exists: tokenExists }),
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

function makePersistentStub() {
  return {
    get: vi.fn(() => null),
    set: vi.fn(),
    clear: vi.fn(),
  } as unknown as PersistentStateService;
}

async function setup(
  opts: {
    guildName?: string;
    clientId?: string;
    tokenOnly?: boolean;
    bridgeOpts?: BridgeFactory;
  } = {},
) {
  const tokenExists =
    opts.tokenOnly === true || opts.guildName !== undefined || opts.clientId !== undefined;
  const { bridge, fire } = makeBridge({ ...(opts.bridgeOpts ?? {}), tokenExists });

  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: '', children: [] },
        { path: 'ready', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: LogStore, useValue: makeLogStore() },
      { provide: PersistentStateService, useValue: makePersistentStub() },
    ],
  });

  const store = TestBed.inject(WizardStore);

  if (opts.tokenOnly === true) {
    // Token-only provisioning: hydrate _hasToken via loadFromVault.
    await store.loadFromVault();
  } else {
    if (opts.clientId !== undefined) store.setClientId(opts.clientId);
    if (opts.guildName !== undefined) store.setGuild({ id: 'g1', name: opts.guildName });
  }

  const fixture = TestBed.createComponent(WorkerLaunchComponent);
  fixture.detectChanges();
  await fixture.whenStable();

  return { fixture, bridge, fire, store };
}

describe('WorkerLaunchComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('calls workerStart on init when guild is set', async () => {
    const { bridge } = await setup({ guildName: 'Scout Server', clientId: 'cid' });
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('calls workerStart on init when token-only (no guild)', async () => {
    const { bridge } = await setup({ tokenOnly: true });
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('does NOT redirect to / when provisioned via token only (no guild)', async () => {
    const { bridge } = makeBridge({ tokenExists: true });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '', children: [] }]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: LogStore, useValue: makeLogStore() },
        { provide: PersistentStateService, useValue: makePersistentStub() },
      ],
    });
    const store = TestBed.inject(WizardStore);
    await store.loadFromVault();
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');

    const fixture = TestBed.createComponent(WorkerLaunchComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(nav).not.toHaveBeenCalledWith('/');
  });

  it('renders the guild name when guild metadata is present', async () => {
    const { fixture } = await setup({ guildName: 'My Guild', clientId: 'cid' });
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('My Guild');
  });

  it('shows env summary chips reflecting token and provider', async () => {
    const { fixture } = await setup({ guildName: 'G', clientId: 'c' });
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('LLM_PROVIDER=Ollama');
    expect(text).toContain('ENV=Production');
  });

  it('redirects to / when hasProvisioning is false (no token, no guild)', async () => {
    const { bridge } = makeBridge({ tokenExists: false });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '', children: [] }]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: LogStore, useValue: makeLogStore() },
        { provide: PersistentStateService, useValue: makePersistentStub() },
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
      const { bridge, fire } = makeBridge({ tokenExists: true });
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          provideRouter([
            { path: '', children: [] },
            { path: 'ready', children: [] },
          ]),
          { provide: ScoutBridgeService, useValue: bridge },
          { provide: LogStore, useValue: makeLogStore() },
          { provide: PersistentStateService, useValue: makePersistentStub() },
        ],
      });
      const store = TestBed.inject(WizardStore);
      await store.loadFromVault();
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
    const { bridge, fire } = makeBridge({ tokenExists: true });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '', children: [] }]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: LogStore, useValue: makeLogStore() },
        { provide: PersistentStateService, useValue: makePersistentStub() },
      ],
    });
    const store = TestBed.inject(WizardStore);
    await store.loadFromVault();
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
    const { bridge, fire } = makeBridge({ workerStartImpl: startImpl, tokenExists: true });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '', children: [] }]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: LogStore, useValue: makeLogStore() },
        { provide: PersistentStateService, useValue: makePersistentStub() },
      ],
    });
    const store = TestBed.inject(WizardStore);
    await store.loadFromVault();
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
    const { bridge, fire } = makeBridge({ tokenExists: true });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '', children: [] }]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: LogStore, useValue: makeLogStore() },
        { provide: PersistentStateService, useValue: makePersistentStub() },
      ],
    });
    const store = TestBed.inject(WizardStore);
    await store.loadFromVault();
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
