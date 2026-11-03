import { DatePipe, PercentPipe } from '@angular/common';
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
  type ClaimDto,
  type ClaimStatus,
  type MandateDto,
  type OperatorProjectDto,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

const POLL_MS = 10_000;
const PROJECTS_SHOWN = 50;
const PILLAR_SLOTS = [0, 1, 2, 3, 4] as const;
const MIN_TOPIC_LENGTH = 3;

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
              data-test="mandate-voice"
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
            [disabled]="starting() || topic().trim().length < minTopicLength"
            (click)="startProject()"
          >
            Make a video
          </button>
        </div>
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
          @if (p.error; as failure) {
            <p class="err" data-test="project-failure">{{ failure }}</p>
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
      select {
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

  async ngOnInit(): Promise<void> {
    this.timer = setInterval(() => void this.refresh(), POLL_MS);
    await Promise.all([this.loadMandate(), this.refresh()]);
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
      const project = await this.bridge.operatorStartProject({ topic: this.topic().trim() });
      this.queued.set(project);
      this.topic.set('');
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
      const updated = await this.bridge.operatorDecideClaim(req);
      this.detail.set(updated);
      this.projects.update((all) => all.map((p) => (p.id === updated.id ? updated : p)));
      this.waiving.set(null);
    } catch (err: unknown) {
      this.claimError.set(readIpcError(err).message);
    } finally {
      this.deciding.set(null);
    }
  }

  copy(url: string): void {
    void navigator.clipboard?.writeText(url).catch(() => undefined);
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
