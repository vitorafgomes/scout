import { createHash, randomBytes } from 'node:crypto';
import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type McpAnswerStartRequest,
  type McpCreatedToken,
  type McpStartMode,
  type McpStatus,
} from '@scout/shared';
import {
  guardChannels,
  senderCheck,
  type ChannelGuard,
  type SenderHooks,
} from '../ipc/sender-check.js';
import type { McpServer } from './mcp-server.js';
import type { McpActivity, McpReviewTools } from './mcp-tools.js';
import type { Logger } from '../util/logger.js';
import type { StoredVaultKey, Vault } from '../vault/safe-storage-vault.js';

/** The port the MCP server listens on until the owner picks another. */
export const MCP_DEFAULT_PORT = 47231;

/** The start of every token, so a token found in a file is recognised as Scout's. */
export const MCP_TOKEN_PREFIX = 'scout_mcp_';

/** What the settings show: never the token nor its hash. */
export interface McpSettingsState {
  readonly enabled: boolean;
  readonly startMode: McpStartMode;
  readonly port: number;
  readonly hasToken: boolean;
  readonly tokenCreatedAt: number | null;
}

export interface McpSettingsDeps {
  vault: Pick<Vault, 'get' | 'store' | 'delete'>;
  logger: Logger;
  now(): number;
}

const KEY = {
  enabled: 'mcp.enabled',
  startMode: 'mcp.startMode',
  port: 'mcp.port',
  token: 'mcp.token',
  tokenHash: 'mcp.tokenHash',
  tokenCreatedAt: 'mcp.tokenCreatedAt',
} as const satisfies Record<string, StoredVaultKey>;

/**
 * The token as the vault keeps it: its hash and when it was created, in one entry, so a write that fails leaves the
 * token before it whole. A vault from before this entry keeps them under two keys, read when the entry is absent.
 */
function readToken(
  entry: string | null,
  hash: string | null,
  createdAt: string | null,
): { hash: string; createdAt: number | null } | null {
  if (entry !== null) {
    try {
      const kept = JSON.parse(entry) as { hash?: unknown; createdAt?: unknown };
      return typeof kept.hash === 'string' && /^[0-9a-f]{64}$/.test(kept.hash)
        ? { hash: kept.hash, createdAt: typeof kept.createdAt === 'number' ? kept.createdAt : null }
        : null;
    } catch {
      return null;
    }
  }

  return hash !== null && /^[0-9a-f]{64}$/.test(hash)
    ? { hash, createdAt: /^\d+$/.test(createdAt ?? '') ? Number(createdAt) : null }
    : null;
}

/** The SHA-256 of a token, in lowercase hex: the only form of a token Scout keeps. */
export function hashToken(token: string): string {
  return createHash('sha256').update(token).digest('hex');
}

/** The settings of the MCP server, kept in the vault: the switch, the start mode, the port and the token's hash. */
export interface McpSettings {
  state(): McpSettingsState;
  /** The SHA-256 of the token, or null when none exists. */
  tokenHash(): string | null;
  setEnabled(enabled: boolean): Promise<void>;
  setStartMode(mode: McpStartMode): Promise<void>;
  setPort(port: number): Promise<void>;
  /** Creates a token, replacing any earlier one, and answers it: the only time it is given. */
  createToken(): Promise<string>;
  revokeToken(): Promise<void>;
}

/** Reads the settings from the vault; a stored value the setting does not take is read as its default. */
export async function loadMcpSettings(deps: McpSettingsDeps): Promise<McpSettings> {
  const { vault, logger, now } = deps;
  const read = (key: StoredVaultKey) => vault.get(key).catch(() => null);
  const [enabled, startMode, port, entry, hash, createdAt] = await Promise.all([
    read(KEY.enabled),
    read(KEY.startMode),
    read(KEY.port),
    read(KEY.token),
    read(KEY.tokenHash),
    read(KEY.tokenCreatedAt),
  ]);
  const kept = readToken(entry, hash, createdAt);
  let state: Omit<McpSettingsState, 'hasToken'> = {
    enabled: enabled === 'true',
    startMode: startMode === 'free' ? 'free' : 'ask',
    port: validPort(port) ?? MCP_DEFAULT_PORT,
    tokenCreatedAt: kept?.createdAt ?? null,
  };
  let tokenHash = kept?.hash ?? null;
  const store = (key: StoredVaultKey, value: string) => vault.store(key, value);

  return {
    state: () => ({ ...state, hasToken: tokenHash !== null }),
    tokenHash: () => tokenHash,
    async setEnabled(on) {
      await store(KEY.enabled, String(on));
      state = { ...state, enabled: on };
    },
    async setStartMode(mode) {
      await store(KEY.startMode, mode);
      state = { ...state, startMode: mode };
    },
    async setPort(value) {
      if (validPort(String(value)) === null)
        throw new RangeError('The port must be from 1 to 65535.');
      await store(KEY.port, String(value));
      state = { ...state, port: value };
    },
    async createToken() {
      const token = MCP_TOKEN_PREFIX + randomBytes(32).toString('base64url');
      const created = now();
      const nextHash = hashToken(token);
      await store(KEY.token, JSON.stringify({ hash: nextHash, createdAt: created }));
      tokenHash = nextHash;
      state = { ...state, tokenCreatedAt: created };
      logger.info('mcp: a token was created; the one before it, if any, no longer works');
      return token;
    },
    async revokeToken() {
      await vault.delete(KEY.tokenCreatedAt);
      await vault.delete(KEY.tokenHash);
      await vault.delete(KEY.token);
      tokenHash = null;
      state = { ...state, tokenCreatedAt: null };
      logger.info('mcp: the token was revoked');
    },
  };
}

function validPort(text: string | null): number | null {
  if (text === null || !/^\d{1,5}$/.test(text)) return null;
  const port = Number(text);
  return port >= 1 && port <= 65535 ? port : null;
}

const MAIN_WINDOW_ONLY: ChannelGuard = {
  allowed: ['main'],
  refusal: () =>
    new ScoutError(
      'ipc/not-main-window',
      'The MCP server can be changed only in the Scout window.',
    ),
};

const MCP_CHANNELS = [
  CHANNEL.mcpStatus,
  CHANNEL.mcpSetEnabled,
  CHANNEL.mcpCreateToken,
  CHANNEL.mcpRevokeToken,
  CHANNEL.mcpSetStartMode,
  CHANNEL.mcpPickPort,
  CHANNEL.mcpAnswerStart,
] as const;

export interface McpHandlerDeps {
  settings: McpSettings;
  server: Pick<McpServer, 'state' | 'start' | 'stop' | 'pickAnotherPort'>;
  tools: Pick<McpReviewTools, 'answerStart' | 'pending'>;
  activity: Pick<McpActivity, 'list'>;
  /** Sends the status to the Scout window. */
  push(status: McpStatus): void;
  senders: SenderHooks;
  logger: Logger;
}

export interface McpHandle {
  /** What the Scout window shows: never the token nor its hash. */
  status(): McpStatus;
  /** Pushes the status after a change made outside these channels: a start waiting, an answer, a call listed. */
  changed(): void;
  dispose(): void;
}

/** The MCP channels, every one answered only for the main window, and the status they give back. */
export function registerMcpHandlers(
  ownIpcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  deps: McpHandlerDeps,
): McpHandle {
  const { settings, server, tools, activity, push, logger } = deps;
  const ipcMain = guardChannels(
    ownIpcMain,
    senderCheck(deps.senders, logger),
    Object.fromEntries(MCP_CHANNELS.map((channel) => [channel, MAIN_WINDOW_ONLY])),
  );

  const status = (): McpStatus => ({
    ...settings.state(),
    listener: server.state(),
    pending: tools.pending(),
    activity: activity.list(),
  });

  const changed = () => push(status());

  async function relay<T>(name: string, call: () => Promise<T>): Promise<T> {
    try {
      return await call();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      logger.warn(`mcp: ${name} failed`, { code: serialized.code });
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), { code: serialized.code });
    }
  }

  const change = (name: string, apply: () => Promise<unknown>) =>
    relay(name, async () => {
      await apply();
      changed();
      return status();
    });

  ipcMain.handle(CHANNEL.mcpStatus, () => status());
  ipcMain.handle(CHANNEL.mcpSetEnabled, (_event, enabled: unknown) =>
    change('setEnabled', async () => {
      await settings.setEnabled(enabled === true);
      await (enabled === true ? server.start() : server.stop());
    }),
  );
  ipcMain.handle(CHANNEL.mcpCreateToken, () =>
    relay('createToken', async (): Promise<McpCreatedToken> => {
      const token = await settings.createToken();
      changed();
      return { token, status: status() };
    }),
  );
  ipcMain.handle(CHANNEL.mcpRevokeToken, () => change('revokeToken', () => settings.revokeToken()));
  ipcMain.handle(CHANNEL.mcpSetStartMode, (_event, mode: unknown) =>
    change('setStartMode', () => settings.setStartMode(mode === 'free' ? 'free' : 'ask')),
  );
  ipcMain.handle(CHANNEL.mcpPickPort, () => change('pickPort', () => server.pickAnotherPort()));
  ipcMain.handle(CHANNEL.mcpAnswerStart, (_event, req: McpAnswerStartRequest | undefined) =>
    tools.answerStart(String(req?.id ?? ''), req?.start === true),
  );

  return {
    status,
    changed,
    dispose: () => MCP_CHANNELS.forEach((channel) => ipcMain.removeHandler(channel)),
  };
}
