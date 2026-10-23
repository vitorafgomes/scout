import { createPublicKey, randomUUID, verify, type JsonWebKey } from 'node:crypto';
import {
  ChatGptAuthError,
  DiscordTokenExchangeError,
  OAuthLoopbackPortInUseError,
  VaultUnavailableError,
  type ChatGptModel,
  type ChatGptSignOutResponse,
  type ChatGptStatus,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { awaitCallback, findFreePort } from './loopback-server.js';
import { generatePkcePair, generateState } from './state.js';

const ISSUER = 'https://auth.openai.com';
const AUTHORIZE_URL = `${ISSUER}/api/accounts/authorize`;
const TOKEN_URL = `${ISSUER}/api/accounts/oauth/token`;
const DISCOVERY_URL = `${ISSUER}/.well-known/openid-configuration`;
const RESOURCE = 'https://api.openai.com/v1';
const MODELS_URL = `${RESOURCE}/models`;
const SCOPE = 'openid profile email offline_access resource.invoke chatgpt.tokens.use.direct';
const PLAN_SCOPE = 'chatgpt.tokens.use.direct';
const FIRST_REGISTRATION_CLIENT_ID = 'dynamic_agent_client';
const LOOPBACK_PORT = 1455;
const CALLBACK_PATH = '/auth/callback';
const SIGN_IN_TIMEOUT_MS = 300_000;
const HTTP_TIMEOUT_MS = 15_000;
const REFRESH_MARGIN_MS = 5 * 60_000;
const REFRESH_CHECK_MS = 60_000;
const TERMINAL_REFRESH_ERRORS = new Set([
  'invalid_grant',
  'refresh_token_expired',
  'refresh_token_reused',
  'refresh_token_invalidated',
  'invalid_client',
  'unauthorized_client',
]);

interface StoredSession {
  accessToken: string;
  refreshToken: string;
  idToken: string;
  clientId: string;
  expiresAt: number;
  email: string | null;
  planUsageGranted: boolean;
}

interface TokenResponse {
  access_token: string;
  refresh_token?: string;
  id_token?: string;
  expires_in: number;
  scope?: string;
}

export interface TokenFileFs {
  writeFile(path: string, data: string, options: { mode: number }): Promise<void>;
  rename(from: string, to: string): Promise<void>;
  rm(path: string, options: { force: true }): Promise<void>;
}

export interface ChatGptAuthDeps {
  fetch: typeof globalThis.fetch;
  awaitCallback: typeof awaitCallback;
  findFreePort: typeof findFreePort;
  openExternal(url: string): Promise<void>;
  vault: Pick<Vault, 'get' | 'store' | 'delete'>;
  tokenFilePath: string;
  fs: TokenFileFs;
  now(): number;
  logger: Logger;
}

/** Signs the owner in with their ChatGPT plan, keeps the access token fresh in the token file the worker reads, and signs out. */
export class ChatGptAuth {
  private queue: Promise<unknown> = Promise.resolve();
  private signingIn = false;
  private pendingSignIn: AbortController | null = null;
  private tokenFileStale = false;
  private timer: ReturnType<typeof setInterval> | null = null;

  constructor(private readonly deps: ChatGptAuthDeps) {}

  /** Rewrites the token file from the stored session, refreshes when due, then checks again every minute. */
  async start(): Promise<void> {
    this.timer = setInterval(() => {
      this.refreshIfDue().catch((err: unknown) => {
        if (err instanceof VaultUnavailableError) return;
        this.deps.logger.warn('chatgpt: refresh check failed', { error: errorName(err) });
      });
    }, REFRESH_CHECK_MS);
    this.timer.unref();
    await this.serial(async () => {
      const session = await this.loadSession();
      if (session !== null) await this.writeTokenFileOrMarkStale(session);
    });
    await this.refreshIfDue();
  }

  stop(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = null;
  }

  async status(): Promise<ChatGptStatus> {
    return toStatus(await this.loadSession());
  }

  async signIn(): Promise<ChatGptStatus> {
    if (this.signingIn) {
      throw new ChatGptAuthError(
        'already_in_progress',
        'A ChatGPT sign-in is already waiting for the browser.',
      );
    }
    this.signingIn = true;
    try {
      return await this.runSignIn();
    } finally {
      this.signingIn = false;
      this.pendingSignIn = null;
    }
  }

  /** Aborts a sign-in that is waiting for the browser and releases the loopback port. */
  cancelSignIn(): void {
    this.pendingSignIn?.abort();
  }

  async signOut(): Promise<ChatGptSignOutResponse> {
    return this.serial(async () => {
      const session = await this.loadSession();
      let revocationConfirmed = true;
      if (session !== null) {
        revocationConfirmed = await this.revoke(session);
      }
      await this.clearSession();
      this.deps.logger.info('chatgpt: signed out', { revocationConfirmed });
      return { revocationConfirmed };
    });
  }

  /** Lists the models the owner's plan offers, as OpenAI's model list marks them for listing. */
  async listModels(): Promise<ChatGptModel[]> {
    await this.refreshIfDue();
    const session = await this.loadSession();
    if (session === null) {
      throw new ChatGptAuthError('not_signed_in', 'Sign in with ChatGPT to list its models.');
    }
    let response: Response;
    try {
      response = await this.deps.fetch(MODELS_URL, {
        headers: { Authorization: `Bearer ${session.accessToken}` },
        signal: AbortSignal.timeout(HTTP_TIMEOUT_MS),
      });
    } catch (err: unknown) {
      throw new ChatGptAuthError(
        'network_error',
        'Could not reach OpenAI to list ChatGPT models.',
        {
          cause: err,
        },
      );
    }
    const body = await readJson(response);
    if (!response.ok) {
      const code = errorCode(body);
      throw new ChatGptAuthError(code, `OpenAI refused the ChatGPT model list (${code}).`);
    }
    const models = body?.['models'];
    if (!Array.isArray(models)) {
      throw new ChatGptAuthError(
        'unexpected_model_list',
        'OpenAI returned an unexpected model list.',
      );
    }
    return (models as Array<Record<string, unknown>>).flatMap((model) => {
      const { slug, display_name: displayName, visibility } = model;
      if (typeof slug !== 'string' || visibility !== 'list') return [];
      return [{ slug, displayName: typeof displayName === 'string' ? displayName : slug }];
    });
  }

  refreshIfDue(): Promise<void> {
    return this.serial(() => this.refreshNow());
  }

  private async runSignIn(): Promise<ChatGptStatus> {
    const { deps } = this;
    const hostId = await this.hostId();
    const savedClientId = await deps.vault.get('chatgpt.client-id');
    const { verifier, challenge } = generatePkcePair();
    const state = generateState();
    const nonce = generateState();

    let port: number;
    try {
      port = await deps.findFreePort(LOOPBACK_PORT, 0);
    } catch (err: unknown) {
      if (err instanceof OAuthLoopbackPortInUseError) {
        throw new OAuthLoopbackPortInUseError(
          `Port ${LOOPBACK_PORT} is in use by another program; close it and sign in again.`,
        );
      }
      throw err;
    }
    const redirectUri = `http://127.0.0.1:${port}${CALLBACK_PATH}`;

    const query = new URLSearchParams({
      response_type: 'code',
      client_id: savedClientId ?? FIRST_REGISTRATION_CLIENT_ID,
      redirect_uri: redirectUri,
      scope: SCOPE,
      resource: RESOURCE,
      code_challenge: challenge,
      code_challenge_method: 'S256',
      state,
      nonce,
      ext_agent_host_id: hostId,
    });
    if (savedClientId === null) query.set('agent_name_hint', 'Scout');

    const listener = new AbortController();
    this.pendingSignIn = listener;
    const callback = deps.awaitCallback({
      expectedState: state,
      port,
      hostname: '127.0.0.1',
      timeoutMs: SIGN_IN_TIMEOUT_MS,
      logger: deps.logger,
      callbackPath: CALLBACK_PATH,
      signal: listener.signal,
    });
    callback.catch(() => {});

    deps.logger.info('chatgpt: opening the sign-in page', { port });
    try {
      await deps.openExternal(`${AUTHORIZE_URL}?${query.toString()}`);
    } catch (err: unknown) {
      listener.abort();
      throw err;
    }

    let result;
    try {
      result = await callback;
    } catch (err: unknown) {
      if (err instanceof DiscordTokenExchangeError) {
        throw new ChatGptAuthError(err.reason, `ChatGPT sign-in failed: ${err.reason}`);
      }
      throw err;
    }

    const clientId = result.clientId ?? savedClientId;
    if (clientId === null) {
      throw new ChatGptAuthError(
        'missing_client_id',
        'OpenAI did not register Scout during sign-in; sign in again.',
      );
    }

    const tokens = await this.postToken({
      grant_type: 'authorization_code',
      client_id: clientId,
      code: result.code,
      code_verifier: verifier,
      redirect_uri: redirectUri,
      resource: RESOURCE,
    });
    if (tokens.refresh_token === undefined || tokens.id_token === undefined) {
      throw new ChatGptAuthError(
        'incomplete_token_response',
        'OpenAI did not return a refresh token and an ID token.',
      );
    }
    const claims = await this.validateIdToken(tokens.id_token, clientId, nonce);

    const session: StoredSession = {
      accessToken: tokens.access_token,
      refreshToken: tokens.refresh_token,
      idToken: tokens.id_token,
      clientId,
      expiresAt: deps.now() + tokens.expires_in * 1000,
      email: claims.email,
      planUsageGranted: hasPlanScope(tokens.scope),
    };

    await this.serial(async () => {
      await deps.vault.store('chatgpt.client-id', clientId);
      await this.saveSession(session);
    });
    deps.logger.info('chatgpt: signed in', { planUsageGranted: session.planUsageGranted });
    return toStatus(session);
  }

  private async refreshNow(): Promise<void> {
    const { deps } = this;
    const session = await this.loadSession();
    if (session === null) return;
    if (session.expiresAt - deps.now() > REFRESH_MARGIN_MS) {
      if (this.tokenFileStale) await this.writeTokenFileOrMarkStale(session);
      return;
    }

    let response: Response;
    try {
      response = await this.post(TOKEN_URL, {
        grant_type: 'refresh_token',
        refresh_token: session.refreshToken,
        resource: RESOURCE,
        client_id: session.clientId,
      });
    } catch (err: unknown) {
      deps.logger.warn('chatgpt: token refresh did not reach OpenAI; will retry', {
        error: errorName(err),
      });
      return;
    }

    const body = await readJson(response);
    if (!response.ok) {
      const code = errorCode(body);
      if (TERMINAL_REFRESH_ERRORS.has(code)) {
        await this.clearSession();
        deps.logger.warn('chatgpt: refresh rejected; the owner must sign in again', { code });
      } else {
        deps.logger.warn('chatgpt: token refresh failed; will retry', {
          status: response.status,
          code,
        });
      }
      return;
    }

    const tokens = parseTokenResponse(body);
    if (tokens === null) {
      deps.logger.warn('chatgpt: token refresh returned an unexpected body; will retry');
      return;
    }
    await this.saveSession({
      ...session,
      accessToken: tokens.access_token,
      refreshToken: tokens.refresh_token ?? session.refreshToken,
      idToken: tokens.id_token ?? session.idToken,
      expiresAt: deps.now() + tokens.expires_in * 1000,
      planUsageGranted:
        tokens.scope === undefined ? session.planUsageGranted : hasPlanScope(tokens.scope),
    });
    deps.logger.info('chatgpt: access token refreshed');
  }

  private async revoke(session: StoredSession): Promise<boolean> {
    try {
      const discovery = await this.getJson(DISCOVERY_URL);
      const endpoint = discovery['revocation_endpoint'];
      if (typeof endpoint !== 'string') return false;
      const response = await this.post(endpoint, {
        token: session.refreshToken,
        token_type_hint: 'refresh_token',
        client_id: session.clientId,
      });
      if (!response.ok) {
        this.deps.logger.warn('chatgpt: revocation was refused', { status: response.status });
      }
      return response.ok;
    } catch (err: unknown) {
      this.deps.logger.warn('chatgpt: revocation did not reach OpenAI', { error: errorName(err) });
      return false;
    }
  }

  private async validateIdToken(
    idToken: string,
    clientId: string,
    nonce: string,
  ): Promise<{ email: string | null }> {
    const parts = idToken.split('.');
    const [encodedHeader, encodedClaims, encodedSignature] = parts;
    if (
      parts.length !== 3 ||
      encodedHeader === undefined ||
      encodedClaims === undefined ||
      encodedSignature === undefined
    ) {
      throw invalidIdToken('it is not a signed JWT');
    }
    const header = decodeSegment(encodedHeader);
    const claims = decodeSegment(encodedClaims);
    if (header['alg'] !== 'RS256') throw invalidIdToken('its signing algorithm is not RS256');

    let keys: unknown;
    try {
      const discovery = await this.getJson(DISCOVERY_URL);
      const jwksUri = discovery['jwks_uri'];
      if (typeof jwksUri !== 'string') throw new Error('no jwks_uri');
      keys = (await this.getJson(jwksUri))['keys'];
    } catch (err: unknown) {
      throw new ChatGptAuthError(
        'jwks_unavailable',
        "Could not fetch OpenAI's signing keys to check the sign-in; try again.",
        { cause: err },
      );
    }
    const jwk = Array.isArray(keys)
      ? (keys as JsonWebKey[]).find((key) => key.kid === header['kid'])
      : undefined;
    if (jwk === undefined) throw invalidIdToken('its signing key is not published by OpenAI');

    const signedOk = verify(
      'sha256',
      Buffer.from(`${encodedHeader}.${encodedClaims}`),
      createPublicKey({ key: jwk, format: 'jwk' }),
      Buffer.from(encodedSignature, 'base64url'),
    );
    if (!signedOk) throw invalidIdToken('its signature does not match');
    if (claims['iss'] !== ISSUER) throw invalidIdToken('it was not issued by OpenAI');
    const aud = claims['aud'];
    if (aud !== clientId && !(Array.isArray(aud) && aud.includes(clientId))) {
      throw invalidIdToken('it was issued for another client');
    }
    const exp = claims['exp'];
    if (typeof exp !== 'number' || exp * 1000 <= this.deps.now()) {
      throw invalidIdToken('it has expired');
    }
    if (claims['nonce'] !== nonce) throw invalidIdToken('it belongs to another sign-in attempt');
    return { email: typeof claims['email'] === 'string' ? claims['email'] : null };
  }

  private async postToken(fields: Record<string, string>): Promise<TokenResponse> {
    let response: Response;
    try {
      response = await this.post(TOKEN_URL, fields);
    } catch (err: unknown) {
      throw new ChatGptAuthError('network_error', 'Could not reach OpenAI to finish sign-in.', {
        cause: err,
      });
    }
    const body = await readJson(response);
    if (!response.ok) {
      const code = errorCode(body);
      throw new ChatGptAuthError(code, `OpenAI refused the sign-in (${code}).`);
    }
    const tokens = parseTokenResponse(body);
    if (tokens === null) {
      throw new ChatGptAuthError(
        'incomplete_token_response',
        'OpenAI returned an unexpected sign-in response.',
      );
    }
    return tokens;
  }

  private post(url: string, fields: Record<string, string>): Promise<Response> {
    return this.deps.fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams(fields).toString(),
      signal: AbortSignal.timeout(HTTP_TIMEOUT_MS),
    });
  }

  private async getJson(url: string): Promise<Record<string, unknown>> {
    const response = await this.deps.fetch(url, { signal: AbortSignal.timeout(HTTP_TIMEOUT_MS) });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const body = await readJson(response);
    if (body === null) throw new Error('not a JSON object');
    return body;
  }

  private async hostId(): Promise<string> {
    const existing = await this.deps.vault.get('chatgpt.host-id');
    if (existing !== null) return existing;
    const created = `urn:uuid:${randomUUID()}`;
    await this.deps.vault.store('chatgpt.host-id', created);
    return created;
  }

  private async loadSession(): Promise<StoredSession | null> {
    const raw = await this.deps.vault.get('chatgpt.session');
    return raw === null ? null : (JSON.parse(raw) as StoredSession);
  }

  private async saveSession(session: StoredSession): Promise<void> {
    await this.deps.vault.store('chatgpt.session', JSON.stringify(session));
    await this.writeTokenFileOrMarkStale(session);
  }

  private async writeTokenFileOrMarkStale(session: StoredSession): Promise<void> {
    try {
      await this.writeTokenFile(session);
      this.tokenFileStale = false;
    } catch (err: unknown) {
      this.tokenFileStale = true;
      this.deps.logger.warn('chatgpt: could not write the token file; will retry', {
        error: errorName(err),
      });
    }
  }

  private async clearSession(): Promise<void> {
    await this.deps.vault.delete('chatgpt.session');
    await this.deps.fs.rm(this.deps.tokenFilePath, { force: true });
  }

  private async writeTokenFile(session: StoredSession): Promise<void> {
    const tmp = `${this.deps.tokenFilePath}.tmp`;
    const content = JSON.stringify({
      access_token: session.accessToken,
      expires_at: new Date(session.expiresAt).toISOString(),
    });
    await this.deps.fs.writeFile(tmp, content, { mode: 0o600 });
    await this.deps.fs.rename(tmp, this.deps.tokenFilePath);
  }

  private serial<T>(task: () => Promise<T>): Promise<T> {
    const next = this.queue.then(task, task);
    this.queue = next.catch(() => {});
    return next;
  }
}

function toStatus(session: StoredSession | null): ChatGptStatus {
  return {
    signedIn: session !== null,
    email: session?.email ?? null,
    planUsageGranted: session?.planUsageGranted ?? false,
  };
}

function hasPlanScope(scope: string | undefined): boolean {
  return scope !== undefined && scope.split(' ').includes(PLAN_SCOPE);
}

function parseTokenResponse(body: Record<string, unknown> | null): TokenResponse | null {
  if (body === null) return null;
  const { access_token, refresh_token, id_token, expires_in, scope } = body;
  if (typeof access_token !== 'string' || typeof expires_in !== 'number') return null;
  return {
    access_token,
    expires_in,
    ...(typeof refresh_token === 'string' ? { refresh_token } : {}),
    ...(typeof id_token === 'string' ? { id_token } : {}),
    ...(typeof scope === 'string' ? { scope } : {}),
  };
}

async function readJson(response: Response): Promise<Record<string, unknown> | null> {
  try {
    const body: unknown = await response.json();
    return typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function errorCode(body: Record<string, unknown> | null): string {
  const error = body?.['error'];
  if (typeof error === 'string') return error;
  if (typeof error === 'object' && error !== null) {
    const code = (error as Record<string, unknown>)['code'];
    if (typeof code === 'string') return code;
  }
  return 'unknown_error';
}

function errorName(err: unknown): string {
  return err instanceof Error ? err.name : 'unknown';
}

function decodeSegment(segment: string): Record<string, unknown> {
  let value: unknown = null;
  try {
    value = JSON.parse(Buffer.from(segment, 'base64url').toString('utf8'));
  } catch {
    value = null;
  }
  if (typeof value !== 'object' || value === null) throw invalidIdToken('it cannot be decoded');
  return value as Record<string, unknown>;
}

function invalidIdToken(why: string): ChatGptAuthError {
  return new ChatGptAuthError('invalid_id_token', `Rejected the ChatGPT sign-in: ${why}.`);
}
