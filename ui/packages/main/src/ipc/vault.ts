import type { IpcMain } from 'electron';
import { CHANNEL, VAULT_KEYS, serializeError } from '@scout/shared';
import type {
  VaultKey,
  VaultHasResponse,
  VaultDeleteResponse,
  VaultIsAvailableResponse,
} from '@scout/shared';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';

function isKnownVaultKey(key: unknown): key is VaultKey {
  return typeof key === 'string' && (VAULT_KEYS as readonly string[]).includes(key);
}

export function registerVaultHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  vault: Vault,
  logger: Logger,
): () => void {
  ipcMain.handle(CHANNEL.vaultStore, async (_event, req: unknown): Promise<void> => {
    const r = req as Record<string, unknown>;
    if (!isKnownVaultKey(r['key'])) {
      throw Object.assign(new Error(`vault/invalid-key: unknown key "${String(r['key'])}"`), {
        code: 'vault/invalid-key',
      });
    }
    if (typeof r['value'] !== 'string' || r['value'].length === 0) {
      throw Object.assign(new Error('vault/invalid-value: value must be a non-empty string'), {
        code: 'vault/invalid-value',
      });
    }
    const key = r['key'];
    logger.info('vault: store request', { key });
    try {
      await vault.store(key, r['value']);
    } catch (err: unknown) {
      const serialized = serializeError(err);
      throw Object.assign(new Error(serialized.message), { code: serialized.code });
    }
  });

  ipcMain.handle(CHANNEL.vaultHas, async (_event, req: unknown): Promise<VaultHasResponse> => {
    const r = req as Record<string, unknown>;
    if (!isKnownVaultKey(r['key'])) {
      throw Object.assign(new Error(`vault/invalid-key: unknown key "${String(r['key'])}"`), {
        code: 'vault/invalid-key',
      });
    }
    const key = r['key'];
    logger.info('vault: has request', { key });
    try {
      const exists = await vault.has(key);
      return { exists };
    } catch (err: unknown) {
      const serialized = serializeError(err);
      throw Object.assign(new Error(serialized.message), { code: serialized.code });
    }
  });

  ipcMain.handle(
    CHANNEL.vaultDelete,
    async (_event, req: unknown): Promise<VaultDeleteResponse> => {
      const r = req as Record<string, unknown>;
      if (!isKnownVaultKey(r['key'])) {
        throw Object.assign(new Error(`vault/invalid-key: unknown key "${String(r['key'])}"`), {
          code: 'vault/invalid-key',
        });
      }
      const key = r['key'];
      logger.info('vault: delete request', { key });
      try {
        const deleted = await vault.delete(key);
        return { deleted };
      } catch (err: unknown) {
        const serialized = serializeError(err);
        throw Object.assign(new Error(serialized.message), { code: serialized.code });
      }
    },
  );

  ipcMain.handle(CHANNEL.vaultIsAvailable, async (): Promise<VaultIsAvailableResponse> => {
    return vault.availability();
  });

  return () => {
    ipcMain.removeHandler(CHANNEL.vaultIsAvailable);
    ipcMain.removeHandler(CHANNEL.vaultDelete);
    ipcMain.removeHandler(CHANNEL.vaultHas);
    ipcMain.removeHandler(CHANNEL.vaultStore);
  };
}
