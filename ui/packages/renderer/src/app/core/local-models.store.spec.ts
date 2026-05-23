import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ProviderUnreachableError } from '@scout/shared';
import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore } from './local-models.store';
import { ScoutBridgeService } from './scout-bridge';

function provide(): LocalModelsStore {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      {
        provide: ScoutBridgeService,
        useValue: { vaultStore: vi.fn(async () => undefined) } as unknown as ScoutBridgeService,
      },
    ],
  });
  return TestBed.inject(LocalModelsStore);
}

describe('LocalModelsStore', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  it('starts empty', () => {
    const store = provide();
    expect(store.downloaded()).toEqual([]);
    expect(store.selected()).toEqual({ transcription: null, analysis: null });
  });

  it('markDownloaded auto-selects the first model of its kind', () => {
    const store = provide();
    store.markDownloaded('whisper-large-v3-turbo');
    expect(store.downloaded()).toEqual(['whisper-large-v3-turbo']);
    expect(store.selectedFor('transcription')).toBe('whisper-large-v3-turbo');
    expect(store.selectedFor('analysis')).toBeNull();
  });

  it('markDownloaded does not change selection when a model is already selected', () => {
    const store = provide();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-large-v3');
    expect(store.selectedFor('transcription')).toBe('whisper-large-v3-turbo');
  });

  it('select changes the active model for a kind', () => {
    const store = provide();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-large-v3');
    store.select('transcription', 'whisper-large-v3');
    expect(store.selectedFor('transcription')).toBe('whisper-large-v3');
  });

  it('select rejects a model that is not downloaded', () => {
    const store = provide();
    store.select('transcription', 'whisper-large-v3');
    expect(store.selectedFor('transcription')).toBeNull();
  });

  it('select rejects a model from a different kind', () => {
    const store = provide();
    store.markDownloaded('qwen-3-vl-2b');
    store.select('transcription', 'qwen-3-vl-2b');
    expect(store.selectedFor('transcription')).toBeNull();
  });

  it('markRemoved drops the model and reselects the next downloaded of the same kind', () => {
    const store = provide();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-large-v3');
    store.markRemoved('whisper-large-v3-turbo');
    expect(store.downloaded()).toEqual(['whisper-large-v3']);
    expect(store.selectedFor('transcription')).toBe('whisper-large-v3');
  });

  it('markRemoved clears selection when nothing else of the kind is downloaded', () => {
    const store = provide();
    store.markDownloaded('qwen-3-vl-2b');
    store.markRemoved('qwen-3-vl-2b');
    expect(store.downloaded()).toEqual([]);
    expect(store.selectedFor('analysis')).toBeNull();
  });

  it('downloadedFor only returns models matching the kind', () => {
    const store = provide();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('qwen-3-vl-2b');
    expect(store.downloadedFor('transcription').map((m) => m.id)).toEqual([
      'whisper-large-v3-turbo',
    ]);
    expect(store.downloadedFor('analysis').map((m) => m.id)).toEqual(['qwen-3-vl-2b']);
  });

  it('deleteUnused keeps only the currently selected models', () => {
    const store = provide();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-large-v3');
    store.markDownloaded('qwen-3-vl-2b');
    store.markDownloaded('qwen-2.5-1.5b-instruct');
    store.deleteUnused();
    // Only the auto-selected first of each kind survives.
    expect([...store.downloaded()].sort()).toEqual(['qwen-3-vl-2b', 'whisper-large-v3-turbo'].sort());
  });

  it('persists across instances via localStorage', () => {
    const a = provide();
    a.markDownloaded('whisper-large-v3-turbo');
    a.markDownloaded('qwen-3-vl-2b');

    TestBed.resetTestingModule();
    const b = provide();
    expect([...b.downloaded()].sort()).toEqual(['qwen-3-vl-2b', 'whisper-large-v3-turbo'].sort());
    expect(b.selectedFor('transcription')).toBe('whisper-large-v3-turbo');
    expect(b.selectedFor('analysis')).toBe('qwen-3-vl-2b');
  });

  it('ignores unknown model ids in persisted state', () => {
    window.localStorage.setItem(
      LOCAL_MODELS_STORAGE_KEY,
      JSON.stringify({
        downloaded: ['whisper-large-v3-turbo', 'unknown-model'],
        selected: { transcription: 'unknown-model', analysis: null },
      }),
    );
    const store = provide();
    expect(store.downloaded()).toEqual(['whisper-large-v3-turbo']);
    expect(store.selectedFor('transcription')).toBeNull();
  });

  it('totalDownloadedBytes sums catalog sizes of downloaded models', () => {
    const store = provide();
    expect(store.totalDownloadedBytes()).toBe(0);
    store.markDownloaded('whisper-large-v3-turbo');
    expect(store.totalDownloadedBytes()).toBeGreaterThan(0);
  });
});

function provideWithBridge(bridgePartial: Partial<ScoutBridgeService>): LocalModelsStore {
  const bridge = {
    vaultStore: vi.fn(async () => undefined),
    ...bridgePartial,
  } as unknown as ScoutBridgeService;
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  return TestBed.inject(LocalModelsStore);
}

describe('LocalModelsStore — hydrateFromWorker', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  it('hydrateFromWorker_unionsWorkerModels', async () => {
    const bridge = {
      providerStatus: vi.fn(async () => ({
        id: 'ollama' as const,
        installed: true,
        running: true,
        endpoint: null,
        version: null,
        managedByWorker: false,
        autostart: false,
        supportsLifecycle: false,
        lastError: null,
        models: [{ name: 'qwen2.5:3b', sizeBytes: 0, modifiedAt: '' }],
      })),
    };
    const store = provideWithBridge(bridge);
    store.markDownloaded('whisper-large-v3-turbo');

    await store.hydrateFromWorker();

    expect(store.downloaded()).toContain('whisper-large-v3-turbo');
    expect(store.downloaded()).toContain('qwen-3-vl-2b');
    expect(store.downloaded()).toHaveLength(2);
  });

  it('hydrateFromWorker_skipsModelsNotInCatalog', async () => {
    const bridge = {
      providerStatus: vi.fn(async () => ({
        id: 'ollama' as const,
        installed: true,
        running: true,
        endpoint: null,
        version: null,
        managedByWorker: false,
        autostart: false,
        supportsLifecycle: false,
        lastError: null,
        models: [{ name: 'random-unknown:1b', sizeBytes: 0, modifiedAt: '' }],
      })),
    };
    const store = provideWithBridge(bridge);

    await store.hydrateFromWorker();

    expect(store.downloaded()).not.toContain('random-unknown:1b');
    expect(store.downloaded()).toHaveLength(0);
  });

  it('hydrateFromWorker_doesNotDuplicate', async () => {
    const bridge = {
      providerStatus: vi.fn(async () => ({
        id: 'ollama' as const,
        installed: true,
        running: true,
        endpoint: null,
        version: null,
        managedByWorker: false,
        autostart: false,
        supportsLifecycle: false,
        lastError: null,
        models: [{ name: 'qwen2.5:3b', sizeBytes: 0, modifiedAt: '' }],
      })),
    };
    const store = provideWithBridge(bridge);
    store.markDownloaded('qwen-3-vl-2b');

    await store.hydrateFromWorker();

    expect(store.downloaded().filter((id) => id === 'qwen-3-vl-2b')).toHaveLength(1);
  });

  it('hydrateFromWorker_workerThrows_stateUnchanged', async () => {
    const bridge = {
      providerStatus: vi.fn(async (): Promise<never> => {
        throw new ProviderUnreachableError();
      }),
    };
    const store = provideWithBridge(bridge);
    store.markDownloaded('whisper-large-v3-turbo');
    const before = store.downloaded();

    await store.hydrateFromWorker();

    expect(store.downloaded()).toEqual(before);
  });

  it('hydrateFromWorker_persistsToLocalStorage', async () => {
    const bridge = {
      providerStatus: vi.fn(async () => ({
        id: 'ollama' as const,
        installed: true,
        running: true,
        endpoint: null,
        version: null,
        managedByWorker: false,
        autostart: false,
        supportsLifecycle: false,
        lastError: null,
        models: [{ name: 'qwen2.5:3b', sizeBytes: 0, modifiedAt: '' }],
      })),
    };
    const store = provideWithBridge(bridge);
    store.markDownloaded('whisper-large-v3-turbo');

    await store.hydrateFromWorker();

    const raw = window.localStorage.getItem(LOCAL_MODELS_STORAGE_KEY);
    expect(raw).not.toBeNull();
    const parsed = JSON.parse(raw!) as { downloaded: string[] };
    expect(parsed.downloaded).toContain('whisper-large-v3-turbo');
    expect(parsed.downloaded).toContain('qwen-3-vl-2b');
  });
});
