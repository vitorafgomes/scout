import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';

import { readIpcError } from '@scout/shared';

import { LocalConversation } from '../core/local-conversation';
import { ScoutBridgeService } from '../core/scout-bridge';
import { WavRecorder } from '../quick-ask/wav-recorder';

/**
 * The Chat screen: talk to Scout from this computer, with no chat platform. Enter sends,
 * Shift+Enter adds a line, hold the microphone button to talk, Stop cancels the answer.
 */
@Component({
  selector: 'sc-chat',
  standalone: true,
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
              <p class="question" data-test="question" [textContent]="turn.question"></p>
              @if (turn.transcript) {
                <p class="with-transcript" data-test="with-transcript">with transcript</p>
              }
              @if (turn.screenText) {
                <p class="with-transcript" data-test="with-screen-text">with screen text</p>
              }
            }
            <p
              class="reply"
              data-test="reply"
              [class.failed]="turn.state === 'failed'"
              [textContent]="turn.reply"
            ></p>
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
    `
      :host {
        display: block;
        height: 100vh;
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
        overflow-y: auto;
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .question {
        margin: 0 0 6px auto;
        max-width: 80%;
        width: fit-content;
        background: #1e2e45;
        border-radius: 8px;
        padding: 8px 12px;
        white-space: pre-wrap;
      }
      .with-transcript {
        margin: 0 0 6px auto;
        width: fit-content;
        font-size: 11px;
        color: #8a93a6;
      }
      .reply {
        margin: 0;
        white-space: pre-wrap;
        line-height: 1.5;
        font-size: 14px;
      }
      .reply.failed,
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
      .citations {
        margin: 6px 0 0;
        padding-left: 18px;
        font-size: 13px;
      }
      .citations a {
        color: #7aa2f7;
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
}
