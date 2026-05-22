import { describe, expect, test } from 'bun:test';
import {
  CLIENT_KINDS,
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
      'stt.provider',
      'stt.model',
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
        case 'stt.provider':
          return 'stt-provider';
        case 'stt.model':
          return 'stt-model';
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
