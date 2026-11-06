import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  REVIEW_MARKER,
  type LocalAskRequest,
  type LocalEvent,
  type LocalTurn,
  type ReviewFinding,
  type ReviewFindings,
} from '@scout/shared';

import { commentBody, reviewBody } from './review-findings';
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
    push([
      ...turns,
      {
        id: req.id,
        question: at === -1 ? text : text.slice(0, at),
        reply: '',
        citations: [],
        state: 'running',
        ...(at === -1 ? {} : { review: true as const }),
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

async function render(bridge: ReturnType<typeof makeBridge>): Promise<Fixture> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(ReviewFindingsComponent);
  fixture.componentRef.setInput('turnId', 'f-1');
  fixture.componentRef.setInput('review', review);
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

  it('Yes and No mark the card and the summary counts them, Yes to all marks every card', async () => {
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
    expect(el(fixture, 'yes', card(fixture, 1))!.getAttribute('aria-pressed')).toBe('false');
    expect(text(el(fixture, 'summary'))).toBe('1 of 3 selected · 1 undecided');

    await click(fixture, el(fixture, 'yes-all'));

    expect(all(fixture, 'finding').map((c) => c.classList.contains('yes'))).toEqual([
      true,
      true,
      true,
    ]);
    expect(text(el(fixture, 'summary'))).toBe('3 of 3 selected');
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
    expect(all(fixture, 'yes')).toHaveLength(3);

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
});
