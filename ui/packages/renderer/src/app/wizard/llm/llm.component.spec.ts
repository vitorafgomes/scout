import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { OllamaUnreachableError } from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LlmConfigStore, LLM_CONFIG_STORAGE_KEY } from '../../core/llm-config.store';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { LlmComponent } from './llm.component';

interface FakeBridge {
  llmListOllamaModels: ReturnType<typeof vi.fn>;
  workerStart: ReturnType<typeof vi.fn>;
  workerStop: ReturnType<typeof vi.fn>;
  vaultStore: ReturnType<typeof vi.fn>;
  vaultHas: ReturnType<typeof vi.fn>;
}

function makeBridge(opts: {
  models?: ReadonlyArray<{ name: string; modifiedAt: string; sizeBytes: number }>;
  listError?: unknown;
  vaultHas?: boolean;
}): FakeBridge {
  return {
    llmListOllamaModels: vi.fn(async () => {
      if (opts.listError !== undefined) throw opts.listError;
      return { models: opts.models ?? [] };
    }),
    workerStart: vi.fn(async () => ({ pid: 1 })),
    workerStop: vi.fn(async () => ({ exitCode: 0 })),
    vaultStore: vi.fn(async () => undefined),
    vaultHas: vi.fn(async () => ({ exists: opts.vaultHas ?? false })),
  };
}

async function setup(opts: {
  models?: ReadonlyArray<{ name: string; modifiedAt: string; sizeBytes: number }>;
  listError?: unknown;
  vaultHas?: boolean;
}): Promise<{
  fixture: ComponentFixture<LlmComponent>;
  bridge: FakeBridge;
  store: LlmConfigStore;
}> {
  const bridge = makeBridge(opts);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const store = TestBed.inject(LlmConfigStore);
  const fixture = TestBed.createComponent(LlmComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  await Promise.resolve();
  await Promise.resolve();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, bridge, store };
}

async function selectOllama(fixture: ComponentFixture<LlmComponent>): Promise<void> {
  const radio = fixture.nativeElement.querySelector(
    '[data-test="provider-ollama"]',
  ) as HTMLInputElement;
  radio.checked = true;
  radio.dispatchEvent(new Event('change'));
  fixture.detectChanges();
  await fixture.whenStable();
  await Promise.resolve();
  await fixture.whenStable();
  fixture.detectChanges();
}

describe('LlmComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  it('does not carry a cloud model into the local selection', async () => {
    window.localStorage.setItem(
      LLM_CONFIG_STORAGE_KEY,
      JSON.stringify({ provider: 'OpenAI', model: 'gpt-4o', endpoint: 'http://localhost:11434' }),
    );
    const { fixture, bridge } = await setup({
      vaultHas: true,
      listError: new OllamaUnreachableError('http://localhost:11434'),
    });

    expect(fixture.componentInstance.selectedModel()).toBeNull();
    expect(fixture.componentInstance.canSave()).toBe(false);
    const save = fixture.nativeElement.querySelector(
      '[data-test="save"]',
    ) as HTMLButtonElement | null;
    expect(save === null || save.disabled).toBe(true);
    expect(bridge.vaultStore).not.toHaveBeenCalled();
  });

  it('hides Endpoint and Model sections until a provider is selected', async () => {
    const { fixture, bridge } = await setup({ models: [] });

    expect(fixture.nativeElement.querySelector('[data-test="endpoint-input"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="model-select"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="save"]')).toBeNull();
    expect(bridge.llmListOllamaModels).not.toHaveBeenCalled();
  });

  it('selecting Ollama reveals Endpoint, Model and Save controls', async () => {
    const { fixture } = await setup({ models: [] });
    await selectOllama(fixture);

    expect(fixture.nativeElement.querySelector('[data-test="endpoint-input"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="model-select"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="save"]')).not.toBeNull();
  });

  it('selecting Ollama triggers a model refresh against the default endpoint', async () => {
    const { fixture, bridge } = await setup({ models: [] });
    await selectOllama(fixture);

    expect(bridge.llmListOllamaModels).toHaveBeenCalledWith({
      endpoint: 'http://localhost:11434',
    });
  });

  it('renders the returned model names as <option> entries', async () => {
    const { fixture } = await setup({
      models: [
        { name: 'llama3.1', modifiedAt: '2026-01-02T00:00:00Z', sizeBytes: 1 },
        { name: 'qwen2.5', modifiedAt: '2026-01-03T00:00:00Z', sizeBytes: 2 },
      ],
    });
    await selectOllama(fixture);

    const opts = fixture.nativeElement.querySelectorAll(
      '[data-test="model-select"] option',
    ) as NodeListOf<HTMLOptionElement>;
    const labels = Array.from(opts).map((o) => o.textContent?.trim() ?? '');
    expect(labels).toContain('llama3.1');
    expect(labels).toContain('qwen2.5');
  });

  it('Save button is disabled when no model is selected', async () => {
    const { fixture } = await setup({
      models: [{ name: 'llama3.1', modifiedAt: '2026-01-02T00:00:00Z', sizeBytes: 1 }],
    });
    await selectOllama(fixture);

    const save = fixture.nativeElement.querySelector('[data-test="save"]') as HTMLButtonElement;
    expect(save.disabled).toBe(true);
  });

  it('Save button enables once a model is picked', async () => {
    const { fixture } = await setup({
      models: [{ name: 'llama3.1', modifiedAt: '2026-01-02T00:00:00Z', sizeBytes: 1 }],
    });
    await selectOllama(fixture);

    const select = fixture.nativeElement.querySelector(
      '[data-test="model-select"]',
    ) as HTMLSelectElement;
    select.value = 'llama3.1';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const save = fixture.nativeElement.querySelector('[data-test="save"]') as HTMLButtonElement;
    expect(save.disabled).toBe(false);
  });

  it('Save calls LlmConfigStore.setSelection with provider, model, endpoint', async () => {
    const { fixture, bridge } = await setup({
      models: [{ name: 'llama3.1', modifiedAt: '2026-01-02T00:00:00Z', sizeBytes: 1 }],
    });
    await selectOllama(fixture);

    const select = fixture.nativeElement.querySelector(
      '[data-test="model-select"]',
    ) as HTMLSelectElement;
    select.value = 'llama3.1';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const save = fixture.nativeElement.querySelector('[data-test="save"]') as HTMLButtonElement;
    save.click();
    await fixture.whenStable();

    expect(bridge.vaultStore).toHaveBeenCalledTimes(3);
    const calls = bridge.vaultStore.mock.calls.map((c) => c[0] as { key: string; value: string });
    expect(calls[0]).toEqual({ key: 'llm.provider', value: 'Ollama' });
    expect(calls[1]).toEqual({ key: 'llm.model', value: 'llama3.1' });
    expect(calls[2]).toEqual({ key: 'llm.endpoint', value: 'http://localhost:11434' });
  });

  it('shows a friendly error when the bridge rejects with OllamaUnreachableError', async () => {
    const { fixture } = await setup({
      listError: new OllamaUnreachableError('http://localhost:11434'),
    });
    await selectOllama(fixture);

    const err = fixture.nativeElement.querySelector('[data-test="load-error"]');
    expect(err).not.toBeNull();
    expect((err?.textContent ?? '').toLowerCase()).toContain('could not reach ollama');
  });

  it('Restart button calls workerStop then workerStart', async () => {
    const { fixture, bridge } = await setup({
      models: [{ name: 'llama3.1', modifiedAt: '2026-01-02T00:00:00Z', sizeBytes: 1 }],
    });
    await selectOllama(fixture);

    const select = fixture.nativeElement.querySelector(
      '[data-test="model-select"]',
    ) as HTMLSelectElement;
    select.value = 'llama3.1';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const save = fixture.nativeElement.querySelector('[data-test="save"]') as HTMLButtonElement;
    save.click();
    await fixture.whenStable();
    fixture.detectChanges();

    const restart = fixture.nativeElement.querySelector(
      '[data-test="restart-now"]',
    ) as HTMLButtonElement;
    expect(restart).not.toBeNull();
    restart.click();
    await fixture.whenStable();

    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(bridge.workerStop.mock.invocationCallOrder[0]).toBeLessThan(
      bridge.workerStart.mock.invocationCallOrder[0]!,
    );
  });

  it('rejects an invalid endpoint scheme inline', async () => {
    const { fixture } = await setup({ models: [] });
    await selectOllama(fixture);

    const input = fixture.nativeElement.querySelector(
      '[data-test="endpoint-input"]',
    ) as HTMLInputElement;
    input.value = 'javascript:alert(1)';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const err = fixture.nativeElement.querySelector('[data-test="endpoint-error"]');
    expect(err).not.toBeNull();
    const save = fixture.nativeElement.querySelector('[data-test="save"]') as HTMLButtonElement;
    expect(save.disabled).toBe(true);
  });
});
