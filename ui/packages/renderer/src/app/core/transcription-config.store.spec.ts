import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from './scout-bridge';
import {
  STT_CONFIG_STORAGE_KEY,
  TranscriptionConfigStore,
} from './transcription-config.store';

interface FakeBridge {
  vaultStore: ReturnType<typeof vi.fn>;
  vaultHas: ReturnType<typeof vi.fn>;
}

function makeBridge(opts: { vaultHas?: boolean } = {}): FakeBridge {
  return {
    vaultStore: vi.fn(async () => undefined),
    vaultHas: vi.fn(async () => ({ exists: opts.vaultHas ?? false })),
  };
}

function provide(bridge: FakeBridge): TranscriptionConfigStore {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  return TestBed.inject(TranscriptionConfigStore);
}

describe('TranscriptionConfigStore', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(STT_CONFIG_STORAGE_KEY);
  });

  it('is empty before any selection', () => {
    const store = provide(makeBridge());
    expect(store.provider()).toBeNull();
    expect(store.model()).toBeNull();
    expect(store.isConfigured()).toBe(false);
  });

  it('setSelection writes stt.provider and stt.model to the vault', async () => {
    const bridge = makeBridge();
    const store = provide(bridge);
    await store.setSelection('LocalWhisper', 'whisper-large-v3-turbo');

    expect(bridge.vaultStore).toHaveBeenCalledTimes(2);
    const calls = bridge.vaultStore.mock.calls.map((c) => c[0] as { key: string; value: string });
    expect(calls[0]).toEqual({ key: 'stt.provider', value: 'LocalWhisper' });
    expect(calls[1]).toEqual({ key: 'stt.model', value: 'whisper-large-v3-turbo' });
  });

  it('setSelection mirrors the snapshot into localStorage', async () => {
    const store = provide(makeBridge());
    await store.setSelection('LocalWhisper', 'whisper-large-v3');

    const raw = window.localStorage.getItem(STT_CONFIG_STORAGE_KEY);
    expect(raw).not.toBeNull();
    expect(JSON.parse(raw ?? '{}')).toEqual({
      provider: 'LocalWhisper',
      model: 'whisper-large-v3',
    });
  });

  it('loadFromVault hydrates signals when localStorage and vault agree', async () => {
    window.localStorage.setItem(
      STT_CONFIG_STORAGE_KEY,
      JSON.stringify({ provider: 'LocalWhisper', model: 'whisper-large-v3-turbo' }),
    );
    const store = provide(makeBridge({ vaultHas: true }));
    await store.loadFromVault();

    expect(store.provider()).toBe('LocalWhisper');
    expect(store.model()).toBe('whisper-large-v3-turbo');
    expect(store.isConfigured()).toBe(true);
  });

  it('loadFromVault wipes mirror when localStorage is set but vault.has is false', async () => {
    window.localStorage.setItem(
      STT_CONFIG_STORAGE_KEY,
      JSON.stringify({ provider: 'LocalWhisper', model: 'whisper-large-v3' }),
    );
    const store = provide(makeBridge({ vaultHas: false }));
    await store.loadFromVault();

    expect(store.provider()).toBeNull();
    expect(store.model()).toBeNull();
    expect(window.localStorage.getItem(STT_CONFIG_STORAGE_KEY)).toBeNull();
  });

  it('loadFromVault stays empty when nothing is persisted', async () => {
    const store = provide(makeBridge({ vaultHas: true }));
    await store.loadFromVault();
    expect(store.provider()).toBeNull();
    expect(store.model()).toBeNull();
  });

  it('rejects unknown model strings stored in localStorage', async () => {
    window.localStorage.setItem(
      STT_CONFIG_STORAGE_KEY,
      JSON.stringify({ provider: 'LocalWhisper', model: 'whisper-tiny' }),
    );
    const store = provide(makeBridge({ vaultHas: true }));
    await store.loadFromVault();
    expect(store.model()).toBeNull();
  });
});
