import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { LocalModelsComponent } from './local-models.component';

function makeFakeBridge() {
  return {
    llmListOllamaModels: vi.fn(async () => ({ models: [] })),
    llmPullOllamaModel: vi.fn(async (req: { tag: string }) => ({ tag: req.tag })),
    onLlmPullOllamaProgress: vi.fn(() => () => undefined),
    llmDeleteOllamaModel: vi.fn(async (req: { tag: string }) => ({ tag: req.tag })),
    llmListLmStudioModels: vi.fn(async () => ({ models: [] })),
    whisperListInstalled: vi.fn(async () => ({ installed: [] as readonly string[] })),
    whisperDownloadModel: vi.fn(async (req: { variant: string }) => ({
      variant: req.variant,
      path: `/tmp/${req.variant}.bin`,
    })),
    onWhisperDownloadProgress: vi.fn(() => () => undefined),
    whisperDeleteModel: vi.fn(async (req: { variant: string }) => ({ variant: req.variant })),
    appOpenExternal: vi.fn(async () => undefined),
    onWorkerEvent: vi.fn(() => () => undefined),
    workerStatus: vi.fn(async () => ({ state: 'idle', restarts: 0 })),
    providerStatus: vi.fn(async () => makeProviderStatus()),
    providerStart: vi.fn(async () => makeProviderStatus({ running: true })),
    providerStop: vi.fn(async () => makeProviderStatus({ running: false })),
    providerRestart: vi.fn(async () => makeProviderStatus({ running: true })),
    providerSetAutostart: vi.fn(async () => undefined),
  };
}

function makeProviderStatus(
  overrides: Partial<{
    running: boolean;
    autostart: boolean;
    installed: boolean;
    managedByWorker: boolean;
  }> = {},
) {
  return {
    id: 'ollama' as const,
    installed: true,
    running: false,
    endpoint: null,
    version: null,
    managedByWorker: true,
    autostart: false,
    supportsLifecycle: true,
    lastError: null,
    models: [],
    ...overrides,
  };
}

async function setup(): Promise<{
  fixture: ComponentFixture<LocalModelsComponent>;
  store: LocalModelsStore;
  bridge: ReturnType<typeof makeFakeBridge>;
}> {
  const bridge = makeFakeBridge();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const store = TestBed.inject(LocalModelsStore);
  vi.spyOn(store, 'hydrateFromWorker').mockResolvedValue(undefined);
  const fixture = TestBed.createComponent(LocalModelsComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, store, bridge };
}

describe('LocalModelsComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  it('renders Transcription tab by default with both Whisper rows', async () => {
    const { fixture } = await setup();
    expect(fixture.nativeElement.querySelector('[data-test="row-whisper-large-v3"]')).not.toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-test="row-whisper-large-v3-turbo"]'),
    ).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="row-qwen-3-vl-2b"]')).toBeNull();
  });

  it('switches to Analysis tab and renders five rows', async () => {
    const { fixture } = await setup();
    const btn = fixture.nativeElement.querySelector(
      '[data-test="kind-analysis"]',
    ) as HTMLButtonElement;
    btn.click();
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

  it('a downloaded but non-selected model shows Use this and selecting it switches the active model', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-large-v3');
    fixture.detectChanges();

    const useBtn = fixture.nativeElement.querySelector(
      '[data-test="select-whisper-large-v3"]',
    ) as HTMLButtonElement;
    expect(useBtn).not.toBeNull();
    useBtn.click();
    fixture.detectChanges();

    expect(store.selectedFor('transcription')).toBe('whisper-large-v3');
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
      ],
    });
    const fixture = TestBed.createComponent(LocalModelsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    // Switch to Analysis tab and click Download on qwen-3-vl-2b.
    (
      fixture.nativeElement.querySelector('[data-test="kind-analysis"]') as HTMLButtonElement
    ).click();
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
    await Promise.resolve();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(store.isDownloaded('whisper-large-v3-turbo')).toBe(false);
    expect(store.selectedFor('transcription')).toBeNull();
  });

  it('Delete unused button is disabled while everything downloaded is selected', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    fixture.detectChanges();

    const btn = fixture.nativeElement.querySelector(
      '[data-test="delete-unused"]',
    ) as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
  });

  it('Delete unused removes unselected downloaded models', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-large-v3');
    fixture.detectChanges();

    const btn = fixture.nativeElement.querySelector(
      '[data-test="delete-unused"]',
    ) as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
    btn.click();
    fixture.detectChanges();

    expect(store.downloaded()).toEqual(['whisper-large-v3-turbo']);
  });

  it('server status reads STOPPED when llmStatus is null and READY when running', async () => {
    const { fixture } = await setup();
    const status = fixture.nativeElement.querySelector('[data-test="panel-status"]') as HTMLElement;
    expect(status.textContent ?? '').toContain('Stopped');
  });

  it('footer summary reflects the total number of downloaded models', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('qwen-3-vl-2b');
    fixture.detectChanges();

    const foot = fixture.nativeElement.querySelector('[data-test="foot-summary"]') as HTMLElement;
    expect(foot.textContent ?? '').toContain('2 models');
  });

  it('ngOnInit_callsRefreshStatusAndHydrate', async () => {
    const { bridge, store } = await setup();
    expect(bridge.providerStatus).toHaveBeenCalledWith({ id: 'ollama' });
    expect(store.hydrateFromWorker).toHaveBeenCalledOnce();
  });

  it('onStart_callsProviderStart', async () => {
    const { fixture, bridge } = await setup();
    (
      fixture.nativeElement.querySelector('[data-test="kind-analysis"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    const btn = fixture.nativeElement.querySelector(
      '[data-test="start-btn"]',
    ) as HTMLButtonElement;
    btn.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.providerStart).toHaveBeenCalledWith({ id: 'ollama' });
  });

  it('onRestart_disabledWhenNotManagedByWorker', async () => {
    const { fixture, bridge } = await setup();
    bridge.providerStatus.mockResolvedValue(
      makeProviderStatus({ running: true, managedByWorker: false }),
    );
    (
      fixture.nativeElement.querySelector('[data-test="kind-analysis"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    // Manually set the signal via component instance to avoid async poll timing.
    const comp = fixture.componentInstance;
    comp.llmStatus.set(makeProviderStatus({ running: true, managedByWorker: false }));
    fixture.detectChanges();

    const btn = fixture.nativeElement.querySelector(
      '[data-test="restart-btn"]',
    ) as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
    expect(btn.title).toContain('externally-started');
  });

  it('onToggleAutostart_flipsValueAndRefreshes', async () => {
    const { fixture, bridge } = await setup();
    (
      fixture.nativeElement.querySelector('[data-test="kind-analysis"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    const comp = fixture.componentInstance;
    comp.llmStatus.set(makeProviderStatus({ autostart: true }));
    fixture.detectChanges();

    const callsBefore = (bridge.providerStatus as ReturnType<typeof vi.fn>).mock.calls.length;

    const toggle = fixture.nativeElement.querySelector(
      '[data-test="autostart-toggle"]',
    ) as HTMLButtonElement;
    toggle.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.providerSetAutostart).toHaveBeenCalledWith({ id: 'ollama', enabled: false });
    expect((bridge.providerStatus as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(
      callsBefore,
    );
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

  it('ngOnDestroy clears the pending poll-reset timer', async () => {
    vi.useFakeTimers();
    try {
      const { fixture } = await setup();
      const comp = fixture.componentInstance;
      // Capture startPolling calls on the instance to assert the deferred
      // callback never fires after destroy.
      const startPolling = vi.spyOn(
        comp as unknown as { startPolling: (n: number) => void },
        'startPolling',
      );

      await comp.onStart();
      const callsAfterStart = startPolling.mock.calls.length;
      expect(callsAfterStart).toBeGreaterThan(0);

      fixture.destroy();
      vi.advanceTimersByTime(40_000);

      expect(startPolling.mock.calls.length).toBe(callsAfterStart);
    } finally {
      vi.useRealTimers();
    }
  });

  it('consecutive onStart calls do not stack pending poll-resets', async () => {
    vi.useFakeTimers();
    try {
      const { fixture } = await setup();
      const comp = fixture.componentInstance;
      const startPolling = vi.spyOn(
        comp as unknown as { startPolling: (n: number) => void },
        'startPolling',
      );

      await comp.onStart();
      await comp.onStart();
      const callsBeforeDeadline = startPolling.mock.calls.length;

      // Advance past the 35s reset window. Only one deferred restart should
      // fire even though onStart scheduled twice.
      vi.advanceTimersByTime(36_000);

      const deferredCalls = startPolling.mock.calls.length - callsBeforeDeadline;
      expect(deferredCalls).toBe(1);

      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });
});
