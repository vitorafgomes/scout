import { FEATURE_IDS, serializeError, type FeatureId } from '@scout/shared';
import type { ControlPlaneClient } from '../control-plane/client.js';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';

const PENDING_REVOKES_KEY = 'features.pending-revokes';

type RevokeClient = Pick<ControlPlaneClient, 'setWriteGrant'>;

/** The features whose write grant main still has to revoke on the control plane, kept until a revoke goes through. */
export interface PendingRevokes {
  add(id: FeatureId): Promise<void>;
  remove(id: FeatureId): Promise<void>;
  list(): Promise<FeatureId[]>;
}

/** Pending revokes in main's vault under a main-only key, one change at a time, so the renderer and the worker never see them. */
export function vaultPendingRevokes(
  vault: Pick<Vault, 'get' | 'store' | 'delete'>,
): PendingRevokes {
  let queue: Promise<unknown> = Promise.resolve();
  const serial = <T>(run: () => Promise<T>): Promise<T> => {
    const next = queue.then(run, run);
    queue = next.catch(() => undefined);
    return next;
  };
  const read = async (): Promise<FeatureId[]> => {
    const raw = await vault.get(PENDING_REVOKES_KEY);
    if (raw === null) return [];
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed)
      ? FEATURE_IDS.filter((id) => (parsed as unknown[]).includes(id))
      : [];
  };
  const write = async (ids: readonly FeatureId[]): Promise<void> => {
    if (ids.length === 0) await vault.delete(PENDING_REVOKES_KEY);
    else await vault.store(PENDING_REVOKES_KEY, JSON.stringify(ids));
  };
  return {
    list: () => serial(read),
    add: (id) =>
      serial(async () => {
        const ids = await read();
        if (!ids.includes(id)) await write([...ids, id]);
      }),
    remove: (id) =>
      serial(async () => {
        const ids = await read();
        if (ids.includes(id)) await write(ids.filter((pending) => pending !== id));
      }),
  };
}

/**
 * Revokes `featureId`'s write grant on the control plane and then calls `onRevoked`, so the renderer reloads the
 * features. A revoke the control plane does not take is logged with its code and kept in `pending`, never thrown, so it
 * never blocks the credential change that asked; `replayPendingRevokes` sends it again.
 */
export async function revokeFeatureWriteGrant(
  featureId: FeatureId,
  getControlPlane: () => Promise<RevokeClient>,
  pending: PendingRevokes,
  logger: Logger,
  onRevoked?: () => void,
): Promise<void> {
  try {
    await (await getControlPlane()).setWriteGrant(featureId, false, false);
  } catch (err: unknown) {
    logger.warn(`${featureId}: write grant not revoked`, { code: serializeError(err).code });
    await pending.add(featureId).catch((recordErr: unknown) => {
      logger.error(`${featureId}: the revoke to send again was not kept`, undefined, {
        code: serializeError(recordErr).code,
      });
    });
    return;
  }
  await pending.remove(featureId).catch(() => undefined);
  onRevoked?.();
}

/**
 * Sends every pending revoke to `client` and answers the features still pending; each revoke the control plane takes
 * leaves the list, and `onReplayed` hears once that at least one did.
 */
export async function replayPendingRevokes(
  client: RevokeClient,
  pending: PendingRevokes,
  logger: Logger,
  onReplayed?: () => void,
): Promise<FeatureId[]> {
  const still: FeatureId[] = [];
  let replayed = false;
  for (const id of await pending.list()) {
    try {
      await client.setWriteGrant(id, false, false);
      await pending.remove(id);
      replayed = true;
      logger.info('features: pending write revoke sent', { id });
    } catch (err: unknown) {
      still.push(id);
      logger.warn('features: pending write revoke still not taken', {
        id,
        code: serializeError(err).code,
      });
    }
  }
  if (replayed) onReplayed?.();
  return still;
}
