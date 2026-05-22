import { Injectable, computed, inject, signal } from '@angular/core';

import { ScoutBridgeService } from './scout-bridge';

export type TranscriptionProvider = 'LocalWhisper';
export type WhisperModel = 'whisper-large-v3' | 'whisper-large-v3-turbo';

export interface TranscriptionConfigSnapshot {
  readonly provider: TranscriptionProvider;
  readonly model: WhisperModel;
}

const STORAGE_KEY = 'scout:stt-config';
const DEFAULT_PROVIDER: TranscriptionProvider = 'LocalWhisper';

interface PersistedShape {
  readonly provider: TranscriptionProvider;
  readonly model: WhisperModel;
}

function getStorage(): Storage | null {
  try {
    const globalWindow = (globalThis as { window?: Window }).window;
    return globalWindow?.localStorage ?? null;
  } catch {
    return null;
  }
}

function isProvider(value: unknown): value is TranscriptionProvider {
  return value === 'LocalWhisper';
}

function isModel(value: unknown): value is WhisperModel {
  return value === 'whisper-large-v3' || value === 'whisper-large-v3-turbo';
}

function readPersisted(): PersistedShape | null {
  const storage = getStorage();
  if (storage === null) return null;
  try {
    const raw = storage.getItem(STORAGE_KEY);
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return null;
    const obj = parsed as Record<string, unknown>;
    if (!isProvider(obj['provider']) || !isModel(obj['model'])) return null;
    return { provider: obj['provider'], model: obj['model'] };
  } catch {
    return null;
  }
}

function writePersisted(snapshot: PersistedShape): void {
  const storage = getStorage();
  if (storage === null) return;
  try {
    storage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
  } catch {
    // ignore quota / access errors
  }
}

function clearPersisted(): void {
  const storage = getStorage();
  if (storage === null) return;
  try {
    storage.removeItem(STORAGE_KEY);
  } catch {
    // ignore
  }
}

@Injectable({ providedIn: 'root' })
export class TranscriptionConfigStore {
  private readonly bridge = inject(ScoutBridgeService);

  private readonly _provider = signal<TranscriptionProvider | null>(null);
  private readonly _model = signal<WhisperModel | null>(null);

  readonly provider = this._provider.asReadonly();
  readonly model = this._model.asReadonly();
  readonly isConfigured = computed<boolean>(() => this._model() !== null);

  async loadFromVault(): Promise<void> {
    const persisted = readPersisted();
    if (persisted === null) {
      this._provider.set(null);
      this._model.set(null);
      return;
    }

    let modelInVault = false;
    try {
      const res = await this.bridge.vaultHas({ key: 'stt.model' });
      modelInVault = res.exists;
    } catch {
      modelInVault = false;
    }

    if (!modelInVault) {
      console.warn(
        'stt-config: localStorage advertises a model but vault.has reports missing — treating as unconfigured',
      );
      clearPersisted();
      this._provider.set(null);
      this._model.set(null);
      return;
    }

    this._provider.set(persisted.provider);
    this._model.set(persisted.model);
  }

  async setSelection(provider: TranscriptionProvider, model: WhisperModel): Promise<void> {
    await this.bridge.vaultStore({ key: 'stt.provider', value: provider });
    await this.bridge.vaultStore({ key: 'stt.model', value: model });

    writePersisted({ provider, model });

    this._provider.set(provider);
    this._model.set(model);
  }
}

export const STT_CONFIG_STORAGE_KEY = STORAGE_KEY;
export const STT_CONFIG_DEFAULT_PROVIDER = DEFAULT_PROVIDER;
