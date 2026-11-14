import { readFileSync } from 'node:fs';

import { InteractivityChecker } from '@angular/cdk/a11y';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  RESTART_DEFERRED_LINE,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
} from '@scout/shared';
import type {
  AccessAllowedUsers,
  AccessPairing,
  DesktopSettings,
  DesktopToggleRequest,
  LocalBubbleSettings,
  LocalBubbleStatus,
  LocalShortcut,
  ThemeChoice,
  ThemeState,
  UpdateNotice,
  UpdateProgress,
  UpdateState,
  UpdateVersion,
  UpdateVersionList,
  WorkerEvent,
  WorkerStatus,
} from '@scout/shared';

import { LAID_OUT } from '../../core/laid-out.testing';
import { LogStore } from '../../core/log.store';
import { restartAsMain } from '../../core/main-restart.testing';
import { MenuPreferences } from '../../core/menu-preferences';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { ThemeStore } from '../../core/theme.store';
import { WizardStore } from '../../core/wizard.store';

import { PostConnectShellComponent } from '../../shell/post-connect-shell.component';
import { SettingsComponent } from './settings.component';

const OWNER_MONITORS = [
  { id: 35, name: 'Built-in display 1920×1080', primary: false },
  { id: 33, name: 'External display 3440×1440', primary: true },
];

function version(v: string, prerelease = false): UpdateVersion {
  return {
    version: v,
    publishedAt: '2026-11-07T10:00:00.000Z',
    prerelease,
    releaseUrl: `https://github.com/vitorafgomes/scout/releases/tag/v${v}`,
    downloadUrl: `https://github.com/vitorafgomes/scout/releases/download/v${v}/Scout-Desktop-${v}-linux-x86_64.AppImage`,
  };
}

function makeBridge(
  initial: readonly string[] = [],
  desktop: DesktopSettings = { keepRunning: true, startAtLogin: false, trayAvailable: true },
  update: UpdateState = {
    checkEnabled: true,
    channel: 'stable',
    lastCheckAt: '2026-11-02T09:30:00.000Z',
    lastResult: 'up-to-date',
    notice: null,
    packaged: true,
  },
) {
  let stored: readonly string[] = initial;
  let desktopSettings = desktop;
  let updateState = update;
  const workerListeners: ((event: WorkerEvent) => void)[] = [];
  const bubbleListeners: ((status: LocalBubbleStatus) => void)[] = [];
  const noticeListeners: ((notice: UpdateNotice | null) => void)[] = [];
  const progressListeners: ((progress: UpdateProgress) => void)[] = [];
  const workerCalls: string[] = [];
  const bridge = {
    workerCalls,
    emitNotice: (notice: UpdateNotice | null) => noticeListeners.forEach((l) => l(notice)),
    emitProgress: (progress: UpdateProgress) => progressListeners.forEach((l) => l(progress)),
    onUpdateNotice: vi.fn((listener: (notice: UpdateNotice | null) => void) => {
      noticeListeners.push(listener);
      return () => noticeListeners.splice(noticeListeners.indexOf(listener), 1);
    }),
    onUpdateProgress: vi.fn((listener: (progress: UpdateProgress) => void) => {
      progressListeners.push(listener);
      return () => progressListeners.splice(progressListeners.indexOf(listener), 1);
    }),
    updateInstall: vi.fn(async (): Promise<UpdateState> => updateState),
    updateListVersions: vi.fn(async (): Promise<UpdateVersionList> => ({
      versions: [version('1.1.0'), version('1.0.0'), version('0.9.0')],
      canInstall: true,
      failure: null,
    })),
    updateInstallVersion: vi.fn(
      async (_req: { version: string }): Promise<UpdateState> => updateState,
    ),
    updateSetChannel: vi.fn(
      async (req: { channel: UpdateState['channel'] }): Promise<UpdateState> => {
        updateState = { ...updateState, channel: req.channel, notice: null };
        return updateState;
      },
    ),
    bubbleListeners,
    emitBubble: (status: LocalBubbleStatus) => bubbleListeners.forEach((l) => l(status)),
    onLocalBubble: vi.fn((listener: (status: LocalBubbleStatus) => void) => {
      bubbleListeners.push(listener);
      return () => bubbleListeners.splice(bubbleListeners.indexOf(listener), 1);
    }),
    workerStart: vi.fn(async () => {
      workerCalls.push('start');
      return { pid: 4242 };
    }),
    workerStop: vi.fn(async () => {
      workerCalls.push('stop');
      return { exitCode: 0 };
    }),
    workerStatus: vi.fn(async (): Promise<WorkerStatus> => ({ state: 'running', restarts: 0 })),
    localListening: vi.fn(async () => ({ listening: false })),
    emitWorker: (event: WorkerEvent) => workerListeners.forEach((listener) => listener(event)),
    onWorkerEvent: vi.fn((listener: (event: WorkerEvent) => void) => {
      workerListeners.push(listener);
      return () => undefined;
    }),
    appGetVersion: vi.fn(async () => ({ version: '1.0.0' })),
    noticeRecord: vi.fn(async (_req: unknown) => undefined),
    appOpenExternal: vi.fn(async (_req: { url: string }) => undefined),
    slackStatus: vi.fn(async () => ({ connected: true, team: 'Acme', botUser: 'scout' })),
    accessGet: vi.fn(async (): Promise<AccessAllowedUsers> => ({ userIds: stored })),
    accessSet: vi.fn(async (req: AccessAllowedUsers): Promise<AccessAllowedUsers> => {
      stored = [...new Set(req.userIds.map((id) => id.trim()))];
      return { userIds: stored };
    }),
    accessStartPairing: vi.fn(async (): Promise<AccessPairing> => ({
      code: '482913',
      expiresAt: '2026-10-28T12:10:00.000Z',
    })),
    desktopGetSettings: vi.fn(async (): Promise<DesktopSettings> => desktopSettings),
    desktopSetKeepRunning: vi.fn(async (req: DesktopToggleRequest): Promise<DesktopSettings> => {
      desktopSettings = { ...desktopSettings, keepRunning: req.enabled };
      return desktopSettings;
    }),
    desktopSetStartAtLogin: vi.fn(async (req: DesktopToggleRequest): Promise<DesktopSettings> => {
      desktopSettings = { ...desktopSettings, startAtLogin: req.enabled };
      return desktopSettings;
    }),
    localGetShortcut: vi.fn(async (): Promise<LocalShortcut> => ({
      accelerator: 'CommandOrControl+Alt+Space',
      registered: true,
      hint: null,
    })),
    localSuspendShortcut: vi.fn(async (_req: { suspended: boolean }): Promise<LocalShortcut> => ({
      accelerator: 'CommandOrControl+Alt+Space',
      registered: true,
      hint: null,
    })),
    localSetShortcut: vi.fn(async (req: { accelerator: string }): Promise<LocalShortcut> => ({
      accelerator: req.accelerator,
      registered: true,
      hint: null,
    })),
    localGetBubble: vi.fn(async (): Promise<LocalBubbleStatus> => ({
      enabled: true,
      corner: 'bottom-right',
      display: 'cursor',
      monitors: OWNER_MONITORS,
      hint: null,
    })),
    localSetBubble: vi.fn(async (req: LocalBubbleSettings): Promise<LocalBubbleStatus> => ({
      ...req,
      monitors: OWNER_MONITORS,
      hint: null,
    })),
    updateGetState: vi.fn(async (): Promise<UpdateState> => updateState),
    updateSetCheckEnabled: vi.fn(async (req: DesktopToggleRequest): Promise<UpdateState> => {
      updateState = { ...updateState, checkEnabled: req.enabled };
      return updateState;
    }),
    updateCheckNow: vi.fn(async (): Promise<UpdateState> => {
      updateState = {
        ...updateState,
        lastCheckAt: '2026-11-02T10:45:00.000Z',
        lastResult: 'available 0.2.0',
      };
      return updateState;
    }),
  };
  return Object.assign(bridge, { workerRestart: vi.fn(() => restartAsMain(bridge)) });
}

function makeWizard(clients: { discordServer?: string | null; slack?: boolean } = { slack: true }) {
  const discord = clients.discordServer !== undefined && clients.discordServer !== null;
  return {
    hasDiscord: signal(discord),
    discordNeedsServer: signal(false),
    guild: signal(discord ? { id: '482913', name: clients.discordServer } : null),
    slackConnected: signal(clients.slack ?? false),
    hasProvisioning: signal(true),
    setSlackConnected: vi.fn(),
    goTo: vi.fn(),
    reprovision: vi.fn(async () => undefined),
  };
}

async function render(
  bridge: ReturnType<typeof makeBridge>,
  wizard: ReturnType<typeof makeWizard> = makeWizard(),
  worker: WorkerStatus = { state: 'running', restarts: 0 },
): Promise<ComponentFixture<SettingsComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: WizardStore, useValue: wizard },
      { provide: LogStore, useValue: { status: signal<WorkerStatus>(worker) } },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
    ],
  });
  const fixture = TestBed.createComponent(SettingsComponent);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<SettingsComponent>): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el(fixture: ComponentFixture<SettingsComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function text(fixture: ComponentFixture<SettingsComponent>, test: string): string {
  return el(fixture, test)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function listed(fixture: ComponentFixture<SettingsComponent>): string[] {
  return Array.from(
    fixture.nativeElement.querySelectorAll(
      '[data-test^="access-user-"]',
    ) as NodeListOf<HTMLElement>,
  ).map((node) => node.textContent?.trim() ?? '');
}

async function openAbout(fixture: ComponentFixture<SettingsComponent>): Promise<void> {
  el(fixture, 'about-toggle')!.click();
  await settle(fixture);
}

/** The switch inside a slide toggle, the element the owner presses and a screen reader reads. */
function switchIn(fixture: ComponentFixture<SettingsComponent>, test: string): HTMLButtonElement {
  return el(fixture, test)!.querySelector('button[role="switch"]') as HTMLButtonElement;
}

function checked(fixture: ComponentFixture<SettingsComponent>, test: string): boolean {
  return switchIn(fixture, test).getAttribute('aria-checked') === 'true';
}

function labelledBy(node: Element): string {
  return (node.getAttribute('aria-labelledby') ?? '')
    .split(' ')
    .map((id) => document.getElementById(id)?.textContent?.replace(/\s+/g, ' ').trim() ?? '')
    .join(' ');
}

async function type(fixture: ComponentFixture<SettingsComponent>, value: string): Promise<void> {
  const input = el(fixture, 'access-input') as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

describe('Settings: Chat connections', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it("opens in the prototype's order, Appearance first, then Chat connections, the worker and About, then who can use Scout, the floating button, Audio, Desktop and Updates", async () => {
    const fixture = await render(makeBridge(), makeWizard({}));

    const headings = Array.from(
      fixture.nativeElement.querySelectorAll('main h2') as NodeListOf<HTMLElement>,
    ).map((node) => node.textContent?.trim());
    const columns = Array.from(
      fixture.nativeElement.querySelectorAll('main .column') as NodeListOf<HTMLElement>,
    ).map((column) =>
      Array.from(column.querySelectorAll('h2') as NodeListOf<HTMLElement>).map((node) =>
        node.textContent?.trim(),
      ),
    );

    expect(headings).toEqual([
      'Appearance',
      'Chat connections',
      'Scout worker',
      'About',
      'Who can use Scout',
      'Floating button and quick ask',
      'Audio',
      'Desktop',
      'Updates',
    ]);
    expect(columns).toEqual([headings.slice(0, 4), headings.slice(4)]);
  });

  it('the Chat connections section has one card each for Discord, Slack and Microsoft Teams, with their state', async () => {
    const fixture = await render(
      makeBridge(),
      makeWizard({ discordServer: 'Ops Guild', slack: false }),
    );

    expect(text(fixture, 'chat-clients-title')).toBe('Chat connections');
    expect(text(fixture, 'discord-state')).toBe('Connected');
    expect(text(fixture, 'discord-server')).toBe('Server: Ops Guild');
    expect(text(fixture, 'slack-state')).toBe('Not connected');
    expect(text(fixture, 'teams-state')).toBe('Not connected');
    expect((el(fixture, 'teams-connect') as HTMLButtonElement).disabled).toBe(false);
  });

  const aboutNotices = (fixture: Awaited<ReturnType<typeof render>>): (string | undefined)[] =>
    Array.from(
      el(fixture, 'trademark-notices')!.querySelectorAll('li') as NodeListOf<HTMLElement>,
    ).map((li) => li.textContent?.replace(/\s+/g, ' ').trim());

  it.each([
    ['OpenAI', 'OpenAI and ChatGPT are trademarks of OpenAI.'],
    ['Google', 'Google and Gemini are trademarks of Google.'],
    ['Anthropic', 'Anthropic and Claude are trademarks of Anthropic.'],
    ['OpenRouter', 'OpenRouter is a trademark of OpenRouter, Inc.'],
    ['Ollama', 'Ollama is a trademark of Ollama, Inc.'],
    ['Element Labs', 'LM Studio is a trademark of Element Labs, Inc.'],
  ])('About names %s as the owner of the logo Scout shows for it', async (_owner, line) => {
    const fixture = await render(makeBridge(), makeWizard({}));
    await openAbout(fixture);

    expect(aboutNotices(fixture)).toContain(line);
  });

  it("About says the logos are not under Scout's licence and imply no endorsement", async () => {
    const fixture = await render(makeBridge(), makeWizard({}));
    await openAbout(fixture);

    expect(aboutNotices(fixture).slice(-2)).toEqual([
      "Third-party logos are not covered by Scout's MIT licence and remain their owners' trademarks.",
      'All other product names and logos are trademarks of their respective owners. Their use identifies the service you connect and does not imply endorsement.',
    ]);
  });

  it('About links to the repository, the changelog, the MIT licence and a private security report through main', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const links = ['about-repository', 'about-changelog', 'about-licence', 'about-security'];

    for (const link of links) el(fixture, link)!.click();
    await settle(fixture);

    expect({
      labels: links.map((link) => text(fixture, link)),
      opened: bridge.appOpenExternal.mock.calls.map(([req]) => req.url),
    }).toEqual({
      labels: ['Repository', 'Changelog', 'MIT licence', 'Report a security problem privately'],
      opened: [
        'https://github.com/vitorafgomes/scout',
        'https://github.com/vitorafgomes/scout/releases',
        'https://github.com/vitorafgomes/scout/blob/main/LICENSE',
        'https://github.com/vitorafgomes/scout/security/advisories/new',
      ],
    });
  });

  it('About names Scout, says what it does, lists the version, the licence and what it is built with, and says where links open', async () => {
    const fixture = await render(makeBridge());
    const card = section(fixture, 'About');
    const facts = Array.from(card.querySelectorAll('dt'), (dt) => [
      dt.textContent?.trim(),
      (dt.nextElementSibling as HTMLElement).textContent?.trim(),
    ]);
    const words = card.textContent?.replace(/\s+/g, ' ') ?? '';

    expect({
      name: card.querySelector('.about-name .name')?.textContent?.trim(),
      description: text(fixture, 'about-description'),
      facts,
      security: words.includes(
        'A private report to the maintainers on GitHub, never a public issue.',
      ),
      browser: words.includes('Links open in your browser.'),
    }).toEqual({
      name: 'Scout',
      description:
        'A chat assistant for Discord, Slack and Teams. It reads your repositories and cloud, reports problems as issues and reviews pull requests. It never writes code.',
      facts: [
        ['Version', '1.0.0'],
        ['Licence', 'MIT, open source'],
        ['Built with', 'Angular, Electron and .NET'],
      ],
      security: true,
      browser: true,
    });
  });

  it('About says why a link main refused did not open', async () => {
    const bridge = makeBridge();
    bridge.appOpenExternal.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:app.openExternal': Error: [scout:app/external-host-not-allowed] That address is not allowed.",
      ),
    );
    const fixture = await render(bridge);
    await openAbout(fixture);

    el(fixture, 'about-repository')!.click();
    await settle(fixture);

    expect(text(fixture, 'about-error')).toBe('That address is not allowed.');
  });

  it('has no separate Reprovision section: Discord is disconnected from its card', async () => {
    const fixture = await render(makeBridge(), makeWizard({ discordServer: 'Ops Guild' }));

    expect(el(fixture, 'reprovision')).toBeNull();
    expect(text(fixture, 'discord-disconnect')).toBe('Disconnect');
  });
});

describe('Settings: Scout worker', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
    localStorage.clear();
  });

  it('shows the worker state, PID, uptime and restarts', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-30T10:00:00.000Z'));
    const fixture = await render(makeBridge(), makeWizard(), {
      state: 'running',
      pid: 4242,
      restarts: 2,
      startedAt: '2026-10-30T09:58:55.000Z',
    });

    expect(text(fixture, 'worker-state')).toBe('Running');
    expect(text(fixture, 'worker-pid')).toBe('PID 4242');
    expect(text(fixture, 'worker-uptime')).toBe('Uptime 1 min');
    expect(text(fixture, 'worker-restarts')).toBe('Restarts 2');
  });

  for (const [state, label, tone, glyphName, glyph] of [
    ['running', 'Running', 'ok', 'the check', 'M20 6L9 17l-5-5'],
    ['starting', 'Starting', 'info', 'the arc', 'M12 3a9 9 0 1 0 9 9'],
    [
      'crashed',
      'Stopped unexpectedly',
      'error',
      'the crossed circle',
      'M15 9l-6 6M9 9l6 6M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z',
    ],
    ['stopped', 'Stopped', 'neutral', 'the dot', 'M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z'],
    ['idle', 'Not started', 'neutral', 'the dot', 'M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z'],
  ] as const) {
    it(`a ${state} worker: the chip reads "${label}" in sentence case after ${glyphName}, on the ${tone} container, never the raw state`, async () => {
      const fixture = await render(makeBridge(), makeWizard(), { state, restarts: 0 });

      const chip = el(fixture, 'worker-state')!;

      expect({
        chip: chip.tagName.toLowerCase(),
        text: chip.textContent?.trim(),
        tone: chip.getAttribute('data-tone'),
        leadingGlyph: chip.firstElementChild?.querySelector('path')?.getAttribute('d'),
      }).toEqual({ chip: 'sc-status-chip', text: label, tone, leadingGlyph: glyph });
    });
  }

  it('Start starts a stopped worker through the bridge', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, makeWizard(), { state: 'stopped', restarts: 0 });

    el(fixture, 'worker-start')!.click();
    await settle(fixture);

    expect(bridge.workerCalls).toEqual(['start']);
    expect(el(fixture, 'worker-stop')).toBeNull();
  });

  it('Stop stops a running worker through the bridge', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, makeWizard(), {
      state: 'running',
      pid: 4242,
      restarts: 0,
    });

    el(fixture, 'worker-stop')!.click();
    await settle(fixture);

    expect(bridge.workerCalls).toEqual(['stop']);
  });

  it('Restart stops the worker and then starts it again', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, makeWizard(), {
      state: 'running',
      pid: 4242,
      restarts: 0,
    });

    el(fixture, 'worker-restart')!.click();
    await new Promise((resolve) => setTimeout(resolve, 0));
    await settle(fixture);

    expect(bridge.workerCalls).toEqual(['stop', 'start']);
  });

  it('Restart while a live listen runs leaves Scout running and says it restarts once the listen ends', async () => {
    const bridge = makeBridge();
    bridge.localListening.mockResolvedValue({ listening: true });
    const fixture = await render(bridge, makeWizard(), {
      state: 'running',
      pid: 4242,
      restarts: 0,
    });

    el(fixture, 'worker-restart')!.click();
    await new Promise((resolve) => setTimeout(resolve, 0));
    await settle(fixture);

    expect({ calls: bridge.workerCalls, note: text(fixture, 'worker-restart-note') }).toEqual({
      calls: [],
      note: RESTART_DEFERRED_LINE,
    });
  });

  it('holds the log verbosity setting, with no separate section for it', async () => {
    const fixture = await render(makeBridge());

    expect(text(fixture, 'log-verbosity')).toBe('Log verbosity: coming soon.');
    const headings = Array.from(
      fixture.nativeElement.querySelectorAll('h2') as NodeListOf<HTMLElement>,
    ).map((node) => node.textContent?.trim());
    expect(headings).not.toContain('Log verbosity');
  });

  it('"Show Logs in the menu" is on by default; turning it off hides Logs and survives a reload', async () => {
    const fixture = await render(makeBridge());
    expect(checked(fixture, 'show-logs')).toBe(true);

    switchIn(fixture, 'show-logs').click();
    await settle(fixture);

    expect(TestBed.inject(MenuPreferences).showLogs()).toBe(false);
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    });
    const shell = TestBed.createComponent(PostConnectShellComponent);
    shell.detectChanges();
    expect(shell.nativeElement.querySelector('[data-test="nav-settings"]')).not.toBeNull();
    expect(shell.nativeElement.querySelector('[data-test="nav-logs"]')).toBeNull();
  });
});

describe('Settings: Who can use Scout', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('with nobody listed, says the list only matters once a chat client is connected', async () => {
    const fixture = await render(makeBridge([]));

    expect(text(fixture, 'access-empty')).toBe(
      'No one is listed yet. The list only matters once Discord or Slack is connected; chat on this computer never needs it.',
    );
  });

  it('hides the empty-list hint once someone is listed', async () => {
    const fixture = await render(makeBridge(['111']));

    expect(el(fixture, 'access-empty')).toBeNull();
  });

  it('shows the Slack connection so a Discord user can add Slack later', async () => {
    const fixture = await render(makeBridge());

    expect(text(fixture, 'slack-state')).toBe('Connected');
    expect(text(fixture, 'slack-detail')).toBe('Workspace: Acme');
    expect(text(fixture, 'slack-manage')).toBe('Manage');
  });

  it('tells the owner where each platform shows a user id', async () => {
    const fixture = await render(makeBridge());

    expect(
      Array.from(el(fixture, 'access-help')!.querySelectorAll('li'), (li) =>
        li.textContent?.trim(),
      ),
    ).toEqual([
      'Discord: turn on Developer Mode, right-click a person, Copy User ID.',
      'Slack: open their profile, More, Copy member ID.',
      "Teams: the user's Bot Framework user ID, which starts with 29:.",
    ]);
  });

  it('Pair from chat shows the message to send, "pair" and the code, to Scout in a direct message on Discord', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    expect(el(fixture, 'access-pair-instructions')).toBeNull();
    (el(fixture, 'access-pair') as HTMLButtonElement).click();
    await settle(fixture);

    expect(text(fixture, 'access-pair-instructions')).toBe(
      'Send pair 482913 to Scout in a direct message on Discord.',
    );
    expect(el(fixture, 'access-pair-instructions')?.querySelector('code')?.textContent).toBe(
      'pair 482913',
    );
  });

  it('a burned pairing code from the worker replaces the code with "Pairing expired. Start again."', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    (el(fixture, 'access-pair') as HTMLButtonElement).click();
    await settle(fixture);

    bridge.emitWorker({
      kind: 'stderr',
      line: 'SCOUT_PAIRING_BURNED',
      ts: '2026-10-28T12:01:00.000Z',
    });
    await settle(fixture);
    expect(el(fixture, 'access-pair-expired')).toBeNull();

    bridge.emitWorker({
      kind: 'stdout',
      line: 'SCOUT_PAIRING_BURNED',
      ts: '2026-10-28T12:02:00.000Z',
    });
    await settle(fixture);

    expect(el(fixture, 'access-pair-instructions')).toBeNull();
    expect(text(fixture, 'access-pair-expired')).toBe('Pairing expired. Start again.');

    (el(fixture, 'access-pair') as HTMLButtonElement).click();
    await settle(fixture);

    expect(el(fixture, 'access-pair-expired')).toBeNull();
    expect(text(fixture, 'access-pair-instructions')).toBe(
      'Send pair 482913 to Scout in a direct message on Discord.',
    );
  });

  it('a pairing that main refuses shows its reason and no code', async () => {
    const bridge = makeBridge();
    bridge.accessStartPairing.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:access.startPairing': Error: [scout:vault/unavailable] The credential store is unavailable.",
      ),
    );
    const fixture = await render(bridge);

    (el(fixture, 'access-pair') as HTMLButtonElement).click();
    await settle(fixture);

    expect(text(fixture, 'access-error')).toBe('The credential store is unavailable.');
    expect(el(fixture, 'access-pair-instructions')).toBeNull();
  });

  it('lists the allowed users the app already stores', async () => {
    const fixture = await render(makeBridge(['111', 'U02ABC']));

    expect(listed(fixture)).toEqual(['111', 'U02ABC']);
  });

  it('adding then removing a user saves the whole list through access.set each time', async () => {
    const bridge = makeBridge(['111']);
    const fixture = await render(bridge);

    await type(fixture, ' 222 ');
    (el(fixture, 'access-add') as HTMLButtonElement).click();
    await settle(fixture);

    expect(bridge.accessSet).toHaveBeenNthCalledWith(1, { userIds: ['111', ' 222 '] });
    expect(listed(fixture)).toEqual(['111', '222']);
    expect((el(fixture, 'access-input') as HTMLInputElement).value).toBe('');

    (el(fixture, 'access-remove-111') as HTMLButtonElement).click();
    await settle(fixture);
    document.querySelector<HTMLElement>('[data-test="confirm-ok"]')!.click();
    await settle(fixture);
    await settle(fixture);

    expect(bridge.accessSet).toHaveBeenNthCalledWith(2, { userIds: ['222'] });
    expect(listed(fixture)).toEqual(['222']);
  });

  describe('removing an allowed user asks first', () => {
    afterEach(() => {
      document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
    });

    for (const choice of ['confirm-ok', 'confirm-cancel'] as const) {
      it(`Remove opens "Remove 111?" with Cancel focused; ${choice === 'confirm-ok' ? 'Remove saves the list without 111' : 'Cancel saves nothing and keeps 111'}`, async () => {
        const bridge = makeBridge(['111', 'U02ABC']);
        const fixture = await render(bridge);

        (el(fixture, 'access-remove-111') as HTMLButtonElement).click();
        await settle(fixture);
        const title = document.querySelector('sc-confirm-dialog h2')?.textContent?.trim();
        const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
        expect({ title, focused }).toEqual({ title: 'Remove 111?', focused: 'confirm-cancel' });
        document.querySelector<HTMLElement>(`[data-test="${choice}"]`)!.click();
        await settle(fixture);
        await settle(fixture);

        expect(bridge.accessSet.mock.calls).toEqual(
          choice === 'confirm-ok' ? [[{ userIds: ['U02ABC'] }]] : [],
        );
        expect(listed(fixture)).toEqual(choice === 'confirm-ok' ? ['U02ABC'] : ['111', 'U02ABC']);
      });
    }
  });

  it('shows why main refused an id and keeps the list and the typed id', async () => {
    const bridge = makeBridge(['111']);
    bridge.accessSet.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:access.set': Error: [scout:access/invalid-id] A user id must be 1 to 64 letters, digits or . _ : @ - characters.",
      ),
    );
    const fixture = await render(bridge);

    await type(fixture, 'bad id');
    (el(fixture, 'access-add') as HTMLButtonElement).click();
    await settle(fixture);

    expect(text(fixture, 'access-error')).toBe(
      'A user id must be 1 to 64 letters, digits or . _ : @ - characters.',
    );
    expect(listed(fixture)).toEqual(['111']);
    expect((el(fixture, 'access-input') as HTMLInputElement).value).toBe('bad id');
  });

  it('keeps Add disabled until an id is typed', async () => {
    const fixture = await render(makeBridge());

    expect((el(fixture, 'access-add') as HTMLButtonElement).disabled).toBe(true);
    await type(fixture, '   ');
    expect((el(fixture, 'access-add') as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('Settings: Desktop', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  async function toggle(fixture: ComponentFixture<SettingsComponent>, test: string): Promise<void> {
    switchIn(fixture, test).click();
    await settle(fixture);
  }

  it('shows "Keep Scout running when the window is closed" on and "Start Scout when I log in" off by default', async () => {
    const fixture = await render(makeBridge());

    expect(labelledBy(switchIn(fixture, 'desktop-keep-running'))).toBe(
      'Keep Scout running when the window is closed',
    );
    expect(checked(fixture, 'desktop-keep-running')).toBe(true);
    expect(labelledBy(switchIn(fixture, 'desktop-start-at-login'))).toBe(
      'Start Scout when I log in',
    );
    expect(checked(fixture, 'desktop-start-at-login')).toBe(false);
    expect(el(fixture, 'desktop-no-tray')).toBeNull();
  });

  it('turning keep running off saves it and shows what main stored', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await toggle(fixture, 'desktop-keep-running');

    expect(bridge.desktopSetKeepRunning).toHaveBeenCalledWith({ enabled: false });
    expect(checked(fixture, 'desktop-keep-running')).toBe(false);
  });

  it('without a system tray, keep running is off and locked, and Settings says why', async () => {
    const fixture = await render(
      makeBridge([], { keepRunning: false, startAtLogin: false, trayAvailable: false }),
    );

    expect(checked(fixture, 'desktop-keep-running')).toBe(false);
    expect(switchIn(fixture, 'desktop-keep-running').disabled).toBe(true);
    expect(text(fixture, 'desktop-no-tray')).toBe(
      'This desktop has no system tray, so Scout quits when its window is closed.',
    );
  });

  it('turning start at login on reports it on', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await toggle(fixture, 'desktop-start-at-login');

    expect(bridge.desktopSetStartAtLogin).toHaveBeenCalledWith({ enabled: true });
    expect(checked(fixture, 'desktop-start-at-login')).toBe(true);
    expect(el(fixture, 'desktop-error')).toBeNull();
  });

  it('when the autostart entry cannot be written, shows the typed error and the toggle stays off', async () => {
    const bridge = makeBridge();
    bridge.desktopSetStartAtLogin.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:desktop.setStartAtLogin': Error: [scout:desktop/autostart-failed] Could not change whether Scout starts when you log in.",
      ),
    );
    const fixture = await render(bridge);

    await toggle(fixture, 'desktop-start-at-login');

    expect(text(fixture, 'desktop-error')).toBe(
      'Could not change whether Scout starts when you log in.',
    );
    expect(checked(fixture, 'desktop-start-at-login')).toBe(false);
    expect(switchIn(fixture, 'desktop-start-at-login').disabled).toBe(false);
  });
});

describe('Settings: Desktop, new versions', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  const lastCheck = (at: string, result: string) =>
    `Last check: ${new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(at))} · ${result}`;

  it('the Check for new versions toggle and Check now reach main', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const toggle = switchIn(fixture, 'update-check-enabled');

    expect(labelledBy(toggle)).toBe('Check for new versions');
    expect(checked(fixture, 'update-check-enabled')).toBe(true);
    expect(text(fixture, 'update-last-check')).toBe(
      lastCheck('2026-11-02T09:30:00.000Z', 'You have the latest version'),
    );

    toggle.click();
    await settle(fixture);
    expect(bridge.updateSetCheckEnabled).toHaveBeenCalledWith({ enabled: false });
    expect(checked(fixture, 'update-check-enabled')).toBe(false);

    el(fixture, 'update-check-now')!.click();
    await settle(fixture);
    expect(bridge.updateCheckNow).toHaveBeenCalledTimes(1);
    expect(text(fixture, 'update-last-check')).toBe(
      lastCheck('2026-11-02T10:45:00.000Z', 'Version 0.2.0 is available'),
    );
  });

  for (const [result, shown] of [
    ['up-to-date', 'You have the latest version'],
    ['available 1.2.3', 'Version 1.2.3 is available'],
    ['skipped 1.2.3', 'Version 1.2.3 is available (skipped)'],
    ['error network', 'Could not check for updates (no connection to GitHub)'],
    ['error timeout', 'Could not check for updates (GitHub did not answer in time)'],
    ['error parse', 'Could not check for updates (GitHub sent an unexpected answer)'],
    ['error http 403', 'Could not check for updates (GitHub answered HTTP 403)'],
  ] as const) {
    it(`the last check "${result}" reads "${shown}"`, async () => {
      const fixture = await render(
        makeBridge(
          [],
          { keepRunning: true, startAtLogin: false, trayAvailable: true },
          {
            checkEnabled: true,
            channel: 'stable',
            lastCheckAt: '2026-11-02T09:30:00.000Z',
            lastResult: result,
            notice: null,
            packaged: true,
          },
        ),
      );

      expect(text(fixture, 'update-last-check')).toBe(lastCheck('2026-11-02T09:30:00.000Z', shown));
    });
  }

  it('a Check now main refuses says why under the update controls and leaves the desktop error slot empty', async () => {
    const bridge = makeBridge();
    bridge.updateCheckNow.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:update.checkNow': Error: [scout:update/check-failed] The update check could not start.",
      ),
    );
    const fixture = await render(bridge);

    el(fixture, 'update-check-now')!.click();
    await settle(fixture);

    expect({
      update: text(fixture, 'update-error'),
      desktop: el(fixture, 'desktop-error'),
    }).toEqual({
      update: 'The update check could not start.',
      desktop: null,
    });
  });

  it('development build shows the explanatory line and no toggle', async () => {
    const fixture = await render(
      makeBridge(
        [],
        { keepRunning: true, startAtLogin: false, trayAvailable: true },
        {
          checkEnabled: true,
          channel: 'stable',
          lastCheckAt: null,
          lastResult: null,
          notice: null,
          packaged: false,
        },
      ),
    );

    expect(text(fixture, 'update-dev-build')).toBe('Update checks run only in the installed app.');
    expect(el(fixture, 'update-check-enabled')).toBeNull();
    expect(el(fixture, 'update-check-now')).toBeNull();
  });
});

describe('Settings: Updates', () => {
  const DESKTOP = { keepRunning: true, startAtLogin: false, trayAvailable: true };
  const NOTICE: UpdateNotice = {
    currentVersion: '1.0.0',
    latestVersion: '1.1.0',
    notes: '- A change',
    releaseUrl: 'https://github.com/vitorafgomes/scout/releases/tag/v1.1.0',
    downloadUrl: 'https://github.com/vitorafgomes/scout/releases/download/v1.1.0/Scout.AppImage',
    publishedAt: '2026-11-13T10:00:00.000Z',
    canInstall: true,
  };
  const withNotice = (notice: UpdateNotice | null, lastResult: string | null = 'available 1.1.0') =>
    makeBridge([], DESKTOP, {
      checkEnabled: true,
      channel: 'stable',
      lastCheckAt: '2026-11-02T09:30:00.000Z',
      lastResult,
      notice,
      packaged: true,
    });
  const progress = (phase: UpdateProgress['phase'], extra: Partial<UpdateProgress> = {}) => ({
    phase,
    percent: null,
    transferred: null,
    total: null,
    message: null,
    ...extra,
  });

  it('is its own card: "Updates", "Nothing downloads or installs without a click.", the current version, the check toggle, when Scout checks, and the last check', async () => {
    const fixture = await render(makeBridge());
    const card = section(fixture, 'Updates');

    expect({
      lede: card.querySelector('.lede')?.textContent?.trim(),
      version: text(fixture, 'update-current-version'),
      versionLabel: card.querySelector('.version-row .muted')?.textContent?.trim(),
      schedule: text(fixture, 'update-schedule'),
      holds: ['update-check-enabled', 'update-check-now', 'update-last-check'].map((test) =>
        card.contains(el(fixture, test)),
      ),
      desktopHoldsUpdates: section(fixture, 'Desktop').contains(
        el(fixture, 'update-check-enabled'),
      ),
    }).toEqual({
      lede: 'Nothing downloads or installs without a click.',
      version: '1.0.0',
      versionLabel: 'Current version',
      schedule: 'Scout checks GitHub Releases 10 seconds after it starts, then once a day.',
      holds: [true, true, true],
      desktopHoldsUpdates: false,
    });
  });

  it('says "You are up to date" with the newest version after a check that found nothing newer, and says nothing of it after a failed check', async () => {
    const upToDate = await render(withNotice(null, 'up-to-date'));
    const shown = Array.from(
      el(upToDate, 'settings-update-up-to-date')!.querySelectorAll('span'),
      (span) => span.textContent?.trim(),
    );
    TestBed.resetTestingModule();
    const failed = await render(withNotice(null, 'error network'));

    expect([shown, el(failed, 'settings-update-up-to-date')]).toEqual([
      ['You are up to date', 'Scout 1.0.0 is the newest version on the Stable channel.'],
      null,
    ]);
  });

  it('an available version that can install: the headline, Update and restart starts the install, Release notes opens the release, no Download', async () => {
    const bridge = withNotice(NOTICE);
    const fixture = await render(bridge);

    el(fixture, 'settings-update-install')!.click();
    el(fixture, 'settings-update-release-notes')!.click();
    await settle(fixture);

    expect({
      headline: text(fixture, 'settings-update-headline'),
      installs: bridge.updateInstall.mock.calls.length,
      opened: bridge.appOpenExternal.mock.calls.map(([req]) => req.url),
      download: el(fixture, 'settings-update-download'),
      bar: section(fixture, 'Updates').textContent?.includes(
        'Later and Skip this version are on the bar at the top of the window.',
      ),
    }).toEqual({
      headline: 'Scout 1.1.0 is available. You have 1.0.0.',
      installs: 1,
      opened: [NOTICE.releaseUrl],
      download: null,
      bar: true,
    });
  });

  it('a package that cannot update itself says so and offers Download, which opens the download, and no Update and restart', async () => {
    const bridge = withNotice({ ...NOTICE, canInstall: false });
    const fixture = await render(bridge);

    el(fixture, 'settings-update-download')!.click();
    await settle(fixture);

    expect({
      cannot: text(fixture, 'settings-update-cannot'),
      install: el(fixture, 'settings-update-install'),
      opened: bridge.appOpenExternal.mock.calls.map(([req]) => req.url),
    }).toEqual({
      cannot:
        'This package cannot update itself. Download the new version and replace this copy; your settings stay on this computer.',
      install: null,
      opened: [NOTICE.downloadUrl],
    });
  });

  it('follows the install main reports: "Downloading 42%" with its bar, then "Restarting to install", with Update and restart hidden meanwhile', async () => {
    const bridge = withNotice(NOTICE);
    const fixture = await render(bridge);

    bridge.emitProgress(progress('downloading', { percent: 42 }));
    await settle(fixture);
    const downloading = {
      line: text(fixture, 'settings-update-progress'),
      bar: (
        el(fixture, 'settings-update-progress')!.querySelector('progress') as HTMLProgressElement
      ).value,
      install: el(fixture, 'settings-update-install'),
    };
    bridge.emitProgress(progress('installing'));
    await settle(fixture);

    expect([downloading, text(fixture, 'settings-update-progress')]).toEqual([
      { line: 'Downloading 42%', bar: 42, install: null },
      'Restarting to install',
    ]);
  });

  it("a failed install shows main's reason as an alert and offers Update and restart again and Download", async () => {
    const bridge = withNotice(NOTICE);
    const fixture = await render(bridge);

    bridge.emitProgress(
      progress('failed', { message: 'The download did not finish; nothing was installed' }),
    );
    await settle(fixture);

    expect({
      alert: el(fixture, 'settings-update-failed')?.getAttribute('role'),
      reason: text(fixture, 'settings-update-failed'),
      install: el(fixture, 'settings-update-install') !== null,
      download: el(fixture, 'settings-update-download') !== null,
    }).toEqual({
      alert: 'alert',
      reason: 'The download did not finish; nothing was installed',
      install: true,
      download: true,
    });
  });

  it('shows a version main announces and drops it when Later or Skip on the bar clears it', async () => {
    const bridge = withNotice(null, 'up-to-date');
    const fixture = await render(bridge);

    bridge.emitNotice(NOTICE);
    await settle(fixture);
    const announced = text(fixture, 'settings-update-headline');
    bridge.emitNotice(null);
    await settle(fixture);

    expect([announced, el(fixture, 'settings-update-available')]).toEqual([
      'Scout 1.1.0 is available. You have 1.0.0.',
      null,
    ]);
  });

  for (const [link, test] of [
    ['Release notes', 'settings-update-release-notes'],
    ['Download', 'settings-update-download'],
  ] as const) {
    it(`a ${link} link main refuses says why in the Updates card, not in About`, async () => {
      const bridge = withNotice({ ...NOTICE, canInstall: false });
      bridge.appOpenExternal.mockRejectedValueOnce(
        new Error(
          "Error invoking remote method 'scout:app.openExternal': Error: [scout:app/external-host-not-allowed] That address is not allowed.",
        ),
      );
      const fixture = await render(bridge);

      el(fixture, test)!.click();
      await settle(fixture);

      expect({
        inUpdates: section(fixture, 'Updates').contains(el(fixture, 'update-error')),
        reason: text(fixture, 'update-error'),
        about: el(fixture, 'about-error'),
      }).toEqual({ inUpdates: true, reason: 'That address is not allowed.', about: null });
    });
  }

  it('the Channel row offers Stable and Beta, Stable pressed by default, with the line that says what Stable gets', async () => {
    const fixture = await render(makeBridge());
    const card = section(fixture, 'Updates');
    const group = card.querySelector('[aria-labelledby="lbl-channel"]')!;

    expect({
      label: card.querySelector('#lbl-channel')?.textContent?.trim(),
      buttons: Array.from(group.querySelectorAll('button'), (b) => [
        b.textContent?.trim(),
        b.getAttribute('aria-pressed'),
      ]),
      hint: text(fixture, 'update-channel-hint'),
      backToStable: el(fixture, 'update-back-to-stable'),
    }).toEqual({
      label: 'Channel',
      buttons: [
        ['Stable', 'true'],
        ['Beta', 'false'],
      ],
      hint: 'Stable gets released versions only.',
      backToStable: null,
    });
  });

  it('pressing Beta asks main for the Beta channel, then Beta is pressed and the line says pre-releases can have bugs', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    el(fixture, 'update-channel-beta')!.click();
    await settle(fixture);

    expect({
      asked: bridge.updateSetChannel.mock.calls.map(([req]) => req),
      pressed: el(fixture, 'update-channel-beta')!.getAttribute('aria-pressed'),
      hint: text(fixture, 'update-channel-hint'),
    }).toEqual({
      asked: [{ channel: 'beta' }],
      pressed: 'true',
      hint: 'Beta also gets pre-releases, to try changes before they are released. A pre-release can have bugs; keep Stable for daily work.',
    });
  });

  it('a channel change main refuses says why in the Updates card and keeps the channel pressed before', async () => {
    const bridge = makeBridge();
    bridge.updateSetChannel.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:update.setChannel': Error: [scout:vault/unavailable] The credential store is not available.",
      ),
    );
    const fixture = await render(bridge);

    el(fixture, 'update-channel-beta')!.click();
    await settle(fixture);

    expect({
      reason: text(fixture, 'update-error'),
      inUpdates: section(fixture, 'Updates').contains(el(fixture, 'update-error')),
      stable: el(fixture, 'update-channel-stable')!.getAttribute('aria-pressed'),
    }).toEqual({
      reason: 'The credential store is not available.',
      inUpdates: true,
      stable: 'true',
    });
  });

  it('running a pre-release marks the current version and, on Stable, says Stable never installs an older version instead of "You are up to date"', async () => {
    const bridge = makeBridge();
    bridge.appGetVersion.mockResolvedValue({ version: '1.1.0-beta.2' });
    const fixture = await render(bridge);

    expect([
      text(fixture, 'update-current-prerelease'),
      text(fixture, 'update-back-to-stable'),
      el(fixture, 'settings-update-up-to-date'),
    ]).toEqual([
      'Pre-release',
      'You are on the pre-release 1.1.0-beta.2. Stable never installs an older version: Scout offers the next stable version newer than it.',
      null,
    ]);
  });

  it('up to date on Beta while running a pre-release: the Pre-release chip and "newest version on the Beta channel"', async () => {
    const bridge = makeBridge([], DESKTOP, {
      checkEnabled: true,
      channel: 'beta',
      lastCheckAt: '2026-11-02T09:30:00.000Z',
      lastResult: 'up-to-date',
      notice: null,
      packaged: true,
    });
    bridge.appGetVersion.mockResolvedValue({ version: '0.10.0-beta.2' });
    const fixture = await render(bridge);

    expect([
      text(fixture, 'update-current-prerelease'),
      Array.from(el(fixture, 'settings-update-up-to-date')!.querySelectorAll('span'), (span) =>
        span.textContent?.replace(/\s+/g, ' ').trim(),
      ),
      el(fixture, 'update-back-to-stable'),
    ]).toEqual([
      'Pre-release',
      ['You are up to date', 'Scout 0.10.0-beta.2 is the newest version on the Beta channel.'],
      null,
    ]);
  });

  it('a pre-release on offer is marked Pre-release beside its headline; a released version is not', async () => {
    const preRelease = await render(withNotice({ ...NOTICE, latestVersion: '1.1.0-beta.3' }));
    const shown = text(preRelease, 'settings-update-prerelease');
    TestBed.resetTestingModule();
    const released = await render(withNotice(NOTICE));

    expect([shown, el(released, 'settings-update-prerelease')]).toEqual(['Pre-release', null]);
  });

  describe('Install a specific version', () => {
    afterEach(() => {
      document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
    });

    const open = async (bridge: ReturnType<typeof makeBridge>) => {
      const fixture = await render(bridge);
      el(fixture, 'versions-toggle')!.click();
      await settle(fixture);
      return fixture;
    };
    const rows = (fixture: Awaited<ReturnType<typeof render>>) =>
      Array.from(
        el(fixture, 'versions-list')!.querySelectorAll('li') as NodeListOf<HTMLElement>,
        (li) =>
          Array.from(li.children, (part) => part.textContent?.trim())
            .filter((part) => part !== '')
            .join(' '),
      );

    it('is folded by default behind "Install a specific version", and asks main for nothing until opened', async () => {
      const bridge = makeBridge();
      const fixture = await render(bridge);
      const toggle = el(fixture, 'versions-toggle')!;

      expect({
        row: toggle.parentElement?.textContent?.trim(),
        expanded: toggle.getAttribute('aria-expanded'),
        label: toggle.getAttribute('aria-label'),
        list: el(fixture, 'versions-list'),
        asked: bridge.updateListVersions.mock.calls.length,
      }).toEqual({
        row: 'Install a specific version',
        expanded: 'false',
        label: 'Show the versions',
        list: null,
        asked: 0,
      });
    });

    it('opened, lists every released version newest first with its date, Running on the current one and Older below it, Install on the others', async () => {
      const bridge = makeBridge();
      const fixture = await open(bridge);

      expect({
        lede: text(fixture, 'versions-lede'),
        label: el(fixture, 'versions-toggle')!.getAttribute('aria-label'),
        rows: rows(fixture),
        actions: ['1.1.0', '1.0.0', '0.9.0'].map(
          (v) => el(fixture, `version-action-${v}`)?.getAttribute('aria-label') ?? null,
        ),
      }).toEqual({
        lede: 'Every released version, newest first. A version older than the one you run asks first.',
        label: 'Hide the versions',
        rows: [
          '1.1.0 7 Nov 2026 Install',
          '1.0.0 Running 7 Nov 2026',
          '0.9.0 Older 7 Nov 2026 Install',
        ],
        actions: ['Install Scout 1.1.0', null, 'Install Scout 0.9.0'],
      });
    });

    it('on Beta the lede says pre-releases are included, and a pre-release carries its chip', async () => {
      const bridge = withNotice(null, 'up-to-date');
      bridge.updateGetState.mockResolvedValue({
        checkEnabled: true,
        channel: 'beta',
        lastCheckAt: null,
        lastResult: null,
        notice: null,
        packaged: true,
      });
      bridge.updateListVersions.mockResolvedValueOnce({
        versions: [version('1.2.0-beta.1', true), version('1.0.0')],
        canInstall: true,
        failure: null,
      });
      const fixture = await open(bridge);

      expect([text(fixture, 'versions-lede'), rows(fixture)[0]]).toEqual([
        'Every published version, newest first, pre-releases included on the Beta channel. A version older than the one you run asks first.',
        '1.2.0-beta.1 Pre-release 7 Nov 2026 Install',
      ]);
    });

    it('says it is loading while main reads the list', async () => {
      const bridge = makeBridge();
      bridge.updateListVersions.mockReturnValueOnce(new Promise(() => undefined));
      const fixture = await open(bridge);

      expect(text(fixture, 'versions-loading')).toBe('Loading the versions...');
    });

    it('a list GitHub did not give says why, and Try again asks again', async () => {
      const bridge = makeBridge();
      bridge.updateListVersions.mockResolvedValueOnce({
        versions: [],
        canInstall: true,
        failure: 'network',
      });
      const fixture = await open(bridge);
      const failed = text(fixture, 'versions-failed');

      el(fixture, 'versions-retry')!.click();
      await settle(fixture);

      expect({
        failed,
        role: 'alert',
        asked: bridge.updateListVersions.mock.calls.length,
        listed: rows(fixture).length,
      }).toEqual({
        failed: 'Could not list the versions: no connection to GitHub.',
        role: 'alert',
        asked: 2,
        listed: 3,
      });
    });

    it('Install on a newer version installs it at once', async () => {
      const bridge = makeBridge();
      const fixture = await open(bridge);

      el(fixture, 'version-action-1.1.0')!.click();
      await settle(fixture);

      expect([
        bridge.updateInstallVersion.mock.calls,
        document.querySelector('sc-confirm-dialog'),
      ]).toEqual([[[{ version: '1.1.0' }]], null]);
    });

    for (const choice of ['confirm-ok', 'confirm-cancel'] as const) {
      it(`Install on an older version asks "Install Scout 0.9.0, an older version?" with Cancel focused; ${choice === 'confirm-ok' ? 'Install 0.9.0 installs it' : 'Cancel installs nothing'}`, async () => {
        const bridge = makeBridge();
        const fixture = await open(bridge);

        el(fixture, 'version-action-0.9.0')!.click();
        await settle(fixture);
        const dialog = {
          title: document.querySelector('sc-confirm-dialog h2')?.textContent?.trim(),
          body: document
            .querySelector('sc-confirm-dialog p')
            ?.textContent?.replace(/\s+/g, ' ')
            .trim(),
          confirm: document.querySelector('[data-test="confirm-ok"]')?.textContent?.trim(),
          focused: (document.activeElement as HTMLElement | null)?.getAttribute('data-test'),
        };
        document.querySelector<HTMLElement>(`[data-test="${choice}"]`)!.click();
        await settle(fixture);
        await settle(fixture);

        expect([dialog, bridge.updateInstallVersion.mock.calls]).toEqual([
          {
            title: 'Install Scout 0.9.0, an older version?',
            body: 'You are running 1.0.0. An older version may not read everything a newer one saved, such as a setting it does not know. Nothing is deleted, and you can update again from this card. Scout restarts once the download ends.',
            confirm: 'Install 0.9.0',
            focused: 'confirm-cancel',
          },
          choice === 'confirm-ok' ? [[{ version: '0.9.0' }]] : [],
        ]);
      });
    }

    it('while the bar installs an update, no version of the list can be installed', async () => {
      const bridge = makeBridge();
      const fixture = await open(bridge);

      bridge.emitProgress(progress('downloading', { percent: 10 }));
      await settle(fixture);

      expect(['1.1.0', '0.9.0'].map((v) => el(fixture, `version-action-${v}`))).toEqual([
        null,
        null,
      ]);
    });

    it('a list read before a channel change that answers last does not replace the newer one', async () => {
      const bridge = makeBridge();
      let answerFirst: (list: UpdateVersionList) => void = () => undefined;
      bridge.updateListVersions.mockReturnValueOnce(
        new Promise<UpdateVersionList>((resolve) => {
          answerFirst = resolve;
        }),
      );
      const fixture = await open(bridge);

      el(fixture, 'update-channel-beta')!.click();
      await settle(fixture);
      answerFirst({ versions: [], canInstall: true, failure: null });
      await settle(fixture);

      expect([rows(fixture).length, el(fixture, 'versions-loading')]).toEqual([3, null]);
    });

    it('when the running version is unknown, any install asks first, since it may be older', async () => {
      const bridge = makeBridge();
      bridge.appGetVersion.mockRejectedValue(new Error('no version'));
      const fixture = await open(bridge);

      el(fixture, 'version-action-1.1.0')!.click();
      await settle(fixture);

      expect([
        document.querySelector('sc-confirm-dialog h2')?.textContent?.trim(),
        bridge.updateInstallVersion.mock.calls,
      ]).toEqual(['Install Scout 1.1.0, an older version?', []]);
    });

    it('follows the install of the picked version: "Downloading Scout 1.1.0 · 42%" with no Install meanwhile, then a failure with the buttons back', async () => {
      const bridge = makeBridge();
      const fixture = await open(bridge);
      el(fixture, 'version-action-1.1.0')!.click();
      await settle(fixture);

      bridge.emitProgress(progress('downloading', { percent: 42 }));
      await settle(fixture);
      const downloading = {
        line: text(fixture, 'versions-progress'),
        bar: (el(fixture, 'versions-progress')!.querySelector('progress') as HTMLProgressElement)
          .value,
        install: el(fixture, 'version-action-0.9.0'),
      };
      bridge.emitProgress(
        progress('failed', { message: 'The download did not finish; nothing was installed' }),
      );
      await settle(fixture);

      expect([
        downloading,
        text(fixture, 'versions-install-failed'),
        el(fixture, 'version-action-0.9.0') !== null,
      ]).toEqual([
        { line: 'Downloading Scout 1.1.0 · 42%', bar: 42, install: null },
        'The download did not finish; nothing was installed',
        true,
      ]);
    });

    it('a package that cannot replace itself offers Download, which opens the version page, and says so', async () => {
      const bridge = makeBridge();
      bridge.updateListVersions.mockResolvedValueOnce({
        versions: [version('1.1.0'), version('1.0.0')],
        canInstall: false,
        failure: null,
      });
      const fixture = await open(bridge);

      el(fixture, 'version-action-1.1.0')!.click();
      await settle(fixture);

      expect({
        label: text(fixture, 'version-action-1.1.0'),
        hint: text(fixture, 'versions-cannot'),
        opened: bridge.appOpenExternal.mock.calls.map(([req]) => req.url),
        installed: bridge.updateInstallVersion.mock.calls,
      }).toEqual({
        label: 'Download',
        hint: "This package cannot replace itself: Download opens that version's page, and you install it by hand.",
        opened: ['https://github.com/vitorafgomes/scout/releases/tag/v1.1.0'],
        installed: [],
      });
    });

    it('changing the channel reads the list again on the new channel', async () => {
      const bridge = makeBridge();
      const fixture = await open(bridge);

      el(fixture, 'update-channel-beta')!.click();
      await settle(fixture);

      expect(bridge.updateListVersions.mock.calls.length).toBe(2);
    });
  });

  it('a development build still shows the current version beside the line that checks run only in the installed app', async () => {
    const fixture = await render(
      makeBridge([], DESKTOP, {
        checkEnabled: true,
        channel: 'stable',
        lastCheckAt: null,
        lastResult: null,
        notice: null,
        packaged: false,
      }),
    );

    expect([text(fixture, 'update-current-version'), text(fixture, 'update-dev-build')]).toEqual([
      '1.0.0',
      'Update checks run only in the installed app.',
    ]);
  });
});

describe('Settings: Floating button', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  function pressed(fixture: ComponentFixture<SettingsComponent>, test: string): string | null {
    return el(fixture, test)?.getAttribute('aria-pressed') ?? null;
  }

  async function click(fixture: ComponentFixture<SettingsComponent>, test: string): Promise<void> {
    (el(fixture, test) as HTMLButtonElement).click();
    await settle(fixture);
  }

  it('shows the stored floating button settings in the "Floating button and quick ask" section', async () => {
    const fixture = await render(makeBridge());

    expect(text(fixture, 'shortcut-section')).toContain('Floating button and quick ask');
    expect(switchIn(fixture, 'bubble-toggle').getAttribute('role')).toBe('switch');
    expect(checked(fixture, 'bubble-toggle')).toBe(true);
    expect(labelledBy(switchIn(fixture, 'bubble-toggle'))).toBe('Show the floating button');
    expect(
      ['bottom-right', 'bottom-left', 'top-right', 'top-left'].map((c) =>
        pressed(fixture, `bubble-corner-${c}`),
      ),
    ).toEqual(['true', 'false', 'false', 'false']);
    expect(text(fixture, 'bubble-display-cursor')).toBe('Follow the mouse cursor');
    expect(text(fixture, 'bubble-monitor-33')).toBe('External display 3440×1440 (primary)');
    expect(pressed(fixture, 'bubble-display-cursor')).toBe('true');
    expect(pressed(fixture, 'bubble-monitor-33')).toBe('false');
  });

  it('the display choice lists each connected monitor by name and size, and a click saves that monitor', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const before = [text(fixture, 'bubble-monitor-35'), text(fixture, 'bubble-monitor-33')];

    await click(fixture, 'bubble-monitor-35');

    expect(before).toEqual(['Built-in display 1920×1080', 'External display 3440×1440 (primary)']);
    expect(bridge.localSetBubble).toHaveBeenCalledWith({
      enabled: true,
      corner: 'bottom-right',
      display: 35,
    });
    expect(
      ['bubble-display-cursor', 'bubble-monitor-35', 'bubble-monitor-33'].map((test) =>
        pressed(fixture, test),
      ),
    ).toEqual(['false', 'true', 'false']);
  });

  it('the display choice shows the hint main sends when the choice cannot be followed', async () => {
    const bridge = makeBridge();
    const hint =
      "Your desktop (Wayland) does not tell Scout where the pointer is outside Scout's own windows, so the button follows the cursor only when the pointer passes over one of them.";
    bridge.localGetBubble.mockResolvedValueOnce({
      enabled: true,
      corner: 'bottom-right',
      display: 'cursor',
      monitors: OWNER_MONITORS,
      hint,
    });

    const fixture = await render(bridge);

    expect(text(fixture, 'bubble-hint')).toBe(hint);
  });

  it('the monitor list and the hint follow what main pushes when a monitor is unplugged', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const hint =
      'The monitor chosen for the floating button is not connected; the button is on the primary display until it is back.';

    bridge.emitBubble({
      enabled: true,
      corner: 'bottom-right',
      display: 35,
      monitors: [{ id: 33, name: 'External display 3440×1440', primary: true }],
      hint,
    });
    await settle(fixture);

    expect(el(fixture, 'bubble-monitor-35')).toBeNull();
    expect(el(fixture, 'bubble-monitor-33')).toBeNull();
    expect(text(fixture, 'bubble-hint')).toBe(hint);
    fixture.destroy();
    expect(bridge.bubbleListeners).toHaveLength(0);
  });

  it('corner buttons save the chosen corner', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await click(fixture, 'bubble-corner-top-left');

    expect(bridge.localSetBubble).toHaveBeenCalledWith({
      enabled: true,
      corner: 'top-left',
      display: 'cursor',
    });
    expect(pressed(fixture, 'bubble-corner-top-left')).toBe('true');
    expect(pressed(fixture, 'bubble-corner-bottom-right')).toBe('false');
  });

  it('toggle hides the floating button', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    switchIn(fixture, 'bubble-toggle').click();
    await settle(fixture);

    expect(bridge.localSetBubble).toHaveBeenCalledWith({
      enabled: false,
      corner: 'bottom-right',
      display: 'cursor',
    });
    expect(checked(fixture, 'bubble-toggle')).toBe(false);
  });

  it("the primary monitor's button saves the primary display, so the choice follows whichever monitor is primary", async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await click(fixture, 'bubble-monitor-33');

    expect(bridge.localSetBubble).toHaveBeenCalledWith({
      enabled: true,
      corner: 'bottom-right',
      display: 'primary',
    });
    expect(pressed(fixture, 'bubble-monitor-33')).toBe('true');
    expect(pressed(fixture, 'bubble-display-cursor')).toBe('false');
  });

  it('a refused floating button switch goes back to what main kept and says why', async () => {
    const bridge = makeBridge();
    bridge.localSetBubble.mockRejectedValueOnce(
      new Error(
        '[scout:quick-ask/invalid-bubble] Choose a corner and a display for the floating button.',
      ),
    );
    const fixture = await render(bridge);

    switchIn(fixture, 'bubble-toggle').click();
    await settle(fixture);

    expect({ on: checked(fixture, 'bubble-toggle'), error: text(fixture, 'bubble-error') }).toEqual(
      {
        on: true,
        error: 'Choose a corner and a display for the floating button.',
      },
    );
  });

  it('shows why main refused the settings and keeps the previous choice', async () => {
    const bridge = makeBridge();
    bridge.localSetBubble.mockRejectedValueOnce(
      new Error(
        '[scout:quick-ask/invalid-bubble] Choose a corner and a display for the floating button.',
      ),
    );
    const fixture = await render(bridge);

    await click(fixture, 'bubble-corner-top-right');

    expect(text(fixture, 'bubble-error')).toBe(
      'Choose a corner and a display for the floating button.',
    );
    expect(pressed(fixture, 'bubble-corner-bottom-right')).toBe('true');
    expect(pressed(fixture, 'bubble-corner-top-right')).toBe('false');
  });
});

describe('Settings: Appearance', () => {
  type Start = ThemeChoice | 'not loaded';
  const resolved = (choice: ThemeChoice): 'light' | 'dark' =>
    choice === 'light' ? 'light' : 'dark';

  function themeFakes(start: Start, refuse: boolean) {
    return {
      themeGet: vi.fn(async (): Promise<ThemeState> => {
        if (start === 'not loaded') throw new Error('no answer yet');
        return { choice: start, resolved: resolved(start) };
      }),
      themeSet: vi.fn(async (choice: ThemeChoice): Promise<ThemeState> => {
        if (refuse) {
          const serialized = serializeError(
            new ScoutError('theme/invalid-choice', 'Scout could not use that theme.'),
          );
          throw new Error(encodeIpcErrorMessage(serialized));
        }
        return { choice, resolved: resolved(choice) };
      }),
      onTheme: vi.fn(() => () => undefined),
    };
  }

  async function renderAppearance(start: Start, refuse = false) {
    const theme = themeFakes(start, refuse);
    const fixture = await render({ ...makeBridge(), ...theme });
    TestBed.inject(ThemeStore).start();
    await settle(fixture);
    return { fixture, theme };
  }

  function pressed(fixture: ComponentFixture<SettingsComponent>): ThemeChoice[] {
    return (['light', 'dark', 'system'] as const).filter(
      (choice) => el(fixture, `settings-theme-${choice}`)?.getAttribute('aria-pressed') === 'true',
    );
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    document.documentElement.removeAttribute('data-theme');
  });

  it('replaces "Coming soon" with Light, Dark and System, each with its icon and a label for screen readers', async () => {
    const { fixture } = await renderAppearance('dark');

    const choices = (['light', 'dark', 'system'] as const).map((choice) => {
      const button = el(fixture, `settings-theme-${choice}`)!;
      return [
        button.textContent?.trim(),
        button.getAttribute('aria-label'),
        button.querySelector('sc-icon svg') !== null,
      ];
    });

    expect(choices).toEqual([
      ['Light', 'Light theme', true],
      ['Dark', 'Dark theme', true],
      ['System', 'Follow the system theme', true],
    ]);
    expect(fixture.nativeElement.textContent).not.toContain('Coming soon.');
  });

  const starts: Start[] = ['not loaded', 'light', 'dark', 'system'];
  const clicks: ThemeChoice[] = ['light', 'dark', 'system'];

  for (const start of starts) {
    it(`starting ${start}: ${start === 'not loaded' ? 'no choice' : start} is pressed`, async () => {
      const { fixture } = await renderAppearance(start);

      expect(pressed(fixture)).toEqual(start === 'not loaded' ? [] : [start]);
    });

    for (const click of clicks) {
      it(`starting ${start}, choosing ${click}: main is asked for ${click} and ${click} alone is pressed`, async () => {
        const { fixture, theme } = await renderAppearance(start);

        el(fixture, `settings-theme-${click}`)!.click();
        await settle(fixture);

        expect(theme.themeSet.mock.calls).toEqual([[click]]);
        expect(pressed(fixture)).toEqual([click]);
        expect(document.documentElement.getAttribute('data-theme')).toBe(resolved(click));
        expect(el(fixture, 'theme-error')).toBeNull();
      });

      it(`starting ${start}, choosing ${click} while main refuses: the old choice stays pressed and the card says why`, async () => {
        const { fixture } = await renderAppearance(start, true);

        el(fixture, `settings-theme-${click}`)!.click();
        await settle(fixture);

        expect(pressed(fixture)).toEqual(start === 'not loaded' ? [] : [start]);
        expect(text(fixture, 'theme-error')).toBe('Scout could not use that theme.');
      });
    }
  }
});

describe('Settings follows the theme', () => {
  for (const file of [
    'settings.component.ts',
    'worker-settings.component.ts',
    'chat-clients.component.ts',
    'chat-connection-dialog.component.ts',
    'audio-settings.component.ts',
  ]) {
    it(`${file} paints only with the theme tokens, no hex, rgb() or hsl() colour left`, () => {
      const source = readFileSync(`src/app/wizard/settings/${file}`, 'utf8');
      const start = source.indexOf('  styles:');
      const css = source.slice(start, source.indexOf('\n})\n', start));

      expect(start).toBeGreaterThanOrEqual(0);
      expect(css).toContain('var(--sc-');
      expect(
        Array.from(source.matchAll(/#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(/g), (m) => m[0]),
      ).toEqual([]);
    });
  }

  it('settings-switch.styles.ts colours the switches only with the theme tokens', () => {
    const source = readFileSync('src/app/wizard/settings/settings-switch.styles.ts', 'utf8');

    expect({
      tokens: source.includes('var(--sc-'),
      literals: Array.from(
        source.matchAll(/#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(/g),
        (m) => m[0],
      ),
    }).toEqual({ tokens: true, literals: [] });
  });
});

describe('Settings: what the owner sees matches what main kept', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('a Start that main refuses says why in the Scout worker section', async () => {
    const bridge = makeBridge();
    bridge.workerStart.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:worker.start': Error: [scout:worker/binary-missing] Worker binary not found: /opt/scout/Scout.Host.Console.",
      ),
    );
    const fixture = await render(bridge, makeWizard(), { state: 'stopped', restarts: 0 });

    el(fixture, 'worker-start')!.click();
    await settle(fixture);

    expect(
      el(fixture, 'worker-section')?.querySelector('[role="alert"]')?.textContent?.trim() ?? '',
    ).toContain('Worker binary not found: /opt/scout/Scout.Host.Console.');
  });

  it('a Stop that main refuses says why in the Scout worker section', async () => {
    const bridge = makeBridge();
    bridge.workerStop.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:worker.stop': Error: [scout:worker/stop-failed] The worker did not stop in time.",
      ),
    );
    const fixture = await render(bridge, makeWizard(), {
      state: 'running',
      pid: 4242,
      restarts: 0,
    });

    el(fixture, 'worker-stop')!.click();
    await settle(fixture);

    expect(text(fixture, 'worker-error')).toBe(
      'Scout could not stop the worker. It is still running. The worker did not stop in time.',
    );
  });

  it('adding a user after someone paired from chat keeps the paired user on the allowed list', async () => {
    const bridge = makeBridge(['111']);
    const fixture = await render(bridge);
    (el(fixture, 'access-pair') as HTMLButtonElement).click();
    await settle(fixture);
    bridge.emitWorker({ kind: 'stdout', line: 'SCOUT_PAIRED=222', ts: '2026-10-28T12:01:00.000Z' });
    await settle(fixture);
    await bridge.accessSet({ userIds: ['111', '222'] });

    await type(fixture, '333');
    (el(fixture, 'access-add') as HTMLButtonElement).click();
    await settle(fixture);

    expect((await bridge.accessGet()).userIds).toEqual(
      expect.arrayContaining(['111', '222', '333']),
    );
  });

  it('a pairing code that was used takes its instructions off the screen', async () => {
    const bridge = makeBridge(['111']);
    const fixture = await render(bridge);
    (el(fixture, 'access-pair') as HTMLButtonElement).click();
    await settle(fixture);

    bridge.emitWorker({ kind: 'stdout', line: 'SCOUT_PAIRED=222', ts: '2026-10-28T12:01:00.000Z' });
    await settle(fixture);

    expect(el(fixture, 'access-pair-instructions')).toBeNull();
  });

  it('a pairing code past its ten minutes is replaced with "Pairing expired. Start again."', async () => {
    vi.useFakeTimers({
      toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'],
    });
    vi.setSystemTime(new Date('2026-10-28T12:00:00.000Z'));
    const bridge = makeBridge();
    const fixture = await render(bridge);
    (el(fixture, 'access-pair') as HTMLButtonElement).click();
    await settle(fixture);

    await vi.advanceTimersByTimeAsync(10 * 60_000 + 1_000);
    await settle(fixture);

    expect({
      code: el(fixture, 'access-pair-instructions') !== null,
      expired: text(fixture, 'access-pair-expired'),
    }).toEqual({ code: false, expired: 'Pairing expired. Start again.' });
  });

  it('Check now keeps the start-at-login error on screen', async () => {
    const bridge = makeBridge();
    bridge.desktopSetStartAtLogin.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:desktop.setStartAtLogin': Error: [scout:desktop/autostart-failed] Could not change whether Scout starts when you log in.",
      ),
    );
    const fixture = await render(bridge);
    switchIn(fixture, 'desktop-start-at-login').click();
    await settle(fixture);
    const before = text(fixture, 'desktop-error');

    el(fixture, 'update-check-now')!.click();
    await settle(fixture);

    expect({ before, after: text(fixture, 'desktop-error') }).toEqual({
      before: 'Could not change whether Scout starts when you log in.',
      after: 'Could not change whether Scout starts when you log in.',
    });
  });

  it('Add after the allowed list could not be read never overwrites the stored list with only the new id', async () => {
    const bridge = makeBridge(['111']);
    bridge.accessGet.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:access.get': Error: [scout:vault/unavailable] The credential store is unavailable.",
      ),
    );
    const fixture = await render(bridge);

    await type(fixture, '333');
    (el(fixture, 'access-add') as HTMLButtonElement).click();
    await settle(fixture);

    expect((await bridge.accessGet()).userIds).toContain('111');
  });
});

/** The value the last rule matching `node` in the page's style elements declares for `property`; null when none does. */
function declared(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found === undefined) continue;
    const matches = selector!.split(',').some((one) => {
      try {
        return node.matches(one.trim());
      } catch {
        return false;
      }
    });
    if (matches) value = found;
  }
  return value;
}

function section(fixture: ComponentFixture<SettingsComponent>, heading: string): HTMLElement {
  const h2 = Array.from(
    fixture.nativeElement.querySelectorAll('h2') as NodeListOf<HTMLElement>,
  ).find((node) => node.textContent?.trim() === heading);
  return h2!.closest('section') as HTMLElement;
}

function iconOf(node: Element | null): string | null {
  return node?.querySelector('sc-icon')?.getAttribute('name') ?? null;
}

describe('Settings as Settings.dc.html draws it, state by state', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    localStorage.clear();
  });

  it('one h1, "Settings", over its lede; the page starts at the left edge of the content area, at most 1200 px wide, its text at 14 px', async () => {
    const fixture = await render(makeBridge());
    const page = fixture.nativeElement.querySelector('main.settings') as HTMLElement;

    expect({
      headings: Array.from(
        fixture.nativeElement.querySelectorAll('h1') as NodeListOf<HTMLElement>,
        (h) => h.textContent?.trim(),
      ),
      maxWidth: declared(page, 'max-width'),
      margin: declared(page, 'margin'),
      size: declared(page, 'font-size'),
    }).toEqual({ headings: ['Settings'], maxWidth: '1200px', margin: null, size: '14px' });
  });

  for (const [heading, gap] of [
    ['Appearance', '12px'],
    ['Chat connections', '4px'],
    ['Scout worker', '12px'],
    ['About', '12px'],
    ['Who can use Scout', '12px'],
    ['Floating button and quick ask', '12px'],
    ['Audio', '12px'],
    ['Desktop', '12px'],
    ['Updates', '12px'],
  ] as const) {
    it(`the ${heading} card stacks its parts ${gap} apart with the card corners (the 14 px token), as the artboard's card does, with no margins of their own`, async () => {
      const fixture = await render(makeBridge());
      const card = section(fixture, heading);
      const h2 = card.querySelector('h2')!;

      expect({
        display: declared(card, 'display'),
        direction: declared(card, 'flex-direction'),
        gap: declared(card, 'gap'),
        headingMargin: declared(h2, 'margin'),
        corners: declared(card, 'border-radius'),
      }).toEqual({
        display: 'flex',
        direction: 'column',
        gap,
        headingMargin: '0',
        corners: 'var(--sc-radius)',
      });
    });
  }

  for (const [test, label, on] of [
    ['show-logs', 'Show Logs in the menu', true],
    ['bubble-toggle', 'Show the floating button', true],
    ['desktop-keep-running', 'Keep Scout running when the window is closed', true],
    ['desktop-start-at-login', 'Start Scout when I log in', false],
    ['update-check-enabled', 'Check for new versions', true],
  ] as const) {
    it(`"${label}" is a Material slide toggle at the right of its row, labelled by the row's text, reachable by keyboard and drawn as the artboard's 40 by 24 switch`, async () => {
      const fixture = await render(makeBridge());
      const host = el(fixture, test)!;
      const button = switchIn(fixture, test);
      const row = host.parentElement!;
      const labelNode = document.getElementById(button.getAttribute('aria-labelledby')!)!;

      expect({
        component: host.tagName.toLowerCase(),
        role: button.getAttribute('role'),
        native: button.tagName.toLowerCase(),
        tabIndex: button.tabIndex,
        checked: button.getAttribute('aria-checked'),
        label: labelledBy(button),
        labelFirst: row.firstElementChild === labelNode && row.lastElementChild === host,
        pushedRight: declared(labelNode, 'margin-right'),
        rowAlign: declared(row, 'align-items'),
        glyphInHandle: host.querySelector('.mdc-switch__icons') !== null,
        track: [
          declared(host, '--mat-slide-toggle-track-width'),
          declared(host, '--mat-slide-toggle-track-height'),
          declared(host, '--mat-slide-toggle-track-outline-width'),
        ],
        handle: [
          declared(host, '--mat-slide-toggle-unselected-handle-size'),
          declared(host, '--mat-slide-toggle-selected-handle-size'),
          declared(host, '--mat-slide-toggle-pressed-handle-size'),
        ],
        colours: [
          declared(host, '--mat-slide-toggle-selected-track-color'),
          declared(host, '--mat-slide-toggle-selected-handle-color'),
          declared(host, '--mat-slide-toggle-unselected-track-color'),
          declared(host, '--mat-slide-toggle-unselected-handle-color'),
          declared(host, '--mat-slide-toggle-track-outline-color'),
        ],
      }).toEqual({
        component: 'mat-slide-toggle',
        role: 'switch',
        native: 'button',
        tabIndex: 0,
        checked: String(on),
        label,
        labelFirst: true,
        pushedRight: 'auto',
        rowAlign: 'center',
        glyphInHandle: false,
        track: ['40px', '24px', '1px'],
        handle: ['16px', '16px', '16px'],
        colours: [
          'var(--sc-accent-strong)',
          'var(--sc-on-accent)',
          'var(--sc-surface-2)',
          'var(--sc-muted)',
          'var(--sc-line)',
        ],
      });
    });
  }

  it('About keeps its trademark notices folded behind the "Trademark notices" button, which opens and closes them', async () => {
    const fixture = await render(makeBridge());
    const toggle = el(fixture, 'about-toggle') as HTMLButtonElement;
    const closed = {
      expanded: toggle.getAttribute('aria-expanded'),
      controls: toggle.getAttribute('aria-controls'),
      icon: iconOf(toggle),
      label: toggle.getAttribute('aria-label'),
      row: toggle.parentElement?.textContent?.trim(),
      links: el(fixture, 'about-repository') !== null,
      notices: el(fixture, 'trademark-notices') !== null,
    };

    toggle.click();
    await settle(fixture);
    const region = document.getElementById(toggle.getAttribute('aria-controls') ?? '');

    expect([
      closed,
      {
        expanded: toggle.getAttribute('aria-expanded'),
        label: toggle.getAttribute('aria-label'),
        region: region === el(fixture, 'trademark-notices'),
      },
    ]).toEqual([
      {
        expanded: 'false',
        controls: null,
        icon: 'chevron-down',
        label: 'Show the trademark notices',
        row: 'Trademark notices',
        links: true,
        notices: false,
      },
      {
        expanded: 'true',
        label: 'Hide the trademark notices',
        region: true,
      },
    ]);
  });

  it('Who can use Scout: the lede, the bullets for each platform, a "User ID" field with its hint, Add with its icon, and the pairing line beside Pair from chat', async () => {
    const fixture = await render(makeBridge());
    const input = el(fixture, 'access-input') as HTMLInputElement;
    const label = document.querySelector(`label[for="${input.id}"]`);

    (el(fixture, 'access-pair') as HTMLButtonElement).click();
    await settle(fixture);

    expect({
      lede: text(fixture, 'access-lede'),
      bullets: Array.from(el(fixture, 'access-help')!.querySelectorAll('li'), (li) =>
        li.textContent?.trim(),
      ),
      label: label?.textContent?.trim(),
      placeholder: input.placeholder,
      hint: document
        .getElementById(input.getAttribute('aria-describedby') ?? '')
        ?.textContent?.trim(),
      addIcon: iconOf(el(fixture, 'access-add')),
      pairIcon: iconOf(el(fixture, 'access-pair')),
      pairingBesideButton:
        el(fixture, 'access-pair-instructions')?.parentElement ===
        el(fixture, 'access-pair')!.parentElement,
      list: fixture.nativeElement.querySelector('.access-list')?.getAttribute('aria-label'),
    }).toEqual({
      lede: 'People listed here can ask Scout from Discord or Slack. Chat on this computer never needs the list.',
      bullets: [
        'Discord: turn on Developer Mode, right-click a person, Copy User ID.',
        'Slack: open their profile, More, Copy member ID.',
        "Teams: the user's Bot Framework user ID, which starts with 29:.",
      ],
      label: 'User ID',
      placeholder: '412398765432109876',
      hint: 'A Discord user ID or a Slack member ID.',
      addIcon: 'user-plus',
      pairIcon: 'link',
      pairingBesideButton: true,
      list: 'Allowed people',
    });
  });

  for (const [ran, uptime] of [
    [null, '—'],
    [5_000, '5 s'],
    [65_000, '1 min'],
    [59 * 60_000 + 59_000, '59 min'],
    [2 * 3_600_000 + 14 * 60_000 + 30_000, '2 h 14 min'],
  ] as const) {
    it(`a worker that has run ${ran === null ? 'not at all' : `${ran / 1000} s`}: the line under the chip reads "${ran === null ? '' : 'PID 4242 · '}Uptime ${uptime} · Restarts 0"`, async () => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(new Date('2026-10-30T10:00:00.000Z'));
      const fixture = await render(
        makeBridge(),
        makeWizard(),
        ran === null
          ? { state: 'stopped', restarts: 0 }
          : {
              state: 'running',
              pid: 4242,
              restarts: 0,
              startedAt: new Date(Date.parse('2026-10-30T10:00:00.000Z') - ran).toISOString(),
            },
      );

      expect(
        el(fixture, 'worker-section')!
          .querySelector('.meta')
          ?.textContent?.replace(/\s+/g, ' ')
          .trim(),
      ).toBe(`${ran === null ? '' : 'PID 4242 · '}Uptime ${uptime} · Restarts 0`);
    });
  }

  it("a Stop main refuses says, after the artboard's alert glyph, that the worker is still running, then main's reason", async () => {
    const bridge = makeBridge();
    bridge.workerStop.mockRejectedValueOnce(
      new Error('[scout:worker/stop-failed] The worker did not stop in time.'),
    );
    const fixture = await render(bridge);

    el(fixture, 'worker-stop')!.click();
    await settle(fixture);

    expect({
      text: text(fixture, 'worker-error'),
      icon: iconOf(el(fixture, 'worker-error')),
    }).toEqual({
      text: 'Scout could not stop the worker. It is still running. The worker did not stop in time.',
      icon: 'alert',
    });
  });

  function snackbars(): { text: string; action: string | null }[] {
    return Array.from(
      document.querySelectorAll<HTMLElement>('.cdk-overlay-container .sc-notice'),
    ).map((bar) => ({
      text:
        bar
          .querySelector('.mat-mdc-snack-bar-label [aria-hidden="true"]')
          ?.textContent?.replace(/\s+/g, ' ')
          .replace('Retry', '')
          .trim() ?? '',
      action: bar.querySelector('button')?.textContent?.trim() ?? null,
    }));
  }

  it('a Stop main refuses also shows the artboard snackbar, "Could not stop the Scout worker. Try again." with Retry, and leaves the focus on Stop', async () => {
    const bridge = makeBridge();
    bridge.workerStop.mockRejectedValueOnce(
      new Error('[scout:worker/stop-failed] The worker did not stop in time.'),
    );
    const fixture = await render(bridge);
    const stop = el(fixture, 'worker-stop') as HTMLButtonElement;
    stop.focus();

    stop.click();
    await settle(fixture);

    expect({ bars: snackbars(), focused: document.activeElement === stop }).toEqual({
      bars: [{ text: 'Could not stop the Scout worker. Try again.', action: 'Retry' }],
      focused: true,
    });
    TestBed.inject(MatSnackBar).dismiss();
  });

  it('pressing Retry puts the focus back on Stop, where it was before the notice', async () => {
    const bridge = makeBridge();
    bridge.workerStop.mockRejectedValue(
      new Error('[scout:worker/stop-failed] The worker did not stop in time.'),
    );
    const fixture = await render(bridge);
    el(fixture, 'worker-stop')!.click();
    await settle(fixture);
    const retry = document.querySelector<HTMLButtonElement>(
      '.cdk-overlay-container .sc-notice button',
    )!;
    retry.focus();

    retry.click();
    await settle(fixture);

    expect(document.activeElement?.getAttribute('data-test')).toBe('worker-stop');
    TestBed.inject(MatSnackBar).dismiss();
  });

  for (const second of ['stops', 'fails again'] as const) {
    it(`Retry asks main to stop the worker again; when it ${second}, ${second === 'stops' ? 'no snackbar is left' : 'the snackbar shows again, once'}`, async () => {
      const bridge = makeBridge();
      bridge.workerStop.mockRejectedValueOnce(
        new Error('[scout:worker/stop-failed] The worker did not stop in time.'),
      );
      if (second === 'fails again') {
        bridge.workerStop.mockRejectedValueOnce(
          new Error('[scout:worker/stop-failed] The worker did not stop in time.'),
        );
      }
      const fixture = await render(bridge);
      el(fixture, 'worker-stop')!.click();
      await settle(fixture);

      document
        .querySelector<HTMLButtonElement>('.cdk-overlay-container .sc-notice button')!
        .click();
      await settle(fixture);
      await settle(fixture);

      expect({ stops: bridge.workerStop.mock.calls.length, bars: snackbars() }).toEqual({
        stops: 2,
        bars:
          second === 'stops'
            ? []
            : [{ text: 'Could not stop the Scout worker. Try again.', action: 'Retry' }],
      });
      TestBed.inject(MatSnackBar).dismiss();
    });
  }

  it('Floating button and quick ask: the lede as drawn, and Corner and Display as visible labels over their groups', async () => {
    const fixture = await render(makeBridge());
    const groups = Array.from(
      el(fixture, 'shortcut-section')!.querySelectorAll(
        '[role="group"]',
      ) as NodeListOf<HTMLElement>,
      (group) => labelledBy(group),
    );

    expect({
      lede: el(fixture, 'shortcut-section')!.querySelector('.lede')?.textContent?.trim(),
      groups,
    }).toEqual({
      lede: 'A small round Scout button stays above your windows. Click it to open the quick-ask box in the same corner.',
      groups: ['Corner', 'Display', 'Quick-ask shortcut'],
    });
  });

  it('the last check reads "Last check: 2 Nov 2026, 09:30 · You have the latest version", in local time', async () => {
    const at = new Date(2026, 10, 2, 9, 30).toISOString();
    const fixture = await render(
      makeBridge([], undefined, {
        checkEnabled: true,
        channel: 'stable',
        lastCheckAt: at,
        lastResult: 'up-to-date',
        notice: null,
        packaged: true,
      }),
    );

    expect(text(fixture, 'update-last-check')).toBe(
      'Last check: 2 Nov 2026, 09:30 · You have the latest version',
    );
  });

  it('Audio: the lede in the second text colour, the field names at weight 500 and the Test line at 12 px', async () => {
    const fixture = await render(makeBridge());
    const card = section(fixture, 'Audio');
    const name = card.querySelector('label span')!;

    expect({
      lede: [
        declared(card.querySelector('.lede')!, 'color'),
        declared(card.querySelector('.lede')!, 'line-height'),
      ],
      name: declared(name, 'font-weight'),
      hint: declared(card.querySelector('.hint')!, 'font-size'),
    }).toEqual({ lede: ['var(--sc-fg-2)', '1.5'], name: '500', hint: '12px' });
  });

  for (const test of [
    'settings-theme-dark',
    'bubble-corner-bottom-right',
    'bubble-display-cursor',
  ]) {
    it(`a pressed segment (${test}) is on the pressed colour every artboard draws, in the text colour`, async () => {
      document.documentElement.setAttribute('data-theme', 'dark');
      const theme = {
        themeGet: vi.fn(async () => ({ choice: 'dark' as const, resolved: 'dark' as const })),
        themeSet: vi.fn(),
        onTheme: vi.fn(() => () => undefined),
      };
      const fixture = await render({ ...makeBridge(), ...theme });
      TestBed.inject(ThemeStore).start();
      await settle(fixture);
      const button = el(fixture, test)!;

      expect({
        pressed: button.getAttribute('aria-pressed'),
        background: declared(button, 'background'),
        color: declared(button, 'color'),
      }).toEqual({ pressed: 'true', background: 'var(--sc-pressed)', color: 'var(--sc-fg)' });
      document.documentElement.removeAttribute('data-theme');
    });
  }

  it('Disconnect carries the unplugged plug, as drawn', async () => {
    const fixture = await render(
      makeBridge(),
      makeWizard({ discordServer: 'Ops Guild', slack: true }),
    );

    expect([
      iconOf(el(fixture, 'discord-disconnect')),
      iconOf(el(fixture, 'slack-disconnect')),
    ]).toEqual(['plug-off', 'plug-off']);
  });

  it('Appearance: the theme choices are 34 px pills with 14 px sides in a 22 px-round group, and the line under them is 12 px', async () => {
    const fixture = await render(makeBridge());
    const choice = el(fixture, 'settings-theme-dark')!;
    const card = section(fixture, 'Appearance');

    expect({
      choice: [
        declared(choice, 'height'),
        declared(choice, 'padding'),
        declared(choice, 'border-radius'),
        declared(choice, 'gap'),
      ],
      group: declared(choice.parentElement!, 'border-radius'),
      line: declared(card.querySelector('.hint')!, 'font-size'),
    }).toEqual({ choice: ['34px', '0 14px', '18px', '8px'], group: '22px', line: '12px' });
  });
});

describe('Settings: the floating button display is one row', () => {
  const PRIMARY = { id: 33, name: 'External display 3440×1440', primary: true };
  const BUILT_IN = { id: 35, name: 'Built-in display 1920×1080', primary: false };
  const SIDE = { id: 41, name: 'External display 2560×1440', primary: false };
  const MISSING_MONITOR_HINT =
    'The monitor chosen for the floating button is not connected; the button is on the primary display until it is back.';
  const LAYOUTS = {
    'one monitor': [PRIMARY],
    'two monitors, primary first': [PRIMARY, BUILT_IN],
    'two monitors, primary second': [BUILT_IN, PRIMARY],
    'three monitors, primary second': [BUILT_IN, PRIMARY, SIDE],
  } as const;
  type Layout = keyof typeof LAYOUTS;
  type Stored = LocalBubbleSettings['display'];
  const STORED: readonly [string, Stored][] = [
    ['cursor', 'cursor'],
    ['primary', 'primary'],
    ['the id of the primary', 33],
    ['the id of the built-in display', 35],
    ['the id of an unplugged monitor', 99],
  ];

  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  function label(monitor: { name: string; primary: boolean }): string {
    return monitor.primary ? `${monitor.name} (primary)` : monitor.name;
  }

  async function renderWith(layout: Layout, display: Stored) {
    const monitors = LAYOUTS[layout];
    const unplugged = typeof display === 'number' && !monitors.some((m) => m.id === display);
    const bridge = makeBridge();
    bridge.localGetBubble.mockResolvedValue({
      enabled: true,
      corner: 'bottom-right',
      display,
      monitors,
      hint: unplugged ? MISSING_MONITOR_HINT : null,
    });
    bridge.localSetBubble.mockImplementation(
      async (req: LocalBubbleSettings): Promise<LocalBubbleStatus> => ({
        ...req,
        monitors,
        hint: null,
      }),
    );
    const fixture = await render(bridge);
    return { fixture, bridge, monitors, unplugged };
  }

  function row(fixture: ComponentFixture<SettingsComponent>) {
    const group = el(fixture, 'bubble-display');
    return {
      shown: group !== null,
      cursorButton: el(fixture, 'bubble-display-cursor') !== null,
      label: group === null ? null : labelledBy(group),
      buttons: Array.from(
        (group?.querySelectorAll('button') ?? []) as NodeListOf<HTMLButtonElement>,
        (button) => button.textContent?.trim(),
      ),
      pressed: Array.from(
        (group?.querySelectorAll('button[aria-pressed="true"]') ??
          []) as NodeListOf<HTMLButtonElement>,
        (button) => button.textContent?.trim(),
      ),
      hint: el(fixture, 'bubble-hint')?.textContent?.trim() ?? null,
    };
  }

  function expectedPressed(
    monitors: readonly { id: number; name: string; primary: boolean }[],
    display: Stored,
  ): string[] {
    if (display === 'cursor') return ['Follow the mouse cursor'];
    const hit = monitors.find((m) => (display === 'primary' ? m.primary : m.id === display));
    return hit === undefined ? [] : [label(hit)];
  }

  it('the primary changing while Settings is open moves the pressed button with it when the stored value is primary', async () => {
    const { fixture, bridge } = await renderWith('two monitors, primary first', 'primary');
    const before = row(fixture).pressed;

    bridge.emitBubble({
      enabled: true,
      corner: 'bottom-right',
      display: 'primary',
      monitors: [
        { ...PRIMARY, primary: false },
        { ...BUILT_IN, primary: true },
      ],
      hint: null,
    });
    await settle(fixture);

    expect([before, row(fixture).pressed, bridge.localSetBubble.mock.calls]).toEqual([
      ['External display 3440×1440 (primary)'],
      ['Built-in display 1920×1080 (primary)'],
      [],
    ]);
  });

  it('a stored monitor id that has become the primary stays stored as that id when its pressed button is pressed again', async () => {
    const { fixture, bridge } = await renderWith('two monitors, primary first', 33);

    (el(fixture, 'bubble-monitor-33') as HTMLButtonElement).click();
    await settle(fixture);

    expect([bridge.localSetBubble.mock.calls, row(fixture).pressed]).toEqual([
      [],
      ['External display 3440×1440 (primary)'],
    ]);
  });

  it('two monitors of the same make and size, told apart by main with where they sit, have buttons a screen reader names differently', async () => {
    const bridge = makeBridge();
    const twins = [
      { id: 33, name: 'External display 3440×1440 at 1920, 0', primary: true },
      { id: 34, name: 'External display 3440×1440 at 5360, 0', primary: false },
    ];
    bridge.localGetBubble.mockResolvedValue({
      enabled: true,
      corner: 'bottom-right',
      display: 'cursor',
      monitors: twins,
      hint: null,
    });
    const fixture = await render(bridge);
    const names = row(fixture).buttons;

    expect({ names, unique: new Set(names).size === names.length }).toEqual({
      names: [
        'Follow the mouse cursor',
        'External display 3440×1440 at 1920, 0 (primary)',
        'External display 3440×1440 at 5360, 0',
      ],
      unique: true,
    });
  });

  for (const layout of Object.keys(LAYOUTS) as Layout[]) {
    for (const [name, display] of STORED) {
      it(`${layout}, stored ${name}: ${LAYOUTS[layout].length > 1 ? 'one Display row with the cursor first and each monitor by name' : 'no Display row'}, the right button pressed, the hint only for a monitor not connected`, async () => {
        const { fixture, monitors, unplugged } = await renderWith(layout, display);

        expect(row(fixture)).toEqual(
          monitors.length > 1
            ? {
                shown: true,
                cursorButton: true,
                label: 'Display',
                buttons: ['Follow the mouse cursor', ...monitors.map(label)],
                pressed: expectedPressed(monitors, display),
                hint: unplugged ? MISSING_MONITOR_HINT : null,
              }
            : {
                shown: false,
                cursorButton: false,
                label: null,
                buttons: [],
                pressed: [],
                hint: unplugged ? MISSING_MONITOR_HINT : null,
              },
        );
      });

      if (LAYOUTS[layout].length < 2) continue;
      const choices = ['Follow the mouse cursor', ...LAYOUTS[layout].map(label)];
      for (const [index, choice] of choices.entries()) {
        const monitor = index === 0 ? null : LAYOUTS[layout][index - 1]!;
        const sent: Stored = monitor === null ? 'cursor' : monitor.primary ? 'primary' : monitor.id;
        const already = expectedPressed(LAYOUTS[layout], display).includes(choice);
        it(`${layout}, stored ${name}, pressing "${choice}": ${already ? 'it is pressed already, so nothing is stored' : `stores ${JSON.stringify(sent)}`} and "${choice}" alone is pressed`, async () => {
          const { fixture, bridge } = await renderWith(layout, display);
          const button = Array.from(
            el(fixture, 'bubble-display')!.querySelectorAll(
              'button',
            ) as NodeListOf<HTMLButtonElement>,
          ).find((b) => b.textContent?.trim() === choice)!;

          button.click();
          await settle(fixture);

          expect({
            sent: bridge.localSetBubble.mock.calls.map(([req]) => req),
            pressed: row(fixture).pressed,
          }).toEqual({
            sent: already ? [] : [{ enabled: true, corner: 'bottom-right', display: sent }],
            pressed: [choice],
          });
        });
      }
    }
  }
});

describe('Settings: Record shortcut', () => {
  const SAVED = 'CommandOrControl+Alt+Space';
  const RECORDING = 'Recording. Press the new shortcut, or Esc to cancel.';

  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  function keycaps(fixture: ComponentFixture<SettingsComponent>): string[] {
    return Array.from(
      el(fixture, 'shortcut-input')!.querySelectorAll('kbd'),
      (kbd) => kbd.textContent?.trim() ?? '',
    );
  }

  function recordButton(fixture: ComponentFixture<SettingsComponent>): HTMLButtonElement {
    return el(fixture, 'shortcut-record') as HTMLButtonElement;
  }

  async function startRecording(fixture: ComponentFixture<SettingsComponent>): Promise<void> {
    recordButton(fixture).click();
    await settle(fixture);
  }

  /** Presses one key on the focused Record shortcut button and reports whether the page past it saw the press. */
  async function press(
    fixture: ComponentFixture<SettingsComponent>,
    init: KeyboardEventInit,
  ): Promise<{ prevented: boolean; reachedPage: boolean }> {
    let reachedPage = false;
    const page = (): void => {
      reachedPage = true;
    };
    document.addEventListener('keydown', page);
    const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init });
    recordButton(fixture).dispatchEvent(event);
    document.removeEventListener('keydown', page);
    await settle(fixture);
    return { prevented: event.defaultPrevented, reachedPage };
  }

  async function save(fixture: ComponentFixture<SettingsComponent>): Promise<void> {
    (el(fixture, 'shortcut-save') as HTMLButtonElement).click();
    await settle(fixture);
  }

  function state(fixture: ComponentFixture<SettingsComponent>) {
    return {
      recording: recordButton(fixture).getAttribute('aria-pressed'),
      keys: keycaps(fixture),
      waiting:
        el(fixture, 'shortcut-input')?.querySelector('.keys-waiting')?.textContent?.trim() ?? null,
      status: text(fixture, 'shortcut-status'),
      refused: el(fixture, 'shortcut-refused')?.textContent?.trim() ?? null,
      saveDisabled: (el(fixture, 'shortcut-save') as HTMLButtonElement).disabled,
    };
  }

  it('the saved shortcut is drawn as keycaps, labelled "Quick-ask shortcut", beside Record shortcut and Save, with the help the artboard writes', async () => {
    const fixture = await render(makeBridge());
    const field = el(fixture, 'shortcut-input')!;

    expect({
      keys: keycaps(fixture),
      label: labelledBy(field),
      help: document
        .getElementById(field.getAttribute('aria-describedby') ?? '')
        ?.textContent?.trim(),
      record: [
        recordButton(fixture).tagName,
        recordButton(fixture).type,
        text(fixture, 'shortcut-record'),
      ],
      recordIcon: iconOf(recordButton(fixture)),
      save: text(fixture, 'shortcut-save'),
      liveRegion: el(fixture, 'shortcut-status')?.getAttribute('role'),
    }).toEqual({
      keys: ['Ctrl', 'Alt', 'Space'],
      label: 'Quick-ask shortcut',
      help: 'Click Record shortcut, then press the keys. Press it anywhere to ask Scout, typed or spoken.',
      record: ['BUTTON', 'button', 'Record shortcut'],
      recordIcon: 'keyboard',
      save: 'Save',
      liveRegion: 'status',
    });
  });

  it('on a Mac the keycaps read Cmd and Option', async () => {
    vi.spyOn(navigator, 'platform', 'get').mockReturnValue('MacIntel');
    const fixture = await render(makeBridge());

    expect(keycaps(fixture)).toEqual(['Cmd', 'Option', 'Space']);
  });

  it('Record shortcut starts recording: pressed, the field waits for the keys, a screen reader hears how, Save waits, and main lets the saved shortcut go', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await startRecording(fixture);

    expect({ ...state(fixture), suspend: bridge.localSuspendShortcut.mock.calls }).toEqual({
      recording: 'true',
      keys: [],
      waiting: 'Press the new shortcut',
      status: RECORDING,
      refused: null,
      saveDisabled: true,
      suspend: [[{ suspended: true }]],
    });
  });

  for (const [what, init, keys, accelerator] of [
    [
      'a letter',
      { code: 'KeyK', key: 'k', ctrlKey: true, altKey: true },
      ['Ctrl', 'Alt', 'K'],
      'CommandOrControl+Alt+K',
    ],
    [
      'a digit',
      { code: 'Digit4', key: '$', ctrlKey: true, shiftKey: true },
      ['Ctrl', 'Shift', '4'],
      'CommandOrControl+Shift+4',
    ],
    [
      'Space',
      { code: 'Space', key: ' ', altKey: true, metaKey: true },
      ['Super', 'Alt', 'Space'],
      'Super+Alt+Space',
    ],
    [
      'a function key',
      { code: 'F8', key: 'F8', ctrlKey: true },
      ['Ctrl', 'F8'],
      'CommandOrControl+F8',
    ],
    [
      'an arrow',
      { code: 'ArrowLeft', key: 'ArrowLeft', ctrlKey: true, altKey: true },
      ['Ctrl', 'Alt', 'Left'],
      'CommandOrControl+Alt+Left',
    ],
    ['the comma', { code: 'Comma', key: ',', ctrlKey: true }, ['Ctrl', ','], 'CommandOrControl+,'],
    [
      'the keypad plus',
      { code: 'NumpadAdd', key: '+', altKey: true },
      ['Alt', 'Num +'],
      'Alt+numadd',
    ],
    [
      'the AZERTY key that types "a" (at the QWERTY "Q" position), by its character',
      { code: 'KeyQ', key: 'a', ctrlKey: true, altKey: true },
      ['Ctrl', 'Alt', 'A'],
      'CommandOrControl+Alt+A',
    ],
  ] as const) {
    it(`recording ${what} shows it as keycaps, says so, keeps the press from the page, and Save stores ${accelerator}`, async () => {
      const bridge = makeBridge();
      const fixture = await render(bridge);
      await startRecording(fixture);

      const seen = await press(fixture, init);
      const recorded = state(fixture);
      await save(fixture);

      expect({
        seen,
        recorded,
        sent: bridge.localSetShortcut.mock.calls,
        after: keycaps(fixture),
      }).toEqual({
        seen: { prevented: true, reachedPage: false },
        recorded: {
          recording: 'false',
          keys,
          waiting: null,
          status: `New shortcut: ${keys.join(' ')}. Press Save to keep it.`,
          refused: null,
          saveDisabled: false,
        },
        sent: [[{ accelerator }]],
        after: keys,
      });
    });
  }

  for (const code of ['ControlLeft', 'AltRight', 'ShiftLeft', 'MetaLeft'] as const) {
    it(`${code} alone keeps the recording waiting and reaches nothing else`, async () => {
      const fixture = await render(makeBridge());
      await startRecording(fixture);

      const seen = await press(fixture, {
        code,
        key: code.replace(/Left|Right/, ''),
        ctrlKey: true,
      });

      expect({ seen, ...state(fixture) }).toMatchObject({
        seen: { prevented: true, reachedPage: false },
        recording: 'true',
        waiting: 'Press the new shortcut',
        refused: null,
      });
    });
  }

  for (const [what, init, refusal] of [
    [
      'a key with no modifier',
      { code: 'KeyK', key: 'k' },
      '"K" has no modifier, so it would fire whenever you type it; add Ctrl, Alt, Super or Cmd.',
    ],
    [
      'a key with Shift alone',
      { code: 'KeyK', key: 'K', shiftKey: true },
      '"Shift+K" has no modifier but Shift, so it would fire whenever you type it; add Ctrl, Alt, Super or Cmd.',
    ],
  ] as const) {
    it(`${what} is refused with a line saying why, and the recording keeps waiting for a whole shortcut`, async () => {
      const bridge = makeBridge();
      const fixture = await render(bridge);
      await startRecording(fixture);

      const seen = await press(fixture, init);
      const refused = state(fixture);
      await press(fixture, { code: 'KeyJ', key: 'j', ctrlKey: true, altKey: true });

      expect({ seen, refused, then: state(fixture).keys }).toEqual({
        seen: { prevented: true, reachedPage: false },
        refused: {
          recording: 'true',
          keys: [],
          waiting: 'Press the new shortcut',
          status: RECORDING,
          refused: refusal,
          saveDisabled: true,
        },
        then: ['Ctrl', 'Alt', 'J'],
      });
    });
  }

  it('Esc cancels the recording and restores the saved shortcut, even over one recorded before, and main gets its shortcut back', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await startRecording(fixture);
    await press(fixture, { code: 'KeyK', key: 'k', ctrlKey: true, altKey: true });
    await startRecording(fixture);

    const seen = await press(fixture, { code: 'Escape', key: 'Escape' });

    expect({
      seen,
      ...state(fixture),
      suspend: bridge.localSuspendShortcut.mock.calls.map(([r]) => r.suspended),
    }).toEqual({
      seen: { prevented: true, reachedPage: false },
      recording: 'false',
      keys: ['Ctrl', 'Alt', 'Space'],
      waiting: null,
      status: 'Recording cancelled. The shortcut is unchanged.',
      refused: null,
      saveDisabled: false,
      suspend: [true, false, true, false],
    });
  });

  for (const [what, init] of [
    ['Tab', { code: 'Tab', key: 'Tab' }],
    ['Shift+Tab', { code: 'Tab', key: 'Tab', shiftKey: true }],
  ] as const) {
    it(`${what} is never trapped: it moves focus as usual and ends the recording with the shortcut unchanged, which a screen reader is told`, async () => {
      const bridge = makeBridge();
      const fixture = await render(bridge);
      await startRecording(fixture);

      const seen = await press(fixture, init);

      expect({
        seen,
        ...state(fixture),
        suspend: bridge.localSuspendShortcut.mock.calls.map(([r]) => r.suspended),
      }).toEqual({
        seen: { prevented: false, reachedPage: true },
        recording: 'false',
        keys: ['Ctrl', 'Alt', 'Space'],
        waiting: null,
        status: 'Recording ended. The shortcut is unchanged.',
        refused: null,
        saveDisabled: false,
        suspend: [true, false],
      });
    });
  }

  it('leaving the button ends the recording with the shortcut unchanged, which a screen reader is told', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await startRecording(fixture);
    await press(fixture, { code: 'KeyK', key: 'k' });

    recordButton(fixture).blur();
    await settle(fixture);

    expect({
      ...state(fixture),
      suspend: bridge.localSuspendShortcut.mock.calls.map(([r]) => r.suspended),
    }).toEqual({
      recording: 'false',
      keys: ['Ctrl', 'Alt', 'Space'],
      waiting: null,
      status: 'Recording ended. The shortcut is unchanged.',
      refused: null,
      saveDisabled: false,
      suspend: [true, false],
    });
  });

  for (const [what, init] of [
    [
      'Space, the key that presses a button,',
      { code: 'Space', key: ' ', ctrlKey: true, altKey: true },
    ],
    ['a letter', { code: 'KeyK', key: 'k', ctrlKey: true, altKey: true }],
    ['Esc', { code: 'Escape', key: 'Escape' }],
  ] as const) {
    it(`the release of ${what} recorded is kept from the button too, so it cannot press Record shortcut again; another key's release is left alone`, async () => {
      const fixture = await render(makeBridge());
      await startRecording(fixture);
      await press(fixture, init);
      const release = (code: string): boolean => {
        const up = new KeyboardEvent('keyup', { bubbles: true, cancelable: true, code, key: code });
        recordButton(fixture).dispatchEvent(up);
        return up.defaultPrevented;
      };

      expect([release('Tab'), release(init.code), release(init.code)]).toEqual([
        false,
        true,
        false,
      ]);
    });
  }

  it('Record shortcut takes the focus, so the next key press reaches it after a mouse click', async () => {
    const fixture = await render(makeBridge());
    (document.activeElement as HTMLElement | null)?.blur();

    await startRecording(fixture);

    expect(document.activeElement).toBe(recordButton(fixture));
  });

  it('a second press of Record shortcut stops the recording with the shortcut unchanged', async () => {
    const fixture = await render(makeBridge());
    await startRecording(fixture);

    recordButton(fixture).click();
    await settle(fixture);

    expect(state(fixture)).toMatchObject({ recording: 'false', keys: ['Ctrl', 'Alt', 'Space'] });
  });

  it('a key pressed on the button while it is not recording is left alone and reaches the page', async () => {
    const fixture = await render(makeBridge());
    recordButton(fixture).focus();

    const seen = await press(fixture, { code: 'KeyK', key: 'k', ctrlKey: true, altKey: true });

    expect({ seen, keys: keycaps(fixture) }).toEqual({
      seen: { prevented: false, reachedPage: true },
      keys: ['Ctrl', 'Alt', 'Space'],
    });
  });

  it('the same combination recorded twice is one shortcut, and Save sends it once', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    for (let i = 0; i < 2; i++) {
      await startRecording(fixture);
      await press(fixture, { code: 'KeyK', key: 'k', ctrlKey: true, altKey: true });
    }

    await save(fixture);

    expect({ keys: keycaps(fixture), sent: bridge.localSetShortcut.mock.calls }).toEqual({
      keys: ['Ctrl', 'Alt', 'K'],
      sent: [[{ accelerator: 'CommandOrControl+Alt+K' }]],
    });
  });

  it('the saved shortcut pressed while recording reaches the page, because main let it go first, and is recorded as itself', async () => {
    const bridge = makeBridge();
    const order: string[] = [];
    bridge.localSuspendShortcut.mockImplementation(async (req: { suspended: boolean }) => {
      order.push(req.suspended ? 'let go' : 'taken back');
      return { accelerator: SAVED, registered: true, hint: null };
    });
    const fixture = await render(bridge);
    await startRecording(fixture);
    order.push('pressed');

    await press(fixture, { code: 'Space', key: ' ', ctrlKey: true, altKey: true });
    await save(fixture);

    expect({ order, keys: keycaps(fixture), sent: bridge.localSetShortcut.mock.calls }).toEqual({
      order: ['let go', 'pressed', 'taken back'],
      keys: ['Ctrl', 'Alt', 'Space'],
      sent: [[{ accelerator: SAVED }]],
    });
  });

  for (const [how, end] of [
    ['a recorded combination', { code: 'KeyK', key: 'k', ctrlKey: true, altKey: true }],
    ['Esc', { code: 'Escape', key: 'Escape' }],
    ['Tab', { code: 'Tab', key: 'Tab' }],
  ] as const) {
    it(`a recording ended by ${how} whose shortcut the system will not give back says so where a save's error shows, and the keycaps stay on the saved shortcut`, async () => {
      const bridge = makeBridge();
      bridge.localSuspendShortcut.mockImplementation(async (req: { suspended: boolean }) => ({
        accelerator: SAVED,
        registered: req.suspended,
        hint: null,
      }));
      const fixture = await render(bridge);
      await startRecording(fixture);

      await press(fixture, end);
      await settle(fixture);

      expect({ keys: keycaps(fixture), error: text(fixture, 'shortcut-error') }).toEqual({
        keys: ['Ctrl', 'Alt', 'Space'],
        error:
          'Scout could not register Ctrl+Alt+Space again: another application may hold it now. Choose another shortcut.',
      });
    });
  }

  it('a shortcut the system never registered (Wayland) says nothing more after a recording, as it is still not registered', async () => {
    const bridge = makeBridge();
    bridge.localGetShortcut.mockResolvedValueOnce({
      accelerator: SAVED,
      registered: false,
      hint: null,
    });
    bridge.localSuspendShortcut.mockImplementation(async () => ({
      accelerator: SAVED,
      registered: false,
      hint: null,
    }));
    const fixture = await render(bridge);
    await startRecording(fixture);

    await press(fixture, { code: 'Escape', key: 'Escape' });
    await settle(fixture);

    expect(el(fixture, 'shortcut-error')).toBeNull();
  });

  it('a take-back that fails as a call shows nothing new and keeps the recorded keycaps for Save', async () => {
    const bridge = makeBridge();
    bridge.localSuspendShortcut.mockImplementation(async (req: { suspended: boolean }) => {
      if (!req.suspended) throw new Error('connection reset');
      return { accelerator: SAVED, registered: false, hint: null };
    });
    const fixture = await render(bridge);
    await startRecording(fixture);

    await press(fixture, { code: 'KeyK', key: 'k', ctrlKey: true, altKey: true });
    await settle(fixture);

    expect({ keys: keycaps(fixture), error: el(fixture, 'shortcut-error') }).toEqual({
      keys: ['Ctrl', 'Alt', 'K'],
      error: null,
    });
  });

  it('Settings closed while recording gives main its shortcut back', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await startRecording(fixture);

    fixture.destroy();

    expect(bridge.localSuspendShortcut.mock.calls.map(([r]) => r.suspended)).toEqual([true, false]);
  });

  it('when main cannot let the shortcut go, the recording still records every other combination', async () => {
    const bridge = makeBridge();
    bridge.localSuspendShortcut.mockRejectedValue(new Error('[scout:ipc/not-main-window] no'));
    const fixture = await render(bridge);
    await startRecording(fixture);

    await press(fixture, { code: 'KeyK', key: 'k', ctrlKey: true, altKey: true });

    expect(state(fixture)).toMatchObject({
      recording: 'false',
      keys: ['Ctrl', 'Alt', 'K'],
      refused: null,
    });
  });

  it('a Save main refuses shows its reason and puts the keycaps back to the saved shortcut', async () => {
    const bridge = makeBridge();
    bridge.localSetShortcut.mockRejectedValueOnce(
      new Error('[scout:quick-ask/shortcut-taken] That shortcut is taken'),
    );
    const fixture = await render(bridge);
    await startRecording(fixture);
    await press(fixture, { code: 'KeyT', key: 't', ctrlKey: true, altKey: true });

    await save(fixture);

    expect({ keys: keycaps(fixture), error: text(fixture, 'shortcut-error') }).toEqual({
      keys: ['Ctrl', 'Alt', 'Space'],
      error: 'That shortcut is taken',
    });
  });

  it('a Save that fails as the call itself fails says why and keeps the saved shortcut', async () => {
    const bridge = makeBridge();
    bridge.localSetShortcut.mockRejectedValueOnce(new Error('connection reset'));
    const fixture = await render(bridge);
    await startRecording(fixture);
    await press(fixture, { code: 'KeyT', key: 't', ctrlKey: true, altKey: true });

    await save(fixture);

    expect({ keys: keycaps(fixture), error: text(fixture, 'shortcut-error') }).toEqual({
      keys: ['Ctrl', 'Alt', 'Space'],
      error: 'connection reset',
    });
  });

  it('shows the Wayland hint under the shortcut and keeps Record shortcut usable', async () => {
    const bridge = makeBridge();
    const hint =
      'Your desktop (Wayland) does not let apps register global shortcuts. Add a custom shortcut in your system settings that runs: /home/owner/Apps/Scout.AppImage --ask';
    bridge.localGetShortcut.mockResolvedValueOnce({ accelerator: SAVED, registered: true, hint });
    const fixture = await render(bridge);

    expect({
      hint: text(fixture, 'shortcut-hint'),
      record: recordButton(fixture).disabled,
    }).toEqual({
      hint,
      record: false,
    });
  });

  it('shows the hint main returns after saving a shortcut', async () => {
    const bridge = makeBridge();
    bridge.localSetShortcut.mockResolvedValueOnce({
      accelerator: 'Alt+Shift+S',
      registered: true,
      hint: 'Add a custom shortcut in your system settings that runs scout-desktop --ask.',
    });
    const fixture = await render(bridge);
    await startRecording(fixture);
    await press(fixture, { code: 'KeyS', key: 'S', altKey: true, shiftKey: true });

    await save(fixture);

    expect({ hint: text(fixture, 'shortcut-hint'), keys: keycaps(fixture) }).toEqual({
      hint: 'Add a custom shortcut in your system settings that runs scout-desktop --ask.',
      keys: ['Alt', 'Shift', 'S'],
    });
  });

  it('shows no hint when the shortcut status carries none', async () => {
    const fixture = await render(makeBridge());

    expect(el(fixture, 'shortcut-hint')).toBeNull();
  });
});
