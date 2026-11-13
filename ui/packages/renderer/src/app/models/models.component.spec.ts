import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import { InteractivityChecker } from '@angular/cdk/a11y';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  RESTART_DEFERRED_LINE,
  type OllamaModelInfo,
  type WorkerRestartResponse,
} from '@scout/shared';

import { appRoutes } from '../app.routes';

import { LLM_CONFIG_STORAGE_KEY, LlmConfigStore } from '../core/llm-config.store';
import { LogStore } from '../core/log.store';
import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore } from '../core/local-models.store';
import { restartAsMain } from '../core/main-restart.testing';
import { ScoutBridgeService } from '../core/scout-bridge';
import { MAIN_WHISPER_TABLE } from '../core/whisper-table.testing';
import { CLAUDE_ICON, GEMINI_ICON, OLLAMA_ICON, OPENAI_ICON } from '../sources/sources.component';
import { ModelsComponent } from './models.component';

class FakeLogStore {
  private state: { state: string; restarts: number } = { state: 'idle', restarts: 0 };
  readonly diagnostics: string[] = [];
  appendDiagnostic(line: string): void {
    this.diagnostics.push(line);
  }
  status(): { state: string; restarts: number } {
    return this.state;
  }
  setRunning(running: boolean): void {
    this.state = { state: running ? 'running' : 'idle', restarts: 0 };
  }
}

async function setup(opts: { running?: boolean; ollamaTags?: readonly string[] } = {}): Promise<{
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
    models: (opts.ollamaTags ?? []).map((name) => ({ name, modifiedAt: '', sizeBytes: 0 })),
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

  it('renders four tabs in the prototype order, Analysis, Transcription, Translation, Speech, and opens on Analysis', async () => {
    const { fixture } = await setup();

    const tabs = Array.from(
      fixture.nativeElement.querySelectorAll('[role="tab"]') as NodeListOf<HTMLButtonElement>,
    );
    expect(tabs.map((tab) => [tab.textContent?.trim(), tab.getAttribute('aria-selected')])).toEqual(
      [
        ['Analysis', 'true'],
        ['Transcription', 'false'],
        ['Translation', 'false'],
        ['Speech', 'false'],
      ],
    );
    expect(tabs.every((tab) => !tab.disabled)).toBe(true);
    expect(fixture.nativeElement.querySelector('h1')?.textContent?.trim()).toBe('Models');
  });

  it.each([
    ['the ?tab=speech link', '/?tab=speech', false],
    ['a click on the Speech tab', '/', true],
  ])('opened by %s, Models shows the Speech panel and no other', async (_name, url, byClick) => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: LogStore, useValue: new FakeLogStore() },
      ],
    });
    const bridge = TestBed.inject(ScoutBridgeService);
    vi.spyOn(bridge, 'llmListOllamaModels').mockResolvedValue({ models: [] });
    vi.spyOn(bridge, 'localVoices').mockResolvedValue({
      provider: 'Ollama',
      providerVoices: [],
      localVoices: [{ id: 'piper:en_GB-cori-medium', label: 'Cori (medium)' }],
      defaultLocal: 'piper:en_GB-cori-medium',
    });
    await TestBed.inject(Router).navigateByUrl(url);
    const fixture = TestBed.createComponent(ModelsComponent);
    fixture.detectChanges();
    if (byClick) {
      (
        fixture.nativeElement.querySelector('[data-test="tab-speech"]') as HTMLButtonElement
      ).click();
      fixture.detectChanges();
    }
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-test="tab-speech"]')?.getAttribute('aria-selected')).toBe(
      'true',
    );
    expect(el.querySelector('[data-test="panel-speech"] h2')?.textContent?.trim()).toBe('Speech');
    expect(el.querySelector('[data-test="speech-row-piper:en_GB-cori-medium"]')).not.toBeNull();
    expect(el.querySelector('[data-test="panel-analysis"]')).toBeNull();
    expect(el.querySelector('[data-test="panel-translation"]')).toBeNull();
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

  it('clicking the Translation tab opens the Translation panel', async () => {
    const { fixture } = await setup();
    const tl = fixture.nativeElement.querySelector(
      '[data-test="tab-translation"]',
    ) as HTMLButtonElement;
    tl.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-test="panel-translation"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="panel-transcription"]')).toBeNull();
  });

  it('Analysis tab lists every downloaded local model and marks only the selected one', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('qwen-2.5-0.5b-instruct');
    store.markDownloaded('qwen-2.5-1.5b-instruct');
    store.markDownloaded('qwen-3-vl-2b');
    vi.spyOn(TestBed.inject(ScoutBridgeService), 'vaultStore').mockResolvedValue(undefined);
    await store.select('analysis', 'qwen-2.5-1.5b-instruct');

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
    const fresh = await setup({ ollamaTags: ['qwen2.5:0.5b', 'qwen2.5:1.5b', 'qwen2.5:3b'] });

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
  /** The models the local Ollama lists while it answers, by tag only. */
  readonly ollamaTags?: readonly string[];
  /** The models the local Ollama lists while it answers, with their details; used in place of `ollamaTags`. */
  readonly ollamaModels?: readonly OllamaModelInfo[];
  readonly lmStudioRuntime?: boolean;
  readonly cloudModels?: Readonly<Record<string, readonly { id: string; displayName: string }[]>>;
  readonly listening?: boolean;
  readonly listeningError?: boolean;
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
  Anthropic: [
    { id: 'claude-opus-5-5', displayName: 'Claude Opus 5.5' },
    { id: 'claude-haiku-5', displayName: 'Claude Haiku 5' },
  ],
  OpenRouter: [
    { id: 'openai/gpt-4o-mini', displayName: 'OpenAI: GPT-4o-mini' },
    { id: 'anthropic/claude-opus-5-5', displayName: 'Anthropic: Claude Opus 5.5' },
  ],
};

function makeBridge(opts: BridgeOptions = {}) {
  const stores: { key: string; value: string }[] = [];
  const events: string[] = [];
  const failing = new Set(opts.failingLists ?? []);
  const slow = new Set(opts.slowStoreKeys ?? []);
  const deletes: string[] = [];
  const listeningListeners = new Set<(state: { listening: boolean }) => void>();
  const bridge = {
    stores,
    events,
    deletes,
    pushListening: (listening: boolean) => {
      for (const listener of listeningListeners) listener({ listening });
    },
    onLocalListening: vi.fn((listener: (state: { listening: boolean }) => void) => {
      listeningListeners.add(listener);
      return () => void listeningListeners.delete(listener);
    }),
    vaultDelete: vi.fn(async (req: { key: string }) => {
      deletes.push(req.key);
      events.push(`deleted ${req.key}`);
      return { deleted: true };
    }),
    localListening: vi.fn(async () => {
      if (opts.listeningError === true) throw new Error('[scout:ipc/unavailable] gone');
      return { listening: opts.listening ?? false };
    }),
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
      return {
        models:
          opts.ollamaModels ??
          (opts.ollamaTags ?? []).map((name) => ({ name, modifiedAt: '', sizeBytes: 0 })),
      };
    }),
    llmUnloadOllamaModels: vi.fn(async () => ({ unloaded: [], refused: [] })),
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
  return Object.assign(bridge, { workerRestart: vi.fn(() => restartAsMain(bridge)) });
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

  it('Models → Analysis lists the Claude models when Anthropic is connected, and choosing one writes provider Anthropic and that model, then restarts a running Scout', async () => {
    const { fixture, bridge, el } = await openAnalysis({
      keys: ['llm.anthropic.api-key'],
      workerState: 'running',
    });

    expect(bridge.llmListModels).toHaveBeenCalledWith({ provider: 'Anthropic' });
    expect(text(el, 'group-summary-claude')).toContain('Claude API');
    expect(rowIds(el, 'claude')).toEqual(['claude-opus-5-5', 'claude-haiku-5']);
    expect(el.querySelector('[data-test="model-claude-claude-opus-5-5"]')?.textContent).toContain(
      'Claude Opus 5.5',
    );

    click(el, 'model-claude-claude-opus-5-5');
    await settle(fixture);

    expect(bridge.stores).toEqual([
      { key: 'llm.cloud-model', value: 'claude-opus-5-5' },
      { key: 'llm.provider', value: 'Anthropic' },
    ]);
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(text(el, 'selected-pill-claude')).toContain('claude-opus-5-5');
  });

  it('Models → Analysis lists the OpenRouter models when OpenRouter is connected, and choosing one writes provider OpenRouter and that model, then restarts a running Scout', async () => {
    const { fixture, bridge, el } = await openAnalysis({
      keys: ['llm.openrouter.api-key'],
      workerState: 'running',
    });

    expect(bridge.llmListModels).toHaveBeenCalledWith({ provider: 'OpenRouter' });
    expect(text(el, 'group-summary-openrouter')).toContain('OpenRouter');
    expect(rowIds(el, 'openrouter').sort()).toEqual(
      ['anthropic/claude-opus-5-5', 'openai/gpt-4o-mini'].sort(),
    );
    expect(
      el.querySelector('[data-test="model-openrouter-anthropic/claude-opus-5-5"]')?.textContent,
    ).toContain('Anthropic: Claude Opus 5.5');

    click(el, 'model-openrouter-anthropic/claude-opus-5-5');
    await settle(fixture);

    expect(bridge.stores).toEqual([
      { key: 'llm.cloud-model', value: 'anthropic/claude-opus-5-5' },
      { key: 'llm.provider', value: 'OpenRouter' },
    ]);
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(text(el, 'selected-pill-openrouter')).toContain('anthropic/claude-opus-5-5');
  });

  it('a disconnected provider shows no block and its model list is never requested', async () => {
    const { el, bridge } = await openAnalysis();

    expect(el.querySelector('[data-test="provider-chatgpt"]')).toBeNull();
    expect(el.querySelector('[data-test="provider-openai"]')).toBeNull();
    expect(el.querySelector('[data-test="provider-gemini"]')).toBeNull();
    expect(el.querySelector('[data-test="provider-claude"]')).toBeNull();
    expect(el.querySelector('[data-test="provider-openrouter"]')).toBeNull();
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
    expect(text(el, 'in-use-strip')).toBe('In use gpt-6.1-sol via ChatGPT plan');
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
      'Scout will use OpenAI API the next time it starts.',
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
        ollamaTags: ['qwen2.5:3b'],
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

  it('choosing another model replaces the previous message with the restarting line while main restarts Scout', async () => {
    const { fixture, bridge, el } = await openAnalysis({ keys: ['llm.openai.api-key'] });
    click(el, 'model-openai-gpt-4.1');
    await settle(fixture);
    expect(el.querySelector('[data-test="analysis-status"]')?.textContent).toContain(
      'the next time it starts',
    );

    let answer: (value: WorkerRestartResponse) => void = () => undefined;
    bridge.workerRestart.mockImplementationOnce(() => new Promise((resolve) => (answer = resolve)));
    click(el, 'model-openai-gpt-4o-mini');
    await settle(fixture);

    expect(bridge.stores.at(-2)).toEqual({ key: 'llm.cloud-model', value: 'gpt-4o-mini' });
    expect(el.querySelector('[data-test="analysis-status"]')?.textContent).toContain(
      'Restarting Scout to use OpenAI API.',
    );

    answer({ outcome: 'none' });
    await settle(fixture);
    expect(el.querySelector('[data-test="analysis-status"]')?.textContent).toContain(
      'Scout will use OpenAI API the next time it starts.',
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
        ollamaTags: ['qwen2.5:3b'],
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
    expect(
      el.querySelector('[data-test="analysis-status"]')?.textContent?.replace(/\s+/g, ' ').trim(),
    ).toBe('Restart Scout in Settings, Scout worker, to use Google Gemini.');
    expect(el.querySelector('[data-test="analysis-restart-settings"]')?.getAttribute('href')).toBe(
      '/settings',
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
    const local = await openAnalysis({ localRuntime: true, ollamaTags: ['qwen2.5:3b'] }, (store) =>
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
    const { el } = await openAnalysis(
      { localRuntime: true, ollamaTags: ['qwen2.5:1.5b', 'gemma3:4b'] },
      (store) => {
        store.markDownloaded('qwen-2.5-1.5b-instruct');
        store.markDownloaded('gemma-3-4b');
      },
    );

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
    expect(el.querySelector('[data-test="empty-cta"]')?.getAttribute('href')).toBe('/sources');
    expect(el.querySelector('[data-test="local-not-connected"]')).toBeNull();
  });

  it('each connected source is a collapsible group with its selected model and count on the summary, open only for the source in use', async () => {
    const { fixture, el } = await openAnalysis(
      {
        keys: ['llm.openai.api-key', 'llm.gemini.api-key'],
        chatgptSignedIn: true,
        localRuntime: true,
        ollamaTags: ['qwen2.5:3b'],
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
    expect(text(el, 'group-summary-chatgpt')).toBe('ChatGPT plan Plan 2 models');
    expect(text(el, 'group-summary-ollama')).toBe('Local LLM On this computer 1 model');
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
        ollamaTags: ['qwen2.5:3b', 'gemma3:4b'],
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

type TranslationInUse = 'OpenAI' | 'Gemini' | 'ChatGptPlan' | 'Ollama' | 'unset' | 'None';

const ALL_CONNECTED: BridgeOptions = {
  keys: ['llm.openai.api-key', 'llm.gemini.api-key'],
  chatgptSignedIn: true,
  localRuntime: true,
  ollamaTags: ['qwen2.5:3b'],
};

const IN_USE: Readonly<
  Record<
    TranslationInUse,
    {
      readonly publicValues: Readonly<Record<string, string>>;
      readonly models: readonly string[];
      readonly pick: string | null;
      readonly savedAs: string;
      readonly elsewhere: { readonly model: string; readonly provider: string };
    }
  >
> = {
  OpenAI: {
    publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o-mini' },
    models: ['gpt-4.1', 'gpt-4o-mini'],
    pick: 'gpt-4.1',
    savedAs: 'OpenAI',
    elsewhere: { model: 'gemini-2.5-flash', provider: 'Gemini' },
  },
  Gemini: {
    publicValues: { 'llm.provider': 'Gemini', 'llm.cloud-model': 'gemini-2.5-flash' },
    models: ['gemini-2.5-flash'],
    pick: 'gemini-2.5-flash',
    savedAs: 'Gemini',
    elsewhere: { model: 'gpt-4.1', provider: 'OpenAI' },
  },
  ChatGptPlan: {
    publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
    models: ['gpt-5.6-luna', 'gpt-6.1-sol'],
    pick: 'gpt-6.1-sol',
    savedAs: 'ChatGptPlan',
    elsewhere: { model: 'gpt-4.1', provider: 'OpenAI' },
  },
  Ollama: {
    publicValues: { 'llm.provider': 'Ollama' },
    models: ['qwen2.5:3b'],
    pick: 'qwen2.5:3b',
    savedAs: 'Ollama',
    elsewhere: { model: 'gpt-4.1', provider: 'OpenAI' },
  },
  unset: {
    publicValues: {},
    models: ['qwen2.5:3b'],
    pick: 'qwen2.5:3b',
    savedAs: 'Ollama',
    elsewhere: { model: 'gpt-4.1', provider: 'OpenAI' },
  },
  None: {
    publicValues: { 'llm.provider': 'None' },
    models: [],
    pick: null,
    savedAs: 'None',
    elsewhere: { model: 'gpt-4.1', provider: 'OpenAI' },
  },
};

type Saved = 'nothing' | 'here' | 'elsewhere';

function savedValues(provider: TranslationInUse, saved: Saved): Record<string, string> {
  const use = IN_USE[provider];
  if (saved === 'here') {
    return {
      'llm.translation-model': use.pick ?? 'gpt-4.1',
      'llm.translation-provider': use.savedAs,
    };
  }
  if (saved === 'elsewhere') {
    return {
      'llm.translation-model': use.elsewhere.model,
      'llm.translation-provider': use.elsewhere.provider,
    };
  }
  return {};
}

async function openTranslation(
  provider: TranslationInUse,
  saved: Saved,
  opts: BridgeOptions = {},
  url = '/?tab=translation',
): Promise<{ fixture: ComponentFixture<ModelsComponent>; bridge: Bridge; el: HTMLElement }> {
  const bridge = makeBridge({
    ...ALL_CONNECTED,
    ...opts,
    publicValues: { ...IN_USE[provider].publicValues, ...savedValues(provider, saved) },
  });
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: LogStore, useValue: new FakeLogStore() },
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  await TestBed.inject(Router).navigateByUrl(url);
  TestBed.inject(LocalModelsStore).markDownloaded('qwen-3-vl-2b');
  const fixture = TestBed.createComponent(ModelsComponent);
  fixture.detectChanges();
  await settle(fixture);
  bridge.stores.length = 0;
  bridge.events.length = 0;
  return { fixture, bridge, el: fixture.nativeElement as HTMLElement };
}

function translationRows(el: HTMLElement): string[] {
  return [...el.querySelectorAll('button.opt[data-test^="translation-"]')]
    .map((row) => row.getAttribute('data-test') ?? '')
    .filter((id) => id !== 'translation-same')
    .map((id) => id.slice('translation-'.length))
    .sort();
}

function translationSelected(el: HTMLElement): string[] {
  return [...el.querySelectorAll('[data-test="panel-translation"] .option--selected')].map(
    (row) => row.getAttribute('data-test') ?? '',
  );
}

describe('ModelsComponent - Translation', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  const providers: readonly TranslationInUse[] = [
    'OpenAI',
    'Gemini',
    'ChatGptPlan',
    'Ollama',
    'unset',
    'None',
  ];
  const saves: readonly Saved[] = ['nothing', 'here', 'elsewhere'];

  describe.each(providers.flatMap((provider) => saves.map((saved) => ({ provider, saved }))))(
    'provider in use $provider, translation model saved: $saved',
    ({ provider, saved }) => {
      const use = IN_USE[provider];
      const none = provider === 'None';

      it('lists only the models of the provider in use', async () => {
        const { el } = await openTranslation(provider, saved);

        expect(translationRows(el)).toEqual([...use.models].sort());
      });

      it('marks the model chosen under the provider in use, else Same as Analysis', async () => {
        const { el } = await openTranslation(provider, saved);

        const expected = none
          ? []
          : saved === 'here'
            ? [`translation-${use.pick}`]
            : ['translation-same'];
        expect(translationSelected(el)).toEqual(expected);
      });

      it('says a choice made under another provider is not in use, and names that provider', async () => {
        const { el } = await openTranslation(provider, saved);

        expect(text(el, 'translation-stale')).toBe(
          saved === 'elsewhere' && !none
            ? `Not in use: ${use.elsewhere.model} was chosen for ${use.elsewhere.provider}, which is not the provider in use. Translation uses the Analysis model.`
            : '',
        );
      });

      it('with no provider in use, says nothing is translated and offers no row', async () => {
        const { el } = await openTranslation(provider, saved);

        expect(el.querySelector('[data-test="translation-no-provider"]') !== null).toBe(none);
        expect(el.querySelector('[data-test="translation-same"]') !== null).toBe(!none);
      });
    },
  );

  const cases = (['OpenAI', 'Gemini', 'ChatGptPlan', 'Ollama', 'unset'] as const).flatMap(
    (provider) =>
      (['running', 'idle'] as const).flatMap((worker) => [
        {
          provider,
          worker,
          saved: 'nothing' as Saved,
          click: 'pick',
          stores: [
            { key: 'llm.translation-model', value: IN_USE[provider].pick },
            { key: 'llm.translation-provider', value: IN_USE[provider].savedAs },
          ],
          deletes: [] as string[],
          selected: `translation-${IN_USE[provider].pick}`,
          whileAsked: `Restarting Scout to use ${IN_USE[provider].pick} for translation.`,
          status:
            worker === 'running'
              ? ''
              : `Scout will use ${IN_USE[provider].pick} for translation the next time it starts.`,
        },
        {
          provider,
          worker,
          saved: 'here' as Saved,
          click: 'same',
          stores: [],
          deletes: ['llm.translation-model', 'llm.translation-provider'],
          selected: 'translation-same',
          whileAsked: 'Restarting Scout to use the Analysis model for translation.',
          status:
            worker === 'running'
              ? ''
              : 'Scout will use the Analysis model for translation the next time it starts.',
        },
        {
          provider,
          worker,
          saved: 'elsewhere' as Saved,
          click: 'same',
          stores: [],
          deletes: ['llm.translation-model', 'llm.translation-provider'],
          selected: 'translation-same',
          whileAsked: '',
          status: '',
        },
        {
          provider,
          worker,
          saved: 'nothing' as Saved,
          click: 'same',
          stores: [],
          deletes: [],
          selected: 'translation-same',
          whileAsked: '',
          status: '',
        },
        {
          provider,
          worker,
          saved: 'here' as Saved,
          click: 'pick',
          stores: [],
          deletes: [],
          selected: `translation-${IN_USE[provider].pick}`,
          whileAsked: '',
          status: '',
        },
      ]),
  );

  it('the restart hint points to a real place: a failed restart after a translation pick says to restart Scout in Settings, Scout worker, and links there', async () => {
    const { fixture, el } = await openTranslation('OpenAI', 'nothing', {
      workerState: 'running',
      workerStartError: new Error('spawn failed'),
    });

    click(el, `translation-${IN_USE.OpenAI.pick}`);
    await settle(fixture);

    expect(
      el
        .querySelector('[data-test="translation-status"]')
        ?.textContent?.replace(/\s+/g, ' ')
        .trim(),
    ).toBe(
      `Restart Scout in Settings, Scout worker, to use ${IN_USE.OpenAI.pick} for translation.`,
    );
    expect(
      el.querySelector('[data-test="translation-restart-settings"]')?.getAttribute('href'),
    ).toBe('/settings');
  });

  it.each(cases)(
    '$provider, worker $worker, saved $saved, clicking $click: writes, restart and Selected',
    async ({
      provider,
      worker,
      saved,
      click: target,
      stores,
      deletes,
      selected,
      whileAsked,
      status,
    }) => {
      const { fixture, bridge, el } = await openTranslation(provider, saved, {
        workerState: worker,
      });
      let shownWhileAsked = '';
      bridge.workerRestart.mockImplementation(() => {
        fixture.detectChanges();
        shownWhileAsked = text(el, 'translation-status');
        return restartAsMain(bridge);
      });

      click(el, target === 'pick' ? `translation-${IN_USE[provider].pick}` : 'translation-same');
      await settle(fixture);

      const restarted = whileAsked !== '' && worker === 'running';
      expect(shownWhileAsked).toBe(whileAsked);
      expect(bridge.stores).toEqual(stores);
      expect(bridge.events).toEqual([
        ...stores.map((entry) => `stored ${entry.key}`),
        ...deletes.map((key) => `deleted ${key}`),
        ...(restarted ? ['workerStop'] : []),
      ]);
      expect(bridge.workerStart).toHaveBeenCalledTimes(restarted ? 1 : 0);
      expect(translationSelected(el)).toEqual([selected]);
      expect(text(el, 'translation-status')).toBe(status);
    },
  );

  it.each(
    [
      { entry: 'query', url: '/?tab=translation', open: (): void => undefined },
      { entry: 'click', url: '/', open: (el: HTMLElement): void => click(el, 'tab-translation') },
    ].flatMap((way) =>
      ([true, false, 'error'] as const).map((listening) => ({ ...way, listening })),
    ),
  )(
    'opened by $entry with a live listen $listening: the warning that saving ends the listen shows only while one runs, and nothing locks',
    async ({ url, open, listening }) => {
      const { fixture, bridge, el } = await openTranslation(
        'OpenAI',
        'nothing',
        {
          listening: listening === true,
          listeningError: listening === 'error',
          workerState: 'running',
        },
        url,
      );
      open(el);
      await settle(fixture);

      expect(text(el, 'translation-listen-warning')).toBe(
        listening === true
          ? 'A live listen is running. Saving a choice restarts Scout once the listen ends.'
          : '',
      );
      click(el, 'translation-gpt-4.1');
      await settle(fixture);
      expect(bridge.stores.map((entry) => entry.key)).toEqual([
        'llm.translation-model',
        'llm.translation-provider',
      ]);
    },
  );

  it('a model list that fails to load says so, and Same as Analysis can still be chosen', async () => {
    const { fixture, bridge, el } = await openTranslation('OpenAI', 'here', {
      failingLists: ['OpenAI'],
    });

    expect(text(el, 'translation-models-error')).toBe('Could not load the OpenAI model list.');
    click(el, 'translation-same');
    await settle(fixture);
    expect(bridge.deletes).toEqual(['llm.translation-model', 'llm.translation-provider']);
  });

  it('a vault that cannot be written leaves the choice as it was and shows the error', async () => {
    const { fixture, bridge, el } = await openTranslation('OpenAI', 'nothing', {
      workerState: 'running',
    });
    bridge.vaultStore.mockRejectedValueOnce(new Error('keyring locked'));

    click(el, 'translation-gpt-4.1');
    await settle(fixture);

    expect(text(el, 'translation-error')).toBe('keyring locked');
    expect(translationSelected(el)).toEqual(['translation-same']);
    expect(bridge.workerStop).not.toHaveBeenCalled();
  });
});

interface VoiceOptions extends BridgeOptions {
  readonly installed?: readonly { variant: string; verified: boolean }[];
  readonly installedReadFails?: boolean;
  readonly saved?: string;
}

const LOAD_ORDER = ['large-v3-turbo', 'large-v3', 'small', 'base', 'tiny'];

/** A main that keeps the installed models and the saved choice, and reports in use as `chooseWhisperModel` picks. */
function voiceBridge(opts: VoiceOptions = {}) {
  let installed = [...(opts.installed ?? [])];
  let saved = opts.saved ?? null;
  const verified = () => installed.filter((m) => m.verified).map((m) => m.variant);
  return Object.assign(makeBridge(opts), {
    onLlmPullOllamaProgress: vi.fn(() => () => undefined),
    onWhisperDownloadProgress: vi.fn(() => () => undefined),
    whisperListInstalled: vi.fn(async () => {
      if (opts.installedReadFails === true) throw new Error('[scout:ipc/unavailable] gone');
      const here = verified();
      return {
        installed,
        inUse:
          saved !== null && here.includes(saved)
            ? saved
            : (LOAD_ORDER.find((v) => here.includes(v)) ?? null),
        preferred: saved,
      };
    }),
    whisperModelTable: vi.fn(async () => MAIN_WHISPER_TABLE),
    whisperUseModel: vi.fn(
      async (req: { variant: string }): Promise<{ variant: string; restart: string }> => {
        saved = req.variant;
        return { variant: req.variant, restart: 'none' };
      },
    ),
    whisperDownloadModel: vi.fn(() => new Promise<never>(() => undefined)),
    whisperDeleteModel: vi.fn(async (req: { variant: string }) => {
      installed = installed.filter((m) => m.variant !== req.variant);
      return { variant: req.variant };
    }),
    llmPullOllamaModel: vi.fn(() => new Promise<never>(() => undefined)),
    llmDeleteOllamaModel: vi.fn(async (req: { tag: string }) => ({ tag: req.tag })),
  });
}

type VoiceBridge = ReturnType<typeof voiceBridge>;

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

async function openTab(
  tab: 'transcription' | 'translation',
  opts: VoiceOptions = {},
  prepare: (store: LocalModelsStore) => void = () => undefined,
  running = false,
): Promise<{
  fixture: ComponentFixture<ModelsComponent>;
  bridge: VoiceBridge;
  el: HTMLElement;
  store: LocalModelsStore;
}> {
  const bridge = voiceBridge(opts);
  const logStore = new FakeLogStore();
  logStore.setRunning(running);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: LogStore, useValue: logStore },
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
    ],
  });
  await TestBed.inject(Router).navigateByUrl(`/?tab=${tab}`);
  const store = TestBed.inject(LocalModelsStore);
  prepare(store);
  const fixture = TestBed.createComponent(ModelsComponent);
  fixture.detectChanges();
  await settle(fixture);
  return { fixture, bridge, el: fixture.nativeElement as HTMLElement, store };
}

async function confirmDialog(
  fixture: ComponentFixture<ModelsComponent>,
  answer: 'confirm-ok' | 'confirm-cancel',
): Promise<{ title: string | undefined; focused: string | null | undefined }> {
  for (let i = 0; i < 2; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
  const title = document.querySelector('sc-confirm-dialog h2')?.textContent?.trim();
  const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
  const button = document.querySelector<HTMLButtonElement>(`[data-test="${answer}"]`);
  if (button === null) throw new Error('the confirmation dialog did not open');
  button.click();
  await settle(fixture);
  return { title, focused };
}

describe('Models → Transcription manages the Whisper models', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  type State = 'available' | 'downloading' | 'downloaded' | 'in use';

  function installedFor(state: State): VoiceOptions {
    const here = (variants: readonly string[]) =>
      variants.map((variant) => ({ variant, verified: true }));
    if (state === 'downloaded') return { installed: here(['tiny', 'base']), saved: 'tiny' };
    if (state === 'in use') return { installed: here(['base']) };
    return {};
  }

  async function opened(
    state: State,
    running: boolean,
  ): Promise<Awaited<ReturnType<typeof openTab>>> {
    const view = await openTab('transcription', installedFor(state), () => undefined, running);
    if (state === 'downloading') {
      click(view.el, 'download-whisper-base');
      await settle(view.fixture);
    }
    return view;
  }

  function controls(el: HTMLElement): Record<string, string | boolean> {
    const row = el.querySelector('[data-test="row-whisper-base"]') as HTMLElement;
    const find = (test: string): HTMLElement | null =>
      row.querySelector(`[data-test="${test}-whisper-base"]`);
    const shown: Record<string, string | boolean> = {};
    for (const test of ['download', 'progress', 'check', 'downloaded', 'select', 'remove']) {
      const node = find(test);
      if (node === null) continue;
      shown[test] =
        node instanceof HTMLButtonElement
          ? node.disabled
            ? 'disabled'
            : 'enabled'
          : text(row, `${test}-whisper-base`);
    }
    return shown;
  }

  const SHOWN: Readonly<Record<State, Record<string, string | boolean>>> = {
    available: { download: 'enabled' },
    downloading: { progress: 'Downloading 0%' },
    downloaded: { downloaded: 'Downloaded', select: 'enabled', remove: 'enabled' },
    'in use': { check: 'In use', remove: 'enabled' },
  };

  const STATES: readonly State[] = ['available', 'downloading', 'downloaded', 'in use'];

  describe('Use this on another model did nothing while Scout ran: the lock is gone', () => {
    for (const state of STATES) {
      for (const running of [false, true]) {
        it(`a model ${state}, Scout ${running ? 'running' : 'stopped'}: the row shows ${JSON.stringify(SHOWN[state])}, with no lock`, async () => {
          const { el } = await opened(state, running);

          expect(controls(el)).toEqual(SHOWN[state]);
          expect(el.textContent).not.toContain('locked');
        });
      }
    }
  });

  it('Download on an available model asks main for that Whisper variant and shows the progress', async () => {
    const { fixture, bridge, el } = await opened('available', false);

    click(el, 'download-whisper-base');
    await settle(fixture);

    expect(bridge.whisperDownloadModel).toHaveBeenCalledWith(
      expect.objectContaining({ variant: 'base' }),
    );
    expect(controls(el)).toEqual({ progress: 'Downloading 0%' });
  });

  describe('Use this switches the voice model through main, which restarts a running Scout', () => {
    const DEFERRED =
      'The voice model is ready; Scout restarts to use it when the live listen ends.';
    const ROWS = [
      { running: true, listening: false, restart: 'now' },
      { running: true, listening: true, restart: 'deferred' },
      { running: false, listening: false, restart: 'none' },
      { running: false, listening: true, restart: 'none' },
    ] as const;

    for (const { running, listening, restart } of ROWS) {
      it(`Scout ${running ? 'running' : 'stopped'}, live listen ${listening ? 'open' : 'closed'}: the row says Switching… until main answers (restart ${restart}), then ${restart === 'deferred' ? 'In use stays on the model Scout still runs, the notice says the restart waits for the listen, and In use moves once the restart ran' : 'In use moves to the new model'}`, async () => {
        const { fixture, bridge, el, store } = await opened('downloaded', running);
        let restarted = restart !== 'deferred';
        const list = bridge.whisperListInstalled.getMockImplementation()!;
        bridge.whisperListInstalled.mockImplementation(async () => {
          const res = await list();
          return restarted ? res : { ...res, inUse: 'tiny' };
        });
        let answer: () => void = () => undefined;
        const main = bridge.whisperUseModel.getMockImplementation()!;
        bridge.whisperUseModel.mockImplementationOnce(
          (req: { variant: string }) =>
            new Promise((resolve) => {
              answer = () => void main(req).then((saved) => resolve({ ...saved, restart }));
            }),
        );
        bridge.vaultStore.mockClear();

        click(el, 'select-whisper-base');
        await settle(fixture);
        const during = {
          label: text(el, 'select-whisper-base'),
          inUse: store.whisperInUse(),
        };
        answer();
        await settle(fixture);

        expect(bridge.whisperUseModel.mock.calls).toEqual([[{ variant: 'base' }]]);
        const shown = restart === 'deferred' ? 'whisper-tiny' : 'whisper-base';
        expect(during).toEqual({ label: 'Switching…', inUse: 'whisper-tiny' });
        expect(store.whisperInUse()).toBe(shown);
        expect(text(el, `check-${shown}`)).toBe('In use');
        expect(text(el, 'whisper-switch-notice')).toBe(restart === 'deferred' ? DEFERRED : '');
        expect(bridge.vaultStore.mock.calls.filter(([req]) => req.key === 'stt.model')).toEqual([]);
        restarted = true;
        await vi.waitFor(() => expect(store.whisperInUse()).toBe('whisper-base'), {
          timeout: 6_000,
        });
      }, 10_000);
    }

    it('the restart fails: the line says the choice is saved and Scout starts with it next time, with the reason, and the row shows no error', async () => {
      const { fixture, bridge, el } = await opened('downloaded', true);
      bridge.whisperUseModel.mockRejectedValueOnce(
        new Error(
          "Error invoking remote method 'scout:whisper.useModel': Error: [scout:whisper/restart-failed] spawn ENOENT Scout.Host.Console",
        ),
      );

      click(el, 'select-whisper-base');
      await settle(fixture);

      expect(text(el, 'whisper-switch-notice')).toBe(
        'Saved; Scout starts with base next time. It could not restart now: spawn ENOENT Scout.Host.Console',
      );
      expect(el.querySelector('[data-test="error-whisper-base"]')).toBeNull();
    });
  });

  for (const state of ['downloaded', 'in use'] as const) {
    for (const answer of ['confirm-ok', 'confirm-cancel'] as const) {
      it(`Remove on a model ${state} asks first with Cancel focused; ${answer === 'confirm-ok' ? 'Remove deletes it through main' : 'Cancel keeps it'}`, async () => {
        const { fixture, bridge, el, store } = await opened(state, false);

        click(el, 'remove-whisper-base');
        const dialog = await confirmDialog(fixture, answer);

        expect(dialog).toEqual({ title: 'Remove Whisper Base?', focused: 'confirm-cancel' });
        expect(bridge.whisperDeleteModel.mock.calls).toEqual(
          answer === 'confirm-ok' ? [[{ variant: 'base' }]] : [],
        );
        expect(store.isDownloaded('whisper-base')).toBe(answer === 'confirm-cancel');
      });
    }
  }

  it('no local runtime and no AI Source: Transcription lists the Whisper models and Download works', async () => {
    const { fixture, bridge, el } = await openTab('transcription');

    const rows = [...el.querySelectorAll('[data-test^="row-whisper-"]')].map((row) =>
      row.getAttribute('data-test'),
    );
    click(el, 'download-whisper-tiny');
    await settle(fixture);

    expect(bridge.llmListOllamaModels).toHaveBeenCalled();
    expect(rows).toEqual([
      'row-whisper-tiny',
      'row-whisper-base',
      'row-whisper-small',
      'row-whisper-large-v3-turbo',
      'row-whisper-large-v3',
    ]);
    expect(text(el, 'panel-status')).toBe('On this computer');
    expect(bridge.whisperDownloadModel).toHaveBeenCalledWith(
      expect.objectContaining({ variant: 'tiny' }),
    );
  });

  it('the old Local Models address opens Models on the Transcription tab', async () => {
    @Component({ standalone: true, template: '' })
    class Stub {}
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([
          { path: 'llm', component: Stub },
          ...appRoutes.filter((route) => route.path === 'sources/local-models'),
        ]),
      ],
    });
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/sources/local-models');

    expect(router.url).toBe('/llm?tab=transcription');
  });
});

describe('Models → Translation asks for a voice model first', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  const NOTICE =
    'Live translation needs a voice model to transcribe first; none is installed. Choose a voice model';

  const CASES = [
    {
      label: 'main has a verified Whisper model',
      opts: { installed: [{ variant: 'base', verified: true }] },
      stored: false,
      shown: false,
    },
    {
      label: 'main has only an unverified Whisper file',
      opts: { installed: [{ variant: 'base', verified: false }] },
      stored: true,
      shown: true,
    },
    { label: 'main has no Whisper model', opts: { installed: [] }, stored: false, shown: true },
    {
      label: 'main cannot answer, the app remembers a download',
      opts: { installedReadFails: true },
      stored: true,
      shown: false,
    },
    {
      label: 'main cannot answer, the app remembers none',
      opts: { installedReadFails: true },
      stored: false,
      shown: true,
    },
  ] as const;

  for (const { label, opts, stored, shown } of CASES) {
    it(`${label}: the voice model notice is ${shown ? 'shown' : 'hidden'}`, async () => {
      const { el } = await openTab('translation', opts, (store) => {
        if (stored) store.markDownloaded('whisper-base');
      });

      expect(text(el, 'translation-needs-voice')).toBe(shown ? NOTICE : '');
    });
  }

  it('Choose a voice model switches to the Transcription tab and lists no model in Translation', async () => {
    const { fixture, el } = await openTab('translation');

    const whisperRowsInTranslation = el.querySelectorAll('[data-test^="row-whisper-"]').length;
    click(el, 'translation-choose-voice');
    await settle(fixture);

    expect(whisperRowsInTranslation).toBe(0);
    expect(el.querySelector('[data-test="tab-transcription"]')?.getAttribute('aria-selected')).toBe(
      'true',
    );
    expect(el.querySelector('[data-test="panel-transcription"]')).not.toBeNull();
    expect(el.querySelector('[data-test="panel-translation"]')).toBeNull();
  });
});

function brandFile(name: string): string {
  return join(process.cwd(), 'src/assets/brands', name);
}

describe('Models → Analysis shows the official logos of AI Sources', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
    document.documentElement.removeAttribute('data-theme');
  });

  async function allGroups(): Promise<HTMLElement> {
    const { el } = await openAnalysis(
      {
        keys: ['llm.openai.api-key', 'llm.gemini.api-key', 'llm.anthropic.api-key'],
        chatgptSignedIn: true,
        localRuntime: true,
        ollamaTags: ['qwen2.5:3b'],
      },
      (store) => store.markDownloaded('qwen-3-vl-2b'),
    );
    return el;
  }

  const GROUPS = [
    ['chatgpt', 'ChatGPT plan', OPENAI_ICON],
    ['openai', 'OpenAI API', OPENAI_ICON],
    ['gemini', 'Google Gemini', GEMINI_ICON],
    ['claude', 'Claude API', CLAUDE_ICON],
    ['ollama', 'Ollama', OLLAMA_ICON],
  ] as const;

  for (const [key, name, icon] of GROUPS) {
    it(`the ${key} group shows ${name}'s official logo from the same local files as its AI Sources card`, async () => {
      const el = await allGroups();
      const logo = el.querySelector(`[data-test="logo-${key}"]`) as HTMLElement;
      const src = (variant: string): string | null | undefined =>
        logo.querySelector(`img.mark--${variant}`)?.getAttribute('src');

      expect(icon.kind).toBe('asset');
      expect([logo.getAttribute('role'), logo.getAttribute('aria-label')]).toEqual([
        'img',
        `${name} logo`,
      ]);
      expect([src('dark'), src('light')]).toEqual(
        icon.kind === 'asset' ? [icon.dark, icon.light] : [],
      );
      expect(logo.querySelector('path')).toBeNull();
      for (const file of [src('dark'), src('light')]) {
        expect(existsSync(brandFile(String(file).replace('assets/brands/', '')))).toBe(true);
      }
    });
  }

  it('in the dark theme the dark variant of a logo is the visible one, in the light theme the light variant', async () => {
    const el = await allGroups();
    const logo = el.querySelector('[data-test="logo-openai"]') as HTMLElement;
    const shown = (): string[] =>
      [...logo.querySelectorAll('img')]
        .filter((img) => getComputedStyle(img).display !== 'none')
        .map((img) => img.getAttribute('src') ?? '');

    document.documentElement.setAttribute('data-theme', 'dark');
    const dark = shown();
    document.documentElement.setAttribute('data-theme', 'light');
    const light = shown();

    expect([dark, light]).toEqual([
      ['assets/brands/openai-blossom-white.svg'],
      ['assets/brands/openai-blossom-black.svg'],
    ]);
  });

  it('the local runtime with no official logo, while not connected, keeps a generic icon with an accessible name', async () => {
    const { el } = await openAnalysis();
    const logo = el.querySelector('[data-test="logo-local-runtime"]') as HTMLElement;

    expect([logo.getAttribute('role'), logo.getAttribute('aria-label')]).toEqual([
      'img',
      'Local LLM logo',
    ]);
    expect(logo.querySelector('img')).toBeNull();
    expect(logo.querySelector('svg path')).not.toBeNull();
  });

  it('Models draws no logo of its own any more: every brand mark comes from the shared AI Sources icons', () => {
    const source = readFileSync('src/app/models/models.component.ts', 'utf8');

    expect(source).not.toMatch(/\bLOGOS\b/);
    expect(source).toContain("from '../sources/sources.component'");
  });
});

describe('Models follows the theme', () => {
  it('its styles paint only with the theme tokens, no hex, rgb() or hsl() colour left', () => {
    const source = readFileSync('src/app/models/models.component.ts', 'utf8');
    const start = source.indexOf('  styles:');
    const css = source.slice(start, source.indexOf('\n})\n', start));

    expect(start).toBeGreaterThanOrEqual(0);
    expect(css).toContain('var(--sc-');
    expect(
      Array.from(source.matchAll(/#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(/g), (m) => m[0]),
    ).toEqual([]);
  });
});

describe('Models → Analysis names the AI Source card in its restart line', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  const CASES = [
    { card: 'ChatGPT plan', opts: { chatgptSignedIn: true }, row: 'model-chatgpt-gpt-6.1-sol' },
    { card: 'OpenAI API', opts: { keys: ['llm.openai.api-key'] }, row: 'model-openai-gpt-4.1' },
    {
      card: 'Google Gemini',
      opts: { keys: ['llm.gemini.api-key'] },
      row: 'model-gemini-gemini-2.5-flash',
    },
    {
      card: 'Claude API',
      opts: { keys: ['llm.anthropic.api-key'] },
      row: 'model-claude-claude-opus-5-5',
    },
    {
      card: 'Ollama',
      opts: { localRuntime: true, ollamaTags: ['qwen2.5:3b'] },
      row: 'option-qwen-3-vl-2b',
    },
  ] as const;

  for (const { card, opts, row } of CASES) {
    it(`choosing a model of ${card} while Scout runs says "Restarting Scout to use ${card}." until main has restarted it, and nothing after`, async () => {
      const { fixture, bridge, el } = await openAnalysis(
        { ...opts, workerState: 'running', publicValues: { 'llm.provider': 'None' } },
        (store) => store.markDownloaded('qwen-3-vl-2b'),
      );
      let answer: (value: WorkerRestartResponse) => void = () => undefined;
      bridge.workerRestart.mockImplementationOnce(
        () => new Promise((resolve) => (answer = resolve)),
      );

      click(el, row);
      await settle(fixture);
      const whileRestarting = text(el, 'analysis-status');
      answer({ outcome: 'now' });
      await settle(fixture);

      expect({ whileRestarting, after: text(el, 'analysis-status') }).toEqual({
        whileRestarting: `Restarting Scout to use ${card}. Chat answers resume in a few seconds.`,
        after: '',
      });
    });
  }
});

describe('Models → Translation follows the live listen', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  it('a live listen that starts while Translation is open brings up the warning that saving restarts Scout', async () => {
    const { fixture, bridge, el } = await openTranslation('OpenAI', 'nothing', {
      workerState: 'running',
    });
    const before = el.querySelector('[data-test="translation-listen-warning"]') !== null;

    bridge.pushListening(true);
    await settle(fixture);

    expect({
      before,
      after: el.querySelector('[data-test="translation-listen-warning"]') !== null,
    }).toEqual({ before: false, after: true });
  });

  it('choosing a translation model while a live listen runs leaves Scout running and says it restarts once the listen ends', async () => {
    const { fixture, bridge, el } = await openTranslation('OpenAI', 'nothing', {
      listening: true,
      workerState: 'running',
    });

    click(el, 'translation-gpt-4.1');
    await settle(fixture);

    expect({
      restartAsked: bridge.workerRestart.mock.calls.length,
      stopped: bridge.workerStop.mock.calls.length,
      line: text(el, 'translation-status'),
    }).toEqual({ restartAsked: 1, stopped: 0, line: RESTART_DEFERRED_LINE });
  });

  it('while main restarts Scout the tab says it is restarting, then shows what main did', async () => {
    const { fixture, bridge, el } = await openTranslation('OpenAI', 'nothing', {
      workerState: 'running',
    });
    let answer: (value: WorkerRestartResponse) => void = () => undefined;
    bridge.workerRestart.mockImplementationOnce(() => new Promise((resolve) => (answer = resolve)));

    click(el, 'translation-gpt-4.1');
    await settle(fixture);
    const whileRestarting = text(el, 'translation-status');
    answer({ outcome: 'deferred' });
    await settle(fixture);

    expect({ whileRestarting, after: text(el, 'translation-status') }).toEqual({
      whileRestarting: 'Restarting Scout to use gpt-4.1 for translation.',
      after: RESTART_DEFERRED_LINE,
    });
  });
});

describe('Models looks like its artboard', () => {
  const TOKENS: Readonly<Record<string, string>> = {
    '--sc-fg': 'rgb(1, 1, 1)',
    '--sc-muted': 'rgb(2, 2, 2)',
    '--sc-line': 'rgb(3, 3, 3)',
    '--sc-ok-container': 'rgb(4, 4, 4)',
    '--sc-info-container': 'rgb(5, 5, 5)',
    '--sc-surface-2': 'rgb(6, 6, 6)',
    '--sc-error-container': 'rgb(7, 7, 7)',
  };

  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
    for (const [name, value] of Object.entries(TOKENS)) {
      document.documentElement.style.setProperty(name, value);
    }
  });

  afterEach(() => {
    for (const name of Object.keys(TOKENS)) document.documentElement.style.removeProperty(name);
  });

  const OPENAI_IN_USE: BridgeOptions = {
    keys: ['llm.openai.api-key'],
    publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4.1' },
  };

  function inOrder(el: HTMLElement, selector: string): (string | undefined)[] {
    return [...el.querySelectorAll(selector)].map(
      (node) => node.getAttribute('data-test') ?? node.textContent?.trim(),
    );
  }

  it('the tabs are 14 px, the active one in the text colour at 600, over a rule', async () => {
    const { el } = await openAnalysis();
    const list = el.querySelector('[role="tablist"]') as HTMLElement;
    const look = (id: string): string[] => {
      const style = getComputedStyle(el.querySelector(`[data-test="tab-${id}"]`) as HTMLElement);
      return [style.fontSize, style.fontWeight, style.color];
    };

    expect({
      analysis: look('analysis'),
      transcription: look('transcription'),
      translation: look('translation'),
      speech: look('speech'),
    }).toEqual({
      analysis: ['14px', '600', TOKENS['--sc-fg']],
      transcription: ['14px', '500', TOKENS['--sc-muted']],
      translation: ['14px', '500', TOKENS['--sc-muted']],
      speech: ['14px', '500', TOKENS['--sc-muted']],
    });
    expect([
      getComputedStyle(list).borderBottom,
      getComputedStyle(list).display,
      list.parentElement?.classList.contains('page'),
      getComputedStyle(list.parentElement as HTMLElement).flexDirection,
    ]).toEqual([`1px solid ${TOKENS['--sc-line']}`, 'flex', true, 'column']);
  });

  it('Analysis shows the model in use above a field labelled Search models and the filters All, Recommended, Fast, On this computer, with no paragraph under the tabs', async () => {
    const { el } = await openAnalysis(OPENAI_IN_USE);
    const field = el.querySelector('[data-test="model-search"]') as HTMLInputElement;

    expect(
      inOrder(
        el,
        '[data-test="in-use-strip"], label[for="model-search"], [data-test="filter-all"]',
      ),
    ).toEqual(['in-use-strip', 'Search models', 'filter-all']);
    expect(el.querySelector('[data-test="in-use-strip"]')?.getAttribute('aria-label')).toBe(
      'Model in use',
    );
    expect(text(el, 'in-use')).toBe('In use gpt-4.1 via OpenAI API');
    expect(el.querySelector('[data-test="in-use"] .inuse-label svg path')?.getAttribute('d')).toBe(
      'M20 6 9 17l-5-5',
    );
    expect([field.id, field.placeholder, field.type]).toEqual([
      'model-search',
      'For example mini, flash, gemma',
      'search',
    ]);
    expect(field.parentElement?.querySelector('svg')).toBeNull();
    expect(
      [...el.querySelectorAll('[data-test^="filter-"]')].map((chip) => chip.textContent?.trim()),
    ).toEqual(['All', 'Recommended', 'Fast', 'On this computer']);
    expect(
      [...el.querySelectorAll('[data-test="panel-analysis"] p')].map((p) => p.textContent?.trim()),
    ).toEqual([]);
  });

  it('Normal: the source in use carries In use, the other groups their kind, Plan on the info colours and the local group On this computer', async () => {
    const { el } = await openAnalysis(
      { ...OPENAI_IN_USE, chatgptSignedIn: true, localRuntime: true, ollamaTags: ['qwen2.5:3b'] },
      (store) => store.markDownloaded('qwen-3-vl-2b'),
    );
    const badge = (key: string): [string | undefined, string] => {
      const chip = el.querySelector(`[data-test="group-summary-${key}"] .gbadge`) as HTMLElement;
      return [chip.textContent?.trim(), getComputedStyle(chip).backgroundColor];
    };

    expect({ openai: badge('openai'), chatgpt: badge('chatgpt'), ollama: badge('ollama') }).toEqual(
      {
        openai: ['In use', TOKENS['--sc-ok-container']],
        chatgpt: ['Plan', TOKENS['--sc-info-container']],
        ollama: ['On this computer', TOKENS['--sc-surface-2']],
      },
    );
    expect(
      [...el.querySelectorAll('.option--selected')].map((row) => row.getAttribute('data-test')),
    ).toEqual(['model-openai-gpt-4.1']);
    expect(el.querySelector('[data-test="row-restarting"]')).toBeNull();
    expect(el.querySelector('[data-test="analysis-restart-progress"]')).toBeNull();
  });

  it('Restarting: while main restarts Scout the in-use strip says so over a progress bar and the chosen row says Restarting Scout..., and once main has restarted it the strip shows only the model in use', async () => {
    const { fixture, bridge, el } = await openAnalysis({
      ...OPENAI_IN_USE,
      workerState: 'running',
    });
    let answer: (value: WorkerRestartResponse) => void = () => undefined;
    bridge.workerRestart.mockImplementationOnce(() => new Promise((resolve) => (answer = resolve)));

    click(el, 'model-openai-gpt-4o-mini');
    await settle(fixture);
    const during = {
      line: text(el, 'in-use-strip'),
      bar: el
        .querySelector('[data-test="in-use-strip"] [data-test="analysis-restart-progress"]')
        ?.getAttribute('mode'),
      row: text(el, 'row-restarting'),
      rowOf: el
        .querySelector('[data-test="row-restarting"]')
        ?.closest('.opt')
        ?.getAttribute('data-test'),
    };
    answer({ outcome: 'now' });
    await settle(fixture);

    expect(during).toEqual({
      line: 'In use gpt-4o-mini via OpenAI API Restarting Scout to use OpenAI API. Chat answers resume in a few seconds.',
      bar: 'indeterminate',
      row: 'Restarting Scout...',
      rowOf: 'model-openai-gpt-4o-mini',
    });
    expect({
      line: text(el, 'in-use-strip'),
      bar: el.querySelector('[data-test="analysis-restart-progress"]'),
      row: el.querySelector('[data-test="row-restarting"]'),
    }).toEqual({
      line: 'In use gpt-4o-mini via OpenAI API',
      bar: null,
      row: null,
    });
  });

  const SECOND_PICKS = [
    { second: 'model-openai-gpt-4.1', inUse: 'In use gpt-4.1 via OpenAI API', card: 'OpenAI API' },
    {
      second: 'model-gemini-gemini-2.5-flash',
      inUse: 'In use gemini-2.5-flash via Google Gemini',
      card: 'Google Gemini',
    },
    { second: 'option-qwen-3-vl-2b', inUse: 'In use Qwen 2.5 3B via Ollama', card: 'Ollama' },
  ] as const;
  const RESUME = 'Chat answers resume in a few seconds.';
  const settingsLine = (card: string): string =>
    `Restart Scout in Settings, Scout worker , to use ${card}.`;
  const nextStartLine = (card: string): string => `Scout will use ${card} the next time it starts.`;
  const FIRST_ANSWERS: Readonly<Record<string, WorkerRestartResponse>> = {
    restarted: { outcome: 'now' },
    failed: { outcome: 'failed', reason: 'first start failed' },
    'not running': { outcome: 'none' },
    deferred: { outcome: 'deferred' },
  };
  const SECOND_ANSWERS: Readonly<Record<string, WorkerRestartResponse>> = {
    ...FIRST_ANSWERS,
    failed: { outcome: 'failed', reason: 'second start failed' },
  };
  const FIRST_ALONE: Readonly<
    Record<string, { readonly error: string; readonly failedRow: string; readonly line: string }>
  > = {
    restarted: { error: '', failedRow: '', line: '' },
    failed: {
      error: 'first start failed',
      failedRow: 'Restart failed',
      line: settingsLine('OpenAI API'),
    },
    'not running': { error: '', failedRow: '', line: nextStartLine('OpenAI API') },
    deferred: { error: '', failedRow: '', line: RESTART_DEFERRED_LINE },
  };
  const ENDINGS = {
    clean: { ends: 'only the model in use', error: '', line: (): string => '' },
    secondFailure: {
      ends: 'the second failure and the way to Settings',
      error: 'second start failed',
      line: settingsLine,
    },
    firstFailure: {
      ends: 'the first failure and the way to Settings, since Scout is down',
      error: 'first start failed',
      line: settingsLine,
    },
    nextStart: {
      ends: 'the line that Scout uses the choice the next time it starts, with no failure',
      error: '',
      line: nextStartLine,
    },
    waits: {
      ends: 'the line that Scout waits, then restarts, with no failure',
      error: '',
      line: (): string => RESTART_DEFERRED_LINE,
    },
  } as const;
  const ENDS: Readonly<Record<string, keyof typeof ENDINGS>> = {
    'restarted, then restarted': 'clean',
    'restarted, then failed': 'secondFailure',
    'restarted, then not running': 'nextStart',
    'restarted, then deferred': 'waits',
    'failed, then restarted': 'clean',
    'failed, then failed': 'secondFailure',
    'failed, then not running': 'firstFailure',
    'failed, then deferred': 'waits',
    'not running, then restarted': 'clean',
    'not running, then failed': 'secondFailure',
    'not running, then not running': 'nextStart',
    'not running, then deferred': 'waits',
    'deferred, then restarted': 'clean',
    'deferred, then failed': 'secondFailure',
    'deferred, then not running': 'nextStart',
    'deferred, then deferred': 'waits',
  };
  const FIRST_ARRIVES = [
    'while the second restart is pending',
    'while the second choice is saved',
  ] as const;
  const SEQUENCES = FIRST_ARRIVES.flatMap((firstArrives) =>
    Object.entries(ENDS).map(([name, ending]) => {
      const [first = '', second = ''] = name.split(', then ');
      return {
        name,
        firstArrives,
        first,
        firstAnswer: FIRST_ANSWERS[first] as WorkerRestartResponse,
        secondAnswer: SECOND_ANSWERS[second] as WorkerRestartResponse,
        ...ENDINGS[ending],
      };
    }),
  );

  function restartShown(el: HTMLElement): Record<string, string | undefined> {
    return {
      strip: text(el, 'in-use-strip'),
      line: text(el, 'analysis-status'),
      bar:
        el.querySelector('[data-test="analysis-restart-progress"]')?.getAttribute('mode') ??
        undefined,
      rowOf:
        el
          .querySelector('[data-test="row-restarting"]')
          ?.closest('.opt')
          ?.getAttribute('data-test') ?? undefined,
      error: text(el, 'analysis-error'),
      failedRow: text(el, 'row-restart-failed'),
    };
  }

  const ALL_SOURCES: BridgeOptions = {
    ...OPENAI_IN_USE,
    keys: ['llm.openai.api-key', 'llm.gemini.api-key'],
    localRuntime: true,
    ollamaTags: ['qwen2.5:3b'],
    workerState: 'running',
  };

  it.each(SECOND_PICKS.flatMap((pick) => SEQUENCES.map((sequence) => ({ ...pick, ...sequence }))))(
    'Restarting, a second model picked meanwhile ($second; main answers $name, the first answer arriving $firstArrives): the restarting line and the bar stay while the second choice is saved and while its restart is pending, and the screen ends on $ends',
    async ({
      second,
      inUse,
      card,
      firstArrives,
      first,
      firstAnswer,
      secondAnswer,
      error,
      line,
    }) => {
      const { fixture, bridge, el } = await openAnalysis(ALL_SOURCES, (store) =>
        store.markDownloaded('qwen-3-vl-2b'),
      );
      const answers: ((value: WorkerRestartResponse) => void)[] = [];
      bridge.workerRestart.mockImplementation(
        () => new Promise((resolve) => answers.push(resolve)),
      );

      click(el, 'model-openai-gpt-4o-mini');
      await settle(fixture);
      let saved: () => void = () => undefined;
      bridge.vaultStore.mockImplementationOnce(
        () => new Promise<void>((resolve) => (saved = resolve)),
      );
      click(el, second);
      await settle(fixture);
      const whileSaving = restartShown(el);
      let firstWhileSaving: Record<string, string | undefined> | null = null;
      if (firstArrives === 'while the second choice is saved') {
        answers[0]?.(firstAnswer);
        await settle(fixture);
        firstWhileSaving = restartShown(el);
      }
      saved();
      await settle(fixture);
      if (firstArrives === 'while the second restart is pending') {
        answers[0]?.(firstAnswer);
        await settle(fixture);
      }
      const secondPending = restartShown(el);
      answers[1]?.(secondAnswer);
      await settle(fixture);

      expect(answers).toHaveLength(2);
      expect({ line: whileSaving['line'], bar: whileSaving['bar'] }).toEqual({
        line: `Restarting Scout to use OpenAI API. ${RESUME}`,
        bar: 'indeterminate',
      });
      if (firstWhileSaving !== null) {
        expect({
          error: firstWhileSaving['error'],
          failedRow: firstWhileSaving['failedRow'],
          line: firstWhileSaving['line'],
          bar: firstWhileSaving['bar'],
        }).toEqual({ ...FIRST_ALONE[first], bar: undefined });
      }
      expect(secondPending).toEqual({
        strip: `${inUse} Restarting Scout to use ${card}. ${RESUME}`,
        line: `Restarting Scout to use ${card}. ${RESUME}`,
        bar: 'indeterminate',
        rowOf: second,
        error: '',
        failedRow: '',
      });
      expect(restartShown(el)).toEqual({
        strip: [inUse, error, line(card)].filter((part) => part !== '').join(' '),
        line: line(card),
        bar: undefined,
        rowOf: undefined,
        error,
        failedRow: error === '' ? '' : 'Restart failed',
      });
    },
  );

  const REFUSED_WRITES = [
    {
      second: 'model-gemini-gemini-2.5-flash',
      refused: 'its first write',
      writesBefore: 0,
      inUse: 'In use gpt-4o-mini via OpenAI API',
      card: 'OpenAI API',
      row: 'model-openai-gpt-4o-mini',
    },
    {
      second: 'option-qwen-3-vl-2b',
      refused: 'its first write',
      writesBefore: 0,
      inUse: 'In use gpt-4o-mini via OpenAI API',
      card: 'OpenAI API',
      row: 'model-openai-gpt-4o-mini',
    },
    {
      second: 'option-qwen-3-vl-2b',
      refused: 'its last write, the provider',
      writesBefore: 1,
      inUse: 'In use gpt-4o-mini via OpenAI API',
      card: 'OpenAI API',
      row: 'model-openai-gpt-4o-mini',
    },
  ] as const;

  const BOTH_FAILED =
    'The choice was not saved: keyring locked. Scout did not restart: first start failed.';
  const ORDERS = [
    'the write refused, then the restart answering',
    'the restart answering, then the write refused',
  ] as const;

  it.each(
    REFUSED_WRITES.flatMap((pick) =>
      ORDERS.filter((order) => order === ORDERS[0] || pick.writesBefore === 0).flatMap((order) =>
        ['restarted', 'failed'].map((answered) => ({ ...pick, order, answered })),
      ),
    ),
  )(
    'Restarting, a second pick the vault refuses ($second, $refused; $order $answered): the write error stays whatever the restart answers and in either order, a failed restart beside it as a sentence of its own, and the strip and the line name the same source',
    async ({ second, writesBefore, inUse, card, row, order, answered }) => {
      const { fixture, bridge, el } = await openAnalysis(ALL_SOURCES, (store) =>
        store.markDownloaded('qwen-3-vl-2b'),
      );
      const first = FIRST_ANSWERS[answered] as WorkerRestartResponse;
      let answer: (value: WorkerRestartResponse) => void = () => undefined;
      bridge.workerRestart.mockImplementationOnce(
        () => new Promise((resolve) => (answer = resolve)),
      );

      click(el, 'model-openai-gpt-4o-mini');
      await settle(fixture);
      let refuse: () => void = () => undefined;
      if (order === ORDERS[0]) {
        for (let i = 0; i < writesBefore; i += 1) {
          bridge.vaultStore.mockResolvedValueOnce(undefined);
        }
        bridge.vaultStore.mockRejectedValueOnce(new Error('keyring locked'));
      } else {
        bridge.vaultStore.mockImplementationOnce(
          () =>
            new Promise<void>((_resolve, reject) => {
              refuse = () => reject(new Error('keyring locked'));
            }),
        );
      }
      click(el, second);
      await settle(fixture);
      const refused = order === ORDERS[0] ? restartShown(el) : null;
      answer(first);
      await settle(fixture);
      refuse();
      await settle(fixture);

      const failed = answered === 'failed';
      if (refused !== null) {
        expect(refused).toEqual({
          strip: `${inUse} keyring locked Restarting Scout to use ${card}. ${RESUME}`,
          line: `Restarting Scout to use ${card}. ${RESUME}`,
          bar: 'indeterminate',
          rowOf: row,
          error: 'keyring locked',
          failedRow: '',
        });
      }
      expect(restartShown(el)).toEqual({
        strip: failed ? `${inUse} ${BOTH_FAILED} ${settingsLine(card)}` : `${inUse} keyring locked`,
        line: failed ? settingsLine(card) : '',
        bar: undefined,
        rowOf: undefined,
        error: failed ? BOTH_FAILED : 'keyring locked',
        failedRow: failed ? 'Restart failed' : '',
      });
    },
  );

  it.each([
    {
      name: 'failed, then not running',
      first: 'failed',
      error: 'first start failed',
      line: settingsLine('Ollama'),
    },
    {
      name: 'not running, then not running',
      first: 'not running',
      error: '',
      line: nextStartLine('Ollama'),
    },
  ])(
    'Restarting, a local pick while a restart for OpenAI API is pending (main answers $name, the first answer arriving while the pick is saved, and the vault refuses llm.model once Ollama is the provider): the strip and the line name the same source and no sentence names OpenAI API',
    async ({ first, error, line }) => {
      const { fixture, bridge, el } = await openAnalysis(ALL_SOURCES, (store) =>
        store.markDownloaded('qwen-3-vl-2b'),
      );
      const answers: ((value: WorkerRestartResponse) => void)[] = [];
      bridge.workerRestart.mockImplementation(
        () => new Promise((resolve) => answers.push(resolve)),
      );
      click(el, 'model-openai-gpt-4o-mini');
      await settle(fixture);
      let saved: () => void = () => undefined;
      let ollamaSaved = false;
      bridge.vaultStore.mockImplementation(async (req: { key: string; value: string }) => {
        if (req.key === 'llm.model' && ollamaSaved) throw new Error('keyring locked');
        if (req.key === 'llm.model') await new Promise<void>((resolve) => (saved = resolve));
        if (req.key === 'llm.provider' && req.value === 'Ollama') ollamaSaved = true;
      });

      click(el, 'option-qwen-3-vl-2b');
      await settle(fixture);
      answers[0]?.(FIRST_ANSWERS[first] as WorkerRestartResponse);
      await settle(fixture);
      saved();
      await settle(fixture);
      answers[1]?.({ outcome: 'none' });
      await settle(fixture);

      const shown = restartShown(el);
      expect({
        inUse: text(el, 'in-use'),
        line: shown['line'],
        error: shown['error'],
        namesOpenAi: Object.values(shown).some((part) => part?.includes('OpenAI API') === true),
      }).toEqual({ inUse: 'In use Qwen 2.5 3B via Ollama', line, error, namesOpenAi: false });
    },
  );

  it('Analysis: a local model removed just before its row is clicked is not saved, asks for no restart, the screen says it is no longer on this computer, and the list no longer offers it', async () => {
    const { fixture, bridge, el } = await openAnalysis(
      { ...ALL_SOURCES, ollamaTags: ['qwen2.5:3b', 'gemma3:4b'] },
      (store) => {
        store.markDownloaded('qwen-3-vl-2b');
        store.markDownloaded('gemma-3-4b');
      },
    );
    bridge.stores.length = 0;

    TestBed.inject(LocalModelsStore).markRemoved('gemma-3-4b');
    click(el, 'option-gemma-3-4b');
    await settle(fixture);

    expect({
      stores: bridge.stores,
      restartAsked: bridge.workerRestart.mock.calls.length,
      inUse: text(el, 'in-use'),
      error: text(el, 'analysis-error'),
      offered: localRowIds(el),
    }).toEqual({
      stores: [],
      restartAsked: 0,
      inUse: 'In use gpt-4.1 via OpenAI API',
      error: 'Gemma 3 4B is no longer on this computer.',
      offered: ['qwen-3-vl-2b'],
    });
  });

  it('Analysis: a local pick that is saved writes llm.model once, then Ollama as the provider, and asks main to restart Scout for Ollama as any other pick does', async () => {
    const { fixture, bridge, el } = await openAnalysis(ALL_SOURCES, (store) =>
      store.markDownloaded('qwen-3-vl-2b'),
    );
    bridge.stores.length = 0;

    click(el, 'option-qwen-3-vl-2b');
    await settle(fixture);

    expect({
      stores: bridge.stores,
      restartAsked: bridge.workerRestart.mock.calls,
      strip: text(el, 'in-use-strip'),
    }).toEqual({
      stores: [
        { key: 'llm.model', value: 'qwen2.5:3b' },
        { key: 'llm.provider', value: 'Ollama' },
      ],
      restartAsked: [[{ reason: 'Ollama' }]],
      strip: 'In use Qwen 2.5 3B via Ollama',
    });
  });

  it('Restart failed: the in-use strip shows the reason as an alert and the way to Settings, Scout worker, with no progress bar, and the chosen row says Restart failed', async () => {
    const { fixture, el } = await openAnalysis({
      ...OPENAI_IN_USE,
      workerState: 'running',
      workerStartError: new Error('spawn failed'),
    });

    click(el, 'model-openai-gpt-4o-mini');
    await settle(fixture);
    const alert = el.querySelector('[data-test="in-use-strip"] [data-test="analysis-error"]');

    expect([alert?.getAttribute('role'), alert?.textContent?.trim()]).toEqual([
      'alert',
      'spawn failed',
    ]);
    expect(getComputedStyle(alert as HTMLElement).backgroundColor).toBe(
      TOKENS['--sc-error-container'],
    );
    expect(text(el, 'in-use-strip')).toBe(
      'In use gpt-4o-mini via OpenAI API spawn failed Restart Scout in Settings, Scout worker , to use OpenAI API.',
    );
    expect(
      el
        .querySelector('[data-test="row-restart-failed"]')
        ?.closest('.opt')
        ?.getAttribute('data-test'),
    ).toBe('model-openai-gpt-4o-mini');
    expect(text(el, 'row-restart-failed')).toBe('Restart failed');
    expect(el.querySelector('[data-test="analysis-restart-progress"]')).toBeNull();
  });

  it('Checking local: until the local runtime answers a status row says Checking the local runtime..., and the row goes once it answers', async () => {
    let answer: (value: { models: never[] }) => void = () => undefined;
    const { fixture, el } = await openAnalysis(OPENAI_IN_USE, () => {
      (TestBed.inject(ScoutBridgeService) as unknown as Bridge).llmListOllamaModels.mockReturnValue(
        new Promise((resolve) => (answer = resolve)),
      );
    });
    const row = el.querySelector('[data-test="local-checking"]');
    const during = {
      role: row?.getAttribute('role'),
      line: text(el, 'local-checking'),
      order: inOrder(el, '[data-test="provider-openai"], [data-test="local-checking"]'),
      notConnected: el.querySelector('[data-test="local-not-connected"]'),
    };

    answer({ models: [] });
    await settle(fixture);

    expect(during).toEqual({
      role: 'status',
      line: 'Checking the local runtime...',
      order: ['provider-openai', 'local-checking'],
      notConnected: null,
    });
    expect(el.querySelector('[data-test="local-checking"]')).toBeNull();
    expect(text(el, 'empty-state')).toBe(
      'No models downloaded yet. Download one on the Ollama card in AI Sources.',
    );
  });

  it('No source: with no AI Source and no local runtime, Analysis says there is no model to choose and links to AI Sources, with no in-use strip and nothing to pick', async () => {
    const { el } = await openAnalysis();
    const link = el.querySelector('[data-test="no-source-cta"]');

    expect(text(el, 'no-source')).toBe(
      'No source is connected yet, so there is no model to choose. Connect a source in AI Sources',
    );
    expect([link?.tagName, link?.getAttribute('href')]).toEqual(['A', '/sources']);
    expect(inOrder(el, '[data-test="model-search"], [data-test="no-source"]')).toEqual([
      'model-search',
      'no-source',
    ]);
    expect(el.querySelector('[data-test="in-use-strip"]')).toBeNull();
    expect(el.querySelector('[data-test="no-active-model-nudge"]')).toBeNull();
  });

  it('a connected source with no model in use keeps the nudge and shows no No source box', async () => {
    const { el } = await openAnalysis({ keys: ['llm.gemini.api-key'] });

    expect(el.querySelector('[data-test="no-source"]')).toBeNull();
    expect(text(el, 'no-active-model-nudge')).toBe(
      'No model in use for this task. Pick one below.',
    );
  });
});

describe('Models keeps the corners Models.dc.html draws for its small boxes', () => {
  const source = readFileSync('src/app/models/models.component.ts', 'utf8');
  const radius = (selector: string): string | null => {
    const block = new RegExp(`\\n\\s*\\${selector} \\{([^}]*)\\}`).exec(source)?.[1] ?? '';
    return /border-radius:\s*([^;]+);/.exec(block)?.[1] ?? null;
  };

  for (const [selector, what] of [
    ['.opt', 'a model row'],
    ['.checking', 'the Checking local line'],
    ['.offline', 'the Local LLM not connected box'],
  ] as const) {
    it(`${what} (${selector}) has 12 px corners, not the 14 px card token`, () => {
      expect(radius(selector)).toBe('12px');
    });
  }

  it('the No source box keeps the card token, 14 px, as drawn', () => {
    expect(radius('.empty')).toBe('var(--sc-radius)');
  });
});

describe('the pressed segment of the Models filter (Models.dc.html)', () => {
  it('is on the pressed colour every artboard draws, white in light and #253249 in dark, under the text colour', () => {
    const source = readFileSync('src/app/models/models.component.ts', 'utf8');
    const block = /\.chip\[aria-pressed='true'\] \{([^}]*)\}/.exec(source)?.[1] ?? '';

    expect({
      background: /background:\s*([^;]+);/.exec(block)?.[1] ?? null,
      color: /color:\s*([^;]+);/.exec(block)?.[1] ?? null,
    }).toEqual({ background: 'var(--sc-pressed)', color: 'var(--sc-fg)' });
  });
});

describe('Models marks its tabs and rows as Models.dc.html draws them', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  async function openOn(tab: string): Promise<HTMLElement> {
    const bridge = Object.assign(voiceBridge(), {
      localVoices: vi.fn(async () => ({
        provider: 'Ollama',
        providerVoices: [],
        localVoices: [{ id: 'piper:en_GB-cori-medium', label: 'Cori (medium)' }],
        defaultLocal: 'piper:en_GB-cori-medium',
      })),
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: LogStore, useValue: new FakeLogStore() },
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: InteractivityChecker, useValue: LAID_OUT },
      ],
    });
    await TestBed.inject(Router).navigateByUrl(`/?tab=${tab}`);
    const fixture = TestBed.createComponent(ModelsComponent);
    fixture.detectChanges();
    await settle(fixture);
    return fixture.nativeElement as HTMLElement;
  }

  it.each([
    ['analysis', 'Analysis', 'panel-analysis'],
    ['transcription', 'Transcription', 'panel-transcription'],
    ['translation', 'Translation', 'panel-translation'],
    ['speech', 'Speech', 'SC-SPEECH-VOICES'],
  ])(
    'tab selected %s: each tab has its id, aria-selected and aria-controls on models-panel, and the panel shown is the tabpanel labelled by %s',
    async (id, label, shown) => {
      const el = await openOn(id);
      const panels = [...el.querySelectorAll('#models-panel')];
      const panel = panels[0];

      expect(
        [...el.querySelectorAll('[role="tablist"] > button')].map((tab) => [
          tab.getAttribute('role'),
          tab.id,
          tab.getAttribute('aria-selected'),
          tab.getAttribute('aria-controls'),
        ]),
      ).toEqual(
        ['analysis', 'transcription', 'translation', 'speech'].map((tab) => [
          'tab',
          `tab-${tab}`,
          String(tab === id),
          'models-panel',
        ]),
      );
      expect(panels).toHaveLength(1);
      expect([
        panel?.getAttribute('role'),
        panel?.getAttribute('aria-labelledby'),
        panel?.getAttribute('data-test') ?? panel?.tagName,
        el.querySelector(`#${panel?.getAttribute('aria-labelledby')}`)?.textContent?.trim(),
      ]).toEqual(['tabpanel', `tab-${id}`, shown, label]);
    },
  );

  type RowState = readonly [string, string | null, string | null, string | null, string | null];

  function rowsOf(el: HTMLElement, panel: string): { groups: string[]; rows: RowState[] } {
    const root = el.querySelector(`[data-test="${panel}"]`) as HTMLElement;
    return {
      groups: [...root.querySelectorAll('[role="radiogroup"]')].map(
        (group) => group.getAttribute('aria-label') ?? '',
      ),
      rows: [...root.querySelectorAll('button.opt')].map((row) => [
        row.getAttribute('data-test') ?? '',
        row.getAttribute('role'),
        row.getAttribute('aria-checked'),
        row.getAttribute('tabindex'),
        row.closest('[role="radiogroup"]')?.getAttribute('aria-label') ?? null,
      ]),
    };
  }

  const ANALYSIS = 'Analysis model';
  const TRANSLATION = 'Translation model';

  it.each<{
    readonly state: string;
    readonly open: () => Promise<{ el: HTMLElement; fixture: ComponentFixture<ModelsComponent> }>;
    readonly panel: string;
    readonly group: string;
    readonly rows: readonly RowState[];
  }>([
    {
      state: 'Analysis with nothing chosen and every group closed',
      open: () => openAnalysis({ keys: ['llm.openai.api-key', 'llm.gemini.api-key'] }),
      panel: 'panel-analysis',
      group: ANALYSIS,
      rows: [
        ['model-openai-gpt-4.1', 'radio', 'false', '-1', ANALYSIS],
        ['model-openai-gpt-4o-mini', 'radio', 'false', '-1', ANALYSIS],
        ['model-gemini-gemini-2.5-flash', 'radio', 'false', '-1', ANALYSIS],
      ],
    },
    {
      state: 'Analysis with a cloud model chosen',
      open: () =>
        openAnalysis({
          keys: ['llm.openai.api-key', 'llm.gemini.api-key'],
          publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o-mini' },
        }),
      panel: 'panel-analysis',
      group: ANALYSIS,
      rows: [
        ['model-openai-gpt-4.1', 'radio', 'false', '-1', ANALYSIS],
        ['model-openai-gpt-4o-mini', 'radio', 'true', '0', ANALYSIS],
        ['model-gemini-gemini-2.5-flash', 'radio', 'false', '-1', ANALYSIS],
      ],
    },
    {
      state: 'Analysis with a local model chosen',
      open: () =>
        openAnalysis(
          {
            keys: ['llm.gemini.api-key'],
            localRuntime: true,
            ollamaTags: ['qwen2.5:1.5b', 'gemma3:4b'],
          },
          (store) => {
            store.markDownloaded('qwen-2.5-1.5b-instruct');
            store.markDownloaded('gemma-3-4b');
            void store.select('analysis', 'gemma-3-4b');
          },
        ),
      panel: 'panel-analysis',
      group: ANALYSIS,
      rows: [
        ['model-gemini-gemini-2.5-flash', 'radio', 'false', '-1', ANALYSIS],
        ['option-qwen-2.5-1.5b-instruct', 'radio', 'false', '-1', ANALYSIS],
        ['option-gemma-3-4b', 'radio', 'true', '0', ANALYSIS],
      ],
    },
    {
      state: 'Translation on Same as Analysis',
      open: () => openTranslation('OpenAI', 'nothing'),
      panel: 'panel-translation',
      group: TRANSLATION,
      rows: [
        ['translation-same', 'radio', 'true', '0', TRANSLATION],
        ['translation-gpt-4.1', 'radio', 'false', '-1', TRANSLATION],
        ['translation-gpt-4o-mini', 'radio', 'false', '-1', TRANSLATION],
      ],
    },
    {
      state: 'Translation with a model chosen',
      open: () => openTranslation('OpenAI', 'here'),
      panel: 'panel-translation',
      group: TRANSLATION,
      rows: [
        ['translation-same', 'radio', 'false', '-1', TRANSLATION],
        ['translation-gpt-4.1', 'radio', 'true', '0', TRANSLATION],
        ['translation-gpt-4o-mini', 'radio', 'false', '-1', TRANSLATION],
      ],
    },
    {
      state: 'Translation with the saved model filtered out',
      open: async () => {
        const opened = await openTranslation('OpenAI', 'here');
        await search(opened.fixture, opened.el, 'mini');
        return opened;
      },
      panel: 'panel-translation',
      group: TRANSLATION,
      rows: [
        ['translation-same', 'radio', 'false', '0', TRANSLATION],
        ['translation-gpt-4o-mini', 'radio', 'false', '-1', TRANSLATION],
      ],
    },
    {
      state: 'Translation with the saved model unlisted, its model list failed',
      open: () => openTranslation('OpenAI', 'here', { failingLists: ['OpenAI'] }),
      panel: 'panel-translation',
      group: TRANSLATION,
      rows: [['translation-same', 'radio', 'false', '0', TRANSLATION]],
    },
  ])(
    'rows per state, $state: one radiogroup "$group", rows as radios, checked only on the chosen one, one row in the tab order',
    async ({ open, panel, group, rows }) => {
      const { el } = await open();

      expect(rowsOf(el, panel)).toEqual({ groups: [group], rows });
    },
  );

  const NONE_IN_USE: BridgeOptions = {
    keys: ['llm.openai.api-key', 'llm.gemini.api-key', 'llm.anthropic.api-key'],
  };
  const OPENAI_MINI_IN_USE: BridgeOptions = {
    ...NONE_IN_USE,
    publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o-mini' },
  };
  const TAB_ROWS = [
    'model-openai-gpt-4.1',
    'model-openai-gpt-4o-mini',
    'model-gemini-gemini-2.5-flash',
    'model-claude-claude-opus-5-5',
    'model-claude-claude-haiku-5',
  ];

  it.each<{
    readonly state: string;
    readonly opts: BridgeOptions;
    readonly toggles: readonly (readonly [string, boolean])[];
    readonly stop: string | null;
  }>([
    { state: 'no model in use, every group closed', opts: NONE_IN_USE, toggles: [], stop: null },
    {
      state: 'no model in use, the second group opened',
      opts: NONE_IN_USE,
      toggles: [['gemini', true]],
      stop: 'model-gemini-gemini-2.5-flash',
    },
    {
      state: 'no model in use, the second and third groups opened',
      opts: NONE_IN_USE,
      toggles: [
        ['claude', true],
        ['gemini', true],
      ],
      stop: 'model-gemini-gemini-2.5-flash',
    },
    {
      state: 'no model in use, a group opened and closed again',
      opts: NONE_IN_USE,
      toggles: [
        ['gemini', true],
        ['gemini', false],
      ],
      stop: null,
    },
    {
      state: "the checked row's group open",
      opts: OPENAI_MINI_IN_USE,
      toggles: [],
      stop: 'model-openai-gpt-4o-mini',
    },
    {
      state: "the checked row's group open and another opened before it is the first",
      opts: OPENAI_MINI_IN_USE,
      toggles: [['claude', true]],
      stop: 'model-openai-gpt-4o-mini',
    },
    {
      state: "the checked row's group closed and another open",
      opts: OPENAI_MINI_IN_USE,
      toggles: [
        ['openai', false],
        ['claude', true],
      ],
      stop: 'model-claude-claude-opus-5-5',
    },
    {
      state: "the checked row's group closed and none open",
      opts: OPENAI_MINI_IN_USE,
      toggles: [['openai', false]],
      stop: null,
    },
    {
      state: "the checked row's group closed, then opened again",
      opts: OPENAI_MINI_IN_USE,
      toggles: [
        ['openai', false],
        ['claude', true],
        ['openai', true],
      ],
      stop: 'model-openai-gpt-4o-mini',
    },
  ])(
    'tab stop by open groups, $state: the stop is $stop and every other row is out of the tab order',
    async ({ opts, toggles, stop }) => {
      const { fixture, el } = await openAnalysis(opts);
      for (const [key, open] of toggles) {
        (el.querySelector(`[data-test="provider-${key}"]`) as HTMLDetailsElement).open = open;
        await settle(fixture);
      }
      const rows = [...el.querySelectorAll('[data-test="panel-analysis"] [role="radio"]')];

      expect({
        rows: rows.map((row) => [row.getAttribute('data-test'), row.getAttribute('tabindex')]),
        stops: rows.filter((row) => row.getAttribute('tabindex') === '0').length,
      }).toEqual({
        rows: TAB_ROWS.map((row) => [row, row === stop ? '0' : '-1']),
        stops: stop === null ? 0 : 1,
      });
    },
  );

  const RETURN_ROWS = [
    'model-chatgpt-gpt-5.6-luna',
    'model-chatgpt-gpt-6.1-sol',
    'model-openai-gpt-4.1',
    'model-openai-gpt-4o-mini',
    'model-gemini-gemini-2.5-flash',
    'option-gemma-3-4b',
  ];
  const RETURN_SOURCES: BridgeOptions = {
    keys: ['llm.openai.api-key', 'llm.gemini.api-key'],
    chatgptSignedIn: true,
    localRuntime: true,
    ollamaTags: ['gemma3:4b'],
    publicValues: { 'llm.provider': 'None' },
  };

  type Opened = { fixture: ComponentFixture<ModelsComponent>; el: HTMLElement };
  const setOpen =
    (key: string, open: boolean) =>
    async ({ fixture, el }: Opened): Promise<void> => {
      (el.querySelector(`[data-test="provider-${key}"]`) as HTMLDetailsElement).open = open;
      await settle(fixture);
    };
  const chatGpt =
    (connected: boolean) =>
    async ({ fixture }: Opened): Promise<void> => {
      TestBed.inject(LlmConfigStore).setChatGptConnected(connected);
      await settle(fixture);
    };
  const localModel =
    (downloaded: boolean) =>
    async ({ fixture }: Opened): Promise<void> => {
      const store = TestBed.inject(LocalModelsStore);
      if (downloaded) store.markDownloaded('gemma-3-4b');
      else store.markRemoved('gemma-3-4b');
      await settle(fixture);
    };

  it.each<{
    readonly state: string;
    readonly opts: BridgeOptions;
    readonly steps: readonly ((opened: Opened) => Promise<void>)[];
    readonly stop: string | null;
  }>([
    {
      state: 'a provider disconnected while its group is open, then reconnected',
      opts: RETURN_SOURCES,
      steps: [setOpen('chatgpt', true), chatGpt(false), chatGpt(true)],
      stop: null,
    },
    {
      state:
        'a provider disconnected while open, another group opened, then the provider reconnected',
      opts: RETURN_SOURCES,
      steps: [setOpen('chatgpt', true), chatGpt(false), setOpen('openai', true), chatGpt(true)],
      stop: 'model-openai-gpt-4.1',
    },
    {
      state: 'the local list gone while its group is open, then back',
      opts: RETURN_SOURCES,
      steps: [setOpen('ollama', true), localModel(false), localModel(true)],
      stop: null,
    },
    {
      state:
        'a group that holds the checked row gone while open, then back open as its expression says',
      opts: {
        ...RETURN_SOURCES,
        publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
      },
      steps: [chatGpt(false), chatGpt(true)],
      stop: 'model-chatgpt-gpt-5.6-luna',
    },
  ])(
    'tab stop when a group leaves and comes back, $state: the stop is $stop',
    async ({ opts, steps, stop }) => {
      const opened = await openAnalysis(opts, (store) => store.markDownloaded('gemma-3-4b'));
      for (const step of steps) await step(opened);
      const rows = [...opened.el.querySelectorAll('[data-test="panel-analysis"] [role="radio"]')];

      expect({
        rows: rows.map((row) => [row.getAttribute('data-test'), row.getAttribute('tabindex')]),
        stops: rows.filter((row) => row.getAttribute('tabindex') === '0').length,
      }).toEqual({
        rows: RETURN_ROWS.map((row) => [row, row === stop ? '0' : '-1']),
        stops: stop === null ? 0 : 1,
      });
    },
  );

  async function openKeyRows(): Promise<{
    fixture: ComponentFixture<ModelsComponent>;
    bridge: Bridge;
    el: HTMLElement;
  }> {
    const opened = await openAnalysis(
      {
        keys: ['llm.openai.api-key', 'llm.gemini.api-key', 'llm.anthropic.api-key'],
        localRuntime: true,
        ollamaTags: ['qwen2.5:1.5b', 'gemma3:4b'],
        publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4.1' },
      },
      (store) => {
        store.markDownloaded('qwen-2.5-1.5b-instruct');
        store.markDownloaded('gemma-3-4b');
      },
    );
    for (const key of ['claude', 'ollama']) {
      (opened.el.querySelector(`[data-test="provider-${key}"]`) as HTMLDetailsElement).open = true;
    }
    opened.bridge.stores.length = 0;
    opened.bridge.deletes.length = 0;
    return opened;
  }

  const OPENAI_FIRST = 'model-openai-gpt-4.1';
  const OPENAI_LAST = 'model-openai-gpt-4o-mini';
  const CLAUDE_FIRST = 'model-claude-claude-opus-5-5';
  const CLAUDE_LAST = 'model-claude-claude-haiku-5';
  const LOCAL_FIRST = 'option-qwen-2.5-1.5b-instruct';
  const LOCAL_LAST = 'option-gemma-3-4b';
  const SAME = 'translation-same';
  const PICK = 'translation-gpt-4.1';
  const LAST_PICK = 'translation-gpt-4o-mini';

  it.each<{
    readonly tab: 'Analysis' | 'Translation';
    readonly position: string;
    readonly start: string;
    readonly key: string;
    readonly focus: string;
    readonly prevented: boolean;
  }>([
    {
      tab: 'Analysis',
      position: 'a middle row',
      start: CLAUDE_FIRST,
      key: 'ArrowDown',
      focus: CLAUDE_LAST,
      prevented: true,
    },
    {
      tab: 'Analysis',
      position: 'a middle row',
      start: CLAUDE_FIRST,
      key: 'ArrowRight',
      focus: CLAUDE_LAST,
      prevented: true,
    },
    {
      tab: 'Analysis',
      position: 'the last row of a source, the next group closed',
      start: OPENAI_LAST,
      key: 'ArrowDown',
      focus: CLAUDE_FIRST,
      prevented: true,
    },
    {
      tab: 'Analysis',
      position: 'the first row of a source, the group before closed',
      start: CLAUDE_FIRST,
      key: 'ArrowUp',
      focus: OPENAI_LAST,
      prevented: true,
    },
    {
      tab: 'Analysis',
      position: 'the last cloud row, the local group next',
      start: CLAUDE_LAST,
      key: 'ArrowDown',
      focus: LOCAL_FIRST,
      prevented: true,
    },
    {
      tab: 'Analysis',
      position: 'the first local row',
      start: LOCAL_FIRST,
      key: 'ArrowLeft',
      focus: CLAUDE_LAST,
      prevented: true,
    },
    {
      tab: 'Analysis',
      position: 'the last row',
      start: LOCAL_LAST,
      key: 'ArrowDown',
      focus: OPENAI_FIRST,
      prevented: true,
    },
    {
      tab: 'Analysis',
      position: 'the last row',
      start: LOCAL_LAST,
      key: 'ArrowRight',
      focus: OPENAI_FIRST,
      prevented: true,
    },
    {
      tab: 'Analysis',
      position: 'the first row',
      start: OPENAI_FIRST,
      key: 'ArrowUp',
      focus: LOCAL_LAST,
      prevented: true,
    },
    {
      tab: 'Analysis',
      position: 'the first row',
      start: OPENAI_FIRST,
      key: 'ArrowLeft',
      focus: LOCAL_LAST,
      prevented: true,
    },
    {
      tab: 'Analysis',
      position: 'a middle row',
      start: CLAUDE_FIRST,
      key: 'Home',
      focus: CLAUDE_FIRST,
      prevented: false,
    },
    {
      tab: 'Analysis',
      position: 'a middle row',
      start: CLAUDE_FIRST,
      key: 'End',
      focus: CLAUDE_FIRST,
      prevented: false,
    },
    {
      tab: 'Analysis',
      position: 'a middle row',
      start: CLAUDE_FIRST,
      key: 'a',
      focus: CLAUDE_FIRST,
      prevented: false,
    },
    {
      tab: 'Analysis',
      position: 'a middle row',
      start: CLAUDE_FIRST,
      key: 'Enter',
      focus: CLAUDE_FIRST,
      prevented: false,
    },
    {
      tab: 'Analysis',
      position: 'a middle row',
      start: CLAUDE_FIRST,
      key: ' ',
      focus: CLAUDE_FIRST,
      prevented: false,
    },
    {
      tab: 'Translation',
      position: 'Same as Analysis, the first row',
      start: SAME,
      key: 'ArrowDown',
      focus: PICK,
      prevented: true,
    },
    {
      tab: 'Translation',
      position: 'a middle row',
      start: PICK,
      key: 'ArrowRight',
      focus: LAST_PICK,
      prevented: true,
    },
    {
      tab: 'Translation',
      position: 'the last row',
      start: LAST_PICK,
      key: 'ArrowDown',
      focus: SAME,
      prevented: true,
    },
    {
      tab: 'Translation',
      position: 'Same as Analysis, the first row',
      start: SAME,
      key: 'ArrowUp',
      focus: LAST_PICK,
      prevented: true,
    },
    {
      tab: 'Translation',
      position: 'a middle row',
      start: PICK,
      key: 'ArrowLeft',
      focus: SAME,
      prevented: true,
    },
    {
      tab: 'Translation',
      position: 'a middle row',
      start: PICK,
      key: 'Enter',
      focus: PICK,
      prevented: false,
    },
    {
      tab: 'Translation',
      position: 'a middle row',
      start: PICK,
      key: ' ',
      focus: PICK,
      prevented: false,
    },
  ])(
    'key x focus position, $tab, "$key" on $position: the focus goes to $focus, nothing is checked, nothing is written and Scout does not restart',
    async ({ tab, start, key, focus, prevented }) => {
      const { fixture, bridge, el } =
        tab === 'Analysis' ? await openKeyRows() : await openTranslation('OpenAI', 'nothing');
      const panel = tab === 'Analysis' ? 'panel-analysis' : 'panel-translation';
      const checked = (): string[] =>
        [...el.querySelectorAll(`[data-test="${panel}"] [role="radio"][aria-checked="true"]`)].map(
          (row) => row.getAttribute('data-test') ?? '',
        );
      const row = el.querySelector(`[data-test="${start}"]`) as HTMLButtonElement;
      row.focus();
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });

      row.dispatchEvent(event);
      await settle(fixture);

      expect({
        focused: (document.activeElement as HTMLElement | null)?.getAttribute('data-test'),
        prevented: event.defaultPrevented,
        checked: checked(),
        stores: bridge.stores,
        deletes: bridge.deletes,
        restarts: bridge.workerRestart.mock.calls.length,
      }).toEqual({
        focused: focus,
        prevented,
        checked: tab === 'Analysis' ? [OPENAI_FIRST] : [SAME],
        stores: [],
        deletes: [],
        restarts: 0,
      });
    },
  );
});

describe('Models → Analysis lists every model the local Ollama has', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  const LISTED: readonly OllamaModelInfo[] = [
    { name: 'gemma3:4b', modifiedAt: '', sizeBytes: 3_338_801_804, capabilities: ['completion'] },
    {
      name: 'sample-chat:3b',
      modifiedAt: '',
      sizeBytes: 2_019_393_189,
      family: 'llama',
      parameterSize: '3.2B',
      quantization: 'Q4_K_M',
      capabilities: ['completion', 'tools'],
    },
    {
      name: 'sample-vectors:latest',
      modifiedAt: '',
      sizeBytes: 669_615_493,
      family: 'bert',
      capabilities: ['embedding'],
    },
  ];

  it('Analysis lists every installed chat model and choosing one outside the catalog saves its tag and switches to Ollama', async () => {
    const { fixture, bridge, el } = await openAnalysis({
      keys: ['llm.openai.api-key'],
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4.1' },
      workerState: 'running',
      localRuntime: true,
      ollamaModels: LISTED,
    });
    expect({
      rows: localRowIds(el),
      outside: text(el, 'option-sample-chat:3b'),
    }).toEqual({
      rows: ['gemma-3-4b', 'sample-chat:3b'],
      outside: 'sample-chat:3b llama · 3.2B · Q4_K_M · 1.9 GB on disk',
    });
    bridge.stores.length = 0;

    click(el, 'option-sample-chat:3b');
    await settle(fixture);

    expect({
      stores: bridge.stores,
      restartAsked: bridge.workerRestart.mock.calls,
      strip: text(el, 'in-use-strip'),
      checked: el
        .querySelector('[data-test="option-sample-chat:3b"]')
        ?.getAttribute('aria-checked'),
    }).toEqual({
      stores: [
        { key: 'llm.model', value: 'sample-chat:3b' },
        { key: 'llm.provider', value: 'Ollama' },
      ],
      restartAsked: [[{ reason: 'Ollama' }]],
      strip: 'In use sample-chat:3b via Ollama',
      checked: 'true',
    });
  });

  it('an embeddings-only model is never a row in Analysis or Translation', async () => {
    const analysis = await openAnalysis({ localRuntime: true, ollamaModels: LISTED });
    const analysisRows = localRowIds(analysis.el);
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    const translation = await openTranslation('Ollama', 'nothing', { ollamaModels: LISTED });

    expect({ analysisRows, translationRows: translationRows(translation.el) }).toEqual({
      analysisRows: ['gemma-3-4b', 'sample-chat:3b'],
      translationRows: ['gemma3:4b', 'sample-chat:3b'],
    });
  });

  it('when the local Ollama does not answer, Analysis keeps the models it listed before and offers no new one', async () => {
    window.localStorage.setItem(
      LOCAL_MODELS_STORAGE_KEY,
      JSON.stringify({
        downloaded: ['gemma-3-4b', 'sample-chat:3b'],
        selected: { transcription: null, analysis: 'sample-chat:3b' },
      }),
    );
    const { el } = await openAnalysis({ lmStudioRuntime: true });

    expect({ rows: localRowIds(el), strip: text(el, 'in-use-strip') }).toEqual({
      rows: ['gemma-3-4b', 'sample-chat:3b'],
      strip: 'In use sample-chat:3b via Ollama',
    });
  });
});

describe('Models → Analysis says when a local model cannot call tools', () => {
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
    extra: Partial<OllamaModelInfo> = {},
  ): OllamaModelInfo {
    const base = { name, modifiedAt: '', sizeBytes: 838_860_800, ...extra };
    return capabilities === undefined ? base : { ...base, capabilities };
  }

  type State = 'in use with Ollama' | 'in use under a cloud provider' | 'not in use';

  async function opened(models: readonly OllamaModelInfo[], inUse: string | null, state: State) {
    window.localStorage.setItem(
      LOCAL_MODELS_STORAGE_KEY,
      JSON.stringify({
        downloaded: models.map((m) => m.name),
        selected: { transcription: null, analysis: inUse },
      }),
    );
    return openAnalysis({
      keys: ['llm.openai.api-key'],
      publicValues:
        state === 'in use with Ollama'
          ? { 'llm.provider': 'Ollama' }
          : state === 'in use under a cloud provider'
            ? { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4.1' }
            : {},
      workerState: 'running',
      localRuntime: true,
      ollamaModels: models,
    });
  }

  /** What Models, Analysis says about tools for `id`: the chip on its row, whether it can be chosen, and the strip's line. */
  function toolsWarning(el: HTMLElement, id: string) {
    const option = el.querySelector<HTMLButtonElement>(`[data-test="option-${id}"]`);
    const chip = option?.querySelector(`[data-test="no-tools-${id}"]`);
    return {
      row: option === null ? 'absent' : option.disabled ? 'disabled' : 'enabled',
      chip: chip == null ? null : (chip.textContent ?? '').trim(),
      stripLine: text(el, 'in-use-no-tools') || null,
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
    readonly stripLine: boolean;
  }[] = [
    { state: 'in use with Ollama', inUse: MODEL, stripLine: true },
    { state: 'in use under a cloud provider', inUse: MODEL, stripLine: false },
    { state: 'not in use', inUse: null, stripLine: false },
  ];

  for (const c of CAPABILITIES) {
    for (const s of STATES) {
      it(`capabilities ${c.name}, ${s.state}: the chip on the Local LLM row, the row still choosable, and the In use strip line`, async () => {
        const { el } = await opened([listedWith(MODEL, c.capabilities)], s.inUse, s.state);

        expect(toolsWarning(el, MODEL)).toEqual({
          row: c.chatModel ? 'enabled' : 'absent',
          chip: c.warns ? 'No tools' : null,
          stripLine: c.warns && s.stripLine ? SENTENCE : null,
        });
      });
    }
  }

  it('a model in use without tools says so in the In use strip and on its row, and can still be chosen', async () => {
    const { fixture, bridge, el } = await opened(
      [listedWith(MODEL, ['completion'])],
      MODEL,
      'in use under a cloud provider',
    );
    const before = { ...toolsWarning(el, MODEL), strip: text(el, 'in-use') };
    bridge.stores.length = 0;

    click(el, `option-${MODEL}`);
    await settle(fixture);

    expect({
      before,
      stores: bridge.stores,
      restartAsked: bridge.workerRestart.mock.calls,
      after: { ...toolsWarning(el, MODEL), strip: text(el, 'in-use') },
      stripRole: el.querySelector('[data-test="in-use-no-tools"]')?.getAttribute('role'),
    }).toEqual({
      before: {
        row: 'enabled',
        chip: 'No tools',
        stripLine: null,
        strip: 'In use gpt-4.1 via OpenAI API',
      },
      stores: [
        { key: 'llm.model', value: MODEL },
        { key: 'llm.provider', value: 'Ollama' },
      ],
      restartAsked: [[{ reason: 'Ollama' }]],
      after: {
        row: 'enabled',
        chip: 'No tools',
        stripLine: SENTENCE,
        strip: `In use ${MODEL} via Ollama`,
      },
      stripRole: 'status',
    });
  });

  it('In use without tools, as Models.dc.html previews it', async () => {
    const { el } = await opened(
      [
        listedWith('gemma3:4b', ['completion', 'tools']),
        listedWith('example-chat:1b', ['completion'], {
          family: 'example',
          parameterSize: '1.2B',
          quantization: 'Q4_K_M',
        }),
      ],
      'example-chat:1b',
      'in use with Ollama',
    );

    expect({
      strip: text(el, 'in-use-strip'),
      row: text(el, 'option-example-chat:1b'),
      toolCapableRow: text(el, 'option-gemma-3-4b'),
    }).toEqual({
      strip: `In use example-chat:1b via Ollama ${SENTENCE}`,
      row: 'example-chat:1b No tools example · 1.2B · Q4_K_M · 800 MB on disk',
      toolCapableRow:
        'Gemma 3 4B Vision Recommended default. Handles both text and image analysis.',
    });
  });
});
