import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkerEvent, WorkerStatus } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { LogStore } from '../../core/log.store';
import { WizardStore } from '../../core/wizard.store';
import { ReadyComponent } from './ready.component';

function makeLogStore(
  initialStatus: WorkerStatus = {
    state: 'running',
    pid: 99,
    restarts: 0,
    startedAt: new Date().toISOString(),
  },
) {
  const statusSig = signal<WorkerStatus>(initialStatus);
  const eventsSig = signal<readonly WorkerEvent[]>([]);

  return {
    status: statusSig.asReadonly(),
    events: eventsSig.asReadonly(),
    _setStatus: (s: WorkerStatus) => statusSig.set(s),
    _addEvent: (e: WorkerEvent) => eventsSig.update((prev) => [...prev, e]),
    clear: vi.fn(),
    copy: vi.fn(),
  };
}

function makeBridge(
  opts: {
    onWorkerEventImpl?: (listener: (e: WorkerEvent) => void) => () => void;
  } = {},
) {
  let capturedListener: ((e: WorkerEvent) => void) | null = null;
  const bridge = {
    workerStart: vi.fn().mockResolvedValue({ pid: 99 }),
    workerStop: vi.fn().mockResolvedValue({ exitCode: 0 }),
    workerStatus: vi.fn().mockResolvedValue({ state: 'running', restarts: 0 }),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    onWorkerEvent: vi.fn((listener: (e: WorkerEvent) => void) => {
      capturedListener = listener;
      if (opts.onWorkerEventImpl) return opts.onWorkerEventImpl(listener);
      return () => {
        capturedListener = null;
      };
    }),
  } as unknown as ScoutBridgeService;

  return { bridge, emit: (e: WorkerEvent) => capturedListener?.(e) };
}

async function setup(
  opts: {
    initialStatus?: WorkerStatus;
    guildName?: string;
  } = {},
) {
  const initialStatus = opts.initialStatus ?? {
    state: 'running' as const,
    pid: 99,
    restarts: 0,
    startedAt: new Date().toISOString(),
  };
  const logStore = makeLogStore(initialStatus);
  const { bridge } = makeBridge();

  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: '', children: [] },
        { path: 'ready', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: LogStore, useValue: logStore },
    ],
  });

  const store = TestBed.inject(WizardStore);
  if (opts.guildName) {
    store.setGuild({ id: 'g1', name: opts.guildName });
  }

  const fixture = TestBed.createComponent(ReadyComponent);
  fixture.detectChanges();
  await fixture.whenStable();

  return { fixture, bridge, logStore, store };
}

describe('ReadyComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the running status badge', async () => {
    const { fixture } = await setup();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('running');
  });

  it('renders the crashed badge when status is crashed', async () => {
    const { fixture } = await setup({
      initialStatus: { state: 'crashed', restarts: 2 },
    });
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('crashed');
  });

  it('renders the stopped badge when status is stopped', async () => {
    const { fixture } = await setup({
      initialStatus: { state: 'stopped', restarts: 0 },
    });
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('stopped');
  });

  it('shows PID when status has pid', async () => {
    const { fixture } = await setup({
      initialStatus: {
        state: 'running',
        pid: 12345,
        restarts: 0,
        startedAt: new Date().toISOString(),
      },
    });
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('12345');
  });

  it('shows restart count', async () => {
    const { fixture } = await setup({
      initialStatus: { state: 'running', pid: 1, restarts: 3, startedAt: new Date().toISOString() },
    });
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('3');
  });

  it('shows guild name when set', async () => {
    const { fixture } = await setup({ guildName: 'Test Guild' });
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Test Guild');
  });

  it('shows Stop and Restart buttons when running', async () => {
    const { fixture } = await setup();
    const stop = fixture.nativeElement.querySelector('[data-test="stop"]');
    const restart = fixture.nativeElement.querySelector('[data-test="restart"]');
    expect(stop).not.toBeNull();
    expect(restart).not.toBeNull();
  });

  it('shows Start button when stopped', async () => {
    const { fixture } = await setup({
      initialStatus: { state: 'stopped', restarts: 0 },
    });
    const start = fixture.nativeElement.querySelector('[data-test="start"]');
    expect(start).not.toBeNull();
  });

  it('stop button calls workerStop', async () => {
    const { fixture, bridge } = await setup();
    const stopBtn = fixture.nativeElement.querySelector('[data-test="stop"]') as HTMLButtonElement;
    stopBtn.click();
    await fixture.whenStable();
    expect(bridge.workerStop).toHaveBeenCalled();
  });

  it('restart button calls workerStop then workerStart', async () => {
    const { fixture, bridge } = await setup();
    const restartBtn = fixture.nativeElement.querySelector(
      '[data-test="restart"]',
    ) as HTMLButtonElement;
    restartBtn.click();
    await fixture.whenStable();
    expect(bridge.workerStop).toHaveBeenCalled();
    expect(bridge.workerStart).toHaveBeenCalled();
  });

  it('copy button calls logStore.copy()', async () => {
    const { fixture, logStore } = await setup();
    const copyBtn = fixture.nativeElement.querySelector(
      '[data-test="copy-logs"]',
    ) as HTMLButtonElement;
    copyBtn.click();
    await fixture.whenStable();
    expect(logStore.copy).toHaveBeenCalled();
  });

  it('back-home stops the worker and navigates to /', async () => {
    const { fixture, bridge } = await setup({ guildName: 'G' });
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');

    const backBtn = fixture.nativeElement.querySelector(
      '[data-test="back-home"]',
    ) as HTMLButtonElement;
    backBtn.click();
    await fixture.whenStable();

    expect(bridge.workerStop).toHaveBeenCalled();
    expect(nav).toHaveBeenCalledWith('/');
  });

  it('crash banner is shown when state is crashed', async () => {
    const { fixture } = await setup({
      initialStatus: { state: 'crashed', restarts: 1 },
    });
    const banner = fixture.nativeElement.querySelector('.crash-banner');
    expect(banner).not.toBeNull();
  });

  it('crash banner is not shown when state is running', async () => {
    const { fixture } = await setup();
    const banner = fixture.nativeElement.querySelector('.crash-banner');
    expect(banner).toBeNull();
  });

  it('log tail renders stdout event lines', async () => {
    const logStore = makeLogStore({ state: 'running', pid: 1, restarts: 0 });
    logStore._addEvent({ kind: 'stdout', line: 'hello from worker', ts: new Date().toISOString() });

    const { bridge } = makeBridge();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '', children: [] }]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: LogStore, useValue: logStore },
      ],
    });

    const fixture = TestBed.createComponent(ReadyComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('hello from worker');
  });

  it('uptime defaults to — when no startedAt', async () => {
    const { fixture } = await setup({
      initialStatus: { state: 'running', pid: 1, restarts: 0 },
    });
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('—');
  });
});
