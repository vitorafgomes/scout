import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { SlackStatus, WorkerStatus } from '@scout/shared';

import { LogStore } from '../../core/log.store';
import { PersistentStateService } from '../../core/persistent-state';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

import { ChatClientsComponent } from './chat-clients.component';

const NOT_CONNECTED: SlackStatus = { connected: false, team: null, botUser: null };

function makeBridge(opts: { discord: boolean; slack: SlackStatus }) {
  const calls: string[] = [];
  let slack = opts.slack;
  return {
    calls,
    vaultHas: vi.fn(async () => ({ exists: opts.discord })),
    vaultDelete: vi.fn(async (req: { key: string }) => {
      calls.push(`vaultDelete:${req.key}`);
      return { deleted: true };
    }),
    workerStop: vi.fn(async () => {
      calls.push('stop');
      return { exitCode: 0 };
    }),
    workerStart: vi.fn(async () => {
      calls.push('start');
      return { pid: 4242 };
    }),
    slackStatus: vi.fn(async () => slack),
    slackDisconnect: vi.fn(async () => {
      calls.push('slackDisconnect');
      slack = NOT_CONNECTED;
      return slack;
    }),
    slackManifestUrl: vi.fn(async () => ({ url: 'https://api.slack.com/apps' })),
    appOpenExternal: vi.fn(async () => undefined),
  };
}

async function render(opts: {
  discord: boolean;
  slack: SlackStatus;
  worker?: WorkerStatus;
}): Promise<{
  fixture: ComponentFixture<ChatClientsComponent>;
  bridge: ReturnType<typeof makeBridge>;
  wizard: WizardStore;
}> {
  const bridge = makeBridge(opts);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: '', children: [] },
        { path: 'provision', children: [] },
        { path: 'settings', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      {
        provide: LogStore,
        useValue: {
          status: signal<WorkerStatus>(opts.worker ?? { state: 'running', pid: 4242, restarts: 0 }),
        },
      },
      {
        provide: PersistentStateService,
        useValue: {
          get: () =>
            opts.discord ? { clientId: 'c', guildId: 'g', guildName: 'Ops Guild' } : null,
          set: vi.fn(),
          clear: vi.fn(),
        },
      },
    ],
  });
  const wizard = TestBed.inject(WizardStore);
  await wizard.loadFromVault();
  const fixture = TestBed.createComponent(ChatClientsComponent);
  await settle(fixture);
  return { fixture, bridge, wizard };
}

async function settle(fixture: ComponentFixture<ChatClientsComponent>): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el(fixture: ComponentFixture<ChatClientsComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function text(fixture: ComponentFixture<ChatClientsComponent>, test: string): string | null {
  return el(fixture, test)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
}

async function click(fixture: ComponentFixture<ChatClientsComponent>, test: string): Promise<void> {
  el(fixture, test)!.click();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await settle(fixture);
}

describe('Settings chat clients: Discord card', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('when the owner declines the confirmation, Disconnect touches nothing', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false);
    const { fixture, bridge } = await render({ discord: true, slack: NOT_CONNECTED });

    await click(fixture, 'discord-disconnect');

    expect(bridge.calls).toEqual([]);
    expect(text(fixture, 'discord-state')).toBe('Connected');
  });

  it('Disconnect forgets only the Discord app and restarts the worker so Slack keeps answering', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const { fixture, bridge, wizard } = await render({
      discord: true,
      slack: { connected: true, team: 'Acme', botUser: 'scout' },
    });
    await TestBed.inject(Router).navigateByUrl('/settings');

    await click(fixture, 'discord-disconnect');

    expect(bridge.calls).toEqual([
      'stop',
      'vaultDelete:discord.bot-token',
      'vaultDelete:discord.client-id',
      'vaultDelete:discord.guild-id',
      'vaultDelete:discord.guild-name',
      'start',
    ]);
    expect(bridge.slackDisconnect).not.toHaveBeenCalled();
    expect(wizard.slackConnected()).toBe(true);
    expect(text(fixture, 'discord-state')).toBe('Not connected');
    expect(text(fixture, 'discord-connect')).toBe('Connect');
    expect(TestBed.inject(Router).url).toBe('/settings');
  });

  it('Disconnect with no other chat client left stops the worker and goes back to Welcome', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const { fixture, bridge } = await render({ discord: true, slack: NOT_CONNECTED });
    await TestBed.inject(Router).navigateByUrl('/settings');

    await click(fixture, 'discord-disconnect');

    expect(bridge.calls).toContain('stop');
    expect(bridge.workerStart).not.toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('when the worker cannot be stopped, Disconnect still forgets the Discord app', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const { fixture, bridge, wizard } = await render({
      discord: true,
      slack: { connected: true, team: 'Acme', botUser: 'scout' },
    });
    bridge.workerStop.mockRejectedValueOnce(new Error('[scout:worker/stop-failed] timed out'));

    await click(fixture, 'discord-disconnect');

    expect(bridge.calls).toContain('vaultDelete:discord.bot-token');
    expect(wizard.hasDiscord()).toBe(false);
    expect(text(fixture, 'discord-connect')).toBe('Connect');
  });

  it('Connect opens the existing Discord setup', async () => {
    const { fixture, wizard } = await render({ discord: false, slack: NOT_CONNECTED });

    await click(fixture, 'discord-connect');

    expect(TestBed.inject(Router).url).toBe('/provision');
    expect(wizard.currentStep()).toBe('provision');
  });
});

describe('Settings chat clients: Slack card', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('Connect expands the Slack setup in place', async () => {
    const { fixture } = await render({ discord: true, slack: NOT_CONNECTED });
    expect(el(fixture, 'slack-setup')).toBeNull();

    await click(fixture, 'slack-connect');

    expect(el(fixture, 'client-slack')).toBeNull();
    expect(text(fixture, 'slack-create')).toBe('Create the Slack app');
    expect(el(fixture, 'slack-bot-token')).not.toBeNull();
  });

  it('a connected Slack shows its workspace, and Disconnect drops Slack only', async () => {
    const { fixture, bridge, wizard } = await render({
      discord: true,
      slack: { connected: true, team: 'Acme', botUser: 'scout' },
    });
    expect(text(fixture, 'slack-connected')).toBe('Connected to Acme as scout');

    await click(fixture, 'slack-disconnect');

    expect(bridge.calls).toEqual(['slackDisconnect']);
    expect(wizard.slackConnected()).toBe(false);
    expect(wizard.hasDiscord()).toBe(true);
    expect(text(fixture, 'slack-state')).toBe('Not connected');
  });
});
