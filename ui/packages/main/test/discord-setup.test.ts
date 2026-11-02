import { describe, expect, it } from 'bun:test';
import { ScoutError } from '@scout/shared';
import {
  INVITE_PERMISSIONS,
  checkBotToken,
  enableMessageContent,
  inviteUrl,
  listGuilds,
} from '../src/discord/discord-setup.js';

const TOKEN = 'bot-token-secret-value';
const MESSAGE_CONTENT = 1 << 18;
const MESSAGE_CONTENT_LIMITED = 1 << 19;
const GUILD_MEMBERS_LIMITED = 1 << 15;
const EMBEDDED = 1 << 17;

interface Call {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: string | undefined;
}

type Reply = Response | Error;

function fakeFetch(...replies: Reply[]): { fetch: typeof fetch; calls: Call[] } {
  const calls: Call[] = [];
  const fn = async (input: Parameters<typeof fetch>[0], init?: RequestInit) => {
    calls.push({
      url: String(input),
      method: init?.method ?? 'GET',
      headers: (init?.headers ?? {}) as Record<string, string>,
      body: typeof init?.body === 'string' ? init.body : undefined,
    });
    const reply = replies.shift();
    if (reply === undefined) throw new Error('unexpected extra request');
    if (reply instanceof Error) throw reply;
    return reply;
  };
  return { fetch: fn as unknown as typeof fetch, calls };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function application(flags: number, guildCount = 3): Response {
  return json({
    id: '111222333',
    name: 'Scout',
    bot: { id: '111222333', username: 'scout-bot' },
    flags,
    approximate_guild_count: guildCount,
  });
}

async function rejection(promise: Promise<unknown>): Promise<ScoutError> {
  try {
    await promise;
  } catch (err: unknown) {
    if (err instanceof ScoutError) return err;
    throw err;
  }
  throw new Error('expected a rejection');
}

describe('checkBotToken', () => {
  it('reads the application, the bot name and whether message content is on', async () => {
    const { fetch, calls } = fakeFetch(application(MESSAGE_CONTENT_LIMITED));

    const info = await checkBotToken(fetch, TOKEN);

    expect(info).toEqual({
      applicationId: '111222333',
      applicationName: 'Scout',
      botUsername: 'scout-bot',
      messageContent: 'on',
    });
    expect(calls[0]?.url).toBe('https://discord.com/api/v10/applications/@me');
    expect(calls[0]?.headers['Authorization']).toBe(`Bot ${TOKEN}`);
    expect(calls[0]?.headers['User-Agent']).toStartWith('DiscordBot (');
  });

  it('reports message content off for a bot in under 100 servers without either intent bit', async () => {
    const { fetch } = fakeFetch(application(EMBEDDED, 99));

    expect((await checkBotToken(fetch, TOKEN)).messageContent).toBe('off');
  });

  it('reports privileged-100plus for a bot in 100 or more servers without the intent', async () => {
    const { fetch } = fakeFetch(application(0, 100));

    expect((await checkBotToken(fetch, TOKEN)).messageContent).toBe('privileged-100plus');
  });

  it('says Discord rejected this bot token on 401', async () => {
    const { fetch } = fakeFetch(json({ message: '401: Unauthorized', code: 0 }, 401));

    const err = await rejection(checkBotToken(fetch, TOKEN));

    expect(err.code).toBe('discord/token-rejected');
    expect(err.message).toBe('Discord rejected this bot token.');
  });

  it('raises a network error when Discord cannot be reached', async () => {
    const { fetch } = fakeFetch(new TypeError('fetch failed'));

    const err = await rejection(checkBotToken(fetch, TOKEN));

    expect(err.code).toBe('discord/network-error');
    expect(err.message).not.toContain(TOKEN);
  });
});

describe('enableMessageContent', () => {
  it('sends PATCH with the old flags plus the limited message content bit, keeping the other bits', async () => {
    const before = EMBEDDED | GUILD_MEMBERS_LIMITED;
    const { fetch, calls } = fakeFetch(
      application(before),
      application(before | MESSAGE_CONTENT_LIMITED),
    );

    const result = await enableMessageContent(fetch, TOKEN);

    expect(result).toEqual({ enabled: true });
    expect(calls).toHaveLength(2);
    expect(calls[1]?.method).toBe('PATCH');
    expect(calls[1]?.url).toBe('https://discord.com/api/v10/applications/@me');
    expect(JSON.parse(calls[1]?.body ?? '{}')).toEqual({
      flags: EMBEDDED | GUILD_MEMBERS_LIMITED | MESSAGE_CONTENT_LIMITED,
    });
  });

  it('does not PATCH when message content is already on', async () => {
    const { fetch, calls } = fakeFetch(application(MESSAGE_CONTENT));

    const result = await enableMessageContent(fetch, TOKEN);

    expect(result).toEqual({ enabled: true });
    expect(calls.map((c) => c.method)).toEqual(['GET']);
  });

  it('returns the Developer Portal bot page and does not PATCH for a bot in 100 or more servers', async () => {
    const { fetch, calls } = fakeFetch(application(0, 150));

    const result = await enableMessageContent(fetch, TOKEN);

    expect(result.enabled).toBe(false);
    expect(result).toMatchObject({
      portalUrl: 'https://discord.com/developers/applications/111222333/bot',
    });
    expect(calls.map((c) => c.method)).toEqual(['GET']);
  });

  it('returns a clear message with the portal link when Discord refuses the PATCH with 403', async () => {
    const { fetch } = fakeFetch(application(0), json({ message: 'Missing Access' }, 403));

    const result = await enableMessageContent(fetch, TOKEN);

    expect(result).toEqual({
      enabled: false,
      message:
        'Discord did not let Scout turn on Message Content Intent (HTTP 403). Turn it on in the Developer Portal (Bot, Privileged Gateway Intents).',
      portalUrl: 'https://discord.com/developers/applications/111222333/bot',
    });
  });

  it('raises a network error when the PATCH cannot reach Discord', async () => {
    const { fetch } = fakeFetch(application(0), new TypeError('fetch failed'));

    const err = await rejection(enableMessageContent(fetch, TOKEN));

    expect(err.code).toBe('discord/network-error');
  });
});

describe('inviteUrl', () => {
  it('builds the bot-scope invite with the exact permissions integer and no redirect or code flow', () => {
    expect(INVITE_PERMISSIONS).toBe('309237730304');
    expect(inviteUrl('111222333')).toBe(
      'https://discord.com/oauth2/authorize?client_id=111222333&scope=bot&permissions=309237730304',
    );
  });
});

describe('listGuilds', () => {
  it('returns the id and name of every server the bot is in', async () => {
    const { fetch, calls } = fakeFetch(
      json([
        { id: '1', name: 'Team', icon: null },
        { id: '2', name: 'Ops', icon: null },
      ]),
    );

    expect(await listGuilds(fetch, TOKEN)).toEqual([
      { id: '1', name: 'Team' },
      { id: '2', name: 'Ops' },
    ]);
    expect(calls[0]?.url).toBe('https://discord.com/api/v10/users/@me/guilds');
  });

  it('raises a network error when the guild list cannot reach Discord', async () => {
    const { fetch } = fakeFetch(new TypeError('fetch failed'));

    expect((await rejection(listGuilds(fetch, TOKEN))).code).toBe('discord/network-error');
  });

  it('says Discord rejected this bot token on 401', async () => {
    const { fetch } = fakeFetch(json({ message: '401: Unauthorized' }, 401));

    expect((await rejection(listGuilds(fetch, TOKEN))).message).toBe(
      'Discord rejected this bot token.',
    );
  });
});
