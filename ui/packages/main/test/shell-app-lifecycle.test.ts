import { describe, expect, it } from 'bun:test';
import { EventEmitter } from 'node:events';
import type { Menu, Tray } from 'electron';
import type { WorkerStatus } from '@scout/shared';
import {
  installAppLifecycle,
  launchWindow,
  type AppLifecycleDeps,
  type LifecycleWindow,
} from '../src/shell/app-lifecycle.js';
import { createPresenceNotifier } from '../src/shell/notifications.js';
import { createTrayController } from '../src/tray/tray.js';
import { recordingLogger } from './kubernetes-fakes.js';
import { FakeTray } from './shell-fakes.js';

class FakeApp extends EventEmitter {
  quitRequests = 0;
  exitCode: number | null = null;

  quit(): void {
    this.quitRequests += 1;
    this.emit('before-quit', { preventDefault: () => undefined });
  }

  exit(code: number): void {
    this.exitCode = code;
  }
}

class FakeWindow implements LifecycleWindow {
  visible = true;
  minimized = false;
  focused = false;
  destroyed = false;

  isDestroyed = () => this.destroyed;
  isMinimized = () => this.minimized;
  restore = () => {
    this.minimized = false;
  };
  show = () => {
    this.visible = true;
  };
  focus = () => {
    this.focused = true;
  };
  hide = () => {
    this.visible = false;
    this.focused = false;
  };
}

function fakeWorker(state: WorkerStatus['state'] = 'running') {
  const worker = {
    status: { state, restarts: 0 } as WorkerStatus,
    async stop() {
      worker.status = { state: 'stopped', restarts: 0 };
      return { exitCode: 0 };
    },
  };
  return worker;
}

function setup(options: {
  keepRunning: boolean;
  platform?: NodeJS.Platform;
  quickAskWindowOpen?: boolean;
}) {
  const app = new FakeApp();
  const worker = fakeWorker();
  const windows: FakeWindow[] = [new FakeWindow()];
  let disposed = 0;
  let asked = 0;
  const lifecycle = installAppLifecycle({
    app: app as unknown as AppLifecycleDeps['app'],
    platform: options.platform ?? 'linux',
    supervisor: () => worker,
    keepRunning: () => options.keepRunning,
    window: () => windows.at(-1) ?? null,
    createWindow: () => windows.push(new FakeWindow()),
    onHiddenToTray: () => undefined,
    onAsk: () => {
      asked += 1;
    },
    dispose: () => {
      disposed += 1;
    },
    logger: recordingLogger().logger,
  });
  const closeWindow = () => {
    let prevented = false;
    lifecycle.onWindowClose({ preventDefault: () => (prevented = true) });
    if (!prevented) {
      windows.at(-1)!.destroyed = true;
      if (options.quickAskWindowOpen !== true) app.emit('window-all-closed');
    }
    return prevented;
  };
  return {
    app,
    worker,
    windows,
    lifecycle,
    closeWindow,
    disposed: () => disposed,
    asked: () => asked,
  };
}

const settle = () => new Promise((r) => setTimeout(r, 0));

describe('keep running when the window is closed: on', () => {
  it('closing the window hides it, the app does not quit and the worker keeps running', async () => {
    const { app, worker, windows, closeWindow } = setup({ keepRunning: true });

    const prevented = closeWindow();
    await settle();

    expect(prevented).toBe(true);
    expect(windows[0]!.visible).toBe(false);
    expect(app.quitRequests).toBe(0);
    expect(worker.status.state).toBe('running');
  });

  it('window-all-closed does not quit and does not stop the worker', async () => {
    const { app, worker } = setup({ keepRunning: true });

    app.emit('window-all-closed');
    await settle();

    expect(app.quitRequests).toBe(0);
    expect(worker.status.state).toBe('running');
  });

  it('the first hide shows "Scout is still running in the tray." once, not on later hides', async () => {
    const shown: string[] = [];
    const entries = new Map<string, string>();
    const notifier = createPresenceNotifier({
      platform: 'win32',
      isSupported: () => true,
      show: (_title, body) => shown.push(body),
      vault: {
        get: async (key) => entries.get(key) ?? null,
        store: async (key, value) => {
          entries.set(key, value);
        },
      },
      logger: recordingLogger().logger,
    });
    const app = new FakeApp();
    const window = new FakeWindow();
    const lifecycle = installAppLifecycle({
      app: app as unknown as AppLifecycleDeps['app'],
      platform: 'linux',
      supervisor: () => fakeWorker(),
      keepRunning: () => true,
      window: () => window,
      createWindow: () => undefined,
      onHiddenToTray: () => void notifier.hiddenToTray(),
      onAsk: () => undefined,
      dispose: () => undefined,
      logger: recordingLogger().logger,
    });

    lifecycle.onWindowClose({ preventDefault: () => undefined });
    await settle();
    app.emit('second-instance');
    lifecycle.onWindowClose({ preventDefault: () => undefined });
    await settle();

    expect(shown).toEqual(['Scout is still running in the tray.']);
  });
});

describe('keep running when the window is closed: off', () => {
  it('closing the last window quits and stops the worker through before-quit', async () => {
    const { app, worker, closeWindow, disposed } = setup({ keepRunning: false });

    const prevented = closeWindow();
    await settle();

    expect(prevented).toBe(false);
    expect(app.quitRequests).toBe(1);
    expect(worker.status.state).toBe('stopped');
    expect(disposed()).toBe(1);
    expect(app.exitCode).toBe(0);
  });

  it('on macOS closing the last window still leaves the app open, as before', async () => {
    const { app, worker, closeWindow } = setup({ keepRunning: false, platform: 'darwin' });

    closeWindow();
    await settle();

    expect(app.quitRequests).toBe(0);
    expect(worker.status.state).toBe('running');
  });
});

describe('the quick-ask window never keeps Scout alive', () => {
  it('with keep running off, closing the main window quits through before-quit while the hidden quick-ask window is still open', async () => {
    const { app, worker, closeWindow, disposed } = setup({
      keepRunning: false,
      quickAskWindowOpen: true,
    });

    const prevented = closeWindow();
    await settle();

    expect(prevented).toBe(false);
    expect(app.quitRequests).toBe(1);
    expect(worker.status.state).toBe('stopped');
    expect(disposed()).toBe(1);
    expect(app.exitCode).toBe(0);
  });

  it('with keep running on and quick ask opened earlier, closing the main window hides it and Scout keeps running', async () => {
    const { app, worker, windows, closeWindow } = setup({
      keepRunning: true,
      quickAskWindowOpen: true,
    });

    const prevented = closeWindow();
    await settle();

    expect(prevented).toBe(true);
    expect(windows[0]!.visible).toBe(false);
    expect(app.quitRequests).toBe(0);
    expect(worker.status.state).toBe('running');
  });

  it('a second launch or activate shows the main window, never the quick-ask window', () => {
    const quickAskWindow = new FakeWindow();
    quickAskWindow.hide();
    const { app, windows, closeWindow } = setup({ keepRunning: true, quickAskWindowOpen: true });
    closeWindow();

    app.emit('second-instance');
    app.emit('activate');

    expect(windows).toHaveLength(1);
    expect(windows[0]).toMatchObject({ visible: true, focused: true });
    expect(quickAskWindow).toMatchObject({ visible: false, focused: false });
  });
});

describe('Quit Scout from the tray', () => {
  it('goes through the normal before-quit: the worker is stopped, everything is disposed, the app exits', async () => {
    const { app, worker, disposed, closeWindow } = setup({ keepRunning: true });
    const fakeTray = new FakeTray();
    createTrayController({
      createTray: () => fakeTray as unknown as Tray,
      buildContextMenu: (template) => template as unknown as Menu,
      iconPath: '/fake/icon.png',
      onOpen: () => undefined,
      onAskScout: () => undefined,
      onStart: async () => undefined,
      onStop: async () => undefined,
      onQuit: () => app.quit(),
      logger: recordingLogger().logger,
    });

    fakeTray.click('quit');
    await settle();

    expect(worker.status.state).toBe('stopped');
    expect(disposed()).toBe(1);
    expect(app.exitCode).toBe(0);
    expect(closeWindow()).toBe(false);
  });
});

describe('a second launch', () => {
  it('shows and focuses the hidden window instead of opening another', () => {
    const { app, windows, closeWindow } = setup({ keepRunning: true });
    closeWindow();

    app.emit('second-instance');

    expect(windows).toHaveLength(1);
    expect(windows[0]).toMatchObject({ visible: true, focused: true });
  });

  it('restores a minimized window', () => {
    const { app, windows } = setup({ keepRunning: true });
    windows[0]!.minimized = true;

    app.emit('second-instance');

    expect(windows[0]).toMatchObject({ minimized: false, visible: true, focused: true });
  });

  it('second-instance without --ask shows the main window', () => {
    const { app, windows, closeWindow, asked } = setup({ keepRunning: true });
    closeWindow();

    app.emit('second-instance', {}, ['/opt/Scout/scout-desktop']);

    expect(windows[0]).toMatchObject({ visible: true, focused: true });
    expect(asked()).toBe(0);
  });

  it('second-instance with --ask opens quick ask instead of the main window', () => {
    const { app, windows, closeWindow, asked } = setup({ keepRunning: true });
    closeWindow();

    app.emit('second-instance', {}, ['/opt/Scout/scout-desktop', '--ask']);

    expect(asked()).toBe(1);
    expect(windows).toHaveLength(1);
    expect(windows[0]).toMatchObject({ visible: false, focused: false });
  });

  it('opens a new window when the old one is gone', () => {
    const { app, windows, closeWindow } = setup({ keepRunning: false, platform: 'darwin' });
    closeWindow();

    app.emit('activate');

    expect(windows).toHaveLength(2);
    expect(windows[1]!.destroyed).toBe(false);
  });
});

describe('a --hidden launch', () => {
  function launch(options: {
    platform: NodeJS.Platform;
    trayAvailable?: boolean;
    supported?: boolean;
    argv?: string[];
  }) {
    const shown: string[] = [];
    const windowShown: boolean[] = [];
    const window = new EventEmitter();
    let asked = 0;
    const notifier = createPresenceNotifier({
      platform: options.platform,
      isSupported: () => options.supported ?? true,
      show: (_title, body) => shown.push(body),
      vault: { get: async () => null, store: async () => undefined },
      logger: recordingLogger().logger,
    });
    launchWindow({
      argv: options.argv ?? ['/opt/Scout/scout-desktop', '--hidden'],
      platform: options.platform,
      trayAvailable: options.trayAvailable ?? true,
      notifier,
      openWindow: (show) => {
        windowShown.push(show);
        return window;
      },
      onAsk: () => {
        asked += 1;
      },
    });
    return { shown, windowShown, window, asked: () => asked };
  }

  it('on Linux starts with no window and says "Scout is running in the background. Open it from the app menu or the tray."', () => {
    const { shown, windowShown } = launch({ platform: 'linux' });

    expect(windowShown).toEqual([false]);
    expect(shown).toEqual([
      'Scout is running in the background. Open it from the app menu or the tray.',
    ]);
  });

  it('on Linux without notifications shows the window instead of starting hidden', () => {
    const { shown, windowShown } = launch({ platform: 'linux', supported: false });

    expect(windowShown).toEqual([true]);
    expect(shown).toEqual([]);
  });

  it.each(['darwin', 'win32'] as const)(
    'on %s starts with no window and no notification',
    (platform) => {
      const { shown, windowShown } = launch({ platform });

      expect(windowShown).toEqual([false]);
      expect(shown).toEqual([]);
    },
  );

  it('shows the window when the tray is unavailable, so the app is never invisible', () => {
    const { shown, windowShown } = launch({ platform: 'win32', trayAvailable: false });

    expect(windowShown).toEqual([true]);
    expect(shown).toEqual([]);
  });

  it('shows the window on a normal launch', () => {
    const { windowShown } = launch({ platform: 'linux', argv: ['/opt/Scout/scout-desktop'] });

    expect(windowShown).toEqual([true]);
  });

  it('a launch without --ask never opens quick ask, even once the window shows', () => {
    const { window, asked } = launch({ platform: 'linux', argv: ['/opt/Scout/scout-desktop'] });

    window.emit('show');

    expect(asked()).toBe(0);
  });

  it('a visible launch with --ask opens quick ask only once the main window has shown', () => {
    const { window, asked } = launch({
      platform: 'linux',
      argv: ['/opt/Scout/scout-desktop', '--ask'],
    });
    const beforeShow = asked();

    window.emit('show');
    window.emit('show');

    expect(beforeShow).toBe(0);
    expect(asked()).toBe(1);
  });

  it('a hidden launch with --ask opens quick ask right away', () => {
    const { windowShown, asked } = launch({
      platform: 'linux',
      argv: ['/opt/Scout/scout-desktop', '--hidden', '--ask'],
    });

    expect(windowShown).toEqual([false]);
    expect(asked()).toBe(1);
  });
});
