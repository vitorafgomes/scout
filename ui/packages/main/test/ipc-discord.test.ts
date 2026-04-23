import { describe, it, expect, beforeEach } from 'bun:test';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { registerDiscordHandlers } from '../src/ipc/discord.js';
import type { DiscordFlowDeps } from '../src/oauth/discord-flow.js';
import { OAuthUserCancelledError } from '@scout/shared';
import { createLogger } from '../src/util/logger.js';

const logger = createLogger('test', { sink: () => {} });

type FakeFetch = (
  url: Parameters<typeof fetch>[0],
  init?: Parameters<typeof fetch>[1],
) => Promise<Response>;

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => Promise<unknown>;

interface FakeIpcMain extends Pick<IpcMain, 'handle' | 'removeHandler'> {
  handlers: Map<string, IpcHandler>;
}

function makeFakeIpcMain(): FakeIpcMain {
  const handlers: Map<string, IpcHandler> = new Map();
  return {
    handlers,
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
}

function invoke(ipc: FakeIpcMain, channel: string, req?: unknown): Promise<unknown> {
  const fn = ipc.handlers.get(channel);
  if (!fn) throw new Error(`No handler for ${channel}`);
  return fn(null as unknown as IpcMainInvokeEvent, req);
}

function makeGoodTokenResponse(): Response {
  return new Response(
    JSON.stringify({
      access_token: 'tok',
      guild: { id: 'g1', name: 'Server' },
    }),
    { status: 200, headers: { 'Content-Type': 'application/json' } },
  );
}

function makeDeps(overrides?: Partial<DiscordFlowDeps>): DiscordFlowDeps {
  const defaultFetch: FakeFetch = async () => makeGoodTokenResponse();
  return {
    awaitCallback: async () => ({ code: 'code', state: 'state' }),
    fetch: defaultFetch as typeof fetch,
    shell: { openExternal: async () => {} },
    logger,
    findFreePort: async () => 53682,
    ...overrides,
  };
}

describe('registerDiscordHandlers', () => {
  let ipc: FakeIpcMain;
  let unregister: () => void;

  beforeEach(() => {
    ipc = makeFakeIpcMain();
    const goodFetch: FakeFetch = async () => makeGoodTokenResponse();
    const deps = makeDeps({ fetch: goodFetch as typeof fetch });
    unregister = registerDiscordHandlers(ipc, deps, logger);
  });

  it('registers discordBuildOAuthUrl handler', () => {
    expect(ipc.handlers.has('scout:discord.buildOAuthUrl')).toBe(true);
  });

  it('registers discordStartOAuthInstall handler', () => {
    expect(ipc.handlers.has('scout:discord.startOAuthInstall')).toBe(true);
  });

  it('registers discordCancelOAuthInstall handler', () => {
    expect(ipc.handlers.has('scout:discord.cancelOAuthInstall')).toBe(true);
  });

  it('buildOAuthUrl returns url and state', async () => {
    const result = (await invoke(ipc, 'scout:discord.buildOAuthUrl', {
      clientId: 'c1',
      permissions: '8',
      scopes: ['bot', 'applications.commands'],
    })) as { url: string; state: string };
    expect(typeof result.url).toBe('string');
    expect(result.url).toContain('discord.com/oauth2/authorize');
    expect(typeof result.state).toBe('string');
  });

  it('startOAuthInstall resolves with guildId and guildName', async () => {
    const result = (await invoke(ipc, 'scout:discord.startOAuthInstall', {
      clientId: 'c1',
      permissions: '8',
    })) as { guildId: string; guildName: string };
    expect(result.guildId).toBe('g1');
    expect(result.guildName).toBe('Server');
  });

  it('cancelOAuthInstall is idempotent (no-op when nothing in flight)', async () => {
    await expect(invoke(ipc, 'scout:discord.cancelOAuthInstall')).resolves.toBeUndefined();
    await expect(invoke(ipc, 'scout:discord.cancelOAuthInstall')).resolves.toBeUndefined();
  });

  it('unregister removes all three handlers', () => {
    unregister();
    expect(ipc.handlers.has('scout:discord.buildOAuthUrl')).toBe(false);
    expect(ipc.handlers.has('scout:discord.startOAuthInstall')).toBe(false);
    expect(ipc.handlers.has('scout:discord.cancelOAuthInstall')).toBe(false);
  });

  it('startOAuthInstall propagates errors as serialized ScoutError', async () => {
    const failDeps = makeDeps({
      awaitCallback: async () => {
        throw new OAuthUserCancelledError('cancelled');
      },
    });
    const ipc2 = makeFakeIpcMain();
    registerDiscordHandlers(ipc2, failDeps, logger);
    let caught: unknown;
    try {
      await invoke(ipc2, 'scout:discord.startOAuthInstall', { clientId: 'c1', permissions: '8' });
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(Error);
    expect((caught as Error & { code?: string }).code).toBe('oauth/user-cancelled');
  });
});
