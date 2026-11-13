import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { mkdir, mkdtemp, readFile, rm, stat, symlink, utimes, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import {
  CHANNEL,
  type DockerPullProgress,
  type DockerRepository,
  type DockerRun,
} from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import type { ExecResult } from '../src/detect/types.js';
import {
  CLEAR_UNREADABLE,
  CLEAR_WHILE_PULLING,
  CLEAR_WHILE_RUNNING,
  PINNED_IMAGES,
  PULL_BUSY,
  RUN_IN_PROGRESS_MS,
  createDocker,
  githubSnapshotsFolder,
  sandboxWorkFolder,
  type DockerDeps,
} from '../src/docker/docker.js';
import { registerDockerHandlers } from '../src/ipc/docker.js';
import { recordingLogger } from './kubernetes-fakes.js';

interface Call {
  readonly cmd: string;
  readonly args: readonly string[];
  readonly timeoutMs: number;
}

const ok = (stdout = ''): ExecResult => ({ stdout, stderr: '', exitCode: 0 });
const exit = (code: number, stderr = '', stdout = ''): ExecResult => ({
  stdout,
  stderr,
  exitCode: code,
});
const missing: ExecResult = { stdout: '', stderr: '', exitCode: 1, failure: 'missing' };
const timedOut: ExecResult = { stdout: '', stderr: '', exitCode: 1, failure: 'timeout' };

const SERVER_JSON = JSON.stringify({
  Client: { Version: '28.5.1' },
  Server: { Version: '28.5.1', Os: 'linux' },
});
const CLIENT_ONLY_JSON = JSON.stringify({ Client: { Version: '28.5.1' }, Server: null });

function fakeDocker(
  answer: (args: readonly string[], index: number) => ExecResult | Promise<ExecResult>,
  overrides: Partial<DockerDeps> = {},
) {
  const calls: Call[] = [];
  const { logger, lines } = recordingLogger();
  const docker = createDocker({
    exec: async (cmd, args, timeoutMs) => {
      calls.push({ cmd, args, timeoutMs });
      return answer(args, calls.length - 1);
    },
    dockerPath: 'docker',
    workFolder: '/nonexistent/scout-sandbox',
    snapshotsFolder: '/nonexistent/scout-snapshots',
    now: () => Date.now(),
    logger,
    ...overrides,
  });
  return { docker, calls, lines };
}

const isInspect = (args: readonly string[]): boolean => args[0] === 'image';
const pulls = (calls: readonly Call[]): string[] =>
  calls.filter((c) => c.args[0] === 'pull').map((c) => c.args[1] ?? '');

describe("Docker's state: not installed, not running, running", () => {
  const rows: readonly [string, ExecResult, unknown][] = [
    ['exit 0 with a server version', ok(SERVER_JSON), { state: 'running', version: '28.5.1' }],
    [
      'exit non-zero with a client only',
      exit(1, 'Cannot connect to the Docker daemon', CLIENT_ONLY_JSON),
      { state: 'notRunning' },
    ],
    ['exit 0 with no server in the JSON', ok(CLIENT_ONLY_JSON), { state: 'notRunning' }],
    ['exit 0 with output that is not JSON', ok('Client: 28.5.1'), { state: 'notRunning' }],
    ['the command missing (ENOENT)', missing, { state: 'notInstalled' }],
    ['the 5 s timeout', timedOut, { state: 'notRunning' }],
  ];

  for (const [name, result, expected] of rows) {
    it(`${name} reads as ${(expected as { state: string }).state}`, async () => {
      const { docker, calls } = fakeDocker(() => result);

      expect(await docker.state()).toEqual(expected as never);
      expect(calls).toEqual([
        { cmd: 'docker', args: ['version', '--format', '{{json .}}'], timeoutMs: 5_000 },
      ]);
    });
  }

  it('a timeout is logged with its reason', async () => {
    const { docker, lines } = fakeDocker(() => timedOut);

    await docker.state();

    expect(lines.join('\n')).toContain('docker version timed out');
  });

  it('calls the configured docker path', async () => {
    const { docker, calls } = fakeDocker(() => ok(SERVER_JSON), {
      dockerPath: '/opt/docker/bin/docker',
    });

    await docker.state();

    expect(calls[0]?.cmd).toBe('/opt/docker/bin/docker');
  });
});

describe('the pinned images, present or missing', () => {
  it('inspects every pinned reference and reports each as present or missing', async () => {
    const present = new Set([PINNED_IMAGES[0]!.reference, PINNED_IMAGES[2]!.reference]);
    const { docker, calls } = fakeDocker((args) =>
      present.has(args[args.length - 1] ?? '') ? ok('sha256:abc') : exit(1, 'No such image'),
    );

    const images = await docker.images();

    expect(images.map((i) => [i.reference, i.present])).toEqual(
      PINNED_IMAGES.map((p) => [p.reference, present.has(p.reference)]),
    );
    expect(calls.map((c) => c.args)).toEqual(
      PINNED_IMAGES.map((p) => ['image', 'inspect', '--format', '{{.Id}}', p.reference]),
    );
  });

  const failures: readonly [string, ExecResult][] = [
    ['the command missing', missing],
    ['a non-zero exit', exit(1, 'Cannot connect to the Docker daemon')],
    ['a timeout', timedOut],
  ];
  for (const [name, result] of failures) {
    it(`with ${name} every image reads as missing`, async () => {
      const { docker } = fakeDocker(() => result);

      expect((await docker.images()).every((i) => !i.present)).toBe(true);
    });
  }
});

describe('download the images', () => {
  const MAVEN = 'Java (Maven)';
  const maven = PINNED_IMAGES.filter((i) => i.stack === MAVEN).map((i) => i.reference);
  const absent = new Set([maven[1]!, maven[2]!, maven[4]!, PINNED_IMAGES[1]!.reference]);
  const absentMaven = maven.filter((ref) => absent.has(ref));
  const inspect = (args: readonly string[]): ExecResult =>
    absent.has(args[args.length - 1] ?? '') ? exit(1, 'No such image') : ok('sha256:abc');

  it('pulls only the missing images of the stack asked for, one at a time, reports progress, logs each, and answers no error', async () => {
    const events: DockerPullProgress[] = [];
    const { docker, calls, lines } = fakeDocker((args) => (isInspect(args) ? inspect(args) : ok()));

    const result = await docker.pull(MAVEN, (p) => events.push(p));

    expect(result).toEqual({ error: null });
    expect(pulls(calls)).toEqual(absentMaven);
    expect(events.map((e) => [e.reference, e.index, e.total, e.state])).toEqual(
      absentMaven.flatMap((ref, i) => [
        [ref, i + 1, 3, 'pulling'],
        [ref, i + 1, 3, 'done'],
      ]),
    );
    expect(calls.filter((c) => c.args[0] === 'pull').every((c) => c.timeoutMs === 1_800_000)).toBe(
      true,
    );
    const log = lines.join('\n');
    expect(log).toContain('image download started by the owner');
    for (const ref of absentMaven) expect(log).toContain(ref);
  });

  it('never pulls every pinned image: a stack Scout does not pin is refused before any docker call', async () => {
    const { docker, calls } = fakeDocker(() => ok());

    const result = await docker.pull('', () => {});

    expect(result).toEqual({ error: 'Scout has no pinned images for .' });
    expect(calls).toEqual([]);
  });

  it('a pull that fails midway stops, answers a sentence naming the image, and the rest never start', async () => {
    const events: DockerPullProgress[] = [];
    const [first, second] = absentMaven;
    const { docker, calls, lines } = fakeDocker((args) => {
      if (isInspect(args)) return inspect(args);
      return args[1] === second
        ? exit(1, 'Pulling fs layer\nError response from daemon: manifest unknown\n')
        : ok();
    });

    const result = await docker.pull(MAVEN, (p) => events.push(p));

    expect(result.error).toBe(
      `Docker could not download ${second}: Error response from daemon: manifest unknown. The other 1 image was not downloaded.`,
    );
    expect(pulls(calls)).toEqual([first!, second!]);
    expect(events.at(-1)).toEqual({ reference: second!, index: 2, total: 3, state: 'failed' });
    expect(lines.join('\n')).not.toContain('manifest unknown');
  });

  const boundary: readonly [string, ExecResult, string][] = [
    ['the command missing', missing, `Docker could not download {ref}: Docker is not installed.`],
    [
      'a timeout',
      timedOut,
      'Docker could not download {ref}: the download took longer than 30 minutes.',
    ],
    [
      'a non-zero exit with no message',
      exit(125),
      'Docker could not download {ref}: docker pull exited with code 125.',
    ],
    [
      'Docker Hub answering 429 toomanyrequests',
      exit(
        1,
        'Error response from daemon: toomanyrequests: You have reached your unauthenticated pull rate limit.',
      ),
      'Docker Hub is limiting downloads; try again later.',
    ],
    [
      'a registry answering 429 Too Many Requests',
      exit(1, 'error pulling image configuration: 429 Too Many Requests'),
      'Docker Hub is limiting downloads; try again later.',
    ],
  ];
  for (const [name, failure, sentence] of boundary) {
    it(`with ${name} on the first pull, the answer says why and that the other images were not downloaded`, async () => {
      const [first] = absentMaven;
      const { docker, calls } = fakeDocker((args) => (isInspect(args) ? inspect(args) : failure));

      const result = await docker.pull(MAVEN, () => {});

      expect(result.error).toBe(
        `${sentence.replace('{ref}', first!)} The other 2 images were not downloaded.`,
      );
      expect(pulls(calls)).toEqual([first!]);
    });
  }

  it('a second download while one runs is refused with a sentence and pulls nothing more', async () => {
    let release: () => void = () => {};
    let entered: () => void = () => {};
    const gate = new Promise<void>((resolve) => (release = resolve));
    const pullEntered = new Promise<void>((resolve) => (entered = resolve));
    const { docker, calls } = fakeDocker(async (args) => {
      if (isInspect(args)) return inspect(args);
      entered();
      await gate;
      return ok();
    });

    const running = docker.pull(MAVEN, () => {});
    await pullEntered;
    const second = await docker.pull('Node', () => {});
    release();
    await running;

    expect(second).toEqual({ error: PULL_BUSY });
    expect(pulls(calls)).toEqual(absentMaven);
  });
});

describe('the work folder path, owned by main', () => {
  const rows: readonly [NodeJS.Platform, Record<string, string>, string][] = [
    ['linux', {}, '/home/owner/.local/share/Scout/sandbox'],
    ['linux', { XDG_DATA_HOME: '/data/xdg' }, '/data/xdg/Scout/sandbox'],
    ['linux', { XDG_DATA_HOME: 'relative/xdg' }, '/home/owner/.local/share/Scout/sandbox'],
    ['darwin', {}, '/home/owner/Library/Application Support/Scout/sandbox'],
    [
      'win32',
      { LOCALAPPDATA: 'C:\\Users\\owner\\AppData\\Local' },
      'C:\\Users\\owner\\AppData\\Local\\Scout\\sandbox',
    ],
  ];
  for (const [platform, env, expected] of rows) {
    it(`on ${platform} with ${JSON.stringify(env)} is ${expected}`, () => {
      expect(sandboxWorkFolder(platform, env, '/home/owner')).toBe(expected);
    });
  }
});

describe('clear the work folder', () => {
  let root: string;
  let folder: string;
  let outside: string;
  const now = Date.now();
  const old = new Date(now - RUN_IN_PROGRESS_MS - 60_000);

  beforeEach(async () => {
    root = await mkdtemp(path.join(tmpdir(), 'scout-docker-'));
    folder = path.join(root, 'sandbox');
    outside = path.join(root, 'outside');
    await mkdir(folder, { recursive: true });
    await mkdir(outside, { recursive: true });
    await writeFile(path.join(outside, 'keep.txt'), 'owner file');
  });

  afterEach(async () => {
    await rm(root, { recursive: true, force: true });
  });

  function docker(
    answer: (args: readonly string[]) => ExecResult | Promise<ExecResult> = () => ok(),
  ) {
    return fakeDocker(answer, { workFolder: folder, now: () => now });
  }

  async function oldRun(name: string): Promise<string> {
    const dir = path.join(folder, name);
    await mkdir(path.join(dir, 'src'), { recursive: true });
    await writeFile(path.join(dir, 'src', 'a.txt'), 'x'.repeat(100));
    await utimes(dir, old, old);
    return dir;
  }

  it('reports the path and the bytes it holds, and zero for a folder that does not exist yet', async () => {
    await writeFile(path.join(folder, 'run-1.log'), 'x'.repeat(250));
    await oldRun('run-2');

    expect(await docker().docker.workFolder()).toEqual({ path: folder, bytes: 350 });
    const absent = fakeDocker(() => ok(), { workFolder: path.join(root, 'none') });
    expect(await absent.docker.workFolder()).toEqual({ path: path.join(root, 'none'), bytes: 0 });
  });

  it("deletes the folder's contents, keeps the folder, and logs the clear", async () => {
    await writeFile(path.join(folder, 'run-1.log'), 'log');
    await oldRun('run-2');
    const { docker: d, lines } = docker();

    const result = await d.clear();

    expect(result).toEqual({ refused: null, folder: { path: folder, bytes: 0 } });
    expect((await stat(folder)).isDirectory()).toBe(true);
    expect(lines.join('\n')).toContain('work folder cleared by the owner');
  });

  it('never follows a symbolic link inside the folder: the files it points to stay', async () => {
    await symlink(outside, path.join(folder, 'link-to-outside'));
    const run = await oldRun('run-3');
    await symlink(outside, path.join(run, 'src', 'nested-link'));
    await symlink(path.join(outside, 'keep.txt'), path.join(folder, 'file-link'));
    await utimes(run, old, old);

    const result = await docker().docker.clear();

    expect(result.refused).toBeNull();
    expect(await readFile(path.join(outside, 'keep.txt'), 'utf8').catch(() => 'deleted')).toBe(
      'owner file',
    );
    expect(result.folder.bytes).toBe(0);
  });

  it('is refused while a run folder is younger than the run limit, and deletes nothing', async () => {
    await writeFile(path.join(folder, 'run-1.log'), 'log');
    const running = path.join(folder, 'run-now');
    await mkdir(running);
    const recent = new Date(now - 60_000);
    await utimes(running, recent, recent);

    const result = await docker().docker.clear();

    expect(result.refused).toBe(CLEAR_WHILE_RUNNING);
    expect(await readFile(path.join(folder, 'run-1.log'), 'utf8')).toBe('log');
    expect((await stat(running)).isDirectory()).toBe(true);
  });

  it('is refused while a download is in progress, and deletes nothing', async () => {
    await writeFile(path.join(folder, 'run-1.log'), 'log');
    let release: () => void = () => {};
    let entered: () => void = () => {};
    const gate = new Promise<void>((resolve) => (release = resolve));
    const pullEntered = new Promise<void>((resolve) => (entered = resolve));
    const { docker: d } = docker(async (args) => {
      if (isInspect(args)) return exit(1, 'No such image');
      entered();
      await gate;
      return ok();
    });

    const pulling = d.pull('Go', () => {});
    await pullEntered;
    const result = await d.clear();
    release();
    await pulling;

    expect(result.refused).toBe(CLEAR_WHILE_PULLING);
    expect(await readFile(path.join(folder, 'run-1.log'), 'utf8')).toBe('log');
  });

  it('is refused, deletes nothing and logs no clear when the work folder cannot be read', async () => {
    const unreadable = path.join(root, 'not-a-folder');
    await writeFile(unreadable, 'x'.repeat(42));
    const { docker: d, lines } = fakeDocker(() => ok(), { workFolder: unreadable, now: () => now });

    const result = await d.clear();

    expect(result).toEqual({ refused: CLEAR_UNREADABLE, folder: { path: unreadable, bytes: 42 } });
    expect(await readFile(unreadable, 'utf8')).toBe('x'.repeat(42));
    expect(lines.join('\n')).not.toContain('work folder cleared by the owner');
  });

  it('a work folder that does not exist yet clears with no refusal', async () => {
    const absent = path.join(root, 'none');
    const { docker: d } = fakeDocker(() => ok(), { workFolder: absent, now: () => now });

    expect(await d.clear()).toEqual({ refused: null, folder: { path: absent, bytes: 0 } });
  });
});

describe('main keeps the pinned list equal to the sandbox', () => {
  it("main's pinned list equals the SandboxImages constants in SandboxStacks.cs", async () => {
    const source = await readFile(
      path.resolve(import.meta.dir, '../../../../src/Scout.Sandbox/SandboxStacks.cs'),
      'utf8',
    );
    const block = /static class SandboxImages\s*\{([\s\S]*?)\n\}/.exec(source)?.[1] ?? '';
    const sandbox = [...block.matchAll(/public const string \w+ = "([^"]+)";/g)].map(
      (m) => m[1] ?? '',
    );

    expect(sandbox.length).toBeGreaterThan(0);
    expect([...PINNED_IMAGES.map((i) => i.reference)].sort()).toEqual([...sandbox].sort());
  });
});

describe('the docker channels', () => {
  it('send the download progress to the window that asked, and answer state, images, the folder and the clear', async () => {
    const handlers = new Map<string, (event: IpcMainInvokeEvent) => unknown>();
    const ipc = {
      handle: (channel: string, h: (event: IpcMainInvokeEvent) => unknown) =>
        void handlers.set(channel, h),
      removeHandler: (channel: string) => void handlers.delete(channel),
    } as unknown as Pick<IpcMain, 'handle' | 'removeHandler'>;
    const sent: [string, unknown][] = [];
    const sender = { isDestroyed: () => false, send: (c: string, p: unknown) => sent.push([c, p]) };
    const { docker } = fakeDocker((args) =>
      args[0] === 'version' ? ok(SERVER_JSON) : isInspect(args) ? exit(1) : ok(),
    );

    const dispose = registerDockerHandlers(ipc, docker);
    const event = { sender } as unknown as IpcMainInvokeEvent;
    const state = await handlers.get(CHANNEL.dockerState)!(event);
    const images = (await handlers.get(CHANNEL.dockerImages)!(event)) as { images: unknown[] };
    const pull = await (
      handlers.get(CHANNEL.dockerPull) as (e: IpcMainInvokeEvent, req: unknown) => unknown
    )(event, { stack: 'Java (Gradle)' });

    expect(state).toEqual({ state: 'running', version: '28.5.1' });
    expect(images.images).toHaveLength(PINNED_IMAGES.length);
    expect(pull).toEqual({ error: null });
    expect(sent.filter(([c]) => c === CHANNEL.dockerPullProgress)).toHaveLength(
      PINNED_IMAGES.filter((i) => i.stack === 'Java (Gradle)').length * 2,
    );
    expect([...handlers.keys()].sort()).toEqual(
      [
        CHANNEL.dockerState,
        CHANNEL.dockerImages,
        CHANNEL.dockerPull,
        CHANNEL.dockerWorkFolder,
        CHANNEL.dockerClear,
        CHANNEL.dockerRepositories,
        CHANNEL.dockerLastRun,
        CHANNEL.dockerPullImage,
      ].sort(),
    );
    dispose();
    expect(handlers.size).toBe(0);
  });
});

const NODE_RECORD: DockerRepository = {
  repository: 'octocat/hello',
  commit: 'abc1234def5678',
  stack: 'Node',
  image: 'node:20@sha256:8f69',
  runs: 'Node 20.20.2',
  declared: [{ source: '.nvmrc', version: 'Node 20' }],
  refusal: null,
  detectedAt: '2026-11-07T10:00:00+00:00',
};

const REFUSED_RECORD: DockerRepository = {
  repository: 'octocat/legacy',
  commit: 'def5678abc1234',
  stack: 'Node',
  image: null,
  runs: null,
  declared: [],
  refusal: 'This project asks for Node 16; Scout runs Node 18, 20, 22, 24 and 26.',
  detectedAt: '2026-11-07T11:00:00+00:00',
};

const PASSED_RUN: DockerRun = {
  repository: 'octocat/hello',
  pullRequest: 42,
  stack: 'Node',
  image: 'node:20@sha256:8f69',
  result: 'passed',
  reason: null,
  exitCode: 0,
  durationSeconds: 12.5,
  finishedAt: '2026-11-07T10:05:00+00:00',
};

describe('the repositories folder, beside the work folder', () => {
  const rows: readonly [NodeJS.Platform, Record<string, string>, string][] = [
    ['linux', {}, '/home/owner/.local/share/Scout/github-snapshots'],
    ['linux', { XDG_DATA_HOME: '/data/xdg' }, '/data/xdg/Scout/github-snapshots'],
    ['darwin', {}, '/home/owner/Library/Application Support/Scout/github-snapshots'],
    [
      'win32',
      { LOCALAPPDATA: 'C:\\Users\\owner\\AppData\\Local' },
      'C:\\Users\\owner\\AppData\\Local\\Scout\\github-snapshots',
    ],
  ];
  for (const [platform, env, expected] of rows) {
    it(`on ${platform} with ${JSON.stringify(env)} is ${expected}`, () => {
      expect(githubSnapshotsFolder(platform, env, '/home/owner')).toBe(expected);
    });
  }
});

describe("each repository's stack and the last run, read from the worker's records", () => {
  let root: string;
  let snapshots: string;
  let work: string;

  beforeEach(async () => {
    root = await mkdtemp(path.join(tmpdir(), 'scout-docker-records-'));
    snapshots = path.join(root, 'github-snapshots');
    work = path.join(root, 'sandbox');
    await mkdir(snapshots, { recursive: true });
    await mkdir(work, { recursive: true });
  });

  afterEach(async () => {
    await rm(root, { recursive: true, force: true });
  });

  const records = () => fakeDocker(() => ok(), { snapshotsFolder: snapshots, workFolder: work });

  it('lists every repository record, newest first, with the stack, image, versions and source, or the refusal', async () => {
    await writeFile(path.join(snapshots, 'octocat__hello.stack.json'), JSON.stringify(NODE_RECORD));
    await writeFile(
      path.join(snapshots, 'octocat__legacy.stack.json'),
      JSON.stringify(REFUSED_RECORD),
    );
    await mkdir(path.join(snapshots, 'octocat__hello', 'abc1234def5678'), { recursive: true });
    await writeFile(path.join(snapshots, 'octocat__hello', 'abc1234def5678.json'), '{}');

    const { docker } = records();

    expect(await docker.repositories()).toEqual([REFUSED_RECORD, NODE_RECORD]);
  });

  it('no folder or no records reads as empty', async () => {
    const { docker } = fakeDocker(() => ok(), {
      snapshotsFolder: path.join(root, 'absent'),
      workFolder: path.join(root, 'absent-too'),
    });

    expect(await docker.repositories()).toEqual([]);
    expect(await docker.lastRun()).toBeNull();
    expect(await records().docker.repositories()).toEqual([]);
    expect(await records().docker.lastRun()).toBeNull();
  });

  const malformedRepositories: readonly [string, string][] = [
    ['not JSON', '{"repository": '],
    ['an array', JSON.stringify([NODE_RECORD])],
    ['no repository', JSON.stringify({ ...NODE_RECORD, repository: undefined })],
    ['both an image and a refusal', JSON.stringify({ ...NODE_RECORD, refusal: 'no' })],
    ['neither an image nor a refusal', JSON.stringify({ ...NODE_RECORD, image: null })],
    [
      'a declared version without its file',
      JSON.stringify({ ...NODE_RECORD, declared: [{ version: 'Node 20' }] }),
    ],
    ['a time that is not one', JSON.stringify({ ...NODE_RECORD, detectedAt: 'yesterday' })],
  ];
  for (const [name, text] of malformedRepositories) {
    it(`skips a repository record with ${name}, logs it, and keeps the others`, async () => {
      await writeFile(
        path.join(snapshots, 'octocat__hello.stack.json'),
        JSON.stringify(NODE_RECORD),
      );
      await writeFile(path.join(snapshots, 'octocat__broken.stack.json'), text);
      const { docker, lines } = records();

      expect(await docker.repositories()).toEqual([NODE_RECORD]);
      expect(lines.filter((l) => l.includes('skipped a record that does not parse'))).toHaveLength(
        1,
      );
      expect(lines.join('\n')).toContain('octocat__broken.stack.json');
    });
  }

  it('drops any field the record carries beyond the ones the card shows', async () => {
    await writeFile(
      path.join(snapshots, 'octocat__hello.stack.json'),
      JSON.stringify({ ...NODE_RECORD, output: 'never shown' }),
    );

    expect(await records().docker.repositories()).toEqual([NODE_RECORD]);
  });

  it('answers the newest run by its finish time, whatever the file names', async () => {
    const older = {
      ...PASSED_RUN,
      result: 'failed',
      exitCode: 1,
      finishedAt: '2026-11-07T09:00:00+00:00',
    };
    await writeFile(path.join(work, 'ffff.run.json'), JSON.stringify(older));
    await writeFile(path.join(work, '0000.run.json'), JSON.stringify(PASSED_RUN));
    await writeFile(path.join(work, '0000.log'), 'npm test');

    expect(await records().docker.lastRun()).toEqual(PASSED_RUN);
  });

  const runKinds: readonly [string, DockerRun][] = [
    ['passed', PASSED_RUN],
    ['failed', { ...PASSED_RUN, result: 'failed', exitCode: 1 }],
    [
      'did not run, with its reason',
      {
        ...PASSED_RUN,
        result: 'notRun',
        exitCode: null,
        reason: 'The tests ran past the 20-minute limit.',
      },
    ],
    [
      'with no repository or pull request named',
      { ...PASSED_RUN, repository: null, pullRequest: null },
    ],
  ];
  for (const [name, run] of runKinds) {
    it(`reads a run that ${name}`, async () => {
      await writeFile(path.join(work, 'a.run.json'), JSON.stringify(run));

      expect(await records().docker.lastRun()).toEqual(run);
    });
  }

  const malformedRuns: readonly [string, string][] = [
    ['not JSON', 'nope'],
    ['an unknown result', JSON.stringify({ ...PASSED_RUN, result: 'skipped' })],
    [
      'a pull request that is not a whole number',
      JSON.stringify({ ...PASSED_RUN, pullRequest: '42' }),
    ],
    ['no duration', JSON.stringify({ ...PASSED_RUN, durationSeconds: undefined })],
    ['no finish time', JSON.stringify({ ...PASSED_RUN, finishedAt: undefined })],
  ];
  for (const [name, text] of malformedRuns) {
    it(`skips a run record with ${name}, even when it would be the newest`, async () => {
      await writeFile(path.join(work, 'good.run.json'), JSON.stringify(PASSED_RUN));
      await writeFile(path.join(work, 'bad.run.json'), text.replace('10:05', '23:59'));
      const { docker, lines } = records();

      expect(await docker.lastRun()).toEqual(PASSED_RUN);
      expect(lines.filter((l) => l.includes('skipped a record that does not parse'))).toHaveLength(
        1,
      );
    });
  }

  it('the channels answer the repositories and the last run', async () => {
    await writeFile(path.join(snapshots, 'octocat__hello.stack.json'), JSON.stringify(NODE_RECORD));
    await writeFile(path.join(work, 'a.run.json'), JSON.stringify(PASSED_RUN));
    const handlers = new Map<string, () => unknown>();
    const ipc = {
      handle: (channel: string, h: () => unknown) => void handlers.set(channel, h),
      removeHandler: (channel: string) => void handlers.delete(channel),
    } as unknown as Pick<IpcMain, 'handle' | 'removeHandler'>;

    registerDockerHandlers(ipc, records().docker);

    expect(await handlers.get(CHANNEL.dockerRepositories)!()).toEqual({
      repositories: [NODE_RECORD],
    });
    expect(await handlers.get(CHANNEL.dockerLastRun)!()).toEqual({ run: PASSED_RUN });
  });
});

describe("download one repository's image", () => {
  const MAVEN = 'Java (Maven)';
  const maven = PINNED_IMAGES.filter((i) => i.stack === MAVEN).map((i) => i.reference);
  const [wanted, sibling] = [maven[1]!, maven[2]!];
  const inspect = (args: readonly string[]): ExecResult =>
    [wanted, sibling].includes(args[args.length - 1] ?? '')
      ? exit(1, 'No such image')
      : ok('sha256:abc');

  it('pulls only that pinned image, never the rest of its stack, with its progress', async () => {
    const events: DockerPullProgress[] = [];
    const { docker, calls, lines } = fakeDocker((args) => (isInspect(args) ? inspect(args) : ok()));

    const result = await docker.pullImage(wanted, (p) => events.push(p));

    expect(result).toEqual({ error: null });
    expect(pulls(calls)).toEqual([wanted]);
    expect(events).toEqual([
      { reference: wanted, index: 1, total: 1, state: 'pulling' },
      { reference: wanted, index: 1, total: 1, state: 'done' },
    ]);
    expect(lines.join('\n')).toContain('image download started by the owner');
  });

  it('an image Docker already holds is not pulled', async () => {
    const { docker, calls } = fakeDocker((args) => (isInspect(args) ? ok('sha256:abc') : ok()));

    expect(await docker.pullImage(wanted, () => {})).toEqual({ error: null });
    expect(pulls(calls)).toEqual([]);
  });

  const unpinned: readonly [string, string][] = [
    ['a tag Scout does not pin', 'node:16'],
    ['a pinned tag with another digest', `${wanted.split('@')[0]!}@sha256:${'0'.repeat(64)}`],
    ['an empty reference', ''],
  ];
  for (const [name, reference] of unpinned) {
    it(`${name} is refused before any docker call`, async () => {
      const { docker, calls } = fakeDocker(() => ok());

      const result = await docker.pullImage(reference, () => {});

      expect(result).toEqual({ error: `${reference} is not an image Scout pins.` });
      expect(calls).toEqual([]);
    });
  }

  const failures: readonly [string, ExecResult, string][] = [
    [
      'Docker Hub answering 429 toomanyrequests',
      exit(
        1,
        'Error response from daemon: toomanyrequests: You have reached your unauthenticated pull rate limit.',
      ),
      'Docker Hub is limiting downloads; try again later.',
    ],
    [
      'a pull that fails',
      exit(1, 'Pulling fs layer\nError response from daemon: manifest unknown\n'),
      `Docker could not download ${wanted}: Error response from daemon: manifest unknown.`,
    ],
    [
      'the command missing',
      missing,
      `Docker could not download ${wanted}: Docker is not installed.`,
    ],
  ];
  for (const [name, failure, sentence] of failures) {
    it(`with ${name}, the answer says why and the progress ends failed`, async () => {
      const events: DockerPullProgress[] = [];
      const { docker } = fakeDocker((args) => (isInspect(args) ? inspect(args) : failure));

      const result = await docker.pullImage(wanted, (p) => events.push(p));

      expect(result).toEqual({ error: sentence });
      expect(events.at(-1)).toEqual({ reference: wanted, index: 1, total: 1, state: 'failed' });
    });
  }

  it('is refused while another download runs, and pulls nothing more', async () => {
    let release: () => void = () => {};
    let entered: () => void = () => {};
    const gate = new Promise<void>((resolve) => (release = resolve));
    const pullEntered = new Promise<void>((resolve) => (entered = resolve));
    const { docker, calls } = fakeDocker(async (args) => {
      if (isInspect(args)) return inspect(args);
      entered();
      await gate;
      return ok();
    });

    const running = docker.pull(MAVEN, () => {});
    await pullEntered;
    const second = await docker.pullImage(wanted, () => {});
    release();
    await running;

    expect(second).toEqual({ error: PULL_BUSY });
    expect(pulls(calls)).toEqual([wanted, sibling]);
  });

  it('the channel passes only the reference and sends the progress to the window that asked', async () => {
    const handlers = new Map<string, (event: IpcMainInvokeEvent, req: unknown) => unknown>();
    const ipc = {
      handle: (channel: string, h: (event: IpcMainInvokeEvent, req: unknown) => unknown) =>
        void handlers.set(channel, h),
      removeHandler: (channel: string) => void handlers.delete(channel),
    } as unknown as Pick<IpcMain, 'handle' | 'removeHandler'>;
    const sent: [string, unknown][] = [];
    const sender = { isDestroyed: () => false, send: (c: string, p: unknown) => sent.push([c, p]) };
    const { docker, calls } = fakeDocker((args) => (isInspect(args) ? inspect(args) : ok()));
    registerDockerHandlers(ipc, docker);
    const event = { sender } as unknown as IpcMainInvokeEvent;

    const answer = await handlers.get(CHANNEL.dockerPullImage)!(event, {
      reference: wanted,
      stack: MAVEN,
    });
    const refused = await handlers.get(CHANNEL.dockerPullImage)!(event, { reference: 42 });

    expect(answer).toEqual({ error: null });
    expect(refused).toEqual({ error: ' is not an image Scout pins.' });
    expect(pulls(calls)).toEqual([wanted]);
    expect(sent.map(([c]) => c)).toEqual([CHANNEL.dockerPullProgress, CHANNEL.dockerPullProgress]);
  });
});
