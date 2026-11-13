import type { NotificationAction, PendingActionDto } from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { NotificationStore } from './notification-store.js';

export const APPROVALS_ID = 'approvals:pending';
export const APPROVALS_POLL_MS = 15_000;
export const OPEN_APPROVALS: readonly NotificationAction[] = [
  { id: 'open-approvals', label: 'Open Approvals' },
];
const MAX_DETAIL_CHARS = 400;
const LISTED = 3;

export interface PendingApprovalsDeps {
  /** The control plane's action queue; any status, only `pending` ones wait for the owner. */
  list(): Promise<readonly PendingActionDto[]>;
  store: Pick<NotificationStore, 'record' | 'remove'>;
  /** Runs `callback` once after `ms`; returns the cancel function. */
  setTimer(ms: number, callback: () => void): () => void;
  logger: Logger;
}

export interface PendingApprovals {
  /** Reads the queue now; a read already running is not doubled. */
  check(): Promise<void>;
  dispose(): void;
}

/**
 * Keeps one bell entry while actions wait for the owner's approval: how many, the first few by name and target, and
 * Open Approvals. Read every 15 seconds and after every decision; the entry is replaced when the queue changes and
 * removed when nothing waits. A queue that cannot be read keeps the entry as it was.
 */
export function watchPendingApprovals(deps: PendingApprovalsDeps): PendingApprovals {
  let running: Promise<void> | null = null;
  let cancel: (() => void) | null = null;
  let disposed = false;

  async function read(): Promise<void> {
    let actions: readonly PendingActionDto[];
    try {
      actions = await deps.list();
    } catch (err: unknown) {
      deps.logger.debug('approvals: the queue could not be read; the bell entry is kept', {
        error: err instanceof Error ? err.name : typeof err,
      });
      return;
    }
    const pending = actions.filter((a) => a.status === 'pending');
    if (pending.length === 0) {
      deps.store.remove((n) => n.id === APPROVALS_ID);
      return;
    }
    const newest = Math.max(...pending.map((a) => Date.parse(a.requestedAt) || 0));
    const listed = pending
      .slice(0, LISTED)
      .map((a) => `${a.action} on ${a.target}`)
      .join('; ');
    const more = pending.length > LISTED ? `; and ${pending.length - LISTED} more` : '';
    const detail = `${listed}${more}`;
    deps.store.record({
      id: APPROVALS_ID,
      kind: 'paused',
      title:
        pending.length === 1
          ? 'An action waits for your approval'
          : `${pending.length} actions wait for your approval`,
      detail:
        detail.length <= MAX_DETAIL_CHARS ? detail : `${detail.slice(0, MAX_DETAIL_CHARS - 1)}…`,
      ...(newest > 0 ? { at: newest } : {}),
      actions: OPEN_APPROVALS,
      source: 'worker',
    });
  }

  function schedule(): void {
    cancel?.();
    cancel = disposed ? null : deps.setTimer(APPROVALS_POLL_MS, () => void check());
  }

  function check(): Promise<void> {
    if (disposed) return Promise.resolve();
    running ??= read().finally(() => {
      running = null;
      schedule();
    });
    return running;
  }

  return {
    check,
    dispose() {
      disposed = true;
      cancel?.();
      cancel = null;
    },
  };
}
