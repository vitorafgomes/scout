import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import {
  readIpcError,
  type UpdateNotice,
  type UpdateProgress,
  type UpdateState,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

type NoteBlock =
  | { kind: 'heading'; text: string }
  | { kind: 'list'; items: string[] }
  | { kind: 'paragraph'; lines: string[] };

/**
 * Splits release notes into "#" headings, paragraphs and "- " or "* " lists, dropping the "**" around a
 * leading label; the result is only ever interpolated as text.
 */
function parseNotes(notes: string): NoteBlock[] {
  const blocks: NoteBlock[] = [];
  for (const chunk of notes.replace(/\r\n/g, '\n').split(/\n[ \t]*\n/)) {
    let current: NoteBlock | null = null;
    for (const line of chunk.split('\n')) {
      if (line.trim() === '') continue;
      const heading = /^\s*#+ +(.*)$/.exec(line);
      if (heading !== null) {
        blocks.push({ kind: 'heading', text: (heading[1] ?? '').trim() });
        current = null;
        continue;
      }
      const item = /^\s*[-*] +(.*)$/.exec(line);
      if (item !== null) {
        if (current?.kind !== 'list') {
          current = { kind: 'list', items: [] };
          blocks.push(current);
        }
        current.items.push(item[1] ?? '');
      } else {
        if (current?.kind !== 'paragraph') {
          current = { kind: 'paragraph', lines: [] };
          blocks.push(current);
        }
        current.lines.push(line.trim().replace(/^\*\*(.+?)\*\*/, '$1'));
      }
    }
  }
  return blocks;
}

@Component({
  selector: 'sc-update-notice',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (notice(); as n) {
      <section class="update-notice" role="status" data-test="update-notice">
        <div class="row">
          <p class="headline" data-test="update-headline">
            Scout {{ n.latestVersion }} is available. You have {{ n.currentVersion }}.
          </p>
          <a
            class="link"
            data-test="update-release-notes"
            [href]="n.releaseUrl"
            (click)="$event.preventDefault(); open(n.releaseUrl)"
            >Release notes</a
          >
          <div class="actions">
            @if (n.canInstall) {
              <button
                type="button"
                class="primary"
                data-test="update-install"
                [disabled]="busy()"
                (click)="install()"
              >
                Update and restart
              </button>
              @if (progress()?.phase === 'failed') {
                <button type="button" data-test="update-download" (click)="open(n.downloadUrl)">
                  Download
                </button>
              }
            } @else {
              <button
                type="button"
                class="primary"
                data-test="update-download"
                (click)="open(n.downloadUrl)"
              >
                Download
              </button>
            }
            <button type="button" data-test="update-later" [disabled]="busy()" (click)="later()">
              Later
            </button>
            <button
              type="button"
              data-test="update-skip"
              [disabled]="busy()"
              (click)="skip(n.latestVersion)"
            >
              Skip this version
            </button>
          </div>
        </div>
        @if (progress(); as p) {
          <p
            class="progress"
            data-test="update-progress"
            [class.error]="p.phase === 'failed'"
            [attr.role]="p.phase === 'failed' ? 'alert' : null"
          >
            @switch (p.phase) {
              @case ('checking') {
                Checking the release
              }
              @case ('downloading') {
                Downloading{{ p.percent === null ? '' : ' ' + p.percent + '%' }}
                @if (p.percent !== null) {
                  <progress max="100" [value]="p.percent"></progress>
                }
              }
              @case ('failed') {
                {{ p.message }}
              }
              @default {
                Restarting to install
              }
            }
          </p>
        }
        @if (notes().length > 0) {
          <details class="notes" data-test="update-notes">
            <summary>What's new</summary>
            @for (block of notes(); track $index) {
              @if (block.kind === 'heading') {
                <h4>{{ block.text }}</h4>
              } @else if (block.kind === 'list') {
                <ul>
                  @for (item of block.items; track $index) {
                    <li>{{ item }}</li>
                  }
                </ul>
              } @else {
                <p>{{ block.lines.join('\\n') }}</p>
              }
            }
          </details>
        }
        @if (error(); as message) {
          <p class="error" role="alert" data-test="update-error">{{ message }}</p>
        }
      </section>
    }
  `,
  styles: [
    `
      .update-notice {
        background: #1e2e45;
        border-bottom: 1px solid #2563eb;
        color: #e4e8f0;
        padding: 8px 16px;
        font-size: 13px;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 12px;
      }
      .headline {
        margin: 0;
        font-weight: 600;
      }
      .link {
        color: #8ab4ff;
      }
      .actions {
        display: flex;
        gap: 8px;
        margin-left: auto;
      }
      button {
        min-height: 32px;
        padding: 0 12px;
        border-radius: 6px;
        border: 1px solid #252b3b;
        background: #171b26;
        color: #e4e8f0;
        cursor: pointer;
      }
      button.primary {
        background: #2563eb;
        border-color: #2563eb;
        color: #ffffff;
      }
      .notes {
        margin-top: 8px;
      }
      .notes summary {
        cursor: pointer;
      }
      .notes h4 {
        margin: 12px 0 0;
        font-size: 13px;
        font-weight: 600;
      }
      .notes p {
        margin: 8px 0 0;
        white-space: pre-line;
      }
      .notes ul {
        margin: 8px 0 0;
        padding-left: 20px;
      }
      .progress {
        display: flex;
        align-items: center;
        gap: 8px;
        margin: 8px 0 0;
      }
      .error {
        color: #d45a5a;
        margin: 8px 0 0;
      }
    `,
  ],
})
export class UpdateNoticeComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly destroyRef = inject(DestroyRef);

  readonly notice = signal<UpdateNotice | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly progress = signal<UpdateProgress | null>(null);
  readonly notes = computed(() => parseNotes(this.notice()?.notes ?? ''));

  ngOnInit(): void {
    this.destroyRef.onDestroy(this.bridge.onUpdateNotice((notice) => this.notice.set(notice)));
    this.destroyRef.onDestroy(this.bridge.onUpdateProgress((next) => this.progress.set(next)));
    this.bridge
      .updateGetState()
      .then((state) => this.notice.set(state.notice))
      .catch(() => this.notice.set(null));
  }

  open(url: string): void {
    this.error.set(null);
    this.bridge
      .appOpenExternal({ url })
      .catch((err: unknown) => this.error.set(readIpcError(err).message));
  }

  install(): Promise<void> {
    this.progress.set(null);
    return this.act(() => this.bridge.updateInstall());
  }

  later(): Promise<void> {
    return this.act(() => this.bridge.updateRemindLater());
  }

  skip(version: string): Promise<void> {
    return this.act(() => this.bridge.updateSkipVersion({ version }));
  }

  private async act(call: () => Promise<UpdateState>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.notice.set((await call()).notice);
    } catch (err: unknown) {
      this.error.set(readIpcError(err).message);
    } finally {
      this.busy.set(false);
    }
  }
}
