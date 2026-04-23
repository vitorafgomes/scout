import { beforeEach, describe, expect, it } from 'vitest';

import { PERSISTENT_STATE_STORAGE_KEY, PersistentStateService } from './persistent-state';

describe('PersistentStateService', () => {
  let service: PersistentStateService;

  beforeEach(() => {
    window.localStorage.clear();
    service = new PersistentStateService();
  });

  it('round-trips set and get', () => {
    service.set({ clientId: 'c1', guildId: 'g1', guildName: 'Scout Dev' });
    expect(service.get()).toEqual({ clientId: 'c1', guildId: 'g1', guildName: 'Scout Dev' });
  });

  it('merges partial updates without clobbering untouched fields', () => {
    service.set({ clientId: 'c1', guildId: 'g1', guildName: 'Scout Dev' });
    service.set({ guildName: 'Renamed Guild' });
    expect(service.get()).toEqual({
      clientId: 'c1',
      guildId: 'g1',
      guildName: 'Renamed Guild',
    });
  });

  it('clear wipes the persisted state', () => {
    service.set({ clientId: 'c1', guildId: 'g1', guildName: 'n' });
    service.clear();
    expect(service.get()).toBeNull();
    expect(window.localStorage.getItem(PERSISTENT_STATE_STORAGE_KEY)).toBeNull();
  });

  it('get returns null when nothing was persisted', () => {
    expect(service.get()).toBeNull();
  });

  it('get returns null on malformed JSON', () => {
    window.localStorage.setItem(PERSISTENT_STATE_STORAGE_KEY, '{not json');
    expect(service.get()).toBeNull();
  });

  it('get returns null when payload is missing required fields', () => {
    window.localStorage.setItem(PERSISTENT_STATE_STORAGE_KEY, JSON.stringify({ clientId: 'c1' }));
    expect(service.get()).toBeNull();
  });

  it('get returns null when any field is empty', () => {
    window.localStorage.setItem(
      PERSISTENT_STATE_STORAGE_KEY,
      JSON.stringify({ clientId: '', guildId: 'g', guildName: 'n' }),
    );
    expect(service.get()).toBeNull();
  });
});
