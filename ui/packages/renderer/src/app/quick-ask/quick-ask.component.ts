import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  afterRenderEffect,
  inject,
  signal,
  viewChild,
} from '@angular/core';

import { readIpcError } from '@scout/shared';

import { LocalConversation } from '../core/local-conversation';
import { ScoutBridgeService } from '../core/scout-bridge';
import { WavRecorder } from './wav-recorder';

/**
 * The box the global shortcut opens: type a question and press Enter, or hold
 * Space (with the box empty) or the microphone button to talk. It shows the same
 * conversation as the Chat screen, each question with its reply streaming in as
 * plain text; Escape or the Close button hides the window.
 */
@Component({
  selector: 'sc-quick-ask',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(window:keydown)': 'onKeyDown($event)',
    '(window:keyup)': 'onKeyUp($event)',
    '(window:blur)': 'discardRecording()',
  },
  template: `
    <main class="quick-ask">
      <header class="grip" data-test="grip">Scout</header>
      <form class="ask" (submit)="$event.preventDefault(); ask()">
        <input
          #questionInput
          type="text"
          data-test="question"
          aria-label="Ask Scout"
          placeholder="Ask Scout, or hold Space to talk"
          [value]="question()"
          (input)="question.set($any($event.target).value)"
        />
        <button
          type="button"
          data-test="mic"
          [class.recording]="recording()"
          [attr.aria-pressed]="recording()"
          aria-label="Hold to talk"
          (pointerdown)="startRecording()"
          (pointerup)="stopRecording()"
          (pointerleave)="stopRecording()"
        >
          {{ recording() ? 'Listening' : 'Mic' }}
        </button>
        <button
          type="button"
          class="close"
          data-test="close"
          aria-label="Close"
          title="Close (Esc)"
          (click)="close()"
        >
          ×
        </button>
      </form>
      @if (recording()) {
        <p class="hint" data-test="recording">Listening. Release to send.</p>
      }
      @if (error(); as message) {
        <p class="error" role="alert" data-test="error">{{ message }}</p>
      }
      <ol #turnList class="turns" data-test="turns" aria-live="polite">
        @for (turn of turns(); track turn.id) {
          <li class="turn">
            <p class="asked" data-test="asked" [textContent]="turn.question"></p>
            <p
              class="reply"
              data-test="reply"
              [class.failed]="turn.state === 'failed'"
              [textContent]="turn.reply"
            ></p>
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
        }
      </ol>
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        height: 100vh;
        background: #0f1117;
        color: #e4e8f0;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .quick-ask {
        display: flex;
        flex-direction: column;
        gap: 10px;
        padding: 0 14px 14px;
        height: 100%;
        box-sizing: border-box;
      }
      .grip {
        -webkit-app-region: drag;
        height: 14px;
        margin: 0 -14px;
        padding: 2px 14px 0;
        font-size: 10px;
        line-height: 12px;
        color: #7b8299;
        user-select: none;
        cursor: grab;
      }
      input,
      button {
        -webkit-app-region: no-drag;
      }
      .ask {
        display: flex;
        gap: 8px;
      }
      .ask input {
        flex: 1;
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        color: #e4e8f0;
        padding: 10px 12px;
        font-size: 15px;
      }
      .ask button {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        color: #e4e8f0;
        padding: 0 14px;
        cursor: pointer;
      }
      .ask button.close {
        font-size: 20px;
        line-height: 1;
      }
      .ask button.recording {
        background: #2b1010;
        border-color: #d45a5a;
      }
      .turns {
        flex: 1;
        overflow-y: auto;
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .asked {
        margin: 0 0 4px auto;
        max-width: 80%;
        width: fit-content;
        background: #1e2e45;
        border-radius: 8px;
        padding: 6px 10px;
        white-space: pre-wrap;
        font-size: 14px;
      }
      .reply {
        margin: 0;
        white-space: pre-wrap;
        font-size: 14px;
        line-height: 1.5;
      }
      .reply.failed {
        color: #d45a5a;
      }
      .hint {
        margin: 0;
        font-size: 13px;
        color: #7b8299;
      }
      .error {
        margin: 0;
        font-size: 13px;
        color: #d45a5a;
      }
      .citations {
        margin: 4px 0 0;
        padding-left: 18px;
        font-size: 13px;
      }
      .citations a {
        color: #7aa2f7;
      }
    `,
  ],
})
export class QuickAskComponent {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly conversation = inject(LocalConversation);
  private readonly recorder = inject(WavRecorder);
  private readonly input = viewChild<ElementRef<HTMLInputElement>>('questionInput');
  private readonly turnList = viewChild<ElementRef<HTMLElement>>('turnList');

  readonly turns = this.conversation.turns;
  readonly question = signal('');
  readonly recording = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    afterRenderEffect(() => {
      this.turns();
      const list = this.turnList()?.nativeElement;
      if (list !== undefined) list.scrollTop = list.scrollHeight;
    });
    inject(DestroyRef).onDestroy(() => {
      if (this.recording()) void this.recorder.stop();
    });
    afterNextRender(() => this.input()?.nativeElement.focus());
  }

  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.close();
      return;
    }
    if (event.code !== 'Space') return;
    if (this.recording()) {
      event.preventDefault();
      return;
    }
    if (event.repeat || this.question().length > 0) return;
    event.preventDefault();
    void this.startRecording();
  }

  onKeyUp(event: KeyboardEvent): void {
    if (event.code === 'Space' && this.recording()) {
      event.preventDefault();
      void this.stopRecording();
    }
  }

  /** Throws away any recording in progress and hides the window. */
  close(): void {
    this.discardRecording();
    void this.bridge.localHide();
  }

  /** Stops the microphone and throws the recording away, as on Escape or when the window loses focus. */
  discardRecording(): void {
    if (!this.recording()) return;
    this.recording.set(false);
    void this.recorder.stop();
  }

  async ask(): Promise<void> {
    const text = this.question().trim();
    if (text.length === 0) return;
    this.question.set('');
    this.error.set(null);
    this.conversation.stop();
    this.error.set(await this.conversation.ask(text));
  }

  async startRecording(): Promise<void> {
    if (this.recording()) return;
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
    if (wav === null) return;
    this.error.set(null);
    this.conversation.stop();
    this.error.set(await this.conversation.askByVoice(wav));
  }

  open(url: string): void {
    this.bridge
      .appOpenExternal({ url })
      .catch((err: unknown) => this.error.set(readIpcError(err).message));
  }
}
