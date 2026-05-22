import { describe, expect, test } from 'bun:test';
import {
  DiscordTokenExchangeError,
  ExternalHostNotAllowedError,
  OAuthLoopbackPortInUseError,
  OAuthStateMismatchError,
  OAuthUserCancelledError,
  OllamaUnreachableError,
  ScoutError,
  VaultUnavailableError,
  WorkerBinaryMissingError,
  WorkerSpawnError,
  deserializeError,
  serializeError,
  type SerializedScoutError,
} from '../src/errors.js';

describe('serializeError / deserializeError round trip', () => {
  test('VaultUnavailableError', () => {
    const original = new VaultUnavailableError('no keyring');
    const serialized = serializeError(original);
    expect(serialized).toEqual({
      __scoutError: true,
      code: 'vault/unavailable',
      message: 'no keyring',
    });
    const restored = deserializeError(serialized);
    expect(restored).toBeInstanceOf(VaultUnavailableError);
    expect(restored.code).toBe('vault/unavailable');
    expect(restored.message).toBe('no keyring');
  });

  test('OAuthStateMismatchError', () => {
    const restored = deserializeError(serializeError(new OAuthStateMismatchError()));
    expect(restored).toBeInstanceOf(OAuthStateMismatchError);
    expect(restored.code).toBe('oauth/state-mismatch');
  });

  test('OAuthUserCancelledError', () => {
    const restored = deserializeError(serializeError(new OAuthUserCancelledError()));
    expect(restored).toBeInstanceOf(OAuthUserCancelledError);
    expect(restored.code).toBe('oauth/user-cancelled');
  });

  test('OAuthLoopbackPortInUseError', () => {
    const restored = deserializeError(serializeError(new OAuthLoopbackPortInUseError()));
    expect(restored).toBeInstanceOf(OAuthLoopbackPortInUseError);
    expect(restored.code).toBe('oauth/loopback-port-in-use');
  });

  test('DiscordTokenExchangeError carries reason in extras', () => {
    const original = new DiscordTokenExchangeError('invalid_grant', 'token exchange failed');
    const serialized = serializeError(original);
    expect(serialized).toEqual({
      __scoutError: true,
      code: 'discord/token-exchange-failed',
      message: 'token exchange failed',
      extras: { reason: 'invalid_grant' },
    });
    const restored = deserializeError(serialized);
    expect(restored).toBeInstanceOf(DiscordTokenExchangeError);
    expect((restored as DiscordTokenExchangeError).reason).toBe('invalid_grant');
    expect(restored.message).toBe('token exchange failed');
  });

  test('WorkerBinaryMissingError', () => {
    const restored = deserializeError(serializeError(new WorkerBinaryMissingError()));
    expect(restored).toBeInstanceOf(WorkerBinaryMissingError);
    expect(restored.code).toBe('worker/binary-missing');
  });

  test('WorkerSpawnError', () => {
    const restored = deserializeError(serializeError(new WorkerSpawnError()));
    expect(restored).toBeInstanceOf(WorkerSpawnError);
    expect(restored.code).toBe('worker/spawn-failed');
  });

  test('ExternalHostNotAllowedError carries host in extras', () => {
    const original = new ExternalHostNotAllowedError('evil.example.com');
    const serialized = serializeError(original);
    expect(serialized.code).toBe('app/external-host-not-allowed');
    expect(serialized.extras).toEqual({ host: 'evil.example.com' });
    const restored = deserializeError(serialized);
    expect(restored).toBeInstanceOf(ExternalHostNotAllowedError);
    expect((restored as ExternalHostNotAllowedError).host).toBe('evil.example.com');
  });

  test('OllamaUnreachableError carries endpoint in extras', () => {
    const original = new OllamaUnreachableError('http://localhost:11434');
    const serialized = serializeError(original);
    expect(serialized.code).toBe('llm/ollama-unreachable');
    expect(serialized.message).toBe('Could not reach Ollama at http://localhost:11434');
    expect(serialized.extras).toEqual({ endpoint: 'http://localhost:11434' });
    const restored = deserializeError(serialized);
    expect(restored).toBeInstanceOf(OllamaUnreachableError);
    expect((restored as OllamaUnreachableError).endpoint).toBe('http://localhost:11434');
    expect(restored.code).toBe('llm/ollama-unreachable');
  });

  test('OllamaUnreachableError preserves custom message override', () => {
    const original = new OllamaUnreachableError('http://10.0.0.5:11434', 'connection refused');
    const serialized = serializeError(original);
    expect(serialized.message).toBe('connection refused');
    const restored = deserializeError(serialized);
    expect(restored).toBeInstanceOf(OllamaUnreachableError);
    expect(restored.message).toBe('connection refused');
    expect((restored as OllamaUnreachableError).endpoint).toBe('http://10.0.0.5:11434');
  });

  test('OllamaUnreachableError with missing extras falls back to "unknown" endpoint', () => {
    const payload: SerializedScoutError = {
      __scoutError: true,
      code: 'llm/ollama-unreachable',
      message: 'unknown reach failure',
    };
    const restored = deserializeError(payload);
    expect(restored).toBeInstanceOf(OllamaUnreachableError);
    expect((restored as OllamaUnreachableError).endpoint).toBe('unknown');
  });
});

describe('serializeError with non-ScoutError values', () => {
  test('plain Error becomes code=unknown with Name: message', () => {
    // Stable form: "<Error constructor name>: <message>". Chosen over arbitrary
    // strings so the renderer can display something meaningful without leaking stacks.
    expect(serializeError(new Error('boom'))).toEqual({
      __scoutError: true,
      code: 'unknown',
      message: 'Error: boom',
    });
  });

  test('non-Error primitive becomes code=unknown with String() message', () => {
    expect(serializeError(42)).toEqual({
      __scoutError: true,
      code: 'unknown',
      message: '42',
    });
  });

  test('null becomes code=unknown', () => {
    expect(serializeError(null)).toEqual({
      __scoutError: true,
      code: 'unknown',
      message: 'null',
    });
  });
});

describe('serializeError never leaks stack traces', () => {
  test('ScoutError serialisation has no stack key', () => {
    const serialized = serializeError(new VaultUnavailableError('x'));
    expect(Object.keys(serialized)).not.toContain('stack');
  });

  test('plain Error serialisation has no stack key', () => {
    const serialized = serializeError(new Error('boom'));
    expect(Object.keys(serialized)).not.toContain('stack');
  });

  test('ScoutError with extras has no stack key in extras', () => {
    const serialized = serializeError(new DiscordTokenExchangeError('invalid_grant'));
    expect(serialized.extras && Object.keys(serialized.extras)).not.toContain('stack');
  });
});

describe('deserializeError fallbacks', () => {
  test('unknown code returns a generic ScoutError', () => {
    const payload: SerializedScoutError = {
      __scoutError: true,
      code: 'future/code-not-yet-mapped',
      message: 'hello',
    };
    const restored = deserializeError(payload);
    expect(restored).toBeInstanceOf(ScoutError);
    expect(restored.code).toBe('future/code-not-yet-mapped');
    expect(restored.message).toBe('hello');
  });

  test('DiscordTokenExchangeError with missing extras falls back to unknown reason', () => {
    const payload: SerializedScoutError = {
      __scoutError: true,
      code: 'discord/token-exchange-failed',
      message: 'generic',
    };
    const restored = deserializeError(payload);
    expect(restored).toBeInstanceOf(DiscordTokenExchangeError);
    expect((restored as DiscordTokenExchangeError).reason).toBe('unknown');
  });
});
