import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { OllamaUnreachableError, type OllamaModelInfo } from '@scout/shared';
import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore, ollamaKeyForName } from './local-models.store';
import { ScoutBridgeService } from './scout-bridge';

const vaultStore = vi.fn(async (_req: unknown) => undefined);
const vaultDelete = vi.fn(async (_req: unknown) => ({ deleted: true }));
const whisperModelTable = vi.fn(async () => ({
  models: [
    { variant: 'base', file: 'ggml-base.bin', size: 147_951_465 },
    { variant: 'large-v3-turbo', file: 'ggml-large-v3-turbo.bin', size: 1_624_555_275 },
  ],
}));

function provide(): LocalModelsStore {
  vaultStore.mockClear();
  vaultDelete.mockClear();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      {
        provide: ScoutBridgeService,
        useValue: { vaultStore, vaultDelete, whisperModelTable } as unknown as ScoutBridgeService,
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
    void store.select('transcription', 'whisper-large-v3');
    expect(store.selectedFor('transcription')).toBe('whisper-large-v3');
  });

  it('selecting a whisper model moves the window selection and leaves stt.model to main', async () => {
    const store = provide();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-base');
    vaultStore.mockClear();
    await store.select('transcription', 'whisper-base');
    expect(store.selectedFor('transcription')).toBe('whisper-base');
    expect(vaultStore).not.toHaveBeenCalled();
  });

  it('selecting an analysis model does not touch stt.model', () => {
    const store = provide();
    store.markDownloaded('qwen-3-vl-2b');
    store.markDownloaded('qwen-2.5-1.5b-instruct');
    void store.select('analysis', 'qwen-2.5-1.5b-instruct');
    expect(vaultStore.mock.calls.some(([r]) => (r as { key: string }).key === 'stt.model')).toBe(
      false,
    );
  });

  it('removing the only whisper model clears the window selection and leaves stt.model to main', () => {
    const store = provide();
    store.markDownloaded('whisper-base');
    store.markRemoved('whisper-base');
    expect(store.selectedFor('transcription')).toBeNull();
    expect(vaultDelete).not.toHaveBeenCalled();
  });

  it('removing the selected whisper model while another remains moves the window selection and leaves stt.model to main', () => {
    const store = provide();
    store.markDownloaded('whisper-base');
    store.markDownloaded('whisper-large-v3-turbo');
    vaultStore.mockClear();
    vaultDelete.mockClear();
    store.markRemoved('whisper-base');
    expect(store.selectedFor('transcription')).toBe('whisper-large-v3-turbo');
    expect(vaultStore).not.toHaveBeenCalled();
    expect(vaultDelete).not.toHaveBeenCalled();
  });

  it('select rejects a model that is not downloaded', () => {
    const store = provide();
    void store.select('transcription', 'whisper-large-v3');
    expect(store.selectedFor('transcription')).toBeNull();
  });

  it('selecting a voice model removed just before still resolves quietly, saves nothing and keeps the selection', async () => {
    const store = provide();
    store.markDownloaded('whisper-base');
    store.markDownloaded('whisper-large-v3-turbo');
    store.markRemoved('whisper-large-v3-turbo');
    vaultStore.mockClear();

    const outcome = await store.select('transcription', 'whisper-large-v3-turbo').then(
      () => 'resolved',
      (err: unknown) => (err as Error).message,
    );

    expect({
      outcome,
      saved: vaultStore.mock.calls,
      selected: store.selectedFor('transcription'),
    }).toEqual({ outcome: 'resolved', saved: [], selected: 'whisper-base' });
  });

  it('select rejects a model from a different kind', () => {
    const store = provide();
    store.markDownloaded('qwen-3-vl-2b');
    void store.select('transcription', 'qwen-3-vl-2b');
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

  it('downloadedFor only returns models matching the kind', async () => {
    const store = provide();
    await store.loadWhisperTable();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('qwen-3-vl-2b');
    expect(store.downloadedFor('transcription').map((m) => m.id)).toEqual([
      'whisper-large-v3-turbo',
    ]);
    expect(store.downloadedFor('analysis').map((m) => m.id)).toEqual(['qwen-3-vl-2b']);
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

  it('a selection stored as a catalog id is still the model in use after a reload', () => {
    window.localStorage.setItem(
      LOCAL_MODELS_STORAGE_KEY,
      JSON.stringify({
        downloaded: ['gemma-3-4b', 'qwen-3-vl-2b'],
        selected: { transcription: null, analysis: 'qwen-3-vl-2b' },
      }),
    );

    const store = provide();

    expect({
      selected: store.selectedFor('analysis'),
      title: store.lookup(store.selectedFor('analysis')!)?.title,
      downloaded: store.downloadedFor('analysis').map((m) => m.id),
    }).toEqual({
      selected: 'qwen-3-vl-2b',
      title: 'Qwen 2.5 3B',
      downloaded: ['qwen-3-vl-2b', 'gemma-3-4b'],
    });
  });

  it('totalDownloadedBytes sums the exact sizes of main table for the downloaded Whisper models', async () => {
    const store = provide();
    await store.loadWhisperTable();
    expect(store.totalDownloadedBytes()).toBe(0);
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-base');
    expect(store.totalDownloadedBytes()).toBe(1_624_555_275 + 147_951_465);
  });
});

function provideWithBridge(bridgePartial: Partial<ScoutBridgeService>): LocalModelsStore {
  const bridge = {
    vaultStore: vi.fn(async () => undefined),
    vaultHas: vi.fn(async () => ({ exists: false })),
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

/** Ollama's list answer for the given tags, as `scout:llm.listOllamaModels` resolves. */
function listed(...tags: readonly string[]) {
  return { models: tags.map((name) => ({ name, modifiedAt: '', sizeBytes: 0 })) };
}

function seed(downloaded: readonly string[], analysis: string | null = null): void {
  window.localStorage.setItem(
    LOCAL_MODELS_STORAGE_KEY,
    JSON.stringify({ downloaded, selected: { transcription: null, analysis } }),
  );
}

function persistedDownloads(): readonly string[] {
  return (
    (
      JSON.parse(window.localStorage.getItem(LOCAL_MODELS_STORAGE_KEY) ?? '{}') as {
        downloaded?: string[];
      }
    ).downloaded ?? []
  );
}

describe('refreshOllama lists exactly what Ollama reports', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  const rows: readonly {
    name: string;
    before: readonly string[];
    reports: readonly string[];
    after: readonly string[];
  }[] = [
    {
      name: 'a catalog model Ollama lists joins the list, and the voice models stay',
      before: ['whisper-large-v3-turbo'],
      reports: ['qwen2.5:3b'],
      after: ['whisper-large-v3-turbo', 'qwen-3-vl-2b'],
    },
    {
      name: 'a model removed outside Scout leaves the list',
      before: ['gemma-3-4b', 'qwen-3-vl-2b'],
      reports: ['gemma3:4b'],
      after: ['gemma-3-4b'],
    },
    {
      name: 'an Ollama that answers with no model leaves no Ollama model downloaded',
      before: ['whisper-base', 'gemma-3-4b', 'nomic-embed-text'],
      reports: [],
      after: ['whisper-base'],
    },
    {
      name: 'a model outside the catalog is listed under its tag',
      before: ['gemma-3-4b'],
      reports: ['sample-chat:1b'],
      after: ['sample-chat:1b'],
    },
    {
      name: 'only models outside the catalog: each is listed under its tag, as Ollama orders them',
      before: [],
      reports: ['sample-chat:7b', 'sample-coder:latest'],
      after: ['sample-chat:7b', 'sample-coder:latest'],
    },
    {
      name: 'catalog and outside models mixed: the catalog ones under their catalog id, the others under their tag',
      before: ['whisper-base'],
      reports: ['sample-chat:7b', 'gemma3:4b', 'sample-chat:latest'],
      after: ['whisper-base', 'sample-chat:7b', 'gemma-3-4b', 'sample-chat:latest'],
    },
    {
      name: 'a model already in the list is counted once',
      before: ['qwen-3-vl-2b'],
      reports: ['qwen2.5:3b'],
      after: ['qwen-3-vl-2b'],
    },
    {
      name: 'the skill-matching model listed as nomic-embed-text:latest counts as installed',
      before: ['gemma-3-4b'],
      reports: ['gemma3:4b', 'nomic-embed-text:latest'],
      after: ['gemma-3-4b', 'nomic-embed-text'],
    },
    {
      name: 'the skill-matching model listed as nomic-embed-text counts as installed',
      before: ['gemma-3-4b'],
      reports: ['gemma3:4b', 'nomic-embed-text'],
      after: ['gemma-3-4b', 'nomic-embed-text'],
    },
    {
      name: 'only another tag of the skill-matching model counts as missing, and that tag is listed as a model of its own',
      before: ['gemma-3-4b', 'nomic-embed-text'],
      reports: ['gemma3:4b', 'nomic-embed-text:v1.5'],
      after: ['gemma-3-4b', 'nomic-embed-text:v1.5'],
    },
  ];

  for (const { name, before, reports, after } of rows) {
    it(name, async () => {
      seed(before, 'gemma-3-4b');
      const llmListOllamaModels = vi.fn(async () => listed(...reports));
      const store = provideWithBridge({ llmListOllamaModels } as Partial<ScoutBridgeService>);

      await store.refreshOllama();

      expect({
        asked: llmListOllamaModels.mock.calls,
        answers: store.ollamaAnswers(),
        downloaded: store.downloaded(),
        persisted: persistedDownloads(),
      }).toEqual({
        asked: [[{ endpoint: 'http://localhost:11434' }]],
        answers: true,
        downloaded: after,
        persisted: after,
      });
    });
  }

  it('when the Ollama list call fails, ollamaAnswers is false and the last list is kept', async () => {
    const llmListOllamaModels = vi
      .fn()
      .mockResolvedValueOnce(listed('gemma3:4b', 'nomic-embed-text'))
      .mockRejectedValueOnce(new OllamaUnreachableError('http://localhost:11434'));
    const store = provideWithBridge({ llmListOllamaModels } as Partial<ScoutBridgeService>);
    const beforeAnyRead = store.ollamaAnswers();
    await store.refreshOllama();
    const afterAnswer = store.downloaded();

    await store.refreshOllama();

    expect({
      beforeAnyRead,
      afterAnswer,
      answers: store.ollamaAnswers(),
      downloaded: store.downloaded(),
      persisted: persistedDownloads(),
    }).toEqual({
      beforeAnyRead: null,
      afterAnswer: ['gemma-3-4b', 'nomic-embed-text'],
      answers: false,
      downloaded: ['gemma-3-4b', 'nomic-embed-text'],
      persisted: ['gemma-3-4b', 'nomic-embed-text'],
    });
  });

  it('opening the dialog never chooses a model or writes llm.model: with no selection and two models listed, SELECTED is the dash and the vault is not written', async () => {
    seed([]);
    const vaultStore = vi.fn(async (_req: { key: string; value: string }) => undefined);
    const vaultHas = vi.fn(async () => ({ exists: false }));
    const store = provideWithBridge({
      vaultStore,
      vaultHas,
      llmListOllamaModels: vi.fn(async () => listed('qwen2.5:3b', 'gemma3:4b')),
    } as unknown as Partial<ScoutBridgeService>);

    await store.refreshOllama();
    await Promise.resolve();

    expect({
      downloaded: store.downloaded(),
      selected: store.selectedFor('analysis'),
      vaultWrites: vaultStore.mock.calls,
      persisted: (
        JSON.parse(window.localStorage.getItem(LOCAL_MODELS_STORAGE_KEY)!) as {
          selected: { analysis: string | null };
        }
      ).selected.analysis,
    }).toEqual({
      downloaded: ['qwen-3-vl-2b', 'gemma-3-4b'],
      selected: null,
      vaultWrites: [],
      persisted: null,
    });
  });

  it('with an Analysis selection already made, reading the list saves no llm.model', async () => {
    seed(['qwen-3-vl-2b'], 'qwen-3-vl-2b');
    const vaultStore = vi.fn(async () => undefined);
    const store = provideWithBridge({
      vaultStore,
      llmListOllamaModels: vi.fn(async () => listed('qwen2.5:3b')),
    } as unknown as Partial<ScoutBridgeService>);

    await store.refreshOllama();

    expect({ selected: store.selectedFor('analysis'), saved: vaultStore.mock.calls }).toEqual({
      selected: 'qwen-3-vl-2b',
      saved: [],
    });
  });
});

/** One model of Ollama's list with the details main read for it. */
function info(name: string, extra: Partial<OllamaModelInfo> = {}): OllamaModelInfo {
  return { name, modifiedAt: '', sizeBytes: 2_019_393_189, ...extra };
}

describe('every model the local Ollama lists can be chosen', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  function ollamaStore(models: readonly OllamaModelInfo[]) {
    const vaultStore = vi.fn(async (_req: { key: string; value: string }) => undefined);
    const llmListOllamaModels = vi.fn(async () => ({ models }));
    const store = provideWithBridge({
      vaultStore,
      llmListOllamaModels,
    } as unknown as Partial<ScoutBridgeService>);
    return { store, vaultStore, llmListOllamaModels };
  }

  it.each([
    { name: 'a model outside the catalog', key: 'sample-chat:7b', saved: 'sample-chat:7b' },
    {
      name: 'a model outside the catalog listed with :latest',
      key: 'sample-chat:latest',
      saved: 'sample-chat:latest',
    },
    { name: 'a recommended catalog model', key: 'qwen-3-vl-2b', saved: 'qwen2.5:3b' },
  ])(
    'select saves the raw tag of a model outside the catalog and the catalog tag of a recommended one: $name',
    async ({ key, saved }) => {
      const { store, vaultStore } = ollamaStore([
        info('qwen2.5:3b'),
        info('sample-chat:7b', { capabilities: ['completion', 'tools'] }),
        info('sample-chat:latest'),
      ]);
      await store.refreshOllama();

      await store.select('analysis', key);

      expect({ saved: vaultStore.mock.calls, selected: store.selectedFor('analysis') }).toEqual({
        saved: [[{ key: 'llm.model', value: saved }]],
        selected: key,
      });
    },
  );

  it('a model outside the catalog chosen before a reload is still the model in use, and its row is its tag', async () => {
    const first = ollamaStore([info('sample-chat:7b')]);
    await first.store.refreshOllama();
    await first.store.select('analysis', 'sample-chat:7b');

    TestBed.resetTestingModule();
    const again = provideWithBridge({} as Partial<ScoutBridgeService>);

    expect({
      selected: again.selectedFor('analysis'),
      rows: again.downloadedFor('analysis').map((m) => [m.id, m.title, m.kind]),
    }).toEqual({
      selected: 'sample-chat:7b',
      rows: [['sample-chat:7b', 'sample-chat:7b', 'analysis']],
    });
  });

  it.each([
    {
      name: 'capabilities say embedding without completion',
      model: info('sample-vectors:latest', { capabilities: ['embedding'] }),
      refused: true,
    },
    {
      name: 'capabilities unknown and the name says embed',
      model: info('sample-embed-large:latest', { capabilities: null }),
      refused: true,
    },
    {
      name: 'capabilities unknown and the family is a bert',
      model: info('sample-vectors:335m', { family: 'nomic-bert', capabilities: null }),
      refused: true,
    },
    {
      name: 'a chat model whose capabilities are unknown stays selectable',
      model: info('sample-chat:7b', { family: 'llama', capabilities: null }),
      refused: false,
    },
    {
      name: 'capabilities that list completion next to embedding stay selectable',
      model: info('sample-embed-chat:7b', { capabilities: ['completion', 'embedding'] }),
      refused: false,
    },
  ])(
    'select refuses an embeddings-only model and writes nothing: $name',
    async ({ model, refused }) => {
      const { store, vaultStore } = ollamaStore([model]);
      await store.refreshOllama();

      const outcome = await store.select('analysis', model.name).then(
        () => 'chosen',
        (err: unknown) => (err as Error).message,
      );

      expect({
        outcome,
        embeddingsOnly: store.isEmbeddingsOnly(model.name),
        saved: vaultStore.mock.calls.length,
        selected: store.selectedFor('analysis'),
      }).toEqual(
        refused
          ? {
              outcome: `${model.name} only turns text into embeddings; it cannot answer in chat.`,
              embeddingsOnly: true,
              saved: 0,
              selected: null,
            }
          : { outcome: 'chosen', embeddingsOnly: false, saved: 1, selected: model.name },
      );
    },
  );

  it('a second read asked while one is in flight sends no second request and both callers get the same answer', async () => {
    let answer!: (value: { models: OllamaModelInfo[] }) => void;
    const llmListOllamaModels = vi
      .fn()
      .mockImplementationOnce(
        () => new Promise<{ models: OllamaModelInfo[] }>((resolve) => (answer = resolve)),
      )
      .mockResolvedValue({ models: [info('sample-chat:7b')] });
    const store = provideWithBridge({
      llmListOllamaModels,
    } as unknown as Partial<ScoutBridgeService>);

    const first = store.refreshOllama();
    const second = store.refreshOllama();
    const reopened = store.openOllama();
    const whileReading = store.ollamaAnswers();
    answer({ models: [info('sample-chat:7b')] });
    const results = await Promise.all([first, second, reopened]);
    const third = await store.refreshOllama();

    expect({
      requests: llmListOllamaModels.mock.calls.length,
      whileReading,
      results,
      third,
      downloaded: store.downloaded(),
    }).toEqual({
      requests: 2,
      whileReading: null,
      results: [true, true, true],
      third: true,
      downloaded: ['sample-chat:7b'],
    });
  });

  it('a list read that fails changes nothing and reports false', async () => {
    seed(['whisper-base'], 'sample-chat:7b');
    const llmListOllamaModels = vi
      .fn()
      .mockResolvedValueOnce({
        models: [
          info('sample-chat:7b', { family: 'llama', parameterSize: '7B', quantization: 'Q4_0' }),
          info('gemma3:4b'),
        ],
      })
      .mockRejectedValueOnce(new OllamaUnreachableError('http://localhost:11434'));
    const store = provideWithBridge({ llmListOllamaModels } as Partial<ScoutBridgeService>);
    const answered = await store.refreshOllama();
    const before = {
      downloaded: store.downloaded(),
      details: store.detailsOf('sample-chat:7b'),
      selected: store.selectedFor('analysis'),
    };

    const failed = await store.refreshOllama();

    expect({
      answered,
      failed,
      answers: store.ollamaAnswers(),
      after: {
        downloaded: store.downloaded(),
        details: store.detailsOf('sample-chat:7b'),
        selected: store.selectedFor('analysis'),
      },
      persisted: persistedDownloads(),
    }).toEqual({
      answered: true,
      failed: false,
      answers: false,
      after: before,
      persisted: ['whisper-base', 'sample-chat:7b', 'gemma-3-4b'],
    });
    expect(before).toEqual({
      downloaded: ['whisper-base', 'sample-chat:7b', 'gemma-3-4b'],
      details: 'llama · 7B · Q4_0 · 1.9 GB on disk',
      selected: 'sample-chat:7b',
    });
  });

  it.each([
    {
      name: 'every detail known',
      model: info('sample-chat:3b', {
        family: 'llama',
        parameterSize: '3.2B',
        quantization: 'Q4_K_M',
      }),
      line: 'llama · 3.2B · Q4_K_M · 1.9 GB on disk',
    },
    { name: 'no details reported', model: info('sample-chat:3b'), line: '1.9 GB on disk' },
    {
      name: 'no size reported',
      model: info('sample-chat:3b', { family: 'llama', sizeBytes: 0 }),
      line: 'llama',
    },
  ])(
    'a model outside the catalog says its family, parameters, quantization and size on disk, each when known: $name',
    async ({ model, line }) => {
      const { store } = ollamaStore([model]);
      await store.refreshOllama();

      expect(store.detailsOf(model.name)).toBe(line);
    },
  );

  it('Removing the selected model when only an embeddings-only model is left clears the selection and saves no llm.model', async () => {
    const { store, vaultStore } = ollamaStore([
      info('sample-chat:7b'),
      info('sample-vectors:latest', { capabilities: ['embedding'] }),
    ]);
    await store.refreshOllama();
    await store.select('analysis', 'sample-chat:7b');
    vaultStore.mockClear();

    store.markRemoved('sample-chat:7b');
    await Promise.resolve();

    expect({ selected: store.selectedFor('analysis'), saved: vaultStore.mock.calls }).toEqual({
      selected: null,
      saved: [],
    });
  });
});

describe('LocalModelsStore writes only the settings it owns', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  function vaultBridge(saved: Record<string, string>, ollamaTags: readonly string[] = []) {
    const vault = new Map(Object.entries(saved));
    const sttWrites: string[] = [];
    const bridge = {
      vaultStore: vi.fn(async (req: { key: string; value: string }) => {
        if (req.key === 'stt.model') sttWrites.push(`store ${req.value}`);
        vault.set(req.key, req.value);
      }),
      vaultHas: vi.fn(async (req: { key: string }) => ({ exists: vault.has(req.key) })),
      vaultDelete: vi.fn(async (req: { key: string }) => {
        if (req.key === 'stt.model') sttWrites.push('delete');
        vault.delete(req.key);
        return { deleted: true };
      }),
      whisperModelTable: vi.fn(async () => ({
        models: [
          { variant: 'tiny', file: 'ggml-tiny.bin', size: 77_691_713 },
          { variant: 'base', file: 'ggml-base.bin', size: 147_951_465 },
          { variant: 'large-v3-turbo', file: 'ggml-large-v3-turbo.bin', size: 1_624_555_275 },
        ],
      })),
      llmListOllamaModels: vi.fn(async () => listed(...ollamaTags)),
    };
    return { vault, sttWrites, store: provideWithBridge(bridge as Partial<ScoutBridgeService>) };
  }

  const settle = async () => {
    for (let i = 0; i < 5; i++) await Promise.resolve();
  };

  it('A first voice download in a window with no saved choice never writes stt.model from the renderer', async () => {
    const { store, sttWrites } = vaultBridge({ 'stt.model': 'whisper-small' });
    await store.loadWhisperTable();

    store.markDownloaded('whisper-base');
    await settle();

    expect(sttWrites).toEqual([]);
  });

  it('Removing the voice model the window had selected never writes stt.model from the renderer', async () => {
    window.localStorage.setItem(
      LOCAL_MODELS_STORAGE_KEY,
      JSON.stringify({
        downloaded: ['whisper-base', 'whisper-tiny', 'whisper-large-v3-turbo'],
        selected: { transcription: 'whisper-base', analysis: null },
      }),
    );
    const { store, sttWrites } = vaultBridge({ 'stt.model': 'whisper-base' });
    await store.loadWhisperTable();

    store.markRemoved('whisper-base');
    await settle();

    expect(sttWrites).toEqual([]);
  });

  it('Opening the models screen with an empty window copy never overwrites the saved llm.model', async () => {
    const { store, vault } = vaultBridge({ 'llm.model': 'gemma3:4b' }, ['qwen2.5:3b', 'gemma3:4b']);

    await store.refreshOllama();
    await settle();

    expect(vault.get('llm.model')).toBe('gemma3:4b');
  });

  it('Selecting an Ollama model whose llm.model cannot be saved rejects with the reason and keeps the previous selection', async () => {
    const { store } = vaultBridge({});
    store.markDownloaded('qwen-3-vl-2b');
    store.markDownloaded('gemma-3-4b');
    await settle();
    vi.mocked(TestBed.inject(ScoutBridgeService).vaultStore).mockRejectedValueOnce(
      new Error('[scout:vault/unavailable] the keyring is locked'),
    );

    const outcome = await store.select('analysis', 'gemma-3-4b').then(
      () => 'saved',
      (err: unknown) => (err as Error).message,
    );

    expect({ outcome, selected: store.selectedFor('analysis') }).toEqual({
      outcome: '[scout:vault/unavailable] the keyring is locked',
      selected: 'qwen-3-vl-2b',
    });
  });

  it('Selecting an Ollama model that is no longer downloaded rejects with a sentence that says so, saves no llm.model and keeps the previous selection', async () => {
    const { store, vault } = vaultBridge({});
    store.markDownloaded('qwen-3-vl-2b');
    store.markDownloaded('gemma-3-4b');
    await settle();
    store.markRemoved('gemma-3-4b');
    await settle();

    const outcome = await store.select('analysis', 'gemma-3-4b').then(
      () => 'saved',
      (err: unknown) => (err as Error).message,
    );

    expect({
      outcome,
      saved: vault.get('llm.model'),
      selected: store.selectedFor('analysis'),
    }).toEqual({
      outcome: 'Gemma 3 4B is no longer on this computer.',
      saved: 'qwen2.5:3b',
      selected: 'qwen-3-vl-2b',
    });
  });

  it('Removing the selected Ollama model moves llm.model to the model the selection moved to', async () => {
    const { store, vault } = vaultBridge({});
    store.markDownloaded('qwen-3-vl-2b');
    store.markDownloaded('gemma-3-4b');
    await settle();
    const before = vault.get('llm.model');

    store.markRemoved('qwen-3-vl-2b');
    await settle();

    expect({
      before,
      after: vault.get('llm.model'),
      selected: store.selectedFor('analysis'),
    }).toEqual({ before: 'qwen2.5:3b', after: 'gemma3:4b', selected: 'gemma-3-4b' });
  });
});

describe('the skill-matching model', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  it('download pulls the tag nomic-embed-text and marks it downloaded without changing the Analysis selection or the stored chat model', async () => {
    const llmPullOllamaModel = vi.fn(async (req: { tag: string }) => ({ tag: req.tag }));
    const vaultStore = vi.fn(async () => undefined);
    const store = provideWithBridge({
      llmPullOllamaModel,
      vaultStore,
      onLlmPullOllamaProgress: vi.fn(() => () => undefined),
      onWhisperDownloadProgress: vi.fn(() => () => undefined),
    } as unknown as Partial<ScoutBridgeService>);

    await store.download('nomic-embed-text');

    expect({
      tags: llmPullOllamaModel.mock.calls.map(([req]) => req.tag),
      downloaded: store.isDownloaded('nomic-embed-text'),
      selected: store.selected(),
      chatModels: store.downloadedFor('analysis'),
      vaultWrites: vaultStore.mock.calls,
    }).toEqual({
      tags: ['nomic-embed-text'],
      downloaded: true,
      selected: { transcription: null, analysis: null },
      chatModels: [],
      vaultWrites: [],
    });
  });
});

/** A local Ollama whose list holds `installed` and every model a pull asks for, as Ollama names it. */
function fakeOllama(installed: readonly string[]) {
  const tags = [...installed];
  return {
    tags,
    vaultStore: vi.fn(async (_req: { key: string; value: string }) => undefined),
    llmListOllamaModels: vi.fn(async () => listed(...tags)),
    llmPullOllamaModel: vi.fn(async (req: { tag: string }) => {
      const name = req.tag.includes(':') ? req.tag : `${req.tag}:latest`;
      if (!tags.includes(name)) tags.push(name);
      return { tag: req.tag };
    }),
    onLlmPullOllamaProgress: vi.fn(() => () => undefined),
    onWhisperDownloadProgress: vi.fn(() => () => undefined),
  };
}

describe('a pull ends with Ollama list read again', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  it.each([
    ['sample-chat', 'sample-chat:latest'],
    ['sample-chat:3b', 'sample-chat:3b'],
    ['example-org/sample-chat', 'example-org/sample-chat:latest'],
    ['gemma3:4b', 'gemma-3-4b'],
    ['nomic-embed-text', 'nomic-embed-text'],
    ['nomic-embed-text:latest', 'nomic-embed-text'],
  ])('the name %s is kept under the key %s, the one the list read gives it', (name, key) => {
    expect(ollamaKeyForName(name)).toBe(key);
  });

  it('a Download whose pull ends while an older list read is in flight is listed by a read that starts after the pull', async () => {
    const ollama = fakeOllama([]);
    let answerOld!: (value: ReturnType<typeof listed>) => void;
    ollama.llmListOllamaModels.mockImplementationOnce(
      () => new Promise((resolve) => (answerOld = resolve)),
    );
    const store = provideWithBridge(ollama as unknown as Partial<ScoutBridgeService>);
    const olderRead = store.refreshOllama();

    const download = store.download('gemma-3-4b');
    for (let i = 0; i < 5; i++) await Promise.resolve();
    answerOld(listed());
    await olderRead;
    await download;

    expect({
      reads: ollama.llmListOllamaModels.mock.calls.length,
      downloaded: store.downloaded(),
    }).toEqual({ reads: 2, downloaded: ['gemma-3-4b'] });
  });

  it.each([
    {
      how: 'a Download of a catalog model with nothing chosen',
      id: 'gemma-3-4b',
      byName: false,
      installed: [],
      pulled: ['gemma3:4b'],
      selected: 'gemma-3-4b',
      saved: [[{ key: 'llm.model', value: 'gemma3:4b' }]],
    },
    {
      how: 'a pull by name of a catalog model with nothing chosen',
      id: 'gemma-3-4b',
      byName: true,
      installed: [],
      pulled: ['gemma3:4b'],
      selected: null,
      saved: [],
    },
    {
      how: 'a pull by name of a model outside the catalog with nothing chosen',
      id: 'sample-chat:3b',
      byName: true,
      installed: [],
      pulled: ['sample-chat:3b'],
      selected: null,
      saved: [],
    },
    {
      how: 'a pull by name of a model already installed',
      id: 'sample-chat:3b',
      byName: true,
      installed: ['sample-chat:3b'],
      pulled: ['sample-chat:3b'],
      selected: null,
      saved: [],
    },
    {
      how: 'a Download of a catalog model already installed',
      id: 'gemma-3-4b',
      byName: false,
      installed: ['gemma3:4b'],
      pulled: [],
      selected: null,
      saved: [],
    },
  ])(
    '$how: pulls $pulled, chooses $selected, and the model is listed after',
    async ({ id, byName, installed, pulled, selected, saved }) => {
      const ollama = fakeOllama(installed);
      const store = provideWithBridge(ollama as unknown as Partial<ScoutBridgeService>);
      await store.refreshOllama();

      await store.download(id, { byName });
      for (let i = 0; i < 5; i++) await Promise.resolve();

      expect({
        pulled: ollama.llmPullOllamaModel.mock.calls.map(([req]) => req.tag),
        listed: store.isDownloaded(id),
        selected: store.selectedFor('analysis'),
        saved: ollama.vaultStore.mock.calls,
      }).toEqual({ pulled, listed: true, selected, saved });
    },
  );
});

describe('Only main writes stt.model', () => {
  it('no renderer source stores or deletes stt.model through the vault', () => {
    const root = 'src/app';
    const writers = (readdirSync(root, { recursive: true }) as string[])
      .filter((file) => file.endsWith('.ts') && !file.endsWith('.spec.ts'))
      .filter((file) =>
        /vault(?:Store|Delete)\(\{\s*key:\s*'stt\.model'/.test(
          readFileSync(join(root, file), 'utf8'),
        ),
      );

    expect(writers).toEqual([]);
  });
});
