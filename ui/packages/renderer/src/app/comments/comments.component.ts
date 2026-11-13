import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  TemplateRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, type MatDialogConfig, type MatDialogRef } from '@angular/material/dialog';

import {
  readIpcError,
  type AudienceCommentDto,
  type CommentDecision,
  type CommentsViewDto,
} from '@scout/shared';

import { IconComponent } from '../core/icon.component';
import { ScoutBridgeService } from '../core/scout-bridge';
import { StatusChipComponent, type ChipTone } from '../core/status-chip.component';
import { formatLondon } from '../operator/london';

const POLL_MS = 10_000;
const MAX_REPLY = 1000;
const DAY_MS = 86_400_000;
const OPEN = ['new', 'draft_requested', 'drafted', 'failed'];
const ASKING = ['question', 'request'];
const REPLIES = ['reply_approved', 'replying', 'replied'];
const QUARANTINE = ['quarantined', 'hold_approved', 'held'];
const UNCOUNTED = ['quarantined', 'held', 'dismissed'];
const KINDS: Readonly<Record<string, { label: string; tone: string }>> = {
  question: { label: 'Question', tone: 'info' },
  request: { label: 'Request', tone: 'info' },
  praise: { label: 'Praise', tone: 'ok' },
  feedback: { label: 'Feedback', tone: 'neutral' },
  other: { label: 'Other', tone: 'neutral' },
  spam: { label: 'Looks like spam', tone: 'error' },
};
const REPLY_STATES: Readonly<Record<string, { label: string; tone: ChipTone }>> = {
  reply_approved: { label: 'Waiting to post', tone: 'info' },
  replying: { label: 'Posting', tone: 'info' },
  replied: { label: 'Posted on YouTube', tone: 'ok' },
};
const HOLD_STATES: Readonly<Record<string, { label: string; tone: ChipTone }>> = {
  hold_approved: { label: 'Waiting to hold', tone: 'info' },
  held: { label: 'Held for review on YouTube', tone: 'ok' },
};
const FAILED: { label: string; tone: ChipTone } = { label: 'Failed', tone: 'error' };

type Tab = 'questions' | 'replies' | 'others' | 'quarantine' | 'ideas';

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
  imports: [IconComponent, MatButtonModule, StatusChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <div class="head">
        <div class="titles">
          <h1>Comments</h1>
          <p class="lede">
            The comments on your channel, the replies Scout drafted and the ideas your audience
            asked for. Nothing is posted until you approve it.
          </p>
        </div>
        <div class="sync">
          <button
            type="button"
            matButton="filled"
            data-test="sync-now"
            [disabled]="busy() || syncRequested() || !loaded()"
            (click)="sync()"
          >
            <sc-icon name="refresh" [size]="16" />{{
              syncRequested() ? 'Sync requested' : 'Sync now'
            }}
          </button>
          <span class="meta" role="status" data-test="sync-status">{{ syncLine() }}</span>
          @if (view()?.syncError; as message) {
            <span class="err" data-test="sync-error">{{ message }}</span>
          }
        </div>
      </div>

      @if (error(); as message) {
        <p class="err" role="alert" data-test="comments-error">{{ message }}</p>
      }
      @if (actionError(); as message) {
        @if (pendingReply() === null && holding() === null) {
          <p class="err" role="alert" data-test="comments-action-error">{{ message }}</p>
        }
      }

      @if (!loaded()) {
        @if (error() === null) {
          <div
            class="loading"
            role="status"
            aria-label="Loading comments"
            data-test="comments-loading"
          >
            <div class="bar"><div class="bar-fill"></div></div>
            @for (w of skeleton; track $index) {
              <div class="card skeleton-card">
                <div class="skeleton skeleton--meta"></div>
                <div class="skeleton skeleton--line" [style.width]="w"></div>
                <div class="skeleton skeleton--box"></div>
              </div>
            }
          </div>
        }
      } @else if (nothing()) {
        <div class="card empty-card" data-test="comments-empty">
          <span class="empty-icon" aria-hidden="true"><sc-icon name="chat" [size]="22" /></span>
          <span>No comments on your channel yet. Scout reads new ones each time it syncs.</span>
          <button
            type="button"
            matButton="outlined"
            class="sm"
            data-test="sync-now-empty"
            [disabled]="busy() || syncRequested() || !loaded()"
            (click)="sync()"
          >
            <sc-icon name="refresh" [size]="16" />Sync now
          </button>
        </div>
      } @else {
        <div class="tabs" role="tablist" aria-label="Comment sections">
          @for (t of tabs(); track t.id) {
            <button
              type="button"
              role="tab"
              class="tab"
              [id]="'tab-' + t.id"
              [attr.aria-selected]="tab() === t.id"
              [attr.aria-controls]="'panel-' + t.id"
              [attr.data-test]="'tab-' + t.id"
              (click)="tab.set(t.id)"
            >
              {{ t.label
              }}<span class="count" [class.count--warn]="t.id === 'questions'">{{ t.count }}</span>
            </button>
          }
        </div>

        <div class="themes" role="group" aria-label="Filter by theme">
          <span class="meta themes-label">Themes</span>
          @if (themes().length === 0) {
            <span class="meta" data-test="themes-empty">No themes yet.</span>
          } @else {
            <button
              type="button"
              class="chip"
              data-test="theme-all"
              [attr.aria-pressed]="theme() === null"
              (click)="theme.set(null)"
            >
              All
            </button>
            @for (t of themes(); track t.theme) {
              <button
                type="button"
                class="chip"
                data-test="theme-chip"
                [attr.aria-pressed]="theme() === t.theme"
                (click)="theme.set(t.theme)"
              >
                {{ t.theme }} ({{ t.count }})
              </button>
            }
          }
        </div>

        <div
          class="panel"
          role="tabpanel"
          id="panel-questions"
          aria-labelledby="tab-questions"
          [hidden]="tab() !== 'questions'"
        >
          <p class="intro">Scout drafted these replies. Nothing is posted until you approve it.</p>
          @if (questions().length === 0) {
            <p class="empty" data-test="questions-empty">
              No question or request waits for a reply.
            </p>
          } @else {
            <ul class="cards">
              @for (c of questions(); track c.id) {
                <li class="card" [attr.data-test]="'question-' + c.id">
                  <div class="meta-row">
                    <strong class="author" data-test="comment-author">{{ c.author }}</strong>
                    <span>on "{{ c.videoTitle }}"</span>
                    <span [title]="london(c.publishedAt) + ' London'"
                      >· {{ when(c.publishedAt) }}</span
                    >
                    <span [class]="'kind kind--' + kind(c.kind).tone">{{
                      kind(c.kind).label
                    }}</span>
                    @if (c.status === 'drafted') {
                      <sc-status-chip tone="warn" label="Draft waiting for your approval" />
                    }
                  </div>
                  <p class="text" data-test="comment-text">{{ c.text }}</p>
                  @if (c.status === 'draft_requested') {
                    <p class="meta" [attr.data-test]="'draft-requested-' + c.id">
                      Draft requested; the Scout worker writes it within a minute.
                    </p>
                  }
                  @if (failedReply(c)) {
                    <p class="err" [attr.data-test]="'question-error-' + c.id">{{ c.error }}</p>
                  }
                  <div class="field">
                    <div class="label-row">
                      <label [for]="'reply-' + c.id">{{
                        c.draft === '' ? 'Your reply' : 'Your reply (drafted by Scout, edit freely)'
                      }}</label>
                      <span
                        class="meta"
                        [class.over]="replyText(c).trim().length > maxReply"
                        [attr.data-test]="'reply-count-' + c.id"
                        >{{ replyText(c).trim().length }} / {{ maxReply }}</span
                      >
                    </div>
                    <textarea
                      rows="3"
                      [id]="'reply-' + c.id"
                      [attr.data-test]="'reply-text-' + c.id"
                      [value]="replyText(c)"
                      (input)="edit(c, $any($event.target).value)"
                    ></textarea>
                  </div>
                  <div class="row">
                    <button
                      type="button"
                      matButton="filled"
                      class="sm"
                      [attr.data-test]="'reply-approve-' + c.id"
                      [disabled]="busy() || !replyValid(c)"
                      (click)="openReply(c)"
                    >
                      <sc-icon name="check" [size]="16" />Approve reply
                    </button>
                    <button
                      type="button"
                      matButton="outlined"
                      class="sm"
                      [attr.data-test]="'question-dismiss-' + c.id"
                      [disabled]="busy()"
                      (click)="decide(c, 'dismiss')"
                    >
                      <sc-icon name="close" [size]="16" />Dismiss
                    </button>
                  </div>
                </li>
              }
            </ul>
          }
        </div>

        <div
          class="panel"
          role="tabpanel"
          id="panel-replies"
          aria-labelledby="tab-replies"
          [hidden]="tab() !== 'replies'"
        >
          @if (replies().length === 0) {
            <p class="empty" data-test="replies-empty">No reply approved yet.</p>
          } @else {
            <ul class="cards">
              @for (c of replies(); track c.id) {
                <li class="card" [attr.data-test]="'reply-' + c.id">
                  <div class="meta-row">
                    <span
                      >Reply to
                      <strong class="to" data-test="comment-author">{{ c.author }}</strong> on "{{
                        c.videoTitle
                      }}"</span
                    >
                    <sc-status-chip
                      [attr.data-test]="'reply-status-' + c.id"
                      [tone]="replyState(c).tone"
                      [label]="replyState(c).label"
                    />
                  </div>
                  <p class="text">{{ c.approvedText }}</p>
                  @switch (c.status) {
                    @case ('reply_approved') {
                      <button
                        type="button"
                        matButton="outlined"
                        class="xs"
                        [attr.data-test]="'cancel-' + c.id"
                        [disabled]="busy()"
                        (click)="decide(c, 'dismiss')"
                      >
                        <sc-icon name="close" [size]="14" />Cancel before it posts
                      </button>
                    }
                    @case ('replying') {}
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
                      <p class="err" [attr.data-test]="'reply-error-' + c.id">{{ c.error }}</p>
                    }
                  }
                </li>
              }
            </ul>
          }
        </div>

        <div
          class="panel"
          role="tabpanel"
          id="panel-others"
          aria-labelledby="tab-others"
          [hidden]="tab() !== 'others'"
        >
          @if (others().length === 0) {
            <p class="empty" data-test="others-empty">No other comment.</p>
          } @else {
            <ul class="cards">
              @for (c of others(); track c.id) {
                <li class="card" [attr.data-test]="'other-' + c.id">
                  <div class="meta-row">
                    <strong class="author" data-test="comment-author">{{ c.author }}</strong>
                    <span>on "{{ c.videoTitle }}" · {{ when(c.publishedAt) }}</span>
                    <span [class]="'kind kind--' + kind(c.kind).tone">{{
                      kind(c.kind).label
                    }}</span>
                  </div>
                  <p class="text" data-test="comment-text">{{ c.text }}</p>
                  <div class="row">
                    <button
                      type="button"
                      matButton="outlined"
                      class="xs"
                      [attr.data-test]="'other-draft-' + c.id"
                      [disabled]="busy()"
                      (click)="decide(c, 'draft')"
                    >
                      <sc-icon name="edit" [size]="14" />Draft a reply
                    </button>
                    <button
                      type="button"
                      matButton="outlined"
                      class="xs"
                      [attr.data-test]="'other-dismiss-' + c.id"
                      [disabled]="busy()"
                      (click)="decide(c, 'dismiss')"
                    >
                      <sc-icon name="close" [size]="14" />Dismiss
                    </button>
                  </div>
                </li>
              }
            </ul>
          }
        </div>

        <div
          class="panel"
          role="tabpanel"
          id="panel-quarantine"
          aria-labelledby="tab-quarantine"
          [hidden]="tab() !== 'quarantine'"
        >
          @if (quarantine().length === 0) {
            <p class="empty" data-test="quarantine-empty">No comment is quarantined.</p>
          } @else {
            <ul class="cards">
              @for (c of quarantine(); track c.id) {
                <li class="card" [attr.data-test]="'quarantine-' + c.id">
                  <div class="meta-row">
                    <strong class="author" data-test="comment-author">{{ c.author }}</strong>
                    <span>on "{{ c.videoTitle }}" · {{ when(c.publishedAt) }}</span>
                    <span [class]="'kind kind--' + kind(c.kind).tone">{{
                      kind(c.kind).label
                    }}</span>
                    @if (c.status !== 'quarantined') {
                      <sc-status-chip
                        [attr.data-test]="'quarantine-status-' + c.id"
                        [tone]="holdState(c).tone"
                        [label]="holdState(c).label"
                      />
                    }
                  </div>
                  <p class="text" data-test="comment-text">{{ c.text }}</p>
                  @switch (c.status) {
                    @case ('quarantined') {
                      <div class="row">
                        <button
                          type="button"
                          matButton="outlined"
                          class="xs"
                          [attr.data-test]="'not-spam-' + c.id"
                          [disabled]="busy()"
                          (click)="decide(c, 'not_spam')"
                        >
                          <sc-icon name="check" [size]="14" />Not spam
                        </button>
                        <button
                          type="button"
                          matButton="outlined"
                          class="xs"
                          [attr.data-test]="'hold-' + c.id"
                          [disabled]="busy()"
                          (click)="openHold(c)"
                        >
                          <svg
                            class="glyph"
                            width="14"
                            height="14"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            stroke-width="2"
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            aria-hidden="true"
                          >
                            <path d="M12 2l8 4v6c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10V6z" /></svg
                          >Hold for review on YouTube
                        </button>
                      </div>
                    }
                    @case ('hold_approved') {
                      <button
                        type="button"
                        matButton="outlined"
                        class="xs"
                        [attr.data-test]="'cancel-' + c.id"
                        [disabled]="busy()"
                        (click)="decide(c, 'dismiss')"
                      >
                        <sc-icon name="close" [size]="14" />Cancel
                      </button>
                    }
                    @case ('held') {}
                    @default {
                      <p class="err" [attr.data-test]="'quarantine-error-' + c.id">{{ c.error }}</p>
                      <button
                        type="button"
                        matButton="outlined"
                        class="xs"
                        [attr.data-test]="'quarantine-dismiss-' + c.id"
                        [disabled]="busy()"
                        (click)="decide(c, 'dismiss')"
                      >
                        <sc-icon name="close" [size]="14" />Dismiss
                      </button>
                    }
                  }
                </li>
              }
            </ul>
          }
        </div>

        <div
          class="panel"
          role="tabpanel"
          id="panel-ideas"
          aria-labelledby="tab-ideas"
          [hidden]="tab() !== 'ideas'"
        >
          @if (ideas().length === 0) {
            <p class="empty" data-test="ideas-empty">No idea from your audience yet.</p>
          } @else {
            <ul class="cards">
              @for (i of ideas(); track i.id) {
                <li class="card idea" [attr.data-test]="'idea-' + i.id">
                  <div class="idea-text">
                    <span class="idea-title" data-test="idea-text">{{ i.text }}</span>
                    @if (askedBy(i.commentId); as c) {
                      <span class="meta" [attr.data-test]="'idea-source-' + i.id"
                        >Asked by {{ c.author }} on "{{ c.videoTitle }}"</span
                      >
                    }
                  </div>
                  <button
                    type="button"
                    matButton="filled"
                    class="sm"
                    [attr.data-test]="'idea-use-' + i.id"
                    [disabled]="busy()"
                    (click)="decideIdea(i.id, true)"
                  >
                    <sc-icon name="video" [size]="16" />Make a video
                  </button>
                  <button
                    type="button"
                    matButton="outlined"
                    class="sm"
                    [attr.data-test]="'idea-dismiss-' + i.id"
                    [disabled]="busy()"
                    (click)="decideIdea(i.id, false)"
                  >
                    <sc-icon name="close" [size]="16" />Dismiss
                  </button>
                </li>
              }
            </ul>
          }
        </div>
      }
    </main>

    <ng-template #replyDialog>
      @if (pendingReply(); as r) {
        <div class="dialog" data-test="reply-dialog">
          <h2 id="rp-title" class="dialog-title">Approve the reply?</h2>
          <p id="rp-body" class="dialog-body">
            Scout posts this reply to {{ r.comment.author }} on "{{ r.comment.videoTitle }}" within
            a minute. You can cancel it until then.
          </p>
          <blockquote class="quote" data-test="reply-preview">{{ r.text }}</blockquote>
          <span class="meta" data-test="reply-actions">Action: post a public reply on YouTube</span>
          @if (actionError(); as message) {
            <p class="err" data-test="comments-dialog-error">{{ message }}</p>
          }
          <div class="actions">
            <button
              type="button"
              matButton="outlined"
              cdkFocusInitial
              data-test="reply-cancel"
              (click)="closeDialogs()"
            >
              Cancel
            </button>
            <button
              type="button"
              matButton="filled"
              data-test="reply-confirm"
              [disabled]="busy()"
              (click)="approve(r)"
            >
              <sc-icon name="check" [size]="16" />Approve and post
            </button>
          </div>
        </div>
      }
    </ng-template>

    <ng-template #holdDialog>
      @if (holding(); as c) {
        <div class="dialog" data-test="hold-dialog">
          <h2 id="hd-title" class="dialog-title">Hold the comment on YouTube?</h2>
          <p id="hd-body" class="dialog-body">
            YouTube hides the comment until you review it in YouTube Studio. Scout does this within
            a minute.
          </p>
          <span class="meta" data-test="hold-actions"
            >Action: hold a comment for review on YouTube</span
          >
          @if (actionError(); as message) {
            <p class="err" data-test="comments-dialog-error">{{ message }}</p>
          }
          <div class="actions">
            <button
              type="button"
              matButton="outlined"
              cdkFocusInitial
              data-test="hold-cancel"
              (click)="closeDialogs()"
            >
              Cancel
            </button>
            <button
              type="button"
              matButton="filled"
              data-test="hold-confirm"
              [disabled]="busy()"
              (click)="hold(c)"
            >
              <svg
                class="glyph"
                width="16"
                height="16"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path d="M12 2l8 4v6c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10V6z" /></svg
              >Hold the comment
            </button>
          </div>
        </div>
      }
    </ng-template>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .page,
      .dialog {
        --mat-button-outlined-container-height: 40px;
        --mat-button-outlined-horizontal-padding: 16px;
        --mat-button-outlined-label-text-weight: 400;
        --mat-button-outlined-label-text-size: 14px;
        --mat-button-outlined-label-text-tracking: 0;
        --mat-button-filled-container-height: 40px;
        --mat-button-filled-horizontal-padding: 16px;
        --mat-button-filled-label-text-weight: 400;
        --mat-button-filled-label-text-size: 14px;
        --mat-button-filled-label-text-tracking: 0;
        --mat-button-filled-disabled-container-color: var(--sc-surface-2);
        --mat-button-filled-disabled-label-text-color: var(--sc-muted);
        color: var(--sc-fg);
        font-size: 14px;
      }
      .page {
        padding: 24px;
        max-width: 1180px;
        width: 100%;
        box-sizing: border-box;
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .sm {
        --mat-button-outlined-container-height: 36px;
        --mat-button-outlined-horizontal-padding: 14px;
        --mat-button-filled-container-height: 36px;
        --mat-button-filled-horizontal-padding: 14px;
      }
      .xs {
        --mat-button-outlined-container-height: 32px;
        --mat-button-outlined-horizontal-padding: 12px;
        --mat-button-outlined-label-text-size: 13px;
      }
      sc-icon,
      .glyph {
        margin-right: 8px;
        vertical-align: middle;
      }
      .xs sc-icon,
      .xs .glyph {
        margin-right: 6px;
      }
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-start;
        gap: 12px;
      }
      .titles {
        display: flex;
        flex-direction: column;
        gap: 4px;
        margin-right: auto;
        min-width: 0;
        flex: 1 1 320px;
      }
      h1 {
        margin: 0;
        font-size: 24px;
        font-weight: 600;
        letter-spacing: -0.01em;
      }
      .lede {
        margin: 0;
        color: var(--sc-fg-2);
        line-height: 1.5;
      }
      .sync {
        display: flex;
        flex-direction: column;
        align-items: flex-end;
        gap: 4px;
      }
      .meta,
      .empty {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .err {
        margin: 0;
        color: var(--sc-error);
      }
      .intro {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .card {
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .loading {
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .bar {
        height: 4px;
        border-radius: 2px;
        background: var(--sc-surface-2);
        overflow: hidden;
      }
      .bar-fill {
        height: 4px;
        width: 40%;
        background: var(--sc-accent);
      }
      .skeleton {
        background: var(--sc-surface-2);
      }
      .skeleton--meta {
        height: 10px;
        width: 30%;
        border-radius: 5px;
      }
      .skeleton--line {
        height: 12px;
        border-radius: 6px;
      }
      .skeleton--box {
        height: 60px;
        width: 100%;
        border-radius: 10px;
      }
      .empty-card {
        padding: 36px 18px;
        align-items: center;
        text-align: center;
        gap: 10px;
      }
      .empty-icon {
        width: 44px;
        height: 44px;
        border-radius: 22px;
        background: var(--sc-surface-2);
        color: var(--sc-accent);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .empty-icon sc-icon {
        margin-right: 0;
      }
      .tabs {
        display: flex;
        flex-wrap: wrap;
        gap: 4px;
        border-bottom: 1px solid var(--sc-line);
      }
      .tab {
        border: 0;
        border-bottom: 2px solid transparent;
        background: transparent;
        color: var(--sc-fg-2);
        padding: 10px 12px;
        font: inherit;
        font-weight: 500;
        display: flex;
        align-items: center;
        gap: 6px;
        cursor: pointer;
      }
      .tab[aria-selected='true'] {
        border-bottom-color: var(--sc-accent);
        color: var(--sc-fg);
        font-weight: 600;
      }
      .count {
        min-width: 20px;
        padding: 1px 6px;
        box-sizing: border-box;
        border-radius: 10px;
        font-size: 12px;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .count--warn {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .themes {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        align-items: center;
      }
      .themes-label {
        margin-right: 4px;
      }
      .chip {
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg-2);
        border-radius: 14px;
        padding: 4px 12px;
        font: inherit;
        font-size: 13px;
        cursor: pointer;
      }
      .chip[aria-pressed='true'] {
        border-color: var(--sc-accent);
        background: var(--sc-surface-2);
        color: var(--sc-fg);
      }
      .panel {
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .panel[hidden] {
        display: none;
      }
      .cards {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .meta-row {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        align-items: center;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .author {
        color: var(--sc-fg);
        font-size: 14px;
      }
      .to {
        color: var(--sc-fg);
      }
      .kind {
        padding: 2px 9px;
        border-radius: 12px;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .kind--info {
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
      }
      .kind--ok {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .kind--error {
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
      }
      .text {
        margin: 0;
        line-height: 1.5;
        white-space: pre-wrap;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .label-row {
        display: flex;
        justify-content: space-between;
      }
      label {
        font-size: 13px;
        font-weight: 600;
      }
      .over {
        color: var(--sc-error);
      }
      textarea {
        padding: 10px 12px;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        color: var(--sc-fg);
        font: inherit;
        resize: vertical;
        line-height: 1.45;
      }
      .row {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      .card > button {
        align-self: flex-start;
      }
      .idea {
        flex-direction: row;
        flex-wrap: wrap;
        gap: 10px;
        align-items: center;
      }
      .idea-text {
        flex: 1 1 300px;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .idea-title {
        font-weight: 600;
      }
      .link {
        align-self: flex-start;
        background: none;
        border: none;
        color: var(--sc-accent);
        cursor: pointer;
        padding: 0;
        font: inherit;
        text-decoration: underline;
      }
      .dialog {
        padding: 20px;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .dialog-title {
        margin: 0;
        font-size: 17px;
        font-weight: 600;
        overflow-wrap: anywhere;
      }
      .dialog-body {
        margin: 0;
        color: var(--sc-fg-2);
        line-height: 1.5;
      }
      .quote {
        margin: 0;
        padding: 10px 12px;
        border-left: 3px solid var(--sc-accent);
        background: var(--sc-bg);
        border-radius: 0 10px 10px 0;
        line-height: 1.45;
        white-space: pre-wrap;
        overflow-wrap: anywhere;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
        justify-content: flex-end;
      }
    `,
  ],
})
export class CommentsComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly dialog = inject(MatDialog);
  private readonly replyDialog = viewChild.required<TemplateRef<unknown>>('replyDialog');
  private readonly holdDialog = viewChild.required<TemplateRef<unknown>>('holdDialog');
  private dialogRef: MatDialogRef<unknown> | null = null;
  private timer: ReturnType<typeof setInterval> | null = null;

  readonly maxReply = MAX_REPLY;
  readonly skeleton = ['70%', '55%', '62%'];
  readonly failedReply = failedReply;
  readonly loaded = signal(false);
  readonly error = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly busy = signal(false);
  readonly view = signal<CommentsViewDto | null>(null);
  readonly theme = signal<string | null>(null);
  readonly tab = signal<Tab>('questions');
  readonly edits = signal<Readonly<Record<string, ReplyEdit>>>({});
  readonly pendingReply = signal<PendingReply | null>(null);
  readonly holding = signal<AudienceCommentDto | null>(null);

  readonly syncRequested = computed(() => (this.view()?.syncRequestedAt ?? null) !== null);
  readonly syncLine = computed(() => {
    if (!this.loaded()) return 'Loading comments';
    if (this.syncRequested()) return 'Scout syncs within a minute.';
    const at = this.view()?.syncedAt ?? null;
    return at === null ? 'Not synced yet.' : `Last synced ${this.dayAt(at)}, London time`;
  });
  readonly nothing = computed(
    () => (this.view()?.comments.length ?? 0) === 0 && (this.view()?.ideas.length ?? 0) === 0,
  );
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
  readonly tabs = computed<{ id: Tab; label: string; count: number }[]>(() => [
    { id: 'questions', label: 'Waiting for you', count: this.questions().length },
    { id: 'replies', label: 'Replies', count: this.replies().length },
    { id: 'others', label: 'Other comments', count: this.others().length },
    { id: 'quarantine', label: 'Quarantine', count: this.quarantine().length },
    { id: 'ideas', label: 'Audience ideas', count: this.ideas().length },
  ]);

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

  when(iso: string): string {
    const at = formatLondon(iso);
    const day = at.slice(0, 10);
    const now = Date.now();
    if (day === formatLondon(new Date(now).toISOString()).slice(0, 10)) {
      return `Today, ${at.slice(11)}`;
    }
    if (day === formatLondon(new Date(now - DAY_MS).toISOString()).slice(0, 10)) {
      return `Yesterday, ${at.slice(11)}`;
    }
    return `${day}, ${at.slice(11)}`;
  }

  dayAt(iso: string): string {
    const at = formatLondon(iso);
    const today = formatLondon(new Date().toISOString()).slice(0, 10);
    return `${at.slice(0, 10) === today ? 'today' : at.slice(0, 10)} at ${at.slice(11)}`;
  }

  kind(kind: string): { label: string; tone: string } {
    return KINDS[kind] ?? { label: kind, tone: 'neutral' };
  }

  replyState(c: AudienceCommentDto): { label: string; tone: ChipTone } {
    return REPLY_STATES[c.status] ?? FAILED;
  }

  holdState(c: AudienceCommentDto): { label: string; tone: ChipTone } {
    return HOLD_STATES[c.status] ?? FAILED;
  }

  askedBy(commentId: string): AudienceCommentDto | null {
    return this.view()?.comments.find((c) => c.id === commentId) ?? null;
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
    this.openDialog(this.replyDialog(), {
      ariaLabelledBy: 'rp-title',
      ariaDescribedBy: 'rp-body',
      width: '500px',
      maxHeight: 'calc(100vh - 216px)',
    });
  }

  openHold(c: AudienceCommentDto): void {
    this.actionError.set(null);
    this.holding.set(c);
    this.openDialog(this.holdDialog(), {
      ariaLabelledBy: 'hd-title',
      ariaDescribedBy: 'hd-body',
      width: '460px',
    });
  }

  closeDialogs(): void {
    this.dialogRef?.close();
    this.dialogRef = null;
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
      this.closeDialogs();
    }
  }

  async hold(c: AudienceCommentDto): Promise<void> {
    if (await this.decide(c, 'hold')) this.closeDialogs();
  }

  private openDialog(template: TemplateRef<unknown>, config: MatDialogConfig): void {
    const ref = this.dialog.open(template, {
      role: 'dialog',
      restoreFocus: true,
      panelClass: 'sc-dialog',
      backdropClass: 'sc-dialog-backdrop',
      maxWidth: 'calc(100vw - 32px)',
      position: { top: '200px' },
      ...config,
    });
    this.dialogRef = ref;
    ref.afterClosed().subscribe(() => {
      if (this.dialogRef === ref) this.closeDialogs();
    });
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
      this.loaded.set(true);
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    }
  }
}
