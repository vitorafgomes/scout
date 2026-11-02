import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { mkdir, mkdtemp, readdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { readIpcError } from '@scout/shared';
import { createLoginItem, type LoginItemDeps } from '../src/shell/login-item.js';

function fakeApp() {
  const calls: { openAtLogin: boolean; args?: string[] }[] = [];
  let openAtLogin = false;
  return {
    calls,
    app: {
      getLoginItemSettings: () => ({ openAtLogin }),
      setLoginItemSettings(settings: { openAtLogin: boolean; args?: string[] }) {
        calls.push(settings);
        openAtLogin = settings.openAtLogin;
      },
    } satisfies LoginItemDeps['app'],
  };
}

describe.each(['darwin', 'win32'] as const)('start at login on %s', (platform) => {
  it('turning it on registers Scout as a login item that starts with --hidden', async () => {
    const { app, calls } = fakeApp();
    const item = createLoginItem({ platform, home: '/unused', executable: '/unused', app });

    await item.setEnabled(true);

    expect(calls).toEqual([{ openAtLogin: true, args: ['--hidden'] }]);
    expect(await item.isEnabled()).toBe(true);
  });

  it('turning it off removes the login item', async () => {
    const { app, calls } = fakeApp();
    const item = createLoginItem({ platform, home: '/unused', executable: '/unused', app });
    await item.setEnabled(true);

    await item.setEnabled(false);

    expect(calls.at(-1)).toEqual({ openAtLogin: false, args: ['--hidden'] });
    expect(await item.isEnabled()).toBe(false);
  });
  it('when the OS refuses the login item, rejects with a typed autostart error and stays off', async () => {
    const item = createLoginItem({
      platform,
      home: '/unused',
      executable: '/unused',
      app: {
        getLoginItemSettings: () => ({ openAtLogin: false }),
        setLoginItemSettings: () => {
          throw new Error('login items unavailable');
        },
      },
    });

    const error = await item.setEnabled(true).then(
      () => null,
      (err: unknown) => err,
    );

    expect(readIpcError(error).code).toBe('desktop/autostart-failed');
    expect(await item.isEnabled()).toBe(false);
  });
});

describe('start at login on Linux', () => {
  let home: string;
  const executable = '/home/owner/Applications/Scout Desktop.AppImage';

  beforeEach(async () => {
    home = await mkdtemp(path.join(os.tmpdir(), 'scout-home-'));
  });

  afterEach(async () => {
    await rm(home, { recursive: true, force: true });
  });

  function linuxItem(exe = executable) {
    return createLoginItem({ platform: 'linux', home, executable: exe, app: fakeApp().app });
  }

  const entryPath = () => path.join(home, '.config', 'autostart', 'scout.desktop');

  it('writes exactly the expected entry to <home>/.config/autostart/scout.desktop', async () => {
    await linuxItem().setEnabled(true);

    expect(await readFile(entryPath(), 'utf8')).toBe(
      [
        '[Desktop Entry]',
        'Type=Application',
        'Name=Scout',
        'Exec="/home/owner/Applications/Scout Desktop.AppImage" --hidden',
        'X-GNOME-Autostart-enabled=true',
        '',
      ].join('\n'),
    );
    expect(await readdir(path.dirname(entryPath()))).toEqual(['scout.desktop']);
    expect(await linuxItem().isEnabled()).toBe(true);
  });

  it.skipIf(process.platform === 'win32')('writes the entry with mode 0644', async () => {
    const previous = process.umask(0o077);
    try {
      await linuxItem().setEnabled(true);
    } finally {
      process.umask(previous);
    }

    expect((await stat(entryPath())).mode & 0o777).toBe(0o644);
  });

  it('turning it off removes only scout.desktop and leaves the other autostart entries', async () => {
    const dir = path.dirname(entryPath());
    await mkdir(dir, { recursive: true });
    await writeFile(path.join(dir, 'other-app.desktop'), 'kept');
    await linuxItem().setEnabled(true);

    await linuxItem().setEnabled(false);

    expect(await readdir(dir)).toEqual(['other-app.desktop']);
    expect(await readFile(path.join(dir, 'other-app.desktop'), 'utf8')).toBe('kept');
    expect(await linuxItem().isEnabled()).toBe(false);
  });

  it('a failed write rejects with a typed autostart error and leaves start at login off', async () => {
    await mkdir(path.join(home, '.config'), { recursive: true });
    await writeFile(path.join(home, '.config', 'autostart'), 'a file where the folder should be');

    const error = await linuxItem()
      .setEnabled(true)
      .then(
        () => null,
        (err: unknown) => err,
      );

    expect(readIpcError(error).code).toBe('desktop/autostart-failed');
    expect(await linuxItem().isEnabled()).toBe(false);
  });

  it('refuses an executable path that would break the Exec line and writes nothing', async () => {
    const error = await linuxItem('/tmp/evil"\nExec=/bin/sh')
      .setEnabled(true)
      .then(
        () => null,
        (err: unknown) => err,
      );

    expect(readIpcError(error).code).toBe('desktop/autostart-failed');
    expect(await linuxItem().isEnabled()).toBe(false);
  });
});
