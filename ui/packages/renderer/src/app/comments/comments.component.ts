import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';

import {
  readIpcError,
  type AudienceCommentDto,
  type CommentDecision,
  type CommentsViewDto,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';
import { formatLondon } from '../operator/london';

const POLL_MS = 10_000;
const MAX_REPLY = 1000;
const OPEN = ['new', 'draft_requested', 'drafted', 'failed'];
const ASKING = ['question', 'request'];
const REPLIES = ['reply_approved', 'replying', 'replied'];
const QUARANTINE = ['quarantined', 'hold_approved', 'held'];
const UNCOUNTED = ['quarantined', 'held', 'dismissed'];

interface ThemeCount {
  readonly theme: string;
  readonly count: number;
}

interface ReplyEdit {
  readonly from: string;
  readonly text: string;
}

interface PendingReply {
  readonly comment: AudienceCommentDto;
  readonly text: string;
}

function failedReply(c: AudienceCommentDto): boolean {
  return c.status === 'failed' && c.approvedText !== null;
}

function replyable(c: AudienceCommentDto): boolean {
  return (
    OPEN.includes(c.status) &&
    c.kind !== 'spam' &&
    (ASKING.includes(c.kind) ||
      c.status === 'draft_requested' ||
      c.status === 'drafted' ||
      failedReply(c))
  );
}

function quarantined(c: AudienceCommentDto): boolean {
  return QUARANTINE.includes(c.status) || (c.kind === 'spam' && c.status === 'failed');
}

@Component({
  selector: 'sc-comments',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <header class="header">
        <h1>Comments</h1>
        <p class="lede">
          The comments on your channel, the replies Scout drafted and the ideas your audience asked
          for. Nothing is posted until you approve it.
        </p>
      </header>

      @if (error(); as message) {
        <p class="err" data-test="comments-error">{{ message }}</p>
      }
      @if (actionError(); as message) {
        @if (pendingReply() === null && holding() === null) {
          <p class="err" data-test="comments-action-error">{{ message }}</p>
        }
      }

      <section class="block">
        <h2>Sync</h2>
        <p class="meta" data-test="sync-status">
          @if (view()?.syncedAt; as at) {
            Last synced {{ london(at) }} London
          } @else {
            Not synced yet.
          }
        </p>
        @if (view()?.syncError; as message) {
          <p class="err" data-test="sync-error">{{ message }}</p>
        }
        <button
          type="button"
          class="btn btn--primary"
          data-test="sync-now"
          [disabled]="busy() || syncRequested()"
          (click)="sync()"
        >
          {{
            syncRequested()
              ? 'Sync requested; the Scout worker runs it within a minute.'
              : 'Sync now'
          }}
        </button>
      </section>

      <section class="block">
        <h2>Themes</h2>
        @if (themes().length === 0) {
          <p class="empty" data-test="themes-empty">No themes yet.</p>
        } @else {
          <div class="chips">
            <button
              type="button"
              class="chip"
              [class.chip--active]="theme() === null"
              data-test="theme-all"
              (click)="theme.set(null)"
            >
              All
            </button>
            @for (t of themes(); track t.theme) {
              <button
                type="button"
                class="chip"
                [class.chip--active]="theme() === t.theme"
                data-test="theme-chip"
                (click)="theme.set(t.theme)"
              >
                {{ t.theme }} ({{ t.count }})
              </button>
            }
          </div>
        }
      </section>

      <section class="block">
        <h2>Questions and requests</h2>
        @if (questions().length === 0) {
          <p class="empty" data-test="questions-empty">No question or request waits for a reply.</p>
        } @else {
          <ul class="cards">
            @for (c of questions(); track c.id) {
              <li class="card" [attr.data-test]="'question-' + c.id">
                <p class="meta">
                  <span data-test="comment-author">{{ c.author }}</span> on "{{ c.videoTitle }}" ·
                  {{ london(c.publishedAt) }} · {{ c.kind }}
                </p>
                <p class="text" data-test="comment-text">{{ c.text }}</p>
                @if (c.status === 'draft_requested') {
                  <p class="meta" [attr.data-test]="'draft-requested-' + c.id">
                    Draft requested; the Scout worker writes it within a minute.
                  </p>
                }
                @if (failedReply(c)) {
                  <p class="err" [attr.data-test]="'question-error-' + c.id">{{ c.error }}</p>
                }
                <label>
                  Reply
                  <textarea
                    rows="4"
                    [attr.data-test]="'reply-text-' + c.id"
                    [value]="replyText(c)"
                    (input)="edit(c, $any($event.target).value)"
                  ></textarea>
                </label>
                <p class="meta" [attr.data-test]="'reply-count-' + c.id">
                  {{ replyText(c).trim().length }} / {{ maxReply }}
                </p>
                <div class="row">
                  <button
                    type="button"
                    class="btn btn--primary"
                    [attr.data-test]="'reply-approve-' + c.id"
                    [disabled]="busy() || !replyValid(c)"
                    (click)="openReply(c)"
                  >
                    Approve reply
                  </button>
                  <button
                    type="button"
                    class="btn btn--secondary"
                    [attr.data-test]="'question-dismiss-' + c.id"
                    [disabled]="busy()"
                    (click)="decide(c, 'dismiss')"
                  >
                    Dismiss
                  </button>
                </div>
              </li>
            }
          </ul>
        }
      </section>

      <section class="block">
        <h2>Replies</h2>
        @if (replies().length === 0) {
          <p class="empty" data-test="replies-empty">No reply approved yet.</p>
        } @else {
          <ul class="cards">
            @for (c of replies(); track c.id) {
              <li class="card" [attr.data-test]="'reply-' + c.id">
                <p class="meta">
                  <span data-test="comment-author">{{ c.author }}</span> on "{{ c.videoTitle }}"
                </p>
                <p class="text">{{ c.approvedText }}</p>
                @switch (c.status) {
                  @case ('reply_approved') {
                    <p class="meta" [attr.data-test]="'reply-status-' + c.id">Waiting to publish</p>
                    <button
                      type="button"
                      class="btn btn--secondary"
                      [attr.data-test]="'cancel-' + c.id"
                      [disabled]="busy()"
                      (click)="decide(c, 'dismiss')"
                    >
                      Cancel
                    </button>
                  }
                  @case ('replying') {
                    <p class="meta" [attr.data-test]="'reply-status-' + c.id">Publishing</p>
                  }
                  @case ('replied') {
                    @if (c.replyId; as replyId) {
                      <button
                        type="button"
                        class="link"
                        [attr.data-test]="'reply-link-' + c.id"
                        (click)="openReplyLink(c.videoId, replyId)"
                      >
                        {{ replyUrl(c.videoId, replyId) }}
                      </button>
                    }
                  }
                  @default {
                    <p class="err" [attr.data-test]="'reply-status-' + c.id">{{ c.error }}</p>
                  }
                }
              </li>
            }
          </ul>
        }
      </section>

      <section class="block">
        <h2>Other comments</h2>
        @if (others().length === 0) {
          <p class="empty" data-test="others-empty">No other comment.</p>
        } @else {
          <ul class="cards">
            @for (c of others(); track c.id) {
              <li class="card" [attr.data-test]="'other-' + c.id">
                <p class="meta">
                  <span data-test="comment-author">{{ c.author }}</span> on "{{ c.videoTitle }}" ·
                  {{ london(c.publishedAt) }} · {{ c.kind }}
                </p>
                <p class="text" data-test="comment-text">{{ c.text }}</p>
                <div class="row">
                  <button
                    type="button"
                    class="btn btn--secondary"
                    [attr.data-test]="'other-draft-' + c.id"
                    [disabled]="busy()"
                    (click)="decide(c, 'draft')"
                  >
                    Draft
                  </button>
                  <button
                    type="button"
                    class="btn btn--secondary"
                    [attr.data-test]="'other-dismiss-' + c.id"
                    [disabled]="busy()"
                    (click)="decide(c, 'dismiss')"
                  >
                    Dismiss
                  </button>
                </div>
              </li>
            }
          </ul>
        }
      </section>

      <section class="block">
        <h2>Quarantine</h2>
        @if (quarantine().length === 0) {
          <p class="empty" data-test="quarantine-empty">No comment is quarantined.</p>
        } @else {
          <ul class="cards">
            @for (c of quarantine(); track c.id) {
              <li class="card" [attr.data-test]="'quarantine-' + c.id">
                <p class="meta">
                  <span data-test="comment-author">{{ c.author }}</span> on "{{ c.videoTitle }}" ·
                  {{ london(c.publishedAt) }}
                </p>
                <p class="text" data-test="comment-text">{{ c.text }}</p>
                @switch (c.status) {
                  @case ('quarantined') {
                    <div class="row">
                      <button
                        type="button"
                        class="btn btn--secondary"
                        [attr.data-test]="'not-spam-' + c.id"
                        [disabled]="busy()"
                        (click)="decide(c, 'not_spam')"
                      >
                        Not spam
                      </button>
                      <button
                        type="button"
                        class="btn btn--secondary"
                        [attr.data-test]="'hold-' + c.id"
                        [disabled]="busy()"
                        (click)="openHold(c)"
                      >
                        Hold on YouTube
                      </button>
                    </div>
                  }
                  @case ('hold_approved') {
                    <p class="meta" [attr.data-test]="'quarantine-status-' + c.id">
                      Waiting to hold
                    </p>
                    <button
                      type="button"
                      class="btn btn--secondary"
                      [attr.data-test]="'cancel-' + c.id"
                      [disabled]="busy()"
                      (click)="decide(c, 'dismiss')"
                    >
                      Cancel
                    </button>
                  }
                  @case ('held') {
                    <p class="meta" [attr.data-test]="'quarantine-status-' + c.id">
                      Held for review on YouTube
                    </p>
                  }
                  @default {
                    <p class="err" [attr.data-test]="'quarantine-status-' + c.id">{{ c.error }}</p>
                    <button
                      type="button"
                      class="btn btn--secondary"
                      [attr.data-test]="'quarantine-dismiss-' + c.id"
                      [disabled]="busy()"
                      (click)="decide(c, 'dismiss')"
                    >
                      Dismiss
                    </button>
                  }
                }
              </li>
            }
          </ul>
        }
      </section>

      <section class="block">
        <h2>Audience ideas</h2>
        @if (ideas().length === 0) {
          <p class="empty" data-test="ideas-empty">No idea from your audience yet.</p>
        } @else {
          <ul class="cards">
            @for (i of ideas(); track i.id) {
              <li class="card" [attr.data-test]="'idea-' + i.id">
                <p class="text" data-test="idea-text">{{ i.text }}</p>
                <div class="row">
                  <button
                    type="button"
                    class="btn btn--primary"
                    [attr.data-test]="'idea-use-' + i.id"
                    [disabled]="busy()"
                    (click)="decideIdea(i.id, true)"
                  >
                    Make a video
                  </button>
                  <button
                    type="button"
                    class="btn btn--secondary"
                    [attr.data-test]="'idea-dismiss-' + i.id"
                    [disabled]="busy()"
                    (click)="decideIdea(i.id, false)"
                  >
                    Dismiss
                  </button>
                </div>
              </li>
            }
          </ul>
        }
      </section>

      @if (pendingReply(); as r) {
        <div class="backdrop" role="dialog" aria-modal="true" data-test="reply-dialog">
          <div class="dialog">
            <h2>Approve the reply?</h2>
            <p>Approving it approves this action; the Scout worker posts it.</p>
            <ul data-test="reply-actions">
              <li>
                comments.insert: reply to {{ r.comment.author }} on "{{ r.comment.videoTitle }}":
                "{{ r.text }}"
              </li>
            </ul>
            @if (actionError(); as message) {
              <p class="err" data-test="comments-dialog-error">{{ message }}</p>
            }
            <div class="row">
              <button type="button" class="btn btn--secondary" (click)="closeDialogs()">
                Cancel
              </button>
              <button
                type="button"
                class="btn btn--primary"
                data-test="reply-confirm"
                [disabled]="busy()"
                (click)="approve(r)"
              >
                Confirm and approve
              </button>
            </div>
          </div>
        </div>
      }

      @if (holding(); as c) {
        <div class="backdrop" role="dialog" aria-modal="true" data-test="hold-dialog">
          <div class="dialog">
            <h2>Hold the comment on YouTube?</h2>
            <p>Holding it approves this action; the Scout worker runs it.</p>
            <ul data-test="hold-actions">
              <li>comments.setModerationStatus: hold for review on YouTube</li>
            </ul>
            @if (actionError(); as message) {
              <p class="err" data-test="comments-dialog-error">{{ message }}</p>
            }
            <div class="row">
              <button type="button" class="btn btn--secondary" (click)="closeDialogs()">
                Cancel
              </button>
              <button
                type="button"
                class="btn btn--primary"
                data-test="hold-confirm"
                [disabled]="busy()"
                (click)="hold(c)"
              >
                Confirm and hold
              </button>
            </div>
          </div>
        </div>
      }
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .page {
        padding: 32px;
        max-width: 1080px;
        margin: 0 auto;
        color: #e4e8f0;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .lede,
      .meta,
      .empty {
        color: #7b8299;
        font-size: 13px;
      }
      .err {
        color: #f87171;
      }
      .block {
        margin-top: 24px;
      }
      .text {
        white-space: pre-wrap;
      }
      .cards {
        list-style: none;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .card {
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 12px;
      }
      .chips {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      .chip {
        background: transparent;
        color: #e4e8f0;
        border: 1px solid #252b3b;
        border-radius: 999px;
        padding: 4px 10px;
        cursor: pointer;
      }
      .chip--active {
        border-color: #2563eb;
      }
      label {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      textarea {
        background: #0f1117;
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 6px 8px;
        color: #e4e8f0;
        font-size: 13px;
        font-family: inherit;
      }
      .link {
        background: none;
        border: none;
        color: #60a5fa;
        cursor: pointer;
        padding: 0;
        text-decoration: underline;
      }
      .backdrop {
        position: fixed;
        inset: 0;
        background: rgba(0, 0, 0, 0.6);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .dialog {
        background: #11131a;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 20px;
        max-width: 560px;
        width: 100%;
      }
      .row {
        display: flex;
        gap: 8px;
      }
      .btn {
        padding: 6px 12px;
        border-radius: 6px;
        border: 1px solid transparent;
        cursor: pointer;
      }
      .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
      .btn--primary {
        background: #2563eb;
        color: white;
      }
      .btn--secondary {
        background: transparent;
        color: #e4e8f0;
        border-color: #252b3b;
      }
    `,
  ],
})
export class CommentsComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private timer: ReturnType<typeof setInterval> | null = null;

  readonly maxReply = MAX_REPLY;
  readonly failedReply = failedReply;
  readonly error = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly busy = signal(false);
  readonly view = signal<CommentsViewDto | null>(null);
  readonly theme = signal<string | null>(null);
  readonly edits = signal<Readonly<Record<string, ReplyEdit>>>({});
  readonly pendingReply = signal<PendingReply | null>(null);
  readonly holding = signal<AudienceCommentDto | null>(null);

  readonly syncRequested = computed(() => (this.view()?.syncRequestedAt ?? null) !== null);
  readonly themes = computed<ThemeCount[]>(() => {
    const counts = new Map<string, number>();
    for (const c of this.view()?.comments ?? []) {
      if (c.theme === '' || UNCOUNTED.includes(c.status)) continue;
      counts.set(c.theme, (counts.get(c.theme) ?? 0) + 1);
    }
    return [...counts]
      .map(([theme, count]) => ({ theme, count }))
      .sort((a, b) => b.count - a.count || a.theme.localeCompare(b.theme));
  });
  private readonly shown = computed(() =>
    (this.view()?.comments ?? []).filter((c) => this.theme() === null || c.theme === this.theme()),
  );
  readonly questions = computed(() => this.shown().filter(replyable));
  readonly replies = computed(() =>
    this.shown().filter((c) => REPLIES.includes(c.status) || failedReply(c)),
  );
  readonly others = computed(() =>
    this.shown().filter((c) => OPEN.includes(c.status) && !replyable(c) && !quarantined(c)),
  );
  readonly quarantine = computed(() => this.shown().filter(quarantined));
  readonly ideas = computed(() =>
    (this.view()?.ideas ?? []).filter((i) => i.status === 'proposed'),
  );

  async ngOnInit(): Promise<void> {
    this.timer = setInterval(() => void this.refresh(), POLL_MS);
    await this.refresh();
  }

  ngOnDestroy(): void {
    if (this.timer !== null) clearInterval(this.timer);
  }

  london(iso: string): string {
    return formatLondon(iso);
  }

  replyText(c: AudienceCommentDto): string {
    const edit = this.edits()[c.id];
    return edit !== undefined && edit.from === c.draft ? edit.text : c.draft;
  }

  replyValid(c: AudienceCommentDto): boolean {
    const length = this.replyText(c).trim().length;
    return length > 0 && length <= MAX_REPLY;
  }

  edit(c: AudienceCommentDto, text: string): void {
    this.edits.update((all) => ({ ...all, [c.id]: { from: c.draft, text } }));
  }

  replyUrl(videoId: string, replyId: string): string {
    return `https://www.youtube.com/watch?v=${encodeURIComponent(videoId)}&lc=${encodeURIComponent(replyId)}`;
  }

  async openReplyLink(videoId: string, replyId: string): Promise<void> {
    this.actionError.set(null);
    try {
      await this.bridge.appOpenExternal({ url: this.replyUrl(videoId, replyId) });
    } catch (err: unknown) {
      this.actionError.set(readIpcError(err).message);
    }
  }

  openReply(c: AudienceCommentDto): void {
    this.actionError.set(null);
    this.pendingReply.set({ comment: c, text: this.replyText(c).trim() });
  }

  openHold(c: AudienceCommentDto): void {
    this.actionError.set(null);
    this.holding.set(c);
  }

  closeDialogs(): void {
    this.pendingReply.set(null);
    this.holding.set(null);
    this.actionError.set(null);
  }

  sync(): Promise<boolean> {
    return this.act(() => this.bridge.operatorRequestCommentSync());
  }

  decide(c: AudienceCommentDto, decision: CommentDecision): Promise<boolean> {
    return this.act(() => this.bridge.operatorDecideComment({ commentId: c.id, decision }));
  }

  decideIdea(ideaId: string, use: boolean): Promise<boolean> {
    return this.act(() => this.bridge.operatorDecideIdea({ ideaId, use }));
  }

  async approve(r: PendingReply): Promise<void> {
    if (
      await this.act(() =>
        this.bridge.operatorApproveReply({ commentId: r.comment.id, text: r.text }),
      )
    ) {
      this.pendingReply.set(null);
    }
  }

  async hold(c: AudienceCommentDto): Promise<void> {
    if (await this.decide(c, 'hold')) this.holding.set(null);
  }

  private async act(action: () => Promise<CommentsViewDto>): Promise<boolean> {
    this.busy.set(true);
    this.actionError.set(null);
    try {
      this.show(await action());
      return true;
    } catch (err: unknown) {
      this.actionError.set(readIpcError(err).message);
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  private show(view: CommentsViewDto): void {
    this.view.set(view);
    if (!this.themes().some((t) => t.theme === this.theme())) this.theme.set(null);
    const kept = new Map(view.comments.filter(replyable).map((c) => [c.id, c.draft]));
    this.edits.update((all) =>
      Object.fromEntries(Object.entries(all).filter(([id, e]) => kept.get(id) === e.from)),
    );
  }

  private async refresh(): Promise<void> {
    try {
      this.show(await this.bridge.operatorComments());
      this.error.set(null);
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    }
  }
}
