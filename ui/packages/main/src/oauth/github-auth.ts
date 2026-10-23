import { ScoutError, type GitHubPendingSignIn, type GitHubStatus } from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { Vault } from '../vault/safe-storage-vault.js';

/** Public identifier of the Scout GitHub App; empty until the App is registered. */
export const GITHUB_APP_CLIENT_ID = '';
/** URL slug of the Scout GitHub App; empty until the App is registered. */
export const GITHUB_APP_SLUG = '';

const DEVICE_CODE_URL = 'https://github.com/login/device/code';
const TOKEN_URL = 'https://github.com/login/oauth/access_token';
const API_URL = 'https://api.github.com';
const DEVICE_GRANT = 'urn:ietf:params:oauth:grant-type:device_code';
const VERIFICATION_PREFIX = 'https://github.com/';
const HTTP_TIMEOUT_MS = 15_000;
const SLOW_DOWN_STEP_S = 5;
const REFRESH_MARGIN_MS = 5 * 60_000;
const REFRESH_CHECK_MS = 60_000;
const SESSION_KEY = 'github.session';
const TOKEN_KEY = 'github.token';
const WRITE_WARNING =
  'This token can write to your repositories. A read-only fine-grained token is safer.';
const WRITE_SCOPES = new Set(['repo', 'public_repo', 'delete_repo', 'workflow']);

interface StoredSession {
  accessToken: string;
  refreshToken: string | null;
  expiresAt: number | null;
  login: string;
}

interface StoredToken {
  token: string;
  login: string;
  writeCapable: boolean;
}

interface TokenResponse {
  accessToken: string;
  refreshToken: string | null;
  expiresIn: number | null;
}

interface PendingSignIn {
  view: GitHubPendingSignIn;
  abort: AbortController;
}

export interface TokenFileFs {
  writeFile(path: string, data: string, options: { mode: number }): Promise<void>;
  rename(from: string, to: string): Promise<void>;
  rm(path: string, options: { force: true }): Promise<void>;
}

export interface GitHubAuthDeps {
  fetch: typeof globalThis.fetch;
  vault: Pick<Vault, 'get' | 'store' | 'delete'>;
  tokenFilePath: string;
  fs: TokenFileFs;
  now(): number;
  sleep(ms: number, signal: AbortSignal): Promise<void>;
  clientId: string;
  appSlug: string;
  onConnectionChanged(): void;
  /** Subscribes to the system waking from sleep; returns the unsubscribe function. */
  onResume(listener: () => void): () => void;
  logger: Logger;
}

/** Waits for the given time, or less when the signal aborts. */
export function abortableSleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, ms);
    signal.addEventListener(
      'abort',
      () => {
        clearTimeout(timer);
        resolve();
      },
      { once: true },
    );
  });
}

/**
 * Connects the owner's GitHub account either through the GitHub App device flow, keeping the
 * user access token fresh in the token file the worker reads, or with a token the owner pastes;
 * exactly one of the two is stored at a time. Tokens and the device code never leave this
 * class except as the worker's environment: not in logs, not in the status the renderer sees.
 */
export class GitHubAuth {
  private queue: Promise<unknown> = Promise.resolve();
  private pending: PendingSignIn | null = null;
  private poll: Promise<void> = Promise.resolve();
  private lastError: ScoutError | null = null;
  private installed = false;
  private tokenFileStale = false;
  private timer: ReturnType<typeof setInterval> | null = null;
  private unsubscribeResume: (() => void) | null = null;

  constructor(private readonly deps: GitHubAuthDeps) {}

  /** Rewrites the token file from the stored session, refreshes when due, then checks every minute and on wake. */
  async start(): Promise<void> {
    const check = (): Promise<void> =>
      this.refreshIfDue().catch((err: unknown) => {
        this.deps.logger.warn('github: refresh check failed', { error: errorName(err) });
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
    this.cancel();
  }

  /** The variables that give the worker the active GitHub credential, or none when disconnected. */
  async workerEnv(): Promise<Record<string, string>> {
    if ((await this.loadSession()) !== null) {
      return { SCOUT__PLUGINS__GITHUB__TOKENFILE: this.deps.tokenFilePath };
    }
    const stored = await this.loadToken();
    return stored === null ? {} : { SCOUT__PLUGINS__GITHUB__TOKEN: stored.token };
  }

  async status(): Promise<GitHubStatus> {
    const session = await this.loadSession();
    if (session !== null) {
      this.installed = await this.checkInstalled(session.accessToken);
      return this.toStatus(session, null);
    }
    return this.toStatus(null, await this.loadToken());
  }

  /** Checks a pasted token against GitHub and, when it works, makes it the one GitHub credential. */
  async saveToken(raw: string): Promise<GitHubStatus> {
    const token = raw.trim();
    if (token.length === 0) throw new ScoutError('github/token-empty', 'Enter a GitHub token.');
    const { login, writeCapable } = await this.checkToken(token);
    this.cancel();
    return this.serial(async () => {
      if ((await this.loadSession()) !== null) await this.clearSession();
      const stored: StoredToken = { token, login, writeCapable };
      await this.deps.vault.store(TOKEN_KEY, JSON.stringify(stored));
      this.lastError = null;
      this.deps.logger.info('github: connected with a token', { login, writeCapable });
      this.deps.onConnectionChanged();
      return this.toStatus(null, stored);
    });
  }

  /** Starts the device flow and returns the code to show; the approval completes in the background. */
  async signIn(): Promise<GitHubPendingSignIn> {
    if (this.deps.clientId.length === 0) {
      throw new ScoutError('github/not-configured', 'The Scout GitHub App is not configured yet.');
    }
    if (this.pending !== null) return this.pending.view;
    this.lastError = null;

    const body = await this.postForm(DEVICE_CODE_URL, { client_id: this.deps.clientId }, 'start');
    const error = body?.['error'];
    if (typeof error === 'string') throw refusal(error);
    const deviceCode = body?.['device_code'];
    const userCode = body?.['user_code'];
    const verificationUri = body?.['verification_uri'];
    const expiresIn = body?.['expires_in'];
    const interval = body?.['interval'];
    if (
      typeof deviceCode !== 'string' ||
      typeof userCode !== 'string' ||
      typeof verificationUri !== 'string' ||
      !verificationUri.startsWith(VERIFICATION_PREFIX) ||
      typeof expiresIn !== 'number' ||
      typeof interval !== 'number'
    ) {
      throw new ScoutError(
        'github/unexpected-response',
        'GitHub returned an unexpected sign-in response.',
      );
    }

    const pending: PendingSignIn = {
      view: { userCode, verificationUri },
      abort: new AbortController(),
    };
    this.pending = pending;
    this.deps.logger.info('github: waiting for the owner to enter the code');
    this.poll = this.waitForApproval(
      deviceCode,
      interval,
      this.deps.now() + expiresIn * 1000,
      pending,
    );
    return pending.view;
  }

  /** Resolves when the sign-in that is waiting for approval has finished, either way. */
  settled(): Promise<void> {
    return this.poll;
  }

  cancel(): void {
    this.pending?.abort.abort();
    this.pending = null;
  }

  async signOut(): Promise<GitHubStatus> {
    this.cancel();
    return this.serial(async () => {
      const hadSession = (await this.loadSession()) !== null;
      const hadToken = (await this.loadToken()) !== null;
      await this.clearSession();
      await this.deps.vault.delete(TOKEN_KEY);
      this.lastError = null;
      this.deps.logger.info('github: disconnected');
      if (hadSession || hadToken) this.deps.onConnectionChanged();
      return this.toStatus(null, null);
    });
  }

  refreshIfDue(): Promise<void> {
    return this.serial(() => this.refreshNow());
  }

  private async waitForApproval(
    deviceCode: string,
    intervalSeconds: number,
    deadline: number,
    pending: PendingSignIn,
  ): Promise<void> {
    const { signal } = pending.abort;
    let interval = intervalSeconds;
    try {
      for (;;) {
        await this.deps.sleep(interval * 1000, signal);
        if (signal.aborted) return;
        if (this.deps.now() >= deadline) throw refusal('expired_token');
        const body = await this.postForm(
          TOKEN_URL,
          { client_id: this.deps.clientId, device_code: deviceCode, grant_type: DEVICE_GRANT },
          'finish',
        );
        if (signal.aborted) return;
        const error = body?.['error'];
        if (error === 'authorization_pending') continue;
        if (error === 'slow_down') {
          const next = body?.['interval'];
          interval = typeof next === 'number' ? next : interval + SLOW_DOWN_STEP_S;
          continue;
        }
        if (typeof error === 'string') throw refusal(error);
        const tokens = parseTokens(body);
        if (tokens === null) {
          throw new ScoutError(
            'github/unexpected-response',
            'GitHub returned an unexpected sign-in response.',
          );
        }
        const login = await this.fetchLogin(tokens.accessToken);
        if (signal.aborted) return;
        await this.serial(async () => {
          await this.deps.vault.delete(TOKEN_KEY);
          await this.saveSession(this.toSession(tokens, login));
        });
        this.installed = await this.checkInstalled(tokens.accessToken);
        this.deps.logger.info('github: connected', { login, installed: this.installed });
        this.deps.onConnectionChanged();
        return;
      }
    } catch (err: unknown) {
      if (signal.aborted) return;
      this.lastError =
        err instanceof ScoutError
          ? err
          : new ScoutError('github/sign-in-failed', 'The GitHub sign-in failed.');
      this.deps.logger.warn('github: sign-in failed', { code: this.lastError.code });
    } finally {
      if (this.pending === pending) this.pending = null;
    }
  }

  private async refreshNow(): Promise<void> {
    const { deps } = this;
    const session = await this.loadSession();
    if (session === null) return;
    if (
      session.refreshToken === null ||
      session.expiresAt === null ||
      session.expiresAt - deps.now() > REFRESH_MARGIN_MS
    ) {
      if (this.tokenFileStale) await this.writeTokenFileOrMarkStale(session);
      return;
    }

    let body: Record<string, unknown> | null;
    try {
      body = await this.postForm(
        TOKEN_URL,
        {
          client_id: deps.clientId,
          grant_type: 'refresh_token',
          refresh_token: session.refreshToken,
        },
        'refresh',
      );
    } catch {
      deps.logger.warn('github: token refresh did not reach GitHub; will retry');
      return;
    }

    const error = body?.['error'];
    if (error === 'bad_refresh_token') {
      try {
        await this.clearSession();
      } catch (err: unknown) {
        this.lastError = err instanceof ScoutError ? err : null;
        deps.logger.warn('github: refresh rejected but the token file could not be removed', {
          error: errorName(err),
        });
        return;
      }
      this.lastError = new ScoutError(
        'github/signed-out',
        'GitHub no longer accepts the saved sign-in. Connect with GitHub again.',
      );
      deps.logger.warn('github: refresh rejected; the owner must connect again', { code: error });
      deps.onConnectionChanged();
      return;
    }
    const tokens = typeof error === 'string' ? null : parseTokens(body);
    if (tokens === null) {
      deps.logger.warn('github: token refresh failed; will retry', {
        code: typeof error === 'string' ? error : 'unexpected-response',
      });
      return;
    }
    await this.saveSession(this.toSession(tokens, session.login));
    deps.logger.info('github: access token refreshed');
  }

  private toSession(tokens: TokenResponse, login: string): StoredSession {
    return {
      accessToken: tokens.accessToken,
      refreshToken: tokens.refreshToken,
      expiresAt: tokens.expiresIn === null ? null : this.deps.now() + tokens.expiresIn * 1000,
      login,
    };
  }

  private async fetchLogin(accessToken: string): Promise<string> {
    const body = await this.getApi('/user', accessToken);
    const login = body?.['login'];
    if (typeof login !== 'string') {
      throw new ScoutError('github/unexpected-response', 'GitHub did not say who signed in.');
    }
    return login;
  }

  private async checkInstalled(accessToken: string): Promise<boolean> {
    try {
      const body = await this.getApi('/user/installations', accessToken);
      const count = body?.['total_count'];
      return typeof count === 'number' ? count > 0 : this.installed;
    } catch {
      return this.installed;
    }
  }

  private async checkToken(token: string): Promise<{ login: string; writeCapable: boolean }> {
    const response = await this.requestApi('/user', token);
    if (response.status === 401) {
      throw new ScoutError('github/token-rejected', 'GitHub rejected this token.');
    }
    if (!response.ok) {
      throw new ScoutError('github/request-failed', `GitHub answered HTTP ${response.status}.`);
    }
    const login = (await readJson(response))?.['login'];
    if (typeof login !== 'string') {
      throw new ScoutError('github/unexpected-response', 'GitHub did not say whose token this is.');
    }
    const scopes = (response.headers.get('X-OAuth-Scopes') ?? '').split(',').map((s) => s.trim());
    return { login, writeCapable: scopes.some(isWriteScope) };
  }

  private async getApi(path: string, accessToken: string): Promise<Record<string, unknown> | null> {
    const response = await this.requestApi(path, accessToken);
    if (!response.ok) {
      throw new ScoutError('github/request-failed', `GitHub answered HTTP ${response.status}.`);
    }
    return readJson(response);
  }

  private async requestApi(path: string, accessToken: string): Promise<Response> {
    try {
      return await this.deps.fetch(`${API_URL}${path}`, {
        method: 'GET',
        headers: {
          Accept: 'application/vnd.github+json',
          Authorization: `Bearer ${accessToken}`,
          'User-Agent': 'Scout',
          'X-GitHub-Api-Version': '2022-11-28',
        },
        signal: AbortSignal.timeout(HTTP_TIMEOUT_MS),
      });
    } catch (err: unknown) {
      throw new ScoutError('github/network-error', 'Could not reach GitHub.', { cause: err });
    }
  }

  private async postForm(
    url: string,
    fields: Record<string, string>,
    step: 'start' | 'finish' | 'refresh',
  ): Promise<Record<string, unknown> | null> {
    let response: Response;
    try {
      response = await this.deps.fetch(url, {
        method: 'POST',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/x-www-form-urlencoded',
        },
        body: new URLSearchParams(fields).toString(),
        signal: AbortSignal.timeout(HTTP_TIMEOUT_MS),
      });
    } catch (err: unknown) {
      throw new ScoutError(
        'github/network-error',
        `Could not reach GitHub to ${step} the sign-in.`,
        {
          cause: err,
        },
      );
    }
    return readJson(response);
  }

  private toStatus(session: StoredSession | null, stored: StoredToken | null): GitHubStatus {
    const { clientId, appSlug } = this.deps;
    return {
      configured: clientId.length > 0,
      connected: session !== null || stored !== null,
      method: session !== null ? 'browser' : stored !== null ? 'token' : null,
      login: session?.login ?? stored?.login ?? null,
      warning: stored?.writeCapable === true ? WRITE_WARNING : null,
      installed: session !== null && this.installed,
      installUrl:
        appSlug.length > 0 ? `https://github.com/apps/${appSlug}/installations/new` : null,
      pending: this.pending?.view ?? null,
      error:
        this.lastError === null
          ? null
          : { code: this.lastError.code, message: this.lastError.message },
    };
  }

  private async loadSession(): Promise<StoredSession | null> {
    const raw = await this.deps.vault.get(SESSION_KEY);
    return raw === null ? null : (JSON.parse(raw) as StoredSession);
  }

  private async loadToken(): Promise<StoredToken | null> {
    const raw = await this.deps.vault.get(TOKEN_KEY);
    return raw === null ? null : (JSON.parse(raw) as StoredToken);
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
        'github/token-file-not-removed',
        'Could not remove the GitHub token file, so Scout is still connected. Try Disconnect again.',
        { cause: err },
      );
    }
    await this.deps.vault.delete(SESSION_KEY);
    this.installed = false;
  }

  private async writeTokenFileOrMarkStale(session: StoredSession): Promise<void> {
    const { fs, tokenFilePath } = this.deps;
    try {
      const tmp = `${tokenFilePath}.tmp`;
      await fs.writeFile(tmp, JSON.stringify({ access_token: session.accessToken }), {
        mode: 0o600,
      });
      await fs.rename(tmp, tokenFilePath);
      this.tokenFileStale = false;
    } catch (err: unknown) {
      this.tokenFileStale = true;
      this.deps.logger.warn('github: could not write the token file; will retry', {
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
  return WRITE_SCOPES.has(scope) || scope.startsWith('write:') || scope.startsWith('admin:');
}

function refusal(error: string): ScoutError {
  switch (error) {
    case 'access_denied':
      return new ScoutError('github/access-denied', 'The sign-in was cancelled on GitHub.');
    case 'expired_token':
      return new ScoutError(
        'github/expired-token',
        'The code expired. Click Connect with GitHub to get a new one.',
      );
    case 'device_flow_disabled':
      return new ScoutError(
        'github/device-flow-disabled',
        'Device flow is not enabled on the Scout GitHub App.',
      );
    default: {
      const code = error.toLowerCase().replace(/[^a-z0-9]+/g, '-');
      return new ScoutError(`github/${code}`, `GitHub refused the sign-in (${error}).`);
    }
  }
}

function parseTokens(body: Record<string, unknown> | null): TokenResponse | null {
  const accessToken = body?.['access_token'];
  if (typeof accessToken !== 'string' || accessToken.length === 0) return null;
  const refreshToken = body?.['refresh_token'];
  const expiresIn = body?.['expires_in'];
  return {
    accessToken,
    refreshToken: typeof refreshToken === 'string' ? refreshToken : null,
    expiresIn: typeof expiresIn === 'number' ? expiresIn : null,
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

function errorName(err: unknown): string {
  return err instanceof Error ? err.name : 'unknown';
}
