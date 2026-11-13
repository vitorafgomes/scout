import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from './scout-bridge';
import { PersistentStateService } from './persistent-state';
import { WizardStore } from './wizard.store';
import { requireBotTokenGuard } from './wizard.guards';

function makeBridgeStub(tokenExists: boolean) {
  return {
    vaultHas: vi.fn().mockResolvedValue({ exists: tokenExists }),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
  } as unknown as ScoutBridgeService;
}

function makePersistentStub() {
  return {
    get: vi.fn(() => null),
    set: vi.fn(),
    clear: vi.fn(),
  } as unknown as PersistentStateService;
}

function setup(tokenExists: boolean) {
  const bridge = makeBridgeStub(tokenExists);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: '', children: [] },
        { path: 'launch', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: PersistentStateService, useValue: makePersistentStub() },
    ],
  });
  return { bridge, router: TestBed.inject(Router), store: TestBed.inject(WizardStore) };
}

describe('requireBotTokenGuard', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  for (const [held, opens] of [
    [['discord.bot-token', 'discord.guild-id'], true],
    [['discord.bot-token'], false],
  ] as const) {
    it(`${opens ? 'opens' : 'redirects to / instead of opening'} /launch when the vault holds ${held.join(' and ')}`, async () => {
      const { bridge, router, store } = setup(true);
      vi.mocked(bridge.vaultHas).mockImplementation(async ({ key }) => ({
        exists: (held as readonly string[]).includes(key),
      }));
      await store.loadFromVault();

      const result = TestBed.runInInjectionContext(() =>
        requireBotTokenGuard({} as never, {} as never),
      );
      expect(result).toEqual(opens ? true : router.parseUrl('/'));
    });
  }

  it('redirects to / when hasProvisioning is false (no token, no guild)', () => {
    const { router } = setup(false);

    const result = TestBed.runInInjectionContext(() =>
      requireBotTokenGuard({} as never, {} as never),
    );

    expect(result).not.toBe(true);
    expect(result).toEqual(router.parseUrl('/'));
  });

  it('returns true when guild is set even without calling loadFromVault', () => {
    setup(false);
    const store = TestBed.inject(WizardStore);
    store.setClientId('cid');
    store.setGuild({ id: 'g1', name: 'My Guild' });

    const result = TestBed.runInInjectionContext(() =>
      requireBotTokenGuard({} as never, {} as never),
    );
    expect(result).toBe(true);
  });
});
