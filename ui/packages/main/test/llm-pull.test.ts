import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { spawn } from 'node:child_process';
import { chmod, mkdir, mkdtemp, readdir, rm, stat, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { CHANNEL, readIpcError } from '@scout/shared';
import { registerLlmPullHandlers } from '../src/ipc/llm-pull.js';
import type { Logger } from '../src/util/logger.js';

const MODULE = path.join(import.meta.dir, '..', 'src', 'ipc', 'llm-pull.ts');

const HARNESS = `
import os from 'node:os';
const [modulePath, pullChannel, cancelChannel, body, expectedHome, mode] = process.argv.slice(2);
if (os.homedir() !== expectedHome) {
  console.log(JSON.stringify({ outcome: 'home not isolated: ' + os.homedir() }));
  process.exit(0);
}
setTimeout(() => {
  console.log(JSON.stringify({ outcome: 'still pending after 4 s', progress: [] }));
  process.exit(0);
}, 4000);
const { registerLlmPullHandlers } = await import(modulePath);
const handlers = new Map();
const quiet = { debug() {}, info() {}, warn() {}, error() {}, child() { return quiet; } };
const progress = [];
const stream = () =>
  new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode(body));
      if (mode === 'reset') controller.error(new Error('ECONNRESET'));
      if (mode === 'verify') {
        setTimeout(() => {
          controller.enqueue(new TextEncoder().encode('{"status":"success"}\\n'));
          controller.close();
        }, 1500);
      }
    },
  });
registerLlmPullHandlers(
  { handle: (c, h) => handlers.set(c, h), removeHandler: () => undefined },
  async () => new Response(mode === 'plain' ? body : stream()),
  quiet,
  1000,
  2000,
);
const sender = {
  isDestroyed: () => false,
  send: (_channel, event) => {
    progress.push(event.percent);
    if (mode === 'cancel') void handlers.get(cancelChannel)({}, { requestId: 'req-1' });
  },
};
let outcome;
try {
  await handlers.get(pullChannel)(
    { sender },
    { endpoint: 'http://localhost:11434', tag: 'qwen2.5:3b', requestId: 'req-1' },
  );
  outcome = 'resolved';
} catch (err) {
  outcome = 'rejected: ' + err.message;
}
console.log(JSON.stringify({ outcome, progress }));
process.exit(0);
`;

/**
 * Main's Ollama pull handler, run in a child process whose home directory is a temporary folder and whose PATH
 * starts with an \`ssh-keygen\` that only records that it ran: Bun reads the home directory once at start, so only a
 * child process keeps the pull away from the real home folder and the real \`ssh-keygen\`. The child's stall window
 * is 1 s while Ollama reports byte progress and 2 s while it reports only statuses.
 */
describe('Ollama pull', () => {
  let root: string;
  let home: string;
  let keygenRuns: string;
  let harness: string;
  let bin: string;

  beforeEach(async () => {
    root = await mkdtemp(path.join(os.tmpdir(), 'scout-llm-pull-'));
    home = path.join(root, 'home');
    bin = path.join(root, 'bin');
    await mkdir(home);
    await mkdir(bin);
    keygenRuns = path.join(root, 'ssh-keygen-runs.txt');
    const shim = path.join(bin, 'ssh-keygen');
    await writeFile(shim, `#!/bin/sh\necho "$@" >> "${keygenRuns}"\nexit 0\n`);
    await chmod(shim, 0o755);
    harness = path.join(root, 'pull-harness.mjs');
    await writeFile(harness, HARNESS);
  });

  afterEach(async () => {
    await rm(root, { recursive: true, force: true });
  });

  type Mode = 'plain' | 'reset' | 'stall' | 'cancel' | 'verify';

  function pull(body: string, mode: Mode = 'plain'): Promise<string> {
    return run(body, mode).then((result) => result.outcome);
  }

  function run(body: string, mode: Mode): Promise<{ outcome: string; progress: number[] }> {
    return new Promise((resolve, reject) => {
      const child = spawn(
        process.execPath,
        [
          harness,
          MODULE,
          CHANNEL.llmPullOllamaModel,
          CHANNEL.llmCancelOllamaPull,
          body,
          home,
          mode,
        ],
        {
          env: {
            ...Object.fromEntries(
              Object.entries(process.env).filter(([key]) => key.toUpperCase() !== 'PATH'),
            ),
            HOME: home,
            USERPROFILE: home,
            PATH: `${bin}${path.delimiter}${process.env['PATH'] ?? process.env['Path'] ?? ''}`,
          },
          stdio: ['ignore', 'pipe', 'pipe'],
        },
      );
      let out = '';
      let err = '';
      child.stdout.on('data', (chunk: Buffer) => (out += chunk.toString('utf8')));
      child.stderr.on('data', (chunk: Buffer) => (err += chunk.toString('utf8')));
      child.on('error', reject);
      child.on('close', () => {
        try {
          resolve(
            JSON.parse(out.trim().split('\n').at(-1) ?? '') as {
              outcome: string;
              progress: number[];
            },
          );
        } catch {
          reject(new Error(`the pull harness printed no outcome: ${out} ${err}`));
        }
      });
    });
  }

  it('A pull never runs ssh-keygen', async () => {
    const outcome = await pull('{"status":"success"}\n');

    expect({
      outcome,
      keygen: await stat(keygenRuns).then(
        () => 'ssh-keygen ran',
        () => 'ssh-keygen did not run',
      ),
    }).toEqual({ outcome: 'resolved', keygen: 'ssh-keygen did not run' });
  });

  it('A pull never writes under ~/.ollama', async () => {
    const outcome = await pull('{"status":"success"}\n');

    expect({
      outcome,
      ollama: await readdir(path.join(home, '.ollama')).then(
        (entries) => `~/.ollama exists with [${entries.join(', ')}]`,
        () => 'no ~/.ollama',
      ),
    }).toEqual({ outcome: 'resolved', ollama: 'no ~/.ollama' });
  });

  it('A pull whose stream ends without a success line is reported as failed', async () => {
    expect(await pull('{"status":"pulling","total":10,"completed":3}\n')).toStartWith('rejected: ');
  });

  it('A final error line without a trailing newline fails the pull with that error', async () => {
    expect(await pull('{"status":"pulling","total":10,"completed":3}\n{"error":"disk full"}')).toBe(
      'rejected: [scout:llm/pull-failed] disk full',
    );
  });

  it('A pull that receives nothing for the stall window stops with the reason', async () => {
    expect(await pull('{"status":"pulling","total":10,"completed":3}\n', 'stall')).toBe(
      'rejected: [scout:llm/pull-failed] Ollama sent nothing for 1 s; the download stopped.',
    );
  });

  it('A pull whose manifest phase hangs is stopped once the status window ends, with the reason in plain words', async () => {
    expect(await pull('{"status":"pulling manifest"}\n', 'stall')).toBe(
      'rejected: [scout:llm/pull-failed] Ollama reported no progress for 2 s; the download stopped.',
    );
  });

  it('A pull that goes silent longer than the stall window while Ollama verifies the digest, then succeeds, resolves', async () => {
    expect(
      await pull(
        '{"status":"pulling","total":10,"completed":10}\n{"status":"verifying sha256 digest"}\n',
        'verify',
      ),
    ).toBe('resolved');
  });

  it('Cancel stops a running pull: it rejects as cancelled after the progress it reported', async () => {
    expect(await run('{"status":"pulling","total":10,"completed":3}\n', 'cancel')).toEqual({
      outcome: 'rejected: [scout:llm/pull-cancelled] The download was cancelled.',
      progress: [30],
    });
  });

  it('A pull whose stream breaks off fails with the reason instead of counting as done', async () => {
    expect(await pull('{"status":"pulling","total":10,"completed":3}\n', 'reset')).toStartWith(
      'rejected: [scout:llm/pull-failed] the download from Ollama broke off (',
    );
  });
});

/** The names the Pull by name field may send to Ollama; the same rows are in the shared contract test and the dialog's spec. */
const OLLAMA_NAME_ROWS: readonly { name: string; ok: boolean }[] = [
  { name: 'sample-chat', ok: true },
  { name: 'sample-chat:3b', ok: true },
  { name: 'sample-chat2.5:0.5b', ok: true },
  { name: 'example-org/sample-chat:7b', ok: true },
  { name: 'sample_chat-v2:latest', ok: true },
  { name: 'sample-chat:7b-instruct-q4_K_M', ok: true },
  { name: `${'a'.repeat(80)}:${'b'.repeat(80)}`, ok: true },
  { name: '  sample-chat:3b  ', ok: false },
  { name: '', ok: false },
  { name: 'Sample-Chat:3b', ok: false },
  { name: 'sample chat:3b', ok: false },
  { name: 'sample-chat:', ok: false },
  { name: ':3b', ok: false },
  { name: 'sample-chat:3b:q4', ok: false },
  { name: 'registry.example/example-org/sample-chat:7b', ok: false },
  { name: 'registry.example/sample-chat:7b', ok: false },
  { name: 'https://registry.example/sample-chat', ok: false },
  { name: 'example-org/', ok: false },
  { name: '/sample-chat', ok: false },
  { name: '../sample-chat', ok: false },
  { name: 'sample\u0000chat', ok: false },
  { name: 'sample-chat\t3b', ok: false },
  { name: 'sample-chat?x=1', ok: false },
  { name: 'user@sample-chat', ok: false },
  { name: 'ollama run sample-chat', ok: false },
  { name: 'a'.repeat(81), ok: false },
  { name: '-sample-chat', ok: false },
];

const NAME_RULE = 'That is not a model name. Use name or name:tag, for example llama3.2:3b.';
const LOCAL = 'http://localhost:11434';

/**
 * The pull handler in this process, over a fetch that only records what it was asked and answers as `answer` says:
 * nothing here can leave the process, so the refusals are proven by what reached the fetch.
 */
function mainPull(
  answer: () => Promise<Response> = async () => streamed('{"status":"success"}\n'),
) {
  const fetched: { url: string; body: unknown }[] = [];
  const warnings: { msg: string; ctx: unknown }[] = [];
  const handlers = new Map<string, (event: unknown, req: unknown) => Promise<unknown>>();
  const logger: Logger = {
    debug: () => undefined,
    info: () => undefined,
    warn: (msg, ctx) => void warnings.push({ msg, ctx }),
    error: () => undefined,
    child: () => logger,
  };
  registerLlmPullHandlers(
    {
      handle: (channel, handler) => void handlers.set(channel, handler as never),
      removeHandler: () => undefined,
    },
    (async (url: string, init?: RequestInit) => {
      fetched.push({ url, body: JSON.parse(String(init?.body)) });
      return answer();
    }) as unknown as typeof fetch,
    logger,
  );
  const sender = { isDestroyed: () => false, send: () => undefined };
  const pull = (endpoint: string, tag: string): Promise<{ outcome: string; code: string | null }> =>
    handlers.get(CHANNEL.llmPullOllamaModel)!(
      { sender },
      { endpoint, tag, requestId: 'req-1' },
    ).then(
      () => ({ outcome: 'resolved', code: null }),
      (err: unknown) => {
        const read = readIpcError(err);
        return { outcome: `rejected: ${read.message}`, code: read.code };
      },
    );
  return { pull, fetched, warnings };
}

function streamed(lines: string, status = 200): Response {
  return new Response(lines, { status });
}

describe('Pull by name in main', () => {
  it.each(OLLAMA_NAME_ROWS.map((row) => [JSON.stringify(row.name), row] as const))(
    'a pull of %s reaches Ollama only when it is a model name, else it is refused before any request with the sentence that says what a name looks like and a log line without the name',
    async (_label, row) => {
      const { pull, fetched, warnings } = mainPull();

      const result = await pull(LOCAL, row.name);

      expect({ result, fetched, warnings }).toEqual(
        row.ok
          ? {
              result: { outcome: 'resolved', code: null },
              fetched: [{ url: `${LOCAL}/api/pull`, body: { name: row.name, stream: true } }],
              warnings: [],
            }
          : {
              result: { outcome: `rejected: ${NAME_RULE}`, code: 'llm/pull-failed' },
              fetched: [],
              warnings: [
                { msg: 'llm-pull: refused a name that is not a model name', ctx: undefined },
              ],
            },
      );
    },
  );

  it.each([
    ['http://localhost:11434', true],
    ['http://127.0.0.1:11434', true],
    ['http://[::1]:11434', true],
    ['http://192.168.1.20:11434', false],
    ['https://ollama.example', false],
    ['http://localhost.example:11434', false],
    ['file:///tmp/ollama', false],
    ['', false],
  ] as const)(
    'a pull asked of %s goes ahead only when that is an Ollama on this computer',
    async (endpoint, local) => {
      const { pull, fetched } = mainPull();

      const result = await pull(endpoint, 'sample-chat:3b');

      expect({ result, fetched: fetched.map((f) => f.url) }).toEqual(
        local
          ? { result: { outcome: 'resolved', code: null }, fetched: [`${endpoint}/api/pull`] }
          : {
              result: {
                outcome: 'rejected: Scout pulls models only from the Ollama on this computer.',
                code: 'llm/pull-failed',
              },
              fetched: [],
            },
      );
    },
  );

  it.each([
    [
      'Ollama does not answer (connection refused)',
      async (): Promise<Response> => {
        throw new TypeError('fetch failed: ECONNREFUSED');
      },
      { outcome: `rejected: Could not reach Ollama at ${LOCAL}`, code: 'llm/ollama-unreachable' },
    ],
    [
      'the library has no model by that name (the error line)',
      async () =>
        streamed(
          '{"status":"pulling manifest"}\n{"error":"pull model manifest: file does not exist"}\n',
        ),
      {
        outcome: 'rejected: pull model manifest: file does not exist',
        code: 'llm/model-not-found',
      },
    ],
    [
      'the library has no model by that name (a 404 that says so)',
      async () => streamed('{"error":"pull model manifest: file does not exist"}', 404),
      {
        outcome: 'rejected: pull model manifest: file does not exist',
        code: 'llm/model-not-found',
      },
    ],
    [
      'a manifest error that is not a missing model',
      async () =>
        streamed('{"error":"pull model manifest: open id_ed25519: no such file or directory"}\n'),
      {
        outcome: 'rejected: pull model manifest: open id_ed25519: no such file or directory',
        code: 'llm/pull-failed',
      },
    ],
    [
      'HTTP 500 with Ollama reason',
      async () => streamed('{"error":"no space left on device"}', 500),
      { outcome: 'rejected: no space left on device', code: 'llm/pull-failed' },
    ],
    [
      'HTTP 500 with no reason',
      async () => streamed('Internal Server Error', 500),
      { outcome: 'rejected: Ollama returned HTTP 500', code: 'llm/pull-failed' },
    ],
  ] as const)(
    'a pull that fails because %s rejects with the code the dialog reads and Ollama own words',
    async (_case, answer, expected) => {
      const { pull } = mainPull(answer);

      expect(await pull(LOCAL, 'sample-chat:3b')).toEqual(expected);
    },
  );
});
