import {
  ChangeDetectionStrategy,
  Component,
  Pipe,
  computed,
  effect,
  inject,
  input,
  signal,
  type PipeTransform,
} from '@angular/core';

import type {
  GitHubVerdictRequest,
  ReviewFinding,
  ReviewFindings,
  ReviewTests,
} from '@scout/shared';

import { LocalConversation } from './local-conversation';
import { MarkdownPipe } from './markdown.pipe';
import {
  checkMark,
  commentBody,
  issueBody,
  issuesAsk,
  parseReviewFindings,
  reviewBody,
  verdictRequest,
} from './review-findings';
import { ReviewFindingsStore, type EditedText } from './review-findings.store';
import { ScoutBridgeService } from './scout-bridge';

const SEVERITY_LABELS: Readonly<Record<ReviewFinding['severity'], string>> = {
  block: 'Blocking',
  fix: 'Fix',
  nit: 'Suggestion',
};
const MAX_COMMENT_CHARS = 2000;
/** `reply | reviewFindings`: the reply's findings block and the text around it, parsed once per reply. */
@Pipe({ name: 'reviewFindings', standalone: true, pure: true })
export class ReviewFindingsPipe implements PipeTransform {
  transform(reply: string): ReturnType<typeof parseReviewFindings> {
    return parseReviewFindings(reply);
  }
}

/**
 * The findings of a code review or an audit as cards the owner decides on one by one, Yes, No or an
 * edited text, with a preview of how they read on GitHub; for a review, Post sends the chosen comments
 * to Scout Desktop as one review the owner approved by that click, for an audit, Open asks Scout to queue
 * one issue per chosen finding for the owner's approval, and the cards say where each went.
 */
@Component({
  selector: 'sc-review-findings',
  standalone: true,
  imports: [MarkdownPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (review().checks || review().tests) {
      <section class="tests" data-test="tests" aria-label="Tests">
        <div class="chips">
          @if (review().checks; as checks) {
            @for (check of checks; track $index) {
              @let tone = checkMark(check).toLowerCase();
              @if (check.url) {
                <a
                  href="#"
                  data-test="check"
                  [class]="'chip ' + tone"
                  [attr.title]="'Details of ' + check.name"
                  (click)="$event.preventDefault(); open(check.url)"
                  ><svg viewBox="0 0 24 24" aria-hidden="true">
                    <path [attr.d]="chipIcon(tone)"></path></svg
                  ><span data-test="check-name">{{ check.name }}</span
                  >&ngsp; <span data-test="check-mark">{{ tone }}</span></a
                >
              } @else {
                <span data-test="check" [class]="'chip ' + tone"
                  ><svg viewBox="0 0 24 24" aria-hidden="true">
                    <path [attr.d]="chipIcon(tone)"></path></svg
                  ><span data-test="check-name">{{ check.name }}</span
                  >&ngsp; <span data-test="check-mark">{{ tone }}</span></span
                >
              }
            }
          }
          @if (review().tests; as tests) {
            @let tone = runTone(tests);
            <span data-test="tests-run" [class]="'chip ' + tone"
              ><svg viewBox="0 0 24 24" aria-hidden="true">
                <path [attr.d]="chipIcon(tone)"></path></svg
              >{{ runChip(tests) }}</span
            >
          }
        </div>
        @if (review().tests; as tests) {
          @if (tests.ran && tests.exitCode !== 0 && tests.output) {
            <details class="output" data-test="tests-output">
              <summary>Last output</summary>
              <pre>{{ tests.output }}</pre>
            </details>
          }
        }
      </section>
    }
    <div class="cards">
      @for (finding of review().findings; track finding.id) {
        <article
          class="card"
          data-test="finding"
          [class.yes]="state().picks[finding.id] === 'yes' && !sentIds().has(finding.id)"
          [class.no]="state().picks[finding.id] === 'no' && !sentIds().has(finding.id)"
          [class.sent]="sentIds().has(finding.id)"
        >
          @if (sentIds().has(finding.id)) {
            <p class="sent" data-test="sent">
              <span class="sent-title">{{ finding.title }}</span>
              <span class="sent-mark" data-test="sent-mark"
                ><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 6 9 17l-5-5"></path></svg
                >Issue queued</span
              >
            </p>
          } @else if (state().picks[finding.id] === 'no') {
            <p class="dismissed" data-test="dismissed">
              <span class="dismissed-title">Dismissed: {{ finding.title }}</span>
              @if (!closed()) {
                <button
                  type="button"
                  class="small"
                  data-test="undo"
                  (click)="store.undo(turnId(), finding.id)"
                >
                  Undo
                </button>
              }
            </p>
          } @else {
            <div class="head">
              <span [class]="'pill ' + finding.severity" data-test="severity">{{
                severityLabel(finding)
              }}</span>
              <h3>{{ finding.title }}</h3>
            </div>
            <a
              class="path"
              href="#"
              data-test="path"
              (click)="$event.preventDefault(); open(pathUrl(finding))"
              >{{ finding.path }}:{{ finding.line }}</a
            >
            <div class="body" [innerHTML]="details(finding) | markdown"></div>
            @if (finding.rule) {
              <p class="rule" data-test="rule">Rule: {{ finding.rule }}</p>
            }
            @if (!closed() && !postingReview()) {
              <div class="actions">
                <button
                  type="button"
                  data-test="yes"
                  class="yes-btn"
                  [class.on]="state().picks[finding.id] === 'yes'"
                  [attr.aria-pressed]="state().picks[finding.id] === 'yes'"
                  (click)="store.pick(turnId(), finding.id, 'yes')"
                >
                  <svg viewBox="0 0 24 24" aria-hidden="true">
                    <path d="M20 6 9 17l-5-5"></path>
                  </svg>
                  <span class="lbl">{{
                    audit() ? 'Yes, open an issue' : 'Yes, add to the PR'
                  }}</span>
                </button>
                <button
                  type="button"
                  data-test="no"
                  class="no-btn"
                  [class.on]="state().picks[finding.id] === 'no'"
                  [attr.aria-pressed]="state().picks[finding.id] === 'no'"
                  (click)="store.pick(turnId(), finding.id, 'no')"
                >
                  <svg viewBox="0 0 24 24" aria-hidden="true">
                    <path d="M18 6 6 18M6 6l12 12"></path>
                  </svg>
                  <span class="lbl">No</span>
                </button>
                <button
                  type="button"
                  data-test="edit"
                  [attr.aria-expanded]="state().editing === finding.id"
                  (click)="toggleEditor(finding)"
                >
                  <svg viewBox="0 0 24 24" aria-hidden="true">
                    <path d="M12 20h9"></path>
                    <path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"></path>
                  </svg>
                  <span class="lbl">{{
                    state().editing === finding.id ? 'Close editor' : 'Edit'
                  }}</span>
                </button>
              </div>
              @if (state().editing === finding.id) {
                <div class="editor" data-test="editor">
                  <label class="field-label" [attr.for]="'why-' + turnId() + '-' + finding.id"
                    >What is wrong</label
                  >
                  <textarea
                    data-test="comment-text"
                    [id]="'why-' + turnId() + '-' + finding.id"
                    [attr.maxlength]="maxChars"
                    [value]="draft().why"
                    (input)="draft.set({ why: $any($event.target).value, fix: draft().fix })"
                  ></textarea>
                  <label class="field-label" [attr.for]="'fix-' + turnId() + '-' + finding.id"
                    >Fix</label
                  >
                  <textarea
                    data-test="fix-text"
                    [id]="'fix-' + turnId() + '-' + finding.id"
                    [attr.maxlength]="maxChars"
                    [value]="draft().fix"
                    (input)="draft.set({ why: draft().why, fix: $any($event.target).value })"
                  ></textarea>
                  <div class="editor-row">
                    <span class="counter"
                      >{{ draft().why.length + draft().fix.length }} / {{ maxChars * 2 }}</span
                    >
                    <button
                      type="button"
                      class="small"
                      data-test="use-text"
                      [disabled]="
                        draft().why.trim().length === 0 || draft().fix.trim().length === 0
                      "
                      (click)="store.useText(turnId(), finding.id, draft())"
                    >
                      Use this text and mark Yes
                    </button>
                    <button type="button" class="small" (click)="store.cancelEdit(turnId())">
                      Cancel
                    </button>
                  </div>
                </div>
              }
            }
          }
        </article>
      }
    </div>
    @if (review().known; as known) {
      @if (known.length > 0) {
        <section class="known" data-test="known">
          <h4>Already documented, not raised again</h4>
          <ul>
            @for (item of known; track $index) {
              <li>{{ item }}</li>
            }
          </ul>
        </section>
      }
    }
    @if (posted(); as done) {
      <div class="posted" data-test="posted" role="status">
        <strong
          >{{ done.urls.length }} {{ done.urls.length === 1 ? 'issue' : 'issues' }} opened on
          {{ review().repository }}</strong
        >
        @for (url of done.urls; track url) {
          <a href="#" (click)="$event.preventDefault(); open(url)">{{ url }}</a>
        }
      </div>
    } @else if (reviewPosted(); as done) {
      <div class="posted" data-test="posted" role="status">
        <strong>Review posted on PR #{{ review().number }}</strong>
        <span class="posted-line"
          >Posted as one review that only comments, without approval or request for changes. The
          items with No stayed here in the chat.</span
        >
        @if (done.url) {
          <a href="#" (click)="$event.preventDefault(); open(done.url)">{{ done.url }}</a>
        }
      </div>
    } @else if (given(); as done) {
      <div class="posted" data-test="verdict-given" role="status">
        <strong>{{
          done.verdict === 'approve'
            ? 'Approved on PR #' + review().number
            : 'Changes requested on PR #' + review().number
        }}</strong>
        @if (done.url) {
          <a href="#" (click)="$event.preventDefault(); open(done.url)">{{ done.url }}</a>
        }
      </div>
    } @else if (postingReview()) {
      <div class="posting" role="status" data-test="posting">
        @if (target(); as writesTo) {
          <p class="target" data-test="target">{{ writesTo }}</p>
        }
        <span class="posting-line" data-test="verdict-giving"
          ><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M21 12a9 9 0 1 1-9-9"></path></svg
          >Posting the review… {{ selected().length }}
          {{ selected().length === 1 ? 'comment' : 'comments' }} in one review on PR #{{
            review().number
          }}; your click on Post was the approval.</span
        >
        <div class="bar" role="progressbar" aria-label="Posting the review"><div></div></div>
      </div>
    } @else if (allSent()) {
      <div class="posted" data-test="all-sent" role="status">
        <strong>Every chosen issue is queued on {{ review().repository }}</strong>
        <span class="posted-line"
          >Each waits for your approval, here and in Approvals; nothing is left to open.</span
        >
      </div>
    } @else if (allDismissed() && audit()) {
      <p class="all-dismissed" data-test="all-dismissed">
        Nothing to open; every finding was dismissed.
      </p>
    } @else {
      <div class="footer">
        @if (target(); as writesTo) {
          <p class="target" data-test="target">{{ writesTo }}</p>
        }
        @if (clean()) {
          <p class="clean" data-test="clean">No findings.</p>
          @if (review().read; as read) {
            <p class="clean" data-test="clean-read">What Scout says it read: {{ read }}</p>
          }
        } @else if (allDismissed()) {
          <p class="all-dismissed" data-test="all-dismissed">
            Nothing to post; every finding was dismissed.
          </p>
        } @else {
          <span class="summary" data-test="summary">{{ summary() }}</span>
        }
        @if (!clean()) {
          <div class="group">
            @if (!allDismissed()) {
              <button
                type="button"
                data-test="yes-all"
                (click)="store.yesAll(turnId(), findingIds())"
              >
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path d="M18 6 7 17l-5-5"></path>
                  <path d="m22 10-7.5 7.5L13 16"></path>
                </svg>
                <span class="lbl">Yes to all</span>
              </button>
            }
            @if (!allDismissed()) {
              <button
                type="button"
                class="primary"
                data-test="post"
                [disabled]="selected().length === 0 || writing() || (!audit() && !review().commit)"
                [attr.title]="audit() || review().commit ? null : noCommit"
                [attr.aria-label]="postLabel()"
                (click)="post()"
              >
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path d="M22 2 11 13"></path>
                  <path d="M22 2 15 22l-4-9-9-4Z"></path>
                </svg>
                <span class="lbl">{{ postLabel() }}</span>
              </button>
            }
          </div>
        }
        @if (!audit() && !giving() && !(clean() && review().selfAuthored === true)) {
          <div class="verdict" role="group" aria-label="Verdict">
            <span class="verdict-label">Verdict on the reviewed commit</span>
            @if (review().selfAuthored !== true) {
              <button
                type="button"
                class="approve"
                data-test="approve"
                [disabled]="verdictLocked()"
                [attr.title]="review().commit ? null : noCommit"
                (click)="giveVerdict('approve')"
              >
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6zM8.5 12l2.5 2.5 4.5-5"></path>
                </svg>
                <span class="lbl">Approve</span>
              </button>
            }
            @if (!clean()) {
              <button
                type="button"
                class="request-changes"
                data-test="request-changes"
                [disabled]="verdictLocked()"
                [attr.title]="review().commit ? null : noCommit"
                (click)="giveVerdict('request_changes')"
              >
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path d="M12 20h9M16.5 3.5a2.1 2.1 0 1 1 3 3L7 19l-4 1 1-4z"></path>
                </svg>
                <span class="lbl">Request changes</span>
              </button>
            }
          </div>
        }
        @if (giving()) {
          <p class="giving" data-test="verdict-giving" role="status">
            <span class="dot" aria-hidden="true"></span
            >{{ verdict()?.verdict === 'comment' ? 'Posting the review…' : 'Giving the verdict…' }}
          </p>
        } @else if (verdict()?.state === 'failed') {
          <p class="error" role="alert" data-test="verdict-failed">
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM12 8v5M12 16v.5"></path></svg
            >{{
              verdict()?.reason ??
                (verdict()?.verdict === 'comment'
                  ? 'The review was not posted.'
                  : 'The verdict was not given.')
            }}
          </p>
        }
      </div>
      @if (error(); as message) {
        <p class="error" role="alert" data-test="post-error">{{ message }}</p>
      }
    }
    @if (!posted() && !given() && !allDismissed() && !allSent() && !postingReview() && !clean()) {
      <div class="preview-row">
        <button
          type="button"
          class="small"
          data-test="toggle-preview"
          [attr.aria-expanded]="state().preview"
          (click)="store.togglePreview(turnId())"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"></path>
            <circle cx="12" cy="12" r="3"></circle>
          </svg>
          <span class="lbl">{{
            state().preview
              ? 'Hide the preview'
              : audit()
                ? 'See how the issues read'
                : 'See how it reads on the PR'
          }}</span>
        </button>
        @if (audit()) {
          <span class="note"
            >Each chosen finding as its own issue on GitHub, in the card order.</span
          >
        } @else {
          <span class="note"
            >The same finding, as the PR author reads it on GitHub: a Comment review with the
            comment on the line.</span
          >
        }
      </div>
      @if (state().preview && audit()) {
        <div class="gh" data-test="preview">
          @for (item of selected(); track item.finding.id) {
            <div class="gh-issue">
              <div class="gh-title">{{ item.finding.title }}</div>
              <div class="gh-body" [innerHTML]="item.body | markdown"></div>
            </div>
          }
        </div>
      } @else if (state().preview) {
        <div class="gh" data-test="preview">
          @for (item of selected(); track item.finding.id) {
            <div class="gh-file">{{ item.finding.path }}</div>
            <div class="gh-comment">
              <div class="gh-ch">
                <span class="gh-avatar" aria-hidden="true">
                  <svg viewBox="0 0 24 24">
                    <circle cx="12" cy="8" r="4"></circle>
                    <path d="M4 21c0-4 3.6-7 8-7s8 3 8 7"></path>
                  </svg>
                </span>
                <b>you</b>
                <span>commented via Scout · now</span>
              </div>
              <div class="gh-body" [innerHTML]="item.body | markdown"></div>
            </div>
          }
          <div class="gh-review">
            <div class="gh-ch">
              <span class="gh-avatar" aria-hidden="true">
                <svg viewBox="0 0 24 24">
                  <circle cx="12" cy="8" r="4"></circle>
                  <path d="M4 21c0-4 3.6-7 8-7s8 3 8 7"></path>
                </svg>
              </span>
              <b>you</b>
              <span>reviewed via Scout · now</span>
              <span class="gh-pill">Comment</span>
            </div>
            <div class="gh-body" [innerHTML]="previewBody() | markdown"></div>
          </div>
        </div>
      }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: 14px;
      margin-top: 14px;
      container-type: inline-size;
      color: var(--sc-fg);
    }
    .cards {
      display: flex;
      flex-direction: column;
      gap: 14px;
    }
    .card {
      border: 1px solid var(--sc-line);
      border-radius: 12px;
      background: var(--sc-surface);
      padding: 12px;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .card.yes {
      border-color: var(--sc-on-ok-container);
    }
    .dismissed {
      margin: 0;
      display: flex;
      align-items: center;
      gap: 8px;
      color: var(--sc-fg-2);
      font-size: 13px;
    }
    .dismissed-title {
      margin-right: auto;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      text-decoration: line-through;
    }
    .sent {
      margin: 0;
      display: flex;
      align-items: center;
      gap: 8px;
      color: var(--sc-fg-2);
      font-size: 13px;
    }
    .sent-title {
      margin-right: auto;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .sent-mark {
      flex: none;
      display: inline-flex;
      align-items: center;
      gap: 5px;
      padding: 3px 9px;
      border-radius: 12px;
      font-size: 12px;
      background: var(--sc-ok-container);
      color: var(--sc-on-ok-container);
    }
    .sent-mark svg {
      width: 12px;
      height: 12px;
      fill: none;
      stroke: currentColor;
      stroke-width: 2.6;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    .all-dismissed {
      margin: 0;
      color: var(--sc-muted);
      font-size: 13px;
    }
    .head {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
    }
    h3 {
      margin: 0;
      font-size: 14px;
      font-weight: 600;
    }
    .pill {
      font-size: 11px;
      font-weight: 600;
      border-radius: 10px;
      padding: 2px 8px;
    }
    .pill.block {
      background: var(--sc-error-container);
      color: var(--sc-on-error-container);
    }
    .pill.fix {
      background: var(--sc-warn-container);
      color: var(--sc-on-warn-container);
    }
    .pill.nit {
      background: var(--sc-surface-2);
      color: var(--sc-fg-2);
    }
    .path {
      align-self: flex-start;
      font-family: var(--sc-mono);
      font-size: 12px;
      color: var(--sc-accent);
      text-decoration: none;
      overflow-wrap: anywhere;
    }
    .path:hover {
      text-decoration: underline;
    }
    .body {
      font-size: 13px;
      color: var(--sc-fg-2);
      display: flex;
      flex-direction: column;
      gap: 6px;
      line-height: 1.5;
    }
    .body :is(p, ul, ol) {
      margin: 0;
    }
    .body code {
      font-family: var(--sc-mono);
      font-size: 12.5px;
    }
    .rule {
      margin: 0;
      font-size: 13px;
      color: var(--sc-muted);
    }
    .known {
      border: 1px dashed var(--sc-line);
      border-radius: 12px;
      padding: 12px 16px;
      font-size: 13px;
      color: var(--sc-fg-2);
    }
    .known h4 {
      margin: 0 0 6px;
      font-size: 13px;
      font-weight: 600;
    }
    .known ul {
      margin: 0;
      padding-left: 20px;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    button {
      font: inherit;
      font-size: 13px;
      font-weight: 400;
      border-radius: 8px;
      padding: 0 10px;
      border: 1px solid var(--sc-line);
      background: transparent;
      color: var(--sc-fg);
      min-height: 32px;
      box-sizing: border-box;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: 6px;
      cursor: pointer;
    }
    button:hover:not(:disabled) {
      filter: brightness(1.12);
    }
    button:focus-visible,
    a:focus-visible,
    textarea:focus-visible {
      outline: 2px solid var(--sc-accent);
      outline-offset: 2px;
    }
    button svg {
      width: 14px;
      height: 14px;
      flex: none;
      fill: none;
      stroke: currentColor;
      stroke-width: 2;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    .yes-btn.on {
      background: var(--sc-ok-container);
      border-color: var(--sc-on-ok-container);
      color: var(--sc-on-ok-container);
    }
    .no-btn.on {
      background: var(--sc-error-container);
      border-color: var(--sc-error);
      color: var(--sc-on-error-container);
    }
    .verdict {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      padding-top: 8px;
      border-top: 1px solid var(--sc-line);
    }
    .verdict-label {
      margin-right: auto;
      font-size: 12px;
      color: var(--sc-muted);
    }
    .verdict button {
      min-height: 30px;
      font-size: 12px;
    }
    .tests {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .chips {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .chip {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      padding: 3px 9px;
      border-radius: 12px;
      font-size: 12px;
      text-decoration: none;
    }
    .chip svg {
      width: 12px;
      height: 12px;
      fill: none;
      stroke: currentColor;
      stroke-width: 2.6;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    a.chip:hover {
      text-decoration: underline;
    }
    .chip.passed {
      background: var(--sc-ok-container);
      color: var(--sc-on-ok-container);
    }
    .chip.failed {
      background: var(--sc-error-container);
      color: var(--sc-on-error-container);
    }
    .chip.pending {
      background: var(--sc-warn-container);
      color: var(--sc-on-warn-container);
    }
    .chip.notrun {
      background: var(--sc-surface-2);
      color: var(--sc-fg-2);
    }
    .output pre {
      margin: 6px 0 0;
      max-height: 240px;
      overflow: auto;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      font-size: 12px;
    }
    .primary {
      background: var(--sc-accent-strong);
      border-color: var(--sc-accent-strong);
      color: var(--sc-on-accent);
      padding: 0 12px;
      font-weight: 500;
    }
    button:disabled {
      opacity: 0.45;
      cursor: default;
    }
    button.small {
      min-height: 30px;
      padding: 0 10px;
    }
    .editor {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .editor textarea {
      width: 100%;
      box-sizing: border-box;
      font: inherit;
      font-size: 13.5px;
      min-height: 120px;
      border-radius: 8px;
      border: 1px solid var(--sc-line);
      background: var(--sc-surface-2);
      color: var(--sc-fg);
      padding: 10px 12px;
      resize: vertical;
    }
    .editor-row {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
    }
    .counter,
    .note {
      font-size: 13px;
      color: var(--sc-muted);
    }
    .counter {
      margin-right: auto;
    }
    .posting {
      background: var(--sc-info-container);
      color: var(--sc-fg);
      padding: 10px 12px;
      border-radius: 12px;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .posting-line {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 13px;
    }
    .posting-line svg {
      flex: none;
      width: 14px;
      height: 14px;
      fill: none;
      stroke: var(--sc-on-info-container);
      stroke-width: 2.4;
      stroke-linecap: round;
    }
    .bar {
      height: 4px;
      border-radius: 2px;
      background: var(--sc-surface-2);
      overflow: hidden;
    }
    .bar div {
      height: 4px;
      width: 55%;
      background: var(--sc-accent);
    }
    .footer {
      background: var(--sc-surface-2);
      padding: 10px 12px;
      border-radius: 12px;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .target {
      margin: 0;
      font-size: 13px;
      color: var(--sc-fg);
      overflow-wrap: anywhere;
    }
    .summary {
      font-size: 13px;
      color: var(--sc-fg-2);
    }
    .clean {
      margin: 0;
      font-size: 13px;
      color: var(--sc-fg-2);
    }
    .group {
      display: flex;
      gap: 6px;
      flex-wrap: wrap;
      justify-content: flex-end;
    }
    .giving {
      margin: 0;
      font-size: 13px;
      color: var(--sc-accent);
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .giving .dot {
      width: 8px;
      height: 8px;
      border-radius: 4px;
      background: var(--sc-accent);
      animation: giving-pulse 1.2s ease-in-out infinite;
    }
    @keyframes giving-pulse {
      50% {
        opacity: 0.35;
        transform: scale(0.8);
      }
    }
    @media (prefers-reduced-motion: reduce) {
      .giving .dot {
        animation: none;
      }
    }
    .field-label {
      font-size: 12px;
      color: var(--sc-muted);
    }
    .error {
      margin: 0;
      font-size: 13px;
      color: var(--sc-error);
      display: flex;
      align-items: flex-start;
      gap: 6px;
    }
    .error svg {
      flex: 0 0 15px;
      width: 15px;
      height: 15px;
      margin-top: 2px;
      fill: none;
      stroke: currentColor;
      stroke-width: 2.2;
      stroke-linecap: round;
    }
    .preview-row {
      display: flex;
      align-items: center;
      gap: 10px;
      flex-wrap: wrap;
    }
    .gh {
      color-scheme: light;
      background: #fff;
      color: #1f2328;
      border: 1px solid #d1d9e0;
      border-radius: 8px;
      overflow: hidden;
      font-family: -apple-system, 'Segoe UI', 'Noto Sans', Helvetica, Arial, sans-serif;
      font-size: 14px;
    }
    .gh-issue {
      padding: 12px 14px;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .gh-issue + .gh-issue {
      border-top: 1px solid #d1d9e0;
    }
    .gh-title {
      font-size: 16px;
      font-weight: 600;
      overflow-wrap: anywhere;
    }
    .gh-body h2 {
      margin: 0;
      font-size: 15px;
    }
    .gh-review {
      padding: 12px 14px;
      border-top: 1px solid #d1d9e0;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .gh-file {
      background: #f6f8fa;
      border-bottom: 1px solid #d1d9e0;
      padding: 8px 12px;
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 12px;
      overflow-wrap: anywhere;
    }
    .gh-comment {
      margin: 8px 12px 12px 48px;
      border: 1px solid #d1d9e0;
      border-radius: 6px;
      overflow: hidden;
    }
    .gh-ch {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
      font-size: 13px;
      color: #59636e;
    }
    .gh-comment .gh-ch {
      background: #f6f8fa;
      padding: 8px 12px;
      border-bottom: 1px solid #d1d9e0;
    }
    .gh-ch b {
      color: #1f2328;
    }
    .gh-avatar {
      width: 20px;
      height: 20px;
      border-radius: 50%;
      background: var(--sc-accent-strong);
      color: var(--sc-on-accent);
      display: inline-flex;
      align-items: center;
      justify-content: center;
      flex: none;
    }
    .gh-avatar svg {
      width: 14px;
      height: 14px;
      fill: none;
      stroke: currentColor;
      stroke-width: 2;
    }
    .gh-pill {
      background: #ddf4ff;
      color: #0969da;
      border-radius: 999px;
      padding: 1px 8px;
      font-size: 12px;
      font-weight: 600;
    }
    .gh-body {
      display: flex;
      flex-direction: column;
      gap: 8px;
      line-height: 1.5;
      overflow-wrap: anywhere;
    }
    .gh-comment .gh-body {
      padding: 12px 14px;
    }
    .gh-body :is(p, ul, ol) {
      margin: 0;
    }
    .gh-body ul {
      padding-left: 22px;
    }
    .gh-body code {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 12px;
      background: #eff1f3;
      border-radius: 4px;
      padding: 1px 5px;
    }
    .gh-body a {
      color: #0969da;
    }
    .posted {
      background: var(--sc-ok-container);
      border-radius: 12px;
      padding: 10px 12px;
      display: flex;
      flex-direction: column;
      gap: 4px;
      color: var(--sc-on-ok-container);
      overflow-wrap: anywhere;
    }
    .posted-line {
      color: var(--sc-fg-2);
      font-size: 13px;
    }
    .posted a {
      color: inherit;
    }
    @container (max-width: 480px) {
      .lbl {
        position: absolute;
        width: 1px;
        height: 1px;
        padding: 0;
        margin: -1px;
        overflow: hidden;
        clip: rect(0, 0, 0, 0);
        white-space: nowrap;
        border: 0;
      }
      .actions button,
      .group button {
        min-width: 44px;
      }
    }
  `,
})
export class ReviewFindingsComponent {
  protected readonly store = inject(ReviewFindingsStore);
  private readonly conversation = inject(LocalConversation);
  private readonly bridge = inject(ScoutBridgeService);

  readonly turnId = input.required<string>();
  readonly review = input.required<ReviewFindings>();

  constructor() {
    effect(() => {
      const { kind, repository, number, commit } = this.review();
      this.store.register(this.turnId(), repository, number);
      if (kind !== 'audit' && number !== undefined && commit) {
        this.store.lookUpVerdict(this.turnId(), { repository, number, commit });
      }
    });
  }

  readonly draft = signal<EditedText>({ why: '', fix: '' });
  readonly error = signal<string | null>(null);
  protected readonly maxChars = MAX_COMMENT_CHARS;

  readonly state = computed(() => this.store.state(this.turnId()));
  readonly posted = computed(() => this.state().posted ?? null);
  readonly posting = computed(() => this.state().postTurnId !== undefined);
  readonly busy = computed(() => this.conversation.running() !== null);
  readonly verdict = computed(() => {
    const { kind, repository, number, commit } = this.review();
    return kind === 'audit' || number === undefined || !commit
      ? null
      : this.store.verdict(repository, number, commit);
  });
  readonly given = computed(() => {
    const verdict = this.verdict();
    return verdict?.state === 'given' ? verdict : null;
  });
  /** The card's Post, once GitHub took the review: the verdict `comment`, given. */
  readonly reviewPosted = computed(() => {
    const given = this.given();
    return given?.verdict === 'comment' ? given : null;
  });
  readonly giving = computed(() => {
    const state = this.verdict()?.state;
    return state === 'sending' || state === 'queued' || state === 'running';
  });
  /** True while the card's own Post, the verdict `comment`, is on its way to GitHub. */
  readonly postingReview = computed(() => this.giving() && this.verdict()?.verdict === 'comment');
  readonly closed = computed(() => this.posted() !== null || this.given() !== null);
  readonly writing = computed(() => this.posting() || this.busy() || this.giving());
  readonly verdictLocked = computed(() => this.giving() || this.posting() || !this.review().commit);
  readonly audit = computed(() => this.review().kind === 'audit');
  /** True on a review that found nothing: the card shows what was checked and offers Approve alone. */
  readonly clean = computed(() => !this.audit() && this.review().findings.length === 0);
  /** The findings of an audit whose issue a finished issues turn on its repository already asked for, by title. */
  readonly sentIds = computed((): ReadonlySet<string> => {
    const { repository, findings } = this.review();
    const titles =
      this.audit() && this.posted() === null
        ? this.store.sent().get(repository.toLowerCase())
        : undefined;
    if (titles === undefined) return new Set<string>();
    return new Set(findings.filter((f) => titles.has(f.title.trim())).map((f) => f.id));
  });
  /** The findings still to decide or send: every finding but the ones already sent. */
  readonly unsent = computed(() => {
    const sent = this.sentIds();
    return this.review().findings.filter((f) => !sent.has(f.id));
  });
  readonly findingIds = computed(() => this.unsent().map((f) => f.id));
  readonly selected = computed(() => {
    const { picks, edited } = this.state();
    return this.unsent()
      .filter((f) => picks[f.id] === 'yes')
      .map((finding) => ({ finding, body: this.bodyOf({ ...finding, ...edited[finding.id] }) }));
  });
  /** True on an audit once every finding not dismissed was sent: nothing is left to open. */
  readonly allSent = computed(() => {
    const picks = this.state().picks;
    return this.sentIds().size > 0 && this.unsent().every((f) => picks[f.id] === 'no');
  });
  readonly allDismissed = computed(() => {
    const picks = this.state().picks;
    const { findings } = this.review();
    return !this.allSent() && findings.length > 0 && findings.every((f) => picks[f.id] === 'no');
  });
  readonly summary = computed(() => {
    const picks = this.state().picks;
    const findings = this.unsent();
    const decided = findings.filter((f) => picks[f.id] !== undefined).length;
    if (decided === 0) return 'Nothing selected yet. Decide item by item, or Yes to all.';
    const kept = findings.filter((f) => picks[f.id] !== 'no').length;
    const undecided = findings.length - decided;
    const rest = undecided > 0 ? ` · ${undecided} undecided` : '';
    return `${this.selected().length} of ${kept} selected${rest}`;
  });
  /** What a click on Post, Approve or Request changes writes to: the repository, the pull request and the commit reviewed; null on an audit. */
  readonly target = computed(() => {
    const { repository, number, commit } = this.review();
    if (this.audit()) return null;
    const pullRequest = `${repository} · PR #${number}`;
    return commit ? `${pullRequest} · commit ${commit.slice(0, 7)}` : pullRequest;
  });
  readonly postLabel = computed(() => {
    const n = this.selected().length;
    if (this.audit()) {
      const on = `on ${this.review().repository}`;
      return n === 0 ? `Open issues ${on}` : `Open ${n} issue${n === 1 ? '' : 's'} ${on}`;
    }
    return n === 0
      ? `Post on PR #${this.review().number}`
      : `Post ${n} on PR #${this.review().number}`;
  });
  readonly previewBody = computed(() =>
    reviewBody(
      this.review(),
      this.selected().map((s) => s.finding),
    ),
  );

  protected readonly noCommit = 'The review names no commit to pin the verdict to.';
  protected readonly checkMark = checkMark;

  protected runTone(tests: ReviewTests): 'passed' | 'failed' | 'notrun' {
    if (!tests.ran) return 'notrun';
    return tests.exitCode === 0 ? 'passed' : 'failed';
  }

  protected runChip(tests: ReviewTests): string {
    if (!tests.ran) return `tests did not run: ${tests.reason}`;
    return tests.exitCode === 0
      ? `tests passed in ${Math.round(tests.durationSeconds ?? 0)} s (${tests.command})`
      : `tests failed (exit ${tests.exitCode})`;
  }

  protected chipIcon(tone: string): string {
    if (tone === 'passed') return 'M20 6L9 17l-5-5';
    if (tone === 'failed') return 'M6 6l12 12M18 6L6 18';
    if (tone === 'pending') return 'M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM12 7v5l3 2';
    return 'M5 12h14';
  }

  severityLabel(finding: ReviewFinding): string {
    return SEVERITY_LABELS[finding.severity];
  }

  pathUrl(finding: ReviewFinding): string {
    const { repository, number, commit } = this.review();
    return this.audit()
      ? `https://github.com/${repository}/blob/${commit ?? 'HEAD'}/${finding.path}#L${finding.line}`
      : `https://github.com/${repository}/pull/${number}/files`;
  }

  details(finding: ReviewFinding): string {
    const context = finding.context ? `${finding.context}\n\n` : '';
    return `${context}${finding.why}\n\n**Fix:** ${finding.fix}`;
  }

  toggleEditor(finding: ReviewFinding): void {
    if (this.state().editing !== finding.id) {
      const { why, fix } = { ...finding, ...this.state().edited[finding.id] };
      this.draft.set({ why, fix });
    }
    this.store.edit(this.turnId(), finding.id);
  }

  /**
   * A review's Post is the owner's approval of that one review: it goes to Scout Desktop as an approved action with
   * the chosen comments, never through the model, and nothing is sent again while it is being posted or once posted.
   * An audit's Open asks the worker for the issues, each of which then waits for its own approval.
   */
  async post(): Promise<void> {
    const selected = this.selected();
    if (selected.length === 0 || this.writing() || this.closed()) return;
    if (!this.audit()) {
      const request = verdictRequest(this.review(), selected, 'comment');
      if (request !== null) await this.store.giveVerdict(request);
      return;
    }
    const turnId = this.turnId();
    const id = crypto.randomUUID();
    this.error.set(null);
    this.store.markPosting(turnId, id);
    const ask = issuesAsk(this.review(), selected);
    const refusal = await this.conversation.askAs(id, ask);
    if (refusal !== null) {
      this.store.markPosting(turnId, undefined);
      this.error.set(refusal);
    }
  }

  /** Gives the owner's verdict, pinned to the reviewed commit, with the chosen findings as its comments; only this click gives one. */
  giveVerdict(verdict: GitHubVerdictRequest['verdict']): void {
    if (this.closed() || this.verdictLocked()) return;
    if (verdict === 'approve' && this.review().selfAuthored === true) return;
    const request = verdictRequest(this.review(), this.selected(), verdict);
    if (request !== null) void this.store.giveVerdict(request);
  }

  private bodyOf(finding: ReviewFinding): string {
    return this.audit() ? issueBody(this.review(), finding) : commentBody(finding);
  }

  open(url: string): void {
    this.bridge
      .appOpenExternal({ url })
      .catch(() => this.error.set('Scout could not open the link.'));
  }
}
