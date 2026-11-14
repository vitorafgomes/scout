import { describe, expect, it } from 'bun:test';
import {
  CHANNEL,
  VaultUnavailableError,
  type UpdateNotice,
  type UpdateProgress,
  type UpdateState,
} from '@scout/shared';
import type { BrowserWindow, IpcMain, IpcMainInvokeEvent } from 'electron';
import {
  registerUpdateNotice,
  type Updater,
  type UpdateNoticeDeps,
} from '../src/update/update-notice.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;
const LATEST_URL = 'https://api.github.com/repos/vitorafgomes/scout/releases/latest';
const RELEASES_URL = 'https://api.github.com/repos/vitorafgomes/scout/releases?per_page=30';
const NOW = new Date('2026-11-02T09:30:00.000Z');
const MOUNT = '/tmp/.mount_ScoutAb12';
const APPIMAGE_ENV = { APPIMAGE: '/home/owner/Applications/Scout.AppImage', APPDIR: MOUNT };
const DAY = 86_400_000;

const download = (tag: string, name: string) =>
  `https://github.com/vitorafgomes/scout/releases/download/${tag}/${name}`;

function release(tag = 'v0.2.0', overrides: Record<string, unknown> = {}) {
  const version = tag.replace(/^v/, '');
  return {
    tag_name: tag,
    body: "## What's Changed\r\n* Desktop update notice by @someone\r\n\r\n**Full Changelog**: v0.1.0...v0.2.0",
    html_url: `https://github.com/vitorafgomes/scout/releases/tag/${tag}`,
    published_at: '2026-11-01T18:00:00Z',
    draft: false,
    prerelease: false,
    assets: [
      `SHA256SUMS-linux.txt`,
      `Scout-Desktop-${version}-linux-x86_64.AppImage`,
      `Scout-Desktop-${version}-linux-amd64.deb`,
      `Scout-Desktop-${version}-mac-arm64.dmg`,
      `Scout-Desktop-${version}-win-x64.exe`,
    ].map((name) => ({ name, browser_download_url: download(tag, name) })),
    ...overrides,
  };
}

const PENDING = 'still pending after 1 s';

function within<T>(promise: Promise<T>, ms = 1000): Promise<T | typeof PENDING> {
  return Promise.race([
    promise,
    new Promise<typeof PENDING>((resolve) => setTimeout(() => resolve(PENDING), ms)),
  ]);
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

type Listener = (...args: unknown[]) => void;

/**
 * Stands in for electron-updater's AppUpdater: its three options start at the dangerous value `true`, every
 * property assignment is recorded, and the test decides when the check and the download answer and which
 * events fire.
 */
function fakeUpdater(offered = '0.2.0') {
  const listeners = new Map<string, Set<Listener>>();
  const assigned: [string, unknown][] = [];
  const calls: string[] = [];
  const installs: unknown[][] = [];
  let flagsAtCheck: Record<string, unknown> | null = null;
  let check: () => Promise<{ updateInfo: { version: string } } | null> = async () => ({
    updateInfo: { version: offered },
  });
  let download: () => Promise<unknown> = () =>
    new Promise((resolve, reject) => {
      finishDownload = resolve;
      failDownload = reject;
    });
  let finishDownload: (value: unknown) => void = () => undefined;
  let failDownload: (err: unknown) => void = () => undefined;
  let onInstall: () => void = () => undefined;
  const emit = (event: string, ...args: unknown[]) => {
    for (const listener of [...(listeners.get(event) ?? [])]) listener(...args);
  };
  const target = {
    autoDownload: true,
    autoInstallOnAppQuit: true,
    allowPrerelease: true,
    checkForUpdates() {
      calls.push('checkForUpdates');
      flagsAtCheck = {
        autoDownload: target.autoDownload,
        autoInstallOnAppQuit: target.autoInstallOnAppQuit,
        allowPrerelease: target.allowPrerelease,
      };
      return check();
    },
    downloadUpdate() {
      calls.push('downloadUpdate');
      return download();
    },
    quitAndInstall(...args: unknown[]) {
      installs.push(args);
      onInstall();
    },
    on(event: string, listener: Listener) {
      if (!listeners.has(event)) listeners.set(event, new Set());
      listeners.get(event)!.add(listener);
      return target;
    },
    removeListener(event: string, listener: Listener) {
      listeners.get(event)?.delete(listener);
      return target;
    },
  };
  const updater = new Proxy(target, {
    set(object, key, value) {
      assigned.push([String(key), value]);
      return Reflect.set(object, key, value);
    },
  }) as unknown as Updater;
  return {
    updater,
    assigned,
    calls,
    installs,
    emit,
    flagsAtCheck: () => flagsAtCheck,
    listenerCount: () => [...listeners.values()].reduce((n, set) => n + set.size, 0),
    finishDownload: (value: unknown = ['/tmp/update']) => finishDownload(value),
    /** Fails the way electron-updater does: it emits `error`, then rejects the call. */
    failDownload: (err: unknown) => {
      emit('error', err);
      failDownload(err);
    },
    failCheck: (err: unknown) => {
      check = async () => {
        emit('error', err);
        throw err;
      };
    },
    /** The download a working updater does: progress to 100 %, `update-downloaded`, then it resolves. */
    downloadsCleanly: () => {
      download = async () => {
        emit('download-progress', { percent: 100, transferred: 100, total: 100 });
        emit('update-downloaded', { version: offered });
        return ['/tmp/update'];
      };
    },
    endsDownloadSilently: () => {
      download = async () => ['/tmp/update'];
    },
    failsToApply: (err: unknown) => {
      onInstall = () => emit('error', err);
    },
  };
}

const progressOf = (window: { sent: [string, unknown][] }) =>
  window.sent
    .filter(([channel]) => channel === CHANNEL.updateProgress)
    .map(([, payload]) => payload as UpdateProgress);

const phase = (
  name: UpdateProgress['phase'],
  extra: Partial<UpdateProgress> = {},
): UpdateProgress => ({
  phase: name,
  percent: null,
  transferred: null,
  total: null,
  message: null,
  ...extra,
});

interface FakeWindow {
  destroyed: boolean;
  sent: [string, unknown][];
}

function setup(
  options: {
    packaged?: boolean;
    entries?: Map<string, string>;
    answer?: (signal: AbortSignal | undefined) => Promise<Response>;
    env?: Record<string, string | undefined>;
    vault?: UpdateNoticeDeps['vault'];
    updater?: Updater | null;
    platform?: string;
    arch?: string;
    execPath?: string;
    prepareForRestart?: () => Promise<void>;
    currentVersion?: string;
  } = {},
) {
  const handlers = new Map<string, IpcHandler>();
  const ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'> = {
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
  const entries = options.entries ?? new Map<string, string>();
  let answer: (signal: AbortSignal | undefined) => Promise<Response> =
    options.answer ?? (async () => json(release()));
  const requests: { url: string; init: RequestInit | undefined }[] = [];
  const fetch = (async (url: string, init?: RequestInit) => {
    requests.push({ url, init });
    return answer(init?.signal ?? undefined);
  }) as unknown as typeof globalThis.fetch;
  const windows: FakeWindow[] = [
    { destroyed: false, sent: [] },
    { destroyed: false, sent: [] },
  ];
  const timers: { ms: number; fn: () => void; cancelled: boolean }[] = [];
  const noticeChanges: (string | null)[] = [];
  let recoveries = 0;
  const { logger, lines } = recordingLogger();
  const deps: UpdateNoticeDeps = {
    ipcMain,
    vault: options.vault ?? {
      get: async (key) => entries.get(key) ?? null,
      store: async (key, value) => {
        entries.set(key, value);
      },
    },
    fetch,
    requestTimeoutMs: 20,
    logger,
    currentVersion: options.currentVersion ?? '0.1.0',
    packaged: options.packaged ?? true,
    platform: options.platform ?? 'linux',
    arch: options.arch ?? 'x64',
    env: options.env ?? APPIMAGE_ENV,
    execPath: options.execPath ?? `${MOUNT}/scout-desktop`,
    windows: () =>
      windows.map((w) => ({
        isDestroyed: () => w.destroyed,
        webContents: {
          send: (channel: string, payload: unknown) => w.sent.push([channel, payload]),
        },
      })) as unknown as BrowserWindow[],
    now: () => NOW,
    setTimer: (ms, fn) => {
      const timer = { ms, fn, cancelled: false };
      timers.push(timer);
      return () => {
        timer.cancelled = true;
      };
    },
    onNoticeChanged: (version) => noticeChanges.push(version),
    updater: options.updater === undefined ? fakeUpdater().updater : options.updater,
    prepareForRestart: options.prepareForRestart ?? (async () => undefined),
    recoverFromFailedRestart: async () => {
      recoveries += 1;
    },
  };
  const invoke = (channel: string, req?: unknown) =>
    Promise.resolve(handlers.get(channel)!(NULL_EVENT, req)) as Promise<UpdateState>;
  const settle = async () => {
    for (let i = 0; i < 10; i++) await new Promise((r) => setTimeout(r, 0));
  };
  const fire = async () => {
    const live = timers.filter((t) => !t.cancelled);
    const timer = live[live.length - 1]!;
    timer.cancelled = true;
    timer.fn();
    await settle();
  };
  return {
    deps,
    handlers,
    entries,
    requests,
    windows,
    timers,
    noticeChanges,
    lines,
    recoveries: () => recoveries,
    invoke,
    fire,
    answerWith: (next: () => Promise<Response>) => {
      answer = next;
    },
  };
}

async function started(options: Parameters<typeof setup>[0] = {}) {
  const fake = setup(options);
  const handle = await registerUpdateNotice(fake.deps);
  return { ...fake, handle };
}

const EXPECTED_NOTICE: UpdateNotice = {
  currentVersion: '0.1.0',
  latestVersion: '0.2.0',
  notes:
    "## What's Changed\n* Desktop update notice by @someone\n\n**Full Changelog**: v0.1.0...v0.2.0",
  releaseUrl: 'https://github.com/vitorafgomes/scout/releases/tag/v0.2.0',
  downloadUrl: download('v0.2.0', 'Scout-Desktop-0.2.0-linux-x86_64.AppImage'),
  publishedAt: '2026-11-01T18:00:00Z',
  canInstall: true,
};

describe('the update notice', () => {
  it('a newer release produces a notice with the right download for this machine and reaches every window', async () => {
    const app = await started();

    const state = await app.invoke(CHANNEL.updateCheckNow);

    expect(state).toEqual({
      checkEnabled: true,
      channel: 'stable',
      lastCheckAt: NOW.toISOString(),
      lastResult: 'available 0.2.0',
      notice: EXPECTED_NOTICE,
      packaged: true,
    });
    expect(app.windows.map((w) => w.sent)).toEqual([
      [[CHANNEL.updateNotice, EXPECTED_NOTICE]],
      [[CHANNEL.updateNotice, EXPECTED_NOTICE]],
    ]);
    expect(app.noticeChanges).toEqual(['0.2.0']);
    expect(app.requests).toHaveLength(1);
    expect(app.requests[0]!.url).toBe(LATEST_URL);
    expect(app.requests[0]!.init?.method).toBe('GET');
    expect(app.requests[0]!.init?.headers).toEqual({
      Accept: 'application/vnd.github+json',
      'User-Agent': 'Scout-Desktop/0.1.0',
    });
    expect(app.requests[0]!.init?.body).toBeUndefined();
    expect(JSON.parse(app.entries.get('update.last-check')!)).toEqual({
      at: NOW.toISOString(),
      result: 'available 0.2.0',
    });
  });

  it('outside an AppImage the Linux download is the deb', async () => {
    const app = await started({ env: {} });

    const state = await app.invoke(CHANNEL.updateCheckNow);

    expect(state.notice?.downloadUrl).toBe(
      download('v0.2.0', 'Scout-Desktop-0.2.0-linux-amd64.deb'),
    );
  });

  it.each(['v0.1.0', 'v0.0.9', '0.1.0'])(
    'the same or an older release produces no notice (%s)',
    async (tag) => {
      const app = await started({ answer: async () => json(release(tag)) });

      const state = await app.invoke(CHANNEL.updateCheckNow);

      expect(state.notice).toBeNull();
      expect(state.lastResult).toBe('up-to-date');
      expect(app.noticeChanges).toEqual([null]);
      expect(app.windows[0]!.sent).toEqual([[CHANNEL.updateNotice, null]]);
    },
  );

  it('a skipped version is not offered again, a newer one is', async () => {
    const app = await started();
    await app.invoke(CHANNEL.updateCheckNow);
    await app.invoke(CHANNEL.updateSkipVersion, { version: '0.2.0' });

    const again = await app.invoke(CHANNEL.updateCheckNow);
    const nextRun = await started({ entries: app.entries });
    const afterRestart = await nextRun.invoke(CHANNEL.updateCheckNow);
    nextRun.answerWith(async () => json(release('v0.3.0')));
    const newer = await nextRun.invoke(CHANNEL.updateCheckNow);

    expect(again.notice).toBeNull();
    expect(again.lastResult).toBe('skipped 0.2.0');
    expect(afterRestart.notice).toBeNull();
    expect(afterRestart.lastResult).toBe('skipped 0.2.0');
    expect(newer.notice?.latestVersion).toBe('0.3.0');
    expect(newer.lastResult).toBe('available 0.3.0');
  });

  it.each([
    ['a prerelease', { prerelease: true }],
    ['a draft', { draft: true }],
  ])('prereleases and drafts are ignored (%s)', async (_label, overrides) => {
    const app = await started({ answer: async () => json(release('v0.2.0', overrides)) });

    const state = await app.invoke(CHANNEL.updateCheckNow);

    expect(state.notice).toBeNull();
    expect(app.noticeChanges).toEqual([null]);
  });

  it('the first check runs shortly after start and repeats daily', async () => {
    const app = await started();

    expect(app.timers.map((t) => t.ms)).toEqual([10_000]);
    expect(app.requests).toHaveLength(0);

    await app.fire();
    expect(app.requests).toHaveLength(1);
    expect(app.noticeChanges).toEqual(['0.2.0']);
    expect(app.timers.map((t) => t.ms)).toEqual([10_000, DAY]);

    await app.fire();
    expect(app.requests).toHaveLength(2);
    expect(app.timers.map((t) => t.ms)).toEqual([10_000, DAY, DAY]);
  });

  it('nothing is fetched in a development build', async () => {
    const app = await started({ packaged: false });

    const state = await app.invoke(CHANNEL.updateCheckNow);
    await app.invoke(CHANNEL.updateSetCheckEnabled, { enabled: true });

    expect(app.timers).toEqual([]);
    expect(app.requests).toEqual([]);
    expect(state).toEqual({
      checkEnabled: true,
      channel: 'stable',
      lastCheckAt: null,
      lastResult: null,
      notice: null,
      packaged: false,
    });
  });

  it('nothing is fetched when the check is disabled, and enabling starts it', async () => {
    const app = await started({ entries: new Map([['update.check-enabled', 'false']]) });

    expect(app.timers).toEqual([]);
    expect((await app.invoke(CHANNEL.updateGetState)).checkEnabled).toBe(false);

    const enabled = await app.invoke(CHANNEL.updateSetCheckEnabled, { enabled: true });
    expect(enabled.checkEnabled).toBe(true);
    expect(app.entries.get('update.check-enabled')).toBe('true');
    expect(app.timers.map((t) => t.ms)).toEqual([10_000]);

    await app.fire();
    expect(app.requests).toHaveLength(1);

    await app.invoke(CHANNEL.updateSetCheckEnabled, { enabled: false });
    expect(app.entries.get('update.check-enabled')).toBe('false');
    expect(app.timers.every((t) => t.cancelled)).toBe(true);
  });

  it('the opt-out takes effect for the session even when the vault cannot be read or written', async () => {
    const locked = async (): Promise<never> => {
      throw new VaultUnavailableError();
    };
    const app = await started({ vault: { get: locked, store: locked } });
    expect(app.timers.map((t) => t.ms)).toEqual([10_000]);

    const answer = await within(
      app.invoke(CHANNEL.updateSetCheckEnabled, { enabled: false }).catch((err: Error) => err),
    );
    const cancelled = app.timers.every((t) => t.cancelled);
    for (const timer of app.timers.filter((t) => !t.cancelled)) timer.fn();
    await new Promise((r) => setTimeout(r, 0));

    expect(cancelled).toBe(true);
    expect(app.requests).toEqual([]);
    expect(answer).toMatchObject({ checkEnabled: false });
    const warnings = app.lines
      .map((line) => JSON.parse(line) as { lvl: string; msg: string; ctx?: { code?: string } })
      .filter((line) => line.lvl === 'warn')
      .map((line) => `${line.msg} ${line.ctx?.code}`);
    expect(warnings).toContain('update: vault read failed vault/unavailable');
    expect(warnings).toContain('update: check setting not stored vault/unavailable');
  });

  it.each([
    ['a 404', async () => json({ message: 'Not Found' }, 404), 'error http 404'],
    ['a 403', async () => json({ message: 'API rate limit exceeded' }, 403), 'error http 403'],
    [
      'a network failure',
      async (): Promise<Response> => {
        throw new TypeError('fetch failed');
      },
      'error network',
    ],
    [
      'a timeout',
      (signal: AbortSignal | undefined) =>
        new Promise<Response>((_resolve, reject) => {
          signal?.addEventListener('abort', () => reject(signal.reason));
        }),
      'error timeout',
    ],
  ])(
    'a 404, a 403, a network failure and a timeout leave no notice, record the error and never throw (%s)',
    async (_label, answer, result) => {
      const app = await started({ answer });

      await app.fire();
      const first = await within(app.invoke(CHANNEL.updateCheckNow));
      const requestsBefore = app.requests.length;
      const answered = await within(app.invoke(CHANNEL.updateCheckNow));

      expect(first).not.toBe(PENDING);
      expect(answered).not.toBe(PENDING);
      expect(app.requests).toHaveLength(requestsBefore + 1);
      const state = answered as UpdateState;
      expect(state.notice).toBeNull();
      expect(state.lastResult).toBe(result);
      expect(JSON.parse(app.entries.get('update.last-check')!).result).toBe(result);
      expect(app.noticeChanges).toEqual([]);
      expect(app.windows[0]!.sent).toEqual([]);
      expect(app.timers.map((t) => t.ms)).toEqual([10_000, DAY]);
      const failures = app.lines
        .map((line) => JSON.parse(line) as { lvl: string; msg: string })
        .filter((line) => line.msg === 'update: check failed');
      expect(failures.map((line) => line.lvl)).toEqual(app.requests.map(() => 'info'));
    },
  );

  it.each([
    ['a body that is not JSON', async () => new Response('<html>busy</html>', { status: 200 })],
    ['JSON without a tag', async () => json({ message: 'odd' })],
  ])('malformed JSON is recorded as a parse error (%s)', async (_label, answer) => {
    const app = await started({ answer });

    const state = await app.invoke(CHANNEL.updateCheckNow);

    expect(state.notice).toBeNull();
    expect(state.lastResult).toBe('error parse');
  });

  it('skipVersion stores the version and clears the notice', async () => {
    const app = await started();
    await app.invoke(CHANNEL.updateCheckNow);

    const state = await app.invoke(CHANNEL.updateSkipVersion, { version: '0.2.0' });

    expect(state.notice).toBeNull();
    expect(app.entries.get('update.skipped-version')).toBe('0.2.0');
    expect(app.noticeChanges).toEqual(['0.2.0', null]);
    expect(app.windows[1]!.sent.at(-1)).toEqual([CHANNEL.updateNotice, null]);
  });

  it('remindLater clears the notice until the next check', async () => {
    const app = await started();
    await app.invoke(CHANNEL.updateCheckNow);

    const later = await app.invoke(CHANNEL.updateRemindLater);
    expect(later.notice).toBeNull();
    expect(app.entries.has('update.skipped-version')).toBe(false);
    expect(app.noticeChanges).toEqual(['0.2.0', null]);

    await app.fire();
    expect((await app.invoke(CHANNEL.updateGetState)).notice).toEqual(EXPECTED_NOTICE);
    expect(app.noticeChanges).toEqual(['0.2.0', null, '0.2.0']);
  });

  it('checkNow returns the state and runs one check at a time', async () => {
    let release0: (response: Response) => void = () => undefined;
    const app = await started({
      answer: () =>
        new Promise<Response>((resolve) => {
          release0 = resolve;
        }),
    });

    const first = app.invoke(CHANNEL.updateCheckNow);
    const second = app.invoke(CHANNEL.updateCheckNow);
    await new Promise((r) => setTimeout(r, 0));
    release0(json(release()));
    const states = await Promise.all([first, second]);

    expect(app.requests).toHaveLength(1);
    expect(states.map((s) => s.notice)).toEqual([EXPECTED_NOTICE, EXPECTED_NOTICE]);
  });

  it('dispose cancels the timer and removes the handlers', async () => {
    const app = await started();

    app.handle.dispose();

    expect(app.timers.map((t) => t.cancelled)).toEqual([true]);
    expect([...app.handlers.keys()]).toEqual([]);
  });
});

describe('Update and restart', () => {
  async function offered(options: Parameters<typeof setup>[0] = {}) {
    const fake = fakeUpdater();
    const app = await started({ updater: fake.updater, ...options });
    await app.invoke(CHANNEL.updateCheckNow);
    return { app, fake };
  }

  const settle = async () => {
    for (let i = 0; i < 10; i++) await new Promise((r) => setTimeout(r, 0));
  };

  it('Update and restart downloads the release and restarts only after the download succeeded', async () => {
    const { app, fake } = await offered();

    const answer = app.invoke(CHANNEL.updateInstall);
    await settle();
    fake.emit('download-progress', { percent: 10.2, transferred: 10, total: 100 });
    fake.emit('download-progress', { percent: 10.7, transferred: 11, total: 100 });
    fake.emit('download-progress', { percent: 50, transferred: 50, total: 100 });
    fake.emit('download-progress', { percent: 100, transferred: 100, total: 100 });
    await settle();
    const installsBeforeDownloaded = [...fake.installs];
    fake.emit('update-downloaded', { version: '0.2.0' });
    fake.finishDownload();
    const state = await within(answer);

    expect(installsBeforeDownloaded).toEqual([]);
    expect(fake.calls).toEqual(['checkForUpdates', 'downloadUpdate']);
    expect(fake.installs).toEqual([[false, true]]);
    const expected = [
      phase('checking'),
      phase('downloading', { percent: 10, transferred: 10, total: 100 }),
      phase('downloading', { percent: 50, transferred: 50, total: 100 }),
      phase('downloading', { percent: 100, transferred: 100, total: 100 }),
      phase('ready'),
      phase('installing'),
    ];
    expect(progressOf(app.windows[0]!)).toEqual(expected);
    expect(progressOf(app.windows[1]!)).toEqual(expected);
    expect(state).toMatchObject({ notice: EXPECTED_NOTICE, lastResult: 'available 0.2.0' });
    expect(fake.listenerCount()).toBe(0);
    expect(app.recoveries()).toBe(0);
  });

  it('the worker is stopped and the single-instance lock released before the new version is started', async () => {
    let prepare: () => void = () => undefined;
    const events: string[] = [];
    const { app, fake } = await offered({
      prepareForRestart: () =>
        new Promise<void>((resolve) => {
          events.push(`prepare after ${progressOf(app.windows[0]!).at(-1)?.phase}`);
          prepare = () => {
            events.push('prepared');
            resolve();
          };
        }),
    });
    fake.downloadsCleanly();

    const answer = app.invoke(CHANNEL.updateInstall);
    await settle();
    const installsWhilePreparing = [...fake.installs];
    prepare();
    await within(answer);

    expect(installsWhilePreparing).toEqual([]);
    expect(events).toEqual(['prepare after installing', 'prepared']);
    expect(fake.installs).toEqual([[false, true]]);
    expect(progressOf(app.windows[0]!).at(-1)).toEqual(phase('installing'));
  });

  it('a slow prepareForRestart does not block the restart forever', async () => {
    const { app, fake } = await offered({
      prepareForRestart: () => new Promise<void>(() => undefined),
    });
    fake.downloadsCleanly();

    const answer = app.invoke(CHANNEL.updateInstall);
    await settle();
    const installsBeforeTimeout = [...fake.installs];
    const timeout = app.timers.at(-1)!;
    await app.fire();
    const state = await within(answer);

    expect(installsBeforeTimeout).toEqual([]);
    expect(timeout.ms).toBe(10_000);
    expect(state).not.toBe(PENDING);
    expect(fake.installs).toEqual([[false, true]]);
    const warnings = app.lines
      .map((line) => JSON.parse(line) as { lvl: string; msg: string })
      .filter((line) => line.lvl === 'warn')
      .map((line) => line.msg);
    expect(warnings).toEqual([
      'update: the restart was not prepared within 10 s; restarting anyway',
    ]);
  });

  it('the updater runs with autoDownload, autoInstallOnAppQuit and allowPrerelease off', async () => {
    const { app, fake } = await offered();
    await app.fire();
    const touchedBeforeClick = [...fake.assigned];
    const calledBeforeClick = [...fake.calls];
    fake.downloadsCleanly();

    await app.invoke(CHANNEL.updateInstall);

    expect(touchedBeforeClick).toEqual([]);
    expect(calledBeforeClick).toEqual([]);
    expect(fake.flagsAtCheck()).toEqual({
      autoDownload: false,
      autoInstallOnAppQuit: false,
      allowPrerelease: false,
    });
    expect(fake.assigned).toEqual([
      ['autoDownload', false],
      ['autoInstallOnAppQuit', false],
      ['allowPrerelease', false],
    ]);
    expect(fake.installs).toEqual([[false, true]]);
  });

  it.each([
    ['macOS', 'darwin', 'arm64', {}, download('v0.2.0', 'Scout-Desktop-0.2.0-mac-arm64.dmg')],
    ['a deb', 'linux', 'x64', {}, download('v0.2.0', 'Scout-Desktop-0.2.0-linux-amd64.deb')],
  ])(
    'install is refused when the package cannot self-update and the notice keeps Download (%s)',
    async (_label, platform, arch, env, downloadUrl) => {
      const { app, fake } = await offered({ platform, arch, env });

      const state = await within(app.invoke(CHANNEL.updateInstall));

      expect(fake.calls).toEqual([]);
      expect(state).toMatchObject({ notice: { canInstall: false, downloadUrl } });
      expect(fake.assigned).toEqual([]);
      expect(fake.installs).toEqual([]);
      expect(progressOf(app.windows[0]!)).toEqual([
        phase('failed', { message: 'This copy of Scout cannot update itself; use Download' }),
      ]);
    },
  );

  it('install without a notice touches nothing and says there is no update', async () => {
    const fake = fakeUpdater();
    const app = await started({ updater: fake.updater });

    const state = await within(app.invoke(CHANNEL.updateInstall));

    expect(fake.calls).toEqual([]);
    expect(fake.assigned).toEqual([]);
    expect(state).toMatchObject({ notice: null });
    expect(progressOf(app.windows[0]!)).toEqual([
      phase('failed', { message: 'There is no update to install' }),
    ]);
  });

  it('a corrupt download installs nothing and says so', async () => {
    const { app, fake } = await offered();

    const answer = app.invoke(CHANNEL.updateInstall);
    await settle();
    fake.emit('download-progress', { percent: 100, transferred: 100, total: 100 });
    fake.failDownload(
      Object.assign(new Error('sha512 checksum mismatch, expected abc, got def'), {
        code: 'ERR_CHECKSUM_MISMATCH',
      }),
    );
    const state = (await within(answer)) as UpdateState;

    expect(fake.installs).toEqual([]);
    expect(progressOf(app.windows[0]!).at(-1)).toEqual(
      phase('failed', { message: 'The download did not match the release; nothing was installed' }),
    );
    expect(state.notice).toEqual(EXPECTED_NOTICE);
    expect(state.lastResult).toBe('error install checksum');
    expect(JSON.parse(app.entries.get('update.last-check')!).result).toBe('error install checksum');
    expect(fake.listenerCount()).toBe(0);
  });

  const httpError = (statusCode: number) =>
    Object.assign(new Error(`HttpError: ${statusCode} Forbidden`), {
      statusCode,
      code: `HTTP_ERROR_${statusCode}`,
    });

  it.each([
    [
      'a network failure',
      'download',
      new Error('net::ERR_INTERNET_DISCONNECTED'),
      'network',
      'The update failed (network); nothing was installed',
    ],
    [
      'a timeout',
      'download',
      new Error('Request timed out'),
      'timeout',
      'The download timed out; nothing was installed',
    ],
    [
      'a 404 on the package',
      'download',
      new Error(
        'Cannot download "https://github.com/vitorafgomes/scout/releases/download/v0.2.0/Scout-Desktop-0.2.0-linux-x86_64.AppImage", status 404: Not Found',
      ),
      'http 404',
      'The update failed (http 404); nothing was installed',
    ],
    [
      'a 403',
      'check',
      httpError(403),
      'http 403',
      'The update failed (http 403); nothing was installed',
    ],
    [
      'no asset for this platform',
      'check',
      Object.assign(new Error('Cannot find latest-linux.yml in the latest release artifacts'), {
        code: 'ERR_UPDATER_CHANNEL_FILE_NOT_FOUND',
      }),
      'no asset',
      'This release has no update for this package; nothing was installed',
    ],
    [
      'a download that ends without update-downloaded',
      'silent',
      null,
      'incomplete',
      'The download did not finish; nothing was installed',
    ],
  ])(
    'a network failure, a timeout, a 404 and a 403 from the updater leave the app on the current version (%s)',
    async (_label, stage, error, kind, message) => {
      const { app, fake } = await offered();
      if (stage === 'check') fake.failCheck(error);
      if (stage === 'silent') fake.endsDownloadSilently();

      const answer = app.invoke(CHANNEL.updateInstall);
      await settle();
      if (stage === 'download') fake.failDownload(error);
      const state = await within(answer);

      expect(state).not.toBe(PENDING);
      expect(fake.installs).toEqual([]);
      expect(progressOf(app.windows[0]!).at(-1)).toEqual(phase('failed', { message }));
      expect(state).toMatchObject({ notice: EXPECTED_NOTICE, lastResult: `error install ${kind}` });
      expect(fake.listenerCount()).toBe(0);
    },
  );

  it('an update the updater cannot apply says so, keeps the notice and takes the lock and the worker back', async () => {
    const { app, fake } = await offered();
    fake.downloadsCleanly();
    fake.failsToApply(new Error('EACCES: permission denied, unlink'));

    const state = await app.invoke(CHANNEL.updateInstall);

    expect(fake.installs).toEqual([[false, true]]);
    expect(progressOf(app.windows[0]!).slice(-2)).toEqual([
      phase('installing'),
      phase('failed', {
        message: 'Scout could not replace its own file; it keeps running this version',
      }),
    ]);
    expect(state).toMatchObject({ notice: EXPECTED_NOTICE, lastResult: 'error install apply' });
    expect(app.recoveries()).toBe(1);
    expect(fake.listenerCount()).toBe(0);
  });

  it('the updater offering a different version than the notice is not installed', async () => {
    const fake = fakeUpdater('0.3.0');
    const app = await started({ updater: fake.updater });
    await app.invoke(CHANNEL.updateCheckNow);

    await within(app.invoke(CHANNEL.updateInstall));

    expect(fake.calls).toEqual(['checkForUpdates']);
    expect(fake.installs).toEqual([]);
    expect(progressOf(app.windows[0]!)).toEqual([
      phase('checking'),
      phase('failed', { message: 'The release changed; check again' }),
    ]);
    expect(app.requests).toHaveLength(2);
    expect(fake.listenerCount()).toBe(0);
  });

  it('a second click while an install is in flight is ignored', async () => {
    const { app, fake } = await offered();

    const first = app.invoke(CHANNEL.updateInstall);
    await settle();
    const second = await within(app.invoke(CHANNEL.updateInstall));
    const progressAfterSecond = progressOf(app.windows[0]!);
    fake.emit('update-downloaded', { version: '0.2.0' });
    fake.finishDownload();
    await within(first);

    expect(second).not.toBe(PENDING);
    expect(second).toMatchObject({ notice: EXPECTED_NOTICE });
    expect(progressAfterSecond).toEqual([phase('checking')]);
    expect(fake.calls).toEqual(['checkForUpdates', 'downloadUpdate']);
    expect(fake.installs).toEqual([[false, true]]);
  });

  it('dispose during an install removes the updater listeners', async () => {
    const { app, fake } = await offered();
    void app.invoke(CHANNEL.updateInstall);
    await settle();
    const attached = fake.listenerCount();

    app.handle.dispose();

    expect(attached).toBe(3);
    expect(fake.listenerCount()).toBe(0);
  });

  it.each([
    ['an AppImage', 'linux', APPIMAGE_ENV, true, true],
    ['Windows', 'win32', {}, true, true],
    ['Linux outside an AppImage', 'linux', {}, true, false],
    ['an empty APPIMAGE', 'linux', { APPIMAGE: '' }, true, false],
    ['macOS', 'darwin', {}, true, false],
    ['an AppImage without an updater', 'linux', APPIMAGE_ENV, false, false],
    [
      "a .deb started with another program's APPIMAGE",
      'linux',
      { APPIMAGE: '/opt/Other.AppImage', APPDIR: '/tmp/.mount_OtherXy34' },
      true,
      false,
    ],
    ['an APPIMAGE without its mount', 'linux', { APPIMAGE: '/opt/Other.AppImage' }, true, false],
  ])(
    'canInstall is true for an AppImage and for Windows, false elsewhere (%s)',
    async (_label, platform, env, withUpdater, expected) => {
      const app = await started({ platform, env, ...(withUpdater ? {} : { updater: null }) });

      const state = await app.invoke(CHANNEL.updateCheckNow);

      expect(state.notice?.canInstall).toBe(expected);
    },
  );
});

describe('the Beta channel', () => {
  const list =
    (...releases: unknown[]) =>
    async () =>
      json(releases);
  const beta = (tag: string, extra: Record<string, unknown> = {}) =>
    release(tag, { prerelease: true, ...extra });
  const onChannel = (value: string) => new Map([['update.channel', value]]);

  it('starts on Stable, which reads only the latest release and offers no pre-release', async () => {
    const app = await started({ answer: async () => json(beta('v0.3.0-beta.1')) });

    const state = await app.invoke(CHANNEL.updateCheckNow);

    expect({
      channel: state.channel,
      url: app.requests[0]!.url,
      result: state.lastResult,
      notice: state.notice,
    }).toEqual({ channel: 'stable', url: LATEST_URL, result: 'up-to-date', notice: null });
  });

  it('Beta reads the recent releases and offers the highest version, a pre-release included, skipping drafts', async () => {
    const app = await started({
      entries: onChannel('beta'),
      answer: list(
        release('v0.2.0'),
        beta('v0.3.0-beta.2'),
        beta('v0.3.0-beta.10'),
        release('v0.4.0', { draft: true }),
      ),
    });

    const state = await app.invoke(CHANNEL.updateCheckNow);

    expect({
      url: app.requests[0]!.url,
      method: app.requests[0]!.init?.method,
      headers: app.requests[0]!.init?.headers,
      result: state.lastResult,
      latest: state.notice?.latestVersion,
    }).toEqual({
      url: RELEASES_URL,
      method: 'GET',
      headers: { Accept: 'application/vnd.github+json', 'User-Agent': 'Scout-Desktop/0.1.0' },
      result: 'available 0.3.0-beta.10',
      latest: '0.3.0-beta.10',
    });
  });

  it('Beta still prefers a released version that is higher than every pre-release', async () => {
    const app = await started({
      entries: onChannel('beta'),
      answer: list(beta('v0.3.0-beta.1'), release('v0.3.0')),
    });

    const state = await app.invoke(CHANNEL.updateCheckNow);

    expect(state.notice?.latestVersion).toBe('0.3.0');
  });

  it('back on Stable while running a pre-release, nothing older is offered', async () => {
    const app = await started({
      currentVersion: '0.3.0-beta.2',
      answer: async () => json(release('v0.2.0')),
    });

    const state = await app.invoke(CHANNEL.updateCheckNow);

    expect([state.lastResult, state.notice]).toEqual(['up-to-date', null]);
  });

  it('setChannel stores the choice, drops the notice of the other channel and checks again on the new one', async () => {
    const app = await started();
    await app.invoke(CHANNEL.updateCheckNow);
    app.answerWith(list(beta('v0.3.0-beta.1')));

    const state = await app.invoke(CHANNEL.updateSetChannel, { channel: 'beta' });

    expect({
      stored: app.entries.get('update.channel'),
      channel: state.channel,
      urls: app.requests.map((r) => r.url),
      latest: state.notice?.latestVersion,
      cleared: app.windows[0]!.sent.filter(([c]) => c === CHANNEL.updateNotice).map(
        ([, n]) => n === null,
      ),
    }).toEqual({
      stored: 'beta',
      channel: 'beta',
      urls: [LATEST_URL, RELEASES_URL],
      latest: '0.3.0-beta.1',
      cleared: [false, true, false],
    });
  });

  it('a channel switch while a check is still waiting for GitHub checks again on the new channel once that check ends', async () => {
    let releaseStable: (response: Response) => void = () => undefined;
    const app = await started({
      answer: () =>
        new Promise<Response>((resolve) => {
          releaseStable = resolve;
        }),
    });
    const pending = app.invoke(CHANNEL.updateCheckNow);
    await new Promise((r) => setTimeout(r, 0));

    app.answerWith(list(beta('v0.3.0-beta.1')));
    const switched = app.invoke(CHANNEL.updateSetChannel, { channel: 'beta' });
    releaseStable(json(release('v0.2.0')));
    await pending;
    const state = await switched;

    expect({
      urls: app.requests.map((r) => r.url),
      latest: state.notice?.latestVersion,
      result: state.lastResult,
    }).toEqual({
      urls: [LATEST_URL, RELEASES_URL],
      latest: '0.3.0-beta.1',
      result: 'available 0.3.0-beta.1',
    });
  });

  it('with the check switched off, a Check now still waiting when the channel changes shows nothing of the old channel', async () => {
    let answerBeta: (response: Response) => void = () => undefined;
    const app = await started({
      entries: new Map([
        ['update.channel', 'beta'],
        ['update.check-enabled', 'false'],
      ]),
      answer: () =>
        new Promise<Response>((resolve) => {
          answerBeta = resolve;
        }),
    });
    const pending = app.invoke(CHANNEL.updateCheckNow);
    await new Promise((r) => setTimeout(r, 0));

    const switched = await app.invoke(CHANNEL.updateSetChannel, { channel: 'stable' });
    answerBeta(json([beta('v0.3.0-beta.1')]));
    const after = await pending;

    expect({
      urls: app.requests.map((r) => r.url),
      notice: after.notice,
      channel: switched.channel,
      announced: app.windows[0]!.sent.filter(([c, n]) => c === CHANNEL.updateNotice && n !== null),
    }).toEqual({ urls: [RELEASES_URL], notice: null, channel: 'stable', announced: [] });
  });

  it("when the new channel's check fails, the old channel's release is not left on offer", async () => {
    let answerBeta: (response: Response) => void = () => undefined;
    const app = await started({
      entries: onChannel('beta'),
      answer: () =>
        new Promise<Response>((resolve) => {
          answerBeta = resolve;
        }),
    });
    const pending = app.invoke(CHANNEL.updateCheckNow);
    await new Promise((r) => setTimeout(r, 0));

    app.answerWith(async () => new Response('busy', { status: 503 }));
    const switched = app.invoke(CHANNEL.updateSetChannel, { channel: 'stable' });
    answerBeta(json([beta('v0.3.0-beta.1')]));
    await pending;
    const state = await switched;

    expect([state.notice, state.lastResult]).toEqual([null, 'error http 503']);
  });

  it('on Stable, a tag with a pre-release suffix GitHub did not mark as a pre-release is still not offered', async () => {
    const app = await started({ answer: async () => json(release('v0.3.0-beta.1')) });

    const state = await app.invoke(CHANNEL.updateCheckNow);

    expect([state.lastResult, state.notice]).toEqual(['up-to-date', null]);
  });

  it('a channel the vault cannot store still applies for this session, is logged, and the check runs on it', async () => {
    const app = await started({
      vault: {
        get: async () => null,
        store: async () => {
          throw new VaultUnavailableError();
        },
      },
      answer: list(beta('v0.3.0-beta.1')),
    });

    const state = await app.invoke(CHANNEL.updateSetChannel, { channel: 'beta' });

    expect({
      channel: state.channel,
      urls: app.requests.map((r) => r.url),
      warned: app.lines.some((line) => line.includes('update: channel not stored')),
    }).toEqual({ channel: 'beta', urls: [RELEASES_URL], warned: true });
  });

  it.each([['alpha'], [''], [undefined], [42]])(
    'setChannel refuses a channel that is neither stable nor beta (%p): nothing is stored and nothing is fetched',
    async (value) => {
      const app = await started();

      const state = await app.invoke(CHANNEL.updateSetChannel, { channel: value });

      expect([state.channel, app.entries.has('update.channel'), app.requests]).toEqual([
        'stable',
        false,
        [],
      ]);
    },
  );

  it('a stored channel that is not beta reads as Stable', async () => {
    const app = await started({ entries: onChannel('nightly') });

    const state = await app.invoke(CHANNEL.updateGetState);

    expect(state.channel).toBe('stable');
  });

  it.each([
    ['a 503', async () => new Response('busy', { status: 503 }), 'error http 503'],
    [
      'a reset',
      async () => {
        throw new TypeError('fetch failed: ECONNRESET');
      },
      'error network',
    ],
    ['a body that is not a list', async () => json({ tag_name: 'v0.3.0' }), 'error parse'],
    [
      'a list with no usable release',
      async () => json([{ draft: true, tag_name: 'v0.3.0' }]),
      'error parse',
    ],
  ])(
    'on Beta, %s leaves Scout on its version with no notice and says why',
    async (_label, answer, result) => {
      const app = await started({ entries: onChannel('beta'), answer });

      const state = await app.invoke(CHANNEL.updateCheckNow);

      expect([state.lastResult, state.notice]).toEqual([result, null]);
    },
  );

  it('on Beta, a list GitHub does not answer in time says the check timed out', async () => {
    const app = await started({
      entries: onChannel('beta'),
      answer: (signal) =>
        new Promise((_resolve, reject) => {
          signal?.addEventListener('abort', () => reject(signal.reason));
        }),
    });

    const state = await app.invoke(CHANNEL.updateCheckNow);

    expect(state.lastResult).toBe('error timeout');
  });

  it('on Beta, Update and restart lets the updater take pre-releases; on Stable it does not', async () => {
    const results: unknown[] = [];
    for (const [entries, offered] of [
      [onChannel('beta'), '0.3.0-beta.1'],
      [new Map<string, string>(), '0.2.0'],
    ] as const) {
      const fake = fakeUpdater(offered);
      const app = await started({
        entries,
        updater: fake.updater,
        answer:
          offered === '0.2.0' ? async () => json(release('v0.2.0')) : list(beta(`v${offered}`)),
      });
      await app.invoke(CHANNEL.updateCheckNow);
      fake.downloadsCleanly();
      await app.invoke(CHANNEL.updateInstall);
      results.push(fake.flagsAtCheck()?.['allowPrerelease']);
    }

    expect(results).toEqual([true, false]);
  });
});
