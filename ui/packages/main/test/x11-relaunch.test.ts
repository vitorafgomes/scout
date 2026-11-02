import { describe, expect, it } from 'bun:test';
import { EventEmitter } from 'node:events';
import {
  OZONE_X11_FLAG,
  restartThroughX11,
  x11RelaunchArgs as relaunchFor,
  type X11RestartDeps,
} from '../src/shell/x11-relaunch.js';
import { recordingLogger } from './kubernetes-fakes.js';

const EXEC = '/tmp/.mount_ScoutAb12/scout-desktop';

function x11RelaunchArgs(
  platform: string,
  env: Record<string, string | undefined>,
  argv: readonly string[],
): string[] | null {
  return relaunchFor(platform, env, argv, EXEC, true)?.args ?? null;
}

const WAYLAND_WITH_XWAYLAND = {
  XDG_SESSION_TYPE: 'wayland',
  WAYLAND_DISPLAY: 'wayland-1',
  DISPLAY: ':1',
};

describe('x11RelaunchArgs', () => {
  it('on Linux in a Wayland session with XWayland, restarts with the X11 ozone flag', () => {
    expect(x11RelaunchArgs('linux', WAYLAND_WITH_XWAYLAND, [])).toEqual(['--ozone-platform=x11']);
    expect(OZONE_X11_FLAG).toBe('--ozone-platform=x11');
  });

  it('keeps the launch arguments, such as --ask, in order before the flag', () => {
    expect(x11RelaunchArgs('linux', WAYLAND_WITH_XWAYLAND, ['--hidden', '--ask'])).toEqual([
      '--hidden',
      '--ask',
      '--ozone-platform=x11',
    ]);
  });

  it('detects the Wayland session from WAYLAND_DISPLAY alone', () => {
    expect(x11RelaunchArgs('linux', { WAYLAND_DISPLAY: 'wayland-0', DISPLAY: ':0' }, [])).toEqual([
      '--ozone-platform=x11',
    ]);
  });

  it.each(['darwin', 'win32'])('never restarts on %s', (platform) => {
    expect(x11RelaunchArgs(platform, WAYLAND_WITH_XWAYLAND, [])).toBeNull();
  });

  it('never restarts in an X11 session', () => {
    expect(x11RelaunchArgs('linux', { XDG_SESSION_TYPE: 'x11', DISPLAY: ':0' }, [])).toBeNull();
  });

  it('stays on native Wayland when there is no X server (DISPLAY unset), since X11 would not start', () => {
    expect(
      x11RelaunchArgs('linux', { XDG_SESSION_TYPE: 'wayland', WAYLAND_DISPLAY: 'wayland-1' }, []),
    ).toBeNull();
  });

  it.each(['--ozone-platform=x11 --ask', '--ask --ozone-platform=wayland', '--ozone-platform x11'])(
    'never restarts when an ozone platform is already chosen (%p), so there is no relaunch loop',
    (args) => {
      expect(x11RelaunchArgs('linux', WAYLAND_WITH_XWAYLAND, args.split(' '))).toBeNull();
    },
  );

  it('a packaged app started from an AppImage relaunches the AppImage, not the binary inside its mount', () => {
    const env = { ...WAYLAND_WITH_XWAYLAND, APPIMAGE: '/home/owner/Apps/Scout.AppImage' };

    expect(relaunchFor('linux', env, ['--ask'], EXEC, true)).toEqual({
      execPath: '/home/owner/Apps/Scout.AppImage',
      args: ['--ask', '--ozone-platform=x11'],
    });
  });

  it('a packaged app without an AppImage relaunches its own executable', () => {
    expect(relaunchFor('linux', WAYLAND_WITH_XWAYLAND, [], EXEC, true)?.execPath).toBe(EXEC);
  });

  it('outside a packaged app an inherited APPIMAGE from another program is ignored', () => {
    const env = { ...WAYLAND_WITH_XWAYLAND, APPIMAGE: '/home/owner/Apps/Other.AppImage' };

    expect(
      relaunchFor('linux', env, ['.'], '/repo/node_modules/electron/dist/electron', false),
    ).toEqual({
      execPath: '/repo/node_modules/electron/dist/electron',
      args: ['.', '--ozone-platform=x11'],
    });
  });
});

describe('restartThroughX11', () => {
  const decision = {
    execPath: '/home/owner/Apps/Scout.AppImage',
    args: ['--ask', '--ozone-platform=x11'],
  };

  function fakes() {
    const child = Object.assign(new EventEmitter(), { unrefs: 0, unref: () => child.unrefs++ });
    const spawned: unknown[][] = [];
    const exits: number[] = [];
    const { logger, lines } = recordingLogger();
    const deps: X11RestartDeps = {
      spawn: (...call) => {
        spawned.push(call);
        return child;
      },
      exit: (code) => exits.push(code),
      logger,
    };
    return { child, spawned, exits, lines, deps };
  }

  it('starts the new process detached, lets it outlive this one, and exits once it is running', async () => {
    const f = fakes();

    const restarting = restartThroughX11(decision, f.deps);
    f.child.emit('spawn');

    expect(await restarting).toBe(true);
    expect(f.spawned).toEqual([
      [decision.execPath, decision.args, { detached: true, stdio: 'ignore' }],
    ]);
    expect(f.child.unrefs).toBe(1);
    expect(f.exits).toEqual([0]);
    expect(f.lines.some((l) => l.includes('relaunching through XWayland'))).toBe(true);
  });

  it('when the new process cannot start, keeps running as a native Wayland client and warns why', async () => {
    const f = fakes();

    const restarting = restartThroughX11(decision, f.deps);
    f.child.emit(
      'error',
      Object.assign(new Error('spawn /home/owner/Apps/Scout.AppImage EACCES'), { code: 'EACCES' }),
    );

    expect(await restarting).toBe(false);
    expect(f.exits).toEqual([]);
    const warning = f.lines.find((l) =>
      l.includes('relaunch through XWayland failed; staying on native Wayland'),
    );
    expect(warning).toContain('EACCES');
  });
});
