import { describe, expect, it } from 'bun:test';
import { createHash } from 'node:crypto';
import type { IpcMainInvokeEvent } from 'electron';
import { CHANNEL, readIpcError, type McpCreatedToken, type McpStatus } from '@scout/shared';
import {
  MCP_DEFAULT_PORT,
  MCP_TOKEN_PREFIX,
  loadMcpSettings,
  registerMcpHandlers,
  type McpSettingsDeps,
} from '../src/mcp/mcp-settings.js';
import type { McpServerState } from '../src/mcp/mcp-server.js';
import { McpActivity } from '../src/mcp/mcp-tools.js';
import { MAIN_EVENT, SENDERS, SENDER_HOOKS } from './sender-fakes.js';
import type { StoredVaultKey } from '../src/vault/safe-storage-vault.js';
import { recordingLogger } from './kubernetes-fakes.js';

/** A vault that fails a store, a delete or a get of the keys `failing` names. */
function fakeVault(
  initial: Record<string, string> = {},
  failing: { store?: string[]; delete?: string[]; get?: string[] } = {},
) {
  const entries = new Map(Object.entries(initial));
  const refuse = (key: string, op: keyof typeof failing) => {
    if (failing[op]?.includes(key)) throw new Error(`the vault refused to ${op} ${key}`);
  };
  return {
    entries,
    vault: {
      get: async (key: StoredVaultKey) => {
        refuse(key, 'get');
        return entries.get(key) ?? null;
      },
      store: async (key: StoredVaultKey, value: string) => {
        refuse(key, 'store');
        entries.set(key, value);
      },
      delete: async (key: StoredVaultKey) => {
        refuse(key, 'delete');
        return entries.delete(key);
      },
    } as McpSettingsDeps['vault'],
  };
}

function deps(initial: Record<string, string> = {}, failing: Parameters<typeof fakeVault>[1] = {}) {
  const { entries, vault } = fakeVault(initial, failing);
  const { logger, lines } = recordingLogger();
  let clock = 1_000;
  return {
    entries,
    lines,
    deps: { vault, logger, now: () => clock++ } satisfies McpSettingsDeps,
  };
}

const sha256 = (text: string) => createHash('sha256').update(text).digest('hex');

describe('the MCP server settings', () => {
  it('are off, ask each time, on port 47231 and hold no token by default', async () => {
    const settings = await loadMcpSettings(deps().deps);

    expect(
      settings.state(),
      'the server is off and asks before each start until the owner says otherwise',
    ).toEqual({
      enabled: false,
      startMode: 'ask',
      port: MCP_DEFAULT_PORT,
      hasToken: false,
      tokenCreatedAt: null,
    });
    expect(MCP_DEFAULT_PORT).toBe(47231);
    expect(settings.tokenHash(), 'no token exists until one is created').toBeNull();
  });

  it('are read from the vault at start, and a value the vault holds wrongly falls back to its default', async () => {
    const stored = await loadMcpSettings(
      deps({
        'mcp.enabled': 'true',
        'mcp.startMode': 'free',
        'mcp.port': '48000',
        'mcp.tokenHash': sha256('scout_mcp_x'),
        'mcp.tokenCreatedAt': '1700000000000',
      }).deps,
    );
    const wrong = await loadMcpSettings(
      deps({
        'mcp.enabled': 'yes',
        'mcp.startMode': 'always',
        'mcp.port': '70000',
        'mcp.tokenCreatedAt': 'soon',
      }).deps,
    );

    expect(stored.state()).toEqual({
      enabled: true,
      startMode: 'free',
      port: 48000,
      hasToken: true,
      tokenCreatedAt: 1700000000000,
    });
    expect(
      wrong.state(),
      'a stored value that is not one the setting takes is read as its default',
    ).toEqual({
      enabled: false,
      startMode: 'ask',
      port: MCP_DEFAULT_PORT,
      hasToken: false,
      tokenCreatedAt: null,
    });
  });

  it('create a token shown once: only its SHA-256 is kept and nothing logs the token', async () => {
    const { deps: d, entries, lines } = deps();
    const settings = await loadMcpSettings(d);

    const token = await settings.createToken();

    expect(token, 'the token is the prefix and 32 random bytes in base64url').toMatch(
      new RegExp(`^${MCP_TOKEN_PREFIX}[A-Za-z0-9_-]{43}$`),
    );
    expect(
      JSON.parse(entries.get('mcp.token') ?? '{}'),
      'the vault keeps only the SHA-256 of the token, with when it was created, in one entry',
    ).toEqual({ hash: sha256(token), createdAt: 1_000 });
    expect(
      [...entries.values()].some((value) => value.includes(token)),
      'the token itself is stored nowhere',
    ).toBe(false);
    expect(settings.tokenHash()).toBe(sha256(token));
    expect(
      JSON.stringify(settings.state()),
      'the state never carries the token or its hash',
    ).not.toContain(token.slice(MCP_TOKEN_PREFIX.length));
    expect(JSON.stringify(settings.state())).not.toContain(sha256(token));
    expect(settings.state().hasToken).toBe(true);
    expect(settings.state().tokenCreatedAt, 'the time the token was created is kept').toBe(1_000);
    expect(
      lines.some((line) => line.includes(token.slice(MCP_TOKEN_PREFIX.length))),
      'no log line holds the token',
    ).toBe(false);
  });

  it('replace the old token with a new one, and revoke removes it', async () => {
    const { deps: d, entries } = deps();
    const settings = await loadMcpSettings(d);
    const first = await settings.createToken();

    const second = await settings.createToken();
    const replaced = settings.tokenHash();
    await settings.revokeToken();

    expect(second, 'a new token is another token').not.toBe(first);
    expect(replaced, 'the new token replaces the old one').toBe(sha256(second));
    expect(settings.tokenHash(), 'a revoked token is gone').toBeNull();
    expect(settings.state()).toMatchObject({ hasToken: false, tokenCreatedAt: null });
    expect(
      [entries.has('mcp.token'), entries.has('mcp.tokenHash'), entries.has('mcp.tokenCreatedAt')],
      'revoke removes the token from the vault, in either shape',
    ).toEqual([false, false, false]);
  });

  it('carry over a token kept in the earlier two-key shape, and a new token replaces it', async () => {
    const legacy = 'scout_mcp_kept-in-the-earlier-shape';
    const { deps: d, entries } = deps({
      'mcp.tokenHash': sha256(legacy),
      'mcp.tokenCreatedAt': '1700000000000',
    });

    const settings = await loadMcpSettings(d);
    const carried = [settings.tokenHash(), settings.state().tokenCreatedAt];
    const next = await settings.createToken();
    const reopened = await loadMcpSettings(d);

    expect(carried, 'a token from the earlier shape still works').toEqual([
      sha256(legacy),
      1700000000000,
    ]);
    expect(reopened.tokenHash(), 'the new token is the one read after a restart').toBe(
      sha256(next),
    );
    expect(entries.has('mcp.token')).toBe(true);
  });

  it('leave the token before them whole when the vault refuses a write, a delete or a read', async () => {
    const before = 'scout_mcp_the-token-before';
    const kept = { 'mcp.token': JSON.stringify({ hash: sha256(before), createdAt: 5 }) };
    const refusedStore = deps(kept, {
      store: ['mcp.token', 'mcp.enabled', 'mcp.startMode', 'mcp.port'],
    });
    const storing = await loadMcpSettings(refusedStore.deps);
    const refusedDelete = deps(kept, { delete: ['mcp.token'] });
    const deleting = await loadMcpSettings(refusedDelete.deps);
    const unreadable = await loadMcpSettings(deps(kept, { get: ['mcp.token'] }).deps);

    const outcomes = await Promise.allSettled([
      storing.createToken(),
      storing.setEnabled(true),
      storing.setStartMode('free'),
      storing.setPort(48000),
      deleting.revokeToken(),
    ]);

    expect(
      outcomes.map((o) => o.status),
      'each write the vault refuses fails',
    ).toEqual(['rejected', 'rejected', 'rejected', 'rejected', 'rejected']);
    expect(
      [storing.tokenHash(), (await loadMcpSettings(refusedStore.deps)).tokenHash()],
      'a refused token write leaves the token before it, in memory and after a restart',
    ).toEqual([sha256(before), sha256(before)]);
    expect(storing.state(), 'a refused write changes nothing in memory').toMatchObject({
      enabled: false,
      startMode: 'ask',
      port: MCP_DEFAULT_PORT,
      tokenCreatedAt: 5,
    });
    expect(
      [deleting.tokenHash(), (await loadMcpSettings(refusedDelete.deps)).tokenHash()],
      'a refused revoke leaves the token valid, in memory and after a restart',
    ).toEqual([sha256(before), sha256(before)]);
    expect(unreadable.tokenHash(), 'a token the vault cannot read is no token').toBeNull();
  });

  it('keep the switch, the start mode and the port the owner sets', async () => {
    const { deps: d, entries } = deps();
    const settings = await loadMcpSettings(d);

    await settings.setEnabled(true);
    await settings.setStartMode('free');
    await settings.setPort(48100);

    expect(settings.state()).toMatchObject({ enabled: true, startMode: 'free', port: 48100 });
    expect([
      entries.get('mcp.enabled'),
      entries.get('mcp.startMode'),
      entries.get('mcp.port'),
    ]).toEqual(['true', 'free', '48100']);
  });
});

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

async function channels(failing: Parameters<typeof fakeVault>[1] = {}) {
  const d = deps({}, failing);
  const settings = await loadMcpSettings(d.deps);
  const handlers = new Map<string, IpcHandler>();
  let listener: McpServerState = { state: 'off' };
  const answered: [string, boolean][] = [];
  const pushed: McpStatus[] = [];
  const activity = new McpActivity();
  activity.record({ at: 1, tool: 'list_reviews', target: '', result: 'listed' });
  const handle = registerMcpHandlers(
    {
      handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
      removeHandler: (channel: string) => void handlers.delete(channel),
    } as unknown as Parameters<typeof registerMcpHandlers>[0],
    {
      settings,
      server: {
        state: () => listener,
        start: async () => (listener = { state: 'listening', port: settings.state().port }),
        stop: async () => void (listener = { state: 'off' }),
        pickAnotherPort: async () => (listener = { state: 'listening', port: 50_000 }),
      },
      tools: {
        answerStart: (id, start) => (answered.push([id, start]), true),
        pending: () => ({
          id: 'start-1',
          pullRequest: 'https://github.com/acme/shop/pull/12',
          askedAt: 9,
        }),
      },
      activity,
      push: (status) => pushed.push(status),
      senders: SENDER_HOOKS,
      logger: d.deps.logger,
    },
  );
  const invoke = async (event: IpcMainInvokeEvent, channel: string, ...args: unknown[]) =>
    handlers.get(channel)!(event, ...args);
  return { ...d, settings, handlers, handle, answered, pushed, invoke };
}

const CHANGES: readonly [string, string, unknown[]][] = [
  ['turn it on', CHANNEL.mcpSetEnabled, [true]],
  ['create the token', CHANNEL.mcpCreateToken, []],
  ['revoke the token', CHANNEL.mcpRevokeToken, []],
  ['set the start mode', CHANNEL.mcpSetStartMode, ['free']],
  ['pick another port', CHANNEL.mcpPickPort, []],
  ['answer a start', CHANNEL.mcpAnswerStart, [{ id: 'start-1', start: true }]],
  ['read the status', CHANNEL.mcpStatus, []],
];

describe('the MCP channels', () => {
  it('answer a vault that refuses a write with an error, push nothing and leave the settings as they were', async () => {
    const t = await channels({ store: ['mcp.token', 'mcp.enabled', 'mcp.startMode'] });

    const errors = await Promise.all(
      [CHANNEL.mcpCreateToken, CHANNEL.mcpSetEnabled, CHANNEL.mcpSetStartMode].map((channel) =>
        t.invoke(MAIN_EVENT, channel, channel === CHANNEL.mcpSetEnabled ? true : 'free').then(
          () => null,
          (err: unknown) => readIpcError(err),
        ),
      ),
    );

    expect(
      errors.every((e) => e !== null && typeof e.code === 'string'),
      'each channel answers an error the window can read',
    ).toBe(true);
    expect(
      JSON.stringify(errors),
      'the error carries no part of the vault keys or the token',
    ).not.toContain('scout_mcp_');
    expect(t.settings.state(), 'nothing changed').toEqual({
      enabled: false,
      startMode: 'ask',
      port: MCP_DEFAULT_PORT,
      hasToken: false,
      tokenCreatedAt: null,
    });
    expect(t.pushed, 'nothing is pushed for a change that failed').toEqual([]);
  });

  it('are registered for exactly the MCP channels the contract names, and the event is pushed, never invoked', async () => {
    const t = await channels();

    expect(
      [...t.handlers.keys()].sort(),
      'every MCP channel the contract names is handled',
    ).toEqual(CHANGES.map(([, channel]) => channel).sort());
    t.handle.dispose();
    expect(t.handlers.size, 'dispose removes every MCP channel').toBe(0);
  });

  for (const [from, event, kind] of SENDERS.filter(([, , k]) => k !== 'main')) {
    it(`only the main window can turn it on, create or revoke the token, or answer a start: ${from} (${kind}) is refused on every channel and changes nothing`, async () => {
      const t = await channels();

      const refusals = await Promise.all(
        CHANGES.map(([, channel, args]) =>
          t.invoke(event, channel, ...args).then(
            () => null,
            (err: unknown) => readIpcError(err),
          ),
        ),
      );

      expect(refusals, 'every MCP channel refuses a sender that is not the main window').toEqual(
        CHANGES.map(() => ({
          code: 'ipc/not-main-window',
          message: 'The MCP server can be changed only in the Scout window.',
        })),
      );
      expect(t.settings.state(), 'a refused call changes no setting').toEqual({
        enabled: false,
        startMode: 'ask',
        port: MCP_DEFAULT_PORT,
        hasToken: false,
        tokenCreatedAt: null,
      });
      expect(t.answered, 'a refused call answers no start').toEqual([]);
      expect(t.pushed, 'a refused call pushes nothing').toEqual([]);
    });
  }

  it('the main window turns it on, creates and revokes the token and answers a start; no status or event holds the token or its hash', async () => {
    const t = await channels();

    const on = (await t.invoke(MAIN_EVENT, CHANNEL.mcpSetEnabled, true)) as McpStatus;
    const created = (await t.invoke(MAIN_EVENT, CHANNEL.mcpCreateToken)) as McpCreatedToken;
    const hash = t.settings.tokenHash()!;
    const mode = (await t.invoke(MAIN_EVENT, CHANNEL.mcpSetStartMode, 'free')) as McpStatus;
    const answered = await t.invoke(MAIN_EVENT, CHANNEL.mcpAnswerStart, {
      id: 'start-1',
      start: false,
    });
    const revoked = (await t.invoke(MAIN_EVENT, CHANNEL.mcpRevokeToken)) as McpStatus;
    const off = (await t.invoke(MAIN_EVENT, CHANNEL.mcpSetEnabled, false)) as McpStatus;

    expect(on.listener, 'turning it on starts the listener').toEqual({
      state: 'listening',
      port: MCP_DEFAULT_PORT,
    });
    expect(
      [on.enabled, created.status.hasToken, mode.startMode, revoked.hasToken],
      'the main window turns it on, creates the token, sets the mode and revokes the token',
    ).toEqual([true, true, 'free', false]);
    expect(
      created.token.startsWith(MCP_TOKEN_PREFIX),
      'the token is given once, to the main window',
    ).toBe(true);
    expect([off.enabled, off.listener], 'turning it off closes the listener').toEqual([
      false,
      { state: 'off' },
    ]);
    expect(on.pending, 'the status shows the start waiting for the owner').toEqual({
      id: 'start-1',
      pullRequest: 'https://github.com/acme/shop/pull/12',
      askedAt: 9,
    });
    expect(on.activity, 'the status shows the activity list').toEqual([
      { at: 1, tool: 'list_reviews', target: '', result: 'listed' },
    ]);
    expect([answered, t.answered], 'the main window answers a start').toEqual([
      true,
      [['start-1', false]],
    ]);
    const shown = JSON.stringify([on, created.status, mode, revoked, off, t.pushed]);
    expect(shown, 'no status or event holds the token').not.toContain(created.token);
    expect(shown, 'no status or event holds its hash').not.toContain(hash);
    expect(t.pushed.length, 'each change of a setting is pushed to the main window').toBe(5);
  });
});
