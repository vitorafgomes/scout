import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  kubernetesLabelError,
  serializeError,
  type KubernetesDetection,
  type KubernetesLabelledContext,
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
    const parsed = JSON.parse(raw) as { path?: unknown; contexts?: unknown };
    const contexts = Array.isArray(parsed.contexts) ? parsed.contexts.flatMap(storedContext) : [];
    if (typeof parsed.path !== 'string' || contexts.length === 0) return null;
    return { path: parsed.path, contexts };
  } catch {
    return null;
  }
}

/** A stored context: `{name, label}`, or a bare name from before labels, which keeps its name as label. */
function storedContext(entry: unknown): KubernetesLabelledContext[] {
  if (typeof entry === 'string') return [{ name: entry, label: entry }];
  if (typeof entry !== 'object' || entry === null) return [];
  const { name, label } = entry as { name?: unknown; label?: unknown };
  if (typeof name !== 'string') return [];
  return [{ name, label: typeof label === 'string' && label.trim() !== '' ? label : name }];
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
      ? req.contexts
          .filter(
            (c): c is KubernetesLabelledContext =>
              typeof c?.name === 'string' && typeof c?.label === 'string',
          )
          .map((c) => ({ name: c.name, label: c.label.trim() }))
          .filter((c, i, all) => all.findIndex((other) => other.name === c.name) === i)
      : [];
    if (requested.length === 0) {
      throw new ScoutError('kubernetes/no-context', 'Select at least one context.');
    }
    const labelError = kubernetesLabelError(requested);
    if (labelError !== null) throw new ScoutError('kubernetes/invalid-label', labelError);
    const detection = await deps.detect();
    if (detection.path === null || detection.error !== null) {
      throw new ScoutError(
        'kubernetes/no-config',
        detection.error ?? 'No Kubernetes configuration found on this computer.',
      );
    }
    const known = new Set(detection.contexts.map((c) => c.name));
    const unknown = requested.find((c) => !known.has(c.name));
    if (unknown !== undefined) {
      throw new ScoutError(
        'kubernetes/unknown-context',
        `The context ${unknown.name} is not in ${detection.path}.`,
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
