import { DatePipe, NgTemplateOutlet, PercentPipe } from '@angular/common';
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
  type PresenterVersionDto,
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
const RUNNING = [
  'researching',
  'scripting',
  'transcribing',
  'producing',
  'assembling',
  'presenting',
];
const RECORDING_ATTACHABLE = ['recording_waiting', 'queued'];
const PRESENTER_REQUESTABLE = [
  'script_ready',
  'media_ready',
  'assembled',
  'approved',
  'assembly_queued',
];

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
const MAX_PRESENTER_PROMPT = 1500;
const MAX_PRESENTER_FEEDBACK = 300;
const MAX_PICTURE_PROMPT = 700;
const DAILY_PICTURES = 40;
const VERSION_SUMMARY = 60;

const PRESENTER_STYLE =
  'flat 2D cartoon illustration, front-facing, looking at the viewer, mouth closed, neutral expression, head and shoulders, on a solid bright green background (#00FF00)';
const PRESENTER_SAFETY =
  "no text, no letters, no logos, no trademarks, no real person's likeness and no public figure.";

const PRESENTER_SOURCES: Readonly<Record<string, string>> = {
  generated: 'Generated',
  builtin: "Scout's presenter",
  owner: 'Your picture',
};

const DEFAULT_FORMATS: readonly ProjectFormat[] = [
  'generated_pictures',
  'own_pictures',
  'animated_character',
  'own_video',
];

const HELP = {
  mandate:
    'What the channel is for: who it speaks to, what it covers and how it sounds. Scout reads it before every topic, script and picture.',
  makeAVideo:
    'One topic starts a project. Scout researches it, writes the script, then waits for you at every step that costs something or goes public.',
  topicCandidates:
    'The angles Scout found for this topic before writing. They stay here for reference once the script exists.',
  script:
    "The hook, the sections and the close, in the words the narration will say. Edit a scene's text below to change it.",
  claims:
    'Facts the script states that a viewer could dispute. Scout looked for a source for each one. Supported keeps the line; Unsupported rewrites without it; Waive keeps it without a source, with your reason. Production waits until every claim is decided.',
  presenter:
    'The character that speaks the whole video. Scout makes it from the script as soon as the script is ready, finds the mouth on the picture and shows a short preview. Ask for changes in plain words; every request makes a new version you can switch back to.',
  scenes:
    'The video, one scene per sentence group: its narration, its picture and the presenter over it. Produce makes the narration and pictures; Assemble joins them into the file.',
  background:
    'The picture behind any scene that has none of its own. Without it, that scene is a dark card with its text.',
  recording:
    'The video you recorded. Scout transcribes it on this machine and cuts the scenes from it; nothing is uploaded.',
} as const;

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
  imports: [DatePipe, NgTemplateOutlet, PercentPipe],
  template: `
    <main class="page">
      <header class="header">
        <h1>Production</h1>
        <p class="lede">What the channel is for, the videos Scout is making, and what to check.</p>
      </header>

      @if (error(); as message) {
        <p class="err" data-test="production-error">{{ message }}</p>
      }

      <details class="block channel" data-test="channel" [open]="mandate() === null">
        <summary>
          <h2>Channel</h2>
          <span class="meta" data-test="channel-summary">
            @if (mandate(); as m) {
              Saved {{ m.updatedAt | date: 'yyyy-MM-dd HH:mm' }} · Default format:
              {{ formatName(m.defaultFormat || 'generated_pictures') }}
            } @else {
              No mandate yet
            }
            @if (readiness(); as r) {
              · Can make: {{ makes(r) }}
            }
            @if (readinessError(); as message) {
              <span class="err" data-test="channel-error">{{ message }}</span>
            }
          </span>
        </summary>

        <section class="block" data-test="mandate">
          <div class="block-head">
            <h2>Mandate</h2>
            <button
              type="button"
              class="help"
              data-test="help-mandate"
              aria-label="What this block is for"
              [attr.data-tip]="help.mandate"
              [attr.aria-description]="help.mandate"
            >
              ?
            </button>
          </div>
          @if (mandateLoaded() && mandate() === null) {
            <p class="empty" data-test="mandate-empty">
              The operator needs a mandate before it makes a video: what the channel is for, who it
              is for and its language. Fill it in and save it.
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
            <label
              >Default format
              <select
                data-test="mandate-default-format"
                (change)="defaultFormat.set($any($event.target).value)"
              >
                @for (f of defaultFormats; track f) {
                  <option [value]="f" [selected]="defaultFormat() === f">
                    {{ formatName(f) }}
                  </option>
                }
              </select>
            </label>
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
      </details>

      <section class="block" data-test="make-a-video">
        <div class="block-head">
          <h2>Make a video</h2>
          <button
            type="button"
            class="help"
            data-test="help-make-a-video"
            aria-label="What this block is for"
            [attr.data-tip]="help.makeAVideo"
            [attr.aria-description]="help.makeAVideo"
          >
            ?
          </button>
        </div>
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
            Start
          </button>
        </div>
        <details class="fold" data-test="start-options">
          <summary>More options</summary>
          <fieldset>
            <legend>Format</legend>
            <label
              ><input
                type="radio"
                name="start-format"
                data-test="format-generated_pictures"
                [checked]="startFormat() === 'generated_pictures'"
                (change)="startChoice.set('generated_pictures')"
              />
              Generated pictures</label
            >
            <label
              ><input
                type="radio"
                name="start-format"
                data-test="format-own_pictures"
                [checked]="startFormat() === 'own_pictures'"
                (change)="startChoice.set('own_pictures')"
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
                (change)="startChoice.set('animated_character')"
              />
              Animated character</label
            >
            @if (!lipSyncable()) {
              <p class="meta" data-test="format-animated_character-reason">
                Scout has no lip-sync tool on this machine; it ships with Scout Desktop.
              </p>
            }
            <label
              ><input type="radio" name="start-format" data-test="format-ai_presenter" disabled />
              AI presenter</label
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
                (change)="startChoice.set('own_video')"
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
        </details>
        @if (queued(); as q) {
          <p class="meta" data-test="queued">
            {{ q.topic }} <span class="badge">{{ q.status }}</span>
          </p>
        }
        @if (startError(); as message) {
          <p class="err" data-test="start-error">{{ message }}</p>
        }
      </section>

      <div class="work" data-test="work" [class.work--open]="selected() !== null">
        <section class="block" data-test="projects">
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
                    [attr.aria-pressed]="p.id === selectedId()"
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
          <section class="block detail" data-test="project-detail">
            <div class="title-row" data-test="project-title-row">
              <h2>{{ p.topic }}</h2>
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
              <button
                type="button"
                class="close"
                data-test="project-close"
                aria-label="Close the project"
                (click)="close()"
              >
                &times;
              </button>
            </div>
            <div class="row">
              <span class="meta" data-test="project-voice"
                >Voice: {{ voiceLabel(p.narrationVoice) }}</span
              >
              @if (p.delegated) {
                <span class="badge" data-test="project-delegated">Delegated</span>
              }
            </div>
            <p class="meta" data-test="project-format">Format: {{ formatName(p.format) }}</p>
            <p class="meta" data-test="project-visual">Visual: {{ motionName(p.motion) }} motion</p>
            @if (p.direction) {
              <p class="meta" data-test="project-direction">{{ p.direction }}</p>
            }
            @if (p.error; as note) {
              @if (p.status === 'failed') {
                <p class="err" data-test="project-failure">{{ note }}</p>
              } @else {
                <p class="meta" data-test="project-note">{{ note }}</p>
              }
            }

            <details class="fold" data-test="candidates" [open]="p.script === null">
              <summary>
                <h3>Topic candidates</h3>
                <button
                  type="button"
                  class="help"
                  data-test="help-topic-candidates"
                  aria-label="What this block is for"
                  [attr.data-tip]="help.topicCandidates"
                  [attr.aria-description]="help.topicCandidates"
                >
                  ?
                </button>
              </summary>
              @if (p.candidates.length === 0) {
                <p class="empty" data-test="candidates-empty">No topic candidates yet.</p>
              } @else {
                <ul class="plain">
                  @for (c of p.candidates; track $index) {
                    <li data-test="candidate">
                      <strong>{{ c.title }}</strong>
                      <span class="badge">{{ c.fit | percent }}</span>
                      <p class="meta">{{ c.reason }}</p>
                    </li>
                  }
                </ul>
              }
            </details>

            <div class="block-head">
              <h3>Script</h3>
              <button
                type="button"
                class="help"
                data-test="help-script"
                aria-label="What this block is for"
                [attr.data-tip]="help.script"
                [attr.aria-description]="help.script"
              >
                ?
              </button>
            </div>
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

            <details class="fold" data-test="claims" [open]="pendingClaims(p) > 0">
              <summary (click)="pendingClaims(p) > 0 && $event.preventDefault()">
                <h3>Claims</h3>
                <button
                  type="button"
                  class="help"
                  data-test="help-claims"
                  aria-label="What this block is for"
                  [attr.data-tip]="help.claims"
                  [attr.aria-description]="help.claims"
                >
                  ?
                </button>
                @if (pendingClaims(p) > 0) {
                  <span class="meta" data-test="claims-pending"
                    >{{ pendingClaims(p) }} to check · production waits for them</span
                  >
                }
              </summary>
              @if (p.claims.length === 0) {
                <p class="empty" data-test="claims-empty">No claims to check.</p>
              } @else {
                <ul class="claims" data-test="claims-open">
                  @for (c of pendingOf(p); track c.id) {
                    <ng-container *ngTemplateOutlet="claimItem; context: { $implicit: c }" />
                  }
                </ul>
                @if (decidedOf(p).length > 0) {
                  <details class="fold" data-test="claims-decided">
                    <summary>Decided ({{ decidedOf(p).length }})</summary>
                    <ul class="claims">
                      @for (c of decidedOf(p); track c.id) {
                        <ng-container *ngTemplateOutlet="claimItem; context: { $implicit: c }" />
                      }
                    </ul>
                  </details>
                }
              }
              @if (claimError(); as message) {
                <p class="err" data-test="claim-error">{{ message }}</p>
              }
            </details>

            <ng-template #claimItem let-c>
              <li
                class="claim"
                [class.claim--high]="c.risk === 'high'"
                [class.claim--medium]="c.risk === 'medium'"
                [class.claim--low]="c.risk === 'low'"
                [attr.data-test]="'claim-' + c.id"
              >
                <p>{{ c.text }}</p>
                <p class="meta">
                  <span
                    class="chip"
                    [class.chip--high]="c.risk === 'high'"
                    [class.chip--medium]="c.risk === 'medium'"
                    [class.chip--low]="c.risk === 'low'"
                    [attr.data-test]="'claim-risk-' + c.id"
                    >Risk: {{ c.risk }}</span
                  >
                  Status:
                  <span class="badge" [attr.data-test]="'claim-status-' + c.id">{{
                    c.status
                  }}</span>
                  @if (c.note; as note) {
                    · {{ note }}
                  }
                </p>
                @for (url of c.sourceUrls; track $index) {
                  <div class="source">
                    <span
                      class="chip"
                      [attr.title]="url"
                      [attr.data-test]="'claim-source-' + c.id + '-' + $index"
                      >{{ domainOf(url) }}</span
                    >
                    <button
                      type="button"
                      class="btn btn--secondary btn--small"
                      [attr.data-test]="'claim-copy-' + c.id + '-' + $index"
                      (click)="copy(url)"
                    >
                      Copy link
                    </button>
                  </div>
                }
                @if (c.status === 'pending' || reopenedClaim() === c.id) {
                  <div class="segmented" role="group" aria-label="Decision">
                    <button
                      type="button"
                      [attr.aria-pressed]="c.status === 'supported'"
                      [attr.data-test]="'supported-' + c.id"
                      [disabled]="deciding() === c.id"
                      (click)="decide(p, c, 'supported')"
                    >
                      Supported
                    </button>
                    <button
                      type="button"
                      [attr.aria-pressed]="c.status === 'unsupported'"
                      [attr.data-test]="'unsupported-' + c.id"
                      [disabled]="deciding() === c.id"
                      (click)="decide(p, c, 'unsupported')"
                    >
                      Unsupported
                    </button>
                    <button
                      type="button"
                      [attr.aria-pressed]="c.status === 'waived' || waiving() === c.id"
                      [attr.data-test]="'waive-' + c.id"
                      [disabled]="deciding() === c.id"
                      (click)="openWaive(c.id)"
                    >
                      Waive
                    </button>
                  </div>
                } @else {
                  <div class="row">
                    <button
                      type="button"
                      class="btn btn--secondary btn--small"
                      [attr.data-test]="'claim-change-' + c.id"
                      (click)="reopenedClaim.set(c.id)"
                    >
                      Change
                    </button>
                  </div>
                }
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
            </ng-template>

            @if (p.format === 'animated_character') {
              <div class="block-head">
                <h3>Presenter</h3>
                <button
                  type="button"
                  class="help"
                  data-test="help-presenter"
                  aria-label="What this block is for"
                  [attr.data-tip]="help.presenter"
                  [attr.aria-description]="help.presenter"
                >
                  ?
                </button>
                <span class="meta" data-test="presenter-header">{{ presenterHeader(p) }}</span>
              </div>
              @if (p.presenter) {
                <p class="meta" data-test="project-presenter">Presenter: {{ p.presenter }}</p>
              }
              <div class="presenter" data-test="presenter">
                <div class="presenter__view">
                  @if (p.status === 'presenting') {
                    <p class="meta" data-test="presenter-making">Scout is making the presenter.</p>
                  } @else if (p.presenterPreviewFile; as preview) {
                    <video
                      controls
                      class="preview"
                      data-test="presenter-preview"
                      [src]="mediaUrl(p, preview, presenterStamp(p))"
                    ></video>
                  } @else if (p.presenterFile; as file) {
                    <img
                      class="preview"
                      data-test="presenter-picture"
                      [src]="mediaUrl(p, file)"
                      alt="Presenter of the video"
                    />
                  }
                  @if (
                    p.status !== 'presenting' &&
                    !p.presenterPreviewFile &&
                    p.presenterVersions.length === 0
                  ) {
                    <p class="meta" data-test="presenter-pending">
                      The presenter is made as soon as the script is ready.
                    </p>
                  }
                  @if (!p.presenterFile) {
                    <p class="empty" data-test="presenter-empty">
                      No presenter picture yet: Scout makes one when it produces the video if the
                      connected AI Source makes pictures; or attach your own.
                    </p>
                  }
                  @if (p.presenterVersions.length > 0) {
                    <ol class="versions" data-test="presenter-versions">
                      @for (
                        v of p.presenterVersions;
                        track v.file + ' ' + v.previewFile + ' ' + v.madeAt;
                        let i = $index
                      ) {
                        <li
                          class="version"
                          [class.version--current]="isCurrent(p, v)"
                          [attr.data-test]="'presenter-version-' + i"
                        >
                          @if (!isCurrent(p, v) && presenterRequestable(p)) {
                            <button
                              type="button"
                              class="version__pick"
                              [attr.data-test]="'presenter-use-' + i"
                              [attr.aria-label]="'Use presenter version ' + (i + 1)"
                              [disabled]="projectMediaBusy()"
                              (click)="usePresenterVersion(p, i)"
                            >
                              <img
                                class="version__thumb"
                                [src]="mediaUrl(p, v.file, v.madeAt)"
                                [alt]="'Presenter version ' + (i + 1)"
                              />
                            </button>
                          } @else {
                            <img
                              class="version__thumb"
                              [src]="mediaUrl(p, v.file, v.madeAt)"
                              [alt]="'Presenter version ' + (i + 1)"
                            />
                          }
                          @if (removable(p, v)) {
                            <button
                              type="button"
                              class="version__remove"
                              [attr.data-test]="'presenter-version-remove-' + i"
                              [attr.aria-label]="'Remove presenter version ' + (i + 1)"
                              [disabled]="projectMediaBusy()"
                              (click)="removePresenterVersion(p, i)"
                            >
                              &times;
                            </button>
                          }
                          <span [attr.data-test]="'presenter-version-label-' + i"
                            >{{ i + 1 }} · {{ presenterSourceName(v.source) }}</span
                          >
                          <span
                            class="meta version__summary"
                            [attr.data-test]="'presenter-version-summary-' + i"
                            [attr.title]="versionSummary(v)"
                            >{{ versionSummary(v) }}</span
                          >
                          @if (isCurrent(p, v)) {
                            <span class="badge" [attr.data-test]="'presenter-current-' + i"
                              >Current</span
                            >
                          }
                        </li>
                      }
                    </ol>
                  }
                </div>
                <div class="presenter__controls">
                  @if (presenterRequestable(p)) {
                    <label
                      >What would you change?
                      <textarea
                        rows="2"
                        data-test="presenter-feedback"
                        [value]="presenterFeedback()"
                        (input)="presenterFeedback.set($any($event.target).value)"
                      ></textarea>
                    </label>
                    <span class="meta" data-test="presenter-feedback-count"
                      >{{ presenterFeedback().trim().length }} / {{ maxPresenterFeedback }}</span
                    >
                    <div class="row">
                      <button
                        type="button"
                        class="btn btn--primary"
                        data-test="presenter-change"
                        [disabled]="
                          projectMediaBusy() ||
                          noPictures() ||
                          presenterFeedback().trim().length === 0 ||
                          presenterFeedback().trim().length > maxPresenterFeedback
                        "
                        [attr.title]="
                          noPictures() ? 'The connected AI Source makes no pictures' : null
                        "
                        (click)="askPresenterChange(p)"
                      >
                        Ask for a change
                      </button>
                    </div>
                    <details class="fold" data-test="presenter-prompt-options">
                      <summary>Edit the full prompt</summary>
                      <label
                        >Prompt
                        <textarea
                          rows="4"
                          data-test="presenter-prompt"
                          [value]="presenterPromptText(p)"
                          (input)="presenterPrompt.set($any($event.target).value)"
                        ></textarea>
                      </label>
                      <span class="meta" data-test="presenter-prompt-count"
                        >{{ presenterPromptText(p).trim().length }} / {{ maxPresenterPrompt }}</span
                      >
                      <p class="meta safety" data-test="presenter-safety">{{ presenterSafety }}</p>
                      <div class="row">
                        <button
                          type="button"
                          class="btn btn--secondary"
                          data-test="presenter-generate"
                          [disabled]="
                            projectMediaBusy() ||
                            noPictures() ||
                            presenterPromptText(p).trim().length > maxPresenterPrompt
                          "
                          [attr.title]="
                            noPictures() ? 'The connected AI Source makes no pictures' : null
                          "
                          (click)="generatePresenter(p)"
                        >
                          Generate
                        </button>
                      </div>
                    </details>
                  }
                  @if (presenterRequestable(p) || backgroundAttachable(p) || p.presenterFile) {
                    <details
                      class="fold"
                      data-test="presenter-own"
                      [open]="!!p.presenterFile && !p.mouthBox"
                    >
                      <summary>Use one of Scout's presenters, or your own picture</summary>
                      @if (presenterRequestable(p) && builtinPresenters().length > 0) {
                        <div class="row">
                          <label
                            >Scout's presenters
                            <select
                              data-test="presenter-builtin"
                              (change)="builtinChoice.set($any($event.target).value)"
                            >
                              @for (b of builtinPresenters(); track b.id) {
                                <option [value]="b.id" [selected]="builtinChosen() === b.id">
                                  {{ b.label }}
                                </option>
                              }
                            </select>
                          </label>
                          <button
                            type="button"
                            class="btn btn--secondary"
                            data-test="presenter-builtin-use"
                            [disabled]="projectMediaBusy()"
                            (click)="useBuiltinPresenter(p)"
                          >
                            Use this one
                          </button>
                        </div>
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
                        </div>
                      }
                      @if (p.presenterFile; as file) {
                        <h4>Mark the mouth</h4>
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
                      }
                    </details>
                  }
                  @if (presenterError(); as message) {
                    <p class="err" data-test="presenter-error">{{ message }}</p>
                  }
                </div>
              </div>
            }

            <div class="block-head">
              <h3>Scenes</h3>
              <button
                type="button"
                class="help"
                data-test="help-scenes"
                aria-label="What this block is for"
                [attr.data-tip]="help.scenes"
                [attr.aria-description]="help.scenes"
              >
                ?
              </button>
            </div>
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
            @if (p.status === 'presenting') {
              <p class="meta" data-test="presenting-progress">Making the presenter.</p>
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
              <ol class="scenes">
                @for (s of orderedScenes(p); track s.id; let first = $first, last = $last) {
                  <li class="scene" [attr.data-test]="'scene-' + s.id">
                    <div class="scene__media">
                      @if (
                        s.visualSource !== 'text_card' &&
                          p.format === 'animated_character' &&
                          s.visualFile;
                        as file
                      ) {
                        <div class="card" [attr.data-test]="'scene-preview-' + s.id">
                          <img
                            class="card__image"
                            [attr.data-test]="'scene-image-' + s.id"
                            [src]="mediaUrl(p, file)"
                            [alt]="'Picture of scene ' + s.order"
                          />
                          @if (p.presenterFile; as presenter) {
                            <img
                              class="card__badge"
                              [attr.data-test]="'scene-presenter-badge-' + s.id"
                              [src]="mediaUrl(p, presenter)"
                              alt=""
                            />
                          }
                        </div>
                      } @else if (s.visualSource !== 'text_card' && s.visualFile; as file) {
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
                      @if (
                        p.format === 'generated_pictures' ||
                        p.format === 'animated_character' ||
                        p.format === 'own_pictures'
                      ) {
                        <div class="scene__tools">
                          @if (
                            p.format === 'generated_pictures' || p.format === 'animated_character'
                          ) {
                            <button
                              type="button"
                              class="btn btn--secondary btn--small"
                              [attr.data-test]="'scene-picture-regenerate-' + s.id"
                              [disabled]="s.locked || sceneBusy() !== null"
                              (click)="regeneratePicture(p, s)"
                            >
                              Regenerate picture
                            </button>
                          }
                          @if (p.format === 'own_pictures' || p.format === 'animated_character') {
                            <button
                              type="button"
                              class="btn btn--secondary btn--small"
                              [attr.data-test]="'scene-attach-visual-' + s.id"
                              [disabled]="sceneBusy() !== null"
                              (click)="attach(p, s, 'visual')"
                            >
                              Attach image
                            </button>
                          }
                        </div>
                      }
                    </div>
                    <div class="scene__body">
                      <p data-test="scene-narration">{{ s.narration }}</p>
                      @if (p.format === 'own_video') {
                        <p class="meta" [attr.data-test]="'scene-clip-' + s.id">
                          Clip: {{ clock(s.clipStartMs) }} to {{ clock(s.clipEndMs) }}
                        </p>
                      } @else if (p.format === 'own_pictures') {
                        <p class="meta">Picture prompt: {{ s.visualPrompt }}</p>
                      } @else if (picturePromptDrafts()[s.id] === undefined) {
                        <div class="row prompt-line">
                          <span
                            class="meta prompt-line__text"
                            [attr.data-test]="'scene-picture-prompt-line-' + s.id"
                            [attr.title]="s.visualPrompt"
                            >Picture prompt: {{ s.visualPrompt }}</span
                          >
                          <button
                            type="button"
                            class="btn btn--secondary btn--small"
                            [attr.data-test]="'scene-picture-prompt-edit-' + s.id"
                            [disabled]="s.locked || sceneBusy() !== null"
                            (click)="setPicturePrompt(s, s.visualPrompt)"
                          >
                            Edit prompt
                          </button>
                        </div>
                      } @else {
                        <label class="meta"
                          >Picture prompt
                          <textarea
                            rows="2"
                            [attr.data-test]="'scene-picture-prompt-' + s.id"
                            [value]="picturePromptOf(s)"
                            (input)="setPicturePrompt(s, $any($event.target).value)"
                          ></textarea>
                        </label>
                        @if (picturePromptInvalid(s)) {
                          <p class="err" [attr.data-test]="'scene-picture-prompt-invalid-' + s.id">
                            A picture prompt is 1 to 700 characters.
                          </p>
                        }
                        <div class="row">
                          <button
                            type="button"
                            class="btn btn--secondary"
                            [attr.data-test]="'scene-picture-prompt-save-' + s.id"
                            [disabled]="
                              picturePromptInvalid(s) ||
                              picturePromptOf(s).trim() === s.visualPrompt ||
                              s.locked ||
                              sceneBusy() !== null
                            "
                            (click)="savePicturePrompt(p, s)"
                          >
                            Save
                          </button>
                          <button
                            type="button"
                            class="btn btn--secondary"
                            [attr.data-test]="'scene-picture-prompt-cancel-' + s.id"
                            (click)="cancelPicturePrompt(s)"
                          >
                            Cancel
                          </button>
                        </div>
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
                            [attr.data-test]="'scene-narration-replace-' + s.id"
                            [disabled]="sceneBusy() !== null"
                            (click)="attach(p, s, 'audio')"
                          >
                            Replace narration
                          </button>
                          <details class="menu" [attr.data-test]="'scene-narration-menu-' + s.id">
                            <summary class="btn btn--secondary btn--small">Narration</summary>
                            <div class="menu__items">
                              @if (
                                p.format === 'generated_pictures' ||
                                p.format === 'animated_character'
                              ) {
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
                                [attr.data-test]="'scene-narration-regenerate-' + s.id"
                                [disabled]="s.locked || sceneBusy() !== null"
                                (click)="askRegenerateNarration(p, s)"
                              >
                                Regenerate narration
                              </button>
                            </div>
                          </details>
                        </div>
                      }
                      <div class="row">
                        <button
                          type="button"
                          class="btn btn--secondary"
                          [attr.data-test]="'scene-edit-' + s.id"
                          [disabled]="s.locked || sceneBusy() !== null"
                          (click)="openSceneText(s)"
                        >
                          Edit text
                        </button>
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
                      </div>
                      @if (editingScene() === s.id) {
                        <div class="panel">
                          @if (p.format === 'own_video') {
                            <p class="meta" [attr.data-test]="'scene-text-hint-' + s.id">
                              The new text replaces this scene's captions; the recording is not
                              changed.
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
                    </div>
                  </li>
                }
              </ol>
            }
            @if (sceneError(); as message) {
              <p class="err" data-test="scene-error">{{ message }}</p>
            }

            @if (p.format === 'own_video') {
              <div class="block-head">
                <h3>Recording</h3>
                <button
                  type="button"
                  class="help"
                  data-test="help-recording"
                  aria-label="What this block is for"
                  [attr.data-tip]="help.recording"
                  [attr.aria-description]="help.recording"
                >
                  ?
                </button>
              </div>
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
              <div class="block-head">
                <h3>Background</h3>
                <button
                  type="button"
                  class="help"
                  data-test="help-background"
                  aria-label="What this block is for"
                  [attr.data-tip]="help.background"
                  [attr.aria-description]="help.background"
                >
                  ?
                </button>
              </div>
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
                  Replace your own narration on this scene with Scout's voice? Your file stays on
                  disk but the video will use the generated narration.
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
      </div>
      <audio #samplePlayer data-test="voice-sample-player"></audio>
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .page {
        container-type: inline-size;
        padding: 32px;
        max-width: 1280px;
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
      .claims,
      .scenes {
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
        border-left-width: 4px;
        border-radius: 8px;
        padding: 12px 16px;
      }
      .claim--high {
        border-left-color: #ef4444;
      }
      .claim--medium {
        border-left-color: #f59e0b;
      }
      .claim--low {
        border-left-color: #22c55e;
      }
      .claim p {
        margin: 0 0 4px;
      }
      .chip {
        display: inline-block;
        border-radius: 999px;
        padding: 0 8px;
        font-size: 12px;
        background: #1f2433;
        color: #e4e8f0;
      }
      .chip--high {
        background: rgba(239, 68, 68, 0.15);
        color: #fca5a5;
      }
      .chip--medium {
        background: rgba(245, 158, 11, 0.15);
        color: #fcd34d;
      }
      .chip--low {
        background: rgba(34, 197, 94, 0.15);
        color: #86efac;
      }
      .segmented {
        display: inline-flex;
        margin-top: 8px;
        border: 1px solid #252b3b;
        border-radius: 6px;
        overflow: hidden;
      }
      .segmented button {
        background: transparent;
        color: #e4e8f0;
        border: 0;
        border-right: 1px solid #252b3b;
        padding: 6px 12px;
        font-size: 13px;
        cursor: pointer;
      }
      .segmented button:last-child {
        border-right: 0;
      }
      .segmented button[aria-pressed='true'] {
        background: #2563eb;
        color: white;
      }
      .segmented button:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
      summary {
        cursor: pointer;
      }
      .channel > summary,
      .fold > summary {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
        margin: 16px 0 8px;
      }
      .channel > summary h2,
      .fold > summary h3 {
        margin: 0;
      }
      .block-head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
        margin: 16px 0 8px;
      }
      .block-head h2,
      .block-head h3 {
        margin: 0;
      }
      .help {
        position: relative;
        width: 18px;
        height: 18px;
        padding: 0;
        border: 1px solid #3a4258;
        border-radius: 50%;
        background: transparent;
        color: #7b8299;
        font-size: 11px;
        line-height: 1;
        cursor: help;
      }
      .help:hover::after,
      .help:focus-visible::after {
        content: attr(data-tip);
        position: absolute;
        top: calc(100% + 6px);
        left: 0;
        z-index: 10;
        width: 320px;
        padding: 8px 10px;
        background: #11131a;
        border: 1px solid #252b3b;
        border-radius: 6px;
        color: #e4e8f0;
        font-size: 12px;
        line-height: 1.4;
        text-align: left;
        white-space: normal;
        pointer-events: none;
      }
      .work {
        display: flex;
        flex-direction: column;
      }
      @container (min-width: 960px) {
        .work--open {
          display: grid;
          grid-template-columns: minmax(220px, 300px) minmax(0, 1fr);
          gap: 24px;
          align-items: start;
        }
      }
      .detail {
        container: detail / inline-size;
      }
      .title-row {
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .title-row h2 {
        flex: 1;
        margin: 0;
      }
      .close {
        width: 28px;
        height: 28px;
        padding: 0;
        border: 1px solid #252b3b;
        border-radius: 6px;
        background: transparent;
        color: #e4e8f0;
        font-size: 18px;
        line-height: 1;
        cursor: pointer;
      }
      .presenter {
        display: grid;
        grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
        gap: 16px;
        align-items: start;
      }
      @container detail (max-width: 720px) {
        .presenter {
          grid-template-columns: minmax(0, 1fr);
        }
      }
      .presenter__controls {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .presenter__controls .fold > summary {
        margin: 8px 0 4px;
      }
      .scene {
        display: flex;
        flex-wrap: wrap;
        gap: 16px;
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 12px 16px;
      }
      .scene__media {
        position: relative;
        flex: 0 0 320px;
        align-self: flex-start;
      }
      .scene__media .card,
      .scene__media .preview {
        margin: 0;
      }
      .scene__tools {
        position: absolute;
        left: 8px;
        bottom: 8px;
        display: flex;
        gap: 6px;
      }
      .scene__tools .btn {
        background: rgba(15, 17, 23, 0.85);
      }
      .scene__body {
        flex: 1 1 260px;
        min-width: 0;
      }
      .scene__body p {
        margin: 0 0 4px;
      }
      .prompt-line {
        margin-top: 0;
      }
      .prompt-line__text {
        flex: 1;
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .menu {
        position: relative;
      }
      .menu > summary {
        list-style: none;
      }
      .menu > summary::-webkit-details-marker {
        display: none;
      }
      .menu__items {
        position: absolute;
        z-index: 5;
        display: flex;
        flex-direction: column;
        gap: 4px;
        margin-top: 4px;
        padding: 6px;
        background: #11131a;
        border: 1px solid #252b3b;
        border-radius: 6px;
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
      .card__badge {
        position: absolute;
        right: 3%;
        bottom: 3%;
        height: 45%;
        object-fit: contain;
      }
      .safety {
        opacity: 0.6;
      }
      .versions {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        list-style: none;
        margin: 8px 0;
        padding: 0;
      }
      .version {
        position: relative;
        display: flex;
        flex-direction: column;
        gap: 2px;
        width: 96px;
        font-size: 11px;
      }
      .version__thumb {
        display: block;
        width: 100%;
        border: 2px solid transparent;
        border-radius: 4px;
        box-sizing: border-box;
      }
      .version--current .version__thumb {
        border-color: #22c55e;
      }
      .version__pick {
        padding: 0;
        border: 0;
        background: none;
        cursor: pointer;
      }
      .version__remove {
        position: absolute;
        top: 4px;
        right: 4px;
        width: 20px;
        height: 20px;
        padding: 0;
        border: 0;
        border-radius: 50%;
        background: rgba(15, 17, 23, 0.85);
        color: #e4e8f0;
        line-height: 1;
        cursor: pointer;
      }
      .version__summary {
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
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
      .btn--small {
        padding: 2px 8px;
        font-size: 12px;
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
  readonly defaultFormat = signal<ProjectFormat>('generated_pictures');
  readonly defaultFormats = DEFAULT_FORMATS;

  readonly topic = signal('');
  readonly startChoice = signal<ProjectFormat | null>(null);
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
  readonly reopenedClaim = signal<string | null>(null);
  readonly help = HELP;

  readonly readiness = signal<ReadinessDto | null>(null);
  readonly readinessError = signal<string | null>(null);
  readonly transcribable = computed(() => this.readiness()?.transcriptionAvailable === true);
  readonly lipSyncable = computed(() => this.readiness()?.lipSyncAvailable === true);
  readonly noPictures = computed(() => this.readiness()?.imagesAvailable === false);
  readonly preferredFormat = computed((): ProjectFormat => {
    const chosen = this.mandate()?.defaultFormat || 'generated_pictures';
    if (chosen === 'animated_character' && !this.lipSyncable()) return 'generated_pictures';
    if (chosen === 'own_video' && !this.transcribable()) return 'generated_pictures';
    return chosen;
  });
  readonly startFormat = computed(() => this.startChoice() ?? this.preferredFormat());
  readonly builtinPresenters = computed(() => this.readiness()?.builtinPresenters ?? []);
  readonly builtinChoice = signal<string | null>(null);
  readonly builtinChosen = computed(
    () => this.builtinChoice() ?? this.builtinPresenters()[0]?.id ?? null,
  );
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
  readonly maxPresenterPrompt = MAX_PRESENTER_PROMPT;
  readonly maxPresenterFeedback = MAX_PRESENTER_FEEDBACK;
  readonly presenterSafety = PRESENTER_SAFETY;
  readonly presenterPrompt = signal<string | null>(null);
  readonly presenterFeedback = signal('');
  readonly picturePromptDrafts = signal<Readonly<Record<string, string>>>({});
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
        defaultFormat: this.defaultFormat(),
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
      this.startChoice.set(null);
      this.startDirection.set('');
      this.startPresenter.set('');
      await this.refresh();
    } catch (err: unknown) {
      this.startError.set(readIpcError(err).message);
    } finally {
      this.starting.set(false);
    }
  }

  /** Opens a project's detail, or closes it when the project is the one already open. */
  async select(id: string): Promise<void> {
    if (id === this.selectedId()) {
      this.close();
      return;
    }
    this.selectedId.set(id);
    this.waiving.set(null);
    this.reopenedClaim.set(null);
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
    this.presenterPrompt.set(null);
    this.presenterFeedback.set('');
    this.builtinChoice.set(null);
    this.picturePromptDrafts.set({});
    this.sceneError.set(null);
    this.editingScene.set(null);
    await this.loadSelected();
  }

  close(): void {
    this.selectedId.set(null);
    this.detail.set(null);
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
      this.reopenedClaim.set(null);
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

  pendingOf(project: OperatorProjectDto): ClaimDto[] {
    return project.claims.filter((c) => c.status === 'pending');
  }

  decidedOf(project: OperatorProjectDto): ClaimDto[] {
    return project.claims.filter((c) => c.status !== 'pending');
  }

  /** The host of a source URL for its chip, or the text as it came when it is not a URL. */
  domainOf(url: string): string {
    try {
      return new URL(url).hostname || url;
    } catch {
      return url;
    }
  }

  /** What the worker can make, as the short labels of the Channel summary. */
  makes(r: ReadinessDto): string {
    const labels = [
      r.speechAvailable ? 'Narration' : null,
      r.imagesAvailable ? 'Generated pictures' : r.stockPictures ? 'Openverse pictures' : null,
      r.lipSyncAvailable ? 'Animated character' : null,
      r.transcriptionAvailable ? 'Your own video' : null,
    ].filter((label) => label !== null);
    return labels.length > 0 ? labels.join(', ') : 'text cards only';
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

  mediaUrl(
    project: OperatorProjectDto,
    fileName: string,
    stamp: string = project.updatedAt,
  ): string {
    return `scout-media://${project.id}/${encodeURIComponent(fileName)}?v=${encodeURIComponent(stamp)}`;
  }

  /** The control plane's current version: its picture and its preview are the project's. */
  isCurrent(project: OperatorProjectDto, version: PresenterVersionDto): boolean {
    return (
      version.file === project.presenterFile && version.previewFile === project.presenterPreviewFile
    );
  }

  presenterStamp(project: OperatorProjectDto): string {
    return (
      project.presenterVersions.find((v) => this.isCurrent(project, v))?.madeAt ?? project.updatedAt
    );
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

  presenterRequestable(project: OperatorProjectDto): boolean {
    return (
      PRESENTER_REQUESTABLE.includes(project.status) ||
      (project.status === 'failed' && project.script !== null)
    );
  }

  /**
   * The prompt of the current presenter version, else the one the worker composes: the direction, the presenter
   * description or the topic, and the style; the safety tail is never part of it.
   */
  presenterPromptOf(project: OperatorProjectDto): string {
    const current = project.presenterVersions.find((v) => this.isCurrent(project, v));
    if (current !== undefined && current.prompt !== '') return current.prompt;
    const parts = [project.direction, project.presenter || project.topic, PRESENTER_STYLE]
      .map((part) => part.trim().replace(/\.+$/, ''))
      .filter((part) => part.length > 0);
    return `${parts.join('. ')}.`;
  }

  presenterPromptText(project: OperatorProjectDto): string {
    return this.presenterPrompt() ?? this.presenterPromptOf(project);
  }

  presenterSourceName(source: string): string {
    return PRESENTER_SOURCES[source] ?? source;
  }

  versionSummary(version: PresenterVersionDto): string {
    return (version.feedback || version.prompt).slice(0, VERSION_SUMMARY);
  }

  generatePresenter(project: OperatorProjectDto): Promise<void> {
    const prompt = this.presenterPromptText(project).trim();
    return this.projectMediaAction(this.presenterError, async () => {
      const updated = await this.bridge.operatorRequestPresenter({
        projectId: project.id,
        feedback: '',
        prompt,
      });
      this.presenterPrompt.set(null);
      return updated;
    });
  }

  askPresenterChange(project: OperatorProjectDto): Promise<void> {
    const feedback = this.presenterFeedback().trim();
    return this.projectMediaAction(this.presenterError, async () => {
      const updated = await this.bridge.operatorRequestPresenter({
        projectId: project.id,
        feedback,
        prompt: '',
      });
      this.presenterFeedback.set('');
      return updated;
    });
  }

  useBuiltinPresenter(project: OperatorProjectDto): Promise<void> {
    const id = this.builtinChosen();
    if (id === null) return Promise.resolve();
    return this.projectMediaAction(this.presenterError, () =>
      this.bridge.operatorRequestPresenter({
        projectId: project.id,
        feedback: `builtin:${id}`,
        prompt: '',
      }),
    );
  }

  /** "Version i of n · source · k of 40 pictures left today", each part only when it is known. */
  presenterHeader(project: OperatorProjectDto): string {
    const index = project.presenterVersions.findIndex((v) => this.isCurrent(project, v));
    const current = project.presenterVersions[index];
    const parts =
      current === undefined
        ? []
        : [
            `Version ${index + 1} of ${project.presenterVersions.length}`,
            this.presenterSourceName(current.source),
          ];
    const r = this.readiness();
    if (r !== null) parts.push(`${r.picturesLeftToday} of ${DAILY_PICTURES} pictures left today`);
    return parts.join(' · ');
  }

  removePresenterVersion(project: OperatorProjectDto, index: number): Promise<void> {
    return this.projectMediaAction(this.presenterError, () =>
      this.bridge.operatorRemovePresenterVersion({ projectId: project.id, index }),
    );
  }

  usePresenterVersion(project: OperatorProjectDto, index: number): Promise<void> {
    return this.projectMediaAction(this.presenterError, () =>
      this.bridge.operatorUsePresenterVersion({ projectId: project.id, index }),
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

  regeneratePicture(project: OperatorProjectDto, scene: SceneDto): Promise<void> {
    return this.sceneAction(scene.id, () =>
      this.bridge.operatorRegenerateScenePicture({ projectId: project.id, sceneId: scene.id }),
    );
  }

  picturePromptOf(scene: SceneDto): string {
    return this.picturePromptDrafts()[scene.id] ?? scene.visualPrompt;
  }

  picturePromptInvalid(scene: SceneDto): boolean {
    const length = this.picturePromptOf(scene).trim().length;
    return length === 0 || length > MAX_PICTURE_PROMPT;
  }

  setPicturePrompt(scene: SceneDto, value: string): void {
    this.picturePromptDrafts.update((drafts) => ({ ...drafts, [scene.id]: value }));
  }

  cancelPicturePrompt(scene: SceneDto): void {
    this.picturePromptDrafts.update(({ [scene.id]: _dropped, ...rest }) => rest);
  }

  savePicturePrompt(project: OperatorProjectDto, scene: SceneDto): Promise<void> {
    const prompt = this.picturePromptOf(scene).trim();
    return this.sceneAction(scene.id, async () => {
      const updated = await this.bridge.operatorEditScenePicturePrompt({
        projectId: project.id,
        sceneId: scene.id,
        prompt,
      });
      this.cancelPicturePrompt(scene);
      return updated;
    });
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
    this.defaultFormat.set(mandate.defaultFormat || 'generated_pictures');
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

  /**
   * The control plane's removal rule: never while presenting, and never the last version naming the current picture
   * and preview; a version that is not current, or a current one with a twin left, can go.
   */
  removable(project: OperatorProjectDto, version: PresenterVersionDto): boolean {
    if (project.status === 'presenting') return false;
    if (!this.isCurrent(project, version)) return true;
    return project.presenterVersions.filter((v) => this.isCurrent(project, v)).length > 1;
  }
}
