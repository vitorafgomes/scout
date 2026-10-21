import { describe, expect, test } from 'bun:test';
import {
  LLM_PROVIDER_CATALOG,
  LLM_PROVIDER_IDS,
  findLlmProviderDescriptor,
  findLlmProviderDescriptorByBackendName,
  getLlmProviderDescriptor,
} from '../src/llm-providers.js';
import { VAULT_KEYS } from '../src/client-kinds.js';

// The exact set matched by the switch(provider) in
// src/Scout.Llm/LlmServiceCollectionExtensions.cs. Every descriptor's
// backendName must be one of these — a typo here would crash the worker at
// startup, not surface as a UI bug.
const BACKEND_SWITCH_NAMES = new Set([
  'Ollama',
  'OpenAI',
  'Gemini',
  'OpenRouter',
  'AzureOpenAI',
  'Mistral',
  'HuggingFace',
  'AzureAIInference',
]);

describe('LLM_PROVIDER_CATALOG', () => {
  test('is frozen to prevent mutation at runtime', () => {
    expect(Object.isFrozen(LLM_PROVIDER_CATALOG)).toBe(true);
    for (const descriptor of LLM_PROVIDER_CATALOG) {
      expect(Object.isFrozen(descriptor.fields)).toBe(true);
    }
  });

  test('every descriptor.backendName is one of the backend switch cases', () => {
    for (const descriptor of LLM_PROVIDER_CATALOG) {
      expect(
        BACKEND_SWITCH_NAMES.has(descriptor.backendName),
        `${descriptor.id} has unknown backendName '${descriptor.backendName}'`,
      ).toBe(true);
    }
  });

  test("every descriptor.envSegment equals 'SCOUT__LLM__' + backendName.toUpperCase()", () => {
    for (const descriptor of LLM_PROVIDER_CATALOG) {
      expect(descriptor.envSegment).toBe(`SCOUT__LLM__${descriptor.backendName.toUpperCase()}`);
    }
  });

  test('every field.vaultKey is present in the shared VAULT_KEYS allow-list', () => {
    for (const descriptor of LLM_PROVIDER_CATALOG) {
      for (const field of descriptor.fields) {
        expect(
          VAULT_KEYS.includes(field.vaultKey),
          `${descriptor.id}.${field.key} vaultKey '${field.vaultKey}' missing from VAULT_KEYS`,
        ).toBe(true);
      }
    }
  });

  test('every descriptor id appears exactly once', () => {
    const ids = LLM_PROVIDER_CATALOG.map((d) => d.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  test('LLM_PROVIDER_IDS mirrors the catalog order', () => {
    expect(LLM_PROVIDER_IDS).toEqual(LLM_PROVIDER_CATALOG.map((d) => d.id));
  });

  test('secret fields are never given a plain-text defaultValue that looks like a real credential placeholder', () => {
    // lm-studio's local server accepts any non-empty string as a key, so its
    // documented sentinel default is intentional and not a leaked secret.
    for (const descriptor of LLM_PROVIDER_CATALOG) {
      for (const field of descriptor.fields) {
        if (!field.secret) continue;
        if (descriptor.id === 'lm-studio' && field.key === 'apiKey') continue;
        expect(field.defaultValue).toBeUndefined();
      }
    }
  });

  test('priority providers (openai, gemini, openrouter, azure-openai) all required fields have non-empty labels', () => {
    const priority = ['openai', 'gemini', 'openrouter', 'azure-openai'];
    for (const id of priority) {
      const descriptor = getLlmProviderDescriptor(id as never);
      for (const field of descriptor.fields) {
        expect(field.label.trim().length).toBeGreaterThan(0);
      }
    }
  });
});

describe('getLlmProviderDescriptor', () => {
  test('resolves a known id', () => {
    expect(getLlmProviderDescriptor('openai').backendName).toBe('OpenAI');
  });

  test('throws on an unknown id', () => {
    expect(() => getLlmProviderDescriptor('nope' as never)).toThrow(/unknown provider id/);
  });
});

describe('findLlmProviderDescriptor', () => {
  test('returns null instead of throwing for an unknown raw string', () => {
    expect(findLlmProviderDescriptor('not-a-real-id')).toBeNull();
  });

  test('resolves a known id from an untyped string', () => {
    expect(findLlmProviderDescriptor('azure-openai')?.backendName).toBe('AzureOpenAI');
  });
});

describe('findLlmProviderDescriptorByBackendName', () => {
  test('resolves the first catalog entry with a matching backendName', () => {
    // Both 'openai' and 'lm-studio' share backendName 'OpenAI'; the plain
    // OpenAI descriptor is listed first and wins the legacy lookup.
    expect(findLlmProviderDescriptorByBackendName('OpenAI')?.id).toBe('openai');
  });

  test('returns null for an unmatched backend name', () => {
    expect(findLlmProviderDescriptorByBackendName('LmStudio')).toBeNull();
  });
});
