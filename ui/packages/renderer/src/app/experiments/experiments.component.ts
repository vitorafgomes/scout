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
import { MatDialog } from '@angular/material/dialog';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';

import {
  readIpcError,
  type ExperimentArmDto,
  type ExperimentDto,
  type OperatorProjectDto,
} from '@scout/shared';

import { StatusChipComponent, type ChipTone } from '../core/status-chip.component';
import { IconComponent } from '../core/icon.component';
import { ScoutBridgeService } from '../core/scout-bridge';
import { formatLondon } from '../operator/london';

import {
  ExperimentDialogComponent,
  type ApprovedAction,
  type ExperimentDialogData,
} from './experiment-dialog.component';

const POLL_MS = 10_000;
const PROJECTS_READ = 50;
const MIN_HOURS = 24;
const MAX_HOURS = 168;
const TESTED_KINDS = ['title', 'thumbnail'];
const TESTABLE = ['published', 'scheduled'];
const MAX_VARIANTS = 2;
const CONTROL = 'control';
const NOT_YET = 'Not yet';

let opened = 0;

const STATUS: Readonly<Record<string, readonly [string, ChipTone]>> = {
  running: ['Running', 'info'],
  concluded: ['Concluded', 'ok'],
  inconclusive: ['Inconclusive', 'warn'],
  adopted: ['Adopted', 'ok'],
};

interface PlannedArm {
  readonly kind: string;
  readonly label: string;
  readonly value: string;
}

function scopeOf(kind: string): string {
  return kind === 'title' ? 'videos.update' : 'thumbnails.set';
}

function capitalised(word: string): string {
  return word.charAt(0).toUpperCase() + word.slice(1);
}

@Component({
  selector: 'sc-experiments',
  standalone: true,
  imports: [IconComponent, MatButtonModule, MatProgressBarModule, RouterLink, StatusChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <header class="header">
        <div class="title-block">
          <h1>Experiments</h1>
          <p class="lede">Title and thumbnail tests on a published video, and their results.</p>
        </div>
        <a matButton="outlined" class="big" routerLink="../reports" data-test="experiments-reports">
          <svg
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
            <path d="M4 20V10M10 20V4M16 20v-7M22 20H2" /></svg
          >Proposals in Reports
        </a>
      </header>

      @if (error(); as message) {
        <div class="alert" role="alert">
          <sc-icon name="error" [size]="20" />
          <span data-test="experiments-error">{{ message }}</span>
        </div>
      }

      @if (!loaded()) {
        <div
          class="card loading"
          role="status"
          aria-label="Loading published videos"
          data-test="experiments-loading"
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
            <span data-test="experiments-empty"
              >No video is on YouTube yet. Publish one from the Assembly page.</span
            >
            <a matButton="outlined" routerLink="../assembly" data-test="experiments-assembly">
              <sc-icon name="forward" [size]="16" />Go to Assembly
            </a>
          </div>
        }
      } @else {
        <div class="picker">
          <label for="experiments-video">Video</label>
          <select
            id="experiments-video"
            aria-describedby="experiments-video-help"
            data-test="experiments-project"
            (change)="select($any($event.target).value)"
          >
            @for (p of projects(); track p.id) {
              <option [value]="p.id" [selected]="p.id === selected()?.id">
                {{ videoLabel(p) }}
              </option>
            }
          </select>
          <span id="experiments-video-help" class="help"
            >The newest published video is chosen for you.</span
          >
        </div>
      }

      @if (selected(); as p) {
        <section class="card" aria-labelledby="experiments-start-heading">
          <h2 id="experiments-start-heading">Start a test</h2>
          <div class="start-row">
            <div class="field">
              <label for="experiments-hours">Hours per variant</label>
              <input
                id="experiments-hours"
                type="number"
                min="24"
                max="168"
                step="1"
                aria-describedby="experiments-hours-help"
                data-test="experiment-hours"
                [value]="hours()"
                (input)="hours.set($any($event.target).value)"
              />
              <span id="experiments-hours-help" class="help">24 to 168 hours</span>
            </div>
            <button
              type="button"
              matButton="filled"
              class="big"
              data-test="experiment-start"
              [attr.aria-describedby]="blockedReason(p) === null ? null : 'experiments-why'"
              [disabled]="
                busy() || !hoursValid() || !testable(p) || running(p) || planned(p).length === 0
              "
              (click)="openStart(p)"
            >
              <sc-icon name="play" [size]="16" />Start experiment
            </button>
          </div>
          @if (!hoursValid()) {
            <span class="err" data-test="experiment-hours-error"
              >Each variant runs 24 to 168 hours.</span
            >
          }
          @if (blockedReason(p); as reason) {
            <span id="experiments-why" class="why" data-test="experiment-start-blocked">{{
              reason
            }}</span>
          } @else {
            <span class="help-line" data-test="experiment-start-plan">{{ planLine(p) }}</span>
          }
        </section>

        @if (p.publication?.experiment; as e) {
          <section class="card" aria-labelledby="experiments-test-heading">
            <div class="test-head">
              <h2 id="experiments-test-heading">{{ kindName(e.kind) }} test</h2>
              <sc-status-chip
                data-test="experiment-status"
                [tone]="statusTone(e.status)"
                [label]="statusLabel(e.status)"
              />
            </div>
            <span class="help">Times in London</span>
            <div class="table-frame">
              <table data-test="experiment-arms">
                <thead>
                  <tr>
                    <th scope="col">Variant</th>
                    <th scope="col">{{ kindName(e.kind) }} shown</th>
                    <th scope="col">Started</th>
                    <th scope="col">Ended</th>
                    <th scope="col" class="num">Impressions</th>
                    <th scope="col" class="num">Clicks</th>
                    <th scope="col" class="num">Click-through</th>
                    <th scope="col" class="num">Average view percentage</th>
                  </tr>
                </thead>
                <tbody>
                  @for (a of e.arms; track a.id) {
                    <tr [class.winner]="a.id === e.winnerArmId">
                      <th scope="row">
                        {{ armLabel(e, a) }}
                        @if (a.id === e.winnerArmId) {
                          <sc-status-chip data-test="experiment-winner" tone="ok" label="Winner" />
                        }
                      </th>
                      <td class="value">{{ a.value }}</td>
                      <td class="when">
                        {{ a.startedAt === null ? notYet : london(a.startedAt) }}
                      </td>
                      <td class="when">{{ a.endedAt === null ? notYet : london(a.endedAt) }}</td>
                      <td class="num">{{ measured(a, a.impressions) }}</td>
                      <td class="num">{{ measured(a, a.clicks) }}</td>
                      <td class="num ctr">{{ clickThrough(a) }}</td>
                      <td class="num">{{ measured(a, a.averageViewPercentage) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <div class="note">
              <sc-icon name="info" [size]="16" />
              <div class="note-text">
                <span class="note-title">How the winner is chosen</span>
                <span data-test="experiment-rule">{{ ruleOf(e.kind) }}</span>
              </div>
            </div>
            @if (e.result; as result) {
              <div class="result" role="status" [attr.data-tone]="resultTone(e)">
                <svg
                  width="16"
                  height="16"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2.4"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  aria-hidden="true"
                >
                  <path [attr.d]="resultTone(e) === 'ok' ? okGlyph : warnGlyph" />
                </svg>
                <span data-test="experiment-result-text">{{ result }}</span>
              </div>
            }
            @if (e.status === 'concluded' && winner(e) !== null) {
              <button
                type="button"
                matButton="filled"
                class="big adopt"
                data-test="experiment-adopt"
                [disabled]="busy()"
                (click)="openAdopt(p)"
              >
                <sc-icon name="check" [size]="16" />Adopt the winner
              </button>
            }
          </section>
        }
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
        --mat-button-filled-horizontal-padding: 16px;
        --mat-button-filled-label-text-weight: 400;
        --mat-button-filled-label-text-size: 14px;
        --mat-button-filled-label-text-tracking: 0;
        --mat-button-filled-disabled-container-color: var(--sc-surface-2);
        --mat-button-filled-disabled-label-text-color: var(--sc-muted);
      }
      .big {
        --mat-button-outlined-container-height: 40px;
        --mat-button-outlined-horizontal-padding: 16px;
        --mat-button-filled-container-height: 40px;
      }
      a sc-icon,
      a svg,
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
      .picker,
      .field {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .picker {
        max-width: 420px;
      }
      .field {
        flex: 0 1 220px;
      }
      label {
        font-size: 13px;
        font-weight: 600;
      }
      select,
      input {
        height: 40px;
        box-sizing: border-box;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        color: var(--sc-fg);
        font: inherit;
      }
      select {
        padding: 0 10px;
        background: var(--sc-surface);
      }
      input {
        padding: 0 12px;
        background: var(--sc-bg);
      }
      .help {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .help-line {
        font-size: 13px;
        color: var(--sc-muted);
      }
      .why {
        font-size: 13px;
        color: var(--sc-on-warn-container);
      }
      .err {
        font-size: 13px;
        color: var(--sc-error);
      }
      .start-row {
        display: flex;
        flex-wrap: wrap;
        gap: 12px;
        align-items: flex-end;
      }
      .test-head {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        align-items: center;
      }
      .test-head h2 {
        margin-right: auto;
      }
      .table-frame {
        overflow-x: auto;
        border: 1px solid var(--sc-line);
        border-radius: 10px;
      }
      table {
        width: 100%;
        min-width: 760px;
        border-collapse: collapse;
        font-size: 13px;
      }
      thead tr {
        background: var(--sc-surface-2);
        text-align: left;
      }
      th,
      td {
        padding: 8px 12px;
      }
      tbody tr {
        border-top: 1px solid var(--sc-line);
      }
      tbody tr.winner {
        background: var(--sc-ok-container);
      }
      tbody th {
        text-align: left;
        white-space: nowrap;
        font-weight: 600;
      }
      tbody th sc-status-chip {
        margin-left: 6px;
      }
      .value {
        min-width: 220px;
        overflow-wrap: anywhere;
      }
      .when {
        white-space: nowrap;
      }
      .num {
        text-align: right;
      }
      .ctr {
        font-weight: 600;
      }
      .note {
        display: flex;
        gap: 10px;
        padding: 12px;
        border-radius: 10px;
        background: var(--sc-bg);
        border: 1px solid var(--sc-line);
      }
      .note sc-icon {
        color: var(--sc-accent);
        margin-top: 2px;
      }
      .note-text {
        display: flex;
        flex-direction: column;
        gap: 4px;
        font-size: 13px;
        line-height: 1.45;
        color: var(--sc-fg-2);
      }
      .note-title {
        font-weight: 600;
        color: var(--sc-fg);
      }
      .result {
        display: flex;
        gap: 10px;
        padding: 12px;
        border-radius: 10px;
        font-size: 13px;
        line-height: 1.45;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .result[data-tone='ok'] {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .result svg {
        flex: 0 0 16px;
        margin-top: 2px;
      }
      .adopt {
        align-self: flex-start;
      }
    `,
  ],
})
export class ExperimentsComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly dialog = inject(MatDialog);
  private timer: ReturnType<typeof setInterval> | null = null;

  readonly notYet = NOT_YET;
  readonly okGlyph = 'M20 6L9 17l-5-5';
  readonly warnGlyph = 'M12 8v5M12 16.5h.01';
  readonly error = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly busy = signal(false);
  readonly loaded = signal(false);
  readonly hours = signal<string | number>(48);
  readonly projects = signal<readonly OperatorProjectDto[]>([]);
  readonly selectedId = signal<string | null>(null);
  readonly selected = computed(
    () => this.projects().find((p) => p.id === this.selectedId()) ?? this.projects()[0] ?? null,
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

  kindName(kind: string): string {
    return capitalised(kind);
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

  blockedReason(project: OperatorProjectDto): string | null {
    if (!this.testable(project)) {
      return `Only a published or scheduled video is tested; this one is ${project.publication?.status}.`;
    }
    if (this.running(project)) {
      return 'A test is already running on this video. Start another when it ends.';
    }
    if (this.planned(project).length > 0) return null;
    return project.publication?.experiment == null
      ? 'Accept a title or thumbnail proposal on the Reports page first.'
      : 'The last test has ended. Accept another title or thumbnail proposal in Reports to test it.';
  }

  planLine(project: OperatorProjectDto): string {
    const [, ...variants] = this.planned(project);
    const kind = variants[0]?.kind ?? '';
    const values = variants.map((v) => `"${v.value}"`).join(' and ');
    return `Tests the accepted ${kind}${variants.length > 1 ? 's' : ''} ${values} against the current one.`;
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

  startActions(project: OperatorProjectDto): ApprovedAction[] {
    const arms = this.planned(project);
    const [control] = arms;
    if (control === undefined) return [];
    const hours = Number(this.hours());
    const scope = scopeOf(control.kind);
    return [
      ...arms.map((a) => ({
        text:
          a === control
            ? `Set the ${a.kind} to "${a.value}", the current one, for ${hours} hours`
            : `Change the ${a.kind} to "${a.value}" for ${hours} hours`,
        scope,
      })),
      { text: `Put back "${control.value}" when the test ends`, scope },
    ];
  }

  statusLabel(status: string): string {
    return STATUS[status]?.[0] ?? status;
  }

  statusTone(status: string): ChipTone {
    return STATUS[status]?.[1] ?? 'neutral';
  }

  armLabel(experiment: ExperimentDto, arm: ExperimentArmDto): string {
    return arm.id === CONTROL ? `Current ${experiment.kind}` : arm.label;
  }

  measured(arm: ExperimentArmDto, value: number): string | number {
    return arm.endedAt === null ? NOT_YET : value;
  }

  clickThrough(arm: ExperimentArmDto): string {
    if (arm.endedAt === null) return NOT_YET;
    const ctr = arm.impressions === 0 ? 0 : arm.clicks / arm.impressions;
    return `${(ctr * 100).toFixed(1)}%`;
  }

  ruleOf(kind: string): string {
    return (
      `Each variant, the current ${kind} included, needs at least 1,000 impressions and 10 clicks. ` +
      'The best click-through rate must beat the next one by a clear margin (a z-score of at least 1.96), ' +
      `and viewers must not watch the winner more than 5 points less than the current ${kind}. ` +
      `Otherwise the test is inconclusive. When the test ends the current ${kind} is put back; ` +
      'the winner goes on YouTube only when you adopt it.'
    );
  }

  resultTone(experiment: ExperimentDto): 'ok' | 'warn' {
    return this.winner(experiment) === null ? 'warn' : 'ok';
  }

  winner(experiment: ExperimentDto): string | null {
    return experiment.arms.find((a) => a.id === experiment.winnerArmId)?.label ?? null;
  }

  winnerValue(experiment: ExperimentDto): string {
    return experiment.arms.find((a) => a.id === experiment.winnerArmId)?.value ?? '';
  }

  openStart(project: OperatorProjectDto): void {
    const kind = this.planned(project)[0]?.kind ?? '';
    this.openDialog(500, {
      test: 'experiment-start',
      title: `Start the ${kind} test?`,
      body: 'Starting it approves these changes on YouTube; each runs when its variant goes live.',
      actions: this.startActions(project),
      confirmLabel: 'Start experiment',
      confirmIcon: 'play',
      busy: this.busy,
      error: this.actionError,
      confirm: () => this.start(project),
    });
  }

  openAdopt(project: OperatorProjectDto): void {
    const e = project.publication?.experiment;
    if (e == null) return;
    const value = this.winnerValue(e);
    this.openDialog(480, {
      test: 'experiment-adopt',
      title: 'Adopt the winner?',
      body: `Scout changes the ${e.kind} on YouTube for good to "${value}".`,
      actions: [{ text: `Set the ${e.kind} to "${value}"`, scope: scopeOf(e.kind) }],
      confirmLabel: 'Adopt the winner',
      confirmIcon: 'check',
      busy: this.busy,
      error: this.actionError,
      confirm: () => this.adopt(project),
    });
  }

  start(project: OperatorProjectDto): Promise<boolean> {
    return this.act(() =>
      this.bridge.operatorStartExperiment({
        projectId: project.id,
        armHours: Number(this.hours()),
      }),
    );
  }

  adopt(project: OperatorProjectDto): Promise<boolean> {
    return this.act(() => this.bridge.operatorAdoptExperimentWinner({ id: project.id }));
  }

  private openDialog(width: number, data: ExperimentDialogData): void {
    this.actionError.set(null);
    opened += 1;
    const id = `sc-experiment-dialog-${opened}`;
    this.dialog.open<ExperimentDialogComponent, ExperimentDialogData, boolean>(
      ExperimentDialogComponent,
      {
        id,
        data,
        ariaLabelledBy: `${id}-title`,
        ariaDescribedBy: `${id}-body`,
        restoreFocus: true,
        panelClass: 'sc-dialog',
        backdropClass: 'sc-dialog-backdrop',
        width: `${width}px`,
        maxWidth: 'calc(100vw - 32px)',
        position: { top: '200px' },
      },
    );
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
    } finally {
      this.loaded.set(true);
    }
  }
}
