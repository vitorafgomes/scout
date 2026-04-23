import { describe, expect, test } from 'bun:test';
import { CLIENT_KINDS, assertNeverClient, type ClientKind } from '../src/client-kinds.js';

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
