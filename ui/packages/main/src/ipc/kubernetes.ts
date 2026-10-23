import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type KubernetesDetection,
  type KubernetesStatus,
  type KubernetesUseRequest,
} from '@scout/shared';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { KubernetesSelection } from '../worker/build-env.js';
import type { Logger } from '../util/logger.js';

const SELECTION_KEY = 'kubernetes.selection';

export interface KubernetesHandlerDeps {
  vault: Pick<Vault, 'get' | 'store' | 'delete'>;
  detect(): Promise<KubernetesDetection>;
  onSelectionChanged(): void;
  logger: Logger;
}

/** The selection the owner approved, or null when Kubernetes is not connected. */
export async function readKubernetesSelection(
  vault: Pick<Vault, 'get'>,
): Promise<KubernetesSelection | null> {
  const raw = await vault.get(SELECTION_KEY);
  if (raw === null) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<KubernetesSelection>;
    const contexts = Array.isArray(parsed.contexts)
      ? parsed.contexts.filter((c): c is string => typeof c === 'string')
      : [];
    if (typeof parsed.path !== 'string' || contexts.length === 0) return null;
    return { path: parsed.path, contexts };
  } catch {
    return null;
  }
}

export function registerKubernetesHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  deps: KubernetesHandlerDeps,
): () => void {
  const { vault, logger } = deps;

  async function relay<T>(name: string, call: () => Promise<T>): Promise<T> {
    try {
      return await call();
    } catch (err: unknown) {
      const serialized = serializeError(err);
      logger.warn(`kubernetes: ${name} failed`, { code: serialized.code });
      throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), {
        code: serialized.code,
      });
    }
  }

  async function status(): Promise<KubernetesStatus> {
    const selection = await readKubernetesSelection(vault);
    return selection === null
      ? { path: null, contexts: [], connected: false }
      : { path: selection.path, contexts: selection.contexts, connected: true };
  }

  async function use(req: KubernetesUseRequest | undefined): Promise<KubernetesStatus> {
    const requested = Array.isArray(req?.contexts)
      ? [...new Set(req.contexts.filter((c): c is string => typeof c === 'string'))]
      : [];
    if (requested.length === 0) {
      throw new ScoutError('kubernetes/no-context', 'Select at least one context.');
    }
    const detection = await deps.detect();
    if (detection.path === null || detection.error !== null) {
      throw new ScoutError(
        'kubernetes/no-config',
        detection.error ?? 'No Kubernetes configuration found on this computer.',
      );
    }
    const known = new Set(detection.contexts.map((c) => c.name));
    const unknown = requested.find((c) => !known.has(c));
    if (unknown !== undefined) {
      throw new ScoutError(
        'kubernetes/unknown-context',
        `The context ${unknown} is not in ${detection.path}.`,
      );
    }
    const selection: KubernetesSelection = { path: detection.path, contexts: requested };
    await vault.store(SELECTION_KEY, JSON.stringify(selection));
    logger.info('kubernetes: contexts selected', { count: requested.length });
    deps.onSelectionChanged();
    return status();
  }

  async function disconnect(): Promise<KubernetesStatus> {
    await vault.delete(SELECTION_KEY);
    logger.info('kubernetes: disconnected');
    deps.onSelectionChanged();
    return status();
  }

  ipcMain.handle(CHANNEL.kubernetesDetect, (): Promise<KubernetesDetection> =>
    relay('detect', () => deps.detect()),
  );
  ipcMain.handle(
    CHANNEL.kubernetesUse,
    (_event, req: KubernetesUseRequest | undefined): Promise<KubernetesStatus> =>
      relay('use', () => use(req)),
  );
  ipcMain.handle(CHANNEL.kubernetesStatus, (): Promise<KubernetesStatus> =>
    relay('status', status),
  );
  ipcMain.handle(CHANNEL.kubernetesDisconnect, (): Promise<KubernetesStatus> =>
    relay('disconnect', disconnect),
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.kubernetesDisconnect);
    ipcMain.removeHandler(CHANNEL.kubernetesStatus);
    ipcMain.removeHandler(CHANNEL.kubernetesUse);
    ipcMain.removeHandler(CHANNEL.kubernetesDetect);
  };
}
