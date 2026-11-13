import { INSECURE_STORE_MESSAGE, VaultUnavailableError } from '@scout/shared';
import type { VaultKey, VaultUnavailableReason } from '@scout/shared';
import type { Logger } from '../util/logger.js';

/** Keys only the main process reads or writes; they are not in VAULT_KEYS, so the renderer's vault channels reject them. */
export type MainOnlyVaultKey =
  | 'chatgpt.session'
  | 'chatgpt.client-id'
  | 'chatgpt.host-id'
  | 'github.session'
  | 'github.token'
  | 'github.bot'
  | 'youtube.session'
  | 'youtube.api-key'
  | 'llm.openai.admin-key'
  | 'llm.anthropic.admin-key'
  | 'kubernetes.selection'
  | 'access.allowed-users'
  | 'slack.connection'
  | 'app.keep-running'
  | 'app.tray-notice-shown'
  | 'app.notifications'
  | 'quick-ask.shortcut'
  | 'quick-ask.bubble'
  | 'quick-ask.window'
  | 'operator.window'
  | 'app.theme'
  | 'update.check-enabled'
  | 'update.skipped-version'
  | 'update.last-check'
  | 'features.pending-revokes';

export type StoredVaultKey = VaultKey | MainOnlyVaultKey;

export interface SafeStorageSubset {
  isEncryptionAvailable(): boolean;
  /** Linux only: the password store Electron selected; never called on another platform. */
  getSelectedStorageBackend(): string;
  encryptString(plaintext: string): Buffer;
  decryptString(encrypted: Buffer): string;
}

export interface VaultDeps {
  platform: NodeJS.Platform;
  safeStorage: SafeStorageSubset;
  readFile(path: string): Promise<string | null>;
  writeFile(path: string, content: string): Promise<void>;
  rename(oldPath: string, newPath: string): Promise<void>;
  vaultPath: string;
  logger: Logger;
}

export interface VaultAvailability {
  available: boolean;
  reason?: VaultUnavailableReason;
}

const KEYRING_BACKENDS: ReadonlySet<string> = new Set([
  'gnome_libsecret',
  'kwallet',
  'kwallet5',
  'kwallet6',
]);

export interface Vault {
  isAvailable(): boolean;
  availability(): VaultAvailability;
  store(key: StoredVaultKey, value: string): Promise<void>;
  get(key: StoredVaultKey): Promise<string | null>;
  has(key: StoredVaultKey): Promise<boolean>;
  delete(key: StoredVaultKey): Promise<boolean>;
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
  const { platform, safeStorage, readFile, writeFile, rename, vaultPath, logger } = deps;
  const tmpPath = vaultPath + '.tmp';

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

  let insecureStoreLogged = false;

  function availability(): VaultAvailability {
    if (platform === 'linux') {
      let backend: string;
      try {
        backend = safeStorage.getSelectedStorageBackend();
      } catch {
        backend = 'unreadable';
      }
      if (!KEYRING_BACKENDS.has(backend)) {
        if (!insecureStoreLogged) {
          insecureStoreLogged = true;
          logger.warn('vault: refused, no keyring service was selected', { backend });
        }
        return { available: false, reason: 'insecure-store' };
      }
    }
    if (!safeStorage.isEncryptionAvailable()) {
      return { available: false, reason: 'no-keyring' };
    }
    return { available: true };
  }

  function whenAvailable<T>(task: () => Promise<T>): Promise<T> {
    const state = availability();
    if (state.available) return enqueue(task);
    return Promise.reject(
      state.reason === 'insecure-store'
        ? new VaultUnavailableError(INSECURE_STORE_MESSAGE)
        : new VaultUnavailableError(),
    );
  }

  return {
    isAvailable(): boolean {
      return availability().available;
    },

    availability,

    store(key: StoredVaultKey, value: string): Promise<void> {
      return whenAvailable(async (): Promise<void> => {
        const entries = await loadEntries();
        const encrypted = safeStorage.encryptString(value);
        entries[key] = encrypted.toString('base64');
        await persistEntries(entries);
        logger.info('vault: stored key', { key });
      });
    },

    get(key: StoredVaultKey): Promise<string | null> {
      return whenAvailable(async (): Promise<string | null> => {
        const entries = await loadEntries();
        const encoded = entries[key];
        if (encoded === undefined) return null;
        const decrypted = safeStorage.decryptString(Buffer.from(encoded, 'base64'));
        return decrypted;
      });
    },

    has(key: StoredVaultKey): Promise<boolean> {
      return whenAvailable(async (): Promise<boolean> => {
        const entries = await loadEntries();
        return key in entries;
      });
    },

    delete(key: StoredVaultKey): Promise<boolean> {
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
