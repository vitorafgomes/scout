import { DatePipe, PercentPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';

import {
  readIpcError,
  type ClaimDto,
  type ClaimStatus,
  type MandateDto,
  type MouthBoxDto,
  type OperatorProjectDto,
  type ProjectFormat,
  type ProjectMotion,
  type ReadinessDto,
  type VoiceDto,
  type SceneDto,
  type SceneMediaKind,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

const POLL_MS = 10_000;
const PROJECTS_SHOWN = 50;
const PILLAR_SLOTS = [0, 1, 2, 3, 4] as const;
const MIN_TOPIC_LENGTH = 3;
const PRODUCIBLE = ['script_ready', 'media_ready', 'assembled', 'approved'];
const BACKGROUND_ATTACHABLE = [
  'script_ready',
  'media_ready',
  'assembled',
  'approved',
  'assembly_queued',
  'assembling',
  'failed',
];
const DELEGABLE = ['script_ready', 'media_ready'];
const RUNNING = ['researching', 'scripting', 'transcribing', 'producing', 'assembling'];
const RECORDING_ATTACHABLE = ['recording_waiting', 'queued'];

const SOURCE_LABELS: Readonly<Record<string, string>> = {
  generated: 'generated',
  owner: 'your file',
  none: 'none',
  text_card: 'text card',
  stock: 'Openverse',
  recording: 'your recording',
};

const MOTIONS: readonly { readonly value: ProjectMotion; readonly label: string }[] = [
  { value: 'still', label: 'Still' },
  { value: 'gentle', label: 'Gentle (recommended)' },
  { value: 'dynamic', label: 'Dynamic' },
];

const MOTION_NAMES: Readonly<Record<ProjectMotion, string>> = {
  still: 'Still',
  gentle: 'Gentle',
  dynamic: 'Dynamic',
};

const FORMAT_NAMES: Readonly<Record<ProjectFormat, string>> = {
  generated_pictures: 'Generated pictures',
  own_pictures: 'Your own pictures',
  own_video: 'Your own video',
  animated_character: 'Animated character',
};

const MAX_DIRECTION = 1000;
const MAX_PRESENTER = 300;
const MIN_MOUTH_SIZE = 0.02;
const MAX_SCENE_TEXT = 700;

const TARGET_METRICS: readonly { readonly value: string; readonly label: string }[] = [
  { value: 'views', label: 'Views' },
  { value: 'watch_hours', label: 'Watch hours' },
  { value: 'subscribers', label: 'Subscribers' },
  { value: 'engagement', label: 'Engagement' },
];

/** The channel operator's desk: the mandate, a video request, the projects and the claims to check. */
@Component({
  selector: 'sc-production',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, PercentPipe],
  template: `
    <main class="page">
      <header class="header">
        <h1>Production</h1>
        <p class="lede">What the channel is for, the videos Scout is making, and what to check.</p>
      </header>

      @if (error(); as message) {
        <p class="err" data-test="production-error">{{ message }}</p>
      }

      <section class="block" data-test="readiness">
        <h2>What Scout can make</h2>
        @if (readiness(); as r) {
          <ul class="plain">
            <li data-test="readiness-speech">
              Narration: {{ r.speechAvailable ? 'available' : 'not available' }}
            </li>
            <li data-test="readiness-images">
              Generated pictures: {{ r.imagesAvailable ? 'available' : 'not available' }}
            </li>
            <li data-test="readiness-stock">{{ picturesLine(r) }}</li>
            <li data-test="readiness-provider">Provider: {{ r.provider || 'none' }}</li>
            <li data-test="readiness-youtube">
              YouTube: {{ r.youtubeSignedIn ? 'signed in' : 'not signed in' }}
            </li>
            <li data-test="readiness-voices">
              Voices: {{ r.voices.length > 0 ? r.voices.length + ' available' : 'none' }}
            </li>
            @for (note of r.notes; track $index) {
              <li class="meta" data-test="readiness-note">{{ note }}</li>
            }
          </ul>
        }
        @if (readinessError(); as message) {
          <p class="err" data-test="readiness-error">{{ message }}</p>
        }
      </section>

      <section class="block" data-test="mandate">
        <h2>Mandate</h2>
        @if (mandateLoaded() && mandate() === null) {
          <p class="empty" data-test="mandate-empty">
            The operator needs a mandate before it makes a video: what the channel is for, who it is
            for and its language. Fill it in and save it.
          </p>
        }
        <div class="form">
          <label
            >Objective
            <input
              type="text"
              data-test="mandate-objective"
              [value]="objective()"
              (input)="objective.set($any($event.target).value)"
          /></label>
          <label
            >Audience
            <input
              type="text"
              data-test="mandate-audience"
              [value]="audience()"
              (input)="audience.set($any($event.target).value)"
          /></label>
          <fieldset>
            <legend>Pillars (up to 5)</legend>
            @for (slot of pillarSlots; track slot) {
              <input
                type="text"
                maxlength="80"
                [attr.data-test]="'mandate-pillar-' + slot"
                [value]="pillars()[slot]"
                (input)="setPillar(slot, $any($event.target).value)"
              />
            }
          </fieldset>
          <label
            >Cadence
            <input
              type="text"
              data-test="mandate-cadence"
              [value]="cadence()"
              (input)="cadence.set($any($event.target).value)"
          /></label>
          <label
            >Language
            <input
              type="text"
              data-test="mandate-language"
              [value]="language()"
              (input)="language.set($any($event.target).value)"
          /></label>
          <label
            >Voice and style
            <input
              type="text"
              data-test="mandate-style"
              [value]="voiceAndStyle()"
              (input)="voiceAndStyle.set($any($event.target).value)"
          /></label>
          <label
            >Blocked topics (comma separated)
            <input
              type="text"
              data-test="mandate-blocked"
              [value]="blockedTopics()"
              (input)="blockedTopics.set($any($event.target).value)"
          /></label>
          <label
            >Target metric
            <select
              data-test="mandate-metric"
              (change)="targetMetric.set($any($event.target).value)"
            >
              <option value="" [selected]="targetMetric() === ''">Not set</option>
              @for (m of metrics; track m.value) {
                <option [value]="m.value" [selected]="targetMetric() === m.value">
                  {{ m.label }}
                </option>
              }
            </select>
          </label>
          <label
            >Target value
            <input
              type="text"
              data-test="mandate-target"
              [value]="targetValue()"
              (input)="targetValue.set($any($event.target).value)"
          /></label>
        </div>
        <div class="row">
          <button
            type="button"
            class="btn btn--primary"
            data-test="mandate-save"
            [disabled]="savingMandate()"
            (click)="saveMandate()"
          >
            Save
          </button>
          @if (mandate()?.updatedAt; as savedAt) {
            <span class="meta" data-test="mandate-saved">
              Saved {{ savedAt | date: 'yyyy-MM-dd HH:mm' }}
            </span>
          }
        </div>
        @if (mandateError(); as message) {
          <p class="err" data-test="mandate-error">{{ message }}</p>
        }
      </section>

      <section class="block">
        <h2>Make a video</h2>
        <div class="row">
          <input
            type="text"
            class="grow"
            placeholder="Topic"
            maxlength="200"
            data-test="topic"
            [value]="topic()"
            (input)="topic.set($any($event.target).value)"
          />
          <button
            type="button"
            class="btn btn--primary"
            data-test="start-project"
            [disabled]="
              starting() ||
              topic().trim().length < minTopicLength ||
              startDirection().trim().length > maxDirection ||
              (startFormat() === 'animated_character' &&
                startPresenter().trim().length > maxPresenter)
            "
            (click)="startProject()"
          >
            Make a video
          </button>
        </div>
        <fieldset>
          <legend>Format</legend>
          <label
            ><input
              type="radio"
              name="start-format"
              data-test="format-generated_pictures"
              [checked]="startFormat() === 'generated_pictures'"
              (change)="startFormat.set('generated_pictures')"
            />
            Generated pictures</label
          >
          <label
            ><input
              type="radio"
              name="start-format"
              data-test="format-own_pictures"
              [checked]="startFormat() === 'own_pictures'"
              (change)="startFormat.set('own_pictures')"
            />
            Your own pictures</label
          >
          <label
            ><input
              type="radio"
              name="start-format"
              data-test="format-animated_character"
              [disabled]="!lipSyncable()"
              [checked]="startFormat() === 'animated_character'"
              (change)="startFormat.set('animated_character')"
            />
            Animated character</label
          >
          @if (!lipSyncable()) {
            <p class="meta" data-test="format-animated_character-reason">
              Scout has no lip-sync tool on this machine; it ships with Scout Desktop.
            </p>
          }
          <label
            ><input type="radio" name="start-format" data-test="format-ai_presenter" disabled /> AI
            presenter</label
          >
          <p class="meta" data-test="format-ai_presenter-reason">
            The connected AI Source has no video model.
          </p>
          <label
            ><input
              type="radio"
              name="start-format"
              data-test="format-own_video"
              [disabled]="!transcribable()"
              [checked]="startFormat() === 'own_video'"
              (change)="startFormat.set('own_video')"
            />
            Your own video</label
          >
          @if (!transcribable()) {
            <p class="meta" data-test="format-own_video-reason">
              Scout has no speech-to-text model on this machine; download one in AI Sources, Local
              Models.
            </p>
          }
        </fieldset>
        <label
          >Direction
          <textarea
            rows="3"
            data-test="start-direction"
            [value]="startDirection()"
            (input)="startDirection.set($any($event.target).value)"
          ></textarea>
        </label>
        <span class="meta" data-test="start-direction-count"
          >{{ startDirection().trim().length }} / {{ maxDirection }}</span
        >
        @if (startFormat() === 'animated_character') {
          <label
            >Presenter
            <textarea
              rows="2"
              data-test="start-presenter"
              [value]="startPresenter()"
              (input)="startPresenter.set($any($event.target).value)"
            ></textarea>
          </label>
          <span class="meta" data-test="start-presenter-count"
            >{{ startPresenter().trim().length }} / {{ maxPresenter }}</span
          >
        }
        @if (queued(); as q) {
          <p class="meta" data-test="queued">
            {{ q.topic }} <span class="badge">{{ q.status }}</span>
          </p>
        }
        @if (startError(); as message) {
          <p class="err" data-test="start-error">{{ message }}</p>
        }
      </section>

      <section class="block">
        <h2>Projects</h2>
        @if (projects().length === 0) {
          <p class="empty" data-test="projects-empty">
            No videos yet. Ask for one above, or ask Scout in chat.
          </p>
        } @else {
          <ul class="projects">
            @for (p of projects(); track p.id) {
              <li>
                <button
                  type="button"
                  class="project"
                  [class.project--selected]="p.id === selectedId()"
                  [attr.data-test]="'project-' + p.id"
                  (click)="select(p.id)"
                >
                  <strong>{{ p.topic }}</strong>
                  <span class="badge">{{ p.status }}</span>
                  <span class="meta">{{ p.updatedAt | date: 'yyyy-MM-dd HH:mm' }}</span>
                </button>
              </li>
            }
          </ul>
        }
      </section>

      @if (selected(); as p) {
        <section class="block" data-test="project-detail">
          <h2>{{ p.topic }}</h2>
          <div class="row">
            <span class="meta" data-test="project-voice"
              >Voice: {{ voiceLabel(p.narrationVoice) }}</span
            >
            @if (p.delegated) {
              <span class="badge" data-test="project-delegated">Delegated</span>
            }
            @if (delegable(p)) {
              <button
                type="button"
                class="btn btn--secondary"
                data-test="delegate"
                (click)="openDelegate(p)"
              >
                Delegate
              </button>
            }
            @if (deletable(p)) {
              <button
                type="button"
                class="btn btn--secondary"
                data-test="delete"
                (click)="openDelete(p)"
              >
                Delete project
              </button>
            }
          </div>
          <p class="meta" data-test="project-format">Format: {{ formatName(p.format) }}</p>
          <p class="meta" data-test="project-visual">Visual: {{ motionName(p.motion) }} motion</p>
          @if (p.direction) {
            <p class="meta" data-test="project-direction">{{ p.direction }}</p>
          }
          @if (p.presenter) {
            <p class="meta" data-test="project-presenter">Presenter: {{ p.presenter }}</p>
          }
          @if (p.error; as note) {
            @if (p.status === 'failed') {
              <p class="err" data-test="project-failure">{{ note }}</p>
            } @else {
              <p class="meta" data-test="project-note">{{ note }}</p>
            }
          }

          <h3>Topic candidates</h3>
          @if (p.candidates.length === 0) {
            <p class="empty" data-test="candidates-empty">No topic candidates yet.</p>
          } @else {
            <ul class="plain">
              @for (c of p.candidates; track $index) {
                <li data-test="candidate">
                  <strong>{{ c.title }}</strong> <span class="badge">{{ c.fit | percent }}</span>
                  <p class="meta">{{ c.reason }}</p>
                </li>
              }
            </ul>
          }

          <h3>Script</h3>
          @if (p.script; as s) {
            <div data-test="script">
              <p><strong>Hook:</strong> {{ s.hook }}</p>
              @for (section of s.sections; track $index) {
                <div class="section" data-test="script-section">
                  @if (p.format === 'own_video') {
                    <p class="meta" data-test="script-section-time">
                      {{ clock(section.startMs) }} to {{ clock(section.endMs) }}
                    </p>
                  }
                  <h4>{{ section.heading }}</h4>
                  <p>{{ section.narration }}</p>
                </div>
              }
              <p><strong>Call to action:</strong> {{ s.callToAction }}</p>
              <p><strong>Titles:</strong></p>
              <ul class="plain">
                @for (t of s.titleVariants; track $index) {
                  <li data-test="title-variant">{{ t }}</li>
                }
              </ul>
              <p><strong>Description:</strong> {{ s.description }}</p>
              <p data-test="tags"><strong>Tags:</strong> {{ s.tags.join(', ') }}</p>
              <ul class="plain">
                @for (ch of s.chapters; track $index) {
                  <li data-test="chapter">{{ ch.at }} {{ ch.title }}</li>
                }
              </ul>
            </div>
          } @else {
            <p class="empty" data-test="script-empty">The script is not written yet.</p>
          }

          <h3>Claims</h3>
          @if (p.claims.length === 0) {
            <p class="empty" data-test="claims-empty">No claims to check.</p>
          } @else {
            <ul class="claims">
              @for (c of p.claims; track c.id) {
                <li class="claim" [attr.data-test]="'claim-' + c.id">
                  <p>{{ c.text }}</p>
                  <p class="meta">
                    Risk: {{ c.risk }} · Status:
                    <span class="badge" [attr.data-test]="'claim-status-' + c.id">{{
                      c.status
                    }}</span>
                    @if (c.note; as note) {
                      · {{ note }}
                    }
                  </p>
                  @for (url of c.sourceUrls; track $index) {
                    <div class="source">
                      <code>{{ url }}</code>
                      <button type="button" class="btn btn--secondary" (click)="copy(url)">
                        Copy
                      </button>
                    </div>
                  }
                  <div class="row">
                    <button
                      type="button"
                      class="btn btn--secondary"
                      [attr.data-test]="'supported-' + c.id"
                      [disabled]="deciding() === c.id"
                      (click)="decide(p, c, 'supported')"
                    >
                      Supported
                    </button>
                    <button
                      type="button"
                      class="btn btn--secondary"
                      [attr.data-test]="'unsupported-' + c.id"
                      [disabled]="deciding() === c.id"
                      (click)="decide(p, c, 'unsupported')"
                    >
                      Unsupported
                    </button>
                    <button
                      type="button"
                      class="btn btn--secondary"
                      [attr.data-test]="'waive-' + c.id"
                      [disabled]="deciding() === c.id"
                      (click)="openWaive(c.id)"
                    >
                      Waive
                    </button>
                  </div>
                  @if (waiving() === c.id) {
                    <div class="row">
                      <input
                        type="text"
                        class="grow"
                        placeholder="Why this claim needs no source (required)"
                        [attr.data-test]="'waive-note-' + c.id"
                        [value]="waiveNote()"
                        (input)="waiveNote.set($any($event.target).value)"
                      />
                      <button
                        type="button"
                        class="btn btn--primary"
                        [attr.data-test]="'waive-confirm-' + c.id"
                        [disabled]="waiveNote().trim().length === 0 || deciding() === c.id"
                        (click)="decide(p, c, 'waived')"
                      >
                        Waive claim
                      </button>
                    </div>
                  }
                </li>
              }
            </ul>
          }
          @if (claimError(); as message) {
            <p class="err" data-test="claim-error">{{ message }}</p>
          }

          @if (p.format === 'animated_character') {
            <h3>Presenter</h3>
            <div data-test="presenter">
              @if (p.presenterFile; as file) {
                <div
                  class="mouth-area"
                  data-test="mouth-area"
                  (pointerdown)="startMouth($event)"
                  (pointermove)="moveMouth($event)"
                  (pointerup)="endMouth($event)"
                  (pointercancel)="cancelMouth()"
                >
                  <img
                    class="mouth-area__image"
                    data-test="presenter-image"
                    draggable="false"
                    [src]="mediaUrl(p, file)"
                    alt="Presenter of the video"
                  />
                  @if (p.mouthBox; as box) {
                    <div
                      class="mouth"
                      data-test="mouth-current"
                      [style.left.%]="box.x * 100"
                      [style.top.%]="box.y * 100"
                      [style.width.%]="box.width * 100"
                      [style.height.%]="box.height * 100"
                    ></div>
                  }
                  @if (mouthDraft(); as box) {
                    <div
                      class="mouth mouth--drawn"
                      data-test="mouth-drawn"
                      [style.left.%]="box.x * 100"
                      [style.top.%]="box.y * 100"
                      [style.width.%]="box.width * 100"
                      [style.height.%]="box.height * 100"
                    ></div>
                  }
                </div>
                <p class="meta" data-test="mouth-hint">
                  Drag a box around the mouth so the character speaks.
                </p>
                <div class="row">
                  <button
                    type="button"
                    class="btn btn--primary"
                    data-test="mouth-save"
                    [disabled]="!mouthDraftValid() || projectMediaBusy()"
                    (click)="saveMouth(p)"
                  >
                    Save mouth
                  </button>
                  <button
                    type="button"
                    class="btn btn--secondary"
                    data-test="mouth-clear"
                    (click)="mouthDraft.set(null)"
                  >
                    Clear
                  </button>
                </div>
              } @else {
                <p class="empty" data-test="presenter-empty">
                  No presenter picture yet: Scout makes one when it produces the video if the
                  connected AI Source makes pictures; or attach your own.
                </p>
              }
              @if (backgroundAttachable(p)) {
                <div class="row">
                  <button
                    type="button"
                    class="btn btn--secondary"
                    data-test="presenter-attach"
                    [disabled]="projectMediaBusy()"
                    (click)="attachProjectMedia(p, 'presenter')"
                  >
                    {{ p.presenterFile ? 'Replace picture' : 'Attach picture' }}
                  </button>
                  <button
                    type="button"
                    class="btn btn--secondary"
                    data-test="presenter-regenerate"
                    [disabled]="projectMediaBusy() || noPictures()"
                    [attr.title]="noPictures() ? 'The connected AI Source makes no pictures' : null"
                    (click)="regeneratePresenter(p)"
                  >
                    Regenerate picture
                  </button>
                </div>
              }
              @if (presenterError(); as message) {
                <p class="err" data-test="presenter-error">{{ message }}</p>
              }
            </div>
          }
          @if (p.format === 'own_video') {
            <h3>Recording</h3>
            <div data-test="recording">
              @if (p.recordingFile === null) {
                <p class="empty" data-test="recording-empty">
                  No recording yet: attach the video you recorded; Scout transcribes it on this
                  machine.
                </p>
              }
              @if (recordingAttachable(p)) {
                <button
                  type="button"
                  class="btn btn--secondary"
                  data-test="recording-attach"
                  [disabled]="projectMediaBusy()"
                  (click)="attachProjectMedia(p, 'recording')"
                >
                  {{ p.recordingFile ? 'Replace recording' : 'Attach recording' }}
                </button>
              }
              @if (projectMediaError(); as message) {
                <p class="err" data-test="recording-error">{{ message }}</p>
              }
            </div>
          } @else {
            <h3>Background</h3>
            <div data-test="background">
              @if (p.backgroundFile; as file) {
                <img
                  class="preview"
                  data-test="background-image"
                  [src]="mediaUrl(p, file)"
                  alt="Background of the video"
                />
              } @else {
                <p class="empty" data-test="background-empty">
                  No background yet: every scene without its own picture is a dark text card.
                </p>
              }
              @if (backgroundAttachable(p)) {
                <button
                  type="button"
                  class="btn btn--secondary"
                  data-test="background-attach"
                  [disabled]="projectMediaBusy()"
                  (click)="attachProjectMedia(p, 'background')"
                >
                  {{ p.backgroundFile ? 'Replace background' : 'Attach background' }}
                </button>
              }
              @if (projectMediaError(); as message) {
                <p class="err" data-test="background-error">{{ message }}</p>
              }
            </div>
          }

          <h3>Scenes</h3>
          @if (producible(p)) {
            <div class="row">
              <button
                type="button"
                class="btn btn--primary"
                data-test="produce"
                [disabled]="producing() || pendingClaims(p) > 0"
                (click)="openProduce(p)"
              >
                Produce video media
              </button>
              @if (pendingClaims(p) > 0) {
                <span class="err" data-test="produce-blocked">
                  {{ pendingClaims(p) }} {{ pendingClaims(p) === 1 ? 'claim is' : 'claims are' }}
                  still pending; decide every claim before producing the video.
                </span>
              }
            </div>
            @if (producePanel() === p.id) {
              <div class="panel" data-test="produce-panel">
                @if (p.format !== 'own_video') {
                  <label
                    >Narration voice
                    <select
                      data-test="produce-voice"
                      (change)="produceVoice.set($any($event.target).value)"
                    >
                      <option value="" [selected]="produceVoice() === ''">Scout's choice</option>
                      @if (unavailableVoice(p); as id) {
                        <option [value]="id" [selected]="produceVoice() === id" disabled>
                          {{ id }} (not available on this machine)
                        </option>
                      }
                      @for (group of voiceGroups(); track group.source) {
                        <optgroup [label]="group.source">
                          @for (v of group.voices; track v.id) {
                            <option [value]="v.id" [selected]="produceVoice() === v.id">
                              {{ v.label }}
                            </option>
                          }
                        </optgroup>
                      }
                    </select>
                  </label>
                  <button
                    type="button"
                    class="btn btn--secondary"
                    data-test="produce-voice-preview"
                    [disabled]="voiceSample(produceVoice()) === null"
                    [attr.title]="voiceSample(produceVoice()) === null ? 'No sample yet' : null"
                    (click)="playSample(produceVoice())"
                  >
                    Preview
                  </button>
                }
                <label
                  >Direction
                  <textarea
                    rows="3"
                    data-test="produce-brief"
                    [value]="produceBrief()"
                    (input)="produceBrief.set($any($event.target).value)"
                  ></textarea>
                </label>
                <span class="meta" data-test="produce-brief-count"
                  >{{ produceBrief().trim().length }} / {{ maxDirection }}</span
                >
                @if (p.format === 'animated_character') {
                  <label
                    >Presenter
                    <textarea
                      rows="2"
                      data-test="produce-presenter"
                      [value]="producePresenter()"
                      (input)="producePresenter.set($any($event.target).value)"
                    ></textarea>
                  </label>
                  <span class="meta" data-test="produce-presenter-count"
                    >{{ producePresenter().trim().length }} / {{ maxPresenter }}</span
                  >
                }
                @if (p.format !== 'own_video') {
                  <label
                    >Motion
                    <select
                      data-test="produce-motion"
                      (change)="produceMotion.set($any($event.target).value)"
                    >
                      @for (m of motions; track m.value) {
                        <option [value]="m.value" [selected]="produceMotion() === m.value">
                          {{ m.label }}
                        </option>
                      }
                    </select>
                  </label>
                }
                @if (mediaWarning(); as warning) {
                  <p class="meta" data-test="produce-warning">{{ warning }}</p>
                }
                <div class="row">
                  <button
                    type="button"
                    class="btn btn--primary"
                    data-test="produce-confirm"
                    [disabled]="producing()"
                    (click)="confirmProduce(p)"
                  >
                    Produce
                  </button>
                  <button
                    type="button"
                    class="btn btn--secondary"
                    data-test="produce-cancel"
                    (click)="producePanel.set(null)"
                  >
                    Cancel
                  </button>
                </div>
              </div>
            }
          }
          @if (produceError(); as message) {
            <p class="err" data-test="produce-error">{{ message }}</p>
          }
          @if (p.status === 'producing') {
            <p class="meta" data-test="production-progress">
              {{ readyScenes(p) }} of {{ p.scenes.length }} scenes ready
            </p>
          }
          @if (p.status === 'transcribing') {
            <p class="meta" data-test="transcription-progress">
              Transcribed {{ clock(transcribedMs(p)) }} of {{ clock(p.recordingMs) }}
            </p>
          }
          @if (p.scenes.length === 0) {
            <p class="empty" data-test="scenes-empty">No scenes yet.</p>
          } @else {
            <ol class="claims">
              @for (s of orderedScenes(p); track s.id; let first = $first, last = $last) {
                <li class="claim" [attr.data-test]="'scene-' + s.id">
                  <p data-test="scene-narration">{{ s.narration }}</p>
                  @if (p.format === 'own_video') {
                    <p class="meta" [attr.data-test]="'scene-clip-' + s.id">
                      Clip: {{ clock(s.clipStartMs) }} to {{ clock(s.clipEndMs) }}
                    </p>
                  } @else if (p.format !== 'animated_character') {
                    <p class="meta">Picture prompt: {{ s.visualPrompt }}</p>
                  }
                  <p class="meta">
                    <span class="badge" [attr.data-test]="'scene-status-' + s.id">{{
                      s.status
                    }}</span>
                    <span [attr.data-test]="'scene-sources-' + s.id"
                      >Audio: {{ sourceLabel(s.audioSource) }} · Picture:
                      {{ sourceLabel(s.visualSource) }}</span
                    >
                  </p>
                  @if (s.visualCredit; as credit) {
                    <p class="meta" [attr.data-test]="'scene-credit-' + s.id">
                      Credit: {{ credit }}
                    </p>
                  }
                  @if (s.error; as note) {
                    @if (s.status === 'failed') {
                      <p class="err" [attr.data-test]="'scene-failure-' + s.id">{{ note }}</p>
                    } @else {
                      <p class="meta" [attr.data-test]="'scene-note-' + s.id">{{ note }}</p>
                    }
                  }
                  @if (s.visualSource !== 'text_card' && s.visualFile; as file) {
                    <img
                      class="preview"
                      [attr.data-test]="'scene-image-' + s.id"
                      [src]="mediaUrl(p, file)"
                      [alt]="'Picture of scene ' + s.order"
                    />
                  } @else {
                    <div
                      class="card"
                      [class.card--background]="p.backgroundFile !== null"
                      [attr.data-test]="'scene-preview-' + s.id"
                    >
                      @if (p.backgroundFile; as background) {
                        <img
                          class="card__image"
                          [attr.data-test]="'scene-preview-background-' + s.id"
                          [src]="mediaUrl(p, background)"
                          alt=""
                        />
                      }
                      @if (p.format === 'animated_character' && p.presenterFile; as presenter) {
                        <img
                          class="card__image card__image--presenter"
                          [attr.data-test]="'scene-preview-presenter-' + s.id"
                          [src]="mediaUrl(p, presenter)"
                          alt=""
                        />
                      } @else {
                        <p class="card__text" [attr.data-test]="'scene-preview-text-' + s.id">
                          {{ firstSentence(s.narration) }}
                        </p>
                      }
                    </div>
                  }
                  @if (p.format !== 'own_video') {
                    <div class="row" [attr.data-test]="'scene-audio-row-' + s.id">
                      @if (s.audioFile; as file) {
                        <audio
                          controls
                          [attr.data-test]="'scene-audio-' + s.id"
                          [src]="mediaUrl(p, file)"
                        ></audio>
                      }
                      <button
                        type="button"
                        class="btn btn--secondary"
                        [attr.data-test]="'scene-narration-regenerate-' + s.id"
                        [disabled]="s.locked || sceneBusy() !== null"
                        (click)="askRegenerateNarration(p, s)"
                      >
                        Regenerate narration
                      </button>
                      <button
                        type="button"
                        class="btn btn--secondary"
                        [attr.data-test]="'scene-narration-replace-' + s.id"
                        [disabled]="sceneBusy() !== null"
                        (click)="attach(p, s, 'audio')"
                      >
                        Replace narration
                      </button>
                    </div>
                  }
                  <div class="row">
                    <button
                      type="button"
                      class="btn btn--secondary"
                      [attr.data-test]="'scene-lock-' + s.id"
                      [disabled]="sceneBusy() !== null"
                      (click)="toggleLock(p, s)"
                    >
                      {{ s.locked ? 'Unlock' : 'Lock' }}
                    </button>
                    <button
                      type="button"
                      class="btn btn--secondary"
                      [attr.data-test]="'scene-up-' + s.id"
                      [disabled]="first || sceneBusy() !== null"
                      (click)="move(p, s, -1)"
                    >
                      Move up
                    </button>
                    <button
                      type="button"
                      class="btn btn--secondary"
                      [attr.data-test]="'scene-down-' + s.id"
                      [disabled]="last || sceneBusy() !== null"
                      (click)="move(p, s, 1)"
                    >
                      Move down
                    </button>
                    @if (p.format === 'generated_pictures') {
                      <button
                        type="button"
                        class="btn btn--secondary"
                        [attr.data-test]="'scene-regenerate-' + s.id"
                        [disabled]="s.locked || sceneBusy() !== null"
                        (click)="regenerate(p, s)"
                      >
                        Regenerate scene
                      </button>
                    }
                    <button
                      type="button"
                      class="btn btn--secondary"
                      [attr.data-test]="'scene-edit-' + s.id"
                      [disabled]="s.locked || sceneBusy() !== null"
                      (click)="openSceneText(s)"
                    >
                      Edit text
                    </button>
                    @if (p.format === 'own_pictures') {
                      <button
                        type="button"
                        class="btn btn--secondary"
                        [attr.data-test]="'scene-attach-visual-' + s.id"
                        [disabled]="sceneBusy() !== null"
                        (click)="attach(p, s, 'visual')"
                      >
                        Attach image
                      </button>
                    }
                  </div>
                  @if (editingScene() === s.id) {
                    <div class="panel">
                      @if (p.format === 'own_video') {
                        <p class="meta" [attr.data-test]="'scene-text-hint-' + s.id">
                          The new text replaces this scene's captions; the recording is not changed.
                        </p>
                      }
                      <textarea
                        rows="4"
                        [attr.data-test]="'scene-text-' + s.id"
                        [value]="sceneText()"
                        (input)="sceneText.set($any($event.target).value)"
                      ></textarea>
                      <span class="meta" [attr.data-test]="'scene-text-count-' + s.id"
                        >{{ sceneText().trim().length }} / {{ maxSceneText }}</span
                      >
                      @if (sceneTextInvalid()) {
                        <p class="err" [attr.data-test]="'scene-text-invalid-' + s.id">
                          A scene's text is 1 to 700 characters.
                        </p>
                      }
                      <div class="row">
                        <button
                          type="button"
                          class="btn btn--primary"
                          [attr.data-test]="'scene-text-save-' + s.id"
                          [disabled]="
                            sceneTextInvalid() ||
                            sceneText().trim() === s.narration ||
                            sceneBusy() !== null
                          "
                          (click)="saveSceneText(p, s)"
                        >
                          Save
                        </button>
                        <button
                          type="button"
                          class="btn btn--secondary"
                          [attr.data-test]="'scene-text-cancel-' + s.id"
                          (click)="editingScene.set(null)"
                        >
                          Cancel
                        </button>
                      </div>
                    </div>
                  }
                </li>
              }
            </ol>
          }
          @if (sceneError(); as message) {
            <p class="err" data-test="scene-error">{{ message }}</p>
          }
        </section>

        @if (replacingNarration(); as replacing) {
          <div
            class="backdrop"
            role="dialog"
            aria-modal="true"
            aria-labelledby="narration-replace-title"
            data-test="narration-replace-dialog"
          >
            <div class="dialog">
              <h2 id="narration-replace-title">Replace your own narration</h2>
              <p>
                Replace your own narration on this scene with Scout's voice? Your file stays on disk
                but the video will use the generated narration.
              </p>
              <div class="row">
                <button
                  type="button"
                  class="btn btn--secondary"
                  data-test="narration-replace-cancel"
                  (click)="replacingNarration.set(null)"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  class="btn btn--primary"
                  data-test="narration-replace-confirm"
                  (click)="confirmRegenerateNarration(p, replacing)"
                >
                  Confirm
                </button>
              </div>
            </div>
          </div>
        }

        @if (deleting() === p.id) {
          <div
            class="backdrop"
            role="dialog"
            aria-modal="true"
            aria-labelledby="delete-title"
            data-test="delete-dialog"
          >
            <div class="dialog">
              <h2 id="delete-title">Delete {{ p.topic }}</h2>
              <p>
                This removes the project, its script, scenes and media files from Scout's data
                folder. A video already on YouTube stays there.
              </p>
              @if (deleteError(); as message) {
                <p class="err" data-test="delete-error">{{ message }}</p>
              }
              <div class="row">
                <button
                  type="button"
                  class="btn btn--secondary"
                  data-test="delete-cancel"
                  (click)="deleting.set(null)"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  class="btn btn--primary"
                  data-test="delete-confirm"
                  [disabled]="deletingBusy()"
                  (click)="confirmDelete(p)"
                >
                  Confirm and delete
                </button>
              </div>
            </div>
          </div>
        }

        @if (delegating() === p.id) {
          <div
            class="backdrop"
            role="dialog"
            aria-modal="true"
            aria-labelledby="delegate-title"
            data-test="delegate-dialog"
          >
            <div class="dialog">
              <h2 id="delegate-title">Delegate {{ p.topic }}</h2>
              @if (p.format !== 'own_video') {
                <label
                  >Narration voice
                  <select
                    data-test="delegate-voice"
                    (change)="delegateVoice.set($any($event.target).value)"
                  >
                    <option value="" [selected]="delegateVoice() === ''">Scout's choice</option>
                    @if (unavailableVoice(p); as id) {
                      <option [value]="id" [selected]="delegateVoice() === id" disabled>
                        {{ id }} (not available on this machine)
                      </option>
                    }
                    @for (group of voiceGroups(); track group.source) {
                      <optgroup [label]="group.source">
                        @for (v of group.voices; track v.id) {
                          <option [value]="v.id" [selected]="delegateVoice() === v.id">
                            {{ v.label }}
                          </option>
                        }
                      </optgroup>
                    }
                  </select>
                </label>
                <button
                  type="button"
                  class="btn btn--secondary"
                  data-test="delegate-voice-preview"
                  [disabled]="voiceSample(delegateVoice()) === null"
                  [attr.title]="voiceSample(delegateVoice()) === null ? 'No sample yet' : null"
                  (click)="playSample(delegateVoice())"
                >
                  Preview
                </button>
              }
              <label
                >Direction
                <textarea
                  rows="3"
                  data-test="delegate-brief"
                  [value]="delegateBrief()"
                  (input)="delegateBrief.set($any($event.target).value)"
                ></textarea>
              </label>
              <span class="meta" data-test="delegate-brief-count"
                >{{ delegateBrief().trim().length }} / {{ maxDirection }}</span
              >
              @if (p.format === 'animated_character') {
                <label
                  >Presenter
                  <textarea
                    rows="2"
                    data-test="delegate-presenter"
                    [value]="delegatePresenter()"
                    (input)="delegatePresenter.set($any($event.target).value)"
                  ></textarea>
                </label>
                <span class="meta" data-test="delegate-presenter-count"
                  >{{ delegatePresenter().trim().length }} / {{ maxPresenter }}</span
                >
              }
              @if (p.format !== 'own_video') {
                <label
                  >Motion
                  <select
                    data-test="delegate-motion"
                    (change)="delegateMotion.set($any($event.target).value)"
                  >
                    @for (m of motions; track m.value) {
                      <option [value]="m.value" [selected]="delegateMotion() === m.value">
                        {{ m.label }}
                      </option>
                    }
                  </select>
                </label>
              }
              @if (mediaWarning(); as warning) {
                <p class="meta" data-test="delegate-warning">{{ warning }}</p>
              }
              <h3>What will run</h3>
              <ul data-test="delegate-steps">
                <li>Produce media</li>
                <li>Assemble the video</li>
                <li>Stop at ready to approve: publishing needs your approval</li>
              </ul>
              @if (delegateError(); as message) {
                <p class="err" data-test="delegate-error">{{ message }}</p>
              }
              <div class="row">
                <button
                  type="button"
                  class="btn btn--secondary"
                  data-test="delegate-cancel"
                  (click)="delegating.set(null)"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  class="btn btn--primary"
                  data-test="delegate-confirm"
                  [disabled]="delegatingBusy()"
                  (click)="confirmDelegate(p)"
                >
                  Confirm and delegate
                </button>
              </div>
            </div>
          </div>
        }
      }
      <audio #samplePlayer data-test="voice-sample-player"></audio>
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .page {
        padding: 32px;
        max-width: 1080px;
        margin: 0 auto;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
        color: #e4e8f0;
        font-size: 13px;
      }
      .header {
        margin-bottom: 24px;
      }
      h1 {
        margin: 0 0 8px;
        font-size: 24px;
      }
      h2 {
        margin: 0 0 12px;
        font-size: 16px;
      }
      h3 {
        margin: 16px 0 8px;
        font-size: 14px;
      }
      h4 {
        margin: 8px 0 4px;
        font-size: 13px;
      }
      .lede,
      .empty,
      .meta {
        margin: 0;
        color: #7b8299;
      }
      .err {
        color: #f87171;
      }
      .block {
        margin-bottom: 32px;
      }
      .form {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 12px;
        margin: 12px 0;
      }
      label,
      fieldset {
        display: flex;
        flex-direction: column;
        gap: 4px;
        color: #7b8299;
      }
      fieldset {
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 8px;
        grid-column: span 2;
      }
      input,
      select,
      textarea {
        background: #0f1117;
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 6px 8px;
        color: #e4e8f0;
        font-size: 13px;
      }
      .row {
        display: flex;
        gap: 8px;
        align-items: center;
        margin-top: 8px;
      }
      .grow {
        flex: 1;
      }
      .projects,
      .plain,
      .claims {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .project {
        width: 100%;
        display: flex;
        gap: 8px;
        align-items: center;
        text-align: left;
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 8px 12px;
        color: #e4e8f0;
        cursor: pointer;
      }
      .project--selected {
        border-color: #2563eb;
      }
      .claim {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 12px 16px;
      }
      .claim p {
        margin: 0 0 4px;
      }
      .preview {
        display: block;
        max-width: 320px;
        border-radius: 6px;
        margin: 8px 0;
      }
      .card {
        position: relative;
        display: flex;
        align-items: center;
        justify-content: center;
        width: 320px;
        aspect-ratio: 16 / 9;
        margin: 8px 0;
        border-radius: 6px;
        overflow: hidden;
        background: #14181f;
      }
      .card--background {
        align-items: flex-end;
      }
      .card__image {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        object-fit: cover;
      }
      .card__image--presenter {
        object-fit: contain;
      }
      .mouth-area {
        position: relative;
        display: inline-block;
        margin: 8px 0;
        cursor: crosshair;
        touch-action: none;
        user-select: none;
      }
      .mouth-area__image {
        display: block;
        max-width: 320px;
        border-radius: 6px;
      }
      .mouth {
        position: absolute;
        border: 2px solid #22c55e;
        box-sizing: border-box;
        pointer-events: none;
      }
      .mouth--drawn {
        border-style: dashed;
        border-color: #facc15;
      }
      .card p {
        position: relative;
        margin: 0;
        padding: 8px 12px;
        color: #ffffff;
        text-align: center;
      }
      .panel {
        display: flex;
        flex-direction: column;
        gap: 8px;
        margin-top: 8px;
        padding: 12px 16px;
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
      }
      .backdrop {
        position: fixed;
        inset: 0;
        background: rgba(0, 0, 0, 0.6);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .dialog {
        background: #11131a;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 20px;
        max-width: 560px;
        width: 100%;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .source {
        display: flex;
        gap: 8px;
        align-items: center;
      }
      .badge {
        border: 1px solid #252b3b;
        border-radius: 999px;
        padding: 0 8px;
        font-size: 12px;
      }
      code {
        font-family: 'JetBrains Mono', ui-monospace, monospace;
        font-size: 12px;
        word-break: break-all;
      }
      .btn {
        font-size: 13px;
        padding: 6px 12px;
        border-radius: 6px;
        border: 1px solid transparent;
        cursor: pointer;
      }
      .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
      .btn--primary {
        background: #2563eb;
        color: white;
      }
      .btn--secondary {
        background: transparent;
        color: #e4e8f0;
        border-color: #252b3b;
      }
    `,
  ],
})
export class ProductionComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private timer: ReturnType<typeof setInterval> | null = null;

  readonly pillarSlots = PILLAR_SLOTS;
  readonly metrics = TARGET_METRICS;
  readonly minTopicLength = MIN_TOPIC_LENGTH;

  readonly error = signal<string | null>(null);

  readonly mandate = signal<MandateDto | null>(null);
  readonly mandateLoaded = signal(false);
  readonly mandateError = signal<string | null>(null);
  readonly savingMandate = signal(false);
  readonly objective = signal('');
  readonly audience = signal('');
  readonly pillars = signal<readonly string[]>(['', '', '', '', '']);
  readonly cadence = signal('');
  readonly language = signal('');
  readonly voiceAndStyle = signal('');
  readonly blockedTopics = signal('');
  readonly targetMetric = signal('');
  readonly targetValue = signal('');

  readonly topic = signal('');
  readonly startFormat = signal<ProjectFormat>('generated_pictures');
  readonly startDirection = signal('');
  readonly startPresenter = signal('');
  readonly starting = signal(false);
  readonly startError = signal<string | null>(null);
  readonly queued = signal<OperatorProjectDto | null>(null);

  readonly projects = signal<readonly OperatorProjectDto[]>([]);
  readonly selectedId = signal<string | null>(null);
  private readonly detail = signal<OperatorProjectDto | null>(null);
  readonly selected = computed(() => {
    const detail = this.detail();
    return detail !== null && detail.id === this.selectedId() ? detail : null;
  });

  readonly waiving = signal<string | null>(null);
  readonly waiveNote = signal('');
  readonly deciding = signal<string | null>(null);
  readonly claimError = signal<string | null>(null);

  readonly readiness = signal<ReadinessDto | null>(null);
  readonly readinessError = signal<string | null>(null);
  readonly transcribable = computed(() => this.readiness()?.transcriptionAvailable === true);
  readonly lipSyncable = computed(() => this.readiness()?.lipSyncAvailable === true);
  readonly noPictures = computed(() => this.readiness()?.imagesAvailable === false);
  readonly voiceGroups = computed(() => {
    const groups = new Map<string, VoiceDto[]>();
    for (const voice of this.readiness()?.voices ?? []) {
      groups.set(voice.source, [...(groups.get(voice.source) ?? []), voice]);
    }
    return Array.from(groups, ([source, voices]) => ({ source, voices }));
  });
  readonly mediaWarning = computed(() => {
    const r = this.readiness();
    if (r === null) return null;
    const format = this.selected()?.format;
    if (format === 'own_video') return null;
    const provider = r.provider || 'The connected AI Source';
    if (format === 'animated_character') {
      if (!r.speechAvailable) {
        return `${provider} has no speech service in Scout: the character will be silent. Replace the narration on each scene afterwards, or connect an AI Source with speech first.`;
      }
      if (!r.imagesAvailable && !this.selected()?.presenterFile) {
        return `${provider} has no image service in Scout: attach a presenter picture, or every scene will be a text card.`;
      }
      return null;
    }
    const silent = `${provider} has no speech service in Scout: every scene will be silent. Replace the narration on each scene afterwards, or connect an AI Source with speech first.`;
    if (format === 'own_pictures') return r.speechAvailable ? null : silent;
    if (r.speechAvailable && (r.imagesAvailable || r.stockPictures)) return null;
    if (!r.speechAvailable && !r.imagesAvailable && r.stockPictures) {
      return `${provider} has no speech or image service in Scout: every scene will be silent and pictures come from Openverse, credited. Replace the narration on each scene afterwards, or connect an AI Source with speech first.`;
    }
    if (!r.speechAvailable && !r.imagesAvailable) {
      return `${provider} has no speech or image service in Scout: every scene will be a silent text card. Replace the narration on each scene and attach a background afterwards, or connect an AI Source with speech and pictures first.`;
    }
    if (!r.speechAvailable) return silent;
    return `${provider} has no image service in Scout: every scene will be a text card. Attach a background afterwards, or connect an AI Source with pictures first.`;
  });
  readonly motions = MOTIONS;
  readonly maxDirection = MAX_DIRECTION;
  readonly maxPresenter = MAX_PRESENTER;
  readonly maxSceneText = MAX_SCENE_TEXT;
  private readonly samplePlayer = viewChild<ElementRef<HTMLAudioElement>>('samplePlayer');
  readonly producing = signal(false);
  readonly produceError = signal<string | null>(null);
  readonly producePanel = signal<string | null>(null);
  readonly produceVoice = signal('');
  readonly produceBrief = signal('');
  readonly produceMotion = signal<ProjectMotion>('gentle');
  readonly producePresenter = signal('');
  readonly delegating = signal<string | null>(null);
  readonly delegateVoice = signal('');
  readonly delegateBrief = signal('');
  readonly delegateMotion = signal<ProjectMotion>('gentle');
  readonly delegatePresenter = signal('');
  readonly delegatingBusy = signal(false);
  readonly delegateError = signal<string | null>(null);
  readonly deleting = signal<string | null>(null);
  readonly replacingNarration = signal<SceneDto | null>(null);
  readonly deletingBusy = signal(false);
  readonly deleteError = signal<string | null>(null);
  readonly projectMediaBusy = signal(false);
  readonly projectMediaError = signal<string | null>(null);
  readonly presenterError = signal<string | null>(null);
  readonly mouthDraft = signal<MouthBoxDto | null>(null);
  readonly mouthDraftValid = computed(() => {
    const box = this.mouthDraft();
    return box !== null && box.width >= MIN_MOUTH_SIZE && box.height >= MIN_MOUTH_SIZE;
  });
  private mouthStart: { readonly x: number; readonly y: number } | null = null;
  readonly sceneBusy = signal<string | null>(null);
  readonly sceneError = signal<string | null>(null);
  readonly editingScene = signal<string | null>(null);
  readonly sceneText = signal('');
  readonly sceneTextInvalid = computed(() => {
    const length = this.sceneText().trim().length;
    return length === 0 || length > MAX_SCENE_TEXT;
  });

  async ngOnInit(): Promise<void> {
    this.timer = setInterval(() => void this.refresh(), POLL_MS);
    await Promise.all([this.loadMandate(), this.refresh(), this.loadReadiness()]);
  }

  ngOnDestroy(): void {
    if (this.timer !== null) clearInterval(this.timer);
  }

  setPillar(slot: number, value: string): void {
    this.pillars.update((current) => current.map((p, i) => (i === slot ? value : p)));
  }

  async saveMandate(): Promise<void> {
    const trimmedList = (values: readonly string[]): string[] =>
      values.map((v) => v.trim()).filter((v) => v.length > 0);
    this.savingMandate.set(true);
    this.mandateError.set(null);
    try {
      const saved = await this.bridge.operatorSaveMandate({
        objective: this.objective().trim(),
        audience: this.audience().trim(),
        pillars: trimmedList(this.pillars()),
        cadence: this.cadence().trim(),
        language: this.language().trim(),
        voiceAndStyle: this.voiceAndStyle().trim(),
        blockedTopics: trimmedList(this.blockedTopics().split(',')),
        targetMetric: this.targetMetric(),
        targetValue: this.targetValue().trim(),
      });
      this.mandate.set(saved);
    } catch (err: unknown) {
      this.mandateError.set(readIpcError(err).message);
    } finally {
      this.savingMandate.set(false);
    }
  }

  async startProject(): Promise<void> {
    this.starting.set(true);
    this.startError.set(null);
    try {
      const project = await this.bridge.operatorStartProject({
        topic: this.topic().trim(),
        format: this.startFormat(),
        direction: this.startDirection().trim(),
        presenter: this.startFormat() === 'animated_character' ? this.startPresenter().trim() : '',
      });
      this.queued.set(project);
      this.topic.set('');
      this.startFormat.set('generated_pictures');
      this.startDirection.set('');
      this.startPresenter.set('');
      await this.refresh();
    } catch (err: unknown) {
      this.startError.set(readIpcError(err).message);
    } finally {
      this.starting.set(false);
    }
  }

  async select(id: string): Promise<void> {
    this.selectedId.set(id);
    this.waiving.set(null);
    this.claimError.set(null);
    this.produceError.set(null);
    this.producePanel.set(null);
    this.delegating.set(null);
    this.deleting.set(null);
    this.replacingNarration.set(null);
    this.projectMediaError.set(null);
    this.presenterError.set(null);
    this.mouthDraft.set(null);
    this.mouthStart = null;
    this.sceneError.set(null);
    this.editingScene.set(null);
    await this.loadSelected();
  }

  openWaive(claimId: string): void {
    this.waiving.set(claimId);
    this.waiveNote.set('');
  }

  async decide(project: OperatorProjectDto, claim: ClaimDto, status: ClaimStatus): Promise<void> {
    const note = this.waiveNote().trim();
    if (status === 'waived' && note.length === 0) return;
    const req =
      status === 'waived'
        ? { projectId: project.id, claimId: claim.id, status, note }
        : { projectId: project.id, claimId: claim.id, status };
    this.deciding.set(claim.id);
    this.claimError.set(null);
    try {
      this.apply(await this.bridge.operatorDecideClaim(req));
      this.waiving.set(null);
    } catch (err: unknown) {
      this.claimError.set(readIpcError(err).message);
    } finally {
      this.deciding.set(null);
    }
  }

  producible(project: OperatorProjectDto): boolean {
    return (
      PRODUCIBLE.includes(project.status) ||
      (project.status === 'failed' && project.script !== null)
    );
  }

  pendingClaims(project: OperatorProjectDto): number {
    return project.claims.filter((c) => c.status === 'pending').length;
  }

  readyScenes(project: OperatorProjectDto): number {
    return project.scenes.filter((s) => s.status === 'ready').length;
  }

  orderedScenes(project: OperatorProjectDto): SceneDto[] {
    return [...project.scenes].sort((a, b) => a.order - b.order);
  }

  picturesLine(r: ReadinessDto): string {
    if (r.imagesAvailable)
      return `Pictures: generated by ${r.provider || 'the connected AI Source'}`;
    if (r.stockPictures) {
      return 'Pictures: from Openverse (CC0, public domain or CC BY, credited in the description)';
    }
    return 'Pictures: text cards unless you attach your own';
  }

  sourceLabel(source: string): string {
    return SOURCE_LABELS[source] ?? source;
  }

  mediaUrl(project: OperatorProjectDto, fileName: string): string {
    return `scout-media://${project.id}/${encodeURIComponent(fileName)}?v=${encodeURIComponent(project.updatedAt)}`;
  }

  delegable(project: OperatorProjectDto): boolean {
    return (
      DELEGABLE.includes(project.status) || (project.status === 'failed' && project.script !== null)
    );
  }

  deletable(project: OperatorProjectDto): boolean {
    return !RUNNING.includes(project.status) && project.publication?.status !== 'uploading';
  }

  backgroundAttachable(project: OperatorProjectDto): boolean {
    return BACKGROUND_ATTACHABLE.includes(project.status);
  }

  recordingAttachable(project: OperatorProjectDto): boolean {
    return (
      RECORDING_ATTACHABLE.includes(project.status) ||
      (project.status === 'failed' && project.script === null)
    );
  }

  clock(ms: number): string {
    const seconds = Math.floor(ms / 1000);
    const ss = String(seconds % 60).padStart(2, '0');
    if (seconds < 3600) return `${Math.floor(seconds / 60)}:${ss}`;
    return `${Math.floor(seconds / 3600)}:${String(Math.floor(seconds / 60) % 60).padStart(2, '0')}:${ss}`;
  }

  transcribedMs(project: OperatorProjectDto): number {
    return project.script?.sections.at(-1)?.endMs ?? 0;
  }

  firstSentence(narration: string): string {
    const text = narration
      .replace(/\p{Cc}/gu, ' ')
      .replace(/\s+/g, ' ')
      .trim();
    const end = /[.!?](?=\s|$)/.exec(text);
    return end === null ? text : text.slice(0, end.index + 1);
  }

  voiceLabel(id: string): string {
    if (id === '') return "Scout's choice";
    return this.readiness()?.voices.find((v) => v.id === id)?.label ?? id;
  }

  unavailableVoice(project: OperatorProjectDto): string | null {
    const id = project.narrationVoice;
    return id !== '' && this.readiness()?.voices.some((v) => v.id === id) !== true ? id : null;
  }

  formatName(format: ProjectFormat): string {
    return FORMAT_NAMES[format];
  }

  motionName(motion: ProjectMotion): string {
    return MOTION_NAMES[motion];
  }

  /**
   * The sample of a voice. For Scout's choice: none while a provider voice is listed (the video uses the provider's
   * speech), else Cori on Piper, else the first Piper voice.
   */
  voiceSample(voiceId: string): string | null {
    const voices = this.readiness()?.voices ?? [];
    if (voiceId !== '') return voices.find((v) => v.id === voiceId)?.sampleFile ?? null;
    if (voices.some((v) => v.source !== 'piper')) return null;
    const piper = voices.filter((v) => v.source === 'piper');
    return (piper.find((v) => v.id.includes('cori')) ?? piper[0])?.sampleFile ?? null;
  }

  playSample(voiceId: string): void {
    const file = this.voiceSample(voiceId);
    const player = this.samplePlayer()?.nativeElement;
    if (file === null || player === undefined) return;
    player.src = `scout-media://voice-samples/${encodeURIComponent(file)}`;
    void player.play().catch(() => undefined);
  }

  private briefOf(project: OperatorProjectDto): string {
    return project.direction || (this.mandate()?.voiceAndStyle ?? '');
  }

  openProduce(project: OperatorProjectDto): void {
    this.produceVoice.set(project.narrationVoice);
    this.produceBrief.set(this.briefOf(project));
    this.produceMotion.set(project.motion);
    this.producePresenter.set(project.presenter);
    this.produceError.set(null);
    this.producePanel.set(project.id);
  }

  async confirmProduce(project: OperatorProjectDto): Promise<void> {
    this.producing.set(true);
    this.produceError.set(null);
    try {
      this.apply(
        await this.bridge.operatorProduce({
          id: project.id,
          narrationVoice: this.produceVoice(),
          direction: this.produceBrief().trim(),
          motion: this.produceMotion(),
          presenter: project.format === 'animated_character' ? this.producePresenter().trim() : '',
        }),
      );
      this.producePanel.set(null);
    } catch (err: unknown) {
      this.produceError.set(readIpcError(err).message);
    } finally {
      this.producing.set(false);
    }
  }

  openDelegate(project: OperatorProjectDto): void {
    this.delegateVoice.set(project.narrationVoice);
    this.delegateBrief.set(this.briefOf(project));
    this.delegateMotion.set(project.motion);
    this.delegatePresenter.set(project.presenter);
    this.delegateError.set(null);
    this.delegating.set(project.id);
  }

  async confirmDelegate(project: OperatorProjectDto): Promise<void> {
    this.delegatingBusy.set(true);
    this.delegateError.set(null);
    try {
      this.apply(
        await this.bridge.operatorDelegate({
          id: project.id,
          narrationVoice: this.delegateVoice(),
          direction: this.delegateBrief().trim(),
          motion: this.delegateMotion(),
          presenter: project.format === 'animated_character' ? this.delegatePresenter().trim() : '',
        }),
      );
      this.delegating.set(null);
    } catch (err: unknown) {
      this.delegateError.set(readIpcError(err).message);
    } finally {
      this.delegatingBusy.set(false);
    }
  }

  openDelete(project: OperatorProjectDto): void {
    this.deleteError.set(null);
    this.deleting.set(project.id);
  }

  async confirmDelete(project: OperatorProjectDto): Promise<void> {
    this.deletingBusy.set(true);
    this.deleteError.set(null);
    try {
      await this.bridge.operatorDeleteProject({ id: project.id });
      this.deleting.set(null);
      this.selectedId.set(null);
      this.detail.set(null);
      this.projects.update((list) => list.filter((p) => p.id !== project.id));
      await this.refresh();
    } catch (err: unknown) {
      this.deleteError.set(readIpcError(err).message);
    } finally {
      this.deletingBusy.set(false);
    }
  }

  attachProjectMedia(
    project: OperatorProjectDto,
    kind: 'background' | 'recording' | 'presenter',
  ): Promise<void> {
    return this.projectMediaAction(
      kind === 'presenter' ? this.presenterError : this.projectMediaError,
      async () =>
        (
          await this.bridge.operatorAttachSceneMedia({
            projectId: project.id,
            sceneId: '',
            kind,
          })
        ).project,
    );
  }

  regeneratePresenter(project: OperatorProjectDto): Promise<void> {
    return this.projectMediaAction(this.presenterError, () =>
      this.bridge.operatorRegeneratePresenter({ id: project.id }),
    );
  }

  startMouth(event: PointerEvent): void {
    const at = this.pointOnPicture(event);
    if (at === null) return;
    event.preventDefault();
    (event.currentTarget as Element).setPointerCapture(event.pointerId);
    this.mouthStart = at;
    this.mouthDraft.set(this.boxBetween(at, at));
  }

  moveMouth(event: PointerEvent): void {
    const at = this.mouthStart === null ? null : this.pointOnPicture(event);
    if (this.mouthStart === null || at === null) return;
    this.mouthDraft.set(this.boxBetween(this.mouthStart, at));
  }

  endMouth(event: PointerEvent): void {
    this.moveMouth(event);
    this.mouthStart = null;
  }

  cancelMouth(): void {
    this.mouthStart = null;
  }

  saveMouth(project: OperatorProjectDto): Promise<void> {
    const mouthBox = this.mouthDraft();
    if (mouthBox === null) return Promise.resolve();
    return this.projectMediaAction(this.presenterError, async () => {
      const updated = await this.bridge.operatorSetMouthBox({ projectId: project.id, mouthBox });
      this.mouthDraft.set(null);
      return updated;
    });
  }

  /** The pointer on the presenter picture in thousandths of its rendered width and height, clamped to the picture. */
  private pointOnPicture(event: MouseEvent): { x: number; y: number } | null {
    const image = (event.currentTarget as HTMLElement | null)?.querySelector('img');
    const rect = image?.getBoundingClientRect();
    if (rect === undefined || rect.width <= 0 || rect.height <= 0) return null;
    const thousandths = (offset: number, size: number): number =>
      Math.round(Math.min(Math.max(offset / size, 0), 1) * 1000);
    return {
      x: thousandths(event.clientX - rect.left, rect.width),
      y: thousandths(event.clientY - rect.top, rect.height),
    };
  }

  private boxBetween(a: { x: number; y: number }, b: { x: number; y: number }): MouthBoxDto {
    return {
      x: Math.min(a.x, b.x) / 1000,
      y: Math.min(a.y, b.y) / 1000,
      width: Math.abs(a.x - b.x) / 1000,
      height: Math.abs(a.y - b.y) / 1000,
    };
  }

  private async projectMediaAction(
    failure: { set(message: string | null): void },
    action: () => Promise<OperatorProjectDto | null>,
  ): Promise<void> {
    this.projectMediaBusy.set(true);
    failure.set(null);
    try {
      const updated = await action();
      if (updated !== null) this.apply(updated);
    } catch (err: unknown) {
      failure.set(readIpcError(err).message);
    } finally {
      this.projectMediaBusy.set(false);
    }
  }

  toggleLock(project: OperatorProjectDto, scene: SceneDto): Promise<void> {
    return this.sceneAction(scene.id, () =>
      this.bridge.operatorSetSceneLocked({
        projectId: project.id,
        sceneId: scene.id,
        locked: !scene.locked,
      }),
    );
  }

  move(project: OperatorProjectDto, scene: SceneDto, step: -1 | 1): Promise<void> {
    const ids = this.orderedScenes(project).map((s) => s.id);
    const from = ids.indexOf(scene.id);
    [ids[from], ids[from + step]] = [ids[from + step]!, ids[from]!];
    return this.sceneAction(scene.id, () =>
      this.bridge.operatorReorderScenes({ projectId: project.id, sceneIds: ids }),
    );
  }

  regenerate(project: OperatorProjectDto, scene: SceneDto): Promise<void> {
    return this.sceneAction(scene.id, () =>
      this.bridge.operatorRegenerateScene({ projectId: project.id, sceneId: scene.id }),
    );
  }

  askRegenerateNarration(project: OperatorProjectDto, scene: SceneDto): Promise<void> {
    if (scene.audioSource !== 'owner') return this.regenerateNarration(project, scene);
    this.replacingNarration.set(scene);
    return Promise.resolve();
  }

  confirmRegenerateNarration(project: OperatorProjectDto, scene: SceneDto): Promise<void> {
    this.replacingNarration.set(null);
    return this.regenerateNarration(project, scene);
  }

  regenerateNarration(project: OperatorProjectDto, scene: SceneDto): Promise<void> {
    return this.sceneAction(scene.id, () =>
      this.bridge.operatorRegenerateNarration({ projectId: project.id, sceneId: scene.id }),
    );
  }

  openSceneText(scene: SceneDto): void {
    this.sceneText.set(scene.narration);
    this.sceneError.set(null);
    this.editingScene.set(scene.id);
  }

  saveSceneText(project: OperatorProjectDto, scene: SceneDto): Promise<void> {
    const text = this.sceneText().trim();
    return this.sceneAction(scene.id, async () => {
      const updated = await this.bridge.operatorEditSceneText({
        projectId: project.id,
        sceneId: scene.id,
        text,
      });
      this.editingScene.set(null);
      return updated;
    });
  }

  attach(project: OperatorProjectDto, scene: SceneDto, kind: SceneMediaKind): Promise<void> {
    return this.sceneAction(scene.id, async () => {
      const { project: updated } = await this.bridge.operatorAttachSceneMedia({
        projectId: project.id,
        sceneId: scene.id,
        kind,
      });
      return updated;
    });
  }

  copy(url: string): void {
    void navigator.clipboard?.writeText(url).catch(() => undefined);
  }

  private apply(updated: OperatorProjectDto): void {
    this.detail.set(updated);
    this.projects.update((all) => all.map((p) => (p.id === updated.id ? updated : p)));
  }

  private async sceneAction(
    sceneId: string,
    action: () => Promise<OperatorProjectDto | null>,
  ): Promise<void> {
    this.sceneBusy.set(sceneId);
    this.sceneError.set(null);
    try {
      const updated = await action();
      if (updated !== null) this.apply(updated);
    } catch (err: unknown) {
      this.sceneError.set(readIpcError(err).message);
    } finally {
      this.sceneBusy.set(null);
    }
  }

  private async loadReadiness(): Promise<void> {
    try {
      this.readiness.set(await this.bridge.operatorReadiness());
    } catch (err: unknown) {
      this.readinessError.set(readIpcError(err).message);
    }
  }

  private fill(mandate: MandateDto): void {
    this.objective.set(mandate.objective);
    this.audience.set(mandate.audience);
    this.pillars.set(PILLAR_SLOTS.map((i) => mandate.pillars[i] ?? ''));
    this.cadence.set(mandate.cadence);
    this.language.set(mandate.language);
    this.voiceAndStyle.set(mandate.voiceAndStyle);
    this.blockedTopics.set(mandate.blockedTopics.join(', '));
    this.targetMetric.set(mandate.targetMetric);
    this.targetValue.set(mandate.targetValue);
  }

  private async loadMandate(): Promise<void> {
    try {
      const { mandate } = await this.bridge.operatorMandate();
      this.mandate.set(mandate);
      if (mandate !== null) this.fill(mandate);
      this.mandateLoaded.set(true);
    } catch (err: unknown) {
      this.mandateError.set(readIpcError(err).message);
    }
  }

  private async refresh(): Promise<void> {
    try {
      const { projects } = await this.bridge.operatorProjects({ limit: PROJECTS_SHOWN });
      this.projects.set(
        [...projects].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)),
      );
      const id = this.selectedId();
      const listed = id === null ? undefined : projects.find((p) => p.id === id);
      if (listed !== undefined) this.detail.set(listed);
      if (id !== null && listed === undefined) await this.loadSelected();
      else this.error.set(null);
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    }
  }

  private async loadSelected(): Promise<void> {
    const id = this.selectedId();
    if (id === null) return;
    try {
      const project = await this.bridge.operatorProject({ id });
      if (this.selectedId() === id) this.detail.set(project);
      this.error.set(null);
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    }
  }
}
