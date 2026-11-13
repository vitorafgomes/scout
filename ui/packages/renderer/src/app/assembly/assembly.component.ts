import { formatDate } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  TemplateRef,
  computed,
  inject,
  signal,
  viewChild,
  type WritableSignal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule, type MatDialogRef } from '@angular/material/dialog';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import {
  readIpcError,
  type OperatorProjectDto,
  type PrivacyStatus,
  type VideoPackageDto,
} from '@scout/shared';

import { IconComponent } from '../core/icon.component';
import { ScoutBridgeService } from '../core/scout-bridge';
import { StatusChipComponent, type ChipTone } from '../core/status-chip.component';
import { formatLondon, londonToUtcIso } from '../operator/london';

const POLL_MS = 10_000;
const PROJECTS_READ = 50;
const ASSEMBLY_STATUSES = [
  'media_ready',
  'assembly_queued',
  'assembling',
  'assembled',
  'approved',
  'failed',
];
const ASSEMBLABLE = ['media_ready', 'assembled'];
const EDITABLE = ['assembled', 'approved'];
const MAX_TITLE = 100;
const MAX_DESCRIPTION = 5000;
const MAX_TAG = 100;
const MAX_TAGS = 500;
const PRIVACY: readonly { value: PrivacyStatus; label: string; helper: string }[] = [
  { value: 'public', label: 'Public', helper: 'Anyone can find and watch it.' },
  { value: 'unlisted', label: 'Unlisted', helper: 'Only people with the link can watch it.' },
  {
    value: 'private',
    label: 'Private',
    helper: 'Only you can watch it. Needed to schedule it.',
  },
];
const STATUS_WORDS: Readonly<Record<string, string>> = {
  media_ready: 'Waiting to assemble',
  assembly_queued: 'Queued for assembly',
};
const STATUS_TONES: Readonly<Record<string, ChipTone>> = {
  assembly_queued: 'info',
  assembling: 'info',
  assembled: 'ok',
  approved: 'ok',
  published: 'ok',
  failed: 'error',
};
const CHECK_RESULTS: Readonly<Record<string, { label: string; tone: ChipTone }>> = {
  pass: { label: 'Passed', tone: 'ok' },
  warn: { label: 'Warning', tone: 'warn' },
  fail: { label: 'Failed', tone: 'error' },
};

function clock(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

function length(ms: number): string {
  const seconds = Math.round(ms / 1000);
  const minutes = Math.floor(seconds / 60);
  return minutes > 0 ? `${minutes} min ${seconds % 60} s` : `${seconds} s`;
}

/** The assembled video of a project: assembly, the video, Shorts and thumbnail, the checks, the metadata, the media rights and the approval. */
@Component({
  selector: 'sc-assembly',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, MatButtonModule, MatDialogModule, RouterLink, StatusChipComponent],
  template: `
    <main class="page">
      <div class="head">
        <div class="titles">
          <h1>Assembly</h1>
          <p class="lede">
            The finished video, its Shorts and thumbnail, what to check, and the approval.
          </p>
        </div>
        <a matButton="outlined" routerLink="/operator/production" data-test="open-production">
          <sc-icon name="video" [size]="16" />Open in Production
        </a>
      </div>

      @if (error(); as message) {
        <p class="err" role="alert" data-test="assembly-error">{{ message }}</p>
      }

      @if (!loaded()) {
        @if (error() === null) {
          <div
            class="card loading"
            role="status"
            aria-label="Loading videos"
            data-test="assembly-loading"
          >
            <div class="bar"><div class="bar-fill"></div></div>
            <div class="skeleton skeleton--pick"></div>
            <div class="skeleton skeleton--media"></div>
          </div>
        }
      } @else if (projects().length === 0) {
        <div class="card empty-card">
          <span class="empty-icon" aria-hidden="true">
            <svg
              width="22"
              height="22"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <path d="M4 4h16v6H4zM4 14h7v6H4zM15 14h5v6h-5z" />
            </svg>
          </span>
          <span data-test="assembly-empty"
            >No video is ready for assembly yet. Make its media on the Production page first.</span
          >
          <a
            matButton="outlined"
            class="sm"
            routerLink="/operator/production"
            data-test="go-production"
          >
            <svg
              class="glyph"
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
              <path d="M5 12h14M12 5l7 7-7 7" /></svg
            >Go to Production
          </a>
        </div>
      } @else {
        <section class="card card--video" data-test="assembly-video">
          <div class="pick">
            <div class="field grow">
              <label for="asm-video">Video</label>
              <select
                id="asm-video"
                data-test="assembly-project"
                (change)="select($any($event.target).value)"
              >
                <option value="" [selected]="selectedId() === null">Choose a video</option>
                @for (p of projects(); track p.id) {
                  <option [value]="p.id" [selected]="p.id === selectedId()">
                    {{ p.topic }} · {{ statusLabel(p.status) }}
                  </option>
                }
              </select>
            </div>
            @if (selected(); as p) {
              <sc-status-chip
                data-test="assembly-status"
                [tone]="statusTone(p.status)"
                [label]="statusLabel(p.status)"
              />
              @if (again(p)) {
                <button
                  type="button"
                  matButton="outlined"
                  data-test="assemble"
                  aria-describedby="asm-again"
                  [disabled]="busy() || !assemblable(p)"
                  (click)="assemble(p)"
                >
                  <sc-icon name="refresh" [size]="16" />Assemble again
                </button>
              } @else {
                <button
                  type="button"
                  matButton="filled"
                  data-test="assemble"
                  [disabled]="busy() || !assemblable(p)"
                  (click)="assemble(p)"
                >
                  <sc-icon name="play" [size]="16" />Assemble video
                </button>
              }
            }
          </div>

          @if (selected(); as p) {
            @if (p.status === 'assembly_queued') {
              <span class="meta" data-test="assembly-progress"
                >Queued for assembly; the Scout worker starts it next.</span
              >
            } @else if (p.status === 'assembling') {
              <span class="meta" data-test="assembly-progress"
                >Assembling the video, its Shorts and its thumbnail.</span
              >
            } @else if (again(p)) {
              <span class="meta" id="asm-again" data-test="assemble-again-help"
                >Assembling again replaces the video, Shorts and thumbnail below.</span
              >
            }
            @if (assembleError(); as message) {
              <p class="err" data-test="assemble-error">{{ message }}</p>
            }

            @if (p.scenes.length > 0) {
              <h2>
                Scenes
                <span class="h2-meta" data-test="scenes-summary">{{ scenesSummary(p) }}</span>
              </h2>
              <ol class="scenes" data-test="scenes">
                @for (s of sceneTiles(p); track s.label) {
                  <li>
                    <div class="tile" role="img" [attr.aria-label]="s.alt">{{ s.label }}</div>
                    <span class="meta">{{ s.time }}</span>
                  </li>
                }
              </ol>
            }

            @if (p.package; as pkg) {
              @if (pkg.error; as message) {
                <p class="err" data-test="package-error">{{ message }}</p>
              }

              <div class="media">
                <div class="media-main">
                  @if (pkg.videoFile; as file) {
                    <h2>Video</h2>
                    <video
                      class="player"
                      controls
                      data-test="package-video"
                      [attr.aria-label]="
                        'Video player: ' + pkg.title + ', ' + length(pkg.durationMs)
                      "
                      [src]="mediaUrl(p, file)"
                    ></video>
                  }
                </div>
                <div class="media-side">
                  @if (pkg.thumbnailFile; as file) {
                    <h2>Thumbnail</h2>
                    <img
                      class="thumbnail"
                      data-test="package-thumbnail"
                      alt="Thumbnail of the video"
                      [src]="mediaUrl(p, file)"
                    />
                  }
                  <h2>Shorts</h2>
                  @if (pkg.shorts.length === 0) {
                    <p class="empty" data-test="shorts-empty">No Shorts yet.</p>
                  } @else {
                    <ul class="shorts">
                      @for (s of pkg.shorts; track s.file; let i = $index) {
                        <li>
                          <video
                            class="short"
                            controls
                            [attr.aria-label]="
                              'Short ' + (i + 1) + ', ' + seconds(s.durationMs) + ' seconds'
                            "
                            [attr.data-test]="'short-video-' + i"
                            [src]="mediaUrl(p, s.file)"
                          ></video>
                          <span class="hook" [attr.data-test]="'short-hook-' + i">{{
                            s.hook
                          }}</span>
                        </li>
                      }
                    </ul>
                  }
                </div>
              </div>

              <h2>Checks</h2>
              @if (pkg.checks.length === 0) {
                <p class="empty" data-test="checks-empty">No checks yet.</p>
              } @else {
                <div class="table-frame">
                  <table class="checks" data-test="package-checks">
                    <thead>
                      <tr>
                        <th scope="col">Check</th>
                        <th scope="col">Result</th>
                        <th scope="col">Detail</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (c of pkg.checks; track $index; let i = $index) {
                        <tr>
                          <td [attr.data-test]="'check-name-' + i">{{ c.name }}</td>
                          <td>
                            <sc-status-chip
                              [attr.data-test]="'check-result-' + i"
                              [tone]="checkResult(c.result).tone"
                              [label]="checkResult(c.result).label"
                            />
                          </td>
                          <td class="detail" [attr.data-test]="'check-detail-' + i">
                            {{ c.detail }}
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            }
          }
        </section>

        @if (selected(); as p) {
          @if (p.package; as pkg) {
            @if (editable(p)) {
              <section class="card" aria-labelledby="md-h" data-test="youtube-details">
                <h2 id="md-h">YouTube details</h2>
                <div class="field">
                  <div class="label-row">
                    <label for="md-title">Title</label>
                    <span class="meta" data-test="title-count"
                      >{{ title().trim().length }} / {{ maxTitle }}</span
                    >
                  </div>
                  <input
                    id="md-title"
                    type="text"
                    data-test="metadata-title"
                    [value]="title()"
                    (input)="title.set($any($event.target).value)"
                  />
                </div>
                <div class="field">
                  <div class="label-row">
                    <label for="md-desc">Description</label>
                    <span class="meta" data-test="description-count"
                      >{{ description().trim().length }} / {{ maxDescription }}</span
                    >
                  </div>
                  <textarea
                    id="md-desc"
                    rows="3"
                    data-test="metadata-description"
                    [value]="description()"
                    (input)="description.set($any($event.target).value)"
                  ></textarea>
                </div>
                <div class="field">
                  <div class="label-row">
                    <label for="md-tag">Tags</label>
                    <span class="meta" data-test="tags-count"
                      >{{ tagsLength() }} / {{ maxTags }}</span
                    >
                  </div>
                  <ul class="chips">
                    @for (tag of tags(); track $index; let i = $index) {
                      <li class="chip">
                        <span [attr.data-test]="'tag-' + i">{{ tag }}</span>
                        <button
                          type="button"
                          class="chip-remove"
                          [attr.data-test]="'tag-remove-' + i"
                          [attr.aria-label]="'Remove tag ' + tag"
                          [attr.title]="'Remove tag ' + tag"
                          (click)="removeTag(i)"
                        >
                          <svg
                            width="12"
                            height="12"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            stroke-width="2.4"
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            aria-hidden="true"
                          >
                            <path d="M18 6L6 18M6 6l12 12" />
                          </svg>
                        </button>
                      </li>
                    }
                  </ul>
                  <div class="row">
                    <input
                      id="md-tag"
                      type="text"
                      class="grow tag-input"
                      data-test="tag-input"
                      placeholder="Add tags, separated by commas"
                      aria-describedby="md-tag-help"
                      [value]="newTag()"
                      (input)="newTag.set($any($event.target).value)"
                      (keydown.enter)="addTag()"
                    />
                    <button
                      type="button"
                      matButton="outlined"
                      class="sm"
                      data-test="tag-add"
                      (click)="addTag()"
                    >
                      <svg
                        class="glyph"
                        width="15"
                        height="15"
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        stroke-width="2"
                        stroke-linecap="round"
                        stroke-linejoin="round"
                        aria-hidden="true"
                      >
                        <path d="M12 5v14M5 12h14" /></svg
                      >Add tag
                    </button>
                  </div>
                  <span class="meta" id="md-tag-help">Duplicates are skipped.</span>
                </div>
                <div class="row">
                  <button
                    type="button"
                    matButton="outlined"
                    data-test="metadata-save"
                    [disabled]="busy() || !metadataValid()"
                    (click)="saveMetadata(p)"
                  >
                    <sc-icon name="save" [size]="16" />Save details
                  </button>
                  @if (pkg.metadataEdited) {
                    <span class="meta" data-test="metadata-saved">Saved</span>
                  }
                </div>
                @if (metadataError(); as message) {
                  <p class="err" data-test="metadata-error">{{ message }}</p>
                }
              </section>
            }

            <section class="card" aria-labelledby="ap-h" data-test="approval">
              <h2 id="ap-h">Approval</h2>
              <div class="rights">
                <input
                  id="ap-rights"
                  type="checkbox"
                  data-test="media-rights"
                  aria-describedby="ap-rights-help"
                  [checked]="pkg.rightsConfirmed"
                  [disabled]="busy() || pkg.rightsConfirmed || p.status !== 'assembled'"
                  (change)="confirmRights(p, $event)"
                />
                <div class="rights-text">
                  <label for="ap-rights" data-test="media-rights-label"
                    >I own or have the rights to every image, recording and music in this
                    video</label
                  >
                  @if (pkg.rightsConfirmed) {
                    <span class="meta ok" id="ap-rights-help" data-test="media-rights-help"
                      >Confirmed. This cannot be undone.</span
                    >
                  } @else {
                    <span class="meta" id="ap-rights-help" data-test="media-rights-help"
                      >You confirm this once per video, in a confirm step. It cannot be
                      undone.</span
                    >
                  }
                </div>
              </div>
              @if (rightsError(); as message) {
                <p class="err" data-test="rights-error">{{ message }}</p>
              }

              @if (pkg.approvedAt; as at) {
                <p class="ok approved" data-test="package-approved">
                  <sc-icon name="check" [size]="16" />Approved {{ dayAt(at) }}
                </p>
              } @else {
                @if (blocks(p, pkg); as reasons) {
                  @if (reasons.length > 0) {
                    <div class="blocked" data-test="approve-blocked">
                      <sc-icon name="warning" [size]="16" />
                      <span>Before approving: {{ blockedSentence(reasons) }}</span>
                    </div>
                  }
                }
              }
              <div class="row">
                @if (pkg.approvedAt === null) {
                  @if (blocks(p, pkg); as reasons) {
                    <button
                      type="button"
                      matButton="filled"
                      data-test="package-approve"
                      [disabled]="busy() || reasons.length > 0"
                      (click)="approve(p)"
                    >
                      <sc-icon name="check" [size]="16" />Approve package
                    </button>
                  }
                }
                @if (publishable(p)) {
                  <button
                    type="button"
                    matButton="filled"
                    data-test="publish-open"
                    (click)="openPublish(p)"
                  >
                    <svg
                      class="glyph"
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
                      <path
                        d="M12 15V3M7 8l5-5 5 5M4 15v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4"
                      /></svg
                    >Publish
                  </button>
                }
              </div>
              @if (approveError(); as message) {
                <p class="err" data-test="approve-error">{{ message }}</p>
              }
            </section>
          }

          @if (p.publication; as pub) {
            <section class="card" aria-labelledby="pub-h" data-test="publication">
              <h2 id="pub-h">Publication</h2>
              <dl class="publication">
                <dt>Status</dt>
                <dd data-test="publication-status">{{ pub.status }}</dd>
                @if (pub.videoId; as id) {
                  <dt>Video</dt>
                  <dd>
                    <button
                      type="button"
                      class="link"
                      data-test="publication-video"
                      (click)="openVideo(id)"
                    >
                      {{ watchUrl(id) }}
                    </button>
                  </dd>
                }
                @if (pub.publishAt; as at) {
                  <dt>Scheduled</dt>
                  <dd data-test="publication-scheduled">{{ london(at) }} Europe/London</dd>
                }
              </dl>
              @if (pub.error; as message) {
                <p class="err" data-test="publication-error">{{ message }}</p>
              }
              @if (openError(); as message) {
                <p class="err" data-test="publication-open-error">{{ message }}</p>
              }
            </section>
          }
        }
      }
    </main>

    <ng-template #rightsDialog>
      <div class="dialog" data-test="rights-dialog">
        <h2 id="rights-title" class="dialog-title">Confirm the media rights?</h2>
        <p id="rights-body" class="dialog-body">
          You confirm that you own or may use every image, recording and piece of music in "{{
            selected()?.package?.title
          }}". Scout keeps this with the package and it cannot be undone.
        </p>
        <div class="actions">
          <button
            type="button"
            matButton="outlined"
            cdkFocusInitial
            data-test="rights-cancel"
            [mat-dialog-close]="false"
          >
            Cancel
          </button>
          <button
            type="button"
            matButton="filled"
            data-test="rights-confirm"
            [mat-dialog-close]="true"
          >
            <sc-icon name="check" [size]="16" />Confirm the rights
          </button>
        </div>
      </div>
    </ng-template>

    <ng-template #publishDialog>
      @if (publishing(); as p) {
        <div class="dialog" data-test="publish-dialog">
          <h2 id="publish-title" class="dialog-title">Publish "{{ p.package?.title }}"</h2>
          <fieldset class="privacy">
            <legend>Who can watch it</legend>
            @for (v of privacyChoices; track v.value) {
              <label class="choice" [class.choice--on]="privacy() === v.value">
                <input
                  type="radio"
                  name="publish-privacy"
                  [attr.data-test]="'privacy-' + v.value"
                  [checked]="privacy() === v.value"
                  (change)="choosePrivacy(v.value)"
                />
                <span class="choice-text">
                  <span class="choice-label">{{ v.label }}</span>
                  <span class="meta">{{ v.helper }}</span>
                </span>
              </label>
            }
          </fieldset>
          <div class="field">
            <label for="pb-at">Publish at (London time)</label>
            <input
              id="pb-at"
              type="datetime-local"
              data-test="publish-schedule"
              aria-describedby="pb-at-help"
              [disabled]="privacy() !== 'private'"
              [value]="schedule()"
              (input)="schedule.set($any($event.target).value)"
            />
            <span class="meta" id="pb-at-help"
              >Optional, only with Private: YouTube makes it public at that time.</span
            >
          </div>
          @if (scheduleUtc(); as utc) {
            <p class="meta" data-test="publish-schedule-utc">
              Publishes {{ schedule().replace('T', ' ') }} Europe/London ({{ utc }} UTC).
            </p>
          }
          <fieldset class="kids">
            <legend>Made for kids</legend>
            <label class="check">
              <input
                type="radio"
                name="publish-kids"
                data-test="kids-yes"
                [checked]="madeForKids() === true"
                (change)="madeForKids.set(true)"
              />Yes
            </label>
            <label class="check">
              <input
                type="radio"
                name="publish-kids"
                data-test="kids-no"
                [checked]="madeForKids() === false"
                (change)="madeForKids.set(false)"
              />No
            </label>
          </fieldset>
          <label class="check">
            <input
              type="checkbox"
              data-test="publish-synthetic"
              [checked]="synthetic()"
              (change)="synthetic.set($any($event.target).checked)"
            />Contains realistic synthetic or altered media
          </label>
          <div class="will-run">
            <span class="will-run-title">What will run</span>
            <ul data-test="publish-actions">
              <li>Upload the video "{{ p.package?.title }}" as {{ privacyLabel() }}</li>
              @if (p.package?.thumbnailFile; as file) {
                <li>Set the thumbnail shown above ({{ file }})</li>
              }
              @if (scheduleUtc() !== null) {
                <li>Schedule: public at {{ schedule().replace('T', ' ') }} Europe/London</li>
              }
            </ul>
          </div>
          @if (publishBlocks().length > 0) {
            <ul class="blocked-list" data-test="publish-blocked">
              @for (reason of publishBlocks(); track reason) {
                <li>{{ reason }}</li>
              }
            </ul>
          }
          @if (publishError(); as message) {
            <p class="err" data-test="publish-error">{{ message }}</p>
          }
          <div class="actions">
            <button
              type="button"
              matButton="outlined"
              cdkFocusInitial
              data-test="publish-cancel"
              (click)="closePublish()"
            >
              Cancel
            </button>
            <button
              type="button"
              matButton="filled"
              data-test="publish-confirm"
              [disabled]="busy() || publishBlocks().length > 0"
              (click)="publish(p)"
            >
              <svg
                class="glyph"
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
                <path d="M12 15V3M7 8l5-5 5 5M4 15v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4" /></svg
              >Confirm and publish
            </button>
          </div>
        </div>
      }
    </ng-template>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .page,
      .dialog {
        --mat-button-outlined-container-height: 40px;
        --mat-button-outlined-horizontal-padding: 16px;
        --mat-button-outlined-label-text-weight: 400;
        --mat-button-outlined-label-text-size: 14px;
        --mat-button-outlined-label-text-tracking: 0;
        --mat-button-filled-container-height: 40px;
        --mat-button-filled-horizontal-padding: 16px;
        --mat-button-filled-label-text-weight: 400;
        --mat-button-filled-label-text-size: 14px;
        --mat-button-filled-label-text-tracking: 0;
        --mat-button-filled-disabled-container-color: var(--sc-surface-2);
        --mat-button-filled-disabled-label-text-color: var(--sc-muted);
        color: var(--sc-fg);
        font-size: 14px;
      }
      .page {
        padding: 24px;
        max-width: 1180px;
        width: 100%;
        box-sizing: border-box;
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .sm {
        --mat-button-outlined-container-height: 36px;
        --mat-button-outlined-horizontal-padding: 14px;
      }
      sc-icon,
      .glyph {
        margin-right: 8px;
        vertical-align: middle;
      }
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-start;
        gap: 12px;
      }
      .titles {
        display: flex;
        flex-direction: column;
        gap: 4px;
        margin-right: auto;
        min-width: 0;
        flex: 1 1 320px;
      }
      h1 {
        margin: 0;
        font-size: 24px;
        font-weight: 600;
        letter-spacing: -0.01em;
      }
      h2 {
        margin: 4px 0 0;
        font-size: 16px;
        font-weight: 600;
      }
      .h2-meta {
        font-weight: 500;
        color: var(--sc-muted);
        font-size: 13px;
      }
      .lede {
        margin: 0;
        color: var(--sc-fg-2);
        line-height: 1.5;
      }
      .meta,
      .empty {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .err {
        margin: 0;
        color: var(--sc-error);
      }
      .ok {
        color: var(--sc-on-ok-container);
      }
      .card {
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 18px;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .card--video {
        gap: 14px;
      }
      .bar {
        height: 4px;
        border-radius: 2px;
        background: var(--sc-surface-2);
        overflow: hidden;
      }
      .bar-fill {
        height: 4px;
        width: 40%;
        background: var(--sc-accent);
      }
      .skeleton {
        border-radius: 10px;
        background: var(--sc-surface-2);
      }
      .skeleton--pick {
        height: 40px;
        width: 50%;
      }
      .skeleton--media {
        height: 200px;
        width: 100%;
      }
      .empty-card {
        padding: 36px 18px;
        align-items: center;
        text-align: center;
        gap: 10px;
      }
      .empty-icon {
        width: 44px;
        height: 44px;
        border-radius: 22px;
        background: var(--sc-surface-2);
        color: var(--sc-accent);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .pick {
        display: flex;
        flex-wrap: wrap;
        gap: 12px;
        align-items: flex-end;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .grow {
        flex: 1 1 320px;
      }
      label {
        font-size: 13px;
        font-weight: 600;
      }
      .label-row {
        display: flex;
        justify-content: space-between;
      }
      input[type='text'],
      input[type='datetime-local'],
      select,
      textarea {
        box-sizing: border-box;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        color: var(--sc-fg);
        font: inherit;
      }
      input[type='text'],
      input[type='datetime-local'],
      select {
        height: 40px;
        padding: 0 12px;
      }
      select {
        padding: 0 10px;
      }
      textarea {
        padding: 10px 12px;
        resize: vertical;
      }
      .tag-input {
        height: 36px;
        flex: 1 1 220px;
      }
      .row {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
        align-items: center;
      }
      .scenes {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        gap: 8px;
        overflow-x: auto;
      }
      .scenes li {
        flex: 0 0 132px;
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .tile {
        aspect-ratio: 16 / 9;
        border-radius: 8px;
        background: var(--sc-media);
        color: var(--sc-fg-2);
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 12px;
      }
      .media {
        display: flex;
        flex-wrap: wrap;
        gap: 16px;
      }
      .media-main {
        flex: 2 1 360px;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .media-side {
        flex: 1 1 240px;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .media h2 {
        margin: 0;
      }
      .media-side h2 + .shorts,
      .media-side .thumbnail + h2 {
        margin-top: 8px;
      }
      .player {
        display: block;
        width: 100%;
        aspect-ratio: 16 / 9;
        border-radius: 10px;
        background: var(--sc-media);
      }
      .thumbnail {
        display: block;
        width: 100%;
        aspect-ratio: 16 / 9;
        object-fit: cover;
        border-radius: 10px;
        background: var(--sc-media);
      }
      .shorts {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
      }
      .shorts li {
        flex: 1 1 100px;
        max-width: 140px;
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .short {
        display: block;
        width: 100%;
        aspect-ratio: 9 / 16;
        background: var(--sc-media);
        border-radius: 8px;
      }
      .hook {
        font-size: 12px;
        color: var(--sc-fg-2);
      }
      .table-frame {
        overflow-x: auto;
        border: 1px solid var(--sc-line);
        border-radius: 10px;
      }
      .checks {
        width: 100%;
        border-collapse: collapse;
        min-width: 560px;
        font-size: 13px;
      }
      .checks thead tr {
        background: var(--sc-surface-2);
        text-align: left;
      }
      .checks th,
      .checks td {
        padding: 8px 12px;
        text-align: left;
      }
      .checks tbody tr {
        border-top: 1px solid var(--sc-line);
      }
      .checks .detail {
        color: var(--sc-fg-2);
      }
      .chips {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
      }
      .chip {
        display: flex;
        align-items: center;
        gap: 2px;
        padding: 2px 2px 2px 10px;
        border-radius: 14px;
        background: var(--sc-surface-2);
        font-size: 13px;
      }
      .chip-remove {
        width: 24px;
        height: 24px;
        border: 0;
        border-radius: 12px;
        background: transparent;
        color: var(--sc-fg-2);
        display: flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
      }
      .rights {
        display: flex;
        gap: 10px;
        align-items: flex-start;
      }
      .rights input {
        width: 18px;
        height: 18px;
        margin: 2px 0 0;
      }
      .rights-text {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .rights-text label {
        font-size: 14px;
        font-weight: 400;
      }
      .rights-text .ok {
        color: var(--sc-on-ok-container);
      }
      .blocked {
        display: flex;
        gap: 10px;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
        font-size: 13px;
      }
      .blocked sc-icon {
        margin-right: 0;
      }
      .approved {
        margin: 0;
        display: flex;
        align-items: center;
      }
      .publication {
        margin: 0;
        display: grid;
        grid-template-columns: max-content 1fr;
        gap: 6px 16px;
      }
      .publication dt {
        color: var(--sc-muted);
      }
      .publication dd {
        margin: 0;
      }
      .link {
        background: none;
        border: none;
        color: var(--sc-accent);
        cursor: pointer;
        padding: 0;
        font: inherit;
      }
      .dialog {
        padding: 20px;
        display: flex;
        flex-direction: column;
        gap: 14px;
      }
      .dialog-title {
        margin: 0;
        font-size: 17px;
        font-weight: 600;
        overflow-wrap: anywhere;
      }
      .dialog-body {
        margin: 0;
        color: var(--sc-fg-2);
        line-height: 1.5;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
        justify-content: flex-end;
      }
      fieldset {
        border: 0;
        margin: 0;
        padding: 0;
        display: flex;
        gap: 8px;
      }
      legend {
        font-size: 13px;
        font-weight: 600;
        padding: 0 0 6px;
      }
      .privacy {
        flex-direction: column;
      }
      .kids {
        gap: 16px;
        flex-wrap: wrap;
      }
      .choice {
        display: flex;
        gap: 10px;
        align-items: flex-start;
        padding: 8px 10px;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        font-size: 14px;
        font-weight: 400;
      }
      .choice--on {
        border-color: var(--sc-accent);
      }
      .choice input {
        margin-top: 3px;
      }
      .choice-text {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .choice-label {
        font-weight: 500;
      }
      .check {
        display: flex;
        gap: 6px;
        align-items: center;
        font-size: 14px;
        font-weight: 400;
      }
      .will-run {
        padding: 12px;
        border-radius: 10px;
        background: var(--sc-bg);
        border: 1px solid var(--sc-line);
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .will-run-title {
        font-size: 13px;
        font-weight: 600;
      }
      .will-run ul {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .blocked-list {
        margin: 0;
        padding-left: 18px;
        font-size: 13px;
        color: var(--sc-on-warn-container);
      }
    `,
  ],
})
export class AssemblyComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly dialog = inject(MatDialog);
  private readonly rightsDialog = viewChild.required<TemplateRef<unknown>>('rightsDialog');
  private readonly publishDialog = viewChild.required<TemplateRef<unknown>>('publishDialog');
  private publishRef: MatDialogRef<unknown> | null = null;
  private timer: ReturnType<typeof setInterval> | null = null;
  private filledFrom: string | null = null;

  readonly maxTitle = MAX_TITLE;
  readonly maxDescription = MAX_DESCRIPTION;
  readonly maxTags = MAX_TAGS;
  readonly length = length;
  readonly seconds = (ms: number): number => Math.round(ms / 1000);

  readonly loaded = signal(false);
  readonly error = signal<string | null>(null);
  readonly projects = signal<readonly OperatorProjectDto[]>([]);
  readonly selectedId = signal<string | null>(null);
  readonly selected = computed(
    () => this.projects().find((p) => p.id === this.selectedId()) ?? null,
  );

  readonly busy = signal(false);
  readonly assembleError = signal<string | null>(null);
  readonly metadataError = signal<string | null>(null);
  readonly rightsError = signal<string | null>(null);
  readonly approveError = signal<string | null>(null);
  readonly openError = signal<string | null>(null);

  readonly privacyChoices = PRIVACY;
  readonly publishing = signal<OperatorProjectDto | null>(null);
  readonly privacy = signal<PrivacyStatus | null>(null);
  readonly privacyLabel = computed(
    () =>
      PRIVACY.find((choice) => choice.value === this.privacy())?.label ??
      '(choose who can watch it)',
  );
  readonly madeForKids = signal<boolean | null>(null);
  readonly synthetic = signal(false);
  readonly schedule = signal('');
  readonly publishError = signal<string | null>(null);
  readonly scheduleUtc = computed(() =>
    this.privacy() === 'private' ? londonToUtcIso(this.schedule()) : null,
  );
  readonly publishBlocks = computed(() => {
    const reasons: string[] = [];
    if (this.privacy() === null) reasons.push('Choose public, private or unlisted.');
    if (this.madeForKids() === null) reasons.push('Say whether the video is made for kids.');
    const utc = this.scheduleUtc();
    if (this.schedule() !== '' && utc === null) reasons.push('Enter a date and a time.');
    if (utc !== null && Date.parse(utc) <= Date.now()) {
      reasons.push('The scheduled time must be in the future.');
    }
    return reasons;
  });

  readonly title = signal('');
  readonly description = signal('');
  readonly tags = signal<readonly string[]>([]);
  readonly newTag = signal('');
  readonly tagsLength = computed(
    () =>
      this.tags().reduce((total, tag) => total + tag.length + (tag.includes(' ') ? 2 : 0), 0) +
      Math.max(0, this.tags().length - 1),
  );
  readonly metadataValid = computed(() => {
    const title = this.title().trim().length;
    return (
      title > 0 &&
      title <= MAX_TITLE &&
      this.description().trim().length <= MAX_DESCRIPTION &&
      this.tags().every((tag) => tag.length <= MAX_TAG) &&
      this.tagsLength() <= MAX_TAGS
    );
  });

  async ngOnInit(): Promise<void> {
    this.timer = setInterval(() => void this.refresh(), POLL_MS);
    await this.refresh();
  }

  ngOnDestroy(): void {
    if (this.timer !== null) clearInterval(this.timer);
  }

  select(id: string): void {
    this.selectedId.set(id === '' ? null : id);
    this.assembleError.set(null);
    this.metadataError.set(null);
    this.rightsError.set(null);
    this.approveError.set(null);
    this.fill();
  }

  statusLabel(status: string): string {
    return (
      STATUS_WORDS[status] ?? status.charAt(0).toUpperCase() + status.slice(1).replace(/_/g, ' ')
    );
  }

  statusTone(status: string): ChipTone {
    return STATUS_TONES[status] ?? 'neutral';
  }

  checkResult(result: string): { label: string; tone: ChipTone } {
    return CHECK_RESULTS[result] ?? { label: result, tone: 'neutral' };
  }

  again(project: OperatorProjectDto): boolean {
    return (project.package?.videoFile ?? null) !== null;
  }

  sceneTiles(project: OperatorProjectDto): { label: string; time: string; alt: string }[] {
    let start = 0;
    return [...project.scenes]
      .sort((a, b) => a.order - b.order)
      .map((s, i) => {
        const time = clock(start);
        start += s.durationMs;
        return { label: `Scene ${i + 1}`, time, alt: `Scene ${i + 1}, starts at ${time}` };
      });
  }

  scenesSummary(project: OperatorProjectDto): string {
    const count = project.scenes.length;
    const total = project.scenes.reduce((sum, s) => sum + s.durationMs, 0);
    return `${count} ${count === 1 ? 'scene' : 'scenes'} · ${length(total)}`;
  }

  dayAt(iso: string): string {
    const day = formatDate(iso, 'yyyy-MM-dd', 'en-US');
    const today = formatDate(Date.now(), 'yyyy-MM-dd', 'en-US');
    return `${day === today ? 'today' : day} at ${formatDate(iso, 'HH:mm', 'en-US')}`;
  }

  blockedSentence(reasons: readonly string[]): string {
    const sentence = reasons.join(' ');
    return sentence.charAt(0).toLowerCase() + sentence.slice(1);
  }

  assemblable(project: OperatorProjectDto): boolean {
    return (
      ASSEMBLABLE.includes(project.status) ||
      (project.status === 'failed' &&
        project.scenes.length > 0 &&
        project.scenes.every((s) => s.status === 'ready'))
    );
  }

  editable(project: OperatorProjectDto): boolean {
    return EDITABLE.includes(project.status);
  }

  blocks(project: OperatorProjectDto, pkg: VideoPackageDto): string[] {
    const reasons: string[] = [];
    if (project.status !== 'assembled') reasons.push('The video is not assembled.');
    if (!pkg.rightsConfirmed) reasons.push('Confirm the media rights first.');
    const failed = pkg.checks.filter((c) => c.result === 'fail').length;
    if (failed > 0) reasons.push(failed === 1 ? '1 check failed.' : `${failed} checks failed.`);
    const open = project.claims.filter(
      (c) => c.status !== 'supported' && c.status !== 'waived',
    ).length;
    if (open > 0) {
      reasons.push(
        open === 1
          ? '1 claim is not supported or waived.'
          : `${open} claims are not supported or waived.`,
      );
    }
    return reasons;
  }

  publishable(project: OperatorProjectDto): boolean {
    const pub = project.publication;
    return (
      project.package?.approvedAt != null &&
      (pub === null || (pub.status === 'failed' && pub.videoId === null))
    );
  }

  openPublish(project: OperatorProjectDto): void {
    this.privacy.set(null);
    this.madeForKids.set(null);
    this.schedule.set('');
    this.publishError.set(null);
    this.synthetic.set(
      project.scenes.some((s) => s.audioSource === 'generated' || s.visualSource === 'generated'),
    );
    this.publishing.set(project);
    const ref = this.dialog.open(this.publishDialog(), {
      role: 'dialog',
      ariaLabelledBy: 'publish-title',
      restoreFocus: true,
      panelClass: 'sc-dialog',
      backdropClass: 'sc-dialog-backdrop',
      width: '540px',
      maxWidth: 'calc(100vw - 32px)',
      maxHeight: 'calc(100vh - 176px)',
      position: { top: '160px' },
    });
    this.publishRef = ref;
    ref.afterClosed().subscribe(() => {
      if (this.publishRef === ref) {
        this.publishRef = null;
        this.publishing.set(null);
      }
    });
  }

  closePublish(): void {
    this.publishRef?.close();
  }

  choosePrivacy(privacy: PrivacyStatus): void {
    this.privacy.set(privacy);
    if (privacy !== 'private') this.schedule.set('');
  }

  async publish(project: OperatorProjectDto): Promise<void> {
    const privacyStatus = this.privacy();
    const madeForKids = this.madeForKids();
    if (privacyStatus === null || madeForKids === null) return;
    await this.act(this.publishError, () =>
      this.bridge.operatorPublishPackage({
        projectId: project.id,
        privacyStatus,
        publishAt: this.scheduleUtc(),
        madeForKids,
        syntheticMedia: this.synthetic(),
      }),
    );
    if (this.publishError() === null) this.closePublish();
  }

  watchUrl(videoId: string): string {
    return `https://www.youtube.com/watch?v=${encodeURIComponent(videoId)}`;
  }

  async openVideo(videoId: string): Promise<void> {
    this.openError.set(null);
    try {
      await this.bridge.appOpenExternal({ url: this.watchUrl(videoId) });
    } catch (err: unknown) {
      this.openError.set(readIpcError(err).message);
    }
  }

  london(iso: string): string {
    return formatLondon(iso);
  }

  mediaUrl(project: OperatorProjectDto, fileName: string): string {
    return `scout-media://${project.id}/${encodeURIComponent(fileName)}?v=${encodeURIComponent(project.updatedAt)}`;
  }

  addTag(): void {
    const added = this.newTag()
      .split(',')
      .map((tag) => tag.trim())
      .filter((tag) => tag.length > 0);
    this.tags.update((tags) => [...new Set([...tags, ...added])]);
    this.newTag.set('');
  }

  removeTag(index: number): void {
    this.tags.update((tags) => tags.filter((_, i) => i !== index));
  }

  assemble(project: OperatorProjectDto): Promise<void> {
    return this.act(this.assembleError, () => this.bridge.operatorAssemble({ id: project.id }));
  }

  saveMetadata(project: OperatorProjectDto): Promise<void> {
    return this.act(this.metadataError, () =>
      this.bridge.operatorEditPackageMetadata({
        projectId: project.id,
        title: this.title().trim(),
        description: this.description().trim(),
        tags: this.tags(),
      }),
    );
  }

  async confirmRights(project: OperatorProjectDto, event: Event): Promise<void> {
    const box = event.target as HTMLInputElement;
    box.checked = false;
    this.rightsError.set(null);
    const ref = this.dialog.open<unknown, unknown, boolean>(this.rightsDialog(), {
      role: 'alertdialog',
      ariaLabelledBy: 'rights-title',
      ariaDescribedBy: 'rights-body',
      restoreFocus: true,
      panelClass: 'sc-dialog',
      backdropClass: 'sc-dialog-backdrop',
      width: '480px',
      maxWidth: 'calc(100vw - 32px)',
      position: { top: '260px' },
    });
    if ((await firstValueFrom(ref.afterClosed())) === true) {
      await this.act(this.rightsError, () =>
        this.bridge.operatorConfirmMediaRights({ id: project.id }),
      );
    }
    box.checked = this.selected()?.package?.rightsConfirmed ?? false;
  }

  approve(project: OperatorProjectDto): Promise<void> {
    return this.act(this.approveError, () =>
      this.bridge.operatorApprovePackage({ id: project.id }),
    );
  }

  private async act(
    failure: WritableSignal<string | null>,
    action: () => Promise<OperatorProjectDto>,
  ): Promise<void> {
    this.busy.set(true);
    failure.set(null);
    try {
      const updated = await action();
      this.projects.update((all) => all.map((p) => (p.id === updated.id ? updated : p)));
      this.fill();
    } catch (err: unknown) {
      failure.set(readIpcError(err).message);
    } finally {
      this.busy.set(false);
    }
  }

  private fill(): void {
    const pkg = this.selected()?.package ?? null;
    const from =
      pkg === null
        ? null
        : JSON.stringify([this.selectedId(), pkg.title, pkg.description, pkg.tags]);
    if (pkg === null || from === this.filledFrom) return;
    this.filledFrom = from;
    this.title.set(pkg.title);
    this.description.set(pkg.description);
    this.tags.set(pkg.tags);
    this.newTag.set('');
  }

  private async refresh(): Promise<void> {
    try {
      const { projects } = await this.bridge.operatorProjects({ limit: PROJECTS_READ });
      this.projects.set(
        projects
          .filter((p) => ASSEMBLY_STATUSES.includes(p.status) || p.publication !== null)
          .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)),
      );
      this.fill();
      this.error.set(null);
      this.loaded.set(true);
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    }
  }
}
