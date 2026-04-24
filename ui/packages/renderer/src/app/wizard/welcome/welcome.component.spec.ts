import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import {
  ScoutError,
  type DetectClientsResponse,
  type DetectedClient,
  type WorkerStatus,
} from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LogStore } from '../../core/log.store';
import { PersistentStateService } from '../../core/persistent-state';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { WelcomeComponent } from './welcome.component';

type ResolvablePromise<T> = {
  readonly promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
};

function createResolvablePromise<T>(): ResolvablePromise<T> {
  let resolve: (value: T) => void = () => undefined;
  let reject: (reason: unknown) => void = () => undefined;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

class FakeBridge {
  pending: ResolvablePromise<DetectClientsResponse> | null = null;
  calls = 0;
  vaultHasResult: { exists: boolean } = { exists: false };
  workerStart = vi.fn().mockResolvedValue({ pid: 1 });
  appOpenExternal = vi.fn().mockResolvedValue(undefined);

  detectClients(): Promise<DetectClientsResponse> {
    this.calls += 1;
    const next = createResolvablePromise<DetectClientsResponse>();
    this.pending = next;
    return next.promise;
  }

  vaultHas(): Promise<{ exists: boolean }> {
    return Promise.resolve(this.vaultHasResult);
  }

  vaultDelete(): Promise<{ deleted: boolean }> {
    return Promise.resolve({ deleted: true });
  }
}

function makeLogStore(initial: WorkerStatus = { state: 'idle', restarts: 0 }) {
  const s = signal<WorkerStatus>(initial);
  return {
    status: s.asReadonly(),
    events: signal<readonly never[]>([]).asReadonly(),
    clear: vi.fn(),
    copy: vi.fn(),
    _setStatus: (next: WorkerStatus) => s.set(next),
  } as unknown as LogStore & { _setStatus: (s: WorkerStatus) => void };
}

function makePersistent(
  initial: {
    clientId: string;
    guildId: string;
    guildName: string;
  } | null,
) {
  let state = initial;
  return {
    get: vi.fn(() => state),
    set: vi.fn((p: Partial<typeof initial & object>) => {
      state = {
        clientId: (p?.clientId ?? state?.clientId ?? '') as string,
        guildId: (p?.guildId ?? state?.guildId ?? '') as string,
        guildName: (p?.guildName ?? state?.guildName ?? '') as string,
      };
    }),
    clear: vi.fn(() => {
      state = null;
    }),
  } as unknown as PersistentStateService;
}

interface SetupOpts {
  readonly vaultHas?: boolean;
  readonly persistent?: {
    clientId: string;
    guildId: string;
    guildName: string;
  } | null;
  readonly workerStatus?: WorkerStatus;
}

async function createComponent(
  bridge: FakeBridge,
  opts: SetupOpts = {},
): Promise<{
  fixture: ComponentFixture<WelcomeComponent>;
  logStore: ReturnType<typeof makeLogStore>;
}> {
  bridge.vaultHasResult = { exists: opts.vaultHas ?? false };
  const logStore = makeLogStore(opts.workerStatus);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: 'provision', children: [] },
        { path: 'ready', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: LogStore, useValue: logStore },
      {
        provide: PersistentStateService,
        useValue: makePersistent(opts.persistent ?? null),
      },
    ],
  });
  const fixture = TestBed.createComponent(WelcomeComponent);
  fixture.detectChanges();
  return { fixture, logStore };
}

describe('WelcomeComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('does not render a standalone "Provision Discord" primary button', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const legacy = fixture.nativeElement.querySelector('[data-test="provision-discord"]');
    expect(legacy).toBeNull();
    const primaryCta = fixture.nativeElement.querySelector('.primary-cta');
    expect(primaryCta).toBeNull();
  });

  it('renders the Discord card with an "Installed" badge when detection reports installed', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);

    const clients: readonly DetectedClient[] = [
      { kind: 'discord', installed: true, method: 'uri-scheme' },
    ];
    bridge.pending?.resolve({ clients });
    await fixture.whenStable();
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('[data-test="discord-badge"]');
    expect(badge).not.toBeNull();
    expect((badge?.textContent ?? '').trim()).toContain('Installed');
  });

  it('renders a "Not installed" badge when detection reports discord missing', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({ clients: [{ kind: 'discord', installed: false, method: 'none' }] });
    await fixture.whenStable();
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('[data-test="discord-badge"]');
    expect((badge?.textContent ?? '').trim()).toContain('Not installed');
  });

  it('navigates to /provision on click when Discord is idle', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');

    const card = fixture.nativeElement.querySelector('.card--discord') as HTMLElement;
    card.click();
    expect(nav).toHaveBeenCalledWith('/provision');
    expect(TestBed.inject(WizardStore).currentStep()).toBe('provision');
  });

  it('Enter on the focused card fires the same handler as click', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');

    const card = fixture.nativeElement.querySelector('.card--discord') as HTMLElement;
    card.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    expect(nav).toHaveBeenCalledWith('/provision');
  });

  it('auto-starts the worker once when provisioning exists and worker is idle', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge, {
      vaultHas: true,
      persistent: { clientId: 'c', guildId: 'g42', guildName: 'Scout Dev' },
      workerStatus: { state: 'idle', restarts: 0 },
    });
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('does NOT auto-start the worker when there is no provisioning', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge, {
      vaultHas: false,
      persistent: null,
    });
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.workerStart).not.toHaveBeenCalled();
  });

  it('does NOT auto-start the worker when worker is already running', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge, {
      vaultHas: true,
      persistent: { clientId: 'c', guildId: 'g', guildName: 'n' },
      workerStatus: { state: 'running', restarts: 0, pid: 1 },
    });
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.workerStart).not.toHaveBeenCalled();
  });

  it('hydrates wizard signals from persistent state so the Connected card can render', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge, {
      vaultHas: true,
      persistent: { clientId: 'c', guildId: 'g', guildName: 'My Guild' },
      workerStatus: { state: 'running', restarts: 0, pid: 1 },
    });
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('[data-test="discord-badge"]');
    expect((badge?.textContent ?? '').trim()).toContain('Connected');
    const caption = fixture.nativeElement.querySelector('[data-test="discord-caption"]');
    expect(caption?.textContent?.trim()).toBe('My Guild');
  });

  it('Connected card click navigates to /ready', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge, {
      vaultHas: true,
      persistent: { clientId: 'c', guildId: 'g', guildName: 'My Guild' },
      workerStatus: { state: 'running', restarts: 0, pid: 1 },
    });
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');
    (fixture.nativeElement.querySelector('.card--discord') as HTMLElement).click();
    expect(nav).toHaveBeenCalledWith('/ready');
  });

  it('Disconnected card click triggers workerStart instead of navigating', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge, {
      vaultHas: true,
      persistent: { clientId: 'c', guildId: 'g', guildName: 'My Guild' },
      workerStatus: { state: 'stopped', restarts: 0 },
    });
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    // Initial boot does not trigger workerStart when status != 'idle'.
    expect(bridge.workerStart).not.toHaveBeenCalled();

    (fixture.nativeElement.querySelector('.card--discord') as HTMLElement).click();
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('renders the loading state before detection settles', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);

    const loadingEl = fixture.nativeElement.querySelector('.loading');
    expect(loadingEl).not.toBeNull();
    expect(loadingEl?.textContent?.trim()).toBe('Detecting clients...');
  });

  it('renders the error state with placeholder cards when detection rejects', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);

    bridge.pending?.reject(new ScoutError('bridge/not-available', 'preload missing'));
    await Promise.resolve();
    await Promise.resolve();
    await fixture.whenStable();
    fixture.detectChanges();

    const message = fixture.componentInstance.error();
    expect(message).toContain('bridge/not-available');
    const cards = fixture.nativeElement.querySelectorAll('.card');
    expect(cards.length).toBe(3);
  });

  it('Download Discord link calls appOpenExternal with the Discord download URL', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({ clients: [{ kind: 'discord', installed: false, method: 'none' }] });
    await fixture.whenStable();
    fixture.detectChanges();

    const link = fixture.nativeElement.querySelector(
      '[data-test="discord-download"]',
    ) as HTMLElement;
    link.click();
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: 'https://discord.com/download' });
  });
});
