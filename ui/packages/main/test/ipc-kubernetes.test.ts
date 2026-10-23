import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { CHANNEL, readIpcError, type WorkerStatus } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { restartWorkerIfRunning } from '../src/ipc/github.js';
import { readKubernetesSelection, registerKubernetesHandlers } from '../src/ipc/kubernetes.js';
import { detectKubeconfig } from '../src/kubernetes/kubeconfig.js';
import { buildEnv } from '../src/worker/build-env.js';
import {
  KUBECONFIG,
  SECRETS,
  recordingLogger,
  tempHome,
  type TempHome,
} from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

function makeFakeIpcMain(): Pick<IpcMain, 'handle' | 'removeHandler'> & {
  handlers: Map<string, IpcHandler>;
} {
  const handlers = new Map<string, IpcHandler>();
  return {
    handlers,
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
}

function makeVault(): {
  entries: Map<string, string>;
  get(key: string): Promise<string | null>;
  store(key: string, value: string): Promise<void>;
  delete(key: string): Promise<boolean>;
} {
  const entries = new Map<string, string>();
  return {
    entries,
    get: async (key) => entries.get(key) ?? null,
    store: async (key, value) => {
      entries.set(key, value);
    },
    delete: async (key) => entries.delete(key),
  };
}

function makeWorker(state: WorkerStatus['state']): {
  status: WorkerStatus;
  calls: string[];
  stop(): Promise<{ exitCode: number | null }>;
  start(): Promise<{ pid: number }>;
} {
  const calls: string[] = [];
  return {
    status: { state, restarts: 0 },
    calls,
    async stop() {
      calls.push('stop');
      return { exitCode: 0 };
    },
    async start() {
      calls.push('start');
      return { pid: 42 };
    },
  };
}

const labelled = (name: string, label = name): { name: string; label: string } => ({ name, label });

describe('Kubernetes IPC', () => {
  let home: TempHome;
  let kubeconfigPath: string;

  beforeEach(async () => {
    home = await tempHome();
    kubeconfigPath = await home.write('.kube/config', KUBECONFIG);
  });

  afterEach(async () => {
    await home.dispose();
  });

  function setup(workerState: WorkerStatus['state'] = 'running') {
    const ipc = makeFakeIpcMain();
    const vault = makeVault();
    const worker = makeWorker(workerState);
    const { logger, lines } = recordingLogger();
    let restart: Promise<void> = Promise.resolve();
    const dispose = registerKubernetesHandlers(ipc, {
      vault,
      detect: () => detectKubeconfig({ env: {}, homedir: home.home, logger }),
      onSelectionChanged: () => {
        restart = restartWorkerIfRunning(worker, logger);
      },
      logger,
    });
    const call = (channel: string, req?: unknown): Promise<unknown> => {
      const handler = ipc.handlers.get(channel);
      if (handler === undefined) throw new Error(`no handler for ${channel}`);
      return Promise.resolve(handler(NULL_EVENT, req));
    };
    const env = async (): Promise<Record<string, string>> =>
      buildEnv({
        vaultGet: async (key) => (key === 'discord.bot-token' ? 'tok' : null),
        kubernetesSelection: await readKubernetesSelection(vault),
      });
    return { ipc, vault, worker, lines, call, env, dispose, restarted: () => restart };
  }

  async function rejection(
    promise: Promise<unknown>,
  ): Promise<{ code: string | null; message: string }> {
    try {
      await promise;
    } catch (err: unknown) {
      return readIpcError(new Error((err as Error).message));
    }
    throw new Error('expected a rejection');
  }

  const kubernetesVars = (env: Record<string, string>): Record<string, string> =>
    Object.fromEntries(
      Object.entries(env).filter(([key]) => key.startsWith('SCOUT__PLUGINS__KUBERNETES')),
    );

  it('gives the worker no Kubernetes variables until the owner uses selected contexts', async () => {
    const s = setup();

    await s.call(CHANNEL.kubernetesDetect);
    await s.call(CHANNEL.kubernetesStatus);
    const before = kubernetesVars(await s.env());
    await s.call(CHANNEL.kubernetesUse, { contexts: [labelled('dev'), labelled('eks', 'aws')] });
    const after = kubernetesVars(await s.env());

    expect(before).toEqual({});
    expect(after).toEqual({
      SCOUT__PLUGINS__KUBERNETES__KUBECONFIGPATH: kubeconfigPath,
      SCOUT__PLUGINS__KUBERNETES__CONTEXTS__0__NAME: 'dev',
      SCOUT__PLUGINS__KUBERNETES__CONTEXTS__0__LABEL: 'dev',
      SCOUT__PLUGINS__KUBERNETES__CONTEXTS__1__NAME: 'eks',
      SCOUT__PLUGINS__KUBERNETES__CONTEXTS__1__LABEL: 'aws',
    });
  });

  it('shows the command an exec context runs before the owner consents', async () => {
    const s = setup();

    const detection = (await s.call(CHANNEL.kubernetesDetect)) as {
      contexts: { name: string; execCommand?: string }[];
    };

    expect(detection.contexts.find((c) => c.name === 'eks')?.execCommand).toBe(
      'aws eks get-token --cluster-name prod',
    );
  });

  it('use stores the path main located and the contexts, and restarts the running worker', async () => {
    const s = setup('running');

    const status = await s.call(CHANNEL.kubernetesUse, {
      contexts: [
        labelled('dev', ' Dev cluster '),
        labelled('dev', 'again'),
        labelled('prod', 'live'),
      ],
    });
    await s.restarted();

    const stored = [
      { name: 'dev', label: 'Dev cluster' },
      { name: 'prod', label: 'live' },
    ];
    expect(status).toEqual({ path: kubeconfigPath, contexts: stored, connected: true });
    expect(JSON.parse(s.vault.entries.get('kubernetes.selection')!)).toEqual({
      path: kubeconfigPath,
      contexts: stored,
    });
    expect(s.worker.calls).toEqual(['stop', 'start']);
  });

  it('refuses a context that is not in the kubeconfig and stores nothing', async () => {
    const s = setup();

    const err = await rejection(
      s.call(CHANNEL.kubernetesUse, { contexts: [labelled('dev'), labelled('nope')] }),
    );

    expect(err).toEqual({
      code: 'kubernetes/unknown-context',
      message: `The context nope is not in ${kubeconfigPath}.`,
    });
    expect(s.vault.entries.size).toBe(0);
    expect(s.worker.calls).toEqual([]);
  });

  it.each([
    [
      'an invalid label',
      [labelled('dev', 'dev/eu')],
      'The label for dev must be 1 to 32 letters, digits, spaces or hyphens.',
    ],
    [
      'a duplicate label',
      [labelled('dev', 'prod'), labelled('prod', 'PROD')],
      'The label PROD is used more than once.',
    ],
  ])('refuses %s and stores nothing', async (_case, contexts, message) => {
    const s = setup();

    const err = await rejection(s.call(CHANNEL.kubernetesUse, { contexts }));

    expect(err).toEqual({ code: 'kubernetes/invalid-label', message });
    expect(s.vault.entries.size).toBe(0);
    expect(s.worker.calls).toEqual([]);
  });

  it('reads a selection stored before labels, using each context name as its label', async () => {
    const s = setup();
    s.vault.entries.set(
      'kubernetes.selection',
      JSON.stringify({ path: kubeconfigPath, contexts: ['dev', 'prod'] }),
    );

    const status = await s.call(CHANNEL.kubernetesStatus);

    expect(status).toEqual({
      path: kubeconfigPath,
      contexts: [
        { name: 'dev', label: 'dev' },
        { name: 'prod', label: 'prod' },
      ],
      connected: true,
    });
    expect(kubernetesVars(await s.env())).toMatchObject({
      SCOUT__PLUGINS__KUBERNETES__CONTEXTS__1__NAME: 'prod',
      SCOUT__PLUGINS__KUBERNETES__CONTEXTS__1__LABEL: 'prod',
    });
  });

  it('refuses an empty selection', async () => {
    const s = setup();

    const err = await rejection(s.call(CHANNEL.kubernetesUse, { contexts: [] }));

    expect(err.code).toBe('kubernetes/no-context');
    expect(s.vault.entries.size).toBe(0);
  });

  it('refuses use when the kubeconfig is gone', async () => {
    const s = setup();
    await home.dispose();

    const err = await rejection(s.call(CHANNEL.kubernetesUse, { contexts: [labelled('dev')] }));

    expect(err).toEqual({
      code: 'kubernetes/no-config',
      message: 'No Kubernetes configuration found on this computer.',
    });
  });

  it('disconnect clears the selection, removes the worker variables and restarts the worker', async () => {
    const s = setup('running');
    await s.call(CHANNEL.kubernetesUse, { contexts: [labelled('dev')] });
    await s.restarted();
    s.worker.calls.length = 0;

    const status = await s.call(CHANNEL.kubernetesDisconnect);
    await s.restarted();

    expect(status).toEqual({ path: null, contexts: [], connected: false });
    expect(s.vault.entries.has('kubernetes.selection')).toBe(false);
    expect(kubernetesVars(await s.env())).toEqual({});
    expect(s.worker.calls).toEqual(['stop', 'start']);
  });

  it('no token, key, certificate, password or exec env value reaches the renderer or a log line', async () => {
    const s = setup();

    const payloads = [
      await s.call(CHANNEL.kubernetesDetect),
      await s.call(CHANNEL.kubernetesUse, {
        contexts: ['dev', 'prod', 'eks', 'gke', 'legacy'].map((name) => labelled(name)),
      }),
      await s.call(CHANNEL.kubernetesStatus),
      await s.call(CHANNEL.kubernetesDisconnect),
    ];

    const visible = JSON.stringify(payloads) + s.lines.join('\n');
    expect(s.lines.length).toBeGreaterThan(0);
    for (const secret of SECRETS) expect(visible).not.toContain(secret);
  });

  it('removes all four handlers on dispose', () => {
    const s = setup();
    const registered = [...s.ipc.handlers.keys()].sort();

    s.dispose();

    expect(registered).toEqual(
      [
        CHANNEL.kubernetesDetect,
        CHANNEL.kubernetesDisconnect,
        CHANNEL.kubernetesStatus,
        CHANNEL.kubernetesUse,
      ].sort(),
    );
    expect(s.ipc.handlers.size).toBe(0);
  });
});
