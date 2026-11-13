import { readFileSync } from 'node:fs';

import { InteractivityChecker } from '@angular/cdk/a11y';
import {
  EnvironmentInjector,
  provideZonelessChangeDetection,
  runInInjectionContext,
} from '@angular/core';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { OllamaUnreachableError, type OllamaModelInfo } from '@scout/shared';

import { LLM_CONFIG_STORAGE_KEY, LlmConfigStore } from '../core/llm-config.store';
import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { MAIN_WHISPER_TABLE, OTHER_WHISPER_TABLE } from '../core/whisper-table.testing';
import { LocalModelsComponent } from './local-models.component';

const LOAD_ORDER = ['large-v3-turbo', 'large-v3', 'small', 'base', 'tiny'];

interface FakeOllamaModel {
  name: string;
  modifiedAt: string;
  sizeBytes: number;
}

function ollamaModel(name: string): FakeOllamaModel {
  return { name, modifiedAt: '', sizeBytes: 0 };
}

/** A main that keeps the installed Whisper models and the saved choice, and reports in use as `chooseWhisperModel` picks. */
function makeFakeBridge(whisper: { installed?: readonly string[]; saved?: string } = {}) {
  let installed = [...(whisper.installed ?? [])];
  let saved = whisper.saved ?? null;
  return {
    llmListOllamaModels: vi.fn(async (): Promise<{ models: FakeOllamaModel[] }> => ({
      models: [],
    })),
    llmPullOllamaModel: vi.fn(async (req: { tag: string }) => ({ tag: req.tag })),
    onLlmPullOllamaProgress: vi.fn(() => () => undefined),
    llmDeleteOllamaModel: vi.fn(async (req: { tag: string }) => ({ tag: req.tag })),
    llmListLmStudioModels: vi.fn(async () => ({ models: [] })),
    whisperListInstalled: vi.fn(
      async (): Promise<{
        installed: readonly { variant: string; verified: boolean }[];
        inUse: string | null;
        preferred: string | null;
      }> => ({
        installed: installed.map((variant) => ({ variant, verified: true })),
        inUse:
          saved !== null && installed.includes(saved)
            ? saved
            : (LOAD_ORDER.find((v) => installed.includes(v)) ?? null),
        preferred: saved,
      }),
    ),
    whisperModelTable: vi.fn(async (): Promise<typeof MAIN_WHISPER_TABLE> => MAIN_WHISPER_TABLE),
    whisperUseModel: vi.fn(async (req: { variant: string }) => {
      saved = req.variant;
      return { variant: req.variant, restart: 'none' };
    }),
    whisperDownloadModel: vi.fn(async (req: { variant: string }) => {
      installed.push(req.variant);
      return { variant: req.variant, path: `/tmp/${req.variant}.bin` };
    }),
    onWhisperDownloadProgress: vi.fn(() => () => undefined),
    whisperDeleteModel: vi.fn(async (req: { variant: string }) => {
      installed = installed.filter((variant) => variant !== req.variant);
      return { variant: req.variant };
    }),
    appOpenExternal: vi.fn(async () => undefined),
    onWorkerEvent: vi.fn(() => () => undefined),
    workerStatus: vi.fn(async () => ({ state: 'idle', restarts: 0 })),
  };
}

/** jsdom lays nothing out, so the CDK focus trap would find no visible button; this checker answers as a browser would for an enabled one. */
const LAID_OUT: Pick<
  InteractivityChecker,
  'isDisabled' | 'isVisible' | 'isTabbable' | 'isFocusable'
> = {
  isDisabled: (el: HTMLElement) => el.hasAttribute('disabled'),
  isVisible: () => true,
  isTabbable: (el: HTMLElement) => !el.hasAttribute('disabled') && el.tabIndex >= 0,
  isFocusable: (el: HTMLElement) => !el.hasAttribute('disabled') && el.tabIndex >= 0,
};

async function setup(
  bridge = makeFakeBridge(),
  kind?: 'analysis',
): Promise<{
  fixture: ComponentFixture<LocalModelsComponent>;
  store: LocalModelsStore;
  bridge: ReturnType<typeof makeFakeBridge>;
}> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
    ],
  });
  const store = TestBed.inject(LocalModelsStore);
  const fixture = TestBed.createComponent(LocalModelsComponent);
  if (kind !== undefined) {
    fixture.componentRef.setInput('kind', kind);
    fixture.componentRef.setInput('embedded', true);
  }
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, store, bridge };
}

async function answer(test: 'confirm-ok' | 'confirm-cancel'): Promise<void> {
  const button = document.querySelector<HTMLButtonElement>(`[data-test="${test}"]`);
  if (button === null) throw new Error('the confirmation dialog did not open');
  button.click();
  await Promise.resolve();
}

describe('LocalModelsComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  it('renders Transcription tab by default with both Whisper rows', async () => {
    const { fixture } = await setup();
    expect(
      fixture.nativeElement.querySelector('[data-test="row-whisper-large-v3"]'),
    ).not.toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-test="row-whisper-large-v3-turbo"]'),
    ).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="row-qwen-3-vl-2b"]')).toBeNull();
  });

  it('offers Whisper tiny, base and small next to the large models, with their sizes, and marks base recommended for live transcription', async () => {
    const { fixture } = await setup();
    const row = (id: string) =>
      fixture.nativeElement.querySelector(`[data-test="row-${id}"]`) as HTMLElement | null;
    const text = (id: string, part: string) =>
      row(id)?.querySelector(part)?.textContent?.replace(/\s+/g, ' ').trim();

    expect(
      ['whisper-tiny', 'whisper-base', 'whisper-small'].map((id) => text(id, '.row-meta')),
    ).toEqual([
      '74 MB on disk · 273 MB RAM',
      '141 MB on disk · 388 MB RAM',
      '465 MB on disk · 852 MB RAM',
    ]);
    expect(text('whisper-base', '.row-name')).toBe('Whisper Base');
    expect(text('whisper-base', '.tag--recommended')).toBe('Recommended');
    expect(text('whisper-base', '.row-desc')).toBe(
      'Recommended for live transcription: words appear within about a second on most computers.',
    );
    expect(
      ['whisper-tiny', 'whisper-small', 'whisper-large-v3', 'whisper-large-v3-turbo'].filter((id) =>
        row(id)?.querySelector('.tag--recommended'),
      ),
    ).toEqual([]);
  });

  it('marks only the Whisper models main verified as downloaded', async () => {
    const bridge = makeFakeBridge();
    bridge.whisperListInstalled.mockResolvedValueOnce({
      installed: [
        { variant: 'base', verified: true },
        { variant: 'small', verified: false },
      ],
      inUse: 'base',
      preferred: null,
    });

    const { store } = await setup(bridge);

    expect(store.isDownloaded('whisper-base')).toBe(true);
    expect(store.isDownloaded('whisper-small')).toBe(false);
  });

  it('downloads Whisper base as the base variant', async () => {
    const { fixture, bridge } = await setup();

    (
      fixture.nativeElement.querySelector(
        '[data-test="download-whisper-base"]',
      ) as HTMLButtonElement
    ).click();
    await fixture.whenStable();

    expect(bridge.whisperDownloadModel).toHaveBeenCalledTimes(1);
    expect(bridge.whisperDownloadModel.mock.calls[0]![0]).toMatchObject({ variant: 'base' });
  });

  it('the Ollama view (kind analysis) renders the five Ollama rows', async () => {
    const { fixture } = await setup();
    fixture.componentRef.setInput('kind', 'analysis');
    fixture.detectChanges();

    const expected = [
      'qwen-2.5-0.5b-instruct',
      'qwen-2.5-1.5b-instruct',
      'qwen-3-vl-2b',
      'gemma-3-4b',
      'gemma-3-12b',
    ];
    for (const id of expected) {
      expect(
        fixture.nativeElement.querySelector(`[data-test="row-${id}"]`),
        `row ${id} should render on Analysis tab`,
      ).not.toBeNull();
    }
  });

  it('clicking Download shows a progress bar then resolves to the selected check', async () => {
    const { fixture, store } = await setup();
    const dl = fixture.nativeElement.querySelector(
      '[data-test="download-whisper-large-v3-turbo"]',
    ) as HTMLButtonElement;
    dl.click();
    fixture.detectChanges();

    // Progress UI appears immediately and Download button is gone.
    expect(
      fixture.nativeElement.querySelector('[data-test="progress-whisper-large-v3-turbo"]'),
    ).not.toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-test="download-whisper-large-v3-turbo"]'),
    ).toBeNull();

    // Simulate completion by marking the store directly; covers the post-download UI.
    store.markDownloaded('whisper-large-v3-turbo');
    fixture.detectChanges();

    expect(store.selectedFor('transcription')).toBe('whisper-large-v3-turbo');
  });

  it('a downloaded but non-selected model shows Use this and selecting it switches the active model once main saved it', async () => {
    const { fixture, store, bridge } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-large-v3');
    fixture.detectChanges();

    const useBtn = fixture.nativeElement.querySelector(
      '[data-test="select-whisper-large-v3"]',
    ) as HTMLButtonElement;
    expect(useBtn).not.toBeNull();
    useBtn.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.whisperUseModel.mock.calls).toEqual([[{ variant: 'large-v3' }]]);
    expect(store.selectedFor('transcription')).toBe('whisper-large-v3');
  });

  it('Use this on an Ollama model removed just before it is clicked says on its row that it is no longer on this computer, saves nothing, and never hands it on to switch the provider or restart Scout', async () => {
    const bridge = Object.assign(makeFakeBridge(), {
      vaultStore: vi.fn(async (_req: { key: string; value: string }) => undefined),
      workerRestart: vi.fn(async () => ({ outcome: 'now' as const })),
    });
    const { fixture, store } = await setup(bridge);
    fixture.componentRef.setInput('kind', 'analysis');
    store.markDownloaded('qwen-3-vl-2b');
    store.markDownloaded('gemma-3-4b');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const handedOn: string[] = [];
    fixture.componentInstance.ollamaModelChosen.subscribe((id) => handedOn.push(id));
    bridge.vaultStore.mockClear();
    const use = fixture.nativeElement.querySelector(
      '[data-test="select-gemma-3-4b"]',
    ) as HTMLButtonElement | null;

    store.markRemoved('gemma-3-4b');
    use?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    const error = fixture.nativeElement.querySelector(
      '[data-test="error-gemma-3-4b"]',
    ) as HTMLElement | null;
    expect({
      useOffered: use !== null,
      role: error?.getAttribute('role'),
      error: error?.querySelector(':scope > span')?.textContent?.trim(),
      seeLogs: error?.querySelector('[data-test="goto-logs"]')?.getAttribute('href'),
      handedOn,
      saved: bridge.vaultStore.mock.calls,
      restartAsked: bridge.workerRestart.mock.calls,
      selected: store.selectedFor('analysis'),
    }).toEqual({
      useOffered: true,
      role: 'alert',
      error: 'Gemma 3 4B is no longer on this computer.',
      seeLogs: '/logs',
      handedOn: [],
      saved: [],
      restartAsked: [],
      selected: 'qwen-3-vl-2b',
    });
  });

  it('records and displays Ollama pull errors per row', async () => {
    const bridge = makeFakeBridge();
    bridge.llmPullOllamaModel = vi.fn(async () => {
      throw new Error('pull model manifest: open id_ed25519: no such file or directory');
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(LocalModelsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    // Switch to Analysis tab and click Download on qwen-3-vl-2b.
    fixture.componentRef.setInput('kind', 'analysis');
    fixture.detectChanges();
    (
      fixture.nativeElement.querySelector(
        '[data-test="download-qwen-3-vl-2b"]',
      ) as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    await Promise.resolve();
    await fixture.whenStable();
    fixture.detectChanges();

    const err = fixture.nativeElement.querySelector(
      '[data-test="error-qwen-3-vl-2b"]',
    ) as HTMLElement;
    expect(err).not.toBeNull();
    expect(err.textContent ?? '').toContain('id_ed25519');
  });

  it('downloaded rows expose a Remove button that drops the model from the store', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    fixture.detectChanges();

    const rm = fixture.nativeElement.querySelector(
      '[data-test="remove-whisper-large-v3-turbo"]',
    ) as HTMLButtonElement;
    expect(rm).not.toBeNull();
    rm.click();
    await fixture.whenStable();
    await answer('confirm-ok');
    await fixture.whenStable();
    await Promise.resolve();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(store.isDownloaded('whisper-large-v3-turbo')).toBe(false);
    expect(store.selectedFor('transcription')).toBeNull();
  });

  it('Delete unused button is disabled while everything downloaded is selected', async () => {
    const { fixture } = await setup(makeFakeBridge({ installed: ['large-v3-turbo'] }));

    const btn = fixture.nativeElement.querySelector(
      '[data-test="delete-unused"]',
    ) as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
  });

  describe('Delete unused asks first', () => {
    const UNUSED = {
      'one unused model': ['whisper-large-v3'],
      'several unused models': ['whisper-large-v3', 'whisper-base', 'whisper-tiny'],
    } as const;

    for (const [count, unused] of Object.entries(UNUSED)) {
      for (const choice of ['confirm-ok', 'confirm-cancel'] as const) {
        it(`with ${count}, Delete unused opens "Delete unused models?" with Cancel focused; ${choice === 'confirm-ok' ? 'confirming deletes the files of the unused models through main and keeps only the model in use' : 'Cancel deletes no file and keeps every model'}`, async () => {
          const { fixture, store, bridge } = await setup(
            makeFakeBridge({
              installed: ['large-v3-turbo', ...unused.map((id) => id.replace('whisper-', ''))],
            }),
          );

          (
            fixture.nativeElement.querySelector('[data-test="delete-unused"]') as HTMLButtonElement
          ).click();
          for (let i = 0; i < 2; i++) {
            await fixture.whenStable();
            fixture.detectChanges();
          }
          const title = document.querySelector('sc-confirm-dialog h2')?.textContent?.trim();
          const body = document.querySelector('sc-confirm-dialog .body')?.textContent?.trim();
          const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
          await answer(choice);
          for (let i = 0; i < 4; i++) {
            await fixture.whenStable();
            await Promise.resolve();
          }

          expect({ title, body, focused }).toEqual({
            title: 'Delete unused models?',
            body: 'Scout deletes the voice models that are not in use from this computer. The model in use stays.',
            focused: 'confirm-cancel',
          });
          expect(bridge.whisperDeleteModel.mock.calls.map(([req]) => req.variant)).toEqual(
            choice === 'confirm-ok' ? unused.map((id) => id.replace('whisper-', '')) : [],
          );
          expect([...store.downloaded()].sort()).toEqual(
            choice === 'confirm-ok'
              ? ['whisper-large-v3-turbo']
              : ['whisper-large-v3-turbo', ...unused].sort(),
          );
        });
      }
    }
  });

  it('Delete unused, when main fails to delete one file, shows its reason on that row and keeps it, and deletes the others', async () => {
    const bridge = makeFakeBridge({ installed: ['base', 'tiny', 'large-v3-turbo'] });
    const main = bridge.whisperDeleteModel.getMockImplementation()!;
    bridge.whisperDeleteModel.mockImplementation(async (req: { variant: string }) => {
      if (req.variant === 'base') {
        throw new Error('[scout:whisper/delete] EBUSY: the file is in use');
      }
      return main(req);
    });
    const { fixture, store } = await setup(bridge);

    (
      fixture.nativeElement.querySelector('[data-test="delete-unused"]') as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    await answer('confirm-ok');
    for (let i = 0; i < 4; i++) {
      await fixture.whenStable();
      await Promise.resolve();
    }
    fixture.detectChanges();

    expect(bridge.whisperDeleteModel.mock.calls.map(([req]) => req.variant)).toEqual([
      'base',
      'tiny',
    ]);
    expect([...store.downloaded()].sort()).toEqual(['whisper-base', 'whisper-large-v3-turbo']);
    expect(
      (
        fixture.nativeElement.querySelector(
          '[data-test="error-whisper-base"]',
        ) as HTMLElement | null
      )?.textContent,
    ).toContain('EBUSY: the file is in use');
    expect(fixture.nativeElement.querySelector('[data-test="row-whisper-base"]')).not.toBeNull();
  });

  it('Delete unused counts only the voice models: an unused Ollama model leaves it off and is never deleted from here', async () => {
    const { fixture, store, bridge } = await setup(makeFakeBridge({ installed: ['base'] }));
    store.markDownloaded('qwen-3-vl-2b');
    store.markDownloaded('gemma-3-4b');
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector(
      '[data-test="delete-unused"]',
    ) as HTMLButtonElement;

    expect(button.disabled).toBe(true);
    expect(bridge.llmDeleteOllamaModel).not.toHaveBeenCalled();
    expect(store.isDownloaded('gemma-3-4b')).toBe(true);
  });

  describe('In use showed base while the worker used large-v3-turbo', () => {
    describe('In use is the model Scout uses, as main reports it', () => {
      const ROWS = [
        {
          saved: 'base',
          installed: ['large-v3-turbo'],
          inUse: 'large-v3-turbo',
          note: 'base is selected but not downloaded; Scout uses large-v3-turbo.',
        },
        { saved: 'base', installed: ['base', 'large-v3-turbo'], inUse: 'base', note: '' },
        { saved: undefined, installed: ['tiny', 'small'], inUse: 'small', note: '' },
        {
          saved: 'base',
          installed: [],
          inUse: null,
          note: 'base is selected but not downloaded; no voice model is installed.',
        },
        { saved: undefined, installed: [], inUse: null, note: '' },
      ] as const;

      for (const { saved, installed, inUse, note } of ROWS) {
        it(`saved ${saved ?? 'nothing'}, installed ${installed.join(' and ') || 'none'}: In use ${inUse ?? 'nowhere'}${note === '' ? ', no note' : `, and "${note}"`}`, async () => {
          window.localStorage.setItem(
            LOCAL_MODELS_STORAGE_KEY,
            JSON.stringify({
              downloaded: ['whisper-base'],
              selected: { transcription: 'whisper-base', analysis: null },
            }),
          );
          const { fixture } = await setup(
            makeFakeBridge({ installed, ...(saved === undefined ? {} : { saved }) }),
          );
          const host = fixture.nativeElement as HTMLElement;

          expect(
            Array.from(host.querySelectorAll('[data-test^="check-whisper-"]'), (node) =>
              node.getAttribute('data-test')!.replace('check-whisper-', ''),
            ),
          ).toEqual(inUse === null ? [] : [inUse]);
          expect(
            host.querySelector('[data-test="whisper-preference-note"]')?.textContent?.trim() ?? '',
          ).toBe(note);
          expect(
            Array.from(host.querySelectorAll('[data-test^="downloaded-whisper-"]'), (node) =>
              node.getAttribute('data-test')!.replace('downloaded-whisper-', ''),
            ).sort(),
          ).toEqual(installed.filter((variant) => variant !== inUse).sort());
        });
      }
    });
  });

  describe('the lists show exactly the models main downloads', () => {
    function listed(fixture: ComponentFixture<LocalModelsComponent>): string[][] {
      return Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>(
          '[data-test^="row-whisper-"]',
        ),
        (row) => [
          row.getAttribute('data-test')!.replace('row-whisper-', ''),
          row.querySelector('.row-meta')!.textContent!.replace(/\s+/g, ' ').trim(),
        ],
      );
    }

    it('Models, Transcription lists main pinned table today, in its order, with its sizes', async () => {
      const { fixture } = await setup();

      expect(listed(fixture)).toEqual([
        ['tiny', '74 MB on disk · 273 MB RAM'],
        ['base', '141 MB on disk · 388 MB RAM'],
        ['small', '465 MB on disk · 852 MB RAM'],
        ['large-v3-turbo', '1.5 GB on disk · 3 GB RAM'],
        ['large-v3', '2.9 GB on disk · 4.7 GB RAM'],
      ]);
    });

    it('another table from main gives Models, Transcription its rows, order and sizes', async () => {
      const bridge = makeFakeBridge();
      bridge.whisperModelTable.mockResolvedValue(OTHER_WHISPER_TABLE);
      const { fixture } = await setup(bridge);

      expect(listed(fixture)).toEqual([
        ['large-v3', '2 GB on disk · 4.7 GB RAM'],
        ['small', '286 MB on disk · 852 MB RAM'],
        ['tiny', '48 MB on disk · 273 MB RAM'],
        ['base', '191 MB on disk · 388 MB RAM'],
      ]);
    });

    it('when main cannot give its Whisper table, Transcription says why and invents no rows', async () => {
      const bridge = makeFakeBridge();
      bridge.whisperModelTable.mockRejectedValue(
        new Error('[scout:ipc/unavailable] The main process did not answer.'),
      );
      const { fixture } = await setup(bridge);

      expect(
        (
          fixture.nativeElement.querySelector(
            '[data-test="whisper-table-error"]',
          ) as HTMLElement | null
        )?.textContent?.trim(),
      ).toBe('Scout could not list the voice models: The main process did not answer.');
      expect(listed(fixture)).toEqual([]);
    });
  });

  it('the Whisper view says it runs on this computer and never shows the Ollama server state', async () => {
    const bridge = makeFakeBridge();
    bridge.llmListOllamaModels.mockResolvedValue({ models: [] });
    const { fixture } = await setup(bridge);
    const status = fixture.nativeElement.querySelector('[data-test="panel-status"]') as HTMLElement;

    expect({
      status: status.textContent?.trim(),
      line: fixture.nativeElement.querySelector('[data-test="server-line"]'),
      asked: bridge.llmListOllamaModels.mock.calls,
    }).toEqual({ status: 'On this computer', line: null, asked: [] });
  });

  it('footer summary counts the downloaded voice models only', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('qwen-3-vl-2b');
    fixture.detectChanges();

    const foot = fixture.nativeElement.querySelector('[data-test="foot-summary"]') as HTMLElement;
    expect((foot.textContent ?? '').replace(/\s+/g, ' ')).toContain('1.5 GB · 1 model ');
  });

  it('polling_clearedOnDestroy', async () => {
    const setIntervalSpy = vi.spyOn(globalThis, 'setInterval');
    const clearIntervalSpy = vi.spyOn(globalThis, 'clearInterval');

    const { fixture } = await setup();
    expect(setIntervalSpy).toHaveBeenCalled();

    fixture.destroy();

    expect(clearIntervalSpy).toHaveBeenCalled();

    setIntervalSpy.mockRestore();
    clearIntervalSpy.mockRestore();
  });

  describe('the GPU metric reads real state', () => {
    function text(fixture: ComponentFixture<LocalModelsComponent>, test: string): string {
      return (
        (fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null)
          ?.textContent ?? ''
      )
        .replace(/\s+/g, ' ')
        .trim();
    }

    it('shows the GPU state read-only as not reported, and names where that comes from', async () => {
      const { fixture } = await setup();

      const gpu = fixture.nativeElement.querySelector('[data-test="metric-gpu"]') as HTMLElement;
      expect(text(fixture, 'metric-gpu-value')).toBe('Not reported');
      expect(text(fixture, 'metric-gpu-source')).toBe(
        'The Whisper runtime does not tell Scout whether it uses a GPU.',
      );
      expect(text(fixture, 'metric-gpu')).not.toContain('GPU off');
      expect(gpu.querySelector('button, input, [role="switch"]')).toBeNull();
    });
  });

  describe('removing a model', () => {
    const cases = [
      { kind: 'analysis', id: 'qwen-3-vl-2b', call: 'llmDeleteOllamaModel' },
      { kind: 'transcription', id: 'whisper-base', call: 'whisperDeleteModel' },
    ] as const;

    for (const { kind, id, call } of cases) {
      for (const fails of [false, true]) {
        it(`${kind}: when the delete ${fails ? 'fails, the row shows the error and the model stays downloaded' : 'succeeds, the model leaves the downloaded list with no error'}`, async () => {
          const bridge = makeFakeBridge();
          if (fails) bridge[call].mockRejectedValueOnce(new Error('Could not reach the server'));
          const { fixture, store } = await setup(bridge);
          if (kind === 'analysis') fixture.componentRef.setInput('kind', 'analysis');
          if (kind === 'analysis') store.markDownloaded('gemma-3-4b');
          store.markDownloaded(id);
          fixture.detectChanges();

          (
            fixture.nativeElement.querySelector(`[data-test="remove-${id}"]`) as HTMLButtonElement
          ).click();
          await fixture.whenStable();
          fixture.detectChanges();
          if (kind === 'analysis') {
            (
              fixture.nativeElement.querySelector(
                `[data-test="remove-ok-${id}"]`,
              ) as HTMLButtonElement
            ).click();
          } else {
            await answer('confirm-ok');
          }
          await Promise.resolve();
          await fixture.whenStable();
          fixture.detectChanges();

          const error = fixture.nativeElement.querySelector(
            `[data-test="error-${id}"]`,
          ) as HTMLElement | null;
          expect(bridge[call]).toHaveBeenCalledTimes(1);
          expect(store.isDownloaded(id)).toBe(fails);
          expect(error?.textContent ?? null).toEqual(
            fails ? expect.stringContaining('Could not reach the server') : null,
          );
        });
      }
    }
  });

  it('the Whisper view manages Whisper only, with the downloads footer and no breadcrumb, since it lives in Models', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="kind-analysis"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="row-gemma-3-4b"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="row-whisper-base"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="crumb-back"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="delete-unused"]')).not.toBeNull();
  });

  it('embedded in the Ollama card it shows the Ollama models and server without the breadcrumb or the footer', async () => {
    const { fixture } = await setup();
    fixture.componentRef.setInput('kind', 'analysis');
    fixture.componentRef.setInput('embedded', true);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="row-gemma-3-4b"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="server-panel"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="crumb-back"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="delete-unused"]')).toBeNull();
  });

  it('an Ollama download shows its progress as main reports it', async () => {
    const bridge = makeFakeBridge();
    let report: ((e: { requestId: string; percent: number }) => void) | null = null;
    bridge.onLlmPullOllamaProgress = vi.fn(
      (listener: (e: { requestId: string; percent: number }) => void) => {
        report = listener;
        return () => undefined;
      },
    ) as unknown as typeof bridge.onLlmPullOllamaProgress;
    let finish!: () => void;
    bridge.llmPullOllamaModel = vi.fn(
      (req: { tag: string }) =>
        new Promise<{ tag: string }>((r) => (finish = () => r({ tag: req.tag }))),
    );
    const { fixture } = await setup(bridge);
    fixture.componentRef.setInput('kind', 'analysis');
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="download-gemma-3-4b"]') as HTMLButtonElement
    ).click();
    const requestId = (
      bridge.llmPullOllamaModel.mock.calls[0]![0] as unknown as { requestId: string }
    ).requestId;
    report!({ requestId, percent: 42 });
    fixture.detectChanges();
    const progress =
      (fixture.nativeElement.querySelector('[data-test="progress-gemma-3-4b"]') as HTMLElement)
        .textContent ?? '';
    finish();
    await fixture.whenStable();

    expect(progress).toContain('42');
  });

  it('Remove asks first in the row, and Cancel keeps the downloaded model', async () => {
    const { fixture, store, bridge } = await setup();
    store.markDownloaded('qwen-2.5-0.5b-instruct');
    store.markDownloaded('gemma-3-4b');
    fixture.componentRef.setInput('kind', 'analysis');
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="remove-gemma-3-4b"]') as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    fixture.detectChanges();
    const ask = fixture.nativeElement.querySelector(
      '[data-test="remove-confirm-gemma-3-4b"]',
    ) as HTMLElement;
    const question = ask.querySelector('.confirm-text')?.textContent?.trim();
    (ask.querySelector('[data-test="remove-cancel-gemma-3-4b"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(ask.getAttribute('role')).toBe('alertdialog');
    expect(question).toBe('Remove Gemma 3 4B? 3.3 GB is deleted from this computer.');
    expect(
      fixture.nativeElement.querySelector('[data-test="remove-confirm-gemma-3-4b"]'),
    ).toBeNull();
    expect(bridge.llmDeleteOllamaModel).not.toHaveBeenCalled();
    expect(store.isDownloaded('gemma-3-4b')).toBe(true);
  });

  it('Remove confirmed deletes the Ollama model through main and drops it from the list', async () => {
    const { fixture, store, bridge } = await setup();
    store.markDownloaded('qwen-2.5-0.5b-instruct');
    store.markDownloaded('gemma-3-4b');
    fixture.componentRef.setInput('kind', 'analysis');
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="remove-gemma-3-4b"]') as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    fixture.detectChanges();
    (
      fixture.nativeElement.querySelector('[data-test="remove-ok-gemma-3-4b"]') as HTMLButtonElement
    ).click();
    for (let i = 0; i < 3; i += 1) await fixture.whenStable();

    expect(bridge.llmDeleteOllamaModel).toHaveBeenCalledWith(
      expect.objectContaining({ tag: 'gemma3:4b' }),
    );
    expect(store.isDownloaded('gemma-3-4b')).toBe(false);
  });
});

describe('Local Models follows the theme', () => {
  it('its styles paint only with the theme tokens, no hex, rgb() or hsl() colour left', () => {
    const source = readFileSync('src/app/sources/local-models.component.ts', 'utf8');
    const start = source.indexOf('  styles:');
    const css = source.slice(start, source.indexOf('\n})\n', start));

    expect(start).toBeGreaterThanOrEqual(0);
    expect(css).toContain('var(--sc-');
    expect(
      Array.from(source.matchAll(/#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(/g), (m) => m[0]),
    ).toEqual([]);
  });
});

describe('Local Models keeps the choices main owns and the downloads it runs', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  const settle = async (fixture: ComponentFixture<LocalModelsComponent>) => {
    for (let i = 0; i < 4; i++) {
      await fixture.whenStable();
      await Promise.resolve();
      fixture.detectChanges();
    }
  };

  /** Main reports base saved as the choice while the running worker still has tiny loaded, as during a deferred switch. */
  function deferredSwitchBridge() {
    const bridge = makeFakeBridge({ installed: ['tiny', 'base'], saved: 'base' });
    bridge.whisperListInstalled.mockImplementation(async () => ({
      installed: [
        { variant: 'tiny', verified: true },
        { variant: 'base', verified: true },
      ],
      inUse: 'tiny',
      preferred: 'base',
    }));
    return bridge;
  }

  it('Delete unused with a stale window selection among the unused never writes stt.model from the renderer', async () => {
    window.localStorage.setItem(
      LOCAL_MODELS_STORAGE_KEY,
      JSON.stringify({
        downloaded: [],
        selected: { transcription: 'whisper-base', analysis: null },
      }),
    );
    const sttWrites: string[] = [];
    const bridge = Object.assign(
      makeFakeBridge({ installed: ['base', 'tiny', 'large-v3-turbo'], saved: 'large-v3-turbo' }),
      {
        vaultStore: vi.fn(async (req: { key: string; value: string }) => {
          if (req.key === 'stt.model') sttWrites.push(`store ${req.value}`);
        }),
        vaultDelete: vi.fn(async (req: { key: string }) => {
          if (req.key === 'stt.model') sttWrites.push('delete');
          return { deleted: true };
        }),
      },
    );
    const { fixture } = await setup(bridge);

    (
      fixture.nativeElement.querySelector('[data-test="delete-unused"]') as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    await answer('confirm-ok');
    await settle(fixture);

    expect({
      deleted: bridge.whisperDeleteModel.mock.calls.map(([req]) => req.variant).sort(),
      sttWrites,
    }).toEqual({ deleted: ['base', 'tiny'], sttWrites: [] });
  });

  it('During a deferred switch the preference note never says the downloaded choice is not downloaded', async () => {
    const { fixture } = await setup(deferredSwitchBridge());

    expect(
      (fixture.nativeElement as HTMLElement)
        .querySelector('[data-test="whisper-preference-note"]')
        ?.textContent?.trim() ?? '',
    ).not.toContain('not downloaded');
  });

  it('Use this whose restart fails says the choice is saved and Scout starts with it next time', async () => {
    const bridge = makeFakeBridge({ installed: ['tiny', 'base'], saved: 'tiny' });
    bridge.whisperUseModel.mockImplementation(async () => {
      throw new Error('[scout:whisper/restart-failed] spawn ENOENT Scout.Host.Console');
    });
    const { fixture } = await setup(bridge);

    (
      fixture.nativeElement.querySelector('[data-test="select-whisper-base"]') as HTMLButtonElement
    ).click();
    await settle(fixture);

    expect((fixture.nativeElement as HTMLElement).textContent ?? '').toMatch(
      /Saved; Scout starts with [^.]*base[^.]* next time/i,
    );
  });

  it('Leaving Models during a voice download and coming back shows the download in progress, not Download', async () => {
    const bridge = makeFakeBridge();
    bridge.whisperDownloadModel.mockImplementation(() => new Promise(() => undefined));
    const { fixture } = await setup(bridge);
    (
      fixture.nativeElement.querySelector(
        '[data-test="download-whisper-base"]',
      ) as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    const before =
      fixture.nativeElement.querySelector('[data-test="progress-whisper-base"]') !== null;

    fixture.destroy();
    const again = TestBed.createComponent(LocalModelsComponent);
    again.detectChanges();
    await settle(again);
    const host = again.nativeElement as HTMLElement;

    expect({
      before,
      progress: host.querySelector('[data-test="progress-whisper-base"]') !== null,
      download: host.querySelector('[data-test="download-whisper-base"]') !== null,
    }).toEqual({ before: true, progress: true, download: false });
  });

  it('The Transcription footer counts and sizes only the voice models, as the header does', async () => {
    const { fixture, store } = await setup(makeFakeBridge({ installed: ['base'] }));
    store.markDownloaded('qwen-3-vl-2b');
    fixture.detectChanges();
    const foot = (
      (fixture.nativeElement as HTMLElement).querySelector('[data-test="foot-summary"]')
        ?.textContent ?? ''
    ).replace(/\s+/g, ' ');

    expect({
      count: Number(/(\d+) models?/.exec(foot)?.[1]),
      size: /Downloads: (\S+ \S+)/.exec(foot)?.[1],
    }).toEqual({ count: 1, size: '141 MB' });
  });

  it('Delete unused during a pending switch never deletes the model the owner just chose', async () => {
    const bridge = deferredSwitchBridge();
    const { fixture } = await setup(bridge);

    (
      fixture.nativeElement.querySelector('[data-test="delete-unused"]') as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    await answer('confirm-ok').catch(() => undefined);
    await settle(fixture);

    expect(bridge.whisperDeleteModel.mock.calls.map(([req]) => req.variant)).not.toContain('base');
  });
});

describe('Local Models: Cancel on a download', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  const settle = async (fixture: ComponentFixture<LocalModelsComponent>) => {
    for (let i = 0; i < 4; i++) {
      await fixture.whenStable();
      await Promise.resolve();
      fixture.detectChanges();
    }
  };

  /** A main whose running download or pull rejects as cancelled once its cancel channel names it. */
  function cancellableBridge() {
    const stopRunning = new Map<string, () => void>();
    const running = (key: string) =>
      new Promise<never>((_resolve, reject) => {
        stopRunning.set(key, () => reject(new Error('[scout:whisper/download] cancelled')));
      });
    return Object.assign(makeFakeBridge(), {
      whisperDownloadModel: vi.fn((req: { variant: string }) => running(req.variant)),
      llmPullOllamaModel: vi.fn((req: { requestId: string }) => running(req.requestId)),
      whisperCancelDownload: vi.fn(async (req: { variant: string }) =>
        stopRunning.get(req.variant)?.(),
      ),
      llmCancelOllamaPull: vi.fn(async (req: { requestId: string }) =>
        stopRunning.get(req.requestId)?.(),
      ),
    });
  }

  function row(fixture: ComponentFixture<LocalModelsComponent>, id: string) {
    const host = fixture.nativeElement as HTMLElement;
    return {
      progress: host.querySelector(`[data-test="progress-${id}"]`) !== null,
      download: host.querySelector(`[data-test="download-${id}"]`) !== null,
      error: host.querySelector(`[data-test="error-${id}"]`)?.textContent?.trim() ?? null,
    };
  }

  it('Cancel on a voice model downloading stops it through main, and the row goes back to Download with no error', async () => {
    const bridge = cancellableBridge();
    const { fixture } = await setup(bridge);
    (
      fixture.nativeElement.querySelector(
        '[data-test="download-whisper-base"]',
      ) as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="cancel-whisper-base"]') as HTMLButtonElement
    ).click();
    await settle(fixture);

    expect(bridge.whisperCancelDownload.mock.calls).toEqual([[{ variant: 'base' }]]);
    expect(row(fixture, 'whisper-base')).toEqual({ progress: false, download: true, error: null });
  });

  it('Cancel on an Ollama model pulling stops that pull through main, and the row goes back to Download with no error', async () => {
    const bridge = cancellableBridge();
    const { fixture } = await setup(bridge);
    fixture.componentRef.setInput('kind', 'analysis');
    fixture.detectChanges();
    (
      fixture.nativeElement.querySelector('[data-test="download-gemma-3-4b"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    const requestId = (
      bridge.llmPullOllamaModel.mock.calls[0] as unknown as [{ requestId: string }]
    )[0].requestId;

    (
      fixture.nativeElement.querySelector('[data-test="cancel-gemma-3-4b"]') as HTMLButtonElement
    ).click();
    await settle(fixture);

    expect(bridge.llmCancelOllamaPull.mock.calls).toEqual([[{ requestId }]]);
    expect(row(fixture, 'gemma-3-4b')).toEqual({ progress: false, download: true, error: null });
  });

  it('A Cancel that main cannot carry out shows why on the row, the download keeps running, and its own failure later still shows', async () => {
    let failDownload!: (reason: Error) => void;
    const bridge = Object.assign(makeFakeBridge(), {
      whisperDownloadModel: vi.fn(
        () => new Promise<never>((_resolve, reject) => (failDownload = reject)),
      ),
      whisperCancelDownload: vi.fn(async () => {
        throw new Error('[scout:ipc/unavailable] main is gone');
      }),
    });
    const { fixture } = await setup(bridge);
    (
      fixture.nativeElement.querySelector(
        '[data-test="download-whisper-base"]',
      ) as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="cancel-whisper-base"]') as HTMLButtonElement
    ).click();
    await settle(fixture);
    const afterCancel = row(fixture, 'whisper-base');
    failDownload(new Error('whisper: checksum mismatch for ggml-base.bin'));
    await settle(fixture);

    expect({ afterCancel, afterFailure: row(fixture, 'whisper-base') }).toEqual({
      afterCancel: {
        progress: true,
        download: false,
        error: expect.stringContaining('main is gone'),
      },
      afterFailure: {
        progress: false,
        download: true,
        error: expect.stringContaining('checksum mismatch for ggml-base.bin'),
      },
    });
  });

  it('A voice download cancelled from another window shows as cancelled here too: the row goes back to Download with no error', async () => {
    const waiting: ((reason: Error) => void)[] = [];
    const bridge = Object.assign(makeFakeBridge(), {
      whisperDownloadModel: vi.fn(
        () => new Promise<never>((_resolve, reject) => void waiting.push(reject)),
      ),
      whisperCancelDownload: vi.fn(async () => {
        for (const reject of waiting) {
          reject(
            new Error(
              "Error invoking remote method 'scout:whisper.downloadModel': Error: [scout:whisper/download-cancelled] whisper: the download was cancelled",
            ),
          );
        }
      }),
    });
    const { fixture } = await setup(bridge);
    const otherWindow = runInInjectionContext(
      TestBed.inject(EnvironmentInjector),
      () => new LocalModelsStore(),
    );
    (
      fixture.nativeElement.querySelector(
        '[data-test="download-whisper-base"]',
      ) as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    const theirs = otherWindow.download('whisper-base').catch((err: unknown) => err as Error);

    await otherWindow.cancelDownload('whisper-base');
    await theirs;
    await settle(fixture);

    expect(row(fixture, 'whisper-base')).toEqual({ progress: false, download: true, error: null });
  });

  it('An Ollama pull main reports as cancelled shows as cancelled, never as an error', async () => {
    const bridge = Object.assign(makeFakeBridge(), {
      llmPullOllamaModel: vi.fn(async () => {
        throw new Error(
          "Error invoking remote method 'scout:llm.pullOllamaModel': Error: [scout:llm/pull-cancelled] The download was cancelled.",
        );
      }),
    });
    const { fixture } = await setup(bridge);
    fixture.componentRef.setInput('kind', 'analysis');
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="download-gemma-3-4b"]') as HTMLButtonElement
    ).click();
    await settle(fixture);

    expect(row(fixture, 'gemma-3-4b')).toEqual({ progress: false, download: true, error: null });
  });
});

describe('the Ollama models as Sources.dc.html draws them in the Local models in Ollama dialog', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  async function ollama(inUse?: string): Promise<{
    fixture: ComponentFixture<LocalModelsComponent>;
    store: LocalModelsStore;
  }> {
    const { fixture, store } = await setup();
    if (inUse !== undefined) store.markDownloaded(inUse);
    store.markDownloaded('gemma-3-4b');
    fixture.componentRef.setInput('kind', 'analysis');
    fixture.componentRef.setInput('embedded', true);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, store };
  }

  function rowsOf(fixture: ComponentFixture<LocalModelsComponent>, list: string): string[] {
    return Array.from(
      fixture.nativeElement.querySelectorAll(
        `[data-test="${list}"] > li`,
      ) as NodeListOf<HTMLElement>,
      (li) => li.getAttribute('data-test') ?? '',
    );
  }

  it('the downloaded models are under Installed, the others under "Recommended", each a row of its name over what it reads, its size and its memory', async () => {
    const { fixture } = await ollama();
    const row = fixture.nativeElement.querySelector('[data-test="row-gemma-3-4b"]') as HTMLElement;

    expect({
      titles: Array.from(
        fixture.nativeElement.querySelectorAll('.list-title') as NodeListOf<HTMLElement>,
        (h) => h.textContent?.trim(),
      ).slice(1),
      installed: rowsOf(fixture, 'installed-models'),
      pull: rowsOf(fixture, 'available-models'),
      meta: row.querySelector('.model-meta')?.textContent?.trim(),
      empty: fixture.nativeElement.querySelector('[data-test="no-installed"]'),
    }).toEqual({
      titles: ['Skill matching', 'Installed', 'Pull by name', 'Recommended'],
      installed: ['row-gemma-3-4b'],
      pull: [
        'row-qwen-2.5-0.5b-instruct',
        'row-qwen-2.5-1.5b-instruct',
        'row-qwen-3-vl-2b',
        'row-gemma-3-12b',
      ],
      meta: 'Text and images · 3.3 GB on disk · 8 GB RAM',
      empty: null,
    });
  });

  it('with nothing downloaded, Installed says "No models installed yet. Pull one below."', async () => {
    const { fixture, store } = await ollama();
    store.markRemoved('gemma-3-4b');
    fixture.detectChanges();

    expect(
      (
        fixture.nativeElement.querySelector('[data-test="no-installed"]') as HTMLElement | null
      )?.textContent?.trim(),
    ).toBe('No models installed yet. Pull one below.');
  });

  it('a pull in progress shows the bar and "<done> of <size> GB · <percent>%" under the row, with Cancel', async () => {
    const { fixture, store } = await ollama();
    vi.spyOn(store, 'progressOf').mockImplementation((id: string) =>
      id === 'gemma-3-12b' ? 40 : null,
    );
    fixture.componentRef.setInput('kind', 'transcription');
    fixture.detectChanges();
    fixture.componentRef.setInput('kind', 'analysis');
    fixture.detectChanges();
    const progress = fixture.nativeElement.querySelector(
      '[data-test="progress-gemma-3-12b"]',
    ) as HTMLElement;

    expect({
      bar: progress.querySelector('[role="progressbar"]')?.getAttribute('aria-valuenow'),
      text: progress.querySelector('.model-meta')?.textContent?.trim(),
      cancel: (
        fixture.nativeElement.querySelector('[data-test="cancel-gemma-3-12b"]') as HTMLElement
      ).textContent?.trim(),
    }).toEqual({ bar: '40', text: '3.2 of 8.0 GB · 40%', cancel: 'Cancel' });
  });

  it('Remove asks in the row as an alert dialog on the error container, Cancel takes the focus, and Escape cancels without closing anything else', async () => {
    const { fixture } = await ollama('qwen-2.5-0.5b-instruct');
    (fixture.nativeElement.querySelector('[data-test="remove-gemma-3-4b"]') as HTMLElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const ask = fixture.nativeElement.querySelector(
      '[data-test="remove-confirm-gemma-3-4b"]',
    ) as HTMLElement;
    const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
    let reachedDocument = false;
    const listener = (): void => {
      reachedDocument = true;
    };
    document.addEventListener('keydown', listener);
    ask.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    document.removeEventListener('keydown', listener);
    fixture.detectChanges();

    expect({
      role: ask.getAttribute('role'),
      focused,
      ok: (
        ask.querySelector('[data-test="remove-ok-gemma-3-4b"]') as HTMLElement
      ).classList.contains('pill--danger-solid'),
      reachedDocument,
      after: fixture.nativeElement.querySelector('[data-test="remove-confirm-gemma-3-4b"]'),
    }).toEqual({
      role: 'alertdialog',
      focused: 'remove-cancel-gemma-3-4b',
      ok: true,
      reachedDocument: false,
      after: null,
    });
  });

  describe('the Skill matching row', () => {
    const OLLAMA = { endpoint: 'http://localhost:11434' };
    const NOTE =
      'The first download also gets nomic-embed-text (274 MB), which Scout uses to match a message to a skill.';
    const SKILL_DESC =
      'Scout uses this model to match a message to a skill. Without it, Scout picks a skill by the words of the message.';

    const settle = async (fixture: ComponentFixture<LocalModelsComponent>) => {
      for (let i = 0; i < 6; i++) {
        await fixture.whenStable();
        await Promise.resolve();
        fixture.detectChanges();
      }
    };

    /**
     * A main whose Ollama lists `listed` and answers each pull as `pull` says, keyed by the request id; a pull that
     * succeeds adds the model to the list, as Ollama does.
     */
    function ollamaBridge(
      listed: readonly string[],
      pull: (tag: string, requestId: string) => Promise<{ tag: string }> = async (tag) => ({ tag }),
    ) {
      const names = [...listed];
      return Object.assign(makeFakeBridge(), {
        llmListOllamaModels: vi.fn(async () => ({
          models: names.map((name) => ({ name, modifiedAt: '', sizeBytes: 0 })),
        })),
        llmPullOllamaModel: vi.fn(async (req: { tag: string; requestId: string }) => {
          const answer = await pull(req.tag, req.requestId);
          names.push(req.tag.includes(':') ? req.tag : `${req.tag}:latest`);
          return answer;
        }),
      });
    }

    function text(fixture: ComponentFixture<LocalModelsComponent>, test: string): string | null {
      const el = (fixture.nativeElement as HTMLElement).querySelector(`[data-test="${test}"]`);
      return el === null ? null : (el.textContent ?? '').replace(/\s+/g, ' ').trim();
    }

    function skillRow(fixture: ComponentFixture<LocalModelsComponent>) {
      const host = fixture.nativeElement as HTMLElement;
      const row = host.querySelector('[data-test="row-nomic-embed-text"]') as HTMLElement;
      return {
        title: row.querySelector('#model-nomic-embed-text')?.textContent?.trim(),
        meta: row.querySelector('.model-meta')?.textContent?.trim(),
        desc: row.querySelector('.model-desc')?.textContent?.trim(),
        installed: text(fixture, 'skill-installed'),
        download:
          row
            .querySelector('[data-test="download-nomic-embed-text"]')
            ?.getAttribute('aria-label') ?? null,
        error: text(fixture, 'error-nomic-embed-text'),
      };
    }

    function pulledTags(bridge: ReturnType<typeof ollamaBridge>): string[] {
      return bridge.llmPullOllamaModel.mock.calls.map(([req]) => req.tag);
    }

    function click(fixture: ComponentFixture<LocalModelsComponent>, test: string): void {
      (
        (fixture.nativeElement as HTMLElement).querySelector(`[data-test="${test}"]`) as HTMLElement
      ).click();
    }

    it('the row and the note are drawn as the artboard has them when the model is missing', async () => {
      window.localStorage.setItem(
        LOCAL_MODELS_STORAGE_KEY,
        JSON.stringify({
          downloaded: ['gemma-3-4b'],
          selected: { transcription: null, analysis: 'gemma-3-4b' },
        }),
      );
      const bridge = ollamaBridge(['gemma3:4b']);
      const { fixture } = await setup(bridge, 'analysis');
      await settle(fixture);
      const host = fixture.nativeElement as HTMLElement;
      const pullHeading = Array.from(host.querySelectorAll('.list-title')).find(
        (h) => h.textContent?.trim() === 'Recommended',
      );

      expect({
        asked: bridge.llmListOllamaModels.mock.calls,
        titles: Array.from(host.querySelectorAll('.list-title'), (h) =>
          h.textContent?.trim(),
        ).slice(1),
        installed: Array.from(host.querySelectorAll('[data-test="installed-models"] > li'), (li) =>
          li.getAttribute('data-test'),
        ),
        row: skillRow(fixture),
        noteAfterPullHeading: pullHeading?.nextElementSibling?.getAttribute('data-test'),
        note: text(fixture, 'skill-model-note'),
        pulled: pulledTags(bridge),
      }).toEqual({
        asked: [[OLLAMA]],
        titles: ['Skill matching', 'Installed', 'Pull by name', 'Recommended'],
        installed: ['row-gemma-3-4b'],
        row: {
          title: 'nomic-embed-text',
          meta: '274 MB on disk',
          desc: SKILL_DESC,
          installed: null,
          download: 'Download nomic-embed-text',
          error: null,
        },
        noteAfterPullHeading: 'skill-model-note',
        note: NOTE,
        pulled: [],
      });
    });

    it('the row shows Installed and the note is gone when it is installed', async () => {
      const bridge = ollamaBridge(['gemma3:4b', 'nomic-embed-text:latest']);
      const { fixture } = await setup(bridge, 'analysis');
      await settle(fixture);

      expect({
        asked: bridge.llmListOllamaModels.mock.calls,
        row: skillRow(fixture),
        remove: text(fixture, 'remove-nomic-embed-text'),
        note: text(fixture, 'skill-model-note'),
      }).toEqual({
        asked: [[OLLAMA]],
        row: {
          title: 'nomic-embed-text',
          meta: '274 MB on disk',
          desc: SKILL_DESC,
          installed: 'Installed',
          download: null,
          error: null,
        },
        remove: null,
        note: null,
      });
    });

    it('Download on a chat model also pulls nomic-embed-text when it is missing', async () => {
      const bridge = ollamaBridge([]);
      const { fixture, store } = await setup(bridge, 'analysis');
      await settle(fixture);

      click(fixture, 'download-gemma-3-4b');
      await settle(fixture);

      expect({
        pulled: pulledTags(bridge),
        chatModel: store.isDownloaded('gemma-3-4b'),
        row: skillRow(fixture),
        note: text(fixture, 'skill-model-note'),
      }).toEqual({
        pulled: ['gemma3:4b', 'nomic-embed-text'],
        chatModel: true,
        row: {
          title: 'nomic-embed-text',
          meta: '274 MB on disk',
          desc: SKILL_DESC,
          installed: 'Installed',
          download: null,
          error: null,
        },
        note: null,
      });
    });

    it('Download on a chat model pulls only that model when it is installed', async () => {
      const bridge = ollamaBridge(['nomic-embed-text:latest']);
      const { fixture, store } = await setup(bridge, 'analysis');
      await settle(fixture);

      click(fixture, 'download-gemma-3-4b');
      await settle(fixture);

      expect({
        pulled: pulledTags(bridge),
        chatModel: store.isDownloaded('gemma-3-4b'),
        installed: text(fixture, 'skill-installed'),
      }).toEqual({ pulled: ['gemma3:4b'], chatModel: true, installed: 'Installed' });
    });

    it('the nomic-embed-text pull fails: its row shows the reason and the chat model still ends downloaded', async () => {
      const bridge = ollamaBridge([], async (tag) => {
        if (tag === 'nomic-embed-text') {
          throw new Error(
            '[scout:ollama/unreachable] Ollama sent nothing for 60 s; the download stopped.',
          );
        }
        return { tag };
      });
      const { fixture, store } = await setup(bridge, 'analysis');
      await settle(fixture);

      click(fixture, 'download-gemma-3-4b');
      await settle(fixture);

      expect({
        pulled: pulledTags(bridge),
        chatModel: store.isDownloaded('gemma-3-4b'),
        chatError: text(fixture, 'error-gemma-3-4b'),
        row: skillRow(fixture),
        note: text(fixture, 'skill-model-note'),
      }).toEqual({
        pulled: ['gemma3:4b', 'nomic-embed-text'],
        chatModel: true,
        chatError: null,
        row: {
          title: 'nomic-embed-text',
          meta: '274 MB on disk',
          desc: SKILL_DESC,
          installed: null,
          download: 'Download nomic-embed-text',
          error: expect.stringContaining(
            'Ollama sent nothing for 60 s; the download stopped.',
          ) as unknown as string,
        },
        note: NOTE,
      });
    });

    it('Cancel on the skill row cancels only that pull', async () => {
      const rejecters = new Map<string, (reason: Error) => void>();
      const bridge = Object.assign(
        ollamaBridge(
          [],
          (_tag, requestId) =>
            new Promise((_resolve, reject) => void rejecters.set(requestId, reject)),
        ),
        {
          llmCancelOllamaPull: vi.fn(async (req: { requestId: string }) =>
            rejecters.get(req.requestId)?.(
              new Error('[scout:llm/pull-cancelled] The download was cancelled.'),
            ),
          ),
        },
      );
      const { fixture } = await setup(bridge, 'analysis');
      await settle(fixture);
      click(fixture, 'download-gemma-3-4b');
      fixture.detectChanges();
      const requestOf = (tag: string): string =>
        (
          bridge.llmPullOllamaModel.mock.calls as unknown as [{ tag: string; requestId: string }][]
        ).find(([req]) => req.tag === tag)![0].requestId;

      click(fixture, 'cancel-nomic-embed-text');
      await settle(fixture);

      expect({
        pulled: pulledTags(bridge),
        cancelled: bridge.llmCancelOllamaPull.mock.calls,
        row: skillRow(fixture),
        skillProgress: text(fixture, 'progress-nomic-embed-text'),
        chatProgress: text(fixture, 'progress-gemma-3-4b') !== null,
        chatCancel: text(fixture, 'cancel-gemma-3-4b'),
      }).toEqual({
        pulled: ['gemma3:4b', 'nomic-embed-text'],
        cancelled: [[{ requestId: requestOf('nomic-embed-text') }]],
        row: {
          title: 'nomic-embed-text',
          meta: '274 MB on disk',
          desc: SKILL_DESC,
          installed: null,
          download: 'Download nomic-embed-text',
          error: null,
        },
        skillProgress: null,
        chatProgress: true,
        chatCancel: 'Cancel',
      });
    });
  });

  it('the Whisper view in Models is unchanged: its panel card, its rows in one list, Remove through the shared confirmation', async () => {
    const { fixture } = await setup();
    fixture.componentRef.setInput('embedded', true);
    fixture.detectChanges();

    expect({
      panel: fixture.nativeElement.querySelector('main .panel-card') !== null,
      list: fixture.nativeElement.querySelector('.catalog-inline') !== null,
      split: fixture.nativeElement.querySelector('[data-test="installed-models"]'),
      server: fixture.nativeElement.querySelector('[data-test="server-line"]'),
    }).toEqual({ panel: true, list: true, split: null, server: null });
  });
});

describe('LLM Server panel: state x what it shows', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  const RUN_BY_THIS_COMPUTER = 'Ollama is run by this computer, not by Scout.';
  const NOT_ANSWERING =
    'Ollama is not answering. Open the Ollama app or run ollama serve, then scan again.';
  const GONE = [
    'start-btn',
    'stop-btn',
    'restart-btn',
    'autostart-toggle',
    'metric-autostart',
    'ollama-first-start',
    'action-error',
  ];

  type ServerState =
    | 'answers with models'
    | 'answers with models, nothing chosen'
    | 'answers with none'
    | 'does not answer'
    | 'first read pending';

  function listAnswer(state: ServerState): () => Promise<{ models: FakeOllamaModel[] }> {
    switch (state) {
      case 'answers with models':
      case 'answers with models, nothing chosen':
        return async () => ({ models: ['gemma3:4b', 'qwen2.5:3b'].map(ollamaModel) });
      case 'answers with none':
        return async () => ({ models: [] });
      case 'does not answer':
        return async () => {
          throw new OllamaUnreachableError('http://localhost:11434');
        };
      case 'first read pending':
        return () => new Promise(() => undefined);
    }
  }

  /**
   * Opens the Ollama view with Ollama's list answering as `state`, no `llm.model` saved, and Gemma 3 4B downloaded and
   * selected from before, except when nothing was chosen.
   */
  async function open(state: ServerState, base = makeFakeBridge()) {
    const chosen = state !== 'answers with models, nothing chosen';
    window.localStorage.setItem(
      LOCAL_MODELS_STORAGE_KEY,
      JSON.stringify({
        downloaded: chosen ? ['gemma-3-4b'] : [],
        selected: { transcription: null, analysis: chosen ? 'gemma-3-4b' : null },
      }),
    );
    const bridge = Object.assign(base, {
      vaultHas: vi.fn(async () => ({ exists: false })),
      vaultStore: vi.fn(async (_req: { key: string; value: string }) => undefined),
    });
    bridge.llmListOllamaModels.mockImplementation(listAnswer(state));
    const { fixture } = await setup(bridge, 'analysis');
    for (let i = 0; i < 4; i += 1) {
      await fixture.whenStable();
      await Promise.resolve();
      fixture.detectChanges();
    }
    return { fixture, bridge };
  }

  function shown(
    fixture: ComponentFixture<LocalModelsComponent>,
    bridge: Awaited<ReturnType<typeof open>>['bridge'],
  ) {
    const host = fixture.nativeElement as HTMLElement;
    const text = (test: string) =>
      host.querySelector(`[data-test="${test}"]`)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
    return {
      chip: text('panel-status'),
      chipOk: host.querySelector('[data-test="panel-status"]')?.classList.contains('chip--ok'),
      selected: text('metric-selected'),
      downloaded: text('metric-downloaded'),
      storage: text('metric-storage'),
      line: text('server-line'),
      present: GONE.filter((test) => host.querySelector(`[data-test="${test}"]`) !== null),
      vaultWrites: bridge.vaultStore.mock.calls,
    };
  }

  const SHOWN: Readonly<Record<ServerState, ReturnType<typeof shown>>> = {
    'answers with models': {
      chip: 'Running',
      chipOk: true,
      selected: 'Gemma 3 4B',
      downloaded: '2',
      storage: '5.2 GB',
      line: RUN_BY_THIS_COMPUTER,
      present: [],
      vaultWrites: [],
    },
    'answers with models, nothing chosen': {
      chip: 'Running',
      chipOk: true,
      selected: '—',
      downloaded: '2',
      storage: '5.2 GB',
      line: RUN_BY_THIS_COMPUTER,
      present: [],
      vaultWrites: [],
    },
    'answers with none': {
      chip: 'Running',
      chipOk: true,
      selected: '—',
      downloaded: '0',
      storage: '0 B',
      line: RUN_BY_THIS_COMPUTER,
      present: [],
      vaultWrites: [],
    },
    'does not answer': {
      chip: 'Not running',
      chipOk: false,
      selected: 'Gemma 3 4B',
      downloaded: '1',
      storage: '3.3 GB',
      line: NOT_ANSWERING,
      present: [],
      vaultWrites: [],
    },
    'first read pending': {
      chip: 'Checking',
      chipOk: false,
      selected: 'Gemma 3 4B',
      downloaded: '1',
      storage: '3.3 GB',
      line: RUN_BY_THIS_COMPUTER,
      present: [],
      vaultWrites: [],
    },
  };

  for (const state of Object.keys(SHOWN) as ServerState[]) {
    it(`Ollama ${state}: the chip, SELECTED, DOWNLOADED, STORAGE and the line, and no server control`, async () => {
      const { fixture, bridge } = await open(state);

      expect(shown(fixture, bridge)).toEqual(SHOWN[state]);
    });
  }

  it('the LLM Server panel shows Running while Ollama is answering', async () => {
    const { fixture } = await open('answers with models');
    const chip = fixture.nativeElement.querySelector('[data-test="panel-status"]') as HTMLElement;

    expect({ text: chip.textContent?.trim(), ok: chip.classList.contains('chip--ok') }).toEqual({
      text: 'Running',
      ok: true,
    });
  });

  it('the panel never shows Checking for Autostart: it has no Autostart metric and no switch that cannot be selected', async () => {
    const { fixture } = await open('answers with models');
    const panel = fixture.nativeElement.querySelector('[data-test="server-panel"]') as HTMLElement;

    expect({
      metrics: Array.from(panel.querySelectorAll('dt'), (dt) => dt.textContent?.trim()),
      switches: panel.querySelectorAll('[role="switch"]').length,
      disabled: panel.querySelectorAll('[disabled]').length,
      mentions: /autostart/i.test(panel.textContent ?? ''),
    }).toEqual({
      metrics: ['SELECTED', 'DOWNLOADED', 'STORAGE'],
      switches: 0,
      disabled: 0,
      mentions: false,
    });
  });

  it('the dialog offers no Start, Stop or Restart, and says Ollama is run by this computer, not by Scout', async () => {
    const { fixture } = await open('answers with models');
    const panel = fixture.nativeElement.querySelector('[data-test="server-panel"]') as HTMLElement;

    expect({
      buttons: Array.from(panel.querySelectorAll('button'), (b) => b.textContent?.trim()),
      line: panel.querySelector('[data-test="server-line"]')?.textContent?.trim(),
    }).toEqual({ buttons: [], line: RUN_BY_THIS_COMPUTER });
  });

  it('opening the dialog asks the worker nothing and shows no Worker HTTP endpoint not available error', async () => {
    vi.useFakeTimers({ toFake: ['setInterval'] });
    try {
      const { fixture, bridge } = await open('answers with models');
      vi.advanceTimersByTime(4000);
      for (let i = 0; i < 4; i += 1) {
        await fixture.whenStable();
        fixture.detectChanges();
      }
      const page = (fixture.nativeElement as HTMLElement).textContent ?? '';

      expect({
        httpError: page.includes('Worker HTTP endpoint not available'),
        workerError: page.includes('Worker is not running'),
        chip: shown(fixture, bridge).chip,
      }).toEqual({ httpError: false, workerError: false, chip: 'Running' });
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });

  it('a poll tick while a slow read is in flight sends no second read, and the next tick after it settles sends one', async () => {
    vi.useFakeTimers({ toFake: ['setInterval'] });
    try {
      let answer!: (value: { models: FakeOllamaModel[] }) => void;
      const base = makeFakeBridge();
      base.llmListOllamaModels
        .mockImplementationOnce(
          () => new Promise<{ models: FakeOllamaModel[] }>((resolve) => (answer = resolve)),
        )
        .mockResolvedValue({ models: ['gemma3:4b'].map(ollamaModel) });
      const bridge = Object.assign(base, {
        vaultHas: vi.fn(async () => ({ exists: false })),
        vaultStore: vi.fn(async (_req: { key: string; value: string }) => undefined),
      });
      const { fixture } = await setup(bridge, 'analysis');
      const onOpen = bridge.llmListOllamaModels.mock.calls.length;
      vi.advanceTimersByTime(4000);
      await fixture.whenStable();
      const duringSlowRead = bridge.llmListOllamaModels.mock.calls.length;
      const chipDuring = shown(fixture, bridge).chip;
      answer({ models: ['gemma3:4b'].map(ollamaModel) });
      for (let i = 0; i < 4; i += 1) {
        await fixture.whenStable();
        await Promise.resolve();
        fixture.detectChanges();
      }
      vi.advanceTimersByTime(4000);
      for (let i = 0; i < 4; i += 1) {
        await fixture.whenStable();
        await Promise.resolve();
        fixture.detectChanges();
      }

      expect({
        onOpen,
        duringSlowRead,
        chipDuring,
        afterNextTick: bridge.llmListOllamaModels.mock.calls.length,
        chip: shown(fixture, bridge).chip,
      }).toEqual({
        onOpen: 1,
        duringSlowRead: 1,
        chipDuring: 'Checking',
        afterNextTick: 2,
        chip: 'Running',
      });
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });

  it('one read of Ollama list serves the panel and the Skill matching row: one when the dialog opens and one per 4 s poll', async () => {
    vi.useFakeTimers({ toFake: ['setInterval'] });
    try {
      const { fixture, bridge } = await open('answers with models');
      const onOpen = bridge.llmListOllamaModels.mock.calls.length;
      bridge.llmListOllamaModels.mockResolvedValue({
        models: ['gemma3:4b', 'nomic-embed-text:latest'].map(ollamaModel),
      });
      vi.advanceTimersByTime(4000);
      for (let i = 0; i < 4; i += 1) {
        await fixture.whenStable();
        await Promise.resolve();
        fixture.detectChanges();
      }

      expect({
        onOpen,
        afterOneTick: bridge.llmListOllamaModels.mock.calls.length,
        downloaded: shown(fixture, bridge).downloaded,
        skill:
          fixture.nativeElement
            .querySelector('[data-test="skill-installed"]')
            ?.textContent?.trim() ?? null,
      }).toEqual({ onOpen: 1, afterOneTick: 2, downloaded: '1', skill: 'Installed' });
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });

  for (const [before, after] of [
    ['answers with models', 'does not answer'],
    ['does not answer', 'answers with models'],
  ] as const) {
    it(`a dialog opened again after Ollama ${before} shows Checking until the new read settles, then the state of Ollama that ${after}`, async () => {
      const { fixture, bridge } = await open(before);
      const previous = shown(fixture, bridge).chip;
      fixture.destroy();
      let settle!: () => void;
      bridge.llmListOllamaModels.mockImplementation(
        () =>
          new Promise((resolve, reject) => {
            settle = () => void listAnswer(after)().then(resolve, reject);
          }),
      );

      const again = TestBed.createComponent(LocalModelsComponent);
      again.componentRef.setInput('kind', 'analysis');
      again.componentRef.setInput('embedded', true);
      again.detectChanges();
      await again.whenStable();
      again.detectChanges();
      const whileReading = shown(again, bridge).chip;
      settle();
      for (let i = 0; i < 4; i += 1) {
        await again.whenStable();
        await Promise.resolve();
        again.detectChanges();
      }

      expect({ previous, whileReading, settled: shown(again, bridge).chip }).toEqual({
        previous: SHOWN[before].chip,
        whileReading: 'Checking',
        settled: SHOWN[after].chip,
      });
      again.destroy();
    });
  }
});

describe('every model the local Ollama lists, in the Local models in Ollama dialog', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  const SIZE = 2_019_393_189;

  function listed(name: string, extra: Partial<OllamaModelInfo> = {}): OllamaModelInfo {
    return { name, modifiedAt: '', sizeBytes: SIZE, ...extra };
  }

  /** Opens the Ollama view while the local Ollama lists `models`, with the window's last state `seeded`. */
  async function dialog(
    models: readonly OllamaModelInfo[],
    seeded?: { downloaded: readonly string[]; analysis: string | null },
  ) {
    if (seeded !== undefined) {
      window.localStorage.setItem(
        LOCAL_MODELS_STORAGE_KEY,
        JSON.stringify({
          downloaded: seeded.downloaded,
          selected: { transcription: null, analysis: seeded.analysis },
        }),
      );
    }
    const bridge = Object.assign(makeFakeBridge(), {
      vaultStore: vi.fn(async (_req: { key: string; value: string }) => undefined),
      llmUnloadOllamaModels: vi.fn(async () => ({ unloaded: [], refused: [] })),
    });
    bridge.llmListOllamaModels.mockImplementation(async () => ({ models: [...models] }));
    const { fixture, store } = await setup(bridge, 'analysis');
    await settle(fixture);
    const handedOn: string[] = [];
    fixture.componentInstance.ollamaModelChosen.subscribe((id) => handedOn.push(id));
    return { fixture, store, bridge, handedOn };
  }

  async function settle(fixture: ComponentFixture<LocalModelsComponent>): Promise<void> {
    for (let i = 0; i < 6; i++) {
      await fixture.whenStable();
      await Promise.resolve();
      fixture.detectChanges();
    }
  }

  function host(fixture: ComponentFixture<LocalModelsComponent>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(fixture: ComponentFixture<LocalModelsComponent>, test: string): string | null {
    const el = host(fixture).querySelector(`[data-test="${test}"]`);
    return el === null ? null : (el.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  function rows(fixture: ComponentFixture<LocalModelsComponent>, list: string): string[] {
    return Array.from(
      host(fixture).querySelectorAll(`[data-test="${list}"] > li`),
      (li) => li.getAttribute('data-test')?.replace(/^row-/, '') ?? '',
    );
  }

  function row(fixture: ComponentFixture<LocalModelsComponent>, id: string) {
    const li = host(fixture).querySelector<HTMLElement>(`[data-test="row-${id}"]`);
    if (li === null) return null;
    const remove = li.querySelector(`[data-test="remove-${id}"]`) as HTMLButtonElement | null;
    return {
      title: li.querySelector(`[id="model-${id}"]`)?.textContent?.trim(),
      meta: li.querySelector('.model-meta')?.textContent?.trim(),
      description: li.querySelector('.model-desc')?.textContent?.trim() ?? null,
      embeddingsOnly: text(fixture, `embeddings-${id}`),
      useThis: li.querySelector(`[data-test="select-${id}"]`) !== null,
      remove: remove === null ? 'none' : remove.disabled ? 'disabled' : 'enabled',
    };
  }

  function click(fixture: ComponentFixture<LocalModelsComponent>, test: string): void {
    (host(fixture).querySelector(`[data-test="${test}"]`) as HTMLElement).click();
  }

  it.each([
    {
      name: 'a catalog model',
      model: listed('gemma3:4b', { family: 'gemma3', capabilities: ['completion', 'vision'] }),
      id: 'gemma-3-4b',
      shown: {
        title: 'Gemma 3 4B',
        meta: 'Text and images · 3.3 GB on disk · 8 GB RAM',
        description: 'Recommended default. Handles both text and image analysis.',
        embeddingsOnly: null,
        useThis: true,
        remove: 'enabled',
      },
    },
    {
      name: 'a model outside the catalog with its details',
      model: listed('sample-chat:3b', {
        family: 'llama',
        parameterSize: '3.2B',
        quantization: 'Q4_K_M',
        capabilities: ['completion', 'tools'],
      }),
      id: 'sample-chat:3b',
      shown: {
        title: 'sample-chat:3b',
        meta: 'llama · 3.2B · Q4_K_M · 1.9 GB on disk',
        description: null,
        embeddingsOnly: null,
        useThis: true,
        remove: 'enabled',
      },
    },
    {
      name: 'a model outside the catalog without details',
      model: listed('sample-chat:latest', { capabilities: null }),
      id: 'sample-chat:latest',
      shown: {
        title: 'sample-chat:latest',
        meta: '1.9 GB on disk',
        description: null,
        embeddingsOnly: null,
        useThis: true,
        remove: 'enabled',
      },
    },
    {
      name: 'an embeddings-only model',
      model: listed('sample-vectors:335m', { family: 'bert', capabilities: ['embedding'] }),
      id: 'sample-vectors:335m',
      shown: {
        title: 'sample-vectors:335m',
        meta: 'bert · 1.9 GB on disk',
        description: null,
        embeddingsOnly: 'Embeddings only',
        useThis: false,
        remove: 'enabled',
      },
    },
  ])(
    'Installed lists every model the local Ollama reports, in the catalog or not: $name',
    async ({ model, id, shown }) => {
      const { fixture } = await dialog([model]);

      expect({ installed: rows(fixture, 'installed-models'), row: row(fixture, id) }).toEqual({
        installed: [id],
        row: shown,
      });
    },
  );

  it('the catalog models not installed are listed under Recommended with Download', async () => {
    const { fixture } = await dialog([listed('gemma3:4b'), listed('sample-chat:7b')]);
    const heading = Array.from(host(fixture).querySelectorAll('.list-title')).find(
      (h) => h.textContent?.trim() === 'Recommended',
    );

    expect({
      headingBeforeList: heading?.nextElementSibling?.getAttribute('data-test'),
      recommended: rows(fixture, 'available-models'),
      downloads: rows(fixture, 'available-models').map((id) => text(fixture, `download-${id}`)),
    }).toEqual({
      headingBeforeList: 'skill-model-note',
      recommended: [
        'qwen-2.5-0.5b-instruct',
        'qwen-2.5-1.5b-instruct',
        'qwen-3-vl-2b',
        'gemma-3-12b',
      ],
      downloads: ['Download', 'Download', 'Download', 'Download'],
    });
  });

  it('Use this on a model outside the catalog saves its tag as llm.model and hands it on', async () => {
    const { fixture, store, bridge, handedOn } = await dialog([
      listed('gemma3:4b'),
      listed('sample-coder:latest', { capabilities: ['completion'] }),
    ]);

    click(fixture, 'select-sample-coder:latest');
    await settle(fixture);

    expect({
      saved: bridge.vaultStore.mock.calls,
      handedOn,
      selected: store.selectedFor('analysis'),
      shown: text(fixture, 'metric-selected'),
      inUse: text(fixture, 'check-sample-coder:latest'),
    }).toEqual({
      saved: [[{ key: 'llm.model', value: 'sample-coder:latest' }]],
      handedOn: ['sample-coder:latest'],
      selected: 'sample-coder:latest',
      shown: 'sample-coder:latest',
      inUse: 'In use',
    });
  });

  it('Use this on a model outside the catalog whose save fails says why on its row, hands nothing on and shows it not in use', async () => {
    const { fixture, store, bridge, handedOn } = await dialog([listed('sample-coder:latest')]);
    bridge.vaultStore.mockRejectedValueOnce(
      new Error('[scout:vault/unavailable] the keyring is locked'),
    );

    click(fixture, 'select-sample-coder:latest');
    await settle(fixture);

    expect({
      error: host(fixture)
        .querySelector('[data-test="error-sample-coder:latest"] > span')
        ?.textContent?.trim(),
      handedOn,
      selected: store.selectedFor('analysis'),
      shown: text(fixture, 'metric-selected'),
      inUse: text(fixture, 'check-sample-coder:latest'),
      useThis: text(fixture, 'select-sample-coder:latest'),
    }).toEqual({
      error: '[scout:vault/unavailable] the keyring is locked',
      handedOn: [],
      selected: null,
      shown: '—',
      inUse: null,
      useThis: 'Use this',
    });
  });

  it('Remove on a model outside the catalog deletes it by its tag through main', async () => {
    const { fixture, store, bridge } = await dialog([
      listed('gemma3:4b'),
      listed('sample-chat:7b'),
    ]);

    click(fixture, 'remove-sample-chat:7b');
    await settle(fixture);
    const question = text(fixture, 'remove-confirm-sample-chat:7b');
    click(fixture, 'remove-ok-sample-chat:7b');
    await settle(fixture);

    expect({
      question,
      deleted: bridge.llmDeleteOllamaModel.mock.calls,
      installed: store.isDownloaded('sample-chat:7b'),
      saved: bridge.vaultStore.mock.calls,
    }).toEqual({
      question: 'Remove sample-chat:7b? 1.9 GB is deleted from this computer. Cancel Remove',
      deleted: [[{ endpoint: 'http://localhost:11434', tag: 'sample-chat:7b' }]],
      installed: false,
      saved: [],
    });
  });

  it.each([
    { provider: 'Ollama', inUse: true },
    { provider: 'none saved', inUse: true },
    { provider: 'a cloud provider', inUse: false },
  ] as const)(
    'the model in use offers no Remove until another is in use: $provider',
    async ({ provider, inUse }) => {
      const { fixture } = await dialog([listed('gemma3:4b'), listed('sample-chat:7b')], {
        downloaded: ['gemma-3-4b', 'sample-chat:7b'],
        analysis: 'sample-chat:7b',
      });
      const config = TestBed.inject(LlmConfigStore);
      if (provider === 'Ollama') await config.useOllama();
      if (provider === 'a cloud provider') await config.use('OpenAI', 'gpt-4.1');
      await settle(fixture);
      const before = {
        chat: row(fixture, 'sample-chat:7b')?.remove,
        note: text(fixture, 'in-use-note-sample-chat:7b'),
        describedBy: host(fixture)
          .querySelector('[data-test="remove-sample-chat:7b"]')
          ?.getAttribute('aria-describedby'),
        other: row(fixture, 'gemma-3-4b')?.remove,
      };

      if (inUse) {
        click(fixture, 'select-gemma-3-4b');
        await settle(fixture);
      }

      expect({ before, after: row(fixture, 'sample-chat:7b')?.remove }).toEqual(
        inUse
          ? {
              before: {
                chat: 'disabled',
                note: 'In use. Choose another model before removing it.',
                describedBy: 'in-use-sample-chat:7b',
                other: 'enabled',
              },
              after: 'enabled',
            }
          : {
              before: { chat: 'enabled', note: null, describedBy: null, other: 'enabled' },
              after: 'enabled',
            },
      );
    },
  );

  it('a disabled Remove on the model in use deletes nothing when clicked', async () => {
    const { fixture, bridge } = await dialog([listed('sample-chat:7b')], {
      downloaded: ['sample-chat:7b'],
      analysis: 'sample-chat:7b',
    });

    click(fixture, 'remove-sample-chat:7b');
    await settle(fixture);

    expect({
      asked: text(fixture, 'remove-confirm-sample-chat:7b'),
      deleted: bridge.llmDeleteOllamaModel.mock.calls,
    }).toEqual({ asked: null, deleted: [] });
  });

  describe('installed list x action: what is listed, what SELECTED shows, what is written', () => {
    interface Variant {
      readonly name: string;
      readonly models: readonly OllamaModelInfo[];
      readonly seeded?: { downloaded: readonly string[]; analysis: string | null };
      readonly installed: readonly string[];
      readonly selectedOnOpen: string;
      readonly choose: string | null;
      readonly chosenTag: string | null;
      readonly chosenTitle: string | null;
      readonly remove: string | null;
      readonly removedTag: string | null;
    }

    const VARIANTS: readonly Variant[] = [
      {
        name: 'none installed',
        models: [],
        installed: [],
        selectedOnOpen: '—',
        choose: null,
        chosenTag: null,
        chosenTitle: null,
        remove: null,
        removedTag: null,
      },
      {
        name: 'only catalog models',
        models: [listed('gemma3:4b'), listed('qwen2.5:3b')],
        installed: ['qwen-3-vl-2b', 'gemma-3-4b'],
        selectedOnOpen: '—',
        choose: 'gemma-3-4b',
        chosenTag: 'gemma3:4b',
        chosenTitle: 'Gemma 3 4B',
        remove: 'qwen-3-vl-2b',
        removedTag: 'qwen2.5:3b',
      },
      {
        name: 'only models outside the catalog',
        models: [listed('sample-chat:7b'), listed('sample-coder:latest')],
        installed: ['sample-chat:7b', 'sample-coder:latest'],
        selectedOnOpen: '—',
        choose: 'sample-coder:latest',
        chosenTag: 'sample-coder:latest',
        chosenTitle: 'sample-coder:latest',
        remove: 'sample-chat:7b',
        removedTag: 'sample-chat:7b',
      },
      {
        name: 'catalog and outside models mixed',
        models: [listed('sample-chat:7b'), listed('gemma3:4b')],
        installed: ['gemma-3-4b', 'sample-chat:7b'],
        selectedOnOpen: '—',
        choose: 'sample-chat:7b',
        chosenTag: 'sample-chat:7b',
        chosenTitle: 'sample-chat:7b',
        remove: 'gemma-3-4b',
        removedTag: 'gemma3:4b',
      },
      {
        name: 'the skill-matching model installed, which stays in its own row',
        models: [listed('nomic-embed-text:latest'), listed('sample-chat:7b')],
        installed: ['sample-chat:7b'],
        selectedOnOpen: '—',
        choose: 'sample-chat:7b',
        chosenTag: 'sample-chat:7b',
        chosenTitle: 'sample-chat:7b',
        remove: 'sample-chat:7b',
        removedTag: 'sample-chat:7b',
      },
      {
        name: 'a tag with :latest next to the same model with a size tag',
        models: [listed('sample-chat:latest'), listed('sample-chat:7b')],
        installed: ['sample-chat:latest', 'sample-chat:7b'],
        selectedOnOpen: '—',
        choose: 'sample-chat:latest',
        chosenTag: 'sample-chat:latest',
        chosenTitle: 'sample-chat:latest',
        remove: 'sample-chat:7b',
        removedTag: 'sample-chat:7b',
      },
      {
        name: 'a model removed outside Scout while it was selected',
        models: [listed('gemma3:4b')],
        seeded: { downloaded: ['gemma-3-4b', 'sample-gone:7b'], analysis: 'sample-gone:7b' },
        installed: ['gemma-3-4b'],
        selectedOnOpen: '—',
        choose: 'gemma-3-4b',
        chosenTag: 'gemma3:4b',
        chosenTitle: 'Gemma 3 4B',
        remove: null,
        removedTag: null,
      },
    ];

    for (const v of VARIANTS) {
      it(`${v.name}, open: lists what Ollama lists, SELECTED reads ${v.selectedOnOpen}, nothing is written`, async () => {
        const { fixture, bridge } = await dialog(v.models, v.seeded);

        expect({
          installed: rows(fixture, 'installed-models'),
          selected: text(fixture, 'metric-selected'),
          written: bridge.vaultStore.mock.calls,
        }).toEqual({ installed: v.installed, selected: v.selectedOnOpen, written: [] });
      });

      it(`${v.name}, poll tick: the list is read again, nothing is chosen and nothing is written`, async () => {
        vi.useFakeTimers({ toFake: ['setInterval'] });
        try {
          const { fixture, bridge } = await dialog(v.models, v.seeded);
          const reads = bridge.llmListOllamaModels.mock.calls.length;
          vi.advanceTimersByTime(4000);
          await settle(fixture);

          expect({
            reads: bridge.llmListOllamaModels.mock.calls.length - reads,
            installed: rows(fixture, 'installed-models'),
            selected: text(fixture, 'metric-selected'),
            written: bridge.vaultStore.mock.calls,
          }).toEqual({
            reads: 1,
            installed: v.installed,
            selected: v.selectedOnOpen,
            written: [],
          });
          fixture.destroy();
        } finally {
          vi.useRealTimers();
        }
      });

      if (v.choose !== null) {
        const choose = v.choose;
        it(`${v.name}, Use this on ${choose}: SELECTED shows it and llm.model is written once, as ${v.chosenTag}`, async () => {
          const { fixture, bridge, handedOn } = await dialog(v.models, v.seeded);

          click(fixture, `select-${choose}`);
          await settle(fixture);

          expect({
            selected: text(fixture, 'metric-selected'),
            written: bridge.vaultStore.mock.calls,
            handedOn,
          }).toEqual({
            selected: v.chosenTitle,
            written: [[{ key: 'llm.model', value: v.chosenTag }]],
            handedOn: [choose],
          });
        });
      }

      if (v.remove !== null) {
        const remove = v.remove;
        it(`${v.name}, Remove on ${remove}: it is deleted as ${v.removedTag}, leaves the list, and nothing is written`, async () => {
          const { fixture, bridge } = await dialog(v.models, v.seeded);

          click(fixture, `remove-${remove}`);
          await settle(fixture);
          click(fixture, `remove-ok-${remove}`);
          await settle(fixture);

          expect({
            deleted: bridge.llmDeleteOllamaModel.mock.calls.map(([req]) => req.tag),
            installed: rows(fixture, 'installed-models'),
            selected: text(fixture, 'metric-selected'),
            written: bridge.vaultStore.mock.calls,
          }).toEqual({
            deleted: [v.removedTag],
            installed: v.installed.filter((id) => id !== remove),
            selected: '—',
            written: [],
          });
        });
      }
    }
  });

  it('a list read that fails keeps every listed model, outside the catalog too, and says Not running', async () => {
    vi.useFakeTimers({ toFake: ['setInterval'] });
    try {
      const { fixture, bridge } = await dialog([
        listed('sample-chat:7b', { family: 'llama', parameterSize: '7B' }),
        listed('gemma3:4b'),
      ]);
      const before = rows(fixture, 'installed-models');
      bridge.llmListOllamaModels.mockRejectedValue(
        new OllamaUnreachableError('http://localhost:11434'),
      );
      vi.advanceTimersByTime(4000);
      await settle(fixture);

      expect({
        before,
        after: rows(fixture, 'installed-models'),
        meta: row(fixture, 'sample-chat:7b')?.meta,
        chip: text(fixture, 'panel-status'),
        written: bridge.vaultStore.mock.calls,
      }).toEqual({
        before: ['gemma-3-4b', 'sample-chat:7b'],
        after: ['gemma-3-4b', 'sample-chat:7b'],
        meta: 'llama · 7B · 1.9 GB on disk',
        chip: 'Not running',
        written: [],
      });
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });

  it('the Installed outside Scout preview state of Sources.dc.html: a model outside the catalog, an embeddings-only model and the model in use', async () => {
    const { fixture } = await dialog(
      [
        listed('gemma3:4b'),
        listed('sample-chat:3b', {
          family: 'llama',
          parameterSize: '3.2B',
          quantization: 'Q4_K_M',
        }),
        listed('sample-vectors:latest', { family: 'bert', capabilities: ['embedding'] }),
      ],
      { downloaded: ['gemma-3-4b'], analysis: 'gemma-3-4b' },
    );

    expect({
      installed: rows(fixture, 'installed-models'),
      inUse: row(fixture, 'gemma-3-4b'),
      inUseNote: text(fixture, 'in-use-note-gemma-3-4b'),
      outside: row(fixture, 'sample-chat:3b'),
      embeddings: row(fixture, 'sample-vectors:latest'),
    }).toEqual({
      installed: ['gemma-3-4b', 'sample-chat:3b', 'sample-vectors:latest'],
      inUse: {
        title: 'Gemma 3 4B',
        meta: 'Text and images · 3.3 GB on disk · 8 GB RAM',
        description: 'Recommended default. Handles both text and image analysis.',
        embeddingsOnly: null,
        useThis: false,
        remove: 'disabled',
      },
      inUseNote: 'In use. Choose another model before removing it.',
      outside: {
        title: 'sample-chat:3b',
        meta: 'llama · 3.2B · Q4_K_M · 1.9 GB on disk',
        description: null,
        embeddingsOnly: null,
        useThis: true,
        remove: 'enabled',
      },
      embeddings: {
        title: 'sample-vectors:latest',
        meta: 'bert · 1.9 GB on disk',
        description: null,
        embeddingsOnly: 'Embeddings only',
        useThis: false,
        remove: 'enabled',
      },
    });
  });
});

/** The names the Pull by name field may send to Ollama; the same rows are in the shared contract test and main's pull test. */
const OLLAMA_NAME_ROWS: readonly { name: string; ok: boolean }[] = [
  { name: 'sample-chat', ok: true },
  { name: 'sample-chat:3b', ok: true },
  { name: 'sample-chat2.5:0.5b', ok: true },
  { name: 'example-org/sample-chat:7b', ok: true },
  { name: 'sample_chat-v2:latest', ok: true },
  { name: 'sample-chat:7b-instruct-q4_K_M', ok: true },
  { name: `${'a'.repeat(80)}:${'b'.repeat(80)}`, ok: true },
  { name: '  sample-chat:3b  ', ok: false },
  { name: '', ok: false },
  { name: 'Sample-Chat:3b', ok: false },
  { name: 'sample chat:3b', ok: false },
  { name: 'sample-chat:', ok: false },
  { name: ':3b', ok: false },
  { name: 'sample-chat:3b:q4', ok: false },
  { name: 'registry.example/example-org/sample-chat:7b', ok: false },
  { name: 'registry.example/sample-chat:7b', ok: false },
  { name: 'https://registry.example/sample-chat', ok: false },
  { name: 'example-org/', ok: false },
  { name: '/sample-chat', ok: false },
  { name: '../sample-chat', ok: false },
  { name: 'sample\u0000chat', ok: false },
  { name: 'sample-chat\t3b', ok: false },
  { name: 'sample-chat?x=1', ok: false },
  { name: 'user@sample-chat', ok: false },
  { name: 'ollama run sample-chat', ok: false },
  { name: 'a'.repeat(81), ok: false },
  { name: '-sample-chat', ok: false },
];

/** The rows main refuses as sent and the dialog pulls once it trimmed the spaces around them. */
const TRIMMED_TO_A_NAME: ReadonlySet<string> = new Set(['  sample-chat:3b  ']);

describe('Pull by name in the Local models in Ollama dialog', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  const NAME_RULE = 'That is not a model name. Use name or name:tag, for example llama3.2:3b.';
  const NOT_ANSWERING =
    'Ollama is not answering. Open the Ollama app or run ollama serve, then scan again.';
  const SKILL = 'nomic-embed-text:latest';

  /** Main as Electron rejects it: the handler's message behind the invoke prefix, the code only in the marker. */
  function fromMain(code: string, message: string): Error {
    return new Error(
      `Error invoking remote method 'scout:llm.pullOllamaModel': Error: [scout:${code}] ${message}`,
    );
  }

  /**
   * A local Ollama that lists `installed` and every model a pull finished, as Ollama names it; each pull waits for
   * `answer`, and Cancel rejects the pull it names as main does.
   */
  function pullBridge(
    installed: readonly string[],
    answer: (tag: string) => Promise<void> = async () => undefined,
  ) {
    const names = [...installed];
    const cancels = new Map<string, () => void>();
    let progress: (event: { requestId: string; percent: number }) => void = () => undefined;
    const bridge = Object.assign(makeFakeBridge(), {
      vaultStore: vi.fn(async (_req: { key: string; value: string }) => undefined),
      vaultHas: vi.fn(async () => ({ exists: false })),
      llmListOllamaModels: vi.fn(async () => ({ models: names.map(ollamaModel) })),
      llmPullOllamaModel: vi.fn(
        (req: { tag: string; requestId: string }) =>
          new Promise<{ tag: string }>((resolve, reject) => {
            cancels.set(req.requestId, () =>
              reject(fromMain('llm/pull-cancelled', 'The download was cancelled.')),
            );
            answer(req.tag).then(() => {
              names.push(req.tag.includes(':') ? req.tag : `${req.tag}:latest`);
              resolve({ tag: req.tag });
            }, reject);
          }),
      ),
      llmCancelOllamaPull: vi.fn(async (req: { requestId: string }) =>
        cancels.get(req.requestId)?.(),
      ),
      onLlmPullOllamaProgress: vi.fn((listener: typeof progress) => {
        progress = listener;
        return () => undefined;
      }),
    });
    const report = (percent: number): void => {
      const [req] = bridge.llmPullOllamaModel.mock.calls.at(-1)! as unknown as [
        { tag: string; requestId: string },
      ];
      progress({ requestId: req.requestId, percent });
    };
    return { bridge, report };
  }

  async function settle(fixture: ComponentFixture<LocalModelsComponent>): Promise<void> {
    for (let i = 0; i < 8; i += 1) {
      await fixture.whenStable();
      await Promise.resolve();
      fixture.detectChanges();
    }
  }

  async function dialog(bridge: ReturnType<typeof pullBridge>['bridge']) {
    const { fixture } = await setup(
      bridge as unknown as ReturnType<typeof makeFakeBridge>,
      'analysis',
    );
    await settle(fixture);
    return fixture;
  }

  function el<T extends HTMLElement>(
    fixture: ComponentFixture<LocalModelsComponent>,
    test: string,
  ): T | null {
    return (fixture.nativeElement as HTMLElement).querySelector<T>(`[data-test="${test}"]`);
  }

  function text(fixture: ComponentFixture<LocalModelsComponent>, test: string): string | null {
    const found = el(fixture, test);
    return found === null ? null : (found.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  function type(fixture: ComponentFixture<LocalModelsComponent>, name: string): void {
    const input = el<HTMLInputElement>(fixture, 'pull-name')!;
    input.value = name;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /** Pull clicks the button; Enter is the form's submit, which a browser sends for Enter in the field. */
  function act(fixture: ComponentFixture<LocalModelsComponent>, action: 'Pull' | 'Enter'): void {
    if (action === 'Pull') el<HTMLButtonElement>(fixture, 'pull-submit')!.click();
    else
      el<HTMLFormElement>(fixture, 'pull-by-name')!.dispatchEvent(
        new Event('submit', { cancelable: true }),
      );
    fixture.detectChanges();
  }

  /** The block as the owner sees it, and what reached main and the vault. */
  function shown(
    fixture: ComponentFixture<LocalModelsComponent>,
    bridge: ReturnType<typeof pullBridge>['bridge'],
  ) {
    const input = el<HTMLInputElement>(fixture, 'pull-name')!;
    const submit = el<HTMLButtonElement>(fixture, 'pull-submit');
    return {
      pulled: bridge.llmPullOllamaModel.mock.calls.map(([req]) => req.tag),
      field: input.value,
      fieldDisabled: input.disabled,
      describedBy: input.getAttribute('aria-describedby'),
      pull: submit === null ? null : submit.disabled ? 'disabled' : 'enabled',
      refused: text(fixture, 'pull-refused'),
      error: text(fixture, 'pull-error-reason'),
      seeLogs: el(fixture, 'pull-error')?.querySelector('a')?.getAttribute('href') ?? null,
      done: text(fixture, 'pull-done'),
      installed: Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll(
          '[data-test="installed-models"] > li',
        ),
        (li) => li.getAttribute('data-test'),
      ),
      selected: text(fixture, 'metric-selected'),
      saved: bridge.vaultStore.mock.calls,
    };
  }

  for (const row of OLLAMA_NAME_ROWS) {
    for (const action of ['Pull', 'Enter'] as const) {
      const trimmed = row.name.trim();
      const pulls = row.ok || TRIMMED_TO_A_NAME.has(row.name);
      const outcome = !pulls
        ? 'refused before main'
        : trimmed === row.name
          ? 'pulled'
          : 'pulled once trimmed';
      it(`${JSON.stringify(row.name)} x ${action}: ${outcome}`, async () => {
        const { bridge } = pullBridge([SKILL]);
        const fixture = await dialog(bridge);

        type(fixture, row.name);
        const before = shown(fixture, bridge);
        act(fixture, action);
        await settle(fixture);
        const after = shown(fixture, bridge);
        const tag = trimmed.includes(':') ? trimmed : `${trimmed}:latest`;

        expect({
          pullBefore: before.pull,
          refused: before.refused,
          describedBy: before.describedBy,
          pulled: after.pulled,
          done: after.done,
        }).toEqual(
          pulls
            ? {
                pullBefore: 'enabled',
                refused: null,
                describedBy: null,
                pulled: [tag],
                done: `Pulled ${tag}. It is under Installed: Use this makes it the model Scout uses.`,
              }
            : {
                pullBefore: 'disabled',
                refused: trimmed === '' ? null : NAME_RULE,
                describedBy: trimmed === '' ? null : 'pull-name-error',
                pulled: [],
                done: null,
              },
        );
      });
    }
  }

  it.each([
    {
      outcome: 'done',
      answer: async () => undefined,
      error: null,
      field: '',
      installed: ['row-sample-chat:3b'],
      done: 'Pulled sample-chat:3b. It is under Installed: Use this makes it the model Scout uses.',
    },
    {
      outcome: 'the library has none',
      answer: async () => {
        throw fromMain('llm/model-not-found', 'pull model manifest: file does not exist');
      },
      error: 'The Ollama library has no model named sample-chat:3b.',
      field: 'sample-chat:3b',
      installed: [],
      done: null,
    },
    {
      outcome: 'Ollama not answering',
      answer: async () => {
        throw fromMain(
          'llm/ollama-unreachable',
          'Could not reach Ollama at http://localhost:11434',
        );
      },
      error: `${NOT_ANSWERING}`,
      field: 'sample-chat:3b',
      installed: [],
      done: null,
    },
    {
      outcome: 'HTTP 500',
      answer: async () => {
        throw fromMain('llm/pull-failed', 'Ollama returned HTTP 500');
      },
      error: 'Ollama could not download sample-chat:3b: Ollama returned HTTP 500',
      field: 'sample-chat:3b',
      installed: [],
      done: null,
    },
    {
      outcome: 'the stream broke off',
      answer: async () => {
        throw fromMain('llm/pull-failed', 'the download from Ollama broke off (Error: ECONNRESET)');
      },
      error:
        'Ollama could not download sample-chat:3b: the download from Ollama broke off (Error: ECONNRESET)',
      field: 'sample-chat:3b',
      installed: [],
      done: null,
    },
    {
      outcome: 'a stall',
      answer: async () => {
        throw fromMain('llm/pull-failed', 'Ollama sent nothing for 60 s; the download stopped.');
      },
      error:
        'Ollama could not download sample-chat:3b: Ollama sent nothing for 60 s; the download stopped.',
      field: 'sample-chat:3b',
      installed: [],
      done: null,
    },
    {
      outcome: 'the disk full',
      answer: async () => {
        throw fromMain('llm/pull-failed', 'no space left on device');
      },
      error: 'Ollama could not download sample-chat:3b: no space left on device',
      field: 'sample-chat:3b',
      installed: [],
      done: null,
    },
  ])(
    'a pull by name that ends as $outcome: the sentence under the field, the field, the list after, and nothing chosen or saved',
    async ({ answer, error, field, installed, done }) => {
      const { bridge } = pullBridge([SKILL], answer);
      const fixture = await dialog(bridge);

      type(fixture, 'sample-chat:3b');
      act(fixture, 'Pull');
      await settle(fixture);
      const after = shown(fixture, bridge);

      expect({
        pulled: after.pulled,
        error: after.error,
        seeLogs: after.seeLogs,
        describedBy: after.describedBy,
        field: after.field,
        installed: after.installed,
        done: after.done,
        selected: after.selected,
        saved: after.saved,
      }).toEqual({
        pulled: ['sample-chat:3b'],
        error,
        seeLogs: error === null ? null : '/logs',
        describedBy: error === null ? null : 'pull-name-error',
        field,
        installed,
        done,
        selected: '—',
        saved: [],
      });
    },
  );

  it('typing after a failed pull takes its sentence away, so a refused name shows only the rule, tied to the field', async () => {
    const { bridge } = pullBridge([SKILL], async () => {
      throw fromMain('llm/model-not-found', 'pull model manifest: file does not exist');
    });
    const fixture = await dialog(bridge);
    type(fixture, 'sample-chat:3b');
    act(fixture, 'Pull');
    await settle(fixture);
    const failed = shown(fixture, bridge).error;

    type(fixture, 'Sample-Chat:3b');
    const after = shown(fixture, bridge);

    expect({
      failed,
      error: after.error,
      refused: after.refused,
      describedBy: after.describedBy,
      described: (fixture.nativeElement as HTMLElement).querySelectorAll('#pull-name-error').length,
    }).toEqual({
      failed: 'The Ollama library has no model named sample-chat:3b.',
      error: null,
      refused: NAME_RULE,
      describedBy: 'pull-name-error',
      described: 1,
    });
  });

  it("a pull by name shows main's percent with Cancel and the field disabled; Cancel returns the field with the name and no error", async () => {
    let finish!: () => void;
    const { bridge, report } = pullBridge(
      [SKILL],
      () => new Promise<void>((resolve) => (finish = resolve)),
    );
    const fixture = await dialog(bridge);

    type(fixture, 'sample-chat:3b');
    act(fixture, 'Enter');
    report(42);
    fixture.detectChanges();
    const pulling = {
      field: shown(fixture, bridge).fieldDisabled,
      percent: text(fixture, 'pull-progress'),
      bar: el(fixture, 'pull-progress')
        ?.querySelector('[role="progressbar"]')
        ?.getAttribute('aria-valuenow'),
      cancel: el(fixture, 'pull-cancel')?.getAttribute('aria-label'),
      pull: el(fixture, 'pull-submit'),
    };
    el<HTMLButtonElement>(fixture, 'pull-cancel')!.click();
    await settle(fixture);
    const after = shown(fixture, bridge);

    expect({
      pulling,
      cancelled: bridge.llmCancelOllamaPull.mock.calls.length,
      after: {
        field: after.field,
        fieldDisabled: after.fieldDisabled,
        progress: el(fixture, 'pull-progress'),
        pull: after.pull,
        error: after.error,
        installed: after.installed,
      },
    }).toEqual({
      pulling: {
        field: true,
        percent: '42%',
        bar: '42',
        cancel: 'Cancel the download of sample-chat:3b',
        pull: null,
      },
      cancelled: 1,
      after: {
        field: 'sample-chat:3b',
        fieldDisabled: false,
        progress: null,
        pull: 'enabled',
        error: null,
        installed: [],
      },
    });
    finish();
  });

  it.each([
    {
      name: 'a name already installed',
      installed: [SKILL, 'sample-chat:3b'],
      typed: 'sample-chat:3b',
      pulled: ['sample-chat:3b'],
      after: ['row-sample-chat:3b'],
      skill: 'Installed',
    },
    {
      name: 'the skill-matching model by its name',
      installed: [],
      typed: 'nomic-embed-text',
      pulled: ['nomic-embed-text'],
      after: [],
      skill: 'Installed',
    },
    {
      name: 'a first model while the skill-matching model is missing',
      installed: [],
      typed: 'sample-chat:3b',
      pulled: ['sample-chat:3b', 'nomic-embed-text'],
      after: ['row-sample-chat:3b'],
      skill: 'Installed',
    },
    {
      name: 'a catalog model by its tag, with nothing chosen',
      installed: [SKILL],
      typed: 'gemma3:4b',
      pulled: ['gemma3:4b'],
      after: ['row-gemma-3-4b'],
      skill: 'Installed',
    },
  ])(
    'Pull on $name: requests $pulled in order, the list after, and no model put in use',
    async ({ installed, typed, pulled, after, skill }) => {
      const { bridge } = pullBridge(installed);
      const fixture = await dialog(bridge);

      type(fixture, typed);
      act(fixture, 'Pull');
      await settle(fixture);
      const shownAfter = shown(fixture, bridge);

      expect({
        pulled: shownAfter.pulled,
        installed: shownAfter.installed,
        skill: text(fixture, 'skill-installed'),
        selected: shownAfter.selected,
        saved: shownAfter.saved,
      }).toEqual({ pulled, installed: after, skill, selected: '—', saved: [] });
    },
  );

  it.each([
    {
      outcome: 'the library has none',
      error: fromMain('llm/model-not-found', 'pull model manifest: file does not exist'),
      shown: 'The Ollama library has no model named gemma3:4b.',
    },
    {
      outcome: 'Ollama not answering',
      error: fromMain('llm/ollama-unreachable', 'Could not reach Ollama at http://localhost:11434'),
      shown: `${NOT_ANSWERING}`,
    },
    {
      outcome: 'another error',
      error: fromMain('llm/pull-failed', 'no space left on device'),
      shown: 'Ollama could not download gemma3:4b: no space left on device',
    },
  ])(
    'a Download that ends as $outcome says it as a pull by name does',
    async ({ error, shown: said }) => {
      const { bridge } = pullBridge([SKILL], async () => {
        throw error;
      });
      const fixture = await dialog(bridge);

      el<HTMLButtonElement>(fixture, 'download-gemma-3-4b')!.click();
      await settle(fixture);

      expect(
        el(fixture, 'error-gemma-3-4b')?.querySelector('span')?.textContent?.trim() ?? null,
      ).toBe(said);
    },
  );

  it('a Download whose pull ends while an older list read is in flight is listed under Installed', async () => {
    const { bridge } = pullBridge([SKILL]);
    const fixture = await dialog(bridge);
    let answerOld!: (value: { models: FakeOllamaModel[] }) => void;
    bridge.llmListOllamaModels.mockImplementationOnce(
      () => new Promise((resolve) => (answerOld = resolve)),
    );
    void TestBed.inject(LocalModelsStore).refreshOllama();

    el<HTMLButtonElement>(fixture, 'download-gemma-3-4b')!.click();
    await settle(fixture);
    answerOld({ models: [SKILL].map(ollamaModel) });
    await settle(fixture);

    expect(shown(fixture, bridge).installed).toEqual(['row-gemma-3-4b']);
  });

  it.each([
    { browser: 'opens', fails: false, said: null },
    {
      browser: 'cannot be opened',
      fails: true,
      said: 'Could not open the browser. The library is at ollama.com/library.',
    },
  ])(
    'Browse the Ollama library asks main to open https://ollama.com/library; when the browser $browser, says so only when it cannot',
    async ({ fails, said }) => {
      const { bridge } = pullBridge([SKILL]);
      if (fails) {
        bridge.appOpenExternal.mockRejectedValueOnce(
          new Error(
            "Error invoking remote method 'scout:app.openExternal': Error: No application knows how to open URL https://ollama.com/library",
          ),
        );
      }
      const fixture = await dialog(bridge);

      el<HTMLButtonElement>(fixture, 'browse-library')!.click();
      await settle(fixture);

      expect({
        opened: bridge.appOpenExternal.mock.calls,
        said: text(fixture, 'browse-error'),
        pulls: bridge.llmPullOllamaModel.mock.calls.length,
      }).toEqual({
        opened: [[{ url: 'https://ollama.com/library' }]],
        said,
        pulls: 0,
      });
    },
  );

  it('the Pull by name preview states of Sources.dc.html: empty, refused, pulling, not in the library, failed, pulled', async () => {
    let fail: (err: Error) => void = () => undefined;
    let finish: () => void = () => undefined;
    const { bridge, report } = pullBridge(
      [SKILL],
      () =>
        new Promise<void>((resolve, reject) => {
          finish = resolve;
          fail = reject;
        }),
    );
    const fixture = await dialog(bridge);
    const state = () => {
      const now = shown(fixture, bridge);
      return {
        field: now.field,
        fieldDisabled: now.fieldDisabled,
        pull: now.pull,
        cancel: text(fixture, 'pull-cancel'),
        refused: now.refused,
        progress: text(fixture, 'pull-progress'),
        error: now.error,
        done: now.done,
      };
    };
    const states: Record<string, ReturnType<typeof state>> = {};

    states['empty'] = state();
    type(fixture, 'ollama run sample-chat');
    states['refused'] = state();
    type(fixture, 'sample-chat:3b');
    act(fixture, 'Pull');
    report(42);
    fixture.detectChanges();
    states['pulling'] = state();
    fail(fromMain('llm/model-not-found', 'pull model manifest: file does not exist'));
    await settle(fixture);
    states['not in the library'] = state();
    act(fixture, 'Pull');
    fail(fromMain('llm/pull-failed', 'no space left on device'));
    await settle(fixture);
    states['failed'] = state();
    act(fixture, 'Pull');
    finish();
    await settle(fixture);
    states['pulled'] = state();

    const idle = { fieldDisabled: false, cancel: null, refused: null, progress: null };
    expect(states).toEqual({
      empty: { ...idle, field: '', pull: 'disabled', error: null, done: null },
      refused: {
        ...idle,
        field: 'ollama run sample-chat',
        pull: 'disabled',
        refused: NAME_RULE,
        error: null,
        done: null,
      },
      pulling: {
        field: 'sample-chat:3b',
        fieldDisabled: true,
        pull: null,
        cancel: 'Cancel',
        refused: null,
        progress: '42%',
        error: null,
        done: null,
      },
      'not in the library': {
        ...idle,
        field: 'sample-chat:3b',
        pull: 'enabled',
        error: 'The Ollama library has no model named sample-chat:3b.',
        done: null,
      },
      failed: {
        ...idle,
        field: 'sample-chat:3b',
        pull: 'enabled',
        error: 'Ollama could not download sample-chat:3b: no space left on device',
        done: null,
      },
      pulled: {
        ...idle,
        field: '',
        pull: 'disabled',
        error: null,
        done: 'Pulled sample-chat:3b. It is under Installed: Use this makes it the model Scout uses.',
      },
    });
  });
});

describe('the tools warning in the Local models in Ollama dialog', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  const SENTENCE =
    'This model cannot call tools, so Scout cannot read your repositories or cluster with it. Choose a model that supports tools.';
  const MODEL = 'sample-chat:1b';

  function listedWith(
    name: string,
    capabilities: readonly string[] | null | undefined,
  ): OllamaModelInfo {
    const base = { name, modifiedAt: '', sizeBytes: 838_860_800 };
    return capabilities === undefined ? base : { ...base, capabilities };
  }

  async function settle(fixture: ComponentFixture<LocalModelsComponent>): Promise<void> {
    for (let i = 0; i < 6; i++) {
      await fixture.whenStable();
      await Promise.resolve();
      fixture.detectChanges();
    }
  }

  type State = 'in use with Ollama' | 'in use under a cloud provider' | 'not in use';

  /** Opens the Ollama view while the local Ollama lists `models`, with `inUse` the window's choice under `state`. */
  async function opened(models: readonly OllamaModelInfo[], inUse: string | null, state: State) {
    window.localStorage.setItem(
      LOCAL_MODELS_STORAGE_KEY,
      JSON.stringify({
        downloaded: models.map((m) => m.name),
        selected: { transcription: null, analysis: inUse },
      }),
    );
    const bridge = Object.assign(makeFakeBridge(), {
      vaultStore: vi.fn(async (_req: { key: string; value: string }) => undefined),
      llmUnloadOllamaModels: vi.fn(async () => ({ unloaded: [], refused: [] })),
    });
    bridge.llmListOllamaModels.mockImplementation(async () => ({ models: [...models] }));
    const { fixture } = await setup(bridge, 'analysis');
    const config = TestBed.inject(LlmConfigStore);
    if (state === 'in use with Ollama') await config.useOllama();
    if (state === 'in use under a cloud provider') await config.use('OpenAI', 'gpt-4.1');
    await settle(fixture);
    const handedOn: string[] = [];
    fixture.componentInstance.ollamaModelChosen.subscribe((id) => handedOn.push(id));
    return { fixture, bridge, handedOn };
  }

  /** What the dialog says about tools for `id`: its chip, the sentence under its row, Use this, and the panel's line. */
  function toolsWarning(fixture: ComponentFixture<LocalModelsComponent>, id: string) {
    const el = fixture.nativeElement as HTMLElement;
    const read = (test: string): string | null => {
      const found = el.querySelector(`[data-test="${test}"]`);
      return found === null ? null : (found.textContent ?? '').replace(/\s+/g, ' ').trim();
    };
    const useThis = el.querySelector<HTMLButtonElement>(`[data-test="select-${id}"]`);
    const describedBy = useThis?.getAttribute('aria-describedby') ?? null;
    return {
      chip: read(`no-tools-${id}`),
      sentence: read(`no-tools-note-${id}`),
      useThis: useThis === null ? 'none' : useThis.disabled ? 'disabled' : 'enabled',
      useThisDescribedBy:
        describedBy === null
          ? null
          : (el.querySelector(`#${CSS.escape(describedBy)}`)?.textContent ?? '').trim(),
      panelLine: read('in-use-no-tools'),
    };
  }

  const CAPABILITIES: readonly {
    readonly name: string;
    readonly capabilities: readonly string[] | null | undefined;
    readonly warns: boolean;
    readonly chatModel: boolean;
  }[] = [
    {
      name: 'not reported (no capabilities field)',
      capabilities: undefined,
      warns: false,
      chatModel: true,
    },
    {
      name: 'unknown after a failed read (main sends null)',
      capabilities: null,
      warns: false,
      chatModel: true,
    },
    { name: 'an empty list', capabilities: [], warns: true, chatModel: true },
    { name: 'completion only', capabilities: ['completion'], warns: true, chatModel: true },
    {
      name: 'completion and tools',
      capabilities: ['completion', 'tools'],
      warns: false,
      chatModel: true,
    },
    { name: 'embedding only', capabilities: ['embedding'], warns: false, chatModel: false },
    {
      name: 'completion and vision',
      capabilities: ['completion', 'vision'],
      warns: true,
      chatModel: true,
    },
    { name: 'tools only', capabilities: ['tools'], warns: false, chatModel: true },
  ];

  const STATES: readonly {
    readonly state: State;
    readonly inUse: string | null;
    readonly useThisShown: boolean;
    readonly panelLine: boolean;
  }[] = [
    { state: 'in use with Ollama', inUse: MODEL, useThisShown: false, panelLine: true },
    { state: 'in use under a cloud provider', inUse: MODEL, useThisShown: true, panelLine: false },
    { state: 'not in use', inUse: null, useThisShown: true, panelLine: false },
  ];

  for (const c of CAPABILITIES) {
    for (const s of STATES) {
      it(`capabilities ${c.name}, ${s.state}: the chip and the sentence on the row, Use this, and the LLM Server line`, async () => {
        const { fixture } = await opened([listedWith(MODEL, c.capabilities)], s.inUse, s.state);
        const useThisShown = s.useThisShown && c.chatModel;

        expect(toolsWarning(fixture, MODEL)).toEqual({
          chip: c.warns ? 'No tools' : null,
          sentence: c.warns ? SENTENCE : null,
          useThis: useThisShown ? 'enabled' : 'none',
          useThisDescribedBy: c.warns && useThisShown ? SENTENCE : null,
          panelLine: c.warns && s.panelLine ? SENTENCE : null,
        });
      });
    }
  }

  it('a recommended catalog model that is not installed says nothing about tools', async () => {
    const { fixture } = await opened([], null, 'not in use');

    expect(toolsWarning(fixture, 'gemma-3-12b')).toEqual({
      chip: null,
      sentence: null,
      useThis: 'none',
      useThisDescribedBy: null,
      panelLine: null,
    });
  });

  it('Use this on a model that cannot call tools still saves it as llm.model and hands it on, and the LLM Server panel then says it cannot call tools', async () => {
    const { fixture, bridge, handedOn } = await opened(
      [listedWith('sample-chat:7b', ['completion', 'tools']), listedWith(MODEL, ['completion'])],
      'sample-chat:7b',
      'in use with Ollama',
    );
    const before = toolsWarning(fixture, MODEL);
    bridge.vaultStore.mockClear();

    (
      (fixture.nativeElement as HTMLElement).querySelector(
        `[data-test="select-${MODEL}"]`,
      ) as HTMLButtonElement
    ).click();
    await settle(fixture);

    expect({
      before,
      saved: bridge.vaultStore.mock.calls,
      handedOn,
      after: toolsWarning(fixture, MODEL),
    }).toEqual({
      before: {
        chip: 'No tools',
        sentence: SENTENCE,
        useThis: 'enabled',
        useThisDescribedBy: SENTENCE,
        panelLine: null,
      },
      saved: [[{ key: 'llm.model', value: MODEL }]],
      handedOn: [MODEL],
      after: {
        chip: 'No tools',
        sentence: SENTENCE,
        useThis: 'none',
        useThisDescribedBy: null,
        panelLine: SENTENCE,
      },
    });
  });

  it('the No tools and In use without tools preview states of Sources.dc.html', async () => {
    const models = [
      listedWith('gemma3:4b', ['completion', 'tools']),
      {
        ...listedWith('example-chat:1b', ['completion']),
        family: 'example',
        parameterSize: '1.2B',
        quantization: 'Q4_K_M',
      },
    ];
    const notInUse = await opened(models, 'gemma-3-4b', 'in use with Ollama');
    const drawnNotInUse = {
      meta: (notInUse.fixture.nativeElement as HTMLElement)
        .querySelector('[data-test="row-example-chat:1b"] .model-meta')
        ?.textContent?.trim(),
      row: toolsWarning(notInUse.fixture, 'example-chat:1b'),
    };
    TestBed.resetTestingModule();
    const inUse = await opened(models, 'example-chat:1b', 'in use with Ollama');

    expect({ drawnNotInUse, drawnInUse: toolsWarning(inUse.fixture, 'example-chat:1b') }).toEqual({
      drawnNotInUse: {
        meta: 'example · 1.2B · Q4_K_M · 800 MB on disk',
        row: {
          chip: 'No tools',
          sentence: SENTENCE,
          useThis: 'enabled',
          useThisDescribedBy: SENTENCE,
          panelLine: null,
        },
      },
      drawnInUse: {
        chip: 'No tools',
        sentence: SENTENCE,
        useThis: 'none',
        useThisDescribedBy: null,
        panelLine: SENTENCE,
      },
    });
  });
});
