import { VaultUnavailableError } from '@scout/shared';
import type { VaultKey } from '@scout/shared';
import type { Logger } from '../util/logger.js';

export interface SafeStorageSubset {
  isEncryptionAvailable(): boolean;
  encryptString(plaintext: string): Buffer;
  decryptString(encrypted: Buffer): string;
}

export interface VaultDeps {
  safeStorage: SafeStorageSubset;
  readFile(path: string): Promise<string | null>;
  writeFile(path: string, content: string): Promise<void>;
  rename(oldPath: string, newPath: string): Promise<void>;
  vaultPath: string;
  logger: Logger;
}

export interface VaultAvailability {
  available: boolean;
  reason?: 'no-keyring' | 'unknown';
}

export interface Vault {
  isAvailable(): boolean;
  availability(): VaultAvailability;
  store(key: VaultKey, value: string): Promise<void>;
  get(key: VaultKey): Promise<string | null>;
  has(key: VaultKey): Promise<boolean>;
  delete(key: VaultKey): Promise<boolean>;
}

interface VaultFileV1 {
  v: 1;
  entries: Record<string, string>;
}

function isVaultFileV1(value: unknown): value is VaultFileV1 {
  if (typeof value !== 'object' || value === null) return false;
  const obj = value as Record<string, unknown>;
  return obj['v'] === 1 && typeof obj['entries'] === 'object' && obj['entries'] !== null;
}

export function createVault(deps: VaultDeps): Vault {
  const { safeStorage, readFile, writeFile, rename, vaultPath, logger } = deps;
  const tmpPath = vaultPath + '.tmp';

  // Serialises concurrent writes so that two parallel store calls cannot
  // produce interleaved reads and writes that would corrupt the file.
  let queue: Promise<unknown> = Promise.resolve();

  async function loadEntries(): Promise<Record<string, string>> {
    const raw = await readFile(vaultPath);
    if (raw === null) return {};

    try {
      const parsed: unknown = JSON.parse(raw);
      if (!isVaultFileV1(parsed)) {
        logger.warn('vault file has unexpected shape, treating as empty', { vaultPath });
        return {};
      }
      return { ...parsed.entries };
    } catch {
      logger.warn('vault file contains invalid JSON, treating as empty', { vaultPath });
      return {};
    }
  }

  async function persistEntries(entries: Record<string, string>): Promise<void> {
    const payload: VaultFileV1 = { v: 1, entries };
    await writeFile(tmpPath, JSON.stringify(payload));
    await rename(tmpPath, vaultPath);
  }

  function enqueue<T>(task: () => Promise<T>): Promise<T> {
    const next = queue.then(task, task) as Promise<T>;
    queue = next;
    return next;
  }

  return {
    isAvailable(): boolean {
      return safeStorage.isEncryptionAvailable();
    },

    availability(): VaultAvailability {
      if (!safeStorage.isEncryptionAvailable()) {
        return { available: false, reason: 'no-keyring' };
      }
      return { available: true };
    },

    store(key: VaultKey, value: string): Promise<void> {
      if (!safeStorage.isEncryptionAvailable()) {
        return Promise.reject(new VaultUnavailableError());
      }

      return enqueue(async (): Promise<void> => {
        const entries = await loadEntries();
        const encrypted = safeStorage.encryptString(value);
        entries[key] = encrypted.toString('base64');
        await persistEntries(entries);
        logger.info('vault: stored key', { key });
      });
    },

    get(key: VaultKey): Promise<string | null> {
      if (!safeStorage.isEncryptionAvailable()) {
        return Promise.reject(new VaultUnavailableError());
      }
      return enqueue(async (): Promise<string | null> => {
        const entries = await loadEntries();
        const encoded = entries[key];
        if (encoded === undefined) return null;
        const decrypted = safeStorage.decryptString(Buffer.from(encoded, 'base64'));
        return decrypted;
      });
    },

    has(key: VaultKey): Promise<boolean> {
      return enqueue(async (): Promise<boolean> => {
        const entries = await loadEntries();
        return key in entries;
      });
    },

    delete(key: VaultKey): Promise<boolean> {
      return enqueue(async (): Promise<boolean> => {
        const entries = await loadEntries();
        if (!(key in entries)) {
          return false;
        }
        delete entries[key];
        await persistEntries(entries);
        logger.info('vault: deleted key', { key });
        return true;
      });
    },
  };
}
