import { DatePipe } from '@angular/common';
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
import { MatButtonModule } from '@angular/material/button';

import {
  LAST_ANSWER_MARKER,
  LOCAL_CANCELLED_REPLY,
  MEETING_WINDOWS,
  SCREEN_TEXT_MARKER,
  readIpcError,
  transcriptMarker,
  type LocalImage,
  type LocalListenLane,
  type LocalMeeting,
  type LocalTranslationMode,
} from '@scout/shared';

import { ApprovalStripComponent } from '../core/approval-strip.component';
import { IconComponent } from '../core/icon.component';
import { LlmConfigStore, providerLabel } from '../core/llm-config.store';
import { LocalConversation, POSTING_CANNOT_STOP } from '../core/local-conversation';
import { MarkdownPipe } from '../core/markdown.pipe';
import { replyLinkTarget } from '../core/reply-links';
import { REPLY_STYLES } from '../core/reply-styles';
import { ReviewFindingsComponent, ReviewFindingsPipe } from '../core/review-findings.component';
import { ReviewFindingsStore } from '../core/review-findings.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { THEME_OPTIONS, ThemeStore } from '../core/theme.store';
import { VoiceModelAsk } from '../features/voice-model-ask';
import { LiveListener, MEETING_FINISH_WAIT_MS, NO_SPEECH_HEARD } from './live-listener';
import { grabScreenshot, type Screenshot } from './screen-grab';
import { TranscriptStore } from './transcript-store';
import { ConversationMode } from '../speech/conversation-mode';
import { ReadAloud } from '../speech/read-aloud';
import { ReadAloudButtonComponent } from '../speech/read-aloud-button.component';
import { ReadAloudSwitchComponent } from '../speech/read-aloud-switch.component';
import { TalkButtonComponent } from '../speech/talk-button.component';

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
 * the microphone and Space are off and Stop shows beside Send; Stop ends the answer and keeps what
 * already arrived, marked Interrupted, and a new question sent from the box stops the answer and is
 * asked instead. While Scout queues a card's issues for approval the box, Send and Stop are off. The tray holds Attach screenshot and Read text, Listen
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
  imports: [
    ApprovalStripComponent,
    DatePipe,
    IconComponent,
    MarkdownPipe,
    ReviewFindingsComponent,
    MatButtonModule,
    ReviewFindingsPipe,
    ReadAloudButtonComponent,
    ReadAloudSwitchComponent,
    TalkButtonComponent,
  ],
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
            <path d="M12 8V4M9 4h6M9 14h.01M15 14h.01"></path></svg
        ></span>
        <div class="title">
          <b>Scout</b>
          <span class="state" role="status" data-test="status">
            <i
              class="dot"
              [class.live]="busy()"
              [class.off]="!busy() && sourceless()"
              [attr.data-state]="busy() ? 'answering' : sourceless() ? 'none' : 'ready'"
              aria-hidden="true"
            ></i
            >{{ busy() ? (posting() ? 'Queuing cards…' : 'Answering…') : source() }}
          </span>
        </div>
        <div class="theme" role="group" aria-label="Theme">
          @for (option of themeOptions; track option.choice) {
            <button
              type="button"
              class="theme-option"
              [attr.data-test]="'theme-' + option.choice"
              [attr.aria-label]="option.ariaLabel"
              [attr.aria-pressed]="theme.choice() === option.choice"
              [title]="option.ariaLabel"
              (click)="theme.set(option.choice)"
            >
              <sc-icon [name]="option.icon" [size]="14" />
            </button>
          }
        </div>
        <button
          type="button"
          class="ib tools-toggle"
          data-test="tools-toggle"
          aria-label="Tools"
          aria-controls="tools-tray"
          title="Tools: screen, listen, meeting, translation"
          [attr.aria-pressed]="toolsOpen()"
          (click)="toolsOpen.set(!toolsOpen())"
        >
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            aria-hidden="true"
          >
            <path d="M3 6h10M18 6h3M3 12h3M11 12h10M3 18h13"></path>
            <circle cx="15.5" cy="6" r="2.2"></circle>
            <circle cx="8.5" cy="12" r="2.2"></circle>
            <circle cx="18.5" cy="18" r="2.2"></circle>
          </svg>
        </button>
        <button
          type="button"
          class="ib"
          data-test="clear-conversation"
          aria-label="Clear the conversation"
          [title]="posting() ? postingCannotStop : 'Clear the conversation'"
          [disabled]="sending() || posting() || turns().length === 0 || meeting() !== 'off'"
          (click)="clearConversation()"
        >
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"></path>
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
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2.2"
            stroke-linecap="round"
            aria-hidden="true"
          >
            <path d="M6 6l12 12M18 6L6 18"></path>
          </svg>
        </button>
      </header>
      <div class="tools" data-test="tools">
        <div
          class="tray"
          id="tools-tray"
          role="group"
          aria-label="Tools"
          data-test="tools-tray"
          [hidden]="!trayShown()"
        >
          <div class="tool-row">
            <button
              type="button"
              matButton="outlined"
              class="tool"
              data-test="attach-screenshot"
              title="Attach a screenshot of the screen to the next question"
              [disabled]="capturing()"
              (click)="attachScreenshot()"
            >
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path d="M3 5h18v12H3zM8 21h8"></path>
              </svg>
              Attach screenshot
            </button>
            <button
              type="button"
              matButton="outlined"
              class="tool"
              data-test="read-text"
              [title]="
                screenshot() === null
                  ? 'Attach a screenshot first'
                  : 'Read the text on the attached screenshot'
              "
              [disabled]="screenshot() === null || reading()"
              (click)="readText()"
            >
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path d="M4 7V4h16v3M9 20h6M12 4v16"></path>
              </svg>
              {{ reading() ? 'Reading...' : 'Read text' }}
            </button>
            <button
              type="button"
              matButton="outlined"
              class="tool"
              data-test="listen"
              title="Listen and write what you say"
              [class.recording]="listening()"
              [attr.aria-pressed]="listening()"
              [disabled]="recording() || meetingLanesEnding()"
              (click)="toggleListening()"
            >
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path d="M3 10v4M7 7v10M11 5v14M15 8v8M19 10v4"></path>
              </svg>
              {{ listening() ? 'Stop listening' : 'Listen' }}
            </button>
            <button
              type="button"
              matButton="outlined"
              class="tool"
              data-test="listen-others"
              title="Listen to the sound this computer plays"
              [class.recording]="othersListening()"
              [attr.aria-pressed]="othersListening()"
              [disabled]="!live.systemAudioAvailable || meetingLanesEnding()"
              (click)="toggleOthers()"
            >
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path d="M3 9h4l5-4v14l-5-4H3zM16 9a4 4 0 0 1 0 6M19 6a8 8 0 0 1 0 12"></path>
              </svg>
              {{ othersListening() ? 'Stop system audio' : 'System audio' }}
            </button>
            @if (listening() || othersListening()) {
              <button
                type="button"
                matButton="outlined"
                class="tool"
                data-test="stop-all"
                (click)="stopAll()"
              >
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor"></rect>
                </svg>
                Stop all
              </button>
            }
            @if (meeting() === 'running') {
              <button
                type="button"
                matButton="outlined"
                class="tool recording"
                data-test="meeting-stop"
                aria-pressed="true"
                (click)="stopMeeting()"
              >
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor"></rect>
                </svg>
                Stop meeting
              </button>
            } @else {
              <button
                type="button"
                matButton="outlined"
                class="tool"
                data-test="meeting-start"
                aria-pressed="false"
                title="Record a meeting; keep it at the end to see it in Meetings"
                [disabled]="meeting() === 'stopping' || recording()"
                (click)="startMeeting()"
              >
                <svg
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  aria-hidden="true"
                >
                  <path
                    d="M8 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM16 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM2 20c0-3 2.7-5 6-5s6 2 6 5M14 15.5c.6-.3 1.3-.5 2-.5 3.3 0 6 2 6 5"
                  ></path>
                </svg>
                Meeting
              </button>
            }
            <label class="translate"
              >Translate:
              <select
                data-test="translation-mode"
                aria-label="Translate what Scout hears"
                title="Translate what Scout hears, from the next Listen"
                [value]="translation()"
                (change)="setTranslation($any($event.target).value)"
              >
                <option value="off">Off</option>
                <option value="system">To the system language</option>
                <option value="channel">To the channel language</option>
              </select>
            </label>
            <sc-read-aloud-switch />
          </div>
          <p class="hint" data-test="tools-help">
            Read text works on an attached screenshot. System audio is what this computer plays.
          </p>
        </div>
        @if (meeting() === 'running') {
          <p class="hint live" data-test="meeting-indicator" role="status">
            Meeting {{ clock() }}{{ live.systemAudioAvailable ? '' : ', microphone only'
            }}{{ stoppedLanesNotice() }}
          </p>
          @if (stoppedLanes().length > 0) {
            <button type="button" data-test="meeting-resume" (click)="resumeMeeting()">
              Resume
            </button>
          }
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
        @if (listening() && !recording()) {
          <p class="hint live" data-test="listening" role="status">Listening...</p>
        }
        @if (voiceNotice(); as notice) {
          <p class="hint" data-test="voice-notice" role="status">{{ notice }}</p>
        }
        @if (voiceAsk.status(); as status) {
          <p class="hint" data-test="voice-ask-status" role="status">{{ status }}</p>
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
              @if (meeting() === 'off') {
                <button type="button" data-test="clear-transcript" (click)="transcript.clear()">
                  Clear
                </button>
              }
            </div>
          </section>
        }
      </div>
      <ol #turnList class="turns" data-test="turns" aria-live="polite" (scroll)="onTurnsScroll()">
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
                aria-hidden="true"
              >
                <rect x="4" y="8" width="16" height="12" rx="3"></rect>
                <path d="M12 8V4M9 4h6M9 14h.01M15 14h.01"></path></svg
            ></span>
            @if (noSource()) {
              <p data-test="empty-text">
                Connect an AI Source in Scout, under AI Sources, so Scout can answer.
              </p>
            } @else {
              <p data-test="empty-text">Ask anything. Hold Space or the mic to talk.</p>
              <button
                type="button"
                matButton="outlined"
                class="suggestion"
                data-test="empty-suggestion"
                (click)="fillQuestion(suggestion)"
              >
                <svg
                  viewBox="0 0 24 24"
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
            }
          </li>
        }
        @for (turn of turns(); track turn.id) {
          <li class="turn">
            @if (!turn.notice) {
              <div class="row user">
                <p class="who" data-test="who-user"><span>You</span></p>
                <p class="question" data-test="asked">
                  @if (turn.voice) {
                    <sc-icon name="mic" [size]="14" data-test="asked-voice" aria-label="Spoken" />
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
            }
            <div class="row scout">
              <p class="who" data-test="who-scout">
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
                    <path d="M12 8V4M9 4h6"></path></svg
                ></span>
                <span class="name">Scout</span>
                @if (turn.state !== 'running' && turn.askedAt; as at) {
                  <span data-test="reply-time">{{ at | date: 'HH:mm' }}</span>
                }
                @if (turn.reply.length > 0) {
                  <sc-read-aloud-button class="speak" [turnId]="turn.id" />
                }
              </p>
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
              @if (findingCards().get(turn.id); as cards) {
                @if (cards.moved) {
                  <p class="moved-below" data-test="moved-below">
                    Its findings are in the cards below.
                  </p>
                } @else {
                  <sc-review-findings [turnId]="cards.anchor" [review]="cards.review" />
                }
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
      </ol>
      <sc-approval-strip class="approvals" />
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
                <svg
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2.4"
                  stroke-linecap="round"
                  aria-hidden="true"
                >
                  <path d="M6 6l12 12M18 6L6 18"></path>
                </svg>
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
              [placeholder]="
                noSource()
                  ? 'Connect an AI Source first'
                  : posting()
                    ? 'Waiting for Scout to queue…'
                    : busy()
                      ? 'Ask something else to stop this answer'
                      : 'Ask Scout'
              "
              [disabled]="posting() || noSource()"
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
              [disabled]="(listening() && !recording()) || busy() || noSource()"
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
          <sc-talk-button
            [disabled]="listening() || recording() || noSource()"
            [offReason]="noSource() ? 'Connect an AI Source first' : null"
          />
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
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor"></rect>
              </svg>
            </button>
          }
          <button
            type="submit"
            class="send"
            data-test="send"
            aria-label="Send"
            [title]="posting() ? postingCannotStop : 'Send'"
            [disabled]="posting() || noSource()"
          >
            <svg
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
        @if (recording()) {
          <p class="hint speaking" data-test="recording" role="status">
            <sc-icon name="mic" [size]="14" class="pulse" data-test="speaking" aria-hidden="true" />
            Listening. Release to send.
          </p>
        } @else {
          <p class="hint" data-test="composer-hint">
            {{
              posting()
                ? postingCannotStop
                : busy()
                  ? 'A new question stops this answer · Stop ends it'
                  : 'Enter sends · Shift+Enter for a new line · Esc closes'
            }}
          </p>
        }
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
        background: var(--sc-bg);
        color: var(--sc-fg);
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
        padding: 8px 10px;
        border-bottom: 1px solid var(--sc-line);
        background: var(--sc-surface);
        flex: none;
        user-select: none;
        cursor: grab;
      }
      .mark {
        width: 26px;
        height: 26px;
        border-radius: 7px;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
        display: inline-flex;
        align-items: center;
        justify-content: center;
        flex: none;
      }
      .mark svg {
        width: 16px;
        height: 16px;
      }
      .title {
        display: flex;
        flex-direction: column;
        min-width: 0;
        margin-right: auto;
      }
      .head b {
        font-weight: 700;
        font-size: 14px;
      }
      .state {
        font-size: 12px;
        color: var(--sc-muted);
        display: inline-flex;
        gap: 6px;
        align-items: center;
        min-width: 0;
        overflow: hidden;
        white-space: nowrap;
        text-overflow: ellipsis;
      }
      .dot {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        color: var(--sc-on-ok-container);
        background: currentColor;
        box-shadow: 0 0 0 3px color-mix(in srgb, currentColor 18%, transparent);
        display: inline-block;
        flex: none;
      }
      .dot.live {
        color: var(--sc-accent);
        box-shadow: 0 0 0 3px color-mix(in srgb, currentColor 22%, transparent);
        animation: pulse 1.2s ease-in-out infinite;
      }
      .dot.off {
        color: var(--sc-muted);
        box-shadow: 0 0 0 3px var(--sc-surface-2);
      }
      @keyframes pulse {
        50% {
          opacity: 0.35;
          transform: scale(0.8);
        }
      }
      .theme {
        display: flex;
        gap: 1px;
        padding: 2px;
        border: 1px solid var(--sc-line);
        border-radius: 16px;
        background: var(--sc-bg);
        flex: none;
      }
      .theme-option {
        width: 26px;
        height: 24px;
        border: 0;
        border-radius: 12px;
        padding: 0;
        display: flex;
        align-items: center;
        justify-content: center;
        background: transparent;
        color: var(--sc-muted);
        cursor: pointer;
      }
      .theme-option[aria-pressed='true'] {
        background: var(--sc-surface);
        color: var(--sc-fg);
      }
      :host-context(html[data-theme='dark']) .theme-option[aria-pressed='true'] {
        background: var(--sc-line);
      }
      @media (prefers-color-scheme: dark) {
        :host-context(html:not([data-theme])) .theme-option[aria-pressed='true'] {
          background: var(--sc-line);
        }
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
        border-radius: var(--sc-radius-sm);
        border: 1px solid transparent;
        background: transparent;
        color: var(--sc-fg-2);
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
        background: var(--sc-surface-2);
      }
      .ib.tools-toggle {
        color: var(--sc-fg);
      }
      .ib[aria-pressed='true'] {
        background: var(--sc-surface-2);
        color: var(--sc-fg);
        border-color: var(--sc-line);
      }
      .ib:disabled {
        color: var(--sc-muted);
        cursor: default;
      }
      .ib:disabled:hover {
        background: transparent;
      }
      .ib:focus-visible,
      .send:focus-visible,
      .tool:focus-visible,
      .field textarea:focus-visible {
        outline: 2px solid var(--sc-accent);
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
        flex-direction: column;
        gap: 6px;
        padding: 8px 10px;
        border-bottom: 1px solid var(--sc-line);
        background: var(--sc-surface);
      }
      .tray[hidden] {
        display: none;
      }
      .tool-row {
        display: flex;
        gap: 6px;
        flex-wrap: wrap;
        align-items: center;
      }
      .tool {
        --mat-button-outlined-container-height: 32px;
        --mat-button-outlined-horizontal-padding: 10px;
        --mat-button-outlined-label-text-size: 13px;
        --mat-button-outlined-label-text-weight: 400;
        --mat-button-outlined-label-text-tracking: normal;
        --mat-button-outlined-disabled-label-text-color: var(--sc-muted);
        --mat-button-outlined-disabled-outline-color: var(--sc-line);
        min-width: 0;
      }
      .tool svg {
        width: 14px;
        height: 14px;
        margin-right: 6px;
        vertical-align: middle;
      }
      .tool.recording {
        --mat-button-outlined-label-text-color: var(--sc-error);
        --mat-button-outlined-outline-color: var(--sc-error);
        background: var(--sc-error-container);
      }
      .translate {
        height: 32px;
        box-sizing: border-box;
        padding: 0 4px 0 10px;
        border-radius: 16px;
        border: 1px solid var(--sc-line);
        display: flex;
        align-items: center;
        gap: 4px;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .translate select {
        font: inherit;
        font-size: 13px;
        height: 28px;
        background: transparent;
        color: var(--sc-fg);
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
        background: var(--sc-surface-2);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius-sm);
        color: var(--sc-fg);
        padding: 4px 10px;
        font-size: 13px;
        cursor: pointer;
      }
      .approvals {
        --approval-inset: 12px;
      }
      .composer {
        flex: none;
        border-top: 1px solid var(--sc-line);
        background: var(--sc-surface);
        padding: 8px 12px 10px;
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
        gap: 8px;
        padding: 3px 3px 3px 4px;
        border-radius: 10px;
        background: var(--sc-bg);
        border: 1px solid var(--sc-line);
        color: var(--sc-fg-2);
      }
      .chip .preview {
        width: 36px;
        height: 24px;
        box-sizing: border-box;
        object-fit: cover;
        border-radius: 5px;
        border: 1px solid var(--sc-line);
        background: var(--sc-surface-2);
      }
      .chip button svg {
        width: 14px;
        height: 14px;
      }
      .chip button {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        width: 24px;
        height: 24px;
        border: 0;
        background: transparent;
        color: var(--sc-fg-2);
        cursor: pointer;
        border-radius: 6px;
        padding: 0;
      }
      .line {
        display: flex;
        gap: 8px;
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
        max-height: 120px;
        overflow-y: auto;
        font: inherit;
        font-size: 14px;
        line-height: 20px;
        padding: 9px 44px 9px 12px;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        color: var(--sc-fg);
        min-height: 40px;
        box-sizing: border-box;
      }
      .field textarea:disabled {
        background: var(--sc-surface-2);
        color: var(--sc-muted);
      }
      .ib.in {
        position: absolute;
        right: 4px;
        bottom: 4px;
      }
      .ib.in svg {
        width: 18px;
        height: 18px;
      }
      .ib.in.recording {
        background: var(--sc-error-container);
        color: var(--sc-error);
        border-color: transparent;
      }
      .send {
        width: 40px;
        height: 40px;
        border-radius: 10px;
        border: 0;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
        display: inline-flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        flex: none;
        padding: 0;
      }
      .send svg {
        width: 18px;
        height: 18px;
      }
      .send.stop svg {
        width: 16px;
        height: 16px;
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
      .transcript {
        display: flex;
        flex-direction: column;
        gap: 4px;
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius-sm);
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
        color: var(--sc-accent);
      }
      .transcript .partial {
        color: var(--sc-muted);
      }
      .transcript .translation {
        display: block;
        padding-left: 12px;
        color: var(--sc-muted);
      }
      .transcript .translation.gap {
        font-style: italic;
      }
      .transcript-actions {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 8px;
        color: var(--sc-muted);
      }
      .transcript-actions button,
      .meeting-stop button {
        background: var(--sc-surface-2);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius-sm);
        color: var(--sc-fg);
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
        background: var(--sc-surface-2);
        border: 1px solid var(--sc-line);
        border-radius: 6px;
        color: var(--sc-fg);
        padding: 4px 8px;
      }
      .screen-text {
        display: flex;
        flex-direction: column;
        gap: 4px;
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius-sm);
        padding: 6px 10px;
        font-size: 13px;
      }
      .screen-text textarea {
        background: var(--sc-surface-2);
        border: 1px solid var(--sc-line);
        border-radius: 6px;
        color: var(--sc-fg);
        font: inherit;
        resize: vertical;
      }
      .turns {
        flex: 1 1 0;
        min-height: 0;
        overflow-y: auto;
        list-style: none;
        margin: 0;
        padding: 12px 12px 4px;
        display: flex;
        flex-direction: column;
        gap: 14px;
      }
      .who .speak {
        margin-left: auto;
      }
      .suggestion {
        --mat-button-outlined-container-height: 36px;
        --mat-button-outlined-horizontal-padding: 14px;
        --mat-button-outlined-label-text-tracking: normal;
      }
      .suggestion svg {
        width: 16px;
        height: 16px;
        margin-right: 8px;
        vertical-align: middle;
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
      .hint {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .composer .hint {
        display: inline-flex;
        align-items: center;
        gap: 6px;
      }
      .hint.live {
        color: var(--sc-error);
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
        margin: 0;
        font-size: 13px;
        color: var(--sc-error);
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
  protected readonly findingCards = inject(ReviewFindingsStore).cards;
  private readonly llmConfig = inject(LlmConfigStore);
  protected readonly theme = inject(ThemeStore);
  protected readonly themeOptions = THEME_OPTIONS;
  protected readonly live = inject(LiveListener);
  protected readonly voiceAsk = inject(VoiceModelAsk);
  protected readonly transcript = inject(TranscriptStore);
  private readonly voice = inject(ConversationMode);
  private readonly readAloud = inject(ReadAloud);
  private readonly input = viewChild<ElementRef<HTMLTextAreaElement>>('questionInput');
  private readonly turnList = viewChild<ElementRef<HTMLElement>>('turnList');
  private readonly transcriptList = viewChild<ElementRef<HTMLElement>>('transcriptList');

  readonly turns = this.conversation.turns;
  readonly sending = signal(false);
  /** True while a question is on its way to main or any turn is being answered; a new question then stops that answer. */
  readonly busy = computed(() => this.sending() || this.conversation.running() !== null);
  readonly toolsOpen = signal(false);
  /** The tray shows when the owner opened it, and stays open while a lane listens or a meeting runs so its Stop stays in reach. */
  readonly trayShown = computed(
    () =>
      this.toolsOpen() ||
      (this.live.listening() && !this.recording()) ||
      this.live.othersListening() ||
      this.meeting() === 'running',
  );
  /** True when no AI Source is chosen or none is known yet, so the status dot is grey. */
  readonly sourceless = computed(() => {
    const provider = this.llmConfig.provider();
    return provider === null || provider === 'None';
  });
  readonly source = computed(() => {
    const provider = this.llmConfig.provider();
    if (provider === 'None') return 'No AI Source connected';
    if (provider === null) return 'No AI Source';
    const model = this.llmConfig.model();
    return model === null ? providerLabel(provider) : `${providerLabel(provider)} · ${model}`;
  });
  /** True when the owner chose no AI Source, so Scout cannot answer. */
  readonly noSource = computed(() => this.llmConfig.provider() === 'None');
  protected readonly suggestion = 'List my open pull requests';
  readonly question = signal('');
  readonly recording = signal(false);
  readonly error = signal<string | null>(null);
  /** Said after a hold to talk that heard nothing. */
  readonly voiceNotice = signal<string | null>(null);
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
  private readonly meetingSessions = new Set<string>();
  private meetingLanesEnded: Promise<unknown> = Promise.resolve();
  /** True from Stop meeting until both of its lanes have ended, so no new listen takes a lane still finishing. */
  readonly meetingLanesEnding = signal(false);
  /** The lanes of a running meeting that are not listening, each named as the indicator says it. */
  readonly stoppedLanes = computed(() => {
    if (this.meeting() !== 'running') return [];
    const stopped: LocalListenLane[] = [];
    if (!this.listening()) stopped.push('me');
    if (this.live.systemAudioAvailable && !this.othersListening()) stopped.push('others');
    return stopped;
  });
  readonly stoppedLanesNotice = computed(() =>
    this.stoppedLanes()
      .map((lane) => ` · ${lane === 'me' ? 'microphone' : 'system audio'} stopped`)
      .join(''),
  );
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
    void this.voiceAsk.refresh();
    effect(() => {
      if (this.readAloud.error() !== null) untracked(() => this.toolsOpen.set(true));
    });
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
    let wasBusy = false;
    afterRenderEffect(() => {
      const busy = this.busy();
      const lost = document.activeElement === null || document.activeElement === document.body;
      if (wasBusy && !busy && lost) this.input()?.nativeElement.focus();
      wasBusy = busy;
    });
    inject(DestroyRef).onDestroy(() => {
      this.stopClock();
      if (this.recording()) void this.live.stop('me');
      void this.live.stopAll();
      this.voice.escape();
    });
    afterNextRender(() => this.input()?.nativeElement.focus());
  }

  onKeyDown(event: KeyboardEvent): void {
    const target = event.target;
    if (target instanceof HTMLElement && target.closest('.cdk-overlay-container') !== null) return;
    if (target instanceof HTMLElement && target !== this.input()?.nativeElement) {
      if (target.matches('input, textarea, select')) {
        if (event.key === 'Escape') target.blur();
        return;
      }
      if (target.matches('button') && event.key !== 'Escape') return;
    }
    if (event.key === 'Escape') {
      event.preventDefault();
      if (this.voice.escape()) return;
      this.close();
      return;
    }
    if (event.code !== 'Space') return;
    if (this.recording()) {
      event.preventDefault();
      return;
    }
    if (event.repeat || this.question().length > 0 || this.busy() || this.noSource()) return;
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
    void this.voiceAsk.refresh();
    this.input()?.nativeElement.focus();
  }

  /** Remembers whether the reader is within 48 px of the newest turn, so a new turn only scrolls a list left at the bottom. */
  onTurnsScroll(): void {
    const list = this.turnList()?.nativeElement;
    if (list !== undefined) {
      this.atBottom = list.scrollHeight - list.scrollTop - list.clientHeight <= 48;
    }
  }

  /** Empties the conversation in every window, stopping an answer still running; never while Scout queues a card's issues. */
  async clearConversation(): Promise<void> {
    if (this.sending() || this.posting() || this.meeting() !== 'off') return;
    this.error.set(await this.conversation.clear());
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
    this.voice.escape();
    void this.bridge.localHide();
  }

  /** Opens the microphone and, where this platform can, the system audio, and starts the meeting clock. */
  async startMeeting(): Promise<void> {
    if (this.meeting() !== 'off' || this.recording()) return;
    if (!this.voiceAsk.allowsVoice()) return;
    const startedAt = Date.now();
    this.turnsBeforeMeeting.clear();
    this.turns().forEach((turn) => this.turnsBeforeMeeting.add(turn.id));
    this.meetingTurnTimes.clear();
    this.meetingSessions.clear();
    this.meetingKept.set(false);
    this.meetingError.set(null);
    this.meetingStartedAt.set(startedAt);
    this.now.set(startedAt);
    this.meeting.set('running');
    this.clockTimer = setInterval(() => this.now.set(Date.now()), 1000);
    this.error.set(null);
    await Promise.all([
      this.listenInMeeting('me'),
      this.live.systemAudioAvailable ? this.listenInMeeting('others') : Promise.resolve(),
    ]);
  }

  /** Opens again, on one click, every lane of the running meeting that stopped; nothing reopens on its own. */
  async resumeMeeting(): Promise<void> {
    if (!this.voiceAsk.allowsVoice()) return;
    this.error.set(null);
    await Promise.all(this.stoppedLanes().map((lane) => this.listenInMeeting(lane)));
  }

  private async listenInMeeting(lane: LocalListenLane): Promise<void> {
    const id = await this.live.listen(lane, this.translation(), false, true);
    if (id !== null) this.meetingSessions.add(id);
  }

  /** Ends both lanes and the clock, and shows the row where the owner may keep the meeting. */
  stopMeeting(): void {
    if (this.meeting() !== 'running') return;
    this.meetingEndedAt = Date.now();
    this.stopClock();
    this.now.set(this.meetingEndedAt);
    const ended = Promise.all([
      this.live.finish('me', MEETING_FINISH_WAIT_MS),
      this.live.finish('others', MEETING_FINISH_WAIT_MS),
    ]);
    this.meetingLanesEnded = ended;
    this.meetingLanesEnding.set(true);
    void ended.then(() => {
      if (this.meetingLanesEnded === ended) this.meetingLanesEnding.set(false);
    });
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
      await this.meetingLanesEnded;
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
        .filter(
          (line) =>
            line.at >= startedAt &&
            line.session !== undefined &&
            this.meetingSessions.has(line.session),
        )
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
    if (!this.voiceAsk.allowsVoice()) return;
    this.error.set(null);
    if (this.meeting() === 'running') void this.listenInMeeting('others');
    else void this.live.listen('others', this.translation());
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
    if (!this.voiceAsk.allowsVoice()) return;
    this.error.set(null);
    if (this.meeting() === 'running') void this.listenInMeeting('me');
    else void this.live.listen('me', this.translation());
  }

  /** Stops hold to talk and throws what was heard away, as on Escape or when the window loses focus. */
  discardRecording(): void {
    if (!this.recording()) return;
    this.recording.set(false);
    void this.live.stop('me');
    this.live.clear();
  }

  async ask(voice = false): Promise<void> {
    const question = this.question().trim();
    if (question.length === 0 || this.sending() || this.posting() || this.noSource()) return;
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
      refusal = await this.conversation.ask(question, this.image(shot), inMeeting, context, voice);
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

  /** Hold to talk: the microphone streams to the live transcriber and what it hears fills the question box. */
  async startRecording(): Promise<void> {
    if (this.recording() || this.listening() || this.busy() || this.noSource()) return;
    if (!this.voiceAsk.allowsVoice()) return;
    this.error.set(null);
    this.voiceNotice.set(null);
    this.recording.set(true);
    await this.live.listen('me', 'off', true);
    if (!this.live.listening()) this.recording.set(false);
  }

  /**
   * Release: waits for the last final, then sends the heard text as a typed question marked `voice`; nothing heard
   * sends nothing and says so, and a transcription failure shows its reason and sends nothing.
   */
  async stopRecording(): Promise<void> {
    if (!this.recording()) return;
    this.recording.set(false);
    const { text, error } = await this.live.finish('me');
    if (error !== null) {
      this.error.set(error);
      this.live.clear();
      return;
    }
    if (text.trim().length === 0) {
      this.voiceNotice.set(NO_SPEECH_HEARD);
      this.live.clear();
      return;
    }
    this.question.set(text.trim());
    await this.ask(true);
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
