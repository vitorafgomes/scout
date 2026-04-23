import type { IpcMain } from 'electron';
import { CHANNEL, ExternalHostNotAllowedError, serializeError } from '@scout/shared';
import type { AppGetVersionResponse } from '@scout/shared';
import type { Logger } from '../util/logger.js';

const ALLOWED_HOSTS = new Set([
  'discord.com',
  'discordapp.com',
  'support-dev.discord.com',
  'github.com',
]);

function isAllowedUrl(raw: string): { ok: true; url: URL } | { ok: false; reason: string } {
  let parsed: URL;
  try {
    parsed = new URL(raw);
  } catch {
    return { ok: false, reason: 'invalid URL' };
  }

  if (parsed.protocol !== 'https:') {
    return { ok: false, reason: `protocol not allowed: ${parsed.protocol}` };
  }

  if (!ALLOWED_HOSTS.has(parsed.hostname)) {
    return { ok: false, reason: `host not in allow-list: ${parsed.hostname}` };
  }

  return { ok: true, url: parsed };
}

export function registerAppHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  shell: { openExternal(url: string): Promise<void> },
  app: { getVersion(): string; quit(): void },
  logger: Logger,
): () => void {
  ipcMain.handle(CHANNEL.appOpenExternal, async (_event, req: unknown): Promise<void> => {
    const r = req as Record<string, unknown>;
    const url = typeof r['url'] === 'string' ? r['url'] : '';

    const check = isAllowedUrl(url);
    if (!check.ok) {
      logger.warn('app: openExternal rejected', { url, reason: check.reason });
      let parsed: URL | null = null;
      try {
        parsed = new URL(url);
      } catch {
        // invalid URL — fall through with empty host
      }
      const host = parsed?.hostname ?? url;
      const err = new ExternalHostNotAllowedError(host);
      throw Object.assign(new Error(err.message), { code: err.code });
    }

    try {
      await shell.openExternal(url);
    } catch (err: unknown) {
      const serialized = serializeError(err);
      throw Object.assign(new Error(serialized.message), { code: serialized.code });
    }
  });

  ipcMain.handle(CHANNEL.appGetVersion, async (): Promise<AppGetVersionResponse> => {
    return { version: app.getVersion() };
  });

  ipcMain.handle(CHANNEL.appQuit, async (): Promise<void> => {
    app.quit();
  });

  return () => {
    ipcMain.removeHandler(CHANNEL.appQuit);
    ipcMain.removeHandler(CHANNEL.appGetVersion);
    ipcMain.removeHandler(CHANNEL.appOpenExternal);
  };
}
