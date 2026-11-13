import { describe, expect, test } from 'bun:test';
import {
  CLIENT_KINDS,
  PUBLIC_VAULT_KEYS,
  VAULT_KEYS,
  assertNeverClient,
  type ClientKind,
  type VaultKey,
} from '../src/client-kinds.js';

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
      'llm.model',
      'llm.endpoint',
      'llm.api-key',
      'llm.openai.api-key',
      'llm.gemini.api-key',
      'llm.anthropic.api-key',
      'llm.openrouter.api-key',
      'llm.cloud-model',
      'llm.translation-model',
      'llm.translation-provider',
      'stt.provider',
      'stt.model',
      'guard.typesafe-api-key',
      'sandbox.enabled',
      'speech.voice.provider',
      'speech.voice.local',
    ]);
  });

  test('is frozen to prevent mutation at runtime', () => {
    expect(Object.isFrozen(VAULT_KEYS)).toBe(true);
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
        case 'llm.model':
          return 'llm-model';
        case 'llm.endpoint':
          return 'llm-endpoint';
        case 'llm.api-key':
          return 'llm-api-key';
        case 'llm.openai.api-key':
          return 'llm-openai-api-key';
        case 'llm.gemini.api-key':
          return 'llm-gemini-api-key';
        case 'llm.anthropic.api-key':
          return 'llm-anthropic-api-key';
        case 'llm.openrouter.api-key':
          return 'llm-openrouter-api-key';
        case 'llm.cloud-model':
          return 'llm-cloud-model';
        case 'llm.translation-model':
          return 'llm-translation-model';
        case 'llm.translation-provider':
          return 'llm-translation-provider';
        case 'stt.provider':
          return 'stt-provider';
        case 'stt.model':
          return 'stt-model';
        case 'guard.typesafe-api-key':
          return 'guard-typesafe-api-key';
        case 'sandbox.enabled':
          return 'sandbox-enabled';
        case 'speech.voice.provider':
          return 'speech-voice-provider';
        case 'speech.voice.local':
          return 'speech-voice-local';
        default: {
          const _exhaustive: never = key;
          return _exhaustive;
        }
      }
    }
    expect(describeKey('discord.bot-token')).toBe('token');
    expect(describeKey('discord.guild-id')).toBe('guild-id');
    expect(describeKey('discord.guild-name')).toBe('guild-name');
  });
});

describe('PUBLIC_VAULT_KEYS', () => {
  test('the saved speech voices are keys the renderer may read back, so Models, Speech shows the voice in use', () => {
    expect(PUBLIC_VAULT_KEYS).toContain('speech.voice.provider');
    expect(PUBLIC_VAULT_KEYS).toContain('speech.voice.local');
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
