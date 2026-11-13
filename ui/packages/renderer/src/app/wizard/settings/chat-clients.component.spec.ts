import { InteractivityChecker } from '@angular/cdk/a11y';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { Router, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  RESTART_DEFERRED_LINE,
  type DiscordBotInfo,
  type DiscordEnableIntentResult,
  type SlackStatus,
  type WorkerStatus,
} from '@scout/shared';

import { ICONS } from '../../core/icon.component';
import { LogStore } from '../../core/log.store';
import { restartAsMain } from '../../core/main-restart.testing';
import { PersistentStateService } from '../../core/persistent-state';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

import { ChatClientsComponent } from './chat-clients.component';

const NOT_CONNECTED: SlackStatus = { connected: false, team: null, botUser: null };
const ACME: SlackStatus = { connected: true, team: 'Acme', botUser: 'scout' };
const BOT: DiscordBotInfo = {
  applicationId: 'app-1',
  applicationName: 'Scout',
  botUsername: 'scout',
  messageContent: 'on',
};

function makeBridge(opts: {
  discord: boolean;
  slack: SlackStatus;
  worker?: WorkerStatus;
  listening?: boolean;
  server?: boolean;
  vaultClientId?: string | Error;
}) {
  const calls: string[] = [];
  let slack = opts.slack;
  const bridge = {
    calls,
    vaultHas: vi.fn(async ({ key }: { key: string }) => ({
      exists: key === 'discord.guild-id' ? (opts.server ?? opts.discord) : opts.discord,
    })),
    vaultGetPublic: vi.fn(async ({ key }: { key: string }) => {
      if (key !== 'discord.client-id') return { value: null };
      if (opts.vaultClientId instanceof Error) throw opts.vaultClientId;
      return { value: opts.vaultClientId ?? null };
    }),
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
    workerStatus: vi.fn(
      async (): Promise<WorkerStatus> =>
        opts.worker ?? { state: 'running', pid: 4242, restarts: 0 },
    ),
    localListening: vi.fn(async () => ({ listening: opts.listening === true })),
    slackStatus: vi.fn(async () => slack),
    slackDisconnect: vi.fn(async () => {
      calls.push('slackDisconnect');
      slack = NOT_CONNECTED;
      return slack;
    }),
    slackSave: vi.fn(async (req: { botToken: string; appToken: string }) => {
      calls.push(`slackSave:${req.botToken}:${req.appToken}`);
      slack = ACME;
      return slack;
    }),
    slackManifestUrl: vi.fn(async () => ({ url: 'https://api.slack.com/apps?new_app=1' })),
    appOpenExternal: vi.fn(async () => undefined),
    discordCheckToken: vi.fn(async (req: { token: string }) => {
      calls.push(`discordCheckToken:${req.token}`);
      return BOT;
    }),
    discordEnableIntent: vi.fn(async (): Promise<DiscordEnableIntentResult> => ({ enabled: true })),
  };
  return Object.assign(bridge, { workerRestart: vi.fn(() => restartAsMain(bridge)) });
}

const LAID_OUT: Pick<
  InteractivityChecker,
  'isDisabled' | 'isVisible' | 'isTabbable' | 'isFocusable'
> = {
  isDisabled: (el: HTMLElement) => el.hasAttribute('disabled'),
  isVisible: () => true,
  isTabbable: (el: HTMLElement) => focusable(el),
  isFocusable: (el: HTMLElement) => focusable(el),
};

function focusable(el: HTMLElement): boolean {
  if (el.hasAttribute('disabled')) return false;
  return ['button', 'input', 'select', 'textarea'].includes(el.tagName.toLowerCase());
}

async function render(opts: {
  discord: boolean;
  slack: SlackStatus;
  worker?: WorkerStatus;
  clientId?: string;
  listening?: boolean;
  server?: boolean;
  vaultClientId?: string | Error;
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
        { path: 'oauth', children: [] },
        { path: 'settings', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
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
            opts.discord
              ? { clientId: opts.clientId ?? 'app-1', guildId: 'g', guildName: 'Ops Guild' }
              : null,
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
  for (let round = 0; round < 50; round++) {
    const before = document.body.innerHTML;
    fixture.detectChanges();
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
    if (document.body.innerHTML === before) return;
  }
  throw new Error('settle: the page was still changing after 50 rounds');
}

function el(fixture: ComponentFixture<ChatClientsComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function overlay(test: string): HTMLElement | null {
  return document.querySelector(`.cdk-overlay-container [data-test="${test}"]`);
}

function text(fixture: ComponentFixture<ChatClientsComponent>, test: string): string | null {
  return el(fixture, test)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
}

async function click(fixture: ComponentFixture<ChatClientsComponent>, test: string): Promise<void> {
  (el(fixture, test) ?? overlay(test))!.click();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await settle(fixture);
  await new Promise((resolve) => setTimeout(resolve, 0));
  await settle(fixture);
}

async function type(
  fixture: ComponentFixture<ChatClientsComponent>,
  test: string,
  value: string,
): Promise<void> {
  const input = overlay(test) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

function dialogOpen(): boolean {
  return document.querySelector('.cdk-overlay-container mat-dialog-container') !== null;
}

function focused(): string | null {
  return (document.activeElement as HTMLElement | null)?.getAttribute('data-test') ?? null;
}

type Platform = 'discord' | 'slack' | 'teams';
type Connection = 'connected' | 'not connected';

const CONNECTIONS: Readonly<Record<Platform, readonly Connection[]>> = {
  discord: ['connected', 'not connected'],
  slack: ['connected', 'not connected'],
  teams: ['not connected'],
};

function start(platform: Platform, connection: Connection) {
  const on = connection === 'connected';
  return render({
    discord: platform === 'discord' ? on : false,
    slack: platform === 'slack' && on ? ACME : NOT_CONNECTED,
  });
}

beforeEach(() => {
  TestBed.resetTestingModule();
  localStorage.clear();
});

afterEach(() => {
  TestBed.inject(MatDialog).closeAll();
  vi.restoreAllMocks();
});

describe('Settings chat connections: the cards', () => {
  const CARD: Readonly<
    Record<
      Platform,
      Readonly<Partial<Record<Connection, { state: string; detail: string; buttons: string[] }>>>
    >
  > = {
    discord: {
      connected: {
        state: 'Connected',
        detail: 'Server: Ops Guild',
        buttons: ['Manage', 'Disconnect'],
      },
      'not connected': {
        state: 'Not connected',
        detail: 'Answer in your Discord server.',
        buttons: ['Connect'],
      },
    },
    slack: {
      connected: {
        state: 'Connected',
        detail: 'Workspace: Acme',
        buttons: ['Manage', 'Disconnect'],
      },
      'not connected': {
        state: 'Not connected',
        detail: 'Answer in your Slack workspace.',
        buttons: ['Connect'],
      },
    },
    teams: {
      'not connected': {
        state: 'Not connected',
        detail: 'Answer in Microsoft Teams.',
        buttons: ['Connect'],
      },
    },
  };

  it('the section is called Chat connections and holds Discord, Slack and Microsoft Teams', async () => {
    const { fixture } = await start('discord', 'connected');

    expect(text(fixture, 'chat-clients-title')).toBe('Chat connections');
    expect(
      Array.from(fixture.nativeElement.querySelectorAll('h3') as NodeListOf<HTMLElement>).map((h) =>
        h.textContent?.trim(),
      ),
    ).toEqual(['Discord', 'Slack', 'Microsoft Teams']);
  });

  for (const platform of ['discord', 'slack', 'teams'] as const) {
    for (const connection of CONNECTIONS[platform]) {
      const expected = CARD[platform][connection]!;
      it(`${platform} ${connection}: the card says ${expected.state}, ${expected.detail}, and offers ${expected.buttons.join(' and ')}, each with its icon`, async () => {
        const { fixture } = await start(platform, connection);
        const card = el(fixture, `client-${platform}`)!;
        const buttons = Array.from(
          card.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
        );

        expect({
          state: text(fixture, `${platform}-state`),
          detail: text(fixture, `${platform}-detail`),
          buttons: buttons.map((button) => button.textContent?.trim()),
        }).toEqual(expected);
        expect(buttons.every((button) => button.querySelector('sc-icon svg') !== null)).toBe(true);
        expect(buttons.every((button) => !button.disabled)).toBe(true);
      });
    }
  }
});

describe('Settings chat connections: a Discord bot token kept without a server', () => {
  it('shows Discord as needing a server, and Add to server opens the step that adds the bot with the application id main kept', async () => {
    const { fixture, wizard } = await render({
      discord: true,
      server: false,
      slack: ACME,
      vaultClientId: 'app-9',
    });
    const card = {
      state: text(fixture, 'discord-state'),
      detail: text(fixture, 'discord-detail'),
      buttons: Array.from(
        fixture.nativeElement.querySelectorAll('[data-test="client-discord"] button'),
        (button) => (button as HTMLElement).textContent?.replace(/\s+/g, ' ').trim(),
      ),
    };

    expect(card).toEqual({
      state: 'Needs a server',
      detail: 'Scout is in no server yet. Add it to the server it answers in.',
      buttons: ['Add to server', 'Disconnect'],
    });

    await click(fixture, 'discord-add-server');

    expect({
      url: TestBed.inject(Router).url,
      clientId: wizard.clientId(),
      step: wizard.currentStep(),
    }).toEqual({ url: '/oauth', clientId: 'app-9', step: 'oauth' });
  });

  it('Add to server says why when the application id cannot be read, and stays on Settings', async () => {
    const { fixture } = await render({
      discord: true,
      server: false,
      slack: ACME,
      vaultClientId: new Error('[scout:vault/unavailable] The credential store is unavailable.'),
    });
    await TestBed.inject(Router).navigateByUrl('/settings');

    await click(fixture, 'discord-add-server');

    expect({ error: text(fixture, 'chat-clients-error'), url: TestBed.inject(Router).url }).toEqual(
      {
        error:
          'Scout could not read which Discord app to add: The credential store is unavailable.',
        url: '/settings',
      },
    );
  });
});

describe('Settings chat connections: each card opens a modal asking for the credentials', () => {
  const OPEN: Readonly<
    Record<
      Platform,
      Readonly<
        Partial<Record<Connection, { title: string; fields: string[]; save: string | null }>>
      >
    >
  > = {
    discord: {
      connected: { title: 'Manage Discord', fields: ['Discord bot token'], save: 'Save token' },
      'not connected': { title: 'Connect Discord', fields: ['Discord bot token'], save: 'Connect' },
    },
    slack: {
      connected: {
        title: 'Manage Slack',
        fields: ['Bot User OAuth Token (xoxb-)', 'App-level token (xapp-)'],
        save: 'Save tokens',
      },
      'not connected': {
        title: 'Connect Slack',
        fields: ['Bot User OAuth Token (xoxb-)', 'App-level token (xapp-)'],
        save: 'Connect',
      },
    },
    teams: {
      'not connected': { title: 'Connect Microsoft Teams', fields: [], save: null },
    },
  };

  function opener(platform: Platform, connection: Connection): string {
    return platform !== 'teams' && connection === 'connected'
      ? `${platform}-manage`
      : `${platform}-connect`;
  }

  for (const platform of ['discord', 'slack', 'teams'] as const) {
    for (const connection of CONNECTIONS[platform]) {
      const expected = OPEN[platform][connection]!;

      it(`${platform} ${connection}, open: "${expected.title}" asks for ${expected.fields.length === 0 ? 'nothing' : expected.fields.join(' and ')} in password fields, with Cancel focused`, async () => {
        const { fixture } = await start(platform, connection);

        await click(fixture, opener(platform, connection));
        const inputs = Array.from(
          document.querySelectorAll('.cdk-overlay-container input') as NodeListOf<HTMLInputElement>,
        );

        expect({
          title: document.querySelector('.mat-mdc-dialog-title')?.textContent?.trim(),
          fields: inputs.map((input) => input.labels?.[0]?.textContent?.trim()),
          save: overlay('dialog-save')?.textContent?.trim() ?? null,
        }).toEqual(expected);
        expect(
          inputs.every((input) => input.type === 'password' && input.autocomplete === 'off'),
        ).toBe(true);
        expect(focused()).toBe(platform === 'teams' ? 'dialog-close' : 'dialog-cancel');
      });

      it(`${platform} ${connection}, cancel: the modal closes and nothing is sent`, async () => {
        const { fixture, bridge } = await start(platform, connection);

        await click(fixture, opener(platform, connection));
        if (platform !== 'teams')
          await type(
            fixture,
            `dialog-${platform === 'discord' ? 'discord-token' : 'slack-bot-token'}`,
            'typed-secret',
          );
        await click(fixture, platform === 'teams' ? 'dialog-close' : 'dialog-cancel');

        expect(dialogOpen()).toBe(false);
        expect(bridge.calls).toEqual([]);
        expect(bridge.discordCheckToken).not.toHaveBeenCalled();
        expect(bridge.slackSave).not.toHaveBeenCalled();
        expect(text(fixture, `${platform}-state`)).toBe(
          platform !== 'teams' && connection === 'connected' ? 'Connected' : 'Not connected',
        );
      });
    }
  }

  it('teams, open: the modal says Teams cannot be connected yet, and keeps nothing', async () => {
    const { fixture } = await start('teams', 'not connected');

    await click(fixture, 'teams-connect');

    expect(overlay('dialog-note')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Teams cannot be connected yet. Scout has no place on this computer to keep a Teams bot registration, and Teams sends its messages to a public HTTPS endpoint that Scout does not open.',
    );
    expect(overlay('dialog-save')).toBeNull();
  });

  it('discord connected, save: the new token goes to main once, the field is emptied, the modal closes and the app stays on Settings', async () => {
    const { fixture, bridge } = await start('discord', 'connected');
    await TestBed.inject(Router).navigateByUrl('/settings');

    await click(fixture, 'discord-manage');
    await type(fixture, 'dialog-discord-token', '  new-bot-token  ');
    await click(fixture, 'dialog-save');

    expect(bridge.calls).toEqual(['discordCheckToken:new-bot-token']);
    expect(dialogOpen()).toBe(false);
    expect(TestBed.inject(Router).url).toBe('/settings');
    expect(text(fixture, 'discord-state')).toBe('Connected');
  });

  it('discord connected, save of a token from another Discord app: the modal closes on the step that adds the bot to a server', async () => {
    const { fixture, bridge, wizard } = await start('discord', 'connected');
    bridge.discordCheckToken.mockResolvedValueOnce({ ...BOT, applicationId: 'app-2' });

    await click(fixture, 'discord-manage');
    await type(fixture, 'dialog-discord-token', 'other-app-token');
    await click(fixture, 'dialog-save');

    expect(dialogOpen()).toBe(false);
    expect(wizard.clientId()).toBe('app-2');
    expect(TestBed.inject(Router).url).toBe('/oauth');
  });

  for (const [worker, calls] of [
    ['running', ['discordCheckToken:bot-token']],
    ['idle', ['discordCheckToken:bot-token']],
  ] as const) {
    it(`discord not connected, save with the worker ${worker}: the token goes to main, which restarts a running worker itself, the dialog asks for no second restart, and the wizard step that adds the bot to a server opens`, async () => {
      const { fixture, bridge, wizard } = await render({
        discord: false,
        slack: ACME,
        worker: { state: worker, restarts: 0 },
      });

      await click(fixture, 'discord-connect');
      await type(fixture, 'dialog-discord-token', 'bot-token');
      await click(fixture, 'dialog-save');

      expect(bridge.calls).toEqual(calls);
      expect(dialogOpen()).toBe(false);
      expect(wizard.clientId()).toBe('app-1');
      expect(wizard.currentStep()).toBe('oauth');
      expect(TestBed.inject(Router).url).toBe('/oauth');
    });
  }

  it('discord, save while Message Content Intent cannot be turned on: the modal stays open and says why, and Check again asks once more', async () => {
    const { fixture, bridge } = await start('discord', 'not connected');
    bridge.discordCheckToken.mockResolvedValueOnce({ ...BOT, messageContent: 'off' });
    bridge.discordEnableIntent
      .mockResolvedValueOnce({
        enabled: false,
        message: 'Turn on Message Content Intent in the Developer Portal.',
        portalUrl: 'https://discord.com/developers/applications/app-1/bot',
      })
      .mockResolvedValueOnce({ enabled: true });

    await click(fixture, 'discord-connect');
    await type(fixture, 'dialog-discord-token', 'bot-token');
    await click(fixture, 'dialog-save');
    const stuck = {
      open: dialogOpen(),
      message: overlay('dialog-error')?.textContent?.trim(),
      field: (overlay('dialog-discord-token') as HTMLInputElement | null)?.value ?? null,
    };
    expect(stuck).toEqual({
      open: true,
      message: 'Turn on Message Content Intent in the Developer Portal.',
      field: '',
    });

    await click(fixture, 'dialog-check-again');

    expect(bridge.discordEnableIntent).toHaveBeenCalledTimes(2);
    expect(dialogOpen()).toBe(false);
    expect(TestBed.inject(Router).url).toBe('/oauth');
  });

  it('discord, a token main refuses: the modal stays open with the reason and nothing else changes', async () => {
    const { fixture, bridge } = await start('discord', 'not connected');
    bridge.discordCheckToken.mockRejectedValueOnce(
      new Error('[scout:discord/token-rejected] Discord rejected this token.'),
    );

    await click(fixture, 'discord-connect');
    await type(fixture, 'dialog-discord-token', 'bad');
    await click(fixture, 'dialog-save');

    expect(dialogOpen()).toBe(true);
    expect(overlay('dialog-error')?.textContent?.trim()).toBe('Discord rejected this token.');
    expect(text(fixture, 'discord-state')).toBe('Not connected');
  });

  for (const connection of ['connected', 'not connected'] as const) {
    it(`slack ${connection}, save: both tokens go to main through the Slack setup call, the card shows the workspace, and a stopped worker starts`, async () => {
      const bridge = await render({
        discord: false,
        slack: connection === 'connected' ? ACME : NOT_CONNECTED,
        worker: { state: 'idle', restarts: 0 },
      });
      const { fixture, wizard } = bridge;

      await click(fixture, connection === 'connected' ? 'slack-manage' : 'slack-connect');
      await type(fixture, 'dialog-slack-bot-token', 'xoxb-1');
      await type(fixture, 'dialog-slack-app-token', 'xapp-1');
      await click(fixture, 'dialog-save');

      expect(bridge.bridge.calls).toEqual(['slackSave:xoxb-1:xapp-1', 'start']);
      expect(dialogOpen()).toBe(false);
      expect(wizard.slackConnected()).toBe(true);
      expect(text(fixture, 'slack-state')).toBe('Connected');
      expect(text(fixture, 'slack-detail')).toBe('Workspace: Acme');
    });
  }

  type Field = 'dialog-discord-token' | 'dialog-slack-bot-token' | 'dialog-slack-app-token';
  const INVALID: readonly {
    platform: 'discord' | 'slack';
    typed: Partial<Record<Field, string>>;
    marked: Partial<Record<Field, string>>;
  }[] = [
    { platform: 'discord', typed: {}, marked: { 'dialog-discord-token': 'Enter the bot token.' } },
    {
      platform: 'discord',
      typed: { 'dialog-discord-token': '   ' },
      marked: { 'dialog-discord-token': 'Enter the bot token.' },
    },
    {
      platform: 'slack',
      typed: {},
      marked: {
        'dialog-slack-bot-token': 'This is not a bot token: it starts with xoxb-.',
        'dialog-slack-app-token': 'This is not an app-level token: it starts with xapp-.',
      },
    },
    {
      platform: 'slack',
      typed: { 'dialog-slack-bot-token': 'xapp-1', 'dialog-slack-app-token': 'xapp-1' },
      marked: { 'dialog-slack-bot-token': 'This is not a bot token: it starts with xoxb-.' },
    },
    {
      platform: 'slack',
      typed: { 'dialog-slack-bot-token': 'xoxb-1', 'dialog-slack-app-token': 'xoxb-1' },
      marked: { 'dialog-slack-app-token': 'This is not an app-level token: it starts with xapp-.' },
    },
  ];

  for (const { platform, typed, marked } of INVALID) {
    it(`${platform}, save with ${JSON.stringify(typed)}: ${Object.keys(marked).join(' and ')} marked with the reason, "Check the highlighted fields. Nothing was saved.", and nothing sent`, async () => {
      const { fixture, bridge } = await start(platform, 'not connected');

      await click(fixture, `${platform}-connect`);
      for (const [field, value] of Object.entries(typed)) await type(fixture, field, value);
      await click(fixture, 'dialog-save');
      const fields = Array.from(
        document.querySelectorAll('.cdk-overlay-container input') as NodeListOf<HTMLInputElement>,
      );

      expect(
        Object.fromEntries(
          fields
            .filter((input) => input.getAttribute('aria-invalid') === 'true')
            .map((input) => [
              input.dataset['test'],
              overlay(
                `dialog-help-${input.dataset['test']!.replace('dialog-', '')}`,
              )?.textContent?.trim(),
            ]),
        ),
      ).toEqual(marked);
      expect(overlay('dialog-invalid')?.textContent?.trim()).toBe(
        'Check the highlighted fields. Nothing was saved.',
      );
      expect(bridge.calls).toEqual([]);
      expect(dialogOpen()).toBe(true);
    });
  }

  it('slack: each field has its placeholder and its own Show toggle, and Create the Slack app opens the manifest link', async () => {
    const { fixture, bridge } = await start('slack', 'not connected');

    await click(fixture, 'slack-connect');
    const fields = ['slack-bot-token', 'slack-app-token'].map((key) => ({
      placeholder: (overlay(`dialog-${key}`) as HTMLInputElement).placeholder,
      toggle: overlay(`dialog-reveal-${key}`)?.getAttribute('aria-label'),
    }));
    await click(fixture, 'dialog-slack-create');

    expect(fields).toEqual([
      { placeholder: 'xoxb-...', toggle: 'Show Bot User OAuth Token (xoxb-)' },
      { placeholder: 'xapp-...', toggle: 'Show App-level token (xapp-)' },
    ]);
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({
      url: 'https://api.slack.com/apps?new_app=1',
    });
    expect(
      Array.from(
        document.querySelectorAll('.cdk-overlay-container [data-test="dialog-steps"] li'),
      ).map((li) => li.textContent?.replace(/\s+/g, ' ').trim()),
    ).toEqual([
      'Pick the workspace and click Create.',
      'Click Install to Workspace, then copy the Bot User OAuth Token.',
      'Open Basic Information, App-Level Tokens, Generate Token and Scopes, add the scope connections:write, Generate, then copy the token.',
      'In Slack, invite @scout to each channel where it should answer.',
    ]);
  });

  it('slack, a save main refuses: the modal stays open with the reason, the tokens stay typed, and the card stays Not connected', async () => {
    const { fixture, bridge, wizard } = await start('slack', 'not connected');
    bridge.slackSave.mockRejectedValueOnce(
      new Error('[scout:slack/auth-failed] Slack rejected the bot token.'),
    );

    await click(fixture, 'slack-connect');
    await type(fixture, 'dialog-slack-bot-token', 'xoxb-1');
    await type(fixture, 'dialog-slack-app-token', 'xapp-1');
    await click(fixture, 'dialog-save');

    expect(dialogOpen()).toBe(true);
    expect(overlay('dialog-error')?.textContent?.trim()).toBe('Slack rejected the bot token.');
    expect((overlay('dialog-slack-bot-token') as HTMLInputElement).value).toBe('xoxb-1');
    expect(wizard.slackConnected()).toBe(false);
    expect(bridge.workerStart).not.toHaveBeenCalled();
    expect(text(fixture, 'slack-state')).toBe('Not connected');
  });

  it('discord, an intent check that fails outright: the modal stays open with the reason', async () => {
    const { fixture, bridge } = await start('discord', 'not connected');
    bridge.discordCheckToken.mockResolvedValueOnce({ ...BOT, messageContent: 'off' });
    bridge.discordEnableIntent.mockRejectedValueOnce(
      new Error('[scout:discord/unreachable] Discord could not be reached.'),
    );

    await click(fixture, 'discord-connect');
    await type(fixture, 'dialog-discord-token', 'bot-token');
    await click(fixture, 'dialog-save');

    expect(dialogOpen()).toBe(true);
    expect(overlay('dialog-error')?.textContent?.trim()).toBe('Discord could not be reached.');
    expect(TestBed.inject(Router).url).not.toBe('/oauth');
  });

  it('a secret typed in a modal is never shown: Show reveals it only on demand', async () => {
    const { fixture } = await start('discord', 'not connected');

    await click(fixture, 'discord-connect');
    await type(fixture, 'dialog-discord-token', 'secret');
    const before = (overlay('dialog-discord-token') as HTMLInputElement).type;
    await click(fixture, 'dialog-reveal-discord-token');

    expect(before).toBe('password');
    expect((overlay('dialog-discord-token') as HTMLInputElement).type).toBe('text');
    expect(overlay('dialog-reveal-discord-token')?.getAttribute('aria-pressed')).toBe('true');
  });
});

describe('Settings chat connections: Disconnect asks first', () => {
  async function disconnect(
    platform: 'discord' | 'slack',
    answer: 'confirm-cancel' | 'confirm-ok',
    slack: SlackStatus = ACME,
  ) {
    const rendered = await render({ discord: platform === 'discord', slack });
    await TestBed.inject(Router).navigateByUrl('/settings');
    await click(rendered.fixture, `${platform}-disconnect`);
    const asked = {
      title: document.querySelector('sc-confirm-dialog h2')?.textContent?.trim(),
      focused: focused(),
      glyph: document.querySelector('[data-test="confirm-ok"] svg path')?.getAttribute('d'),
    };
    await click(rendered.fixture, answer);
    return { ...rendered, asked };
  }

  for (const [platform, title] of [
    ['discord', 'Disconnect Discord?'],
    ['slack', 'Disconnect Slack?'],
  ] as const) {
    it(`${platform}, Disconnect then Cancel: the confirmation "${title}" opens with Cancel focused and Disconnect carrying the unplugged glyph the artboard draws, and nothing is disconnected`, async () => {
      const { fixture, bridge, asked } = await disconnect(platform, 'confirm-cancel');

      expect(asked).toEqual({ title, focused: 'confirm-cancel', glyph: ICONS['plug-off'] });
      expect(bridge.calls).toEqual([]);
      expect(text(fixture, `${platform}-state`)).toBe('Connected');
    });
  }

  it('discord, Disconnect then confirm: forgets only the Discord app and restarts the worker so Slack keeps answering', async () => {
    const { fixture, bridge, wizard } = await disconnect('discord', 'confirm-ok');

    expect(bridge.calls).toEqual([
      'vaultDelete:discord.bot-token',
      'vaultDelete:discord.client-id',
      'vaultDelete:discord.guild-id',
      'vaultDelete:discord.guild-name',
      'stop',
      'start',
    ]);
    expect(bridge.slackDisconnect).not.toHaveBeenCalled();
    expect(wizard.slackConnected()).toBe(true);
    expect(text(fixture, 'discord-state')).toBe('Not connected');
    expect(text(fixture, 'discord-connect')).toBe('Connect');
    expect(TestBed.inject(Router).url).toBe('/settings');
  });

  it('discord, Disconnect then confirm with no other chat client left: stops the worker and goes back to Welcome', async () => {
    const { bridge } = await disconnect('discord', 'confirm-ok', NOT_CONNECTED);

    expect(bridge.calls).toContain('stop');
    expect(bridge.workerStart).not.toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('discord, Disconnect then confirm while a live listen runs: forgets the Discord app, leaves Scout running and says it restarts once the listen ends', async () => {
    const rendered = await render({ discord: true, slack: ACME, listening: true });

    await click(rendered.fixture, 'discord-disconnect');
    await click(rendered.fixture, 'confirm-ok');

    expect({
      calls: rendered.bridge.calls,
      note: text(rendered.fixture, 'chat-clients-restart'),
    }).toEqual({
      calls: [
        'vaultDelete:discord.bot-token',
        'vaultDelete:discord.client-id',
        'vaultDelete:discord.guild-id',
        'vaultDelete:discord.guild-name',
      ],
      note: RESTART_DEFERRED_LINE,
    });
  });

  it('discord, when the worker cannot be stopped, a confirmed Disconnect still forgets the Discord app', async () => {
    const rendered = await render({ discord: true, slack: ACME });
    rendered.bridge.workerStop.mockRejectedValueOnce(
      new Error('[scout:worker/stop-failed] timed out'),
    );

    await click(rendered.fixture, 'discord-disconnect');
    await click(rendered.fixture, 'confirm-ok');

    expect(rendered.bridge.calls).toContain('vaultDelete:discord.bot-token');
    expect(rendered.wizard.hasDiscord()).toBe(false);
    expect(text(rendered.fixture, 'discord-connect')).toBe('Connect');
  });

  it('slack, a disconnect main refuses: the card says why and Slack stays Connected', async () => {
    const rendered = await render({ discord: false, slack: ACME });
    rendered.bridge.slackDisconnect.mockRejectedValueOnce(
      new Error('[scout:slack/disconnect-failed] Scout could not remove the Slack tokens.'),
    );

    await click(rendered.fixture, 'slack-disconnect');
    await click(rendered.fixture, 'confirm-ok');

    expect(text(rendered.fixture, 'chat-clients-error')).toBe(
      'Scout could not remove the Slack tokens.',
    );
    expect(rendered.wizard.slackConnected()).toBe(true);
    expect(text(rendered.fixture, 'slack-state')).toBe('Connected');
  });

  it('slack, Disconnect then confirm while Discord stays connected: drops Slack only', async () => {
    const rendered = await render({ discord: true, slack: ACME });
    await TestBed.inject(Router).navigateByUrl('/settings');
    await click(rendered.fixture, 'slack-disconnect');
    await click(rendered.fixture, 'confirm-ok');
    const { fixture, bridge, wizard } = rendered;

    expect(bridge.calls).toEqual(['slackDisconnect']);
    expect(TestBed.inject(Router).url).toBe('/settings');
    expect(wizard.slackConnected()).toBe(false);
    expect(text(fixture, 'slack-state')).toBe('Not connected');
    expect(text(fixture, 'slack-connect')).toBe('Connect');
  });
});

describe('Settings chat connections: what the owner sees matches what main kept', () => {
  async function confirmDisconnect(
    rendered: Awaited<ReturnType<typeof render>>,
    platform: 'discord' | 'slack',
  ): Promise<void> {
    await TestBed.inject(Router).navigateByUrl('/settings');
    await click(rendered.fixture, `${platform}-disconnect`);
    await click(rendered.fixture, 'confirm-ok');
  }

  it('a Disconnect Discord whose bot token cannot be deleted says so and keeps the card Connected', async () => {
    const rendered = await render({ discord: true, slack: ACME });
    rendered.bridge.vaultDelete.mockImplementation(async (req: { key: string }) => {
      rendered.bridge.calls.push(`vaultDelete:${req.key}`);
      if (req.key === 'discord.bot-token') {
        throw new Error('[scout:vault/unavailable] The credential store is unavailable.');
      }
      return { deleted: true };
    });

    await confirmDisconnect(rendered, 'discord');

    expect({
      card: text(rendered.fixture, 'discord-state'),
      error: el(rendered.fixture, 'chat-clients-error') !== null,
    }).toEqual({ card: 'Connected', error: true });
  });

  it('Cancel at the Message Content Intent step of a first Discord token leaves Discord Not connected and restarts and opens nothing, since main stores the token only once the intent is on', async () => {
    const { fixture, bridge } = await render({
      discord: false,
      slack: ACME,
      worker: { state: 'running', pid: 4242, restarts: 0 },
    });
    await TestBed.inject(Router).navigateByUrl('/settings');
    bridge.discordCheckToken.mockResolvedValueOnce({ ...BOT, messageContent: 'off' });
    bridge.discordEnableIntent.mockResolvedValueOnce({
      enabled: false,
      message: 'Turn on Message Content Intent in the Developer Portal.',
      portalUrl: 'https://discord.com/developers/applications/app-1/bot',
    });

    await click(fixture, 'discord-connect');
    await type(fixture, 'dialog-discord-token', 'bot-token');
    await click(fixture, 'dialog-save');
    await click(fixture, 'dialog-cancel');

    expect({
      open: dialogOpen(),
      card: text(fixture, 'discord-state'),
      calls: bridge.calls,
      url: TestBed.inject(Router).url,
    }).toEqual({
      open: false,
      card: 'Not connected',
      calls: [],
      url: '/settings',
    });
  });

  it('Cancel while a first Discord token is being checked either waits for the save or stops it: the dialog restarts no worker either way, and a cancelled save opens no step that adds the bot', async () => {
    const { fixture, bridge } = await render({
      discord: false,
      slack: ACME,
      worker: { state: 'running', pid: 4242, restarts: 0 },
    });
    await TestBed.inject(Router).navigateByUrl('/settings');
    let answer: (bot: DiscordBotInfo) => void = () => undefined;
    bridge.discordCheckToken.mockImplementationOnce(
      () => new Promise<DiscordBotInfo>((resolve) => (answer = resolve)),
    );

    await click(fixture, 'discord-connect');
    await type(fixture, 'dialog-discord-token', 'bot-token');
    await click(fixture, 'dialog-save');
    await click(fixture, 'dialog-cancel');
    const closedByCancel = !dialogOpen();
    answer(BOT);
    await settle(fixture);
    await new Promise((resolve) => setTimeout(resolve, 0));
    await settle(fixture);

    expect([
      { closedByCancel: false, oauth: true, restarted: false },
      { closedByCancel: true, oauth: false, restarted: false },
    ]).toContainEqual({
      closedByCancel,
      oauth: TestBed.inject(Router).url === '/oauth',
      restarted: bridge.calls.includes('stop') || bridge.calls.includes('start'),
    });
  });

  it('Escape while a Discord token is being checked keeps the modal open, and Escape once it is idle closes it', async () => {
    const { fixture, bridge } = await start('discord', 'not connected');
    let answer: (bot: DiscordBotInfo) => void = () => undefined;
    bridge.discordCheckToken.mockImplementationOnce(
      () => new Promise<DiscordBotInfo>((resolve) => (answer = resolve)),
    );
    bridge.discordEnableIntent.mockResolvedValueOnce({
      enabled: false,
      message: 'Turn on Message Content Intent in the Developer Portal.',
      portalUrl: 'https://discord.com/developers/applications/app-1/bot',
    });
    const escape = async (): Promise<void> => {
      document.body.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
      );
      await settle(fixture);
    };

    await click(fixture, 'discord-connect');
    await type(fixture, 'dialog-discord-token', 'bot-token');
    await click(fixture, 'dialog-save');
    await escape();
    const whileBusy = dialogOpen();
    answer({ ...BOT, messageContent: 'off' });
    await settle(fixture);
    await new Promise((resolve) => setTimeout(resolve, 0));
    await settle(fixture);
    await escape();

    expect({ whileBusy, idle: dialogOpen() }).toEqual({ whileBusy: true, idle: false });
  });

  it('a Disconnect Discord whose worker restart fails says why under the cards', async () => {
    const rendered = await render({ discord: true, slack: ACME });
    rendered.bridge.workerStart.mockRejectedValueOnce(
      new Error(
        '[scout:worker/binary-missing] Worker binary not found: /opt/scout/Scout.Host.Console.',
      ),
    );

    await confirmDisconnect(rendered, 'discord');

    expect(text(rendered.fixture, 'chat-clients-restart') ?? '').toContain(
      'Worker binary not found: /opt/scout/Scout.Host.Console.',
    );
  });

  it('a Slack save starts a worker that gave up restarting', async () => {
    const { fixture, bridge } = await render({
      discord: false,
      slack: NOT_CONNECTED,
      worker: { state: 'crashed', restarts: 5, gaveUp: true },
    });

    await click(fixture, 'slack-connect');
    await type(fixture, 'dialog-slack-bot-token', 'xoxb-1');
    await type(fixture, 'dialog-slack-app-token', 'xapp-1');
    await click(fixture, 'dialog-save');

    expect(bridge.calls).toEqual(['slackSave:xoxb-1:xapp-1', 'start']);
  });

  it('a Slack save that main stored shows the workspace on the card even when the worker then fails to start', async () => {
    const { fixture, bridge } = await render({
      discord: false,
      slack: NOT_CONNECTED,
      worker: { state: 'idle', restarts: 0 },
    });
    bridge.workerStart.mockRejectedValueOnce(
      new Error(
        '[scout:worker/binary-missing] Worker binary not found: /opt/scout/Scout.Host.Console.',
      ),
    );

    await click(fixture, 'slack-connect');
    await type(fixture, 'dialog-slack-bot-token', 'xoxb-1');
    await type(fixture, 'dialog-slack-app-token', 'xapp-1');
    await click(fixture, 'dialog-save');

    expect(text(fixture, 'slack-detail')).toBe('Workspace: Acme');
  });

  it('a Slack save whose worker then fails to start says "Slack saved. Scout did not start: <reason>." under the cards', async () => {
    const { fixture, bridge } = await render({
      discord: false,
      slack: NOT_CONNECTED,
      worker: { state: 'idle', restarts: 0 },
    });
    bridge.workerStart.mockRejectedValueOnce(
      new Error(
        '[scout:worker/binary-missing] Worker binary not found: /opt/scout/Scout.Host.Console.',
      ),
    );

    await click(fixture, 'slack-connect');
    await type(fixture, 'dialog-slack-bot-token', 'xoxb-1');
    await type(fixture, 'dialog-slack-app-token', 'xapp-1');
    await click(fixture, 'dialog-save');

    expect({ open: dialogOpen(), notice: text(fixture, 'chat-clients-error') }).toEqual({
      open: false,
      notice:
        'Slack saved. Scout did not start: Worker binary not found: /opt/scout/Scout.Host.Console.',
    });
  });

  it('a Slack save leaves a worker the owner stopped stopped', async () => {
    const { fixture, bridge } = await render({
      discord: false,
      slack: NOT_CONNECTED,
      worker: { state: 'stopped', restarts: 0 },
    });

    await click(fixture, 'slack-connect');
    await type(fixture, 'dialog-slack-bot-token', 'xoxb-1');
    await type(fixture, 'dialog-slack-app-token', 'xapp-1');
    await click(fixture, 'dialog-save');

    expect(bridge.calls).toEqual(['slackSave:xoxb-1:xapp-1']);
  });

  it('a Disconnect Discord whose bot token cannot be deleted leaves the running worker with Discord, neither stopped nor restarted', async () => {
    const rendered = await render({ discord: true, slack: ACME });
    rendered.bridge.vaultDelete.mockRejectedValueOnce(
      new Error('[scout:vault/unavailable] The credential store is unavailable.'),
    );

    await confirmDisconnect(rendered, 'discord');

    expect({
      calls: rendered.bridge.calls,
      error: text(rendered.fixture, 'chat-clients-error'),
    }).toEqual({
      calls: [],
      error: 'Discord is still connected: The credential store is unavailable.',
    });
  });

  it('disconnecting Slack while Scout is kept for this computer stays on Settings with the worker running', async () => {
    localStorage.setItem('scout:local-only', '1');
    const rendered = await render({ discord: false, slack: ACME });

    await confirmDisconnect(rendered, 'slack');

    expect({ url: TestBed.inject(Router).url, calls: rendered.bridge.calls }).toEqual({
      url: '/settings',
      calls: ['slackDisconnect'],
    });
  });

  it('disconnecting Slack, the last chat app, stops the worker and goes back to Welcome, as Discord does', async () => {
    const rendered = await render({ discord: false, slack: ACME });

    await confirmDisconnect(rendered, 'slack');

    expect({
      url: TestBed.inject(Router).url,
      stopped: rendered.bridge.calls.includes('stop'),
    }).toEqual({ url: '/', stopped: true });
  });
});

describe('Settings chat connections: each state on the shared status chip', () => {
  const CHECK = 'M20 6L9 17l-5-5';
  const DOT = 'M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z';

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    localStorage.clear();
  });

  for (const [name, opts, id, label, tone, glyph] of [
    [
      'Discord connected',
      { discord: true, slack: NOT_CONNECTED },
      'discord',
      'Connected',
      'ok',
      CHECK,
    ],
    [
      'Discord with a token and no server',
      { discord: true, server: false, slack: NOT_CONNECTED },
      'discord',
      'Needs a server',
      'neutral',
      DOT,
    ],
    [
      'Discord not connected',
      { discord: false, slack: NOT_CONNECTED },
      'discord',
      'Not connected',
      'neutral',
      DOT,
    ],
    ['Slack connected', { discord: false, slack: ACME }, 'slack', 'Connected', 'ok', CHECK],
    [
      'Slack not connected',
      { discord: false, slack: NOT_CONNECTED },
      'slack',
      'Not connected',
      'neutral',
      DOT,
    ],
    ['Microsoft Teams', { discord: false, slack: ACME }, 'teams', 'Not connected', 'neutral', DOT],
  ] as const) {
    it(`${name}: the chip reads "${label}" in sentence case after the ${tone === 'ok' ? 'check' : 'dot'}, on the ${tone} container`, async () => {
      const { fixture } = await render(opts);

      const chip = el(fixture, `${id}-state`)!;

      expect({
        chip: chip.tagName.toLowerCase(),
        text: chip.textContent?.trim(),
        tone: chip.getAttribute('data-tone'),
        leadingGlyph: chip.firstElementChild?.querySelector('path')?.getAttribute('d'),
      }).toEqual({ chip: 'sc-status-chip', text: label, tone, leadingGlyph: glyph });
    });
  }
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

describe('the credentials modal as Settings.dc.html draws it, state by state', () => {
  for (const platform of ['discord', 'slack', 'teams'] as const) {
    it(`${platform}: the modal sits 120 px from the top of the window, 480 px wide, its title at 17 px and 600, and 20 px around its parts`, async () => {
      const { fixture } = await start(platform, 'not connected');

      await click(fixture, `${platform}-connect`);
      const pane = document.querySelector('.cdk-overlay-pane') as HTMLElement;
      const host = document.querySelector('sc-chat-connection-dialog')!;

      expect({
        top: pane.style.marginTop,
        width: pane.style.width,
        themedAsTheArtboardsCard: pane.classList.contains('sc-dialog'),
        scrim: document
          .querySelector('.cdk-overlay-backdrop')
          ?.classList.contains('sc-dialog-backdrop'),
        title: [
          declared(host, '--mat-dialog-subhead-size'),
          declared(host, '--mat-dialog-subhead-weight'),
        ],
        insets: [
          declared(host, '--mat-dialog-headline-padding'),
          declared(host, '--mat-dialog-with-actions-content-padding'),
          declared(host, '--mat-dialog-actions-padding'),
        ],
      }).toEqual({
        top: '120px',
        width: '480px',
        themedAsTheArtboardsCard: true,
        scrim: true,
        title: ['17px', '600'],
        insets: ['20px 20px 0', '12px 20px 0', '16px 20px 20px'],
      });
    });
  }

  it('Discord, a first token: one description, the next step in the same paragraph', async () => {
    const { fixture } = await start('discord', 'not connected');

    await click(fixture, 'discord-connect');
    const descriptions = Array.from(
      document.querySelectorAll('.cdk-overlay-container .desc') as NodeListOf<HTMLElement>,
    );

    expect({
      count: descriptions.length,
      next: overlay('dialog-next')?.parentElement === descriptions[0],
      text: descriptions[0]?.textContent?.replace(/\s+/g, ' ').trim(),
    }).toEqual({
      count: 1,
      next: true,
      text: 'Scout answers as your Discord bot. Paste the bot token from the Discord Developer Portal. After the token, Scout opens the step that adds the bot to your server.',
    });
  });

  it('Slack: Create the Slack app is the 36 px outlined button with 14 px sides inside the steps box, which has 10 px corners', async () => {
    const { fixture } = await start('slack', 'not connected');

    await click(fixture, 'slack-connect');
    const create = overlay('dialog-slack-create')!;

    expect({
      height: declared(create, '--mat-button-outlined-container-height'),
      sides: declared(create, '--mat-button-outlined-horizontal-padding'),
      box: declared(create.closest('.steps')!, 'border-radius'),
    }).toEqual({ height: '36px', sides: '14px', box: '10px' });
  });

  for (const platform of ['discord', 'slack'] as const) {
    it(`${platform}: Save is white on the strong accent with 16 px sides, and the eye's glyph is in the second text colour (its outline is checked in the built app)`, async () => {
      const { fixture } = await start(platform, 'not connected');

      await click(fixture, `${platform}-connect`);
      const save = overlay('dialog-save')!;
      const eye = document.querySelector(
        '.cdk-overlay-container [data-test^="dialog-reveal-"]',
      ) as HTMLElement;

      expect({
        save: [
          declared(save, '--mat-button-filled-container-color'),
          declared(save, '--mat-button-filled-label-text-color'),
          declared(save, '--mat-button-filled-horizontal-padding'),
        ],
        eye: declared(eye, '--mat-icon-button-icon-color'),
      }).toEqual({
        save: ['var(--sc-accent-strong)', 'var(--sc-on-accent)', '16px'],
        eye: 'var(--sc-fg-2)',
      });
    });
  }

  it('Teams: the note is on the warn pair with 10 px corners, its text in the warn colour, not the muted grey', async () => {
    const { fixture } = await start('teams', 'not connected');

    await click(fixture, 'teams-connect');
    const note = overlay('dialog-note')!;

    expect([
      declared(note, 'background'),
      declared(note, 'color'),
      declared(note, 'border-radius'),
    ]).toEqual(['var(--sc-warn-container)', 'var(--sc-on-warn-container)', '10px']);
  });
});
