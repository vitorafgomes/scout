import { Injectable, inject, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';

import { readIpcError, type WhisperVariant } from '@scout/shared';

import { LocalModelsStore, isCancelled } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';

import { askVoiceModel } from './voice-model-dialog.component';

/**
 * The voice model ask at the first use of voice in a window: when no verified Whisper model is on this computer,
 * pressing the microphone or starting a live listen opens "Download a voice model?" instead. Not now is remembered
 * for this window until it reloads; an unknown state, when main could not say what is installed, never asks.
 */
@Injectable({ providedIn: 'root' })
export class VoiceModelAsk {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly dialog = inject(MatDialog);
  private readonly store = inject(LocalModelsStore);
  private installed: boolean | null = null;
  private declined = false;
  private busy = false;
  private requestId: string | null = null;

  /** What the ask is doing once the owner answered: the download's progress, its end, or why it failed. */
  readonly status = signal<string | null>(null);

  constructor() {
    this.bridge.onWhisperDownloadProgress((event) => {
      if (event.requestId !== this.requestId) return;
      this.status.set(
        `Downloading the ${event.variant} voice model... ${Math.round(event.percent)}%`,
      );
    });
  }

  /** Reads from main whether a verified Whisper model is installed; a failed read leaves it unknown. */
  async refresh(): Promise<void> {
    try {
      const { installed } = await this.bridge.whisperListInstalled();
      this.installed = installed.some((model) => model.verified);
    } catch {
      this.installed = null;
    }
  }

  /**
   * True when voice may start now. False when no model is installed and the owner has not said Not now in this
   * window: the ask opens instead, or is already open or downloading. Synchronous, so a press and its release
   * keep their order.
   */
  allowsVoice(): boolean {
    if (this.busy) return false;
    if (this.installed !== false || this.declined) return true;
    void this.ask();
    return false;
  }

  private async ask(): Promise<void> {
    this.busy = true;
    try {
      const picked = await askVoiceModel(this.dialog, this.store, []);
      if (picked === null) {
        this.declined = true;
        return;
      }
      await this.download(picked);
    } finally {
      this.busy = false;
    }
  }

  private async download(variant: WhisperVariant): Promise<void> {
    const requestId = `whisper:ask:${variant}:${Date.now()}`;
    this.requestId = requestId;
    this.status.set(`Downloading the ${variant} voice model... 0%`);
    try {
      await this.bridge.whisperDownloadModel({ variant, requestId });
      this.store.markDownloaded(`whisper-${variant}`);
      this.installed = true;
      this.status.set(`The ${variant} voice model is installed.`);
    } catch (err: unknown) {
      this.status.set(
        isCancelled(err)
          ? null
          : `Could not download the ${variant} voice model: ${readIpcError(err).message}`,
      );
    } finally {
      this.requestId = null;
    }
  }
}
