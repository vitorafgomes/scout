import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from './scout-bridge';
import {
  LlmConfigStore,
  LLM_CONFIG_STORAGE_KEY,
  LLM_PROVIDERS_STORAGE_KEY,
} from './llm-config.store';

interface VaultStoreCall {
  readonly key: string;
  readonly value: string;
}

// Keys that already hold a value in the fake vault by default, used by
// saveProvider's "leave a required secret blank to keep it" edit path and by
// hydrate's distrust checks. Individual tests override this per-key via
// opts.vaultHasOverrides.
function makeBridge(
  opts: { vaultHasResult?: boolean; vaultHasOverrides?: Readonly<Record<string, boolean>> } = {},
) {
  const vaultStoreCalls: VaultStoreCall[] = [];
  const vaultDeleteCalls: string[] = [];
  const bridge = {
    vaultStore: vi.fn(async (req: { key: string; value: string }) => {
      vaultStoreCalls.push({ key: req.key, value: req.value });
    }),
    vaultHas: vi.fn(async (req: { key: string }) => {
      const override = opts.vaultHasOverrides?.[req.key];
      if (override !== undefined) return { exists: override };
      return { exists: opts.vaultHasResult ?? false };
    }),
    vaultDelete: vi.fn(async (req: { key: string }) => {
      vaultDeleteCalls.push(req.key);
      return { deleted: true };
    }),
  } as unknown as ScoutBridgeService;
  return { bridge, vaultStoreCalls, vaultDeleteCalls };
}

function setup(
  opts: { vaultHasResult?: boolean; vaultHasOverrides?: Readonly<Record<string, boolean>> } = {},
) {
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

function dumpLocalStorage(): Record<string, string> {
  const dump: Record<string, string> = {};
  for (let i = 0; i < window.localStorage.length; i++) {
    const k = window.localStorage.key(i);
    if (k === null) continue;
    dump[k] = window.localStorage.getItem(k) ?? '';
  }
  return dump;
}

describe('LlmConfigStore', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
    window.localStorage.removeItem(LLM_PROVIDERS_STORAGE_KEY);
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

  describe('multi-provider layer', () => {
    it('defaults activeProviderId to ollama with an empty configById', () => {
      const { store } = setup();
      expect(store.activeProviderId()).toBe('ollama');
      expect(store.configById()).toEqual({});
      expect(store.isProviderConfigured('openai')).toBe(false);
    });

    it('saveProvider(openai) stores apiKey and model in the vault and mirrors only model to localStorage', async () => {
      const { store, vaultStoreCalls } = setup();
      const SECRET = 'sk-super-secret-value';

      await store.saveProvider('openai', { apiKey: SECRET, model: 'gpt-4o' });

      expect(vaultStoreCalls).toContainEqual({ key: 'llm.openai.api-key', value: SECRET });
      expect(vaultStoreCalls).toContainEqual({ key: 'llm.openai.model', value: 'gpt-4o' });

      const raw = window.localStorage.getItem(LLM_PROVIDERS_STORAGE_KEY);
      expect(raw).not.toBeNull();
      expect(raw).toContain('gpt-4o');
      expect(raw).not.toContain(SECRET);

      // Defense in depth: scan every localStorage entry, not just the one
      // key this store happens to use today.
      expect(JSON.stringify(dumpLocalStorage())).not.toContain(SECRET);
    });

    it('saveProvider(openai) rejects when a required field is missing and no secret is already stored', async () => {
      const { store } = setup({ vaultHasResult: false });
      await expect(store.saveProvider('openai', { apiKey: 'sk-abc' })).rejects.toThrow(/Model/);
      await expect(store.saveProvider('openai', { model: 'gpt-4o' })).rejects.toThrow(/API key/);
    });

    it('saveProvider(azure-openai) rejects unless apiKey, endpoint, chatDeployment, and embeddingsDeployment are all present', async () => {
      const { store } = setup({ vaultHasResult: false });
      await expect(
        store.saveProvider('azure-openai', {
          apiKey: 'az-key',
          endpoint: 'https://res.openai.azure.com',
          chatDeployment: 'gpt-4o',
        }),
      ).rejects.toThrow(/Embeddings deployment/);

      await expect(
        store.saveProvider('azure-openai', {
          apiKey: 'az-key',
          endpoint: 'https://res.openai.azure.com',
          chatDeployment: 'gpt-4o',
          embeddingsDeployment: 'text-embedding-3-large',
        }),
      ).resolves.toBeUndefined();
    });

    it('saveProvider(openrouter) rejects unless the embeddings trio is present alongside apiKey and model', async () => {
      const { store } = setup({ vaultHasResult: false });
      await expect(
        store.saveProvider('openrouter', {
          apiKey: 'or-key',
          model: 'openai/gpt-4o',
          embeddingsModel: 'text-embedding-3-small',
          embeddingsEndpoint: 'https://api.openai.com/v1',
        }),
      ).rejects.toThrow(/Embeddings API key/);

      await expect(
        store.saveProvider('openrouter', {
          apiKey: 'or-key',
          model: 'openai/gpt-4o',
          embeddingsModel: 'text-embedding-3-small',
          embeddingsEndpoint: 'https://api.openai.com/v1',
          embeddingsApiKey: 'emb-key',
        }),
      ).resolves.toBeUndefined();
    });

    it('saveProvider accepts a blank required secret when the vault already holds one (edit-in-place)', async () => {
      const { store, vaultStoreCalls } = setup({
        vaultHasOverrides: { 'llm.openai.api-key': true },
      });

      await store.saveProvider('openai', { model: 'gpt-4o-mini' });

      expect(vaultStoreCalls).toContainEqual({ key: 'llm.openai.model', value: 'gpt-4o-mini' });
      expect(vaultStoreCalls.some((c) => c.key === 'llm.openai.api-key')).toBe(false);
    });

    it('isProviderConfigured is true only once required non-secret fields and the required secret are both present', async () => {
      const { store } = setup({ vaultHasResult: true });
      expect(store.isProviderConfigured('openai')).toBe(false);

      await store.saveProvider('openai', { apiKey: 'sk-abc', model: 'gpt-4o' });
      expect(store.isProviderConfigured('openai')).toBe(true);
    });

    it('setActive writes llm.active-id then llm.provider and updates the signal', async () => {
      const { store, vaultStoreCalls } = setup();
      await store.setActive('gemini');

      expect(vaultStoreCalls).toContainEqual({ key: 'llm.active-id', value: 'gemini' });
      expect(vaultStoreCalls).toContainEqual({ key: 'llm.provider', value: 'Gemini' });
      expect(store.activeProviderId()).toBe('gemini');
    });

    it('configuring a second provider keeps the first provider configured and switches only the active pointer', async () => {
      const { store } = setup({ vaultHasResult: true });

      await store.saveProvider('openai', { apiKey: 'sk-abc', model: 'gpt-4o' });
      await store.setActive('openai');
      await store.saveProvider('gemini', { apiKey: 'g-key', model: 'gemini-2.5-flash' });
      await store.setActive('gemini');

      expect(store.activeProviderId()).toBe('gemini');
      expect(store.isProviderConfigured('openai')).toBe(true);
      expect(store.isProviderConfigured('gemini')).toBe(true);
    });

    it('clearProvider deletes every field key from the vault and drops the provider from configById', async () => {
      const { store, vaultDeleteCalls } = setup({ vaultHasResult: true });
      await store.saveProvider('openai', { apiKey: 'sk-abc', model: 'gpt-4o' });

      await store.clearProvider('openai');

      expect(vaultDeleteCalls.sort()).toEqual(
        ['llm.openai.api-key', 'llm.openai.model', 'llm.openai.endpoint'].sort(),
      );
      expect(store.configById()['openai']).toBeUndefined();
      expect(store.isProviderConfigured('openai')).toBe(false);
    });

    it('clearProvider resets the active provider back to ollama when the cleared provider was active', async () => {
      const { store, vaultStoreCalls } = setup({ vaultHasResult: true });
      await store.saveProvider('openai', { apiKey: 'sk-abc', model: 'gpt-4o' });
      await store.setActive('openai');

      await store.clearProvider('openai');

      expect(store.activeProviderId()).toBe('ollama');
      expect(vaultStoreCalls).toContainEqual({ key: 'llm.active-id', value: 'ollama' });
      expect(vaultStoreCalls).toContainEqual({ key: 'llm.provider', value: 'Ollama' });
    });

    it('clearProvider leaves the active pointer untouched when clearing a non-active provider', async () => {
      const { store, vaultStoreCalls } = setup({ vaultHasResult: true });
      await store.saveProvider('openai', { apiKey: 'sk-abc', model: 'gpt-4o' });
      await store.setActive('openai');
      vaultStoreCalls.length = 0;
      await store.saveProvider('gemini', { apiKey: 'g-key', model: 'gemini-2.5-flash' });

      await store.clearProvider('gemini');

      expect(store.activeProviderId()).toBe('openai');
      expect(vaultStoreCalls.some((c) => c.key === 'llm.active-id')).toBe(false);
    });

    it('loadFromVault hydrates a provider whose required secret is confirmed present', async () => {
      const { store } = setup({ vaultHasResult: true });
      window.localStorage.setItem(
        LLM_PROVIDERS_STORAGE_KEY,
        JSON.stringify({
          activeProviderId: 'openai',
          config: { openai: { model: 'gpt-4o' } },
        }),
      );

      await store.loadFromVault();

      expect(store.activeProviderId()).toBe('openai');
      expect(store.isProviderConfigured('openai')).toBe(true);
    });

    it('loadFromVault distrusts and drops a provider whose required secret is missing from the vault, and warns', async () => {
      const { store } = setup({ vaultHasResult: false });
      const warnSpy = vi.spyOn(console, 'warn');
      window.localStorage.setItem(
        LLM_PROVIDERS_STORAGE_KEY,
        JSON.stringify({
          activeProviderId: 'openai',
          config: { openai: { model: 'gpt-4o' } },
        }),
      );

      await store.loadFromVault();

      expect(store.isProviderConfigured('openai')).toBe(false);
      expect(store.configById()['openai']).toBeUndefined();
      expect(store.activeProviderId()).toBe('ollama');
      expect(warnSpy).toHaveBeenCalled();
    });

    it('loadFromVault is a no-op for the multi-provider layer when its localStorage key is absent', async () => {
      const { store } = setup({ vaultHasResult: true });
      await store.loadFromVault();

      expect(store.activeProviderId()).toBe('ollama');
      expect(store.configById()).toEqual({});
    });

    // Regression coverage for the P1 "LLM not configured" banner: isConfigured
    // must read the live multi-provider layer, not only the legacy _model
    // signal that the routed provider flows never write.
    describe('isConfigured reflects the live multi-provider layer', () => {
      it('is false when nothing is configured', () => {
        const { store } = setup();
        expect(store.isConfigured()).toBe(false);
      });

      it('is true after saveProvider + setActive for a cloud provider', async () => {
        const { store } = setup({ vaultHasResult: true });
        expect(store.isConfigured()).toBe(false);

        await store.saveProvider('openai', { apiKey: 'sk-abc', model: 'gpt-4o' });
        await store.setActive('openai');

        expect(store.isConfigured()).toBe(true);
      });

      it('is true after saveProvider + setActive for Ollama-via-sources (no secret field involved)', async () => {
        const { store } = setup({ vaultHasResult: true });
        expect(store.isConfigured()).toBe(false);

        await store.saveProvider('ollama', {
          model: 'llama3.1',
          endpoint: 'http://localhost:11434',
        });
        await store.setActive('ollama');

        expect(store.isConfigured()).toBe(true);
      });

      it('stays false when a provider is saved but a different, unconfigured provider is active', async () => {
        const { store } = setup({ vaultHasResult: true });

        await store.saveProvider('openai', { apiKey: 'sk-abc', model: 'gpt-4o' });
        // Active pointer stays on the default 'ollama', which has never been
        // configured — the live layer must not report it as configured just
        // because some other provider happens to have data.
        expect(store.activeProviderId()).toBe('ollama');

        expect(store.isConfigured()).toBe(false);
      });
    });
  });
});
