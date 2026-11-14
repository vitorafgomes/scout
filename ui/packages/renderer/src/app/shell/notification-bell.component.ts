import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatBadge } from '@angular/material/badge';
import { Router } from '@angular/router';
import {
  jobControls,
  readIpcError,
  type AppNotification,
  type JobControlAction,
  type NotificationActionId,
  type NotificationKind,
} from '@scout/shared';

import { LocalConversation } from '../core/local-conversation';
import { ScoutBridgeService } from '../core/scout-bridge';

/** The panel's filters, as the prototype names them. */
export type NotificationFilter = 'all' | 'running' | 'github' | 'done';

const FILTERS: readonly { readonly id: NotificationFilter; readonly label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'running', label: 'In progress' },
  { id: 'github', label: 'GitHub' },
  { id: 'done', label: 'Done' },
];

const SHOWN_BY: Readonly<Record<NotificationFilter, readonly NotificationKind[]>> = {
  all: ['running', 'paused', 'github', 'mention', 'done', 'failed'],
  running: ['running', 'paused'],
  github: ['github', 'mention'],
  done: ['done', 'failed'],
};

/** The colour pair of an entry's icon circle, as the artboard names it. */
export type EntryTone = 'accent' | 'ok' | 'warn' | 'err' | 'muted';

/** An entry's icon: the artboard's glyph, or the app's own drawing where the artboard draws none. */
export interface EntryIcon {
  readonly tone: EntryTone;
  readonly glyph: string | null;
  readonly path: string | null;
}

const STOP_PATH = 'M7 7h10v10H7z';

const KIND_TONES: Readonly<Record<NotificationKind, EntryTone>> = {
  running: 'accent',
  paused: 'warn',
  done: 'ok',
  failed: 'err',
  github: 'warn',
  mention: 'warn',
};

const OWN_ICONS: Readonly<Partial<Record<NotificationKind, string>>> = {
  running: 'M12 3a9 9 0 1 0 9 9M12 7v5l3 2',
  paused: 'M9 5v14M15 5v14',
  github: 'M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z',
};

const PAUSED_BY_YOU = 'Paused by you';
const JOB_NAME = /^(?:Audit of|Tests for) (\S+)/;

const ACTION_ICONS: Readonly<Record<NotificationActionId, string>> = {
  'whisper-download-base': 'M12 3v12M7 10l5 5 5-5M5 21h14',
  'open-models-transcription': 'M3 10v4M7 7v10M11 5v14M15 8v8M19 10v4',
  'open-approvals': 'M9 11l3 3 8-8M20 12v7a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11',
  'review-pull-request': 'M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16zM21 21l-4.35-4.35',
};

const CONTROL_ICONS: Readonly<Record<JobControlAction, string>> = {
  pause: 'M9 5v14M15 5v14',
  continue: 'M8 5l11 7-11 7z',
  stop: STOP_PATH,
};

const CONTROL_VERBS: Readonly<Record<JobControlAction, string>> = {
  pause: 'Pause',
  continue: 'Continue',
  stop: 'Stop',
};

const AUDIT_WAITING: Readonly<Record<JobControlAction, string>> = {
  pause: 'Pausing after this area',
  continue: 'Continuing',
  stop: 'Stopping after this area',
};

const IN_WINDOW: Readonly<Partial<Record<NotificationActionId, string>>> = {
  'open-models-transcription': '/llm?tab=transcription',
  'open-approvals': '/review',
};

const PULL_REQUEST_URL =
  /^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/pull\/\d+(?=$|#)/;
const NO_PULL_REQUEST = 'Scout could not tell which pull request this entry is about.';

const CLOCK_MS = 30_000;

/** How long ago `at` was, as the panel shows it. */
export function timeAgo(at: number, now: number): string {
  const minutes = Math.floor((now - at) / 60_000);
  if (minutes < 1) return 'now';
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} h`;
  return `${Math.floor(hours / 24)} d`;
}

/**
 * The bell in the top bar: the unread count on the button and a panel with what Scout does in the background and
 * the GitHub threads that concern the owner, filtered, marked read, and opened in the browser; the times move on while
 * the panel is open. An entry's actions are buttons: Open Models, Transcription and Open Approvals open that screen
 * here, Review it opens the chat and asks Scout to review the pull request the entry links to, any other action is
 * asked of main, which runs it and shows its progress and outcome in the entry. Opening a link or running an action
 * marks that entry read. A running audit offers Pause and Stop at the right of its title, a paused one Continue and
 * Stop, a running test run Stop; main sends the click and the entry shows what Scout does with it, the buttons
 * inactive while main asks and replaced by what Scout does once the click is queued, focus then on that line.
 */
@Component({
  selector: 'sc-notification-bell',
  standalone: true,
  imports: [MatBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(document:keydown.escape)': 'onEscape()',
    '(document:click)': 'onDocumentClick($event)',
  },
  template: `
    <button
      #bell
      type="button"
      class="bell"
      data-test="bell"
      [attr.aria-label]="label()"
      [attr.aria-expanded]="open()"
      aria-controls="sc-notification-panel"
      (click)="toggle()"
    >
      <svg
        width="19"
        height="19"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        aria-hidden="true"
      >
        <path d="M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9"></path>
        <path d="M10.3 21a1.9 1.9 0 0 0 3.4 0"></path>
      </svg>
      @if (unread() > 0) {
        <span class="badge" data-test="badge" aria-hidden="true" [matBadge]="unread()"></span>
      }
    </button>

    @if (open()) {
      <section
        id="sc-notification-panel"
        class="panel"
        aria-label="Notifications"
        data-test="panel"
      >
        <div class="head">
          <span class="heading">Notifications</span>
          <button type="button" class="mark" data-test="mark-all" (click)="markAllRead()">
            <svg
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
              <path d="M18 6L7 17l-5-5M22 10l-7.5 7.5L13 16"></path>
            </svg>
            Mark all as read
          </button>
        </div>
        <div role="tablist" aria-label="Filter notifications" class="tabs">
          @for (f of filters; track f.id) {
            <button
              type="button"
              role="tab"
              class="tab"
              [attr.data-test]="'filter-' + f.id"
              [attr.aria-selected]="filter() === f.id"
              (click)="filter.set(f.id)"
            >
              {{ f.label }}
            </button>
          }
        </div>
        <div class="entries">
          @for (n of shown(); track n.id) {
            <div
              class="entry"
              data-test="entry"
              [attr.data-kind]="n.kind"
              [attr.data-entry]="n.id"
              [class.unread]="!n.read"
            >
              @let icon = iconOf(n);
              <div class="icon" [attr.data-tone]="icon.tone" aria-hidden="true">
                @if (icon.path !== null) {
                  <svg
                    width="16"
                    height="16"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <path [attr.d]="icon.path"></path>
                  </svg>
                } @else {
                  {{ icon.glyph }}
                }
              </div>
              <div class="body">
                <div class="line">
                  <span class="title" data-test="entry-title">{{ n.title }}</span>
                  @if (controlsOf(n).length > 0) {
                    <span class="controls">
                      @for (c of controlsOf(n); track c) {
                        <button
                          type="button"
                          class="control"
                          [attr.data-test]="'entry-control-' + c"
                          [attr.aria-label]="controlLabel(n, c)"
                          [attr.title]="controlLabel(n, c)"
                          [attr.aria-disabled]="n.control === undefined ? null : 'true'"
                          (click)="control(n, c)"
                        >
                          <svg
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
                            <path [attr.d]="controlIcons[c]"></path>
                          </svg>
                        </button>
                      }
                    </span>
                  }
                  @if (n.control?.phase === 'waiting') {
                    <span class="status" role="status" tabindex="-1" data-test="entry-status">{{
                      waitingText(n)
                    }}</span>
                  } @else {
                    <span class="when" data-test="entry-when">{{ ago(n.at) }}</span>
                  }
                </div>
                @if (n.detail.length > 0) {
                  <span class="detail" data-test="entry-detail">{{ n.detail }}</span>
                }
                @if (n.note !== undefined) {
                  <span class="note" role="status" data-test="entry-note">{{ n.note }}</span>
                }
                @if (n.progress !== undefined) {
                  <div
                    class="progress"
                    role="progressbar"
                    data-test="entry-progress"
                    [attr.aria-label]="n.title"
                    aria-valuemin="0"
                    aria-valuemax="100"
                    [attr.aria-valuenow]="n.progress"
                  >
                    <div class="fill" [style.width.%]="n.progress"></div>
                  </div>
                } @else if (n.indeterminate === true && n.kind === 'running') {
                  <div
                    class="progress"
                    role="progressbar"
                    data-test="entry-progress"
                    [attr.aria-label]="n.title"
                  >
                    <div class="sweep" data-test="entry-sweep"></div>
                  </div>
                }
                @if (n.url !== undefined) {
                  <button
                    type="button"
                    class="link"
                    data-test="entry-link"
                    (click)="openLink(n.id, n.url)"
                  >
                    {{ linkLabel(n) }}
                    <svg
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
                      <path d="M7 17L17 7M8 7h9v9"></path>
                    </svg>
                  </button>
                }
                @if (n.actions !== undefined && n.actions.length > 0) {
                  <div class="actions">
                    @for (a of n.actions; track a.id) {
                      <button
                        type="button"
                        class="action"
                        [attr.data-test]="'entry-action-' + a.id"
                        (click)="act(n.id, a.id)"
                      >
                        <svg
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
                          <path [attr.d]="actionIcons[a.id]"></path>
                        </svg>
                        {{ a.label }}
                      </button>
                    }
                  </div>
                }
              </div>
            </div>
          } @empty {
            <p class="empty" data-test="empty">Nothing here yet.</p>
          }
        </div>
        @if (error(); as message) {
          <p class="error" role="alert" data-test="error">{{ message }}</p>
        }
        <p class="foot">
          GitHub mentions and comments are read every 5 minutes, only while the GitHub feature is
          connected.
        </p>
      </section>
    }
  `,
  styles: [
    `
      :host {
        position: relative;
        display: inline-block;
      }
      button {
        font: inherit;
        cursor: pointer;
      }
      button:focus-visible {
        outline: 2px solid var(--sc-accent, #7aa2f7);
        outline-offset: 2px;
      }
      .bell {
        position: relative;
        width: 40px;
        height: 40px;
        border-radius: 20px;
        border: 1px solid var(--sc-line, #253249);
        background: transparent;
        color: var(--sc-fg, #e6e9f0);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .bell[aria-expanded='true'] {
        background: var(--sc-surface-2, #1a2436);
      }
      .badge {
        position: absolute;
        inset: -1px;
        pointer-events: none;
      }
      .badge .mat-badge-content {
        left: auto;
        right: -4px;
        margin-right: 0;
      }
      .panel {
        position: absolute;
        top: 48px;
        right: -4px;
        width: 400px;
        max-width: calc(100vw - 32px);
        max-height: 600px;
        display: flex;
        flex-direction: column;
        background: var(--sc-surface, #131c2d);
        color: var(--sc-fg, #e6e9f0);
        border: 1px solid var(--sc-line, #253249);
        border-radius: 14px;
        box-shadow: 0 16px 40px var(--sc-shadow);
        z-index: 5;
        overflow: hidden;
      }
      .head {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 14px 16px 10px;
      }
      .heading {
        font-weight: 600;
        font-size: 15px;
        margin-right: auto;
      }
      .mark {
        border: 0;
        background: transparent;
        color: var(--sc-accent, #7aa2f7);
        font-size: 13px;
        padding: 4px 6px;
        display: flex;
        align-items: center;
        gap: 6px;
      }
      .tabs {
        display: flex;
        gap: 6px;
        padding: 0 16px 10px;
        border-bottom: 1px solid var(--sc-line, #253249);
      }
      .tab {
        border: 1px solid transparent;
        background: transparent;
        color: var(--sc-muted, #9aa3b6);
        border-radius: 14px;
        padding: 5px 12px;
        font-size: 13px;
      }
      .tab[aria-selected='true'] {
        border-color: var(--sc-line, #253249);
        background: var(--sc-surface-2, #1a2436);
        color: var(--sc-fg, #e6e9f0);
      }
      .entries {
        overflow-y: auto;
        display: flex;
        flex-direction: column;
      }
      .entry {
        display: flex;
        gap: 12px;
        padding: 12px 16px;
        border-bottom: 1px solid var(--sc-line, #253249);
      }
      .entry.unread {
        background: var(--sc-unread);
      }
      .entry.unread .title {
        font-weight: 600;
      }
      .icon {
        width: 32px;
        height: 32px;
        flex: 0 0 32px;
        border-radius: 16px;
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 13px;
        font-weight: 700;
        background: var(--sc-surface-2);
        color: var(--sc-accent);
      }
      .icon[data-tone='ok'] {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .icon[data-tone='warn'] {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .icon[data-tone='err'] {
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
      }
      .icon[data-tone='muted'] {
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .controls {
        align-self: center;
        flex: 0 0 auto;
        display: flex;
        gap: 4px;
      }
      .control {
        width: 28px;
        height: 28px;
        padding: 0;
        border-radius: 14px;
        border: 1px solid var(--sc-line, #253249);
        background: transparent;
        color: var(--sc-fg, #e6e9f0);
        display: inline-flex;
        align-items: center;
        justify-content: center;
      }
      .control[aria-disabled='true'] {
        cursor: default;
        opacity: 0.5;
      }
      .status {
        font-size: 12px;
        color: var(--sc-muted, #9aa3b6);
        white-space: nowrap;
      }
      .status:focus-visible {
        outline: 2px solid var(--sc-accent, #7aa2f7);
        outline-offset: 2px;
      }
      .note {
        font-size: 12px;
        color: var(--sc-muted, #9aa3b6);
        line-height: 1.4;
        overflow-wrap: anywhere;
      }
      .body {
        display: flex;
        flex-direction: column;
        gap: 4px;
        min-width: 0;
        flex: 1;
      }
      .line {
        display: flex;
        gap: 8px;
        align-items: baseline;
      }
      .title {
        font-size: 14px;
        font-weight: 500;
        line-height: 1.35;
        margin-right: auto;
        overflow-wrap: anywhere;
      }
      .when {
        font-size: 12px;
        color: var(--sc-muted, #9aa3b6);
        white-space: nowrap;
      }
      .detail {
        font-size: 13px;
        color: var(--sc-fg-2, #c3cadb);
        line-height: 1.4;
        overflow-wrap: anywhere;
      }
      .progress {
        height: 6px;
        border-radius: 3px;
        background: var(--sc-surface-2, #1a2436);
        overflow: hidden;
      }
      .fill {
        height: 6px;
        background: var(--sc-accent, #7aa2f7);
      }
      .sweep {
        height: 6px;
        width: 40%;
        border-radius: 3px;
        background: var(--sc-accent, #7aa2f7);
        animation: sc-sweep 1.4s ease-in-out infinite;
      }
      @keyframes sc-sweep {
        0% {
          transform: translateX(-100%);
        }
        100% {
          transform: translateX(250%);
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .sweep {
          animation: none;
          transform: none;
        }
      }
      .link {
        border: 0;
        background: transparent;
        padding: 0;
        font-size: 13px;
        color: var(--sc-accent, #7aa2f7);
        font-weight: 500;
        display: inline-flex;
        align-items: center;
        gap: 4px;
        align-self: flex-start;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        margin-top: 2px;
      }
      .action {
        border: 1px solid var(--sc-line, #253249);
        background: var(--sc-surface-2, #1a2436);
        color: var(--sc-fg, #e6e9f0);
        border-radius: 8px;
        padding: 5px 10px;
        font-size: 13px;
        display: inline-flex;
        align-items: center;
        gap: 6px;
      }
      .empty,
      .foot,
      .error {
        margin: 0;
        padding: 10px 16px;
        font-size: 12px;
        color: var(--sc-muted, #9aa3b6);
      }
      .empty {
        padding: 16px;
      }
      .error {
        padding: 8px 16px;
        font-size: 13px;
        color: var(--sc-error, #c0504f);
      }
    `,
  ],
})
export class NotificationBellComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly bellButton = viewChild.required<ElementRef<HTMLButtonElement>>('bell');
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  readonly filters = FILTERS;
  readonly actionIcons = ACTION_ICONS;
  readonly controlIcons = CONTROL_ICONS;
  readonly notifications = signal<readonly AppNotification[]>([]);
  readonly open = signal(false);
  readonly filter = signal<NotificationFilter>('all');
  readonly error = signal<string | null>(null);
  private readonly now = signal(Date.now());
  private stopClock: () => void = () => undefined;
  private focusAfterClick: string | null = null;

  readonly unread = computed(() => this.notifications().filter((n) => !n.read).length);
  readonly label = computed(() =>
    this.unread() > 0 ? `Notifications, ${this.unread()} unread` : 'Notifications',
  );
  readonly shown = computed(() =>
    this.notifications().filter((n) => SHOWN_BY[this.filter()].includes(n.kind)),
  );

  ngOnInit(): void {
    this.destroyRef.onDestroy(() => this.stopClock());
    this.destroyRef.onDestroy(this.bridge.onNotifications((list) => this.received(list)));
    this.bridge
      .notificationsList()
      .then((list) => this.notifications.set(list))
      .catch(() => this.notifications.set([]));
  }

  ago(at: number): string {
    return timeAgo(at, this.now());
  }

  /** The controls the entry offers: none for an entry that is not a running or paused job. */
  controlsOf(n: AppNotification): readonly JobControlAction[] {
    return jobControls(n);
  }

  /**
   * The control's accessible name, naming the job as its title does: "Pause the audit of acme/shop", "Stop the tests
   * for Acme/API#41".
   */
  controlLabel(n: AppNotification, action: JobControlAction): string {
    const tests = n.id.startsWith('tests:');
    const name =
      JOB_NAME.exec(n.title)?.[1] ?? n.id.slice(tests ? 'tests:'.length : 'audit:'.length);
    return `${CONTROL_VERBS[action]} ${tests ? 'the tests for' : 'the audit of'} ${name}`;
  }

  /**
   * What Scout does with a click it queued, in place of the time until the worker says how it ended: a stop waits for
   * the area in progress only on a running audit.
   */
  waitingText(n: AppNotification): string {
    const action = n.control?.action ?? 'stop';
    if (n.id.startsWith('tests:') || (n.kind === 'paused' && action === 'stop')) return 'Stopping';
    return AUDIT_WAITING[action];
  }

  /**
   * The entry's icon as the artboard draws it: "A" for an audit and "T" for a test run, on the warn pair when the owner
   * paused it, muted once he stopped it, "!" on the error pair for an audit the AI Source paused; "@" for a mention, "C"
   * for new activity, "I" for issues opened, a tick for any other done entry and "!" for a failed one; any other entry
   * keeps its own drawing.
   */
  iconOf(n: AppNotification): EntryIcon {
    const tone = KIND_TONES[n.kind];
    const letter = n.id.startsWith('audit:') ? 'A' : n.id.startsWith('tests:') ? 'T' : null;
    if (letter !== null) {
      if (n.kind === 'paused' && !n.detail.startsWith(PAUSED_BY_YOU)) {
        return { tone: 'err', glyph: '!', path: null };
      }
      const stopped = n.kind === 'done' && n.title.endsWith(' stopped');
      return { tone: stopped ? 'muted' : tone, glyph: letter, path: null };
    }
    const glyph =
      n.kind === 'mention' || (n.kind === 'github' && n.detail.startsWith('You were mentioned'))
        ? '@'
        : n.kind === 'github' && n.detail.startsWith('New activity')
          ? 'C'
          : n.kind === 'done'
            ? n.id.startsWith('issues:')
              ? 'I'
              : '✓'
            : n.kind === 'failed'
              ? '!'
              : null;
    return { tone, glyph, path: glyph === null ? (OWN_ICONS[n.kind] ?? null) : null };
  }

  /** The label of the entry's link: GitHub's threads open on GitHub, issues opened show them, a verdict its review. */
  linkLabel(n: AppNotification): string {
    if (n.kind === 'github' || n.kind === 'mention') return 'Open on GitHub';
    if (n.id.startsWith('issues:')) return 'See the issues';
    return n.source === 'verdict' ? 'See the review' : 'Open link';
  }

  /**
   * Asks main to pause, continue or stop the entry's job; nothing is sent while a click on it is unanswered, and a
   * refusal shows in the panel.
   */
  control(n: AppNotification, action: JobControlAction): void {
    if (n.control !== undefined) return;
    this.error.set(null);
    this.focusAfterClick = n.id;
    this.bridge
      .notificationsControl({ id: n.id, action })
      .catch((err: unknown) => this.error.set(readIpcError(err).message));
  }

  toggle(): void {
    if (this.open()) {
      this.close();
      return;
    }
    this.now.set(Date.now());
    this.stopClock = this.startClock();
    this.error.set(null);
    this.open.set(true);
  }

  markAllRead(): void {
    this.bridge
      .notificationsMarkRead()
      .then((list) => this.notifications.set(list))
      .catch((err: unknown) => this.error.set(readIpcError(err).message));
  }

  /** Opens the entry's link in the browser and marks the entry read; a link main refuses leaves it unread. */
  openLink(id: string, url: string): void {
    this.error.set(null);
    this.bridge
      .appOpenExternal({ url })
      .then(() => this.markRead(id))
      .catch((err: unknown) => this.error.set(readIpcError(err).message));
  }

  /**
   * Opens Models on Transcription or Approvals here, or asks main to run the entry's action, and marks the entry
   * read; a refusal shows in the panel and leaves the entry unread.
   */
  act(id: string, action: NotificationActionId): void {
    this.error.set(null);
    if (action === 'review-pull-request') {
      void this.reviewIt(id);
      return;
    }
    const route = IN_WINDOW[action];
    if (route !== undefined) {
      this.markRead(id).catch(() => undefined);
      this.close();
      void this.router.navigateByUrl(route);
      return;
    }
    this.bridge
      .notificationsAct({ id, action })
      .then(() => this.markRead(id))
      .catch((err: unknown) => this.error.set(readIpcError(err).message));
  }

  /**
   * Asks Scout in the chat to review the pull request the entry links to, then opens the chat and marks the entry
   * read; a question the chat refuses shows in the panel and leaves the entry unread.
   */
  private async reviewIt(id: string): Promise<void> {
    const url = this.notifications().find((n) => n.id === id)?.url;
    const pullRequest = url === undefined ? undefined : PULL_REQUEST_URL.exec(url)?.[0];
    if (pullRequest === undefined) {
      this.error.set(NO_PULL_REQUEST);
      return;
    }
    const refusal = await this.injector
      .get(LocalConversation)
      .ask(`Review the pull request ${pullRequest}`);
    if (refusal !== null) {
      this.error.set(refusal);
      return;
    }
    this.markRead(id).catch(() => undefined);
    this.close();
    void this.router.navigateByUrl('/chat');
  }

  /** Escape closes the panel and returns focus to the bell. */
  onEscape(): void {
    if (this.open()) this.close();
  }

  /** A click outside closes the panel and leaves the focus where the owner clicked. */
  onDocumentClick(event: MouseEvent): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node | null)) {
      this.close(false);
    }
  }

  /**
   * Takes main's list; once the clicked entry's controls give way to what Scout does, focus moves to that line unless
   * the owner has moved it elsewhere.
   */
  private received(list: readonly AppNotification[]): void {
    this.notifications.set(list);
    const id = this.focusAfterClick;
    const clicked = list.find((n) => n.id === id);
    if (id === null || clicked?.control?.phase === 'asking') return;
    this.focusAfterClick = null;
    if (clicked?.control?.phase !== 'waiting') return;
    afterNextRender(
      () => {
        const entry = Array.from(
          this.host.nativeElement.querySelectorAll<HTMLElement>('[data-test="entry"]'),
        ).find((e) => e.getAttribute('data-entry') === id);
        const active = document.activeElement;
        if (
          entry === undefined ||
          (active !== null && active !== document.body && !entry.contains(active))
        ) {
          return;
        }
        entry.querySelector<HTMLElement>('[data-test="entry-status"]')?.focus();
      },
      { injector: this.injector },
    );
  }

  private markRead(id: string): Promise<void> {
    return this.bridge.notificationsMarkRead([id]).then((list) => this.notifications.set(list));
  }

  private startClock(): () => void {
    const timer = setInterval(() => this.now.set(Date.now()), CLOCK_MS);
    return () => clearInterval(timer);
  }

  private close(focusBell = true): void {
    this.stopClock();
    this.stopClock = () => undefined;
    this.open.set(false);
    if (focusBell) this.bellButton().nativeElement.focus();
  }
}
