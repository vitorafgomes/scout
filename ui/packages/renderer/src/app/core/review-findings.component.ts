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

import type { ReviewFinding, ReviewFindings } from '@scout/shared';

import { LocalConversation } from './local-conversation';
import { MarkdownPipe } from './markdown.pipe';
import {
  commentBody,
  issueBody,
  issuesAsk,
  parseReviewFindings,
  postingAsk,
  reviewBody,
} from './review-findings';
import { ReviewFindingsStore } from './review-findings.store';
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
 * edited text, with a preview of how they read on GitHub; for a review, Post asks Scout to post the
 * chosen comments as one review, for an audit, to open one issue per chosen finding, and once the
 * reply carries the links the cards say so.
 */
@Component({
  selector: 'sc-review-findings',
  standalone: true,
  imports: [MarkdownPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="cards">
      @for (finding of review().findings; track finding.id) {
        <article
          class="card"
          data-test="finding"
          [class.yes]="state().picks[finding.id] === 'yes'"
          [class.no]="state().picks[finding.id] === 'no'"
        >
          @if (state().picks[finding.id] === 'no') {
            <p class="dismissed" data-test="dismissed">
              <span class="dismissed-title">Dismissed: {{ finding.title }}</span>
              @if (!posted()) {
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
            @if (!posted()) {
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
                  <textarea
                    data-test="comment-text"
                    [attr.aria-label]="audit() ? 'Issue text' : 'Comment text'"
                    [attr.maxlength]="maxChars"
                    [value]="draft()"
                    (input)="draft.set($any($event.target).value)"
                  ></textarea>
                  <div class="editor-row">
                    <span class="counter">{{ draft().length }} / {{ maxChars }}</span>
                    <button
                      type="button"
                      class="small"
                      data-test="use-text"
                      [disabled]="draft().trim().length === 0"
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
        @if (audit()) {
          <strong
            >{{ done.urls.length }} {{ done.urls.length === 1 ? 'issue' : 'issues' }} opened on
            {{ review().repository }}</strong
          >
        } @else {
          <strong>Review posted on PR #{{ review().number }}</strong>
          @let comments = done.comments ?? selected().length;
          <span class="posted-line"
            >{{ comments }} {{ comments === 1 ? 'comment' : 'comments' }} in one review, without
            approval or request for changes. The items with No stayed here in the chat.</span
          >
        }
        @for (url of done.urls; track url) {
          <a href="#" (click)="$event.preventDefault(); open(url)">{{ url }}</a>
        }
      </div>
    } @else if (allDismissed()) {
      <p class="all-dismissed" data-test="all-dismissed">
        {{
          audit()
            ? 'Nothing to open; every finding was dismissed.'
            : 'Nothing to post; every finding was dismissed.'
        }}
      </p>
    } @else {
      <div class="footer">
        <span class="summary" data-test="summary">{{ summary() }}</span>
        <div class="group">
          <button type="button" data-test="yes-all" (click)="store.yesAll(turnId(), findingIds())">
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="M18 6 7 17l-5-5"></path>
              <path d="m22 10-7.5 7.5L13 16"></path>
            </svg>
            <span class="lbl">Yes to all</span>
          </button>
          <button
            type="button"
            class="primary"
            data-test="post"
            [disabled]="selected().length === 0 || posting() || busy()"
            [attr.aria-label]="postLabel()"
            (click)="post()"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="M22 2 11 13"></path>
              <path d="M22 2 15 22l-4-9-9-4Z"></path>
            </svg>
            <span class="lbl">{{ postLabel() }}</span>
          </button>
        </div>
      </div>
      @if (error(); as message) {
        <p class="error" role="alert" data-test="post-error">{{ message }}</p>
      }
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
      --rf-surface: #131c2d;
      --rf-surface-2: #1a2436;
      --rf-line: #253249;
      --rf-fg: #e6e9f0;
      --rf-fg-2: #c3cadb;
      --rf-muted: #8a93a6;
      --rf-accent: #7aa2f7;
      --rf-accent-strong: #3b6ff2;
      --rf-block-bg: #4a1f1f;
      --rf-block-fg: #ffb4b4;
      --rf-fix-bg: #4a3a12;
      --rf-fix-fg: #ffd97a;
      --rf-nit-bg: #163d2c;
      --rf-nit-fg: #9fe8c0;
      --rf-yes-bg: #1f4d3a;
      --rf-yes-line: #2f7a57;
      --rf-yes-fg: #c9f2dc;
      --rf-no-bg: #4a2a2a;
      --rf-no-line: #8a4a4a;
      --rf-no-fg: #f3d1d1;
      display: flex;
      flex-direction: column;
      gap: 14px;
      margin-top: 14px;
      container-type: inline-size;
      color: var(--rf-fg);
    }
    :host-context([data-theme='light']) {
      --rf-surface: #fff;
      --rf-surface-2: #eef1f7;
      --rf-line: #d9deea;
      --rf-fg: #141a26;
      --rf-fg-2: #333b4d;
      --rf-muted: #5f6878;
      --rf-accent: #2456c8;
      --rf-accent-strong: #2456c8;
      --rf-block-bg: #fde3e3;
      --rf-block-fg: #8a1f1f;
      --rf-fix-bg: #fdf0c8;
      --rf-fix-fg: #6b4e00;
      --rf-nit-bg: #dff5e9;
      --rf-nit-fg: #155b38;
      --rf-yes-bg: #dff5e9;
      --rf-yes-line: #2f7a57;
      --rf-yes-fg: #155b38;
      --rf-no-bg: #fde3e3;
      --rf-no-line: #c66;
      --rf-no-fg: #8a1f1f;
    }
    .cards {
      display: flex;
      flex-direction: column;
      gap: 14px;
    }
    .card {
      border: 1px solid var(--rf-line);
      border-radius: 12px;
      background: var(--rf-surface);
      padding: 16px 18px;
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .card.yes {
      border-color: var(--rf-yes-line);
    }
    .card.no {
      padding: 6px 12px 6px 18px;
    }
    .dismissed {
      margin: 0;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      color: var(--rf-muted);
      font-size: 13px;
    }
    .dismissed-title {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      opacity: 0.75;
    }
    .all-dismissed {
      margin: 0;
      color: var(--rf-muted);
      font-size: 13px;
    }
    .head {
      display: flex;
      align-items: center;
      gap: 10px;
      flex-wrap: wrap;
    }
    h3 {
      margin: 0;
      font-size: 15px;
      font-weight: 600;
    }
    .pill {
      font-size: 11px;
      font-weight: 600;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      border-radius: 999px;
      padding: 3px 9px;
    }
    .pill.block {
      background: var(--rf-block-bg);
      color: var(--rf-block-fg);
    }
    .pill.fix {
      background: var(--rf-fix-bg);
      color: var(--rf-fix-fg);
    }
    .pill.nit {
      background: var(--rf-nit-bg);
      color: var(--rf-nit-fg);
    }
    .path {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 12.5px;
      color: var(--rf-accent);
      text-decoration: none;
      overflow-wrap: anywhere;
    }
    .path:hover {
      text-decoration: underline;
    }
    .body {
      font-size: 13.5px;
      color: var(--rf-fg-2);
      display: flex;
      flex-direction: column;
      gap: 6px;
      line-height: 1.5;
    }
    .body :is(p, ul, ol) {
      margin: 0;
    }
    .body code {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 12.5px;
    }
    .rule {
      margin: 0;
      font-size: 13px;
      color: var(--rf-muted);
    }
    .known {
      border: 1px dashed var(--rf-line);
      border-radius: 12px;
      padding: 12px 16px;
      font-size: 13px;
      color: var(--rf-fg-2);
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
      gap: 8px;
      padding-top: 4px;
    }
    button {
      font: inherit;
      font-size: 13px;
      font-weight: 500;
      border-radius: 8px;
      padding: 8px 12px;
      border: 1px solid var(--rf-line);
      background: var(--rf-surface-2);
      color: var(--rf-fg);
      min-height: 44px;
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
      outline: 2px solid var(--rf-accent);
      outline-offset: 2px;
    }
    button svg {
      width: 16px;
      height: 16px;
      flex: none;
      fill: none;
      stroke: currentColor;
      stroke-width: 2;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    .yes-btn.on {
      background: var(--rf-yes-bg);
      border-color: var(--rf-yes-line);
      color: var(--rf-yes-fg);
    }
    .no-btn.on {
      background: var(--rf-no-bg);
      border-color: var(--rf-no-line);
      color: var(--rf-no-fg);
    }
    .primary {
      background: var(--rf-accent-strong);
      border-color: var(--rf-accent-strong);
      color: #fff;
    }
    button:disabled {
      opacity: 0.45;
      cursor: default;
    }
    button.small {
      min-height: 36px;
      padding: 6px 12px;
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
      border: 1px solid var(--rf-line);
      background: var(--rf-surface-2);
      color: var(--rf-fg);
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
      color: var(--rf-muted);
    }
    .counter {
      margin-right: auto;
    }
    .footer {
      border-top: 1px solid var(--rf-line);
      padding-top: 14px;
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 10px;
      flex-wrap: wrap;
    }
    .summary {
      font-size: 13px;
      color: var(--rf-fg-2);
    }
    .group {
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
    }
    .error {
      margin: 0;
      font-size: 13px;
      color: #d45a5a;
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
      background: var(--rf-accent-strong);
      color: #fff;
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
      border: 1px solid var(--rf-yes-line);
      background: var(--rf-yes-bg);
      border-radius: 12px;
      padding: 14px 18px;
      display: flex;
      flex-direction: column;
      gap: 6px;
      color: var(--rf-yes-fg);
      overflow-wrap: anywhere;
    }
    .posted-line {
      color: var(--rf-fg-2);
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
      const { repository, number } = this.review();
      this.store.register(this.turnId(), repository, number);
    });
  }

  readonly draft = signal('');
  readonly error = signal<string | null>(null);
  protected readonly maxChars = MAX_COMMENT_CHARS;

  readonly state = computed(() => this.store.state(this.turnId()));
  readonly posted = computed(() => this.state().posted ?? null);
  readonly posting = computed(() => this.state().postTurnId !== undefined);
  readonly busy = computed(() => this.conversation.running() !== null);
  readonly audit = computed(() => this.review().kind === 'audit');
  readonly findingIds = computed(() => this.review().findings.map((f) => f.id));
  readonly selected = computed(() => {
    const { picks, edited } = this.state();
    return this.review()
      .findings.filter((f) => picks[f.id] === 'yes')
      .map((finding) => ({ finding, body: this.bodyOf(finding, edited[finding.id]) }));
  });
  readonly allDismissed = computed(() => {
    const picks = this.state().picks;
    return this.review().findings.every((f) => picks[f.id] === 'no');
  });
  readonly summary = computed(() => {
    const picks = this.state().picks;
    const findings = this.review().findings;
    const decided = findings.filter((f) => picks[f.id] !== undefined).length;
    if (decided === 0) return 'Nothing selected yet. Decide item by item, or Yes to all.';
    const kept = findings.filter((f) => picks[f.id] !== 'no').length;
    const undecided = findings.length - decided;
    const rest = undecided > 0 ? ` · ${undecided} undecided` : '';
    return `${this.selected().length} of ${kept} selected${rest}`;
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
      this.draft.set(this.bodyOf(finding, this.state().edited[finding.id]));
    }
    this.store.edit(this.turnId(), finding.id);
  }

  async post(): Promise<void> {
    const selected = this.selected();
    if (selected.length === 0 || this.posting() || this.busy()) return;
    const turnId = this.turnId();
    const id = crypto.randomUUID();
    this.error.set(null);
    this.store.markPosting(turnId, id);
    const ask = this.audit()
      ? issuesAsk(this.review(), selected)
      : postingAsk(this.review(), selected);
    const refusal = await this.conversation.askAs(id, ask);
    if (refusal !== null) {
      this.store.markPosting(turnId, undefined);
      this.error.set(refusal);
    }
  }

  private bodyOf(finding: ReviewFinding, edited: string | undefined): string {
    return this.audit()
      ? issueBody(this.review(), finding, edited)
      : (edited ?? commentBody(finding));
  }

  open(url: string): void {
    this.bridge
      .appOpenExternal({ url })
      .catch(() => this.error.set('Scout could not open the link.'));
  }
}
