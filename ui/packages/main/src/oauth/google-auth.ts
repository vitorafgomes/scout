import {
  DiscordTokenExchangeError,
  OAuthLoopbackPortInUseError,
  OAuthStateMismatchError,
  OAuthUserCancelledError,
  ScoutError,
  type YouTubeStatus,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { TokenFileFs } from './github-auth.js';
import type { LoopbackResult, awaitCallback, findFreePort } from './loopback-server.js';
import { generatePkcePair, generateState } from './state.js';

/** Scout's own Google OAuth client of type Desktop app; empty until the owner registers it and fills both in. */
export const DEFAULT_GOOGLE_CLIENT_ID =
  '945517212171-k65ivo7d353hq783cp5shc5q6t1bjt2j.apps.googleusercontent.com';
/** Secret of that Desktop client, which Google documents as not confidential for an installed app; empty until registered. */
export const DEFAULT_GOOGLE_CLIENT_SECRET = '';

const AUTHORIZE_URL = 'https://accounts.google.com/o/oauth2/v2/auth';
const TOKEN_URL = 'https://oauth2.googleapis.com/token';
const REVOKE_URL = 'https://oauth2.googleapis.com/revoke';
const CHANNELS_URL = 'https://www.googleapis.com/youtube/v3/channels';
const SCOPE_PREFIX = 'https://www.googleapis.com/auth/';
const READ_SCOPES = [
  `${SCOPE_PREFIX}youtube.readonly`,
  `${SCOPE_PREFIX}yt-analytics.readonly`,
] as const;
const WRITE_SCOPES = [`${SCOPE_PREFIX}youtube.upload`, `${SCOPE_PREFIX}youtube.force-ssl`] as const;
const LOOPBACK_PORT = 8765;
const LOOPBACK_FALLBACKS = 10;
const CALLBACK_PATH = '/auth/callback';
const SIGN_IN_TIMEOUT_MS = 300_000;
const HTTP_TIMEOUT_MS = 15_000;
const REFRESH_MARGIN_MS = 5 * 60_000;
const REFRESH_CHECK_MS = 60_000;
const SESSION_KEY = 'youtube.session';
const API_KEY_KEY = 'youtube.api-key';
const NO_CHANNEL = 'No channel on this account';
const WEB_CLIENT = 'web-client';
const WEB_CLIENT_MESSAGE =
  'The Google OAuth client in the file is a Web application; Sign in with Google needs an OAuth client of type Desktop app.';

interface StoredSession {
  accessToken: string;
  refreshToken: string | null;
  expiresAt: number;
  channelId: string | null;
  channelTitle: string;
  scopes?: string[];
}

interface ClientFields {
  clientId: string;
  clientSecret: string;
}

interface ClientConfig extends ClientFields {
  source: 'env' | 'file' | 'bundled' | 'embedded';
}

interface TokenResponse {
  accessToken: string;
  refreshToken: string | null;
  expiresIn: number;
  scopes: string[] | null;
}

interface ChannelIdentity {
  channelId: string | null;
  channelTitle: string;
}

export interface GoogleAuthFs extends TokenFileFs {
  readFile(path: string): Promise<string>;
}

export interface GoogleAuthDeps {
  fetch: typeof globalThis.fetch;
  awaitCallback: typeof awaitCallback;
  findFreePort: typeof findFreePort;
  openExternal(url: string): Promise<void>;
  vault: Pick<Vault, 'get' | 'store' | 'delete'>;
  tokenFilePath: string;
  clientConfigPath: string;
  env: Readonly<Record<string, string | undefined>>;
  /** The client file packaged with the app at build time; read once, after the env and the user's file. */
  bundledClientPath?: string;
  /** The client built into Scout, used when no other source provides one. */
  defaultClient?: ClientFields;
  fs: GoogleAuthFs;
  now(): number;
  onConnectionChanged(): void;
  /** Revokes the YouTube write grant when the credential changes or is lost; awaited before the connection change, never throws. */
  revokeWriteGrant?(): Promise<void>;
  /** Subscribes to the system waking from sleep; returns the unsubscribe function. */
  onResume(listener: () => void): () => void;
  logger: Logger;
}

/**
 * Connects the owner's YouTube channel either through Google's browser sign-in, keeping the
 * access token fresh in the token file the worker reads, or with a Data API key the owner pastes;
 * exactly one of the two is stored at a time. The OAuth client secret, the authorization code,
 * the tokens and the key never leave this class except as the worker's environment and the token
 * file: not in logs, not in the status the renderer sees.
 */
export class GoogleAuth {
  private queue: Promise<unknown> = Promise.resolve();
  private pending: AbortController | null = null;
  private flow: Promise<void> = Promise.resolve();
  private lastError: ScoutError | null = null;
  private tokenFileStale = false;
  private timer: ReturnType<typeof setInterval> | null = null;
  private unsubscribeResume: (() => void) | null = null;
  private bundled: Promise<ClientFields | typeof WEB_CLIENT | null> | null = null;

  constructor(private readonly deps: GoogleAuthDeps) {}

  /** Rewrites the token file from the stored session, refreshes when due, then checks every minute and on wake. */
  async start(): Promise<void> {
    const check = (): Promise<void> =>
      this.refreshIfDue().catch((err: unknown) => {
        this.deps.logger.warn('youtube: refresh check failed', { error: errorName(err) });
      });
    this.timer = setInterval(check, REFRESH_CHECK_MS);
    this.timer.unref();
    this.unsubscribeResume = this.deps.onResume(check);
    await this.serial(async () => {
      const session = await this.loadSession();
      if (session !== null) await this.writeTokenFileOrMarkStale(session);
    });
    await this.refreshIfDue();
  }

  stop(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = null;
    this.unsubscribeResume?.();
    this.unsubscribeResume = null;
    this.cancelSignIn();
  }

  /** The variables that give the worker the active YouTube credential, or none when disconnected. */
  async workerEnv(): Promise<Record<string, string>> {
    if ((await this.loadSession()) !== null) {
      return { SCOUT__PLUGINS__YOUTUBE__TOKENFILE: this.deps.tokenFilePath };
    }
    const apiKey = await this.deps.vault.get(API_KEY_KEY);
    return apiKey === null ? {} : { SCOUT__PLUGINS__YOUTUBE__APIKEY: apiKey };
  }

  async status(): Promise<YouTubeStatus> {
    return this.toStatus(await this.loadSession(), await this.deps.vault.get(API_KEY_KEY));
  }

  /** Opens Google's consent page once the loopback listener is up; the answer completes in the background. */
  async signIn(): Promise<YouTubeStatus> {
    await this.startSignIn(READ_SCOPES);
    return this.status();
  }

  async ensureWriteScopes(): Promise<void> {
    if (missingWriteScopes(await this.loadSession()).length === 0) return;
    const abort = await this.startSignIn([...READ_SCOPES, ...WRITE_SCOPES]);
    await this.flow;
    if (this.lastError !== null) throw this.lastError;
    if (abort.signal.aborted) {
      throw new ScoutError(
        'youtube/sign-in-cancelled',
        'The Google sign-in was cancelled, so writes stay off.',
      );
    }
    const missing = missingWriteScopes(await this.loadSession());
    if (missing.length > 0) {
      await this.dropWriteScopes();
      throw new ScoutError(
        'youtube/scopes-missing',
        `Google did not grant ${missing.map((scope) => scope.slice(SCOPE_PREFIX.length)).join(' and ')}, so writes stay off. Click Allow writes again and allow every permission on Google's page.`,
      );
    }
  }

  async dropWriteScopes(): Promise<boolean> {
    this.cancelSignIn();
    const dropped = await this.serial(async () => {
      const session = await this.loadSession();
      if (!(session?.scopes ?? []).some((scope) => isWriteScope(scope))) return false;
      await this.revoke(session!);
      await this.clearSession();
      return true;
    });
    if (!dropped) return false;
    this.deps.logger.info('youtube: write scopes revoked; signing in with the read scopes');
    this.deps.onConnectionChanged();
    await this.startSignIn(READ_SCOPES);
    return true;
  }

  /** Resolves when the sign-in that waits for the browser has finished, either way. */
  settled(): Promise<void> {
    return this.flow;
  }

  /** Aborts a sign-in that is waiting for the browser and releases the loopback port. */
  cancelSignIn(): void {
    this.pending?.abort();
    this.pending = null;
  }

  async signOut(): Promise<YouTubeStatus> {
    this.cancelSignIn();
    return this.serial(async () => {
      const session = await this.loadSession();
      const apiKey = await this.deps.vault.get(API_KEY_KEY);
      if (session !== null) {
        await this.revoke(session);
        await this.clearSession();
      }
      await this.deps.vault.delete(API_KEY_KEY);
      this.lastError = null;
      this.deps.logger.info('youtube: disconnected');
      if (session !== null || apiKey !== null) this.deps.onConnectionChanged();
      return this.toStatus(null, null);
    });
  }

  /** Checks a pasted Data API key with one public read and, when Google accepts it, makes it the one YouTube credential. */
  async saveApiKey(raw: string): Promise<YouTubeStatus> {
    const apiKey = raw.trim();
    if (apiKey.length === 0) {
      throw new ScoutError('youtube/key-empty', 'Enter a YouTube Data API key.');
    }
    await this.checkApiKey(apiKey);
    this.cancelSignIn();
    return this.serial(async () => {
      const session = await this.loadSession();
      if (session !== null) {
        await this.revoke(session);
        await this.clearSession();
      }
      await this.deps.vault.store(API_KEY_KEY, apiKey);
      this.lastError = null;
      this.deps.logger.info('youtube: connected with an API key');
      await this.deps.revokeWriteGrant?.();
      this.deps.onConnectionChanged();
      return this.toStatus(null, apiKey);
    });
  }

  async clearApiKey(): Promise<YouTubeStatus> {
    return this.serial(async () => {
      const removed = await this.deps.vault.delete(API_KEY_KEY);
      this.deps.logger.info('youtube: API key removed');
      if (removed) this.deps.onConnectionChanged();
      return this.toStatus(await this.loadSession(), null);
    });
  }

  refreshIfDue(): Promise<void> {
    return this.serial(() => this.refreshNow());
  }

  private async startSignIn(scopes: readonly string[]): Promise<AbortController> {
    if (this.pending !== null) {
      throw new ScoutError(
        'youtube/already-in-progress',
        'A Google sign-in is already waiting for the browser.',
      );
    }
    const abort = new AbortController();
    this.pending = abort;
    this.lastError = null;
    try {
      await this.openConsent(abort, scopes);
    } catch (err: unknown) {
      if (this.pending === abort) this.pending = null;
      throw err;
    }
    return abort;
  }

  private async openConsent(abort: AbortController, scopes: readonly string[]): Promise<void> {
    const { deps } = this;
    const client = await this.readClientConfig();
    if (client === WEB_CLIENT) throw new ScoutError('youtube/not-configured', WEB_CLIENT_MESSAGE);
    if (client === null) {
      throw new ScoutError(
        'youtube/not-configured',
        'Sign in with Google needs a Google OAuth client; use an API key instead, or add the client file.',
      );
    }

    let port: number;
    try {
      port = await deps.findFreePort(LOOPBACK_PORT, LOOPBACK_FALLBACKS);
    } catch (err: unknown) {
      if (err instanceof OAuthLoopbackPortInUseError) {
        throw new ScoutError(
          'youtube/port-in-use',
          `Ports ${LOOPBACK_PORT} to ${LOOPBACK_PORT + LOOPBACK_FALLBACKS} are all in use; close the program using them and sign in again.`,
        );
      }
      throw err;
    }
    const redirectUri = `http://127.0.0.1:${port}${CALLBACK_PATH}`;
    const { verifier, challenge } = generatePkcePair();
    const state = generateState();

    const callback = deps.awaitCallback({
      expectedState: state,
      port,
      hostname: '127.0.0.1',
      timeoutMs: SIGN_IN_TIMEOUT_MS,
      logger: deps.logger,
      callbackPath: CALLBACK_PATH,
      signal: abort.signal,
    });
    callback.catch(() => {});

    const query = new URLSearchParams({
      client_id: client.clientId,
      redirect_uri: redirectUri,
      response_type: 'code',
      scope: scopes.join(' '),
      code_challenge: challenge,
      code_challenge_method: 'S256',
      state,
      access_type: 'offline',
      prompt: 'consent',
    });
    deps.logger.info('youtube: opening the Google sign-in page', { port });
    try {
      await deps.openExternal(`${AUTHORIZE_URL}?${query.toString()}`);
    } catch {
      abort.abort();
      throw new ScoutError('youtube/browser-failed', 'Could not open the browser to sign in.');
    }
    this.flow = this.finishSignIn(callback, { state, verifier, redirectUri, client }, abort);
  }

  private async finishSignIn(
    callback: Promise<LoopbackResult>,
    request: { state: string; verifier: string; redirectUri: string; client: ClientConfig },
    abort: AbortController,
  ): Promise<void> {
    const { deps } = this;
    const { signal } = abort;
    try {
      const result = await callback;
      if (result.state !== request.state) throw new OAuthStateMismatchError();
      const tokens = await this.exchangeCode(result.code, request);
      if (signal.aborted) return;
      const identity = await this.fetchIdentity(tokens.accessToken);
      if (signal.aborted) return;
      await this.serial(async () => {
        await deps.vault.delete(API_KEY_KEY);
        await this.saveSession({
          accessToken: tokens.accessToken,
          refreshToken: tokens.refreshToken,
          expiresAt: deps.now() + tokens.expiresIn * 1000,
          ...identity,
          scopes: tokens.scopes ?? [],
        });
      });
      deps.logger.info('youtube: signed in', { hasChannel: identity.channelId !== null });
      await deps.revokeWriteGrant?.();
      deps.onConnectionChanged();
    } catch (err: unknown) {
      if (signal.aborted) return;
      this.lastError = signInError(err);
      deps.logger.warn('youtube: sign-in failed', { code: this.lastError.code });
    } finally {
      if (this.pending === abort) this.pending = null;
    }
  }

  private async exchangeCode(
    code: string,
    request: { verifier: string; redirectUri: string; client: ClientConfig },
  ): Promise<TokenResponse> {
    let response: Response;
    try {
      response = await this.post(TOKEN_URL, {
        code,
        client_id: request.client.clientId,
        client_secret: request.client.clientSecret,
        redirect_uri: request.redirectUri,
        grant_type: 'authorization_code',
        code_verifier: request.verifier,
      });
    } catch {
      throw new ScoutError(
        'youtube/network-error',
        'Could not reach Google to finish the sign-in. Check the connection and sign in again.',
      );
    }
    const body = await readJson(response);
    if (response.status >= 500) {
      throw new ScoutError(
        'youtube/google-unavailable',
        `Google could not finish the sign-in (HTTP ${response.status}). Sign in again in a moment.`,
      );
    }
    if (!response.ok) {
      throw new ScoutError(
        'youtube/sign-in-refused',
        `Google refused the sign-in (${errorCode(body)}).`,
      );
    }
    const tokens = parseTokens(body);
    if (tokens === null) {
      throw new ScoutError(
        'youtube/unexpected-response',
        'Google returned an unexpected sign-in response.',
      );
    }
    return tokens;
  }

  private async fetchIdentity(accessToken: string): Promise<ChannelIdentity> {
    let response: Response;
    try {
      response = await this.deps.fetch(`${CHANNELS_URL}?part=snippet&mine=true`, {
        method: 'GET',
        headers: { Authorization: `Bearer ${accessToken}` },
        signal: AbortSignal.timeout(HTTP_TIMEOUT_MS),
      });
    } catch {
      throw new ScoutError(
        'youtube/network-error',
        'Could not reach YouTube to read the signed-in channel.',
      );
    }
    if (!response.ok) {
      throw new ScoutError(
        'youtube/request-failed',
        `YouTube answered HTTP ${response.status} when Scout asked which channel signed in.`,
      );
    }
    const items = (await readJson(response))?.['items'];
    if (!Array.isArray(items)) {
      throw new ScoutError(
        'youtube/unexpected-response',
        'YouTube did not say which channel signed in.',
      );
    }
    const first = items[0] as Record<string, unknown> | undefined;
    if (first === undefined) return { channelId: null, channelTitle: NO_CHANNEL };
    const snippet = first['snippet'] as Record<string, unknown> | undefined;
    const id = first['id'];
    const title = snippet?.['title'];
    if (typeof id !== 'string' || typeof title !== 'string') {
      throw new ScoutError(
        'youtube/unexpected-response',
        'YouTube did not say which channel signed in.',
      );
    }
    return { channelId: id, channelTitle: title };
  }

  private async checkApiKey(apiKey: string): Promise<void> {
    const query = new URLSearchParams({ part: 'id', forHandle: 'youtube', key: apiKey });
    let response: Response;
    try {
      response = await this.deps.fetch(`${CHANNELS_URL}?${query.toString()}`, {
        method: 'GET',
        signal: AbortSignal.timeout(HTTP_TIMEOUT_MS),
      });
    } catch {
      throw new ScoutError(
        'youtube/network-error',
        'Could not reach YouTube to check the API key.',
      );
    }
    if (response.status === 400 || response.status === 403) {
      throw keyRefusal(await readJson(response));
    }
    if (!response.ok) {
      throw new ScoutError(
        'youtube/request-failed',
        `YouTube answered HTTP ${response.status} when Scout checked the API key.`,
      );
    }
  }

  private async refreshNow(): Promise<void> {
    const { deps } = this;
    const session = await this.loadSession();
    if (session === null) return;
    if (session.refreshToken === null || session.expiresAt - deps.now() > REFRESH_MARGIN_MS) {
      if (this.tokenFileStale) await this.writeTokenFileOrMarkStale(session);
      return;
    }
    const client = await this.readClientConfig();
    if (client === null || client === WEB_CLIENT) {
      deps.logger.warn('youtube: token refresh skipped; the Google OAuth client is missing');
      return;
    }

    let response: Response;
    try {
      response = await this.post(TOKEN_URL, {
        grant_type: 'refresh_token',
        refresh_token: session.refreshToken,
        client_id: client.clientId,
        client_secret: client.clientSecret,
      });
    } catch (err: unknown) {
      deps.logger.warn('youtube: token refresh did not reach Google; will retry', {
        error: errorName(err),
      });
      return;
    }

    const body = await readJson(response);
    if (!response.ok) {
      const code = errorCode(body);
      if (code === 'invalid_grant') {
        try {
          await this.clearSession();
        } catch (err: unknown) {
          this.lastError = err instanceof ScoutError ? err : null;
          deps.logger.warn('youtube: refresh rejected but the token file could not be removed', {
            error: errorName(err),
          });
          return;
        }
        this.lastError = new ScoutError(
          'youtube/signed-out',
          'Google no longer accepts the saved sign-in. Sign in with Google again.',
        );
        deps.logger.warn('youtube: refresh rejected; the owner must sign in again', { code });
        await deps.revokeWriteGrant?.();
        deps.onConnectionChanged();
        return;
      }
      deps.logger.warn('youtube: token refresh failed; will retry', {
        status: response.status,
        code,
      });
      return;
    }
    const tokens = parseTokens(body);
    if (tokens === null) {
      deps.logger.warn('youtube: token refresh returned an unexpected body; will retry');
      return;
    }
    await this.saveSession({
      ...session,
      accessToken: tokens.accessToken,
      refreshToken: tokens.refreshToken ?? session.refreshToken,
      expiresAt: deps.now() + tokens.expiresIn * 1000,
      scopes: tokens.scopes ?? session.scopes ?? [],
    });
    deps.logger.info('youtube: access token refreshed');
  }

  private async revoke(session: StoredSession): Promise<void> {
    try {
      const response = await this.post(REVOKE_URL, {
        token: session.refreshToken ?? session.accessToken,
      });
      if (!response.ok) {
        this.deps.logger.warn('youtube: revocation was refused', { status: response.status });
      }
    } catch (err: unknown) {
      this.deps.logger.warn('youtube: revocation did not reach Google', { error: errorName(err) });
    }
  }

  private post(url: string, fields: Record<string, string>): Promise<Response> {
    return this.deps.fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams(fields).toString(),
      signal: AbortSignal.timeout(HTTP_TIMEOUT_MS),
    });
  }

  private async readClientConfig(): Promise<ClientConfig | typeof WEB_CLIENT | null> {
    const { env, defaultClient } = this.deps;
    const envId = env['SCOUT_GOOGLE_CLIENT_ID'];
    const envSecret = env['SCOUT_GOOGLE_CLIENT_SECRET'];
    if (envId && envSecret) return { clientId: envId, clientSecret: envSecret, source: 'env' };
    const file = await this.readClientFile(this.deps.clientConfigPath);
    if (file !== null && file !== WEB_CLIENT) return { ...file, source: 'file' };
    const { bundledClientPath } = this.deps;
    if (bundledClientPath !== undefined) {
      this.bundled ??= this.readClientFile(bundledClientPath);
      const bundled = await this.bundled;
      if (bundled !== null && bundled !== WEB_CLIENT) return { ...bundled, source: 'bundled' };
    }
    if (defaultClient?.clientId && defaultClient.clientSecret) {
      return { ...defaultClient, source: 'embedded' };
    }
    return file;
  }

  private async readClientFile(path: string): Promise<ClientFields | typeof WEB_CLIENT | null> {
    let parsed: unknown;
    try {
      parsed = JSON.parse(await this.deps.fs.readFile(path));
    } catch {
      return null;
    }
    if (typeof parsed !== 'object' || parsed === null) return null;
    const file = parsed as Record<string, unknown>;
    if ('installed' in file) return clientFields(file['installed']);
    if ('web' in file) return WEB_CLIENT;
    return clientFields(file);
  }

  private async toStatus(
    session: StoredSession | null,
    apiKey: string | null,
  ): Promise<YouTubeStatus> {
    const client = await this.readClientConfig();
    return {
      configured: client !== null && client !== WEB_CLIENT,
      connected: session !== null || apiKey !== null,
      method: session !== null ? 'oauth' : apiKey !== null ? 'apiKey' : null,
      channelId: session?.channelId ?? null,
      channelTitle: session?.channelTitle ?? null,
      pending: this.pending !== null,
      error: this.lastError?.message ?? (client === WEB_CLIENT ? WEB_CLIENT_MESSAGE : null),
      clientConfigPath: this.deps.clientConfigPath,
      source: client !== null && client !== WEB_CLIENT ? client.source : null,
      scopes: session?.scopes ?? [],
    };
  }

  private async loadSession(): Promise<StoredSession | null> {
    const raw = await this.deps.vault.get(SESSION_KEY);
    return raw === null ? null : (JSON.parse(raw) as StoredSession);
  }

  private async saveSession(session: StoredSession): Promise<void> {
    await this.deps.vault.store(SESSION_KEY, JSON.stringify(session));
    await this.writeTokenFileOrMarkStale(session);
  }

  private async clearSession(): Promise<void> {
    const { fs, tokenFilePath } = this.deps;
    try {
      await fs.rm(tokenFilePath, { force: true });
      await fs.rm(`${tokenFilePath}.tmp`, { force: true });
    } catch (err: unknown) {
      throw new ScoutError(
        'youtube/token-file-not-removed',
        'Could not remove the YouTube token file, so Scout is still connected. Try Disconnect again.',
        { cause: err },
      );
    }
    await this.deps.vault.delete(SESSION_KEY);
  }

  private async writeTokenFileOrMarkStale(session: StoredSession): Promise<void> {
    const { fs, tokenFilePath } = this.deps;
    try {
      const tmp = `${tokenFilePath}.tmp`;
      const content = JSON.stringify({
        access_token: session.accessToken,
        expires_at: session.expiresAt,
      });
      await fs.writeFile(tmp, content, { mode: 0o600 });
      await fs.rename(tmp, tokenFilePath);
      this.tokenFileStale = false;
    } catch (err: unknown) {
      this.tokenFileStale = true;
      this.deps.logger.warn('youtube: could not write the token file; will retry', {
        error: errorName(err),
      });
    }
  }

  private serial<T>(task: () => Promise<T>): Promise<T> {
    const next = this.queue.then(task, task);
    this.queue = next.catch(() => {});
    return next;
  }
}

function isWriteScope(scope: string): boolean {
  return (WRITE_SCOPES as readonly string[]).includes(scope);
}

function missingWriteScopes(session: StoredSession | null): string[] {
  const granted = session?.scopes ?? [];
  return WRITE_SCOPES.filter((scope) => !granted.includes(scope));
}

function signInError(err: unknown): ScoutError {
  if (err instanceof OAuthStateMismatchError) {
    return new ScoutError(
      'youtube/state-mismatch',
      'The answer from the browser did not belong to this sign-in, so nothing was saved. Sign in again.',
    );
  }
  if (err instanceof OAuthUserCancelledError) {
    return new ScoutError(
      'youtube/sign-in-cancelled',
      'The Google sign-in was cancelled or timed out. Click Sign in with Google to try again.',
    );
  }
  if (err instanceof DiscordTokenExchangeError) {
    return new ScoutError('youtube/sign-in-refused', `Google refused the sign-in (${err.reason}).`);
  }
  if (err instanceof ScoutError && err.code.startsWith('youtube/')) return err;
  return new ScoutError('youtube/sign-in-failed', 'The Google sign-in failed.');
}

function clientFields(value: unknown): ClientFields | null {
  if (typeof value !== 'object' || value === null) return null;
  const clientId = (value as Record<string, unknown>)['client_id'];
  const clientSecret = (value as Record<string, unknown>)['client_secret'];
  if (typeof clientId !== 'string' || typeof clientSecret !== 'string') return null;
  if (clientId.length === 0 || clientSecret.length === 0) return null;
  return { clientId, clientSecret };
}

/** Turns Google's refusal of a pasted key into a sentence; never echoes the key, the URL or Google's message. */
function keyRefusal(body: Record<string, unknown> | null): ScoutError {
  const error = body?.['error'];
  const fields =
    typeof error === 'object' && error !== null ? (error as Record<string, unknown>) : {};
  const details = Array.isArray(fields['details']) ? (fields['details'] as unknown[]) : [];
  const errors = Array.isArray(fields['errors']) ? (fields['errors'] as unknown[]) : [];
  const reasons: string[] = [];
  let consumer: string | null = null;
  for (const entry of [...details, errors[0]]) {
    if (typeof entry !== 'object' || entry === null) continue;
    const reason = (entry as Record<string, unknown>)['reason'];
    if (typeof reason === 'string') reasons.push(reason);
    const metadata = (entry as Record<string, unknown>)['metadata'];
    const named =
      typeof metadata === 'object' && metadata !== null
        ? (metadata as Record<string, unknown>)['consumer']
        : undefined;
    if (typeof named === 'string' && /^projects\/[A-Za-z0-9-]+$/.test(named)) consumer = named;
  }
  const has = (...wanted: string[]): boolean => reasons.some((r) => wanted.includes(r));
  if (has('API_KEY_INVALID')) {
    return new ScoutError(
      'youtube/key-rejected',
      'Google does not recognise this API key. Copy it again from Google Cloud Console credentials.',
    );
  }
  if (has('SERVICE_DISABLED', 'accessNotConfigured')) {
    const project = consumer === null ? '' : ` (${consumer})`;
    return new ScoutError(
      'youtube/key-rejected',
      `The YouTube Data API v3 is not enabled for this key's project. Enable it in Google Cloud Console and try again in a minute.${project}`,
    );
  }
  if (has('API_KEY_SERVICE_BLOCKED', 'forbidden')) {
    return new ScoutError(
      'youtube/key-rejected',
      "This key's restrictions do not allow the YouTube Data API v3. Edit the key's API restrictions in Google Cloud Console.",
    );
  }
  if (has('quotaExceeded')) {
    return new ScoutError(
      'youtube/quota-exceeded',
      "YouTube's daily quota for this key is spent; it resets at midnight Pacific time.",
    );
  }
  const first = reasons[0];
  const named = first !== undefined && /^[A-Za-z][A-Za-z0-9_]*$/.test(first) ? ` (${first})` : '';
  return new ScoutError(
    'youtube/key-rejected',
    `Google rejected this API key. Check that the YouTube Data API v3 is enabled for it.${named}`,
  );
}

function parseTokens(body: Record<string, unknown> | null): TokenResponse | null {
  const accessToken = body?.['access_token'];
  const expiresIn = body?.['expires_in'];
  if (typeof accessToken !== 'string' || accessToken.length === 0) return null;
  if (typeof expiresIn !== 'number') return null;
  const refreshToken = body?.['refresh_token'];
  const scope = body?.['scope'];
  return {
    accessToken,
    refreshToken: typeof refreshToken === 'string' ? refreshToken : null,
    expiresIn,
    scopes: typeof scope === 'string' ? scope.split(/\s+/).filter((s) => s.length > 0) : null,
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
  return typeof error === 'string' ? error : 'unknown_error';
}

function errorName(err: unknown): string {
  return err instanceof Error ? err.name : 'unknown';
}
