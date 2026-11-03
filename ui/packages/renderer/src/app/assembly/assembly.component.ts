import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
  type WritableSignal,
} from '@angular/core';

import { readIpcError, type OperatorProjectDto, type VideoPackageDto } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

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

/** The assembled video of a project: assembly, the video, Shorts and thumbnail, the checks, the metadata, the media rights and the approval. */
@Component({
  selector: 'sc-assembly',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  template: `
    <main class="page">
      <header class="header">
        <h1>Assembly</h1>
        <p class="lede">
          The finished video, its Shorts and thumbnail, what to check, and the approval.
        </p>
      </header>

      @if (error(); as message) {
        <p class="err" data-test="assembly-error">{{ message }}</p>
      }

      @if (projects().length === 0) {
        <p class="empty" data-test="assembly-empty">
          No video is ready for assembly yet. Produce its media on the Production page.
        </p>
      } @else {
        <label>
          Video
          <select data-test="assembly-project" (change)="select($any($event.target).value)">
            <option value="" [selected]="selectedId() === null">Choose a video</option>
            @for (p of projects(); track p.id) {
              <option [value]="p.id" [selected]="p.id === selectedId()">
                {{ p.topic }} ({{ p.status }})
              </option>
            }
          </select>
        </label>
      }

      @if (selected(); as p) {
        <section class="block">
          <div class="row">
            <button
              type="button"
              class="btn btn--primary"
              data-test="assemble"
              [disabled]="busy() || !assemblable(p)"
              (click)="assemble(p)"
            >
              Assemble video
            </button>
            @if (p.status === 'assembly_queued') {
              <span class="meta" data-test="assembly-progress"
                >Queued for assembly; the Scout worker starts it next.</span
              >
            } @else if (p.status === 'assembling') {
              <span class="meta" data-test="assembly-progress"
                >Assembling the video, its Shorts and its thumbnail.</span
              >
            }
          </div>
          @if (assembleError(); as message) {
            <p class="err" data-test="assemble-error">{{ message }}</p>
          }

          @if (p.package; as pkg) {
            @if (pkg.error; as message) {
              <p class="err" data-test="package-error">{{ message }}</p>
            }

            @if (pkg.videoFile; as file) {
              <h2>Video</h2>
              <video
                class="player"
                controls
                data-test="package-video"
                [src]="mediaUrl(p, file)"
              ></video>
            }

            <h2>Shorts</h2>
            @if (pkg.shorts.length === 0) {
              <p class="empty" data-test="shorts-empty">No Shorts yet.</p>
            } @else {
              <ul class="shorts">
                @for (s of pkg.shorts; track s.file; let i = $index) {
                  <li class="card">
                    <video
                      class="short"
                      controls
                      [attr.data-test]="'short-video-' + i"
                      [src]="mediaUrl(p, s.file)"
                    ></video>
                    <p [attr.data-test]="'short-hook-' + i">{{ s.hook }}</p>
                  </li>
                }
              </ul>
            }

            @if (pkg.thumbnailFile; as file) {
              <h2>Thumbnail</h2>
              <img
                class="thumbnail"
                data-test="package-thumbnail"
                alt="Thumbnail of the video"
                [src]="mediaUrl(p, file)"
              />
            }

            <h2>Checks</h2>
            @if (pkg.checks.length === 0) {
              <p class="empty" data-test="checks-empty">No checks yet.</p>
            } @else {
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
                        <span
                          [class]="'badge badge--' + c.result"
                          [attr.data-test]="'check-result-' + i"
                          >{{ c.result }}</span
                        >
                      </td>
                      <td [attr.data-test]="'check-detail-' + i">{{ c.detail }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            }

            @if (editable(p)) {
              <h2>Metadata</h2>
              <div class="form">
                <label>
                  <span class="label-row"
                    >Title
                    <span class="meta" data-test="title-count"
                      >{{ title().trim().length }} / {{ maxTitle }}</span
                    ></span
                  >
                  <input
                    data-test="metadata-title"
                    [value]="title()"
                    (input)="title.set($any($event.target).value)"
                  />
                </label>
                <label>
                  <span class="label-row"
                    >Description
                    <span class="meta" data-test="description-count"
                      >{{ description().trim().length }} / {{ maxDescription }}</span
                    ></span
                  >
                  <textarea
                    rows="6"
                    data-test="metadata-description"
                    [value]="description()"
                    (input)="description.set($any($event.target).value)"
                  ></textarea>
                </label>
                <div class="tags">
                  <span class="label-row"
                    >Tags
                    <span class="meta" data-test="tags-count"
                      >{{ tagsLength() }} / {{ maxTags }}</span
                    ></span
                  >
                  <ul class="chips">
                    @for (tag of tags(); track $index; let i = $index) {
                      <li class="chip">
                        <span [attr.data-test]="'tag-' + i">{{ tag }}</span>
                        <button
                          type="button"
                          class="chip-remove"
                          [attr.data-test]="'tag-remove-' + i"
                          [attr.aria-label]="'Remove tag ' + tag"
                          (click)="removeTag(i)"
                        >
                          ×
                        </button>
                      </li>
                    }
                  </ul>
                  <div class="row">
                    <input
                      class="grow"
                      data-test="tag-input"
                      aria-label="New tag"
                      [value]="newTag()"
                      (input)="newTag.set($any($event.target).value)"
                      (keydown.enter)="addTag()"
                    />
                    <button
                      type="button"
                      class="btn btn--secondary"
                      data-test="tag-add"
                      (click)="addTag()"
                    >
                      Add tag
                    </button>
                  </div>
                </div>
              </div>
              <button
                type="button"
                class="btn btn--primary"
                data-test="metadata-save"
                [disabled]="busy() || !metadataValid()"
                (click)="saveMetadata(p)"
              >
                Save metadata
              </button>
              @if (metadataError(); as message) {
                <p class="err" data-test="metadata-error">{{ message }}</p>
              }
            }

            <h2>Approval</h2>
            <label class="check">
              <input
                type="checkbox"
                data-test="media-rights"
                [checked]="pkg.rightsConfirmed"
                [disabled]="busy() || pkg.rightsConfirmed || p.status !== 'assembled'"
                (change)="confirmRights(p, $event)"
              />
              <span data-test="media-rights-label"
                >I own or have the rights to every image, recording and music in this video</span
              >
            </label>
            @if (rightsError(); as message) {
              <p class="err" data-test="rights-error">{{ message }}</p>
            }

            @if (pkg.approvedAt; as at) {
              <p class="ok" data-test="package-approved">
                Approved {{ at | date: 'yyyy-MM-dd HH:mm' }}
              </p>
            } @else {
              @if (blocks(p, pkg); as reasons) {
                @if (reasons.length > 0) {
                  <ul class="blocked" data-test="approve-blocked">
                    @for (reason of reasons; track reason) {
                      <li>{{ reason }}</li>
                    }
                  </ul>
                }
                <button
                  type="button"
                  class="btn btn--primary"
                  data-test="package-approve"
                  [disabled]="busy() || reasons.length > 0"
                  (click)="approve(p)"
                >
                  Approve package
                </button>
              }
              @if (approveError(); as message) {
                <p class="err" data-test="approve-error">{{ message }}</p>
              }
            }
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
        margin: 24px 0 8px;
        font-size: 16px;
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
      .ok {
        color: #4ade80;
      }
      .block {
        margin-top: 16px;
      }
      label {
        display: flex;
        flex-direction: column;
        gap: 4px;
        color: #7b8299;
      }
      .label-row {
        display: flex;
        justify-content: space-between;
      }
      .check {
        flex-direction: row;
        align-items: center;
        gap: 8px;
        color: #e4e8f0;
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
        font-family: inherit;
      }
      .form {
        display: flex;
        flex-direction: column;
        gap: 12px;
        margin: 12px 0;
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
      .player {
        display: block;
        width: 100%;
        max-width: 720px;
        border-radius: 8px;
        background: #000;
      }
      .shorts,
      .chips,
      .blocked {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      .blocked {
        flex-direction: column;
        color: #fbbf24;
        margin-bottom: 8px;
      }
      .card {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 8px;
        width: 200px;
      }
      .card p {
        margin: 8px 0 0;
      }
      .short {
        display: block;
        width: 100%;
        aspect-ratio: 9 / 16;
        background: #000;
        border-radius: 6px;
      }
      .thumbnail {
        display: block;
        max-width: 320px;
        border-radius: 6px;
      }
      .checks {
        border-collapse: collapse;
        width: 100%;
      }
      .checks th,
      .checks td {
        text-align: left;
        padding: 6px 8px;
        border-bottom: 1px solid #252b3b;
      }
      .chip {
        display: flex;
        align-items: center;
        gap: 4px;
        border: 1px solid #252b3b;
        border-radius: 999px;
        padding: 2px 4px 2px 10px;
        color: #e4e8f0;
      }
      .chip-remove {
        background: transparent;
        border: none;
        color: #7b8299;
        cursor: pointer;
        font-size: 14px;
      }
      .badge {
        border: 1px solid #252b3b;
        border-radius: 999px;
        padding: 0 8px;
        font-size: 12px;
      }
      .badge--pass {
        border-color: #166534;
        color: #4ade80;
      }
      .badge--warn {
        border-color: #854d0e;
        color: #fbbf24;
      }
      .badge--fail {
        border-color: #991b1b;
        color: #f87171;
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
export class AssemblyComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private timer: ReturnType<typeof setInterval> | null = null;
  private filledFrom: string | null = null;

  readonly maxTitle = MAX_TITLE;
  readonly maxDescription = MAX_DESCRIPTION;
  readonly maxTags = MAX_TAGS;

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

  mediaUrl(project: OperatorProjectDto, fileName: string): string {
    return `scout-media://${project.id}/${encodeURIComponent(fileName)}?v=${encodeURIComponent(project.updatedAt)}`;
  }

  addTag(): void {
    const tag = this.newTag().trim();
    if (tag.length > 0) this.tags.update((tags) => [...tags, tag]);
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
    await this.act(this.rightsError, () =>
      this.bridge.operatorConfirmMediaRights({ id: project.id }),
    );
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
          .filter((p) => ASSEMBLY_STATUSES.includes(p.status))
          .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)),
      );
      this.fill();
      this.error.set(null);
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    }
  }
}
