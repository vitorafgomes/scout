import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ProviderKeyValidateResponse } from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from './scout-bridge';
import {
  KeyStoredNotSwitchedError,
  LlmConfigStore,
  LLM_CONFIG_STORAGE_KEY,
} from './llm-config.store';

interface VaultStoreCall {
  readonly key: string;
  readonly value: string;
}

interface BridgeOptions {
  readonly vaultHasResult?: boolean;
  readonly vaultHasByKey?: Readonly<Record<string, boolean>>;
  readonly vaultHasErrorKeys?: readonly string[];
  readonly validation?: ProviderKeyValidateResponse;
  readonly publicValues?: Readonly<Record<string, string>>;
  readonly publicReadError?: unknown;
  readonly vaultDeleteError?: unknown;
  readonly chatgptStatus?: () => Promise<{ signedIn: boolean; planUsageGranted: boolean }>;
  readonly unload?: () => Promise<{ unloaded: readonly string[]; refused: readonly string[] }>;
  readonly providerSaveError?: { readonly value: string; readonly error: unknown };
}

function makeBridge(opts: BridgeOptions = {}) {
  const vaultStoreCalls: VaultStoreCall[] = [];
  const vaultDeleteCalls: string[] = [];
  const bridge = {
    vaultStore: vi.fn(async (req: { key: string; value: string }) => {
      if (req.key === 'llm.provider' && req.value === opts.providerSaveError?.value) {
        throw opts.providerSaveError.error;
      }
      vaultStoreCalls.push({ key: req.key, value: req.value });
    }),
    vaultHas: vi.fn(async (req: { key: string }) => {
      if (opts.vaultHasErrorKeys?.includes(req.key) === true) throw new Error('vault unavailable');
      return { exists: opts.vaultHasByKey?.[req.key] ?? opts.vaultHasResult ?? true };
    }),
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
    chatgptStatus: vi.fn(
      opts.chatgptStatus ?? (async () => ({ signedIn: true, email: null, planUsageGranted: true })),
    ),
    llmUnloadOllamaModels: vi.fn(opts.unload ?? (async () => ({ unloaded: [], refused: [] }))),
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
    window.localStorage.removeItem('scout:ollama-disconnected');
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });

  describe('Disconnect Ollama', () => {
    it('with Ollama the provider in use and no cloud provider connected, writes llm.provider=None, asks Ollama to free its memory and marks Ollama disconnected', async () => {
      const unload = vi.fn(async () => ({ unloaded: ['llama3.2'], refused: [] }));
      const { store, vaultStoreCalls } = setup({ vaultHasResult: false, unload });
      await store.loadFromVault();

      await store.disconnectOllama();
      await Promise.resolve();

      expect({
        stored: vaultStoreCalls,
        provider: store.provider(),
        disconnected: store.ollamaDisconnected(),
        kept: window.localStorage.getItem('scout:ollama-disconnected'),
        freed: unload.mock.calls.length,
      }).toEqual({
        stored: [{ key: 'llm.provider', value: 'None' }],
        provider: 'None',
        disconnected: true,
        kept: '1',
        freed: 1,
      });
    });

    it('with Ollama in use and a cloud provider connected, switches to that provider instead of None', async () => {
      const { store, vaultStoreCalls } = setup();
      await store.connect('OpenAI', 'sk-test-key');
      await store.useOllama();
      vaultStoreCalls.length = 0;

      await store.disconnectOllama();

      expect([store.activeCloudProvider(), vaultStoreCalls.at(-1)]).toEqual([
        'OpenAI',
        { key: 'llm.provider', value: 'OpenAI' },
      ]);
    });

    it('with a cloud provider in use, keeps it and only marks Ollama disconnected', async () => {
      const { store, vaultStoreCalls } = setup();
      await store.connect('OpenAI', 'sk-test-key');
      await store.use('OpenAI', 'gpt-4o');
      vaultStoreCalls.length = 0;

      await store.disconnectOllama();

      expect([store.activeCloudProvider(), vaultStoreCalls, store.ollamaDisconnected()]).toEqual([
        'OpenAI',
        [],
        true,
      ]);
    });

    it('the choice is read again by a new store, and Connect clears it without making Ollama the provider in use', async () => {
      const first = setup({ vaultHasResult: false });
      await first.store.loadFromVault();
      await first.store.disconnectOllama();
      TestBed.resetTestingModule();

      const { store } = setup({ vaultHasResult: false, publicValues: { 'llm.provider': 'None' } });
      await store.loadFromVault();
      const remembered = store.ollamaDisconnected();
      store.connectOllama();

      expect([
        remembered,
        store.ollamaDisconnected(),
        window.localStorage.getItem('scout:ollama-disconnected'),
        store.provider(),
      ]).toEqual([true, false, null, 'None']);
    });

    it('when the vault could not say which provider is in use, Disconnect writes nothing and says why', async () => {
      const { store, vaultStoreCalls } = setup({ vaultHasErrorKeys: ['llm.openai.api-key'] });
      await store.loadFromVault();

      await expect(store.disconnectOllama()).rejects.toThrow(
        'Scout could not read which AI Source is in use, so Ollama was not disconnected. Try again.',
      );
      expect([vaultStoreCalls, store.ollamaDisconnected()]).toEqual([[], false]);
    });

    it('when the browser storage refuses the choice, it still holds for this session', async () => {
      const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
        throw new Error('QuotaExceededError');
      });
      const { store } = setup({ vaultHasResult: false });
      await store.loadFromVault();

      await store.disconnectOllama();
      setItem.mockRestore();

      expect(store.ollamaDisconnected()).toBe(true);
    });
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

  it('a provider switch whose llm.provider write fails leaves the stored chat model matching the provider still stored', async () => {
    const { store, bridge } = setup();
    await store.connect('OpenAI', 'sk-test-key');
    await store.connect('Gemini', 'gemini-key');
    const vault = new Map<string, string>([
      ['llm.provider', 'OpenAI'],
      ['llm.cloud-model', 'gpt-4o-mini'],
    ]);
    vi.mocked(bridge.vaultStore).mockImplementation(async (req) => {
      if (req.key === 'llm.provider') throw new Error('keyring locked');
      vault.set(req.key, req.value);
    });

    await expect(store.disconnect('OpenAI')).rejects.toThrow('keyring locked');

    expect(vault.get('llm.provider')).toBe('OpenAI');
    expect(vault.get('llm.cloud-model')).toBe('gpt-4o-mini');
  });

  it.each([
    {
      label: 'the provider in use with another connected',
      arrange: (store: LlmConfigStore) => store.connect('Gemini', 'gemini-key'),
      act: (store: LlmConfigStore) => store.disconnect('OpenAI'),
    },
    {
      label: 'the last provider in use',
      arrange: async () => undefined,
      act: (store: LlmConfigStore) => store.disconnect('OpenAI'),
    },
    {
      label: 'the ChatGPT sign-in in use',
      arrange: (store: LlmConfigStore) => {
        store.setChatGptConnected(true);
        return store.useChatGptPlan('gpt-5.6-luna');
      },
      act: (store: LlmConfigStore) => store.disconnectChatGpt(),
    },
  ])(
    'disconnecting $label never writes or deletes the translation keys, so the next start falls back to the Analysis model',
    async ({ arrange, act }) => {
      const { store, vaultStoreCalls, vaultDeleteCalls } = setup();
      await store.connect('OpenAI', 'sk-test-key');
      await arrange(store);
      vaultStoreCalls.length = 0;

      await act(store);

      const touched = [...vaultStoreCalls.map((call) => call.key), ...vaultDeleteCalls];
      expect(touched.filter((key) => key.startsWith('llm.translation-'))).toEqual([]);
      expect(touched).toContain('llm.provider');
    },
  );

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
      vaultHasByKey: {
        'llm.openai.api-key': false,
        'llm.gemini.api-key': true,
        'llm.anthropic.api-key': false,
        'llm.openrouter.api-key': false,
      },
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
      vaultHasByKey: {
        'llm.openai.api-key': true,
        'llm.gemini.api-key': false,
        'llm.anthropic.api-key': false,
        'llm.openrouter.api-key': false,
      },
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

  for (const [providerRead, publicValues] of [
    ['reads OpenAI', { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' }],
    ['fails', undefined],
  ] as const) {
    it(`keeps a persisted cloud selection when the vault key check fails (llm.provider ${providerRead})`, async () => {
      const mirror = JSON.stringify({
        provider: 'OpenAI',
        model: 'gpt-4o',
        endpoint: 'http://localhost:11434',
      });
      window.localStorage.setItem(LLM_CONFIG_STORAGE_KEY, mirror);
      const failing = setup({
        vaultHasErrorKeys: ['llm.openai.api-key'],
        ...(publicValues === undefined
          ? { publicReadError: new Error('getPublic unavailable') }
          : { publicValues }),
      });

      await failing.store.loadFromVault();

      expect(window.localStorage.getItem(LLM_CONFIG_STORAGE_KEY)).toBe(mirror);
      expect(failing.store.readFailed()).toBe(true);
      expect(failing.store.mirroredProvider()).toBe('OpenAI');

      TestBed.resetTestingModule();
      const { store } = setup({ publicReadError: new Error('getPublic unavailable') });
      await store.loadFromVault();

      expect(store.activeCloudProvider()).toBe('OpenAI');
      expect(store.model()).toBe('gpt-4o');
    });
  }

  for (const [provider, checkedKey, model] of [
    ['ChatGptPlan', 'llm.cloud-model', 'gpt-5.6-luna'],
    ['Ollama', 'llm.model', 'llama3.1'],
  ] as const) {
    it(`a ${provider} selection survives a failed vault read: the ${checkedKey} check failing keeps the mirror`, async () => {
      const mirror = JSON.stringify({ provider, model, endpoint: 'http://localhost:11434' });
      window.localStorage.setItem(LLM_CONFIG_STORAGE_KEY, mirror);
      const failing = setup({
        vaultHasErrorKeys: [checkedKey],
        publicReadError: new Error('getPublic unavailable'),
      });

      await failing.store.loadFromVault();

      expect(window.localStorage.getItem(LLM_CONFIG_STORAGE_KEY)).toBe(mirror);
      expect(failing.store.readFailed()).toBe(true);
      expect(failing.store.mirroredProvider()).toBe(provider);

      TestBed.resetTestingModule();
      const { store } = setup({ publicReadError: new Error('getPublic unavailable') });
      await store.loadFromVault();

      expect(store.provider()).toBe(provider);
      expect(store.model()).toBe(model);
    });
  }

  it('a key that cannot be deleted while disconnecting the provider in use still moves Scout off it, with the key still connected', async () => {
    const { store, vaultStoreCalls } = setup({
      vaultHasByKey: {
        'llm.openai.api-key': true,
        'llm.gemini.api-key': false,
        'llm.anthropic.api-key': false,
        'llm.openrouter.api-key': false,
      },
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

  it('a ChatGPT status that can no longer be read never lets disconnecting OpenAI in use move Scout to the ChatGPT plan', async () => {
    const { store, vaultStoreCalls } = setup({
      vaultHasResult: false,
      vaultHasByKey: { 'llm.openai.api-key': true },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
      chatgptStatus: async () => {
        throw new Error('[scout:ipc/unavailable] The main process did not answer.');
      },
    });
    await store.loadFromVault();
    store.setChatGptConnected(true);

    await store.disconnect('OpenAI');

    expect(vaultStoreCalls).toEqual([{ key: 'llm.provider', value: 'None' }]);
    expect(store.connectedProviders().has('ChatGptPlan')).toBe(false);
  });

  it('connecting a key while the chosen ChatGPT plan has no session makes that provider the one in use', async () => {
    const { store, vaultStoreCalls } = setup();
    await store.useChatGptPlan('gpt-5.6-luna');
    vaultStoreCalls.length = 0;

    await store.connect('OpenAI', 'sk-test-key');

    expect(vaultStoreCalls).toEqual([
      { key: 'llm.openai.api-key', value: 'sk-test-key' },
      { key: 'llm.cloud-model', value: 'gpt-4o-mini' },
      { key: 'llm.provider', value: 'OpenAI' },
    ]);
    expect(store.activeCloudProvider()).toBe('OpenAI');
  });

  it('a connect whose switch fails keeps the stored key and says it was stored but not switched', async () => {
    const { store, bridge, vaultStoreCalls } = setup({ publicValues: { 'llm.provider': 'None' } });
    await store.loadFromVault();
    vi.mocked(bridge.vaultStore).mockImplementation(async (req) => {
      if (req.key === 'llm.provider') throw new Error('keyring locked');
      vaultStoreCalls.push({ key: req.key, value: req.value });
    });

    await expect(store.connect('OpenAI', 'sk-test-key')).rejects.toBeInstanceOf(
      KeyStoredNotSwitchedError,
    );

    expect(vaultStoreCalls).toContainEqual({ key: 'llm.openai.api-key', value: 'sk-test-key' });
    expect(store.connectedProviders().has('OpenAI')).toBe(true);
    expect(store.provider()).toBe('None');
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
    store.setChatGptConnected(true);
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
      vaultHasByKey: {
        'llm.openai.api-key': false,
        'llm.gemini.api-key': false,
        'llm.anthropic.api-key': false,
        'llm.openrouter.api-key': false,
      },
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

  describe('provider before x provider chosen -> unload asked or not', () => {
    type Before = 'Ollama' | 'unset' | 'OpenAI' | 'None';
    const FREED = async () => ({ unloaded: ['fixture-chat:7b'], refused: [] });
    const OLLAMA_UNREACHABLE = async (): Promise<never> => {
      throw new Error(
        "Error invoking remote method 'scout:llm.unloadOllamaModels': Error: [scout:llm/ollama-unreachable] Could not reach Ollama at http://localhost:11434",
      );
    };

    async function arrange(before: Before, opts: BridgeOptions = {}) {
      const ctx = setup({
        unload: FREED,
        publicValues: before === 'OpenAI' || before === 'None' ? { 'llm.provider': before } : {},
        ...opts,
      });
      if (before === 'Ollama') await ctx.store.useOllama();
      if (before === 'OpenAI' || before === 'None') await ctx.store.loadFromVault();
      expect(ctx.store.provider()).toBe(before === 'unset' ? null : before);
      return ctx;
    }

    async function settled(): Promise<void> {
      await new Promise((resolve) => setTimeout(resolve, 0));
    }

    const rows: ReadonlyArray<{
      readonly name: string;
      readonly before: Before;
      readonly choose: (store: LlmConfigStore) => Promise<void>;
      readonly asked: boolean;
      readonly outcome: string | null;
      readonly provider: string;
      readonly unload?: BridgeOptions['unload'];
    }> = [
      {
        name: 'Ollama -> OpenAI API: asked, and the card can say Ollama freed them',
        before: 'Ollama',
        choose: (store) => store.use('OpenAI', 'gpt-4o-mini'),
        asked: true,
        outcome: 'freed',
        provider: 'OpenAI',
      },
      {
        name: 'unset (Ollama by default) -> OpenAI API: asked',
        before: 'unset',
        choose: (store) => store.use('OpenAI', 'gpt-4o-mini'),
        asked: true,
        outcome: 'freed',
        provider: 'OpenAI',
      },
      {
        name: 'Ollama -> ChatGPT plan: asked',
        before: 'Ollama',
        choose: (store) => store.useChatGptPlan('gpt-5.6-luna'),
        asked: true,
        outcome: 'freed',
        provider: 'ChatGptPlan',
      },
      {
        name: 'Ollama -> a key saved while no cloud provider is in use: asked',
        before: 'Ollama',
        choose: async (store) => {
          await store.connect('Anthropic', 'sk-fixture');
        },
        asked: true,
        outcome: 'freed',
        provider: 'Anthropic',
      },
      {
        name: 'OpenAI API -> Google Gemini: not asked',
        before: 'OpenAI',
        choose: (store) => store.use('Gemini', 'gemini-2.5-flash'),
        asked: false,
        outcome: null,
        provider: 'Gemini',
      },
      {
        name: 'None -> OpenAI API: not asked',
        before: 'None',
        choose: (store) => store.use('OpenAI', 'gpt-4o-mini'),
        asked: false,
        outcome: null,
        provider: 'OpenAI',
      },
      {
        name: 'OpenAI API -> Ollama: not asked',
        before: 'OpenAI',
        choose: (store) => store.useOllama(),
        asked: false,
        outcome: null,
        provider: 'Ollama',
      },
      {
        name: 'unset -> cloud, Ollama unreachable: says nothing',
        before: 'unset',
        choose: (store) => store.use('OpenAI', 'gpt-4o-mini'),
        asked: true,
        outcome: null,
        provider: 'OpenAI',
        unload: OLLAMA_UNREACHABLE,
      },
      {
        name: 'Ollama -> cloud, Ollama unreachable: could not ask',
        before: 'Ollama',
        choose: (store) => store.use('OpenAI', 'gpt-4o-mini'),
        asked: true,
        outcome: 'could-not-ask',
        provider: 'OpenAI',
        unload: OLLAMA_UNREACHABLE,
      },
      {
        name: 'unset -> cloud, one model refused: could not ask',
        before: 'unset',
        choose: (store) => store.use('OpenAI', 'gpt-4o-mini'),
        asked: true,
        outcome: 'could-not-ask',
        provider: 'OpenAI',
        unload: async () => ({
          unloaded: ['nomic-embed-text:latest'],
          refused: ['fixture-chat:7b'],
        }),
      },
    ];

    it.each(rows)(
      'when I stop using Ollama, Scout asks it to free its models so they stop running on the machine: $name',
      async (row) => {
        const { store, bridge } = await arrange(
          row.before,
          row.unload === undefined ? {} : { unload: row.unload },
        );

        await row.choose(store);
        await settled();

        expect({
          asked: vi.mocked(bridge.llmUnloadOllamaModels).mock.calls.length,
          outcome: store.ollamaFreed(),
          provider: store.provider(),
        }).toEqual({ asked: row.asked ? 1 : 0, outcome: row.outcome, provider: row.provider });
      },
    );

    it('a provider save that fails asks Ollama nothing and leaves Ollama in use', async () => {
      const { store, bridge } = await arrange('Ollama', {
        providerSaveError: { value: 'OpenAI', error: new Error('vault locked') },
      });

      await expect(store.use('OpenAI', 'gpt-4o-mini')).rejects.toThrow('vault locked');
      await settled();

      expect({
        asked: vi.mocked(bridge.llmUnloadOllamaModels).mock.calls.length,
        outcome: store.ollamaFreed(),
        provider: store.provider(),
      }).toEqual({ asked: 0, outcome: null, provider: 'Ollama' });
    });

    it('a failed unload never undoes the provider switch, and the card says Scout could not ask', async () => {
      const { store, vaultStoreCalls } = await arrange('Ollama', {
        unload: async () => {
          throw new Error('Could not reach Ollama at http://localhost:11434');
        },
      });

      await store.use('OpenAI', 'gpt-4o-mini');
      await settled();

      expect({
        provider: store.provider(),
        saved: vaultStoreCalls.filter((c) => c.key === 'llm.provider').map((c) => c.value),
        outcome: store.ollamaFreed(),
      }).toEqual({ provider: 'OpenAI', saved: ['Ollama', 'OpenAI'], outcome: 'could-not-ask' });
    });

    it.each([
      [
        'Ollama refused one of the models',
        { unloaded: ['nomic-embed-text:latest'], refused: ['fixture-chat:7b'] },
        'could-not-ask',
      ],
      [
        'Ollama refused every model',
        { unloaded: [], refused: ['fixture-chat:7b'] },
        'could-not-ask',
      ],
      ["none of Scout's models was loaded", { unloaded: [], refused: [] }, 'nothing-loaded'],
    ] as const)(
      'a model Ollama did not unload is never shown as freed: %s',
      async (_case, answer, outcome) => {
        const { store } = await arrange('Ollama', { unload: async () => answer });

        await store.use('OpenAI', 'gpt-4o-mini');
        await settled();

        expect(store.ollamaFreed()).toBe(outcome);
      },
    );

    it('an unload that answers after I chose Ollama again says nothing on the card', async () => {
      let answer: (value: { unloaded: string[]; refused: string[] }) => void = () => undefined;
      const { store } = await arrange('Ollama', {
        unload: () => new Promise((resolve) => (answer = resolve)),
      });
      await store.use('OpenAI', 'gpt-4o-mini');
      await store.useOllama();

      answer({ unloaded: ['fixture-chat:7b'], refused: [] });
      await settled();

      expect({ outcome: store.ollamaFreed(), provider: store.provider() }).toEqual({
        outcome: null,
        provider: 'Ollama',
      });
    });

    it('choosing Ollama again clears what Scout said about freeing it', async () => {
      const { store } = await arrange('Ollama');
      await store.use('OpenAI', 'gpt-4o-mini');
      await settled();
      const afterSwitch = store.ollamaFreed();

      await store.useOllama();

      expect([afterSwitch, store.ollamaFreed()]).toEqual(['freed', null]);
    });
  });
});
