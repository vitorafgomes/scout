import { app, safeStorage } from 'electron';
import fs from 'node:fs/promises';
import path from 'node:path';
import type { VaultDeps } from './safe-storage-vault.js';
import type { Logger } from '../util/logger.js';

export function createProductionVaultDeps(logger: Logger): VaultDeps {
  return {
    safeStorage,

    async readFile(filePath: string): Promise<string | null> {
      try {
        return await fs.readFile(filePath, 'utf8');
      } catch (err: unknown) {
        if (isEnoent(err)) return null;
        throw err;
      }
    },

    async writeFile(filePath: string, content: string): Promise<void> {
      // mode 0o600 restricts read/write to the file owner only.
      // On Windows the mode bits are ignored by the OS; the file is still
      // written successfully but without POSIX permission enforcement.
      await fs.writeFile(filePath, content, { mode: 0o600 });
    },

    rename: fs.rename,

    vaultPath: path.join(app.getPath('userData'), 'vault.enc'),

    logger,
  };
}

function isEnoent(err: unknown): boolean {
  return (
    typeof err === 'object' && err !== null && (err as NodeJS.ErrnoException).code === 'ENOENT'
  );
}
