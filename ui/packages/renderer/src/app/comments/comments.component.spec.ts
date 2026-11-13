import { readFileSync } from 'node:fs';

import { InteractivityChecker } from '@angular/cdk/a11y';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneInvalidArgumentError,
  encodeIpcErrorMessage,
  serializeError,
  type AudienceCommentDto,
  type AudienceIdeaDto,
  type CommentsViewDto,
} from '@scout/shared';

import { LAID_OUT } from '../core/laid-out.testing';
import { ScoutBridgeService } from '../core/scout-bridge';
import { formatLondon } from '../operator/london';

import { CommentsComponent } from './comments.component';

function comment(overrides: Partial<AudienceCommentDto> = {}): AudienceCommentDto {
  return {
    id: 'c1',
    threadId: 'c1',
    videoId: 'vid-1',
    videoTitle: 'Records explained',
    author: 'Ana',
    text: 'Will you cover Span<T>?',
    publishedAt: '2026-07-01T08:00:00Z',
    likeCount: 2,
    kind: 'question',
    theme: 'Span<T>',
    status: 'drafted',
    draft: 'Yes, next week.',
    approvedText: null,
    replyId: null,
    error: null,
    decidedAt: null,
    fetchedAt: '2026-07-01T08:05:00Z',
    ...overrides,
  };
}

function idea(overrides: Partial<AudienceIdeaDto> = {}): AudienceIdeaDto {
  return {
    id: 'idea-c1',
    text: 'Span<T> in depth',
    commentId: 'c1',
    status: 'proposed',
    projectId: null,
    ...overrides,
  };
}

function view(overrides: Partial<CommentsViewDto> = {}): CommentsViewDto {
  return {
    comments: [comment()],
    ideas: [],
    syncedAt: null,
    syncRequestedAt: null,
    syncError: null,
    ...overrides,
  };
}

function refusal(message: string): Error {
  return new Error(
    `Error invoking remote method 'scout:operator.replyApproval': Error: ${encodeIpcErrorMessage(
      serializeError(new ControlPlaneInvalidArgumentError(message)),
    )}`,
  );
}

function makeBridge(initial: CommentsViewDto) {
  return {
    operatorComments: vi.fn(async () => initial),
    operatorRequestCommentSync: vi.fn(async () => initial),
    operatorDecideComment: vi.fn(async () => initial),
    operatorApproveReply: vi.fn(async () => initial),
    operatorDecideIdea: vi.fn(async () => initial),
    appOpenExternal: vi.fn(async () => undefined),
  };
}

type Bridge = ReturnType<typeof makeBridge>;

async function settle(fixture: ComponentFixture<CommentsComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
    await TestBed.inject(ApplicationRef).whenStable();
  }
  fixture.detectChanges();
}

async function mount(bridge: Bridge): Promise<ComponentFixture<CommentsComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(CommentsComponent);
  await settle(fixture);
  return fixture;
}

function el<T extends HTMLElement = HTMLElement>(
  fixture: ComponentFixture<CommentsComponent>,
  testId: string,
): T | null {
  const selector = `[data-test="${testId}"]`;
  return ((fixture.nativeElement as HTMLElement).querySelector<T>(selector) ??
    document.querySelector<T>(selector)) as T | null;
}

function text(fixture: ComponentFixture<CommentsComponent>, testId: string): string {
  return (el(fixture, testId)?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function texts(fixture: ComponentFixture<CommentsComponent>, selector: string): string[] {
  return Array.from(
    (fixture.nativeElement as HTMLElement).querySelectorAll(selector).length > 0
      ? (fixture.nativeElement as HTMLElement).querySelectorAll(selector)
      : document.querySelectorAll(selector),
  ).map((node) => ((node as HTMLElement).textContent ?? '').replace(/\s+/g, ' ').trim());
}

function cards(fixture: ComponentFixture<CommentsComponent>, prefix: string): string[] {
  return Array.from(
    fixture.nativeElement.querySelectorAll(`li[data-test^="${prefix}"]`) as NodeListOf<HTMLElement>,
  ).map((li) => li.getAttribute('data-test')!.slice(prefix.length));
}

async function click(fixture: ComponentFixture<CommentsComponent>, testId: string) {
  el(fixture, testId)!.click();
  await settle(fixture);
}

async function type(fixture: ComponentFixture<CommentsComponent>, testId: string, value: string) {
  const input = el<HTMLTextAreaElement>(fixture, testId)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

describe('CommentsComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    TestBed.inject(MatDialog).closeAll();
    vi.useRealTimers();
  });

  it('shows_sync_status_and_sends_sync_now', async () => {
    const bridge = makeBridge(view({ syncError: 'YouTube answered 503.' }));
    bridge.operatorRequestCommentSync.mockImplementation(async () =>
      view({ syncedAt: '2026-07-01T12:00:00Z', syncRequestedAt: '2026-07-01T12:30:00Z' }),
    );
    const fixture = await mount(bridge);
    const before = [text(fixture, 'sync-status'), text(fixture, 'sync-error')];
    const enabled = el<HTMLButtonElement>(fixture, 'sync-now')!.disabled;

    await click(fixture, 'sync-now');

    expect(before).toEqual(['Not synced yet.', 'YouTube answered 503.']);
    expect(enabled).toBe(false);
    expect(bridge.operatorRequestCommentSync).toHaveBeenCalledTimes(1);
    expect(text(fixture, 'sync-status')).toBe('Scout syncs within a minute.');
    expect(text(fixture, 'sync-now')).toBe('Sync requested');
    expect(el<HTMLButtonElement>(fixture, 'sync-now')!.disabled).toBe(true);
  });

  it('groups_themes_and_filters_by_a_chip', async () => {
    const fixture = await mount(
      makeBridge(
        view({
          comments: [
            comment({ id: 'c1', theme: 'Span<T>', kind: 'question', status: 'new', draft: '' }),
            comment({ id: 'c2', theme: 'Span<T>', kind: 'praise', status: 'new', draft: '' }),
            comment({ id: 'c3', theme: 'Span<T>', kind: 'spam', status: 'quarantined' }),
            comment({ id: 'c4', theme: 'Records', kind: 'question', status: 'new' }),
            comment({ id: 'c5', theme: 'Records', kind: 'question', status: 'dismissed' }),
            comment({ id: 'c6', theme: 'Records', kind: 'spam', status: 'held' }),
            comment({ id: 'c7', theme: 'Async', kind: 'feedback', status: 'new', draft: '' }),
            comment({ id: 'c8', theme: 'Async', kind: 'request', status: 'new' }),
          ],
        }),
      ),
    );
    const chips = texts(fixture, '[data-test="theme-chip"]');

    const records = Array.from(
      fixture.nativeElement.querySelectorAll('[data-test="theme-chip"]') as NodeListOf<HTMLElement>,
    ).find((chip) => chip.textContent?.includes('Records'))!;
    records.click();
    await settle(fixture);
    const filtered = [cards(fixture, 'question-'), cards(fixture, 'other-')];
    await click(fixture, 'theme-all');

    expect(chips).toEqual(['Async (2)', 'Span<T> (2)', 'Records (1)']);
    expect(filtered).toEqual([['c4'], []]);
    expect([cards(fixture, 'question-'), cards(fixture, 'other-')]).toEqual([
      ['c1', 'c4', 'c8'],
      ['c2', 'c7'],
    ]);
  });

  it('a theme filter clears itself when its last counted comment is dismissed, so the lists show every comment again', async () => {
    const records = comment({ id: 'c4', theme: 'Records', status: 'new' });
    const bridge = makeBridge(view({ comments: [comment({ status: 'new' }), records] }));
    bridge.operatorDecideComment.mockImplementation(async () =>
      view({ comments: [comment({ status: 'new' }), { ...records, status: 'dismissed' }] }),
    );
    const fixture = await mount(bridge);
    Array.from(
      fixture.nativeElement.querySelectorAll('[data-test="theme-chip"]') as NodeListOf<HTMLElement>,
    )
      .find((chip) => chip.textContent?.includes('Records'))!
      .click();
    await settle(fixture);
    const filtered = cards(fixture, 'question-');

    await click(fixture, 'question-dismiss-c4');

    expect(filtered).toEqual(['c4']);
    expect(texts(fixture, '[data-test="theme-chip"]')).toEqual(['Span<T> (1)']);
    expect(cards(fixture, 'question-')).toEqual(['c1']);
  });

  it('approve_reply_lists_the_action_and_sends_the_edited_text', async () => {
    const bridge = makeBridge(view());
    bridge.operatorApproveReply.mockImplementation(async () =>
      view({
        comments: [comment({ status: 'reply_approved', approvedText: 'Yes, on Friday.' })],
      }),
    );
    const fixture = await mount(bridge);

    await type(fixture, 'reply-text-c1', '  Yes, on Friday.  ');
    const count = text(fixture, 'reply-count-c1');
    await click(fixture, 'reply-approve-c1');
    const shown = [
      text(fixture, 'reply-dialog').includes(
        'Scout posts this reply to Ana on "Records explained"',
      ),
      text(fixture, 'reply-preview'),
      text(fixture, 'reply-actions'),
    ];
    await click(fixture, 'reply-confirm');

    expect(count).toBe('15 / 1000');
    expect(shown).toEqual([true, 'Yes, on Friday.', 'Action: post a public reply on YouTube']);
    expect(bridge.operatorApproveReply.mock.calls).toEqual([
      [{ commentId: 'c1', text: 'Yes, on Friday.' }],
    ]);
    expect(el(fixture, 'reply-dialog')).toBeNull();
    expect(text(fixture, 'reply-status-c1')).toBe('Waiting to post');
  });

  it("a refused reply keeps the dialog open with the control plane's reason, and an empty reply cannot be approved", async () => {
    const bridge = makeBridge(view());
    bridge.operatorApproveReply.mockImplementation(async () => {
      throw refusal('A quarantined comment gets no reply; mark it not spam first.');
    });
    const fixture = await mount(bridge);

    await click(fixture, 'reply-approve-c1');
    await click(fixture, 'reply-confirm');
    const shown = text(fixture, 'comments-dialog-error');
    const open = el(fixture, 'reply-dialog') !== null;
    await click(fixture, 'reply-cancel');
    await type(fixture, 'reply-text-c1', '   ');

    expect(shown).toBe('A quarantined comment gets no reply; mark it not spam first.');
    expect(open).toBe(true);
    expect(el<HTMLButtonElement>(fixture, 'reply-approve-c1')!.disabled).toBe(true);
  });

  it('draft_edits_survive_a_refresh', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    const bridge = makeBridge(view());
    const fixture = await mount(bridge);

    await type(fixture, 'reply-text-c1', 'An unsaved reply');
    vi.advanceTimersByTime(10_000);
    await settle(fixture);
    const kept = el<HTMLTextAreaElement>(fixture, 'reply-text-c1')!.value;
    bridge.operatorComments.mockImplementation(async () =>
      view({ comments: [comment({ draft: 'A new draft from the worker.' })] }),
    );
    vi.advanceTimersByTime(10_000);
    await settle(fixture);

    expect(bridge.operatorComments).toHaveBeenCalledTimes(3);
    expect(kept).toBe('An unsaved reply');
    expect(el<HTMLTextAreaElement>(fixture, 'reply-text-c1')!.value).toBe(
      'A new draft from the worker.',
    );
  });

  it('quarantine_offers_not_spam_and_hold_with_its_action_listed', async () => {
    const quarantined = comment({ id: 'c9', kind: 'spam', status: 'quarantined', draft: '' });
    const bridge = makeBridge(
      view({
        comments: [
          quarantined,
          comment({ id: 'c10', kind: 'spam', status: 'hold_approved', draft: '' }),
          comment({ id: 'c11', kind: 'spam', status: 'held', draft: '' }),
        ],
      }),
    );
    const fixture = await mount(bridge);

    await click(fixture, 'not-spam-c9');
    await click(fixture, 'hold-c9');
    const actions = [text(fixture, 'hold-actions')];
    await click(fixture, 'hold-confirm');

    expect(cards(fixture, 'question-')).toEqual([]);
    expect(actions).toEqual(['Action: hold a comment for review on YouTube']);
    expect(bridge.operatorDecideComment.mock.calls).toEqual([
      [{ commentId: 'c9', decision: 'not_spam' }],
      [{ commentId: 'c9', decision: 'hold' }],
    ]);
    expect(el(fixture, 'hold-dialog')).toBeNull();
    expect([
      text(fixture, 'quarantine-status-c10'),
      text(fixture, 'quarantine-status-c11'),
    ]).toEqual(['Waiting to hold', 'Held for review on YouTube']);
  });

  it('ideas_make_a_video_and_dismiss', async () => {
    const bridge = makeBridge(
      view({
        ideas: [
          idea(),
          idea({ id: 'idea-c2', text: 'Records vs classes', commentId: 'c2' }),
          idea({ id: 'idea-c3', text: 'Already used', status: 'used', projectId: 'p1' }),
        ],
      }),
    );
    const fixture = await mount(bridge);
    const shown = texts(fixture, '[data-test="idea-text"]');

    await click(fixture, 'idea-use-idea-c1');
    await click(fixture, 'idea-dismiss-idea-c2');

    expect(shown).toEqual(['Span<T> in depth', 'Records vs classes']);
    expect(bridge.operatorDecideIdea.mock.calls).toEqual([
      [{ ideaId: 'idea-c1', use: true }],
      [{ ideaId: 'idea-c2', use: false }],
    ]);
  });

  it('other comments get Draft and Dismiss, and replies show their state with the reply link opened through openExternal', async () => {
    const bridge = makeBridge(
      view({
        comments: [
          comment({ id: 'c2', kind: 'praise', status: 'new', draft: '' }),
          comment({ id: 'c3', status: 'replying', approvedText: 'Thanks!' }),
          comment({ id: 'c4', status: 'replied', approvedText: 'Thanks!', replyId: 'c4.r1' }),
          comment({ id: 'c5', status: 'failed', approvedText: 'Thanks!', error: 'YouTube 503' }),
        ],
      }),
    );
    const fixture = await mount(bridge);

    await click(fixture, 'other-draft-c2');
    await click(fixture, 'other-dismiss-c2');
    await click(fixture, 'reply-link-c4');

    expect(bridge.operatorDecideComment.mock.calls).toEqual([
      [{ commentId: 'c2', decision: 'draft' }],
      [{ commentId: 'c2', decision: 'dismiss' }],
    ]);
    expect([
      text(fixture, 'reply-status-c3'),
      text(fixture, 'reply-status-c5'),
      text(fixture, 'reply-error-c5'),
    ]).toEqual(['Posting', 'Failed', 'YouTube 503']);
    expect(text(fixture, 'reply-link-c4')).toBe('https://www.youtube.com/watch?v=vid-1&lc=c4.r1');
    expect(bridge.appOpenExternal.mock.calls).toEqual([
      [{ url: 'https://www.youtube.com/watch?v=vid-1&lc=c4.r1' }],
    ]);
    expect(cards(fixture, 'question-')).toEqual(['c5']);
  });

  it("Cancel on a reply waiting to publish and on a hold waiting to run sends dismiss, a publishing reply offers no Cancel, and a refusal shows the control plane's reason", async () => {
    const bridge = makeBridge(
      view({
        comments: [
          comment({ id: 'c3', status: 'reply_approved', approvedText: 'Thanks!' }),
          comment({ id: 'c4', status: 'replying', approvedText: 'Thanks!' }),
          comment({ id: 'c10', kind: 'spam', status: 'hold_approved', draft: '' }),
        ],
      }),
    );
    const fixture = await mount(bridge);
    const offered = cards(fixture, 'reply-').filter((id) => el(fixture, 'cancel-' + id) !== null);

    await click(fixture, 'cancel-c3');
    await click(fixture, 'cancel-c10');
    bridge.operatorDecideComment.mockImplementation(async () => {
      throw refusal('A YouTube action on this comment is running; wait for it to end.');
    });
    await click(fixture, 'cancel-c3');

    expect(offered).toEqual(['c3']);
    expect(bridge.operatorDecideComment.mock.calls).toEqual([
      [{ commentId: 'c3', decision: 'dismiss' }],
      [{ commentId: 'c10', decision: 'dismiss' }],
      [{ commentId: 'c3', decision: 'dismiss' }],
    ]);
    expect(text(fixture, 'comments-action-error')).toBe(
      'A YouTube action on this comment is running; wait for it to end.',
    );
  });

  it('comment_text_is_shown_as_text_not_html', async () => {
    const hostile = '<img src=x onerror="alert(1)"><b>bold</b>';
    const fixture = await mount(
      makeBridge(
        view({
          comments: [
            comment({
              text: hostile,
              author: '<a href="https://evil.example">Ana</a>',
              theme: '<i>spoof</i>',
            }),
          ],
        }),
      ),
    );
    const card = el(fixture, 'question-c1')!;

    expect(card.querySelector('[data-test="comment-text"]')!.textContent).toBe(hostile);
    expect(card.querySelector('[data-test="comment-author"]')!.textContent).toBe(
      '<a href="https://evil.example">Ana</a>',
    );
    expect(texts(fixture, '[data-test="theme-chip"]')).toEqual(['<i>spoof</i> (1)']);
    expect(fixture.nativeElement.querySelectorAll('img, b, i, a').length).toBe(0);
  });
});

/** The colour literals left in a component's styles, read from the renderer root: hex, rgb(), hsl() and the named white and black. */
function colourLiterals(relative: string): string[] {
  const source = readFileSync(relative, 'utf8');
  const start = source.indexOf('  styles:');
  const css = source.slice(start, source.indexOf('\n})\n', start));
  expect(start, `${relative} declares its styles`).toBeGreaterThanOrEqual(0);
  expect(css, `${relative} paints with the theme tokens`).toContain('var(--sc-');
  return Array.from(
    css.matchAll(
      /#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(|(?<![\w-])(?:white|black)(?![\w-])/g,
    ),
    (match) => match[0],
  );
}

describe('Comments follows the theme and the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const file of ['src/app/comments/comments.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }

  it('Sync now shows its icon before its text, as the prototype draws it', async () => {
    const fixture = await mount(makeBridge(view()));

    expect([
      el(fixture, 'sync-now')
        ?.querySelector('.mdc-button__label')
        ?.firstElementChild?.tagName.toLowerCase(),
      text(fixture, 'sync-now'),
    ]).toEqual(['sc-icon', 'Sync now']);
  });
});

/** The text of a node as read aloud: its text pieces trimmed and joined by one space. */
function spoken(node: Node | null | undefined): string {
  if (node === null || node === undefined) return '';
  const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
  const pieces: string[] = [];
  for (let n = walker.nextNode(); n !== null; n = walker.nextNode()) {
    const piece = (n.textContent ?? '').replace(/\s+/g, ' ').trim();
    if (piece !== '') pieces.push(piece);
  }
  return pieces.join(' ');
}

/** What the owner sees, in order: headings, tabs, buttons, fields by their label, chips, status lines and comment cards, skipping anything hidden. */
function seen(fixture: ComponentFixture<CommentsComponent>): string[] {
  const roots = [
    (fixture.nativeElement as HTMLElement).querySelector('main')!,
    ...Array.from(document.querySelectorAll<HTMLElement>('.cdk-overlay-container')),
  ];
  const out: string[] = [];
  for (const root of roots) {
    for (const node of Array.from(
      root.querySelectorAll<HTMLElement>(
        'h1, h2, button, textarea, [role="status"], .intro, .meta-row, [data-test="comment-text"], [data-test$="-empty"]:not(button):not(.card), .empty-card > span:not(.empty-icon), .quote, [data-test="reply-actions"], [data-test="hold-actions"], .idea-text',
      ),
    )) {
      if (node.closest('[hidden]') !== null) continue;
      const tag = node.tagName.toLowerCase();
      const words = spoken(node) || (node.getAttribute('aria-label') ?? '');
      if (tag === 'button') {
        const b = node as HTMLButtonElement;
        const flags = [
          b.disabled ? 'disabled' : '',
          b.getAttribute('aria-selected') === 'true' ? 'selected' : '',
          b.getAttribute('aria-pressed') === 'true' ? 'pressed' : '',
        ].filter(Boolean);
        out.push(
          `${b.getAttribute('role') ?? 'button'}${flags.length > 0 ? `(${flags.join(',')})` : ''}: ${words}`,
        );
      } else if (tag === 'textarea') {
        out.push(`textarea: ${spoken(document.querySelector(`label[for="${node.id}"]`))}`);
      } else if (/^h[12]$/.test(tag)) {
        out.push(`${tag}: ${words}`);
      } else if (node.getAttribute('role') === 'status') {
        out.push(
          `status${node.getAttribute('aria-label') ? ` ${node.getAttribute('aria-label')}` : ''}: ${words}`,
        );
      } else {
        out.push(`text: ${words}`);
      }
    }
  }
  return out;
}

describe('Comments as Comments.dc.html draws it, state by state', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    TestBed.inject(MatDialog).closeAll();
  });

  const SYNCED = '2026-07-01T12:00:00Z';
  const full = () =>
    view({
      syncedAt: SYNCED,
      comments: [
        comment({ id: 'q1', author: 'Ana R.', kind: 'question', theme: 'SSDs' }),
        comment({
          id: 'r1',
          author: 'mk_builds',
          theme: 'Git',
          status: 'reply_approved',
          approvedText: 'Use rebase on your own branch.',
        }),
        comment({
          id: 'o1',
          author: 'devon_k',
          kind: 'praise',
          theme: 'Docker',
          status: 'new',
          draft: '',
        }),
        comment({
          id: 'x1',
          author: 'promo_4u',
          kind: 'spam',
          theme: '',
          status: 'quarantined',
          draft: '',
        }),
      ],
      ideas: [idea({ text: 'How Docker networks talk to each other', commentId: 'q1' })],
    });
  const head = [
    'h1: Comments',
    'button: Sync now',
    `status: Last synced ${formatLondon(SYNCED).slice(0, 10)} at ${formatLondon(SYNCED).slice(11)}, London time`,
  ];

  it('Loading: Sync now off, the status line and the loading block named "Loading comments", nothing else', async () => {
    const bridge = makeBridge(view());
    bridge.operatorComments.mockImplementation(() => new Promise<never>(() => undefined));
    const fixture = await mount(bridge);

    expect(seen(fixture)).toEqual([
      'h1: Comments',
      'button(disabled): Sync now',
      'status: Loading comments',
      'status Loading comments: Loading comments',
    ]);
  });

  it('Empty: the sentence and Sync now under it, no tabs', async () => {
    const fixture = await mount(makeBridge(view({ syncedAt: SYNCED, comments: [], ideas: [] })));

    expect(seen(fixture)).toEqual([
      ...head,
      'text: No comments on your channel yet. Scout reads new ones each time it syncs.',
      'button: Sync now',
    ]);
  });

  it('Comments: the five tabs with their counts, Waiting for you selected, the theme filter, and each draft as a card with its author, video, time, kind, the waiting chip, the reply field, Approve reply and Dismiss', async () => {
    const fixture = await mount(makeBridge(full()));

    expect(seen(fixture)).toEqual([
      ...head,
      'tab(selected): Waiting for you 1',
      'tab: Replies 1',
      'tab: Other comments 1',
      'tab: Quarantine 1',
      'tab: Audience ideas 1',
      'button(pressed): All',
      'button: Docker (1)',
      'button: Git (1)',
      'button: SSDs (1)',
      'text: Scout drafted these replies. Nothing is posted until you approve it.',
      `text: Ana R. on "Records explained" · ${formatLondon('2026-07-01T08:00:00Z').slice(0, 10)}, ${formatLondon('2026-07-01T08:00:00Z').slice(11)} Question Draft waiting for your approval`,
      'text: Will you cover Span<T>?',
      'textarea: Your reply (drafted by Scout, edit freely)',
      'button: Approve reply',
      'button: Dismiss',
    ]);
  });

  for (const [tab, expected] of [
    [
      'replies',
      [
        'text: Reply to mk_builds on "Records explained" Waiting to post',
        'button: Cancel before it posts',
      ],
    ],
    [
      'others',
      [
        `text: devon_k on "Records explained" · ${formatLondon('2026-07-01T08:00:00Z').slice(0, 10)}, ${formatLondon('2026-07-01T08:00:00Z').slice(11)} Praise`,
        'text: Will you cover Span<T>?',
        'button: Draft a reply',
        'button: Dismiss',
      ],
    ],
    [
      'quarantine',
      [
        `text: promo_4u on "Records explained" · ${formatLondon('2026-07-01T08:00:00Z').slice(0, 10)}, ${formatLondon('2026-07-01T08:00:00Z').slice(11)} Looks like spam`,
        'text: Will you cover Span<T>?',
        'button: Not spam',
        'button: Hold for review on YouTube',
      ],
    ],
    [
      'ideas',
      [
        'text: How Docker networks talk to each other Asked by Ana R. on "Records explained"',
        'button: Make a video',
        'button: Dismiss',
      ],
    ],
  ] as const) {
    it(`Comments, the ${tab} tab: selecting it shows only its list, drawn as the artboard draws it`, async () => {
      const fixture = await mount(makeBridge(full()));

      await click(fixture, `tab-${tab}`);
      const shown = seen(fixture);

      expect(shown.filter((line) => line.startsWith('tab(selected)'))).toHaveLength(1);
      expect(el(fixture, `tab-${tab}`)!.getAttribute('aria-selected')).toBe('true');
      expect(shown.slice(12)).toEqual([...expected]);
    });
  }

  it('Approve reply dialog: on the theme card, named by its title and described by who it goes to, the exact text that will post, the action, Cancel focused and Approve and post', async () => {
    const fixture = await mount(makeBridge(full()));

    await click(fixture, 'reply-approve-q1');
    const dialog = document.querySelector<HTMLElement>('[role="dialog"]')!;

    expect({
      panel: document.querySelector('.cdk-overlay-pane')?.classList.contains('sc-dialog'),
      title: document.getElementById(dialog.getAttribute('aria-labelledby') ?? '')?.textContent,
      body: spoken(document.getElementById(dialog.getAttribute('aria-describedby') ?? '')),
      focused: (document.activeElement as HTMLElement | null)?.getAttribute('data-test'),
      rest: seen(fixture).slice(-5),
    }).toEqual({
      panel: true,
      title: 'Approve the reply?',
      body: 'Scout posts this reply to Ana R. on "Records explained" within a minute. You can cancel it until then.',
      focused: 'reply-cancel',
      rest: [
        'h2: Approve the reply?',
        'text: Yes, next week.',
        'text: Action: post a public reply on YouTube',
        'button: Cancel',
        'button: Approve and post',
      ],
    });
  });

  it('Hold dialog: named by its title, described by what YouTube does, the action, Cancel focused and Hold the comment', async () => {
    const fixture = await mount(makeBridge(full()));

    await click(fixture, 'tab-quarantine');
    await click(fixture, 'hold-x1');
    const dialog = document.querySelector<HTMLElement>('[role="dialog"]')!;

    expect({
      title: document.getElementById(dialog.getAttribute('aria-labelledby') ?? '')?.textContent,
      body: spoken(document.getElementById(dialog.getAttribute('aria-describedby') ?? '')),
      focused: (document.activeElement as HTMLElement | null)?.getAttribute('data-test'),
      rest: seen(fixture).slice(-4),
    }).toEqual({
      title: 'Hold the comment on YouTube?',
      body: 'YouTube hides the comment until you review it in YouTube Studio. Scout does this within a minute.',
      focused: 'hold-cancel',
      rest: [
        'h2: Hold the comment on YouTube?',
        'text: Action: hold a comment for review on YouTube',
        'button: Cancel',
        'button: Hold the comment',
      ],
    });
  });

  it('Sync requested: the button reads Sync requested and is off, and the line says Scout syncs within a minute', async () => {
    const fixture = await mount(makeBridge(view({ ...full(), syncRequestedAt: SYNCED })));

    expect(seen(fixture).slice(0, 3)).toEqual([
      'h1: Comments',
      'button(disabled): Sync requested',
      'status: Scout syncs within a minute.',
    ]);
  });
});

describe('Comments: what each action sends, and its confirmation path', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    TestBed.inject(MatDialog).closeAll();
  });

  it('Hold for review on YouTube sends nothing until the owner confirms: Cancel closes the dialog with nothing sent, Hold the comment sends hold for that comment once', async () => {
    const bridge = makeBridge(
      view({ comments: [comment({ id: 'c9', kind: 'spam', status: 'quarantined', draft: '' })] }),
    );
    const fixture = await mount(bridge);

    await click(fixture, 'hold-c9');
    const sentOnOpen = bridge.operatorDecideComment.mock.calls.length;
    await click(fixture, 'hold-cancel');
    const afterCancel = {
      sent: bridge.operatorDecideComment.mock.calls.length,
      open: el(fixture, 'hold-dialog') !== null,
    };
    await click(fixture, 'hold-c9');
    await click(fixture, 'hold-confirm');

    expect({ sentOnOpen, afterCancel }).toEqual({
      sentOnOpen: 0,
      afterCancel: { sent: 0, open: false },
    });
    expect(bridge.operatorDecideComment.mock.calls).toEqual([
      [{ commentId: 'c9', decision: 'hold' }],
    ]);
  });

  it('Approve reply sends nothing until Approve and post, and Cancel in the dialog sends nothing', async () => {
    const bridge = makeBridge(view());
    const fixture = await mount(bridge);

    await click(fixture, 'reply-approve-c1');
    await click(fixture, 'reply-cancel');

    expect({
      sent: bridge.operatorApproveReply.mock.calls.length,
      open: el(fixture, 'reply-dialog') !== null,
    }).toEqual({ sent: 0, open: false });
  });
});
