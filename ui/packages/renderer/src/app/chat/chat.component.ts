import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  TemplateRef,
  afterRenderEffect,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';

import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';

import { LOCAL_CANCELLED_REPLY, readIpcError, type LocalTurn } from '@scout/shared';

import { ApprovalStripComponent } from '../core/approval-strip.component';
import { IconComponent } from '../core/icon.component';
import { LlmConfigStore } from '../core/llm-config.store';
import { LocalConversation, POSTING_CANNOT_STOP } from '../core/local-conversation';
import { MarkdownPipe } from '../core/markdown.pipe';
import { replyLinkTarget } from '../core/reply-links';
import { REPLY_STYLES } from '../core/reply-styles';
import { ReviewFindingsComponent, ReviewFindingsPipe } from '../core/review-findings.component';
import { ReviewFindingsStore } from '../core/review-findings.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { ConversationMode } from '../speech/conversation-mode';
import { ReadAloud } from '../speech/read-aloud';
import { ReadAloudButtonComponent } from '../speech/read-aloud-button.component';
import { ReadAloudSwitchComponent } from '../speech/read-aloud-switch.component';
import { TalkButtonComponent } from '../speech/talk-button.component';
import { VoiceModelAsk } from '../features/voice-model-ask';
import { LiveListener, NO_SPEECH_HEARD } from '../quick-ask/live-listener';

/**
 * The Chat screen: talk to Scout from this computer, with no chat platform. Enter sends,
 * Shift+Enter adds a line, hold the microphone button to talk, Stop cancels the answer and a new
 * question sent while Scout answers stops that answer and is asked instead; while Scout queues a
 * card's issues for approval, Send, Stop and Clear are off. The owner's question is a bubble on
 * the right; Scout's reply is a card on the left, headed by its name and the time, rendered from
 * Markdown; while Scout works, the question and a bare line saying what it is doing sit below the
 * approval strip, and the reply joins the conversation once it ends. Clear and Read answers aloud
 * are drawn by the shell's top bar.
 */
@Component({
  selector: 'sc-chat',
  standalone: true,
  imports: [
    ApprovalStripComponent,
    DatePipe,
    IconComponent,
    MarkdownPipe,
    MatButtonModule,
    NgTemplateOutlet,
    ReviewFindingsComponent,
    ReviewFindingsPipe,
    ReadAloudButtonComponent,
    ReadAloudSwitchComponent,
    TalkButtonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(window:blur)': 'discardRecording()',
    '(window:keydown.escape)': 'voice.escape()',
  },
  template: `
    <main class="chat">
      <ng-template #topBarActions>
        <button
          type="button"
          matButton="outlined"
          class="clear"
          data-test="clear"
          [title]="store.posting() ? postingCannotStop : 'Clear the conversation'"
          [disabled]="sending() || store.posting() || turns().length === 0"
          (click)="clear()"
        >
          <sc-icon name="trash" [size]="16" />Clear
        </button>
        <sc-read-aloud-switch look="bar" />
      </ng-template>
      <ng-template #asked let-turn>
        <div class="row user">
          <p class="sr-only" data-test="who-user">
            {{ turn.askedAt === undefined ? 'You' : 'You ' + (turn.askedAt | date: 'HH:mm') }}
          </p>
          <p class="question" data-test="question">
            @if (turn.voice) {
              <sc-icon name="mic" [size]="14" data-test="question-voice" aria-label="Spoken" />
            }
            <span [textContent]="turn.question"></span>
          </p>
          @if (turn.transcript) {
            <p class="with-transcript" data-test="with-transcript">with transcript</p>
          }
          @if (turn.screenText) {
            <p class="with-transcript" data-test="with-screen-text">with screen text</p>
          }
          @if (turn.review) {
            <p class="with-transcript" data-test="with-review">with the review</p>
          }
          @if (turn.issues) {
            <p class="with-transcript" data-test="with-issues">with the issues</p>
          }
        </div>
      </ng-template>
      <h1 class="sr-only">Chat</h1>
      <p class="sr-only" role="status" data-test="announce">{{ announcement() }}</p>
      <ol #turnList class="turns" data-test="turns" (scroll)="onTurnsScroll()">
        @for (turn of listed(); track turn.id) {
          <li class="turn" [attr.data-turn]="turn.id">
            @if (!turn.notice) {
              <ng-container
                [ngTemplateOutlet]="asked"
                [ngTemplateOutletContext]="{ $implicit: turn }"
              />
            }
            <div class="row scout" data-test="scout-reply">
              <p class="who" data-test="who-scout">
                <span class="name">Scout</span>
                @if (turn.askedAt; as at) {
                  <span [attr.data-test]="turn.notice ? 'arrived-at' : 'reply-time'">{{
                    at | date: 'HH:mm'
                  }}</span>
                }
                @if (turn.reply.length > 0) {
                  <sc-read-aloud-button [turnId]="turn.id" />
                }
              </p>
              @let findings = turn.state === 'done' ? (turn.reply | reviewFindings) : null;
              <div
                class="reply"
                data-test="reply"
                [class.failed]="turn.state === 'failed'"
                [innerHTML]="findings?.markdown ?? turn.reply | markdown"
                (click)="onReplyClick($event)"
              ></div>
              @if (turn.failure; as failure) {
                <p class="failure" data-test="failure">
                  {{ failure === cancelledReply ? 'Interrupted' : failure }}
                </p>
              }
              @if (findingCards().get(turn.id); as cards) {
                @if (cards.moved) {
                  <p class="moved-below" data-test="moved-below">
                    Its findings are in the cards below.
                  </p>
                } @else {
                  <sc-review-findings [turnId]="cards.anchor" [review]="cards.review" />
                }
              }
              @if (turn.notRunning && startedFor() !== turn.id) {
                <button
                  type="button"
                  class="start"
                  data-test="start-scout"
                  (click)="startScout(turn.id)"
                >
                  Start Scout
                </button>
              }
              @if (turn.citations.length > 0) {
                <ul class="citations" data-test="citations">
                  @for (citation of turn.citations; track $index) {
                    <li>
                      @if (citation.url; as url) {
                        <a href="#" (click)="$event.preventDefault(); open(url)">{{
                          citation.label
                        }}</a>
                      } @else {
                        <span>{{ citation.label }}</span>
                      }
                    </li>
                  }
                </ul>
              }
            </div>
          </li>
        }
        @if (turns().length === 0) {
          <li class="empty" data-test="empty">
            <span class="empty-mark" aria-hidden="true"
              ><svg
                viewBox="0 0 24 24"
                width="22"
                height="22"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
              >
                <rect x="4" y="8" width="16" height="12" rx="3"></rect>
                <path d="M12 8V4M9 4h6M9 14h.01M15 14h.01"></path></svg
            ></span>
            <p data-test="empty-text">Ask Scout anything, or hold the microphone button to talk.</p>
            <button
              type="button"
              class="suggestion"
              data-test="empty-suggestion"
              (click)="draft.set(suggestion)"
            >
              <svg
                viewBox="0 0 24 24"
                width="16"
                height="16"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path
                  d="M9 18h6M10 21h4M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2.1h5c0-.9.4-1.6 1-2.1A6 6 0 0 0 12 3z"
                ></path>
              </svg>
              Try: list my open pull requests
            </button>
          </li>
        }
      </ol>
      @if (recording()) {
        <p class="hint speaking" data-test="recording" role="status">
          <sc-icon name="mic" [size]="14" class="pulse" data-test="speaking" aria-hidden="true" />
          Listening. Release to send.
        </p>
      }
      @if (voiceNotice(); as notice) {
        <p class="hint" data-test="voice-notice" role="status">{{ notice }}</p>
      }
      @if (readAloud.error(); as reason) {
        <p class="voice-error" role="alert" data-test="voice-error">{{ reason }}</p>
      }
      @if (voiceAsk.status(); as status) {
        <p class="hint" data-test="voice-ask-status" role="status">{{ status }}</p>
      }
      @if (error(); as message) {
        <p class="error" role="alert" data-test="error">{{ message }}</p>
      }
      <sc-approval-strip />
      @if (runningTurns().length > 0) {
        <section
          #runningBlock
          class="running"
          data-test="running-turn"
          aria-live="polite"
          aria-label="Scout is answering"
          (focusin)="onRunningFocus($event)"
          (focusout)="onRunningBlur($event)"
        >
          @for (turn of runningTurns(); track turn.id) {
            <div class="running-item" [attr.data-turn]="turn.id">
              @if (!turn.notice) {
                <ng-container
                  [ngTemplateOutlet]="asked"
                  [ngTemplateOutletContext]="{ $implicit: turn }"
                />
              }
              @if (turn.reply.length > 0) {
                <div
                  class="reply"
                  data-test="reply"
                  [innerHTML]="turn.reply | markdown"
                  (click)="onReplyClick($event)"
                ></div>
              }
              <div class="activity">
                @if (turn.reply.length === 0 || turn.activity) {
                  <p class="working" data-test="working">{{ turn.activity ?? 'Working on it' }}</p>
                }
                @if (turn.reply.length > 0) {
                  <sc-read-aloud-button [turnId]="turn.id" />
                }
              </div>
            </div>
          }
        </section>
      }
      <form class="ask" (submit)="$event.preventDefault(); send()">
        <div class="line">
          <div class="field" data-test="field">
            <textarea
              data-test="input"
              aria-label="Ask Scout"
              rows="1"
              [placeholder]="noSource() ? 'Connect an AI Source first' : 'Ask Scout'"
              [disabled]="noSource()"
              [value]="draft()"
              (input)="draft.set($any($event.target).value)"
              (keydown.enter)="onEnter($any($event))"
            ></textarea>
            <button
              type="button"
              class="in"
              data-test="mic"
              [disabled]="busy() || noSource() || listeningElsewhere()"
              [class.recording]="recording()"
              [attr.aria-pressed]="recording()"
              [attr.aria-label]="recording() ? 'Listening' : 'Hold to talk'"
              [title]="recording() ? 'Listening' : 'Hold to talk'"
              (pointerdown)="startRecording()"
              (pointerup)="stopRecording()"
              (pointerleave)="stopRecording()"
            >
              <svg
                width="18"
                height="18"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                aria-hidden="true"
              >
                <rect x="9" y="3" width="6" height="11" rx="3"></rect>
                <path d="M5 11a7 7 0 0 0 14 0M12 18v3"></path>
              </svg>
            </button>
          </div>
          <sc-talk-button
            [disabled]="recording() || listeningElsewhere() || noSource()"
            [offReason]="noSource() ? 'Connect an AI Source first' : null"
          />
          @if (busy()) {
            <button
              type="button"
              class="send stop"
              data-test="stop"
              aria-label="Stop the answer"
              [disabled]="store.posting()"
              [title]="store.posting() ? postingCannotStop : 'Stop the answer'"
              (click)="store.stop()"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" aria-hidden="true">
                <rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor"></rect>
              </svg>
            </button>
          }
          <button
            type="submit"
            class="send"
            data-test="send"
            aria-label="Send"
            [title]="store.posting() ? postingCannotStop : 'Send'"
            [disabled]="store.posting() || noSource()"
          >
            <svg
              width="18"
              height="18"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="M22 2L11 13M22 2l-7 20-4-9-9-4z"></path>
            </svg>
          </button>
        </div>
        <p class="hint" data-test="composer-hint">
          {{
            store.posting()
              ? postingCannotStop
              : busy()
                ? 'A new question stops this answer · Stop ends it'
                : 'Enter sends · Shift+Enter for a new line'
          }}
        </p>
      </form>
    </main>
  `,
  styles: [
    REPLY_STYLES,
    `
      :host {
        display: block;
        height: 100%;
        overflow: hidden;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .chat {
        display: flex;
        flex-direction: column;
        gap: 16px;
        height: 100%;
        width: 100%;
        max-width: 880px;
        margin-inline: auto;
        padding: 24px;
        box-sizing: border-box;
        position: relative;
      }
      .row.user {
        position: relative;
      }
      .sr-only {
        position: absolute;
        width: 1px;
        height: 1px;
        margin: -1px;
        padding: 0;
        overflow: hidden;
        clip-path: inset(50%);
        white-space: nowrap;
        border: 0;
      }
      .clear {
        --mat-button-outlined-container-height: 32px;
        --mat-button-outlined-horizontal-padding: 12px;
        --mat-button-outlined-label-text-size: 13px;
        --mat-button-outlined-label-text-weight: 400;
        --mat-button-outlined-label-text-color: var(--sc-fg-2);
        --mat-button-outlined-outline-color: var(--sc-line);
        --mat-button-outlined-container-shape: var(--sc-radius-sm);
        --mat-button-outlined-disabled-label-text-color: var(--sc-muted);
        --mat-button-outlined-disabled-outline-color: var(--sc-line);
      }
      .clear sc-icon {
        margin-right: 6px;
      }
      .who .name {
        font-size: 14px;
        font-weight: 600;
      }
      .who sc-read-aloud-button,
      .activity sc-read-aloud-button {
        margin-left: auto;
      }
      .running {
        flex: 0 1 auto;
        min-height: 0;
        max-height: 60%;
        overflow-x: hidden;
        overflow-y: auto;
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .running-item {
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .activity {
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .running .working {
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .turns {
        flex: 1;
        min-height: 0;
        overflow-y: auto;
        list-style: none;
        margin: 0;
        padding: 0 4px 0 0;
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .turn {
        gap: 16px;
      }
      .question {
        max-width: min(520px, 80%);
        padding: 10px 14px;
        line-height: 1.45;
      }
      .row.scout {
        gap: 12px;
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: 14px;
        padding: 16px;
      }
      .empty {
        margin: auto 0;
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 10px;
        text-align: center;
        padding: 24px 12px;
      }
      .empty p {
        margin: 0;
        color: var(--sc-fg-2);
      }
      .empty-mark {
        width: 44px;
        height: 44px;
        border-radius: 50%;
        background: var(--sc-surface-2);
        color: var(--sc-accent);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .hint {
        color: var(--sc-muted);
        font-size: 12px;
        margin: 0;
      }
      .hint.speaking {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        color: var(--sc-error);
      }
      .pulse {
        animation: speaking 1.2s infinite;
      }
      @keyframes speaking {
        50% {
          opacity: 0.35;
        }
      }
      .error {
        color: var(--sc-error);
        margin: 0;
        font-size: 13px;
      }
      .voice-error {
        color: var(--sc-error);
        margin: 0;
        font-size: 12px;
      }
      .ask {
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .line {
        display: flex;
        align-items: flex-end;
        gap: 8px;
      }
      .field {
        position: relative;
        flex: 1;
        min-width: 0;
        display: flex;
      }
      .ask textarea {
        flex: 1;
        min-width: 0;
        resize: none;
        field-sizing: content;
        max-height: calc(6lh + 20px);
        overflow-y: auto;
        background: var(--sc-bg);
        border: 1px solid var(--sc-line);
        border-radius: 10px;
        color: var(--sc-fg);
        padding: 9px 44px 9px 12px;
        font: inherit;
        font-size: 14px;
        line-height: 20px;
      }
      .line sc-talk-button {
        margin-bottom: 4px;
      }
      .in,
      .send,
      .start,
      .suggestion {
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius-sm);
        color: var(--sc-fg);
        padding: 0 14px;
        cursor: pointer;
        display: inline-flex;
        align-items: center;
        gap: 6px;
      }
      .in:disabled,
      .start:disabled,
      .suggestion:disabled {
        opacity: 0.5;
        cursor: default;
      }
      button.in {
        position: absolute;
        right: 4px;
        bottom: 4px;
        width: 32px;
        height: 32px;
        padding: 0;
        justify-content: center;
        border-color: transparent;
        background: transparent;
        color: var(--sc-fg-2);
      }
      button.recording {
        background: var(--sc-error-container);
        border-color: var(--sc-error);
        color: var(--sc-on-error-container);
      }
      .send {
        flex: none;
        width: 40px;
        height: 40px;
        padding: 0;
        justify-content: center;
        border: 0;
        border-radius: 10px;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
      }
      .send.stop {
        background: var(--sc-danger);
        color: var(--sc-on-danger);
      }
      .send:disabled {
        background: var(--sc-surface-2);
        color: var(--sc-muted);
        opacity: 1;
        cursor: default;
      }
      .suggestion {
        height: 36px;
        border-radius: 18px;
        background: transparent;
        font-weight: 500;
      }
      .start {
        align-self: flex-start;
        height: 34px;
      }
      .failure {
        margin: 0;
        font-size: 12px;
        font-style: italic;
        color: var(--sc-muted);
      }
      .moved-below {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
      }
    `,
  ],
})
export class ChatComponent {
  protected readonly store = inject(LocalConversation);
  protected readonly findingCards = inject(ReviewFindingsStore).cards;
  private readonly bridge = inject(ScoutBridgeService);
  protected readonly voice = inject(ConversationMode);
  private readonly live = inject(LiveListener);
  protected readonly voiceAsk = inject(VoiceModelAsk);
  protected readonly readAloud = inject(ReadAloud);
  private readonly llmConfig = inject(LlmConfigStore);
  private readonly turnList = viewChild<ElementRef<HTMLElement>>('turnList');
  private readonly runningBlock = viewChild<ElementRef<HTMLElement>>('runningBlock');
  /** Clear and Read answers aloud, which the shell's top bar draws for this screen. */
  readonly topBarActions = viewChild.required<TemplateRef<unknown>>('topBarActions');

  readonly turns = this.store.turns;
  /** The turns Scout has finished, drawn as the conversation; a running turn is drawn below the approval strip instead. */
  readonly listed = computed(() => this.turns().filter((turn) => turn.state !== 'running'));
  readonly runningTurns = computed(() => this.turns().filter((turn) => turn.state === 'running'));
  readonly sending = signal(false);
  readonly busy = computed(() => this.sending() || this.store.running() !== null);
  /** True when the owner chose no AI Source, so Scout cannot answer. */
  readonly noSource = computed(() => this.llmConfig.provider() === 'None');
  readonly draft = signal('');
  readonly recording = signal(false);
  private readonly mainListening = signal(false);
  /** True while main listens for another window, such as a meeting; never during this screen's own hold or conversation. */
  readonly listeningElsewhere = computed(
    () => this.mainListening() && !this.recording() && !this.live.listening() && !this.voice.on(),
  );
  readonly error = signal<string | null>(null);
  /** The one sentence a screen reader is told about a turn that joined the conversation without passing through the running region. */
  readonly announcement = signal('');
  private readonly known = new Set<string>();
  private readonly seenRunning = new Set<string>();
  private restored = false;
  private focusedWhileRunning: {
    readonly turn: string;
    readonly control: 'read-aloud' | number;
  } | null = null;
  /** Said after a hold to talk that heard nothing. */
  readonly voiceNotice = signal<string | null>(null);
  readonly startedFor = signal<string | null>(null);
  protected readonly cancelledReply = LOCAL_CANCELLED_REPLY;
  protected readonly postingCannotStop = POSTING_CANNOT_STOP;
  protected readonly suggestion = 'List my open pull requests';
  private atBottom = true;
  private follow = false;

  constructor() {
    void this.voiceAsk.refresh();
    void this.llmConfig.loadFromVault();
    effect(() => {
      const heard = this.live.text();
      if (this.recording()) this.draft.set(heard);
    });
    afterRenderEffect(() => {
      this.turns();
      const list = this.turnList()?.nativeElement;
      if (list === undefined || !(this.atBottom || this.follow)) return;
      this.follow = false;
      list.scrollTop = list.scrollHeight;
    });
    effect(() => {
      if (!this.store.loaded()) return;
      const turns = this.turns();
      untracked(() => this.announceArrivals(turns));
    });
    afterRenderEffect(() => {
      this.listed();
      this.restoreFocus();
    });
    afterRenderEffect(() => {
      this.runningTurns();
      const block = this.runningBlock()?.nativeElement;
      if (block !== undefined) block.scrollTop = block.scrollHeight;
    });
    let pushed = false;
    const unsubscribe = this.bridge.onLocalListening(({ listening }) => {
      pushed = true;
      this.mainListening.set(listening);
    });
    this.bridge
      .localListening()
      .then(({ listening }) => {
        if (!pushed) this.mainListening.set(listening);
      })
      .catch(() => undefined);
    inject(DestroyRef).onDestroy(() => {
      unsubscribe();
      this.discardRecording();
      this.voice.escape();
    });
  }

  /** Remembers which control of a running turn holds focus, so the same control in its card can take it when the turn ends. */
  onRunningFocus(event: FocusEvent): void {
    const target = event.target as HTMLElement;
    const turn = target.closest('[data-turn]')?.getAttribute('data-turn');
    if (turn === undefined || turn === null) return;
    const links = Array.from(target.closest('[data-turn]')!.querySelectorAll('.reply a'));
    this.focusedWhileRunning = target.closest('sc-read-aloud-button')
      ? { turn, control: 'read-aloud' }
      : links.includes(target)
        ? { turn, control: links.indexOf(target) }
        : null;
  }

  /** Forgets the running control once focus moves to another element; focus lost with the node is kept for `restoreFocus`. */
  onRunningBlur(event: FocusEvent): void {
    if (event.relatedTarget instanceof Element) this.focusedWhileRunning = null;
  }

  private restoreFocus(): void {
    const held = this.focusedWhileRunning;
    if (held === null || !this.listed().some((turn) => turn.id === held.turn)) return;
    this.focusedWhileRunning = null;
    const active = document.activeElement;
    if (active !== null && active !== document.body) return;
    const card = this.turnList()?.nativeElement.querySelector(`[data-turn="${held.turn}"]`);
    const control =
      held.control === 'read-aloud'
        ? card?.querySelector<HTMLElement>('[data-test="read-aloud"]')
        : card?.querySelectorAll<HTMLElement>('.reply a')[held.control];
    control?.focus();
  }

  private announceArrivals(turns: readonly LocalTurn[]): void {
    const first = !this.restored;
    this.restored = true;
    for (const turn of turns) {
      if (turn.state === 'running') this.seenRunning.add(turn.id);
      else if (this.seenRunning.delete(turn.id) && turn.state === 'failed') {
        this.announcement.set(
          turn.failure === undefined
            ? turn.reply
            : turn.failure === LOCAL_CANCELLED_REPLY
              ? 'Interrupted'
              : turn.failure,
        );
      }
      if (this.known.has(turn.id)) continue;
      this.known.add(turn.id);
      if (!first && turn.state !== 'running') {
        this.announcement.set(turn.notice === true ? `Scout: ${turn.reply}` : turn.reply);
      }
    }
  }

  /** Remembers whether the reader is within 48 px of the newest turn, so a new turn only scrolls a list left at the bottom. */
  onTurnsScroll(): void {
    const list = this.turnList()?.nativeElement;
    if (list !== undefined) {
      this.atBottom = list.scrollHeight - list.scrollTop - list.clientHeight <= 48;
    }
  }

  onEnter(event: KeyboardEvent): void {
    if (event.shiftKey) return;
    event.preventDefault();
    void this.send();
  }

  async send(): Promise<void> {
    const text = this.draft().trim();
    if (text.length === 0 || this.sending() || this.store.posting() || this.noSource()) return;
    this.error.set(null);
    this.sending.set(true);
    this.follow = true;
    let refusal: string | null;
    try {
      refusal = await this.store.ask(text);
    } finally {
      this.sending.set(false);
    }
    this.error.set(refusal);
    if (refusal === null && this.draft().trim() === text) this.draft.set('');
  }

  /** Empties the conversation, stopping an answer still running; main's refusal while Scout queues a card's issues is shown. */
  async clear(): Promise<void> {
    this.error.set(await this.store.clear());
  }

  async startScout(turnId: string): Promise<void> {
    if (this.startedFor() === turnId) return;
    this.startedFor.set(turnId);
    this.error.set(null);
    try {
      await this.bridge.workerStart();
    } catch (err: unknown) {
      this.startedFor.set(null);
      this.error.set(readIpcError(err).message);
    }
  }

  /** Stops hold to talk and throws what was heard away, as when the window loses focus. */
  discardRecording(): void {
    if (!this.recording()) return;
    this.recording.set(false);
    void this.live.stop('me');
    this.live.clear();
    this.draft.set('');
  }

  /**
   * Hold to talk: the microphone streams to the live transcriber and what it hears fills the message box; a listen
   * that does not open shows why.
   */
  async startRecording(): Promise<void> {
    if (this.recording() || this.busy() || this.noSource()) return;
    if (!this.voiceAsk.allowsVoice()) return;
    this.error.set(null);
    this.voiceNotice.set(null);
    this.recording.set(true);
    await this.live.listen('me', 'off', true);
    if (this.live.listening()) return;
    this.recording.set(false);
    this.error.set(this.live.error());
  }

  /**
   * Release: waits for the last final, then sends the heard text as a typed question marked `voice`; nothing heard
   * sends nothing and says so, and a transcription failure shows its reason and sends nothing.
   */
  async stopRecording(): Promise<void> {
    if (!this.recording()) return;
    this.recording.set(false);
    const { text, error } = await this.live.finish('me');
    this.live.clear();
    this.draft.set('');
    if (error !== null) {
      this.error.set(error);
      return;
    }
    const said = text.trim();
    if (said.length === 0) {
      this.voiceNotice.set(NO_SPEECH_HEARD);
      return;
    }
    this.sending.set(true);
    this.follow = true;
    try {
      this.error.set(await this.store.ask(said, undefined, false, '', true));
    } finally {
      this.sending.set(false);
    }
  }

  open(url: string): void {
    this.bridge
      .appOpenExternal({ url })
      .catch((err: unknown) => this.error.set(readIpcError(err).message));
  }

  /** A link in a rendered reply opens in the browser, never in this window. */
  onReplyClick(event: MouseEvent): void {
    const url = replyLinkTarget(event);
    if (url !== null) this.open(url);
  }
}
