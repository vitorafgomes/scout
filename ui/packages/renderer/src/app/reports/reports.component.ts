import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';

import {
  readIpcError,
  type OperatorProjectDto,
  type RetentionPointDto,
  type SnapshotDto,
} from '@scout/shared';

import { IconComponent } from '../core/icon.component';
import { ScoutBridgeService } from '../core/scout-bridge';
import { StatusChipComponent, type ChipTone } from '../core/status-chip.component';
import { formatLondon } from '../operator/london';

const POLL_MS = 10_000;
const PROJECTS_READ = 50;
const WINDOWS = ['24h', '7d'];
const WINDOW_TITLES: Readonly<Record<string, string>> = {
  '24h': 'First 24 hours',
  '7d': 'First 7 days',
};
const PLOT_LEFT = 40;
const PLOT_RIGHT = 410;
const PLOT_TOP = 10;
const PLOT_BOTTOM = 140;
const PLOT_WIDTH = PLOT_RIGHT - PLOT_LEFT;
const PLOT_HEIGHT = PLOT_BOTTOM - PLOT_TOP;

const STATUS: Readonly<Record<string, readonly [string, ChipTone]>> = {
  proposed: ['Waiting for you', 'warn'],
  accepted: ['Accepted', 'ok'],
  rejected: ['Rejected', 'neutral'],
  tested: ['Tested', 'info'],
};

interface Row {
  readonly label: string;
  readonly value: string | number;
}

interface Axis {
  readonly top: string;
  readonly middle: string;
  readonly times: readonly string[];
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}

function clock(ms: number): string {
  const seconds = Math.round(ms / 1000);
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

@Component({
  selector: 'sc-reports',
  standalone: true,
  imports: [IconComponent, MatButtonModule, MatProgressBarModule, RouterLink, StatusChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <header class="header">
        <div class="title-block">
          <h1>Reports</h1>
          <p class="lede">
            What each published video did, as YouTube reported it, and what Scout proposes.
          </p>
        </div>
        <button
          type="button"
          matButton="outlined"
          class="big"
          data-test="reports-refresh"
          (click)="refresh()"
        >
          <sc-icon name="refresh" [size]="16" />Refresh
        </button>
      </header>

      @if (error(); as message) {
        <div class="alert" role="alert">
          <sc-icon name="error" [size]="20" />
          <span data-test="reports-error">{{ message }}</span>
        </div>
      }

      @if (!loaded()) {
        <div
          class="card loading"
          role="status"
          aria-label="Loading published videos"
          data-test="reports-loading"
        >
          <mat-progress-bar mode="indeterminate" aria-label="Loading published videos" />
          <i class="skeleton skeleton--field" aria-hidden="true"></i>
          <i class="skeleton skeleton--block" aria-hidden="true"></i>
        </div>
      } @else if (projects().length === 0) {
        @if (error() === null) {
          <div class="card empty">
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
                <path d="M12 15V3M7 8l5-5 5 5M4 15v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4" />
              </svg>
            </span>
            <span data-test="reports-empty"
              >No video is on YouTube yet. Publish one from the Assembly page.</span
            >
            <a matButton="outlined" routerLink="../assembly" data-test="reports-assembly">
              <sc-icon name="forward" [size]="16" />Go to Assembly
            </a>
          </div>
        }
      } @else {
        <div class="picker">
          <label for="reports-video">Video</label>
          <select
            id="reports-video"
            aria-describedby="reports-video-help"
            data-test="reports-project"
            (change)="select($any($event.target).value)"
          >
            @for (p of projects(); track p.id) {
              <option [value]="p.id" [selected]="p.id === selected()?.id">
                {{ videoLabel(p) }}
              </option>
            }
          </select>
          <span id="reports-video-help" class="help"
            >The newest published video is chosen for you.</span
          >
        </div>
      }

      @if (selected(); as p) {
        <div class="windows">
          @for (w of windows; track w) {
            <section class="card window" [attr.aria-labelledby]="'reports-window-' + w">
              @if (snapshotOf(p, w); as s) {
                <div class="window-head">
                  <h2 [id]="'reports-window-' + w">{{ titleOf(w) }}</h2>
                  <span class="help" [attr.data-test]="'snapshot-taken-' + w"
                    >Taken {{ london(s.takenAt) }} London time</span
                  >
                </div>
                <div class="tiles" [attr.data-test]="'snapshot-' + w">
                  @for (row of rowsOf(s); track row.label) {
                    <div class="tile">
                      <span class="tile-label">{{ row.label }}</span>
                      <span class="tile-value">{{ row.value }}</span>
                    </div>
                  }
                </div>
                @if (s.retention.length > 0) {
                  @let ax = axis(p, s.retention);
                  <h3>Audience retention</h3>
                  <svg
                    class="chart"
                    role="img"
                    viewBox="0 0 420 170"
                    [attr.aria-label]="chartLabel(w, s.retention)"
                  >
                    @for (y of gridY; track y) {
                      <line class="grid" x1="40" x2="410" [attr.y1]="y" [attr.y2]="y" />
                    }
                    <text class="axis" x="32" y="14" text-anchor="end">{{ ax.top }}</text>
                    <text class="axis" x="32" y="79" text-anchor="end">{{ ax.middle }}</text>
                    <text class="axis" x="32" y="144" text-anchor="end">0%</text>
                    @for (x of sceneMarks(p); track $index; let i = $index) {
                      <line
                        class="mark"
                        [attr.data-test]="'scene-mark-' + w + '-' + i"
                        [attr.x1]="x"
                        [attr.x2]="x"
                        y1="10"
                        y2="140"
                      />
                    }
                    <polyline
                      class="line"
                      fill="none"
                      [attr.data-test]="'retention-line-' + w"
                      [attr.points]="points(s.retention)"
                    />
                    @if (ax.times.length === 3) {
                      <text class="axis" x="40" y="160">{{ ax.times[0] }}</text>
                      <text class="axis" x="225" y="160" text-anchor="middle">
                        {{ ax.times[1] }}
                      </text>
                      <text class="axis" x="410" y="160" text-anchor="end">{{ ax.times[2] }}</text>
                    }
                  </svg>
                  <div class="legend">
                    <span
                      ><i class="key key--line" aria-hidden="true"></i>Viewers still watching</span
                    >
                    <span><i class="key key--mark" aria-hidden="true"></i>Scene starts</span>
                  </div>
                }
              } @else {
                <div class="window-head">
                  <h2 [id]="'reports-window-' + w">{{ titleOf(w) }}</h2>
                </div>
                <p class="muted" [attr.data-test]="'snapshot-empty-' + w">
                  No {{ w }} numbers yet.
                </p>
              }
            </section>
          }
        </div>

        <section class="card" aria-labelledby="reports-proposals-heading">
          <h2 id="reports-proposals-heading">What Scout proposes</h2>
          @if (decideError(); as message) {
            <p class="err" role="alert" data-test="proposal-error">{{ message }}</p>
          }
          @if ((p.publication?.proposals ?? []).length === 0) {
            <p class="muted" data-test="proposals-empty">No proposals yet.</p>
          } @else {
            <ul class="proposals">
              @for (pr of p.publication?.proposals ?? []; track pr.id) {
                <li class="proposal" [attr.data-test]="'proposal-' + pr.id">
                  @if (pr.thumbnailFile; as file) {
                    <img
                      class="thumbnail"
                      [attr.data-test]="'proposal-thumbnail-' + pr.id"
                      [alt]="'Proposed thumbnail: ' + pr.text"
                      [src]="mediaUrl(p, file)"
                    />
                  }
                  <div class="proposal-body">
                    <div class="proposal-head">
                      <span class="kind">New {{ pr.kind }}</span>
                      <strong>{{ pr.text }}</strong>
                      <sc-status-chip
                        [attr.data-test]="'proposal-status-' + pr.id"
                        [tone]="statusTone(pr.status)"
                        [label]="statusLabel(pr.status)"
                      />
                    </div>
                    <span class="reason">{{ pr.reason }}</span>
                    @if (pr.status === 'proposed') {
                      <div class="row">
                        <button
                          type="button"
                          matButton="filled"
                          [attr.data-test]="'proposal-accept-' + pr.id"
                          [disabled]="busy()"
                          (click)="decide(p, pr.id, true)"
                        >
                          <sc-icon name="check" [size]="16" />Accept
                        </button>
                        <button
                          type="button"
                          matButton="outlined"
                          class="reject"
                          [attr.data-test]="'proposal-reject-' + pr.id"
                          [disabled]="busy()"
                          (click)="decide(p, pr.id, false)"
                        >
                          <sc-icon name="close" [size]="16" />Reject
                        </button>
                        @if (deciding() === pr.id) {
                          <span
                            class="saving"
                            role="status"
                            [attr.data-test]="'proposal-saving-' + pr.id"
                            >Saving your decision...</span
                          >
                        }
                      </div>
                    }
                  </div>
                </li>
              }
            </ul>
          }
        </section>
      }
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .page {
        padding: 24px;
        max-width: 1180px;
        box-sizing: border-box;
        display: flex;
        flex-direction: column;
        gap: 16px;
        color: var(--sc-fg);
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
        font-size: 14px;
        --mat-button-outlined-container-height: 36px;
        --mat-button-outlined-horizontal-padding: 14px;
        --mat-button-outlined-label-text-weight: 400;
        --mat-button-outlined-label-text-size: 14px;
        --mat-button-outlined-label-text-tracking: 0;
        --mat-button-outlined-disabled-label-text-color: var(--sc-muted);
        --mat-button-outlined-disabled-outline-color: var(--sc-line);
        --mat-button-filled-container-height: 36px;
        --mat-button-filled-horizontal-padding: 14px;
        --mat-button-filled-label-text-weight: 400;
        --mat-button-filled-label-text-size: 14px;
        --mat-button-filled-label-text-tracking: 0;
        --mat-button-filled-disabled-container-color: var(--sc-surface-2);
        --mat-button-filled-disabled-label-text-color: var(--sc-muted);
      }
      .big {
        --mat-button-outlined-container-height: 40px;
        --mat-button-outlined-horizontal-padding: 16px;
      }
      a sc-icon,
      button sc-icon {
        margin-right: 8px;
        vertical-align: middle;
      }
      .header {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-start;
        gap: 12px;
      }
      .title-block {
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
      .lede {
        margin: 0;
        color: var(--sc-fg-2);
        line-height: 1.5;
      }
      h2 {
        margin: 0;
        font-size: 16px;
        font-weight: 600;
      }
      h3 {
        margin: 4px 0 0;
        font-size: 14px;
        font-weight: 600;
      }
      .alert {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 12px 16px;
        border-radius: var(--sc-radius);
        border: 1px solid var(--sc-error);
        background: var(--sc-error-container);
        color: var(--sc-error);
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
      .skeleton {
        display: block;
        border-radius: 10px;
        background: var(--sc-surface-2);
        animation: pulse 1.4s ease-in-out infinite;
      }
      .skeleton--field {
        height: 40px;
        width: 50%;
      }
      .skeleton--block {
        height: 140px;
      }
      @keyframes pulse {
        0%,
        100% {
          opacity: 0.55;
        }
        50% {
          opacity: 1;
        }
      }
      .empty {
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
      .picker {
        display: flex;
        flex-direction: column;
        gap: 6px;
        max-width: 420px;
      }
      label {
        font-size: 13px;
        font-weight: 600;
      }
      select {
        height: 40px;
        padding: 0 10px;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-surface);
        color: var(--sc-fg);
        font: inherit;
      }
      .help {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .muted {
        margin: 0;
        color: var(--sc-muted);
      }
      .err {
        margin: 0;
        color: var(--sc-error);
      }
      .windows {
        display: flex;
        flex-wrap: wrap;
        gap: 16px;
      }
      .window {
        flex: 1 1 420px;
        min-width: 0;
      }
      .window-head {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        align-items: baseline;
      }
      .window-head h2 {
        margin-right: auto;
      }
      .tiles {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
        gap: 10px;
      }
      .tile {
        display: flex;
        flex-direction: column;
        gap: 2px;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-bg);
        border: 1px solid var(--sc-line);
      }
      .tile-label {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .tile-value {
        font-size: 18px;
        font-weight: 600;
      }
      .chart {
        display: block;
        width: 100%;
      }
      .grid {
        stroke: var(--sc-line);
        stroke-width: 1;
      }
      .axis {
        font-size: 11px;
        fill: var(--sc-muted);
      }
      .line {
        stroke: var(--sc-accent);
        stroke-width: 2.2;
        stroke-linejoin: round;
        stroke-linecap: round;
      }
      .mark {
        stroke: var(--sc-muted);
        stroke-width: 1;
        stroke-dasharray: 3 3;
      }
      .legend {
        display: flex;
        flex-wrap: wrap;
        gap: 14px;
        font-size: 12px;
        color: var(--sc-fg-2);
      }
      .legend span {
        display: flex;
        align-items: center;
        gap: 6px;
      }
      .key {
        display: inline-block;
        width: 16px;
      }
      .key--line {
        height: 2px;
        background: var(--sc-accent);
      }
      .key--mark {
        border-top: 1px dashed var(--sc-muted);
      }
      .proposals {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .proposal {
        display: flex;
        flex-wrap: wrap;
        gap: 12px;
        padding: 12px;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
      }
      .thumbnail {
        flex: 0 0 160px;
        width: 160px;
        aspect-ratio: 16 / 9;
        object-fit: cover;
        border-radius: 8px;
        background: var(--sc-media);
      }
      .proposal-body {
        flex: 1 1 280px;
        display: flex;
        flex-direction: column;
        gap: 6px;
        min-width: 0;
      }
      .proposal-head {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        align-items: center;
        overflow-wrap: anywhere;
      }
      .kind {
        padding: 2px 9px;
        border-radius: 12px;
        font-size: 12px;
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
      }
      .reason {
        font-size: 13px;
        color: var(--sc-fg-2);
        line-height: 1.45;
      }
      .row {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      .reject:disabled {
        border-style: dashed;
      }
      .saving {
        align-self: center;
        font-size: 12px;
        color: var(--sc-muted);
      }
    `,
  ],
})
export class ReportsComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private timer: ReturnType<typeof setInterval> | null = null;

  readonly windows = WINDOWS;
  readonly gridY = [PLOT_TOP, PLOT_TOP + PLOT_HEIGHT / 2, PLOT_BOTTOM];
  readonly error = signal<string | null>(null);
  readonly decideError = signal<string | null>(null);
  readonly busy = signal(false);
  readonly deciding = signal<string | null>(null);
  readonly loaded = signal(false);
  readonly projects = signal<readonly OperatorProjectDto[]>([]);
  readonly selectedId = signal<string | null>(null);
  readonly selected = computed(
    () => this.projects().find((p) => p.id === this.selectedId()) ?? this.projects()[0] ?? null,
  );

  async ngOnInit(): Promise<void> {
    this.timer = setInterval(() => void this.refresh(), POLL_MS);
    await this.refresh();
  }

  ngOnDestroy(): void {
    if (this.timer !== null) clearInterval(this.timer);
  }

  select(id: string): void {
    this.selectedId.set(id === '' ? null : id);
    this.decideError.set(null);
  }

  videoLabel(project: OperatorProjectDto): string {
    const publication = project.publication;
    const scheduled = publication?.status === 'scheduled';
    const at = scheduled ? publication?.publishAt : publication?.uploadedAt;
    if (at == null) return project.topic;
    const verb = scheduled
      ? 'scheduled'
      : publication?.status === 'published'
        ? 'published'
        : 'uploaded';
    return `${project.topic} · ${verb} ${formatLondon(at)}`;
  }

  titleOf(window: string): string {
    return WINDOW_TITLES[window] ?? window;
  }

  london(iso: string): string {
    return formatLondon(iso);
  }

  snapshotOf(project: OperatorProjectDto, window: string): SnapshotDto | null {
    return project.publication?.snapshots.find((s) => s.window === window) ?? null;
  }

  rowsOf(s: SnapshotDto): Row[] {
    const rows: Row[] = [
      { label: 'Views', value: s.views },
      { label: 'Minutes watched', value: s.minutesWatched },
      { label: 'Average view duration (seconds)', value: s.averageViewDurationSeconds },
      { label: 'Average view percentage', value: s.averageViewPercentage },
      { label: 'Subscribers gained', value: s.subscribersGained },
      { label: 'Subscribers lost', value: s.subscribersLost },
      { label: 'Likes', value: s.likes },
      { label: 'Comments', value: s.comments },
      { label: 'Shares', value: s.shares },
    ];
    if (s.impressions !== null) rows.push({ label: 'Impressions', value: s.impressions });
    if (s.impressionsCtr !== null) {
      rows.push({ label: 'Click-through rate', value: s.impressionsCtr });
    }
    return rows;
  }

  points(retention: readonly RetentionPointDto[]): string {
    const top = this.topOf(retention);
    return retention
      .map(
        (p) =>
          `${round(PLOT_LEFT + p.elapsedRatio * PLOT_WIDTH)},${round(PLOT_BOTTOM - (p.audienceWatchRatio / top) * PLOT_HEIGHT)}`,
      )
      .join(' ');
  }

  axis(project: OperatorProjectDto, retention: readonly RetentionPointDto[]): Axis {
    const top = this.topOf(retention);
    const total = this.totalMs(project);
    return {
      top: `${Math.round(top * 100)}%`,
      middle: `${Math.round(top * 50)}%`,
      times: total > 0 ? [clock(0), clock(total / 2), clock(total)] : [],
    };
  }

  chartLabel(window: string, retention: readonly RetentionPointDto[]): string {
    const first = Math.round((retention[0]?.audienceWatchRatio ?? 0) * 100);
    const last = Math.round((retention[retention.length - 1]?.audienceWatchRatio ?? 0) * 100);
    return `Audience retention over the ${this.titleOf(window).toLowerCase()}: starts at ${first}%, ends at ${last}%`;
  }

  sceneMarks(project: OperatorProjectDto): number[] {
    const durations = [...project.scenes]
      .sort((a, b) => a.order - b.order)
      .map((s) => s.durationMs);
    const total = durations.reduce((sum, d) => sum + d, 0);
    if (total <= 0) return [];
    const marks: number[] = [];
    let elapsed = 0;
    for (const d of durations.slice(0, -1)) {
      elapsed += d;
      if (elapsed > 0 && elapsed < total) {
        marks.push(round(PLOT_LEFT + (elapsed / total) * PLOT_WIDTH));
      }
    }
    return marks;
  }

  statusLabel(status: string): string {
    return STATUS[status]?.[0] ?? status;
  }

  statusTone(status: string): ChipTone {
    return STATUS[status]?.[1] ?? 'neutral';
  }

  mediaUrl(project: OperatorProjectDto, fileName: string): string {
    return `scout-media://${project.id}/${encodeURIComponent(fileName)}?v=${encodeURIComponent(project.updatedAt)}`;
  }

  async decide(project: OperatorProjectDto, proposalId: string, accept: boolean): Promise<void> {
    this.busy.set(true);
    this.deciding.set(proposalId);
    this.decideError.set(null);
    try {
      const updated = await this.bridge.operatorDecideProposal({
        projectId: project.id,
        proposalId,
        accept,
      });
      this.projects.update((all) => all.map((p) => (p.id === updated.id ? updated : p)));
    } catch (err: unknown) {
      this.decideError.set(readIpcError(err).message);
    } finally {
      this.busy.set(false);
      this.deciding.set(null);
    }
  }

  async refresh(): Promise<void> {
    try {
      const { projects } = await this.bridge.operatorProjects({ limit: PROJECTS_READ });
      this.projects.set(
        projects
          .filter((p) => p.publication?.videoId != null)
          .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)),
      );
      this.error.set(null);
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    } finally {
      this.loaded.set(true);
    }
  }

  private topOf(retention: readonly RetentionPointDto[]): number {
    return Math.max(1, ...retention.map((p) => p.audienceWatchRatio));
  }

  private totalMs(project: OperatorProjectDto): number {
    return project.scenes.reduce((sum, s) => sum + s.durationMs, 0);
  }
}
