import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { OllamaUnreachableError } from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LLM_PROVIDERS_STORAGE_KEY, LlmConfigStore } from '../core/llm-config.store';
import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { SourcesComponent } from './sources.component';

interface FakeBridge {
  llmListOllamaModels: ReturnType<typeof vi.fn>;
  llmListLmStudioModels: ReturnType<typeof vi.fn>;
  appOpenExternal: ReturnType<typeof vi.fn>;
  vaultStore: ReturnType<typeof vi.fn>;
  vaultHas: ReturnType<typeof vi.fn>;
  vaultDelete: ReturnType<typeof vi.fn>;
  workerStop: ReturnType<typeof vi.fn>;
  workerStart: ReturnType<typeof vi.fn>;
}

interface FakeModel {
  name: string;
  modifiedAt: string;
  sizeBytes: number;
}

interface FakeLmStudioModel {
  id: string;
}

function makeBridge(
  opts: {
    models?: readonly FakeModel[];
    listError?: unknown;
    lmStudioModels?: readonly FakeLmStudioModel[];
    lmStudioError?: unknown;
    vaultHasResult?: boolean;
  } = {},
): FakeBridge {
  return {
    llmListOllamaModels: vi.fn(async () => {
      if (opts.listError !== undefined) throw opts.listError;
      return { models: opts.models ?? [] };
    }),
    llmListLmStudioModels: vi.fn(async () => {
      if (opts.lmStudioError !== undefined) throw opts.lmStudioError;
      return { models: opts.lmStudioModels ?? [] };
    }),
    appOpenExternal: vi.fn(async () => undefined),
    vaultStore: vi.fn(async () => undefined),
    vaultHas: vi.fn(async () => ({ exists: opts.vaultHasResult ?? true })),
    vaultDelete: vi.fn(async () => ({ deleted: true })),
    workerStop: vi.fn(async () => ({ stopped: true })),
    workerStart: vi.fn(async () => ({ pid: 123 })),
  };
}

async function setup(
  opts: {
    models?: readonly FakeModel[];
    listError?: unknown;
    lmStudioModels?: readonly FakeLmStudioModel[];
    lmStudioError?: unknown;
    vaultHasResult?: boolean;
  } = {},
): Promise<{
  fixture: ComponentFixture<SourcesComponent>;
  bridge: FakeBridge;
  llmConfig: LlmConfigStore;
}> {
  const bridge = makeBridge(opts);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const fixture = TestBed.createComponent(SourcesComponent);
  const llmConfig = TestBed.inject(LlmConfigStore);
  fixture.detectChanges();
  await fixture.whenStable();
  await Promise.resolve();
  await Promise.resolve();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, bridge, llmConfig };
}

describe('SourcesComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_PROVIDERS_STORAGE_KEY);
  });

  it('renders both Local and Cloud sections by default', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="section-local"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="section-cloud"]')).not.toBeNull();
  });

  it('renders all nine provider cards', async () => {
    const { fixture } = await setup();
    const expectedIds = [
      'local-models',
      'ollama',
      'lm-studio',
      'chatgpt-plus',
      'openai-api',
      'google-gemini',
      'openrouter',
      'azure-openai',
      'custom-api',
    ];

    for (const id of expectedIds) {
      expect(
        fixture.nativeElement.querySelector(`[data-test="card-${id}"]`),
        `card ${id} should render`,
      ).not.toBeNull();
    }
  });

  it('Local filter hides the Cloud section', async () => {
    const { fixture } = await setup();

    const button = fixture.nativeElement.querySelector(
      '[data-test="filter-local"]',
    ) as HTMLButtonElement;
    button.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="section-local"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="section-cloud"]')).toBeNull();
  });

  it('Cloud filter hides the Local section', async () => {
    const { fixture } = await setup();

    const button = fixture.nativeElement.querySelector(
      '[data-test="filter-cloud"]',
    ) as HTMLButtonElement;
    button.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="section-local"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="section-cloud"]')).not.toBeNull();
  });

  it('initial Ollama scan calls the bridge with default endpoint', async () => {
    const { bridge } = await setup();
    expect(bridge.llmListOllamaModels).toHaveBeenCalledWith({
      endpoint: 'http://localhost:11434',
    });
  });

  it('successful scan marks Ollama as Connected (reachable counts, even with zero models)', async () => {
    const { fixture } = await setup({ models: [] });

    const badge = fixture.nativeElement.querySelector('[data-test="badge-ollama"]') as HTMLElement;
    expect(badge.textContent ?? '').toContain('Connected');
    expect(badge.textContent ?? '').not.toContain('Not Connected');
  });

  it('Local Models stays Not Connected when runtime is reachable but no local model selected', async () => {
    const { fixture } = await setup();

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-local-models"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Not Connected');
  });

  it('Local Models flips to Connected once a model is selected via the store', async () => {
    const { fixture } = await setup();
    const store = TestBed.inject(LocalModelsStore);
    store.markDownloaded('whisper-large-v3-turbo');
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-local-models"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Connected');
    expect(badge.textContent ?? '').not.toContain('Not Connected');
  });

  it('Local Models stays Not Connected when both Ollama and LM Studio fail', async () => {
    const { fixture } = await setup({
      listError: new OllamaUnreachableError('http://localhost:11434'),
      lmStudioError: new Error('refused'),
    });

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-local-models"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Not Connected');
  });

  it('Manage Downloaded Models button is enabled when a local runtime is reachable', async () => {
    const { fixture } = await setup();

    const btn = fixture.nativeElement.querySelector(
      '[data-test="action-local-models"]',
    ) as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
  });

  it('Manage Downloaded Models button is disabled when neither runtime is reachable', async () => {
    const { fixture } = await setup({
      listError: new OllamaUnreachableError('http://localhost:11434'),
      lmStudioError: new Error('refused'),
    });

    const btn = fixture.nativeElement.querySelector(
      '[data-test="action-local-models"]',
    ) as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
  });

  it('clicking Manage Downloaded Models navigates to /sources/local-models', async () => {
    const { fixture } = await setup();
    const router = TestBed.inject(Router);
    const navSpy = vi.spyOn(router, 'navigateByUrl');

    const btn = fixture.nativeElement.querySelector(
      '[data-test="action-local-models"]',
    ) as HTMLButtonElement;
    btn.click();
    await fixture.whenStable();

    expect(navSpy).toHaveBeenCalledWith('/sources/local-models');
  });

  it('failed scan marks Ollama as Not Connected', async () => {
    const { fixture } = await setup({
      listError: new OllamaUnreachableError('http://localhost:11434'),
    });

    const badge = fixture.nativeElement.querySelector('[data-test="badge-ollama"]') as HTMLElement;
    expect(badge.textContent ?? '').toContain('Not Connected');
  });

  it('clicking Scan re-runs the bridge call', async () => {
    const { fixture, bridge } = await setup();
    bridge.llmListOllamaModels.mockClear();

    const action = fixture.nativeElement.querySelector(
      '[data-test="action-ollama"]',
    ) as HTMLButtonElement;
    action.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.llmListOllamaModels).toHaveBeenCalledTimes(1);
  });

  it('chatgpt-plus and custom-api stay disabled (coming soon)', async () => {
    const { fixture } = await setup();
    const ids = ['chatgpt-plus', 'custom-api'];

    for (const id of ids) {
      const btn = fixture.nativeElement.querySelector(
        `[data-test="action-${id}"]`,
      ) as HTMLButtonElement;
      expect(btn.disabled, `${id} action should be disabled`).toBe(true);
    }
  });

  it('openai-api, google-gemini, openrouter, and azure-openai primary actions are enabled', async () => {
    const { fixture } = await setup();
    const ids = ['openai-api', 'google-gemini', 'openrouter', 'azure-openai'];

    for (const id of ids) {
      const btn = fixture.nativeElement.querySelector(
        `[data-test="action-${id}"]`,
      ) as HTMLButtonElement;
      expect(btn.disabled, `${id} action should be enabled`).toBe(false);
    }
  });

  it('clicking an enabled cloud card opens the provider dialog', async () => {
    const { fixture } = await setup();
    const btn = fixture.nativeElement.querySelector(
      '[data-test="action-openai-api"]',
    ) as HTMLButtonElement;
    btn.click();
    fixture.detectChanges();

    const dialog = fixture.nativeElement.querySelector('[data-test="llm-provider-dialog"]');
    expect(dialog).not.toBeNull();
  });

  it('clicking a disabled cloud card does not open the provider dialog', async () => {
    const { fixture } = await setup();
    const btn = fixture.nativeElement.querySelector(
      '[data-test="action-chatgpt-plus"]',
    ) as HTMLButtonElement;
    btn.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="llm-provider-dialog"]')).toBeNull();
  });

  it('unconfigured cloud providers show a Not Connected badge', async () => {
    const { fixture } = await setup();
    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-openai-api"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Not Connected');
  });

  it('saving a provider through the dialog stores the secret via vault, mirrors only non-secret fields to localStorage, sets it active, and shows the restart banner', async () => {
    const { fixture, bridge } = await setup();
    const SECRET = 'sk-super-secret-value';

    (
      fixture.nativeElement.querySelector('[data-test="action-openai-api"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    const apiKeyInput = fixture.nativeElement.querySelector(
      '[data-test="llm-input-apiKey"]',
    ) as HTMLInputElement;
    apiKeyInput.value = SECRET;
    apiKeyInput.dispatchEvent(new Event('input'));

    const modelInput = fixture.nativeElement.querySelector(
      '[data-test="llm-input-model"]',
    ) as HTMLInputElement;
    modelInput.value = 'gpt-4o';
    modelInput.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="llm-dialog-submit"]') as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.vaultStore).toHaveBeenCalledWith({ key: 'llm.openai.api-key', value: SECRET });
    expect(bridge.vaultStore).toHaveBeenCalledWith({
      key: 'llm.active-id',
      value: 'openai',
    });
    expect(bridge.vaultStore).toHaveBeenCalledWith({ key: 'llm.provider', value: 'OpenAI' });

    // The secret must never leak into localStorage.
    const dump: Record<string, string> = {};
    for (let i = 0; i < window.localStorage.length; i++) {
      const k = window.localStorage.key(i);
      if (k === null) continue;
      dump[k] = window.localStorage.getItem(k) ?? '';
    }
    expect(JSON.stringify(dump)).not.toContain(SECRET);

    expect(fixture.nativeElement.querySelector('[data-test="llm-provider-dialog"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="llm-restart-banner"]')).not.toBeNull();

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-openai-api"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Active');
  });

  it('clicking Restart now stops then starts the worker and hides the banner', async () => {
    const { fixture, bridge } = await setup();

    (
      fixture.nativeElement.querySelector('[data-test="action-openai-api"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    const apiKeyInput = fixture.nativeElement.querySelector(
      '[data-test="llm-input-apiKey"]',
    ) as HTMLInputElement;
    apiKeyInput.value = 'sk-abc';
    apiKeyInput.dispatchEvent(new Event('input'));
    const modelInput = fixture.nativeElement.querySelector(
      '[data-test="llm-input-model"]',
    ) as HTMLInputElement;
    modelInput.value = 'gpt-4o';
    modelInput.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (
      fixture.nativeElement.querySelector('[data-test="llm-dialog-submit"]') as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="llm-restart-now"]') as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.workerStop).toHaveBeenCalled();
    expect(bridge.workerStart).toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('[data-test="llm-restart-banner"]')).toBeNull();
  });

  it('a second configured-but-inactive provider shows a Configured badge with a Set active affordance', async () => {
    const { fixture, llmConfig } = await setup();

    await llmConfig.saveProvider('openai', { apiKey: 'sk-abc', model: 'gpt-4o' });
    await llmConfig.setActive('openai');
    await llmConfig.saveProvider('gemini', { apiKey: 'g-key', model: 'gemini-2.5-flash' });
    fixture.detectChanges();

    const geminiBadge = fixture.nativeElement.querySelector(
      '[data-test="badge-google-gemini"]',
    ) as HTMLElement;
    expect(geminiBadge.textContent ?? '').toContain('Configured');

    const setActiveBtn = fixture.nativeElement.querySelector(
      '[data-test="set-active-google-gemini"]',
    ) as HTMLButtonElement;
    expect(setActiveBtn).not.toBeNull();

    setActiveBtn.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(llmConfig.activeProviderId()).toBe('gemini');
    const openaiBadge = fixture.nativeElement.querySelector(
      '[data-test="badge-openai-api"]',
    ) as HTMLElement;
    expect(openaiBadge.textContent ?? '').toContain('Configured');
  });

  it('initial LM Studio scan calls the bridge with the default port', async () => {
    const { bridge } = await setup();
    expect(bridge.llmListLmStudioModels).toHaveBeenCalledWith({
      endpoint: 'http://localhost:1234',
    });
  });

  it('successful LM Studio scan marks it Connected (reachable counts)', async () => {
    const { fixture } = await setup({ lmStudioModels: [] });

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-lm-studio"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Connected');
    expect(badge.textContent ?? '').not.toContain('Not Connected');
  });

  it('LM Studio scan failure marks it Not Connected', async () => {
    const { fixture } = await setup({ lmStudioError: new Error('refused') });

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-lm-studio"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Not Connected');
  });

  it('LM Studio Scan button is enabled', async () => {
    const { fixture } = await setup();
    const btn = fixture.nativeElement.querySelector(
      '[data-test="action-lm-studio"]',
    ) as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
  });

  it('Download Ollama secondary opens the external URL', async () => {
    const { fixture, bridge } = await setup();
    const link = fixture.nativeElement.querySelector(
      '[data-test="secondary-ollama"]',
    ) as HTMLButtonElement;
    expect(link).not.toBeNull();
    link.click();
    await fixture.whenStable();

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: 'https://ollama.com/download' });
  });

  it('Download LM Studio secondary opens the external URL', async () => {
    const { fixture, bridge } = await setup();
    const link = fixture.nativeElement.querySelector(
      '[data-test="secondary-lm-studio"]',
    ) as HTMLButtonElement;
    expect(link).not.toBeNull();
    link.click();
    await fixture.whenStable();

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: 'https://lmstudio.ai' });
  });
});
