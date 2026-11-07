import { describe, expect, it } from 'bun:test';
import {
  CHANNEL,
  ControlPlaneUnavailableError,
  readIpcError,
  type GitHubVerdictRequest,
  type GitHubVerdictStatus,
} from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { registerGitHubVerdictHandlers, type VerdictClient } from '../src/ipc/github-verdict.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

const VERDICT: GitHubVerdictRequest = {
  repository: 'acme/shop',
  number: 5,
  verdict: 'approve',
  body: '**Verdict:** merge',
  comments: [{ path: 'src/Cart.cs', line: 12, body: 'Lock the move' }],
  commit: '3f2a9c1d4b5e6f708192a3b4c5d6e7f809a1b2c3',
  testsLine: 'Tests: 42 passed in the container (dotnet test).',
};

function status(
  state: GitHubVerdictStatus['state'],
  url: string | null = null,
  reason: string | null = null,
): GitHubVerdictStatus {
  return { actionId: 'act-1', state, url, reason };
}

function setup(replies: Array<GitHubVerdictStatus | Error> = []) {
  const handlers = new Map<string, IpcHandler>();
  const ipc: Pick<IpcMain, 'handle' | 'removeHandler'> = {
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
  const given: GitHubVerdictRequest[] = [];
  const reads: string[] = [];
  let giveFailure: Error | null = null;
  const client: VerdictClient = {
    async giveVerdict(request) {
      if (giveFailure !== null) throw giveFailure;
      given.push(request);
      return { actionId: 'act-1' };
    },
    async getVerdict(actionId) {
      reads.push(actionId);
      const reply = replies.shift();
      if (reply === undefined) throw new Error('no scripted status');
      if (reply instanceof Error) throw reply;
      return reply;
    },
  };
  const pending: Array<{ callback: () => void; ms: number; cleared: boolean }> = [];
  const pushed: GitHubVerdictStatus[] = [];
  const { logger, lines } = recordingLogger();
  const dispose = registerGitHubVerdictHandlers(
    ipc,
    async () => client,
    (s) => pushed.push(s),
    logger,
    {
      setTimeout(callback, ms) {
        const timer = { callback, ms, cleared: false };
        pending.push(timer);
        return timer;
      },
      clearTimeout(handle) {
        (handle as { cleared: boolean }).cleared = true;
      },
    },
  );

  async function tick(): Promise<number | null> {
    const next = pending.shift();
    if (next === undefined) return null;
    if (!next.cleared) next.callback();
    for (let i = 0; i < 10; i++) await Promise.resolve();
    return next.ms;
  }

  function invoke(channel: string, payload?: unknown): Promise<unknown> {
    const handler = handlers.get(channel);
    if (handler === undefined) throw new Error(`no handler for ${channel}`);
    return Promise.resolve(handler(NULL_EVENT, payload));
  }

  return {
    handlers,
    given,
    reads,
    pushed,
    pending,
    lines,
    tick,
    invoke,
    dispose,
    failGive(err: Error): void {
      giveFailure = err;
    },
  };
}

async function refusal(promise: Promise<unknown>): Promise<string> {
  try {
    await promise;
  } catch (err: unknown) {
    return readIpcError(err).message;
  }
  throw new Error('expected a refusal');
}

describe('GitHub verdict IPC', () => {
  it("the owner's click sends the verdict once to the control plane as an approved action and answers its id", async () => {
    const t = setup();

    const action = await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);

    expect(action).toEqual({ actionId: 'act-1' });
    expect(t.given).toEqual([VERDICT]);
  });

  it('follows the action every 2 seconds, pushes each change once, and stops once the verdict is given', async () => {
    const t = setup([
      status('queued'),
      status('queued'),
      status('running'),
      status('given', 'https://github.com/acme/shop/pull/5#pullrequestreview-1'),
    ]);

    await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    const intervals = [await t.tick(), await t.tick(), await t.tick(), await t.tick()];

    expect(intervals).toEqual([2000, 2000, 2000, 2000]);
    expect(t.pushed).toEqual([
      status('queued'),
      status('running'),
      status('given', 'https://github.com/acme/shop/pull/5#pullrequestreview-1'),
    ]);
    expect(t.pending).toEqual([]);
  });

  it('stops following a verdict that failed and pushes why', async () => {
    const t = setup([status('failed', null, 'The pull request moved since the review.')]);

    await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    await t.tick();

    expect(t.pushed).toEqual([status('failed', null, 'The pull request moved since the review.')]);
    expect(t.pending).toEqual([]);
  });

  it('when the control plane cannot be reached, the card gets the reason and nothing is followed', async () => {
    const t = setup();
    t.failGive(new ControlPlaneUnavailableError('Control plane not reachable at /run/scout.sock'));

    const message = await refusal(t.invoke(CHANNEL.githubGiveVerdict, VERDICT));

    expect(message).toBe('Control plane not reachable at /run/scout.sock');
    expect(t.pending).toEqual([]);
  });

  it('keeps following through a failed read and gives up after five in a row, pushing nothing it did not read', async () => {
    const down = new ControlPlaneUnavailableError('down');
    const t = setup([down, status('running'), down, down, down, down, down]);

    await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    const ticks: Array<number | null> = [];
    for (let i = 0; i < 8; i++) ticks.push(await t.tick());

    expect(t.pushed).toEqual([status('running')]);
    expect(t.reads).toHaveLength(7);
    expect(ticks.at(-1)).toBeNull();
    expect(t.lines.filter((l) => l.includes('github: verdict status not read'))).toHaveLength(6);
  });

  it.each([
    ['a repository that is not owner/name', { ...VERDICT, repository: 'acme' }],
    ['a comment verdict', { ...VERDICT, verdict: 'comment' }],
    ['a pull request number of 0', { ...VERDICT, number: 0 }],
    ['a commit that is not a sha', { ...VERDICT, commit: 'main' }],
    ['comments that are not a list', { ...VERDICT, comments: 'none' }],
    ['a comment without a body', { ...VERDICT, comments: [{ path: 'a.cs', line: 1 }] }],
    ['a body over 65,000 characters', { ...VERDICT, body: 'x'.repeat(65_001) }],
    ['no tests line', { ...VERDICT, testsLine: undefined }],
  ])('refuses %s before reaching the control plane', async (_case, request) => {
    const t = setup();

    const message = await refusal(t.invoke(CHANNEL.githubGiveVerdict, request));

    expect(message).toBe('Scout could not read that verdict.');
    expect(t.given).toEqual([]);
  });

  it('the status channel reads the action, and refuses an id it cannot have issued', async () => {
    const t = setup([status('running')]);

    const read = await t.invoke(CHANNEL.githubVerdictStatus, { actionId: 'act-1' });
    const bad = await refusal(t.invoke(CHANNEL.githubVerdictStatus, { actionId: '../x' }));

    expect(read).toEqual(status('running'));
    expect(bad).toBe('Scout could not read that verdict.');
  });

  it('stops following every verdict when main shuts down', async () => {
    const t = setup([status('queued')]);

    await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    t.dispose();
    await t.tick();

    expect(t.reads).toEqual([]);
    expect(t.handlers.size).toBe(0);
  });
});
