import { describe, it, expect } from 'bun:test';
import { EventEmitter } from 'node:events';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import type { BrowserWindow, Display, Menu, Tray } from 'electron';
import type { FeatureDto, WorkerStatus } from '@scout/shared';
import { createTrayController } from '../src/tray/tray.js';
import { buildEnv, readGrantedWriteFeatures } from '../src/worker/build-env.js';
import { memoryPendingRevokes } from './pending-revokes-fake.js';
import type { TrayDeps } from '../src/tray/tray.js';
import { openQuickAsk, registerQuickAsk, type QuickAskDeps } from '../src/quick-ask/quick-ask.js';
import { recordingLogger } from './kubernetes-fakes.js';
import { FakeTray } from './shell-fakes.js';

function deferred(): { promise: Promise<void>; resolve: () => void } {
  let resolve: () => void = () => undefined;
  const promise = new Promise<void>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

function setup(overrides: Partial<TrayDeps> = {}) {
  const fakeTray = new FakeTray();
  const opened: string[] = [];
  const { logger, lines } = recordingLogger();
  const controller = createTrayController({
    createTray: () => fakeTray as unknown as Tray,
    buildContextMenu: (template) => template as unknown as Menu,
    iconPath: '/fake/icon.png',
    onOpen: () => opened.push('window'),
    onAskScout: openQuickAsk,
    onStart: async () => undefined,
    onStop: async () => undefined,
    onQuit: () => undefined,
    logger,
    ...overrides,
  });
  return { fakeTray, opened, lines, controller: controller! };
}

const status = (state: WorkerStatus['state']): WorkerStatus => ({ state, restarts: 0 });

const tick = () => new Promise((r) => setTimeout(r, 0));

describe('the tray menu', () => {
  it('offers Open Scout, Ask Scout..., the worker toggle and Quit Scout, in that order', () => {
    const { fakeTray } = setup({ initialStatus: status('running') });

    expect(fakeTray.layout()).toEqual([
      'Open Scout',
      'Ask Scout...',
      '---',
      'Stop Scout',
      '---',
      'Quit Scout',
    ]);
  });

  it.each([
    ['idle', 'Start Scout', true],
    ['stopped', 'Start Scout', true],
    ['crashed', 'Start Scout', true],
    ['starting', 'Start Scout', false],
    ['running', 'Stop Scout', true],
  ] as const)('worker %s: shows %s, enabled %p', (state, label, enabled) => {
    const { fakeTray, controller } = setup();

    controller.setStatus(status(state));

    expect(fakeTray.item('worker').label).toBe(label);
    expect(fakeTray.item('worker').enabled).toBe(enabled);
  });

  it.each([
    ['idle', 'Scout - Idle'],
    ['starting', 'Scout - Starting'],
    ['running', 'Scout - Running'],
    ['stopped', 'Scout - Stopped'],
    ['crashed', 'Scout - Crashed'],
  ] as const)('worker %s: the tooltip reads "%s"', (state, tooltip) => {
    const { fakeTray, controller } = setup();

    controller.setStatus(status(state));

    expect(fakeTray.tooltip).toBe(tooltip);
  });

  it('Stop Scout stays disabled while the worker is stopping, then offers Start Scout', async () => {
    const stopping = deferred();
    const { fakeTray, controller } = setup({
      initialStatus: status('running'),
      onStop: () => stopping.promise,
    });

    fakeTray.click('worker');
    controller.setStatus(status('stopped'));

    expect(fakeTray.item('worker')).toMatchObject({ label: 'Stop Scout', enabled: false });

    stopping.resolve();
    await tick();

    expect(fakeTray.item('worker')).toMatchObject({ label: 'Start Scout', enabled: true });
  });

  it('Start Scout stays disabled until the start settles, even before the worker reports starting', async () => {
    const starting = deferred();
    const { fakeTray, controller } = setup({
      initialStatus: status('stopped'),
      onStart: () => starting.promise,
    });

    fakeTray.click('worker');

    expect(fakeTray.item('worker')).toMatchObject({ label: 'Start Scout', enabled: false });

    controller.setStatus(status('running'));
    starting.resolve();
    await tick();

    expect(fakeTray.item('worker')).toMatchObject({ label: 'Stop Scout', enabled: true });
  });

  it('a failed start is logged and the item is offered again', async () => {
    const { fakeTray, lines } = setup({
      initialStatus: status('stopped'),
      onStart: () => Promise.reject(new Error('binary missing')),
    });

    fakeTray.click('worker');
    await tick();

    expect(fakeTray.item('worker')).toMatchObject({ label: 'Start Scout', enabled: true });
    expect(lines.some((line) => line.includes('tray: start failed'))).toBe(true);
  });

  it('Open Scout and a click on the icon both open the window', () => {
    const { fakeTray, opened } = setup();

    fakeTray.click('open');
    fakeTray.emit('click');

    expect(opened).toEqual(['window', 'window']);
  });

  it('Ask Scout... creates and shows the real quick-ask window on the quick-ask route', async () => {
    class QuickAskWindow extends EventEmitter {
      readonly webContents = Object.assign(new EventEmitter(), {
        setWindowOpenHandler() {},
        focus() {},
      });
      readonly calls: string[] = [];
      route: unknown = null;
      isDestroyed = () => false;
      setPosition = () => this.calls.push('setPosition');
      show = () => this.calls.push('show');
      showInactive = () => this.calls.push('showInactive');
      focus = () => this.calls.push('focus');
      hide = () => this.calls.push('hide');
      setAlwaysOnTop = () => undefined;
      moveTop = () => undefined;
      destroy = () => undefined;
      loadFile = (_path: string, options: unknown) => {
        this.route = options;
        return Promise.resolve();
      };
      loadURL = () => Promise.resolve();
    }
    const windows: QuickAskWindow[] = [];
    const display = { workArea: { x: 0, y: 0, width: 1280, height: 800 } } as Display;
    const quickAsk = await registerQuickAsk({
      ipcMain: { handle: () => undefined, removeHandler: () => undefined },
      supervisor: {
        status: { state: 'running', restarts: 0 },
        writeStdin: () => true,
        onEvent: () => () => undefined,
      },
      createWindow: () => {
        const window = new QuickAskWindow();
        windows.push(window);
        return window as unknown as BrowserWindow;
      },
      preloadPath: undefined,
      indexHtmlPath: '/app/renderer/index.html',
      globalShortcut: { register: () => true, unregister: () => undefined },
      vault: { get: async () => null, store: async () => undefined },
      logger: recordingLogger().logger,
      screen: Object.assign(new EventEmitter(), {
        getCursorScreenPoint: () => ({ x: 0, y: 0 }),
        getDisplayNearestPoint: () => display,
        getPrimaryDisplay: () => display,
        getAllDisplays: () => [display],
      }) as unknown as QuickAskDeps['screen'],
      setTimer: () => () => undefined,
      platform: 'linux',
      env: {},
      execPath: '/opt/Scout/scout-desktop',
      windows: () => [],
      locale: () => 'en-US',
      showSaveDialog: async () => ({ canceled: true }),
      writeFile: async () => undefined,
      documentsDir: () => '/home/owner/Documents',
      background: () => '#0f1623',
    });
    const { fakeTray } = setup();

    fakeTray.click('ask');
    quickAsk.dispose();

    const quickAskWindows = windows.filter(
      (w) => (w.route as { hash: string } | null)?.hash === '/quick-ask',
    );
    expect(quickAskWindows).toHaveLength(1);
    expect(quickAskWindows[0]!.calls).toEqual(['setPosition', 'show', 'focus']);
  });

  it('the tray offers Update available while a newer version is known', () => {
    const { fakeTray, controller, opened } = setup({ initialStatus: status('running') });

    controller.setUpdateAvailable('0.2.0');

    expect(fakeTray.layout()).toEqual([
      'Open Scout',
      'Update available: 0.2.0',
      'Ask Scout...',
      '---',
      'Stop Scout',
      '---',
      'Quit Scout',
    ]);
    expect(fakeTray.tooltip).toBe('Scout - Running - update available');
    fakeTray.click('update');
    expect(opened).toEqual(['window']);

    controller.setStatus(status('stopped'));
    expect(fakeTray.item('update').label).toBe('Update available: 0.2.0');
    expect(fakeTray.tooltip).toBe('Scout - Stopped - update available');

    controller.setUpdateAvailable(null);

    expect(fakeTray.layout()).not.toContain('Update available: 0.2.0');
    expect(fakeTray.tooltip).toBe('Scout - Stopped');
  });

  it('returns null and logs when the desktop cannot create a tray', () => {
    const { logger, lines } = recordingLogger();

    const controller = createTrayController({
      createTray: () => {
        throw new Error('no status notifier host');
      },
      buildContextMenu: (template) => template as unknown as Menu,
      iconPath: '/fake/icon.png',
      onOpen: () => undefined,
      onAskScout: () => undefined,
      onStart: async () => undefined,
      onStop: async () => undefined,
      onQuit: () => undefined,
      logger,
    });

    expect(controller).toBeNull();
    expect(lines.some((line) => line.includes('tray: not available on this desktop'))).toBe(true);
  });

  it('destroy removes the icon and later status changes do not touch it', () => {
    const { fakeTray, controller } = setup();

    controller.destroy();
    controller.setStatus(status('running'));

    expect(fakeTray.destroyed).toBe(true);
    expect(fakeTray.tooltip).toBe('Scout - Idle');
  });
});

describe('the tray Start item before the control plane connects', () => {
  it('a worker started from the tray while the control plane is still connecting gets the write grants', async () => {
    const { logger } = recordingLogger();
    let connect: (client: {
      listFeatures(): Promise<FeatureDto[]>;
      setWriteGrant(): Promise<FeatureDto>;
    }) => void = () => undefined;
    const connection = {
      client: new Promise<{
        listFeatures(): Promise<FeatureDto[]>;
        setWriteGrant(): Promise<FeatureDto>;
      }>((resolve) => {
        connect = resolve;
      }),
      workerGate: () => ({ socketPath: '/run/scout/controlplane-1.sock', token: 'worker-token' }),
    };
    const envs: Record<string, string>[] = [];
    let started: Promise<void> = Promise.resolve();
    const { fakeTray } = setup({
      initialStatus: status('stopped'),
      onStart: () =>
        (started = (async () => {
          envs.push(
            await buildEnv({
              vaultGet: async () => null,
              grantedWriteFeatures: await readGrantedWriteFeatures(
                connection.client,
                logger,
                memoryPendingRevokes(),
                5_000,
              ),
              workerGate: connection.workerGate(),
            }),
          );
        })()),
    });

    fakeTray.click('worker');
    setTimeout(
      () =>
        connect({
          listFeatures: async () => [
            { id: 'youtube', granted: true } as FeatureDto,
            { id: 'github', granted: false } as FeatureDto,
          ],
          setWriteGrant: async () => ({}) as FeatureDto,
        }),
      20,
    );
    await started;

    expect(envs.map((env) => env['SCOUT__FEATURES__GRANTEDWRITES'])).toEqual(['youtube']);
    expect(envs[0]!['SCOUT__WORKERGATE__SOCKETPATH']).toBe('/run/scout/controlplane-1.sock');
  });

  it('main connects the control plane before the YouTube sign-in and the supervisor, so every worker start and every revoke at start use that connection', () => {
    const main = readFileSync(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');

    expect(main.indexOf('connectControlPlane({')).toBeGreaterThan(-1);
    expect(main.indexOf('connectControlPlane({')).toBeLessThan(main.indexOf('createSupervisor({'));
    expect(main.indexOf('connectControlPlane({')).toBeLessThan(main.indexOf('new GoogleAuth({'));
    expect(main).toContain(
      'const youTubeControlPlane = (): Promise<ControlPlaneClient> => connection.client;',
    );
    expect(main).toMatch(/readGrantedWriteFeatures\(\s*connection\.client,/);
    expect(main).toContain('workerGate: connection.workerGate(),');
  });

  it('the YouTube operator appears right after sign-in: every YouTube connection change (sign-in completed, sign-out, API key saved or removed) pushes the features change to every window', () => {
    const main = readFileSync(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');
    const google = main.slice(
      main.indexOf('new GoogleAuth({'),
      main.indexOf('googleAuth = google;'),
    );

    expect(main).toContain(
      'const featuresChanged = (): void => sendToAllWindows(CHANNEL.featuresChanged, youTubeLogger);',
    );
    expect(google).toMatch(/onConnectionChanged: \(\) => \{\s*featuresChanged\(\);/);
  });

  it('main runs the YouTube write-grant hooks with the Google sign-in, and a grant change tells every window, so the scope check runs before a grant and the card shows the new scopes', () => {
    const main = readFileSync(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');
    const hooks = main.slice(
      main.indexOf('registerFeaturesHandlers('),
      main.indexOf('registerOperatorHandlers('),
    );

    expect(hooks).toMatch(
      /\.\.\.youTubeWriteGrantHooks\(google, \(\) =>\s*deleteYouTubeData\(youTubeControlPlane, youTubeLogger\),?\s*\)/,
    );
    expect(hooks).toMatch(
      /onWriteGrantChanged: \(\) => \{\s*sendToAllWindows\(CHANNEL\.featuresChanged, featuresLogger\);/,
    );
  });
});
