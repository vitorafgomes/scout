import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ProviderKeyValidateResponse } from '@scout/shared';
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
  readonly validation?: ProviderKeyValidateResponse;
  readonly publicValues?: Readonly<Record<string, string>>;
  readonly publicReadError?: unknown;
  readonly vaultDeleteError?: unknown;
}

function makeBridge(opts: BridgeOptions = {}) {
  const vaultStoreCalls: VaultStoreCall[] = [];
  const vaultDeleteCalls: string[] = [];
  const bridge = {
    vaultStore: vi.fn(async (req: { key: string; value: string }) => {
      vaultStoreCalls.push({ key: req.key, value: req.value });
    }),
    vaultHas: vi.fn(async (req: { key: string }) => ({
      exists: opts.vaultHasByKey?.[req.key] ?? opts.vaultHasResult ?? true,
    })),
    vaultDelete: vi.fn(async (req: { key: string }) => {
      if (opts.vaultDeleteError !== undefined) throw opts.vaultDeleteError;
      vaultDeleteCalls.push(req.key);
      return { deleted: true };
    }),
    validateProviderKey: vi.fn(async () => opts.validation ?? { ok: true }),
    vaultGetPublic: vi.fn(async (req: { key: string }) => {
      if (opts.publicReadError !== undefined) throw opts.publicReadError;
      return { value: opts.publicValues?.[req.key] ?? null };
    }),
  } as unknown as ScoutBridgeService;
  return { bridge, vaultStoreCalls, vaultDeleteCalls };
}

function setup(opts: BridgeOptions = {}) {
  const { bridge, vaultStoreCalls, vaultDeleteCalls } = makeBridge(opts);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const store = TestBed.inject(LlmConfigStore);
  return { store, bridge, vaultStoreCalls, vaultDeleteCalls };
}

function everyLocalStorageValue(): string {
  const values: string[] = [];
  for (let i = 0; i < window.localStorage.length; i++) {
    const key = window.localStorage.key(i);
    if (key !== null) values.push(`${key}=${window.localStorage.getItem(key) ?? ''}`);
  }
  return values.join('\n');
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
    expect(bridge.vaultHas).not.toHaveBeenCalledWith({ key: 'llm.model' });
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
  it('connect while another cloud provider is in use validates the key, stores only that provider own key, and does not switch', async () => {
    const { store, bridge, vaultStoreCalls } = setup();
    await store.use('Gemini', 'gemini-2.5-flash');
    vaultStoreCalls.length = 0;

    const result = await store.connect('OpenAI', '  sk-test-key  ');

    expect(result).toEqual({ ok: true });
    expect(bridge.validateProviderKey).toHaveBeenCalledWith({
      provider: 'OpenAI',
      apiKey: 'sk-test-key',
    });
    expect(vaultStoreCalls).toEqual([{ key: 'llm.openai.api-key', value: 'sk-test-key' }]);
    expect(store.connectedProviders().has('OpenAI')).toBe(true);
    expect(store.activeCloudProvider()).toBe('Gemini');
  });

  it('connect stores a Gemini key in the Gemini slot only', async () => {
    const { store, vaultStoreCalls } = setup();
    await store.use('OpenAI', 'gpt-4o');
    vaultStoreCalls.length = 0;

    await store.connect('Gemini', 'gemini-key');

    expect(vaultStoreCalls).toEqual([{ key: 'llm.gemini.api-key', value: 'gemini-key' }]);
  });

  for (const current of [null, 'Ollama', 'None'] as const) {
    it(`it answers with the provider that is connected: the first connect while the provider is ${String(current)} makes it the provider in use with its default model`, async () => {
      const { store, vaultStoreCalls } = setup({
        publicValues: current === null ? {} : { 'llm.provider': current },
      });
      await store.loadFromVault();

      await store.connect('Gemini', 'gemini-key');

      expect(vaultStoreCalls).toEqual([
        { key: 'llm.gemini.api-key', value: 'gemini-key' },
        { key: 'llm.cloud-model', value: 'gemini-2.5-flash' },
        { key: 'llm.provider', value: 'Gemini' },
      ]);
      expect(store.activeCloudProvider()).toBe('Gemini');
    });
  }

  it('connect with a key the provider rejects stores nothing and reports the rejection', async () => {
    const { store, vaultStoreCalls } = setup({
      validation: { ok: false, reason: 'invalid-key' },
    });

    const result = await store.connect('OpenAI', 'sk-bad-key');

    expect(result).toEqual({ ok: false, reason: 'invalid-key' });
    expect(vaultStoreCalls).toEqual([]);
    expect(store.connectedProviders().has('OpenAI')).toBe(false);
    expect(store.provider()).toBeNull();
  });

  it('connect rejects a blank key without calling the provider', async () => {
    const { store, bridge, vaultStoreCalls } = setup();

    await expect(store.connect('OpenAI', '   ')).rejects.toThrow(/API key must be non-empty/);
    expect(bridge.validateProviderKey).not.toHaveBeenCalled();
    expect(vaultStoreCalls).toHaveLength(0);
  });

  it('use writes the cloud model and the provider, and makes it the active cloud provider', async () => {
    const { store, vaultStoreCalls } = setup();

    await store.use('Gemini', 'gemini-2.5-flash');

    expect(vaultStoreCalls).toEqual([
      { key: 'llm.cloud-model', value: 'gemini-2.5-flash' },
      { key: 'llm.provider', value: 'Gemini' },
    ]);
    expect(store.provider()).toBe('Gemini');
    expect(store.model()).toBe('gemini-2.5-flash');
    expect(store.activeCloudProvider()).toBe('Gemini');
  });

  it('use rejects a blank model and writes nothing', async () => {
    const { store, vaultStoreCalls } = setup();

    await expect(store.use('OpenAI', ' ')).rejects.toThrow(/model must be non-empty/);
    expect(vaultStoreCalls).toHaveLength(0);
  });

  it('disconnecting the last connected provider in use deletes its key and writes llm.provider=None, never Ollama', async () => {
    const { store, vaultStoreCalls, vaultDeleteCalls } = setup();
    await store.connect('OpenAI', 'sk-test-key');
    await store.use('OpenAI', 'gpt-4o');
    vaultStoreCalls.length = 0;

    const wasActive = await store.disconnect('OpenAI');

    expect(wasActive).toBe(true);
    expect(vaultDeleteCalls).toContain('llm.openai.api-key');
    expect(vaultDeleteCalls).toContain('llm.api-key');
    expect(vaultStoreCalls).toEqual([{ key: 'llm.provider', value: 'None' }]);
    expect(store.provider()).toBe('None');
    expect(store.activeCloudProvider()).toBeNull();
    expect(store.connectedProviders().has('OpenAI')).toBe(false);
  });

  it('disconnecting the provider in use switches to the other connected provider with its default model', async () => {
    const { store, vaultStoreCalls, vaultDeleteCalls } = setup();
    await store.connect('OpenAI', 'sk-test-key');
    await store.connect('Gemini', 'gemini-key');
    vaultStoreCalls.length = 0;

    const wasActive = await store.disconnect('OpenAI');

    expect(wasActive).toBe(true);
    expect(vaultStoreCalls).toEqual([
      { key: 'llm.cloud-model', value: 'gemini-2.5-flash' },
      { key: 'llm.provider', value: 'Gemini' },
    ]);
    expect(vaultDeleteCalls).toContain('llm.openai.api-key');
    expect(store.activeCloudProvider()).toBe('Gemini');
    expect(store.model()).toBe('gemini-2.5-flash');
  });

  it('useOllama makes Ollama the provider explicitly and writes only llm.provider', async () => {
    const { store, vaultStoreCalls } = setup();
    await store.use('OpenAI', 'gpt-4o');
    vaultStoreCalls.length = 0;

    await store.useOllama();

    expect(vaultStoreCalls).toEqual([{ key: 'llm.provider', value: 'Ollama' }]);
    expect(store.provider()).toBe('Ollama');
    expect(store.activeCloudProvider()).toBeNull();
  });

  it('loadFromVault with no mirror and the vault on Ollama shows Ollama as the provider', async () => {
    const { store } = setup({ publicValues: { 'llm.provider': 'Ollama' } });

    await store.loadFromVault();

    expect(store.provider()).toBe('Ollama');
    expect(store.activeCloudProvider()).toBeNull();
  });

  it('loadFromVault with the vault on None shows no provider in use', async () => {
    const { store } = setup({
      vaultHasByKey: { 'llm.openai.api-key': true },
      publicValues: { 'llm.provider': 'None' },
    });

    await store.loadFromVault();

    expect(store.provider()).toBe('None');
    expect(store.activeCloudProvider()).toBeNull();
    expect(store.connectedProviders().has('OpenAI')).toBe(true);
  });

  it('disconnecting a provider that is not in use deletes only its own key', async () => {
    const { store, vaultStoreCalls, vaultDeleteCalls } = setup();
    await store.connect('OpenAI', 'sk-test-key');
    await store.connect('Gemini', 'gemini-key');
    await store.use('Gemini', 'gemini-2.5-flash');
    vaultStoreCalls.length = 0;

    const wasActive = await store.disconnect('OpenAI');

    expect(wasActive).toBe(false);
    expect(vaultDeleteCalls).toEqual(['llm.openai.api-key']);
    expect(vaultStoreCalls).toEqual([]);
    expect(store.activeCloudProvider()).toBe('Gemini');
    expect(store.connectedProviders().has('Gemini')).toBe(true);
  });

  it('the key stays secret: no localStorage entry ever contains the API key', async () => {
    const SECRET = 'sk-secret-must-not-be-mirrored';
    const { store } = setup();

    await store.connect('OpenAI', SECRET);
    await store.use('OpenAI', 'gpt-4o-mini');

    expect(everyLocalStorageValue()).toContain('gpt-4o-mini');
    expect(everyLocalStorageValue()).not.toContain(SECRET);
  });

  it('loadFromVault reports which providers have a stored key', async () => {
    const { store } = setup({
      vaultHasByKey: { 'llm.openai.api-key': false, 'llm.gemini.api-key': true },
    });

    await store.loadFromVault();

    expect([...store.connectedProviders()]).toEqual(['Gemini']);
  });

  it('loadFromVault restores a cloud provider as active when the vault holds its own API key', async () => {
    const { store: writer } = setup();
    await writer.use('OpenAI', 'gpt-4o');
    TestBed.resetTestingModule();

    const { store } = setup({
      vaultHasByKey: { 'llm.openai.api-key': true, 'llm.model': false },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
    });
    await store.loadFromVault();

    expect(store.provider()).toBe('OpenAI');
    expect(store.model()).toBe('gpt-4o');
    expect(store.activeCloudProvider()).toBe('OpenAI');
  });

  it('the vault is the truth: with no localStorage mirror and the vault on OpenAI, OpenAI is in use and disconnecting it writes llm.provider=None', async () => {
    const { store, vaultStoreCalls } = setup({
      vaultHasByKey: { 'llm.openai.api-key': true, 'llm.gemini.api-key': false },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
    });

    await store.loadFromVault();

    expect(store.activeCloudProvider()).toBe('OpenAI');
    expect(store.model()).toBe('gpt-4o');

    const wasActive = await store.disconnect('OpenAI');

    expect(wasActive).toBe(true);
    expect(vaultStoreCalls).toEqual([{ key: 'llm.provider', value: 'None' }]);
    expect(store.activeCloudProvider()).toBeNull();
  });

  for (const [keyStored, expected] of [
    [true, 'OpenAI'],
    [false, null],
  ] as const) {
    it(`when the vault read fails, a cloud mirror is kept only while that provider's own key is in the vault (key stored: ${keyStored})`, async () => {
      window.localStorage.setItem(
        LLM_CONFIG_STORAGE_KEY,
        JSON.stringify({ provider: 'OpenAI', model: 'gpt-4o', endpoint: 'http://localhost:11434' }),
      );
      const { store } = setup({
        vaultHasByKey: { 'llm.openai.api-key': keyStored, 'llm.gemini.api-key': true },
        publicReadError: new Error('getPublic unavailable'),
      });

      await store.loadFromVault();

      expect(store.activeCloudProvider()).toBe(expected);
      expect(window.localStorage.getItem(LLM_CONFIG_STORAGE_KEY) !== null).toBe(keyStored);
    });
  }

  it('a key that cannot be deleted while disconnecting the provider in use still moves Scout off it, with the key still connected', async () => {
    const { store, vaultStoreCalls } = setup({
      vaultHasByKey: { 'llm.openai.api-key': true, 'llm.gemini.api-key': false },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
      vaultDeleteError: new Error('keyring locked'),
    });
    await store.loadFromVault();

    await expect(store.disconnect('OpenAI')).rejects.toThrow('keyring locked');

    expect(vaultStoreCalls).toEqual([{ key: 'llm.provider', value: 'None' }]);
    expect(store.activeCloudProvider()).toBeNull();
    expect(store.connectedProviders().has('OpenAI')).toBe(true);
  });

  it('the vault wins over a stale mirror: localStorage says Gemini, the vault says Ollama', async () => {
    window.localStorage.setItem(
      LLM_CONFIG_STORAGE_KEY,
      JSON.stringify({
        provider: 'Gemini',
        model: 'gemini-2.5-flash',
        endpoint: 'http://localhost:11434',
      }),
    );
    const { store } = setup({
      vaultHasByKey: { 'llm.gemini.api-key': true },
      publicValues: { 'llm.provider': 'Ollama' },
    });

    await store.loadFromVault();

    expect(store.activeCloudProvider()).toBeNull();
    expect(store.provider()).toBe('Ollama');
    expect(store.connectedProviders().has('Gemini')).toBe(true);
    expect(window.localStorage.getItem(LLM_CONFIG_STORAGE_KEY)).toBeNull();
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

    const { store } = setup({ vaultHasByKey: { 'llm.gemini.api-key': false, 'llm.model': true } });
    await store.loadFromVault();

    expect(store.provider()).toBeNull();
    expect(store.activeCloudProvider()).toBeNull();
    expect(store.isConfigured()).toBe(false);
    expect(window.localStorage.getItem(LLM_CONFIG_STORAGE_KEY)).toBeNull();
  });

  it('useChatGptPlan points the worker at the ChatGPT plan with the model, and writes no API key', async () => {
    const { store, vaultStoreCalls } = setup();

    await store.useChatGptPlan('gpt-5.6-luna');

    expect(vaultStoreCalls).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-5.6-luna' },
      { key: 'llm.provider', value: 'ChatGptPlan' },
    ]);
    expect(store.provider()).toBe('ChatGptPlan');
    expect(store.model()).toBe('gpt-5.6-luna');
  });

  it('loadFromVault keeps the ChatGPT plan selected on reload, checked against the stored model rather than an API key', async () => {
    const { store: writer } = setup();
    await writer.useChatGptPlan('gpt-5.6-luna');
    TestBed.resetTestingModule();

    const { store, bridge } = setup({
      vaultHasByKey: { 'llm.cloud-model': true, 'llm.api-key': false },
      publicReadError: new Error('getPublic unavailable'),
    });
    await store.loadFromVault();

    expect(bridge.vaultHas).toHaveBeenCalledWith({ key: 'llm.cloud-model' });
    expect(store.provider()).toBe('ChatGptPlan');
    expect(store.model()).toBe('gpt-5.6-luna');
  });

  it('the vault says ChatGptPlan: it is the provider in use with its stored model, whether or not the sign-in is known yet', async () => {
    const { store } = setup({
      vaultHasByKey: { 'llm.openai.api-key': false, 'llm.gemini.api-key': false },
      publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-6.1-sol' },
    });

    await store.loadFromVault();

    expect(store.activeCloudProvider()).toBe('ChatGptPlan');
    expect(store.model()).toBe('gpt-6.1-sol');
  });

  it('ChatGPT counts as a connected provider: disconnecting OpenAI in use switches to the signed-in ChatGPT plan with its default model', async () => {
    const { store, vaultStoreCalls } = setup({
      vaultHasByKey: { 'llm.openai.api-key': true, 'llm.gemini.api-key': false },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
    });
    await store.loadFromVault();
    store.setChatGptConnected(true);

    await store.disconnect('OpenAI');

    expect(vaultStoreCalls).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-5.6-luna' },
      { key: 'llm.provider', value: 'ChatGptPlan' },
    ]);
    expect(store.activeCloudProvider()).toBe('ChatGptPlan');
  });

  it('a status refresh that marks ChatGPT connected keeps it connected across loadFromVault and never changes the provider', async () => {
    const { store, vaultStoreCalls } = setup({
      vaultHasByKey: { 'llm.openai.api-key': false, 'llm.gemini.api-key': false },
      publicValues: { 'llm.provider': 'None' },
    });
    store.setChatGptConnected(true);

    await store.loadFromVault();

    expect(store.connectedProviders().has('ChatGptPlan')).toBe(true);
    expect(store.provider()).toBe('None');
    expect(vaultStoreCalls).toEqual([]);
  });

  it('connecting a key while ChatGPT is in use does not switch', async () => {
    const { store, vaultStoreCalls } = setup();
    await store.useChatGptPlan('gpt-5.6-luna');
    vaultStoreCalls.length = 0;

    await store.connect('OpenAI', 'sk-test-key');

    expect(vaultStoreCalls).toEqual([{ key: 'llm.openai.api-key', value: 'sk-test-key' }]);
    expect(store.activeCloudProvider()).toBe('ChatGptPlan');
  });

  it('signing out of ChatGPT in use switches to another connected cloud provider', async () => {
    const { store, vaultStoreCalls } = setup({
      vaultHasByKey: { 'llm.openai.api-key': false, 'llm.gemini.api-key': true },
      publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
    });
    await store.loadFromVault();
    store.setChatGptConnected(true);

    const wasActive = await store.disconnectChatGpt();

    expect(wasActive).toBe(true);
    expect(vaultStoreCalls).toEqual([
      { key: 'llm.cloud-model', value: 'gemini-2.5-flash' },
      { key: 'llm.provider', value: 'Gemini' },
    ]);
    expect(store.connectedProviders().has('ChatGptPlan')).toBe(false);
  });

  it('signing out of ChatGPT in use with no other connected provider writes None, never Ollama', async () => {
    const { store, vaultStoreCalls } = setup({
      vaultHasByKey: { 'llm.openai.api-key': false, 'llm.gemini.api-key': false },
      publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
    });
    await store.loadFromVault();
    store.setChatGptConnected(true);

    await store.disconnectChatGpt();

    expect(vaultStoreCalls).toEqual([{ key: 'llm.provider', value: 'None' }]);
    expect(store.provider()).toBe('None');
  });

  it('signing out of ChatGPT when it is not in use changes no provider', async () => {
    const { store, vaultStoreCalls } = setup({
      vaultHasByKey: { 'llm.openai.api-key': true, 'llm.gemini.api-key': false },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
    });
    await store.loadFromVault();
    store.setChatGptConnected(true);

    const wasActive = await store.disconnectChatGpt();

    expect(wasActive).toBe(false);
    expect(vaultStoreCalls).toEqual([]);
    expect(store.activeCloudProvider()).toBe('OpenAI');
  });

  it('the local selection always switches the worker back to Ollama', async () => {
    const { store, vaultStoreCalls } = setup();
    await store.use('OpenAI', 'gpt-4o');
    vaultStoreCalls.length = 0;

    await store.setSelection('Ollama', 'llama3.1', 'http://localhost:11434');

    expect(vaultStoreCalls).toContainEqual({ key: 'llm.provider', value: 'Ollama' });
    expect(vaultStoreCalls.filter((c) => c.key === 'llm.provider')).toHaveLength(1);
    expect(store.provider()).toBe('Ollama');
    expect(store.activeCloudProvider()).toBeNull();
  });
});
