import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ISSUES_MARKER,
  type GitHubVerdictAction,
  type GitHubVerdictKey,
  type GitHubVerdictRequest,
  type GitHubVerdictProgress,
  type GitHubVerdictStatus,
  type LocalAskRequest,
  type LocalFindingDecision,
  type LocalSetDecisionsRequest,
  type LocalTurn,
  type ReviewCheck,
  type ReviewFinding,
  type ReviewFindings,
  type ReviewTests,
} from '@scout/shared';

import { ReviewFindingsComponent } from './review-findings.component';
import { findingsCards } from './review-findings.store';
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
type Footer = 'footer' | 'all-dismissed' | 'all-sent' | 'posted' | 'posting';

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

const SUMMARY_AFTER_SENDING: Readonly<Record<Decisions, Readonly<Record<Count, string | null>>>> = {
  'all undecided': SUMMARY['all undecided'],
  'all Yes': { 1: null, 3: null },
  'all No': { 1: null, 3: null },
  'mixed Yes/No/undecided': { 1: null, 3: '0 of 1 selected · 1 undecided' },
  'one edited Yes': {
    1: null,
    3: 'Nothing selected yet. Decide item by item, or Yes to all.',
  },
};

const ALL_SENT = 'Every chosen issue is queued on acme/shop';

const POST_LABEL: Readonly<Record<Kind, Readonly<Record<number, string>>>> = {
  review: { 0: 'Post on PR #5', 1: 'Post 1 on PR #5', 3: 'Post 3 on PR #5' },
  audit: {
    0: 'Open issues on acme/shop',
    1: 'Open 1 issue on acme/shop',
    3: 'Open 3 issues on acme/shop',
  },
};

const SENT_QUESTION: Readonly<Record<Kind, Readonly<Record<number, string>>>> = {
  review: {},
  audit: { 1: 'Open 1 issue on acme/shop', 3: 'Open 3 issues on acme/shop' },
};

const POSTED_TITLE: Readonly<Record<Kind, string>> = {
  review: 'Review posted on PR #5',
  audit: '1 issue opened on acme/shop',
};

const POSTED_LINE = 'Posted as one review that only comments';

interface Row {
  readonly kind: Kind;
  readonly count: Count;
  readonly decisions: Decisions;
  readonly picker: Window;
  readonly phase: Phase;
  readonly window: Window;
}

function possible(row: Row): boolean {
  if (PICKS[row.decisions][row.count].length !== row.count) return false;
  if (row.decisions === 'all undecided' && row.picker === 'the other window') return false;
  if (row.kind === 'review' && row.phase === 'done without link') return false;
  if (row.phase === 'before posting') return row.window === 'this window';
  return row.window === 'the other window' || CHOSEN[row.decisions][row.count] > 0;
}

const ROWS: readonly Row[] = KINDS.flatMap((kind) =>
  COUNTS.flatMap((count) =>
    DECISIONS.flatMap((decisions) =>
      WINDOWS.flatMap((picker) =>
        PHASES.flatMap((phase) =>
          WINDOWS.map((window) => ({ kind, count, decisions, picker, phase, window })),
        ),
      ),
    ),
  ),
).filter(possible);

const VERDICT_KEY = {
  repository: 'acme/shop',
  number: 5,
  commit: 'ec7f951',
  verdict: 'approve',
} as const;

function titlesAfter(text: string, at: number): readonly string[] {
  const json = JSON.parse(text.slice(at + ISSUES_MARKER.length)) as {
    issues: { title: string }[];
  };
  return json.issues.map((issue) => issue.title.trim());
}

function makeBridge() {
  let turns: readonly LocalTurn[] = [
    { id: 'f-1', question: 'review acme/shop', reply: 'the review', citations: [], state: 'done' },
  ];
  const listeners: ((turns: readonly LocalTurn[]) => void)[] = [];
  const verdictListeners: ((status: GitHubVerdictStatus) => void)[] = [];
  let actions = 0;
  let key: Omit<GitHubVerdictStatus, keyof GitHubVerdictProgress> = VERDICT_KEY;
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
      const issues = text.indexOf(ISSUES_MARKER);
      push([
        ...turns,
        {
          id: req.id,
          question: issues === -1 ? text : text.slice(0, issues),
          reply: '',
          citations: [],
          state: 'running',
          ...(issues === -1
            ? {}
            : { issues: true as const, issueTitles: titlesAfter(text, issues) }),
        },
      ]);
    }),
    localCancel: vi.fn(async (): Promise<void> => undefined),
    localClearTurns: vi.fn(async (): Promise<void> => undefined),
    localSetDecisions: vi.fn(async (req: LocalSetDecisionsRequest): Promise<void> =>
      push(
        turns.map((turn) =>
          turn.id === req.turnId ? { ...turn, decisions: req.decisions } : turn,
        ),
      ),
    ),
    githubGiveVerdict: vi.fn(async (req: GitHubVerdictRequest): Promise<GitHubVerdictAction> => {
      key = {
        repository: req.repository,
        number: req.number,
        commit: req.commit,
        verdict: req.verdict,
      };
      return { actionId: `action-${++actions}` };
    }),
    githubVerdictFor: vi.fn(
      async (_req: GitHubVerdictKey): Promise<GitHubVerdictStatus | null> => null,
    ),
    githubVerdictStatus: vi.fn(async (req: GitHubVerdictAction): Promise<GitHubVerdictStatus> => ({
      ...key,
      actionId: req.actionId,
      state: 'queued',
      url: null,
      reason: null,
    })),
    onGitHubVerdict: vi.fn((listener: (status: GitHubVerdictStatus) => void) => {
      verdictListeners.push(listener);
      return () => undefined;
    }),
    pushStatus: (status: GitHubVerdictStatus) => verdictListeners.forEach((l) => l(status)),
    verdict: (status: GitHubVerdictProgress) =>
      verdictListeners.forEach((l) => l({ ...key, ...status })),
    appOpenExternal: vi.fn(async (): Promise<void> => undefined),
  };
}

function decisionsOf(row: Pick<Row, 'decisions' | 'count'>): Record<string, LocalFindingDecision> {
  return Object.fromEntries(
    PICKS[row.decisions][row.count].flatMap((pick, i) => {
      const finding = FINDINGS[i]!;
      if (pick === null) return [];
      if (pick === 'edit') {
        return [
          [
            finding.id,
            {
              pick: 'yes',
              dismissed: false,
              edited: { why: 'Edited by the owner', fix: finding.fix },
            },
          ],
        ];
      }
      return [[finding.id, { pick, dismissed: pick === 'no' }]];
    }),
  );
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
    : { repository: 'acme/shop', number: 5, commit: 'ec7f951', verdict: 'v', findings };
}

function otherWindowTurn(
  state: LocalTurn['state'],
  reply: string,
  titles: readonly string[],
): LocalTurn {
  return {
    id: 'elsewhere',
    question: 'Open 1 issue on acme/shop',
    reply,
    citations: [],
    state,
    issues: true,
    ...(titles.length === 0 ? {} : { issueTitles: titles }),
  };
}

function chosenTitles(row: Pick<Row, 'decisions' | 'count'>): readonly string[] {
  return PICKS[row.decisions][row.count].flatMap((pick, i) =>
    pick === 'yes' || pick === 'edit' ? [FINDINGS[i]!.title] : [],
  );
}

const POST_KEY = {
  repository: 'acme/shop',
  number: 5,
  commit: 'ec7f951',
  verdict: 'comment',
} as const;

function postProgress(phase: Phase, actionId: string): GitHubVerdictProgress {
  return phase === 'posted'
    ? { actionId, state: 'given', url: PR_URL, reason: null }
    : phase === 'failed'
      ? { actionId, state: 'failed', url: null, reason: 'GitHub refused it.' }
      : { actionId, state: 'running', url: null, reason: null };
}

const LINK: Readonly<Record<Kind, string>> = { review: PR_URL, audit: ISSUE_URL };

describe(`review findings matrix (${ROWS.length} rows)`, () => {
  beforeEach(() => TestBed.resetTestingModule());

  it.each(
    ROWS.map(
      (row) =>
        [
          `${row.kind}, ${row.count} finding${row.count === 1 ? '' : 's'}, ${row.decisions}${row.decisions === 'all undecided' ? '' : ` in ${row.picker}`}, ${row.phase}${row.phase === 'before posting' ? '' : `, in ${row.window}`}`,
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

    if (row.picker === 'the other window') {
      bridge.push(bridge.turns().map((turn) => ({ ...turn, decisions: decisionsOf(row) })));
      await settle(fixture);
    } else if (row.decisions === 'all Yes') {
      await click(fixture, all(root, 'yes-all')[0]);
    }
    for (const [i, pick] of PICKS[row.decisions][row.count].entries()) {
      if (row.picker === 'the other window' || row.decisions === 'all Yes') break;
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

    if (row.phase !== 'before posting' && row.kind === 'review') {
      if (row.window === 'this window') {
        await click(fixture, all(root, 'post')[0]);
        expect(all(root, 'post')).toEqual([]);
        if (row.phase !== 'posting') bridge.verdict(postProgress(row.phase, 'action-1'));
      } else {
        bridge.pushStatus({ ...POST_KEY, ...postProgress(row.phase, 'action-9') });
      }
      await settle(fixture);
    } else if (row.phase !== 'before posting') {
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
        bridge.push([...bridge.turns(), otherWindowTurn(state, reply, chosenTitles(row))]);
      }
      await settle(fixture);
    }

    const chosen = CHOSEN[row.decisions][row.count];
    const sentNow = row.kind === 'audit' && row.phase === 'done without link';
    const sentFlags = PICKS[row.decisions][row.count].map(
      (pick) => sentNow && (pick === 'yes' || pick === 'edit'),
    );
    const keptUnsent = PICKS[row.decisions][row.count].filter(
      (pick, i) => pick !== 'no' && !sentFlags[i],
    ).length;
    const footer: Footer =
      row.kind === 'review' && row.phase === 'posting'
        ? 'posting'
        : row.phase === 'posted'
          ? 'posted'
          : row.decisions === 'all No'
            ? 'all-dismissed'
            : sentNow && chosen > 0 && keptUnsent === 0
              ? 'all-sent'
              : 'footer';
    const stillChosen = sentNow ? 0 : chosen;
    const postEnabled = row.phase !== 'posting' && stillChosen > 0;
    const posted = all(root, 'posted')[0];

    expect(cards().map((card) => all(card, 'dismissed').length === 1)).toEqual(
      DISMISSED[row.decisions][row.count],
    );
    expect(cards().map((card) => all(card, 'undo').length)).toEqual(
      DISMISSED[row.decisions][row.count].map((line) => (line && footer !== 'posted' ? 1 : 0)),
    );
    expect(cards().map((card) => all(card, 'yes').length)).toEqual(
      DISMISSED[row.decisions][row.count].map((line, i) =>
        line || sentFlags[i] || footer === 'posted' || footer === 'posting' ? 0 : 1,
      ),
    );
    expect(cards().map((card) => text(all(card, 'sent-mark')[0]))).toEqual(
      sentFlags.map((sent) => (sent ? 'Issue queued' : null)),
    );
    expect({
      footer: all(root, 'summary').length === 1,
      allDismissed: text(all(root, 'all-dismissed')[0]),
      allSent: text(all(root, 'all-sent')[0]?.querySelector('strong')),
      post: all(root, 'post').length,
      posted: posted !== undefined,
    }).toEqual({
      footer: footer === 'footer',
      allSent: footer === 'all-sent' ? ALL_SENT : null,
      post: footer === 'footer' ? 1 : 0,
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
        summary: (sentNow ? SUMMARY_AFTER_SENDING : SUMMARY)[row.decisions][row.count],
        label: POST_LABEL[row.kind][stillChosen],
        enabled: postEnabled,
        yesAll: 1,
        preview: 1,
      });
    }
    if (footer === 'posting') {
      expect({
        box: all(root, 'posting').length,
        line: (text(all(root, 'verdict-giving')[0]) ?? '').startsWith('Posting the review… '),
        target: text(all(root, 'target')[0]),
        post: all(root, 'post').length,
      }).toEqual({ box: 1, line: true, target: 'acme/shop · PR #5 · commit ec7f951', post: 0 });
    }
    if (footer === 'posted') {
      expect({
        title: text(posted!.querySelector('strong')),
        links: Array.from(posted!.querySelectorAll('a')).map((a) => text(a)),
      }).toEqual({ title: POSTED_TITLE[row.kind], links: [LINK[row.kind]] });
      if (row.kind === 'review') {
        expect(text(posted)).toContain(POSTED_LINE);
      }
    }
    if (row.kind === 'review' && row.phase === 'failed') {
      expect(text(all(root, 'verdict-failed')[0])).toBe('GitHub refused it.');
    }
    expect({
      shared: bridge.turns()[0]!.decisions ?? {},
      writes: bridge.localSetDecisions.mock.calls.length > 0,
    }).toEqual({
      shared: decisionsOf(row),
      writes: row.picker === 'this window' && row.decisions !== 'all undecided',
    });
    const sent = bridge.localAsk.mock.calls.map(([req]) => ('text' in req ? req.text : ''));
    const clicked = bridge.githubGiveVerdict.mock.calls.map(([req]) => req);
    if (row.window === 'this window' && row.phase !== 'before posting' && row.kind === 'review') {
      expect(sent).toEqual([]);
      expect(clicked).toHaveLength(1);
      expect({ ...clicked[0]!, body: '', comments: clicked[0]!.comments.length }).toEqual({
        ...POST_KEY,
        body: '',
        comments: chosen,
        testsLine: '',
      });
      expect(JSON.stringify(clicked[0]!.comments).includes('Edited by the owner')).toBe(
        row.decisions === 'one edited Yes',
      );
    } else if (row.window === 'this window' && row.phase !== 'before posting') {
      const [question, json] = sent[0]!.split(ISSUES_MARKER);
      expect(sent).toHaveLength(1);
      expect(clicked).toEqual([]);
      expect(question).toBe(SENT_QUESTION[row.kind][chosen]);
      expect(json!.includes('Edited by the owner')).toBe(row.decisions === 'one edited Yes');
    } else {
      expect(sent).toEqual([]);
      expect(clicked).toEqual([]);
    }
  });
});

type ChecksCase = 'absent' | 'all pass' | 'one failing' | 'pending';
type TestsCase = 'absent' | 'ran pass' | 'ran fail' | 'not ran';
type VerdictPhase = 'none' | 'queued' | 'running' | 'given' | 'failed';
type VerdictButton = 'Approve' | 'Request changes';

const CHECKS_CASES: readonly ChecksCase[] = ['absent', 'all pass', 'one failing', 'pending'];
const TESTS_CASES: readonly TestsCase[] = ['absent', 'ran pass', 'ran fail', 'not ran'];
const VERDICT_PHASES: readonly VerdictPhase[] = ['none', 'queued', 'running', 'given', 'failed'];
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
  'all pass': ['passed', 'passed'],
  'one failing': ['passed', 'failed'],
  pending: ['passed', 'pending'],
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
const RUN_CHIP: Readonly<Record<TestsCase, string | null>> = {
  absent: null,
  'ran pass': 'tests passed in 42 s (dotnet test)',
  'ran fail': 'tests failed (exit 1)',
  'not ran': 'tests did not run: Docker is not installed.',
};
const TESTS_SENTENCE: Readonly<Record<ChecksCase, string | null>> = {
  absent: null,
  'all pass': 'Checks: build passed, lint passed',
  'one failing': 'Checks: build passed, lint failed',
  pending: 'Checks: build passed, lint pending',
};
const VERDICT: Readonly<Record<VerdictButton, GitHubVerdictRequest['verdict']>> = {
  Approve: 'approve',
  'Request changes': 'request_changes',
};
const GIVEN_TITLE: Readonly<Record<VerdictButton, string>> = {
  Approve: 'Approved on PR #5',
  'Request changes': 'Changes requested on PR #5',
};
const BUTTON_TEST: Readonly<Record<VerdictButton, string>> = {
  Approve: 'approve',
  'Request changes': 'request-changes',
};
const FAILED_REASON = 'The pull request moved since the review.';

interface VerdictRow {
  readonly kind: Kind;
  readonly checks: ChecksCase;
  readonly tests: TestsCase;
  readonly phase: VerdictPhase;
  readonly button: VerdictButton;
  readonly selfAuthored: boolean;
  readonly clicked: VerdictWindow;
  readonly clean: boolean;
}

type VerdictWindow = 'this window' | 'a window opened after the click' | 'the other window';
const VERDICT_WINDOWS: readonly VerdictWindow[] = [
  'this window',
  'a window opened after the click',
  'the other window',
];

function possibleVerdict(row: VerdictRow): boolean {
  if (row.kind === 'audit') {
    return (
      row.checks === 'absent' &&
      row.tests === 'absent' &&
      row.phase === 'none' &&
      !row.selfAuthored &&
      row.button === 'Approve' &&
      row.clicked === 'this window' &&
      !row.clean
    );
  }
  if (row.clean && row.button === 'Request changes' && row.clicked === 'this window') return false;
  if (row.phase === 'none') return row.button === 'Approve' && row.clicked === 'this window';
  return !(row.selfAuthored && row.button === 'Approve');
}

const VERDICT_ROWS: readonly VerdictRow[] = KINDS.flatMap((kind) =>
  CHECKS_CASES.flatMap((checks) =>
    TESTS_CASES.flatMap((tests) =>
      VERDICT_PHASES.flatMap((phase) =>
        VERDICT_BUTTONS.flatMap((button) =>
          [false, true].flatMap((selfAuthored) =>
            VERDICT_WINDOWS.flatMap((clicked) =>
              [false, true].map((clean) => ({
                kind,
                checks,
                tests,
                phase,
                button,
                selfAuthored,
                clicked,
                clean,
              })),
            ),
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
    ...(row.clean ? { findings: [] } : {}),
    commit: 'ec7f951',
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
          `${row.kind}${row.clean ? ' with no findings' : ''}, checks ${row.checks}, tests ${row.tests}, ${row.selfAuthored ? 'own pull request' : "someone else's pull request"}, verdict ${row.phase === 'none' ? 'none' : `${row.button} ${row.phase}, clicked in ${row.clicked}`}`,
          row,
        ] as const,
    ),
  )('%s', async (_name, row) => {
    const bridge = makeBridge();
    const elsewhere = {
      ...VERDICT_KEY,
      verdict: VERDICT[row.button],
      actionId: 'action-1',
      state: row.phase === 'none' ? 'queued' : row.phase,
      url: row.phase === 'given' ? PR_URL : null,
      reason: row.phase === 'failed' ? FAILED_REASON : null,
    } as const;
    if (row.phase !== 'none' && row.clicked === 'a window opened after the click') {
      bridge.githubVerdictFor.mockResolvedValue(elsewhere);
    }
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
    const first = all(root, 'finding')[0];
    const yes = first === undefined ? undefined : all(first, 'yes')[0];
    if (yes !== undefined) await click(fixture, yes);
    const verdictsBeforeClick = bridge.githubGiveVerdict.mock.calls.length;

    if (row.phase !== 'none' && row.clicked === 'this window') {
      await click(fixture, all(root, BUTTON_TEST[row.button])[0]);
      if (row.phase !== 'queued') {
        bridge.verdict({
          actionId: 'action-1',
          state: row.phase,
          url: row.phase === 'given' ? PR_URL : null,
          reason: row.phase === 'failed' ? FAILED_REASON : null,
        });
      }
      await settle(fixture);
    }
    if (row.phase !== 'none' && row.clicked === 'the other window') {
      bridge.pushStatus(elsewhere);
      await settle(fixture);
    }
    const here = row.clicked === 'this window' && row.phase !== 'none';

    const review = row.kind === 'review';
    const open = review && row.phase !== 'given';
    const giving = row.phase === 'queued' || row.phase === 'running';
    const given = all(root, 'verdict-given')[0];
    const enabled = (test: string) =>
      all(root, test).map((button) => !(button as HTMLButtonElement).disabled);
    const testsSentence = [TESTS_SENTENCE[row.checks], RUN_LINE[row.tests]]
      .filter((line) => line !== null)
      .join('\n');

    expect({
      tests: all(root, 'tests').length,
      marks: all(root, 'check-mark').map((mark) => text(mark)),
      run: text(all(root, 'tests-run')[0]),
      output: all(root, 'tests-output').length,
    }).toEqual({
      tests: row.checks === 'absent' && row.tests === 'absent' ? 0 : 1,
      marks: MARKS[row.checks],
      run: RUN_CHIP[row.tests],
      output: row.tests === 'ran fail' ? 1 : 0,
    });
    expect({
      approve: enabled('approve'),
      requestChanges: enabled('request-changes'),
      post: enabled('post'),
      giving: text(all(root, 'verdict-giving')[0]),
      failed: text(all(root, 'verdict-failed')[0]),
      given: text(given?.querySelector('strong')),
      link: text(given?.querySelector('a')),
      posted: all(root, 'posted').length,
      cardButtons: all(root, 'yes').length > 0,
    }).toEqual({
      approve: open && !row.selfAuthored && !giving ? [true] : [],
      requestChanges: open && !giving && !row.clean ? [true] : [],
      post: row.phase === 'given' || row.clean ? [] : [!giving],
      giving: giving ? 'Giving the verdict…' : null,
      failed: row.phase === 'failed' ? FAILED_REASON : null,
      given: row.phase === 'given' ? GIVEN_TITLE[row.button] : null,
      link: row.phase === 'given' ? PR_URL : null,
      posted: 0,
      cardButtons: row.phase !== 'given' && !row.clean,
    });
    expect(text(all(root, 'clean')[0])).toBe(
      row.clean && row.phase !== 'given' ? 'No findings.' : null,
    );
    expect(verdictsBeforeClick).toBe(0);
    expect(bridge.localAsk).not.toHaveBeenCalled();
    expect(bridge.githubVerdictFor.mock.calls.map(([key]) => key)).toEqual(
      row.kind === 'review' ? [{ repository: 'acme/shop', number: 5, commit: 'ec7f951' }] : [],
    );
    expect(bridge.githubGiveVerdict.mock.calls.map(([request]) => request)).toEqual(
      !here
        ? []
        : [
            {
              repository: 'acme/shop',
              number: 5,
              verdict: VERDICT[row.button],
              body: expect.any(String),
              comments: row.clean
                ? []
                : [expect.objectContaining({ path: 'src/orders/Checkout.cs', line: 88 })],
              commit: 'ec7f951',
              testsLine: testsSentence,
            },
          ],
    );

    const again = all(root, BUTTON_TEST[row.button])[0];
    const clickedAgain = row.phase !== 'none' && again !== undefined;
    if (clickedAgain) await click(fixture, again);
    expect(bridge.githubGiveVerdict).toHaveBeenCalledTimes(
      (here ? 1 : 0) + (row.phase === 'failed' && clickedAgain ? 1 : 0),
    );
  });
});

type Sent = 0 | 1 | 3;
type Pending = 0 | 'n';
type SendEnd = 'done' | 'failed';

interface SentRow {
  readonly sent: Sent;
  readonly pending: Pending;
  readonly batch: boolean;
  readonly window: Window;
  readonly end: SendEnd;
}

const NEW_FINDINGS: readonly ReviewFinding[] = [
  {
    id: 'rounding',
    severity: 'fix',
    title: 'Total is rounded twice',
    path: 'src/orders/Checkout.cs',
    line: 120,
    why: 'Each line is rounded and then the sum.',
    fix: 'Round once, at the end.',
  },
  {
    id: 'retry',
    severity: 'nit',
    title: 'Retry has no limit',
    path: 'src/orders/Payment.cs',
    line: 40,
    why: 'A failing gateway is called forever.',
    fix: 'Stop after three tries.',
  },
];

const OPEN_LABEL: Readonly<Record<number, string>> = {
  0: 'Open issues on acme/shop',
  1: 'Open 1 issue on acme/shop',
  2: 'Open 2 issues on acme/shop',
  3: 'Open 3 issues on acme/shop',
  4: 'Open 4 issues on acme/shop',
  5: 'Open 5 issues on acme/shop',
};

function possibleSent(row: SentRow): boolean {
  if (row.sent === 0) return row.window === 'this window' && row.end === 'done';
  const left = 3 - (row.end === 'done' ? row.sent : 0) + (row.batch ? 2 : 0);
  return row.pending === 0 || left > 0;
}

const SENT_ROWS: readonly SentRow[] = ([0, 1, 3] as const)
  .flatMap((sent) =>
    ([0, 'n'] as const).flatMap((pending) =>
      [false, true].flatMap((batch) =>
        WINDOWS.flatMap((window) =>
          (['done', 'failed'] as const).map((end) => ({ sent, pending, batch, window, end })),
        ),
      ),
    ),
  )
  .filter(possibleSent);

function auditTurn(id: string, findings: readonly ReviewFinding[]): LocalTurn {
  const block = {
    kind: 'audit',
    repository: 'acme/shop',
    commit: 'ec7f951',
    verdict: 'v',
    findings,
  };
  return {
    id,
    question: 'Audit acme/shop',
    reply: `Findings.\n\n\`\`\`scout-findings\n${JSON.stringify(block)}\n\`\`\``,
    citations: [],
    state: 'done',
  };
}

describe(`review findings sent issues matrix (${SENT_ROWS.length} rows)`, () => {
  beforeEach(() => TestBed.resetTestingModule());

  it.each(
    SENT_ROWS.map(
      (row) =>
        [
          `${row.sent} sent${row.sent === 0 ? '' : ` from ${row.window}, the issues turn ${row.end}`}, ${row.batch ? 'a new batch of 2' : 'no new batch'}, ${row.pending === 0 ? 'nothing more picked' : 'Yes to all on the rest'}`,
          row,
        ] as const,
    ),
  )('%s', async (_name, row) => {
    const bridge = makeBridge();
    bridge.push([auditTurn('a-1', FINDINGS)]);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      ],
    });
    const fixture = TestBed.createComponent(ReviewFindingsComponent);
    fixture.componentRef.setInput('turnId', 'a-1');
    fixture.componentRef.setInput('review', findingsCards(bridge.turns()).get('a-1')!.review);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const cards = () => all(root, 'finding');
    const firstTitles = FINDINGS.slice(0, row.sent).map((f) => f.title);

    if (row.sent > 0 && row.window === 'this window') {
      for (let i = 0; i < row.sent; i++) await click(fixture, all(cards()[i]!, 'yes')[0]);
      await click(fixture, all(root, 'post')[0]);
      const id = (bridge.localAsk.mock.lastCall![0] as { id: string }).id;
      bridge.end(id, row.end, row.end === 'done' ? 'Sent them on.' : 'GitHub refused it.');
      await settle(fixture);
    } else if (row.sent > 0) {
      const decisions = Object.fromEntries(
        FINDINGS.slice(0, row.sent).map((f) => [f.id, { pick: 'yes', dismissed: false }] as const),
      );
      bridge.push([
        ...bridge.turns().map((turn) => (turn.id === 'a-1' ? { ...turn, decisions } : turn)),
        {
          id: 'elsewhere',
          question: `Open ${row.sent} issues on acme/shop`,
          reply: row.end === 'done' ? 'Sent them on.' : 'GitHub refused it.',
          citations: [],
          state: row.end,
          issues: true,
          issueTitles: firstTitles,
        },
      ]);
      await settle(fixture);
    }
    if (row.batch) {
      bridge.push([...bridge.turns(), auditTurn('a-2', [...FINDINGS, ...NEW_FINDINGS])]);
      fixture.componentRef.setInput('review', findingsCards(bridge.turns()).get('a-2')!.review);
      await settle(fixture);
    }
    if (row.pending === 'n') await click(fixture, all(root, 'yes-all')[0]);

    const shown = [...FINDINGS, ...(row.batch ? NEW_FINDINGS : [])];
    const sentCount = row.end === 'done' ? row.sent : 0;
    const unsent = shown.slice(sentCount);
    const chosen = row.pending === 'n' ? unsent : unsent.slice(0, row.sent - sentCount);
    const everySent = sentCount > 0 && unsent.length === 0;
    const post = all(root, 'post')[0] as HTMLButtonElement | undefined;

    expect({
      rows: cards().map((card) => ({
        sent: text(all(card, 'sent-mark')[0]),
        yes: all(card, 'yes').length,
      })),
      open: post === undefined ? null : text(post),
      enabled: post === undefined ? null : !post.disabled,
      allSent: text(all(root, 'all-sent')[0]?.querySelector('strong')),
      asked: bridge.localAsk.mock.calls.length,
    }).toEqual({
      rows: shown.map((_, i) => ({
        sent: i < sentCount ? 'Issue queued' : null,
        yes: i < sentCount ? 0 : 1,
      })),
      open: everySent ? null : OPEN_LABEL[chosen.length],
      enabled: everySent ? null : chosen.length > 0,
      allSent: everySent ? ALL_SENT : null,
      asked: row.sent > 0 && row.window === 'this window' ? 1 : 0,
    });

    if (chosen.length > 0) {
      await click(fixture, all(root, 'post')[0]);
      const asked = bridge.localAsk.mock.lastCall![0] as { id: string; text: string };
      const [question, json] = asked.text.split(ISSUES_MARKER);
      const titles = (JSON.parse(json!) as { issues: { title: string }[] }).issues.map(
        (issue) => issue.title,
      );
      bridge.end(asked.id, 'done', 'Sent them on.');
      await settle(fixture);

      expect({ question, titles }).toEqual({
        question: OPEN_LABEL[chosen.length],
        titles: chosen.map((f) => f.title),
      });
      expect({
        sent: cards().map((card) => text(all(card, 'sent-mark')[0])),
        post: all(root, 'post').length,
      }).toEqual({
        sent: shown.map((f, i) => (i < sentCount || chosen.includes(f) ? 'Issue queued' : null)),
        post: chosen.length + sentCount === shown.length ? 0 : 1,
      });
    }
  });
});
