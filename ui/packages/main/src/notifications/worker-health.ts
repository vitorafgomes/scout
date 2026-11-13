import type { WorkerEvent, WorkerStatus } from '@scout/shared';
import type { NotificationStore } from './notification-store.js';

export const WORKER_HEALTH_ID = 'worker:health';
export const WORKER_RESTARTING_TITLE = 'Scout stopped unexpectedly and is restarting.';
export const WORKER_GAVE_UP_TITLE = 'Scout stopped and does not restart by itself.';
export const WORKER_GAVE_UP_DETAIL = 'Start it again from Logs or from Settings, Scout worker.';

/**
 * Keeps one bell entry for a worker that crashed: running while the supervisor restarts it, failed once the supervisor
 * gave up, removed once the worker runs again or the owner stops it. A stop and start the owner or main asked for
 * records nothing, and so does a first start that fails, which its own screen reports.
 */
export function watchWorkerHealth(
  supervisor: {
    readonly status: WorkerStatus;
    onEvent(listener: (e: WorkerEvent) => void): () => void;
  },
  store: Pick<NotificationStore, 'record' | 'remove'>,
): () => void {
  let restarts = supervisor.status.restarts;
  return supervisor.onEvent((event) => {
    if (event.kind !== 'state') return;
    const status = event.status;
    if (status.state === 'starting' && status.restarts > restarts) {
      store.record({
        id: WORKER_HEALTH_ID,
        kind: 'running',
        title: WORKER_RESTARTING_TITLE,
        detail: `Restart ${status.restarts} since Scout was started.`,
        source: 'worker',
      });
    } else if (status.state === 'crashed' && status.gaveUp === true && status.restarts > 0) {
      store.record({
        id: WORKER_HEALTH_ID,
        kind: 'failed',
        title: WORKER_GAVE_UP_TITLE,
        detail: WORKER_GAVE_UP_DETAIL,
        source: 'worker',
      });
    } else if (status.state === 'running' || status.state === 'stopped') {
      store.remove((n) => n.id === WORKER_HEALTH_ID);
    }
    restarts = status.restarts;
  });
}
