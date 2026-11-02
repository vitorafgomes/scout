import { describe, expect, it, mock } from 'bun:test';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { CHANNEL, VaultUnavailableError, readIpcError, type WorkerStatus } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { readAllowedUserIds, registerAccessHandlers } from '../src/ipc/access.js';
import { restartWorkerIfRunning } from '../src/ipc/github.js';
import { buildEnv } from '../src/worker/build-env.js';
import { createSupervisor } from '../src/worker/supervisor.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

const T0 = Date.parse('2026-10-28T12:00:00.000Z');

function setup(workerState: WorkerStatus['state'] = 'running', code = 482913) {
  const handlers = new Map<string, IpcHandler>();
  const ipc: Pick<IpcMain, 'handle' | 'removeHandler'> = {
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
  const entries = new Map<string, string>();
  const vault = {
    get: async (key: string) => entries.get(key) ?? null,
    store: async (key: string, value: string) => {
      entries.set(key, value);
    },
    delete: async (key: string) => entries.delete(key),
  };
  const calls: string[] = [];
  const worker = {
    status: { state: workerState, restarts: 0 } as WorkerStatus,
    async stop() {
      calls.push('stop');
      return { exitCode: 0 };
    },
    async start() {
      calls.push('start');
      return { pid: 42 };
    },
  };
  const { logger, lines } = recordingLogger();
  let restart: Promise<void> = Promise.resolve();
  let clock = T0;
  const access = registerAccessHandlers(ipc, {
    vault,
    onAllowedUsersChanged: () => {
      restart = restartWorkerIfRunning(worker, logger);
    },
    logger,
    randomInt: () => code,
    now: () => clock,
  });
  const call = (channel: string, req?: unknown): Promise<unknown> => {
    const handler = handlers.get(channel);
    if (handler === undefined) throw new Error(`no handler for ${channel}`);
    return Promise.resolve(handler(NULL_EVENT, req));
  };
  const set = (userIds: unknown) => call(CHANNEL.accessSet, { userIds });
  const startPairing = () => call(CHANNEL.accessStartPairing);
  const advance = (ms: number) => {
    clock += ms;
  };
  return {
    handlers,
    entries,
    vault,
    calls,
    lines,
    call,
    set,
    access,
    startPairing,
    advance,
    dispose: access.dispose,
    restarted: () => restart,
  };
}

async function rejection(promise: Promise<unknown>): Promise<{ code: string | null }> {
  try {
    await promise;
  } catch (err: unknown) {
    return readIpcError(new Error((err as Error).message));
  }
  throw new Error('expected a rejection');
}

describe('Access IPC', () => {
  it('stores the trimmed, de-duplicated ids, returns them and restarts a running worker', async () => {
    const s = setup();

    const result = await s.set([' 111 ', 'U02ABC', '111', '29:human@x.y-z_1']);
    await s.restarted();

    expect(result).toEqual({ userIds: ['111', 'U02ABC', '29:human@x.y-z_1'] });
    expect(await s.call(CHANNEL.accessGet)).toEqual({
      userIds: ['111', 'U02ABC', '29:human@x.y-z_1'],
    });
    expect(s.calls).toEqual(['stop', 'start']);
  });

  it.each([
    ['an empty id', ['111', '   ']],
    ['an id longer than 64 characters', ['a'.repeat(65)]],
    ['an id with a space inside', ['111 222']],
    ['an id with a character outside the allowed set', ['<script>']],
    ['a value that is not a string', [111]],
  ])('rejects %s and stores nothing', async (_label, ids) => {
    const s = setup();

    const err = await rejection(s.set(ids));

    expect(err.code).toBe('access/invalid-id');
    expect(s.entries.size).toBe(0);
    expect(s.calls).toEqual([]);
  });

  it('accepts an id of exactly 64 characters', async () => {
    const s = setup();

    expect(await s.set(['a'.repeat(64)])).toEqual({ userIds: ['a'.repeat(64)] });
  });

  it('accepts 50 ids and rejects 51', async () => {
    const s = setup();
    const ids = Array.from({ length: 51 }, (_, i) => `user-${i}`);

    expect(await s.set(ids.slice(0, 50))).toEqual({ userIds: ids.slice(0, 50) });
    const err = await rejection(s.set(ids));

    expect(err.code).toBe('access/too-many');
    expect(await readAllowedUserIds(s.vault)).toHaveLength(50);
  });

  it('counts the cap after de-duplication', async () => {
    const s = setup();
    const ids = [...Array.from({ length: 50 }, (_, i) => `user-${i}`), 'user-0', ' user-1 '];

    expect(((await s.set(ids)) as { userIds: string[] }).userIds).toHaveLength(50);
  });

  it('rejects a request without a list', async () => {
    const s = setup();

    expect((await rejection(s.call(CHANNEL.accessSet, { userIds: 'u1' }))).code).toBe(
      'access/invalid-request',
    );
  });

  it('leaves the worker alone when the list does not change', async () => {
    const s = setup();
    await s.set(['111']);
    await s.restarted();
    s.calls.length = 0;

    await s.set([' 111']);
    await s.restarted();

    expect(s.calls).toEqual([]);
  });

  it('removing the last id deletes the list, so the worker gets no allowed ids', async () => {
    const s = setup();
    await s.set(['111']);

    await s.set([]);
    const env = await buildEnv({
      vaultGet: async (key) => (key === 'discord.bot-token' ? 'tok' : null),
      allowedUserIds: await readAllowedUserIds(s.vault),
    });

    expect(s.entries.has('access.allowed-users')).toBe(false);
    expect(Object.keys(env).filter((k) => k.startsWith('SCOUT__ACCESS__'))).toEqual([]);
  });

  it('the stored list reaches the worker as one variable per id', async () => {
    const s = setup();
    await s.set(['111', 'U02ABC']);

    const env = await buildEnv({
      vaultGet: async (key) => (key === 'discord.bot-token' ? 'tok' : null),
      allowedUserIds: await readAllowedUserIds(s.vault),
    });

    expect(env['SCOUT__ACCESS__ALLOWEDUSERIDS__0']).toBe('111');
    expect(env['SCOUT__ACCESS__ALLOWEDUSERIDS__1']).toBe('U02ABC');
  });

  it('dispose removes the three channels', () => {
    const s = setup();

    s.dispose();

    expect([...s.handlers.keys()]).toEqual([]);
  });

  it('a vault that fails to store rejects the change, keeps the previous list and leaves the worker alone', async () => {
    const s = setup();
    await s.set(['111']);
    await s.restarted();
    s.calls.length = 0;
    s.vault.store = async () => {
      throw new VaultUnavailableError();
    };

    const err = await rejection(s.set(['111', '222']));
    await s.restarted();

    expect(err.code).toBe('vault/unavailable');
    expect(await readAllowedUserIds(s.vault)).toEqual(['111']);
    expect(s.calls).toEqual([]);
  });

  describe('pairing from chat', () => {
    const tokenOnly = async (key: string) => (key === 'discord.bot-token' ? 'tok' : null);

    it('issues a 6-digit code valid for 10 minutes, restarts a running worker and passes the code to it', async () => {
      const s = setup();

      const pairing = await s.startPairing();
      await s.restarted();
      const env = await buildEnv({ vaultGet: tokenOnly, pairing: s.access.pendingPairing() });

      expect(pairing).toEqual({ code: '482913', expiresAt: '2026-10-28T12:10:00.000Z' });
      expect(s.calls).toEqual(['stop', 'start']);
      expect(env['SCOUT__ACCESS__PAIRINGCODE']).toBe('482913');
      expect(env['SCOUT__ACCESS__PAIRINGEXPIRESAT']).toBe('2026-10-28T12:10:00.000Z');
    });

    it('pads a small random number to six digits', async () => {
      const s = setup('running', 7);

      expect(await s.startPairing()).toMatchObject({ code: '000007' });
    });

    it('draws the code from crypto when no generator is injected', async () => {
      const handlers = new Map<string, IpcHandler>();
      const access = registerAccessHandlers(
        { handle: (channel, handler) => void handlers.set(channel, handler), removeHandler() {} },
        {
          vault: { get: async () => null, store: async () => {}, delete: async () => true },
          onAllowedUsersChanged() {},
          logger: recordingLogger().logger,
        },
      );

      await handlers.get(CHANNEL.accessStartPairing)?.(NULL_EVENT);

      expect(access.pendingPairing()?.code).toMatch(/^[0-9]{6}$/);
    });

    it('stops passing the code to the worker once 10 minutes have passed', async () => {
      const s = setup();
      await s.startPairing();

      s.advance(10 * 60_000);
      const env = await buildEnv({ vaultGet: tokenOnly, pairing: s.access.pendingPairing() });

      expect(s.access.pendingPairing()).toBeNull();
      expect(env['SCOUT__ACCESS__PAIRINGCODE']).toBeUndefined();
    });

    it('a SCOUT_PAIRED line from the worker adds the id, clears the code and the restarted worker allows that user', async () => {
      const s = setup();
      await s.set(['999']);
      await s.startPairing();
      await s.restarted();
      s.calls.length = 0;
      const child = Object.assign(new EventEmitter(), {
        pid: 7,
        stdout: new PassThrough(),
        stderr: new PassThrough(),
        kill() {},
      });
      let reported: Promise<void> = Promise.resolve();
      let lineSeen!: () => void;
      const seen = new Promise<void>((resolve) => {
        lineSeen = resolve;
      });
      const supervisor = createSupervisor({
        spawn: mock(() => child as never),
        logger: recordingLogger().logger,
        resolveBinary: () => '/fake/worker',
        checkBinaryExists: () => true,
        buildEnv: async () => ({}),
        onPaired: (userId) => {
          reported = s.access.paired(userId);
          lineSeen();
        },
      });
      const started = supervisor.start();
      child.stdout.write('SCOUT_READY\n');
      await started;

      child.stdout.write('SCOUT_PAIRED=111\n');
      await Promise.race([seen, new Promise((resolve) => setTimeout(resolve, 1000))]);
      await reported;
      await s.restarted();
      const env = await buildEnv({
        vaultGet: tokenOnly,
        allowedUserIds: await readAllowedUserIds(s.vault),
        pairing: s.access.pendingPairing(),
      });

      expect(await readAllowedUserIds(s.vault)).toEqual(['999', '111']);
      expect(s.access.pendingPairing()).toBeNull();
      expect(s.calls).toEqual(['stop', 'start']);
      expect(env['SCOUT__ACCESS__ALLOWEDUSERIDS__1']).toBe('111');
      expect(env['SCOUT__ACCESS__PAIRINGCODE']).toBeUndefined();
    });

    it('ignores a pairing report when no pairing is pending', async () => {
      const s = setup();

      await s.access.paired('111');
      await s.restarted();

      expect(await readAllowedUserIds(s.vault)).toEqual([]);
      expect(s.calls).toEqual([]);
    });

    it('ignores a pairing report after the code expired', async () => {
      const s = setup();
      await s.startPairing();
      await s.restarted();
      s.calls.length = 0;
      s.advance(10 * 60_000);

      await s.access.paired('111');

      expect(await readAllowedUserIds(s.vault)).toEqual([]);
      expect(s.calls).toEqual([]);
    });

    it('accepts only the first report for one code', async () => {
      const s = setup();
      await s.startPairing();

      await s.access.paired('111');
      await s.access.paired('333');

      expect(await readAllowedUserIds(s.vault)).toEqual(['111']);
    });

    it('rejects an invalid id from the line and stores nothing', async () => {
      const s = setup();
      await s.startPairing();
      await s.restarted();
      s.calls.length = 0;

      await s.access.paired('111 OR 1=1');
      await s.restarted();

      expect(s.entries.has('access.allowed-users')).toBe(false);
      expect(await readAllowedUserIds(s.vault)).toEqual([]);
      expect(s.calls).toEqual([]);
    });

    it('a vault that fails while adding the paired id leaves the list and the worker alone', async () => {
      const s = setup();
      await s.startPairing();
      await s.restarted();
      s.calls.length = 0;
      s.vault.store = async () => {
        throw new VaultUnavailableError();
      };

      await s.access.paired('111');
      await s.restarted();

      expect(await readAllowedUserIds(s.vault)).toEqual([]);
      expect(s.calls).toEqual([]);
      expect(s.lines.filter((line) => line.includes('vault/unavailable'))).toHaveLength(1);
    });

    it('a burned code from the worker stops reaching the worker and a later pairing report is ignored', async () => {
      const s = setup();
      await s.startPairing();
      await s.restarted();
      s.calls.length = 0;

      s.access.pairingBurned();
      await s.access.paired('111');
      const env = await buildEnv({ vaultGet: tokenOnly, pairing: s.access.pendingPairing() });

      expect(s.access.pendingPairing()).toBeNull();
      expect(env['SCOUT__ACCESS__PAIRINGCODE']).toBeUndefined();
      expect(await readAllowedUserIds(s.vault)).toEqual([]);
      expect(s.calls).toEqual([]);
    });

    it('never logs the pairing code', async () => {
      const s = setup();
      await s.startPairing();
      await s.access.paired('111');
      await s.access.paired('333');

      expect(s.lines.length).toBeGreaterThan(0);
      expect(s.lines.filter((line) => line.includes('482913'))).toEqual([]);
    });
  });
});
