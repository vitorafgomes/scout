import {
  ScoutError,
  type DiscordBotInfo,
  type DiscordEnableIntentResult,
  type DiscordGuild,
  type DiscordMessageContent,
} from '@scout/shared';

const API_URL = 'https://discord.com/api/v10';
const AUTHORIZE_URL = 'https://discord.com/oauth2/authorize';
const PORTAL_URL = 'https://discord.com/developers/applications';
const USER_AGENT = 'DiscordBot (https://github.com/vitorafgomes/scout, 1.0)';
const HTTP_TIMEOUT_MS = 15_000;

const GATEWAY_MESSAGE_CONTENT = 1n << 18n;
const GATEWAY_MESSAGE_CONTENT_LIMITED = 1n << 19n;
const SELF_SERVE_GUILD_LIMIT = 100;

const VIEW_CHANNEL = 1n << 10n;
const SEND_MESSAGES = 1n << 11n;
const EMBED_LINKS = 1n << 14n;
const READ_MESSAGE_HISTORY = 1n << 16n;
const CREATE_PUBLIC_THREADS = 1n << 35n;
const SEND_MESSAGES_IN_THREADS = 1n << 38n;

/** The channel permissions the Discord adapter needs to read a mention, open a reply thread and post and edit its reply. */
export const INVITE_PERMISSIONS = (
  VIEW_CHANNEL |
  SEND_MESSAGES |
  EMBED_LINKS |
  READ_MESSAGE_HISTORY |
  CREATE_PUBLIC_THREADS |
  SEND_MESSAGES_IN_THREADS
).toString();

type Fetch = typeof globalThis.fetch;

interface Application {
  id: string;
  name: string;
  botUsername: string;
  flags: bigint;
  guildCount: number;
}

/** The Developer Portal page where the owner switches the privileged intents by hand. */
export function portalBotUrl(applicationId: string): string {
  return `${PORTAL_URL}/${encodeURIComponent(applicationId)}/bot`;
}

/** The link that adds the bot to a server: bot scope only, so Discord needs no redirect and no secret. */
export function inviteUrl(applicationId: string): string {
  const query = new URLSearchParams({
    client_id: applicationId,
    scope: 'bot',
    permissions: INVITE_PERMISSIONS,
  });
  return `${AUTHORIZE_URL}?${query.toString()}`;
}

/** Reads the application behind a bot token. */
export async function checkBotToken(fetch: Fetch, token: string): Promise<DiscordBotInfo> {
  const app = await getApplication(fetch, token);
  return {
    applicationId: app.id,
    applicationName: app.name,
    botUsername: app.botUsername,
    messageContent: messageContentOf(app),
  };
}

/**
 * Turns on the limited Message Content intent of the bot's own application, keeping every other
 * flag. Discord only allows that for an application in under 100 servers; above that, and when
 * Discord refuses the change, the result carries the portal page where the owner does it by hand.
 */
export async function enableMessageContent(
  fetch: Fetch,
  token: string,
): Promise<DiscordEnableIntentResult> {
  const app = await getApplication(fetch, token);
  const state = messageContentOf(app);
  if (state === 'on') return { enabled: true };
  const portalUrl = portalBotUrl(app.id);
  if (state === 'privileged-100plus') {
    return {
      enabled: false,
      message:
        'Scout is in 100 or more servers, so Discord only lets you turn on Message Content Intent in the Developer Portal (Bot, Privileged Gateway Intents).',
      portalUrl,
    };
  }
  const response = await send(fetch, token, '/applications/@me', {
    method: 'PATCH',
    body: JSON.stringify({ flags: Number(app.flags | GATEWAY_MESSAGE_CONTENT_LIMITED) }),
  });
  if (response.status === 401) throw tokenRejected();
  if (!response.ok) {
    return {
      enabled: false,
      message: `Discord did not let Scout turn on Message Content Intent (HTTP ${response.status}). Turn it on in the Developer Portal (Bot, Privileged Gateway Intents).`,
      portalUrl,
    };
  }
  return { enabled: true };
}

/** The servers the bot is in. */
export async function listGuilds(fetch: Fetch, token: string): Promise<DiscordGuild[]> {
  const body = await readJson(await send(fetch, token, '/users/@me/guilds'));
  if (!Array.isArray(body)) throw unexpected();
  return body.flatMap((entry: unknown) => {
    const { id, name } = (entry ?? {}) as Record<string, unknown>;
    return typeof id === 'string' && typeof name === 'string' ? [{ id, name }] : [];
  });
}

function messageContentOf(app: Application): DiscordMessageContent {
  if ((app.flags & (GATEWAY_MESSAGE_CONTENT | GATEWAY_MESSAGE_CONTENT_LIMITED)) !== 0n) return 'on';
  return app.guildCount >= SELF_SERVE_GUILD_LIMIT ? 'privileged-100plus' : 'off';
}

async function getApplication(fetch: Fetch, token: string): Promise<Application> {
  const body = (await readJson(await send(fetch, token, '/applications/@me'))) as Record<
    string,
    unknown
  > | null;
  const id = body?.['id'];
  const name = body?.['name'];
  const bot = body?.['bot'] as Record<string, unknown> | undefined;
  const flags = body?.['flags'];
  const guildCount = body?.['approximate_guild_count'];
  if (typeof id !== 'string' || typeof name !== 'string') throw unexpected();
  if (flags !== undefined && !(typeof flags === 'number' && Number.isSafeInteger(flags))) {
    throw unexpected();
  }
  return {
    id,
    name,
    botUsername: typeof bot?.['username'] === 'string' ? bot['username'] : name,
    flags: flags === undefined ? 0n : BigInt(flags),
    guildCount: typeof guildCount === 'number' ? guildCount : 0,
  };
}

async function send(
  fetch: Fetch,
  token: string,
  path: string,
  init: { method: 'PATCH'; body: string } | null = null,
): Promise<Response> {
  try {
    return await fetch(`${API_URL}${path}`, {
      method: init?.method ?? 'GET',
      headers: {
        Authorization: `Bot ${token}`,
        'User-Agent': USER_AGENT,
        ...(init === null ? {} : { 'Content-Type': 'application/json' }),
      },
      ...(init === null ? {} : { body: init.body }),
      signal: AbortSignal.timeout(HTTP_TIMEOUT_MS),
    });
  } catch (err: unknown) {
    throw new ScoutError(
      'discord/network-error',
      'Could not reach Discord. Check the internet connection and try again.',
      { cause: err },
    );
  }
}

async function readJson(response: Response): Promise<unknown> {
  if (response.status === 401) throw tokenRejected();
  if (!response.ok) {
    throw new ScoutError('discord/request-failed', `Discord answered HTTP ${response.status}.`);
  }
  try {
    return await response.json();
  } catch {
    throw unexpected();
  }
}

function tokenRejected(): ScoutError {
  return new ScoutError('discord/token-rejected', 'Discord rejected this bot token.');
}

function unexpected(): ScoutError {
  return new ScoutError('discord/unexpected-response', 'Discord returned an unexpected response.');
}
