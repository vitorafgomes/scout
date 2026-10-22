import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from './scout-bridge';
import { LlmConfigStore, LLM_CONFIG_STORAGE_KEY } from './llm-config.store';

interface VaultStoreCall {
  readonly key: string;
  readonly value: string;
}

interface BridgeOptions {
  readonly vaultHasResult?: boolean;
  readonly vaultHasByKey?: Readonly<Record<string, boolean>>;
}

function makeBridge(opts: BridgeOptions = {}) {
  const vaultStoreCalls: VaultStoreCall[] = [];
  const bridge = {
    vaultStore: vi.fn(async (req: { key: string; value: string }) => {
      vaultStoreCalls.push({ key: req.key, value: req.value });
    }),
    vaultHas: vi.fn(async (req: { key: string }) => ({
      exists: opts.vaultHasByKey?.[req.key] ?? opts.vaultHasResult ?? true,
    })),
  } as unknown as ScoutBridgeService;
  return { bridge, vaultStoreCalls };
}

function setup(opts: BridgeOptions = {}) {
  const { bridge, vaultStoreCalls } = makeBridge(opts);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const store = TestBed.inject(LlmConfigStore);
  return { store, bridge, vaultStoreCalls };
}

describe('LlmConfigStore', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });

  it('starts with default endpoint and no model', () => {
    const { store } = setup();
    expect(store.provider()).toBeNull();
    expect(store.model()).toBeNull();
    expect(store.endpoint()).toBe('http://localhost:11434');
    expect(store.isConfigured()).toBe(false);
  });

  it('setSelection writes to vault three times in order: provider, model, endpoint', async () => {
    const { store, vaultStoreCalls } = setup();

    await store.setSelection('Ollama', 'llama3.1', 'http://localhost:11434');

    expect(vaultStoreCalls).toHaveLength(3);
    expect(vaultStoreCalls[0]).toEqual({ key: 'llm.provider', value: 'Ollama' });
    expect(vaultStoreCalls[1]).toEqual({ key: 'llm.model', value: 'llama3.1' });
    expect(vaultStoreCalls[2]).toEqual({ key: 'llm.endpoint', value: 'http://localhost:11434' });
  });

  it('setSelection mirrors to localStorage in lockstep with vault writes', async () => {
    const { store } = setup();

    await store.setSelection('Ollama', 'qwen2.5', 'http://10.0.0.5:11434');

    const raw = window.localStorage.getItem(LLM_CONFIG_STORAGE_KEY);
    expect(raw).not.toBeNull();
    const parsed = JSON.parse(raw ?? '{}');
    expect(parsed).toEqual({
      provider: 'Ollama',
      model: 'qwen2.5',
      endpoint: 'http://10.0.0.5:11434',
    });
  });

  it('setSelection updates signals and flips isConfigured to true', async () => {
    const { store } = setup();

    await store.setSelection('Ollama', 'llama3.1', 'http://localhost:11434');

    expect(store.provider()).toBe('Ollama');
    expect(store.model()).toBe('llama3.1');
    expect(store.endpoint()).toBe('http://localhost:11434');
    expect(store.isConfigured()).toBe(true);
  });

  it('setSelection rejects empty model', async () => {
    const { store } = setup();
    await expect(store.setSelection('Ollama', '', 'http://localhost:11434')).rejects.toThrow(
      /model must be non-empty/,
    );
  });

  it('setSelection rejects empty endpoint', async () => {
    const { store } = setup();
    await expect(store.setSelection('Ollama', 'llama3.1', '')).rejects.toThrow(
      /endpoint must be non-empty/,
    );
  });

  it('loadFromVault populates signals when localStorage and vault agree', async () => {
    window.localStorage.setItem(
      LLM_CONFIG_STORAGE_KEY,
      JSON.stringify({
        provider: 'Ollama',
        model: 'llama3.1',
        endpoint: 'http://localhost:11434',
      }),
    );

    const { store, bridge } = setup({ vaultHasResult: true });
    await store.loadFromVault();

    expect(store.provider()).toBe('Ollama');
    expect(store.model()).toBe('llama3.1');
    expect(store.endpoint()).toBe('http://localhost:11434');
    expect(store.isConfigured()).toBe(true);
    expect(bridge.vaultHas).toHaveBeenCalledWith({ key: 'llm.model' });
  });

  it('loadFromVault treats vault.has=false as unconfigured and warns', async () => {
    window.localStorage.setItem(
      LLM_CONFIG_STORAGE_KEY,
      JSON.stringify({
        provider: 'Ollama',
        model: 'llama3.1',
        endpoint: 'http://localhost:11434',
      }),
    );

    const warnSpy = vi.spyOn(console, 'warn');
    const { store } = setup({ vaultHasResult: false });
    await store.loadFromVault();

    expect(store.isConfigured()).toBe(false);
    expect(store.model()).toBeNull();
    expect(window.localStorage.getItem(LLM_CONFIG_STORAGE_KEY)).toBeNull();
    expect(warnSpy).toHaveBeenCalled();
  });

  it('loadFromVault is a no-op when localStorage is empty', async () => {
    const { store, bridge } = setup({ vaultHasResult: true });
    await store.loadFromVault();

    expect(store.isConfigured()).toBe(false);
    expect(store.endpoint()).toBe('http://localhost:11434');
    // No need to ask vault when there's nothing to verify in the mirror.
    expect(bridge.vaultHas).not.toHaveBeenCalled();
  });

  it('loadFromVault ignores malformed localStorage entries', async () => {
    window.localStorage.setItem(LLM_CONFIG_STORAGE_KEY, 'not-json');
    const { store } = setup({ vaultHasResult: true });
    await store.loadFromVault();
    expect(store.isConfigured()).toBe(false);
  });

  it('setEndpointDraft updates only the endpoint signal without touching vault', async () => {
    const { store, vaultStoreCalls } = setup();
    store.setEndpointDraft('http://my-laptop.local:11434');
    expect(store.endpoint()).toBe('http://my-laptop.local:11434');
    expect(vaultStoreCalls).toHaveLength(0);
  });
  it('setCloudProvider stores exactly the API key, the cloud model and the provider in the vault', async () => {
    const { store, vaultStoreCalls } = setup();

    await store.setCloudProvider('OpenAI', 'sk-test-key', 'gpt-4o');

    expect(vaultStoreCalls).toEqual([
      { key: 'llm.api-key', value: 'sk-test-key' },
      { key: 'llm.cloud-model', value: 'gpt-4o' },
      { key: 'llm.provider', value: 'OpenAI' },
    ]);
  });

  it('setCloudProvider makes the provider the active cloud provider with its model', async () => {
    const { store } = setup();

    await store.setCloudProvider('Gemini', 'gemini-key', 'gemini-2.5-flash');

    expect(store.provider()).toBe('Gemini');
    expect(store.model()).toBe('gemini-2.5-flash');
    expect(store.activeCloudProvider()).toBe('Gemini');
    expect(store.isConfigured()).toBe(true);
  });

  it('setCloudProvider rejects an empty or blank API key and writes nothing', async () => {
    const { store, vaultStoreCalls } = setup();

    await expect(store.setCloudProvider('OpenAI', '   ', 'gpt-4o-mini')).rejects.toThrow(
      /API key must be non-empty/,
    );
    expect(vaultStoreCalls).toHaveLength(0);
  });

  it('setCloudProvider rejects an empty or blank model and writes nothing', async () => {
    const { store, vaultStoreCalls } = setup();

    await expect(store.setCloudProvider('OpenAI', 'sk-test-key', ' ')).rejects.toThrow(
      /model must be non-empty/,
    );
    expect(vaultStoreCalls).toHaveLength(0);
  });

  it('the key stays secret: the localStorage mirror never contains the API key', async () => {
    const SECRET = 'sk-secret-must-not-be-mirrored';
    const { store } = setup();

    await store.setCloudProvider('OpenAI', SECRET, 'gpt-4o-mini');

    const raw = window.localStorage.getItem(LLM_CONFIG_STORAGE_KEY);
    expect(raw).not.toBeNull();
    expect(raw).not.toContain(SECRET);
    expect(JSON.parse(raw ?? '{}')).toMatchObject({ provider: 'OpenAI', model: 'gpt-4o-mini' });
  });

  it('loadFromVault restores a cloud provider as active when the vault holds an API key', async () => {
    const { store: writer } = setup();
    await writer.setCloudProvider('OpenAI', 'sk-test-key', 'gpt-4o');
    TestBed.resetTestingModule();

    const { store } = setup({ vaultHasByKey: { 'llm.api-key': true, 'llm.model': false } });
    await store.loadFromVault();

    expect(store.provider()).toBe('OpenAI');
    expect(store.model()).toBe('gpt-4o');
    expect(store.activeCloudProvider()).toBe('OpenAI');
  });

  it('loadFromVault treats a cloud provider with no API key in the vault as unconfigured', async () => {
    window.localStorage.setItem(
      LLM_CONFIG_STORAGE_KEY,
      JSON.stringify({
        provider: 'Gemini',
        model: 'gemini-2.5-flash',
        endpoint: 'http://localhost:11434',
      }),
    );

    const { store } = setup({ vaultHasByKey: { 'llm.api-key': false, 'llm.model': true } });
    await store.loadFromVault();

    expect(store.provider()).toBeNull();
    expect(store.activeCloudProvider()).toBeNull();
    expect(store.isConfigured()).toBe(false);
    expect(window.localStorage.getItem(LLM_CONFIG_STORAGE_KEY)).toBeNull();
  });

  it('the local selection always switches the worker back to Ollama', async () => {
    const { store, vaultStoreCalls } = setup();
    await store.setCloudProvider('OpenAI', 'sk-test-key', 'gpt-4o');
    vaultStoreCalls.length = 0;

    await store.setSelection('Ollama', 'llama3.1', 'http://localhost:11434');

    expect(vaultStoreCalls).toContainEqual({ key: 'llm.provider', value: 'Ollama' });
    expect(vaultStoreCalls.filter((c) => c.key === 'llm.provider')).toHaveLength(1);
    expect(store.provider()).toBe('Ollama');
    expect(store.activeCloudProvider()).toBeNull();
  });
});
