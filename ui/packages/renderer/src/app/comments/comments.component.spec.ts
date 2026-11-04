import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneInvalidArgumentError,
  encodeIpcErrorMessage,
  serializeError,
  type AudienceCommentDto,
  type AudienceIdeaDto,
  type CommentsViewDto,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

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
  }
  fixture.detectChanges();
}

async function mount(bridge: Bridge): Promise<ComponentFixture<CommentsComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
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
  return fixture.nativeElement.querySelector(`[data-test="${testId}"]`) as T | null;
}

function text(fixture: ComponentFixture<CommentsComponent>, testId: string): string {
  return (el(fixture, testId)?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function texts(fixture: ComponentFixture<CommentsComponent>, selector: string): string[] {
  return Array.from(fixture.nativeElement.querySelectorAll(selector)).map((node) =>
    ((node as HTMLElement).textContent ?? '').replace(/\s+/g, ' ').trim(),
  );
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
    expect(text(fixture, 'sync-status')).toBe('Last synced 2026-07-01 13:00 London');
    expect(text(fixture, 'sync-now')).toBe(
      'Sync requested; the Scout worker runs it within a minute.',
    );
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
    const actions = texts(fixture, '[data-test="reply-actions"] li');
    await click(fixture, 'reply-confirm');

    expect(count).toBe('15 / 1000');
    expect(actions).toEqual([
      'comments.insert: reply to Ana on "Records explained": "Yes, on Friday."',
    ]);
    expect(bridge.operatorApproveReply.mock.calls).toEqual([
      [{ commentId: 'c1', text: 'Yes, on Friday.' }],
    ]);
    expect(el(fixture, 'reply-dialog')).toBeNull();
    expect(text(fixture, 'reply-status-c1')).toBe('Waiting to publish');
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
    el(fixture, 'reply-dialog')!.querySelector<HTMLButtonElement>('.btn--secondary')!.click();
    await settle(fixture);
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
    const actions = texts(fixture, '[data-test="hold-actions"] li');
    await click(fixture, 'hold-confirm');

    expect(cards(fixture, 'question-')).toEqual([]);
    expect(actions).toEqual(['comments.setModerationStatus: hold for review on YouTube']);
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
    expect([text(fixture, 'reply-status-c3'), text(fixture, 'reply-status-c5')]).toEqual([
      'Publishing',
      'YouTube 503',
    ]);
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
