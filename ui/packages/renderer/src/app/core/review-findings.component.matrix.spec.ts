import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ISSUES_MARKER,
  REVIEW_MARKER,
  VERDICT_MARKER,
  type LocalAskRequest,
  type LocalTurn,
  type ReviewCheck,
  type ReviewFinding,
  type ReviewFindings,
  type ReviewTests,
} from '@scout/shared';

import { ReviewFindingsComponent } from './review-findings.component';
import { ScoutBridgeService } from './scout-bridge';

const PR_URL = 'https://github.com/acme/shop/pull/5#pullrequestreview-77';
const ISSUE_URL = 'https://github.com/acme/shop/issues/12';

const FINDINGS: readonly ReviewFinding[] = [
  {
    id: 'total',
    severity: 'block',
    title: 'Discounts applied after the sum',
    path: 'src/orders/Checkout.cs',
    line: 88,
    why: 'The total is summed before the discount loop runs.',
    fix: 'Apply the discounts before the sum.',
  },
  {
    id: 'lock',
    severity: 'fix',
    title: 'Move has no lock',
    path: 'src/orders/Cart.cs',
    line: 12,
    why: 'Two moves at once lose an item.',
    fix: 'Take the cart lock.',
  },
  {
    id: 'naming',
    severity: 'nit',
    title: 'Name says the wrong thing',
    path: 'src/orders/Cart.cs',
    line: 30,
    why: 'items holds ids.',
    fix: 'Rename it itemIds.',
  },
];

type Kind = 'review' | 'audit';
type Count = 1 | 3;
type Decisions =
  'all undecided' | 'all Yes' | 'all No' | 'mixed Yes/No/undecided' | 'one edited Yes';
type Phase = 'before posting' | 'posting' | 'posted' | 'failed' | 'done without link';
type Window = 'this window' | 'the other window';
type Footer = 'footer' | 'all-dismissed' | 'posted';

const KINDS: readonly Kind[] = ['review', 'audit'];
const COUNTS: readonly Count[] = [1, 3];
const DECISIONS: readonly Decisions[] = [
  'all undecided',
  'all Yes',
  'all No',
  'mixed Yes/No/undecided',
  'one edited Yes',
];
const PHASES: readonly Phase[] = [
  'before posting',
  'posting',
  'posted',
  'failed',
  'done without link',
];
const WINDOWS: readonly Window[] = ['this window', 'the other window'];

const PICKS: Readonly<
  Record<Decisions, Readonly<Record<Count, readonly ('yes' | 'no' | 'edit' | null)[]>>>
> = {
  'all undecided': { 1: [null], 3: [null, null, null] },
  'all Yes': { 1: ['yes'], 3: ['yes', 'yes', 'yes'] },
  'all No': { 1: ['no'], 3: ['no', 'no', 'no'] },
  'mixed Yes/No/undecided': { 1: [], 3: ['yes', 'no', null] },
  'one edited Yes': { 1: ['edit'], 3: ['edit', null, null] },
};

const DISMISSED: Readonly<Record<Decisions, Readonly<Record<Count, readonly boolean[]>>>> = {
  'all undecided': { 1: [false], 3: [false, false, false] },
  'all Yes': { 1: [false], 3: [false, false, false] },
  'all No': { 1: [true], 3: [true, true, true] },
  'mixed Yes/No/undecided': { 1: [], 3: [false, true, false] },
  'one edited Yes': { 1: [false], 3: [false, false, false] },
};

const CHOSEN: Readonly<Record<Decisions, Readonly<Record<Count, number>>>> = {
  'all undecided': { 1: 0, 3: 0 },
  'all Yes': { 1: 1, 3: 3 },
  'all No': { 1: 0, 3: 0 },
  'mixed Yes/No/undecided': { 1: 0, 3: 1 },
  'one edited Yes': { 1: 1, 3: 1 },
};

const SUMMARY: Readonly<Record<Decisions, Readonly<Record<Count, string | null>>>> = {
  'all undecided': {
    1: 'Nothing selected yet. Decide item by item, or Yes to all.',
    3: 'Nothing selected yet. Decide item by item, or Yes to all.',
  },
  'all Yes': { 1: '1 of 1 selected', 3: '3 of 3 selected' },
  'all No': { 1: null, 3: null },
  'mixed Yes/No/undecided': { 1: null, 3: '1 of 2 selected · 1 undecided' },
  'one edited Yes': { 1: '1 of 1 selected', 3: '1 of 3 selected · 2 undecided' },
};

const POST_LABEL: Readonly<Record<Kind, Readonly<Record<number, string>>>> = {
  review: { 0: 'Post on PR #5', 1: 'Post 1 on PR #5', 3: 'Post 3 on PR #5' },
  audit: {
    0: 'Open issues on acme/shop',
    1: 'Open 1 issue on acme/shop',
    3: 'Open 3 issues on acme/shop',
  },
};

const SENT_QUESTION: Readonly<Record<Kind, Readonly<Record<number, string>>>> = {
  review: { 1: 'Post 1 finding on acme/shop#5', 3: 'Post 3 findings on acme/shop#5' },
  audit: { 1: 'Open 1 issue on acme/shop', 3: 'Open 3 issues on acme/shop' },
};

const POSTED_TITLE: Readonly<Record<Kind, string>> = {
  review: 'Review posted on PR #5',
  audit: '1 issue opened on acme/shop',
};

const POSTED_COMMENTS: Readonly<Record<Window, (chosen: number) => string>> = {
  'this window': (chosen) => `${chosen} ${chosen === 1 ? 'comment' : 'comments'} in one review`,
  'the other window': () => '1 comment in one review',
};

interface Row {
  readonly kind: Kind;
  readonly count: Count;
  readonly decisions: Decisions;
  readonly phase: Phase;
  readonly window: Window;
}

function possible(row: Row): boolean {
  if (PICKS[row.decisions][row.count].length !== row.count) return false;
  if (row.phase === 'before posting') return row.window === 'this window';
  return row.window === 'the other window' || CHOSEN[row.decisions][row.count] > 0;
}

const ROWS: readonly Row[] = KINDS.flatMap((kind) =>
  COUNTS.flatMap((count) =>
    DECISIONS.flatMap((decisions) =>
      PHASES.flatMap((phase) =>
        WINDOWS.map((window) => ({ kind, count, decisions, phase, window })),
      ),
    ),
  ),
).filter(possible);

function makeBridge() {
  let turns: readonly LocalTurn[] = [
    { id: 'f-1', question: 'review acme/shop', reply: 'the review', citations: [], state: 'done' },
  ];
  const listeners: ((turns: readonly LocalTurn[]) => void)[] = [];
  const push = (next: readonly LocalTurn[]) => {
    turns = next;
    listeners.forEach((listener) => listener(turns));
  };
  return {
    push,
    turns: () => turns,
    end: (id: string, state: 'done' | 'failed', reply: string) =>
      push(turns.map((turn) => (turn.id === id ? { ...turn, state, reply } : turn))),
    onLocalTurns: vi.fn((listener: (turns: readonly LocalTurn[]) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
    localGetTurns: vi.fn(async (): Promise<readonly LocalTurn[]> => turns),
    localAsk: vi.fn(async (req: LocalAskRequest): Promise<void> => {
      const text = 'text' in req ? req.text : '';
      const review = text.indexOf(REVIEW_MARKER);
      const issues = text.indexOf(ISSUES_MARKER);
      const verdict = text.indexOf(VERDICT_MARKER);
      const cut = [review, issues, verdict].find((at) => at !== -1) ?? -1;
      push([
        ...turns,
        {
          id: req.id,
          question: cut === -1 ? text : text.slice(0, cut),
          reply: '',
          citations: [],
          state: 'running',
          ...(review === -1 ? {} : { review: true as const }),
          ...(issues === -1 ? {} : { issues: true as const }),
        },
      ]);
    }),
    localCancel: vi.fn(async (): Promise<void> => undefined),
    localClearTurns: vi.fn(async (): Promise<void> => undefined),
    appOpenExternal: vi.fn(async (): Promise<void> => undefined),
  };
}

type Fixture = ComponentFixture<ReviewFindingsComponent>;

async function settle(fixture: Fixture): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function all(root: Element, test: string): HTMLElement[] {
  return Array.from(root.querySelectorAll(`[data-test="${test}"]`));
}

function text(node: Element | null | undefined): string | null {
  return node === null || node === undefined
    ? null
    : (node.textContent ?? '').replace(/\s+/g, ' ').trim();
}

async function click(fixture: Fixture, node: Element | null | undefined): Promise<void> {
  (node as HTMLElement).click();
  await settle(fixture);
}

function findingsOf(kind: Kind, count: Count): ReviewFindings {
  const findings = FINDINGS.slice(0, count);
  return kind === 'audit'
    ? { kind: 'audit', repository: 'acme/shop', commit: 'ec7f951', verdict: 'v', findings }
    : { repository: 'acme/shop', number: 5, verdict: 'v', findings };
}

function otherWindowTurn(kind: Kind, state: LocalTurn['state'], reply: string): LocalTurn {
  return kind === 'audit'
    ? {
        id: 'elsewhere',
        question: 'Open 1 issue on acme/shop',
        reply,
        citations: [],
        state,
        issues: true,
      }
    : {
        id: 'elsewhere',
        question: 'Post 1 finding on acme/shop#5',
        reply,
        citations: [],
        state,
        review: true,
      };
}

const LINK: Readonly<Record<Kind, string>> = { review: PR_URL, audit: ISSUE_URL };

describe(`review findings matrix (${ROWS.length} rows)`, () => {
  beforeEach(() => TestBed.resetTestingModule());

  it.each(
    ROWS.map(
      (row) =>
        [
          `${row.kind}, ${row.count} finding${row.count === 1 ? '' : 's'}, ${row.decisions}, ${row.phase}${row.phase === 'before posting' ? '' : `, in ${row.window}`}`,
          row,
        ] as const,
    ),
  )('%s', async (_name, row) => {
    const bridge = makeBridge();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      ],
    });
    const fixture = TestBed.createComponent(ReviewFindingsComponent);
    fixture.componentRef.setInput('turnId', 'f-1');
    fixture.componentRef.setInput('review', findingsOf(row.kind, row.count));
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const cards = () => all(root, 'finding');

    for (const [i, pick] of PICKS[row.decisions][row.count].entries()) {
      if (pick === 'yes') await click(fixture, all(cards()[i]!, 'yes')[0]);
      if (pick === 'no') await click(fixture, all(cards()[i]!, 'no')[0]);
      if (pick === 'edit') {
        await click(fixture, all(cards()[i]!, 'edit')[0]);
        const editor = all(root, 'comment-text')[0] as HTMLTextAreaElement;
        editor.value = 'Edited by the owner';
        editor.dispatchEvent(new Event('input'));
        await settle(fixture);
        await click(fixture, all(root, 'use-text')[0]);
      }
    }

    if (row.phase !== 'before posting') {
      if (row.window === 'this window') {
        await click(fixture, all(root, 'post')[0]);
        const id = (bridge.localAsk.mock.lastCall![0] as { id: string }).id;
        if (row.phase === 'posted') bridge.end(id, 'done', `Done: ${LINK[row.kind]}`);
        if (row.phase === 'failed') bridge.end(id, 'failed', 'GitHub refused it.');
        if (row.phase === 'done without link') bridge.end(id, 'done', 'Writes are not allowed.');
      } else {
        const state =
          row.phase === 'posting' ? 'running' : row.phase === 'failed' ? 'failed' : 'done';
        const reply =
          row.phase === 'posted'
            ? `Done: ${LINK[row.kind]}`
            : row.phase === 'failed'
              ? 'GitHub refused it.'
              : row.phase === 'done without link'
                ? 'Writes are not allowed.'
                : '';
        bridge.push([...bridge.turns(), otherWindowTurn(row.kind, state, reply)]);
      }
      await settle(fixture);
    }

    const chosen = CHOSEN[row.decisions][row.count];
    const footer: Footer =
      row.phase === 'posted' ? 'posted' : row.decisions === 'all No' ? 'all-dismissed' : 'footer';
    const postEnabled = row.phase !== 'posting' && chosen > 0;
    const posted = all(root, 'posted')[0];

    expect(cards().map((card) => all(card, 'dismissed').length === 1)).toEqual(
      DISMISSED[row.decisions][row.count],
    );
    expect(cards().map((card) => all(card, 'undo').length)).toEqual(
      DISMISSED[row.decisions][row.count].map((line) => (line && footer !== 'posted' ? 1 : 0)),
    );
    expect(cards().map((card) => all(card, 'yes').length)).toEqual(
      DISMISSED[row.decisions][row.count].map((line) => (line || footer === 'posted' ? 0 : 1)),
    );
    expect({
      footer: all(root, 'summary').length === 1,
      allDismissed: text(all(root, 'all-dismissed')[0]),
      posted: posted !== undefined,
    }).toEqual({
      footer: footer === 'footer',
      allDismissed:
        footer === 'all-dismissed'
          ? row.kind === 'audit'
            ? 'Nothing to open; every finding was dismissed.'
            : 'Nothing to post; every finding was dismissed.'
          : null,
      posted: footer === 'posted',
    });
    if (footer === 'footer') {
      const post = all(root, 'post')[0] as HTMLButtonElement;
      expect({
        summary: text(all(root, 'summary')[0]),
        label: text(post),
        enabled: !post.disabled,
        yesAll: all(root, 'yes-all').length,
        preview: all(root, 'toggle-preview').length,
      }).toEqual({
        summary: SUMMARY[row.decisions][row.count],
        label: POST_LABEL[row.kind][chosen],
        enabled: postEnabled,
        yesAll: 1,
        preview: 1,
      });
    }
    if (footer === 'posted') {
      expect({
        title: text(posted!.querySelector('strong')),
        links: Array.from(posted!.querySelectorAll('a')).map((a) => text(a)),
      }).toEqual({ title: POSTED_TITLE[row.kind], links: [LINK[row.kind]] });
      if (row.kind === 'review') {
        expect(text(posted)).toContain(POSTED_COMMENTS[row.window](chosen));
      }
    }
    const sent = bridge.localAsk.mock.calls.map(([req]) => ('text' in req ? req.text : ''));
    if (row.window === 'this window' && row.phase !== 'before posting') {
      const marker = row.kind === 'audit' ? ISSUES_MARKER : REVIEW_MARKER;
      const [question, json] = sent[0]!.split(marker);
      expect(sent).toHaveLength(1);
      expect(question).toBe(SENT_QUESTION[row.kind][chosen]);
      expect(json!.includes('Edited by the owner')).toBe(row.decisions === 'one edited Yes');
    } else {
      expect(sent).toEqual([]);
    }
  });
});

type ChecksCase = 'absent' | 'all pass' | 'one failing' | 'pending';
type TestsCase = 'absent' | 'ran pass' | 'ran fail' | 'not ran';
type VerdictPhase = 'none' | 'giving' | 'given' | 'failed';
type VerdictButton = 'Approve' | 'Request changes';

const CHECKS_CASES: readonly ChecksCase[] = ['absent', 'all pass', 'one failing', 'pending'];
const TESTS_CASES: readonly TestsCase[] = ['absent', 'ran pass', 'ran fail', 'not ran'];
const VERDICT_PHASES: readonly VerdictPhase[] = ['none', 'giving', 'given', 'failed'];
const VERDICT_BUTTONS: readonly VerdictButton[] = ['Approve', 'Request changes'];

const BUILD: ReviewCheck = {
  name: 'build',
  status: 'completed',
  conclusion: 'success',
  url: 'https://github.com/acme/shop/runs/1',
};
const CHECKS: Readonly<Record<ChecksCase, readonly ReviewCheck[] | undefined>> = {
  absent: undefined,
  'all pass': [BUILD, { ...BUILD, name: 'lint' }],
  'one failing': [BUILD, { ...BUILD, name: 'lint', conclusion: 'failure' }],
  pending: [BUILD, { ...BUILD, name: 'lint', status: 'in_progress', conclusion: null }],
};
const MARKS: Readonly<Record<ChecksCase, readonly string[]>> = {
  absent: [],
  'all pass': ['Passed', 'Passed'],
  'one failing': ['Passed', 'Failed'],
  pending: ['Passed', 'Pending'],
};
const TESTS: Readonly<Record<TestsCase, ReviewTests | undefined>> = {
  absent: undefined,
  'ran pass': { ran: true, command: 'dotnet test', exitCode: 0, durationSeconds: 42 },
  'ran fail': {
    ran: true,
    command: 'dotnet test',
    exitCode: 1,
    durationSeconds: 42,
    output: '1 failing',
  },
  'not ran': { ran: false, reason: 'Docker is not installed.' },
};
const RUN_LINE: Readonly<Record<TestsCase, string | null>> = {
  absent: null,
  'ran pass': 'Tests passed in 42s (dotnet test)',
  'ran fail': 'Tests failed (exit 1)',
  'not ran': 'Tests did not run: Docker is not installed.',
};
const QUESTION: Readonly<Record<VerdictButton, string>> = {
  Approve: 'Approve acme/shop#5',
  'Request changes': 'Request changes on acme/shop#5',
};
const GIVEN_TITLE: Readonly<Record<VerdictButton, string>> = {
  Approve: 'Approved on PR #5',
  'Request changes': 'Changes requested on PR #5',
};
const BUTTON_TEST: Readonly<Record<VerdictButton, string>> = {
  Approve: 'approve',
  'Request changes': 'request-changes',
};

interface VerdictRow {
  readonly kind: Kind;
  readonly checks: ChecksCase;
  readonly tests: TestsCase;
  readonly phase: VerdictPhase;
  readonly button: VerdictButton;
  readonly selfAuthored: boolean;
  readonly window: Window;
}

function possibleVerdict(row: VerdictRow): boolean {
  if (row.kind === 'audit') {
    return (
      row.checks === 'absent' &&
      row.tests === 'absent' &&
      row.phase === 'none' &&
      !row.selfAuthored &&
      row.button === 'Approve' &&
      row.window === 'this window'
    );
  }
  if (row.phase === 'none') return row.window === 'this window' && row.button === 'Approve';
  return !(row.selfAuthored && row.button === 'Approve');
}

const VERDICT_ROWS: readonly VerdictRow[] = KINDS.flatMap((kind) =>
  CHECKS_CASES.flatMap((checks) =>
    TESTS_CASES.flatMap((tests) =>
      VERDICT_PHASES.flatMap((phase) =>
        VERDICT_BUTTONS.flatMap((button) =>
          [false, true].flatMap((selfAuthored) =>
            WINDOWS.map((window) => ({
              kind,
              checks,
              tests,
              phase,
              button,
              selfAuthored,
              window,
            })),
          ),
        ),
      ),
    ),
  ),
).filter(possibleVerdict);

function verdictFindings(row: VerdictRow): ReviewFindings {
  const base = findingsOf(row.kind, 3);
  const checks = CHECKS[row.checks];
  const tests = TESTS[row.tests];
  return {
    ...base,
    ...(checks === undefined ? {} : { checks }),
    ...(tests === undefined ? {} : { tests }),
    ...(row.selfAuthored ? { selfAuthored: true } : {}),
  };
}

describe(`review findings tests line and verdict matrix (${VERDICT_ROWS.length} rows)`, () => {
  beforeEach(() => TestBed.resetTestingModule());

  it.each(
    VERDICT_ROWS.map(
      (row) =>
        [
          `${row.kind}, checks ${row.checks}, tests ${row.tests}, ${row.selfAuthored ? 'own pull request' : "someone else's pull request"}, verdict ${row.phase === 'none' ? 'none' : `${row.button} ${row.phase} in ${row.window}`}`,
          row,
        ] as const,
    ),
  )('%s', async (_name, row) => {
    const bridge = makeBridge();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      ],
    });
    const fixture = TestBed.createComponent(ReviewFindingsComponent);
    fixture.componentRef.setInput('turnId', 'f-1');
    fixture.componentRef.setInput('review', verdictFindings(row));
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    await click(fixture, all(all(root, 'finding')[0]!, 'yes')[0]);
    const verdictsBeforeClick = bridge.localAsk.mock.calls.length;

    if (row.phase !== 'none') {
      const reply =
        row.phase === 'given'
          ? `Done: ${PR_URL}`
          : row.phase === 'failed'
            ? 'GitHub refused it.'
            : '';
      const state = row.phase === 'giving' ? 'running' : row.phase === 'failed' ? 'failed' : 'done';
      if (row.window === 'this window') {
        await click(fixture, all(root, BUTTON_TEST[row.button])[0]);
        const id = (bridge.localAsk.mock.lastCall![0] as { id: string }).id;
        if (state !== 'running') bridge.end(id, state, reply);
      } else {
        bridge.push([
          ...bridge.turns(),
          { id: 'elsewhere', question: QUESTION[row.button], reply, citations: [], state },
        ]);
      }
      await settle(fixture);
    }

    const review = row.kind === 'review';
    const open = review && row.phase !== 'given';
    const given = all(root, 'verdict-given')[0];
    const enabled = (test: string) =>
      all(root, test).map((button) => !(button as HTMLButtonElement).disabled);
    const sent = bridge.localAsk.mock.calls.map(([req]) => ('text' in req ? req.text : ''));

    expect({
      tests: all(root, 'tests').length,
      marks: all(root, 'check-mark').map((mark) => text(mark)),
      run: text(all(root, 'tests-run')[0]),
      output: all(root, 'tests-output').length,
    }).toEqual({
      tests: row.checks === 'absent' && row.tests === 'absent' ? 0 : 1,
      marks: MARKS[row.checks],
      run: RUN_LINE[row.tests],
      output: row.tests === 'ran fail' ? 1 : 0,
    });
    expect({
      approve: enabled('approve'),
      requestChanges: enabled('request-changes'),
      post: enabled('post'),
      given: text(given?.querySelector('strong')),
      link: text(given?.querySelector('a')),
      posted: all(root, 'posted').length,
      cardButtons: all(root, 'yes').length > 0,
    }).toEqual({
      approve: open && !row.selfAuthored ? [row.phase !== 'giving'] : [],
      requestChanges: open ? [row.phase !== 'giving'] : [],
      post: row.phase === 'given' ? [] : [row.phase !== 'giving'],
      given: row.phase === 'given' ? GIVEN_TITLE[row.button] : null,
      link: row.phase === 'given' ? PR_URL : null,
      posted: 0,
      cardButtons: row.phase !== 'given',
    });
    expect(verdictsBeforeClick).toBe(0);
    expect(sent.map((ask) => ask.split(VERDICT_MARKER)[0])).toEqual(
      row.window === 'this window' && row.phase !== 'none' ? [QUESTION[row.button]] : [],
    );
  });
});
