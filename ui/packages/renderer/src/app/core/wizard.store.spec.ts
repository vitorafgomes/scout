import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from './scout-bridge';
import { WizardStore } from './wizard.store';

function makeBridgeStub() {
  return {
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
  } as unknown as ScoutBridgeService;
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
      ],
    });
    const store = TestBed.inject(WizardStore);

    expect(store.currentStep()).toBe('welcome');
    expect(store.clientId()).toBeNull();
    expect(store.guild()).toBeNull();
    expect(store.intentsAcknowledged()).toBe(false);
    expect(store.activeStepIndex()).toBe(0);
  });

  it('transitions through wizard steps via goTo', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: makeBridgeStub() },
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
      ],
    });
    const store = TestBed.inject(WizardStore);

    store.setClientId('1234567890');
    store.setGuild({ id: 'g1', name: 'My Guild' });
    store.markIntentsAcknowledged();

    expect(store.clientId()).toBe('1234567890');
    expect(store.guild()).toEqual({ id: 'g1', name: 'My Guild' });
    expect(store.intentsAcknowledged()).toBe(true);
  });

  it('reset clears every signal and deletes both vault keys', async () => {
    const bridge = makeBridgeStub();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge },
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
    expect(bridge.vaultDelete).toHaveBeenCalledTimes(2);
    expect(bridge.vaultDelete).toHaveBeenCalledWith({ key: 'discord.bot-token' });
    expect(bridge.vaultDelete).toHaveBeenCalledWith({ key: 'discord.client-id' });
  });

  it('reset never throws when vault deletion fails', async () => {
    const failing = {
      vaultDelete: vi.fn().mockRejectedValue(new Error('boom')),
    } as unknown as ScoutBridgeService;
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: failing },
      ],
    });
    const store = TestBed.inject(WizardStore);
    store.setClientId('x');

    await expect(store.reset()).resolves.toBeUndefined();
    expect(store.clientId()).toBeNull();
    expect(failing.vaultDelete).toHaveBeenCalledTimes(2);
  });
});
