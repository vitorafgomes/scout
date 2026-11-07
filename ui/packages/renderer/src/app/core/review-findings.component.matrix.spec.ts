import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ISSUES_MARKER,
  REVIEW_MARKER,
  type LocalAskRequest,
  type LocalTurn,
  type ReviewFinding,
  type ReviewFindings,
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
      const cut = review === -1 ? issues : review;
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
