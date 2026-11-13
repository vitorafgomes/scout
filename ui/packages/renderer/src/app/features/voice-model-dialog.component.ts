import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

import type { WhisperVariant } from '@scout/shared';

import { type LocalModelCatalogEntry } from '../core/local-models.catalog';
import { type LocalModelsStore } from '../core/local-models.store';

const MiB = 1024 * 1024;
const GiB = 1024 * MiB;

/** The download size or memory of a model, in MB under a gigabyte and in GB from there. */
export function formatBytes(n: number): string {
  if (n >= GiB) return `${(n / GiB).toFixed(1).replace(/\.0$/, '')} GB`;
  return `${Math.round(n / MiB)} MB`;
}

/** The Whisper variant a transcription catalog entry downloads: its id without the `whisper-` prefix. */
export function variantOf(entry: LocalModelCatalogEntry): WhisperVariant {
  return entry.id.replace(/^whisper-/, '') as WhisperVariant;
}

/** What the dialog needs: the variants already on this computer, main's Whisper rows, and why they could not be read. */
export interface VoiceModelDialogData {
  readonly installed: readonly WhisperVariant[];
  readonly models: readonly LocalModelCatalogEntry[];
  readonly error: string | null;
}

/** Asks whether to download a voice model, listing exactly the Whisper models main downloads, in its order; Not now has the focus. */
@Component({
  selector: 'sc-voice-model-dialog',
  standalone: true,
  imports: [MatButtonModule, MatDialogModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Download a voice model?</h2>
    <mat-dialog-content>
      <p class="lede">
        Voice messages and live transcription run on this computer with a Whisper model; nothing is
        sent to the cloud.
      </p>
      @if (error; as reason) {
        <p class="err" role="alert" data-test="voice-models-error">
          Scout could not list the voice models: {{ reason }}
        </p>
      }
      <div class="models" role="radiogroup" aria-label="Voice models">
        @for (m of models; track m.id) {
          <label class="model" [attr.data-test]="'voice-model-' + variantOf(m)">
            <input
              type="radio"
              name="voice-model"
              [value]="variantOf(m)"
              [checked]="picked() === variantOf(m)"
              [disabled]="isInstalled(m)"
              [attr.data-test]="'voice-pick-' + variantOf(m)"
              (change)="picked.set(variantOf(m))"
            />
            <span class="body">
              <span class="name">
                {{ m.title }}
                @if (m.badges.includes('recommended')) {
                  <span class="mark mark--accent" data-test="voice-recommended">Recommended</span>
                }
                @if (isInstalled(m)) {
                  <span class="mark mark--ok" data-test="voice-installed">Installed</span>
                }
              </span>
              <span class="meta" data-test="voice-size"
                >{{ size(m.sizeBytes) }} download · needs {{ size(m.ramBytes) }} of memory</span
              >
              <span class="note">{{ m.description }}</span>
            </span>
          </label>
        }
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button
        type="button"
        matButton="outlined"
        [mat-dialog-close]="null"
        cdkFocusInitial
        data-test="voice-not-now"
      >
        Not now
      </button>
      <button
        type="button"
        matButton="filled"
        [disabled]="picked() === null"
        [mat-dialog-close]="picked()"
        data-test="voice-download"
      >
        <svg
          viewBox="0 0 24 24"
          width="16"
          height="16"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M12 3v12M7 10l5 5 5-5M4 21h16" /></svg
        >Download
      </button>
    </mat-dialog-actions>
  `,
  styles: [
    `
      :host {
        --mat-button-outlined-container-height: 36px;
        --mat-button-outlined-horizontal-padding: 16px;
        --mat-button-outlined-label-text-weight: 400;
        --mat-button-filled-container-height: 36px;
        --mat-button-filled-horizontal-padding: 16px;
        --mat-button-filled-label-text-weight: 400;
        --mat-button-filled-container-color: var(--sc-accent-strong);
        --mat-button-filled-label-text-color: var(--sc-on-accent);
      }
      .lede {
        margin: 0 0 12px;
        line-height: 1.5;
        color: var(--sc-fg-2);
      }
      [data-test='voice-download'] svg {
        margin-right: 8px;
      }
      .models {
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .model {
        display: flex;
        gap: 10px;
        align-items: flex-start;
        padding: 10px 12px;
        border: 1px solid var(--sc-line);
        border-radius: 10px;
        cursor: pointer;
      }
      .model:has(input:checked) {
        border-color: var(--sc-accent);
      }
      .body {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .name {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
        font-weight: 600;
        color: var(--sc-fg);
      }
      .meta,
      .note {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .note {
        color: var(--sc-fg-2);
      }
      .err {
        margin: 0 0 12px;
        color: var(--sc-error);
      }
      .mark {
        padding: 1px 8px;
        border-radius: 10px;
        font-size: 11px;
        font-weight: 600;
      }
      .mark--accent {
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
      }
      .mark--ok {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
    `,
  ],
})
export class VoiceModelDialogComponent {
  private readonly data = inject<VoiceModelDialogData>(MAT_DIALOG_DATA);
  readonly models = this.data.models;
  readonly error = this.data.error;
  readonly variantOf = variantOf;
  readonly size = formatBytes;
  readonly picked = signal<WhisperVariant | null>(this.firstChoice());

  isInstalled(entry: LocalModelCatalogEntry): boolean {
    return this.data.installed.includes(variantOf(entry));
  }

  private firstChoice(): WhisperVariant | null {
    const open = this.models.filter((m) => !this.isInstalled(m));
    const recommended = open.find((m) => m.badges.includes('recommended')) ?? open[0];
    return recommended === undefined ? null : variantOf(recommended);
  }
}

/** Reads main's Whisper table, opens the voice model dialog and resolves with the variant the owner chose, or null for Not now. */
export async function askVoiceModel(
  dialog: MatDialog,
  store: Pick<LocalModelsStore, 'loadWhisperTable' | 'whisperTableError'>,
  installed: readonly WhisperVariant[],
): Promise<WhisperVariant | null> {
  const models = await store.loadWhisperTable();
  const ref = dialog.open<VoiceModelDialogComponent, VoiceModelDialogData, WhisperVariant | null>(
    VoiceModelDialogComponent,
    {
      data: { installed, models, error: store.whisperTableError() },
      restoreFocus: true,
      panelClass: ['sc-dialog', 'sc-voice-dialog'],
      backdropClass: 'sc-dialog-backdrop',
      position: { top: '160px' },
      width: '560px',
      maxWidth: '95vw',
      maxHeight: '95vh',
    },
  );
  return (await firstValueFrom(ref.afterClosed())) ?? null;
}
