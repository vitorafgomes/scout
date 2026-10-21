import { Injectable, computed, inject, signal } from '@angular/core';

import { LLM_PROVIDER_IDS, getLlmProviderDescriptor, type LlmProviderId } from '@scout/shared';

import { ScoutBridgeService } from './scout-bridge';

export type LlmProvider = 'Ollama';

export interface LlmConfigSnapshot {
  readonly provider: LlmProvider;
  readonly model: string;
  readonly endpoint: string;
}

const STORAGE_KEY = 'scout:llm-config';
const PROVIDERS_STORAGE_KEY = 'scout:llm-providers';
const DEFAULT_ENDPOINT = 'http://localhost:11434';
const DEFAULT_PROVIDER: LlmProvider = 'Ollama';
const DEFAULT_PROVIDER_ID: LlmProviderId = 'ollama';

interface PersistedShape {
  readonly provider: LlmProvider;
  readonly model: string;
  readonly endpoint: string;
}

// Non-secret field values only, keyed by field.key. Secrets are vault-only
// and are never part of this shape.
type ProviderFieldValues = Readonly<Record<string, string>>;

interface ProvidersPersistedShape {
  readonly activeProviderId: LlmProviderId;
  readonly config: Partial<Record<LlmProviderId, ProviderFieldValues>>;
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

function isLlmProviderId(value: unknown): value is LlmProviderId {
  return typeof value === 'string' && (LLM_PROVIDER_IDS as readonly string[]).includes(value);
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

function readProvidersState(): ProvidersPersistedShape | null {
  const storage = getStorage();
  if (storage === null) return null;
  try {
    const raw = storage.getItem(PROVIDERS_STORAGE_KEY);
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return null;
    const obj = parsed as Record<string, unknown>;
    if (!isLlmProviderId(obj['activeProviderId'])) return null;
    const configRaw = obj['config'];
    if (typeof configRaw !== 'object' || configRaw === null) return null;

    const config: Partial<Record<LlmProviderId, ProviderFieldValues>> = {};
    for (const [id, fieldsRaw] of Object.entries(configRaw as Record<string, unknown>)) {
      if (!isLlmProviderId(id)) continue;
      if (typeof fieldsRaw !== 'object' || fieldsRaw === null) continue;
      const fields: Record<string, string> = {};
      for (const [fieldKey, fieldValue] of Object.entries(fieldsRaw as Record<string, unknown>)) {
        if (typeof fieldValue === 'string') fields[fieldKey] = fieldValue;
      }
      config[id] = fields;
    }

    return { activeProviderId: obj['activeProviderId'], config };
  } catch {
    return null;
  }
}

function writeProvidersState(state: ProvidersPersistedShape): void {
  const storage = getStorage();
  if (storage === null) return;
  try {
    storage.setItem(PROVIDERS_STORAGE_KEY, JSON.stringify(state));
  } catch {
    // ignore quota / access errors
  }
}

@Injectable({ providedIn: 'root' })
export class LlmConfigStore {
  private readonly bridge = inject(ScoutBridgeService);

  // --- Legacy Ollama-only selection ---------------------------------------
  // Current selection. Read by both the UI (for display) and tests. Writes
  // happen through setSelection() and loadFromVault() — never set these
  // signals directly from outside.
  private readonly _provider = signal<LlmProvider | null>(null);
  private readonly _model = signal<string | null>(null);
  private readonly _endpoint = signal<string>(DEFAULT_ENDPOINT);

  readonly provider = this._provider.asReadonly();
  readonly model = this._model.asReadonly();
  readonly endpoint = this._endpoint.asReadonly();

  // Reflects the live multi-provider layer (isProviderConfigured), which
  // covers cloud providers (saveProvider + setActive) and Ollama-via-sources
  // alike. Also true when the legacy single-provider flow (setSelection) has
  // written a model, so the wizard/ready consumers and the deprecated
  // LlmComponent keep working unchanged: this is a widening, never a
  // narrowing, of what counts as "configured".
  readonly isConfigured = computed<boolean>(
    () => this._model() !== null || this.isProviderConfigured(this._activeProviderId()),
  );

  // --- Multi-provider layer ------------------------------------------------
  // Which provider the worker should be spawned with next. Defaults to
  // ollama so a fresh install with no cloud provider configured behaves
  // exactly as before this feature existed.
  private readonly _activeProviderId = signal<LlmProviderId>(DEFAULT_PROVIDER_ID);
  // Non-secret field values only, one entry per provider that has ever been
  // saved. Secrets live in the vault exclusively.
  private readonly _configById = signal<Partial<Record<LlmProviderId, ProviderFieldValues>>>({});
  // Whether every required secret field for a given provider is currently
  // present in the vault, refreshed on save and on hydrate.
  private readonly _secretPresentById = signal<Partial<Record<LlmProviderId, boolean>>>({});

  readonly activeProviderId = this._activeProviderId.asReadonly();
  readonly configById = this._configById.asReadonly();
  readonly secretPresentById = this._secretPresentById.asReadonly();

  // Hydrates the in-memory signals from localStorage and verifies the vault
  // mirror via vault.has. The vault holds the same triple so the main
  // process can read it at worker spawn time without a renderer round-trip.
  // If localStorage and the vault disagree, the renderer treats the
  // configuration as unset and logs a warning — this prevents the UI from
  // claiming "configured" when the worker would actually skip the env vars.
  async loadFromVault(): Promise<void> {
    await this.loadLegacyOllamaSelection();
    await this.loadProvidersState();
  }

  private async loadLegacyOllamaSelection(): Promise<void> {
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

  private async loadProvidersState(): Promise<void> {
    const state = readProvidersState();
    if (state === null) {
      this._activeProviderId.set(DEFAULT_PROVIDER_ID);
      this._configById.set({});
      this._secretPresentById.set({});
      return;
    }

    const nextConfig: Partial<Record<LlmProviderId, ProviderFieldValues>> = { ...state.config };
    const nextPresence: Partial<Record<LlmProviderId, boolean>> = {};

    for (const id of Object.keys(nextConfig) as LlmProviderId[]) {
      const allPresent = await this.allRequiredSecretsPresent(id);
      if (!allPresent) {
        console.warn(
          `llm-config: localStorage advertises provider '${id}' but a required secret is missing from the vault — treating as unconfigured`,
        );
        delete nextConfig[id];
        continue;
      }
      nextPresence[id] = true;
    }

    let nextActive = state.activeProviderId;
    if (nextActive !== DEFAULT_PROVIDER_ID && nextConfig[nextActive] === undefined) {
      // The distrust pass above dropped the active provider's config —
      // fall back rather than presenting an active provider with no data.
      nextActive = DEFAULT_PROVIDER_ID;
    }

    this._configById.set(nextConfig);
    this._secretPresentById.set(nextPresence);
    this._activeProviderId.set(nextActive);
    writeProvidersState({ activeProviderId: nextActive, config: nextConfig });
  }

  private async allRequiredSecretsPresent(id: LlmProviderId): Promise<boolean> {
    const descriptor = getLlmProviderDescriptor(id);
    const requiredSecretFields = descriptor.fields.filter((f) => f.required && f.secret);
    for (const f of requiredSecretFields) {
      try {
        const res = await this.bridge.vaultHas({ key: f.vaultKey });
        if (!res.exists) return false;
      } catch {
        return false;
      }
    }
    return true;
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

  // --- Multi-provider API ---------------------------------------------------

  // Validates required fields, stores every provided field in the vault
  // (secret and non-secret alike, so main can read them at spawn time), and
  // mirrors only the non-secret fields to configById/localStorage. A
  // required secret left blank is accepted when the vault already holds a
  // value for it (edit-in-place without re-entering the key); otherwise it
  // fails validation.
  async saveProvider(id: LlmProviderId, values: Readonly<Record<string, string>>): Promise<void> {
    const descriptor = getLlmProviderDescriptor(id);

    for (const field of descriptor.fields) {
      if (!field.required) continue;
      const raw = values[field.key];
      const hasValue = raw !== undefined && raw.trim().length > 0;
      if (hasValue) continue;

      if (field.secret) {
        let alreadyStored = false;
        try {
          const res = await this.bridge.vaultHas({ key: field.vaultKey });
          alreadyStored = res.exists;
        } catch {
          alreadyStored = false;
        }
        if (alreadyStored) continue;
      }

      throw new Error(`llm-config: ${field.label} is required for ${descriptor.displayName}`);
    }

    const nonSecretPatch: Record<string, string> = {};
    for (const field of descriptor.fields) {
      const raw = values[field.key];
      if (raw === undefined) continue;
      const trimmed = raw.trim();
      if (trimmed.length === 0) continue;

      // Sequential (not Promise.all) so vault call order stays deterministic.
      await this.bridge.vaultStore({ key: field.vaultKey, value: trimmed });
      if (!field.secret) {
        nonSecretPatch[field.key] = trimmed;
      }
    }

    const nextConfig = { ...this._configById(), [id]: nonSecretPatch };
    this._configById.set(nextConfig);
    writeProvidersState({ activeProviderId: this._activeProviderId(), config: nextConfig });

    const allPresent = await this.allRequiredSecretsPresent(id);
    this._secretPresentById.set({ ...this._secretPresentById(), [id]: allPresent });
  }

  isProviderConfigured(id: LlmProviderId): boolean {
    const descriptor = getLlmProviderDescriptor(id);
    const config = this._configById()[id] ?? {};
    for (const field of descriptor.fields) {
      if (!field.required || field.secret) continue;
      const v = config[field.key];
      if (v === undefined || v.trim().length === 0) return false;
    }
    const hasRequiredSecret = descriptor.fields.some((f) => f.required && f.secret);
    if (!hasRequiredSecret) return true;
    return this._secretPresentById()[id] === true;
  }

  async setActive(id: LlmProviderId): Promise<void> {
    const descriptor = getLlmProviderDescriptor(id);
    await this.bridge.vaultStore({ key: 'llm.active-id', value: id });
    await this.bridge.vaultStore({ key: 'llm.provider', value: descriptor.backendName });
    this._activeProviderId.set(id);
    writeProvidersState({ activeProviderId: id, config: this._configById() });
  }

  async clearProvider(id: LlmProviderId): Promise<void> {
    const descriptor = getLlmProviderDescriptor(id);
    for (const field of descriptor.fields) {
      try {
        await this.bridge.vaultDelete({ key: field.vaultKey });
      } catch {
        // best-effort: vault entry might already be gone
      }
    }

    const nextConfig = { ...this._configById() };
    delete nextConfig[id];
    this._configById.set(nextConfig);

    const nextPresence = { ...this._secretPresentById() };
    delete nextPresence[id];
    this._secretPresentById.set(nextPresence);

    if (this._activeProviderId() === id) {
      await this.setActive(DEFAULT_PROVIDER_ID);
    } else {
      writeProvidersState({ activeProviderId: this._activeProviderId(), config: nextConfig });
    }
  }
}

export const LLM_CONFIG_STORAGE_KEY = STORAGE_KEY;
export const LLM_CONFIG_DEFAULT_ENDPOINT = DEFAULT_ENDPOINT;
export const LLM_CONFIG_DEFAULT_PROVIDER = DEFAULT_PROVIDER;
export const LLM_PROVIDERS_STORAGE_KEY = PROVIDERS_STORAGE_KEY;
export const LLM_CONFIG_DEFAULT_PROVIDER_ID = DEFAULT_PROVIDER_ID;
