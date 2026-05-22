export class ScoutError extends Error {
  readonly code: string;

  constructor(code: string, message: string, options?: ErrorOptions) {
    super(message, options);
    this.code = code;
    this.name = new.target.name;
  }
}

export class VaultUnavailableError extends ScoutError {
  constructor(message = 'Secret vault unavailable on this system', options?: ErrorOptions) {
    super('vault/unavailable', message, options);
  }
}

export class OAuthStateMismatchError extends ScoutError {
  constructor(message = 'OAuth state did not match expected value', options?: ErrorOptions) {
    super('oauth/state-mismatch', message, options);
  }
}

export class OAuthUserCancelledError extends ScoutError {
  constructor(message = 'OAuth flow cancelled by user', options?: ErrorOptions) {
    super('oauth/user-cancelled', message, options);
  }
}

export class OAuthLoopbackPortInUseError extends ScoutError {
  constructor(message = 'OAuth loopback port is already in use', options?: ErrorOptions) {
    super('oauth/loopback-port-in-use', message, options);
  }
}

export class DiscordTokenExchangeError extends ScoutError {
  readonly reason: string;

  constructor(reason: string, message?: string, options?: ErrorOptions) {
    super(
      'discord/token-exchange-failed',
      message ?? `Discord token exchange failed: ${reason}`,
      options,
    );
    this.reason = reason;
  }
}

export class WorkerBinaryMissingError extends ScoutError {
  constructor(message = 'Worker binary not found on disk', options?: ErrorOptions) {
    super('worker/binary-missing', message, options);
  }
}

export class WorkerSpawnError extends ScoutError {
  constructor(message = 'Worker process failed to spawn', options?: ErrorOptions) {
    super('worker/spawn-failed', message, options);
  }
}

export class ExternalHostNotAllowedError extends ScoutError {
  readonly host: string;

  constructor(host: string, message?: string, options?: ErrorOptions) {
    super(
      'app/external-host-not-allowed',
      message ?? `External host not in allow-list: ${host}`,
      options,
    );
    this.host = host;
  }
}

export class OllamaUnreachableError extends ScoutError {
  readonly endpoint: string;

  constructor(endpoint: string, message?: string, options?: ErrorOptions) {
    super('llm/ollama-unreachable', message ?? `Could not reach Ollama at ${endpoint}`, options);
    this.endpoint = endpoint;
  }
}

export class WorkerHttpUnavailableError extends ScoutError {
  constructor(message = 'Worker HTTP endpoint not available', options?: ErrorOptions) {
    super('worker/http-unavailable', message, options);
  }
}

export class ProviderUnreachableError extends ScoutError {
  constructor(message = 'Provider endpoint unreachable', options?: ErrorOptions) {
    super('providers/unreachable', message, options);
  }
}

export class ProviderUnknownError extends ScoutError {
  constructor(message = 'Unknown provider id', options?: ErrorOptions) {
    super('providers/unknown', message, options);
  }
}

export class ProviderLifecycleUnsupportedError extends ScoutError {
  constructor(message = 'Provider does not support lifecycle management', options?: ErrorOptions) {
    super('providers/lifecycle-unsupported', message, options);
  }
}

export class ProviderNotManagedError extends ScoutError {
  constructor(message = 'Provider is not managed by worker', options?: ErrorOptions) {
    super('providers/not-managed', message, options);
  }
}

export class ProviderStartTimeoutError extends ScoutError {
  constructor(message = 'Provider failed to start within timeout', options?: ErrorOptions) {
    super('providers/start-timeout', message, options);
  }
}

export class ProviderRequestFailedError extends ScoutError {
  constructor(message = 'Provider request failed', options?: ErrorOptions) {
    super('providers/request-failed', message, options);
  }
}

export interface SerializedScoutError {
  readonly __scoutError: true;
  readonly code: string;
  readonly message: string;
  readonly extras?: Readonly<Record<string, unknown>>;
}

export function serializeError(err: unknown): SerializedScoutError {
  if (err instanceof ScoutError) {
    const extras = extractExtras(err);
    if (extras !== undefined) {
      return { __scoutError: true, code: err.code, message: err.message, extras };
    }
    return { __scoutError: true, code: err.code, message: err.message };
  }
  if (err instanceof Error) {
    return { __scoutError: true, code: 'unknown', message: `${err.name}: ${err.message}` };
  }
  return { __scoutError: true, code: 'unknown', message: String(err) };
}

export function deserializeError(payload: SerializedScoutError): ScoutError {
  switch (payload.code) {
    case 'vault/unavailable':
      return new VaultUnavailableError(payload.message);
    case 'oauth/state-mismatch':
      return new OAuthStateMismatchError(payload.message);
    case 'oauth/user-cancelled':
      return new OAuthUserCancelledError(payload.message);
    case 'oauth/loopback-port-in-use':
      return new OAuthLoopbackPortInUseError(payload.message);
    case 'discord/token-exchange-failed': {
      const reason = readExtraString(payload.extras, 'reason') ?? 'unknown';
      return new DiscordTokenExchangeError(reason, payload.message);
    }
    case 'worker/binary-missing':
      return new WorkerBinaryMissingError(payload.message);
    case 'worker/spawn-failed':
      return new WorkerSpawnError(payload.message);
    case 'app/external-host-not-allowed': {
      const host = readExtraString(payload.extras, 'host') ?? 'unknown';
      return new ExternalHostNotAllowedError(host, payload.message);
    }
    case 'llm/ollama-unreachable': {
      const endpoint = readExtraString(payload.extras, 'endpoint') ?? 'unknown';
      return new OllamaUnreachableError(endpoint, payload.message);
    }
    case 'worker/http-unavailable':
      return new WorkerHttpUnavailableError(payload.message);
    case 'providers/unreachable':
      return new ProviderUnreachableError(payload.message);
    case 'providers/unknown':
      return new ProviderUnknownError(payload.message);
    case 'providers/lifecycle-unsupported':
      return new ProviderLifecycleUnsupportedError(payload.message);
    case 'providers/not-managed':
      return new ProviderNotManagedError(payload.message);
    case 'providers/start-timeout':
      return new ProviderStartTimeoutError(payload.message);
    case 'providers/request-failed':
      return new ProviderRequestFailedError(payload.message);
    default:
      return new ScoutError(payload.code, payload.message);
  }
}

function extractExtras(err: ScoutError): Readonly<Record<string, unknown>> | undefined {
  if (err instanceof DiscordTokenExchangeError) {
    return { reason: err.reason };
  }
  if (err instanceof ExternalHostNotAllowedError) {
    return { host: err.host };
  }
  if (err instanceof OllamaUnreachableError) {
    return { endpoint: err.endpoint };
  }
  return undefined;
}

function readExtraString(
  extras: Readonly<Record<string, unknown>> | undefined,
  key: string,
): string | undefined {
  if (extras === undefined) return undefined;
  const value = extras[key];
  return typeof value === 'string' ? value : undefined;
}
