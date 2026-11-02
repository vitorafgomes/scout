import { describe, expect, it } from 'bun:test';
import { VaultUnavailableError, type WorkerStatus } from '@scout/shared';
import { createPresenceNotifier, reportPairing } from '../src/shell/notifications.js';
import { recordingLogger } from './kubernetes-fakes.js';

function memoryVault(entries = new Map<string, string>()) {
  return {
    entries,
    get: async (key: string) => entries.get(key) ?? null,
    store: async (key: string, value: string) => {
      entries.set(key, value);
    },
  };
}

function setup(
  options: {
    supported?: boolean;
    vault?: ReturnType<typeof memoryVault>;
    platform?: NodeJS.Platform;
  } = {},
) {
  const shown: { title: string; body: string }[] = [];
  const vault = options.vault ?? memoryVault();
  const notifier = createPresenceNotifier({
    platform: options.platform ?? 'darwin',
    isSupported: () => options.supported ?? true,
    show: (title, body) => shown.push({ title, body }),
    vault,
    logger: recordingLogger().logger,
  });
  return { notifier, shown, vault };
}

const status = (state: WorkerStatus['state']): WorkerStatus => ({ state, restarts: 5 });

const finalCrash: WorkerStatus = { state: 'crashed', restarts: 5, gaveUp: true };

describe('notification: the worker stopped unexpectedly and could not be restarted', () => {
  it('a crash the supervisor still restarts from emits nothing', () => {
    const { notifier, shown } = setup();

    notifier.workerStatus(status('running'));
    notifier.workerStatus(status('crashed'));
    notifier.workerStatus(status('starting'));
    notifier.workerStatus(status('running'));

    expect(shown).toEqual([]);
  });

  it('fires once with fixed text when the restart policy is exhausted', () => {
    const { notifier, shown } = setup();

    notifier.workerStatus(status('starting'));
    notifier.workerStatus(status('running'));
    notifier.workerStatus(status('crashed'));
    notifier.workerStatus(status('starting'));
    notifier.workerStatus(finalCrash);
    notifier.workerStatus(finalCrash);

    expect(shown).toEqual([
      {
        title: 'Scout',
        body: 'Scout stopped and could not restart. Open Scout to start it again.',
      },
    ]);
  });

  it('says "Scout could not start." when the first start failed', () => {
    const { notifier, shown } = setup();

    notifier.workerStatus(status('idle'));
    notifier.workerStatus(status('starting'));
    notifier.workerStatus(finalCrash);

    expect(shown).toEqual([{ title: 'Scout', body: 'Scout could not start.' }]);
  });

  it('says "Scout could not start." when a start after a stop fails', () => {
    const { notifier, shown } = setup();

    notifier.workerStatus(status('running'));
    notifier.workerStatus(status('stopped'));
    notifier.workerStatus(status('starting'));
    notifier.workerStatus(finalCrash);

    expect(shown).toEqual([{ title: 'Scout', body: 'Scout could not start.' }]);
  });

  it('does not fire for a stop the owner asked for', () => {
    const { notifier, shown } = setup();

    notifier.workerStatus(status('running'));
    notifier.workerStatus(status('stopped'));

    expect(shown).toEqual([]);
  });

  it('fires again for a later terminal crash once the worker ran in between', () => {
    const { notifier, shown } = setup();

    notifier.workerStatus(finalCrash);
    notifier.workerStatus(status('starting'));
    notifier.workerStatus(status('running'));
    notifier.workerStatus(finalCrash);

    expect(shown).toHaveLength(2);
  });
});

describe('notification: pairing completed', () => {
  function access(pending: boolean, allowed: string[]) {
    let code: { code: string } | null = pending ? { code: '482913' } : null;
    return {
      allowed,
      pendingPairing: () => code,
      async paired(userId: string) {
        if (code === null) return;
        code = null;
        allowed.push(userId);
      },
    };
  }

  it('says "<id> can now use Scout" and never the pairing code', async () => {
    const { notifier, shown } = setup();
    const pairing = access(true, []);

    await reportPairing('123456789012345678', pairing, async () => pairing.allowed, notifier);

    expect(shown).toEqual([{ title: 'Scout', body: '123456789012345678 can now use Scout' }]);
    expect(JSON.stringify(shown)).not.toContain('482913');
  });

  it('stays silent when no pairing was pending, so a stray worker line notifies nobody', async () => {
    const { notifier, shown } = setup();
    const pairing = access(false, []);

    await reportPairing('123456789012345678', pairing, async () => pairing.allowed, notifier);

    expect(shown).toEqual([]);
  });

  it('stays silent when access refused to add the id', async () => {
    const { notifier, shown } = setup();
    const refusing = {
      pendingPairing: () => ({ code: '482913' }),
      paired: async () => undefined,
    };

    await reportPairing('not a valid id!', refusing, async () => [], notifier);

    expect(shown).toEqual([]);
  });
});

describe('notification: still running in the tray', () => {
  it('fires the first time only, and remembers it across app runs', async () => {
    const vault = memoryVault();
    const first = setup({ vault });

    await first.notifier.hiddenToTray();
    await first.notifier.hiddenToTray();
    const nextRun = setup({ vault });
    await nextRun.notifier.hiddenToTray();

    expect(first.shown).toEqual([{ title: 'Scout', body: 'Scout is still running in the tray.' }]);
    expect(nextRun.shown).toEqual([]);
  });

  it('on Linux, where a tray icon may be invisible, it points to the app menu too', async () => {
    const { notifier, shown } = setup({ platform: 'linux' });

    await notifier.hiddenToTray();

    expect(shown).toEqual([
      {
        title: 'Scout',
        body: 'Scout is still running. Open it again from the app menu or the tray.',
      },
    ]);
  });

  it('without a keyring it still fires only once in this run', async () => {
    const shown: string[] = [];
    const notifier = createPresenceNotifier({
      platform: 'win32',
      isSupported: () => true,
      show: (_title, body) => shown.push(body),
      vault: {
        get: () => Promise.reject(new VaultUnavailableError()),
        store: () => Promise.reject(new VaultUnavailableError()),
      },
      logger: recordingLogger().logger,
    });

    await notifier.hiddenToTray();
    await notifier.hiddenToTray();

    expect(shown).toEqual(['Scout is still running in the tray.']);
  });
});

describe('notifications on a desktop without support', () => {
  it('shows nothing for any of the three events', async () => {
    const { notifier, shown } = setup({ supported: false });

    notifier.workerStatus(finalCrash);
    notifier.paired('123456789012345678');
    await notifier.hiddenToTray();

    expect(shown).toEqual([]);
  });
});
