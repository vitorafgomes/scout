import type { Dirent } from 'node:fs';
import { lstat, readFile, readdir, rm } from 'node:fs/promises';
import path from 'node:path';
import type {
  DockerClearResponse,
  DockerImage,
  DockerPullProgress,
  DockerPullResponse,
  DockerRepository,
  DockerRun,
  DockerState,
  DockerWorkFolder,
} from '@scout/shared';
import type { ExecResult } from '../detect/types.js';
import type { Logger } from '../util/logger.js';

/** The sandbox's pinned images, grouped by stack; a test keeps this list equal to `SandboxImages` in `SandboxStacks.cs`. */
export const PINNED_IMAGES: readonly { readonly stack: string; readonly reference: string }[] =
  Object.freeze([
    {
      stack: '.NET',
      reference:
        'mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e',
    },
    {
      stack: 'Node',
      reference: 'node:22@sha256:7702d8aa4d5910871692af4480eb3ba86f23047a6417a44d857d06c95d060d3f',
    },
    {
      stack: 'Bun',
      reference:
        'oven/bun:1@sha256:1d22548f177f4ff38e0c15baa3c2c46c6c2060ba2d1bc573bb4720b407548f1d',
    },
    {
      stack: 'Python',
      reference:
        'python:3.13@sha256:ecbe213fb818d557fd19db4078244339e2e2e52874f9c81052d3094ea114d797',
    },
    {
      stack: 'Go',
      reference:
        'golang:1.25@sha256:699337d620559a59b4a2bb298ad59611e535d2ee755a34cf2d2a98f37578dc80',
    },
    {
      stack: 'Java (Maven)',
      reference:
        'maven:3-eclipse-temurin-8@sha256:c5f4e3545103d845324ab1c541b44bcb931ac28f205835ea1ce5cd9ccec1b2dd',
    },
    {
      stack: 'Java (Maven)',
      reference:
        'maven:3-eclipse-temurin-11@sha256:433ab11f5f633557a7e5bf7a18c27be315abb44d2e466898a30bd15ee44f7243',
    },
    {
      stack: 'Java (Maven)',
      reference:
        'maven:3-eclipse-temurin-17@sha256:bec7efd9938efefcf02de53581a19f6816fb6066169a8ea45f52763222ffeb48',
    },
    {
      stack: 'Java (Maven)',
      reference:
        'maven:3-eclipse-temurin-21@sha256:9673eef9ed503146591667ffb92eaaa22f1b8899ed60d9df0df4da3f3a1214a6',
    },
    {
      stack: 'Java (Maven)',
      reference:
        'maven:3-eclipse-temurin-25@sha256:047a82fc82c613266cfb1b66fd920be9c8ba51fd67edc68245e4542768f45e36',
    },
    {
      stack: 'Java (Gradle)',
      reference:
        'gradle:7-jdk8@sha256:ec581274b197d53b665373854ed1955a5d186b89b3a440bdac5cfad13532606a',
    },
    {
      stack: 'Java (Gradle)',
      reference:
        'gradle:7-jdk11@sha256:8a25d9da40e2ce1ae24d3ef93bf1c1cb00814c8d9d5611763b20e812b59c85fa',
    },
    {
      stack: 'Java (Gradle)',
      reference:
        'gradle:7-jdk17@sha256:4a69879ab8c693f03bfed585209f221e3d2be20a7cab1e39be7086e89be50ad9',
    },
    {
      stack: 'Java (Gradle)',
      reference:
        'gradle:8-jdk8@sha256:c58ba7d35df44b76998f4fa661331cefde0825b61344da4f2c6b7d216c56ff95',
    },
    {
      stack: 'Java (Gradle)',
      reference:
        'gradle:8-jdk11@sha256:bdd652d6a71711400950d214a3eaf701ff42997e564933265633dd81af000ba7',
    },
    {
      stack: 'Java (Gradle)',
      reference:
        'gradle:8-jdk17@sha256:2f977f0a5b95ade2eabcfcf4840ff4b29fc176c61ee56fd184df038f64b18f9e',
    },
    {
      stack: 'Java (Gradle)',
      reference:
        'gradle:8-jdk21@sha256:930141cf2b9d472f3fd024b2e711856bede114ce116e49fcdfb64427b875982d',
    },
    {
      stack: 'Java (Gradle)',
      reference:
        'gradle:9-jdk17@sha256:55af4a989cc9188c19668b32e52bbd2a0c801da3b849a6e8f5a25e2134845bf5',
    },
    {
      stack: 'Java (Gradle)',
      reference:
        'gradle:9-jdk21@sha256:85f8f572045bfb31874d66d7d8a2bd0c17d8cb9ad34b6a4d02f5d552b4b92c04',
    },
    {
      stack: 'Java (Gradle)',
      reference:
        'gradle:9-jdk25@sha256:e46e15353757ef9c9be0197a243653e88d25ebcd4c3c841057cf839c11fcf898',
    },
    {
      stack: '.NET',
      reference:
        'mcr.microsoft.com/dotnet/sdk:8.0@sha256:341aa6e8bbaceb1194732328ca27c15bfa9c531687db83a66af3c0c1833f5534',
    },
    {
      stack: '.NET',
      reference:
        'mcr.microsoft.com/dotnet/sdk:9.0@sha256:07ed28d9e7279248fcea27334880f6c415b42c631c39b8bc5b3caf13ec6fdaa2',
    },
    {
      stack: 'Node',
      reference: 'node:18@sha256:c6ae79e38498325db67193d391e6ec1d224d96c693a8a4d943498556716d3783',
    },
    {
      stack: 'Node',
      reference: 'node:20@sha256:8f693eaa7e0a8e71560c9a82b55fd54c2ae920a2ba5d2cde28bac7d1c01c9ba5',
    },
    {
      stack: 'Node',
      reference: 'node:24@sha256:8fa96264f302f58bbb13aa7339c541547b56975766ce956c875da149452a3e40',
    },
    {
      stack: 'Node',
      reference: 'node:26@sha256:438603bbb5cda404b9416f0b5c8026f61501ab911760d861ec28c36b5d675725',
    },
    {
      stack: 'Python',
      reference:
        'python:3.10@sha256:c10791e6f69086d5216f7b5d3b839929c454e24305867ed9bc5152f0355b5423',
    },
    {
      stack: 'Python',
      reference:
        'python:3.11@sha256:731670a425deabbc48d39f0ee3115cfa665b2a4002c19939ad3e88b2832b4549',
    },
    {
      stack: 'Python',
      reference:
        'python:3.12@sha256:cad37dd20c7cac861af9003af8796d03ea7a998ec1e3a664429740fc9d7d2691',
    },
    {
      stack: 'Python',
      reference:
        'python:3.14@sha256:d18055009f327b22993de14458aa5a6bac032cdf459ea2a2e9bbdc3d3d49e4fb',
    },
    {
      stack: 'Go',
      reference:
        'golang:1.23@sha256:60deed95d3888cc5e4d9ff8a10c54e5edc008c6ae3fba6187be6fb592e19e8c0',
    },
    {
      stack: 'Go',
      reference:
        'golang:1.24@sha256:d2d2bc1c84f7e60d7d2438a3836ae7d0c847f4888464e7ec9ba3a1339a1ee804',
    },
    {
      stack: 'Go',
      reference:
        'golang:1.26@sha256:cfa27959837144bf4649802ba38e59daffd0dd2a7994aa83daaf113285b0f551',
    },
  ]);

export const STATE_TIMEOUT_MS = 5_000;
export const INSPECT_TIMEOUT_MS = 10_000;
export const PULL_TIMEOUT_MS = 30 * 60_000;
/** A run's folder younger than this may belong to a run in progress: the sandbox's 20-minute limit and a minute more. */
export const RUN_IN_PROGRESS_MS = 21 * 60_000;

export const PULL_BUSY = 'Scout is already downloading the images.';
export const PULL_RATE_LIMITED = 'Docker Hub is limiting downloads; try again later.';
export const CLEAR_WHILE_PULLING =
  'Scout is downloading images; clear the work folder after the download ends.';
export const CLEAR_WHILE_RUNNING =
  'A test run is in progress in the work folder; clear it after the run ends.';
export const CLEAR_UNREADABLE = 'Scout could not read the work folder, so nothing was deleted.';

/**
 * The work folder .NET's sandbox defaults to, `LocalApplicationData/Scout/sandbox`, for each OS: `XDG_DATA_HOME` or
 * `~/.local/share` on Linux, `~/Library/Application Support` on macOS, `%LOCALAPPDATA%` on Windows.
 */
export function sandboxWorkFolder(
  platform: NodeJS.Platform,
  env: Readonly<Record<string, string | undefined>>,
  homedir: string,
): string {
  if (platform === 'win32') {
    const local = env['LOCALAPPDATA'];
    return path.win32.join(
      local !== undefined && local !== '' ? local : path.win32.join(homedir, 'AppData', 'Local'),
      'Scout',
      'sandbox',
    );
  }
  if (platform === 'darwin') {
    return path.posix.join(homedir, 'Library', 'Application Support', 'Scout', 'sandbox');
  }
  const xdg = env['XDG_DATA_HOME'];
  const data =
    xdg !== undefined && path.posix.isAbsolute(xdg)
      ? xdg
      : path.posix.join(homedir, '.local', 'share');
  return path.posix.join(data, 'Scout', 'sandbox');
}

/** The worker's repository snapshots folder, `LocalApplicationData/Scout/github-snapshots`, beside the work folder. */
export function githubSnapshotsFolder(
  platform: NodeJS.Platform,
  env: Readonly<Record<string, string | undefined>>,
  homedir: string,
): string {
  const p = platform === 'win32' ? path.win32 : path.posix;
  return p.join(p.dirname(sandboxWorkFolder(platform, env, homedir)), 'github-snapshots');
}

export interface DockerDeps {
  exec(cmd: string, args: readonly string[], timeoutMs: number): Promise<ExecResult>;
  readonly dockerPath: string;
  readonly workFolder: string;
  /** Where the worker keeps each repository's `<owner>__<repo>.stack.json` beside the snapshots. */
  readonly snapshotsFolder: string;
  now(): number;
  readonly logger: Logger;
}

/** What main does with Docker: reads its state and images, pulls the pinned images, and sizes and clears the work folder. */
export interface Docker {
  state(): Promise<DockerState>;
  images(): Promise<readonly DockerImage[]>;
  /** Pulls the missing images of one stack, one at a time; never every pinned image at once. */
  pull(
    stack: string,
    onProgress: (progress: DockerPullProgress) => void,
  ): Promise<DockerPullResponse>;
  /** Pulls one pinned image when Docker does not hold it; a reference that is not pinned is refused before any call. */
  pullImage(
    reference: string,
    onProgress: (progress: DockerPullProgress) => void,
  ): Promise<DockerPullResponse>;
  workFolder(): Promise<DockerWorkFolder>;
  clear(): Promise<DockerClearResponse>;
  /** The repositories whose stack the worker recorded, newest first; a record that does not parse is skipped. */
  repositories(): Promise<readonly DockerRepository[]>;
  /** The newest finished run's record in the work folder, or null; a running record or one that does not parse is skipped. */
  lastRun(): Promise<DockerRun | null>;
  /**
   * The command and start of the newest running record of this pull request, its repository compared without case, or
   * null; a record that does not parse is skipped without a log line, because the bell asks every few seconds. A record
   * that started longer ago than the sandbox's limit allows is one a stopped worker left behind, and is skipped.
   */
  running(repository: string, pullRequest: number): Promise<DockerRunning | null>;
}

/** A run the sandbox has recorded as running: its test command and when it started. */
export interface DockerRunning {
  readonly command: string;
  readonly startedAt: string;
}

/** A run's record as the sandbox writes it: finished, or running with no end yet. */
export type RunRecord =
  | DockerRun
  | (Omit<DockerRun, 'result' | 'finishedAt' | 'startedAt' | 'command'> & {
      readonly result: 'running';
      readonly command: string;
      readonly startedAt: string;
      readonly finishedAt: null;
    });

export function createDocker(deps: DockerDeps): Docker {
  const { logger } = deps;
  let pulling = false;

  const run = (args: readonly string[], timeoutMs: number): Promise<ExecResult> =>
    deps.exec(deps.dockerPath, args, timeoutMs);

  async function state(): Promise<DockerState> {
    const result = await run(['version', '--format', '{{json .}}'], STATE_TIMEOUT_MS);
    if (result.failure === 'missing') return { state: 'notInstalled' };
    if (result.failure === 'timeout') {
      logger.info('docker: not running', { reason: 'docker version timed out' });
      return { state: 'notRunning' };
    }
    if (result.exitCode !== 0) {
      logger.info('docker: not running', { reason: `docker version exited ${result.exitCode}` });
      return { state: 'notRunning' };
    }
    const version = serverVersion(result.stdout);
    if (version === null) {
      logger.info('docker: not running', { reason: 'no server in docker version' });
      return { state: 'notRunning' };
    }
    return { state: 'running', version };
  }

  async function images(): Promise<readonly DockerImage[]> {
    const found: DockerImage[] = [];
    for (const image of PINNED_IMAGES) {
      const result = await run(
        ['image', 'inspect', '--format', '{{.Id}}', image.reference],
        INSPECT_TIMEOUT_MS,
      );
      found.push({ ...image, present: result.failure === undefined && result.exitCode === 0 });
    }
    return found;
  }

  async function pull(
    stack: string,
    onProgress: (progress: DockerPullProgress) => void,
  ): Promise<DockerPullResponse> {
    if (!PINNED_IMAGES.some((i) => i.stack === stack)) {
      return { error: `Scout has no pinned images for ${stack}.` };
    }
    return download({ stack }, (i) => i.stack === stack, onProgress);
  }

  async function pullImage(
    reference: string,
    onProgress: (progress: DockerPullProgress) => void,
  ): Promise<DockerPullResponse> {
    if (!PINNED_IMAGES.some((i) => i.reference === reference)) {
      return { error: `${reference.slice(0, 200)} is not an image Scout pins.` };
    }
    return download({ reference }, (i) => i.reference === reference, onProgress);
  }

  async function download(
    asked: Readonly<Record<string, string>>,
    wanted: (image: DockerImage) => boolean,
    onProgress: (progress: DockerPullProgress) => void,
  ): Promise<DockerPullResponse> {
    if (pulling) return { error: PULL_BUSY };
    pulling = true;
    try {
      const missing = (await images()).filter((i) => wanted(i) && !i.present);
      logger.info('docker: image download started by the owner', {
        ...asked,
        count: missing.length,
      });
      for (const [i, image] of missing.entries()) {
        const step = { reference: image.reference, index: i + 1, total: missing.length };
        onProgress({ ...step, state: 'pulling' });
        const result = await run(['pull', image.reference], PULL_TIMEOUT_MS);
        if (result.failure !== undefined || result.exitCode !== 0) {
          logger.warn('docker: image download failed', {
            reference: image.reference,
            exitCode: result.exitCode,
            failure: result.failure ?? null,
          });
          onProgress({ ...step, state: 'failed' });
          const rest = missing.length - i - 1;
          return {
            error:
              (rateLimited(result)
                ? PULL_RATE_LIMITED
                : `Docker could not download ${image.reference}: ${pullReason(result)}.`) +
              (rest > 0
                ? ` The other ${rest} ${rest === 1 ? 'image was' : 'images were'} not downloaded.`
                : ''),
          };
        }
        logger.info('docker: image downloaded', { reference: image.reference });
        onProgress({ ...step, state: 'done' });
      }
      return { error: null };
    } finally {
      pulling = false;
    }
  }

  async function workFolder(): Promise<DockerWorkFolder> {
    const names = await readdir(deps.workFolder).catch((): string[] => []);
    let runs = 0;
    for (const name of names.filter((n) => n.endsWith('.run.json'))) {
      const record = await readFile(path.join(deps.workFolder, name), 'utf8')
        .then((text): unknown => JSON.parse(text))
        .catch(() => null);
      if (fields(record)?.['result'] !== 'running') runs += 1;
    }
    return { path: deps.workFolder, bytes: await sizeOf(deps.workFolder), runs };
  }

  async function clear(): Promise<DockerClearResponse> {
    if (pulling) return { refused: CLEAR_WHILE_PULLING, folder: await workFolder() };
    let entries: Dirent[] = [];
    try {
      entries = await readdir(deps.workFolder, { withFileTypes: true });
    } catch (err: unknown) {
      const code = (err as NodeJS.ErrnoException).code ?? null;
      if (code !== 'ENOENT') {
        logger.warn('docker: the work folder could not be read; nothing was cleared', {
          path: deps.workFolder,
          code,
        });
        return { refused: CLEAR_UNREADABLE, folder: await workFolder() };
      }
    }
    for (const entry of entries) {
      if (!entry.isDirectory()) continue;
      const info = await lstat(path.join(deps.workFolder, entry.name));
      if (deps.now() - info.mtimeMs < RUN_IN_PROGRESS_MS) {
        return { refused: CLEAR_WHILE_RUNNING, folder: await workFolder() };
      }
    }
    const before = await sizeOf(deps.workFolder);
    for (const entry of entries) {
      await rm(path.join(deps.workFolder, entry.name), { recursive: true, force: true });
    }
    logger.info('docker: work folder cleared by the owner', {
      path: deps.workFolder,
      bytes: before,
    });
    return { refused: null, folder: await workFolder() };
  }

  async function records<T>(
    folder: string,
    suffix: string,
    read: (value: unknown) => T | null,
    quiet = false,
  ): Promise<T[]> {
    const names = await readdir(folder).catch(() => []);
    const found: T[] = [];
    for (const name of names.filter((n) => n.endsWith(suffix)).sort()) {
      const record = read(
        await readFile(path.join(folder, name), 'utf8')
          .then((text): unknown => JSON.parse(text))
          .catch(() => null),
      );
      if (record !== null) found.push(record);
      else if (!quiet) logger.warn('docker: skipped a record that does not parse', { name });
    }
    return found;
  }

  async function repositories(): Promise<readonly DockerRepository[]> {
    const found = await records(deps.snapshotsFolder, '.stack.json', toRepository);
    return found.sort((a, b) => Date.parse(b.detectedAt) - Date.parse(a.detectedAt));
  }

  async function lastRun(): Promise<DockerRun | null> {
    const found = await records(deps.workFolder, '.run.json', toRun);
    return found.reduce<DockerRun | null>(
      (newest, run) =>
        run.result !== 'running' &&
        (newest === null || Date.parse(run.finishedAt) > Date.parse(newest.finishedAt))
          ? run
          : newest,
      null,
    );
  }

  async function running(repository: string, pullRequest: number): Promise<DockerRunning | null> {
    const found = await records(deps.workFolder, '.run.json', toRun, true);
    let newest: DockerRunning | null = null;
    for (const run of found) {
      if (
        run.result === 'running' &&
        deps.now() - Date.parse(run.startedAt) < RUN_IN_PROGRESS_MS &&
        run.pullRequest === pullRequest &&
        run.repository?.toLowerCase() === repository.toLowerCase() &&
        (newest === null || Date.parse(run.startedAt) > Date.parse(newest.startedAt))
      ) {
        newest = { command: run.command, startedAt: run.startedAt };
      }
    }
    return newest;
  }

  return { state, images, pull, pullImage, workFolder, clear, repositories, lastRun, running };
}

type Fields = Readonly<Record<string, unknown>>;

function fields(value: unknown): Fields | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Fields)
    : null;
}

function text(value: unknown): value is string {
  return typeof value === 'string' && value !== '';
}

function textOrNull(value: unknown): value is string | null {
  return value === null || text(value);
}

function time(value: unknown): value is string {
  return text(value) && !Number.isNaN(Date.parse(value));
}

function wholeOrNull(value: unknown): value is number | null {
  return value === null || Number.isInteger(value);
}

/** A repository's stack record as the worker writes it: an image or a refusal, never both and never neither. */
export function toRepository(value: unknown): DockerRepository | null {
  const r = fields(value);
  if (r === null) return null;
  const declared = Array.isArray(r['declared']) ? r['declared'].map(fields) : null;
  if (
    !text(r['repository']) ||
    !text(r['commit']) ||
    !textOrNull(r['stack']) ||
    !textOrNull(r['image']) ||
    !textOrNull(r['runs']) ||
    !textOrNull(r['refusal']) ||
    (r['image'] === null) === (r['refusal'] === null) ||
    !time(r['detectedAt']) ||
    declared === null ||
    !declared.every((d) => d !== null && text(d['source']) && text(d['version']))
  ) {
    return null;
  }
  return {
    repository: r['repository'],
    commit: r['commit'],
    stack: r['stack'],
    image: r['image'],
    runs: r['runs'],
    declared: (declared as Fields[]).map((d) => ({
      source: d['source'] as string,
      version: d['version'] as string,
    })),
    refusal: r['refusal'],
    detectedAt: r['detectedAt'],
  };
}

/**
 * A run's record as the sandbox writes it beside the run's log: running, with its command and start and no end, or
 * finished; a record written before `command` or `startedAt` existed reads that field as null.
 */
export function toRun(value: unknown): RunRecord | null {
  const r = fields(value);
  if (r === null) return null;
  const result = r['result'];
  const command = r['command'] ?? null;
  const startedAt = r['startedAt'] ?? null;
  const finishedAt = r['finishedAt'];
  if (
    !textOrNull(r['repository']) ||
    !wholeOrNull(r['pullRequest']) ||
    !text(r['stack']) ||
    !text(r['image']) ||
    !textOrNull(command) ||
    (startedAt !== null && !time(startedAt)) ||
    !textOrNull(r['reason']) ||
    !wholeOrNull(r['exitCode']) ||
    typeof r['durationSeconds'] !== 'number' ||
    !Number.isFinite(r['durationSeconds'])
  ) {
    return null;
  }
  const common = {
    repository: r['repository'],
    pullRequest: r['pullRequest'],
    stack: r['stack'],
    image: r['image'],
    reason: r['reason'],
    exitCode: r['exitCode'],
    durationSeconds: r['durationSeconds'],
  };
  if (result === 'running') {
    return command !== null && startedAt !== null && finishedAt === null
      ? { ...common, command, result, startedAt, finishedAt }
      : null;
  }
  if ((result !== 'passed' && result !== 'failed' && result !== 'notRun') || !time(finishedAt)) {
    return null;
  }
  return { ...common, command, result, startedAt, finishedAt };
}

function serverVersion(stdout: string): string | null {
  try {
    const parsed = JSON.parse(stdout) as { Server?: { Version?: unknown } | null };
    const version = parsed.Server?.Version;
    return typeof version === 'string' && version !== '' ? version : null;
  } catch {
    return null;
  }
}

function rateLimited(result: ExecResult): boolean {
  return /toomanyrequests|429 Too Many Requests/i.test(result.stderr);
}

function pullReason(result: ExecResult): string {
  if (result.failure === 'missing') return 'Docker is not installed';
  if (result.failure === 'timeout') return 'the download took longer than 30 minutes';
  const last = result.stderr.trim().split('\n').pop()?.trim() ?? '';
  return last !== '' ? last.slice(0, 300) : `docker pull exited with code ${result.exitCode}`;
}

async function sizeOf(target: string): Promise<number> {
  let info;
  try {
    info = await lstat(target);
  } catch {
    return 0;
  }
  if (!info.isDirectory()) return info.size;
  const names = await readdir(target).catch(() => []);
  let total = 0;
  for (const name of names) total += await sizeOf(path.join(target, name));
  return total;
}
