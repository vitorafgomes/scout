import { describe, it, expect } from 'bun:test';
import { createTrayController } from '../src/tray/tray.js';
import type { TrayDeps } from '../src/tray/tray.js';
import type { WorkerStatus } from '@scout/shared';
import type { Logger } from '../src/util/logger.js';

// ---------------------------------------------------------------------------
// Fake helpers
// ---------------------------------------------------------------------------

interface FakeMenuItemConstructorOptions {
  label?: string;
  type?: string;
  id?: string;
  enabled?: boolean;
  click?: () => void;
}

class FakeMenu {
  readonly template: FakeMenuItemConstructorOptions[];

  constructor(template: FakeMenuItemConstructorOptions[]) {
    this.template = template;
  }

  itemById(id: string): FakeMenuItemConstructorOptions | undefined {
    return this.template.find((item) => item.id === id);
  }

  hasItemById(id: string): boolean {
    return this.template.some((item) => item.id === id);
  }

  labels(): string[] {
    return this.template.map((item) => item.label).filter((l): l is string => l !== undefined);
  }
}

class FakeTray {
  private contextMenu: FakeMenu | null = null;
  private tooltip = '';
  private destroyed = false;
  private readonly listeners: Map<string, Array<() => void>> = new Map();
  readonly destroyCalls: number[] = [];

  on(event: string, cb: () => void): this {
    const existing = this.listeners.get(event) ?? [];
    existing.push(cb);
    this.listeners.set(event, existing);
    return this;
  }

  emit(event: string): void {
    const cbs = this.listeners.get(event) ?? [];
    for (const cb of cbs) cb();
  }

  setContextMenu(menu: FakeMenu): void {
    this.contextMenu = menu;
  }

  setToolTip(tip: string): void {
    this.tooltip = tip;
  }

  popUpContextMenu(): void {}

  isDestroyed(): boolean {
    return this.destroyed;
  }

  destroy(): void {
    this.destroyed = true;
    this.destroyCalls.push(Date.now());
  }

  getContextMenu(): FakeMenu | null {
    return this.contextMenu;
  }

  getToolTip(): string {
    return this.tooltip;
  }
}

function makeNullLogger(): Logger {
  return {
    debug() {},
    info() {},
    warn() {},
    error() {},
    child() {
      return makeNullLogger();
    },
  };
}

function makeDeps(overrides?: Partial<TrayDeps>): {
  deps: TrayDeps;
  fakeTray: FakeTray;
  menus: FakeMenu[];
  calls: { showWindow: number; quit: number; stopWorker: number };
} {
  const fakeTray = new FakeTray();
  const menus: FakeMenu[] = [];
  const calls = { showWindow: 0, quit: 0, stopWorker: 0 };

  const deps: TrayDeps = {
    createTray: (_iconPath: string) => fakeTray as unknown as Electron.Tray,
    buildContextMenu: (template) => {
      const menu = new FakeMenu(
        template as FakeMenuItemConstructorOptions[],
      ) as unknown as Electron.Menu;
      menus.push(menu as unknown as FakeMenu);
      return menu;
    },
    iconPath: '/fake/icon.png',
    onShowWindow: () => {
      calls.showWindow += 1;
    },
    onStopWorker: async () => {
      calls.stopWorker += 1;
    },
    onQuit: () => {
      calls.quit += 1;
    },
    logger: makeNullLogger(),
    ...overrides,
  };

  return { deps, fakeTray, menus, calls };
}

function statusRunning(): WorkerStatus {
  return { state: 'running', restarts: 0 };
}

function statusIdle(): WorkerStatus {
  return { state: 'idle', restarts: 0 };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('createTrayController', () => {
  it('calls createTray with the provided iconPath and assigns a context menu', () => {
    const { deps, fakeTray, menus } = makeDeps();

    createTrayController(deps);

    expect(fakeTray.isDestroyed()).toBe(false);
    expect(menus.length).toBeGreaterThanOrEqual(1);
  });

  it('calls onShowWindow when the Show Scout Desktop menu item is clicked', () => {
    const { deps, menus, calls } = makeDeps({ initialStatus: statusIdle() });

    createTrayController(deps);

    const lastMenu = menus.at(-1)!;
    const showItem = lastMenu.itemById('show-window');
    expect(showItem).toBeDefined();
    showItem!.click!();

    expect(calls.showWindow).toBe(1);
  });

  it('calls onQuit when the Quit Scout Desktop menu item is clicked', () => {
    const { deps, menus, calls } = makeDeps({ initialStatus: statusIdle() });

    createTrayController(deps);

    const lastMenu = menus.at(-1)!;
    const quitItem = lastMenu.itemById('quit');
    expect(quitItem).toBeDefined();
    quitItem!.click!();

    expect(calls.quit).toBe(1);
  });

  it('calls onStopWorker when Stop worker is clicked', async () => {
    const { deps, menus, calls } = makeDeps({ initialStatus: statusRunning() });

    createTrayController(deps);

    const lastMenu = menus.at(-1)!;
    const stopItem = lastMenu.itemById('stop-worker');
    expect(stopItem).toBeDefined();
    stopItem!.click!();

    // Give the async click handler a tick to resolve.
    await new Promise((r) => setTimeout(r, 0));

    expect(calls.stopWorker).toBe(1);
  });

  it('logs an error and does NOT throw when onStopWorker rejects', async () => {
    const errors: unknown[] = [];
    const logger: Logger = {
      ...makeNullLogger(),
      error(_msg: string, err?: unknown) {
        errors.push(err);
      },
    };

    const { deps, menus } = makeDeps({
      initialStatus: statusRunning(),
      onStopWorker: async () => {
        throw new Error('stop failed');
      },
      logger,
    });

    createTrayController(deps);

    const stopItem = menus.at(-1)!.itemById('stop-worker');
    expect(stopItem).toBeDefined();

    let threw = false;
    try {
      stopItem!.click!();
      await new Promise((r) => setTimeout(r, 10));
    } catch {
      threw = true;
    }

    expect(threw).toBe(false);
    expect(errors.length).toBe(1);
  });

  it('setStatus rebuilds the menu with the new status label', () => {
    const { deps, menus } = makeDeps({ initialStatus: statusIdle() });

    const controller = createTrayController(deps);
    const menusBefore = menus.length;

    controller.setStatus(statusRunning());

    expect(menus.length).toBe(menusBefore + 1);
    const newMenu = menus.at(-1)!;
    const statusItem = newMenu.itemById('status-label');
    expect(statusItem?.label).toBe('Status: Running');
  });

  it('hides Stop worker when status is idle', () => {
    const { deps, menus } = makeDeps({ initialStatus: statusRunning() });

    const controller = createTrayController(deps);

    controller.setStatus(statusIdle());

    const lastMenu = menus.at(-1)!;
    expect(lastMenu.hasItemById('stop-worker')).toBe(false);
  });

  it('shows Stop worker when status is running', () => {
    const { deps, menus } = makeDeps({ initialStatus: statusIdle() });

    const controller = createTrayController(deps);

    controller.setStatus(statusRunning());

    const lastMenu = menus.at(-1)!;
    expect(lastMenu.hasItemById('stop-worker')).toBe(true);
  });

  it('shows Stop worker when status is starting', () => {
    const { deps, menus } = makeDeps({ initialStatus: statusIdle() });

    const controller = createTrayController(deps);

    controller.setStatus({ state: 'starting', restarts: 0 });

    const lastMenu = menus.at(-1)!;
    expect(lastMenu.hasItemById('stop-worker')).toBe(true);
  });

  it('calls tray.destroy() when destroy() is invoked', () => {
    const { deps, fakeTray } = makeDeps();

    const controller = createTrayController(deps);
    controller.destroy();

    expect(fakeTray.isDestroyed()).toBe(true);
    expect(fakeTray.destroyCalls.length).toBe(1);
  });

  it('menu order is stable across setStatus calls', () => {
    const { deps, menus } = makeDeps({ initialStatus: statusIdle() });

    const controller = createTrayController(deps);
    controller.setStatus(statusRunning());
    controller.setStatus(statusIdle());
    controller.setStatus(statusRunning());

    for (const menu of menus) {
      const ids = menu.template.filter((i) => i.id !== undefined).map((i) => i.id);

      expect(ids[0]).toBe('show-window');
      expect(ids[1]).toBe('status-label');

      const lastId = ids.at(-1);
      expect(lastId).toBe('quit');
    }
  });

  it('status label reads "Status: Idle" for idle state', () => {
    const { deps, menus } = makeDeps({ initialStatus: statusIdle() });

    createTrayController(deps);

    const statusItem = menus.at(-1)!.itemById('status-label');
    expect(statusItem?.label).toBe('Status: Idle');
  });

  it('status label reads "Status: Crashed" for crashed state', () => {
    const { deps, menus } = makeDeps({ initialStatus: statusIdle() });

    const controller = createTrayController(deps);
    controller.setStatus({ state: 'crashed', restarts: 2 });

    const statusItem = menus.at(-1)!.itemById('status-label');
    expect(statusItem?.label).toBe('Status: Crashed');
  });

  it('status item is disabled (non-interactive label)', () => {
    const { deps, menus } = makeDeps({ initialStatus: statusRunning() });

    createTrayController(deps);

    const statusItem = menus.at(-1)!.itemById('status-label');
    expect(statusItem?.enabled).toBe(false);
  });
});
