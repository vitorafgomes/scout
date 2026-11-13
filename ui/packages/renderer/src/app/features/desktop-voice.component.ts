import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';

import { readIpcError, type WhisperVariant } from '@scout/shared';

import { IconComponent } from '../core/icon.component';
import { LocalModelsStore, isCancelled } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';

import { askVoiceModel } from './voice-model-dialog.component';

/** The voice model as the Desktop card shows it: checked, unknown, missing, on this computer, downloading, or failed. */
export type VoiceState =
  | { readonly kind: 'checking' }
  | { readonly kind: 'unknown' }
  | { readonly kind: 'none' }
  | { readonly kind: 'installed'; readonly variant: WhisperVariant }
  | { readonly kind: 'downloading'; readonly variant: WhisperVariant; readonly percent: number }
  | { readonly kind: 'failed'; readonly variant: WhisperVariant; readonly reason: string };

/** The Desktop card's voice line: which Whisper model voice messages use, a download when none is here, and the way to Models, Transcription. */
@Component({
  selector: 'sc-desktop-voice',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, MatButtonModule, MatProgressBarModule, RouterLink],
  template: `
    <section class="voice" data-test="desktop-voice">
      @switch (state().kind) {
        @case ('installed') {
          <p class="line" data-test="voice-line"><sc-icon name="mic" [size]="16" />{{ line() }}</p>
        }
        @case ('none') {
          <div class="row">
            <p class="line" data-test="voice-line">
              <sc-icon name="mic" [size]="16" />{{ line() }}
            </p>
            <button
              type="button"
              matButton="filled"
              data-test="voice-download-open"
              (click)="openDialog()"
            >
              <sc-icon name="download" [size]="16" />Download
            </button>
          </div>
        }
        @case ('unknown') {
          <div class="row">
            <p class="line err" role="alert" data-test="voice-line">
              <sc-icon name="error" [size]="16" />{{ line() }}
            </p>
            <button
              type="button"
              matButton="outlined"
              data-test="voice-try-again"
              (click)="refresh()"
            >
              <sc-icon name="refresh" [size]="16" />Try again
            </button>
          </div>
        }
        @case ('downloading') {
          <p class="line" data-test="voice-line">
            <sc-icon name="download" [size]="16" />{{ line() }}
          </p>
          <mat-progress-bar
            mode="determinate"
            [value]="percent()"
            aria-label="Voice model download"
          />
        }
        @case ('failed') {
          <div class="row">
            <p class="line err" role="alert" data-test="voice-line">
              <sc-icon name="error" [size]="16" />{{ line() }}
            </p>
            <button
              type="button"
              matButton="outlined"
              data-test="voice-retry"
              (click)="download(variant()!)"
            >
              <sc-icon name="refresh" [size]="16" />Retry
            </button>
          </div>
        }
        @default {
          <p class="line" data-test="voice-line"><sc-icon name="mic" [size]="16" />{{ line() }}</p>
        }
      }
      <a
        class="manage"
        routerLink="/llm"
        [queryParams]="{ tab: 'transcription' }"
        data-test="voice-manage"
        >Manage in Models, Transcription</a
      >
    </section>
  `,
  styles: [
    `
      .voice {
        display: flex;
        flex-direction: column;
        gap: 8px;
        padding-top: 10px;
        border-top: 1px solid var(--sc-line);
      }
      .row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .line {
        margin: 0 auto 0 0;
        display: inline-flex;
        align-items: center;
        gap: 8px;
        font-size: 13px;
        color: var(--sc-fg);
      }
      .err {
        color: var(--sc-error);
      }
      .manage {
        align-self: flex-start;
        font-size: 13px;
        color: var(--sc-accent);
      }
      button sc-icon {
        margin-right: 6px;
      }
    `,
  ],
})
export class DesktopVoiceComponent implements OnInit, OnDestroy {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly dialog = inject(MatDialog);
  private readonly store = inject(LocalModelsStore);
  private requestId: string | null = null;
  private unsubscribe: (() => void) | null = null;
  private installed: readonly WhisperVariant[] = [];

  readonly state = signal<VoiceState>({ kind: 'checking' });

  async ngOnInit(): Promise<void> {
    this.unsubscribe = this.bridge.onWhisperDownloadProgress((event) => {
      const current = this.state();
      if (event.requestId !== this.requestId || current.kind !== 'downloading') return;
      this.state.set({ ...current, percent: Math.round(event.percent) });
    });
    await this.refresh();
  }

  ngOnDestroy(): void {
    this.unsubscribe?.();
  }

  variant(): WhisperVariant | null {
    const state = this.state();
    return 'variant' in state ? state.variant : null;
  }

  percent(): number {
    const state = this.state();
    return state.kind === 'downloading' ? state.percent : 0;
  }

  /** The voice line in words, as the Desktop panel and the Desktop card read it. */
  line(): string {
    const state = this.state();
    switch (state.kind) {
      case 'installed':
        return `Voice model: ${state.variant} installed`;
      case 'none':
        return 'Voice model: not installed';
      case 'unknown':
        return 'Scout could not read the voice models.';
      case 'downloading':
        return `Downloading the ${state.variant} voice model... ${state.percent}%`;
      case 'failed':
        return `Could not download the ${state.variant} voice model: ${state.reason}`;
      case 'checking':
        return 'Voice model: checking...';
    }
  }

  /** Opens the voice model dialog and downloads the model the owner picks; Not now changes nothing. */
  async openDialog(): Promise<void> {
    const picked = await askVoiceModel(this.dialog, this.store, this.installed);
    if (picked !== null) await this.download(picked);
  }

  /** Downloads one Whisper model through main's pinned download, and records it as the Transcription screen does. */
  async download(variant: WhisperVariant): Promise<void> {
    const requestId = `whisper:desktop:${variant}:${Date.now()}`;
    this.requestId = requestId;
    this.state.set({ kind: 'downloading', variant, percent: 0 });
    try {
      await this.bridge.whisperDownloadModel({ variant, requestId });
      this.store.markDownloaded(`whisper-${variant}`);
      this.installed = [...this.installed, variant];
      this.state.set({ kind: 'installed', variant });
      await this.refresh();
    } catch (err: unknown) {
      if (isCancelled(err)) await this.refresh();
      else this.state.set({ kind: 'failed', variant, reason: readIpcError(err).message });
    } finally {
      this.requestId = null;
    }
  }

  /**
   * Shows the model main says Scout uses, the one the worker loads. While the worker has not loaded one yet, as right
   * after a download whose restart is still waiting, it shows the saved choice when it is installed, else the first
   * installed model; none when nothing verified is installed, unknown when main cannot answer.
   */
  async refresh(): Promise<void> {
    let inUse: WhisperVariant | null;
    try {
      const res = await this.bridge.whisperListInstalled();
      this.installed = res.installed.filter((m) => m.verified).map((m) => m.variant);
      const preferred = res.preferred ?? null;
      inUse =
        res.inUse ??
        (preferred !== null && this.installed.includes(preferred)
          ? preferred
          : (this.installed[0] ?? null));
    } catch {
      this.installed = [];
      this.state.set({ kind: 'unknown' });
      return;
    }
    this.state.set(inUse === null ? { kind: 'none' } : { kind: 'installed', variant: inUse });
  }
}
