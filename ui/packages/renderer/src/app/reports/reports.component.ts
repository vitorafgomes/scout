import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';

import {
  readIpcError,
  type OperatorProjectDto,
  type RetentionPointDto,
  type SnapshotDto,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';
import { formatLondon } from '../operator/london';

const POLL_MS = 10_000;
const PROJECTS_READ = 50;
const WINDOWS = ['24h', '7d'];
const CHART_WIDTH = 400;
const CHART_HEIGHT = 160;

interface Row {
  readonly label: string;
  readonly value: string | number;
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}

@Component({
  selector: 'sc-reports',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <header class="header">
        <h1>Reports</h1>
        <p class="lede">
          What each published video did, as YouTube reported it, and what Scout proposes.
        </p>
      </header>

      @if (error(); as message) {
        <p class="err" data-test="reports-error">{{ message }}</p>
      }

      @if (projects().length === 0) {
        <p class="empty" data-test="reports-empty">
          No video is on YouTube yet. Publish one from the Assembly page.
        </p>
      } @else {
        <label>
          Video
          <select data-test="reports-project" (change)="select($any($event.target).value)">
            <option value="" [selected]="selectedId() === null">Choose a video</option>
            @for (p of projects(); track p.id) {
              <option [value]="p.id" [selected]="p.id === selectedId()">{{ p.topic }}</option>
            }
          </select>
        </label>
      }

      @if (selected(); as p) {
        @for (w of windows; track w) {
          <section class="block">
            <h2>{{ w }}</h2>
            @if (snapshotOf(p, w); as s) {
              <table [attr.data-test]="'snapshot-' + w">
                <tbody>
                  @for (row of rowsOf(s); track row.label) {
                    <tr>
                      <th scope="row">{{ row.label }}</th>
                      <td>{{ row.value }}</td>
                    </tr>
                  }
                </tbody>
              </table>
              @if (s.retention.length > 0) {
                <h3>Audience retention</h3>
                <svg
                  class="chart"
                  role="img"
                  [attr.aria-label]="'Audience retention over ' + w"
                  [attr.viewBox]="'0 0 ' + width + ' ' + height"
                  [attr.width]="width"
                  [attr.height]="height"
                >
                  @for (x of sceneMarks(p); track $index; let i = $index) {
                    <line
                      class="mark"
                      [attr.data-test]="'scene-mark-' + w + '-' + i"
                      [attr.x1]="x"
                      [attr.x2]="x"
                      y1="0"
                      [attr.y2]="height"
                    />
                  }
                  <polyline
                    class="line"
                    fill="none"
                    [attr.data-test]="'retention-line-' + w"
                    [attr.points]="points(s.retention)"
                  />
                </svg>
              }
            } @else {
              <p class="empty" [attr.data-test]="'snapshot-empty-' + w">No {{ w }} numbers yet.</p>
            }
          </section>
        }

        <section class="block">
          <h2>Proposals</h2>
          @if (decideError(); as message) {
            <p class="err" data-test="proposal-error">{{ message }}</p>
          }
          @if ((p.publication?.proposals ?? []).length === 0) {
            <p class="empty" data-test="proposals-empty">No proposals yet.</p>
          } @else {
            <ul class="proposals">
              @for (pr of p.publication?.proposals ?? []; track pr.id) {
                <li class="card" [attr.data-test]="'proposal-' + pr.id">
                  <p>
                    <strong>{{ pr.kind }}: {{ pr.text }}</strong>
                    <span class="badge" [attr.data-test]="'proposal-status-' + pr.id">{{
                      pr.status
                    }}</span>
                  </p>
                  <p class="meta">{{ pr.reason }}</p>
                  @if (pr.thumbnailFile; as file) {
                    <img
                      class="thumbnail"
                      [attr.data-test]="'proposal-thumbnail-' + pr.id"
                      [alt]="'Proposed thumbnail ' + pr.id"
                      [src]="mediaUrl(p, file)"
                    />
                  }
                  @if (pr.status === 'proposed') {
                    <div class="row">
                      <button
                        type="button"
                        class="btn btn--primary"
                        [attr.data-test]="'proposal-accept-' + pr.id"
                        [disabled]="busy()"
                        (click)="decide(p, pr.id, true)"
                      >
                        Accept
                      </button>
                      <button
                        type="button"
                        class="btn btn--secondary"
                        [attr.data-test]="'proposal-reject-' + pr.id"
                        [disabled]="busy()"
                        (click)="decide(p, pr.id, false)"
                      >
                        Reject
                      </button>
                    </div>
                  }
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
        padding: 32px;
        max-width: 1080px;
        margin: 0 auto;
        color: #e4e8f0;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .lede,
      .meta,
      .empty {
        color: #7b8299;
        font-size: 13px;
      }
      .err {
        color: #f87171;
      }
      .block {
        margin-top: 24px;
      }
      th {
        text-align: left;
        font-weight: 400;
        color: #7b8299;
        padding-right: 16px;
      }
      .chart {
        background: #11131a;
        border: 1px solid #252b3b;
      }
      .line {
        stroke: #60a5fa;
        stroke-width: 2;
      }
      .mark {
        stroke: #7b8299;
        stroke-dasharray: 4 4;
      }
      .proposals {
        list-style: none;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .card {
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 12px;
      }
      .badge {
        margin-left: 8px;
        font-size: 12px;
        color: #7b8299;
      }
      .thumbnail {
        max-width: 320px;
      }
      .row {
        display: flex;
        gap: 8px;
      }
      .btn {
        padding: 6px 12px;
        border-radius: 6px;
        border: 1px solid transparent;
        cursor: pointer;
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
export class ReportsComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private timer: ReturnType<typeof setInterval> | null = null;

  readonly windows = WINDOWS;
  readonly width = CHART_WIDTH;
  readonly height = CHART_HEIGHT;
  readonly error = signal<string | null>(null);
  readonly decideError = signal<string | null>(null);
  readonly busy = signal(false);
  readonly projects = signal<readonly OperatorProjectDto[]>([]);
  readonly selectedId = signal<string | null>(null);
  readonly selected = computed(
    () => this.projects().find((p) => p.id === this.selectedId()) ?? null,
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

  snapshotOf(project: OperatorProjectDto, window: string): SnapshotDto | null {
    return project.publication?.snapshots.find((s) => s.window === window) ?? null;
  }

  rowsOf(s: SnapshotDto): Row[] {
    const rows: Row[] = [
      { label: 'Taken', value: `${formatLondon(s.takenAt)} Europe/London` },
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
      rows.push({ label: 'Impressions click-through rate', value: s.impressionsCtr });
    }
    return rows;
  }

  points(retention: readonly RetentionPointDto[]): string {
    const top = Math.max(1, ...retention.map((p) => p.audienceWatchRatio));
    return retention
      .map(
        (p) =>
          `${round(p.elapsedRatio * CHART_WIDTH)},${round(CHART_HEIGHT - (p.audienceWatchRatio / top) * CHART_HEIGHT)}`,
      )
      .join(' ');
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
      if (elapsed > 0 && elapsed < total) marks.push(round((elapsed / total) * CHART_WIDTH));
    }
    return marks;
  }

  mediaUrl(project: OperatorProjectDto, fileName: string): string {
    return `scout-media://${project.id}/${encodeURIComponent(fileName)}?v=${encodeURIComponent(project.updatedAt)}`;
  }

  async decide(project: OperatorProjectDto, proposalId: string, accept: boolean): Promise<void> {
    this.busy.set(true);
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
    }
  }

  private async refresh(): Promise<void> {
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
    }
  }
}
