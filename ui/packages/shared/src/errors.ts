export class ScoutError extends Error {
  readonly code: string;

  constructor(code: string, message: string, options?: ErrorOptions) {
    super(message, options);
    this.code = code;
    this.name = new.target.name;
  }
}

/** What the owner reads when Linux selected no keyring service for the vault. */
export const INSECURE_STORE_MESSAGE =
  'Scout found no keyring service such as GNOME Keyring or KWallet on this computer, so it saved nothing and does not use the credentials saved before; start or install one, then restart Scout.';

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

export class ChatGptAuthError extends ScoutError {
  readonly reason: string;

  constructor(reason: string, message: string, options?: ErrorOptions) {
    super('chatgpt/auth-failed', message, options);
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

export class DesktopAutostartError extends ScoutError {
  constructor(
    message = 'Could not change whether Scout starts when you log in.',
    options?: ErrorOptions,
  ) {
    super('desktop/autostart-failed', message, options);
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

export class ControlPlaneUnavailableError extends ScoutError {
  constructor(message = 'Control plane not reachable', options?: ErrorOptions) {
    super('control-plane/unavailable', message, options);
  }
}

export class ControlPlaneUnauthenticatedError extends ScoutError {
  constructor(message = 'Control plane rejected the session token', options?: ErrorOptions) {
    super('control-plane/unauthenticated', message, options);
  }
}

export class ControlPlaneNotImplementedError extends ScoutError {
  constructor(message = 'Turning this feature on is not available yet.', options?: ErrorOptions) {
    super('control-plane/not-implemented', message, options);
  }
}

/** The control plane refused to turn on a write-capable feature, or to grant its writes, because the owner did not confirm them. */
export class ControlPlaneWriteConfirmationError extends ScoutError {
  constructor(message: string, options?: ErrorOptions) {
    super('control-plane/write-confirmation-required', message, options);
  }
}

/** The control plane refused a request as invalid; the message is the control plane's own reason. */
export class ControlPlaneInvalidArgumentError extends ScoutError {
  constructor(message: string, options?: ErrorOptions) {
    super('control-plane/invalid-argument', message, options);
  }
}

/** The control plane refused the request because the owner has not granted it; the message is the write gate's reason. */
export class ControlPlanePermissionDeniedError extends ScoutError {
  constructor(message: string, options?: ErrorOptions) {
    super('control-plane/permission-denied', message, options);
  }
}

/** The owner's decision on an action came after another one: the control plane keeps the first and changes nothing. */
export class ControlPlaneAlreadyDecidedError extends ScoutError {
  constructor(message = 'Already decided.', options?: ErrorOptions) {
    super('control-plane/already-decided', message, options);
  }
}

/**
 * The control plane refused an approval and rejected the action instead, because it was asked before the feature's current
 * write grant; the message is the reason the write log keeps.
 */
export class ControlPlaneActionRejectedError extends ScoutError {
  constructor(message = 'asked before writes were granted; ask again', options?: ErrorOptions) {
    super('control-plane/action-rejected', message, options);
  }
}

/** The control plane has no record with the id the request named. */
export class ControlPlaneNotFoundError extends ScoutError {
  constructor(message: string, options?: ErrorOptions) {
    super('control-plane/not-found', message, options);
  }
}

export interface SerializedScoutError {
  readonly __scoutError: true;
  readonly code: string;
  readonly message: string;
  readonly extras?: Readonly<Record<string, unknown>>;
}

const CONTROL_PLANE_DID_NOT_ANSWER = 'The Scout control plane did not answer.';

/**
 * The error as it crosses the IPC boundary; a control-plane outage carries a fixed sentence, never the spawn error,
 * socket path or call details it was raised with.
 */
export function serializeError(err: unknown): SerializedScoutError {
  if (err instanceof ScoutError && err.code === 'control-plane/unavailable') {
    return { __scoutError: true, code: err.code, message: CONTROL_PLANE_DID_NOT_ANSWER };
  }
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
    default:
      return new ScoutError(payload.code, payload.message);
  }
}

const IPC_CODE_MARKER = /\[scout:([a-z0-9-]+\/[a-z0-9-]+)\] ([\s\S]*)$/;

/**
 * Builds the message an IPC handler throws so the error code survives Electron's invoke
 * boundary, which forwards only the message text to the renderer.
 */
export function encodeIpcErrorMessage(serialized: SerializedScoutError): string {
  return `[scout:${serialized.code}] ${serialized.message}`;
}

/**
 * Recovers the error code and the original message from an IPC rejection: an own string
 * `code` wins, otherwise the code is read from the marker that encodeIpcErrorMessage wrote.
 */
export function readIpcError(err: unknown): { code: string | null; message: string } {
  const message = err instanceof Error ? err.message : String(err);
  const marked = IPC_CODE_MARKER.exec(message);
  const ownCode =
    typeof err === 'object' && err !== null && typeof (err as { code?: unknown }).code === 'string'
      ? (err as { code: string }).code
      : null;
  if (marked === null) return { code: ownCode, message };
  return { code: ownCode ?? marked[1] ?? null, message: marked[2] ?? message };
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
