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
  LOCAL_CANCELLED_REPLY,
  MEETING_WINDOWS,
  SCREEN_TEXT_MARKER,
  readIpcError,
  transcriptMarker,
  type LocalImage,
  type LocalMeeting,
  type LocalTranslationMode,
} from '@scout/shared';

import { LlmConfigStore, providerLabel } from '../core/llm-config.store';
import { LocalConversation, POSTING_CANNOT_STOP } from '../core/local-conversation';
import { MarkdownPipe } from '../core/markdown.pipe';
import { replyLinkTarget } from '../core/reply-links';
import { REPLY_STYLES } from '../core/reply-styles';
import { ReviewFindingsComponent, ReviewFindingsPipe } from '../core/review-findings.component';
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
 * The box the global shortcut opens: a slim header that drags the window, a tools tray closed by
 * default, the conversation as the one scroll, and the question line at the bottom with the
 * microphone inside it. Type a question and press Enter, or hold Space (with the box empty and the
 * focus in it or nowhere) or the microphone to talk. One question at a time: while Scout answers,
 * the box, the microphone and Space are off and Send becomes Stop, which ends the answer and keeps
 * what already arrived, marked Interrupted. The tray holds Attach screenshot and Read text, Listen
 * (to the owner), Others (the system audio, shown as "Listening to system audio" and filling the
 * transcript panel), Meeting and the translation select; it stays open while a lane listens or a
 * meeting runs. A screenshot shows as a chip above the box and goes with the next question; Read
 * text fills an editable block the next typed question carries as data, as does the transcript's
 * last 10 minutes once Others said a line. Escape from the box, or Close, stops every lane and the
 * microphone and hides the window; inside another field Escape only leaves that field. Meeting
 * opens both lanes with a clock; a question then carries the last answer, the screen text and the
 * transcript of the chosen window as data, and Stop meeting (or Escape and Close) shows the row
 * where the owner may keep the meeting on this machine, which is off by default.
 */
@Component({
  selector: 'sc-quick-ask',
  standalone: true,
  imports: [MarkdownPipe, ReviewFindingsComponent, ReviewFindingsPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(window:keydown)': 'onKeyDown($event)',
    '(window:keyup)': 'onKeyUp($event)',
    '(window:blur)': 'discardRecording()',
    '(window:focus)': 'onWindowFocus()',
  },
  template: `
    <main class="quick-ask">
      <header class="head" data-test="grip">
        <span class="mark" aria-hidden="true"
          ><svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
          >
            <rect x="4" y="8" width="16" height="12" rx="3"></rect>
            <circle cx="9" cy="14" r="1.2" fill="currentColor" stroke="none"></circle>
            <circle cx="15" cy="14" r="1.2" fill="currentColor" stroke="none"></circle>
            <path d="M12 8V4M9 4h6"></path></svg
        ></span>
        <b>Scout</b>
        <span class="state" data-test="status">
          <i class="dot" [class.live]="busy()" aria-hidden="true"></i
          >{{ busy() ? 'Answering…' : source() }}
        </span>
        <span class="spacer"></span>
        <button
          type="button"
          class="ib"
          data-test="tools-toggle"
          aria-label="Tools"
          aria-controls="tools-tray"
          title="Tools: screen, listen, meeting, translation"
          [attr.aria-pressed]="toolsOpen()"
          (click)="toolsOpen.set(!toolsOpen())"
        >
          <svg
            viewBox="0 0 16 16"
            fill="none"
            stroke="currentColor"
            stroke-width="1.7"
            stroke-linecap="round"
            aria-hidden="true"
          >
            <path d="M2 4h7M12 4h2M2 8h2M7 8h7M2 12h9"></path>
            <circle cx="10.5" cy="4" r="1.5"></circle>
            <circle cx="5.5" cy="8" r="1.5"></circle>
            <circle cx="12.5" cy="12" r="1.5"></circle>
          </svg>
        </button>
        <button
          type="button"
          class="ib"
          data-test="clear-conversation"
          aria-label="Clear the conversation"
          title="Clear the conversation"
          [disabled]="busy() || turns().length === 0"
          (click)="clearConversation()"
        >
          <svg
            viewBox="0 0 16 16"
            fill="none"
            stroke="currentColor"
            stroke-width="1.7"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <path d="M3 4h10M6 4V2.5h4V4M4.5 4l.7 9.5h5.6l.7-9.5"></path>
          </svg>
        </button>
        <button
          type="button"
          class="ib close"
          data-test="close"
          aria-label="Close"
          title="Close (Esc)"
          (click)="close()"
        >
          <svg
            viewBox="0 0 16 16"
            fill="none"
            stroke="currentColor"
            stroke-width="1.8"
            stroke-linecap="round"
            aria-hidden="true"
          >
            <path d="M4 4l8 8M12 4l-8 8"></path>
          </svg>
        </button>
      </header>
      <div class="tools" data-test="tools">
        <div class="tray" id="tools-tray" data-test="tools-tray" [hidden]="!trayShown()">
          <button
            type="button"
            class="tool"
            data-test="attach-screenshot"
            [disabled]="capturing()"
            (click)="attachScreenshot()"
          >
            <svg
              viewBox="0 0 16 16"
              fill="none"
              stroke="currentColor"
              stroke-width="1.7"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <rect x="2" y="3" width="12" height="9" rx="1.5"></rect>
              <path d="M6 14h4"></path>
            </svg>
            Attach screenshot
          </button>
          <button
            type="button"
            class="tool"
            data-test="read-text"
            [disabled]="screenshot() === null || reading()"
            (click)="readText()"
          >
            {{ reading() ? 'Reading...' : 'Read text' }}
          </button>
          <button
            type="button"
            class="tool"
            data-test="listen"
            [class.recording]="listening()"
            [attr.aria-pressed]="listening()"
            [disabled]="recording()"
            aria-label="Listen and write what you say"
            title="Listen to me"
            (click)="toggleListening()"
          >
            <svg
              viewBox="0 0 16 16"
              fill="none"
              stroke="currentColor"
              stroke-width="1.7"
              stroke-linecap="round"
              aria-hidden="true"
            >
              <path d="M3 6v4M6 4v8M9 5v6M12 7v2"></path>
            </svg>
            {{ listening() ? 'Stop' : 'Listen' }}
          </button>
          <button
            type="button"
            class="tool"
            data-test="listen-others"
            [class.recording]="othersListening()"
            [attr.aria-pressed]="othersListening()"
            [disabled]="!live.systemAudioAvailable"
            aria-label="Listen to the system audio"
            title="Listen to the computer"
            (click)="toggleOthers()"
          >
            <svg
              viewBox="0 0 16 16"
              fill="none"
              stroke="currentColor"
              stroke-width="1.7"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="M2 6h3l4-3v10l-4-3H2z"></path>
              <path d="M12 5.5a3.5 3.5 0 0 1 0 5"></path>
            </svg>
            {{ othersListening() ? 'Stop others' : 'Others' }}
          </button>
          @if (listening() || othersListening()) {
            <button type="button" class="tool" data-test="stop-all" (click)="stopAll()">
              Stop all
            </button>
          }
          @if (meeting() === 'running') {
            <button
              type="button"
              class="tool recording"
              data-test="meeting-stop"
              aria-pressed="true"
              (click)="stopMeeting()"
            >
              Stop meeting
            </button>
          } @else {
            <button
              type="button"
              class="tool"
              data-test="meeting-start"
              aria-pressed="false"
              [disabled]="meeting() === 'stopping' || recording()"
              (click)="startMeeting()"
            >
              <svg
                viewBox="0 0 16 16"
                fill="none"
                stroke="currentColor"
                stroke-width="1.7"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <circle cx="5" cy="6" r="2"></circle>
                <circle cx="11" cy="6" r="2"></circle>
                <path
                  d="M1.5 13c0-2 1.6-3.5 3.5-3.5S8.5 11 8.5 13M7.5 13c0-2 1.6-3.5 3.5-3.5s3.5 1.5 3.5 3.5"
                ></path>
              </svg>
              Meeting
            </button>
          }
          <span class="tool">
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
          </span>
        </div>
        @if (meeting() === 'running') {
          <p class="hint live" data-test="meeting-indicator" role="status">
            Meeting {{ clock() }}{{ live.systemAudioAvailable ? '' : ', microphone only' }}
          </p>
          <div class="row-tools" data-test="meeting-tools">
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
      </div>
      <ol #turnList class="turns" data-test="turns" aria-live="polite" (scroll)="onTurnsScroll()">
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
                  @if (turn.review) {
                    <p class="with-transcript" data-test="with-review">with the review</p>
                  }
                  @if (turn.issues) {
                    <p class="with-transcript" data-test="with-issues">with the issues</p>
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
                @if (findings) {
                  <sc-review-findings [turnId]="turn.id" [review]="findings.review" />
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
        }
      </ol>
      <form class="composer" (submit)="$event.preventDefault(); ask()">
        @if (screenshot(); as shot) {
          <div class="attach">
            <span class="chip">
              <img
                class="preview"
                data-test="screenshot-preview"
                alt="Screenshot to send with the next question"
                [src]="'data:image/jpeg;base64,' + shot.base64"
              />
              Screenshot
              <button
                type="button"
                data-test="remove-screenshot"
                aria-label="Remove the screenshot"
                title="Remove the screenshot"
                (click)="screenshot.set(null)"
              >
                ✕
              </button>
            </span>
          </div>
        }
        <div class="line">
          <div class="field" data-test="field">
            <textarea
              #questionInput
              rows="1"
              data-test="question"
              aria-label="Ask Scout"
              maxlength="4000"
              [placeholder]="busy() ? 'Waiting for Scout to answer…' : 'Ask Scout'"
              [disabled]="busy()"
              [value]="question()"
              (input)="question.set($any($event.target).value)"
              (keydown.enter)="onEnter($any($event))"
            ></textarea>
            <button
              type="button"
              class="ib in"
              data-test="mic"
              [class.recording]="recording()"
              [attr.aria-pressed]="recording()"
              [disabled]="listening() || busy()"
              [attr.aria-label]="recording() ? 'Listening' : 'Hold to talk'"
              [title]="recording() ? 'Listening' : 'Hold to talk, or hold Space with the box empty'"
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
          @if (busy()) {
            <button
              type="button"
              class="send stop"
              data-test="stop"
              aria-label="Stop the answer"
              [disabled]="posting()"
              [title]="posting() ? postingCannotStop : 'Stop the answer'"
              (click)="stop()"
            >
              <svg viewBox="0 0 16 16" aria-hidden="true">
                <rect x="4" y="4" width="8" height="8" rx="1.5" fill="currentColor"></rect>
              </svg>
            </button>
          } @else {
            <button type="submit" class="send" data-test="send" aria-label="Send" title="Send">
              <svg
                viewBox="0 0 16 16"
                fill="none"
                stroke="currentColor"
                stroke-width="1.9"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path d="M14 2L2 7l5 2 2 5z"></path>
                <path d="M14 2L7 9"></path>
              </svg>
            </button>
          }
        </div>
        <p class="hint" data-test="composer-hint">
          {{
            busy()
              ? 'One question at a time · Stop ends the answer'
              : 'Enter sends · Shift+Enter for a new line · Esc closes'
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
        position: fixed;
        inset: 0;
        overflow: hidden;
        background: var(--sc-bg, #0f1623);
        color: var(--sc-fg, #e6e9f0);
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
        font-size: 14px;
        line-height: 1.45;
      }
      .quick-ask {
        display: flex;
        flex-direction: column;
        height: 100%;
        box-sizing: border-box;
      }
      .head {
        -webkit-app-region: drag;
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 6px 8px 6px 12px;
        border-bottom: 1px solid var(--sc-line, #253249);
        background: var(--sc-surface, #131c2d);
        flex: none;
        user-select: none;
        cursor: grab;
      }
      .mark {
        width: 22px;
        height: 22px;
        border-radius: 6px;
        background: var(--sc-accent-strong, #3b6ff2);
        color: #fff;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        flex: none;
      }
      .mark svg {
        width: 14px;
        height: 14px;
      }
      .head b {
        font-weight: 600;
        font-size: 13px;
      }
      .state {
        font-size: 12px;
        color: var(--sc-muted, #8a93a6);
        display: inline-flex;
        gap: 6px;
        align-items: center;
        min-width: 0;
        overflow: hidden;
        white-space: nowrap;
        text-overflow: ellipsis;
      }
      .dot {
        width: 7px;
        height: 7px;
        border-radius: 50%;
        background: var(--sc-ok, #2f7a57);
        display: inline-block;
        flex: none;
      }
      .dot.live {
        background: var(--sc-error, #c0504f);
        animation: pulse 1.2s infinite;
      }
      @keyframes pulse {
        50% {
          opacity: 0.35;
        }
      }
      .spacer {
        flex: 1 1 auto;
      }
      input,
      button,
      select,
      textarea {
        -webkit-app-region: no-drag;
      }
      .ib {
        width: 32px;
        height: 32px;
        border-radius: var(--sc-radius-sm, 8px);
        border: 1px solid transparent;
        background: transparent;
        color: var(--sc-fg-2, #c3cadb);
        display: inline-flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        flex: none;
        padding: 0;
      }
      .ib svg {
        width: 16px;
        height: 16px;
      }
      .ib:hover {
        background: var(--sc-surface-2, #1a2436);
      }
      .ib[aria-pressed='true'] {
        background: #1e2e45;
        color: var(--sc-accent, #7aa2f7);
        border-color: var(--sc-line, #253249);
      }
      .ib:disabled {
        opacity: 0.4;
        cursor: default;
      }
      .ib:focus-visible,
      .send:focus-visible,
      .tool:focus-visible,
      .field textarea:focus-visible {
        outline: 2px solid var(--sc-accent, #7aa2f7);
        outline-offset: 1px;
      }
      .tools {
        flex: 0 1 auto;
        min-height: 0;
        max-height: 40%;
        overflow-y: auto;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .tools > :not(.tray) {
        margin: 0 12px;
      }
      .tools > :not(.tray):first-child,
      .tray + * {
        margin-top: 8px;
      }
      .tray {
        display: flex;
        gap: 6px;
        flex-wrap: wrap;
        align-items: center;
        padding: 8px 12px;
        border-bottom: 1px solid var(--sc-line, #253249);
        background: var(--sc-surface, #131c2d);
      }
      .tray[hidden] {
        display: none;
      }
      .tool {
        font: inherit;
        font-size: 12.5px;
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 5px 10px;
        border-radius: 999px;
        border: 1px solid var(--sc-line, #253249);
        background: var(--sc-surface-2, #1a2436);
        color: var(--sc-fg-2, #c3cadb);
        cursor: pointer;
        min-height: 30px;
        box-sizing: border-box;
      }
      .tool svg {
        width: 14px;
        height: 14px;
      }
      .tool[aria-pressed='true'] {
        background: #1e2e45;
        color: var(--sc-accent, #7aa2f7);
        border-color: var(--sc-accent-strong, #3b6ff2);
      }
      .tool.recording {
        background: #2b1010;
        border-color: var(--sc-error, #c0504f);
        color: var(--sc-fg, #e6e9f0);
      }
      .tool:disabled {
        opacity: 0.45;
        cursor: default;
      }
      .tool select {
        font: inherit;
        font-size: 12.5px;
        background: transparent;
        color: inherit;
        border: 0;
      }
      .row-tools {
        display: flex;
        gap: 6px;
        flex-wrap: wrap;
        align-items: center;
      }
      .row-tools button,
      .row-tools select {
        background: var(--sc-surface-2, #1a2436);
        border: 1px solid var(--sc-line, #253249);
        border-radius: var(--sc-radius-sm, 8px);
        color: var(--sc-fg, #e6e9f0);
        padding: 4px 10px;
        font-size: 13px;
        cursor: pointer;
      }
      .composer {
        flex: none;
        border-top: 1px solid var(--sc-line, #253249);
        background: var(--sc-surface, #131c2d);
        padding: 8px;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .attach {
        display: flex;
        gap: 6px;
        flex-wrap: wrap;
      }
      .chip {
        font-size: 12px;
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 3px 4px 3px 4px;
        border-radius: 999px;
        background: var(--sc-surface-2, #1a2436);
        border: 1px solid var(--sc-line, #253249);
        color: var(--sc-fg-2, #c3cadb);
      }
      .chip .preview {
        height: 22px;
        max-width: 40px;
        object-fit: cover;
        border-radius: 999px;
      }
      .chip button {
        width: 20px;
        height: 20px;
        border: 0;
        background: transparent;
        color: var(--sc-muted, #8a93a6);
        cursor: pointer;
        border-radius: 50%;
        padding: 0;
      }
      .line {
        display: flex;
        gap: 6px;
        align-items: flex-end;
      }
      .field {
        position: relative;
        flex: 1 1 auto;
        min-width: 0;
        display: flex;
      }
      .field textarea {
        flex: 1 1 auto;
        min-width: 0;
        resize: none;
        field-sizing: content;
        max-height: calc(6lh + 16px);
        overflow-y: auto;
        font: inherit;
        font-size: 14px;
        line-height: 1.4;
        padding: 8px 42px 8px 10px;
        border-radius: 10px;
        border: 1px solid var(--sc-line, #253249);
        background: var(--sc-bg, #0f1623);
        color: var(--sc-fg, #e6e9f0);
        min-height: 38px;
        box-sizing: border-box;
      }
      .field textarea:disabled {
        opacity: 0.6;
      }
      .ib.in {
        position: absolute;
        right: 4px;
        bottom: 3px;
      }
      .ib.in.recording {
        background: #2b1010;
        color: var(--sc-error, #c0504f);
      }
      .send {
        width: 38px;
        height: 38px;
        border-radius: 10px;
        border: 0;
        background: var(--sc-accent-strong, #3b6ff2);
        color: #fff;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        flex: none;
        padding: 0;
      }
      .send svg {
        width: 17px;
        height: 17px;
      }
      .send.stop {
        background: var(--sc-error, #c0504f);
      }
      .transcript {
        display: flex;
        flex-direction: column;
        gap: 4px;
        border: 1px solid var(--sc-line, #253249);
        border-radius: var(--sc-radius-sm, 8px);
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
        color: var(--sc-accent, #7aa2f7);
      }
      .transcript .partial {
        color: var(--sc-muted, #8a93a6);
      }
      .transcript .translation {
        display: block;
        padding-left: 12px;
        color: var(--sc-muted, #8a93a6);
      }
      .transcript .translation.gap {
        font-style: italic;
      }
      .transcript-actions {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 8px;
        color: var(--sc-muted, #8a93a6);
      }
      .transcript-actions button,
      .meeting-stop button {
        background: var(--sc-surface-2, #1a2436);
        border: 1px solid var(--sc-line, #253249);
        border-radius: var(--sc-radius-sm, 8px);
        color: var(--sc-fg, #e6e9f0);
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
        background: var(--sc-surface-2, #1a2436);
        border: 1px solid var(--sc-line, #253249);
        border-radius: 6px;
        color: var(--sc-fg, #e6e9f0);
        padding: 4px 8px;
      }
      .screen-text {
        display: flex;
        flex-direction: column;
        gap: 4px;
        border: 1px solid var(--sc-line, #253249);
        border-radius: var(--sc-radius-sm, 8px);
        padding: 6px 10px;
        font-size: 13px;
      }
      .screen-text textarea {
        background: var(--sc-surface-2, #1a2436);
        border: 1px solid var(--sc-line, #253249);
        border-radius: 6px;
        color: var(--sc-fg, #e6e9f0);
        font: inherit;
        resize: vertical;
      }
      .turns {
        flex: 1 1 0;
        min-height: 0;
        overflow-y: auto;
        list-style: none;
        margin: 0;
        padding: 12px;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .asked {
        margin: 0;
        max-width: 78%;
        width: fit-content;
        background: #1e2e45;
        border-radius: 12px 2px 12px 12px;
        padding: 8px 12px;
        white-space: pre-wrap;
        font-size: 14px;
      }
      .failure {
        margin: 0;
        font-size: 12px;
        font-style: italic;
        color: var(--sc-muted, #8a93a6);
      }
      .hint {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted, #8a93a6);
      }
      .composer .hint {
        padding: 0 4px;
      }
      .hint.live {
        color: var(--sc-error, #c0504f);
      }
      .error {
        margin: 0;
        font-size: 13px;
        color: var(--sc-error, #c0504f);
      }
      @media (prefers-reduced-motion: reduce) {
        .dot.live {
          animation: none;
        }
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
  private readonly input = viewChild<ElementRef<HTMLTextAreaElement>>('questionInput');
  private readonly turnList = viewChild<ElementRef<HTMLElement>>('turnList');
  private readonly transcriptList = viewChild<ElementRef<HTMLElement>>('transcriptList');

  readonly turns = this.conversation.turns;
  readonly sending = signal(false);
  /** True while a question is on its way to main or any turn is being answered: one question at a time. */
  readonly busy = computed(() => this.sending() || this.conversation.running() !== null);
  readonly toolsOpen = signal(false);
  /** The tray shows when the owner opened it, and stays open while a lane listens or a meeting runs so its Stop stays in reach. */
  readonly trayShown = computed(
    () =>
      this.toolsOpen() ||
      this.live.listening() ||
      this.live.othersListening() ||
      this.meeting() === 'running',
  );
  readonly source = computed(() => {
    const provider = this.llmConfig.provider();
    if (provider === 'None') return 'No AI Source';
    if (provider === null) return 'Ready';
    const model = this.llmConfig.model();
    return model === null ? providerLabel(provider) : `${providerLabel(provider)} · ${model}`;
  });
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
  protected readonly cancelledReply = LOCAL_CANCELLED_REPLY;
  protected readonly postingCannotStop = POSTING_CANNOT_STOP;
  readonly posting = this.conversation.posting;
  private asks = 0;
  private atBottom = true;
  private follow = false;

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
      if (list === undefined || !(this.atBottom || this.follow)) return;
      this.follow = false;
      list.scrollTop = list.scrollHeight;
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
    const target = event.target;
    if (target instanceof HTMLElement && target !== this.input()?.nativeElement) {
      if (target.matches('input, textarea, select')) {
        if (event.key === 'Escape') target.blur();
        return;
      }
      if (target.matches('button') && event.key !== 'Escape') return;
    }
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
    if (event.repeat || this.question().length > 0 || this.busy()) return;
    event.preventDefault();
    void this.startRecording();
  }

  /** Enter alone sends the question; the binding leaves Shift+Enter to the textarea, which keeps the newline. */
  onEnter(event: KeyboardEvent): void {
    event.preventDefault();
    void this.ask();
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

  /** Each time the window is shown or focused again: the AI Source is read again and the question box takes the focus. */
  onWindowFocus(): void {
    this.readProvider();
    this.input()?.nativeElement.focus();
  }

  /** Remembers whether the reader is within 48 px of the newest turn, so a new turn only scrolls a list left at the bottom. */
  onTurnsScroll(): void {
    const list = this.turnList()?.nativeElement;
    if (list !== undefined) {
      this.atBottom = list.scrollHeight - list.scrollTop - list.clientHeight <= 48;
    }
  }

  /** Empties the conversation in every window; main refuses while a turn runs. */
  clearConversation(): void {
    if (!this.busy()) this.conversation.clear();
  }

  /** Stops the answer being written; what already arrived stays on the turn. */
  stop(): void {
    this.conversation.stop();
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
    if (question.length === 0 || this.busy()) return;
    const inMeeting = this.meeting() === 'running';
    const minutes = inMeeting ? this.meetingWindow() : 10;
    const heard = inMeeting || this.includeTranscript() ? this.transcript.window(minutes) : '';
    const transcriptBlock = heard.length > 0 ? `${transcriptMarker(minutes)}${heard}` : '';
    const block = this.screenText();
    const read = (block ?? '').trim();
    const screenBlock = read.length > 0 ? `${SCREEN_TEXT_MARKER}${read}` : '';
    const last = inMeeting ? this.lastAnswer() : '';
    const context = inMeeting
      ? (last.length > 0 ? `${LAST_ANSWER_MARKER}${last}` : '') + screenBlock + transcriptBlock
      : transcriptBlock + screenBlock;
    const shot = this.screenshot();
    const mine = ++this.asks;
    this.error.set(null);
    this.sending.set(true);
    this.follow = true;
    let refusal: string | null;
    try {
      refusal = await this.conversation.ask(question, this.image(shot), inMeeting, context);
    } finally {
      this.sending.set(false);
    }
    if (mine === this.asks) this.error.set(refusal);
    if (refusal !== null) return;
    this.question.set('');
    this.live.clear();
    if (this.screenshot() === shot) this.screenshot.set(null);
    if (this.screenText() === block) this.screenText.set(null);
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

  private image(shot: Screenshot | null): LocalImage | undefined {
    return shot !== null ? { contentType: 'image/jpeg', base64: shot.base64 } : undefined;
  }

  async startRecording(): Promise<void> {
    if (this.recording() || this.listening() || this.busy()) return;
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
    const shot = this.screenshot();
    const mine = ++this.asks;
    this.error.set(null);
    this.follow = true;
    const refusal = await this.conversation.askByVoice(wav, this.image(shot));
    if (mine === this.asks) this.error.set(refusal);
    if (refusal === null && this.screenshot() === shot) this.screenshot.set(null);
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
