import type { IpcMain } from 'electron';
import {
  CHANNEL,
  encodeIpcErrorMessage,
  serializeError,
  type SlackManifestUrlResponse,
  type SlackSaveRequest,
  type SlackStatus,
} from '@scout/shared';
import {
  checkAppToken,
  checkBotToken,
  manifestUrl,
  requireTokenPrefix,
  type SlackIdentity,
} from '../slack/slack-setup.js';
import type { SlackTokens } from '../worker/build-env.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';

const CONNECTION_KEY = 'slack.connection';

/** Both tokens and the identity auth.test reported, stored as one vault entry so they never mismatch. */
interface SlackConnection extends SlackTokens, SlackIdentity {}

export interface SlackHandlerDeps {
  vault: Pick<Vault, 'get' | 'store' | 'delete'>;
  fetch: typeof fetch;
  onConnectionChanged(): void;
  logger: Logger;
}

/** Both Slack tokens, or null unless the owner saved them. */
export async function readSlackTokens(vault: Pick<Vault, 'get'>): Promise<SlackTokens | null> {
  const connection = await readConnection(vault);
  return connection === null
    ? null
    : { botToken: connection.botToken, appToken: connection.appToken };
}

async function readConnection(vault: Pick<Vault, 'get'>): Promise<SlackConnection | null> {
  const raw = await vault.get(CONNECTION_KEY);
  if (raw === null) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<Record<keyof SlackConnection, unknown>>;
    const { botToken, appToken, team, botUser } = parsed;
    return typeof botToken === 'string' &&
      typeof appToken === 'string' &&
      typeof team === 'string' &&
      typeof botUser === 'string'
      ? { botToken, appToken, team, botUser }
      : null;
  } catch {
    return null;
  }
}

export function registerSlackHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  deps: SlackHandlerDeps,
): () => void {
  const { vault, logger } = deps;

  async function relay<T>(name: string, call: () => Promise<T>): Promise<T> {
    try {
      return await call();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      logger.warn(`slack: ${name} failed`, { code: serialized.code });
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
  }

  async function status(): Promise<SlackStatus> {
    const connection = await readConnection(vault);
    return connection === null
      ? { connected: false, team: null, botUser: null }
      : { connected: true, team: connection.team, botUser: connection.botUser };
  }

  async function save(req: SlackSaveRequest | undefined): Promise<SlackStatus> {
    const botToken = typeof req?.botToken === 'string' ? req.botToken.trim() : '';
    const appToken = typeof req?.appToken === 'string' ? req.appToken.trim() : '';
    requireTokenPrefix('bot', botToken);
    requireTokenPrefix('app', appToken);
    const identity = await checkBotToken(deps.fetch, botToken);
    await checkAppToken(deps.fetch, appToken);
    const connection: SlackConnection = { botToken, appToken, ...identity };
    await vault.store(CONNECTION_KEY, JSON.stringify(connection));
    logger.info('slack: tokens saved');
    deps.onConnectionChanged();
    return status();
  }

  async function disconnect(): Promise<SlackStatus> {
    await vault.delete(CONNECTION_KEY);
    logger.info('slack: disconnected');
    deps.onConnectionChanged();
    return status();
  }

  ipcMain.handle(CHANNEL.slackManifestUrl, (): SlackManifestUrlResponse => ({
    url: manifestUrl(),
  }));
  ipcMain.handle(
    CHANNEL.slackSave,
    (_event, req: SlackSaveRequest | undefined): Promise<SlackStatus> =>
      relay('save', () => save(req)),
  );
  ipcMain.handle(CHANNEL.slackStatus, (): Promise<SlackStatus> => relay('status', status));
  ipcMain.handle(CHANNEL.slackDisconnect, (): Promise<SlackStatus> =>
    relay('disconnect', disconnect),
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.slackDisconnect);
    ipcMain.removeHandler(CHANNEL.slackStatus);
    ipcMain.removeHandler(CHANNEL.slackSave);
    ipcMain.removeHandler(CHANNEL.slackManifestUrl);
  };
}
