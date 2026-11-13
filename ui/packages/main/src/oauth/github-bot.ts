import { randomInt, sign } from 'node:crypto';
import {
  GITHUB_BOT_WRITES,
  OAuthLoopbackPortInUseError,
  OAuthStateMismatchError,
  OAuthUserCancelledError,
  ScoutError,
  type GitHubBotCreateRequest,
  type GitHubBotStatus,
  type GitHubBotWrite,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { TokenFileFs } from './github-auth.js';
import type { awaitCallback, findFreePort } from './loopback-server.js';
import { generateState } from './state.js';

const API_URL = 'https://api.github.com';
const HOMEPAGE = 'https://github.com/vitorafgomes/scout';
const BOT_KEY = 'github.bot';
const CALLBACK_PATH = '/callback';
const CREATE_TIMEOUT_MS = 5 * 60_000;
const HTTP_TIMEOUT_MS = 15_000;
const REPOSITORIES_PER_PAGE = 100;
const REPOSITORIES_MAX_PAGES = 50;
const REFRESH_MARGIN_MS = 5 * 60_000;
const REFRESH_CHECK_MS = 60_000;
const JWT_BACKDATE_S = 60;
const JWT_LIFETIME_S = 9 * 60;
const PORT_RANGE = [49_152, 60_000] as const;
const PORT_FALLBACKS = 20;
const ORG_PATTERN = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$/;
const NAME_NOT_REFRESHED =
  "Scout could not refresh the bot's name from GitHub; this is the last name it read.";

/** The permissions the Scout bot's App asks for, by GitHub's manifest keys. */
export const BOT_PERMISSIONS = {
  metadata: 'read',
  contents: 'read',
  pull_requests: 'write',
  issues: 'write',
  checks: 'read',
  statuses: 'read',
} as const;

interface StoredBot {
  appId: number;
  clientId: string;
  slug: string;
  name?: string;
  account: string;
  accountType: 'user' | 'org';
  privateKey: string;
  writes: GitHubBotWrite[];
}

interface InstallationToken {
  token: string;
  expiresAt: number;
}

export interface GitHubBotDeps {
  fetch: typeof globalThis.fetch;
  vault: Pick<Vault, 'get' | 'store' | 'delete'>;
  tokenFilePath: string;
  fs: TokenFileFs;
  now(): number;
  awaitCallback: typeof awaitCallback;
  findFreePort: typeof findFreePort;
  openExternal(url: string): Promise<void>;
  /** The login of the owner's connected GitHub account, or null when GitHub is not connected. */
  ownerLogin(): Promise<string | null>;
  /** Called when what the worker gets changes: the bot created, installed, removed, or its writes switched. */
  onChanged(): void;
  /** Subscribes to the system waking from sleep; returns the unsubscribe function. */
  onResume(listener: () => void): () => void;
  logger: Logger;
  createTimeoutMs?: number;
}

/** What the worker needs to sign the switched writes as the bot. */
export interface GitHubBotWorkerConfig {
  readonly tokenFile: string;
  readonly writes: readonly GitHubBotWrite[];
  readonly login: string;
  /** The `owner/repo` names the bot's installation covers. */
  readonly repositories: readonly string[];
}

/** What the bell's poll needs to find a mention of the bot: its slug and the account its installation is on. */
export interface GitHubBotMentionTarget {
  readonly slug: string;
  readonly account: string;
  readonly accountType: 'user' | 'org';
}

/**
 * The owner's optional Scout bot: a GitHub App of their own, created from the desktop with GitHub's manifest flow.
 * Keeps the App's private key in the vault, mints the installation token with a JSON Web Token signed by that key,
 * and keeps it fresh in the token file the worker reads. The key and the tokens never leave this class except as
 * that file: not in logs, not in the status the renderer sees.
 */
export class GitHubBot {
  private queue: Promise<unknown> = Promise.resolve();
  private creating: AbortController | null = null;
  private lastError: ScoutError | null = null;
  private token: InstallationToken | null = null;
  private repositories: string[] = [];
  private installedOn: { account: string; accountType: 'user' | 'org' } | null = null;
  private appReadFailed = false;
  private timer: ReturnType<typeof setInterval> | null = null;
  private unsubscribeResume: (() => void) | null = null;
  private flow: Promise<void> = Promise.resolve();
  private ready: Promise<void> = Promise.resolve();
  private starting = false;

  constructor(private readonly deps: GitHubBotDeps) {}

  /**
   * Mints the installation token when the bot exists, then keeps it fresh every minute and on wake. A worker built
   * meanwhile waits for this first token instead of being restarted when it arrives.
   */
  async start(): Promise<void> {
    const check = (): Promise<void> =>
      this.refreshIfDue().catch((err: unknown) => {
        this.deps.logger.warn('github-bot: refresh check failed', { error: errorName(err) });
      });
    this.timer = setInterval(check, REFRESH_CHECK_MS);
    this.timer.unref();
    this.unsubscribeResume = this.deps.onResume(check);
    this.starting = true;
    this.ready = this.serial(() => this.rereadApp())
      .then(check)
      .finally(() => {
        this.starting = false;
      });
    await this.ready;
  }

  stop(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = null;
    this.unsubscribeResume?.();
    this.unsubscribeResume = null;
    this.creating?.abort();
  }

  /** The bot's token file, writes and login for the worker, or null while there is no installation token. */
  async workerConfig(): Promise<GitHubBotWorkerConfig | null> {
    await this.ready;
    const bot = await this.load();
    if (bot === null || this.token === null) return null;
    return {
      tokenFile: this.deps.tokenFilePath,
      writes: bot.writes,
      login: botLogin(bot),
      repositories: [...this.repositories],
    };
  }

  /**
   * The bot's slug and the account it is installed on, or null while the bot has no installation token; at launch it
   * waits for the first token, as the worker does.
   */
  async mentionTarget(): Promise<GitHubBotMentionTarget | null> {
    await this.ready;
    const bot = await this.load();
    if (bot === null || this.token === null || this.installedOn === null) return null;
    return { slug: bot.slug, ...this.installedOn };
  }

  /**
   * The bot as the card shows it. It reads the App's name and slug again, so a rename on GitHub is followed, and with a token
   * it re-reads the repositories the installation covers, so a change made on GitHub reaches the worker when the
   * owner comes back to the card.
   */
  async status(): Promise<GitHubBotStatus> {
    if ((await this.load()) !== null) {
      await this.serial(() => this.rereadApp());
      await (this.token === null
        ? this.refreshIfDue()
        : this.serial(() => this.rereadRepositories()));
    }
    return this.toStatus(await this.load());
  }

  /** Opens GitHub's page that creates the App from Scout's manifest; the App comes back in the background. */
  async create(req: GitHubBotCreateRequest): Promise<GitHubBotStatus> {
    const { deps } = this;
    if (this.creating !== null) return this.toStatus(null);
    if ((await this.load()) !== null) {
      throw new ScoutError('github-bot/exists', 'The Scout bot already exists. Remove it first.');
    }
    const login = await deps.ownerLogin();
    if (login === null) {
      throw new ScoutError('github-bot/not-connected', 'Connect GitHub before creating the bot.');
    }
    const org = req.target === 'org' ? req.org.trim() : null;
    if (org !== null && !ORG_PATTERN.test(org)) {
      throw new ScoutError(
        'github-bot/invalid-org',
        'Enter the organization as it appears on GitHub.',
      );
    }

    let port: number;
    try {
      port = await deps.findFreePort(randomInt(PORT_RANGE[0], PORT_RANGE[1]), PORT_FALLBACKS);
    } catch (err: unknown) {
      if (err instanceof OAuthLoopbackPortInUseError) {
        throw new ScoutError(
          'github-bot/port-in-use',
          'Scout found no free local port to receive the App from GitHub; try again.',
        );
      }
      throw err;
    }
    const state = generateState();
    const action =
      org === null
        ? `https://github.com/settings/apps/new?state=${state}`
        : `https://github.com/organizations/${org}/settings/apps/new?state=${state}`;
    const manifest = {
      name: `Scout (${login})`,
      url: HOMEPAGE,
      hook_attributes: { url: HOMEPAGE, active: false },
      redirect_url: `http://127.0.0.1:${port}${CALLBACK_PATH}`,
      public: false,
      default_permissions: BOT_PERMISSIONS,
      default_events: [],
    };

    const abort = new AbortController();
    this.creating = abort;
    this.lastError = null;
    const callback = deps.awaitCallback({
      expectedState: state,
      port,
      hostname: '127.0.0.1',
      timeoutMs: deps.createTimeoutMs ?? CREATE_TIMEOUT_MS,
      logger: deps.logger,
      callbackPath: CALLBACK_PATH,
      pages: { '/': manifestPage(action, JSON.stringify(manifest)) },
      signal: abort.signal,
    });
    callback.catch(() => {});
    deps.logger.info('github-bot: opening the App creation page', { port, target: req.target });
    try {
      await deps.openExternal(`http://127.0.0.1:${port}/`);
    } catch {
      abort.abort();
      this.creating = null;
      throw new ScoutError(
        'github-bot/browser-failed',
        'Could not open the browser to create the bot.',
      );
    }
    this.flow = this.finishCreate(callback, abort);
    return this.toStatus(null);
  }

  /** Resolves when the creation waiting for GitHub has finished, either way. */
  settled(): Promise<void> {
    return this.flow;
  }

  async cancel(): Promise<GitHubBotStatus> {
    this.creating?.abort();
    await this.flow;
    return this.toStatus(await this.load());
  }

  /** Forgets the bot: its token file and its private key. The App itself stays on GitHub until the owner deletes it there. */
  async remove(): Promise<GitHubBotStatus> {
    this.creating?.abort();
    await this.flow;
    return this.serial(async () => {
      const bot = await this.load();
      await this.removeTokenFile();
      await this.deps.vault.delete(BOT_KEY);
      this.repositories = [];
      this.lastError = null;
      this.appReadFailed = false;
      this.deps.logger.info('github-bot: removed');
      if (bot !== null) this.deps.onChanged();
      return this.toStatus(null);
    });
  }

  async setWrites(writes: readonly unknown[]): Promise<GitHubBotStatus> {
    const chosen = GITHUB_BOT_WRITES.filter((w) => writes.includes(w));
    if (chosen.length !== writes.length) {
      throw new ScoutError(
        'github-bot/invalid-writes',
        'Scout does not know one of those actions.',
      );
    }
    return this.serial(async () => {
      const bot = await this.load();
      if (bot === null) throw new ScoutError('github-bot/none', 'There is no Scout bot.');
      await this.save({ ...bot, writes: chosen });
      this.deps.logger.info('github-bot: writes switched', { writes: chosen.join(',') });
      this.deps.onChanged();
      return this.toStatus({ ...bot, writes: chosen });
    });
  }

  refreshIfDue(): Promise<void> {
    return this.serial(() => this.refreshNow());
  }

  private async finishCreate(
    callback: ReturnType<typeof awaitCallback>,
    abort: AbortController,
  ): Promise<void> {
    const { deps } = this;
    try {
      const { code } = await callback;
      const bot = await this.convert(code);
      if (abort.signal.aborted) return;
      await this.serial(() => this.save(bot));
      deps.logger.info('github-bot: created', { slug: bot.slug, accountType: bot.accountType });
      deps.onChanged();
    } catch (err: unknown) {
      if (abort.signal.aborted) return;
      this.lastError = createError(err);
      deps.logger.warn('github-bot: creation failed', { code: this.lastError.code });
    } finally {
      if (this.creating === abort) this.creating = null;
    }
  }

  private async convert(code: string): Promise<StoredBot> {
    const response = await this.call(
      `/app-manifests/${encodeURIComponent(code)}/conversions`,
      'POST',
      null,
    );
    if (response.status !== 201) throw httpError('github-bot/conversion-failed', response.status);
    const body = await readJson(response);
    const owner = body?.['owner'] as Record<string, unknown> | undefined;
    const appId = body?.['id'];
    const clientId = body?.['client_id'];
    const slug = body?.['slug'];
    const name = body?.['name'];
    const pem = body?.['pem'];
    const account = owner?.['login'];
    if (
      typeof appId !== 'number' ||
      typeof clientId !== 'string' ||
      typeof slug !== 'string' ||
      typeof pem !== 'string' ||
      typeof account !== 'string'
    ) {
      throw new ScoutError('github-bot/unexpected-response', 'GitHub returned an unexpected App.');
    }
    return {
      appId,
      clientId,
      slug,
      ...(typeof name === 'string' && name.length > 0 ? { name } : {}),
      account,
      accountType: owner?.['type'] === 'Organization' ? 'org' : 'user',
      privateKey: pem,
      writes: [...GITHUB_BOT_WRITES],
    };
  }

  private async refreshNow(): Promise<void> {
    const { deps } = this;
    const bot = await this.load();
    if (bot === null) return;
    if (this.token !== null && this.token.expiresAt - deps.now() > REFRESH_MARGIN_MS) return;

    const hadToken = this.token !== null;
    try {
      const jwt = appJwt(bot, deps.now());
      const installations = await this.call('/app/installations', 'GET', jwt);
      if (!installations.ok) throw httpError('github-bot/token-failed', installations.status);
      const list: unknown = await installations.json().catch(() => null);
      const first = Array.isArray(list)
        ? (list[0] as Record<string, unknown> | undefined)
        : undefined;
      const installationId = first?.['id'];
      if (typeof installationId !== 'number') {
        if (hadToken) await this.dropToken();
        return;
      }
      const minted = await this.call(
        `/app/installations/${installationId}/access_tokens`,
        'POST',
        jwt,
      );
      if (minted.status !== 201) throw httpError('github-bot/token-failed', minted.status);
      const body = await readJson(minted);
      const token = body?.['token'];
      const expiresAt = Date.parse(String(body?.['expires_at']));
      if (typeof token !== 'string' || token.length === 0 || Number.isNaN(expiresAt)) {
        throw new ScoutError(
          'github-bot/unexpected-response',
          'GitHub returned an unexpected token.',
        );
      }
      await this.writeTokenFile(token);
      this.token = { token, expiresAt };
      const account = (first?.['account'] as Record<string, unknown> | undefined) ?? {};
      this.installedOn =
        typeof account['login'] === 'string'
          ? {
              account: account['login'],
              accountType: account['type'] === 'Organization' ? 'org' : 'user',
            }
          : { account: bot.account, accountType: bot.accountType };
      const before = this.repositories;
      this.repositories = await this.listRepositories(token);
      this.lastError = null;
      deps.logger.info('github-bot: installation token refreshed', {
        repositories: this.repositories.length,
      });
      const reposChanged = hadToken && !sameNames(before, this.repositories);
      if ((!hadToken || reposChanged) && !this.starting) deps.onChanged();
    } catch (err: unknown) {
      this.lastError =
        err instanceof ScoutError
          ? err
          : new ScoutError('github-bot/token-failed', 'Scout could not get a token for the bot.');
      deps.logger.warn('github-bot: installation token not refreshed; will retry', {
        code: this.lastError.code,
      });
    }
  }

  private async rereadApp(): Promise<void> {
    const { deps } = this;
    let bot: StoredBot | null;
    try {
      bot = await this.load();
    } catch (err: unknown) {
      deps.logger.warn('github-bot: the bot was not read from the vault; keeping its slug', {
        error: errorName(err),
      });
      return;
    }
    if (bot === null) return;
    let app: Record<string, unknown> | null;
    try {
      const response = await this.call('/app', 'GET', appJwt(bot, deps.now()));
      if (!response.ok) throw httpError('github-bot/app-failed', response.status);
      app = await readJson(response);
    } catch (err: unknown) {
      this.appReadFailed = true;
      deps.logger.warn('github-bot: the App was not read; keeping its slug', {
        error: err instanceof ScoutError ? err.code : errorName(err),
      });
      return;
    }
    this.appReadFailed = false;
    const slug =
      typeof app?.['slug'] === 'string' && app['slug'].length > 0 ? app['slug'] : bot.slug;
    const name =
      typeof app?.['name'] === 'string' && app['name'].length > 0 ? app['name'] : bot.name;
    if (slug === bot.slug && name === bot.name) return;
    try {
      await this.save({ ...bot, slug, ...(name !== undefined ? { name } : {}) });
    } catch (err: unknown) {
      deps.logger.warn('github-bot: the new name and slug were not kept; keeping the last ones', {
        error: errorName(err),
      });
      return;
    }
    if (name !== bot.name) {
      deps.logger.info('github-bot: the App has another name on GitHub; following it', {
        from: bot.name ?? null,
        to: name ?? null,
      });
    }
    if (slug === bot.slug) return;
    deps.logger.info('github-bot: the App was renamed on GitHub; following it', {
      from: bot.slug,
      to: slug,
    });
    if (!this.starting) deps.onChanged();
  }

  private async rereadRepositories(): Promise<void> {
    if (this.token === null) return;
    const before = this.repositories;
    this.repositories = await this.listRepositories(this.token.token);
    if (sameNames(before, this.repositories)) return;
    this.deps.logger.info('github-bot: the installation covers other repositories', {
      repositories: this.repositories.length,
    });
    this.deps.onChanged();
  }

  private async listRepositories(token: string): Promise<string[]> {
    const names: string[] = [];
    try {
      for (let page = 1; page <= REPOSITORIES_MAX_PAGES; page++) {
        const response = await this.call(
          `/installation/repositories?per_page=${REPOSITORIES_PER_PAGE}&page=${page}`,
          'GET',
          token,
        );
        const repos = response.ok ? (await readJson(response))?.['repositories'] : null;
        if (!Array.isArray(repos)) {
          this.deps.logger.warn('github-bot: repositories not listed', { status: response.status });
          return this.repositories;
        }
        for (const repo of repos) {
          const name = (repo as Record<string, unknown> | null)?.['full_name'];
          if (typeof name === 'string') names.push(name);
        }
        if (repos.length < REPOSITORIES_PER_PAGE) return names;
      }
      this.deps.logger.warn('github-bot: repositories not listed', {
        pages: REPOSITORIES_MAX_PAGES,
      });
    } catch (err: unknown) {
      this.deps.logger.warn('github-bot: repositories not listed', { error: errorName(err) });
    }
    return this.repositories;
  }

  private async call(
    path: string,
    method: 'GET' | 'POST',
    bearer: string | null,
  ): Promise<Response> {
    try {
      return await this.deps.fetch(`${API_URL}${path}`, {
        method,
        headers: {
          Accept: 'application/vnd.github+json',
          'User-Agent': 'Scout',
          'X-GitHub-Api-Version': '2022-11-28',
          ...(bearer === null ? {} : { Authorization: `Bearer ${bearer}` }),
        },
        signal: AbortSignal.timeout(HTTP_TIMEOUT_MS),
      });
    } catch (err: unknown) {
      throw new ScoutError('github-bot/network-error', 'Could not reach GitHub.', { cause: err });
    }
  }

  private async dropToken(): Promise<void> {
    await this.removeTokenFile();
    this.repositories = [];
    this.deps.logger.info('github-bot: no installation; the worker signs as the owner');
    this.deps.onChanged();
  }

  private async removeTokenFile(): Promise<void> {
    const { fs, tokenFilePath } = this.deps;
    this.token = null;
    this.installedOn = null;
    await fs.rm(tokenFilePath, { force: true });
    await fs.rm(`${tokenFilePath}.tmp`, { force: true });
  }

  private async writeTokenFile(token: string): Promise<void> {
    const { fs, tokenFilePath } = this.deps;
    const tmp = `${tokenFilePath}.tmp`;
    try {
      await fs.writeFile(tmp, JSON.stringify({ access_token: token }), { mode: 0o600 });
      await fs.rename(tmp, tokenFilePath);
    } catch (err: unknown) {
      throw new ScoutError(
        'github-bot/token-file',
        "Scout could not write the bot's token file; it will try again.",
        { cause: err },
      );
    }
  }

  private toStatus(bot: StoredBot | null): GitHubBotStatus {
    const error =
      this.lastError === null
        ? null
        : { code: this.lastError.code, message: this.lastError.message };
    if (bot === null) {
      return {
        state: this.creating !== null ? 'creating' : 'none',
        login: null,
        name: null,
        nameError: null,
        installUrl: null,
        settingsUrl: null,
        repositories: [],
        writes: [],
        error,
      };
    }
    return {
      state: this.token !== null ? 'installed' : 'created',
      login: botLogin(bot),
      name: bot.name ?? null,
      nameError: this.appReadFailed ? NAME_NOT_REFRESHED : null,
      installUrl: `https://github.com/apps/${bot.slug}/installations/new`,
      settingsUrl:
        bot.accountType === 'org'
          ? `https://github.com/organizations/${bot.account}/settings/apps/${bot.slug}`
          : `https://github.com/settings/apps/${bot.slug}`,
      repositories: this.token !== null ? [...this.repositories] : [],
      writes: bot.writes,
      error,
    };
  }

  private async load(): Promise<StoredBot | null> {
    const raw = await this.deps.vault.get(BOT_KEY);
    return raw === null ? null : (JSON.parse(raw) as StoredBot);
  }

  private async save(bot: StoredBot): Promise<void> {
    await this.deps.vault.store(BOT_KEY, JSON.stringify(bot));
  }

  private serial<T>(task: () => Promise<T>): Promise<T> {
    const next = this.queue.then(task, task);
    this.queue = next.catch(() => {});
    return next;
  }
}

/** A JSON Web Token for the App: RS256, issued 60 seconds in the past, valid 9 minutes, issued by the client id. */
export function appJwt(bot: Pick<StoredBot, 'clientId' | 'privateKey'>, nowMs: number): string {
  const now = Math.floor(nowMs / 1000);
  const header = base64Url(JSON.stringify({ alg: 'RS256', typ: 'JWT' }));
  const payload = base64Url(
    JSON.stringify({ iat: now - JWT_BACKDATE_S, exp: now + JWT_LIFETIME_S, iss: bot.clientId }),
  );
  const signature = sign('sha256', Buffer.from(`${header}.${payload}`), bot.privateKey);
  return `${header}.${payload}.${signature.toString('base64url')}`;
}

function sameNames(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && [...a].sort().join('\n') === [...b].sort().join('\n');
}

function botLogin(bot: Pick<StoredBot, 'slug'>): string {
  return `${bot.slug}[bot]`;
}

function base64Url(text: string): string {
  return Buffer.from(text).toString('base64url');
}

function manifestPage(action: string, manifest: string): string {
  return `<!DOCTYPE html><html><head><meta charset="utf-8"><title>Scout</title></head><body><p>Opening GitHub to create the Scout bot...</p><form method="post" action="${escapeHtml(action)}"><input type="hidden" name="manifest" value="${escapeHtml(manifest)}"></form><script>document.forms[0].submit();</script></body></html>`;
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function createError(err: unknown): ScoutError {
  if (err instanceof OAuthStateMismatchError) {
    return new ScoutError(
      'github-bot/state-mismatch',
      "GitHub's answer did not match this request, so Scout saved nothing. Try again.",
    );
  }
  if (err instanceof OAuthUserCancelledError) {
    return new ScoutError(
      'github-bot/timeout',
      'GitHub did not send the new App back within 5 minutes, so Scout saved nothing. Try again.',
    );
  }
  if (err instanceof ScoutError && err.code.startsWith('github-bot/')) return err;
  return new ScoutError('github-bot/create-failed', 'Scout could not create the bot. Try again.');
}

function httpError(code: string, status: number): ScoutError {
  return new ScoutError(code, `GitHub answered HTTP ${status}.`);
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
