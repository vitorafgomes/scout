import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleChange, MatSlideToggleModule } from '@angular/material/slide-toggle';

import {
  readIpcError,
  type DockerImage,
  type DockerPullProgress,
  type DockerRepository,
  type DockerRun,
  type DockerState,
  type DockerWorkFolder,
  RESTART_DEFERRED_LINE,
} from '@scout/shared';

import { confirmAction } from '../core/confirm-dialog.component';
import { IconComponent } from '../core/icon.component';
import { NoticeService } from '../core/notice.service';
import { ScoutBridgeService } from '../core/scout-bridge';

/** What the GitHub card needs to know of Docker: whether it answers and whether the owner turned its runs on. */
export interface DockerCardStatus {
  readonly running: boolean;
  readonly enabled: boolean;
}

export type DockerSection = 'uses' | 'images' | 'repositories' | 'folder' | 'last';

export const DOCKER_SECTIONS: readonly DockerSection[] = [
  'uses',
  'images',
  'repositories',
  'folder',
  'last',
];

export const DOCKER_SECTIONS_STORAGE_KEY = 'scout:docker-card.sections';
export const DOCKER_INSTALL_URL = 'https://docs.docker.com/get-started/get-docker/';
export const DOCKER_ENABLED_KEY = 'sandbox.enabled';

export const NOTHING_BUT_DOCKER =
  "This computer needs nothing but Docker. The .NET, Node, Bun, Python, Go and Java SDKs come inside the container, so Scout runs a project's tests without installing anything on your machine.";

/** Why "Download images" waits: it pulls only what the repositories Scout has read need, and that list does not exist yet. */
export const NO_REPOSITORIES_DOWNLOAD_HELP =
  'Download images fetches only what the repositories Scout has read need, and Scout has not listed them yet. Docker Hub limits anonymous downloads and the Java images are several hundred MB each, so download a stack below when you need it.';

/** What "Download images" does once Scout has read repositories. */
export const REPOSITORIES_DOWNLOAD_HELP =
  'Download images fetches the one image each repository Scout has read needs and Docker does not hold yet, one at a time; a repository on a version Scout does not run is skipped. Docker Hub limits anonymous downloads, so nothing else is fetched.';

/** "Download images" with nothing left to fetch. */
export const ALL_NEEDED_DOWNLOADED = 'Every image your repositories need is downloaded.';

/** The pinned images of one stack and how many Docker already holds. */
export interface ImageGroup {
  readonly stack: string;
  readonly images: readonly DockerImage[];
  readonly present: number;
}

export const DOCKER_USES: readonly string[] = [
  "Running a pull request's own tests before it gives a verdict, when no check ran them or the review asks for one.",
  "The project's own tests only, never Scout's own code.",
  'Offline during the tests; the network is on only to restore the packages.',
  'One disposable container per run, deleted after it.',
  '20 minutes at most per run.',
];

/** What Refresh says once Docker answered, one notice per state. */
export const REFRESHED: Readonly<Record<DockerState['state'], string>> = {
  running: 'Docker is running',
  notRunning: 'Docker is still not running',
  notInstalled: 'Docker is still not installed',
};

/** What the card's Turn on says once the switch is saved and Scout's restart did not fail. */
export const TURNED_ON = "Scout may run a project's tests in isolated containers";

const SECTION_TITLES: Readonly<Record<DockerSection, string>> = {
  uses: 'What Scout uses Docker for',
  images: 'Pinned images per stack',
  repositories: 'Repositories',
  folder: 'Work folder',
  last: 'Last run',
};

function storage(): Storage | null {
  try {
    return (globalThis as { window?: Window }).window?.localStorage ?? null;
  } catch {
    return null;
  }
}

function readSections(): Partial<Record<DockerSection, boolean>> {
  try {
    const raw = storage()?.getItem(DOCKER_SECTIONS_STORAGE_KEY) ?? null;
    if (raw === null) return {};
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return {};
    const entries = Object.entries(parsed).filter(
      ([key, value]) =>
        DOCKER_SECTIONS.includes(key as DockerSection) && typeof value === 'boolean',
    );
    return Object.fromEntries(entries) as Partial<Record<DockerSection, boolean>>;
  } catch {
    return {};
  }
}

function writeSections(sections: Partial<Record<DockerSection, boolean>>): void {
  try {
    storage()?.setItem(DOCKER_SECTIONS_STORAGE_KEY, JSON.stringify(sections));
  } catch {
    return;
  }
}

/** One repository as the card lists it: the stack and image group from the pinned list when the image is one of them. */
export interface RepositoryRow {
  readonly repository: string;
  readonly stack: string;
  readonly runs: string | null;
  readonly declared: string;
  readonly refusal: string | null;
  readonly image: string | null;
  /** The pinned image's stack group, for its Download; null when no run would happen or Docker's images are unknown. */
  readonly group: string | null;
  /** Whether Docker holds the image; null when no run would happen or Docker's images are unknown. */
  readonly present: boolean | null;
}

export const RUN_RESULT_LABELS: Readonly<Record<DockerRun['result'], string>> = {
  passed: 'Passed',
  failed: 'Failed',
  notRun: 'Did not run',
};

/** A run's duration as the card shows it: seconds under a minute, else minutes and seconds. */
export function formatDuration(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds * 10) / 10} s`;
  const whole = Math.round(seconds);
  return `${Math.floor(whole / 60)} min ${whole % 60} s`;
}

/** Bytes as the card shows them: B, KB, MB, GB or TB, one decimal under ten. */
export function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  const shown = unit === 0 ? String(value) : value.toFixed(value < 10 ? 1 : 0);
  return `${shown} ${units[unit]}`;
}

function shortReference(reference: string): string {
  const at = reference.indexOf('@sha256:');
  return at < 0 ? reference : `${reference.slice(0, at + 16)}...`;
}

/** Docker in its Features panel: Docker's state, the switch for Scout's runs, the pinned images, the repositories, the work folder and the last run; its chip and status feed the Docker card. */
@Component({
  selector: 'sc-docker-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, IconComponent, MatButtonModule, MatProgressBarModule, MatSlideToggleModule],
  template: `
    <div class="docker">
      <div class="status-row">
        <span class="status" data-test="docker-status-line">{{ statusLine() }}</span>
        @if (state()?.state === 'notInstalled') {
          <button
            type="button"
            matButton="outlined"
            data-test="docker-install-guide"
            (click)="openInstallGuide()"
          >
            <sc-icon name="external" [size]="16" />How to install Docker
          </button>
        }
        <button
          type="button"
          matButton="outlined"
          data-test="docker-refresh"
          [disabled]="refreshing()"
          (click)="refresh()"
        >
          <sc-icon name="refresh" [size]="16" />Refresh
        </button>
      </div>

      <div class="switch-row">
        <div class="switch-text">
          <span id="docker-switch-label" class="switch-label"
            >Run a project's tests in isolated containers</span
          >
          <span id="docker-switch-help" class="help" data-test="docker-switch-help">{{
            switchHelp()
          }}</span>
        </div>
        <mat-slide-toggle
          data-test="docker-switch"
          aria-labelledby="docker-switch-label"
          aria-describedby="docker-switch-help"
          [checked]="enabled()"
          [disabled]="!githubConnected() || switchBusy()"
          (change)="setEnabled($event)"
        />
      </div>
      @if (switchError(); as e) {
        <p class="err" role="alert" data-test="docker-switch-error">{{ e }}</p>
      }
      @if (switchNote(); as note) {
        <p class="help" role="status" data-test="docker-switch-note">{{ note }}</p>
      }

      @for (id of sections; track id) {
        <section class="section" [attr.data-test]="'docker-section-' + id">
          <button
            type="button"
            class="disclosure"
            [attr.id]="'docker-head-' + id"
            [attr.aria-expanded]="isOpen(id)"
            [attr.aria-controls]="'docker-body-' + id"
            [attr.data-test]="'docker-toggle-' + id"
            (click)="toggle(id)"
          >
            <svg class="chev" viewBox="0 0 16 16" aria-hidden="true">
              <path d="M6 3l5 5-5 5" fill="none" stroke="currentColor" stroke-width="2" />
            </svg>
            <span>{{ isOpen(id) ? titles[id] : summary(id) }}</span>
          </button>
          @if (isOpen(id)) {
            <div
              class="body"
              role="region"
              [attr.id]="'docker-body-' + id"
              [attr.aria-labelledby]="'docker-head-' + id"
              [attr.data-test]="'docker-body-' + id"
            >
              @switch (id) {
                @case ('uses') {
                  <ul class="uses">
                    @for (use of uses; track use) {
                      <li>{{ use }}</li>
                    }
                  </ul>
                }
                @case ('images') {
                  <div class="row">
                    <span class="help" id="docker-images-help" data-test="docker-images-help">{{
                      imagesHelp()
                    }}</span>
                    <button
                      type="button"
                      matButton="outlined"
                      data-test="docker-download"
                      aria-describedby="docker-download-help"
                      [disabled]="!running() || pulling() || neededMissing().length === 0"
                      (click)="downloadNeeded()"
                    >
                      <sc-icon name="download" [size]="16" />Download images
                    </button>
                  </div>
                  <span class="help" id="docker-download-help" data-test="docker-download-help">{{
                    downloadHelp()
                  }}</span>
                  @if (neededProgress(); as p) {
                    <div class="progress" data-test="docker-needed-progress">
                      <span
                        >Downloading {{ p.index }} of {{ p.total }}: {{ short(p.reference) }}</span
                      >
                      <mat-progress-bar
                        mode="determinate"
                        [value]="((p.index - 1) / p.total) * 100"
                        aria-label="Download progress of the repositories' images"
                      />
                    </div>
                  }
                  @if (pullError(); as e) {
                    <p class="err" role="alert" data-test="docker-pull-error">{{ e }}</p>
                  }
                  <ul class="groups">
                    @for (group of groups(); track group.stack) {
                      <li class="group" [attr.data-test]="'docker-group-' + group.stack">
                        <div class="row">
                          <span
                            class="stack"
                            [attr.data-test]="'docker-group-summary-' + group.stack"
                            >{{ group.stack }}: {{ group.present }} of
                            {{ group.images.length }} downloaded</span
                          >
                          <button
                            type="button"
                            matButton="outlined"
                            [attr.data-test]="'docker-download-' + group.stack"
                            [attr.aria-label]="'Download the ' + group.stack + ' images'"
                            [disabled]="
                              !running() || pulling() || group.present === group.images.length
                            "
                            (click)="download(group.stack)"
                          >
                            <sc-icon name="download" [size]="16" />Download
                          </button>
                        </div>
                        @if (progress(); as p) {
                          @if (pullingStack() === group.stack) {
                            <div class="progress" data-test="docker-progress">
                              <span
                                >Downloading {{ p.index }} of {{ p.total }}:
                                {{ short(p.reference) }}</span
                              >
                              <mat-progress-bar
                                mode="determinate"
                                [value]="((p.index - 1) / p.total) * 100"
                                aria-label="Image download progress"
                              />
                            </div>
                          }
                        }
                        <ul class="images">
                          @for (image of group.images; track image.reference) {
                            <li [attr.data-test]="'docker-image-' + image.reference">
                              <code [title]="image.reference">{{ short(image.reference) }}</code>
                              <span
                                class="chip"
                                [class.chip--ok]="image.present"
                                [class.chip--neutral]="!image.present"
                                ><svg
                                  class="chip-glyph"
                                  viewBox="0 0 24 24"
                                  width="12"
                                  height="12"
                                  fill="none"
                                  stroke="currentColor"
                                  stroke-width="2.4"
                                  stroke-linecap="round"
                                  stroke-linejoin="round"
                                  aria-hidden="true"
                                >
                                  <path [attr.d]="image.present ? glyphs.ok : glyphs.dot" /></svg
                                >{{ image.present ? 'Downloaded' : 'Not downloaded' }}</span
                              >
                            </li>
                          }
                        </ul>
                      </li>
                    }
                  </ul>
                }
                @case ('repositories') {
                  <span class="help"
                    >The stack and versions Scout detected when it read each repository.</span
                  >
                  @if (repositoryRows().length === 0) {
                    <div class="empty" data-test="docker-repositories-empty">
                      <span class="empty-mark" aria-hidden="true"
                        ><svg
                          viewBox="0 0 24 24"
                          width="20"
                          height="20"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="1.8"
                          stroke-linecap="round"
                          stroke-linejoin="round"
                        >
                          <path d="M4 4h6l2 2h8v14H4zM4 10h16" /></svg
                      ></span>
                      <span>Scout lists a repository's stack the first time it reads it.</span>
                    </div>
                  } @else {
                    <ul class="repos" data-test="docker-repositories">
                      @for (row of repositoryRows(); track row.repository) {
                        <li class="repo" [attr.data-test]="'docker-repository-' + row.repository">
                          <div class="repo-text">
                            <span class="repo-name">{{ row.repository }}</span>
                            @if (row.refusal !== null) {
                              <span
                                class="repo-detail repo-detail--refused"
                                data-test="docker-repository-refusal"
                                ><span data-test="docker-repository-stack">{{ row.stack }}</span
                                >: {{ row.refusal }}</span
                              >
                            } @else {
                              <span class="repo-detail"
                                ><span data-test="docker-repository-stack">{{ row.stack }}</span> ·
                                <span data-test="docker-repository-versions"
                                  >Runs on {{ row.runs }}. {{ row.declared }}</span
                                ></span
                              >
                              <code class="repo-image" [title]="row.image">{{
                                short(row.image ?? '')
                              }}</code>
                            }
                          </div>
                          @if (row.refusal !== null) {
                            <span class="chip chip--warn" data-test="docker-repository-unsupported"
                              ><svg
                                class="chip-glyph"
                                viewBox="0 0 24 24"
                                width="12"
                                height="12"
                                fill="none"
                                stroke="currentColor"
                                stroke-width="2.4"
                                stroke-linecap="round"
                                stroke-linejoin="round"
                                aria-hidden="true"
                              >
                                <path [attr.d]="glyphs.warn" /></svg
                              >Not supported</span
                            >
                          } @else {
                            <span
                              class="chip"
                              data-test="docker-repository-image"
                              [attr.id]="'docker-repository-chip-' + $index"
                              [class.chip--ok]="row.present === true"
                              [class.chip--neutral]="row.present !== true"
                              ><svg
                                class="chip-glyph"
                                viewBox="0 0 24 24"
                                width="12"
                                height="12"
                                fill="none"
                                stroke="currentColor"
                                stroke-width="2.4"
                                stroke-linecap="round"
                                stroke-linejoin="round"
                                aria-hidden="true"
                              >
                                <path
                                  [attr.d]="row.present === true ? glyphs.ok : glyphs.dot"
                                /></svg
                              >{{
                                row.present === null
                                  ? 'Docker is not running'
                                  : row.present
                                    ? 'Image downloaded'
                                    : 'Image not downloaded'
                              }}</span
                            >
                          }
                          @if (
                            row.image !== null &&
                            (row.present === false || (row.present === null && !running()))
                          ) {
                            <button
                              type="button"
                              matButton="outlined"
                              data-test="docker-repository-download"
                              [attr.aria-label]="'Download the image for ' + row.repository"
                              [attr.aria-describedby]="
                                running() ? null : 'docker-repository-chip-' + $index
                              "
                              [disabled]="!running() || pulling()"
                              (click)="downloadImage(row.image)"
                            >
                              <sc-icon name="download" [size]="15" />Download
                            </button>
                          }
                        </li>
                      }
                    </ul>
                  }
                }
                @case ('folder') {
                  <div class="row">
                    <div class="folder">
                      <code data-test="docker-folder-path">{{ folder()?.path ?? '' }}</code>
                      <span class="help" data-test="docker-folder-size">{{ folderSize() }}</span>
                    </div>
                    <button
                      type="button"
                      matButton="outlined"
                      class="danger"
                      data-test="docker-clear"
                      [disabled]="clearing() || (folder()?.bytes ?? 0) === 0"
                      (click)="clear()"
                    >
                      <sc-icon name="trash" [size]="16" />Clear
                    </button>
                  </div>
                  @if (clearRefused(); as e) {
                    <p class="err" role="alert" data-test="docker-clear-refused">{{ e }}</p>
                  }
                }
                @case ('last') {
                  @if (lastRun(); as run) {
                    <dl class="tiles" data-test="docker-last-details">
                      <div class="tile">
                        <dt>Repository</dt>
                        <dd data-test="docker-last-what">{{ run.repository ?? 'A repository' }}</dd>
                      </div>
                      @if (run.pullRequest !== null) {
                        <div class="tile">
                          <dt>Pull request</dt>
                          <dd data-test="docker-last-pr">#{{ run.pullRequest }}</dd>
                        </div>
                      }
                      <div class="tile">
                        <dt>Stack</dt>
                        <dd data-test="docker-last-stack">{{ stackOf(run.image, run.stack) }}</dd>
                      </div>
                      <div class="tile">
                        <dt>Result</dt>
                        <dd
                          class="result"
                          data-test="docker-last-result"
                          [class.chip--ok]="run.result === 'passed'"
                          [class.chip--warn]="run.result === 'failed'"
                          [class.chip--neutral]="run.result === 'notRun'"
                        >
                          {{ resultLabel(run) }}
                        </dd>
                      </div>
                      <div class="tile">
                        <dt>Duration</dt>
                        <dd data-test="docker-last-duration">
                          {{ duration(run.durationSeconds) }}
                        </dd>
                      </div>
                      <div class="tile">
                        <dt>Finished</dt>
                        <dd data-test="docker-last-finished">
                          {{ run.finishedAt | date: 'medium' }}
                        </dd>
                      </div>
                    </dl>
                    @if (run.reason !== null) {
                      <span class="help" data-test="docker-last-reason">{{ run.reason }}</span>
                    }
                  } @else {
                    <p class="empty" data-test="docker-last-empty">No run yet.</p>
                  }
                }
              }
            </div>
          }
        </section>
      }
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
        min-width: 0;
      }
      .docker {
        color: var(--sc-fg);
        display: flex;
        flex-direction: column;
        gap: 12px;
        font-size: 14px;
        line-height: 1.5;
      }
      .chip {
        display: inline-flex;
        align-items: center;
        gap: 5px;
        padding: 2px 9px;
        border-radius: 12px;
        font-size: 12px;
        font-weight: 600;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .chip--ok {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .chip--warn {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .chip-glyph {
        flex: none;
      }
      .tiles {
        margin: 0;
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      .tile {
        flex: 1 1 110px;
        display: flex;
        flex-direction: column;
        gap: 2px;
        padding: 8px 10px;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
      }
      .tile dt {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .tile dd {
        margin: 0;
        font-size: 13px;
        font-weight: 600;
        color: var(--sc-fg);
      }
      .tile dd.result {
        background: none;
      }
      .tile dd.chip--ok {
        color: var(--sc-on-ok-container);
      }
      .tile dd.chip--warn {
        color: var(--sc-error);
      }
      .status-row,
      .row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .status,
      .row > .help,
      .folder {
        margin-right: auto;
      }
      .status {
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .switch-row {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 12px;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
      }
      .switch-text {
        display: flex;
        flex-direction: column;
        gap: 3px;
        margin-right: auto;
        min-width: 0;
      }
      .switch-label {
        font-weight: 500;
      }
      .switch-text .help {
        line-height: 1.45;
      }
      mat-slide-toggle {
        flex: none;
        --mat-slide-toggle-track-width: 40px;
        --mat-slide-toggle-track-height: 24px;
        --mat-slide-toggle-track-outline-width: 1px;
        --mat-slide-toggle-track-outline-color: var(--sc-line);
        --mat-slide-toggle-selected-track-outline-color: var(--sc-accent-strong);
        --mat-slide-toggle-unselected-track-color: var(--sc-surface-2);
        --mat-slide-toggle-unselected-hover-track-color: var(--sc-surface-2);
        --mat-slide-toggle-unselected-focus-track-color: var(--sc-surface-2);
        --mat-slide-toggle-unselected-pressed-track-color: var(--sc-surface-2);
        --mat-slide-toggle-selected-track-color: var(--sc-accent-strong);
        --mat-slide-toggle-selected-hover-track-color: var(--sc-accent-strong);
        --mat-slide-toggle-selected-focus-track-color: var(--sc-accent-strong);
        --mat-slide-toggle-selected-pressed-track-color: var(--sc-accent-strong);
        --mat-slide-toggle-unselected-handle-size: 16px;
        --mat-slide-toggle-selected-handle-size: 16px;
        --mat-slide-toggle-pressed-handle-size: 16px;
        --mat-slide-toggle-unselected-handle-color: var(--sc-muted);
        --mat-slide-toggle-unselected-hover-handle-color: var(--sc-muted);
        --mat-slide-toggle-unselected-focus-handle-color: var(--sc-muted);
        --mat-slide-toggle-unselected-pressed-handle-color: var(--sc-muted);
        --mat-slide-toggle-selected-handle-color: var(--sc-on-accent);
        --mat-slide-toggle-selected-hover-handle-color: var(--sc-on-accent);
        --mat-slide-toggle-selected-focus-handle-color: var(--sc-on-accent);
        --mat-slide-toggle-selected-pressed-handle-color: var(--sc-on-accent);
        --mat-slide-toggle-selected-track-outline-width: 1px;
        --mat-slide-toggle-with-icon-handle-size: 16px;
        --mat-slide-toggle-unselected-handle-horizontal-margin: 0 3px;
        --mat-slide-toggle-unselected-pressed-handle-horizontal-margin: 0 3px;
        --mat-slide-toggle-unselected-with-icon-handle-horizontal-margin: 0 3px;
        --mat-slide-toggle-selected-handle-horizontal-margin: 0 21px;
        --mat-slide-toggle-selected-pressed-handle-horizontal-margin: 0 21px;
        --mat-slide-toggle-selected-with-icon-handle-horizontal-margin: 0 21px;
        --mat-slide-toggle-disabled-track-opacity: 0.55;
        --mat-slide-toggle-disabled-unselected-track-color: var(--sc-surface-2);
        --mat-slide-toggle-disabled-unselected-track-outline-color: var(--sc-line);
        --mat-slide-toggle-disabled-unselected-handle-color: var(--sc-muted);
        --mat-slide-toggle-disabled-unselected-handle-opacity: 0.55;
        --mat-slide-toggle-disabled-selected-track-color: var(--sc-accent-strong);
        --mat-slide-toggle-disabled-selected-handle-color: var(--sc-on-accent);
        --mat-slide-toggle-disabled-selected-handle-opacity: 0.55;
        --mat-slide-toggle-handle-elevation-shadow: none;
        --mat-slide-toggle-hidden-track-transition: none;
        --mat-slide-toggle-visible-track-transition: none;
        --mat-slide-toggle-selected-icon-size: 0px;
        --mat-slide-toggle-unselected-icon-size: 0px;
      }
      .help {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .section {
        border-top: 1px solid var(--sc-line);
        padding-top: 8px;
      }
      .disclosure {
        display: flex;
        align-items: center;
        gap: 8px;
        width: 100%;
        border: 0;
        background: transparent;
        color: var(--sc-fg);
        font: inherit;
        font-size: 14px;
        font-weight: 600;
        padding: 4px 0;
        cursor: pointer;
        text-align: left;
      }
      .chev {
        width: 14px;
        height: 14px;
        flex: 0 0 14px;
        transition: transform 0.15s;
      }
      .disclosure[aria-expanded='true'] .chev {
        transform: rotate(90deg);
      }
      .body {
        display: flex;
        flex-direction: column;
        gap: 8px;
        padding: 8px 0 4px 22px;
      }
      .uses {
        margin: 0;
        padding-left: 18px;
        display: flex;
        flex-direction: column;
        gap: 4px;
        font-size: 13px;
        line-height: 1.45;
        color: var(--sc-fg-2);
      }
      .groups,
      .images {
        list-style: none;
        margin: 0;
        padding: 0;
      }
      .group {
        display: flex;
        flex-direction: column;
        gap: 4px;
        padding: 8px 0;
        border-top: 1px solid var(--sc-line);
      }
      .group > .row > .stack {
        margin-right: auto;
      }
      .images {
        display: flex;
        flex-direction: column;
        gap: 2px;
        padding-left: 12px;
      }
      .images li {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .stack {
        font-size: 13px;
        font-weight: 500;
      }
      code {
        font-family: var(--sc-mono);
        font-size: 12px;
        color: var(--sc-fg-2);
        overflow-wrap: anywhere;
      }
      .images code {
        flex: 1 1 200px;
        min-width: 0;
      }
      .folder {
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 0;
      }
      .progress {
        display: flex;
        flex-direction: column;
        gap: 4px;
        font-size: 12px;
        color: var(--sc-fg-2);
      }
      .repos {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
      }
      .repo {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
        padding: 8px 0;
        border-top: 1px solid var(--sc-line);
      }
      .repo-text {
        flex: 1 1 220px;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .repo-name {
        font-size: 13px;
        font-weight: 600;
      }
      .repo-detail {
        font-size: 12px;
        color: var(--sc-fg-2);
      }
      .repo-detail--refused {
        color: var(--sc-on-warn-container);
      }
      .repo-image {
        font-size: 12px;
        color: var(--sc-muted);
      }
      [data-test='docker-repository-download'] sc-icon {
        margin-right: 6px;
      }
      .empty-mark {
        width: 40px;
        height: 40px;
        border-radius: 20px;
        display: flex;
        align-items: center;
        justify-content: center;
        background: var(--sc-surface-2);
        color: var(--sc-muted);
      }
      .empty {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 8px;
        margin: 0;
        padding: 18px 12px;
        border: 1px dashed var(--sc-line);
        border-radius: 10px;
        text-align: center;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .err {
        margin: 0;
        font-size: 13px;
        color: var(--sc-error);
      }
      .danger {
        --mat-button-outlined-label-text-color: var(--sc-error);
      }
      .group button,
      [data-test='docker-repository-download'] {
        --mat-button-outlined-container-height: 32px;
        --mat-button-outlined-horizontal-padding: 12px;
        --mat-button-outlined-label-text-size: 13px;
      }
      .group button sc-icon {
        margin-right: 6px;
      }
      button sc-icon {
        margin-right: 8px;
      }
    `,
  ],
})
export class DockerCardComponent implements OnInit {
  readonly glyphs = {
    ok: 'M20 6L9 17l-5-5',
    dot: 'M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z',
    warn: 'M12 3l9 16H3zM12 10v4M12 17v.5',
  };
  private readonly bridge = inject(ScoutBridgeService);
  private readonly dialog = inject(MatDialog);
  private readonly notice = inject(NoticeService);

  readonly githubConnected = input(false);
  readonly statusChange = output<DockerCardStatus>();

  readonly uses = DOCKER_USES;
  readonly sections = DOCKER_SECTIONS;
  readonly titles = SECTION_TITLES;

  readonly state = signal<DockerState | null>(null);
  readonly enabled = signal(false);
  readonly images = signal<readonly DockerImage[] | null>(null);
  readonly folder = signal<DockerWorkFolder | null>(null);
  readonly repositories = signal<readonly DockerRepository[]>([]);
  readonly lastRun = signal<DockerRun | null>(null);
  readonly progress = signal<DockerPullProgress | null>(null);
  readonly pulling = signal(false);
  readonly pullingStack = signal<string | null>(null);
  readonly pullError = signal<string | null>(null);
  readonly clearing = signal(false);
  readonly clearRefused = signal<string | null>(null);
  readonly switchBusy = signal(false);
  readonly switchError = signal<string | null>(null);
  /** Main's answer when the restart that applies the switch waits for the running answer or the live listen. */
  readonly switchNote = signal<string | null>(null);
  readonly refreshing = signal(false);
  readonly stateFailed = signal(false);
  private readonly imagesNeedAttention = signal(false);
  private readonly chosen = signal<Partial<Record<DockerSection, boolean>>>(readSections());

  readonly running = computed(() => this.state()?.state === 'running');
  readonly missingCount = computed(() => (this.images() ?? []).filter((i) => !i.present).length);

  readonly chipLabel = computed(() => {
    const s = this.state();
    if (this.stateFailed()) return 'Unknown';
    if (s === null) return 'Checking...';
    if (s.state === 'running') return `Running · Docker ${s.version}`;
    return s.state === 'notRunning' ? 'Installed, not running' : 'Not installed';
  });

  readonly chipTone = computed(() => {
    const s = this.state()?.state;
    return s === 'running' ? 'ok' : s === 'notRunning' ? 'warn' : 'neutral';
  });

  readonly statusLine = computed(() => {
    const s = this.state();
    if (this.stateFailed()) return 'Scout could not ask Docker for its state. Try Refresh.';
    if (s === null) return 'Scout is checking Docker on this computer.';
    if (s.state === 'running') return `Docker Engine ${s.version} answers on this computer.`;
    return s.state === 'notRunning'
      ? 'Docker is installed but not running. Start Docker Desktop or the Docker service.'
      : 'Docker is not installed on this computer.';
  });

  readonly switchHelp = computed(() => {
    const base = this.githubConnected()
      ? "Scout runs a project's tests only for a pull request you ask it to review."
      : "Connect GitHub first. Scout runs a project's tests only for a pull request you ask it to review.";
    const waits =
      this.state() !== null && !this.running() ? ' Runs wait for Docker to be running.' : '';
    return `${base} Runs also need GitHub's write permission in its card.${waits}`;
  });

  readonly neededProgress = signal<DockerPullProgress | null>(null);

  /** The pinned images the recorded repositories need and Docker does not hold, each once, in the repositories' order. */
  readonly neededMissing = computed<readonly string[]>(() => [
    ...new Set(
      this.repositoryRows()
        .filter((r) => r.present === false && r.image !== null)
        .map((r) => r.image as string),
    ),
  ]);

  readonly downloadHelp = computed(() => {
    if (this.repositories().length === 0) return NO_REPOSITORIES_DOWNLOAD_HELP;
    if (this.running() && this.images() !== null && this.neededMissing().length === 0) {
      return ALL_NEEDED_DOWNLOADED;
    }
    return REPOSITORIES_DOWNLOAD_HELP;
  });

  readonly imagesHelp = computed(() => {
    if (!this.running()) return 'Docker must be running to download images.';
    const images = this.images();
    if (images === null) return 'Scout is checking the images.';
    return `${images.length - this.missingCount()} of ${images.length} downloaded.`;
  });

  /** The pinned images by stack, in the order main lists them, with how many are downloaded. */
  readonly groups = computed<readonly ImageGroup[]>(() => {
    const groups: ImageGroup[] = [];
    for (const image of this.images() ?? []) {
      const group = groups.find((g) => g.stack === image.stack);
      if (group === undefined) {
        groups.push({ stack: image.stack, images: [image], present: image.present ? 1 : 0 });
      } else {
        groups[groups.indexOf(group)] = {
          ...group,
          images: [...group.images, image],
          present: group.present + (image.present ? 1 : 0),
        };
      }
    }
    return groups;
  });

  /** Each recorded repository with its image's group and presence from the images Docker answered. */
  readonly repositoryRows = computed<readonly RepositoryRow[]>(() =>
    this.repositories().map((r) => {
      const image =
        r.image === null ? undefined : this.images()?.find((i) => i.reference === r.image);
      return {
        repository: r.repository,
        stack: this.stackOf(r.image, r.stack ?? 'No stack'),
        runs: r.runs,
        declared:
          r.declared.length === 0
            ? 'Nothing declared; the default runs.'
            : `${r.declared.map((d) => `${d.source} asks for ${d.version}`).join('; ')}.`,
        refusal: r.refusal,
        image: r.image,
        group: image?.stack ?? null,
        present: image?.present ?? null,
      };
    }),
  );

  readonly folderSize = computed(() => {
    const f = this.folder();
    if (f === null) return 'Scout is checking the folder.';
    return f.bytes === 0
      ? 'Empty · run logs are deleted'
      : `${formatBytes(f.bytes)} used · each run's copy of the code is deleted after it`;
  });

  async ngOnInit(): Promise<void> {
    await Promise.all([this.loadEnabled(), this.load()]);
  }

  isOpen(id: DockerSection): boolean {
    return this.chosen()[id] ?? (id === 'images' && this.imagesNeedAttention());
  }

  toggle(id: DockerSection): void {
    const next = { ...this.chosen(), [id]: !this.isOpen(id) };
    this.chosen.set(next);
    writeSections(next);
  }

  summary(id: DockerSection): string {
    switch (id) {
      case 'uses':
        return "What Scout uses Docker for: a pull request's tests, offline, 20 minutes at most";
      case 'images': {
        const p = this.progress();
        if (p !== null) return `Images: downloading ${p.index} of ${p.total}`;
        if (this.state() !== null && !this.running()) return 'Images: Docker is not running';
        const images = this.images();
        if (images === null) return 'Images: checking';
        return `Images: ${images.length - this.missingCount()} of ${images.length} downloaded`;
      }
      case 'repositories': {
        const rows = this.repositoryRows();
        if (rows.length === 0) return 'Repositories: none read yet';
        const missing = rows.filter((r) => r.present === false).length;
        const refused = rows.filter((r) => r.refusal !== null).length;
        return (
          `Repositories: ${rows.length} read` +
          (missing > 0 ? `, ${missing} missing an image` : '') +
          (refused > 0 ? `, ${refused} Scout cannot run` : '')
        );
      }
      case 'folder': {
        const f = this.folder();
        if (f === null) return 'Work folder: checking';
        return f.bytes === 0 ? 'Work folder: empty' : `Work folder: ${formatBytes(f.bytes)}`;
      }
      case 'last': {
        const run = this.lastRun();
        if (run === null) return 'Last run: none yet';
        const pr = run.pullRequest !== null ? ` #${run.pullRequest}` : '';
        return `Last run: ${run.repository ?? 'a repository'}${pr}, ${this.resultLabel(run).toLowerCase()}`;
      }
    }
  }

  /** The pinned image's stack group (Bun, Java (Maven)) when Docker listed it, else the recorded stack. */
  stackOf(image: string | null, recorded: string): string {
    return this.images()?.find((i) => i.reference === image)?.stack ?? recorded;
  }

  resultLabel(run: DockerRun): string {
    const label = RUN_RESULT_LABELS[run.result];
    return run.result === 'failed' && run.exitCode !== null
      ? `${label} (exit ${run.exitCode})`
      : label;
  }

  duration(seconds: number): string {
    return formatDuration(seconds);
  }

  short(reference: string): string {
    return shortReference(reference);
  }

  /** Asks Docker again and says what it answered in one notice; none when Scout could not ask, which the status line says. */
  async refresh(): Promise<void> {
    await this.load();
    const answered = this.state();
    if (answered !== null) this.notice.show(REFRESHED[answered.state], 'info');
  }

  async setEnabled(change: MatSlideToggleChange): Promise<void> {
    if (!(await this.save(change.checked))) change.source.checked = this.enabled();
  }

  /** The card's Turn on: saves the choice as the switch does, then says so in one notice unless the save or the restart failed. */
  async enable(on: boolean): Promise<boolean> {
    const saved = await this.save(on);
    if (saved && on && this.switchError() === null) this.notice.show(TURNED_ON, 'success');
    return saved;
  }

  private async load(): Promise<void> {
    this.refreshing.set(true);
    try {
      await Promise.all([this.loadState(), this.loadFolder(), this.loadRecords()]);
      if (this.running()) await this.loadImages();
      else this.images.set(null);
      this.imagesNeedAttention.set(this.running() && this.missingCount() > 0);
    } finally {
      this.refreshing.set(false);
    }
  }

  /** Saves the owner's choice for Scout's Docker runs and restarts Scout to apply it; false when the vault refused it. */
  private async save(on: boolean): Promise<boolean> {
    this.switchBusy.set(true);
    this.switchError.set(null);
    this.switchNote.set(null);
    try {
      await this.bridge.vaultStore({ key: DOCKER_ENABLED_KEY, value: on ? 'true' : 'false' });
      this.enabled.set(on);
      this.emitStatus();
    } catch (err: unknown) {
      this.switchError.set(readIpcError(err).message);
      this.switchBusy.set(false);
      return false;
    }
    try {
      const result = await this.bridge.workerRestart({ reason: 'Docker switch' });
      if (result.outcome === 'deferred') this.switchNote.set(RESTART_DEFERRED_LINE);
      if (result.outcome === 'failed') {
        this.switchError.set(`Saved, but Scout could not restart to apply it: ${result.reason}`);
      }
    } catch (err: unknown) {
      this.switchError.set(
        `Saved, but Scout could not restart to apply it: ${readIpcError(err).message}`,
      );
    } finally {
      this.switchBusy.set(false);
    }
    return true;
  }

  async download(stack: string): Promise<void> {
    this.pulling.set(true);
    this.pullingStack.set(stack);
    this.pullError.set(null);
    let told = false;
    const stop = this.bridge.onDockerPullProgress((p) => {
      this.progress.set(p);
      if (told) return;
      told = true;
      this.notice.show(`Downloading the ${stack} images in Docker`, 'info');
    });
    try {
      this.pullError.set((await this.bridge.dockerPull({ stack })).error);
    } catch (err: unknown) {
      this.pullError.set(readIpcError(err).message);
    } finally {
      stop();
      this.progress.set(null);
      this.pullingStack.set(null);
      this.pulling.set(false);
    }
    await this.loadImages();
  }

  /** Pulls one repository's image only. */
  async downloadImage(reference: string): Promise<void> {
    this.pulling.set(true);
    this.pullError.set(null);
    const stop = this.bridge.onDockerPullProgress((p) => this.progress.set(p));
    try {
      this.pullError.set((await this.bridge.dockerPullImage({ reference })).error);
    } catch (err: unknown) {
      this.pullError.set(readIpcError(err).message);
    } finally {
      stop();
      this.progress.set(null);
      this.pulling.set(false);
    }
    await this.loadImages();
  }

  /** Pulls each image the repositories need and Docker lacks, one at a time; the first failure stops the rest. */
  async downloadNeeded(): Promise<void> {
    const needed = this.neededMissing();
    this.pulling.set(true);
    this.pullError.set(null);
    let index = 0;
    let told = false;
    const stop = this.bridge.onDockerPullProgress((p) => {
      this.neededProgress.set({ ...p, index, total: needed.length });
      if (told) return;
      told = true;
      this.notice.show(
        `Downloading ${needed.length} ${needed.length === 1 ? 'image' : 'images'} in Docker`,
        'info',
      );
    });
    try {
      for (const [i, reference] of needed.entries()) {
        index = i + 1;
        this.neededProgress.set({ reference, index, total: needed.length, state: 'pulling' });
        const { error } = await this.bridge.dockerPullImage({ reference });
        if (error !== null) {
          const rest = needed.length - index;
          this.pullError.set(
            error +
              (rest > 0
                ? ` The other ${rest} ${rest === 1 ? 'image was' : 'images were'} not downloaded.`
                : ''),
          );
          break;
        }
      }
    } catch (err: unknown) {
      this.pullError.set(readIpcError(err).message);
    } finally {
      stop();
      this.neededProgress.set(null);
      this.pulling.set(false);
    }
    await this.loadImages();
  }

  async clear(): Promise<void> {
    const f = this.folder();
    if (f === null) return;
    const confirmed = await confirmAction(this.dialog, {
      title: 'Clear the work folder?',
      body: `Scout deletes the logs of past test runs in ${f.path} (${formatBytes(f.bytes)}). The pinned images stay in Docker.`,
      confirmLabel: 'Clear',
    });
    if (!confirmed) return;
    this.clearing.set(true);
    this.clearRefused.set(null);
    try {
      const result = await this.bridge.dockerClear();
      this.folder.set(result.folder);
      this.clearRefused.set(result.refused);
      if (result.refused === null) {
        this.notice.show('Work folder cleared', 'success');
        await this.loadRecords();
      }
    } catch (err: unknown) {
      this.clearRefused.set(readIpcError(err).message);
    } finally {
      this.clearing.set(false);
    }
  }

  async openInstallGuide(): Promise<void> {
    await this.bridge.appOpenExternal({ url: DOCKER_INSTALL_URL }).catch(() => undefined);
  }

  private async loadState(): Promise<void> {
    try {
      this.state.set(await this.bridge.dockerState());
      this.stateFailed.set(false);
    } catch {
      this.state.set(null);
      this.stateFailed.set(true);
    }
    this.emitStatus();
  }

  private async loadEnabled(): Promise<void> {
    try {
      const { value } = await this.bridge.vaultGetPublic({ key: DOCKER_ENABLED_KEY });
      this.enabled.set(value === 'true');
    } catch {
      this.enabled.set(false);
    }
    this.emitStatus();
  }

  private async loadImages(): Promise<void> {
    try {
      this.images.set((await this.bridge.dockerImages()).images);
    } catch {
      this.images.set(null);
    }
  }

  private async loadRecords(): Promise<void> {
    try {
      this.repositories.set((await this.bridge.dockerRepositories()).repositories);
    } catch {
      this.repositories.set([]);
    }
    try {
      this.lastRun.set((await this.bridge.dockerLastRun()).run);
    } catch {
      this.lastRun.set(null);
    }
  }

  private async loadFolder(): Promise<void> {
    try {
      this.folder.set(await this.bridge.dockerWorkFolder());
    } catch {
      this.folder.set(null);
    }
  }

  private emitStatus(): void {
    if (this.state() === null && !this.stateFailed()) return;
    this.statusChange.emit({ running: this.running(), enabled: this.enabled() });
  }
}
