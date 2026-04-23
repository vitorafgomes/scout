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
