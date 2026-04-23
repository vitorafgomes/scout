import type { IpcMain } from 'electron';
import { CHANNEL, serializeError } from '@scout/shared';
import type {
  DiscordBuildOAuthUrlRequest,
  DiscordBuildOAuthUrlResponse,
  DiscordStartOAuthInstallRequest,
  DiscordStartOAuthInstallResponse,
} from '@scout/shared';
import { FlowController } from '../oauth/discord-flow.js';
import { buildAuthorizeUrl } from '../oauth/url-builder.js';
import { generateState } from '../oauth/state.js';
import { awaitCallback, findFreePort } from '../oauth/loopback-server.js';
import type { DiscordFlowDeps } from '../oauth/discord-flow.js';
import type { Logger } from '../util/logger.js';

export function registerDiscordHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  deps: DiscordFlowDeps,
  logger: Logger,
): () => void {
  const controller = new FlowController();

  // buildOAuthUrl is a convenience channel for dev-tools and tests.
  // The real wizard flow drives through startOAuthInstall, which
  // generates state and PKCE internally inside the FlowController.
  ipcMain.handle(
    CHANNEL.discordBuildOAuthUrl,
    async (_event, req: unknown): Promise<DiscordBuildOAuthUrlResponse> => {
      const r = req as DiscordBuildOAuthUrlRequest;
      const state = generateState();
      const url = buildAuthorizeUrl({
        clientId: r.clientId,
        permissions: r.permissions,
        redirectUri: `http://127.0.0.1:53682/callback`,
        state,
        codeChallenge: 'dev-placeholder',
      });
      return { url, state };
    },
  );

  ipcMain.handle(
    CHANNEL.discordStartOAuthInstall,
    async (_event, req: unknown): Promise<DiscordStartOAuthInstallResponse> => {
      const r = req as DiscordStartOAuthInstallRequest;
      logger.info('discord: start OAuth install', { clientId: r.clientId });
      try {
        return await controller.start({ clientId: r.clientId, permissions: r.permissions }, deps);
      } catch (err: unknown) {
        const serialized = serializeError(err);
        throw Object.assign(new Error(serialized.message), { code: serialized.code });
      }
    },
  );

  ipcMain.handle(CHANNEL.discordCancelOAuthInstall, async (): Promise<void> => {
    controller.cancel();
  });

  return () => {
    ipcMain.removeHandler(CHANNEL.discordCancelOAuthInstall);
    ipcMain.removeHandler(CHANNEL.discordStartOAuthInstall);
    ipcMain.removeHandler(CHANNEL.discordBuildOAuthUrl);
  };
}

export { awaitCallback, findFreePort };
