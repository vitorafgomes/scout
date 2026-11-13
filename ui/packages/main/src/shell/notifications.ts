import type { WorkerStatus } from '@scout/shared';
import type { Vault } from '../vault/safe-storage-vault.js';
import type { Logger } from '../util/logger.js';

const TRAY_NOTICE_KEY = 'app.tray-notice-shown';

export interface PresenceNotifierDeps {
  platform: NodeJS.Platform;
  isSupported(): boolean;
  show(title: string, body: string): void;
  vault: Pick<Vault, 'get' | 'store'>;
  logger: Logger;
}

/** Native notifications for the desktop events; they carry fixed text and never a message, token or log line. */
export interface PresenceNotifier {
  isSupported(): boolean;
  workerStatus(status: WorkerStatus): void;
  startedHidden(): void;
  paired(userId: string): void;
  hiddenToTray(): Promise<void>;
}

export function createPresenceNotifier(deps: PresenceNotifierDeps): PresenceNotifier {
  let crashNotified = false;
  let ranSinceStart = false;
  let trayNoticeShown = false;
  const linux = deps.platform === 'linux';

  function notify(body: string): void {
    if (deps.isSupported()) deps.show('Scout', body);
  }

  return {
    isSupported: () => deps.isSupported(),

    workerStatus(status: WorkerStatus): void {
      if (status.state === 'crashed' && status.gaveUp === true && !crashNotified) {
        crashNotified = true;
        notify(
          ranSinceStart
            ? 'Scout stopped and could not restart. Open Scout to start it again.'
            : 'Scout could not start.',
        );
      }
      if (status.state === 'running') ranSinceStart = true;
      if (status.state === 'stopped' || status.state === 'idle') ranSinceStart = false;
      if (status.state !== 'crashed') crashNotified = false;
    },

    startedHidden(): void {
      if (linux)
        notify('Scout is running in the background. Open it from the app menu or the tray.');
    },

    paired(userId: string): void {
      notify(`${userId} can now use Scout`);
    },

    async hiddenToTray(): Promise<void> {
      if (trayNoticeShown) return;
      trayNoticeShown = true;
      const shownBefore = await deps.vault.get(TRAY_NOTICE_KEY).catch(() => null);
      if (shownBefore !== null) return;
      notify(
        linux
          ? 'Scout is still running. Open it again from the app menu or the tray.'
          : 'Scout is still running in the tray.',
      );
      await deps.vault.store(TRAY_NOTICE_KEY, 'true').catch((err: unknown) => {
        deps.logger.warn('notifications: tray notice not remembered', {
          error: err instanceof Error ? err.name : typeof err,
        });
      });
    },
  };
}

/** Hands a worker pairing report to access and notifies only when it added that user. */
export async function reportPairing(
  userId: string,
  access: { pendingPairing(): unknown; paired(userId: string): Promise<void> },
  readAllowedUserIds: () => Promise<readonly string[]>,
  notifier: Pick<PresenceNotifier, 'paired'>,
): Promise<void> {
  const wasPending = access.pendingPairing() !== null;
  await access.paired(userId);
  if (wasPending && (await readAllowedUserIds()).includes(userId)) notifier.paired(userId);
}
