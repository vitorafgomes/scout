import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ISSUES_MARKER,
  REVIEW_MARKER,
  VERDICT_MARKER,
  type LocalAskRequest,
  type LocalEvent,
  type LocalTurn,
  type ReviewFinding,
  type ReviewFindings,
} from '@scout/shared';

import { commentBody, issueBody, reviewBody } from './review-findings';
import { ReviewFindingsComponent } from './review-findings.component';
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
  const push = (next: readonly LocalTurn[]) => {
    turns = next;
    listeners.forEach((listener) => listener(turns));
  };
  const localAsk = vi.fn(async (req: LocalAskRequest): Promise<void> => {
    const text = 'text' in req ? req.text : '';
    const at = text.indexOf(REVIEW_MARKER);
    const issuesAt = text.indexOf(ISSUES_MARKER);
    const verdictAt = text.indexOf(VERDICT_MARKER);
    const cut = [at, issuesAt, verdictAt].find((index) => index !== -1) ?? -1;
    push([
      ...turns,
      {
        id: req.id,
        question: cut === -1 ? text : text.slice(0, cut),
        reply: '',
        citations: [],
        state: 'running',
        ...(at === -1 ? {} : { review: true as const }),
        ...(issuesAt === -1 ? {} : { issues: true as const }),
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
    appOpenExternal: vi.fn(async (_req: { url: string }): Promise<void> => undefined),
  };
}

type Fixture = ComponentFixture<ReviewFindingsComponent>;

async function render(
  bridge: ReturnType<typeof makeBridge>,
  findings: ReviewFindings = review,
): Promise<Fixture> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(ReviewFindingsComponent);
  fixture.componentRef.setInput('turnId', 'f-1');
  fixture.componentRef.setInput('review', findings);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: Fixture): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
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
  (node as HTMLElement).click();
  await settle(fixture);
}

function card(fixture: Fixture, index: number): HTMLElement {
  return all(fixture, 'finding')[index]!;
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

  it('Edit shows the comment body, Use this text keeps the edited body and marks Yes', async () => {
    const fixture = await render(makeBridge());
    const edit = el(fixture, 'edit', card(fixture, 1))!;
    expect(text(edit)).toBe('Edit');

    await click(fixture, edit);
    const area = el(fixture, 'comment-text', card(fixture, 1)) as HTMLTextAreaElement;
    expect(area.value).toBe(commentBody(lock));
    expect(text(el(fixture, 'edit', card(fixture, 1)))).toBe('Close editor');

    area.value = 'Take the cart lock in Move, please.';
    area.dispatchEvent(new Event('input'));
    await settle(fixture);
    await click(fixture, el(fixture, 'use-text', card(fixture, 1)));

    expect(el(fixture, 'editor')).toBeNull();
    expect(card(fixture, 1).classList.contains('yes')).toBe(true);
    await click(fixture, el(fixture, 'edit', card(fixture, 1)));
    expect((el(fixture, 'comment-text', card(fixture, 1)) as HTMLTextAreaElement).value).toBe(
      'Take the cart lock in Move, please.',
    );
  });

  it('Post is disabled until a Yes and then asks the worker with the bare question, the marker and the JSON of the selected findings in order with the edited body', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const post = () => el(fixture, 'post') as HTMLButtonElement;
    expect(post().disabled).toBe(true);
    expect(text(post())).toBe('Post on PR #5');

    await click(fixture, el(fixture, 'yes', card(fixture, 2)));
    await click(fixture, el(fixture, 'edit', card(fixture, 0)));
    const area = el(fixture, 'comment-text', card(fixture, 0)) as HTMLTextAreaElement;
    area.value = 'Edited: sum after the discounts.';
    area.dispatchEvent(new Event('input'));
    await settle(fixture);
    await click(fixture, el(fixture, 'use-text', card(fixture, 0)));

    expect(post().disabled).toBe(false);
    expect(text(post())).toBe('Post 2 on PR #5');
    expect(post().getAttribute('aria-label')).toBe('Post 2 on PR #5');
    await click(fixture, post());

    expect(bridge.localAsk).toHaveBeenCalledTimes(1);
    const sent = bridge.localAsk.mock.calls[0]![0] as { text: string };
    const [question, json] = sent.text.split(REVIEW_MARKER);
    expect(question).toBe('Post 2 findings on acme/shop#5');
    expect(JSON.parse(json!)).toEqual({
      repository: 'acme/shop',
      number: 5,
      body: reviewBody(review, [total, naming]),
      comments: [
        { path: 'src/orders/Checkout.cs', line: 88, body: 'Edited: sum after the discounts.' },
        { path: 'src/orders/Cart.cs', line: 30, body: commentBody(naming) },
      ],
    });
  });

  it('once the post turn completes with the GitHub URL the cards show Review posted with the link and no buttons; a failed post turn keeps the buttons', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'no', card(fixture, 1)));

    await click(fixture, el(fixture, 'post'));
    const failedId = (bridge.localAsk.mock.calls[0]![0] as { id: string }).id;
    bridge.emit({ id: failedId, event: 'fail', text: 'GitHub refused the review.' });
    await settle(fixture);

    expect(el(fixture, 'posted')).toBeNull();
    expect((el(fixture, 'post') as HTMLButtonElement).disabled).toBe(false);
    expect(all(fixture, 'yes')).toHaveLength(2);
    expect(all(fixture, 'undo')).toHaveLength(1);

    await click(fixture, el(fixture, 'post'));
    const postedId = (bridge.localAsk.mock.calls[1]![0] as { id: string }).id;
    bridge.emit({ id: postedId, event: 'complete', text: `Posted the review: [PR #5](${URL}).` });
    await settle(fixture);

    expect(el(fixture, 'posted')?.className).toBe('posted');
    const posted = el(fixture, 'posted')!;
    expect(posted.querySelector('strong')?.textContent).toBe('Review posted on PR #5');
    expect(text(posted)).toContain(
      '1 comment in one review, without approval or request for changes. The items with No stayed here in the chat.',
    );
    expect(text(posted.querySelector('a'))).toBe(URL);
    expect(el(fixture, 'post')).toBeNull();
    expect(el(fixture, 'summary')).toBeNull();
    expect(all(fixture, 'yes')).toEqual([]);
    expect(all(fixture, 'no')).toEqual([]);
    expect(all(fixture, 'edit')).toEqual([]);
    expect(card(fixture, 1).classList.contains('no')).toBe(true);
    expect(text(el(fixture, 'dismissed', card(fixture, 1)))).toBe('Dismissed: Move has no lock');
    expect(all(fixture, 'undo')).toEqual([]);

    await click(fixture, posted.querySelector('a'));
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: URL });
  });

  it('a review posted from the other window shows as posted here too, and a post for another pull request does not', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const findingsTurn = (await bridge.localGetTurns())[0]!;
    const postFrom = (id: string, target: string, reply: string): LocalTurn => ({
      id,
      question: `Post 1 finding on ${target}`,
      reply,
      citations: [],
      state: 'done',
      review: true,
    });

    bridge.push([
      findingsTurn,
      postFrom('p-other-pr', 'acme/shop#6', `Posted: ${URL.replace('/5#', '/6#')}`),
    ]);
    await settle(fixture);
    const stillOpen = el(fixture, 'post') !== null && el(fixture, 'posted') === null;

    bridge.push([
      findingsTurn,
      postFrom('p-elsewhere', 'acme/shop#5', `Posted the review: [PR #5](${URL}).`),
    ]);
    await settle(fixture);

    expect(stillOpen).toBe(true);
    expect(el(fixture, 'posted')?.querySelector('strong')?.textContent).toBe(
      'Review posted on PR #5',
    );
    expect(text(el(fixture, 'posted')?.querySelector('a') ?? null)).toBe(URL);
    expect(el(fixture, 'post')).toBeNull();
  });

  it("a review posted from the other window counts the comments that window posted, not this window's picks", async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const findingsTurn = (await bridge.localGetTurns())[0]!;
    await click(fixture, el(fixture, 'yes-all'));

    bridge.push([
      findingsTurn,
      {
        id: 'p-elsewhere',
        question: 'Post 2 findings on acme/shop#5',
        reply: `Posted the review: [PR #5](${URL}).`,
        citations: [],
        state: 'done',
        review: true,
      },
    ]);
    await settle(fixture);

    expect(text(el(fixture, 'posted')?.querySelector('.posted-line') ?? null)).toBe(
      '2 comments in one review, without approval or request for changes. The items with No stayed here in the chat.',
    );
  });

  it('a review asked after an earlier post of the same pull request starts open, with its Post button', async () => {
    const bridge = makeBridge();
    const findingsTurn = (await bridge.localGetTurns())[0]!;
    const earlierPost: LocalTurn = {
      id: 'p-earlier',
      question: 'Post 2 findings on acme/shop#5',
      reply: `Posted the review: [PR #5](${URL}).`,
      citations: [],
      state: 'done',
      review: true,
    };
    bridge.push([earlierPost, findingsTurn]);
    const fixture = await render(bridge);

    expect(el(fixture, 'posted')).toBeNull();
    expect(el(fixture, 'post')).not.toBeNull();
  });

  it('a post the worker refuses before it becomes a turn shows the sentence and keeps the buttons', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    bridge.localAsk.mockRejectedValueOnce(
      new Error('[scout:local/invalid-request] Type a question of 1 to 4000 characters.'),
    );
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));

    await click(fixture, el(fixture, 'post'));

    expect(text(el(fixture, 'post-error'))).toBe('Type a question of 1 to 4000 characters.');
    expect((el(fixture, 'post') as HTMLButtonElement).disabled).toBe(false);
    expect(el(fixture, 'posted')).toBeNull();
  });

  it('a post turn that ends without a GitHub link keeps the buttons', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'post'));
    const id = (bridge.localAsk.mock.calls[0]![0] as { id: string }).id;

    bridge.emit({
      id,
      event: 'complete',
      text: 'GitHub writes are not allowed; allow them on the Features screen.',
    });
    await settle(fixture);

    expect(el(fixture, 'posted')).toBeNull();
    expect((el(fixture, 'post') as HTMLButtonElement).disabled).toBe(false);
    expect(all(fixture, 'yes')).toHaveLength(3);
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
    expect(area.value).toBe(issueBody(audit, audit.findings[0]!));
    expect(area.getAttribute('aria-label')).toBe('Issue text');
    area.value = 'Edited: sum after the discounts.';
    area.dispatchEvent(new Event('input'));
    await settle(fixture);
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
        { title: 'Discounts applied after the sum', body: 'Edited: sum after the discounts.' },
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

  it('once the issues turn completes with the issue links the cards show them opened, in this window or the other; a failed turn keeps the buttons', async () => {
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

    await click(fixture, el(fixture, 'post'));
    bridge.emit({
      id: lastId(),
      event: 'complete',
      text: 'GitHub writes are not allowed on https://github.com/acme/shop; allow them first.',
    });
    await settle(fixture);
    expect(buttons()).toEqual([null, false]);

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

  it('the tests line shows each check with its passed, failed or pending mark and its link when it has one, then the container run that passed with its time and command; nothing when the review carries neither', async () => {
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
      mark: text(el(fixture, 'check-mark', row)),
      name: text(el(fixture, 'check-name', row)),
      links: all(fixture, 'check-link', row).length,
    }));
    await click(fixture, all(fixture, 'check-link')[1]);
    const run = text(el(fixture, 'tests-run'));
    const output = el(fixture, 'tests-output');
    TestBed.resetTestingModule();
    const bare = await render(makeBridge());

    expect(rows).toEqual([
      { mark: 'Passed', name: 'build', links: 1 },
      { mark: 'Failed', name: 'lint', links: 1 },
      { mark: 'Passed', name: 'docs', links: 1 },
      { mark: 'Failed', name: 'e2e', links: 1 },
      { mark: 'Pending', name: 'deploy', links: 1 },
      { mark: 'Failed', name: 'ci/legacy', links: 0 },
    ]);
    expect(bridge.appOpenExternal).toHaveBeenCalledExactlyOnceWith({
      url: 'https://github.com/acme/shop/runs/2',
    });
    expect(run).toBe('Tests passed in 42s (dotnet test)');
    expect(output).toBeNull();
    expect(el(bare, 'tests')).toBeNull();
  });

  it('a container run that failed shows its exit code and the last output behind Last output, as text; one that did not run says why', async () => {
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
      run: 'Tests failed (exit 2)',
      summary: 'Last output',
      output: '<img src=x onerror=alert(1)> 3 failing',
      images: 0,
      checks: 0,
    });
    expect(text(el(skipped, 'tests-run'))).toBe('Tests did not run: Docker is not installed.');
    expect(el(skipped, 'tests-output')).toBeNull();
  });

  it('Approve and Request changes sit beside Post on a review and never on an audit; Approve is hidden when the owner wrote the pull request', async () => {
    const buttons = (fixture: Fixture) =>
      Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll('.footer .group button'),
      ).map((button) => button.getAttribute('data-test'));

    const onReview = buttons(await render(makeBridge()));
    TestBed.resetTestingModule();
    const own = buttons(await render(makeBridge(), { ...review, selfAuthored: true }));
    TestBed.resetTestingModule();
    const onAudit = buttons(await render(makeBridge(), audit));

    expect(onReview).toEqual(['yes-all', 'approve', 'request-changes', 'post']);
    expect(own).toEqual(['yes-all', 'request-changes', 'post']);
    expect(onAudit).toEqual(['yes-all', 'post']);
  });

  it('Approve sends Approve acme/shop#5, the verdict marker and the verdict with the review body and the chosen findings as comments; Request changes sends its own sentence; no verdict goes before a click', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge, { ...review, verdict: 'approve' });
    await click(fixture, el(fixture, 'yes', card(fixture, 1)));
    await click(fixture, el(fixture, 'no', card(fixture, 0)));
    await click(fixture, el(fixture, 'yes-all'));
    await click(fixture, el(fixture, 'toggle-preview'));
    const beforeClick = bridge.localAsk.mock.calls.length;

    await click(fixture, el(fixture, 'approve'));
    const approve = (bridge.localAsk.mock.lastCall![0] as { text: string }).text;
    bridge.emit({ id: bridge.localAsk.mock.lastCall![0].id, event: 'fail', text: 'Refused.' });
    await settle(fixture);
    await click(fixture, el(fixture, 'request-changes'));
    const changes = (bridge.localAsk.mock.lastCall![0] as { text: string }).text;

    const [approveQuestion, approveJson] = approve.split(VERDICT_MARKER);
    expect(beforeClick).toBe(0);
    expect(approveQuestion).toBe('Approve acme/shop#5');
    expect(JSON.parse(approveJson!)).toEqual({
      repository: 'acme/shop',
      number: 5,
      verdict: 'approve',
      body: reviewBody({ ...review, verdict: 'approve' }, [lock, naming]),
      comments: [
        { path: 'src/orders/Cart.cs', line: 12, body: commentBody(lock) },
        { path: 'src/orders/Cart.cs', line: 30, body: commentBody(naming) },
      ],
    });
    expect(changes.split(VERDICT_MARKER)[0]).toBe('Request changes on acme/shop#5');
    expect(JSON.parse(changes.split(VERDICT_MARKER)[1]!).verdict).toBe('request_changes');
    expect(bridge.localAsk).toHaveBeenCalledTimes(2);
  });

  it('while a verdict turn runs Approve, Request changes and Post are disabled; once it completes with the link the card says Approved on PR #5 with the link and loses its buttons', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await click(fixture, el(fixture, 'yes', card(fixture, 0)));
    await click(fixture, el(fixture, 'approve'));
    const id = bridge.localAsk.mock.lastCall![0].id;
    const giving = ['approve', 'request-changes', 'post'].map(
      (test) => (el(fixture, test) as HTMLButtonElement).disabled,
    );

    bridge.emit({ id, event: 'complete', text: `Approved: ${URL}.` });
    await settle(fixture);
    const given = el(fixture, 'verdict-given');

    expect(giving).toEqual([true, true, true]);
    expect({
      title: text(given?.querySelector('strong') ?? null),
      link: text(given?.querySelector('a') ?? null),
      posted: el(fixture, 'posted'),
      footer: ['approve', 'request-changes', 'post', 'yes', 'no', 'edit', 'toggle-preview'].map(
        (test) => all(fixture, test).length,
      ),
    }).toEqual({
      title: 'Approved on PR #5',
      link: URL,
      posted: null,
      footer: [0, 0, 0, 0, 0, 0, 0],
    });
  });

  it('changes requested from the other window show here as Changes requested on PR #5, not as Review posted; a failed verdict brings the buttons back', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const verdictTurn = (state: LocalTurn['state'], reply: string): LocalTurn => ({
      id: 'elsewhere',
      question: 'Request changes on acme/shop#5',
      reply,
      citations: [],
      state,
      review: true,
    });
    bridge.push([
      {
        id: 'f-1',
        question: 'review acme/shop#5',
        reply: 'the review',
        citations: [],
        state: 'done',
      },
      verdictTurn('failed', 'GitHub refused it.'),
    ]);
    await settle(fixture);
    const afterFailure = (el(fixture, 'request-changes') as HTMLButtonElement).disabled;

    bridge.push([
      {
        id: 'f-1',
        question: 'review acme/shop#5',
        reply: 'the review',
        citations: [],
        state: 'done',
      },
      verdictTurn('done', `Done: ${URL}`),
    ]);
    await settle(fixture);

    expect(afterFailure).toBe(false);
    expect(text(el(fixture, 'verdict-given')?.querySelector('strong') ?? null)).toBe(
      'Changes requested on PR #5',
    );
    expect(el(fixture, 'posted')).toBeNull();
  });

  it('when every finding is dismissed a review still offers Approve and Request changes beside the nothing-to-post line', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    for (const index of [0, 1, 2]) await click(fixture, el(fixture, 'no', card(fixture, index)));

    await click(fixture, el(fixture, 'approve'));
    const [question, json] = (bridge.localAsk.mock.lastCall![0] as { text: string }).text.split(
      VERDICT_MARKER,
    );

    expect(text(el(fixture, 'all-dismissed'))).toBe(
      'Nothing to post; every finding was dismissed.',
    );
    expect(question).toBe('Approve acme/shop#5');
    expect(JSON.parse(json!).comments).toEqual([]);
  });
});
