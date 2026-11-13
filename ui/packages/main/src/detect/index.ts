import { CLIENT_KINDS, type DetectedClient } from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { DetectDeps } from './types.js';

function notInstalledEntry(kind: DetectedClient['kind']): DetectedClient {
  return { kind, installed: false, method: 'none' };
}

async function detectKindSafe(
  kind: DetectedClient['kind'],
  deps: DetectDeps,
  logger: Logger,
): Promise<DetectedClient> {
  const platform = deps.platform();

  if (platform === 'win32') {
    const windows = await import('./windows.js');
    return windows.detectKind(kind, deps);
  }

  if (platform === 'darwin') {
    const macos = await import('./macos.js');
    return macos.detectKind(kind, deps, logger);
  }

  if (platform === 'linux') {
    const linux = await import('./linux.js');
    return linux.detectKind(kind, deps, logger);
  }

  return notInstalledEntry(kind);
}

export async function detectAll(
  deps: DetectDeps,
  logger: Logger,
): Promise<readonly DetectedClient[]> {
  const platform = deps.platform();

  if (platform !== 'win32' && platform !== 'darwin' && platform !== 'linux') {
    logger.warn('unsupported platform for client detection', { platform });
    return CLIENT_KINDS.map(notInstalledEntry);
  }

  const settled = await Promise.allSettled(
    CLIENT_KINDS.map((kind) => detectKindSafe(kind, deps, logger)),
  );

  return CLIENT_KINDS.map((kind, i) => {
    const result = settled[i];
    if (result === undefined || result.status === 'rejected') {
      logger.warn('detection failed for kind, downgrading to not-installed', {
        kind,
        err: result?.status === 'rejected' ? String(result.reason) : 'missing result',
      });
      return notInstalledEntry(kind);
    }
    return result.value;
  });
}
