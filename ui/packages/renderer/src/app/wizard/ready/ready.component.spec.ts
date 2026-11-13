import { readFileSync } from 'node:fs';

import { InteractivityChecker } from '@angular/cdk/a11y';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RESTART_DEFERRED_LINE, type WorkerEvent, type WorkerStatus } from '@scout/shared';

import { restartAsMain } from '../../core/main-restart.testing';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { LlmConfigStore, LLM_CONFIG_STORAGE_KEY } from '../../core/llm-config.store';
import { LogStore } from '../../core/log.store';
import { NoticeService } from '../../core/notice.service';
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
    localListening: vi.fn().mockResolvedValue({ listening: false }),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    vaultStore: vi.fn().mockResolvedValue(undefined),
    vaultHas: vi.fn().mockResolvedValue({ exists: false }),
    onWorkerEvent: vi.fn((listener: (e: WorkerEvent) => void) => {
      capturedListener = listener;
      if (opts.onWorkerEventImpl) return opts.onWorkerEventImpl(listener);
      return () => {
        capturedListener = null;
      };
    }),
  };
  Object.assign(bridge, { workerRestart: vi.fn(() => restartAsMain(bridge)) });

  return {
    bridge: bridge as unknown as ScoutBridgeService,
    emit: (e: WorkerEvent) => capturedListener?.(e),
  };
}

/** jsdom lays nothing out, so the dialog's focus trap is told which controls a real window could focus. */
const LAID_OUT: Pick<
  InteractivityChecker,
  'isDisabled' | 'isVisible' | 'isTabbable' | 'isFocusable'
> = {
  isDisabled: (el: HTMLElement) => el.hasAttribute('disabled'),
  isVisible: () => true,
  isTabbable: (el: HTMLElement) => el.tagName === 'BUTTON' && !el.hasAttribute('disabled'),
  isFocusable: (el: HTMLElement) => el.tagName === 'BUTTON' && !el.hasAttribute('disabled'),
};

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
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
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
    localStorage.clear();
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  it('renders the running status badge', async () => {
    const { fixture } = await setup();
    expect(chip(fixture.nativeElement as HTMLElement)).toEqual(['badge--running', 'Running']);
  });

  it('renders the crashed badge when status is crashed', async () => {
    const { fixture } = await setup({
      initialStatus: { state: 'crashed', restarts: 2 },
    });
    expect(chip(fixture.nativeElement as HTMLElement)).toEqual(['badge--crashed', 'Crashed']);
  });

  it('renders the stopped badge when status is stopped', async () => {
    const { fixture } = await setup({
      initialStatus: { state: 'stopped', restarts: 0 },
    });
    expect(chip(fixture.nativeElement as HTMLElement)).toEqual(['badge--stopped', 'Stopped']);
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

  it('Restart while a live listen runs leaves Scout running and says it restarts once the listen ends', async () => {
    const { fixture, bridge } = await setup();
    vi.mocked(bridge.localListening).mockResolvedValue({ listening: true });

    (fixture.nativeElement.querySelector('[data-test="restart"]') as HTMLButtonElement).click();
    for (let i = 0; i < 4; i++) {
      await fixture.whenStable();
      fixture.detectChanges();
    }

    expect({
      restartAsked: vi.mocked(bridge.workerRestart).mock.calls.length,
      stopped: vi.mocked(bridge.workerStop).mock.calls.length,
      note: (
        fixture.nativeElement.querySelector('[data-test="restart-note"]') as HTMLElement | null
      )?.textContent?.trim(),
    }).toEqual({ restartAsked: 1, stopped: 0, note: RESTART_DEFERRED_LINE });
  });

  it('Restart on a crashed worker starts it; there is no running worker for main to restart', async () => {
    const { fixture, bridge } = await setup({ initialStatus: { state: 'crashed', restarts: 1 } });

    (fixture.nativeElement.querySelector('[data-test="restart"]') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect({
      started: vi.mocked(bridge.workerStart).mock.calls.length,
      restartAsked: vi.mocked(bridge.workerRestart).mock.calls.length,
    }).toEqual({ started: 1, restartAsked: 0 });
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

  it('back-home only navigates; worker and vault are untouched', async () => {
    const { fixture, bridge } = await setup({ guildName: 'G' });
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');
    const store = TestBed.inject(WizardStore);
    const reprovisionSpy = vi.spyOn(store, 'reprovision');
    const resetSpy = vi.spyOn(store, 'reset');

    const backBtn = fixture.nativeElement.querySelector(
      '[data-test="back-home"]',
    ) as HTMLButtonElement;
    expect(backBtn.textContent?.trim()).toBe('Home');

    backBtn.click();
    await fixture.whenStable();

    expect(bridge.workerStop).not.toHaveBeenCalled();
    expect(bridge.vaultDelete).not.toHaveBeenCalled();
    expect(reprovisionSpy).not.toHaveBeenCalled();
    expect(resetSpy).not.toHaveBeenCalled();
    expect(nav).toHaveBeenCalledWith('/');
  });

  for (const [answer, runs] of [
    ['confirm-ok', true],
    ['confirm-cancel', false],
  ] as const) {
    it(`reprovision asks first in a dialog with Cancel focused; ${answer} ${runs ? 'stops the worker, reprovisions and goes home' : 'does nothing'}`, async () => {
      const { fixture, bridge } = await setup({ guildName: 'G' });
      const router = TestBed.inject(Router);
      const nav = vi.spyOn(router, 'navigateByUrl');
      const store = TestBed.inject(WizardStore);
      const reprovisionSpy = vi.spyOn(store, 'reprovision').mockResolvedValue();

      (
        fixture.nativeElement.querySelector('[data-test="reprovision"]') as HTMLButtonElement
      ).click();
      for (let i = 0; i < 4; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
      const asked = {
        title: document.querySelector('sc-confirm-dialog h2')?.textContent?.trim(),
        focused: (document.activeElement as HTMLElement | null)?.getAttribute('data-test'),
      };
      (document.querySelector(`[data-test="${answer}"]`) as HTMLButtonElement | null)?.click();
      for (let i = 0; i < 4; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
      await vi.waitFor(() => expect(nav.mock.calls.some(([url]) => url === '/')).toBe(runs));

      expect(asked).toEqual({ title: 'Start over with another bot?', focused: 'confirm-cancel' });
      expect([
        vi.mocked(bridge.workerStop).mock.calls.length,
        reprovisionSpy.mock.calls.length,
        nav.mock.calls.some(([url]) => url === '/'),
      ]).toEqual(runs ? [1, 1, true] : [0, 0, false]);
    });
  }

  it('reprovision whose bot token cannot be removed says why and stays on the ready step', async () => {
    const { fixture } = await setup({ guildName: 'G' });
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');
    const show = vi
      .spyOn(TestBed.inject(NoticeService), 'show')
      .mockImplementation(() => undefined);
    vi.spyOn(TestBed.inject(WizardStore), 'reprovision').mockRejectedValue(
      new Error('[scout:vault/unavailable] The credential store is unavailable.'),
    );

    (fixture.nativeElement.querySelector('[data-test="reprovision"]') as HTMLButtonElement).click();
    for (let i = 0; i < 4; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    (document.querySelector('[data-test="confirm-ok"]') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(show).toHaveBeenCalled());

    expect({
      notice: show.mock.calls[0],
      home: nav.mock.calls.some(([url]) => url === '/'),
    }).toEqual({
      notice: [
        'Scout could not remove the bot token: The credential store is unavailable.',
        'error',
      ],
      home: false,
    });
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

  it('renders the no-LLM banner when llmConfig is unset', async () => {
    const { fixture } = await setup();
    const banner = fixture.nativeElement.querySelector('[data-test="no-llm-banner"]');
    expect(banner).not.toBeNull();
    expect((banner?.textContent ?? '').toLowerCase()).toContain('no llm model');
  });

  it('omits the no-LLM banner once llmConfig is configured', async () => {
    const { fixture } = await setup();
    const store = TestBed.inject(LlmConfigStore);
    await store.setSelection('Ollama', 'llama3.1', 'http://localhost:11434');
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('[data-test="no-llm-banner"]');
    expect(banner).toBeNull();
  });
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

/** Ready over a running worker with Discord set up, whose restarts go through main's restart path. */
async function renderWithDiscordAnd(remaining: 'Slack' | 'this computer only') {
  if (remaining === 'this computer only') localStorage.setItem('scout:local-only', '1');
  const status = signal<WorkerStatus>({ state: 'running', restarts: 0, pid: 99 });
  const bridge = {
    workerStart: vi.fn(async () => {
      status.set({ state: 'running', restarts: 0, pid: 100 });
      return { pid: 100 };
    }),
    workerStop: vi.fn(async () => {
      status.set({ state: 'stopped', restarts: 0 });
      return { exitCode: 0 };
    }),
    workerStatus: vi.fn(async () => status()),
    localListening: vi.fn().mockResolvedValue({ listening: false }),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    vaultStore: vi.fn().mockResolvedValue(undefined),
    vaultHas: vi.fn().mockResolvedValue({ exists: false }),
    onWorkerEvent: vi.fn(() => () => undefined),
  };
  const workerRestart = vi.fn(() => restartAsMain(bridge));
  Object.assign(bridge, { workerRestart });
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: '', children: [] },
        { path: 'ready', children: [] },
        { path: 'chat', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge },
      {
        provide: LogStore,
        useValue: {
          status: status.asReadonly(),
          events: signal<readonly WorkerEvent[]>([]).asReadonly(),
          clear: vi.fn(),
          copy: vi.fn(),
        },
      },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
    ],
  });
  const store = TestBed.inject(WizardStore);
  store.setClientId('111222333');
  store.setGuild({ id: 'g1', name: 'Team' });
  if (remaining === 'Slack') store.setSlackConnected(true);
  const fixture = TestBed.createComponent(ReadyComponent);
  fixture.detectChanges();
  await fixture.whenStable();

  const confirmReprovision = async (): Promise<void> => {
    (fixture.nativeElement.querySelector('[data-test="reprovision"]') as HTMLButtonElement).click();
    for (let i = 0; i < 4; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    (document.querySelector('[data-test="confirm-ok"]') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(bridge.vaultDelete).toHaveBeenCalled());
  };
  return { bridge, store, status, workerRestart, confirmReprovision };
}

describe('Reprovision forgets Discord only and keeps Scout running for the chat that still counts', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    localStorage.clear();
  });

  for (const remaining of ['Slack', 'this computer only'] as const) {
    it(`with ${remaining} still set up, Scout is running again after Reprovision`, async () => {
      const { store, status, workerRestart, confirmReprovision } =
        await renderWithDiscordAnd(remaining);

      await confirmReprovision();

      await vi.waitFor(() =>
        expect({
          discord: store.hasDiscord(),
          worker: status().state,
          restartedThroughMain: workerRestart.mock.calls.length,
        }).toEqual({
          discord: false,
          worker: 'running',
          restartedThroughMain: 1,
        }),
      );
    });
  }

  it('with Slack still set up, a restart that fails after Reprovision says why', async () => {
    const { bridge, store, confirmReprovision } = await renderWithDiscordAnd('Slack');
    const show = vi
      .spyOn(TestBed.inject(NoticeService), 'show')
      .mockImplementation(() => undefined);
    bridge.workerStart.mockRejectedValueOnce(
      new Error('[scout:worker/binary-missing] Worker binary not found.'),
    );

    await confirmReprovision();

    await vi.waitFor(() => expect(show).toHaveBeenCalled());
    expect({ discord: store.hasDiscord(), notice: show.mock.calls[0] }).toEqual({
      discord: false,
      notice: [
        'Scout could not restart: [scout:worker/binary-missing] Worker binary not found.',
        'error',
      ],
    });
  });
});

describe("The wizard's Ready page follows the theme and the prototype", () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  for (const file of ['src/app/wizard/ready/ready.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }

  it('Restart, Stop and Start over show their icon before their text, as the prototype draws them', async () => {
    const { fixture } = await setup({ guildName: 'G' });

    expect(
      iconAndText(fixture.nativeElement as HTMLElement, [
        '[data-test="restart"]',
        '[data-test="stop"]',
        '[data-test="reprovision"]',
      ]),
    ).toEqual([
      ['sc-icon', 'Restart'],
      ['sc-icon', 'Stop'],
      ['sc-icon', 'Start over'],
    ]);
  });
});

/** The worker chip of the process section: its tone class and the word it reads. */
function chip(root: HTMLElement): (string | undefined)[] {
  const badge = root.querySelector('[data-test="process"] [data-test="worker-chip"]');
  return [
    [...(badge?.classList ?? [])].find((name) => name.startsWith('badge--')),
    badge?.textContent?.trim(),
  ];
}

describe('The Ready step as in the prototype: setup complete, the process section and Reset open', () => {
  const tokens = document.documentElement.style;
  const PAINT = {
    '--sc-ok-container': 'rgb(22, 51, 42)',
    '--sc-on-ok-container': 'rgb(126, 214, 166)',
    '--sc-error-container': 'rgb(58, 28, 27)',
    '--sc-error': 'rgb(242, 139, 130)',
    '--sc-surface-2': 'rgb(26, 36, 54)',
    '--sc-fg-2': 'rgb(195, 202, 219)',
    '--sc-accent': 'rgb(143, 178, 249)',
    '--sc-info-container': 'rgb(23, 34, 58)',
    '--sc-on-info-container': 'rgb(200, 210, 240)',
  } as const;

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    for (const [name, value] of Object.entries(PAINT)) tokens.setProperty(name, value);
  });

  afterEach(() => {
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
    for (const name of Object.keys(PAINT)) tokens.removeProperty(name);
  });

  const rows: readonly {
    readonly state: WorkerStatus['state'];
    readonly guild: string | undefined;
    readonly model: readonly [string, string] | 'None' | 'Ollama, no model' | null;
    readonly summary: string;
    readonly chip: readonly [string, string];
    readonly paint: readonly [keyof typeof PAINT, keyof typeof PAINT];
    readonly processOpen: boolean;
  }[] = [
    {
      state: 'running',
      guild: 'Acme Engineering',
      model: ['ChatGptPlan', 'gpt-5.6-luna'],
      summary: 'Scout is running in Acme Engineering and answers with ChatGPT plan · gpt-5.6-luna.',
      chip: ['badge--running', 'Running'],
      paint: ['--sc-ok-container', '--sc-on-ok-container'],
      processOpen: false,
    },
    {
      state: 'running',
      guild: 'Acme Engineering',
      model: null,
      summary: 'Scout is running in Acme Engineering.',
      chip: ['badge--running', 'Running'],
      paint: ['--sc-ok-container', '--sc-on-ok-container'],
      processOpen: false,
    },
    {
      state: 'running',
      guild: 'Acme Engineering',
      model: 'None',
      summary: 'Scout is running in Acme Engineering.',
      chip: ['badge--running', 'Running'],
      paint: ['--sc-ok-container', '--sc-on-ok-container'],
      processOpen: false,
    },
    {
      state: 'running',
      guild: 'Acme Engineering',
      model: 'Ollama, no model',
      summary: 'Scout is running in Acme Engineering.',
      chip: ['badge--running', 'Running'],
      paint: ['--sc-ok-container', '--sc-on-ok-container'],
      processOpen: false,
    },
    {
      state: 'running',
      guild: undefined,
      model: ['Ollama', 'llama3.1'],
      summary: 'Scout is running and answers with Ollama · llama3.1.',
      chip: ['badge--running', 'Running'],
      paint: ['--sc-ok-container', '--sc-on-ok-container'],
      processOpen: false,
    },
    {
      state: 'stopped',
      guild: 'Acme Engineering',
      model: ['Ollama', 'llama3.1'],
      summary: 'Scout is set up in Acme Engineering.',
      chip: ['badge--stopped', 'Stopped'],
      paint: ['--sc-surface-2', '--sc-fg-2'],
      processOpen: true,
    },
    {
      state: 'crashed',
      guild: 'Acme Engineering',
      model: null,
      summary: 'Scout is set up in Acme Engineering.',
      chip: ['badge--crashed', 'Crashed'],
      paint: ['--sc-error-container', '--sc-error'],
      processOpen: true,
    },
    {
      state: 'starting',
      guild: undefined,
      model: null,
      summary: 'Scout is set up.',
      chip: ['badge--starting', 'Starting'],
      paint: ['--sc-surface-2', '--sc-accent'],
      processOpen: true,
    },
    {
      state: 'idle',
      guild: undefined,
      model: null,
      summary: 'Scout is set up.',
      chip: ['badge--idle', 'Idle'],
      paint: ['--sc-surface-2', '--sc-fg-2'],
      processOpen: true,
    },
  ];

  for (const row of rows) {
    it(`worker ${row.state}, ${row.guild ?? 'no server known'}, ${row.model === null ? 'no AI Source chosen' : row.model === 'None' ? 'the AI Source set to None' : row.model === 'Ollama, no model' ? 'Ollama chosen with no model' : row.model.join(' ')}: "Setup is complete" with "${row.summary}", ${typeof row.model === 'object' && row.model !== null ? 'no banner' : 'the no-model banner'}, the ${row.chip[1]} chip on ${row.paint[0]}, the process section ${row.processOpen ? 'open' : 'closed'}`, async () => {
      const { fixture, bridge } = await setup({
        initialStatus: { state: row.state, restarts: 0 },
        ...(row.guild === undefined ? {} : { guildName: row.guild }),
      });
      const llm = TestBed.inject(LlmConfigStore);
      if (row.model === 'None' || row.model === 'Ollama, no model') {
        const provider = row.model === 'None' ? 'None' : 'Ollama';
        Object.assign(bridge, {
          vaultGetPublic: vi.fn(async ({ key }: { key: string }) => ({
            value: key === 'llm.provider' ? provider : null,
          })),
        });
        await llm.loadFromVault();
      } else if (row.model !== null) {
        await llm.setSelection(row.model[0] as 'Ollama', row.model[1], 'http://localhost:11434');
      }
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      const read = (selector: string): string | undefined =>
        root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
      const column = getComputedStyle(root.querySelector('main') as HTMLElement);
      const process = root.querySelector('[data-test="process"]') as HTMLDetailsElement;
      const startOver = process.querySelector('[data-test="reprovision"]') as HTMLElement;

      expect({
        title: read('h1'),
        lede: read('.lede'),
        column: [column.maxWidth, column.marginLeft, column.marginRight],
        complete: [
          read('[data-test="setup-complete"] .complete-title'),
          read('[data-test="setup-complete"] [data-test="setup-summary"]'),
          root.querySelector('[data-test="setup-complete"] .complete-mark sc-icon') !== null,
        ],
        process: [process.querySelector('summary')?.textContent?.trim(), process.open],
        banner: root.querySelector('[data-test="no-llm-banner"]') !== null,
        chip: chip(root),
        paint: [
          getComputedStyle(process.querySelector('[data-test="worker-chip"]') as HTMLElement)
            .backgroundColor,
          getComputedStyle(process.querySelector('[data-test="worker-chip"]') as HTMLElement).color,
        ],
        chipIcon: process
          .querySelector('[data-test="worker-chip"]')
          ?.firstElementChild?.tagName.toLowerCase(),
        meta: read('[data-test="process"] .meta'),
        startOver: [
          read('[data-test="process"] .start-over-title'),
          read('[data-test="process"] .start-over .muted'),
          startOver.textContent?.trim(),
          startOver.classList.contains('mat-mdc-outlined-button'),
          startOver.classList.contains('danger'),
        ],
        home: read('[data-test="back-home"]'),
        provider: llm.provider(),
      }).toEqual({
        title: 'You are all set',
        lede: 'Mention @Scout in your server, or chat with it in this window.',
        column: ['760px', 'auto', 'auto'],
        complete: ['Setup is complete', row.summary, true],
        process: ['Scout process and start over', row.processOpen],
        banner: row.model === null || typeof row.model === 'string',
        chip: row.chip,
        paint: [PAINT[row.paint[0]], PAINT[row.paint[1]]],
        chipIcon: 'sc-icon',
        meta: 'Uptime — · Restarts 0',
        startOver: [
          'Start over with another bot',
          "Removes this bot's token from Scout and runs setup again.",
          'Start over',
          true,
          true,
        ],
        home: 'Home',
        provider:
          row.model === null
            ? null
            : row.model === 'None'
              ? 'None'
              : row.model === 'Ollama, no model'
                ? 'Ollama'
                : row.model[0],
      });
    });
  }

  const toggles: readonly {
    readonly name: string;
    readonly start: WorkerStatus['state'];
    readonly ownerClicks: boolean;
    readonly then: readonly WorkerStatus['state'][];
    readonly open: readonly boolean[];
  }[] = [
    {
      name: 'opened by the owner while Scout runs, then Restart: it stays open while Scout starts and once it runs again',
      start: 'running',
      ownerClicks: true,
      then: ['starting', 'running'],
      open: [false, true, true, true],
    },
    {
      name: 'closed by the owner after a crash: it stays closed when Scout runs again and when it crashes again',
      start: 'crashed',
      ownerClicks: true,
      then: ['running', 'crashed'],
      open: [true, false, false, false],
    },
    {
      name: 'never touched: it opens when Scout crashes and closes when Scout runs again',
      start: 'running',
      ownerClicks: false,
      then: ['crashed', 'running'],
      open: [false, false, true, false],
    },
  ];

  for (const row of toggles) {
    it(`the process section ${row.name}`, async () => {
      const { fixture, logStore } = await setup({
        initialStatus: { state: row.start, restarts: 0 },
      });
      const root = fixture.nativeElement as HTMLElement;
      const process = root.querySelector('[data-test="process"]') as HTMLDetailsElement;
      const open = [process.open];

      if (row.ownerClicks) (process.querySelector('summary') as HTMLElement).click();
      fixture.detectChanges();
      open.push(process.open);
      if (row.then[0] === 'starting') {
        (root.querySelector('[data-test="restart"]') as HTMLButtonElement).click();
      }
      for (const state of row.then) {
        logStore._setStatus({ state, restarts: 0 });
        fixture.detectChanges();
        await fixture.whenStable();
        open.push(process.open);
      }

      expect(open).toEqual(row.open);
    });
  }

  it('Reset open: Start over asks "Start over with another bot?", says what is removed and what stays, and offers Cancel and Start over; Cancel closes it and changes nothing', async () => {
    const { fixture, bridge, store } = await setup({ guildName: 'Acme Engineering' });
    const reprovision = vi.spyOn(store, 'reprovision');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');
    const dialogs = TestBed.inject(MatDialog);

    (fixture.nativeElement.querySelector('[data-test="reprovision"]') as HTMLButtonElement).click();
    const asked = dialogs.openDialogs[0]!;
    await firstValueFrom(asked.afterOpened());
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = document.querySelector('sc-confirm-dialog') as HTMLElement;
    const open = {
      role: dialog.closest('[role]')?.getAttribute('role'),
      title: dialog.querySelector('h2')?.textContent?.trim(),
      body: dialog.querySelector('.body')?.textContent?.trim(),
      buttons: Array.from(dialog.querySelectorAll('button'), (button) => [
        button.textContent?.trim(),
        button.classList.contains('mat-mdc-unelevated-button'),
      ]),
    };
    (document.querySelector('[data-test="confirm-cancel"]') as HTMLButtonElement).click();
    await firstValueFrom(asked.afterClosed(), { defaultValue: undefined });
    fixture.detectChanges();
    await fixture.whenStable();

    expect({
      open,
      afterCancel: {
        dialog: document.querySelector('sc-confirm-dialog'),
        stillOpen: dialogs.openDialogs.length,
        tokenRemoved: reprovision.mock.calls.length,
        stopped: vi.mocked(bridge.workerStop).mock.calls.length,
        left: nav.mock.calls.length,
        server: store.guild()?.name,
        chip: chip(fixture.nativeElement as HTMLElement),
      },
    }).toEqual({
      open: {
        role: 'alertdialog',
        title: 'Start over with another bot?',
        body: 'The bot token is removed from the keyring and setup starts again from the first step. Scout stops unless Slack or this computer still use it. The bot stays in your Discord server until you remove it there.',
        buttons: [
          ['Cancel', false],
          ['Start over', true],
        ],
      },
      afterCancel: {
        dialog: null,
        stillOpen: 0,
        tokenRemoved: 0,
        stopped: 0,
        left: 0,
        server: 'Acme Engineering',
        chip: ['badge--running', 'Running'],
      },
    });
  });
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
