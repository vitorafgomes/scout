import { describe, expect, it } from 'bun:test';
import {
  CHANNEL,
  ControlPlaneUnavailableError,
  jobControls,
  readIpcError,
  type AppNotification,
  type JobControlAction,
  type NotificationKind,
  type WriteLogEntryDto,
} from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import type { JobControlAnswer, JobControlRequest } from '../src/control-plane/client.js';
import {
  NOTICE_WAIT_MS,
  NOT_CONFIRMED,
  NOT_REPORTED,
  NO_ANSWER,
  RECORD_POLL_MS,
  RECORD_WAIT_MS,
  TESTS_ANSWER_FAILED,
  TESTS_FINISHED,
  TESTS_RUNNING,
  TESTS_WORKER_STOPPED,
  WORKER_STOPPED,
  createJobControls,
  registerJobControlHandler,
} from '../src/notifications/job-controls.js';
import {
  NotificationStore,
  type NotificationInput,
} from '../src/notifications/notification-store.js';
import { workerNotice } from '../src/notifications/sources.js';
import { recordingLogger } from './kubernetes-fakes.js';
import { MAIN_EVENT, SENDERS, SENDER_HOOKS } from './sender-fakes.js';

const QUEUED = 'Queued; Scout applies it within a few seconds.';
const BAD_REPOSITORY =
  'The repository must be given as owner and repo, each made of letters, digits, dots, hyphens or underscores.';

const RUNNING_AUDIT: NotificationInput = {
  id: 'audit:acme/shop',
  kind: 'running',
  title: 'Audit of acme/shop',
  detail: 'Area 3 of 8 done · 4 findings so far · cards arrive in the chat as each area ends',
  progress: 38,
  at: 100,
  source: 'worker',
};
const PAUSED_AUDIT: NotificationInput = {
  id: 'audit:acme/shop',
  kind: 'paused',
  title: 'Audit of acme/shop paused',
  detail: 'Paused by you · area 3 of 8 done · 4 findings so far',
  at: 100,
  source: 'worker',
};
const RUNNING_TESTS: NotificationInput = {
  id: 'tests:acme/api#41',
  kind: 'running',
  title: 'Tests for acme/api#41',
  detail: TESTS_RUNNING,
  at: 100,
  source: 'worker',
};

const OFFERED_BEFORE = new Map<NotificationInput, string>([
  [RUNNING_AUDIT, 'pause stop'],
  [PAUSED_AUDIT, 'continue stop'],
  [RUNNING_TESTS, 'stop'],
]);

function notice(text: string): string {
  return JSON.stringify({ id: null, event: 'notice', text });
}

function record(
  controlStatus: WriteLogEntryDto['controlStatus'],
  outcome: string,
  actionId = 'c1',
): WriteLogEntryDto {
  return {
    at: '2026-11-10T10:00:00Z',
    featureId: 'github',
    action: 'Stop audit',
    target: 'acme/shop',
    outcome,
    approvedBy: 'owner',
    actionId,
    signedAs: null,
    controlStatus,
  };
}

function setup(seed: readonly NotificationInput[] = []) {
  const clock = { now: 1_000 };
  const { logger, lines } = recordingLogger();
  const store = new NotificationStore({
    vault: { get: async () => null, store: async () => undefined },
    push: () => undefined,
    now: () => clock.now,
    setTimer: () => () => undefined,
    logger,
  });
  for (const n of seed) store.record(n);
  const asked: JobControlRequest[] = [];
  const reads: number[] = [];
  let answer: (r: JobControlRequest) => Promise<JobControlAnswer> = async () => ({
    accepted: true,
    reason: QUEUED,
    controlId: 'c1',
  });
  let writes: () => Promise<WriteLogEntryDto[]> = async () => [];
  const timers: { ms: number; callback: () => void; cancelled: boolean }[] = [];
  const jobs = createJobControls({
    store,
    client: async () => ({
      controlJob: (r) => {
        asked.push(r);
        return answer(r);
      },
      listWrites: (limit) => {
        reads.push(limit);
        return writes();
      },
    }),
    setTimer: (ms, callback) => {
      const timer = { ms, callback, cancelled: false };
      timers.push(timer);
      return () => {
        timer.cancelled = true;
      };
    },
    now: () => clock.now,
    logger,
  });
  const flush = async (): Promise<void> => {
    for (let i = 0; i < 6; i += 1) await new Promise((resolve) => setImmediate(resolve));
  };
  const tick = async (): Promise<void> => {
    clock.now += RECORD_POLL_MS;
    for (const timer of timers.splice(0)) if (!timer.cancelled) timer.callback();
    await flush();
  };
  const workerLine = (json: string): void => {
    jobs.handleWorkerLine(json);
    const input = workerNotice(json, () => store.nextId('worker'));
    if (input !== null) jobs.notice(input);
  };
  const get = (id: string): AppNotification | undefined => store.list().find((n) => n.id === id);
  return {
    store,
    jobs,
    asked,
    reads,
    lines,
    timers,
    clock,
    flush,
    tick,
    workerLine,
    get,
    answerWith(next: (r: JobControlRequest) => Promise<JobControlAnswer>): void {
      answer = next;
    },
    writesAre(next: () => Promise<WriteLogEntryDto[]>): void {
      writes = next;
    },
  };
}

function shown(n: AppNotification | undefined): unknown {
  return n === undefined
    ? null
    : {
        kind: n.kind,
        title: n.title,
        detail: n.detail,
        progress: n.progress ?? null,
        control: n.control ?? null,
        note: n.note ?? null,
        offered: jobControls(n).join(' '),
      };
}

const CELLS: readonly [string, NotificationInput, JobControlAction, JobControlRequest][] = [
  [
    'Pause on a running audit',
    RUNNING_AUDIT,
    'pause',
    { kind: 'audit', owner: 'acme', repo: 'shop', action: 'pause' },
  ],
  [
    'Stop on a running audit',
    RUNNING_AUDIT,
    'stop',
    { kind: 'audit', owner: 'acme', repo: 'shop', action: 'stop' },
  ],
  [
    'Continue on a paused audit',
    PAUSED_AUDIT,
    'continue',
    { kind: 'audit', owner: 'acme', repo: 'shop', action: 'continue' },
  ],
  [
    'Stop on a paused audit',
    PAUSED_AUDIT,
    'stop',
    { kind: 'audit', owner: 'acme', repo: 'shop', action: 'stop' },
  ],
  [
    'Stop on a running test run',
    RUNNING_TESTS,
    'stop',
    { kind: 'tests', owner: 'acme', repo: 'api', number: 41, action: 'stop' },
  ],
];

const ANSWERS: readonly [
  string,
  (r: JobControlRequest) => Promise<JobControlAnswer>,
  boolean,
  string | null,
][] = [
  [
    'accepted: the click waits for the worker, the controls replaced, nothing shown as paused or stopped',
    async () => ({ accepted: true, reason: QUEUED, controlId: 'c1' }),
    true,
    null,
  ],
  [
    'refused: the plain reason on the entry and the controls back',
    async () => ({ accepted: false, reason: BAD_REPOSITORY, controlId: null }),
    false,
    BAD_REPOSITORY,
  ],
  [
    'no answer, the control plane down or timed out: said so and the controls back, never applied',
    async () => {
      throw new ControlPlaneUnavailableError('Control plane not reachable at /run/scout.sock');
    },
    false,
    NO_ANSWER,
  ],
  [
    'an error the control plane gave: its message and the controls back',
    async () => {
      throw new Error('PERMISSION_DENIED');
    },
    false,
    'Scout could not send this click: PERMISSION_DENIED',
  ],
];

describe('the bell controls of a job, per entry and per answer of the control plane', () => {
  for (const [cell, seed, action, request] of CELLS) {
    for (const [answer, respond, waits, note] of ANSWERS) {
      it(`${cell}, ${answer}`, async () => {
        const t = setup([seed]);
        t.answerWith(respond);
        const before = shown(t.get(seed.id)) as Record<string, unknown>;

        await t.jobs.control(seed.id, action);

        expect(t.asked).toEqual([request]);
        expect(shown(t.get(seed.id))).toEqual({
          ...before,
          control: waits ? { action, phase: 'waiting' } : null,
          note,
          offered: waits ? '' : OFFERED_BEFORE.get(seed),
        });
        expect(t.get(seed.id)?.read).toBe(note === null);
      });
    }
  }

  for (const [cell, seed, action] of CELLS) {
    it(`${cell}: while the control plane is asked the controls stay shown and inactive, and a second click sends nothing`, async () => {
      const t = setup([seed]);
      let release: (a: JobControlAnswer) => void = () => undefined;
      t.answerWith(() => new Promise((resolve) => (release = resolve)));

      const first = t.jobs.control(seed.id, action);
      await t.flush();
      const asking = shown(t.get(seed.id));
      const second = await t.jobs.control(seed.id, action).then(
        () => null,
        (err: unknown) => (err as Error).message,
      );
      release({ accepted: true, reason: QUEUED, controlId: 'c1' });
      await first;

      expect(asking).toMatchObject({
        control: { action, phase: 'asking' },
        offered: OFFERED_BEFORE.get(seed),
      });
      expect(second).toBe('Scout could not send that control.');
      expect(t.asked.length).toBe(1);
    });
  }
});

const STOP_RECORD =
  'Stopped the audit of acme/shop; an area in progress finishes first. The findings so far stay on the cards. 3 of 8 areas done, 0 not covered, 4 findings, 40 files read.';
const AI_SOURCE_PAUSED =
  "The audit of acme/shop paused: the AI provider's usage limit was reached. Ask Scout to continue the audit of acme/shop when the AI Source has capacity again.";
const AI_SOURCE_REASON =
  'The AI Source reached its usage limit. Ask Scout to continue when it has capacity again.';

describe('what the worker says after a click was queued', () => {
  const rows: readonly [
    string,
    NotificationInput,
    JobControlAction,
    WriteLogEntryDto | null,
    string | null,
    unknown,
  ][] = [
    [
      'a pause applied on a running audit keeps Pausing after this area until the notice, then the entry is paused by you with the area reached',
      RUNNING_AUDIT,
      'pause',
      record('applied', 'Paused the audit of acme/shop; an area in progress finishes first.'),
      'The audit of acme/shop paused: you paused it. Ask Scout to continue the audit of acme/shop to resume it from the first area not done.',
      {
        kind: 'paused',
        title: 'Audit of acme/shop paused',
        detail: 'Paused by you · area 3 of 8 done · 4 findings so far',
        progress: null,
        control: null,
        note: null,
        offered: 'continue stop',
      },
    ],
    [
      'a stop applied on a running audit keeps Stopping after this area until the notice, then the entry is in Done as stopped with the areas done and the findings kept',
      RUNNING_AUDIT,
      'stop',
      record('applied', STOP_RECORD),
      'The audit of acme/shop ended at your request: 4 of 8 areas done, 5 findings kept on the cards.',
      {
        kind: 'done',
        title: 'Audit of acme/shop stopped',
        detail: '4 of 8 areas · 5 findings kept',
        progress: null,
        control: null,
        note: null,
        offered: '',
      },
    ],
    [
      'a continue applied on a paused audit keeps Continuing until the resume line, then the entry is running with the areas done and the findings so far',
      PAUSED_AUDIT,
      'continue',
      record('applied', 'Resuming the audit of acme/shop from the first area not done.'),
      'Audit of acme/shop resumed: area 3 of 8 done, 4 findings so far.',
      {
        kind: 'running',
        title: 'Audit of acme/shop',
        detail: 'Area 3 of 8 done · 4 findings so far · cards arrive in the chat as each area ends',
        progress: 37,
        control: null,
        note: null,
        offered: 'pause stop',
      },
    ],
    [
      'a stop applied on a paused audit, which no area follows, ends the entry as stopped from the record',
      PAUSED_AUDIT,
      'stop',
      record('applied', STOP_RECORD),
      null,
      {
        kind: 'done',
        title: 'Audit of acme/shop stopped',
        detail: '3 of 8 areas · 4 findings kept',
        progress: null,
        control: null,
        note: null,
        offered: '',
      },
    ],
    [
      'a stop applied on a test run keeps Stopping until the notice, then the run is in Done as stopped with no result',
      RUNNING_TESTS,
      'stop',
      record('applied', 'Stopped the tests of acme/api#41; no result was kept.'),
      'The tests of acme/api#41 ended at your request; no result was kept.',
      {
        kind: 'done',
        title: 'Tests for acme/api#41 stopped',
        detail: 'The tests gave no result.',
        progress: null,
        control: null,
        note: null,
        offered: '',
      },
    ],
    [
      'a click that changed nothing shows the sentence the worker recorded and the controls back',
      RUNNING_AUDIT,
      'pause',
      record(
        'not applied',
        "The audit of acme/shop is already paused: the AI provider's usage limit was reached.",
      ),
      null,
      {
        ...shownOf(RUNNING_AUDIT),
        note: "The audit of acme/shop is already paused: the AI provider's usage limit was reached.",
      },
    ],
    [
      'a click the control plane dropped after two minutes shows that and the controls back',
      RUNNING_TESTS,
      'stop',
      record(
        'expired',
        'Not applied: the click waited more than two minutes for Scout, so it was dropped; click again if it still applies.',
      ),
      null,
      {
        ...shownOf(RUNNING_TESTS),
        note: 'Not applied: the click waited more than two minutes for Scout, so it was dropped; click again if it still applies.',
      },
    ],
    [
      'a click Scout took and did not settle shows not confirmed and the controls back',
      RUNNING_AUDIT,
      'stop',
      record(
        'not confirmed',
        'Not confirmed: Scout took this click but did not say how it ended; look at the job before clicking again.',
      ),
      null,
      {
        ...shownOf(RUNNING_AUDIT),
        note: 'Not confirmed: Scout took this click but did not say how it ended; look at the job before clicking again.',
      },
    ],
  ];
  for (const [name, seed, action, written, said, after] of rows) {
    it(name, async () => {
      const t = setup([seed]);
      t.writesAre(async () => [
        record('applied', 'another click', 'c0'),
        ...(written === null ? [] : [written]),
      ]);
      await t.jobs.control(seed.id, action);

      await t.tick();
      const waiting = shown(t.get(seed.id));
      if (said !== null) t.workerLine(notice(said));

      expect(t.reads).toEqual([50]);
      expect(waiting).toEqual(
        said === null
          ? after
          : { ...shownOf(seed), control: { action, phase: 'waiting' }, offered: '' },
      );
      expect(shown(t.get(seed.id))).toEqual(after);
      expect(t.get(seed.id)?.read).toBe((after as { note: string | null }).note === null);
      expect(t.timers.filter((x) => !x.cancelled).length).toBe(0);
    });
  }

  it(`no record within ${RECORD_WAIT_MS / 1000} seconds says the click was not confirmed and offers the controls again`, async () => {
    const t = setup([RUNNING_AUDIT]);
    await t.jobs.control(RUNNING_AUDIT.id, 'stop');

    while (t.clock.now < 1_000 + RECORD_WAIT_MS - RECORD_POLL_MS) await t.tick();
    const stillWaiting = shown(t.get(RUNNING_AUDIT.id));
    await t.tick();

    expect(stillWaiting).toMatchObject({ control: { action: 'stop', phase: 'waiting' } });
    expect(shown(t.get(RUNNING_AUDIT.id))).toEqual({
      ...shownOf(RUNNING_AUDIT),
      note: NOT_CONFIRMED,
    });
    expect(t.reads.length).toBe(RECORD_WAIT_MS / RECORD_POLL_MS);
  });

  it('a write log that cannot be read is read again until the bound, then the click is not confirmed', async () => {
    const t = setup([RUNNING_TESTS]);
    t.writesAre(async () => {
      throw new ControlPlaneUnavailableError('down');
    });
    await t.jobs.control(RUNNING_TESTS.id, 'stop');

    while (t.clock.now < 1_000 + RECORD_WAIT_MS) await t.tick();

    expect(shown(t.get(RUNNING_TESTS.id))).toEqual({
      ...shownOf(RUNNING_TESTS),
      note: NOT_CONFIRMED,
    });
    expect(t.lines.filter((l) => l.includes('the write log was not read')).length).toBe(
      RECORD_WAIT_MS / RECORD_POLL_MS,
    );
  });

  it('a notice on a job the owner did not click is unread, as every new notice is', () => {
    const t = setup([{ ...RUNNING_AUDIT }]);
    t.store.markRead([RUNNING_AUDIT.id]);

    t.workerLine(
      notice(
        'The audit of acme/shop ended at your request: 4 of 8 areas done, 5 findings kept on the cards.',
      ),
    );

    expect([t.get(RUNNING_AUDIT.id)?.title, t.get(RUNNING_AUDIT.id)?.read]).toEqual([
      'Audit of acme/shop stopped',
      false,
    ]);
  });

  it('the next area of a running audit keeps Stopping after this area, with the progress moved on', async () => {
    const t = setup([RUNNING_AUDIT]);
    t.writesAre(async () => [record('applied', 'Stopped the audit of acme/shop.')]);
    await t.jobs.control(RUNNING_AUDIT.id, 'stop');
    await t.tick();

    t.workerLine(notice('Audit of acme/shop, src/billing (4 of 8, 5 findings so far):'));

    expect(shown(t.get(RUNNING_AUDIT.id))).toEqual({
      kind: 'running',
      title: 'Audit of acme/shop',
      detail: 'Area 4 of 8 done · 5 findings so far · cards arrive in the chat as each area ends',
      progress: 50,
      control: { action: 'stop', phase: 'waiting' },
      note: null,
      offered: '',
    });
  });

  it("the AI Source's pause while a continue waits ends the wait with the AI Source's reason", async () => {
    const t = setup([PAUSED_AUDIT]);
    await t.jobs.control(PAUSED_AUDIT.id, 'continue');

    t.workerLine(
      notice(
        "The audit of acme/shop paused: the AI provider's usage limit was reached. Ask Scout to continue the audit of acme/shop when the AI Source has capacity again.",
      ),
    );

    expect(shown(t.get(PAUSED_AUDIT.id))).toMatchObject({
      kind: 'paused',
      detail: AI_SOURCE_REASON,
      control: null,
      offered: 'continue stop',
    });
    expect(t.timers.filter((x) => !x.cancelled).length).toBe(0);
  });

  it('the worker stopping ends a waiting click with that sentence and the controls back', async () => {
    const t = setup([RUNNING_AUDIT]);
    await t.jobs.control(RUNNING_AUDIT.id, 'pause');

    t.jobs.workerStopped();

    expect(shown(t.get(RUNNING_AUDIT.id))).toEqual({
      ...shownOf(RUNNING_AUDIT),
      note: WORKER_STOPPED,
    });
    expect(t.timers.filter((x) => !x.cancelled).length).toBe(0);
  });
});

function shownOf(n: NotificationInput): Record<string, unknown> {
  return {
    ...(shown({ ...n, at: n.at ?? 0, read: true }) as Record<string, unknown>),
    offered: OFFERED_BEFORE.get(n),
  };
}

describe('Stop pressed on a running audit, then the AI Source pauses the audit while the click waits', () => {
  const STOPPED = {
    kind: 'done',
    title: 'Audit of acme/shop stopped',
    detail: '3 of 8 areas · 4 findings kept',
    progress: null,
    control: null,
    note: null,
    offered: '',
  };

  it('the entry is paused and keeps Stopping, then ends as stopped from the record, not Paused with Continue and Stop', async () => {
    const t = setup([RUNNING_AUDIT]);
    await t.jobs.control(RUNNING_AUDIT.id, 'stop');

    t.workerLine(notice(AI_SOURCE_PAUSED));
    const paused = shown(t.get(RUNNING_AUDIT.id));
    t.writesAre(async () => [record('applied', STOP_RECORD)]);
    await t.tick();

    expect(paused).toEqual({
      kind: 'paused',
      title: 'Audit of acme/shop paused',
      detail: AI_SOURCE_REASON,
      progress: null,
      control: { action: 'stop', phase: 'waiting' },
      note: null,
      offered: '',
    });
    expect(shown(t.get(RUNNING_AUDIT.id))).toEqual(STOPPED);
    expect(t.timers.filter((x) => !x.cancelled).length).toBe(0);
  });

  it('the record says applied before the pause arrives: the entry still ends as stopped', async () => {
    const t = setup([RUNNING_AUDIT]);
    t.writesAre(async () => [record('applied', STOP_RECORD)]);
    await t.jobs.control(RUNNING_AUDIT.id, 'stop');
    await t.tick();

    t.workerLine(notice(AI_SOURCE_PAUSED));
    await t.tick();

    expect(shown(t.get(RUNNING_AUDIT.id))).toEqual(STOPPED);
    expect(t.timers.filter((x) => !x.cancelled).length).toBe(0);
  });

  it('the pause arrives while the control plane is still asked about the Stop: the entry still ends as stopped', async () => {
    const t = setup([RUNNING_AUDIT]);
    let release: (a: JobControlAnswer) => void = () => undefined;
    t.answerWith(() => new Promise((resolve) => (release = resolve)));
    const click = t.jobs.control(RUNNING_AUDIT.id, 'stop');
    await t.flush();

    t.workerLine(notice(AI_SOURCE_PAUSED));
    release({ accepted: true, reason: QUEUED, controlId: 'c1' });
    await click;
    t.writesAre(async () => [record('applied', STOP_RECORD)]);
    await t.tick();

    expect(shown(t.get(RUNNING_AUDIT.id))).toEqual(STOPPED);
  });

  it('the record says the Stop changed nothing: the entry is paused with that sentence and Continue and Stop', async () => {
    const t = setup([RUNNING_AUDIT]);
    await t.jobs.control(RUNNING_AUDIT.id, 'stop');

    t.workerLine(notice(AI_SOURCE_PAUSED));
    t.writesAre(async () => [record('not applied', 'Not applied: the audit had already ended.')]);
    await t.tick();

    expect(shown(t.get(RUNNING_AUDIT.id))).toEqual({
      kind: 'paused',
      title: 'Audit of acme/shop paused',
      detail: AI_SOURCE_REASON,
      progress: null,
      control: null,
      note: 'Not applied: the audit had already ended.',
      offered: 'continue stop',
    });
  });

  it('a Pause that waits is still answered by the pause notice, as before', async () => {
    const t = setup([RUNNING_AUDIT]);
    await t.jobs.control(RUNNING_AUDIT.id, 'pause');

    t.workerLine(notice(AI_SOURCE_PAUSED));

    expect(shown(t.get(RUNNING_AUDIT.id))).toEqual({
      kind: 'paused',
      title: 'Audit of acme/shop paused',
      detail: AI_SOURCE_REASON,
      progress: null,
      control: null,
      note: null,
      offered: 'continue stop',
    });
    expect(t.timers.filter((x) => !x.cancelled).length).toBe(0);
  });
});

describe(`a click recorded as applied whose notice does not come within ${NOTICE_WAIT_MS / 60_000} minutes`, () => {
  for (const [cell, seed, action] of CELLS) {
    if (seed === PAUSED_AUDIT && action === 'stop') continue;
    it(`${cell}: still waiting until the bound, then a note says to look at the job and the controls are offered again`, async () => {
      const t = setup([seed]);
      t.writesAre(async () => [record('applied', 'Applied.')]);
      await t.jobs.control(seed.id, action);
      await t.tick();
      const appliedAt = t.clock.now;

      while (t.clock.now < appliedAt + NOTICE_WAIT_MS - RECORD_POLL_MS) await t.tick();
      const beforeBound = shown(t.get(seed.id));
      await t.tick();

      expect(beforeBound).toEqual({
        ...shownOf(seed),
        control: { action, phase: 'waiting' },
        offered: '',
      });
      expect(shown(t.get(seed.id))).toEqual({ ...shownOf(seed), note: NOT_REPORTED });
      expect(t.timers.filter((x) => !x.cancelled).length).toBe(0);
    });
  }

  it('a notice inside the bound wins and no note is written', async () => {
    const t = setup([RUNNING_AUDIT]);
    t.writesAre(async () => [record('applied', 'Paused the audit of acme/shop.')]);
    await t.jobs.control(RUNNING_AUDIT.id, 'pause');
    await t.tick();
    const appliedAt = t.clock.now;
    while (t.clock.now < appliedAt + NOTICE_WAIT_MS - RECORD_POLL_MS) await t.tick();

    t.workerLine(
      notice(
        'The audit of acme/shop paused: you paused it. Ask Scout to continue the audit of acme/shop to resume it from the first area not done.',
      ),
    );
    await t.tick();
    await t.tick();

    expect(shown(t.get(RUNNING_AUDIT.id))).toEqual({
      kind: 'paused',
      title: 'Audit of acme/shop paused',
      detail: 'Paused by you · area 3 of 8 done · 4 findings so far',
      progress: null,
      control: null,
      note: null,
      offered: 'continue stop',
    });
    expect(t.timers.filter((x) => !x.cancelled).length).toBe(0);
  });
});

describe('a test run in the bell', () => {
  const progress = (id: string, text: string): string =>
    JSON.stringify({ id, event: 'progress', text });
  const end = (id: string, event: 'complete' | 'fail'): string =>
    JSON.stringify({ id, event, text: 'done' });

  const rows: readonly [string, readonly string[], unknown][] = [
    [
      "the local turn's progress opens a running entry with Stop, keyed by owner, repo and number without case",
      [progress('q1', 'Run pull request tests: Acme API 41')],
      {
        id: 'tests:acme/api#41',
        kind: 'running',
        title: 'Tests for Acme/API#41',
        detail: TESTS_RUNNING,
        offered: 'stop',
      },
    ],
    [
      'the turn completing ends it as finished, never as passed',
      [progress('q1', 'Run pull request tests: acme api 41'), end('q1', 'complete')],
      {
        id: 'tests:acme/api#41',
        kind: 'done',
        title: 'Tests for acme/api#41 finished',
        detail: TESTS_FINISHED,
        offered: '',
      },
    ],
    [
      'the turn failing ends it as not finished',
      [progress('q1', 'Run pull request tests: acme api 41'), end('q1', 'fail')],
      {
        id: 'tests:acme/api#41',
        kind: 'failed',
        title: 'Tests for acme/api#41 did not finish',
        detail: TESTS_ANSWER_FAILED,
        offered: '',
      },
    ],
    [
      'the stop notice before the turn ends wins over the turn completing',
      [
        progress('q1', 'Run pull request tests: acme api 41'),
        notice('The tests of acme/api#41 ended at your request; no result was kept.'),
        end('q1', 'complete'),
      ],
      {
        id: 'tests:acme/api#41',
        kind: 'done',
        title: 'Tests for acme/api#41 stopped',
        detail: 'The tests gave no result.',
        offered: '',
      },
    ],
    [
      'the stop notice after the turn completed still says stopped',
      [
        progress('q1', 'Run pull request tests: acme api 41'),
        end('q1', 'complete'),
        notice('The tests of acme/api#41 ended at your request; no result was kept.'),
      ],
      {
        id: 'tests:acme/api#41',
        kind: 'done',
        title: 'Tests for acme/api#41 stopped',
        detail: 'The tests gave no result.',
        offered: '',
      },
    ],
    [
      'two test runs of the same pull request share one entry: the first turn ending leaves it running while the second still runs',
      [
        progress('q1', 'Run pull request tests: acme api 41'),
        progress('q2', 'Run pull request tests: acme api 41'),
        end('q1', 'complete'),
      ],
      {
        id: 'tests:acme/api#41',
        kind: 'running',
        title: 'Tests for acme/api#41',
        detail: TESTS_RUNNING,
        offered: 'stop',
      },
    ],
    [
      'two test runs of the same pull request share one entry: the last turn failing closes it as not finished',
      [
        progress('q1', 'Run pull request tests: acme api 41'),
        progress('q2', 'Run pull request tests: acme api 41'),
        end('q1', 'complete'),
        end('q2', 'fail'),
      ],
      {
        id: 'tests:acme/api#41',
        kind: 'failed',
        title: 'Tests for acme/api#41 did not finish',
        detail: TESTS_ANSWER_FAILED,
        offered: '',
      },
    ],
    [
      'two test runs of the same pull request share one entry: the last turn completing closes it as finished after the first failed',
      [
        progress('q1', 'Run pull request tests: acme api 41'),
        progress('q2', 'Run pull request tests: acme api 41'),
        end('q1', 'fail'),
        end('q2', 'complete'),
      ],
      {
        id: 'tests:acme/api#41',
        kind: 'done',
        title: 'Tests for acme/api#41 finished',
        detail: TESTS_FINISHED,
        offered: '',
      },
    ],
    [
      'another turn ending leaves the run running',
      [progress('q1', 'Run pull request tests: acme api 41'), end('q2', 'complete')],
      {
        id: 'tests:acme/api#41',
        kind: 'running',
        title: 'Tests for acme/api#41',
        detail: TESTS_RUNNING,
        offered: 'stop',
      },
    ],
  ];
  for (const [name, lines, after] of rows) {
    it(name, () => {
      const t = setup();
      for (const line of lines) t.workerLine(line);

      const n = t.store.list()[0];
      expect(
        n === undefined
          ? null
          : {
              id: n.id,
              kind: n.kind,
              title: n.title,
              detail: n.detail,
              offered: jobControls(n).join(' '),
            },
      ).toEqual(after as never);
    });
  }

  it('progress of another function, arguments in another order, a notice with a turn id or a broken line opens no entry', () => {
    const t = setup();

    for (const line of [
      progress('q1', 'Get pull request: acme api 41'),
      progress('q1', 'Run pull request tests: 41 acme api'),
      progress('q1', 'Run pull request tests: acme/api 41'),
      JSON.stringify({ id: 'q1', event: 'notice', text: 'Run pull request tests: acme api 41' }),
      '{broken',
    ]) {
      t.workerLine(line);
    }

    expect(t.store.list()).toEqual([]);
  });

  it('the worker stopping ends every running test run as not finished, and leaves a finished one as it was', () => {
    const t = setup();
    t.workerLine(progress('q1', 'Run pull request tests: acme api 41'));
    t.workerLine(progress('q2', 'Run pull request tests: acme web 7'));
    t.workerLine(end('q2', 'complete'));

    t.jobs.workerStopped();
    t.workerLine(end('q1', 'complete'));

    expect(t.store.list().map((n) => [n.id, n.kind, n.title, n.detail])).toEqual([
      ['tests:acme/api#41', 'failed', 'Tests for acme/api#41 did not finish', TESTS_WORKER_STOPPED],
      ['tests:acme/web#7', 'done', 'Tests for acme/web#7 finished', TESTS_FINISHED],
    ]);
  });
});

describe('when a job entry the owner has read becomes unread again, and the badge with it', () => {
  type Setup = ReturnType<typeof setup>;
  const testsProgress = JSON.stringify({
    id: 'q1',
    event: 'progress',
    text: 'Run pull request tests: acme api 41',
  });
  const ASK = 'Ask Scout to continue the audit of acme/shop when the AI Source has capacity again.';
  const aiSourcePause = (reason: string) => async (t: Setup) =>
    t.workerLine(notice(`The audit of acme/shop paused: ${reason}. ${ASK}`));
  const clicked =
    (action: JobControlAction, written: WriteLogEntryDto, said: string | null) =>
    async (t: Setup, id: string) => {
      t.writesAre(async () => [written]);
      await t.jobs.control(id, action);
      await t.tick();
      if (said !== null) t.workerLine(notice(said));
    };

  type Row = readonly [
    string,
    'audit' | 'paused' | 'tests',
    (t: Setup, id: string) => Promise<void>,
    { read: boolean; kind: NotificationKind; title: string; detail: string },
  ];
  const rows: readonly Row[] = [
    [
      'an area ends',
      'audit',
      async (t) =>
        t.workerLine(notice('Audit of acme/shop, src/billing (4 of 8, 5 findings so far):')),
      {
        read: true,
        kind: 'running',
        title: 'Audit of acme/shop',
        detail: 'Area 4 of 8 done · 5 findings so far · cards arrive in the chat as each area ends',
      },
    ],
    [
      'paused by you from the chat',
      'audit',
      async (t) =>
        t.workerLine(
          notice(
            'The audit of acme/shop paused: you paused it. Ask Scout to continue the audit of acme/shop to resume it from the first area not done.',
          ),
        ),
      {
        read: false,
        kind: 'paused',
        title: 'Audit of acme/shop paused',
        detail: 'Paused by you · area 3 of 8 done · 4 findings so far',
      },
    ],
    ...(
      [
        [
          'the usage limit',
          "the AI provider's usage limit was reached",
          'The AI Source reached its usage limit. Ask Scout to continue when it has capacity again.',
        ],
        [
          'an account out of credit',
          "the AI provider's account has run out of credit",
          "The AI Source's account has run out of credit. Ask Scout to continue once it has credit again.",
        ],
        [
          'the rate limit',
          "the AI provider's rate limit was reached",
          'The AI Source reached its rate limit. Ask Scout to continue when it has capacity again.',
        ],
        [
          'a sign-in',
          'the AI provider needs the owner to sign in again',
          'The AI Source needs you to sign in again. Sign in from AI Sources, then ask Scout to continue.',
        ],
        [
          'a rejected key',
          'the AI Source rejected its key',
          'The AI Source rejected its key. Give it a working key in AI Sources, then ask Scout to continue.',
        ],
        [
          'a missing model',
          'the AI Source does not have the model llama3.2:3b',
          'The AI Source does not have the model llama3.2:3b. Choose a model it has in Models, then ask Scout to continue.',
        ],
      ] as const
    ).map(([name, reason, detail]): Row => [
      `paused by the AI Source for ${name}`,
      'audit',
      aiSourcePause(reason),
      { read: false, kind: 'paused', title: 'Audit of acme/shop paused', detail },
    ]),
    [
      'his Continue applied, then the next area',
      'paused',
      clicked(
        'continue',
        record('applied', 'Resuming the audit of acme/shop from the first area not done.'),
        'Audit of acme/shop, src/billing (4 of 8, 5 findings so far):',
      ),
      {
        read: true,
        kind: 'running',
        title: 'Audit of acme/shop',
        detail: 'Area 4 of 8 done · 5 findings so far · cards arrive in the chat as each area ends',
      },
    ],
    [
      'his Stop on the running audit, then the stop notice',
      'audit',
      clicked(
        'stop',
        record('applied', STOP_RECORD),
        'The audit of acme/shop ended at your request: 4 of 8 areas done, 5 findings kept on the cards.',
      ),
      {
        read: true,
        kind: 'done',
        title: 'Audit of acme/shop stopped',
        detail: '4 of 8 areas · 5 findings kept',
      },
    ],
    [
      'his Stop on the paused audit, confirmed by the record',
      'paused',
      clicked('stop', record('applied', STOP_RECORD), null),
      {
        read: true,
        kind: 'done',
        title: 'Audit of acme/shop stopped',
        detail: '3 of 8 areas · 4 findings kept',
      },
    ],
    [
      'stopped from the chat',
      'audit',
      async (t) =>
        t.workerLine(
          notice(
            'The audit of acme/shop ended at your request: 4 of 8 areas done, 5 findings kept on the cards.',
          ),
        ),
      {
        read: false,
        kind: 'done',
        title: 'Audit of acme/shop stopped',
        detail: '4 of 8 areas · 5 findings kept',
      },
    ],
    [
      'finished',
      'audit',
      async (t) =>
        t.workerLine(
          notice('Audit of acme/shop at 3f2a9c1 is done. Verdict: fix (0 High, 2 Medium, 1 Low).'),
        ),
      {
        read: false,
        kind: 'done',
        title: 'Audit of acme/shop is done',
        detail: 'Verdict: fix (0 High, 2 Medium, 1 Low).',
      },
    ],
    [
      'the test run finished',
      'tests',
      async (t) => t.workerLine(JSON.stringify({ id: 'q1', event: 'complete', text: 'done' })),
      {
        read: false,
        kind: 'done',
        title: 'Tests for acme/api#41 finished',
        detail: TESTS_FINISHED,
      },
    ],
    [
      'the test run failed',
      'tests',
      async (t) => t.workerLine(JSON.stringify({ id: 'q1', event: 'fail', text: 'done' })),
      {
        read: false,
        kind: 'failed',
        title: 'Tests for acme/api#41 did not finish',
        detail: TESTS_ANSWER_FAILED,
      },
    ],
    [
      'his Stop on the test run, then the stop notice',
      'tests',
      clicked(
        'stop',
        record('applied', 'Stopped the tests of acme/api#41; no result was kept.'),
        'The tests of acme/api#41 ended at your request; no result was kept.',
      ),
      {
        read: true,
        kind: 'done',
        title: 'Tests for acme/api#41 stopped',
        detail: 'The tests gave no result.',
      },
    ],
    [
      'a note: his Pause recorded as not confirmed',
      'audit',
      clicked(
        'pause',
        record(
          'not confirmed',
          'Not confirmed: Scout took this click but did not say how it ended.',
        ),
        null,
      ),
      {
        read: false,
        kind: 'running',
        title: 'Audit of acme/shop',
        detail: 'Area 3 of 8 done · 4 findings so far · cards arrive in the chat as each area ends',
      },
    ],
    [
      'a note: his Stop refused by the control plane',
      'tests',
      async (t, id) => {
        t.answerWith(async () => ({ accepted: false, reason: BAD_REPOSITORY, controlId: null }));
        await t.jobs.control(id, 'stop');
      },
      {
        read: false,
        kind: 'running',
        title: 'Tests for acme/api#41',
        detail: TESTS_RUNNING,
      },
    ],
  ];

  for (const [event, start, act, after] of rows) {
    it(`a ${start === 'tests' ? 'running test run' : start === 'paused' ? 'paused audit' : 'running audit'}, read, then ${event}: ${after.read ? 'still read' : 'unread'}`, async () => {
      const t = setup(
        start === 'audit' ? [RUNNING_AUDIT] : start === 'paused' ? [PAUSED_AUDIT] : [],
      );
      if (start === 'tests') t.workerLine(testsProgress);
      const id = start === 'tests' ? RUNNING_TESTS.id : RUNNING_AUDIT.id;
      t.store.markRead();

      await act(t, id);

      const n = t.get(id)!;
      expect({
        read: n.read,
        badge: t.store.list().filter((x) => !x.read).length,
        kind: n.kind,
        title: n.title,
        detail: n.detail,
      }).toEqual({ ...after, badge: after.read ? 0 : 1 });
    });
  }
});

describe(`${CHANNEL.notificationsControl}`, () => {
  type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

  function withIpc(seed: readonly NotificationInput[]) {
    const t = setup(seed);
    const handlers = new Map<string, IpcHandler>();
    const ipc: Pick<IpcMain, 'handle' | 'removeHandler'> = {
      handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
      removeHandler: (channel: string) => void handlers.delete(channel),
    };
    const dispose = registerJobControlHandler(ipc, t.jobs, SENDER_HOOKS, recordingLogger().logger);
    const invoke = (event: IpcMainInvokeEvent, arg: unknown): Promise<unknown> =>
      Promise.resolve().then(() => handlers.get(CHANNEL.notificationsControl)!(event, arg));
    return { ...t, handlers, dispose, invoke };
  }

  async function refusal(promise: Promise<unknown>): Promise<string> {
    try {
      await promise;
    } catch (err: unknown) {
      const e = readIpcError(err);
      return `${e.code}: ${e.message}`;
    }
    throw new Error('expected a refusal');
  }

  for (const [from, event, kind] of SENDERS) {
    if (kind === 'main' || kind === 'operator') {
      it(`a click from ${from}, a window that shows the bell, reaches the control plane once`, async () => {
        const t = withIpc([RUNNING_AUDIT]);

        await t.invoke(event, { id: RUNNING_AUDIT.id, action: 'pause' });

        expect(t.asked).toEqual([{ kind: 'audit', owner: 'acme', repo: 'shop', action: 'pause' }]);
      });
    } else {
      it(`a click from ${from} is refused with ipc/not-bell-window and sends nothing`, async () => {
        const t = withIpc([RUNNING_AUDIT]);

        const refused = await refusal(t.invoke(event, { id: RUNNING_AUDIT.id, action: 'pause' }));

        expect(refused).toBe(
          'ipc/not-bell-window: Jobs can be paused, continued or stopped only from the bell of the Scout window or the operator window.',
        );
        expect(t.asked).toEqual([]);
        expect(shown(t.get(RUNNING_AUDIT.id))).toMatchObject({ control: null });
      });
    }
  }

  const forged: readonly [string, unknown][] = [
    ['an entry the bell does not hold', { id: 'audit:acme/other', action: 'pause' }],
    ['an id that names a job the bell does not show', { id: 'tests:acme/api#42', action: 'stop' }],
    ['a pause on a test run', { id: RUNNING_TESTS.id, action: 'pause' }],
    ['a continue on a running audit', { id: RUNNING_AUDIT.id, action: 'continue' }],
    ['a stop on a verdict entry', { id: 'verdict:a1', action: 'stop' }],
    ['an action Scout does not know', { id: RUNNING_AUDIT.id, action: 'delete' }],
    ['no id', { action: 'stop' }],
    ['no request at all', null],
  ];
  for (const [name, request] of forged) {
    it(`${name} is refused and sends nothing`, async () => {
      const t = withIpc([
        RUNNING_AUDIT,
        RUNNING_TESTS,
        { id: 'verdict:a1', kind: 'running', title: 'Verdict', detail: '', source: 'verdict' },
      ]);

      const refused = await refusal(t.invoke(MAIN_EVENT, request));

      expect(refused).toBe('notifications/invalid-control: Scout could not send that control.');
      expect(t.asked).toEqual([]);
    });
  }

  it('a target the renderer adds is ignored: main sends the job its own entry names', async () => {
    const t = withIpc([RUNNING_TESTS]);

    await t.invoke(MAIN_EVENT, {
      id: RUNNING_TESTS.id,
      action: 'stop',
      kind: 'audit',
      owner: 'someone',
      repo: 'else',
      number: 7,
    });

    expect(t.asked).toEqual([
      { kind: 'tests', owner: 'acme', repo: 'api', number: 41, action: 'stop' },
    ]);
  });

  it('dispose removes the channel', () => {
    const t = withIpc([]);

    t.dispose();

    expect([...t.handlers.keys()]).toEqual([]);
  });
});

describe('main.ts wiring of the job controls', () => {
  it('every worker line and notice goes through the job controls, a worker that stops or crashes ends their waits, and the guarded channel is registered and removed', async () => {
    const { readFile } = await import('node:fs/promises');
    const path = await import('node:path');
    const main = await readFile(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');
    const localEvent = main.slice(main.indexOf('onLocalEvent: (json) => {'));
    const body = localEvent.slice(0, localEvent.indexOf('},'));

    expect(body).toContain('jobs.handleWorkerLine(json);');
    expect(body).toContain('if (notice !== null) jobs.notice(notice);');
    expect(body).not.toContain('notifications.record(notice)');
    expect(main).toMatch(
      /if \(event\.status\.state === 'crashed' \|\| event\.status\.state === 'stopped'\) \{\s*jobs\.workerStopped\(\);/,
    );
    expect(main).toMatch(/registerJobControlHandler\(\s*ipcMain,\s*jobs,\s*senders,/);
    expect(main).toMatch(/client: \(\) => connection\.client,/);
    expect(main).toContain('unregisterJobControls?.();');
    expect(main).toContain('jobControls?.dispose();');
  });
});
