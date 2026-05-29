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
    expect(store.intentsAcknowledged()).toBe(false);
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

  it('records clientId, guild, and intents acknowledgement', () => {
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
    store.markIntentsAcknowledged();

    expect(store.clientId()).toBe('1234567890');
    expect(store.guild()).toEqual({ id: 'g1', name: 'My Guild' });
    expect(store.intentsAcknowledged()).toBe(true);
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
    store.markIntentsAcknowledged();

    await store.reset();

    expect(store.currentStep()).toBe('welcome');
    expect(store.clientId()).toBeNull();
    expect(store.guild()).toBeNull();
    expect(store.intentsAcknowledged()).toBe(false);
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
    expect(store.intentsAcknowledged()).toBe(false);
    expect(bridge.vaultDelete).toHaveBeenCalledTimes(4);
    expect(bridge.vaultDelete).toHaveBeenCalledWith({ key: 'discord.bot-token' });
    expect(bridge.vaultDelete).toHaveBeenCalledWith({ key: 'discord.client-id' });
    expect(bridge.vaultDelete).toHaveBeenCalledWith({ key: 'discord.guild-id' });
    expect(bridge.vaultDelete).toHaveBeenCalledWith({ key: 'discord.guild-name' });
    expect(persistent.clear).toHaveBeenCalledTimes(1);
  });

  it('reprovision never throws when vault deletion fails', async () => {
    const failing = makeBridgeStub({
      vaultDelete: vi.fn().mockRejectedValue(new Error('boom')),
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

  it('loadFromVault returns hasProvisioning=true when token exists even if localStorage is empty', async () => {
    const bridge = makeBridgeStub({
      vaultHas: vi.fn().mockResolvedValue({ exists: true }),
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

    // Token alone is enough to provision; clientId/guild remain null (optional metadata).
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

  it('hasProvisioning is true with token alone (no clientId, no guild)', async () => {
    const bridge = makeBridgeStub({
      vaultHas: vi.fn().mockResolvedValue({ exists: true }),
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

    await store.loadFromVault();

    expect(store.clientId()).toBeNull();
    expect(store.guild()).toBeNull();
    expect(store.hasProvisioning()).toBe(true);
  });
});
