import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { PersistentStateService, type PersistentState } from './persistent-state';
import { ScoutBridgeService } from './scout-bridge';
import { WizardStore } from './wizard.store';

function makeBridgeStub(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    vaultHas: vi.fn().mockResolvedValue({ exists: false }),
    ...overrides,
  } as unknown as ScoutBridgeService;
}

function makePersistentStub(initial: PersistentState | null = null) {
  let state: PersistentState | null = initial;
  return {
    get: vi.fn(() => state),
    set: vi.fn((partial: Partial<PersistentState>) => {
      state = {
        clientId: partial.clientId ?? state?.clientId ?? '',
        guildId: partial.guildId ?? state?.guildId ?? '',
        guildName: partial.guildName ?? state?.guildName ?? '',
      };
    }),
    clear: vi.fn(() => {
      state = null;
    }),
  } as unknown as PersistentStateService;
}

describe('WizardStore', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('using Scout only from this computer counts as provisioned and survives a reload', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: ScoutBridgeService,
          useValue: makeBridgeStub({
            slackStatus: vi.fn().mockResolvedValue({ connected: false }),
          }),
        },
        { provide: PersistentStateService, useValue: makePersistentStub() },
      ],
    });
    TestBed.inject(WizardStore).useLocalOnly();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: ScoutBridgeService,
          useValue: makeBridgeStub({
            slackStatus: vi.fn().mockResolvedValue({ connected: false }),
          }),
        },
        { provide: PersistentStateService, useValue: makePersistentStub() },
      ],
    });
    const reloaded = TestBed.inject(WizardStore);

    expect(await reloaded.loadFromVault()).toEqual({ hasProvisioning: true });
    expect(reloaded.hasProvisioning()).toBe(true);
    expect(reloaded.hasDiscord()).toBe(false);
  });

  it('a local-only owner lands on /chat once per launch; with Slack connected the landing is kept', async () => {
    localStorage.setItem('scout:local-only', '1');
    const configure = (connected: boolean) => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          {
            provide: ScoutBridgeService,
            useValue: makeBridgeStub({
              slackStatus: vi.fn().mockResolvedValue({ connected }),
            }),
          },
          { provide: PersistentStateService, useValue: makePersistentStub() },
        ],
      });
      return TestBed.inject(WizardStore);
    };

    const localOnly = configure(false);
    await localOnly.loadFromVault();
    const first = localOnly.takeLandingUrl();
    const second = localOnly.takeLandingUrl();
    const withSlack = configure(true);
    await withSlack.loadFromVault();

    expect(first).toBe('/chat');
    expect(second).toBeNull();
    expect(withSlack.takeLandingUrl()).toBeNull();
  });

  it('defaults to the welcome step with empty state', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: makeBridgeStub() },
        { provide: PersistentStateService, useValue: makePersistentStub() },
      ],
    });
    const store = TestBed.inject(WizardStore);

    expect(store.currentStep()).toBe('welcome');
    expect(store.clientId()).toBeNull();
    expect(store.guild()).toBeNull();
    expect(store.activeStepIndex()).toBe(0);
    expect(store.hasProvisioning()).toBe(false);
  });

  it('transitions through wizard steps via goTo', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: makeBridgeStub() },
        { provide: PersistentStateService, useValue: makePersistentStub() },
      ],
    });
    const store = TestBed.inject(WizardStore);

    store.goTo('provision');
    expect(store.currentStep()).toBe('provision');
    expect(store.activeStepIndex()).toBe(1);

    store.goTo('oauth');
    expect(store.currentStep()).toBe('oauth');
    expect(store.activeStepIndex()).toBe(3);
  });

  it('records clientId and guild', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: makeBridgeStub() },
        { provide: PersistentStateService, useValue: makePersistentStub() },
      ],
    });
    const store = TestBed.inject(WizardStore);

    store.setClientId('1234567890');
    store.setGuild({ id: 'g1', name: 'My Guild' });

    expect(store.clientId()).toBe('1234567890');
    expect(store.guild()).toEqual({ id: 'g1', name: 'My Guild' });
    expect(store.hasProvisioning()).toBe(true);
  });

  it('reset is synchronous and only touches signals (no vault calls)', async () => {
    const bridge = makeBridgeStub();
    const persistent = makePersistentStub();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: PersistentStateService, useValue: persistent },
      ],
    });
    const store = TestBed.inject(WizardStore);

    store.goTo('oauth');
    store.setClientId('abc');
    store.setGuild({ id: 'g', name: 'n' });

    await store.reset();

    expect(store.currentStep()).toBe('welcome');
    expect(store.clientId()).toBeNull();
    expect(store.guild()).toBeNull();
    expect(bridge.vaultDelete).not.toHaveBeenCalled();
    expect(persistent.clear).not.toHaveBeenCalled();
  });

  it('reprovision clears signals, deletes all 4 vault keys, and clears localStorage', async () => {
    const bridge = makeBridgeStub();
    const persistent = makePersistentStub({ clientId: 'c', guildId: 'g', guildName: 'n' });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: PersistentStateService, useValue: persistent },
      ],
    });
    const store = TestBed.inject(WizardStore);

    store.goTo('ready');
    store.setClientId('abc');
    store.setGuild({ id: 'g', name: 'n' });

    await store.reprovision();

    expect(store.currentStep()).toBe('welcome');
    expect(store.clientId()).toBeNull();
    expect(store.guild()).toBeNull();
    expect(bridge.vaultDelete).toHaveBeenCalledTimes(4);
    expect(bridge.vaultDelete).toHaveBeenCalledWith({ key: 'discord.bot-token' });
    expect(bridge.vaultDelete).toHaveBeenCalledWith({ key: 'discord.client-id' });
    expect(bridge.vaultDelete).toHaveBeenCalledWith({ key: 'discord.guild-id' });
    expect(bridge.vaultDelete).toHaveBeenCalledWith({ key: 'discord.guild-name' });
    expect(persistent.clear).toHaveBeenCalledTimes(1);
  });

  it('reprovision rejects and keeps Discord connected when the bot token cannot be deleted', async () => {
    const vaultDelete = vi.fn().mockRejectedValue(new Error('boom'));
    const failing = makeBridgeStub({ vaultDelete });
    const persistent = makePersistentStub();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: failing },
        { provide: PersistentStateService, useValue: persistent },
      ],
    });
    const store = TestBed.inject(WizardStore);
    store.setClientId('x');
    store.setGuild({ id: 'g', name: 'n' });

    await expect(store.reprovision()).rejects.toThrow('boom');
    expect(store.hasDiscord()).toBe(true);
    expect(vaultDelete.mock.calls).toEqual([[{ key: 'discord.bot-token' }]]);
    expect(persistent.clear).not.toHaveBeenCalled();
  });

  it('reprovision still forgets Discord when only the metadata keys cannot be deleted', async () => {
    const failing = makeBridgeStub({
      vaultDelete: vi.fn(async (req: { key: string }) => {
        if (req.key !== 'discord.bot-token') throw new Error('boom');
        return { deleted: true };
      }),
    });
    const persistent = makePersistentStub();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: failing },
        { provide: PersistentStateService, useValue: persistent },
      ],
    });
    const store = TestBed.inject(WizardStore);
    store.setClientId('x');

    await expect(store.reprovision()).resolves.toBeUndefined();
    expect(store.clientId()).toBeNull();
    expect(failing.vaultDelete).toHaveBeenCalledTimes(4);
    expect(persistent.clear).toHaveBeenCalledTimes(1);
  });

  it('loadFromVault returns hasProvisioning=false when token absent', async () => {
    const bridge = makeBridgeStub({
      vaultHas: vi.fn().mockResolvedValue({ exists: false }),
    });
    const persistent = makePersistentStub({ clientId: 'c', guildId: 'g', guildName: 'n' });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: PersistentStateService, useValue: persistent },
      ],
    });
    const store = TestBed.inject(WizardStore);

    const result = await store.loadFromVault();

    expect(result).toEqual({ hasProvisioning: false });
    expect(store.clientId()).toBeNull();
    expect(store.guild()).toBeNull();
  });

  it('loadFromVault counts Discord as set up when the vault holds the token and a chosen server, even if localStorage is empty', async () => {
    const bridge = makeBridgeStub({
      vaultHas: vi.fn(async ({ key }: { key: string }) => ({
        exists: ['discord.bot-token', 'discord.guild-id'].includes(key),
      })),
    });
    const persistent = makePersistentStub(null);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: PersistentStateService, useValue: persistent },
      ],
    });
    const store = TestBed.inject(WizardStore);

    const result = await store.loadFromVault();

    expect(result).toEqual({ hasProvisioning: true });
    expect(store.hasProvisioning()).toBe(true);
    expect(store.clientId()).toBeNull();
    expect(store.guild()).toBeNull();
  });

  it('loadFromVault populates clientId and guild from localStorage when token exists', async () => {
    const bridge = makeBridgeStub({
      vaultHas: vi.fn().mockResolvedValue({ exists: true }),
    });
    const persistent = makePersistentStub({
      clientId: '999',
      guildId: 'g42',
      guildName: 'Scout Dev',
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: PersistentStateService, useValue: persistent },
      ],
    });
    const store = TestBed.inject(WizardStore);

    const result = await store.loadFromVault();

    expect(result).toEqual({ hasProvisioning: true });
    expect(store.clientId()).toBe('999');
    expect(store.guild()).toEqual({ id: 'g42', name: 'Scout Dev' });
    expect(store.hasProvisioning()).toBe(true);
  });

  it('loadFromVault tolerates vaultHas rejection and reports hasProvisioning=false', async () => {
    const bridge = makeBridgeStub({
      vaultHas: vi.fn().mockRejectedValue(new Error('unavailable')),
    });
    const persistent = makePersistentStub({ clientId: 'c', guildId: 'g', guildName: 'n' });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: PersistentStateService, useValue: persistent },
      ],
    });
    const store = TestBed.inject(WizardStore);

    const result = await store.loadFromVault();

    expect(result).toEqual({ hasProvisioning: false });
    expect(store.clientId()).toBeNull();
  });

  it('a server mirrored in localStorage that the vault no longer holds is wiped and not shown', async () => {
    const bridge = makeBridgeStub({
      vaultHas: vi.fn(async ({ key }: { key: string }) => ({
        exists: key === 'discord.bot-token',
      })),
    });
    const persistent = makePersistentStub({
      clientId: '999',
      guildId: '42',
      guildName: 'Old Team',
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: PersistentStateService, useValue: persistent },
      ],
    });
    const store = TestBed.inject(WizardStore);

    await store.loadFromVault();

    expect({ guild: store.guild(), mirror: persistent.get(), set: store.hasDiscord() }).toEqual({
      guild: null,
      mirror: null,
      set: false,
    });
  });

  it('a vault that cannot say whether a server was chosen leaves Discord not set up this launch and keeps the mirror', async () => {
    const bridge = makeBridgeStub({
      vaultHas: vi.fn(async ({ key }: { key: string }) => {
        if (key === 'discord.guild-id') throw new Error('unavailable');
        return { exists: true };
      }),
    });
    const mirror = { clientId: '999', guildId: '42', guildName: 'Team' };
    const persistent = makePersistentStub(mirror);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: PersistentStateService, useValue: persistent },
      ],
    });
    const store = TestBed.inject(WizardStore);

    await store.loadFromVault();

    expect({ set: store.hasDiscord(), mirror: persistent.get() }).toEqual({
      set: false,
      mirror,
    });
  });
  it('Slack only: loadFromVault counts Slack as provisioning but not as Discord', async () => {
    const bridge = makeBridgeStub({
      vaultHas: vi.fn().mockResolvedValue({ exists: false }),
      slackStatus: vi.fn().mockResolvedValue({ connected: true, team: 'Acme', botUser: 'scout' }),
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: PersistentStateService, useValue: makePersistentStub(null) },
      ],
    });
    const store = TestBed.inject(WizardStore);

    const result = await store.loadFromVault();

    expect(result).toEqual({ hasProvisioning: true });
    expect(store.hasProvisioning()).toBe(true);
    expect(store.slackConnected()).toBe(true);
    expect(store.hasDiscord()).toBe(false);
  });

  it('a failing Slack status reads as not connected and keeps Discord, token and chosen server, provisioning', async () => {
    const bridge = makeBridgeStub({
      vaultHas: vi.fn(async ({ key }: { key: string }) => ({
        exists: ['discord.bot-token', 'discord.guild-id'].includes(key),
      })),
      slackStatus: vi.fn().mockRejectedValue(new Error('unavailable')),
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: PersistentStateService, useValue: makePersistentStub(null) },
      ],
    });
    const store = TestBed.inject(WizardStore);

    const result = await store.loadFromVault();

    expect(result).toEqual({ hasProvisioning: true });
    expect(store.slackConnected()).toBe(false);
  });

  it('reprovisioning Discord keeps a Slack connection provisioned', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: makeBridgeStub() },
        { provide: PersistentStateService, useValue: makePersistentStub(null) },
      ],
    });
    const store = TestBed.inject(WizardStore);
    store.setSlackConnected(true);

    await store.reprovision();

    expect(store.hasProvisioning()).toBe(true);
    expect(store.hasDiscord()).toBe(false);
  });

  it('a bot token saved before any server was chosen does not count as Discord set up when the app reopens', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: ScoutBridgeService,
          useValue: makeBridgeStub({
            vaultHas: vi.fn(async ({ key }: { key: string }) => ({
              exists: key === 'discord.bot-token' || key === 'discord.client-id',
            })),
            slackStatus: vi.fn().mockResolvedValue({ connected: false }),
          }),
        },
        { provide: PersistentStateService, useValue: makePersistentStub(null) },
      ],
    });
    const store = TestBed.inject(WizardStore);

    const result = await store.loadFromVault();

    expect({
      result,
      hasDiscord: store.hasDiscord(),
      hasProvisioning: store.hasProvisioning(),
    }).toEqual({ result: { hasProvisioning: false }, hasDiscord: false, hasProvisioning: false });
  });

  it('a local-only owner who left Add to server reopens on Chat, as before, not on the setup step', async () => {
    localStorage.setItem('scout:local-only', '1');
    const configure = () => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          {
            provide: ScoutBridgeService,
            useValue: makeBridgeStub({
              vaultHas: vi.fn(async ({ key }: { key: string }) => ({
                exists: key === 'discord.bot-token',
              })),
              slackStatus: vi.fn().mockResolvedValue({ connected: false }),
            }),
          },
          { provide: PersistentStateService, useValue: makePersistentStub(null) },
        ],
      });
      return TestBed.inject(WizardStore);
    };
    const before = configure();
    before.setClientId('111222333');
    before.goTo('oauth');

    const reopened = configure();
    await reopened.loadFromVault();

    expect({ landing: reopened.takeLandingUrl(), clientId: reopened.clientId() }).toEqual({
      landing: '/chat',
      clientId: '111222333',
    });
  });

  it('after Reprovision a reopened app starts at Welcome instead of resuming the old setup step', async () => {
    const configure = () => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          {
            provide: ScoutBridgeService,
            useValue: makeBridgeStub({
              slackStatus: vi.fn().mockResolvedValue({ connected: false }),
            }),
          },
          { provide: PersistentStateService, useValue: makePersistentStub(null) },
        ],
      });
      return TestBed.inject(WizardStore);
    };
    const before = configure();
    before.setClientId('111222333');
    before.goTo('oauth');
    await before.reprovision();

    const reopened = configure();
    await reopened.loadFromVault();

    expect({
      landing: reopened.takeLandingUrl(),
      step: reopened.currentStep(),
      clientId: reopened.clientId(),
    }).toEqual({ landing: null, step: 'welcome', clientId: null });
  });

  it('a Slack owner who left Add to server through the sidebar reopens on their usual landing, not on the setup step', async () => {
    const configure = () => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          {
            provide: ScoutBridgeService,
            useValue: makeBridgeStub({
              vaultHas: vi.fn(async ({ key }: { key: string }) => ({
                exists: key === 'discord.bot-token',
              })),
              slackStatus: vi
                .fn()
                .mockResolvedValue({ connected: true, team: 'Acme', botUser: 'scout' }),
            }),
          },
          { provide: PersistentStateService, useValue: makePersistentStub(null) },
        ],
      });
      return TestBed.inject(WizardStore);
    };
    const before = configure();
    before.setClientId('111222333');
    before.goTo('oauth');

    const reopened = configure();
    await reopened.loadFromVault();

    expect(reopened.takeLandingUrl()).toBeNull();
  });

  it('a bot token the vault keeps without a server reads as Discord needing a server, not as set up', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: ScoutBridgeService,
          useValue: makeBridgeStub({
            vaultHas: vi.fn(async ({ key }: { key: string }) => ({
              exists: key === 'discord.bot-token',
            })),
            slackStatus: vi
              .fn()
              .mockResolvedValue({ connected: true, team: 'Acme', botUser: 'scout' }),
          }),
        },
        { provide: PersistentStateService, useValue: makePersistentStub(null) },
      ],
    });
    const store = TestBed.inject(WizardStore);

    const result = await store.loadFromVault();

    expect({
      result,
      discord: store.hasDiscord(),
      needsServer: store.discordNeedsServer(),
    }).toEqual({ result: { hasProvisioning: true }, discord: false, needsServer: true });
  });
});
