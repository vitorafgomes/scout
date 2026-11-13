import { describe, expect, it } from 'bun:test';
import {
  CHANNEL,
  ControlPlaneUnavailableError,
  readIpcError,
  type GitHubVerdictProgress,
  type GitHubVerdictRequest,
  type GitHubVerdictStatus,
} from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import {
  pushToWindows,
  registerGitHubVerdictHandlers,
  type VerdictClient,
} from '../src/ipc/github-verdict.js';
import { recordingLogger } from './kubernetes-fakes.js';
import { MAIN_EVENT, SENDERS, SENDER_HOOKS } from './sender-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const VERDICT: GitHubVerdictRequest = {
  repository: 'acme/shop',
  number: 5,
  verdict: 'approve',
  body: '**Verdict:** merge',
  comments: [{ path: 'src/Cart.cs', line: 12, body: 'Lock the move' }],
  commit: '3f2a9c1d4b5e6f708192a3b4c5d6e7f809a1b2c3',
  testsLine: 'Tests: 42 passed in the container (dotnet test).',
};

const KEY = { repository: VERDICT.repository, number: VERDICT.number, commit: VERDICT.commit };
const REVIEW_URL = 'https://github.com/acme/shop/pull/5#pullrequestreview-1';

function status(
  state: GitHubVerdictProgress['state'],
  url: string | null = null,
  reason: string | null = null,
  actionId = 'act-1',
): GitHubVerdictProgress {
  return { actionId, state, url, reason };
}

function full(
  state: GitHubVerdictProgress['state'],
  url: string | null = null,
  reason: string | null = null,
  actionId = 'act-1',
): GitHubVerdictStatus {
  return { ...status(state, url, reason, actionId), ...KEY, verdict: 'approve' };
}

function setup(replies: Array<GitHubVerdictProgress | Error> = [], gate?: Promise<void>) {
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
      const actionId = `act-${given.length}`;
      await gate;
      return { actionId };
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
    SENDER_HOOKS,
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

  function invokeFrom(
    event: IpcMainInvokeEvent,
    channel: string,
    payload?: unknown,
  ): Promise<unknown> {
    const handler = handlers.get(channel);
    if (handler === undefined) throw new Error(`no handler for ${channel}`);
    return Promise.resolve(handler(event, payload));
  }

  function invoke(channel: string, payload?: unknown): Promise<unknown> {
    return invokeFrom(MAIN_EVENT, channel, payload);
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
    invokeFrom,
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

  it('a click on Post goes to the control plane once as the approved verdict comment, a second click answers the same action, and the posted review reaches every window with its link', async () => {
    const t = setup([status('given', REVIEW_URL)]);
    const post: GitHubVerdictRequest = { ...VERDICT, verdict: 'comment', testsLine: '' };

    const first = await t.invoke(CHANNEL.githubGiveVerdict, post);
    const second = await t.invoke(CHANNEL.githubGiveVerdict, post);
    await t.tick();
    const afterPosted = await t.invoke(CHANNEL.githubGiveVerdict, post);

    expect([first, second, afterPosted]).toEqual([
      { actionId: 'act-1' },
      { actionId: 'act-1' },
      { actionId: 'act-1' },
    ]);
    expect(t.given).toEqual([post]);
    expect(t.pushed.at(-1)).toEqual({
      ...KEY,
      verdict: 'comment',
      actionId: 'act-1',
      state: 'given',
      url: REVIEW_URL,
      reason: null,
    });
  });

  it('follows the action every 2 seconds, pushes each change once, and stops once the verdict is given', async () => {
    const t = setup([
      status('queued'),
      status('queued'),
      status('running'),
      status('given', REVIEW_URL),
    ]);

    await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    const intervals = [await t.tick(), await t.tick(), await t.tick(), await t.tick()];

    expect(intervals).toEqual([2000, 2000, 2000, 2000]);
    expect(t.pushed).toEqual([full('queued'), full('running'), full('given', REVIEW_URL)]);
    expect(t.pending).toEqual([]);
  });

  it('stops following a verdict that failed and pushes why', async () => {
    const t = setup([status('failed', null, 'The pull request moved since the review.')]);

    await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    await t.tick();

    expect(t.pushed).toEqual([
      full('queued'),
      full('failed', null, 'The pull request moved since the review.'),
    ]);
    expect(t.pending).toEqual([]);
  });

  it('when the control plane cannot be reached, the card gets a sentence without the socket path, the log keeps the path, and nothing is followed', async () => {
    const t = setup();
    t.failGive(new ControlPlaneUnavailableError('Control plane not reachable at /run/scout.sock'));

    const message = await refusal(t.invoke(CHANNEL.githubGiveVerdict, VERDICT));

    expect(message).toBe('The Scout control plane did not answer.');
    expect(
      t.lines.filter(
        (l) => l.includes('github: verdict not sent') && l.includes('/run/scout.sock'),
      ),
    ).toHaveLength(1);
    expect(t.pending).toEqual([]);
  });

  it('keeps following through a failed read and gives up after five in a row, pushing nothing it did not read', async () => {
    const down = new ControlPlaneUnavailableError('down');
    const t = setup([down, status('running'), down, down, down, down, down]);

    await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    const ticks: Array<number | null> = [];
    for (let i = 0; i < 8; i++) ticks.push(await t.tick());

    expect(t.pushed).toEqual([full('queued'), full('running')]);
    expect(t.reads).toHaveLength(7);
    expect(ticks.at(-1)).toBeNull();
    expect(t.lines.filter((l) => l.includes('github: verdict status not read'))).toHaveLength(6);
  });

  it.each([
    ['a repository that is not owner/name', { ...VERDICT, repository: 'acme' }],
    ['a verdict that is not approve, request_changes or comment', { ...VERDICT, verdict: 'merge' }],
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

  it('the status channel answers the action with its pull request, commit and verdict, and refuses an id it did not give', async () => {
    const t = setup([status('running')]);

    await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    const read = await t.invoke(CHANNEL.githubVerdictStatus, { actionId: 'act-1' });
    const unknown = await refusal(t.invoke(CHANNEL.githubVerdictStatus, { actionId: 'act-9' }));
    const bad = await refusal(t.invoke(CHANNEL.githubVerdictStatus, { actionId: '../x' }));

    expect(read).toEqual(full('running'));
    expect(t.pushed.at(-1)).toEqual(full('running'));
    expect([unknown, bad]).toEqual([
      'Scout has no verdict with that id.',
      'Scout could not read that verdict.',
    ]);
  });

  it('a status read while the control plane is down answers the sentence without the socket path, and the log keeps the path', async () => {
    const t = setup([
      new ControlPlaneUnavailableError('Control plane not reachable at /run/scout.sock'),
    ]);
    await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);

    const message = await refusal(t.invoke(CHANNEL.githubVerdictStatus, { actionId: 'act-1' }));

    expect(message).toBe('The Scout control plane did not answer.');
    expect(
      t.lines.filter(
        (l) => l.includes('github: verdict status not answered') && l.includes('/run/scout.sock'),
      ),
    ).toHaveLength(1);
  });

  it('a second click on the same pull request and commit answers the same action while it is queued, running or given', async () => {
    const t = setup([status('running'), status('given', REVIEW_URL)]);

    const first = await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    const whileQueued = await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    await t.tick();
    const whileRunning = await t.invoke(CHANNEL.githubGiveVerdict, {
      ...VERDICT,
      verdict: 'request_changes',
    });
    await t.tick();
    const onceGiven = await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);

    expect([first, whileQueued, whileRunning, onceGiven]).toEqual([
      { actionId: 'act-1' },
      { actionId: 'act-1' },
      { actionId: 'act-1' },
      { actionId: 'act-1' },
    ]);
    expect(t.given).toHaveLength(1);
  });

  it('a click after the verdict failed gives a new one, and another commit is its own verdict', async () => {
    const t = setup([status('failed', null, 'The pull request moved since the review.')]);

    await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    await t.tick();
    const retried = await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    const otherCommit = await t.invoke(CHANNEL.githubGiveVerdict, {
      ...VERDICT,
      commit: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
    });

    expect([retried, otherCommit]).toEqual([{ actionId: 'act-2' }, { actionId: 'act-3' }]);
    expect(t.given).toHaveLength(3);
  });

  it('two windows clicking at the same moment send one verdict and get the same action', async () => {
    let open: () => void = () => undefined;
    const t = setup(
      [],
      new Promise<void>((resolve) => {
        open = resolve;
      }),
    );

    const fromChat = t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    const fromQuickAsk = t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    open();

    expect(await Promise.all([fromChat, fromQuickAsk])).toEqual([
      { actionId: 'act-1' },
      { actionId: 'act-1' },
    ]);
    expect(t.given).toHaveLength(1);
  });

  it('a window opened later finds the verdict for its pull request and commit, as last read, and null for another', async () => {
    const t = setup([status('running')]);

    const before = await t.invoke(CHANNEL.githubVerdictFor, KEY);
    await t.invoke(CHANNEL.githubGiveVerdict, VERDICT);
    await t.tick();
    const found = await t.invoke(CHANNEL.githubVerdictFor, KEY);
    const other = await t.invoke(CHANNEL.githubVerdictFor, { ...KEY, number: 6 });
    const bad = await refusal(t.invoke(CHANNEL.githubVerdictFor, { ...KEY, commit: 'main' }));

    expect([before, found, other]).toEqual([null, full('running'), null]);
    expect(bad).toBe('Scout could not read that verdict.');
  });

  for (const [from, event, kind] of SENDERS) {
    if (kind === 'main' || kind === 'quick-ask') {
      it(`${CHANNEL.githubGiveVerdict}, only the windows that show the verdict buttons decide: a verdict from ${from} reaches the control plane once and logs no refusal`, async () => {
        const t = setup([]);

        const action = await t.invokeFrom(event, CHANNEL.githubGiveVerdict, VERDICT);

        expect(action).toEqual({ actionId: 'act-1' });
        expect(t.given).toEqual([VERDICT]);
        expect(t.lines.filter((line) => line.includes('refused'))).toEqual([]);
        t.dispose();
      });
    } else {
      it(`${CHANNEL.githubGiveVerdict}, only the windows that show the verdict buttons decide: a verdict from ${from} is refused with github/not-verdict-window, sends nothing, and is logged at warn with the channel and the sender kind, never the payload`, async () => {
        const t = setup([]);

        const refused = await t.invokeFrom(event, CHANNEL.githubGiveVerdict, VERDICT).then(
          () => null,
          (err: unknown) => readIpcError(err),
        );

        expect(refused).toEqual({
          code: 'github/not-verdict-window',
          message: 'A verdict can be given only from the Scout window or quick ask.',
        });
        expect(t.given).toEqual([]);
        expect(
          t.lines.map((line) => {
            const { lvl, msg, ctx } = JSON.parse(line) as Record<string, unknown>;
            return { lvl, msg, ctx };
          }),
        ).toEqual([
          {
            lvl: 'warn',
            msg: 'ipc: call refused for its sender',
            ctx: { channel: CHANNEL.githubGiveVerdict, sender: kind },
          },
        ]);
        t.dispose();
      });
    }
  }

  it('every open window receives each pushed status, and a closed one is skipped', () => {
    const sent: Array<[string, string, unknown]> = [];
    const window = (name: string, destroyed = false) => ({
      isDestroyed: () => destroyed,
      webContents: {
        send: (channel: string, payload: unknown) => sent.push([name, channel, payload]),
      },
    });

    pushToWindows(() => [window('chat'), window('closed', true), window('quick-ask')])(
      full('given', REVIEW_URL),
    );

    expect(sent).toEqual([
      ['chat', CHANNEL.githubVerdict, full('given', REVIEW_URL)],
      ['quick-ask', CHANNEL.githubVerdict, full('given', REVIEW_URL)],
    ]);
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
