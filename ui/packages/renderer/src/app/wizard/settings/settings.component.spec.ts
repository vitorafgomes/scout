import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type {
  AccessAllowedUsers,
  AccessPairing,
  DesktopSettings,
  DesktopToggleRequest,
  LocalBubbleSettings,
  LocalShortcut,
  UpdateState,
  WorkerEvent,
  WorkerStatus,
} from '@scout/shared';

import { LogStore } from '../../core/log.store';
import { MenuPreferences } from '../../core/menu-preferences';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

import { PostConnectShellComponent } from '../../shell/post-connect-shell.component';
import { SettingsComponent } from './settings.component';

function makeBridge(
  initial: readonly string[] = [],
  desktop: DesktopSettings = { keepRunning: true, startAtLogin: false, trayAvailable: true },
  update: UpdateState = {
    checkEnabled: true,
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
  const workerCalls: string[] = [];
  return {
    workerCalls,
    workerStart: vi.fn(async () => {
      workerCalls.push('start');
      return { pid: 4242 };
    }),
    workerStop: vi.fn(async () => {
      workerCalls.push('stop');
      return { exitCode: 0 };
    }),
    emitWorker: (event: WorkerEvent) => workerListeners.forEach((listener) => listener(event)),
    onWorkerEvent: vi.fn((listener: (event: WorkerEvent) => void) => {
      workerListeners.push(listener);
      return () => undefined;
    }),
    appGetVersion: vi.fn(async () => ({ version: '1.0.0' })),
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
    localSetShortcut: vi.fn(async (req: { accelerator: string }): Promise<LocalShortcut> => ({
      accelerator: req.accelerator,
      registered: true,
      hint: null,
    })),
    localGetBubble: vi.fn(async (): Promise<LocalBubbleSettings> => ({
      enabled: true,
      corner: 'bottom-right',
      display: 'cursor',
    })),
    localSetBubble: vi.fn(async (req: LocalBubbleSettings): Promise<LocalBubbleSettings> => req),
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
}

function makeWizard(clients: { discordServer?: string | null; slack?: boolean } = { slack: true }) {
  const discord = clients.discordServer !== undefined && clients.discordServer !== null;
  return {
    hasDiscord: signal(discord),
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

async function type(fixture: ComponentFixture<SettingsComponent>, value: string): Promise<void> {
  const input = el(fixture, 'access-input') as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

describe('Settings: Chat clients', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('opens with a Chat clients section: one card each for Discord, Slack and Teams, with their state', async () => {
    const fixture = await render(
      makeBridge(),
      makeWizard({ discordServer: 'Ops Guild', slack: false }),
    );

    const sections = Array.from(
      fixture.nativeElement.querySelectorAll('main > *') as NodeListOf<HTMLElement>,
    ).filter((node) => node.tagName !== 'H1');
    expect(sections[0]?.tagName).toBe('SC-CHAT-CLIENTS');
    expect(sections[1]?.tagName).toBe('SC-WORKER-SETTINGS');
    expect(sections[2]?.getAttribute('data-test')).toBe('access-section');
    expect(text(fixture, 'discord-state')).toBe('Connected');
    expect(text(fixture, 'discord-server')).toBe('Server: Ops Guild');
    expect(text(fixture, 'slack-state')).toBe('Not connected');
    expect(text(fixture, 'teams-state')).toBe('Coming soon');
    expect((el(fixture, 'teams-connect') as HTMLButtonElement).disabled).toBe(true);
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
  });

  it('shows the worker state, PID, uptime and restarts right after Chat clients', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-30T10:00:00.000Z'));
    const fixture = await render(makeBridge(), makeWizard(), {
      state: 'running',
      pid: 4242,
      restarts: 2,
      startedAt: '2026-10-30T09:58:55.000Z',
    });

    expect(text(fixture, 'worker-state')).toBe('running');
    expect(text(fixture, 'worker-pid')).toBe('PID 4242');
    expect(text(fixture, 'worker-uptime')).toBe('Uptime: 1m 5s');
    expect(text(fixture, 'worker-restarts')).toBe('Restarts: 2');
  });

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
    const toggle = el(fixture, 'show-logs') as HTMLInputElement;
    expect(toggle.checked).toBe(true);

    toggle.checked = false;
    toggle.dispatchEvent(new Event('change'));
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

    expect(text(fixture, 'slack-connected')).toBe('Connected to Acme as scout');
  });

  it('tells the owner where each platform shows a user id', async () => {
    const fixture = await render(makeBridge());

    expect(text(fixture, 'access-help')).toBe(
      "Discord: turn on Developer Mode, right-click a user, Copy User ID. Slack: profile, More, Copy member ID. Teams: the user's Bot Framework user ID, which starts with 29:.",
    );
  });

  it('Pair from chat shows the bare code to send to Scout in a direct message on Discord', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    expect(el(fixture, 'access-pair-instructions')).toBeNull();
    (el(fixture, 'access-pair') as HTMLButtonElement).click();
    await settle(fixture);

    expect(text(fixture, 'access-pair-instructions')).toBe(
      'Send 482913 to Scout in a direct message on Discord.',
    );
    expect(el(fixture, 'access-pair-instructions')?.querySelector('code')?.textContent).toBe(
      '482913',
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
      'Send 482913 to Scout in a direct message on Discord.',
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

    expect(bridge.accessSet).toHaveBeenNthCalledWith(2, { userIds: ['222'] });
    expect(listed(fixture)).toEqual(['222']);
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

  function checkbox(fixture: ComponentFixture<SettingsComponent>, test: string): HTMLInputElement {
    return el(fixture, test) as HTMLInputElement;
  }

  async function toggle(fixture: ComponentFixture<SettingsComponent>, test: string): Promise<void> {
    checkbox(fixture, test).click();
    await settle(fixture);
  }

  it('shows "Keep Scout running when the window is closed" on and "Start Scout when I log in" off by default', async () => {
    const fixture = await render(makeBridge());

    expect(checkbox(fixture, 'desktop-keep-running').parentElement?.textContent?.trim()).toBe(
      'Keep Scout running when the window is closed',
    );
    expect(checkbox(fixture, 'desktop-keep-running').checked).toBe(true);
    expect(checkbox(fixture, 'desktop-start-at-login').parentElement?.textContent?.trim()).toBe(
      'Start Scout when I log in',
    );
    expect(checkbox(fixture, 'desktop-start-at-login').checked).toBe(false);
    expect(el(fixture, 'desktop-no-tray')).toBeNull();
  });

  it('turning keep running off saves it and shows what main stored', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await toggle(fixture, 'desktop-keep-running');

    expect(bridge.desktopSetKeepRunning).toHaveBeenCalledWith({ enabled: false });
    expect(checkbox(fixture, 'desktop-keep-running').checked).toBe(false);
  });

  it('without a system tray, keep running is off and locked, and Settings says why', async () => {
    const fixture = await render(
      makeBridge([], { keepRunning: false, startAtLogin: false, trayAvailable: false }),
    );

    expect(checkbox(fixture, 'desktop-keep-running').checked).toBe(false);
    expect(checkbox(fixture, 'desktop-keep-running').disabled).toBe(true);
    expect(text(fixture, 'desktop-no-tray')).toBe(
      'This desktop has no system tray, so Scout quits when its window is closed.',
    );
  });

  it('turning start at login on reports it on', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await toggle(fixture, 'desktop-start-at-login');

    expect(bridge.desktopSetStartAtLogin).toHaveBeenCalledWith({ enabled: true });
    expect(checkbox(fixture, 'desktop-start-at-login').checked).toBe(true);
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
    expect(checkbox(fixture, 'desktop-start-at-login').checked).toBe(false);
    expect(checkbox(fixture, 'desktop-start-at-login').disabled).toBe(false);
  });
});

describe('Settings: Desktop, new versions', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  const lastCheck = (at: string, result: string) =>
    `Last check: ${new Date(at).toLocaleString()} · ${result}`;

  it('the Check for new versions toggle and Check now reach main', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const toggle = el(fixture, 'update-check-enabled') as HTMLInputElement;

    expect(toggle.parentElement?.textContent?.trim()).toBe('Check for new versions');
    expect(toggle.checked).toBe(true);
    expect(text(fixture, 'update-last-check')).toBe(
      lastCheck('2026-11-02T09:30:00.000Z', 'up-to-date'),
    );

    toggle.click();
    await settle(fixture);
    expect(bridge.updateSetCheckEnabled).toHaveBeenCalledWith({ enabled: false });
    expect((el(fixture, 'update-check-enabled') as HTMLInputElement).checked).toBe(false);

    el(fixture, 'update-check-now')!.click();
    await settle(fixture);
    expect(bridge.updateCheckNow).toHaveBeenCalledTimes(1);
    expect(text(fixture, 'update-last-check')).toBe(
      lastCheck('2026-11-02T10:45:00.000Z', 'available 0.2.0'),
    );
  });

  it('development build shows the explanatory line and no toggle', async () => {
    const fixture = await render(
      makeBridge(
        [],
        { keepRunning: true, startAtLogin: false, trayAvailable: true },
        { checkEnabled: true, lastCheckAt: null, lastResult: null, notice: null, packaged: false },
      ),
    );

    expect(text(fixture, 'update-dev-build')).toBe('Update checks run only in the installed app.');
    expect(el(fixture, 'update-check-enabled')).toBeNull();
    expect(el(fixture, 'update-check-now')).toBeNull();
  });
});

describe('Settings: Floating button and quick ask', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  async function typeShortcut(
    fixture: ComponentFixture<SettingsComponent>,
    value: string,
  ): Promise<void> {
    const input = el(fixture, 'shortcut-input') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
    (el(fixture, 'shortcut-save') as HTMLButtonElement).click();
    await settle(fixture);
  }

  it('shows the shortcut that opens the quick-ask box', async () => {
    const fixture = await render(makeBridge());

    expect((el(fixture, 'shortcut-input') as HTMLInputElement).value).toBe(
      'CommandOrControl+Alt+Space',
    );
  });

  it('saves a new shortcut', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await typeShortcut(fixture, ' Alt+Shift+S ');

    expect(bridge.localSetShortcut).toHaveBeenCalledWith({ accelerator: 'Alt+Shift+S' });
    expect((el(fixture, 'shortcut-input') as HTMLInputElement).value).toBe('Alt+Shift+S');
    expect(el(fixture, 'shortcut-error')).toBeNull();
  });

  it('says "That shortcut is taken" when another app holds it', async () => {
    const bridge = makeBridge();
    bridge.localSetShortcut.mockRejectedValueOnce(
      new Error('[scout:quick-ask/shortcut-taken] That shortcut is taken'),
    );
    const fixture = await render(bridge);

    await typeShortcut(fixture, 'Alt+Tab');

    expect(text(fixture, 'shortcut-error')).toBe('That shortcut is taken');
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
    expect(el(fixture, 'bubble-toggle')?.getAttribute('role')).toBe('switch');
    expect(el(fixture, 'bubble-toggle')?.getAttribute('aria-checked')).toBe('true');
    expect(text(fixture, 'bubble-toggle')).toBe('Show the floating button');
    expect(
      ['bottom-right', 'bottom-left', 'top-right', 'top-left'].map((c) =>
        pressed(fixture, `bubble-corner-${c}`),
      ),
    ).toEqual(['true', 'false', 'false', 'false']);
    expect(text(fixture, 'bubble-display-cursor')).toBe('Display with the mouse cursor');
    expect(text(fixture, 'bubble-display-primary')).toBe('Primary display');
    expect(pressed(fixture, 'bubble-display-cursor')).toBe('true');
    expect(pressed(fixture, 'bubble-display-primary')).toBe('false');
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

    await click(fixture, 'bubble-toggle');

    expect(bridge.localSetBubble).toHaveBeenCalledWith({
      enabled: false,
      corner: 'bottom-right',
      display: 'cursor',
    });
    expect(el(fixture, 'bubble-toggle')?.getAttribute('aria-checked')).toBe('false');
  });

  it('display buttons save the display choice', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await click(fixture, 'bubble-display-primary');

    expect(bridge.localSetBubble).toHaveBeenCalledWith({
      enabled: true,
      corner: 'bottom-right',
      display: 'primary',
    });
    expect(pressed(fixture, 'bubble-display-primary')).toBe('true');
    expect(pressed(fixture, 'bubble-display-cursor')).toBe('false');
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

  it('shows the Wayland hint under the shortcut and keeps the shortcut input usable', async () => {
    const bridge = makeBridge();
    const hint =
      'Your desktop (Wayland) does not let apps register global shortcuts. Add a custom shortcut in your system settings that runs: /home/owner/Apps/Scout.AppImage --ask';
    bridge.localGetShortcut.mockResolvedValueOnce({
      accelerator: 'CommandOrControl+Alt+Space',
      registered: true,
      hint,
    });
    const fixture = await render(bridge);

    expect(text(fixture, 'shortcut-hint')).toBe(hint);
    expect((el(fixture, 'shortcut-input') as HTMLInputElement).disabled).toBe(false);
  });

  it('shows the hint main returns after saving a shortcut', async () => {
    const bridge = makeBridge();
    bridge.localSetShortcut.mockResolvedValueOnce({
      accelerator: 'Alt+Shift+S',
      registered: true,
      hint: 'Add a custom shortcut in your system settings that runs scout-desktop --ask.',
    });
    const fixture = await render(bridge);
    const input = el(fixture, 'shortcut-input') as HTMLInputElement;
    input.value = 'Alt+Shift+S';
    input.dispatchEvent(new Event('input'));
    await settle(fixture);

    (el(fixture, 'shortcut-save') as HTMLButtonElement).click();
    await settle(fixture);

    expect(text(fixture, 'shortcut-hint')).toBe(
      'Add a custom shortcut in your system settings that runs scout-desktop --ask.',
    );
  });

  it('shows no hint when the shortcut status carries none', async () => {
    const fixture = await render(makeBridge());

    expect(el(fixture, 'shortcut-hint')).toBeNull();
  });
});
