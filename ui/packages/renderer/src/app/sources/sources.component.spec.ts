import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { OllamaUnreachableError } from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { SourcesComponent } from './sources.component';

interface FakeBridge {
  llmListOllamaModels: ReturnType<typeof vi.fn>;
  llmListLmStudioModels: ReturnType<typeof vi.fn>;
  appOpenExternal: ReturnType<typeof vi.fn>;
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
  };
}

async function setup(
  opts: {
    models?: readonly FakeModel[];
    listError?: unknown;
    lmStudioModels?: readonly FakeLmStudioModel[];
    lmStudioError?: unknown;
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

  it('cloud cards have disabled primary actions', async () => {
    const { fixture } = await setup();
    const ids = ['chatgpt-plus', 'openai-api', 'google-gemini', 'openrouter', 'custom-api'];

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
});
