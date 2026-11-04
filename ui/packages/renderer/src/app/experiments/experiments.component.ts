import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';

import { readIpcError, type ExperimentDto, type OperatorProjectDto } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';
import { formatLondon } from '../operator/london';

const POLL_MS = 10_000;
const PROJECTS_READ = 50;
const MIN_HOURS = 24;
const MAX_HOURS = 168;
const TESTED_KINDS = ['title', 'thumbnail'];
const TESTABLE = ['published', 'scheduled'];
const MAX_VARIANTS = 2;

interface PlannedArm {
  readonly kind: string;
  readonly label: string;
  readonly value: string;
}

function scopeOf(kind: string): string {
  return kind === 'title' ? 'videos.update' : 'thumbnails.set';
}

@Component({
  selector: 'sc-experiments',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <header class="header">
        <h1>Experiments</h1>
        <p class="lede">Title and thumbnail tests on a published video, and their results.</p>
      </header>

      @if (error(); as message) {
        <p class="err" data-test="experiments-error">{{ message }}</p>
      }

      @if (projects().length === 0) {
        <p class="empty" data-test="experiments-empty">
          No video is on YouTube yet. Publish one from the Assembly page.
        </p>
      } @else {
        <label>
          Video
          <select data-test="experiments-project" (change)="select($any($event.target).value)">
            <option value="" [selected]="selectedId() === null">Choose a video</option>
            @for (p of projects(); track p.id) {
              <option [value]="p.id" [selected]="p.id === selectedId()">{{ p.topic }}</option>
            }
          </select>
        </label>
      }

      @if (selected(); as p) {
        <section class="block">
          <h2>Start</h2>
          <label>
            Hours per arm
            <input
              type="number"
              min="24"
              max="168"
              step="1"
              data-test="experiment-hours"
              [value]="hours()"
              (input)="hours.set($any($event.target).value)"
            />
          </label>
          @if (!hoursValid()) {
            <p class="err" data-test="experiment-hours-error">Each arm runs 24 to 168 hours.</p>
          }
          @if (!testable(p)) {
            <p class="meta" data-test="experiment-start-blocked">
              Only a published or scheduled video is tested; this one is
              {{ p.publication?.status }}.
            </p>
          } @else if (planned(p).length === 0 && !running(p)) {
            <p class="meta" data-test="experiment-start-blocked">
              Accept a title or thumbnail proposal on the Reports page first.
            </p>
          }
          <button
            type="button"
            class="btn btn--primary"
            data-test="experiment-start"
            [disabled]="
              busy() || !hoursValid() || !testable(p) || running(p) || planned(p).length === 0
            "
            (click)="starting.set(p)"
          >
            Start experiment
          </button>
        </section>

        @if (p.publication?.experiment; as e) {
          <section class="block">
            <h2>
              {{ e.kind }} experiment
              <span class="badge" data-test="experiment-status">{{ e.status }}</span>
            </h2>
            <table data-test="experiment-arms">
              <thead>
                <tr>
                  <th scope="col">Arm</th>
                  <th scope="col">Value</th>
                  <th scope="col">Started (Europe/London)</th>
                  <th scope="col">Ended (Europe/London)</th>
                  <th scope="col">Impressions</th>
                  <th scope="col">Clicks</th>
                  <th scope="col">Average view percentage</th>
                </tr>
              </thead>
              <tbody>
                @for (a of e.arms; track a.id) {
                  <tr>
                    <td>{{ a.label }}</td>
                    <td>{{ a.value }}</td>
                    <td>{{ a.startedAt === null ? '-' : london(a.startedAt) }}</td>
                    <td>{{ a.endedAt === null ? '-' : london(a.endedAt) }}</td>
                    <td>{{ a.impressions }}</td>
                    <td>{{ a.clicks }}</td>
                    <td>{{ a.averageViewPercentage }}</td>
                  </tr>
                }
              </tbody>
            </table>
            @if (e.status === 'concluded' || e.status === 'inconclusive') {
              <p data-test="experiment-winner">
                {{ winner(e) === null ? 'Inconclusive' : 'Winner: ' + winner(e) }}
              </p>
            }
            @if (e.result; as result) {
              <p class="meta" data-test="experiment-result-text">{{ result }}</p>
            }
            @if (e.status === 'concluded' && winner(e) !== null) {
              <button
                type="button"
                class="btn btn--primary"
                data-test="experiment-adopt"
                [disabled]="busy()"
                (click)="adopting.set(p)"
              >
                Adopt winner
              </button>
            }
          </section>
        }
      }

      @if (starting(); as p) {
        <div class="backdrop" role="dialog" aria-modal="true" data-test="experiment-start-dialog">
          <div class="dialog">
            <h2>Start the experiment?</h2>
            <p>Starting it approves these actions; each runs when its arm goes live.</p>
            <ul data-test="experiment-start-actions">
              @for (a of planned(p); track $index) {
                <li>
                  {{ scope(a.kind) }}: {{ a.label }}, "{{ a.value }}", for {{ hours() }} hours
                </li>
              }
              @if (planned(p)[0]; as control) {
                <li>
                  {{ scope(control.kind) }}: {{ control.label }}, "{{ control.value }}", again when
                  the test ends
                </li>
              }
            </ul>
            @if (actionError(); as message) {
              <p class="err" data-test="experiment-error">{{ message }}</p>
            }
            <div class="row">
              <button type="button" class="btn btn--secondary" (click)="starting.set(null)">
                Cancel
              </button>
              <button
                type="button"
                class="btn btn--primary"
                data-test="experiment-start-confirm"
                [disabled]="busy()"
                (click)="start(p)"
              >
                Start experiment
              </button>
            </div>
          </div>
        </div>
      }

      @if (adopting(); as p) {
        <div class="backdrop" role="dialog" aria-modal="true" data-test="experiment-adopt-dialog">
          <div class="dialog">
            <h2>Adopt the winner?</h2>
            <p>Adopting it approves this action:</p>
            @if (p.publication?.experiment; as e) {
              <ul data-test="experiment-adopt-actions">
                <li>{{ scope(e.kind) }}: set the {{ e.kind }} to "{{ winnerValue(e) }}"</li>
              </ul>
            }
            @if (actionError(); as message) {
              <p class="err" data-test="experiment-error">{{ message }}</p>
            }
            <div class="row">
              <button type="button" class="btn btn--secondary" (click)="adopting.set(null)">
                Cancel
              </button>
              <button
                type="button"
                class="btn btn--primary"
                data-test="experiment-adopt-confirm"
                [disabled]="busy()"
                (click)="adopt(p)"
              >
                Adopt winner
              </button>
            </div>
          </div>
        </div>
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
      .badge {
        font-size: 12px;
        color: #7b8299;
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
export class ExperimentsComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private timer: ReturnType<typeof setInterval> | null = null;

  readonly error = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly busy = signal(false);
  readonly hours = signal<string | number>(48);
  readonly starting = signal<OperatorProjectDto | null>(null);
  readonly adopting = signal<OperatorProjectDto | null>(null);
  readonly projects = signal<readonly OperatorProjectDto[]>([]);
  readonly selectedId = signal<string | null>(null);
  readonly selected = computed(
    () => this.projects().find((p) => p.id === this.selectedId()) ?? null,
  );
  readonly hoursValid = computed(() => {
    const h = Number(this.hours());
    return Number.isInteger(h) && h >= MIN_HOURS && h <= MAX_HOURS;
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
    this.actionError.set(null);
  }

  scope(kind: string): string {
    return scopeOf(kind);
  }

  london(iso: string): string {
    return formatLondon(iso);
  }

  testable(project: OperatorProjectDto): boolean {
    return TESTABLE.includes(project.publication?.status ?? '');
  }

  running(project: OperatorProjectDto): boolean {
    return project.publication?.experiment?.status === 'running';
  }

  planned(project: OperatorProjectDto): PlannedArm[] {
    const accepted = (project.publication?.proposals ?? []).filter(
      (p) => p.status === 'accepted' && TESTED_KINDS.includes(p.kind),
    );
    const kind = accepted[0]?.kind;
    if (kind === undefined) return [];
    const current =
      kind === 'title' ? (project.package?.title ?? '') : (project.package?.thumbnailFile ?? '');
    return [
      { kind, label: 'Control', value: current },
      ...accepted
        .filter((p) => p.kind === kind)
        .slice(0, MAX_VARIANTS)
        .map((p, i) => ({
          kind,
          label: `Variant ${String.fromCharCode(65 + i)}`,
          value: kind === 'title' ? p.text : (p.thumbnailFile ?? p.text),
        })),
    ];
  }

  winner(experiment: ExperimentDto): string | null {
    return experiment.arms.find((a) => a.id === experiment.winnerArmId)?.label ?? null;
  }

  winnerValue(experiment: ExperimentDto): string {
    return experiment.arms.find((a) => a.id === experiment.winnerArmId)?.value ?? '';
  }

  async start(project: OperatorProjectDto): Promise<void> {
    if (
      await this.act(() =>
        this.bridge.operatorStartExperiment({
          projectId: project.id,
          armHours: Number(this.hours()),
        }),
      )
    ) {
      this.starting.set(null);
    }
  }

  async adopt(project: OperatorProjectDto): Promise<void> {
    if (await this.act(() => this.bridge.operatorAdoptExperimentWinner({ id: project.id }))) {
      this.adopting.set(null);
    }
  }

  private async act(action: () => Promise<OperatorProjectDto>): Promise<boolean> {
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const updated = await action();
      this.projects.update((all) => all.map((p) => (p.id === updated.id ? updated : p)));
      return true;
    } catch (err: unknown) {
      this.actionError.set(readIpcError(err).message);
      return false;
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
