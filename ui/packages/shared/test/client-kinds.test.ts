import { describe, expect, test } from 'bun:test';
import {
  CLIENT_KINDS,
  VAULT_KEYS,
  assertNeverClient,
  type ClientKind,
  type VaultKey,
} from '../src/client-kinds.js';
import { LLM_PROVIDER_CATALOG } from '../src/llm-providers.js';

describe('CLIENT_KINDS', () => {
  test('contains discord, slack, teams in stable order', () => {
    expect(CLIENT_KINDS).toEqual(['discord', 'slack', 'teams']);
  });

  test('has exactly three kinds', () => {
    expect(CLIENT_KINDS.length).toBe(3);
  });

  test('is frozen to prevent mutation at runtime', () => {
    expect(Object.isFrozen(CLIENT_KINDS)).toBe(true);
  });
});

describe('assertNeverClient', () => {
  test('throws with a message that contains the offending value', () => {
    expect(() => assertNeverClient('banana' as never)).toThrow(/banana/);
  });

  test('throws an Error', () => {
    expect(() => assertNeverClient('x' as never)).toThrow(Error);
  });
});

describe('VAULT_KEYS', () => {
  test('exposes every provisioning key in stable order', () => {
    expect(VAULT_KEYS).toEqual([
      'discord.bot-token',
      'discord.client-id',
      'discord.guild-id',
      'discord.guild-name',
      'llm.provider',
      'llm.active-id',
      'llm.openai.api-key',
      'llm.openai.model',
      'llm.openai.endpoint',
      'llm.gemini.api-key',
      'llm.gemini.model',
      'llm.openrouter.api-key',
      'llm.openrouter.model',
      'llm.openrouter.embeddings-model',
      'llm.openrouter.embeddings-endpoint',
      'llm.openrouter.embeddings-api-key',
      'llm.azureopenai.api-key',
      'llm.azureopenai.endpoint',
      'llm.azureopenai.chat-deployment',
      'llm.azureopenai.embeddings-deployment',
      'llm.model',
      'llm.endpoint',
      'llm.lm-studio.endpoint',
      'llm.lm-studio.api-key',
      'llm.lm-studio.model',
      'stt.provider',
      'stt.model',
      'settings.timezone',
    ]);
  });

  test('is frozen to prevent mutation at runtime', () => {
    expect(Object.isFrozen(VAULT_KEYS)).toBe(true);
  });

  test('contains every vaultKey referenced by the LLM provider catalog', () => {
    for (const descriptor of LLM_PROVIDER_CATALOG) {
      for (const field of descriptor.fields) {
        expect(VAULT_KEYS).toContain(field.vaultKey);
      }
    }
  });

  test('exhaustive VaultKey switch (compile-time)', () => {
    function describeKey(key: VaultKey): string {
      switch (key) {
        case 'discord.bot-token':
          return 'token';
        case 'discord.client-id':
          return 'client-id';
        case 'discord.guild-id':
          return 'guild-id';
        case 'discord.guild-name':
          return 'guild-name';
        case 'llm.provider':
          return 'llm-provider';
        case 'llm.active-id':
          return 'llm-active-id';
        case 'llm.model':
          return 'llm-model';
        case 'llm.endpoint':
          return 'llm-endpoint';
        case 'llm.openai.api-key':
          return 'llm-openai-api-key';
        case 'llm.openai.model':
          return 'llm-openai-model';
        case 'llm.openai.endpoint':
          return 'llm-openai-endpoint';
        case 'llm.gemini.api-key':
          return 'llm-gemini-api-key';
        case 'llm.gemini.model':
          return 'llm-gemini-model';
        case 'llm.openrouter.api-key':
          return 'llm-openrouter-api-key';
        case 'llm.openrouter.model':
          return 'llm-openrouter-model';
        case 'llm.openrouter.embeddings-model':
          return 'llm-openrouter-embeddings-model';
        case 'llm.openrouter.embeddings-endpoint':
          return 'llm-openrouter-embeddings-endpoint';
        case 'llm.openrouter.embeddings-api-key':
          return 'llm-openrouter-embeddings-api-key';
        case 'llm.azureopenai.api-key':
          return 'llm-azureopenai-api-key';
        case 'llm.azureopenai.endpoint':
          return 'llm-azureopenai-endpoint';
        case 'llm.azureopenai.chat-deployment':
          return 'llm-azureopenai-chat-deployment';
        case 'llm.azureopenai.embeddings-deployment':
          return 'llm-azureopenai-embeddings-deployment';
        case 'llm.lm-studio.endpoint':
          return 'llm-lm-studio-endpoint';
        case 'llm.lm-studio.api-key':
          return 'llm-lm-studio-api-key';
        case 'llm.lm-studio.model':
          return 'llm-lm-studio-model';
        case 'stt.provider':
          return 'stt-provider';
        case 'stt.model':
          return 'stt-model';
        case 'settings.timezone':
          return 'settings-timezone';
        default: {
          const _exhaustive: never = key;
          return _exhaustive;
        }
      }
    }
    expect(describeKey('discord.bot-token')).toBe('token');
    expect(describeKey('discord.guild-id')).toBe('guild-id');
    expect(describeKey('discord.guild-name')).toBe('guild-name');
    expect(describeKey('llm.active-id')).toBe('llm-active-id');
  });
});

describe('exhaustive ClientKind switch (compile-time)', () => {
  // If a new ClientKind is added without updating this switch, tsc must fail on
  // the assertNeverClient call because the parameter will no longer be `never`.
  function describeKind(kind: ClientKind): string {
    switch (kind) {
      case 'discord':
        return 'discord';
      case 'slack':
        return 'slack';
      case 'teams':
        return 'teams';
      default:
        return assertNeverClient(kind);
    }
  }

  test('maps each kind to its label', () => {
    expect(describeKind('discord')).toBe('discord');
    expect(describeKind('slack')).toBe('slack');
    expect(describeKind('teams')).toBe('teams');
  });
});
