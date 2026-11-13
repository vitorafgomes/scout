import type { WorkerRestartResponse } from '@scout/shared';

interface RestartBridge {
  workerStatus(): Promise<{ state: string }>;
  workerStop(): Promise<unknown>;
  workerStart(): Promise<unknown>;
  localListening?: () => Promise<{ listening: boolean }>;
  localGetTurns?: () => Promise<readonly { state: string }[]>;
}

/**
 * Main's restart path as a screen sees it through a fake bridge: none while no worker runs, deferred while a live
 * listen or an answer runs, otherwise a stop then a start, and failed with the reason a start or stop threw.
 */
export async function restartAsMain(fake: object): Promise<WorkerRestartResponse> {
  const bridge = fake as RestartBridge;
  const { state } = await bridge.workerStatus();
  if (state !== 'running' && state !== 'starting') return { outcome: 'none' };
  const listening = (await bridge.localListening?.())?.listening === true;
  const turns = (await bridge.localGetTurns?.()) ?? [];
  if (listening || turns.some((turn) => turn.state === 'running')) return { outcome: 'deferred' };
  try {
    await bridge.workerStop();
    await bridge.workerStart();
    return { outcome: 'now' };
  } catch (err: unknown) {
    return { outcome: 'failed', reason: err instanceof Error ? err.message : String(err) };
  }
}
