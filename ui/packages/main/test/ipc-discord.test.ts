import { describe, it, expect } from 'bun:test';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { readIpcError } from '@scout/shared';
import { registerDiscordHandlers } from '../src/ipc/discord.js';
import { createLogger } from '../src/util/logger.js';

const TOKEN = 'MTEx.discord-bot-token.secret';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => Promise<unknown>;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

const APPLICATION = {
  id: '111222333',
  name: 'Scout',
  bot: { username: 'scout-bot' },
  flags: 0,
  approximate_guild_count: 1,
};

function setup(routes: Record<string, () => Response>) {
  const handlers = new Map<string, IpcHandler>();
  const ipc: Pick<IpcMain, 'handle' | 'removeHandler'> = {
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  } as Pick<IpcMain, 'handle' | 'removeHandler'>;
  const vault = new Map<string, string>();
  const logLines: string[] = [];
  const requested: { url: string; authorization: string | undefined }[] = [];
  const tokenChanges: number[] = [];
  const fetch = async (input: Parameters<typeof globalThis.fetch>[0], init?: RequestInit) => {
    const url = String(input);
    requested.push({
      url,
      authorization: (init?.headers as Record<string, string> | undefined)?.['Authorization'],
    });
    const route = routes[`${init?.method ?? 'GET'} ${url}`];
    if (route === undefined) throw new TypeError('fetch failed');
    return route();
  };
  const unregister = registerDiscordHandlers(ipc, {
    fetch: fetch as typeof globalThis.fetch,
    vault: {
      get: async (key) => vault.get(key) ?? null,
      store: async (key, value) => {
        vault.set(key, value);
      },
      delete: async (key) => vault.delete(key),
    },
    onTokenChanged: () => tokenChanges.push(1),
    logger: createLogger('test', { level: 'debug', sink: (line) => logLines.push(line) }),
  });
  const invoke = (channel: string, req?: unknown): Promise<unknown> => {
    const fn = handlers.get(channel);
    if (fn === undefined) throw new Error(`No handler for ${channel}`);
    return fn(null as unknown as IpcMainInvokeEvent, req);
  };
  return { handlers, vault, logLines, requested, tokenChanges, invoke, unregister };
}

async function failure(
  promise: Promise<unknown>,
): Promise<{ code: string | null; message: string }> {
  try {
    await promise;
  } catch (err: unknown) {
    return readIpcError(err);
  }
  throw new Error('expected a rejection');
}

const APP_URL = 'https://discord.com/api/v10/applications/@me';
const GUILDS_URL = 'https://discord.com/api/v10/users/@me/guilds';

describe('registerDiscordHandlers', () => {
  it('checkToken stores the bot token and the application id and returns the bot without the token', async () => {
    const ctx = setup({ [`GET ${APP_URL}`]: () => json(APPLICATION) });

    const info = await ctx.invoke('scout:discord.checkToken', { token: `  ${TOKEN}\n` });

    expect(info).toEqual({
      applicationId: '111222333',
      applicationName: 'Scout',
      botUsername: 'scout-bot',
      messageContent: 'off',
    });
    expect(ctx.vault.get('discord.bot-token')).toBe(TOKEN);
    expect(ctx.vault.get('discord.client-id')).toBe('111222333');
  });

  it('checkToken asks for a worker restart when a different token replaces the stored one', async () => {
    const ctx = setup({ [`GET ${APP_URL}`]: () => json(APPLICATION) });
    ctx.vault.set('discord.bot-token', 'old-token');

    await ctx.invoke('scout:discord.checkToken', { token: TOKEN });
    await ctx.invoke('scout:discord.checkToken', { token: TOKEN });

    expect(ctx.tokenChanges).toHaveLength(1);
  });

  it('checkToken asks for no restart on the first token', async () => {
    const ctx = setup({ [`GET ${APP_URL}`]: () => json(APPLICATION) });

    await ctx.invoke('scout:discord.checkToken', { token: TOKEN });

    expect(ctx.tokenChanges).toHaveLength(0);
  });

  it('checkToken forgets the stored server when the token belongs to another application', async () => {
    const ctx = setup({ [`GET ${APP_URL}`]: () => json(APPLICATION) });
    ctx.vault.set('discord.client-id', '999');
    ctx.vault.set('discord.guild-id', '42');
    ctx.vault.set('discord.guild-name', 'Old Team');

    await ctx.invoke('scout:discord.checkToken', { token: TOKEN });

    expect(ctx.vault.has('discord.guild-id')).toBe(false);
    expect(ctx.vault.has('discord.guild-name')).toBe(false);
    expect(ctx.vault.get('discord.client-id')).toBe('111222333');
  });

  it('checkToken keeps the stored server when the token belongs to the same application', async () => {
    const ctx = setup({ [`GET ${APP_URL}`]: () => json(APPLICATION) });
    ctx.vault.set('discord.client-id', '111222333');
    ctx.vault.set('discord.guild-id', '42');
    ctx.vault.set('discord.guild-name', 'Team');

    await ctx.invoke('scout:discord.checkToken', { token: TOKEN });

    expect(ctx.vault.get('discord.guild-id')).toBe('42');
    expect(ctx.vault.get('discord.guild-name')).toBe('Team');
  });

  it('checkToken stores nothing and says Discord rejected this bot token on 401', async () => {
    const ctx = setup({ [`GET ${APP_URL}`]: () => json({ message: '401: Unauthorized' }, 401) });

    const err = await failure(ctx.invoke('scout:discord.checkToken', { token: TOKEN }));

    expect(err).toEqual({
      code: 'discord/token-rejected',
      message: 'Discord rejected this bot token.',
    });
    expect(ctx.vault.size).toBe(0);
  });

  it('checkToken reports a network error when Discord is unreachable', async () => {
    const ctx = setup({});

    const err = await failure(ctx.invoke('scout:discord.checkToken', { token: TOKEN }));

    expect(err.code).toBe('discord/network-error');
    expect(ctx.vault.size).toBe(0);
  });

  it('enableIntent turns message content on with the stored token, not one from the renderer', async () => {
    const ctx = setup({
      [`GET ${APP_URL}`]: () => json(APPLICATION),
      [`PATCH ${APP_URL}`]: () => json({ ...APPLICATION, flags: 1 << 19 }),
    });
    ctx.vault.set('discord.bot-token', TOKEN);

    const result = await ctx.invoke('scout:discord.enableIntent', { token: 'from-renderer' });

    expect(result).toEqual({ enabled: true });
    expect(ctx.requested.map((r) => r.authorization)).toEqual([`Bot ${TOKEN}`, `Bot ${TOKEN}`]);
  });

  it('enableIntent reports a network error when the PATCH cannot reach Discord', async () => {
    const ctx = setup({ [`GET ${APP_URL}`]: () => json(APPLICATION) });
    ctx.vault.set('discord.bot-token', TOKEN);

    const err = await failure(ctx.invoke('scout:discord.enableIntent'));

    expect(err.code).toBe('discord/network-error');
  });

  it('guilds lists the servers the stored bot is in', async () => {
    const ctx = setup({ [`GET ${GUILDS_URL}`]: () => json([{ id: '42', name: 'Team' }]) });
    ctx.vault.set('discord.bot-token', TOKEN);

    expect(await ctx.invoke('scout:discord.guilds')).toEqual({
      guilds: [{ id: '42', name: 'Team' }],
    });
  });

  it('guilds reports a network error when Discord is unreachable', async () => {
    const ctx = setup({});
    ctx.vault.set('discord.bot-token', TOKEN);

    expect((await failure(ctx.invoke('scout:discord.guilds'))).code).toBe('discord/network-error');
  });

  it('inviteUrl builds the invite from the stored application id', async () => {
    const ctx = setup({});
    ctx.vault.set('discord.client-id', '111222333');

    expect(await ctx.invoke('scout:discord.inviteUrl')).toEqual({
      url: 'https://discord.com/oauth2/authorize?client_id=111222333&scope=bot&permissions=309237730304',
    });
  });

  it('the token never leaves main except to Discord: not in a response, an error or a log line', async () => {
    const ctx = setup({
      [`GET ${APP_URL}`]: () => json(APPLICATION),
      [`PATCH ${APP_URL}`]: () => json({ message: 'Missing Access' }, 403),
      [`GET ${GUILDS_URL}`]: () => json({ message: '401: Unauthorized' }, 401),
    });

    const outputs: unknown[] = [];
    outputs.push(await ctx.invoke('scout:discord.checkToken', { token: TOKEN }));
    outputs.push(await ctx.invoke('scout:discord.enableIntent'));
    outputs.push(await ctx.invoke('scout:discord.inviteUrl'));
    outputs.push(await failure(ctx.invoke('scout:discord.guilds')));

    expect(JSON.stringify(outputs)).not.toContain(TOKEN);
    expect(ctx.logLines.length).toBeGreaterThan(0);
    expect(ctx.logLines.filter((line) => line.includes(TOKEN))).toEqual([]);
    expect(ctx.requested.length).toBe(4);
    expect(ctx.requested.filter((r) => new URL(r.url).hostname !== 'discord.com')).toEqual([]);
  });

  it('unregister removes every Discord handler', () => {
    const ctx = setup({});

    ctx.unregister();

    expect([...ctx.handlers.keys()].filter((c) => c.startsWith('scout:discord.'))).toEqual([]);
  });
});
