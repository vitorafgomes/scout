import { Injectable, computed, inject, signal } from '@angular/core';

import { ScoutBridgeService } from './scout-bridge';

export type LlmProvider = 'Ollama';

export interface LlmConfigSnapshot {
  readonly provider: LlmProvider;
  readonly model: string;
  readonly endpoint: string;
}

const STORAGE_KEY = 'scout:llm-config';
const DEFAULT_ENDPOINT = 'http://localhost:11434';
const DEFAULT_PROVIDER: LlmProvider = 'Ollama';

interface PersistedShape {
  readonly provider: LlmProvider;
  readonly model: string;
  readonly endpoint: string;
}

function getStorage(): Storage | null {
  try {
    const globalWindow = (globalThis as { window?: Window }).window;
    return globalWindow?.localStorage ?? null;
  } catch {
    return null;
  }
}

function isProvider(value: unknown): value is LlmProvider {
  return value === 'Ollama';
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
    if (
      !isProvider(obj['provider']) ||
      typeof obj['model'] !== 'string' ||
      typeof obj['endpoint'] !== 'string'
    ) {
      return null;
    }
    if (obj['model'].length === 0 || obj['endpoint'].length === 0) return null;
    return {
      provider: obj['provider'],
      model: obj['model'],
      endpoint: obj['endpoint'],
    };
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
export class LlmConfigStore {
  private readonly bridge = inject(ScoutBridgeService);

  // Current selection. Read by both the UI (for display) and tests. Writes
  // happen through setSelection() and loadFromVault() — never set these
  // signals directly from outside.
  private readonly _provider = signal<LlmProvider | null>(null);
  private readonly _model = signal<string | null>(null);
  private readonly _endpoint = signal<string>(DEFAULT_ENDPOINT);

  readonly provider = this._provider.asReadonly();
  readonly model = this._model.asReadonly();
  readonly endpoint = this._endpoint.asReadonly();

  readonly isConfigured = computed<boolean>(() => this._model() !== null);

  // Hydrates the in-memory signals from localStorage and verifies the vault
  // mirror via vault.has. The vault holds the same triple so the main
  // process can read it at worker spawn time without a renderer round-trip.
  // If localStorage and the vault disagree, the renderer treats the
  // configuration as unset and logs a warning — this prevents the UI from
  // claiming "configured" when the worker would actually skip the env vars.
  async loadFromVault(): Promise<void> {
    const persisted = readPersisted();
    if (persisted === null) {
      this._provider.set(null);
      this._model.set(null);
      this._endpoint.set(DEFAULT_ENDPOINT);
      return;
    }

    let modelInVault = false;
    try {
      const res = await this.bridge.vaultHas({ key: 'llm.model' });
      modelInVault = res.exists;
    } catch {
      modelInVault = false;
    }

    if (!modelInVault) {
      // Storage says configured, vault says no — distrust the mirror,
      // wipe it, and present the user as unconfigured so the wizard
      // re-prompts before the worker spawns.
      console.warn(
        'llm-config: localStorage advertises a model but vault.has reports missing — treating as unconfigured',
      );
      clearPersisted();
      this._provider.set(null);
      this._model.set(null);
      this._endpoint.set(DEFAULT_ENDPOINT);
      return;
    }

    this._provider.set(persisted.provider);
    this._model.set(persisted.model);
    this._endpoint.set(persisted.endpoint);
  }

  async setSelection(provider: LlmProvider, model: string, endpoint: string): Promise<void> {
    if (model.length === 0) {
      throw new Error('llm-config: model must be non-empty');
    }
    if (endpoint.length === 0) {
      throw new Error('llm-config: endpoint must be non-empty');
    }

    await this.bridge.vaultStore({ key: 'llm.provider', value: provider });
    await this.bridge.vaultStore({ key: 'llm.model', value: model });
    await this.bridge.vaultStore({ key: 'llm.endpoint', value: endpoint });

    writePersisted({ provider, model, endpoint });

    this._provider.set(provider);
    this._model.set(model);
    this._endpoint.set(endpoint);
  }

  setEndpointDraft(endpoint: string): void {
    this._endpoint.set(endpoint);
  }
}

export const LLM_CONFIG_STORAGE_KEY = STORAGE_KEY;
export const LLM_CONFIG_DEFAULT_ENDPOINT = DEFAULT_ENDPOINT;
export const LLM_CONFIG_DEFAULT_PROVIDER = DEFAULT_PROVIDER;
