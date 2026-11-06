import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';

import { DatePipe } from '@angular/common';

import { readIpcError } from '@scout/shared';

import { LocalConversation } from '../core/local-conversation';
import { MarkdownPipe } from '../core/markdown.pipe';
import { replyLinkTarget } from '../core/reply-links';
import { REPLY_STYLES } from '../core/reply-styles';
import { ScoutBridgeService } from '../core/scout-bridge';
import { WavRecorder } from '../quick-ask/wav-recorder';

/**
 * The Chat screen: talk to Scout from this computer, with no chat platform. Enter sends,
 * Shift+Enter adds a line, hold the microphone button to talk, Stop cancels the answer. Each
 * turn shows who spoke: the owner's question on the right, Scout's reply under its mark on the
 * left, rendered from Markdown; while Scout works the reply shows three dots and what it is doing.
 */
@Component({
  selector: 'sc-chat',
  standalone: true,
  imports: [DatePipe, MarkdownPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(window:blur)': 'discardRecording()' },
  template: `
    <main class="chat">
      <header class="head">
        <h1>Chat</h1>
        <button
          type="button"
          data-test="clear"
          [disabled]="busy() || turns().length === 0"
          (click)="store.clear()"
        >
          Clear
        </button>
      </header>
      <ol class="turns" data-test="turns" aria-live="polite">
        @for (turn of turns(); track turn.id) {
          <li class="turn">
            @if (!turn.notice) {
              <div class="row user">
                <span class="avatar user" aria-hidden="true"
                  ><svg
                    viewBox="0 0 24 24"
                    width="16"
                    height="16"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    focusable="false"
                  >
                    <circle cx="12" cy="8" r="4"></circle>
                    <path d="M4 21c0-4 3.6-7 8-7s8 3 8 7"></path></svg
                ></span>
                <div class="col">
                  <p class="who" data-test="who-user">
                    <span>You</span>
                    @if (turn.askedAt; as askedAt) {
                      <span>{{ askedAt | date: 'HH:mm' }}</span>
                    }
                  </p>
                  <p class="question" data-test="question" [textContent]="turn.question"></p>
                  @if (turn.transcript) {
                    <p class="with-transcript" data-test="with-transcript">with transcript</p>
                  }
                  @if (turn.screenText) {
                    <p class="with-transcript" data-test="with-screen-text">with screen text</p>
                  }
                </div>
              </div>
            }
            <div class="row scout">
              <span class="avatar scout" aria-hidden="true"
                ><svg
                  viewBox="0 0 24 24"
                  width="16"
                  height="16"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  focusable="false"
                >
                  <rect x="4" y="8" width="16" height="12" rx="3"></rect>
                  <circle cx="9" cy="14" r="1.2" fill="currentColor" stroke="none"></circle>
                  <circle cx="15" cy="14" r="1.2" fill="currentColor" stroke="none"></circle>
                  <path d="M12 8V4M9 4h6"></path></svg
              ></span>
              <div class="col">
                <p class="who" data-test="who-scout"><span>Scout</span></p>
                @if (turn.state === 'running' && (turn.reply.length === 0 || turn.activity)) {
                  <p class="working" data-test="working">
                    <span class="dots" aria-hidden="true"><i></i><i></i><i></i></span>
                    <span>{{ turn.activity ?? 'Working on it' }}</span>
                  </p>
                }
                <div
                  class="reply"
                  data-test="reply"
                  [class.failed]="turn.state === 'failed'"
                  [innerHTML]="turn.reply | markdown"
                  (click)="onReplyClick($event)"
                ></div>
                @if (turn.notRunning && startedFor() !== turn.id) {
                  <button type="button" data-test="start-scout" (click)="startScout(turn.id)">
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
            </div>
          </li>
        } @empty {
          <li class="empty">Ask Scout anything, or hold the microphone button to talk.</li>
        }
      </ol>
      @if (recording()) {
        <p class="hint" data-test="recording">Listening. Release to send.</p>
      }
      @if (error(); as message) {
        <p class="error" role="alert" data-test="error">{{ message }}</p>
      }
      <form class="ask" (submit)="$event.preventDefault(); send()">
        <textarea
          data-test="input"
          aria-label="Ask Scout"
          rows="2"
          placeholder="Ask Scout. Enter sends, Shift+Enter adds a line."
          [value]="draft()"
          (input)="draft.set($any($event.target).value)"
          (keydown.enter)="onEnter($any($event))"
        ></textarea>
        <button
          type="button"
          data-test="mic"
          [disabled]="busy()"
          [class.recording]="recording()"
          [attr.aria-pressed]="recording()"
          aria-label="Hold to talk"
          (pointerdown)="startRecording()"
          (pointerup)="stopRecording()"
          (pointerleave)="stopRecording()"
        >
          {{ recording() ? 'Listening' : 'Mic' }}
        </button>
        @if (busy()) {
          <button type="button" data-test="stop" (click)="store.stop()">Stop</button>
        } @else {
          <button type="submit" data-test="send">Send</button>
        }
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
        gap: 12px;
        height: 100%;
        padding: 24px 32px;
        box-sizing: border-box;
      }
      .head {
        display: flex;
        align-items: center;
        justify-content: space-between;
      }
      h1 {
        font-size: 22px;
        margin: 0;
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
        gap: 20px;
      }
      .error {
        color: #d45a5a;
      }
      .empty,
      .hint {
        color: #7b8299;
        font-size: 13px;
        margin: 0;
      }
      .error {
        margin: 0;
        font-size: 13px;
      }
      .ask {
        display: flex;
        gap: 8px;
      }
      .ask textarea {
        flex: 1;
        resize: none;
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        color: #e4e8f0;
        padding: 10px 12px;
        font: inherit;
        font-size: 14px;
      }
      button {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        color: #e4e8f0;
        padding: 0 14px;
        cursor: pointer;
      }
      button:disabled {
        opacity: 0.5;
        cursor: default;
      }
      button.recording {
        background: #2b1010;
        border-color: #d45a5a;
      }
    `,
  ],
})
export class ChatComponent {
  protected readonly store = inject(LocalConversation);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly recorder = inject(WavRecorder);

  readonly turns = this.store.turns;
  readonly busy = computed(() => this.store.running() !== null);
  readonly draft = signal('');
  readonly recording = signal(false);
  readonly error = signal<string | null>(null);
  readonly startedFor = signal<string | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.discardRecording());
  }

  onEnter(event: KeyboardEvent): void {
    if (event.shiftKey) return;
    event.preventDefault();
    void this.send();
  }

  async send(): Promise<void> {
    const text = this.draft().trim();
    if (text.length === 0 || this.busy()) return;
    this.draft.set('');
    this.error.set(null);
    this.error.set(await this.store.ask(text));
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

  /** Stops the microphone and throws the recording away, as when the window loses focus. */
  discardRecording(): void {
    if (!this.recording()) return;
    this.recording.set(false);
    void this.recorder.stop();
  }

  async startRecording(): Promise<void> {
    if (this.recording() || this.busy()) return;
    this.error.set(null);
    this.recording.set(true);
    try {
      await this.recorder.start(() => void this.stopRecording());
    } catch {
      this.recording.set(false);
      this.error.set('Scout could not use the microphone.');
    }
  }

  async stopRecording(): Promise<void> {
    if (!this.recording()) return;
    this.recording.set(false);
    const wav = await this.recorder.stop();
    if (wav !== null) this.error.set(await this.store.askByVoice(wav));
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
