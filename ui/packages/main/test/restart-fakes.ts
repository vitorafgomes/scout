import type { Supervisor } from '../src/worker/supervisor.js';
import { createWorkerRestart, type WorkerRestart } from '../src/worker/worker-restart.js';
import { recordingLogger } from './kubernetes-fakes.js';

/** Main's restart path around a worker fake, with no answer running and no live listen open. */
export function restartPath(worker: Pick<Supervisor, 'status' | 'stop' | 'start'>): WorkerRestart {
  return createWorkerRestart({
    supervisor: worker,
    currentModel: async () => null,
    loadedModel: () => null,
    listening: () => false,
    store: { record: () => undefined },
    onRestarted: () => undefined,
    logger: recordingLogger().logger,
  });
}
