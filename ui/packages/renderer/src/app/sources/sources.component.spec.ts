import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { OllamaUnreachableError } from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LLM_CONFIG_STORAGE_KEY } from '../core/llm-config.store';
import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { SourcesComponent } from './sources.component';

interface FakeBridge {
  llmListOllamaModels: ReturnType<typeof vi.fn>;
  llmListLmStudioModels: ReturnType<typeof vi.fn>;
  appOpenExternal: ReturnType<typeof vi.fn>;
  vaultStore: ReturnType<typeof vi.fn>;
  vaultHas: ReturnType<typeof vi.fn>;
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
    vaultStoreError?: unknown;
    vaultHas?: boolean;
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
    vaultStore: vi.fn(async () => {
      if (opts.vaultStoreError !== undefined) throw opts.vaultStoreError;
    }),
    vaultHas: vi.fn(async () => ({ exists: opts.vaultHas ?? false })),
  };
}

async function setup(
  opts: {
    models?: readonly FakeModel[];
    listError?: unknown;
    lmStudioModels?: readonly FakeLmStudioModel[];
    lmStudioError?: unknown;
    vaultStoreError?: unknown;
    vaultHas?: boolean;
  } = {},
): Promise<{
  fixture: ComponentFixture<SourcesComponent>;
  bridge: FakeBridge;
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
  fixture.detectChanges();
  await fixture.whenStable();
  await Promise.resolve();
  await Promise.resolve();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, bridge };
}

describe('SourcesComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  it('renders both Local and Cloud sections by default', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="section-local"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="section-cloud"]')).not.toBeNull();
  });

  it('renders all eight provider cards', async () => {
    const { fixture } = await setup();
    const expectedIds = [
      'local-models',
      'ollama',
      'lm-studio',
      'chatgpt-plus',
      'openai-api',
      'google-gemini',
      'openrouter',
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

  it('ChatGPT Plus, OpenRouter and Custom API cards keep disabled primary actions', async () => {
    const { fixture } = await setup();
    const ids = ['chatgpt-plus', 'openrouter', 'custom-api'];

    for (const id of ids) {
      const btn = fixture.nativeElement.querySelector(
        `[data-test="action-${id}"]`,
      ) as HTMLButtonElement;
      expect(btn.disabled, `${id} action should be disabled`).toBe(true);
    }
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
  function query<T extends Element>(fixture: ComponentFixture<SourcesComponent>, sel: string): T {
    return fixture.nativeElement.querySelector(sel) as T;
  }

  function type(input: HTMLInputElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  async function settle(fixture: ComponentFixture<SourcesComponent>): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('click the button to enter the token: the OpenAI API card opens an API-key form and saves the key', async () => {
    const { fixture, bridge } = await setup();
    const action = query<HTMLButtonElement>(fixture, '[data-test="action-openai-api"]');
    expect(action.disabled).toBe(false);
    expect(query(fixture, '[data-test="key-form-openai-api"]')).toBeNull();

    action.click();
    fixture.detectChanges();

    const keyInput = query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]');
    expect(keyInput.type).toBe('password');
    expect(keyInput.getAttribute('autocomplete')).toBe('off');
    expect(keyInput.closest('label')?.textContent).toContain('API key');
    const modelInput = query<HTMLInputElement>(fixture, '[data-test="model-openai-api"]');
    expect(modelInput.value).toBe('gpt-4o-mini');

    type(keyInput, 'sk-typed-key');
    type(modelInput, 'gpt-4o');
    query<HTMLButtonElement>(fixture, '[data-test="save-openai-api"]').click();
    await settle(fixture);

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.api-key', value: 'sk-typed-key' },
      { key: 'llm.cloud-model', value: 'gpt-4o' },
      { key: 'llm.provider', value: 'OpenAI' },
    ]);
    expect(query(fixture, '[data-test="key-form-openai-api"]')).toBeNull();
    const badge = query<HTMLElement>(fixture, '[data-test="badge-openai-api"]');
    expect(badge.textContent ?? '').toContain('Connected');
    expect(badge.textContent ?? '').not.toContain('Not Connected');
    expect(query<HTMLElement>(fixture, '[data-test="saved-openai-api"]').textContent).toContain(
      'Saved. Restart Scout from the status screen to use OpenAI.',
    );
  });

  it('click the button to enter the token: the Google Gemini card opens a form with its default model prefilled and saves the key', async () => {
    const { fixture, bridge } = await setup();
    const action = query<HTMLButtonElement>(fixture, '[data-test="action-google-gemini"]');
    expect(action.disabled).toBe(false);

    action.click();
    fixture.detectChanges();

    const keyInput = query<HTMLInputElement>(fixture, '[data-test="api-key-google-gemini"]');
    expect(keyInput.type).toBe('password');
    const modelInput = query<HTMLInputElement>(fixture, '[data-test="model-google-gemini"]');
    expect(modelInput.value).toBe('gemini-2.5-flash');

    type(keyInput, 'gemini-typed-key');
    query<HTMLButtonElement>(fixture, '[data-test="save-google-gemini"]').click();
    await settle(fixture);

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.api-key', value: 'gemini-typed-key' },
      { key: 'llm.cloud-model', value: 'gemini-2.5-flash' },
      { key: 'llm.provider', value: 'Gemini' },
    ]);
    const badge = query<HTMLElement>(fixture, '[data-test="badge-google-gemini"]');
    expect(badge.textContent ?? '').not.toContain('Not Connected');
    expect(query<HTMLElement>(fixture, '[data-test="saved-google-gemini"]').textContent).toContain(
      'Saved. Restart Scout from the status screen to use Gemini.',
    );
  });

  it('saving with an empty API key shows an error and stores nothing', async () => {
    const { fixture, bridge } = await setup();
    query<HTMLButtonElement>(fixture, '[data-test="action-openai-api"]').click();
    fixture.detectChanges();

    type(query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]'), '   ');
    query<HTMLButtonElement>(fixture, '[data-test="save-openai-api"]').click();
    await settle(fixture);

    expect(bridge.vaultStore).not.toHaveBeenCalled();
    const alert = query<HTMLElement>(fixture, '[data-test="key-form-openai-api"] [role="alert"]');
    expect(alert.textContent).toContain('Enter an API key.');
    expect(query(fixture, '[data-test="key-form-openai-api"]')).not.toBeNull();
  });

  it('a failed save shows the error text, keeps the form open and never shows the key', async () => {
    const { fixture } = await setup({ vaultStoreError: new Error('vault unavailable') });
    query<HTMLButtonElement>(fixture, '[data-test="action-openai-api"]').click();
    fixture.detectChanges();

    type(query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]'), 'sk-typed-key');
    query<HTMLButtonElement>(fixture, '[data-test="save-openai-api"]').click();
    await settle(fixture);

    const alert = query<HTMLElement>(fixture, '[data-test="key-form-openai-api"] [role="alert"]');
    expect(alert.textContent).toContain('vault unavailable');
    expect(fixture.nativeElement.textContent).not.toContain('sk-typed-key');
    expect(query<HTMLElement>(fixture, '[data-test="badge-openai-api"]').textContent).toContain(
      'Not Connected',
    );
  });

  it('cancel closes the form and clears the typed key', async () => {
    const { fixture, bridge } = await setup();
    query<HTMLButtonElement>(fixture, '[data-test="action-openai-api"]').click();
    fixture.detectChanges();
    type(query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]'), 'sk-typed-key');

    query<HTMLButtonElement>(fixture, '[data-test="cancel-openai-api"]').click();
    fixture.detectChanges();
    expect(query(fixture, '[data-test="key-form-openai-api"]')).toBeNull();

    query<HTMLButtonElement>(fixture, '[data-test="action-openai-api"]').click();
    fixture.detectChanges();
    expect(query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]').value).toBe('');
    expect(bridge.vaultStore).not.toHaveBeenCalled();
  });

  it('shows the active keyed cloud provider as Connected on load', async () => {
    window.localStorage.setItem(
      LLM_CONFIG_STORAGE_KEY,
      JSON.stringify({
        provider: 'Gemini',
        model: 'gemini-2.5-flash',
        endpoint: 'http://localhost:11434',
      }),
    );
    const { fixture } = await setup({ vaultHas: true });

    expect(
      query<HTMLElement>(fixture, '[data-test="badge-google-gemini"]').textContent,
    ).not.toContain('Not Connected');
    expect(query<HTMLElement>(fixture, '[data-test="badge-openai-api"]').textContent).toContain(
      'Not Connected',
    );
  });

  it('the ChatGPT Plus card is disabled and says it is not supported', async () => {
    const { fixture } = await setup();
    const card = query<HTMLElement>(fixture, '[data-test="card-chatgpt-plus"]');
    const action = query<HTMLButtonElement>(fixture, '[data-test="action-chatgpt-plus"]');

    expect(action.disabled).toBe(true);
    expect(action.textContent).toContain('Not supported');
    expect(card.textContent).toContain(
      'Not supported: a ChatGPT Plus or Pro subscription does not include API access. Use the OpenAI API card with an API key instead.',
    );
  });
});
