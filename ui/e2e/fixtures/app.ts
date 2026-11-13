import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, statSync } from 'node:fs';
import { chmod, cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import {
  _electron as electron,
  expect,
  test as base,
  type ElectronApplication,
  type Page,
} from '@playwright/test';

/** One scripted answer of the fake worker: the parts it streams, then the final text, unless it waits. */
export interface FakeReply {
  readonly updates?: readonly string[];
  readonly delayMs?: number;
  readonly complete?: string;
  readonly holdUntilCancel?: boolean;
  readonly holdUntilFile?: string;
  /** Lines sent right after the reply begins, with the reply's id unless they set their own (`null` for a notice). */
  readonly events?: readonly Readonly<Record<string, unknown>>[];
  /** Lines sent just before the reply completes or fails, the same way. */
  readonly endEvents?: readonly Readonly<Record<string, unknown>>[];
}

/** One kept meeting as the fake worker lists it on the Meetings screen. */
export interface FakeMeeting {
  readonly id: string;
  readonly startedAt: string;
  readonly endedAt: string;
  readonly title: string;
  readonly lineCount: number;
  readonly turnCount: number;
}

/** The answers the fake worker gives, one per question in order, the last one repeated, and the meetings it lists. */
export interface FakeScript {
  readonly replies: readonly FakeReply[];
  readonly meetings?: readonly FakeMeeting[];
}

/** What the local Ollama answers in a test: its /api/tags rows, and per model the capabilities /api/show reports. */
export interface FakeOllama {
  readonly tags: readonly Readonly<Record<string, unknown>>[];
  readonly capabilities: Readonly<Record<string, readonly string[]>>;
  /** Names the fake library does not have: a pull of one answers Ollama's not-found error line. */
  readonly missing?: readonly string[];
}

export interface Launched {
  readonly app: ElectronApplication;
  readonly page: Page;
}

export interface Desktop {
  /** Starts the built app on this test's profile, with the fake worker, and answers its main window. */
  launch(): Promise<Launched>;
  /** Every line the fake worker read from its stdin, parsed. */
  received(): Promise<Record<string, unknown>[]>;
  /** Lets a reply waiting on `holdUntilFile` finish. */
  release(file: string): Promise<void>;
  /** Kills the control plane host the app started, as a crash would. */
  killControlPlane(): void;
}

const ui = path.resolve(import.meta.dirname, '..', '..');
const mainEntry = path.join(ui, 'packages', 'main', 'dist', 'main.js');
const mainFetchGuard = path.join(import.meta.dirname, 'main-fetch-guard.cjs');
const controlPlaneBuilds = ['Release', 'Debug'].map((configuration) =>
  path.resolve(ui, '..', 'src', 'Scout.Host.ControlPlane', 'bin', configuration, 'net10.0'),
);
const MAX_SOCKET_PATH = 100;
const FIXTURE_SCOUT_KEYS = [
  'SCOUT_CONTROLPLANE_BIN',
  'SCOUT_E2E_FETCH_LOG',
  'SCOUT_E2E_OLLAMA',
  'SCOUT_WORKER_BIN',
];
const electronBinary = createRequire(path.join(ui, 'packages', 'main', 'package.json'))(
  'electron',
) as unknown as string;
const OWN_WINDOWS = /#\/(quick-ask|floating-button|operator)/;
const REMOTE = /^(https?|wss?):\/\//;
const REMOTE_IN_TEXT = /\b(https?|wss?):\/\//;
const LOOPBACK = /^(https?|wss?):\/\/(localhost|127\.0\.0\.1|\[::1\])(:|\/|$)/;

export function isolatedEnv(dir: string): Record<string, string> {
  const home = path.join(dir, 'home');
  const env: Record<string, string> = {};
  for (const [key, value] of Object.entries(process.env)) {
    if (value !== undefined && !/^scout_/i.test(key)) env[key] = value;
  }
  for (const key of [
    'WAYLAND_DISPLAY',
    'XDG_CURRENT_DESKTOP',
    'DESKTOP_SESSION',
    'NODE_ENV',
    'ELECTRON_RUN_AS_NODE',
    'APPIMAGE',
    'KUBECONFIG',
    'DOCKER_CONTEXT',
  ]) {
    delete env[key];
  }
  return {
    ...env,
    HOME: home,
    XDG_CONFIG_HOME: path.join(home, '.config'),
    XDG_DATA_HOME: path.join(home, '.local', 'share'),
    XDG_CACHE_HOME: path.join(home, '.cache'),
    XDG_STATE_HOME: path.join(home, '.local', 'state'),
    XDG_SESSION_TYPE: 'x11',
    SCOUT_WORKER_BIN: path.join(dir, 'worker', 'fake-worker.mjs'),
    SCOUT_CONTROLPLANE_BIN: path.join(dir, 'no-control-plane'),
    SCOUT_E2E_FETCH_LOG: path.join(dir, 'main-fetch.log'),
    SCOUT_E2E_OLLAMA: path.join(dir, 'ollama.json'),
  };
}

function builtAt(build: string): number {
  return Math.max(...readdirSync(build).map((file) => statSync(path.join(build, file)).mtimeMs));
}

/** The control plane host of the build written last, Release or Debug. */
function newestControlPlaneHost(): string {
  const built = controlPlaneBuilds.filter((build) =>
    existsSync(path.join(build, 'Scout.Host.ControlPlane')),
  );
  if (built.length === 0) {
    throw new Error(
      "The control plane host is not built. Run 'dotnet build src/Scout.Host.ControlPlane' first.",
    );
  }
  const newest = built.reduce((a, b) => (builtAt(b) > builtAt(a) ? b : a));
  return path.join(newest, 'Scout.Host.ControlPlane');
}

export async function mainWindow(app: ElectronApplication): Promise<Page> {
  const find = (): Page | undefined =>
    app.windows().find((page) => page.url().startsWith('file:') && !OWN_WINDOWS.test(page.url()));
  await expect.poll(() => find() !== undefined, { timeout: 30_000 }).toBe(true);
  const page = find();
  if (page === undefined) throw new Error('The main window closed as it opened.');
  return page;
}

export const test = base.extend<{
  script: FakeScript;
  scoutData: string | null;
  ollama: FakeOllama | null;
  desktop: Desktop;
}>({
  script: [{ replies: [] }, { option: true }],

  scoutData: [null, { option: true }],

  ollama: [null, { option: true }],

  desktop: async ({ script, scoutData, ollama }, use, testInfo) => {
    if (process.env['SCOUT_E2E_PRIVATE_SESSION'] !== '1') {
      throw new Error(
        'Run the end-to-end tests with bun run test:e2e, which gives them a private keyring and display.',
      );
    }
    const host = scoutData === null ? null : newestControlPlaneHost();
    const socket = path.join(
      os.tmpdir(),
      'scout-e2e-XXXXXX',
      'run',
      'scout',
      'controlplane-4194304.sock',
    );
    if (socket.length >= MAX_SOCKET_PATH) {
      throw new Error(
        `The temporary folder ${os.tmpdir()} (TMPDIR) is too long: the control plane's socket would be ${socket}, and a socket path must stay under ${MAX_SOCKET_PATH} characters. Run with a shorter TMPDIR.`,
      );
    }
    const dir = await mkdtemp(path.join(os.tmpdir(), 'scout-e2e-'));
    const worker = path.join(dir, 'worker');
    const running: ElectronApplication[] = [];
    const remote: string[] = [];
    const log: string[] = [];
    try {
      await mkdir(path.join(dir, 'home'), { recursive: true });
      await mkdir(path.join(dir, 'run'), { mode: 0o700 });
      await mkdir(path.join(dir, 'tmp'));
      await mkdir(worker);
      const env: Record<string, string> = {
        ...isolatedEnv(dir),
        XDG_RUNTIME_DIR: path.join(dir, 'run'),
        TMPDIR: path.join(dir, 'tmp'),
        DOCKER_HOST: `unix://${path.join(dir, 'no-docker.sock')}`,
        TZ: 'UTC',
      };
      if (scoutData !== null && host !== null) {
        await cp(scoutData, path.join(dir, 'home', '.local', 'share', 'Scout'), {
          recursive: true,
        });
        env['SCOUT_CONTROLPLANE_BIN'] = host;
      }
      const source = await readFile(path.join(import.meta.dirname, 'fake-worker.mjs'), 'utf8');
      await writeFile(path.join(worker, 'fake-worker.mjs'), `#!${process.execPath}\n${source}`);
      await chmod(path.join(worker, 'fake-worker.mjs'), 0o755);
      await writeFile(path.join(worker, 'script.json'), JSON.stringify(script));
      if (ollama !== null) await writeFile(path.join(dir, 'ollama.json'), JSON.stringify(ollama));

      const desktop: Desktop = {
        async launch() {
          const app = await electron.launch({
            executablePath: electronBinary,
            args: [
              '-r',
              mainFetchGuard,
              mainEntry,
              `--user-data-dir=${path.join(dir, 'user-data')}`,
              '--password-store=gnome-libsecret',
              '--ozone-platform=x11',
            ],
            cwd: ui,
            env,
          });
          running.push(app);
          app.process().stdout?.on('data', (chunk: Buffer) => log.push(chunk.toString()));
          app.process().stderr?.on('data', (chunk: Buffer) => log.push(chunk.toString()));
          app.context().on('console', (message) => {
            if (
              message.text().includes('Content Security Policy') &&
              REMOTE_IN_TEXT.test(message.text())
            )
              remote.push(message.text());
          });
          await app.context().route(REMOTE, async (route) => {
            remote.push(route.request().url());
            await route.abort('blockedbyclient');
          });
          await app.context().tracing.start({ screenshots: true, snapshots: true });
          const seen = await app.evaluate(({ app: electronApp }) => ({
            userData: electronApp.getPath('userData'),
            runtime: process.env['XDG_RUNTIME_DIR'] ?? '',
            tmp: process.getBuiltinModule('node:os').tmpdir(),
            docker: process.env['DOCKER_HOST'] ?? '',
            zone: Intl.DateTimeFormat().resolvedOptions().timeZone,
            inherited: Object.keys(process.env)
              .filter((key) => /^(scout_|kubeconfig$|docker_context$)/i.test(key))
              .sort(),
          }));
          const strays = [
            seen.userData.startsWith(dir) ? '' : `its profile is ${seen.userData}`,
            seen.runtime === path.join(dir, 'run') ? '' : `its runtime folder is '${seen.runtime}'`,
            seen.tmp === path.join(dir, 'tmp') ? '' : `its temporary folder is ${seen.tmp}`,
            seen.docker === `unix://${path.join(dir, 'no-docker.sock')}`
              ? ''
              : `its Docker host is '${seen.docker}'`,
            seen.zone === 'UTC' ? '' : `its time zone is ${seen.zone}`,
            seen.inherited.join() === FIXTURE_SCOUT_KEYS.join()
              ? ''
              : `it sees ${seen.inherited.join(', ')} where only ${FIXTURE_SCOUT_KEYS.join(', ')} are the test's`,
          ].filter((stray) => stray !== '');
          if (strays.length > 0) {
            throw new Error(
              `The app is not confined to this test's folder ${dir}: ${strays.join('; ')}.`,
            );
          }
          return { app, page: await mainWindow(app) };
        },
        async received() {
          const text = await readFile(path.join(worker, 'received.jsonl'), 'utf8').catch(() => '');
          return text
            .split('\n')
            .filter((line) => line !== '')
            .map((line) => JSON.parse(line) as Record<string, unknown>);
        },
        async release(file) {
          await writeFile(path.join(worker, file), '');
        },
        killControlPlane() {
          const main = running.at(-1)?.process().pid;
          const host = execFileSync(
            'pgrep',
            ['-P', String(main), '-f', 'Scout.Host.ControlPlane'],
            {
              encoding: 'utf8',
            },
          );
          process.kill(Number(host), 'SIGKILL');
        },
      };

      await use(desktop);

      const failed = testInfo.status !== testInfo.expectedStatus;
      if (failed) {
        await testInfo.attach('main-log', { body: log.join(''), contentType: 'text/plain' });
        if (host !== null) await testInfo.attach('control-plane-host', { body: host });
      }
      for (const [index, app] of running.entries()) {
        if (failed) {
          const trace = testInfo.outputPath(`trace-${index}.zip`);
          await app
            .context()
            .tracing.stop({ path: trace })
            .catch(() => undefined);
          await testInfo.attach(`trace-${index}`, { path: trace }).catch(() => undefined);
        }
        await app.close().catch(() => undefined);
      }
      const mainFetches = await readFile(path.join(dir, 'main-fetch.log'), 'utf8').catch(() => '');
      remote.push(...mainFetches.split('\n').filter((url) => url !== '' && !LOOPBACK.test(url)));
    } finally {
      for (const app of running) await app.close().catch(() => undefined);
      await rm(dir, { recursive: true, force: true });
    }
    expect(remote, 'no request may leave the machine').toEqual([]);
  },
});

export { expect, type Page };

/** Takes the "Use on this computer" path of the welcome screen, which starts Scout and opens the Chat screen. */
export async function useOnThisComputer(page: Page): Promise<void> {
  await page.getByTestId('use-local').click();
  await expect(page.getByRole('heading', { name: 'Chat', level: 1 })).toBeVisible();
}

/** Asks a question from the Chat screen's field with Enter. */
export async function ask(page: Page, question: string): Promise<void> {
  const field = page.getByRole('textbox', { name: 'Ask Scout' });
  await field.fill(question);
  await field.press('Enter');
}
