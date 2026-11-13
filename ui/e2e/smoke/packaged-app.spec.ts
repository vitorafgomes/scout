import { execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import { chmod, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import { _electron as electron, expect, test, type Page } from '@playwright/test';

import { isolatedEnv, mainWindow } from '../fixtures/app.js';

interface Packaged {
  readonly page: Page;
  /** The desktop name Electron runs under; its basename is the window class X11 reports and the Wayland app_id. */
  desktopName(): Promise<string | undefined>;
  readonly worker: string;
  /** Every line main wrote to stderr since it started, its own JSON lines and Chromium's alike. */
  mainLog(): Promise<string[]>;
  /** Console errors and uncaught exceptions of the app's windows. */
  readonly rendererErrors: string[];
  /** Requests a window tried to send off the machine. */
  readonly remote: string[];
}

const extracted = process.env['SCOUT_SMOKE_APP'] ?? '';
const work = process.env['SCOUT_SMOKE_WORK'] ?? '';
const REMOTE = /^(https?|wss?):\/\//;
const KNOWN_RENDERER_ERRORS = [
  /^Setting the document's base URI to 'file:\/\/[^']*\/resources\/app\.asar\/packages\/renderer\/dist\/browser\/' violates the following Content Security Policy directive: "base-uri 'none'"\. The action has been blocked\.$/,
];
const run = promisify(execFile);
const cleanups: (() => Promise<void>)[] = [];
const logs: string[] = [];
const FUSE_SENTINEL = 'dL7pKGdnNz796PbbjQWNKmHXBZaB9tsX';
const FUSES = [
  'RunAsNode',
  'EnableCookieEncryption',
  'EnableNodeOptionsEnvironmentVariable',
  'EnableNodeCliInspectArguments',
  'EnableEmbeddedAsarIntegrityValidation',
  'OnlyLoadAppFromAsar',
  'LoadBrowserProcessSpecificV8Snapshot',
  'GrantFileProtocolExtraPrivileges',
] as const;
const FUSE_STATES: Readonly<Record<number, string>> = { 48: 'off', 49: 'on', 114: 'removed' };
let driven = '';

interface FuseWire {
  readonly file: Buffer;
  readonly offset: number;
}

/** The binary's bytes and where its fuse wire starts: one byte per fuse after the sentinel, the version and the length. */
async function fuseWire(binary: string): Promise<FuseWire> {
  const file = await readFile(binary);
  const sentinel = file.indexOf(FUSE_SENTINEL);
  if (sentinel === -1 || file.indexOf(FUSE_SENTINEL, sentinel + 1) !== -1) {
    throw new Error(`Expected one fuse wire in ${binary}`);
  }
  const version = file[sentinel + FUSE_SENTINEL.length];
  const length = file[sentinel + FUSE_SENTINEL.length + 1] ?? 0;
  if (version !== 1 || length < FUSES.length) {
    throw new Error(`Unknown fuse wire in ${binary}: version ${version}, ${length} fuses`);
  }
  return { file, offset: sentinel + FUSE_SENTINEL.length + 2 };
}

async function fusesOf(binary: string): Promise<Record<string, string>> {
  const { file, offset } = await fuseWire(binary);
  return Object.fromEntries(
    FUSES.map((name, index) => {
      const state = file[offset + index] ?? -1;
      return [name, FUSE_STATES[state] ?? `byte ${state}`];
    }),
  );
}

/**
 * A copy of the packaged app that Playwright can drive: the same files, hard-linked, and a copy of the executable with
 * only the inspector fuse turned back on, since Playwright attaches to main through `--inspect`.
 */
async function drivableCopy(): Promise<string> {
  const copy = path.join(await mkdtemp(path.join(work, 'driven-')), 'app');
  await run('cp', ['-al', extracted, copy]);
  const binary = path.join(copy, 'scout-desktop');
  await run('cp', ['--remove-destination', path.join(extracted, 'scout-desktop'), binary]);
  const wire = await fuseWire(binary);
  wire.file[wire.offset + FUSES.indexOf('EnableNodeCliInspectArguments')] = 49;
  await writeFile(binary, wire.file);
  return copy;
}

test.beforeAll(async () => {
  if (process.env['SCOUT_E2E_PRIVATE_SESSION'] !== '1' || extracted === '' || work === '') {
    throw new Error(
      'Run the smoke test with bun run test:smoke, which extracts the AppImage and isolates it.',
    );
  }
  expect(Object.keys(os.networkInterfaces()), 'the smoke test runs with loopback only').toEqual([
    'lo',
  ]);
  expect(
    (process.env['XDG_RUNTIME_DIR'] ?? '').startsWith(`${work}/`),
    "the runtime folder is the run's own",
  ).toBe(true);
  driven = await drivableCopy();
});

test.afterEach(async ({}, testInfo) => {
  if (testInfo.status !== testInfo.expectedStatus) {
    for (const log of logs) {
      await testInfo
        .attach('main-log', { path: log, contentType: 'text/plain' })
        .catch(() => undefined);
    }
  }
  logs.length = 0;
  for (const cleanup of cleanups.splice(0).reverse()) await cleanup();
});

async function launchPackaged(appDir: string): Promise<Packaged> {
  const dir = await mkdtemp(path.join(work, 'run-'));
  cleanups.push(() => rm(dir, { recursive: true, force: true }));
  await mkdir(path.join(dir, 'home'), { recursive: true });
  await mkdir(path.join(dir, 'tmp'));
  const log = path.join(dir, 'main.log');
  logs.push(log);
  const launcher = path.join(dir, 'launch.sh');
  await writeFile(
    launcher,
    `#!/usr/bin/env bash\nexec '${path.join(appDir, 'scout-desktop')}' "$@" 2> >(tee -a '${log}' >&2)\n`,
  );
  await chmod(launcher, 0o755);
  const env = isolatedEnv(dir);
  delete env['SCOUT_WORKER_BIN'];
  delete env['SCOUT_CONTROLPLANE_BIN'];
  delete env['SCOUT_E2E_FETCH_LOG'];
  delete env['SCOUT_E2E_OLLAMA'];

  const app = await electron.launch({
    executablePath: launcher,
    args: [
      `--user-data-dir=${path.join(dir, 'user-data')}`,
      '--password-store=gnome-libsecret',
      '--ozone-platform=x11',
    ],
    cwd: dir,
    env: { ...env, TMPDIR: path.join(dir, 'tmp') },
  });
  const pid = app.process().pid;
  cleanups.push(async () => {
    await Promise.race([
      app.close().catch(() => undefined),
      new Promise((resolve) => setTimeout(resolve, 10_000)),
    ]);
    if (pid !== undefined) await run('kill', ['-KILL', '--', `-${pid}`]).catch(() => undefined);
  });
  const rendererErrors: string[] = [];
  const remote: string[] = [];
  app.context().on('console', (message) => {
    if (message.type() === 'error') rendererErrors.push(message.text());
  });
  app.context().on('weberror', (error) => rendererErrors.push(String(error.error())));
  await app.context().route(REMOTE, async (route) => {
    remote.push(route.request().url());
    await route.abort('blockedbyclient');
  });
  const userData = await app.evaluate(({ app: electronApp }) => electronApp.getPath('userData'));
  if (!userData.startsWith(dir))
    throw new Error(`The app's profile is not this test's: ${userData}`);
  return {
    page: await mainWindow(app),
    desktopName: () => app.evaluate(() => process.env['CHROME_DESKTOP']),
    worker: path.join(appDir, 'resources', 'worker', 'linux-x64', 'Scout.Host.Console'),
    mainLog: async () => (await readFile(log, 'utf8')).split('\n').filter((line) => line !== ''),
    rendererErrors,
    remote,
  };
}

function mainEntries(lines: readonly string[]): Record<string, unknown>[] {
  return lines
    .filter((line) => line.startsWith('{'))
    .map((line) => JSON.parse(line) as Record<string, unknown>);
}

async function useOnThisComputerUntilWorker(page: Page, state: string): Promise<void> {
  await page.getByTestId('use-local').click();
  await expect(page.getByRole('heading', { name: 'Chat', level: 1 })).toBeVisible();
  await page.getByTestId('nav-settings').click();
  await expect(page.getByTestId('worker-state')).toHaveText(state);
}

test("the packaged app cannot be turned into a Node runtime: the shipped binary's fuses turn off run-as-Node, NODE_OPTIONS and the inspector flags, and turn on cookie encryption, asar integrity and loading only from the asar", async () => {
  expect(await fusesOf(path.join(extracted, 'scout-desktop'))).toEqual({
    RunAsNode: 'off',
    EnableCookieEncryption: 'on',
    EnableNodeOptionsEnvironmentVariable: 'off',
    EnableNodeCliInspectArguments: 'off',
    EnableEmbeddedAsarIntegrityValidation: 'on',
    OnlyLoadAppFromAsar: 'on',
    LoadBrowserProcessSpecificV8Snapshot: 'off',
    GrantFileProtocolExtraPrivileges: 'on',
  });
});

test('the window opens: the packaged app shows the welcome screen on a fresh profile', async () => {
  const { page, remote } = await launchPackaged(driven);

  await expect(
    page.getByRole('heading', { name: 'Choose where you chat with Scout' }),
  ).toBeVisible();
  await expect(page.getByTestId('use-local')).toBeVisible();
  expect(remote, 'no request may leave the machine').toEqual([]);
});

test('the installed app gets its icon: the packaged launcher entry names the window class the running app reports, and the icon the AppImage carries', async () => {
  const entry = (await readFile(path.join(extracted, 'scout-desktop.desktop'), 'utf8')).split('\n');
  const { desktopName } = await launchPackaged(driven);

  expect(entry).toContain('StartupWMClass=scout-desktop');
  expect(entry).toContain('Icon=scout-desktop');
  expect(await desktopName()).toBe('scout-desktop.desktop');
  expect(
    existsSync(path.join(extracted, 'usr/share/icons/hicolor/512x512/apps/scout-desktop.png')),
  ).toBe(true);
});

test('the packaged worker starts: main spawns the worker and the control plane shipped in the AppImage and the worker reports ready', async () => {
  const { page, worker, mainLog, remote } = await launchPackaged(driven);

  await useOnThisComputerUntilWorker(page, 'Running');

  const entries = mainEntries(await mainLog());
  const binaries = (msg: string): unknown[] =>
    entries
      .filter((entry) => entry['msg'] === msg)
      .map((entry) => (entry['ctx'] as Record<string, unknown>)['binary']);
  expect(existsSync(worker)).toBe(true);
  expect(binaries('supervisor: worker spawned')).toEqual([worker]);
  expect(binaries('control-plane: host spawned')).toEqual([
    path.join(path.dirname(worker), 'Scout.Host.ControlPlane'),
  ]);
  expect(entries.filter((entry) => entry['msg'] === 'control-plane: unavailable')).toEqual([]);
  expect(remote, 'no request may leave the machine').toEqual([]);
});

test('no startup error: main logs no error and the windows report none until the worker runs', async () => {
  const { page, mainLog, rendererErrors, remote } = await launchPackaged(driven);

  await useOnThisComputerUntilWorker(page, 'Running');

  const lines = await mainLog();
  expect(
    mainEntries(lines).filter((entry) => entry['lvl'] === 'error'),
    'main logged an error',
  ).toEqual([]);
  expect(
    lines.filter((line) => /Uncaught|Unhandled|Error occurred in handler/.test(line)),
    'main threw',
  ).toEqual([]);
  expect(
    rendererErrors.filter((error) => !KNOWN_RENDERER_ERRORS.some((known) => known.test(error))),
    'a window reported an error',
  ).toEqual([]);
  expect(remote, 'no request may leave the machine').toEqual([]);
});

test('a missing worker shows an error, not a hang: the worker is crashed and Start names the missing binary', async () => {
  const copy = path.join(await mkdtemp(path.join(work, 'no-worker-')), 'app');
  cleanups.push(() => rm(path.dirname(copy), { recursive: true, force: true }));
  await run('cp', ['-al', driven, copy]);
  const worker = path.join(copy, 'resources', 'worker', 'linux-x64', 'Scout.Host.Console');
  await rm(worker);
  const { page } = await launchPackaged(copy);

  await useOnThisComputerUntilWorker(page, 'Stopped unexpectedly');
  await page.getByTestId('worker-start').click();

  await expect(page.getByTestId('worker-error')).toContainText(
    `Worker binary not found: ${worker}`,
  );
});
