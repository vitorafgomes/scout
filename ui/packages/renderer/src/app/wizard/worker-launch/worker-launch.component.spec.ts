import { readFileSync } from 'node:fs';

import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkerEvent, WorkerStatus } from '@scout/shared';

import { restartAsMain } from '../../core/main-restart.testing';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { LlmConfigStore } from '../../core/llm-config.store';
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
  serverChosen?: boolean;
}

function makeBridge(opts: BridgeFactory = {}) {
  let capturedListener: WorkerEventListener | null = null;

  const startImpl = opts.workerStartImpl ?? (() => Promise.resolve({ pid: 42 }));
  const eventImpl = opts.onWorkerEventImpl;
  const tokenExists = opts.tokenExists ?? false;
  const serverChosen = opts.serverChosen ?? tokenExists;

  const bridge = {
    workerStart: vi.fn(startImpl),
    workerStop: vi.fn().mockResolvedValue({ exitCode: 0 }),
    workerStatus: vi.fn().mockResolvedValue({ state: 'idle', restarts: 0 }),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    vaultHas: vi.fn(async ({ key }: { key: string }) => ({
      exists: key === 'discord.guild-id' ? serverChosen : tokenExists,
    })),
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
    localStorage.clear();
  });

  it('calls workerStart on init when guild is set', async () => {
    const { bridge } = await setup({ guildName: 'Scout Server', clientId: 'cid' });
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('calls workerStart on init when the vault holds the token and a chosen server', async () => {
    const { bridge } = await setup({ tokenOnly: true });
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('starts no worker when the vault holds the token but no chosen server', async () => {
    const { bridge } = await setup({
      tokenOnly: true,
      bridgeOpts: { serverChosen: false },
    });
    expect(bridge.workerStart).not.toHaveBeenCalled();
  });

  it('redirects to / when the vault holds the token but no chosen server', async () => {
    const { bridge } = makeBridge({ tokenExists: true, serverChosen: false });
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
    const nav = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');

    const fixture = TestBed.createComponent(WorkerLaunchComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(nav).toHaveBeenCalledWith('/');
  });

  it('does NOT redirect to / when the vault holds the token and a chosen server, with no metadata in memory', async () => {
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

  it('with no provider chosen, the AI Source row says Ollama, the provider the worker falls back to', async () => {
    const { fixture } = await setup({ guildName: 'G', clientId: 'c' });
    expect(facts(fixture.nativeElement as HTMLElement)).toEqual([
      ['Chat app', 'Discord · server G'],
      ['AI Source', 'Ollama'],
      ['Bot token', "Saved, encrypted with this computer's keyring"],
    ]);
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

  it('shows the Could not start chip on crashed state', async () => {
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

    expect(
      (fixture.nativeElement as HTMLElement)
        .querySelector('.status-row .badge')
        ?.textContent?.trim(),
    ).toBe('Could not start');
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

/** A worker that already runs, the way main's supervisor answers: a start on a running worker resolves with no event. */
function makeAlreadyRunningWorker(logStoreHeard: boolean) {
  const status = signal<WorkerStatus>({ state: 'running', restarts: 0, pid: 7 });
  const heard = signal<WorkerStatus>(logStoreHeard ? status() : { state: 'idle', restarts: 0 });
  let listener: WorkerEventListener | null = null;
  const worker = { spawns: 0 };
  const report = (next: WorkerStatus): void => {
    status.set(next);
    heard.set(next);
    listener?.({ kind: 'state', status: next });
  };
  const bridge = {
    workerStart: vi.fn(async () => {
      if (status().state === 'running') return { pid: status().pid ?? 0 };
      worker.spawns += 1;
      report({ state: 'starting', restarts: 0 });
      report({ state: 'running', restarts: 0, pid: 8 });
      return { pid: 8 };
    }),
    workerStop: vi.fn(async () => {
      report({ state: 'stopped', restarts: 0 });
      return { exitCode: 0 };
    }),
    workerStatus: vi.fn(async () => status()),
    vaultHas: vi.fn().mockResolvedValue({ exists: true }),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    onWorkerEvent: vi.fn((next: WorkerEventListener) => {
      listener = next;
      return () => {
        listener = null;
      };
    }),
  };
  Object.assign(bridge, { workerRestart: vi.fn(() => restartAsMain(bridge)) });
  const logStore = {
    events: () => [],
    status: heard.asReadonly(),
    clear: vi.fn(),
    copy: vi.fn(),
  };
  return { bridge: bridge as unknown as ScoutBridgeService, logStore, worker };
}

async function launchOverRunningWorker(logStoreHeard: boolean) {
  const { bridge, logStore, worker } = makeAlreadyRunningWorker(logStoreHeard);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: '', children: [] },
        { path: 'ready', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: LogStore, useValue: logStore },
      { provide: PersistentStateService, useValue: makePersistentStub() },
    ],
  });
  const store = TestBed.inject(WizardStore);
  store.setClientId('111222333');
  store.setGuild({ id: 'g1', name: 'Team' });
  const fixture = TestBed.createComponent(WorkerLaunchComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  await new Promise((resolve) => setTimeout(resolve, 0));
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, worker };
}

describe('Launch when the worker already runs, and what it says it starts', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('Launch reads Scout is running, not Starting Scout, when the worker was already running as it opened, before the window heard of it', async () => {
    const { fixture } = await launchOverRunningWorker(false);

    expect(
      (fixture.nativeElement as HTMLElement)
        .querySelector('.status-row .badge')
        ?.textContent?.trim(),
    ).toBe('Scout is running');
  });

  for (const heard of [true, false]) {
    it(`Launch over a worker that already runs starts no second worker${heard ? '' : ' though the window has not heard it runs'}; main restarted it for the new token`, async () => {
      const { fixture, worker } = await launchOverRunningWorker(heard);

      expect({
        spawns: worker.spawns,
        badge: (fixture.nativeElement as HTMLElement)
          .querySelector('.status-row .badge')
          ?.textContent?.trim(),
      }).toEqual({ spawns: 0, badge: 'Scout is running' });
    });
  }

  for (const [token, server, starts] of [
    [true, true, true],
    [true, false, false],
    [false, true, false],
    [false, false, false],
  ] as const) {
    it(`with ${token ? 'a bot token saved' : 'no bot token'} and ${server ? 'a server chosen' : 'no server chosen'} in the vault, Launch ${starts ? 'starts Scout and stays' : 'starts nothing and goes back to the first step'}`, async () => {
      const { bridge } = makeBridge({ tokenExists: token, serverChosen: server });
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          provideRouter([{ path: '', children: [] }]),
          { provide: ScoutBridgeService, useValue: bridge },
          { provide: LogStore, useValue: makeLogStore() },
          { provide: PersistentStateService, useValue: makePersistentStub() },
        ],
      });
      await TestBed.inject(WizardStore).loadFromVault();
      const nav = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');

      const fixture = TestBed.createComponent(WorkerLaunchComponent);
      fixture.detectChanges();
      await fixture.whenStable();

      expect({
        started: vi.mocked(bridge.workerStart).mock.calls.length,
        wentBack: nav.mock.calls.some(([url]) => url === '/'),
      }).toEqual({ started: starts ? 1 : 0, wentBack: !starts });
    });
  }

  for (const [provider, row] of [
    ['OpenRouter', 'OpenRouter'],
    ['None', 'No AI Source'],
  ] as const) {
    it(`with ${provider} as the provider in the vault the AI Source row says ${row}, not always Ollama`, async () => {
      const vaultHas = vi.fn(async ({ key }: { key: string }) => ({
        exists: key === 'discord.bot-token' || key === 'llm.openrouter.api-key',
      }));
      const vaultGetPublic = vi.fn(async ({ key }: { key: string }) => ({
        value:
          key === 'llm.provider'
            ? provider
            : key === 'llm.cloud-model' && provider === 'OpenRouter'
              ? 'openai/gpt-5'
              : null,
      }));
      const { bridge } = makeBridge({ tokenExists: true });
      Object.assign(bridge, { vaultHas, vaultGetPublic });
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
      store.setClientId('111222333');
      store.setGuild({ id: 'g1', name: 'Team' });
      const fixture = TestBed.createComponent(WorkerLaunchComponent);
      fixture.detectChanges();
      await fixture.whenStable();

      await TestBed.inject(LlmConfigStore).loadFromVault();
      fixture.detectChanges();

      expect(facts(fixture.nativeElement as HTMLElement)).toContainEqual(['AI Source', row]);
    });
  }
});

/** The colour literals left in a component's styles, read from the renderer root: hex, rgb(), hsl() and the named white and black. */
function colourLiterals(relative: string): string[] {
  const source = readFileSync(relative, 'utf8');
  const start = source.indexOf('  styles:');
  const css = source.slice(start, source.indexOf('\n})\n', start));
  expect(start, `${relative} declares its styles`).toBeGreaterThanOrEqual(0);
  expect(css, `${relative} paints with the theme tokens`).toContain('var(--sc-');
  return Array.from(
    css.matchAll(
      /#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(|(?<![\w-])(?:white|black)(?![\w-])/g,
    ),
    (match) => match[0],
  );
}

describe("The wizard's Launch page follows the theme and the prototype", () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  for (const file of ['src/app/wizard/worker-launch/worker-launch.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }

  it('Retry shows its icon before its text, as the prototype draws it', async () => {
    const { bridge, fire } = makeBridge({
      workerStartImpl: async (): Promise<{ pid: number }> => {
        throw new Error('binary missing');
      },
      tokenExists: true,
    });
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

    expect(iconAndText(fixture.nativeElement as HTMLElement, ['[data-test="retry"]'])).toEqual([
      ['sc-icon', 'Retry'],
    ]);
  });
});

/** The rows of Launch's summary: each term with what it says. */
function facts(root: HTMLElement): (string | undefined)[][] {
  return Array.from(root.querySelectorAll('[data-test="launch-facts"] dt'), (term) => [
    term.textContent?.trim(),
    term.nextElementSibling?.textContent?.replace(/\s+/g, ' ').trim(),
  ]);
}

const MAIN_RAW =
  "Error invoking remote method 'scout:worker.start': Error: Error: worker/start-failed: the worker crashed before it was running";
const MAIN_CODED =
  "Error invoking remote method 'scout:worker.start': Error: [scout:vault/unavailable] The credential store is unavailable.";

describe('The Launch step as in the prototype: Starting, Running, Could not start', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  const rows: readonly {
    readonly state: string;
    readonly start: 'pending' | 'answers' | 'crashes' | Error;
    readonly retry?: 'answers';
    readonly raw: string | null;
    readonly chip: readonly (string | undefined)[];
    readonly progress: boolean;
    readonly alert: readonly string[] | null;
    readonly detailsOpen: boolean;
    readonly actions: readonly (readonly [string, boolean])[];
  }[] = [
    {
      state: 'Starting',
      start: 'pending',
      raw: null,
      chip: ['badge--starting', 'Starting Scout', 'This usually takes a few seconds.'],
      progress: true,
      alert: null,
      detailsOpen: false,
      actions: [],
    },
    {
      state: 'Running',
      start: 'answers',
      raw: null,
      chip: ['badge--running', 'Scout is running', undefined],
      progress: false,
      alert: null,
      detailsOpen: false,
      actions: [],
    },
    {
      state: 'Could not start, the start refused with no code',
      start: new Error('binary missing'),
      raw: 'binary missing',
      chip: ['badge--crashed', 'Could not start', undefined],
      progress: false,
      alert: [
        'Scout stopped before it was ready.',
        'Scout could not start. The reason is under Show details.',
      ],
      detailsOpen: true,
      actions: [
        ['Retry', true],
        ['Cancel', false],
      ],
    },
    {
      state: "Could not start, the start refused in main's own words across IPC",
      start: new Error(MAIN_RAW),
      raw: MAIN_RAW,
      chip: ['badge--crashed', 'Could not start', undefined],
      progress: false,
      alert: [
        'Scout stopped before it was ready.',
        'Scout could not start. The reason is under Show details.',
      ],
      detailsOpen: true,
      actions: [
        ['Retry', true],
        ['Cancel', false],
      ],
    },
    {
      state: 'Could not start, the start refused with a coded error',
      start: new Error(MAIN_CODED),
      raw: MAIN_CODED,
      chip: ['badge--crashed', 'Could not start', undefined],
      progress: false,
      alert: ['Scout stopped before it was ready.', 'The credential store is unavailable.'],
      detailsOpen: true,
      actions: [
        ['Retry', true],
        ['Cancel', false],
      ],
    },
    {
      state: 'Running after a refused start and Retry',
      start: new Error(MAIN_RAW),
      retry: 'answers',
      raw: null,
      chip: ['badge--running', 'Scout is running', undefined],
      progress: false,
      alert: null,
      detailsOpen: false,
      actions: [],
    },
    {
      state: 'Could not start, the worker crashed',
      start: 'crashes',
      raw: null,
      chip: ['badge--crashed', 'Could not start', undefined],
      progress: false,
      alert: [
        'Scout stopped before it was ready.',
        'Scout crashed while starting. Its last lines are under Show details.',
      ],
      detailsOpen: true,
      actions: [
        ['Retry', true],
        ['Cancel', false],
      ],
    },
  ];

  for (const row of rows) {
    it(`${row.state}: the chip "${row.chip[1]}", ${row.progress ? 'the progress bar' : 'no progress bar'}, the three summary rows, ${row.alert === null ? 'no failure box' : `the plain sentence "${row.alert[1]}"`}, ${row.raw === null ? 'no raw answer' : "main's raw answer only"} with the log under Show details ${row.detailsOpen ? 'open' : 'closed'}`, async () => {
      const refused = row.start instanceof Error ? row.start : null;
      let starts = 0;
      const { bridge, fire } = makeBridge({
        tokenExists: true,
        ...(row.start === 'answers'
          ? {}
          : {
              workerStartImpl:
                refused !== null
                  ? async () => {
                      starts += 1;
                      if (row.retry === 'answers' && starts > 1) return { pid: 42 };
                      throw refused;
                    }
                  : () => new Promise<{ pid: number }>(() => undefined),
            }),
      });
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          provideRouter([
            { path: '', children: [] },
            { path: 'ready', children: [] },
          ]),
          { provide: ScoutBridgeService, useValue: bridge },
          {
            provide: LogStore,
            useValue: {
              events: () => [
                { kind: 'stdout', line: 'Starting Scout' },
                { kind: 'stderr', line: 'AI Source did not answer' },
              ],
              status: () => ({ state: 'idle', restarts: 0 }),
            },
          },
          { provide: PersistentStateService, useValue: makePersistentStub() },
        ],
      });
      const store = TestBed.inject(WizardStore);
      await store.loadFromVault();
      store.setGuild({ id: 'g1', name: 'Acme Engineering' });
      const fixture = TestBed.createComponent(WorkerLaunchComponent);
      fixture.detectChanges();
      await fixture.whenStable();
      if (row.start === 'crashes') fire({ kind: 'state', status: makeCrashedStatus() });
      fixture.detectChanges();
      await fixture.whenStable();
      const root = fixture.nativeElement as HTMLElement;
      if (row.retry === 'answers') {
        (root.querySelector('[data-test="retry"]') as HTMLButtonElement).click();
        await vi.mocked(bridge.workerStart).mock.results[1]!.value;
        fixture.detectChanges();
        await fixture.whenStable();
      }
      const column = getComputedStyle(root.querySelector('main') as HTMLElement);
      const chip = root.querySelector('.card [role="status"] .badge') as HTMLElement;
      const alert = root.querySelector('.card [data-test="launch-error"]');
      const details = root.querySelector('.card details') as HTMLDetailsElement;

      expect({
        title: root.querySelector('h1')?.textContent?.trim(),
        lede: root.querySelector('.lede')?.textContent?.trim(),
        column: [column.maxWidth, column.marginLeft, column.marginRight],
        chip: [
          [...chip.classList].find((name) => name.startsWith('badge--')),
          chip.textContent?.trim(),
          root.querySelector('.card [role="status"] .muted')?.textContent?.trim(),
        ],
        chipIcon: chip.firstElementChild?.tagName.toLowerCase(),
        progress: root.querySelector('.card mat-progress-bar') !== null,
        facts: facts(root),
        alert:
          alert === null
            ? null
            : [
                alert.querySelector('.alert-title')?.textContent?.trim(),
                alert.querySelector('.alert-detail')?.textContent?.trim(),
              ],
        details: [
          details.querySelector('summary')?.textContent?.trim(),
          details.open,
          details.querySelector('[data-test="launch-raw"]')?.textContent?.trim() ?? null,
          details.querySelector('pre')?.textContent,
        ],
        rawOutsideDetails:
          row.raw !== null &&
          (root.querySelector('.card')?.textContent ?? '')
            .replace(details.textContent ?? '', '')
            .includes(row.raw),
        actions: (['retry', 'cancel'] as const)
          .map((test) => root.querySelector(`[data-test="${test}"]`))
          .filter((button): button is Element => button !== null)
          .map((button) => [
            button.textContent?.trim(),
            button.classList.contains('mat-mdc-unelevated-button'),
          ]),
      }).toEqual({
        title: 'Start Scout',
        lede: 'Scout starts on this computer with the AI Source you connected.',
        column: ['760px', 'auto', 'auto'],
        chip: row.chip,
        chipIcon: 'sc-icon',
        progress: row.progress,
        facts: [
          ['Chat app', 'Discord · server Acme Engineering'],
          ['AI Source', 'Ollama'],
          ['Bot token', "Saved, encrypted with this computer's keyring"],
        ],
        alert: row.alert,
        details: [
          'Show details',
          row.detailsOpen,
          row.raw,
          'Starting Scout\nAI Source did not answer',
        ],
        rawOutsideDetails: false,
        actions: row.actions,
      });
    });
  }
});

/** The first tag inside each named Material button's label and its text, so a main action is checked for its icon before its label. */
function iconAndText(root: HTMLElement, selectors: readonly string[]): (string | undefined)[][] {
  return selectors.map((selector) => {
    const button = root.querySelector(selector) as HTMLElement | null;
    return [
      button?.querySelector('.mdc-button__label')?.firstElementChild?.tagName.toLowerCase(),
      (button?.textContent ?? '').replace(/\s+/g, ' ').trim(),
    ];
  });
}
