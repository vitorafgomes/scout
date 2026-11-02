import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type DiscordBotInfo,
  type DiscordCheckTokenRequest,
  type DiscordEnableIntentResult,
  type DiscordGuildsResponse,
  type DiscordInviteUrlResponse,
} from '@scout/shared';
import {
  checkBotToken,
  enableMessageContent,
  inviteUrl,
  listGuilds,
} from '../discord/discord-setup.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';

export interface DiscordHandlerDeps {
  fetch: typeof globalThis.fetch;
  vault: Pick<Vault, 'get' | 'store' | 'delete'>;
  /** Called after a different bot token replaced the stored one. */
  onTokenChanged(): void;
  logger: Logger;
}

/**
 * Sets up the Discord bot from its token alone. The token arrives once through checkToken, is
 * stored in the vault, and from then on is read only here and by the worker environment.
 */
export function registerDiscordHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  deps: DiscordHandlerDeps,
): () => void {
  const { fetch, vault, logger, onTokenChanged } = deps;

  async function relay<T>(name: string, call: () => Promise<T>): Promise<T> {
    try {
      return await call();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      logger.warn(`discord: ${name} failed`, { code: serialized.code });
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
  }

  async function stored(key: 'discord.bot-token' | 'discord.client-id'): Promise<string> {
    const value = await vault.get(key);
    if (value === null) {
      throw new ScoutError('discord/not-connected', 'Paste the Discord bot token first.');
    }
    return value;
  }

  ipcMain.handle(
    CHANNEL.discordCheckToken,
    (_event, req: DiscordCheckTokenRequest): Promise<DiscordBotInfo> =>
      relay('check token', async () => {
        const token = typeof req?.token === 'string' ? req.token.trim() : '';
        if (token.length === 0) {
          throw new ScoutError('discord/token-empty', 'Paste the Discord bot token.');
        }
        const info = await checkBotToken(fetch, token);
        const previousToken = await vault.get('discord.bot-token');
        const previousApplication = await vault.get('discord.client-id');
        if (previousApplication !== null && previousApplication !== info.applicationId) {
          await vault.delete('discord.guild-id');
          await vault.delete('discord.guild-name');
        }
        await vault.store('discord.bot-token', token);
        await vault.store('discord.client-id', info.applicationId);
        logger.info('discord: bot token saved', {
          applicationId: info.applicationId,
          messageContent: info.messageContent,
        });
        if (previousToken !== null && previousToken !== token) onTokenChanged();
        return info;
      }),
  );

  ipcMain.handle(CHANNEL.discordEnableIntent, (): Promise<DiscordEnableIntentResult> =>
    relay('enable intent', async () => {
      const result = await enableMessageContent(fetch, await stored('discord.bot-token'));
      logger.info('discord: message content intent', { enabled: result.enabled });
      return result;
    }),
  );

  ipcMain.handle(CHANNEL.discordInviteUrl, (): Promise<DiscordInviteUrlResponse> =>
    relay('invite url', async () => ({ url: inviteUrl(await stored('discord.client-id')) })),
  );

  ipcMain.handle(CHANNEL.discordGuilds, (): Promise<DiscordGuildsResponse> =>
    relay('guilds', async () => ({
      guilds: await listGuilds(fetch, await stored('discord.bot-token')),
    })),
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.discordGuilds);
    ipcMain.removeHandler(CHANNEL.discordInviteUrl);
    ipcMain.removeHandler(CHANNEL.discordEnableIntent);
    ipcMain.removeHandler(CHANNEL.discordCheckToken);
  };
}
