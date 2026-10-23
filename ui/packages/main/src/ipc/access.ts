import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type AccessAllowedUsers,
} from '@scout/shared';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';

const ALLOWED_USERS_KEY = 'access.allowed-users';
const MAX_IDS = 50;
const ID_PATTERN = /^[A-Za-z0-9._:@-]{1,64}$/;

export interface AccessHandlerDeps {
  vault: Pick<Vault, 'get' | 'store' | 'delete'>;
  onAllowedUsersChanged(): void;
  logger: Logger;
}

/** The platform user ids the owner allowed, in the order they were added; empty when none. */
export async function readAllowedUserIds(vault: Pick<Vault, 'get'>): Promise<string[]> {
  const raw = await vault.get(ALLOWED_USERS_KEY);
  if (raw === null) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed)
      ? parsed.filter((id): id is string => typeof id === 'string' && ID_PATTERN.test(id))
      : [];
  } catch {
    return [];
  }
}

/** Trims and de-duplicates the ids; rejects the whole list when one id is malformed or there are too many. */
export function validateAllowedUserIds(input: unknown): string[] {
  if (!Array.isArray(input)) {
    throw new ScoutError('access/invalid-request', 'The allowed users must be a list of ids.');
  }
  const ids: string[] = [];
  for (const value of input) {
    const id = typeof value === 'string' ? value.trim() : '';
    if (!ID_PATTERN.test(id)) {
      throw new ScoutError(
        'access/invalid-id',
        'A user id must be 1 to 64 letters, digits or . _ : @ - characters.',
      );
    }
    if (!ids.includes(id)) ids.push(id);
  }
  if (ids.length > MAX_IDS) {
    throw new ScoutError('access/too-many', `Scout allows at most ${MAX_IDS} users.`);
  }
  return ids;
}

export function registerAccessHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  deps: AccessHandlerDeps,
): () => void {
  const { vault, logger } = deps;

  async function relay<T>(name: string, call: () => Promise<T>): Promise<T> {
    try {
      return await call();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      logger.warn(`access: ${name} failed`, { code: serialized.code });
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
  }

  async function get(): Promise<AccessAllowedUsers> {
    return { userIds: await readAllowedUserIds(vault) };
  }

  async function set(req: AccessAllowedUsers | undefined): Promise<AccessAllowedUsers> {
    const next = validateAllowedUserIds(req?.userIds);
    const current = await readAllowedUserIds(vault);
    if (next.length === current.length && next.every((id, i) => id === current[i])) {
      return { userIds: current };
    }
    if (next.length === 0) await vault.delete(ALLOWED_USERS_KEY);
    else await vault.store(ALLOWED_USERS_KEY, JSON.stringify(next));
    logger.info('access: allowed users changed', { count: next.length });
    deps.onAllowedUsersChanged();
    return { userIds: next };
  }

  ipcMain.handle(CHANNEL.accessGet, (): Promise<AccessAllowedUsers> => relay('get', get));
  ipcMain.handle(
    CHANNEL.accessSet,
    (_event, req: AccessAllowedUsers | undefined): Promise<AccessAllowedUsers> =>
      relay('set', () => set(req)),
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.accessSet);
    ipcMain.removeHandler(CHANNEL.accessGet);
  };
}
