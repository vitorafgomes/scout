import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ISSUES_MARKER,
  REVIEW_MARKER,
  type GitHubVerdictAction,
  type GitHubVerdictKey,
  type GitHubVerdictRequest,
  type GitHubVerdictProgress,
  type GitHubVerdictStatus,
  type LocalAskRequest,
  type LocalFindingDecision,
  type LocalSetDecisionsRequest,
  type LocalEvent,
  type LocalTurn,
  type ReviewFinding,
  type ReviewFindings,
} from '@scout/shared';

import { commentBody, issueBody, reviewBody } from './review-findings';
import { ReviewFindingsComponent } from './review-findings.component';
import { ReviewFindingsStore, findingsCards } from './review-findings.store';
import { ScoutBridgeService } from './scout-bridge';

const total: ReviewFinding = {
  id: 'total',
  severity: 'block',
  title: 'Discounts applied after the sum',
  path: 'src/orders/Checkout.cs',
  line: 88,
  context: 'Not from this PR, but this is where it shows.',
  why: 'The total is summed before the discount loop runs.',
  fix: 'Apply the discounts before the sum.',
};
const lock: ReviewFinding = {
  id: 'lock',
  severity: 'fix',
  title: 'Move has no lock',
  path: 'src/orders/Cart.cs',
  line: 12,
  why: 'Two moves at once lose an item.',
  fix: 'Take the cart lock.',
};
const naming: ReviewFinding = {
  id: 'naming',
  severity: 'nit',
  title: 'Name says the wrong thing',
  path: 'src/orders/Cart.cs',
  line: 30,
  why: 'items holds ids.',
  fix: 'Rename it itemIds.',
};
const review: ReviewFindings = {
  repository: 'acme/shop',
  number: 5,
  verdict: 'merge after the blocking finding',
  read: '3 of 3 files read',
  findings: [total, lock, naming],
};
const URL = 'https://github.com/acme/shop/pull/5#pullrequestreview-77';
const reviewed: ReviewFindings = { ...review, commit: 'ec7f951' };
const audit: ReviewFindings = {
  kind: 'audit',
  repository: 'acme/shop',
  commit: 'ec7f951',
  verdict: 'two defects to fix before the release',
  read: '41 of 41 files read',
  known: ['Refunds are manual until v2, as the README says.', 'There is no dark mode yet.'],
  findings: [
    { ...total, rule: 'ADR 0007: totals are computed after discounts' },
    lock,
    { ...naming, rule: 'Defect' },
  ],
};
const ISSUE_12 = 'https://github.com/acme/shop/issues/12';
const ISSUE_13 = 'https://github.com/Acme/Shop/issues/13';

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

function numbered(n: number): ReviewFinding {
  return {
    id: `f${n}`,
    severity: 'fix',
    title: `Finding ${n} needs a fix`,
    path: `src/area/File${n}.cs`,
    line: n,
    why: `Defect ${n}.`,
    fix: `Fix ${n}.`,
  };
}

function numberedRange(from: number, to: number): ReviewFinding[] {
  return Array.from({ length: to - from + 1 }, (_, i) => numbered(from + i));
}

function auditBatch(id: string, findings: readonly ReviewFinding[]): LocalTurn {
  const block = {
    kind: 'audit',
    repository: 'acme/shop',
    commit: 'ec7f951',
    verdict: 'defects to fix',
    findings,
  };
  return {
    id,
    question: 'Audit acme/shop',
    reply: `Findings of this area.\n\n\`\`\`scout-findings\n${JSON.stringify(block)}\n\`\`\``,
    citations: [],
    state: 'done',
  };
}

const QUEUED_IN_PORTUGUESE =
  'Coloquei as issues na fila; cada uma espera a sua aprovação no Scout Desktop.';

function makeBridge() {
  let turns: readonly LocalTurn[] = [
    {
      id: 'f-1',
      question: 'review acme/shop#5',
      reply: 'the review',
      citations: [],
      state: 'done',
    },
  ];
  const listeners: ((turns: readonly LocalTurn[]) => void)[] = [];
  const verdictListeners: ((status: GitHubVerdictStatus) => void)[] = [];
  let actions = 0;
  let key: Omit<GitHubVerdictStatus, keyof GitHubVerdictProgress> = VERDICT_KEY;
  const push = (next: readonly LocalTurn[]) => {
    turns = next;
    listeners.forEach((listener) => listener(turns));
  };
  const localAsk = vi.fn(async (req: LocalAskRequest): Promise<void> => {
    const text = 'text' in req ? req.text : '';
    const at = text.indexOf(REVIEW_MARKER);
    const issuesAt = text.indexOf(ISSUES_MARKER);
    const cut = at === -1 ? issuesAt : at;
    push([
      ...turns,
      {
        id: req.id,
        question: cut === -1 ? text : text.slice(0, cut),
        reply: '',
        citations: [],
        state: 'running',
        ...(at === -1 ? {} : { review: true as const }),
        ...(issuesAt === -1
          ? {}
          : { issues: true as const, issueTitles: titlesAfter(text, issuesAt) }),
      },
    ]);
  });
  return {
    push,
    emit: (event: LocalEvent) =>
      push(
        turns.map((turn) =>
          turn.id !== event.id
            ? turn
            : {
                ...turn,
                reply: event.text ?? turn.reply,
                state: event.event === 'complete' ? 'done' : 'failed',
              },
        ),
      ),
    onLocalTurns: vi.fn((listener: (turns: readonly LocalTurn[]) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
    localGetTurns: vi.fn(async (): Promise<readonly LocalTurn[]> => turns),
    localAsk,
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
    verdict: (status: GitHubVerdictProgress) =>
      verdictListeners.forEach((l) => l({ ...key, ...status })),
    pushStatus: (status: GitHubVerdictStatus) => verdictListeners.forEach((l) => l(status)),
    appOpenExternal: vi.fn(async (_req: { url: string }): Promise<void> => undefined),
  };
}

type Fixture = ComponentFixture<ReviewFindingsComponent>;

async function render(
  bridge: ReturnType<typeof makeBridge>,
  findings: ReviewFindings = review,
  turnId = 'f-1',
): Promise<Fixture> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(ReviewFindingsComponent);
  fixture.componentRef.setInput('turnId', turnId);
  fixture.componentRef.setInput('review', findings);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: Fixture): Promise<void> {
  const root = fixture.nativeElement as HTMLElement;
  for (let round = 0; round < 50; round++) {
    const before = root.innerHTML;
    fixture.detectChanges();
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
    if (root.innerHTML === before) return;
  }
  throw new Error('settle: the component was still rendering after 50 rounds');
}

function all(fixture: Fixture, test: string, root?: Element): HTMLElement[] {
  return Array.from(
    (root ?? (fixture.nativeElement as HTMLElement)).querySelectorAll(`[data-test="${test}"]`),
  );
}

function el(fixture: Fixture, test: string, root?: Element): HTMLElement | null {
  return all(fixture, test, root)[0] ?? null;
}

function text(node: Element | null): string {
  return (node?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

async function click(fixture: Fixture, node: Element | null | undefined): Promise<void> {
  if (!node) throw new Error('click: element not found');
  (node as HTMLElement).click();
  await settle(fixture);
}

function card(fixture: Fixture, index: number): HTMLElement {
  return all(fixture, 'finding')[index]!;
}

async function write(fixture: Fixture, test: string, root: Element, value: string): Promise<void> {
  const area = el(fixture, test, root) as HTMLTextAreaElement;
  area.value = value;
  area.dispatchEvent(new Event('input'));
  await settle(fixture);
}

describe('review findings cards', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('a findings block renders one card per finding with its severity, title, path and body', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    const cards = all(fixture, 'finding');
    expect(cards.map((c) => text(el(fixture, 'severity', c)))).toEqual([
      'Blocking',
      'Fix',
      'Suggestion',
    ]);
    expect(cards.map((c) => Array.from(el(fixture, 'severity', c)!.classList).sort())).toEqual([
      ['block', 'pill'],
      ['fix', 'pill'],
      ['nit', 'pill'],
    ]);
    expect(cards.map((c) => text(c.querySelector('h3')))).toEqual([
      'Discounts applied after the sum',
      'Move has no lock',
      'Name says the wrong thing',
    ]);
    expect(cards.map((c) => text(el(fixture, 'path', c)))).toEqual([
      'src/orders/Checkout.cs:88',
      'src/orders/Cart.cs:12',
      'src/orders/Cart.cs:30',
    ]);
    expect(Array.from(cards[0]!.querySelectorAll('.body p')).map(text)).toEqual([
      'Not from this PR, but this is where it shows.',
      'The total is summed before the discount loop runs.',
      'Fix: Apply the discounts before the sum.',
    ]);
    expect(Array.from(cards[1]!.querySelectorAll('.body p')).map(text)).toEqual([
      'Two moves at once lose an item.',
      'Fix: Take the cart lock.',
    ]);

    await click(fixture, el(fixture, 'path', cards[1]));
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({
      url: 'https://github.com/acme/shop/pull/5/files',
    });
  });

  it('Yes and No mark the card and the summary counts the findings not dismissed, Yes to all marks every undecided card', async () => {
    const fixture = await render(makeBridge());
    expect(text(el(fixture, 'summary'))).toBe(
      'Nothing selected yet. Decide item by item, or Yes to all.',
    );

    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'no', card(fixture, 1)));

    expect(
      all(fixture, 'finding').map((c) => [c.classList.contains('yes'), c.classList.contains('no')]),
    ).toEqual([
      [true, false],
      [false, true],
      [false, false],
    ]);
    expect(el(fixture, 'yes', card(fixture, 0))!.getAttribute('aria-pressed')).toBe('true');
    expect(el(fixture, 'yes', card(fixture, 2))!.getAttribute('aria-pressed')).toBe('false');
    expect(el(fixture, 'dismissed', card(fixture, 1))).not.toBeNull();
    expect(text(el(fixture, 'summary'))).toBe('1 of 2 selected · 1 undecided');

    await click(fixture, el(fixture, 'yes-all'));

    expect(all(fixture, 'finding').map((c) => c.classList.contains('yes'))).toEqual([
      true,
      false,
      true,
    ]);
    expect(text(el(fixture, 'summary'))).toBe('2 of 2 selected');
  });

  it('No collapses the card to a dismissed line with Undo, and Undo brings it back', async () => {
    const fixture = await render(makeBridge());

    await click(fixture, el(fixture, 'no', card(fixture, 1)));
    const dismissed = {
      line: text(el(fixture, 'dismissed', card(fixture, 1))),
      buttons: ['yes', 'no', 'edit'].map((test) => el(fixture, test, card(fixture, 1))),
      body: card(fixture, 1).querySelector('.body'),
      others: [0, 2].map((i) => el(fixture, 'dismissed', card(fixture, i))),
    };
    await click(fixture, el(fixture, 'undo', card(fixture, 1)));

    expect(dismissed).toEqual({
      line: 'Dismissed: Move has no lock Undo',
      buttons: [null, null, null],
      body: null,
      others: [null, null],
    });
    expect(el(fixture, 'dismissed')).toBeNull();
    expect(text(card(fixture, 1).querySelector('h3'))).toBe('Move has no lock');
    expect(el(fixture, 'yes', card(fixture, 1))!.getAttribute('aria-pressed')).toBe('false');
    expect(el(fixture, 'no', card(fixture, 1))!.getAttribute('aria-pressed')).toBe('false');
    expect(text(el(fixture, 'summary'))).toBe(
      'Nothing selected yet. Decide item by item, or Yes to all.',
    );
  });

  it.each([
    ['a review', review, 'Nothing to post; every finding was dismissed.'],
    ['an audit', audit, 'Nothing to open; every finding was dismissed.'],
  ])(
    'when every finding is dismissed the footer is replaced by the nothing-to-post line, for %s',
    async (_label, findings, sentence) => {
      const fixture = await render(makeBridge(), findings);

      for (const i of [0, 1, 2]) await click(fixture, el(fixture, 'no', card(fixture, i)));
      const footer = ['summary', 'yes-all', 'post', 'toggle-preview', 'preview'].map((test) =>
        el(fixture, test),
      );
      const line = text(el(fixture, 'all-dismissed'));
      await click(fixture, el(fixture, 'undo', card(fixture, 2)));

      expect(footer).toEqual([null, null, null, null, null]);
      expect(line).toBe(sentence);
      expect(el(fixture, 'all-dismissed')).toBeNull();
      expect(text(el(fixture, 'summary'))).toBe('0 of 1 selected · 1 undecided');
      expect(el(fixture, 'post')).not.toBeNull();
    },
  );

  it('Yes to all leaves dismissed findings dismissed', async () => {
    const fixture = await render(makeBridge());
    await click(fixture, el(fixture, 'no', card(fixture, 1)));

    await click(fixture, el(fixture, 'yes-all'));

    expect(all(fixture, 'finding').map((c) => c.classList.contains('yes'))).toEqual([
      true,
      false,
      true,
    ]);
    expect(text(el(fixture, 'dismissed', card(fixture, 1)))).toBe(
      'Dismissed: Move has no lock Undo',
    );
    expect(text(el(fixture, 'summary'))).toBe('2 of 2 selected');
    expect(text(el(fixture, 'post'))).toBe('Post 2 on PR #5');
  });

  it('Edit shows what is wrong and the fix, Use this text keeps the edited text and marks Yes, and the comment is rebuilt from it', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const edit = el(fixture, 'edit', card(fixture, 1))!;
    expect(text(edit)).toBe('Edit');

    await click(fixture, edit);
    const opened = {
      why: (el(fixture, 'comment-text', card(fixture, 1)) as HTMLTextAreaElement).value,
      fix: (el(fixture, 'fix-text', card(fixture, 1)) as HTMLTextAreaElement).value,
      toggle: text(el(fixture, 'edit', card(fixture, 1))),
    };
    await write(fixture, 'comment-text', card(fixture, 1), 'Two moves at once drop an item.');
    await write(fixture, 'fix-text', card(fixture, 1), 'Take the cart lock in Move.');
    await click(fixture, el(fixture, 'use-text', card(fixture, 1)));
    const closed = el(fixture, 'editor');
    await click(fixture, el(fixture, 'toggle-preview'));
    const preview = text(el(fixture, 'preview'));
    await click(fixture, el(fixture, 'edit', card(fixture, 1)));

    expect(opened).toEqual({ why: lock.why, fix: lock.fix, toggle: 'Close editor' });
    expect(closed).toBeNull();
    expect(card(fixture, 1).classList.contains('yes')).toBe(true);
    expect(preview).toContain('Two moves at once drop an item.');
    expect(preview).toContain('Take the cart lock in Move.');
    expect([
      (el(fixture, 'comment-text', card(fixture, 1)) as HTMLTextAreaElement).value,
      (el(fixture, 'fix-text', card(fixture, 1)) as HTMLTextAreaElement).value,
    ]).toEqual(['Two moves at once drop an item.', 'Take the cart lock in Move.']);
  });

  it('a click on Post is the approval: it goes to the desktop once as the verdict comment with the review body, the chosen comments in order with the edited body and the reviewed commit, never through the model, and while it posts the footer gives way to the posting box with the target, so Post cannot be clicked again', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, reviewed);
    const post = () => el(fixture, 'post') as HTMLButtonElement;
    expect(post().disabled).toBe(true);
    expect(text(post())).toBe('Post on PR #5');

    await click(fixture, el(fixture, 'yes', card(fixture, 2)));
    await click(fixture, el(fixture, 'edit', card(fixture, 0)));
    await write(fixture, 'comment-text', card(fixture, 0), 'Edited: sum after the discounts.');
    await click(fixture, el(fixture, 'use-text', card(fixture, 0)));

    expect(post().disabled).toBe(false);
    expect(text(post())).toBe('Post 2 on PR #5');
    expect(post().getAttribute('aria-label')).toBe('Post 2 on PR #5');
    await click(fixture, post());
    const box = el(fixture, 'posting');
    const posting = {
      post: el(fixture, 'post'),
      decide: ['yes', 'no', 'edit', 'yes-all'].map((test) => all(fixture, test).length),
      target: text(el(fixture, 'target', box ?? undefined)),
      line: text(el(fixture, 'verdict-giving', box ?? undefined)),
      bar: box?.querySelector('[role="progressbar"]')?.getAttribute('aria-label'),
    };
    await fixture.componentInstance.post();
    await settle(fixture);

    expect(posting).toEqual({
      post: null,
      decide: [0, 0, 0, 0],
      target: 'acme/shop · PR #5 · commit ec7f951',
      line: 'Posting the review… 2 comments in one review on PR #5; your click on Post was the approval.',
      bar: 'Posting the review',
    });
    expect(bridge.localAsk).not.toHaveBeenCalled();
    expect(bridge.githubGiveVerdict).toHaveBeenCalledExactlyOnceWith({
      repository: 'acme/shop',
      number: 5,
      verdict: 'comment',
      body: reviewBody(reviewed, [total, naming]),
      comments: [
        {
          path: 'src/orders/Checkout.cs',
          line: 88,
          body: commentBody({ ...total, why: 'Edited: sum after the discounts.' }),
        },
        { path: 'src/orders/Cart.cs', line: 30, body: commentBody(naming) },
      ],
      commit: 'ec7f951',
      testsLine: '',
    });
  });

  it('once GitHub took the review the cards show Review posted with the link and no buttons, and Post cannot post again; a failed post says why and keeps the buttons', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, reviewed);
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'no', card(fixture, 1)));

    await click(fixture, el(fixture, 'post'));
    bridge.verdict({
      actionId: 'action-1',
      state: 'failed',
      url: null,
      reason: 'GitHub is currently unavailable. Try again later.',
    });
    await settle(fixture);

    expect(el(fixture, 'posted')).toBeNull();
    expect(text(el(fixture, 'verdict-failed'))).toBe(
      'GitHub is currently unavailable. Try again later.',
    );
    expect((el(fixture, 'post') as HTMLButtonElement).disabled).toBe(false);
    expect(all(fixture, 'yes')).toHaveLength(2);
    expect(all(fixture, 'undo')).toHaveLength(1);

    await click(fixture, el(fixture, 'post'));
    bridge.verdict({ actionId: 'action-2', state: 'given', url: URL, reason: null });
    await settle(fixture);
    await fixture.componentInstance.post();
    await settle(fixture);

    expect(el(fixture, 'posted')?.className).toBe('posted');
    const posted = el(fixture, 'posted')!;
    expect(posted.querySelector('strong')?.textContent).toBe('Review posted on PR #5');
    expect(text(posted)).toContain(
      'Posted as one review that only comments, without approval or request for changes. The items with No stayed here in the chat.',
    );
    expect(text(posted.querySelector('a'))).toBe(URL);
    expect(el(fixture, 'post')).toBeNull();
    expect(el(fixture, 'approve')).toBeNull();
    expect(el(fixture, 'summary')).toBeNull();
    expect(all(fixture, 'yes')).toEqual([]);
    expect(all(fixture, 'no')).toEqual([]);
    expect(all(fixture, 'edit')).toEqual([]);
    expect(card(fixture, 1).classList.contains('no')).toBe(true);
    expect(text(el(fixture, 'dismissed', card(fixture, 1)))).toBe('Dismissed: Move has no lock');
    expect(all(fixture, 'undo')).toEqual([]);
    expect(bridge.githubGiveVerdict).toHaveBeenCalledTimes(2);

    await click(fixture, posted.querySelector('a'));
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: URL });
  });

  it('a review posted from the other window shows as posted here too, and a post for another pull request or commit does not', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, reviewed);
    await click(fixture, el(fixture, 'yes-all'));
    const elsewhere = {
      repository: 'acme/shop',
      number: 5,
      commit: 'ec7f951',
      verdict: 'comment',
      actionId: 'action-9',
      state: 'given',
      url: URL,
      reason: null,
    } as const;

    bridge.pushStatus({ ...elsewhere, number: 6 });
    bridge.pushStatus({ ...elsewhere, commit: '0000000' });
    await settle(fixture);
    const stillOpen = el(fixture, 'post') !== null && el(fixture, 'posted') === null;

    bridge.pushStatus(elsewhere);
    await settle(fixture);

    expect(stillOpen).toBe(true);
    expect(el(fixture, 'posted')?.querySelector('strong')?.textContent).toBe(
      'Review posted on PR #5',
    );
    expect(text(el(fixture, 'posted')?.querySelector('.posted-line') ?? null)).toBe(
      'Posted as one review that only comments, without approval or request for changes. The items with No stayed here in the chat.',
    );
    expect(text(el(fixture, 'posted')?.querySelector('a') ?? null)).toBe(URL);
    expect(el(fixture, 'post')).toBeNull();
    expect(bridge.githubGiveVerdict).not.toHaveBeenCalled();
  });

  it('a second findings card for a pull request and commit already posted, with nothing selected on it, says the review was posted and counts no comments', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, reviewed);

    bridge.pushStatus({
      repository: 'acme/shop',
      number: 5,
      commit: 'ec7f951',
      verdict: 'comment',
      actionId: 'action-9',
      state: 'given',
      url: URL,
      reason: null,
    });
    await settle(fixture);

    const line = text(el(fixture, 'posted')?.querySelector('.posted-line') ?? null);
    expect(line).toBe(
      'Posted as one review that only comments, without approval or request for changes. The items with No stayed here in the chat.',
    );
    expect(line).not.toMatch(/\d/);
  });

  it.each([
    ['a review that names its commit', reviewed, 'acme/shop · PR #5 · commit ec7f951', false],
    [
      'a review that names a full commit hash',
      { ...review, commit: 'ec7f9510a1b2c3d4e5f60718293a4b5c6d7e8f90' },
      'acme/shop · PR #5 · commit ec7f951',
      false,
    ],
    ['a review that names no commit', review, 'acme/shop · PR #5', true],
  ] as const)(
    'the card says what a click on Post, Approve or Request changes writes to, beside those buttons: %s',
    async (_label, findings, target, postOff) => {
      const fixture = await render(makeBridge(), findings);
      await click(fixture, el(fixture, 'yes', card(fixture, 0)));

      const line = el(fixture, 'target');
      expect(text(line)).toBe(target);
      expect(line?.parentElement?.contains(el(fixture, 'post'))).toBe(true);
      expect((el(fixture, 'post') as HTMLButtonElement).disabled).toBe(postOff);
    },
  );

  it('an audit card, whose Open button names the repository, shows no pull request line', async () => {
    const fixture = await render(makeBridge(), audit);

    expect(el(fixture, 'target')).toBeNull();
  });

  it('a done turn that only says a review was posted, with a GitHub link, never marks the cards posted', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, reviewed);
    const findingsTurn = (await bridge.localGetTurns())[0]!;

    bridge.push([
      findingsTurn,
      {
        id: 'p-said',
        question: 'Post 2 findings on acme/shop#5',
        reply: `Posted the review: [PR #5](${URL}).`,
        citations: [],
        state: 'done',
        review: true,
      },
    ]);
    await settle(fixture);

    expect(el(fixture, 'posted')).toBeNull();
    expect(el(fixture, 'post')).not.toBeNull();
  });

  it('a post main refuses shows the refusal and keeps the buttons; without a reviewed commit Post is off with the reason', async () => {
    const bridge = makeBridge();
    bridge.githubGiveVerdict.mockRejectedValueOnce(
      new Error(
        '[scout:control-plane/failed-precondition] The owner has not allowed github to write; allow it on the Features screen of Scout Desktop.',
      ),
    );
    const fixture = await render(bridge, reviewed);
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));

    await click(fixture, el(fixture, 'post'));
    const refused = {
      line: text(el(fixture, 'verdict-failed')),
      post: (el(fixture, 'post') as HTMLButtonElement).disabled,
      posted: el(fixture, 'posted'),
    };
    fixture.componentRef.setInput('review', review);
    await settle(fixture);
    const post = el(fixture, 'post') as HTMLButtonElement;
    await fixture.componentInstance.post();

    expect(refused).toEqual({
      line: 'The owner has not allowed github to write; allow it on the Features screen of Scout Desktop.',
      post: false,
      posted: null,
    });
    expect({ disabled: post.disabled, title: post.title }).toEqual({
      disabled: true,
      title: 'The review names no commit to pin the verdict to.',
    });
    expect(bridge.githubGiveVerdict).toHaveBeenCalledTimes(1);
    expect(bridge.localAsk).not.toHaveBeenCalled();
  });

  it('a findings turn that leaves the conversation, as on Clear, forgets its picks', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const findingsTurn = (await bridge.localGetTurns())[0]!;
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    expect(text(el(fixture, 'summary'))).toBe('1 of 3 selected · 2 undecided');

    bridge.push([]);
    await settle(fixture);
    bridge.push([findingsTurn]);
    await settle(fixture);

    expect(text(el(fixture, 'summary'))).toBe(
      'Nothing selected yet. Decide item by item, or Yes to all.',
    );
  });

  it('the preview shows the review body and one comment box per selected finding', async () => {
    const fixture = await render(makeBridge());
    const toggle = el(fixture, 'toggle-preview')!;
    expect(text(toggle)).toBe('See how it reads on the PR');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(el(fixture, 'preview')).toBeNull();

    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'yes', card(fixture, 2)));
    await click(fixture, toggle);

    expect(text(el(fixture, 'toggle-preview'))).toBe('Hide the preview');
    expect(el(fixture, 'toggle-preview')!.getAttribute('aria-expanded')).toBe('true');
    const preview = el(fixture, 'preview')!;
    const summary = preview.querySelector('.gh-review .gh-body')!;
    expect(summary.querySelector('strong')?.textContent).toBe('Verdict:');
    expect(Array.from(summary.querySelectorAll('li code')).map(text)).toEqual([
      'src/orders/Checkout.cs:88',
      'src/orders/Cart.cs:30',
    ]);
    expect(Array.from(preview.querySelectorAll('.gh-file')).map(text)).toEqual([
      'src/orders/Checkout.cs',
      'src/orders/Cart.cs',
    ]);
    const comments = Array.from(preview.querySelectorAll('.gh-comment .gh-body'));
    expect(comments).toHaveLength(2);
    expect(comments[0]!.querySelector('strong')?.textContent).toBe('Fix:');
    expect(text(comments[0]!.querySelector('p'))).toContain('Discounts applied after the sum');
    expect(text(comments[1]!.querySelector('p'))).toContain('Name says the wrong thing');
  });

  it('a review card shows its rule line when the finding has one, and none otherwise', async () => {
    const fixture = await render(makeBridge(), {
      ...review,
      findings: [{ ...total, rule: 'Defect' }, lock],
    });

    expect(text(el(fixture, 'rule', card(fixture, 0)))).toBe('Rule: Defect');
    expect(el(fixture, 'rule', card(fixture, 1))).toBeNull();
    expect(el(fixture, 'known')).toBeNull();
    expect(text(el(fixture, 'yes', card(fixture, 0)))).toBe('Yes, add to the PR');
  });

  it('an audit block renders the cards with the rule line and the already-documented note', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, audit);

    const cards = all(fixture, 'finding');
    expect(cards.map((c) => text(c.querySelector('h3')))).toEqual([
      'Discounts applied after the sum',
      'Move has no lock',
      'Name says the wrong thing',
    ]);
    expect(cards.map((c) => text(el(fixture, 'severity', c)))).toEqual([
      'Blocking',
      'Fix',
      'Suggestion',
    ]);
    expect(cards.map((c) => el(fixture, 'rule', c)?.textContent ?? null)).toEqual([
      'Rule: ADR 0007: totals are computed after discounts',
      null,
      'Rule: Defect',
    ]);
    const known = el(fixture, 'known');
    expect(text(known?.querySelector('h4') ?? null)).toBe('Already documented, not raised again');
    expect(cards.some((c) => c.contains(known))).toBe(false);
    expect(Array.from(known?.querySelectorAll('li') ?? []).map(text)).toEqual([
      'Refunds are manual until v2, as the README says.',
      'There is no dark mode yet.',
    ]);
    expect(text(el(fixture, 'yes', cards[0]))).toBe('Yes, open an issue');
    expect(text(el(fixture, 'post'))).toBe('Open issues on acme/shop');

    await click(fixture, el(fixture, 'path', cards[1]));
    expect(bridge.appOpenExternal).toHaveBeenLastCalledWith({
      url: 'https://github.com/acme/shop/blob/ec7f951/src/orders/Cart.cs#L12',
    });
    const { commit: _commit, ...atHead } = audit;
    fixture.componentRef.setInput('review', atHead);
    await settle(fixture);
    await click(fixture, el(fixture, 'path', card(fixture, 0)));
    expect(bridge.appOpenExternal).toHaveBeenLastCalledWith({
      url: 'https://github.com/acme/shop/blob/HEAD/src/orders/Checkout.cs#L88',
    });
  });

  it('Open issues is disabled until a Yes and then asks the worker with the bare question, the issues marker and one issue per chosen finding in order with the edited body', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, audit);
    const post = () => el(fixture, 'post') as HTMLButtonElement;
    expect(post().disabled).toBe(true);

    await click(fixture, el(fixture, 'yes', card(fixture, 2)));
    await click(fixture, el(fixture, 'edit', card(fixture, 0)));
    const area = el(fixture, 'comment-text', card(fixture, 0)) as HTMLTextAreaElement;
    expect(area.value).toBe(audit.findings[0]!.why);
    await write(fixture, 'comment-text', card(fixture, 0), 'Edited: sum after the discounts.');
    await click(fixture, el(fixture, 'use-text', card(fixture, 0)));

    expect(post().disabled).toBe(false);
    expect(text(post())).toBe('Open 2 issues on acme/shop');
    expect(post().getAttribute('aria-label')).toBe('Open 2 issues on acme/shop');
    await click(fixture, post());

    expect(bridge.localAsk).toHaveBeenCalledTimes(1);
    const sent = bridge.localAsk.mock.calls[0]![0] as { text: string };
    expect(sent.text).not.toContain(REVIEW_MARKER);
    const [question, json] = sent.text.split(ISSUES_MARKER);
    expect(question).toBe('Open 2 issues on acme/shop');
    expect(JSON.parse(json!)).toEqual({
      repository: 'acme/shop',
      issues: [
        {
          title: 'Discounts applied after the sum',
          body: issueBody(audit, {
            ...audit.findings[0]!,
            why: 'Edited: sum after the discounts.',
          }),
        },
        { title: 'Name says the wrong thing', body: issueBody(audit, audit.findings[2]!) },
      ],
    });
  });

  it('the audit preview shows each chosen issue with its title and its body', async () => {
    const fixture = await render(makeBridge(), audit);
    expect(text(el(fixture, 'toggle-preview'))).toBe('See how the issues read');

    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'yes', card(fixture, 1)));
    await click(fixture, el(fixture, 'toggle-preview'));

    const preview = el(fixture, 'preview')!;
    expect(Array.from(preview.querySelectorAll('.gh-title')).map(text)).toEqual([
      'Discounts applied after the sum',
      'Move has no lock',
    ]);
    const bodies = Array.from(preview.querySelectorAll('.gh-body'));
    expect(Array.from(bodies[0]!.querySelectorAll('h2')).map(text)).toEqual([
      'Problem',
      'Evidence',
      'Proposed change',
    ]);
    expect(text(bodies[0]!)).toContain('Rule: ADR 0007: totals are computed after discounts');
    expect(text(bodies[1]!)).toContain('Drafted by Scout from an audit of acme/shop at ec7f951.');
    expect(preview.querySelector('.gh-review')).toBeNull();
  });

  it('once the issues turn completes with the issue links the cards show them opened, in this window or the other; a failed turn sends nothing and keeps the buttons', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, audit);
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'yes', card(fixture, 2)));
    const lastId = () => (bridge.localAsk.mock.lastCall![0] as { id: string }).id;

    await click(fixture, el(fixture, 'post'));
    bridge.emit({ id: lastId(), event: 'fail', text: 'GitHub refused the issue.' });
    await settle(fixture);
    const buttons = () => [
      el(fixture, 'posted'),
      (el(fixture, 'post') as HTMLButtonElement | null)?.disabled ?? null,
    ];
    expect(buttons()).toEqual([null, false]);
    expect(all(fixture, 'sent')).toEqual([]);
    expect(text(el(fixture, 'post'))).toBe('Open 2 issues on acme/shop');

    await click(fixture, el(fixture, 'post'));
    bridge.emit({
      id: lastId(),
      event: 'complete',
      text: `[${ISSUE_12}](${ISSUE_12})\n${ISSUE_13}.\nhttps://github.com/acme/other/issues/9`,
    });
    await settle(fixture);

    const posted = el(fixture, 'posted')!;
    expect(text(posted.querySelector('strong'))).toBe('2 issues opened on acme/shop');
    expect(Array.from(posted.querySelectorAll('a')).map(text)).toEqual([ISSUE_12, ISSUE_13]);
    expect(el(fixture, 'post')).toBeNull();
    expect(all(fixture, 'yes')).toEqual([]);
    await click(fixture, posted.querySelectorAll('a')[1]);
    expect(bridge.appOpenExternal).toHaveBeenLastCalledWith({ url: ISSUE_13 });

    TestBed.resetTestingModule();
    const other = makeBridge();
    const elsewhere = await render(other, audit);
    const findingsTurn = (await other.localGetTurns())[0]!;
    const issuesFrom = (id: string, repository: string, reply: string): LocalTurn => ({
      id,
      question: `Open 1 issue on ${repository}`,
      reply,
      citations: [],
      state: 'done',
      issues: true,
    });
    other.push([
      findingsTurn,
      issuesFrom('i-other', 'acme/other', 'https://github.com/acme/other/issues/9'),
    ]);
    await settle(elsewhere);
    const stillOpen = el(elsewhere, 'post') !== null && el(elsewhere, 'posted') === null;
    other.push([findingsTurn, issuesFrom('i-elsewhere', 'acme/shop', `Opened ${ISSUE_12}`)]);
    await settle(elsewhere);

    expect(stillOpen).toBe(true);
    expect(text(el(elsewhere, 'posted')?.querySelector('strong') ?? null)).toBe(
      '1 issue opened on acme/shop',
    );
    expect(all(elsewhere, 'posted')[0]!.querySelectorAll('a')).toHaveLength(1);
    expect(el(elsewhere, 'post')).toBeNull();
  });

  it('once the issues turn completes, in whatever words, the findings it carried show as Issue queued without Yes or No and leave Open, in this window or the other, so a second click cannot queue duplicates', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, audit);
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'yes', card(fixture, 2)));

    await click(fixture, el(fixture, 'post'));
    bridge.emit({
      id: (bridge.localAsk.mock.lastCall![0] as { id: string }).id,
      event: 'complete',
      text: 'Queued for your approval:\n- Discounts applied after the sum\n- Name says the wrong thing',
    });
    await settle(fixture);
    await fixture.componentInstance.post();
    await settle(fixture);

    const rows = () =>
      all(fixture, 'finding').map((c) => ({
        sent: text(el(fixture, 'sent-mark', c)) || null,
        yes: all(fixture, 'yes', c).length,
        no: all(fixture, 'no', c).length,
      }));
    expect(rows()).toEqual([
      { sent: 'Issue queued', yes: 0, no: 0 },
      { sent: null, yes: 1, no: 1 },
      { sent: 'Issue queued', yes: 0, no: 0 },
    ]);
    expect(el(fixture, 'posted')).toBeNull();
    expect(text(el(fixture, 'post'))).toBe('Open issues on acme/shop');
    expect((el(fixture, 'post') as HTMLButtonElement).disabled).toBe(true);
    expect(bridge.localAsk).toHaveBeenCalledTimes(1);

    await click(fixture, el(fixture, 'yes-all'));
    expect(text(el(fixture, 'post'))).toBe('Open 1 issue on acme/shop');

    TestBed.resetTestingModule();
    const other = makeBridge();
    const elsewhere = await render(other, audit);
    const findingsTurn = (await other.localGetTurns())[0]!;
    other.push([
      findingsTurn,
      {
        id: 'i-elsewhere',
        question: 'Open 1 issue on acme/shop',
        reply:
          "Queued for the owner's approval: an Approve card is waiting in the chat and in Approvals of Scout Desktop.",
        citations: [],
        state: 'done',
        issues: true,
        issueTitles: ['Move has no lock'],
      },
    ]);
    await settle(elsewhere);

    expect(
      all(elsewhere, 'finding').map((c) => text(el(elsewhere, 'sent-mark', c)) || null),
    ).toEqual([null, 'Issue queued', null]);
    expect(text(el(elsewhere, 'post'))).toBe('Open issues on acme/shop');
  });

  it('an audit asked after earlier issues on the same repository starts open', async () => {
    const bridge = makeBridge();
    const findingsTurn = (await bridge.localGetTurns())[0]!;
    const earlierIssues: LocalTurn = {
      id: 'i-earlier',
      question: 'Open 2 issues on acme/shop',
      reply: `${ISSUE_12}\n${ISSUE_13}`,
      citations: [],
      state: 'done',
      issues: true,
    };
    bridge.push([earlierIssues, findingsTurn]);
    const fixture = await render(bridge, audit);

    expect(el(fixture, 'posted')).toBeNull();
    expect(el(fixture, 'post')).not.toBeNull();
  });

  it('a title counts as sent only on the repository its issues turn named, compared without case on the repository, and only as written once trimmed', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, audit);
    const findingsTurn = (await bridge.localGetTurns())[0]!;
    const issuesTurn = (id: string, repository: string, titles: string[]): LocalTurn => ({
      id,
      question: `Open ${titles.length} issues on ${repository}`,
      reply: 'Sent them on.',
      citations: [],
      state: 'done',
      issues: true,
      issueTitles: titles,
    });
    const view = async (...turns: LocalTurn[]) => {
      bridge.push([findingsTurn, ...turns]);
      await settle(fixture);
      return all(fixture, 'finding').map((c) => text(el(fixture, 'sent-mark', c)) || null);
    };

    const otherRepository = await view(
      issuesTurn('i-other', 'acme/other', ['Discounts applied after the sum']),
    );
    const otherCaseOrSpacing = await view(
      issuesTurn('i-case', 'acme/shop', ['discounts applied after the sum', 'Move  has no lock']),
    );
    const repositoryCase = await view(
      issuesTurn('i-repo', 'Acme/Shop', ['  Name says the wrong thing ']),
    );
    await click(fixture, el(fixture, 'yes-all'));

    expect(otherRepository).toEqual([null, null, null]);
    expect(otherCaseOrSpacing).toEqual([null, null, null]);
    expect(repositoryCase).toEqual([null, null, 'Issue queued']);
    expect(text(el(fixture, 'post'))).toBe('Open 2 issues on acme/shop');
  });

  it('two findings of one audit with the same title, trimmed, both read as sent once one of them is sent, since the title is the only identity an issue has', async () => {
    const bridge = makeBridge();
    const twin: ReviewFinding = { ...lock, id: 'lock-twin', title: ' Move has no lock ', line: 64 };
    const fixture = await render(bridge, { ...audit, findings: [lock, twin, naming] });

    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    const label = text(el(fixture, 'post'));
    await click(fixture, el(fixture, 'post'));
    const asked = bridge.localAsk.mock.lastCall![0] as { id: string; text: string };
    bridge.emit({ id: asked.id, event: 'complete', text: 'Sent them on.' });
    await settle(fixture);

    expect(label).toBe('Open 1 issue on acme/shop');
    expect(all(fixture, 'finding').map((c) => text(el(fixture, 'sent-mark', c)) || null)).toEqual([
      'Issue queued',
      'Issue queued',
      null,
    ]);
    expect(all(fixture, 'yes')).toHaveLength(1);
  });

  it('issues already sent leave the count and are shown as sent, and the number on Open is the number still to be sent', async () => {
    const bridge = makeBridge();
    bridge.push([
      auditBatch('a-1', numberedRange(1, 20)),
      auditBatch('a-2', numberedRange(15, 29)),
    ]);
    const merged = findingsCards(await bridge.localGetTurns()).get('a-2')!;
    const fixture = await render(bridge, merged.review, merged.anchor);
    const batch = async (id: string, findings: readonly ReviewFinding[]) => {
      bridge.push([...(await bridge.localGetTurns()), auditBatch(id, findings)]);
      fixture.componentRef.setInput(
        'review',
        findingsCards(await bridge.localGetTurns()).get(id)!.review,
      );
      await settle(fixture);
    };
    const lastAsk = () => bridge.localAsk.mock.lastCall![0] as { id: string; text: string };
    const asked = () => {
      const [question, json] = lastAsk().text.split(ISSUES_MARKER);
      const { issues } = JSON.parse(json!) as { issues: { title: string }[] };
      return { question, titles: issues.map((issue) => issue.title) };
    };
    const view = () => ({
      cards: all(fixture, 'finding').length,
      sent: all(fixture, 'sent-mark').filter((m) => text(m) === 'Issue queued').length,
      yes: all(fixture, 'yes').length,
      no: all(fixture, 'no').length,
      open: el(fixture, 'post') === null ? null : text(el(fixture, 'post')),
      allSent: text(el(fixture, 'all-sent')?.querySelector('strong') ?? null) || null,
    });
    const titles = (from: number, to: number) => numberedRange(from, to).map((f) => f.title);

    await click(fixture, el(fixture, 'yes-all'));
    const before = view();
    await click(fixture, el(fixture, 'post'));
    bridge.emit({ id: lastAsk().id, event: 'complete', text: QUEUED_IN_PORTUGUESE });
    await settle(fixture);
    const afterTwentyNine = view();

    await batch('a-3', [...numberedRange(27, 29), ...numberedRange(30, 33)]);
    await click(fixture, el(fixture, 'yes-all'));
    const afterFour = { ...view(), summary: text(el(fixture, 'summary')) };
    await click(fixture, el(fixture, 'toggle-preview'));
    const previewed = all(fixture, 'preview')[0]!.querySelectorAll('.gh-title');
    const preview = Array.from(previewed).map((t) => text(t));
    await click(fixture, el(fixture, 'post'));
    const fourAsked = asked();
    bridge.emit({ id: lastAsk().id, event: 'complete', text: QUEUED_IN_PORTUGUESE });
    await settle(fixture);

    await batch('a-4', numberedRange(34, 35));
    await click(fixture, el(fixture, 'yes-all'));
    const afterTwo = view();
    await click(fixture, el(fixture, 'post'));
    const twoAsked = asked();
    bridge.emit({
      id: lastAsk().id,
      event: 'complete',
      text: 'I queued the 2 new ones; the other 33 were queued earlier.',
    });
    await settle(fixture);

    expect(before).toEqual({
      cards: 29,
      sent: 0,
      yes: 29,
      no: 29,
      open: 'Open 29 issues on acme/shop',
      allSent: null,
    });
    expect(afterTwentyNine).toEqual({
      cards: 29,
      sent: 29,
      yes: 0,
      no: 0,
      open: null,
      allSent: 'Every chosen issue is queued on acme/shop',
    });
    expect(afterFour).toEqual({
      cards: 33,
      sent: 29,
      yes: 4,
      no: 4,
      open: 'Open 4 issues on acme/shop',
      allSent: null,
      summary: '4 of 4 selected',
    });
    expect(preview).toEqual(titles(30, 33));
    expect(fourAsked).toEqual({ question: 'Open 4 issues on acme/shop', titles: titles(30, 33) });
    expect(afterTwo).toEqual({
      cards: 35,
      sent: 33,
      yes: 2,
      no: 2,
      open: 'Open 2 issues on acme/shop',
      allSent: null,
    });
    expect(twoAsked).toEqual({ question: 'Open 2 issues on acme/shop', titles: titles(34, 35) });
    expect(view()).toEqual({
      cards: 35,
      sent: 35,
      yes: 0,
      no: 0,
      open: null,
      allSent: 'Every chosen issue is queued on acme/shop',
    });
    expect(bridge.localAsk).toHaveBeenCalledTimes(3);
  });

  it('the tests show as the artboard chips above the cards, each check on its status pair as a link to its details when it has one, then the container run that passed with its time and command; nothing when the review carries neither', async () => {
    const bridge = makeBridge();
    const checks = [
      {
        name: 'build',
        status: 'completed',
        conclusion: 'success',
        url: 'https://github.com/acme/shop/runs/1',
      },
      {
        name: 'lint',
        status: 'completed',
        conclusion: 'failure',
        url: 'https://github.com/acme/shop/runs/2',
      },
      {
        name: 'docs',
        status: 'completed',
        conclusion: 'skipped',
        url: 'https://github.com/acme/shop/runs/3',
      },
      {
        name: 'e2e',
        status: 'completed',
        conclusion: 'timed_out',
        url: 'https://github.com/acme/shop/runs/4',
      },
      { name: 'deploy', status: 'in_progress', conclusion: null, url: 'https://ci.example.com/7' },
      { name: 'ci/legacy', status: 'completed', conclusion: 'failure', url: '' },
    ];
    const fixture = await render(bridge, {
      ...review,
      checks,
      tests: { ran: true, command: 'dotnet test', exitCode: 0, durationSeconds: 41.6 },
    });
    const rows = all(fixture, 'check').map((row) => ({
      chip: text(row),
      tone: row.className.replace('chip', '').trim(),
      link: row.tagName === 'A',
    }));
    await click(fixture, all(fixture, 'check')[1]);
    const run = text(el(fixture, 'tests-run'));
    const output = el(fixture, 'tests-output');
    TestBed.resetTestingModule();
    const bare = await render(makeBridge());

    expect(rows).toEqual([
      { chip: 'build passed', tone: 'passed', link: true },
      { chip: 'lint failed', tone: 'failed', link: true },
      { chip: 'docs passed', tone: 'passed', link: true },
      { chip: 'e2e failed', tone: 'failed', link: true },
      { chip: 'deploy pending', tone: 'pending', link: true },
      { chip: 'ci/legacy failed', tone: 'failed', link: false },
    ]);
    expect(bridge.appOpenExternal).toHaveBeenCalledExactlyOnceWith({
      url: 'https://github.com/acme/shop/runs/2',
    });
    expect(run).toBe('tests passed in 42 s (dotnet test)');
    expect(output).toBeNull();
    expect(el(bare, 'tests')).toBeNull();
  });

  it('a container run that failed shows its exit code as a chip and the last output behind Last output, as text; one that did not run says why', async () => {
    const failed = await render(makeBridge(), {
      ...review,
      tests: {
        ran: true,
        command: 'npm test',
        exitCode: 2,
        durationSeconds: 9,
        output: '<img src=x onerror=alert(1)> 3 failing',
      },
    });
    const failure = {
      run: text(el(failed, 'tests-run')),
      summary: text(el(failed, 'tests-output')!.querySelector('summary')),
      output: el(failed, 'tests-output')!.querySelector('pre')!.textContent,
      images: el(failed, 'tests-output')!.querySelectorAll('img').length,
      checks: all(failed, 'check').length,
    };
    TestBed.resetTestingModule();
    const skipped = await render(makeBridge(), {
      ...review,
      tests: { ran: false, reason: 'Docker is not installed.' },
    });

    expect(failure).toEqual({
      run: 'tests failed (exit 2)',
      summary: 'Last output',
      output: '<img src=x onerror=alert(1)> 3 failing',
      images: 0,
      checks: 0,
    });
    expect(text(el(skipped, 'tests-run'))).toBe('tests did not run: Docker is not installed.');
    expect(el(skipped, 'tests-output')).toBeNull();
  });

  it('as the artboard draws them, Yes to all and Post share a row, and Approve and Request changes sit in a Verdict group under it on a review and never on an audit; Approve is hidden when the owner wrote the pull request', async () => {
    const buttons = (fixture: Fixture) => {
      const root = fixture.nativeElement as HTMLElement;
      const verdict = root.querySelector('.footer [role="group"][aria-label="Verdict"]');
      return {
        row: Array.from(root.querySelectorAll('.footer .group button')).map((button) =>
          button.getAttribute('data-test'),
        ),
        verdict:
          verdict === null
            ? null
            : [
                text(verdict.querySelector('.verdict-label')),
                ...Array.from(verdict.querySelectorAll('button')).map((button) =>
                  button.getAttribute('data-test'),
                ),
              ],
      };
    };

    const onReview = buttons(await render(makeBridge()));
    TestBed.resetTestingModule();
    const own = buttons(await render(makeBridge(), { ...review, selfAuthored: true }));
    TestBed.resetTestingModule();
    const onAudit = buttons(await render(makeBridge(), audit));

    expect(onReview).toEqual({
      row: ['yes-all', 'post'],
      verdict: ['Verdict on the reviewed commit', 'approve', 'request-changes'],
    });
    expect(own).toEqual({
      row: ['yes-all', 'post'],
      verdict: ['Verdict on the reviewed commit', 'request-changes'],
    });
    expect(onAudit).toEqual({ row: ['yes-all', 'post'], verdict: null });
  });

  it('a click on Approve gives the verdict through the desktop as one action: the review body, the chosen findings as comments, the reviewed commit and the tests line; nothing goes before a click', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, {
      ...reviewed,
      tests: { ran: false, reason: 'No Docker.' },
    });
    await click(fixture, el(fixture, 'yes', card(fixture, 1)));
    await click(fixture, el(fixture, 'no', card(fixture, 0)));
    await click(fixture, el(fixture, 'yes-all'));
    await click(fixture, el(fixture, 'toggle-preview'));
    const beforeClick = bridge.githubGiveVerdict.mock.calls.length;

    await click(fixture, el(fixture, 'approve'));

    expect(beforeClick).toBe(0);
    expect(bridge.localAsk).not.toHaveBeenCalled();
    expect(bridge.githubGiveVerdict).toHaveBeenCalledExactlyOnceWith({
      repository: 'acme/shop',
      number: 5,
      verdict: 'approve',
      body: reviewBody(reviewed, [lock, naming]),
      comments: [
        { path: 'src/orders/Cart.cs', line: 12, body: commentBody(lock) },
        { path: 'src/orders/Cart.cs', line: 30, body: commentBody(naming) },
      ],
      commit: 'ec7f951',
      testsLine: 'Tests did not run: No Docker.',
    });
    expect(bridge.githubVerdictStatus).toHaveBeenCalledExactlyOnceWith({ actionId: 'action-1' });
  });

  it('while the action is queued or running the Verdict group is gone and Post is off, with Giving the verdict…, a second click sends nothing; once given the card says Approved on PR #5 with the link and loses its buttons', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, reviewed);
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'approve'));
    const off = () => ({
      line: text(el(fixture, 'verdict-giving')),
      verdictGroup: (fixture.nativeElement as HTMLElement).querySelector('[aria-label="Verdict"]'),
      verdictButtons: all(fixture, 'approve').length + all(fixture, 'request-changes').length,
      post: (el(fixture, 'post') as HTMLButtonElement).disabled,
    });
    const queued = off();
    fixture.componentInstance.giveVerdict('request_changes');
    bridge.verdict({ actionId: 'action-1', state: 'running', url: null, reason: null });
    await settle(fixture);
    const running = off();

    bridge.verdict({ actionId: 'action-1', state: 'given', url: URL, reason: null });
    await settle(fixture);
    const given = el(fixture, 'verdict-given');

    expect(queued).toEqual({
      line: 'Giving the verdict…',
      verdictGroup: null,
      verdictButtons: 0,
      post: true,
    });
    expect(running).toEqual(queued);
    expect(bridge.githubGiveVerdict).toHaveBeenCalledTimes(1);
    expect({
      title: text(given?.querySelector('strong') ?? null),
      link: text(given?.querySelector('a') ?? null),
      posted: el(fixture, 'posted'),
      giving: el(fixture, 'verdict-giving'),
      buttons: ['approve', 'request-changes', 'post', 'yes', 'no', 'edit', 'toggle-preview'].map(
        (test) => all(fixture, test).length,
      ),
    }).toEqual({
      title: 'Approved on PR #5',
      link: URL,
      posted: null,
      giving: null,
      buttons: [0, 0, 0, 0, 0, 0, 0],
    });
  });

  it('a failed action shows its reason and brings the buttons back, and the next click is a new action; Request changes given reads Changes requested on PR #5, never Review posted', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, reviewed);
    await click(fixture, el(fixture, 'request-changes'));
    bridge.verdict({
      actionId: 'action-1',
      state: 'failed',
      reason: 'The pull request moved since the review.',
      url: null,
    });
    await settle(fixture);
    const failed = {
      reason: text(el(fixture, 'verdict-failed')),
      enabled: ['approve', 'request-changes'].map(
        (test) => !(el(fixture, test) as HTMLButtonElement).disabled,
      ),
    };

    await click(fixture, el(fixture, 'request-changes'));
    bridge.verdict({ actionId: 'action-2', state: 'given', url: URL, reason: null });
    await settle(fixture);

    expect(failed).toEqual({
      reason: 'The pull request moved since the review.',
      enabled: [true, true],
    });
    expect(bridge.githubGiveVerdict).toHaveBeenCalledTimes(2);
    expect(text(el(fixture, 'verdict-given')?.querySelector('strong') ?? null)).toBe(
      'Changes requested on PR #5',
    );
    expect(el(fixture, 'posted')).toBeNull();
  });

  it('a click main refuses shows the refusal and keeps the buttons; without a reviewed commit the verdict buttons are off', async () => {
    const bridge = makeBridge();
    bridge.githubGiveVerdict.mockRejectedValueOnce(
      new Error('[scout:github/verdict-refused] The control plane is not running.'),
    );
    const fixture = await render(bridge, reviewed);
    await click(fixture, el(fixture, 'approve'));
    const refused = {
      reason: text(el(fixture, 'verdict-failed')),
      enabled: !(el(fixture, 'approve') as HTMLButtonElement).disabled,
    };
    TestBed.resetTestingModule();
    const unpinned = await render(makeBridge(), review);
    await click(unpinned, el(unpinned, 'approve'));

    expect(refused).toEqual({ reason: 'The control plane is not running.', enabled: true });
    expect(
      ['approve', 'request-changes'].map(
        (test) => (el(unpinned, test) as HTMLButtonElement).disabled,
      ),
    ).toEqual([true, true]);
    expect(el(unpinned, 'approve')?.title).toBe(
      'The review names no commit to pin the verdict to.',
    );
  });

  it('when every finding is dismissed a review still offers Approve and Request changes beside the nothing-to-post line', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, reviewed);
    for (const index of [0, 1, 2]) await click(fixture, el(fixture, 'no', card(fixture, index)));

    await click(fixture, el(fixture, 'approve'));

    expect(text(el(fixture, 'all-dismissed'))).toBe(
      'Nothing to post; every finding was dismissed.',
    );
    expect(bridge.githubGiveVerdict.mock.calls[0]![0]).toEqual(
      expect.objectContaining({ verdict: 'approve', comments: [] }),
    );
  });

  it('a pick is written to main as the decisions of that findings turn: Yes, No, Undo and Yes to all each send the whole decisions', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'no', card(fixture, 1)));
    await click(fixture, el(fixture, 'undo', card(fixture, 1)));
    await click(fixture, el(fixture, 'no', card(fixture, 2)));
    await click(fixture, el(fixture, 'yes-all'));

    const yes = { pick: 'yes', dismissed: false };
    const no = { pick: 'no', dismissed: true };
    expect(bridge.localSetDecisions.mock.calls.map(([req]) => req)).toEqual([
      { turnId: 'f-1', decisions: { total: yes } },
      { turnId: 'f-1', decisions: { total: yes, lock: no } },
      { turnId: 'f-1', decisions: { total: yes, lock: { pick: null, dismissed: false } } },
      {
        turnId: 'f-1',
        decisions: { total: yes, lock: { pick: null, dismissed: false }, naming: no },
      },
      { turnId: 'f-1', decisions: { total: yes, lock: yes, naming: no } },
    ]);
  });

  it('decisions made in the other window show here: a No there shows the card dismissed, Yes to all there marks every undecided card, an edited text there is the comment Post sends here', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, reviewed);
    const findingsTurn = (await bridge.localGetTurns())[0]!;
    const fromOtherWindow = async (decisions: Record<string, LocalFindingDecision>) => {
      bridge.push([{ ...findingsTurn, decisions }]);
      await settle(fixture);
    };

    await fromOtherWindow({ lock: { pick: 'no', dismissed: true } });
    const dismissed = all(fixture, 'dismissed').map((line) => text(line));
    await fromOtherWindow({
      lock: { pick: 'no', dismissed: true },
      total: {
        pick: 'yes',
        dismissed: false,
        edited: { why: 'Edited elsewhere.', fix: 'Sum later.' },
      },
      naming: { pick: 'yes', dismissed: false },
    });
    const summary = text(el(fixture, 'summary'));
    await click(fixture, el(fixture, 'post'));

    expect(dismissed).toEqual(['Dismissed: Move has no lock Undo']);
    expect(summary).toBe('2 of 2 selected');
    expect(bridge.localSetDecisions).not.toHaveBeenCalled();
    expect(bridge.githubGiveVerdict.mock.calls[0]![0].comments[0]!.body).toBe(
      commentBody({ ...total, why: 'Edited elsewhere.', fix: 'Sum later.' }),
    );
  });

  it('a decisions write main refuses puts the pick back', async () => {
    const bridge = makeBridge();
    bridge.localSetDecisions.mockRejectedValueOnce(
      new Error('[scout:local/invalid-request] Scout could not read those decisions.'),
    );
    const fixture = await render(bridge);

    await click(fixture, el(fixture, 'yes', card(fixture, 0)));

    expect(card(fixture, 0).classList.contains('yes')).toBe(false);
    expect(text(el(fixture, 'summary'))).toBe(
      'Nothing selected yet. Decide item by item, or Yes to all.',
    );
  });
  it('one verdict is one action per pull request and commit: a second give while it is queued, running or given sends nothing, and after it failed the next one is a new action', async () => {
    const bridge = makeBridge();
    await render(bridge, reviewed);
    const store = TestBed.inject(ReviewFindingsStore);
    const request = (verdict: GitHubVerdictRequest['verdict']): GitHubVerdictRequest => ({
      repository: 'acme/shop',
      number: 5,
      verdict,
      body: 'b',
      comments: [],
      commit: 'ec7f951',
      testsLine: '',
    });
    const states: (string | undefined)[] = [];
    const give = async (verdict: GitHubVerdictRequest['verdict']) => {
      await store.giveVerdict(request(verdict));
      states.push(store.verdict('acme/shop', 5, 'ec7f951')?.state);
    };

    await give('approve');
    await give('request_changes');
    bridge.verdict({ actionId: 'action-1', state: 'running', url: null, reason: null });
    await give('approve');
    bridge.verdict({ actionId: 'action-1', state: 'failed', url: null, reason: 'No.' });
    await give('request_changes');
    bridge.verdict({ actionId: 'action-2', state: 'given', url: URL, reason: null });
    await give('approve');
    await store.giveVerdict({ ...request('approve'), commit: 'f00ba47' });

    expect(states).toEqual(['queued', 'queued', 'running', 'queued', 'given']);
    expect(bridge.githubGiveVerdict.mock.calls.map(([r]) => [r.verdict, r.commit])).toEqual([
      ['approve', 'ec7f951'],
      ['request_changes', 'ec7f951'],
      ['approve', 'f00ba47'],
    ]);
  });
  it('a window opened after the click looks the verdict up and shows it in progress, then given by the push for its key, with no click of its own; each findings turn looks it up once', async () => {
    const bridge = makeBridge();
    bridge.githubVerdictFor.mockResolvedValue({
      ...VERDICT_KEY,
      verdict: 'request_changes',
      actionId: 'action-from-the-other-window',
      state: 'running',
      url: null,
      reason: null,
    });
    const fixture = await render(bridge, reviewed);
    const inProgress = {
      line: text(el(fixture, 'verdict-giving')),
      verdictButtons: ['approve', 'request-changes'].map((test) => all(fixture, test).length),
    };
    bridge.verdict({
      actionId: 'action-from-the-other-window',
      state: 'given',
      url: URL,
      reason: null,
    });
    await settle(fixture);
    const given = text(el(fixture, 'verdict-given')?.querySelector('strong') ?? null);
    fixture.componentRef.setInput('review', { ...reviewed, verdict: 'still sound' });
    await settle(fixture);
    fixture.componentRef.setInput('turnId', 'f-2');
    await settle(fixture);

    expect(inProgress).toEqual({ line: 'Giving the verdict…', verdictButtons: [0, 0] });
    expect(given).toBe('Approved on PR #5');
    expect(bridge.githubGiveVerdict).not.toHaveBeenCalled();
    expect(bridge.githubVerdictFor.mock.calls.map(([key]) => key)).toEqual([
      { repository: 'acme/shop', number: 5, commit: 'ec7f951' },
      { repository: 'acme/shop', number: 5, commit: 'ec7f951' },
    ]);
  });

  it('a push for the key from another window shows here without a lookup or a click, and a push for an older action never replaces the one in progress', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, reviewed);
    bridge.verdict({ actionId: 'action-elsewhere', state: 'queued', url: null, reason: null });
    await settle(fixture);
    const queued = text(el(fixture, 'verdict-giving'));
    bridge.verdict({ actionId: 'action-older', state: 'failed', url: null, reason: 'Old.' });
    await settle(fixture);

    expect(queued).toBe('Giving the verdict…');
    expect(text(el(fixture, 'verdict-giving'))).toBe('Giving the verdict…');
    expect(el(fixture, 'verdict-failed')).toBeNull();
    expect(bridge.githubGiveVerdict).not.toHaveBeenCalled();
  });

  const clean: ReviewFindings = {
    ...reviewed,
    verdict: 'ready to merge',
    findings: [],
    checks: [
      {
        name: 'build',
        status: 'completed',
        conclusion: 'success',
        url: 'https://github.com/acme/shop/runs/1',
      },
    ],
    tests: { ran: true, command: 'dotnet test', exitCode: 0, durationSeconds: 42 },
  };

  it('a review with no findings shows the tests chips, the target line and No findings with what was read; no finding card, Yes to all, Post, preview or Request changes', async () => {
    const fixture = await render(makeBridge(), clean);

    expect({
      chips: all(fixture, 'check').map((chip) => text(chip)),
      run: text(el(fixture, 'tests-run')),
      target: text(el(fixture, 'target')),
      clean: text(el(fixture, 'clean')),
      read: text(el(fixture, 'clean-read')),
      approve: all(fixture, 'approve').map((b) => (b as HTMLButtonElement).disabled),
      absent: [
        'finding',
        'summary',
        'all-dismissed',
        'yes-all',
        'post',
        'toggle-preview',
        'preview',
        'request-changes',
      ].map((test) => all(fixture, test).length),
    }).toEqual({
      chips: ['build passed'],
      run: 'tests passed in 42 s (dotnet test)',
      target: 'acme/shop · PR #5 · commit ec7f951',
      clean: 'No findings.',
      read: 'What Scout says it read: 3 of 3 files read',
      approve: [false],
      absent: [0, 0, 0, 0, 0, 0, 0, 0],
    });
  });

  it('Approve on a clean review gives one verdict: approve, comments [], the reviewed commit, the tests line; nothing before the click', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, clean);
    const beforeClick = bridge.githubGiveVerdict.mock.calls.length;

    await click(fixture, el(fixture, 'approve'));
    fixture.componentInstance.giveVerdict('approve');
    await settle(fixture);

    expect(beforeClick).toBe(0);
    expect(bridge.localAsk).not.toHaveBeenCalled();
    expect(bridge.githubGiveVerdict).toHaveBeenCalledExactlyOnceWith({
      repository: 'acme/shop',
      number: 5,
      verdict: 'approve',
      body: '**Verdict:** ready to merge\n\nNo findings.\n\n3 of 3 files read',
      comments: [],
      commit: 'ec7f951',
      testsLine: 'Checks: build passed\nTests passed in 42s (dotnet test)',
    });
  });

  it('a clean review the owner wrote offers no verdict button', async () => {
    const fixture = await render(makeBridge(), { ...clean, selfAuthored: true });

    expect({
      clean: text(el(fixture, 'clean')),
      read: text(el(fixture, 'clean-read')),
      verdictGroup: (fixture.nativeElement as HTMLElement).querySelector('[aria-label="Verdict"]'),
      buttons: ['approve', 'request-changes', 'post'].map((test) => all(fixture, test).length),
    }).toEqual({
      clean: 'No findings.',
      read: 'What Scout says it read: 3 of 3 files read',
      verdictGroup: null,
      buttons: [0, 0, 0],
    });
  });

  it('a clean review without a read sentence shows No findings alone, and a read sentence is shown as text, never as markup', async () => {
    const { read: _read, ...unread } = clean;
    const bare = await render(makeBridge(), unread);
    const bareLines = { clean: text(el(bare, 'clean')), read: all(bare, 'clean-read').length };
    TestBed.resetTestingModule();
    const marked = await render(makeBridge(), { ...clean, read: '<b>all</b> files' });

    expect(bareLines).toEqual({ clean: 'No findings.', read: 0 });
    expect(text(el(marked, 'clean-read'))).toBe('What Scout says it read: <b>all</b> files');
    expect(el(marked, 'clean-read')?.querySelector('b')).toBeNull();
  });

  it('a clean review approved reads Approved on PR #5 with the link, and no button is left', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, clean);
    await click(fixture, el(fixture, 'approve'));
    bridge.verdict({ actionId: 'action-1', state: 'given', url: URL, reason: null });
    await settle(fixture);
    const given = el(fixture, 'verdict-given');

    expect({
      title: text(given?.querySelector('strong') ?? null),
      link: text(given?.querySelector('a') ?? null),
      buttons: ['approve', 'request-changes', 'post', 'yes-all'].map(
        (test) => all(fixture, test).length,
      ),
    }).toEqual({ title: 'Approved on PR #5', link: URL, buttons: [0, 0, 0, 0] });
  });
});

describe('review findings cards: the look the QuickAsk artboard draws', () => {
  function declared(node: Element | null, property: string): string | null {
    if (node === null) return null;
    const css = Array.from(document.querySelectorAll('style'))
      .map((style) => style.textContent ?? '')
      .join('\n');
    const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
    let value: string | null = null;
    for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
      const found = pattern.exec(body!)?.[1]?.trim();
      if (found === undefined) continue;
      const matches = selector!.split(',').some((one) => {
        try {
          return node.matches(one.trim());
        } catch {
          return false;
        }
      });
      if (matches) value = found;
    }
    return value;
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('the card, its pill, title, path, body and buttons have the artboard sizes and colours', async () => {
    const fixture = await render(makeBridge(), reviewed);
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'no', card(fixture, 1)));
    const chosen = card(fixture, 0);
    const open = card(fixture, 2);
    const pill = el(fixture, 'severity', open);
    const nit = all(fixture, 'severity').find((node) => node.classList.contains('nit')) ?? null;
    const no = el(fixture, 'no', open);
    const yes = el(fixture, 'yes', chosen);
    const footer = (fixture.nativeElement as HTMLElement).querySelector('.footer');

    expect({
      card: ['padding', 'gap', 'border-radius', 'background', 'border'].map((p) =>
        declared(open, p),
      ),
      chosen: declared(chosen, 'border-color'),
      pill: ['padding', 'border-radius', 'font-size', 'font-weight', 'text-transform'].map((p) =>
        declared(pill, p),
      ),
      nit: [declared(nit, 'background'), declared(nit, 'color')],
      title: declared(open.querySelector('h3'), 'font-size'),
      path: declared(el(fixture, 'path', open), 'font-size'),
      body: declared(open.querySelector('.body'), 'font-size'),
      button: ['height', 'min-height', 'padding', 'background', 'font-weight'].map((p) =>
        declared(no, p),
      ),
      yes: ['background', 'color', 'border-color'].map((p) => declared(yes, p)),
      dismissed: [
        declared(el(fixture, 'dismissed')!.querySelector('.dismissed-title'), 'text-decoration'),
        declared(el(fixture, 'dismissed'), 'color'),
      ],
      footer: ['background', 'padding', 'border-radius', 'border-top'].map((p) =>
        declared(footer, p),
      ),
      target: declared(el(fixture, 'target'), 'flex'),
    }).toEqual({
      card: ['12px', '8px', '12px', 'var(--sc-surface)', '1px solid var(--sc-line)'],
      chosen: 'var(--sc-on-ok-container)',
      pill: ['2px 8px', '10px', '11px', '600', null],
      nit: ['var(--sc-surface-2)', 'var(--sc-fg-2)'],
      title: '14px',
      path: '12px',
      body: '13px',
      button: [null, '32px', '0 10px', 'transparent', '400'],
      yes: ['var(--sc-ok-container)', 'var(--sc-on-ok-container)', 'var(--sc-on-ok-container)'],
      dismissed: ['line-through', 'var(--sc-fg-2)'],
      footer: ['var(--sc-surface-2)', '10px 12px', '12px', null],
      target: null,
    });
  });

  it('a button keeps the 32 px look as a minimum, so a label that wraps grows the button instead of being cut', async () => {
    const fixture = await render(makeBridge(), reviewed);
    const buttons = [
      el(fixture, 'edit', card(fixture, 0)),
      el(fixture, 'post'),
      el(fixture, 'approve'),
      el(fixture, 'toggle-preview'),
    ];

    expect(
      buttons.map((button) => [
        declared(button, 'height'),
        declared(button, 'min-height'),
        declared(button, 'white-space'),
        declared(button, 'overflow'),
      ]),
    ).toEqual([
      [null, '32px', null, null],
      [null, '32px', null, null],
      [null, '30px', null, null],
      [null, '30px', null, null],
    ]);
  });

  it('Giving the verdict and a verdict that failed show inside the footer, as the artboard draws them: the accent line with its dot, and the error line with its icon', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, reviewed);
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'approve'));
    const giving = el(fixture, 'verdict-giving');
    const inFooter = giving?.closest('.footer') !== null;
    const dot = giving?.querySelector('.dot') ?? null;

    bridge.verdict({ actionId: 'action-1', state: 'failed', url: null, reason: null });
    await settle(fixture);
    const failed = el(fixture, 'verdict-failed');

    expect({ inFooter, color: declared(giving, 'color'), dot: dot !== null }).toEqual({
      inFooter: true,
      color: 'var(--sc-accent)',
      dot: true,
    });
    expect({
      inFooter: failed?.closest('.footer') !== null,
      icon: failed?.querySelector('svg') !== null,
      color: declared(failed, 'color'),
      text: text(failed),
    }).toEqual({
      inFooter: true,
      icon: true,
      color: 'var(--sc-error)',
      text: 'The verdict was not given.',
    });
  });
});
