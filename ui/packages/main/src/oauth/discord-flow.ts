import {
  OAuthUserCancelledError,
  DiscordTokenExchangeError,
  type DiscordOAuthResult,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';
import { generateState, generatePkcePair } from './state.js';
import { buildAuthorizeUrl } from './url-builder.js';
import { awaitCallback, findFreePort } from './loopback-server.js';
import type { LoopbackResult } from './loopback-server.js';

const PREFERRED_PORT = 53682;
const PORT_FALLBACKS = 8;
const TOKEN_ENDPOINT = 'https://discord.com/api/oauth2/token';
const TOKEN_TIMEOUT_MS = 10_000;

export interface DiscordFlowDeps {
  awaitCallback: typeof awaitCallback;
  fetch: typeof globalThis.fetch;
  shell: { openExternal(url: string): Promise<void> };
  logger: Logger;
  findFreePort: typeof findFreePort;
}

export interface DiscordFlowConfig {
  clientId: string;
  clientSecret?: string;
  permissions: string;
  preferredPort?: number;
  timeoutMs?: number;
}

export interface FlowContext {
  abort: () => void;
}

interface DiscordTokenResponse {
  access_token: string;
  guild?: {
    id: string;
    name: string;
  };
  [key: string]: unknown;
}

interface DiscordErrorResponse {
  error?: string;
  error_description?: string;
  [key: string]: unknown;
}

async function exchangeCode(
  code: string,
  redirectUri: string,
  config: DiscordFlowConfig,
  verifier: string,
  fetch: DiscordFlowDeps['fetch'],
): Promise<DiscordOAuthResult> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), TOKEN_TIMEOUT_MS);

  const body = new URLSearchParams({
    client_id: config.clientId,
    grant_type: 'authorization_code',
    code,
    redirect_uri: redirectUri,
    code_verifier: verifier,
  });

  if (config.clientSecret !== undefined && config.clientSecret.length > 0) {
    body.set('client_secret', config.clientSecret);
  }

  let resp: Response;
  try {
    resp = await fetch(TOKEN_ENDPOINT, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: body.toString(),
      signal: controller.signal,
    });
  } catch (err: unknown) {
    clearTimeout(timer);
    if (err instanceof Error && err.name === 'AbortError') {
      throw new DiscordTokenExchangeError('timeout', 'Token exchange timed out');
    }
    throw new DiscordTokenExchangeError(
      'network_error',
      err instanceof Error ? err.message : 'Network error during token exchange',
    );
  } finally {
    clearTimeout(timer);
  }

  const json = (await resp.json()) as DiscordTokenResponse | DiscordErrorResponse;

  if (!resp.ok) {
    const errJson = json as DiscordErrorResponse;
    const reason = errJson['error'] ?? String(resp.status);
    const description = errJson['error_description'] ?? `HTTP ${resp.status}`;
    throw new DiscordTokenExchangeError(reason, description);
  }

  const tokenJson = json as DiscordTokenResponse;

  if (tokenJson['guild'] === undefined || tokenJson['guild'] === null) {
    throw new DiscordTokenExchangeError(
      'missing_guild',
      'Discord token response did not include guild info. Ensure the bot install flow includes the guild scope.',
    );
  }

  const { id: guildId, name: guildName } = tokenJson['guild'];
  if (typeof guildId !== 'string' || typeof guildName !== 'string') {
    throw new DiscordTokenExchangeError(
      'invalid_guild',
      'Discord guild info is malformed (id or name not a string)',
    );
  }

  return { guildId, guildName };
}

export async function runDiscordInstall(
  config: DiscordFlowConfig,
  deps: DiscordFlowDeps,
  ctx: FlowContext,
): Promise<DiscordOAuthResult> {
  const { logger } = deps;
  const { verifier, challenge } = generatePkcePair();
  const state = generateState();

  const port = await deps.findFreePort(config.preferredPort ?? PREFERRED_PORT, PORT_FALLBACKS);
  const redirectUri = `http://127.0.0.1:${port}/callback`;

  const authorizeUrl = buildAuthorizeUrl({
    clientId: config.clientId,
    permissions: config.permissions,
    redirectUri,
    state,
    codeChallenge: challenge,
  });

  let abortLoopback: (() => void) | undefined;

  const loopbackPromise = new Promise<LoopbackResult>((resolve, reject) => {
    abortLoopback = () =>
      reject(new OAuthUserCancelledError('OAuth install cancelled by user request'));

    deps
      .awaitCallback({
        expectedState: state,
        port,
        hostname: '127.0.0.1',
        timeoutMs: config.timeoutMs ?? 300_000,
        logger,
      })
      .then(resolve, reject);
  });

  ctx.abort = () => {
    abortLoopback?.();
  };

  logger.info('discord-flow: opening authorize URL', { port });

  try {
    await deps.shell.openExternal(authorizeUrl);
  } catch (err: unknown) {
    abortLoopback?.();
    throw err;
  }

  const result = await loopbackPromise;

  return exchangeCode(result.code, redirectUri, config, verifier, deps.fetch);
}

export class FlowController {
  private _ctx: FlowContext | null = null;

  async start(config: DiscordFlowConfig, deps: DiscordFlowDeps): Promise<DiscordOAuthResult> {
    if (this._ctx !== null) {
      throw new DiscordTokenExchangeError(
        'already_in_progress',
        'A Discord OAuth install is already in progress. Cancel the current one before starting another.',
      );
    }

    const ctx: FlowContext = { abort: () => {} };
    this._ctx = ctx;

    try {
      return await runDiscordInstall(config, deps, ctx);
    } finally {
      this._ctx = null;
    }
  }

  cancel(): void {
    if (this._ctx === null) return;
    this._ctx.abort();
  }
}
