import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  afterRenderEffect,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';

import {
  LAST_ANSWER_MARKER,
  MEETING_WINDOWS,
  SCREEN_TEXT_MARKER,
  readIpcError,
  transcriptMarker,
  type LocalImage,
  type LocalMeeting,
  type LocalTranslationMode,
} from '@scout/shared';

import { LlmConfigStore, providerLabel } from '../core/llm-config.store';
import { LocalConversation } from '../core/local-conversation';
import { MarkdownPipe } from '../core/markdown.pipe';
import { replyLinkTarget } from '../core/reply-links';
import { REPLY_STYLES } from '../core/reply-styles';
import { ScoutBridgeService } from '../core/scout-bridge';
import { LiveListener } from './live-listener';
import { grabScreenshot, type Screenshot } from './screen-grab';
import { TranscriptStore } from './transcript-store';
import { WavRecorder } from './wav-recorder';

export const CAPTURE_STATUS = 'Choose what Scout may see...';
export const CAPTURE_FAILED = 'Scout could not capture the screen.';
export const SYSTEM_AUDIO_UNAVAILABLE =
  'System audio capture is not available on this platform yet.';
export const MAX_SCREEN_TEXT_CHARS = 5000;
export const TRANSLATION_STORAGE_KEY = 'scout.quickAsk.translation';
export const NO_AI_SOURCE_DISCLOSURE = 'No AI Source is connected; lines are not translated.';
export const MEETING_WINDOW_STORAGE_KEY = 'scout.quickAsk.meetingWindow';
export const MAX_LAST_ANSWER_CHARS = 4000;
export const MEETING_PROMPTS = [
  'Summarise the last minutes',
  'What was decided?',
  'What did they ask me?',
] as const;

type MeetingWindow = (typeof MEETING_WINDOWS)[number];

function storedMeetingWindow(): MeetingWindow {
  try {
    const stored = Number(localStorage.getItem(MEETING_WINDOW_STORAGE_KEY));
    return MEETING_WINDOWS.find((minutes) => minutes === stored) ?? 10;
  } catch {
    return 10;
  }
}

/**
 * A time as ISO 8601 at an offset in minutes east of UTC, the local time zone's by default, such as
 * `2026-11-04T10:00:00.000+01:00`.
 */
export function localIso(ms: number, offset = -new Date(ms).getTimezoneOffset()): string {
  const pad = (n: number): string => String(n).padStart(2, '0');
  const zone = `${offset >= 0 ? '+' : '-'}${pad(Math.floor(Math.abs(offset) / 60))}:${pad(Math.abs(offset) % 60)}`;
  return new Date(ms + offset * 60_000).toISOString().slice(0, 23) + zone;
}

function storedTranslationMode(): LocalTranslationMode {
  try {
    const stored = localStorage.getItem(TRANSLATION_STORAGE_KEY);
    return stored === 'system' || stored === 'channel' ? stored : 'off';
  } catch {
    return 'off';
  }
}

/**
 * The box the global shortcut opens: type a question and press Enter, or hold
 * Space (with the box empty) or the microphone button to talk, or turn on Listen
 * to see what Scout hears fill the box and send it with Enter. Attach screenshot asks
 * the system for one frame of a screen or window, shown as a preview and sent with
 * the next question; Read text asks the worker for the text on it, which fills an
 * editable block the next typed question carries as data. Others listens to the system audio beside the microphone,
 * shows "Listening to system audio" while it runs, and fills the transcript panel,
 * whose last 10 minutes the next typed question carries as data once Others has
 * said a line (the owner can switch it on or off by hand). The translation select,
 * remembered on this machine, sets the language the next listen on either lane is
 * translated into; each translation shows under its line and never goes with a
 * question. While the select is not Off a line names the AI Source each line goes
 * to for translation, or says none is connected. It shows the same
 * conversation as the Chat screen, each question with its reply streaming in as
 * plain text; Escape or the Close button stops every lane and the microphone and
 * hides the window, while losing focus leaves an open lane running. Meeting opens
 * both lanes at once with a clock; a question asked then carries the last answer,
 * the screen text and the transcript of the chosen window as data, and Stop (or
 * Escape and Close) shows the row where the owner may keep the meeting on this
 * machine, which is off by default.
 */
@Component({
  selector: 'sc-quick-ask',
  standalone: true,
  imports: [MarkdownPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(window:keydown)': 'onKeyDown($event)',
    '(window:keyup)': 'onKeyUp($event)',
    '(window:blur)': 'discardRecording()',
    '(window:focus)': 'readProvider()',
  },
  template: `
    <main class="quick-ask">
      <header class="grip" data-test="grip">Scout</header>
      <div class="shot">
        @if (meeting() === 'running') {
          <button
            type="button"
            class="recording"
            data-test="meeting-stop"
            aria-pressed="true"
            (click)="stopMeeting()"
          >
            Stop meeting
          </button>
        } @else {
          <button
            type="button"
            data-test="meeting-start"
            aria-pressed="false"
            [disabled]="meeting() === 'stopping' || recording()"
            (click)="startMeeting()"
          >
            Meeting
          </button>
        }
        <button
          type="button"
          data-test="listen-others"
          [class.recording]="othersListening()"
          [attr.aria-pressed]="othersListening()"
          [disabled]="!live.systemAudioAvailable"
          aria-label="Listen to the system audio"
          (click)="toggleOthers()"
        >
          {{ othersListening() ? 'Stop others' : 'Others' }}
        </button>
        @if (listening() || othersListening()) {
          <button type="button" data-test="stop-all" (click)="stopAll()">Stop all</button>
        }
        <select
          data-test="translation-mode"
          aria-label="Translate what Scout hears"
          title="Translate what Scout hears, from the next Listen"
          [value]="translation()"
          (change)="setTranslation($any($event.target).value)"
        >
          <option value="off">Off</option>
          <option value="system">System language</option>
          <option value="channel">Channel language</option>
        </select>
        <button
          type="button"
          data-test="attach-screenshot"
          [disabled]="capturing()"
          (click)="attachScreenshot()"
        >
          Attach screenshot
        </button>
        @if (screenshot(); as shot) {
          <img
            class="preview"
            data-test="screenshot-preview"
            alt="Screenshot to send with the next question"
            [src]="'data:image/jpeg;base64,' + shot.base64"
          />
          <button type="button" data-test="remove-screenshot" (click)="screenshot.set(null)">
            Remove
          </button>
        }
        <button
          type="button"
          data-test="read-text"
          [disabled]="screenshot() === null || reading()"
          (click)="readText()"
        >
          {{ reading() ? 'Reading...' : 'Read text' }}
        </button>
      </div>
      @if (meeting() === 'running') {
        <p class="hint live" data-test="meeting-indicator" role="status">
          Meeting {{ clock() }}{{ live.systemAudioAvailable ? '' : ', microphone only' }}
        </p>
        <div class="shot" data-test="meeting-tools">
          <select
            data-test="meeting-window"
            aria-label="Transcript a question carries"
            [value]="meetingWindow()"
            (change)="setMeetingWindow(+$any($event.target).value)"
          >
            @for (minutes of meetingWindows; track minutes) {
              <option [value]="minutes">Last {{ minutes }} min</option>
            }
          </select>
          @for (prompt of meetingPrompts; track prompt) {
            <button type="button" data-test="meeting-prompt" (click)="fillQuestion(prompt)">
              {{ prompt }}
            </button>
          }
        </div>
      }
      @if (meeting() === 'stopping') {
        <section class="meeting-stop" data-test="meeting-stop-row" aria-label="Meeting ended">
          <label>
            <input
              type="checkbox"
              data-test="meeting-keep"
              [checked]="keepMeeting()"
              (change)="keepMeeting.set($any($event.target).checked)"
            />
            Keep this meeting
          </label>
          <input
            type="text"
            data-test="meeting-title"
            aria-label="Meeting title"
            maxlength="200"
            [value]="meetingTitle()"
            (input)="meetingTitle.set($any($event.target).value)"
          />
          <button
            type="button"
            data-test="meeting-done"
            [disabled]="keeping()"
            (click)="doneMeeting()"
          >
            Done
          </button>
        </section>
        @if (meetingError(); as message) {
          <p class="error" role="alert" data-test="meeting-keep-error">{{ message }}</p>
        }
      }
      @if (meetingKept()) {
        <p class="hint" data-test="meeting-kept" role="status">Kept</p>
      }
      @if (translation() !== 'off') {
        <p class="hint" data-test="translation-disclosure">{{ disclosure() }}</p>
      }
      @if (readTextError(); as message) {
        <p class="error" role="alert" data-test="read-text-error">{{ message }}</p>
      }
      @if (screenText() !== null) {
        <section class="screen-text" aria-label="Text from the screen">
          <label class="hint" for="screen-text">Text from the screen</label>
          <textarea
            id="screen-text"
            data-test="screen-text"
            rows="3"
            [attr.maxlength]="maxScreenText"
            [value]="screenText() ?? ''"
            (input)="screenText.set($any($event.target).value)"
          ></textarea>
          <div class="transcript-actions">
            <span data-test="screen-text-count"
              >{{ (screenText() ?? '').length }} / {{ maxScreenText }}</span
            >
            <button type="button" data-test="screen-text-clear" (click)="screenText.set(null)">
              Clear
            </button>
          </div>
        </section>
      }
      @if (!live.systemAudioAvailable) {
        <p class="hint" data-test="system-audio-unavailable">{{ systemAudioUnavailable }}</p>
      }
      @if (othersListening()) {
        <p class="hint live" data-test="system-audio-indicator" role="status">
          Listening to system audio
        </p>
      }
      @if (capturing()) {
        <p class="hint live" data-test="capture-status" role="status">{{ captureStatus }}</p>
      }
      @if (captureError(); as message) {
        <p class="error" role="alert" data-test="capture-error">{{ message }}</p>
      }
      @if (recording()) {
        <p class="hint" data-test="recording">Listening. Release to send.</p>
      }
      @if (listening()) {
        <p class="hint live" data-test="listening" role="status">Listening...</p>
      }
      @if (error() ?? live.error(); as message) {
        <p class="error" role="alert" data-test="error">{{ message }}</p>
      }
      @if (lines().length > 0 || listening() || othersListening() || meeting() !== 'off') {
        <section class="transcript" data-test="transcript" aria-label="Transcript">
          <ol #transcriptList class="lines">
            @for (line of lines(); track line) {
              <li data-test="transcript-line">
                <span class="label">{{ line.label }}</span> {{ line.text }}
                @if (line.translation; as translated) {
                  @if (translated.error; as gap) {
                    <span class="translation gap" data-test="transcript-translation-gap">{{
                      gap === 'skipped' ? 'Translation skipped' : gap
                    }}</span>
                  } @else {
                    <span class="translation" data-test="transcript-translation">{{
                      translated.text
                    }}</span>
                  }
                }
              </li>
            }
            @if (othersListening() && live.partials().others; as heard) {
              <li class="partial" data-test="transcript-partial">
                <span class="label">Others</span> {{ heard }}
              </li>
            }
          </ol>
          @for (note of transcript.notes(); track note) {
            <p class="hint" data-test="translation-note" role="status">{{ note }}</p>
          }
          <div class="transcript-actions">
            @if (lines().length > 0 && meeting() !== 'running') {
              <label>
                <input
                  type="checkbox"
                  data-test="include-transcript"
                  [checked]="includeTranscript()"
                  (change)="includeTranscript.set($any($event.target).checked)"
                />
                Include the last 10 min in my question
              </label>
            }
            <button type="button" data-test="clear-transcript" (click)="transcript.clear()">
              Clear
            </button>
          </div>
        </section>
      }
      <ol #turnList class="turns" data-test="turns" aria-live="polite">
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
                  <p class="who" data-test="who-user"><span>You</span></p>
                  <p class="asked" data-test="asked" [textContent]="turn.question"></p>
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
        }
      </ol>
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
          [disabled]="listening()"
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
        <button
          type="button"
          data-test="listen"
          [class.recording]="listening()"
          [attr.aria-pressed]="listening()"
          [disabled]="recording()"
          aria-label="Listen and write what you say"
          (click)="toggleListening()"
        >
          {{ listening() ? 'Stop' : 'Listen' }}
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
    </main>
  `,
  styles: [
    REPLY_STYLES,
    `
      :host {
        display: block;
        height: 100vh;
        overflow: hidden;
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
      button,
      select {
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
      .shot {
        display: flex;
        gap: 8px;
        align-items: center;
      }
      .shot button.recording {
        background: #2b1010;
        border-color: #d45a5a;
      }
      .shot button {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        color: #e4e8f0;
        padding: 4px 10px;
        font-size: 13px;
        cursor: pointer;
      }
      .shot select {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        color: #e4e8f0;
        padding: 4px 6px;
        font-size: 13px;
      }
      .shot .preview {
        height: 48px;
        max-width: 120px;
        object-fit: contain;
        border: 1px solid #252b3b;
        border-radius: 4px;
      }
      .ask button.close {
        font-size: 20px;
        line-height: 1;
      }
      .ask button.recording {
        background: #2b1010;
        border-color: #d45a5a;
      }
      .transcript {
        display: flex;
        flex-direction: column;
        gap: 4px;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 6px 10px;
        font-size: 13px;
      }
      .transcript .lines {
        max-height: 96px;
        overflow-y: auto;
        list-style: none;
        margin: 0;
        padding: 0;
        white-space: pre-wrap;
      }
      .transcript .label {
        color: #7aa2f7;
      }
      .transcript .partial {
        color: #7b8299;
      }
      .transcript .translation {
        display: block;
        padding-left: 12px;
        color: #8a93a6;
      }
      .transcript .translation.gap {
        font-style: italic;
        color: #6b7184;
      }
      .transcript-actions {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 8px;
        color: #7b8299;
      }
      .transcript-actions button {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        color: #e4e8f0;
        padding: 2px 10px;
        cursor: pointer;
      }
      .meeting-stop {
        display: flex;
        gap: 8px;
        align-items: center;
        font-size: 13px;
      }
      .meeting-stop input[type='text'] {
        flex: 1;
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 6px;
        color: #e4e8f0;
        padding: 4px 8px;
      }
      .meeting-stop button {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        color: #e4e8f0;
        padding: 4px 10px;
        cursor: pointer;
      }
      .screen-text {
        display: flex;
        flex-direction: column;
        gap: 4px;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 6px 10px;
        font-size: 13px;
      }
      .screen-text textarea {
        -webkit-app-region: no-drag;
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 6px;
        color: #e4e8f0;
        font: inherit;
        resize: vertical;
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
        gap: 14px;
      }
      .asked {
        margin: 0;
        max-width: 80%;
        width: fit-content;
        background: #1e2e45;
        border-radius: 10px 2px 10px 10px;
        padding: 6px 10px;
        white-space: pre-wrap;
        font-size: 14px;
      }
      .hint {
        margin: 0;
        font-size: 13px;
        color: #7b8299;
      }
      .hint.live {
        color: #d45a5a;
      }
      .error {
        margin: 0;
        font-size: 13px;
        color: #d45a5a;
      }
    `,
  ],
})
export class QuickAskComponent {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly conversation = inject(LocalConversation);
  private readonly llmConfig = inject(LlmConfigStore);
  private readonly recorder = inject(WavRecorder);
  protected readonly live = inject(LiveListener);
  protected readonly transcript = inject(TranscriptStore);
  private readonly input = viewChild<ElementRef<HTMLInputElement>>('questionInput');
  private readonly turnList = viewChild<ElementRef<HTMLElement>>('turnList');
  private readonly transcriptList = viewChild<ElementRef<HTMLElement>>('transcriptList');

  readonly turns = this.conversation.turns;
  readonly question = signal('');
  readonly recording = signal(false);
  readonly error = signal<string | null>(null);
  readonly listening = this.live.listening;
  readonly othersListening = this.live.othersListening;
  readonly lines = this.transcript.lines;
  readonly includeTranscript = signal(false);
  readonly screenshot = signal<Screenshot | null>(null);
  readonly capturing = signal(false);
  readonly captureError = signal<string | null>(null);
  readonly reading = signal(false);
  readonly readTextError = signal<string | null>(null);
  readonly screenText = signal<string | null>(null);
  readonly translation = signal<LocalTranslationMode>(storedTranslationMode());
  readonly disclosure = computed(() => {
    const provider = this.llmConfig.provider();
    if (provider === 'None') return NO_AI_SOURCE_DISCLOSURE;
    if (provider === null && this.llmConfig.readFailed()) {
      const mirrored = this.llmConfig.mirroredProvider();
      return mirrored === null
        ? 'Each line goes to the connected AI Source for translation.'
        : `Each line goes to ${providerLabel(mirrored)} for translation.`;
    }
    return `Each line goes to ${providerLabel(provider ?? 'Ollama')} for translation.`;
  });
  readonly meeting = signal<'off' | 'running' | 'stopping'>('off');
  readonly meetingWindow = signal<MeetingWindow>(storedMeetingWindow());
  readonly keepMeeting = signal(false);
  readonly meetingTitle = signal('');
  readonly keeping = signal(false);
  readonly meetingError = signal<string | null>(null);
  readonly meetingKept = signal(false);
  private readonly meetingStartedAt = signal(0);
  private readonly now = signal(0);
  readonly clock = computed(() => {
    const seconds = Math.max(0, Math.floor((this.now() - this.meetingStartedAt()) / 1000));
    const pad = (n: number): string => String(n).padStart(2, '0');
    return `${pad(Math.floor(seconds / 60))}:${pad(seconds % 60)}`;
  });
  private meetingEndedAt = 0;
  private clockTimer: ReturnType<typeof setInterval> | null = null;
  private readonly turnsBeforeMeeting = new Set<string>();
  private readonly meetingTurnTimes = new Map<string, number>();
  protected readonly meetingWindows = MEETING_WINDOWS;
  protected readonly meetingPrompts = MEETING_PROMPTS;
  protected readonly maxScreenText = MAX_SCREEN_TEXT_CHARS;
  protected readonly captureStatus = CAPTURE_STATUS;
  protected readonly systemAudioUnavailable = SYSTEM_AUDIO_UNAVAILABLE;

  constructor() {
    this.readProvider();
    effect(() => {
      const heard = this.live.text();
      if (untracked(this.meeting) === 'off') this.question.set(heard);
    });
    effect(() => {
      const turns = this.turns();
      if (untracked(this.meeting) !== 'running') return;
      for (const turn of turns) {
        if (
          turn.meeting &&
          !this.turnsBeforeMeeting.has(turn.id) &&
          !this.meetingTurnTimes.has(turn.id)
        ) {
          this.meetingTurnTimes.set(turn.id, Date.now());
        }
      }
    });
    let hadOthers = false;
    effect(() => {
      const hasOthers = this.lines().some((line) => line.lane === 'others');
      if (hasOthers !== hadOthers) this.includeTranscript.set(hasOthers);
      hadOthers = hasOthers;
    });
    afterRenderEffect(() => {
      this.turns();
      const list = this.turnList()?.nativeElement;
      if (list !== undefined) list.scrollTop = list.scrollHeight;
    });
    afterRenderEffect(() => {
      this.lines();
      const list = this.transcriptList()?.nativeElement;
      if (list !== undefined) list.scrollTop = list.scrollHeight;
    });
    inject(DestroyRef).onDestroy(() => {
      this.stopClock();
      if (this.recording()) void this.recorder.stop();
      void this.live.stopAll();
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

  /** Reads the AI Source in use from the vault, as the window opens and each time it gains focus, for the disclosure line. */
  readProvider(): void {
    void this.llmConfig.loadFromVault();
  }

  /**
   * Throws away any recording in progress, stops every listening lane and hides the window; live text stays.
   * During a meeting it stops the meeting instead and shows the row to keep it, leaving the window up.
   */
  close(): void {
    this.discardRecording();
    if (this.meeting() === 'running') {
      this.stopMeeting();
      return;
    }
    void this.live.stopAll();
    void this.bridge.localHide();
  }

  /** Opens the microphone and, where this platform can, the system audio, and starts the meeting clock. */
  async startMeeting(): Promise<void> {
    if (this.meeting() !== 'off' || this.recording()) return;
    const startedAt = Date.now();
    this.turnsBeforeMeeting.clear();
    this.turns().forEach((turn) => this.turnsBeforeMeeting.add(turn.id));
    this.meetingTurnTimes.clear();
    this.meetingKept.set(false);
    this.meetingError.set(null);
    this.meetingStartedAt.set(startedAt);
    this.now.set(startedAt);
    this.meeting.set('running');
    this.clockTimer = setInterval(() => this.now.set(Date.now()), 1000);
    this.error.set(null);
    await Promise.all([
      this.live.listen('me', this.translation()),
      this.live.systemAudioAvailable
        ? this.live.listen('others', this.translation())
        : Promise.resolve(),
    ]);
  }

  /** Ends both lanes and the clock, and shows the row where the owner may keep the meeting. */
  stopMeeting(): void {
    if (this.meeting() !== 'running') return;
    this.meetingEndedAt = Date.now();
    this.stopClock();
    this.now.set(this.meetingEndedAt);
    void this.live.stopAll();
    this.live.clear();
    this.keepMeeting.set(false);
    this.meetingTitle.set(
      `Meeting ${localIso(this.meetingStartedAt()).slice(0, 16).replace('T', ' ')}`,
    );
    this.meeting.set('stopping');
  }

  /** Keeps the meeting on this machine when the box is on, then ends the meeting; with the box off nothing is kept. */
  async doneMeeting(): Promise<void> {
    if (this.meeting() !== 'stopping' || this.keeping()) return;
    if (!this.keepMeeting()) {
      this.meeting.set('off');
      return;
    }
    this.keeping.set(true);
    this.meetingError.set(null);
    try {
      await this.bridge.localKeepMeeting({ meeting: this.meetingRecord() });
      this.meeting.set('off');
      this.meetingKept.set(true);
    } catch (err: unknown) {
      this.meetingError.set(readIpcError(err).message);
    } finally {
      this.keeping.set(false);
    }
  }

  /** Remembers the transcript window a meeting question carries, on this machine. */
  setMeetingWindow(minutes: number): void {
    const chosen = MEETING_WINDOWS.find((window) => window === minutes) ?? 10;
    this.meetingWindow.set(chosen);
    try {
      localStorage.setItem(MEETING_WINDOW_STORAGE_KEY, String(chosen));
    } catch {
      return;
    }
  }

  /** Puts a quick prompt in the question box without sending it. */
  fillQuestion(prompt: string): void {
    this.question.set(prompt);
    this.input()?.nativeElement.focus();
  }

  private stopClock(): void {
    if (this.clockTimer !== null) clearInterval(this.clockTimer);
    this.clockTimer = null;
  }

  private meetingRecord(): LocalMeeting {
    const startedAt = this.meetingStartedAt();
    return {
      startedAt: localIso(startedAt),
      endedAt: localIso(this.meetingEndedAt),
      title: this.meetingTitle().trim(),
      lines: this.lines()
        .filter((line) => line.at >= startedAt)
        .map((line) => ({
          at: localIso(line.at),
          lane: line.lane,
          text: line.text,
          ...(line.translation?.text !== undefined
            ? { translation: line.translation.text, language: line.translation.language }
            : {}),
        })),
      turns: this.turns().flatMap((turn) => {
        const at = this.meetingTurnTimes.get(turn.id);
        return at === undefined
          ? []
          : [
              {
                at: localIso(at),
                question: turn.question,
                answer: turn.reply,
                screenshot: turn.screenshot === true,
                transcript: turn.transcript === true,
                screenText: turn.screenText === true,
              },
            ];
      }),
    };
  }

  private lastAnswer(): string {
    const answered = this.turns().filter(
      (turn) =>
        this.meetingTurnTimes.has(turn.id) && turn.state === 'done' && turn.reply.length > 0,
    );
    return (answered.at(-1)?.reply ?? '').slice(0, MAX_LAST_ANSWER_CHARS);
  }

  toggleOthers(): void {
    if (this.othersListening()) {
      void this.live.stop('others');
      return;
    }
    this.error.set(null);
    void this.live.listen('others', this.translation());
  }

  /** Keeps the translation mode for the next listen on either lane, and on this machine for the next window. */
  setTranslation(mode: LocalTranslationMode): void {
    this.translation.set(mode);
    try {
      localStorage.setItem(TRANSLATION_STORAGE_KEY, mode);
    } catch {
      return;
    }
  }

  stopAll(): void {
    void this.live.stopAll();
  }

  toggleListening(): void {
    if (this.listening()) {
      void this.live.stop();
      return;
    }
    if (this.recording()) return;
    this.error.set(null);
    void this.live.listen('me', this.translation());
  }

  /** Stops push-to-talk and throws the recording away, as on Escape or when the window loses focus. */
  discardRecording(): void {
    if (!this.recording()) return;
    this.recording.set(false);
    void this.recorder.stop();
  }

  async ask(): Promise<void> {
    const question = this.question().trim();
    if (question.length === 0) return;
    const inMeeting = this.meeting() === 'running';
    const minutes = inMeeting ? this.meetingWindow() : 10;
    const heard = inMeeting || this.includeTranscript() ? this.transcript.window(minutes) : '';
    const transcriptBlock = heard.length > 0 ? `${transcriptMarker(minutes)}${heard}` : '';
    const block = this.screenText();
    const read = (block ?? '').trim();
    const screenBlock = read.length > 0 ? `${SCREEN_TEXT_MARKER}${read}` : '';
    const last = inMeeting ? this.lastAnswer() : '';
    const text = inMeeting
      ? question +
        (last.length > 0 ? `${LAST_ANSWER_MARKER}${last}` : '') +
        screenBlock +
        transcriptBlock
      : question + transcriptBlock + screenBlock;
    this.question.set('');
    this.live.clear();
    this.error.set(null);
    this.conversation.stop();
    const refusal = await this.conversation.ask(text, this.takeScreenshot(), inMeeting);
    this.error.set(refusal);
    if (refusal === null && this.screenText() === block) this.screenText.set(null);
  }

  /** Asks the worker for the text on the attached screenshot and puts it in the editable block. */
  async readText(): Promise<void> {
    const shot = this.screenshot();
    if (shot === null || this.reading()) return;
    this.readTextError.set(null);
    this.reading.set(true);
    try {
      const { text } = await this.bridge.localReadText({
        image: { contentType: 'image/jpeg', base64: shot.base64 },
      });
      this.screenText.set(text.slice(0, MAX_SCREEN_TEXT_CHARS));
    } catch (err: unknown) {
      this.readTextError.set(readIpcError(err).message);
    } finally {
      this.reading.set(false);
    }
  }

  /** Asks the system for one frame of a screen or window; a cancelled picker attaches nothing. */
  async attachScreenshot(): Promise<void> {
    if (this.capturing()) return;
    this.captureError.set(null);
    this.capturing.set(true);
    try {
      const shot = await grabScreenshot();
      if (shot !== null) this.screenshot.set(shot);
    } catch {
      this.captureError.set(CAPTURE_FAILED);
    } finally {
      this.capturing.set(false);
    }
  }

  private takeScreenshot(): LocalImage | undefined {
    const shot = this.screenshot();
    this.screenshot.set(null);
    return shot !== null ? { contentType: 'image/jpeg', base64: shot.base64 } : undefined;
  }

  async startRecording(): Promise<void> {
    if (this.recording() || this.listening()) return;
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
    this.error.set(await this.conversation.askByVoice(wav, this.takeScreenshot()));
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
