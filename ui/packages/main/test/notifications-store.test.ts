import { describe, expect, it } from 'bun:test';
import { CHANNEL, readIpcError, type AppNotification } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import {
  MAX_NOTIFICATIONS,
  NotificationStore,
  SAVE_DELAY_MS,
  pushToWindows,
  registerNotificationHandlers,
  type NotificationInput,
} from '../src/notifications/notification-store.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;
const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

function entry(id: string, extra: Partial<NotificationInput> = {}): NotificationInput {
  return { id, kind: 'done', title: `Title ${id}`, detail: '', source: 'worker', ...extra };
}

function setup(saved: string | null | Error = null) {
  const clock = { now: 1_000 };
  const vault = new Map<string, string>();
  const writes: string[] = [];
  const timers: { ms: number; callback: () => void; cancelled: boolean }[] = [];
  const pushes: (readonly AppNotification[])[] = [];
  const { logger, lines } = recordingLogger();
  let failWrite = false;
  const store = new NotificationStore({
    vault: {
      async get(key) {
        if (saved instanceof Error) throw saved;
        return key === 'app.notifications' ? saved : null;
      },
      async store(key, value) {
        if (failWrite) throw new Error('disk full');
        vault.set(key, value);
        writes.push(value);
      },
    },
    push: (list) => pushes.push(list),
    now: () => clock.now,
    setTimer: (ms, callback) => {
      const timer = { ms, callback, cancelled: false };
      timers.push(timer);
      return () => {
        timer.cancelled = true;
      };
    },
    logger,
  });
  const fireTimers = async (): Promise<void> => {
    for (const timer of timers.splice(0)) if (!timer.cancelled) timer.callback();
    await Promise.resolve();
    await Promise.resolve();
  };
  return {
    store,
    clock,
    vault,
    writes,
    timers,
    pushes,
    lines,
    fireTimers,
    failWrites: () => {
      failWrite = true;
    },
  };
}

function setupIpc(store: NotificationStore) {
  const handlers = new Map<string, IpcHandler>();
  const ipc: Pick<IpcMain, 'handle' | 'removeHandler'> = {
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
  const dispose = registerNotificationHandlers(ipc, store);
  const call = async (channel: string, arg?: unknown): Promise<unknown> =>
    handlers.get(channel)!(NULL_EVENT, arg);
  return { handlers, dispose, call };
}

async function refusal(promise: Promise<unknown>): Promise<string> {
  try {
    await promise;
  } catch (err: unknown) {
    return readIpcError(err).message;
  }
  throw new Error('expected a refusal');
}

describe('NotificationStore', () => {
  it('holds at most 200 entries, newest first, dropping the oldest', () => {
    const { store, clock } = setup();

    for (let i = 1; i <= MAX_NOTIFICATIONS + 5; i++) {
      clock.now = i;
      store.record(entry(`n${i}`));
    }

    const ids = store.list().map((n) => n.id);
    expect(ids.length).toBe(MAX_NOTIFICATIONS);
    expect(ids[0]).toBe(`n${MAX_NOTIFICATIONS + 5}`);
    expect(ids.at(-1)).toBe('n6');
    expect(ids).not.toContain('n5');
  });

  it('a new entry is unread and stamped with the time it arrived', () => {
    const { store, clock } = setup();
    clock.now = 42;

    store.record(entry('a'));

    expect(store.list()).toEqual([
      {
        id: 'a',
        kind: 'done',
        title: 'Title a',
        detail: '',
        source: 'worker',
        at: 42,
        read: false,
      },
    ]);
  });

  it('an entry recorded again with its id is updated in place, unread, not duplicated', () => {
    const { store, clock } = setup();
    store.record(entry('voice-no-model:1', { kind: 'running', progress: 25 }));
    clock.now = 2_000;
    store.record(entry('other'));
    store.markRead();
    clock.now = 3_000;

    store.record(entry('voice-no-model:1', { kind: 'running', progress: 50 }));

    expect(store.list().map((n) => [n.id, n.progress, n.read])).toEqual([
      ['voice-no-model:1', 50, false],
      ['other', undefined, true],
    ]);
  });

  const jobUpdates: readonly [string, NotificationInput, NotificationInput, boolean][] = [
    [
      'a running audit whose next area ends stays read, its time moved on',
      entry('audit:acme/shop', { kind: 'running', detail: 'Area 3 of 8 done: src/orders' }),
      entry('audit:acme/shop', { kind: 'running', detail: 'Area 4 of 8 done: src/billing' }),
      true,
    ],
    [
      'a running audit that pauses is unread again',
      entry('audit:acme/shop', { kind: 'running' }),
      entry('audit:acme/shop', { kind: 'paused', detail: 'Paused by you' }),
      false,
    ],
    [
      'a running audit that is stopped or finished is unread again',
      entry('audit:acme/shop', { kind: 'running' }),
      entry('audit:acme/shop', { kind: 'done', title: 'Audit of acme/shop is done' }),
      false,
    ],
    [
      'a running test run that fails is unread again',
      entry('tests:acme/api#41', { kind: 'running' }),
      entry('tests:acme/api#41', { kind: 'failed' }),
      false,
    ],
    [
      'a running audit with a new note is unread again',
      entry('audit:acme/shop', { kind: 'running' }),
      entry('audit:acme/shop', { kind: 'running', note: 'Not confirmed.' }),
      false,
    ],
    [
      'a running audit that keeps its note through the next area stays read',
      entry('audit:acme/shop', { kind: 'running', note: 'Not confirmed.' }),
      entry('audit:acme/shop', { kind: 'running', detail: 'Area 4 of 8', note: 'Not confirmed.' }),
      true,
    ],
    [
      'a running test run recorded again running stays read',
      entry('tests:acme/api#41', { kind: 'running' }),
      entry('tests:acme/api#41', { kind: 'running', detail: 'Running.' }),
      true,
    ],
  ];
  for (const [name, before, after, staysRead] of jobUpdates) {
    it(`${name}, and the badge follows`, () => {
      const { store, clock } = setup();
      store.record(before);
      store.markRead();
      clock.now = 2_000;

      store.record(after);

      expect({
        read: store.list()[0]!.read,
        badge: store.list().filter((n) => !n.read).length,
        at: store.list()[0]!.at,
      }).toEqual({ read: staysRead, badge: staysRead ? 0 : 1, at: 2_000 });
    });
  }

  it('the same content recorded again changes nothing and pushes nothing', () => {
    const { store, pushes } = setup();
    store.record(entry('github:1', { at: 500, kind: 'github' }));
    store.markRead(['github:1']);
    const pushed = pushes.length;

    store.record(entry('github:1', { at: 500, kind: 'github' }));

    expect(pushes.length).toBe(pushed);
    expect(store.list()[0]!.read).toBe(true);
  });

  it('marks the named entries read, and every entry without ids', () => {
    const { store, clock } = setup();
    for (const id of ['a', 'b', 'c']) {
      clock.now += 1;
      store.record(entry(id));
    }

    const some = store.markRead(['a', 'c']).map((n) => [n.id, n.read]);
    const all = store.markRead().map((n) => [n.id, n.read]);

    expect(some).toEqual([
      ['c', true],
      ['b', false],
      ['a', true],
    ]);
    expect(all).toEqual([
      ['c', true],
      ['b', true],
      ['a', true],
    ]);
  });

  it('pushes the whole list on every change', () => {
    const { store, pushes } = setup();

    store.record(entry('a'));
    store.markRead();

    expect(pushes.map((list) => list.map((n) => [n.id, n.read]))).toEqual([
      [['a', false]],
      [['a', true]],
    ]);
  });

  it('writes the list to the vault key app.notifications once, a second after the last change', async () => {
    const { store, vault, writes, timers, fireTimers } = setup();

    store.record(entry('a'));
    store.record(entry('b'));
    expect(writes).toEqual([]);
    expect(timers.filter((t) => !t.cancelled).map((t) => t.ms)).toEqual([SAVE_DELAY_MS]);
    await fireTimers();

    expect(writes.length).toBe(1);
    expect(JSON.parse(vault.get('app.notifications')!)).toEqual(store.list());
  });

  it('reads the saved list on start, newest first', async () => {
    const saved: AppNotification[] = [
      { ...entry('old'), at: 10, read: true } as AppNotification,
      { ...entry('new', { url: 'https://github.com/acme/shop/pull/5' }), at: 20, read: false },
    ];
    const { store, pushes } = setup(JSON.stringify(saved));

    await store.load();

    expect(store.list().map((n) => [n.id, n.read, n.url])).toEqual([
      ['new', false, 'https://github.com/acme/shop/pull/5'],
      ['old', true, undefined],
    ]);
    expect(pushes.length).toBe(1);
  });

  it('a saved entry with its actions loads back with them, beside the entries without', async () => {
    const actions = [
      { id: 'whisper-download-base', label: 'Download the base model (142 MB)' },
      { id: 'open-models-transcription', label: 'Open Models, Transcription' },
    ] as const;
    const saved: AppNotification[] = [
      { ...entry('voice', { kind: 'failed', actions }), at: 20, read: false },
      { ...entry('old'), at: 10, read: true },
    ];
    const { store } = setup(JSON.stringify(saved));

    await store.load();

    expect(store.list().map((n) => [n.id, n.actions])).toEqual([
      ['voice', actions],
      ['old', undefined],
    ]);
  });

  it('a saved entry with an unknown action is dropped alone rather than offered, and the log says so', async () => {
    const saved = [
      { ...entry('voice'), at: 20, read: false, actions: [{ id: 'rm -rf', label: 'x' }] },
      { ...entry('kept'), at: 10, read: true },
    ];
    const { store, lines } = setup(JSON.stringify(saved));

    await store.load();

    expect(store.list().map((n) => n.id)).toEqual(['kept']);
    expect(lines.some((l) => l.includes('dropped saved entries Scout does not recognise'))).toBe(
      true,
    );
  });

  it('an entry recorded again with other actions is updated, not ignored', () => {
    const { store, pushes } = setup();
    store.record(entry('voice', { actions: [{ id: 'open-models-transcription', label: 'Open' }] }));

    store.record(entry('voice'));

    expect(pushes.length).toBe(2);
    expect(store.list()[0]!.actions).toBeUndefined();
  });

  const controlChanges: readonly [
    string,
    Partial<NotificationInput>,
    Partial<NotificationInput>,
  ][] = [
    ['a click asked', {}, { control: { action: 'stop', phase: 'asking' } }],
    [
      'a click queued',
      { control: { action: 'stop', phase: 'asking' } },
      { control: { action: 'stop', phase: 'waiting' } },
    ],
    [
      'another action',
      { control: { action: 'stop', phase: 'waiting' } },
      { control: { action: 'pause', phase: 'waiting' } },
    ],
    ['a click answered', { control: { action: 'stop', phase: 'waiting' } }, {}],
    ['a note shown', {}, { note: 'No audit of acme/shop is running.' }],
    ['a note changed', { note: 'one' }, { note: 'two' }],
    ['a note cleared', { note: 'one' }, {}],
  ];
  for (const [name, before, after] of controlChanges) {
    it(`a job entry recorded again with ${name} is updated, not ignored`, () => {
      const { store, pushes } = setup();
      const job = { kind: 'running', at: 5 } as const;
      store.record(entry('audit:acme/shop', { ...job, ...before }));

      store.record(entry('audit:acme/shop', { ...job, ...after }));

      expect(pushes.length).toBe(2);
      expect([store.list()[0]!.control, store.list()[0]!.note]).toEqual([
        after.control,
        after.note,
      ]);
    });
  }

  it('a saved click still unanswered is dropped from its entry, which keeps its note, and a saved note that is not text drops the entry', async () => {
    const saved = [
      {
        ...entry('audit:acme/shop', {
          kind: 'paused',
          control: { action: 'continue', phase: 'waiting' },
          note: 'No paused audit of acme/shop to continue.',
        }),
        at: 30,
        read: true,
      },
      {
        ...entry('tests:acme/api#41', {
          kind: 'running',
          control: { action: 'stop', phase: 'asking' },
        }),
        at: 20,
        read: false,
      },
      { ...entry('audit:acme/web'), note: 7, at: 10, read: false },
    ];
    const { store } = setup(JSON.stringify(saved));

    await store.load();

    expect(store.list().map((n) => [n.id, n.kind, n.control, n.note])).toEqual([
      ['audit:acme/shop', 'paused', undefined, 'No paused audit of acme/shop to continue.'],
      ['tests:acme/api#41', 'failed', undefined, undefined],
    ]);
  });

  it('a corrupt saved value starts empty and says so in the log', async () => {
    for (const corrupt of ['{not json', '{"v":1}']) {
      const { store, lines } = setup(corrupt);

      await store.load();

      expect(store.list()).toEqual([]);
      expect(lines.some((l) => l.includes('the saved list is corrupt; starting empty'))).toBe(true);
    }
  });

  it('a vault that cannot be read starts empty and says so in the log', async () => {
    const { store, lines } = setup(new Error('vault locked'));

    await store.load();

    expect(store.list()).toEqual([]);
    expect(lines.some((l) => l.includes('the saved list could not be read'))).toBe(true);
  });

  it('keeps what was recorded before the saved list was read', async () => {
    const saved = [{ ...entry('saved'), at: 10, read: true }];
    const { store, clock } = setup(JSON.stringify(saved));
    clock.now = 50;
    store.record(entry('early'));

    await store.load();

    expect(store.list().map((n) => n.id)).toEqual(['early', 'saved']);
  });

  it('a failed write keeps the list in memory and logs without the content', async () => {
    const { store, lines, fireTimers, failWrites } = setup();
    failWrites();
    store.record(entry('a', { title: 'Secret title' }));

    await fireTimers();

    expect(store.list().map((n) => n.id)).toEqual(['a']);
    expect(lines.some((l) => l.includes('the list was not saved'))).toBe(true);
    expect(lines.join('\n')).not.toContain('Secret title');
  });

  it('flush writes a pending change at once', async () => {
    const { store, writes } = setup();
    store.record(entry('a'));

    await store.flush();

    expect(writes.length).toBe(1);
  });
});

describe('pushToWindows', () => {
  it('sends scout:notifications with the list to every open window and skips a closed one', () => {
    const sent: [string, string, unknown][] = [];
    const window = (name: string, destroyed: boolean) => ({
      isDestroyed: () => destroyed,
      webContents: {
        send: (channel: string, payload: unknown) => sent.push([name, channel, payload]),
      },
    });
    const list: AppNotification[] = [{ ...entry('a'), at: 1, read: false } as AppNotification];

    pushToWindows(() => [window('main', false), window('closed', true), window('quick', false)])(
      list,
    );

    expect(sent).toEqual([
      ['main', 'scout:notifications', list],
      ['quick', 'scout:notifications', list],
    ]);
  });
});

describe('notification channels', () => {
  it('lists the entries and marks read by ids or all', async () => {
    const { store, clock } = setup();
    store.record(entry('a'));
    clock.now += 1;
    store.record(entry('b'));
    const { call } = setupIpc(store);

    const listed = (await call(CHANNEL.notificationsList)) as AppNotification[];
    const one = (await call(CHANNEL.notificationsMarkRead, { ids: ['a'] })) as AppNotification[];
    const all = (await call(CHANNEL.notificationsMarkRead, {})) as AppNotification[];

    expect(listed.map((n) => n.id)).toEqual(['b', 'a']);
    expect(one.map((n) => n.read)).toEqual([false, true]);
    expect(all.map((n) => n.read)).toEqual([true, true]);
  });

  it('refuses ids that are not a list of strings', async () => {
    const { store } = setup();
    const { call } = setupIpc(store);

    for (const bad of [{ ids: 'a' }, { ids: [1] }, { ids: Array(201).fill('a') }]) {
      expect(await refusal(call(CHANNEL.notificationsMarkRead, bad))).toBe(
        'Scout could not read that request.',
      );
    }
  });

  it('keeps a notice the renderer showed: an error notice as failed, any other as done', async () => {
    const { store } = setup();
    const { call } = setupIpc(store);

    await call(CHANNEL.noticeRecord, { message: 'Writes allowed for YouTube', kind: 'success' });
    await call(CHANNEL.noticeRecord, { message: 'Could not save', kind: 'error' });

    expect(store.list().map((n) => [n.kind, n.title, n.source])).toEqual([
      ['failed', 'Could not save', 'notice'],
      ['done', 'Writes allowed for YouTube', 'notice'],
    ]);
    expect(new Set(store.list().map((n) => n.id)).size).toBe(2);
  });

  it('refuses a notice without text, with a long text or an unknown kind', async () => {
    const { store } = setup();
    const { call } = setupIpc(store);

    for (const bad of [
      { message: '  ', kind: 'info' },
      { message: 'x'.repeat(1_001), kind: 'info' },
      { message: 'Saved', kind: 'loud' },
      null,
    ]) {
      expect(await refusal(call(CHANNEL.noticeRecord, bad))).toBe(
        'Scout could not read that request.',
      );
    }
    expect(store.list()).toEqual([]);
  });

  it('dispose removes the three handlers', () => {
    const { store } = setup();
    const { handlers, dispose } = setupIpc(store);

    dispose();

    expect([...handlers.keys()]).toEqual([]);
  });
});

describe('NotificationStore: a saved list it cannot fully read', () => {
  it('one unrecognised saved entry is dropped alone and the next save keeps the others', async () => {
    const saved = [
      { ...entry('good-1'), at: 30, read: true },
      { ...entry('good-2'), at: 20, read: false },
      {
        ...entry('voice'),
        at: 10,
        read: false,
        actions: [{ id: 'from-a-newer-version', label: 'x' }],
      },
    ];
    const { store, clock, writes, fireTimers } = setup(JSON.stringify(saved));
    await store.load();
    clock.now = 50;

    store.record(entry('new'));
    await fireTimers();

    const kept = (JSON.parse(writes.at(-1)!) as AppNotification[]).map((n) => n.id);
    expect(kept).toEqual(['new', 'good-1', 'good-2']);
  });

  it('a saved list that failed to read is never saved over', async () => {
    const { store, writes, fireTimers } = setup(new Error('keyring locked'));
    await store.load();

    store.record(entry('new'));
    await fireTimers();

    expect(writes).toEqual([]);
  });

  it('an entry saved as running loads back as failed because Scout closed before it finished', async () => {
    const saved = [
      { ...entry('voice-no-model:1', { kind: 'running', progress: 40 }), at: 10, read: false },
    ];
    const { store } = setup(JSON.stringify(saved));

    await store.load();

    expect(store.list().map((n) => [n.id, n.kind, n.detail, n.progress])).toEqual([
      ['voice-no-model:1', 'failed', 'Scout closed before this finished.', undefined],
    ]);
  });

  const reopened: readonly [string, AppNotification, unknown][] = [
    [
      'a running audit, which the worker resumes, loads back in progress with its last area, its share and its read state, its unanswered click dropped',
      {
        ...entry('audit:acme/shop', {
          kind: 'running',
          title: 'Audit of acme/shop',
          detail: 'Area 3 of 8 done: src/orders',
          progress: 37,
          control: { action: 'pause', phase: 'waiting' },
        }),
        at: 10,
        read: true,
      },
      {
        kind: 'running',
        title: 'Audit of acme/shop',
        detail: 'Area 3 of 8 done: src/orders',
        progress: 37,
        control: undefined,
        read: true,
      },
    ],
    [
      'a running test run, which cannot resume, loads back as failed because Scout closed before it finished',
      {
        ...entry('tests:acme/api#41', {
          kind: 'running',
          title: 'Tests for acme/api#41',
          detail: 'Running in a container on this computer.',
        }),
        at: 10,
        read: false,
      },
      {
        kind: 'failed',
        title: 'Tests for acme/api#41',
        detail: 'Scout closed before this finished.',
        progress: undefined,
        control: undefined,
        read: false,
      },
    ],
    [
      'a paused audit loads back paused',
      {
        ...entry('audit:acme/web', {
          kind: 'paused',
          title: 'Audit of acme/web paused',
          detail: 'Paused by you · area 5 of 9 done',
        }),
        at: 10,
        read: true,
      },
      {
        kind: 'paused',
        title: 'Audit of acme/web paused',
        detail: 'Paused by you · area 5 of 9 done',
        progress: undefined,
        control: undefined,
        read: true,
      },
    ],
  ];
  for (const [name, saved, after] of reopened) {
    it(`when Scout is reopened, ${name}`, async () => {
      const { store } = setup(JSON.stringify([saved]));

      await store.load();

      const n = store.list()[0]!;
      expect({
        kind: n.kind,
        title: n.title,
        detail: n.detail,
        progress: n.progress,
        control: n.control,
        read: n.read,
      }).toEqual(after as never);
    });
  }

  it('an entry recorded as running before the saved list was read stays running', async () => {
    const { store, clock } = setup(JSON.stringify([]));
    clock.now = 50;
    store.record(entry('audit:acme/api', { kind: 'running', progress: 10 }));

    await store.load();

    expect(store.list().map((n) => n.kind)).toEqual(['running']);
  });
});

describe('NotificationStore: removing entries', () => {
  it('removes the entries selected, keeps the rest, pushes the list and saves it', async () => {
    const { store, writes, pushes, fireTimers } = setup();
    store.record(entry('github:1', { kind: 'github', source: 'github' }));
    store.record(entry('worker:1'));
    await fireTimers();
    const pushesBefore = pushes.length;

    store.remove((n) => n.source === 'github');
    await fireTimers();

    expect(store.list().map((n) => n.id)).toEqual(['worker:1']);
    expect(pushes.length).toBe(pushesBefore + 1);
    expect((JSON.parse(writes.at(-1)!) as AppNotification[]).map((n) => n.id)).toEqual([
      'worker:1',
    ]);
  });

  it('removing nothing pushes nothing', () => {
    const { store, pushes } = setup();
    store.record(entry('worker:1'));

    store.remove((n) => n.source === 'github');

    expect(pushes.length).toBe(1);
  });
});

describe('NotificationStore: a GitHub entry the owner read', () => {
  const thread = (extra: Partial<NotificationInput> = {}) =>
    entry('github:7', {
      kind: 'github',
      source: 'github',
      detail: 'You were mentioned in acme/api#7',
      comments: 2,
      ...extra,
    });

  const cases: [string, Partial<NotificationInput>, boolean][] = [
    ['a later update with the same reason and comment count stays read', { at: 2_000 }, true],
    ['a label or a push, fewer comments counted, stays read', { at: 2_000, comments: 1 }, true],
    ['more comments make it unread again', { at: 2_000, comments: 3 }, false],
    [
      'one saved before comment counts were kept stays read and takes the count',
      { at: 2_000, comments: 5 },
      true,
    ],
    [
      'another reason makes it unread again',
      { at: 2_000, detail: 'Review requested on acme/api#7' },
      false,
    ],
  ];

  for (const [name, update, staysRead] of cases) {
    it(name, () => {
      const { store } = setup();
      const before = name.startsWith('one saved before')
        ? entry('github:7', {
            kind: 'github',
            source: 'github',
            detail: 'You were mentioned in acme/api#7',
            at: 1_000,
          })
        : thread({ at: 1_000 });
      store.record(before);
      store.markRead();

      store.record(thread(update));

      expect(store.list()[0]!.read).toBe(staysRead);
      expect(store.list()[0]!.comments).toBe(update.comments ?? 2);
    });
  }

  it('an entry from another source recorded again is unread, as before', () => {
    const { store } = setup();
    store.record(entry('verdict:act-1', { kind: 'running', source: 'verdict' }));
    store.markRead();

    store.record(
      entry('verdict:act-1', { kind: 'running', detail: 'Approve: running', source: 'verdict' }),
    );

    expect(store.list()[0]!.read).toBe(false);
  });
});
