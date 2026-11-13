import { readFileSync } from 'node:fs';

import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  ScoutError,
  type DetectClientsResponse,
  type DetectedClient,
  type WorkerStatus,
} from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { appRoutes } from '../../app.routes';
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
  slackConnected = false;
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

  slackStatus(): Promise<{ connected: boolean; team: string | null; botUser: string | null }> {
    return Promise.resolve({ connected: this.slackConnected, team: null, botUser: null });
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
  readonly slackConnected?: boolean;
}

async function createComponent(
  bridge: FakeBridge,
  opts: SetupOpts = {},
): Promise<{
  fixture: ComponentFixture<WelcomeComponent>;
  logStore: ReturnType<typeof makeLogStore>;
}> {
  bridge.vaultHasResult = { exists: opts.vaultHas ?? false };
  bridge.slackConnected = opts.slackConnected ?? false;
  const logStore = makeLogStore(opts.workerStatus);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: 'provision', children: [] },
        { path: 'oauth', children: [] },
        { path: 'ready', children: [] },
        { path: 'slack', children: [] },
        { path: 'logs', children: [] },
        { path: 'settings', children: [] },
        { path: 'chat', children: [] },
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

function chooseAndContinue(
  fixture: ComponentFixture<WelcomeComponent>,
  kind: 'discord' | 'slack',
): void {
  (fixture.nativeElement.querySelector(`[data-test="choose-${kind}"]`) as HTMLInputElement).click();
  fixture.detectChanges();
  (fixture.nativeElement.querySelector('[data-test="wizard-continue"]') as HTMLElement).click();
}

async function flushBootstrap(fixture: ComponentFixture<WelcomeComponent>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
  await fixture.whenStable();
  fixture.detectChanges();
}

/** The vault after the Bot token step: the token and the application id saved, no server chosen yet. */
async function tokenSavedNoServer({ key }: { key: string }): Promise<{ exists: boolean }> {
  return { exists: key === 'discord.bot-token' || key === 'discord.client-id' };
}

describe('WelcomeComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('lets a new owner with no chat platform skip setup: the worker starts and the Chat screen opens', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({ clients: [] });
    await flushBootstrap(fixture);
    const skip = fixture.nativeElement.querySelector(
      '[data-test="use-local"]',
    ) as HTMLButtonElement;

    expect(skip.textContent?.trim()).toBe('Use on this computer');
    expect(
      Array.from(
        fixture.nativeElement.querySelectorAll('[data-test="no-chat-app"] .local-text span'),
      ).map((span) => (span as HTMLElement).textContent?.replace(/\s+/g, ' ').trim()),
    ).toEqual([
      'No chat app?',
      'Chat with Scout in its own window. You can connect Discord or Slack later in Settings.',
    ]);
    skip.click();
    await flushBootstrap(fixture);

    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(TestBed.inject(Router).url).toBe('/chat');
    expect(TestBed.inject(WizardStore).hasProvisioning()).toBe(true);
  });

  it('after skipping once, the next launch starts the worker with no Discord or Slack', async () => {
    localStorage.setItem('scout:local-only', '1');
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({ clients: [] });
    await flushBootstrap(fixture);

    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.querySelector('[data-test="use-local"]')).toBeNull();
  });

  it('with no external chat configured, the app opens on the Chat screen', async () => {
    localStorage.setItem('scout:local-only', '1');
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({ clients: [] });
    await flushBootstrap(fixture);

    expect(TestBed.inject(Router).url).toBe('/chat');
  });

  it('with Discord set up, a bot token and a chosen server, the app keeps opening on the welcome screen', async () => {
    localStorage.setItem('scout:local-only', '1');
    const bridge = new FakeBridge();
    Object.assign(bridge, {
      vaultHas: async ({ key }: { key: string }) => ({
        exists: ['discord.bot-token', 'discord.guild-id'].includes(key),
      }),
    });
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({ clients: [] });
    await flushBootstrap(fixture);

    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('opens Chat only at launch: a later visit to the welcome screen does not jump to Chat again', async () => {
    localStorage.setItem('scout:local-only', '1');
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({ clients: [] });
    await flushBootstrap(fixture);
    await TestBed.inject(Router).navigateByUrl('/logs');
    fixture.destroy();

    const again = TestBed.createComponent(WelcomeComponent);
    again.detectChanges();
    bridge.pending?.resolve({ clients: [] });
    await flushBootstrap(again);

    expect(TestBed.inject(Router).url).toBe('/logs');
  });

  it('does not offer the skip once a chat platform is connected', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge, { slackConnected: true });
    bridge.pending?.resolve({ clients: [] });
    await flushBootstrap(fixture);

    expect(fixture.nativeElement.querySelector('[data-test="use-local"]')).toBeNull();
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

  it('navigates to /provision on choosing Discord and Continue when Discord is idle', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');

    chooseAndContinue(fixture, 'discord');
    expect(nav).toHaveBeenCalledWith('/provision');
    expect(TestBed.inject(WizardStore).currentStep()).toBe('provision');
  });

  it('Discord is chosen at first, so Continue alone sets it up', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');

    (fixture.nativeElement.querySelector('[data-test="wizard-continue"]') as HTMLElement).click();
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
    await flushBootstrap(fixture);

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

  it('reopening with a bot token saved but no server chosen does not start the worker', async () => {
    const bridge = new FakeBridge();
    Object.assign(bridge, { vaultHas: tokenSavedNoServer });
    const { fixture } = await createComponent(bridge, {
      persistent: null,
      workerStatus: { state: 'idle', restarts: 0 },
    });
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await flushBootstrap(fixture);

    expect(bridge.workerStart).not.toHaveBeenCalled();
  });

  it('reopening the app after quitting on Add to server resumes on Add to server', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: new FakeBridge() },
      ],
    });
    const before = TestBed.inject(WizardStore);
    before.setClientId('111222333');
    before.goTo('oauth');
    TestBed.resetTestingModule();

    const bridge = new FakeBridge();
    Object.assign(bridge, { vaultHas: tokenSavedNoServer });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter(appRoutes),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: LogStore, useValue: makeLogStore() },
      ],
    });
    const harness = await RouterTestingHarness.create('/');
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await new Promise((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/oauth');
  });

  for (const [owner, landing] of [
    ['a Slack owner', '/'],
    ['an owner who uses Scout only on this computer', '/chat'],
  ] as const) {
    it(`${owner} who left Add to server through the sidebar reopens on ${landing}, not on the setup step`, async () => {
      if (owner !== 'a Slack owner') localStorage.setItem('scout:local-only', '1');
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          { provide: ScoutBridgeService, useValue: new FakeBridge() },
        ],
      });
      const before = TestBed.inject(WizardStore);
      before.setClientId('111222333');
      before.goTo('oauth');
      TestBed.resetTestingModule();

      const bridge = new FakeBridge();
      Object.assign(bridge, { vaultHas: tokenSavedNoServer });
      const { fixture } = await createComponent(bridge, {
        slackConnected: owner === 'a Slack owner',
        workerStatus: { state: 'running', restarts: 0, pid: 1 },
      });
      bridge.pending?.resolve({ clients: [] });
      await flushBootstrap(fixture);

      expect(TestBed.inject(Router).url).toBe(landing);
    });
  }

  it('a Slack owner whose Discord token has no server still gets the worker started', async () => {
    const bridge = new FakeBridge();
    Object.assign(bridge, { vaultHas: tokenSavedNoServer });
    const { fixture } = await createComponent(bridge, {
      slackConnected: true,
      workerStatus: { state: 'idle', restarts: 0 },
    });
    bridge.pending?.resolve({ clients: [] });
    await flushBootstrap(fixture);

    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('with a Discord token kept without a server, Continue on Discord opens Add to server with the application id main kept', async () => {
    const bridge = new FakeBridge();
    Object.assign(bridge, {
      vaultHas: tokenSavedNoServer,
      vaultGetPublic: async ({ key }: { key: string }) => ({
        value: key === 'discord.client-id' ? 'app-9' : null,
      }),
    });
    const { fixture } = await createComponent(bridge, {
      workerStatus: { state: 'idle', restarts: 0 },
    });
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await flushBootstrap(fixture);

    chooseAndContinue(fixture, 'discord');
    await flushBootstrap(fixture);

    expect({
      url: TestBed.inject(Router).url,
      clientId: TestBed.inject(WizardStore).clientId(),
      started: bridge.workerStart.mock.calls.length,
    }).toEqual({ url: '/oauth', clientId: 'app-9', started: 0 });
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
    await flushBootstrap(fixture);

    const badge = fixture.nativeElement.querySelector('[data-test="discord-badge"]');
    expect((badge?.textContent ?? '').trim()).toContain('Connected');
    const caption = fixture.nativeElement.querySelector('[data-test="discord-caption"]');
    expect(caption?.textContent?.trim()).toBe('My Guild');
  });

  it('Continue with a connected Discord opens Settings instead of the retired Status screen', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge, {
      vaultHas: true,
      persistent: { clientId: 'c', guildId: 'g', guildName: 'My Guild' },
      workerStatus: { state: 'running', restarts: 0, pid: 1 },
    });
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await flushBootstrap(fixture);

    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');
    chooseAndContinue(fixture, 'discord');
    expect(nav).toHaveBeenCalledWith('/settings');
    expect(nav).not.toHaveBeenCalledWith('/status');
    expect(nav).not.toHaveBeenCalledWith('/ready');
  });

  it('Continue with a disconnected Discord triggers workerStart instead of navigating', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge, {
      vaultHas: true,
      persistent: { clientId: 'c', guildId: 'g', guildName: 'My Guild' },
      workerStatus: { state: 'stopped', restarts: 0 },
    });
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await flushBootstrap(fixture);

    // Initial boot does not trigger workerStart when status != 'idle'.
    expect(bridge.workerStart).not.toHaveBeenCalled();

    chooseAndContinue(fixture, 'discord');
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('choosing Slack and Continue opens the Slack setup', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({ clients: [] });
    await fixture.whenStable();
    fixture.detectChanges();

    const nav = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');
    chooseAndContinue(fixture, 'slack');

    expect(nav).toHaveBeenCalledWith('/slack');
  });

  it('Slack only: starts the worker, shows Slack connected and leaves the Discord card unprovisioned', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge, {
      vaultHas: false,
      slackConnected: true,
      workerStatus: { state: 'idle', restarts: 0 },
    });
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await flushBootstrap(fixture);

    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    const slackBadge = fixture.nativeElement.querySelector('[data-test="slack-badge"]');
    expect(slackBadge?.textContent?.trim()).toBe('Connected');
    const discordBadge = fixture.nativeElement.querySelector('[data-test="discord-badge"]');
    expect(discordBadge?.textContent?.trim()).toBe('Installed');
  });

  it('offers no skip while clients are still being detected, so the worker cannot be started twice', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    await flushBootstrap(fixture);

    expect(fixture.nativeElement.querySelector('[data-test="use-local"]')).toBeNull();
    const next = fixture.nativeElement.querySelector(
      '[data-test="wizard-continue"]',
    ) as HTMLButtonElement;
    expect([
      next.disabled,
      fixture.nativeElement.querySelector('[data-test="wizard-blocked"]')?.textContent,
    ]).toEqual([true, 'Detecting the chat apps on this computer.']);

    bridge.pending?.resolve({ clients: [] });
    await flushBootstrap(fixture);
    expect(next.disabled).toBe(false);

    expect(fixture.nativeElement.querySelector('[data-test="use-local"]')).not.toBeNull();
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

describe("The wizard's Welcome page follows the theme and the prototype", () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const file of ['src/app/wizard/welcome/welcome.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }
});

describe('The Chat app step as in the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('asks where you chat with Scout and offers the three chat apps as one radio group with Continue and no Back; the step count is in the setup header, not under the title', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({
      clients: [{ kind: 'discord', installed: true, method: 'uri-scheme' }],
    });
    await flushBootstrap(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const group = root.querySelector('[role="radiogroup"]') as HTMLElement;

    expect({
      title: root.querySelector('h1')?.textContent?.trim(),
      counter: root.querySelector('[data-test="step-counter"]'),
      lede: root.querySelector('.lede')?.textContent?.trim(),
      group: group.getAttribute('aria-label'),
      apps: Array.from(group.querySelectorAll('article h2')).map((h) => h.textContent?.trim()),
      radios: Array.from(group.querySelectorAll('input[type="radio"]')).map((r) =>
        r.closest('label')?.textContent?.trim(),
      ),
      back: root.querySelector('[data-test="wizard-back"]'),
      next: root.querySelector('[data-test="wizard-continue"]')?.textContent?.trim(),
    }).toEqual({
      title: 'Choose where you chat with Scout',
      counter: null,
      lede: 'Pick the chat app Scout joins. You can add another one later in Settings.',
      group: 'Chat app',
      apps: ['Discord', 'Slack', 'Microsoft Teams'],
      radios: ['Chosen', 'Choose Slack'],
      back: null,
      next: 'Continue',
    });
  });

  it('choosing Slack marks it Chosen and Discord Choose Discord', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({ clients: [] });
    await flushBootstrap(fixture);

    (fixture.nativeElement.querySelector('[data-test="choose-slack"]') as HTMLInputElement).click();
    fixture.detectChanges();

    expect(
      Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll('[role="radiogroup"] label'),
      ).map((l) => l.textContent?.trim()),
    ).toEqual(['Choose Discord', 'Chosen']);
  });

  it('the step is one centred column 760 px wide, and "Use on this computer" is an outlined button under the text of the No chat app box', async () => {
    const bridge = new FakeBridge();
    const { fixture } = await createComponent(bridge);
    bridge.pending?.resolve({ clients: [] });
    await flushBootstrap(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const column = getComputedStyle(root.querySelector('main') as HTMLElement);
    const box = root.querySelector('[data-test="no-chat-app"]') as HTMLElement;
    const button = box.querySelector('[data-test="use-local"]') as HTMLElement;

    expect({
      column: [column.maxWidth, column.marginLeft, column.marginRight],
      box: [
        getComputedStyle(box).flexDirection,
        Array.from(box.children, (child) => child.tagName.toLowerCase()),
      ],
      text: Array.from(box.querySelectorAll('.local-text span'), (line) =>
        (line.textContent ?? '').replace(/\s+/g, ' ').trim(),
      ),
      button: [
        button.textContent?.trim(),
        button.classList.contains('mat-mdc-outlined-button'),
        button.querySelector('.mdc-button__label')?.firstElementChild?.tagName.toLowerCase(),
      ],
    }).toEqual({
      column: ['760px', 'auto', 'auto'],
      box: ['column', ['div', 'button']],
      text: [
        'No chat app?',
        'Chat with Scout in its own window. You can connect Discord or Slack later in Settings.',
      ],
      button: ['Use on this computer', true, 'sc-icon'],
    });
  });
});
