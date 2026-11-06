import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LLM_CONFIG_STORAGE_KEY } from '../core/llm-config.store';
import { LogStore } from '../core/log.store';
import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { ModelsComponent } from './models.component';

class FakeLogStore {
  private state: { state: string; restarts: number } = { state: 'idle', restarts: 0 };
  status(): { state: string; restarts: number } {
    return this.state;
  }
  setRunning(running: boolean): void {
    this.state = { state: running ? 'running' : 'idle', restarts: 0 };
  }
}

async function setup(opts: { running?: boolean } = {}): Promise<{
  fixture: ComponentFixture<ModelsComponent>;
  store: LocalModelsStore;
  logStore: FakeLogStore;
}> {
  const logStore = new FakeLogStore();
  if (opts.running) logStore.setRunning(true);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: LogStore, useValue: logStore },
    ],
  });
  vi.spyOn(TestBed.inject(ScoutBridgeService), 'llmListOllamaModels').mockResolvedValue({
    models: [],
  });
  const store = TestBed.inject(LocalModelsStore);
  const fixture = TestBed.createComponent(ModelsComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  await new Promise((resolve) => setTimeout(resolve, 0));
  fixture.detectChanges();
  return { fixture, store, logStore };
}

describe('ModelsComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  it('renders three tabs with Translation disabled', async () => {
    const { fixture } = await setup();

    const tr = fixture.nativeElement.querySelector(
      '[data-test="tab-transcription"]',
    ) as HTMLButtonElement;
    const an = fixture.nativeElement.querySelector(
      '[data-test="tab-analysis"]',
    ) as HTMLButtonElement;
    const tl = fixture.nativeElement.querySelector(
      '[data-test="tab-translation"]',
    ) as HTMLButtonElement;

    expect(tr).not.toBeNull();
    expect(an).not.toBeNull();
    expect(tl).not.toBeNull();
    expect(tl.disabled).toBe(true);
  });

  it('shows the empty state on Transcription when nothing downloaded', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="empty-state"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="empty-cta"]')).not.toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-test="option-whisper-large-v3-turbo"]'),
    ).toBeNull();
  });

  it('shows only the downloaded models in Transcription', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('[data-test="option-whisper-large-v3-turbo"]'),
    ).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="option-whisper-large-v3"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="empty-state"]')).toBeNull();
  });

  it('shows the Selected pill once a model is auto-selected by download', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-test="selected-pill"]') as HTMLElement;
    expect(pill).not.toBeNull();
    expect(pill.textContent ?? '').toContain('Whisper Large v3 Turbo');
  });

  it('clicking an option changes the selected model via the store', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-large-v3');
    fixture.detectChanges();

    const v3 = fixture.nativeElement.querySelector(
      '[data-test="option-whisper-large-v3"]',
    ) as HTMLButtonElement;
    v3.click();
    fixture.detectChanges();

    expect(store.selectedFor('transcription')).toBe('whisper-large-v3');
  });

  it('Analysis tab shows the Local LLM downloaded models', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('qwen-3-vl-2b');

    const an = fixture.nativeElement.querySelector(
      '[data-test="tab-analysis"]',
    ) as HTMLButtonElement;
    an.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="option-qwen-3-vl-2b"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="panel-analysis"]')).not.toBeNull();
  });

  it('locks Transcription options when worker is running', async () => {
    const { fixture, store } = await setup({ running: true });
    store.markDownloaded('whisper-large-v3-turbo');
    fixture.detectChanges();

    const opt = fixture.nativeElement.querySelector(
      '[data-test="option-whisper-large-v3-turbo"]',
    ) as HTMLButtonElement;
    expect(opt.disabled).toBe(true);
  });

  it('Translation tab cannot be activated', async () => {
    const { fixture } = await setup();
    const tl = fixture.nativeElement.querySelector(
      '[data-test="tab-translation"]',
    ) as HTMLButtonElement;
    tl.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-test="panel-transcription"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="panel-translation"]')).toBeNull();
  });

  it('Analysis tab lists every downloaded local model and marks only the selected one', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('qwen-2.5-0.5b-instruct');
    store.markDownloaded('qwen-2.5-1.5b-instruct');
    store.markDownloaded('qwen-3-vl-2b');
    store.select('analysis', 'qwen-2.5-1.5b-instruct');

    const an = fixture.nativeElement.querySelector(
      '[data-test="tab-analysis"]',
    ) as HTMLButtonElement;
    an.click();
    fixture.detectChanges();

    const options = fixture.nativeElement.querySelectorAll(
      '[data-test^="option-"]',
    ) as NodeListOf<HTMLElement>;
    expect(options.length).toBe(3);
    const selected = fixture.nativeElement.querySelectorAll(
      '.option--selected',
    ) as NodeListOf<HTMLElement>;
    expect(selected.length).toBe(1);
    expect(selected[0]?.getAttribute('data-test')).toBe('option-qwen-2.5-1.5b-instruct');
    expect(fixture.nativeElement.querySelector('[data-test="no-active-model-nudge"]')).toBeNull();
  });

  it('Analysis tab with no selection renders all downloaded options plus a nudge', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('qwen-2.5-0.5b-instruct');
    store.markDownloaded('qwen-2.5-1.5b-instruct');
    store.markDownloaded('qwen-3-vl-2b');
    // Wipe the auto-selection so the nudge path is exercised. The store API
    // does not expose a clear-selection method, so simulate the "no selection"
    // state by purging localStorage and re-hydrating via a fresh component.
    // Easiest path: reach into the private signal through localStorage reset.
    window.localStorage.setItem(
      LOCAL_MODELS_STORAGE_KEY,
      JSON.stringify({
        downloaded: ['qwen-2.5-0.5b-instruct', 'qwen-2.5-1.5b-instruct', 'qwen-3-vl-2b'],
        selected: { transcription: null, analysis: null },
      }),
    );
    TestBed.resetTestingModule();
    const fresh = await setup();

    const an = fresh.fixture.nativeElement.querySelector(
      '[data-test="tab-analysis"]',
    ) as HTMLButtonElement;
    an.click();
    fresh.fixture.detectChanges();

    const options = fresh.fixture.nativeElement.querySelectorAll(
      '[data-test^="option-"]',
    ) as NodeListOf<HTMLElement>;
    expect(options.length).toBe(3);
    expect(
      fresh.fixture.nativeElement.querySelector('[data-test="no-active-model-nudge"]'),
    ).not.toBeNull();
    // store and fixture from outer setup are unused once we rehydrate
    void store;
    void fixture;
  });

  it('Transcription tab still renders the full downloaded list (no narrowing)', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-large-v3');
    fixture.detectChanges();

    const options = fixture.nativeElement.querySelectorAll(
      '[data-test^="option-"]',
    ) as NodeListOf<HTMLElement>;
    expect(options.length).toBe(2);
    expect(
      fixture.nativeElement.querySelector('[data-test="option-whisper-large-v3-turbo"]'),
    ).not.toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-test="option-whisper-large-v3"]'),
    ).not.toBeNull();
  });
});

interface BridgeOptions {
  readonly keys?: readonly string[];
  readonly publicValues?: Readonly<Record<string, string>>;
  readonly chatgptSignedIn?: boolean;
  readonly workerState?: string;
  readonly workerStartError?: unknown;
  readonly failingLists?: readonly string[];
  readonly slowStoreKeys?: readonly string[];
  readonly localRuntime?: boolean;
  readonly lmStudioRuntime?: boolean;
  readonly cloudModels?: Readonly<Record<string, readonly { id: string; displayName: string }[]>>;
}

const CHATGPT_MODELS = [
  { slug: 'gpt-6.1-sol', displayName: 'GPT-6.1 Sol' },
  { slug: 'gpt-5.6-luna', displayName: 'GPT-5.6 Luna' },
];
const CLOUD_MODELS: Readonly<Record<string, readonly { id: string; displayName: string }[]>> = {
  OpenAI: [
    { id: 'gpt-4.1', displayName: 'gpt-4.1' },
    { id: 'gpt-4o-mini', displayName: 'gpt-4o-mini' },
  ],
  Gemini: [{ id: 'gemini-2.5-flash', displayName: 'Gemini 2.5 Flash' }],
};

function makeBridge(opts: BridgeOptions = {}) {
  const stores: { key: string; value: string }[] = [];
  const events: string[] = [];
  const failing = new Set(opts.failingLists ?? []);
  const slow = new Set(opts.slowStoreKeys ?? []);
  return {
    stores,
    events,
    vaultStore: vi.fn(async (req: { key: string; value: string }) => {
      stores.push(req);
      if (slow.has(req.key)) await new Promise((resolve) => setTimeout(resolve, 20));
      events.push(`stored ${req.key}`);
    }),
    vaultHas: vi.fn(async (req: { key: string }) => ({
      exists: (opts.keys ?? []).includes(req.key),
    })),
    vaultGetPublic: vi.fn(async (req: { key: string }) => ({
      value: opts.publicValues?.[req.key] ?? null,
    })),
    chatgptStatus: vi.fn(async () => ({
      signedIn: opts.chatgptSignedIn ?? false,
      email: null,
      planUsageGranted: opts.chatgptSignedIn ?? false,
    })),
    chatgptListModels: vi.fn(async () => {
      if (failing.has('ChatGptPlan')) throw new Error('[scout:providers/unreachable] down');
      return { models: CHATGPT_MODELS };
    }),
    llmListModels: vi.fn(async (req: { provider: string }) => {
      if (failing.has(req.provider)) throw new Error('[scout:providers/unreachable] down');
      return { models: (opts.cloudModels ?? CLOUD_MODELS)[req.provider] ?? [] };
    }),
    llmListOllamaModels: vi.fn(async () => {
      if (opts.localRuntime !== true)
        throw new Error('[scout:llm/unreachable] connect ECONNREFUSED');
      return { models: [] };
    }),
    llmListLmStudioModels: vi.fn(async () => {
      if (opts.lmStudioRuntime !== true)
        throw new Error('[scout:llm/unreachable] connect ECONNREFUSED');
      return { models: [] };
    }),
    workerStatus: vi.fn(async () => ({ state: opts.workerState ?? 'idle', restarts: 0 })),
    workerStop: vi.fn(async () => {
      events.push('workerStop');
      return { exitCode: 0 };
    }),
    workerStart: vi.fn(async () => {
      if (opts.workerStartError !== undefined) throw opts.workerStartError;
      return { pid: 7 };
    }),
  };
}

type Bridge = ReturnType<typeof makeBridge>;

async function settle(fixture: ComponentFixture<ModelsComponent>): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

async function openAnalysis(
  opts: BridgeOptions = {},
  prepare: (store: LocalModelsStore) => void = () => undefined,
): Promise<{ fixture: ComponentFixture<ModelsComponent>; bridge: Bridge; el: HTMLElement }> {
  const bridge = makeBridge(opts);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: LogStore, useValue: new FakeLogStore() },
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  await TestBed.inject(Router).navigateByUrl('/?tab=analysis');
  prepare(TestBed.inject(LocalModelsStore));
  const fixture = TestBed.createComponent(ModelsComponent);
  fixture.detectChanges();
  await settle(fixture);
  return { fixture, bridge, el: fixture.nativeElement as HTMLElement };
}

const LONG_OPENAI = [
  'gpt-4o-mini',
  'gpt-4o',
  'gpt-4.1-mini',
  'gpt-4.1',
  'o3',
  'o4-mini',
  'gpt-5-mini',
  'gpt-5.1',
  'chatgpt-4o-latest',
  'gpt-5-nano',
].map((id) => ({ id, displayName: id }));

function rowIds(el: HTMLElement, key: string): string[] {
  return [...el.querySelectorAll(`[data-test^="model-${key}-"]`)].map(
    (row) => row.getAttribute('data-test')?.slice(`model-${key}-`.length) ?? '',
  );
}

function localRowIds(el: HTMLElement): string[] {
  return [...el.querySelectorAll('[data-test^="option-"]')].map(
    (row) => row.getAttribute('data-test')?.slice('option-'.length) ?? '',
  );
}

function text(el: HTMLElement, dataTest: string): string {
  const root = el.querySelector(`[data-test="${dataTest}"]`);
  if (root === null) return '';
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const words: string[] = [];
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    const value = node.textContent?.replace(/\s+/g, ' ').trim() ?? '';
    if (value !== '') words.push(value);
  }
  return words.join(' ');
}

function isOpen(el: HTMLElement, key: string): boolean | null {
  const group = el.querySelector(`[data-test="provider-${key}"]`) as HTMLDetailsElement | null;
  return group === null ? null : group.open;
}

async function search(
  fixture: ComponentFixture<ModelsComponent>,
  el: HTMLElement,
  query: string,
): Promise<void> {
  const input = el.querySelector('[data-test="model-search"]') as HTMLInputElement;
  input.value = query;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

function click(el: HTMLElement, dataTest: string): void {
  const button = el.querySelector(`[data-test="${dataTest}"]`) as HTMLButtonElement | null;
  if (button === null) throw new Error(`no element ${dataTest}`);
  button.click();
}

describe('ModelsComponent - Analysis per connected provider', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  it('opens on Analysis when AI Sources links here with tab=analysis', async () => {
    const { el } = await openAnalysis();

    expect(el.querySelector('[data-test="panel-analysis"]')).not.toBeNull();
    expect(el.querySelector('[data-test="panel-transcription"]')).toBeNull();
  });

  it("when connected to a provider, its models show there: one block per connected provider, listing that provider's models", async () => {
    const { el } = await openAnalysis({ keys: ['llm.openai.api-key'], chatgptSignedIn: true });

    expect(el.querySelector('[data-test="provider-chatgpt"]')).not.toBeNull();
    expect(el.querySelector('[data-test="provider-openai"]')).not.toBeNull();
    expect(el.querySelector('[data-test="model-chatgpt-gpt-6.1-sol"]')?.textContent).toContain(
      'GPT-6.1 Sol',
    );
    expect(el.querySelector('[data-test="model-openai-gpt-4o-mini"]')).not.toBeNull();
    expect(el.querySelector('[data-test="provider-gemini"]')).toBeNull();
  });

  it('a disconnected provider shows no block and its model list is never requested', async () => {
    const { el, bridge } = await openAnalysis();

    expect(el.querySelector('[data-test="provider-chatgpt"]')).toBeNull();
    expect(el.querySelector('[data-test="provider-openai"]')).toBeNull();
    expect(el.querySelector('[data-test="provider-gemini"]')).toBeNull();
    expect(bridge.chatgptListModels).not.toHaveBeenCalled();
    expect(bridge.llmListModels).not.toHaveBeenCalled();
  });

  it('the person can select which one to use: choosing a ChatGPT model writes provider ChatGptPlan and that model, then restarts a running Scout', async () => {
    const { fixture, bridge, el } = await openAnalysis({
      chatgptSignedIn: true,
      workerState: 'running',
    });

    click(el, 'model-chatgpt-gpt-6.1-sol');
    await settle(fixture);

    expect(bridge.stores).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-6.1-sol' },
      { key: 'llm.provider', value: 'ChatGptPlan' },
    ]);
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(bridge.vaultStore.mock.invocationCallOrder[1]).toBeLessThan(
      bridge.workerStop.mock.invocationCallOrder[0] ?? 0,
    );
    expect(el.querySelector('[data-test="analysis-status"]')?.textContent).toContain(
      'Restarting Scout to use ChatGPT.',
    );
  });

  it('choosing an OpenAI model while ChatGPT is in use switches the provider to OpenAI and moves the Selected marker', async () => {
    const { fixture, bridge, el } = await openAnalysis({
      keys: ['llm.openai.api-key'],
      chatgptSignedIn: true,
      publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
    });
    expect(
      el
        .querySelector('[data-test="model-chatgpt-gpt-5.6-luna"]')
        ?.classList.contains('option--selected'),
    ).toBe(true);

    click(el, 'model-openai-gpt-4.1');
    await settle(fixture);

    expect(bridge.stores).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-4.1' },
      { key: 'llm.provider', value: 'OpenAI' },
    ]);
    const selected = el.querySelectorAll('.option--selected');
    expect(selected.length).toBe(1);
    expect(selected[0]?.getAttribute('data-test')).toBe('model-openai-gpt-4.1');
    expect(el.querySelector('[data-test="analysis-status"]')?.textContent).toContain(
      'Scout will use OpenAI the next time it starts.',
    );
    expect(bridge.workerStop).not.toHaveBeenCalled();
  });

  it('exactly one Selected across all blocks: a cloud provider in use leaves the downloaded local model unmarked, and choosing the local model writes Ollama before the restart', async () => {
    const { fixture, bridge, el } = await openAnalysis(
      {
        chatgptSignedIn: true,
        workerState: 'running',
        publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-6.1-sol' },
        localRuntime: true,
      },
      (store) => store.markDownloaded('qwen-3-vl-2b'),
    );
    bridge.stores.length = 0;
    bridge.vaultStore.mockClear();

    expect(el.querySelectorAll('.option--selected').length).toBe(1);
    expect(el.querySelectorAll('[data-test^="selected-pill"]').length).toBe(1);
    expect(el.querySelector('[data-test="selected-pill-chatgpt"]')).not.toBeNull();

    click(el, 'option-qwen-3-vl-2b');
    await settle(fixture);

    const selected = el.querySelectorAll('.option--selected');
    expect(selected.length).toBe(1);
    expect(selected[0]?.getAttribute('data-test')).toBe('option-qwen-3-vl-2b');
    expect(el.querySelectorAll('[data-test^="selected-pill"]').length).toBe(1);
    expect(bridge.stores).toContainEqual({ key: 'llm.provider', value: 'Ollama' });
    expect(bridge.stores).toContainEqual({ key: 'llm.model', value: 'qwen2.5:3b' });
    expect(bridge.stores.map((entry) => entry.key)).not.toContain('llm.endpoint');
    const lastStore = Math.max(...bridge.vaultStore.mock.invocationCallOrder);
    expect(lastStore).toBeLessThan(bridge.workerStop.mock.invocationCallOrder[0] ?? 0);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('choosing another model does not show the previous message while the worker status is pending', async () => {
    const { fixture, bridge, el } = await openAnalysis({ keys: ['llm.openai.api-key'] });
    click(el, 'model-openai-gpt-4.1');
    await settle(fixture);
    expect(el.querySelector('[data-test="analysis-status"]')?.textContent).toContain(
      'the next time it starts',
    );

    let resolveStatus: (value: { state: string; restarts: number }) => void = () => undefined;
    bridge.workerStatus.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveStatus = resolve;
        }),
    );
    click(el, 'model-openai-gpt-4o-mini');
    await settle(fixture);

    expect(bridge.stores.at(-2)).toEqual({ key: 'llm.cloud-model', value: 'gpt-4o-mini' });
    expect(el.querySelector('[data-test="analysis-status"]')).toBeNull();

    resolveStatus({ state: 'running', restarts: 0 });
    await settle(fixture);
    expect(el.querySelector('[data-test="analysis-status"]')?.textContent).toContain(
      'Restarting Scout to use OpenAI.',
    );
  });

  it('choosing a local model finishes writing its Ollama model before Scout restarts', async () => {
    const { fixture, bridge, el } = await openAnalysis(
      {
        chatgptSignedIn: true,
        workerState: 'running',
        publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-6.1-sol' },
        slowStoreKeys: ['llm.model'],
        localRuntime: true,
      },
      (store) => store.markDownloaded('qwen-3-vl-2b'),
    );
    await new Promise((resolve) => setTimeout(resolve, 40));
    bridge.events.length = 0;

    click(el, 'option-qwen-3-vl-2b');
    await new Promise((resolve) => setTimeout(resolve, 60));
    await settle(fixture);

    const stop = bridge.events.indexOf('workerStop');
    expect(stop).toBeGreaterThan(-1);
    expect(bridge.events.slice(0, stop)).toContain('stored llm.model');
    expect(bridge.events.slice(0, stop)).toContain('stored llm.provider');
  });

  it('a model list that cannot be loaded says so in that block and leaves the other blocks listing', async () => {
    const { el } = await openAnalysis({
      keys: ['llm.openai.api-key', 'llm.gemini.api-key'],
      failingLists: ['OpenAI'],
    });

    expect(el.querySelector('[data-test="models-error-openai"]')?.textContent?.trim()).toBe(
      'Could not load the OpenAI model list.',
    );
    expect(el.querySelector('[data-test="model-gemini-gemini-2.5-flash"]')).not.toBeNull();
  });

  it('a failed restart after choosing a model shows the error and keeps the new model selected', async () => {
    const { fixture, el } = await openAnalysis({
      keys: ['llm.gemini.api-key'],
      workerState: 'running',
      workerStartError: new Error('spawn failed'),
    });

    click(el, 'model-gemini-gemini-2.5-flash');
    await settle(fixture);

    expect(el.querySelector('[data-test="analysis-error"]')?.textContent).toContain('spawn failed');
    expect(el.querySelector('[data-test="analysis-status"]')?.textContent).toContain(
      'Restart Scout from the status screen to use Gemini.',
    );
    expect(
      el
        .querySelector('[data-test="model-gemini-gemini-2.5-flash"]')
        ?.classList.contains('option--selected'),
    ).toBe(true);
  });

  it('the Analysis tab shows the model in use and its source at the top', async () => {
    const cloud = await openAnalysis({
      keys: ['llm.openai.api-key'],
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4.1' },
    });
    expect(text(cloud.el, 'in-use')).toBe('In use gpt-4.1 via OpenAI API');
    expect(cloud.el.querySelector('[data-test="no-active-model-nudge"]')).toBeNull();

    TestBed.resetTestingModule();
    const local = await openAnalysis({ localRuntime: true }, (store) =>
      store.markDownloaded('qwen-3-vl-2b'),
    );
    expect(text(local.el, 'in-use')).toBe('In use Qwen 2.5 3B via Ollama');

    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    const none = await openAnalysis({ keys: ['llm.gemini.api-key'] });
    expect(none.el.querySelector('[data-test="in-use"]')).toBeNull();
    expect(none.el.querySelector('[data-test="no-active-model-nudge"]')).not.toBeNull();
  });

  it('with no local runtime connected the local models are not listed and a Not connected row points to AI Sources', async () => {
    const { el, bridge } = await openAnalysis({}, (store) => store.markDownloaded('qwen-3-vl-2b'));

    expect(bridge.llmListOllamaModels).toHaveBeenCalled();
    expect(el.querySelector('[data-test="provider-ollama"]')).toBeNull();
    expect(el.querySelector('[data-test="option-qwen-3-vl-2b"]')).toBeNull();
    expect(el.querySelector('[data-test="in-use"]')).toBeNull();
    expect(text(el, 'local-not-connected')).toContain('Local LLM');
    expect(text(el, 'local-not-connected')).toContain('Not connected');
    expect(el.querySelector('[data-test="local-not-connected"] a')?.getAttribute('href')).toBe(
      '/sources',
    );
  });

  it('a connected local runtime with downloaded models lists them in the local group', async () => {
    const { el } = await openAnalysis({ localRuntime: true }, (store) => {
      store.markDownloaded('qwen-2.5-1.5b-instruct');
      store.markDownloaded('gemma-3-4b');
    });

    expect(el.querySelector('[data-test="provider-ollama"]')).not.toBeNull();
    expect(localRowIds(el)).toEqual(['qwen-2.5-1.5b-instruct', 'gemma-3-4b']);
    expect(el.querySelector('[data-test="local-not-connected"]')).toBeNull();
  });

  it('a local runtime reached only through LM Studio counts as connected and lists the downloaded models', async () => {
    const { el } = await openAnalysis({ lmStudioRuntime: true }, (store) =>
      store.markDownloaded('gemma-3-4b'),
    );

    expect(localRowIds(el)).toEqual(['gemma-3-4b']);
    expect(el.querySelector('[data-test="local-not-connected"]')).toBeNull();
  });

  it('a connected local runtime with no downloaded model keeps the download prompt', async () => {
    const { el } = await openAnalysis({ localRuntime: true });

    expect(el.querySelector('[data-test="empty-state"]')).not.toBeNull();
    expect(el.querySelector('[data-test="empty-cta"]')?.getAttribute('href')).toBe(
      '/sources/local-models',
    );
    expect(el.querySelector('[data-test="local-not-connected"]')).toBeNull();
  });

  it('each connected source is a collapsible group with its selected model and count on the summary, open only for the source in use', async () => {
    const { fixture, el } = await openAnalysis(
      {
        keys: ['llm.openai.api-key', 'llm.gemini.api-key'],
        chatgptSignedIn: true,
        localRuntime: true,
        publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4.1' },
      },
      (store) => store.markDownloaded('qwen-3-vl-2b'),
    );

    for (const key of ['openai', 'gemini', 'chatgpt', 'ollama']) {
      expect(el.querySelector(`[data-test="provider-${key}"]`)?.tagName).toBe('DETAILS');
      expect(
        el.querySelector(`[data-test="provider-${key}"] > summary`)?.getAttribute('data-test'),
      ).toBe(`group-summary-${key}`);
    }
    expect(text(el, 'group-summary-openai')).toBe('OpenAI API In use Selected: gpt-4.1 2 models');
    expect(text(el, 'group-summary-gemini')).toBe('Google Gemini Cloud 1 model');
    expect(text(el, 'group-summary-chatgpt')).toBe('ChatGPT Plus Plan 2 models');
    expect(text(el, 'group-summary-ollama')).toBe('Local LLM On device 1 model');
    expect([
      isOpen(el, 'openai'),
      isOpen(el, 'gemini'),
      isOpen(el, 'chatgpt'),
      isOpen(el, 'ollama'),
    ]).toEqual([true, false, false, false]);

    click(el, 'model-gemini-gemini-2.5-flash');
    await settle(fixture);

    expect(text(el, 'group-summary-gemini')).toBe(
      'Google Gemini In use Selected: gemini-2.5-flash 1 model',
    );
    expect(text(el, 'group-summary-openai')).toBe('OpenAI API Cloud 2 models');
    expect([isOpen(el, 'openai'), isOpen(el, 'gemini')]).toEqual([false, true]);
  });

  it('a long list shows the recommended and the first six rows, and Show all reveals the rest', async () => {
    const { fixture, el } = await openAnalysis({
      keys: ['llm.openai.api-key'],
      cloudModels: { OpenAI: LONG_OPENAI },
    });

    expect(rowIds(el, 'openai')).toEqual([
      'gpt-5.1',
      'gpt-5-mini',
      'gpt-5-nano',
      'gpt-4.1-mini',
      'gpt-4.1',
      'gpt-4o-mini',
    ]);
    expect(
      [...el.querySelectorAll('[data-test="provider-openai"] [data-test="family"]')].map((f) =>
        f.textContent?.trim(),
      ),
    ).toEqual(['GPT-5', 'GPT-4.1', 'GPT-4o']);
    expect(text(el, 'model-openai-gpt-5.1')).toContain('Recommended');
    expect(text(el, 'model-openai-gpt-5-mini')).toContain('Fast');
    expect(text(el, 'model-openai-gpt-5-mini')).not.toContain('Recommended');
    expect(text(el, 'show-all-openai')).toBe('Show all 10 models');

    click(el, 'show-all-openai');
    await settle(fixture);

    expect(rowIds(el, 'openai')).toEqual([
      'gpt-5.1',
      'gpt-5-mini',
      'gpt-5-nano',
      'gpt-4.1-mini',
      'gpt-4.1',
      'gpt-4o-mini',
      'gpt-4o',
      'o3',
      'o4-mini',
      'chatgpt-4o-latest',
    ]);
    expect(text(el, 'model-openai-o3')).toContain('Reasoning');
    expect(el.querySelector('[data-test="show-all-openai"]')).toBeNull();

    TestBed.resetTestingModule();
    const selectedLate = await openAnalysis({
      keys: ['llm.openai.api-key'],
      cloudModels: { OpenAI: LONG_OPENAI },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'chatgpt-4o-latest' },
    });
    expect(rowIds(selectedLate.el, 'openai')).toEqual([
      'gpt-5.1',
      'gpt-5-mini',
      'gpt-5-nano',
      'gpt-4.1-mini',
      'gpt-4.1',
      'gpt-4o-mini',
      'chatgpt-4o-latest',
    ]);
  });

  it('the search and the chips narrow the rows of every group and say when nothing matches', async () => {
    const { fixture, el } = await openAnalysis(
      {
        keys: ['llm.openai.api-key', 'llm.gemini.api-key'],
        localRuntime: true,
        cloudModels: { ...CLOUD_MODELS, OpenAI: LONG_OPENAI },
      },
      (store) => {
        store.markDownloaded('qwen-3-vl-2b');
        store.markDownloaded('gemma-3-4b');
      },
    );

    await search(fixture, el, 'NANO');
    expect(rowIds(el, 'openai')).toEqual(['gpt-5-nano']);
    expect(rowIds(el, 'gemini')).toEqual([]);
    expect(localRowIds(el)).toEqual([]);
    expect(text(el, 'no-match-gemini')).toBe('No model matches');
    expect(text(el, 'no-match-ollama')).toBe('No model matches');
    expect([isOpen(el, 'openai'), isOpen(el, 'gemini'), isOpen(el, 'ollama')]).toEqual([
      true,
      true,
      true,
    ]);

    await search(fixture, el, '3b');
    expect(localRowIds(el)).toEqual(['qwen-3-vl-2b']);
    expect(rowIds(el, 'openai')).toEqual([]);

    await search(fixture, el, 'gpt');
    expect(rowIds(el, 'openai')).toHaveLength(8);
    expect(el.querySelector('[data-test="show-all-openai"]')).toBeNull();

    await search(fixture, el, '');
    click(el, 'filter-fast');
    await settle(fixture);
    expect(el.querySelector('[data-test="filter-fast"]')?.getAttribute('aria-pressed')).toBe(
      'true',
    );
    expect(el.querySelector('[data-test="filter-all"]')?.getAttribute('aria-pressed')).toBe(
      'false',
    );
    expect(rowIds(el, 'openai')).toEqual([
      'gpt-5-mini',
      'gpt-5-nano',
      'gpt-4.1-mini',
      'gpt-4o-mini',
      'o4-mini',
    ]);
    expect(rowIds(el, 'gemini')).toEqual(['gemini-2.5-flash']);
    expect(text(el, 'no-match-ollama')).toBe('No model matches');

    click(el, 'filter-recommended');
    await settle(fixture);
    expect(rowIds(el, 'openai')).toEqual(['gpt-5.1']);
    expect(rowIds(el, 'gemini')).toEqual(['gemini-2.5-flash']);
    expect(localRowIds(el)).toEqual(['qwen-3-vl-2b']);

    click(el, 'filter-local');
    await settle(fixture);
    expect(rowIds(el, 'openai')).toEqual([]);
    expect(text(el, 'no-match-openai')).toBe('No model matches');
    expect(el.querySelector('[data-test="provider-gemini"]')).not.toBeNull();
    expect(localRowIds(el)).toEqual(['qwen-3-vl-2b', 'gemma-3-4b']);

    click(el, 'filter-all');
    await settle(fixture);
    expect(rowIds(el, 'openai')).toHaveLength(6);
    expect(rowIds(el, 'gemini')).toEqual(['gemini-2.5-flash']);
    expect(localRowIds(el)).toEqual(['qwen-3-vl-2b', 'gemma-3-4b']);
  });
});
