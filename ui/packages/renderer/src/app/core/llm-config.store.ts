import { Injectable, computed, inject, signal } from '@angular/core';
import { CLOUD_API_KEY_VAULT_KEYS, type ProviderKeyValidateResponse } from '@scout/shared';

import { ScoutBridgeService } from './scout-bridge';

export type LlmProvider = 'Ollama' | 'OpenAI' | 'Gemini' | 'ChatGptPlan' | 'None';
/** Cloud providers connected by an API key. */
export type CloudLlmProvider = 'OpenAI' | 'Gemini';
/** Every cloud provider Scout can answer with: the keyed ones and the ChatGPT plan sign-in. */
export type ActiveCloudProvider = CloudLlmProvider | 'ChatGptPlan';

export interface LlmConfigSnapshot {
  readonly provider: LlmProvider;
  readonly model: string;
  readonly endpoint: string;
}

const STORAGE_KEY = 'scout:llm-config';
const DEFAULT_ENDPOINT = 'http://localhost:11434';
const DEFAULT_PROVIDER: LlmProvider = 'Ollama';
const CLOUD_PROVIDERS: readonly CloudLlmProvider[] = ['OpenAI', 'Gemini'];
const ACTIVE_CLOUD_PROVIDERS: readonly ActiveCloudProvider[] = ['OpenAI', 'Gemini', 'ChatGptPlan'];

/** The chat model a cloud provider gets when Scout switches to it without the user choosing one. */
export const DEFAULT_CLOUD_MODELS: Readonly<Record<ActiveCloudProvider, string>> = Object.freeze({
  OpenAI: 'gpt-4o-mini',
  Gemini: 'gemini-2.5-flash',
  ChatGptPlan: 'gpt-5.6-luna',
});

function isActiveCloudProvider(value: unknown): value is ActiveCloudProvider {
  return value === 'OpenAI' || value === 'Gemini' || value === 'ChatGptPlan';
}

interface PersistedShape {
  readonly provider: Exclude<LlmProvider, 'None'>;
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

function isProvider(value: unknown): value is Exclude<LlmProvider, 'None'> {
  return value === 'Ollama' || isActiveCloudProvider(value);
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

  private readonly _connectedProviders = signal<ReadonlySet<ActiveCloudProvider>>(new Set());
  /** Cloud providers Scout could answer with: an API key in the vault, or a ChatGPT sign-in with plan usage. */
  readonly connectedProviders = this._connectedProviders.asReadonly();

  readonly isConfigured = computed<boolean>(() => this._model() !== null);

  /** The cloud provider the next worker start will use, once its API key is confirmed in the vault; null otherwise. */
  readonly activeCloudProvider = computed<ActiveCloudProvider | null>(() => {
    const provider = this._provider();
    return isActiveCloudProvider(provider) ? provider : null;
  });

  // Paints from the localStorage mirror first, then lets the vault decide:
  // llm.provider and llm.cloud-model are read back through the public vault
  // read, and a cloud provider counts only while its key is in the vault.
  // When that read is unavailable the mirror is verified with vault.has, and
  // a mirror the vault does not back is wiped and logged, so the UI never
  // claims "configured" when the worker would skip the env vars.
  async loadFromVault(): Promise<void> {
    const persisted = readPersisted();
    if (persisted !== null) {
      this._provider.set(persisted.provider);
      this._model.set(persisted.model);
      this._endpoint.set(persisted.endpoint);
    }

    const connected = new Set<ActiveCloudProvider>();
    for (const provider of CLOUD_PROVIDERS) {
      const res = await this.bridge
        .vaultHas({ key: CLOUD_API_KEY_VAULT_KEYS[provider] })
        .catch(() => ({ exists: false }));
      if (res.exists) connected.add(provider);
    }
    if (this._connectedProviders().has('ChatGptPlan')) connected.add('ChatGptPlan');
    this._connectedProviders.set(connected);

    const vaultProvider = await this.readPublic('llm.provider');
    if (isActiveCloudProvider(vaultProvider)) {
      if (vaultProvider !== 'ChatGptPlan' && !connected.has(vaultProvider)) {
        this.resetUnconfigured();
        return;
      }
      const model =
        (await this.readPublic('llm.cloud-model')) ??
        (persisted?.provider === vaultProvider ? persisted.model : null);
      const endpoint = persisted?.endpoint ?? DEFAULT_ENDPOINT;
      if (model !== null) writePersisted({ provider: vaultProvider, model, endpoint });
      this._provider.set(vaultProvider);
      this._model.set(model);
      this._endpoint.set(endpoint);
      return;
    }
    if (vaultProvider === 'None') {
      clearPersisted();
      this._provider.set('None');
      this._model.set(null);
      this._endpoint.set(persisted?.endpoint ?? DEFAULT_ENDPOINT);
      return;
    }
    if (vaultProvider === 'Ollama' && persisted === null) {
      this._provider.set('Ollama');
      this._model.set(null);
      this._endpoint.set(DEFAULT_ENDPOINT);
      return;
    }
    if (vaultProvider !== undefined && persisted !== null && persisted.provider !== 'Ollama') {
      clearPersisted();
      this._provider.set(vaultProvider === 'Ollama' ? 'Ollama' : null);
      this._model.set(null);
      this._endpoint.set(persisted.endpoint);
      return;
    }

    if (persisted === null) {
      this._provider.set(null);
      this._model.set(null);
      this._endpoint.set(DEFAULT_ENDPOINT);
      return;
    }

    let requiredInVault = false;
    if (persisted.provider === 'Ollama' || persisted.provider === 'ChatGptPlan') {
      const key = persisted.provider === 'Ollama' ? 'llm.model' : 'llm.cloud-model';
      try {
        requiredInVault = (await this.bridge.vaultHas({ key })).exists;
      } catch {
        requiredInVault = false;
      }
    } else {
      requiredInVault = connected.has(persisted.provider);
    }

    if (!requiredInVault) this.resetUnconfigured();
  }

  private async readPublic(
    key: 'llm.provider' | 'llm.cloud-model',
  ): Promise<string | null | undefined> {
    try {
      return (await this.bridge.vaultGetPublic({ key })).value;
    } catch {
      return undefined;
    }
  }

  private resetUnconfigured(): void {
    console.warn(
      'llm-config: localStorage advertises a provider but the vault does not back it — treating as unconfigured',
    );
    clearPersisted();
    this._provider.set(null);
    this._model.set(null);
    this._endpoint.set(DEFAULT_ENDPOINT);
  }

  async setSelection(provider: 'Ollama', model: string, endpoint: string): Promise<void> {
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

  /** Checks the key with the provider and, if accepted, stores it as that provider's key; when no cloud provider is in use it also becomes the provider in use. The key is never mirrored or kept in memory. */
  async connect(provider: CloudLlmProvider, apiKey: string): Promise<ProviderKeyValidateResponse> {
    const trimmedKey = apiKey.trim();
    if (trimmedKey.length === 0) {
      throw new Error('llm-config: API key must be non-empty');
    }
    const result = await this.bridge.validateProviderKey({ provider, apiKey: trimmedKey });
    if (!result.ok) return result;
    await this.bridge.vaultStore({ key: CLOUD_API_KEY_VAULT_KEYS[provider], value: trimmedKey });
    this._connectedProviders.update((set) => new Set([...set, provider]));
    if (this.activeCloudProvider() === null)
      await this.use(provider, DEFAULT_CLOUD_MODELS[provider]);
    return result;
  }

  /** Makes a connected cloud provider the one the next worker start uses, with this chat model. */
  async use(provider: ActiveCloudProvider, model: string): Promise<void> {
    const trimmedModel = model.trim();
    if (trimmedModel.length === 0) {
      throw new Error('llm-config: model must be non-empty');
    }
    await this.bridge.vaultStore({ key: 'llm.cloud-model', value: trimmedModel });
    await this.bridge.vaultStore({ key: 'llm.provider', value: provider });

    writePersisted({ provider, model: trimmedModel, endpoint: this._endpoint() });

    this._provider.set(provider);
    this._model.set(trimmedModel);
  }

  /** Deletes the provider's stored key; when it was the provider in use, Scout switches to another connected cloud provider or to None, never to Ollama. Returns whether it was in use. */
  async disconnect(provider: CloudLlmProvider): Promise<boolean> {
    const wasActive = this._provider() === provider;
    if (wasActive) await this.moveOffActive(provider);
    await this.bridge.vaultDelete({ key: CLOUD_API_KEY_VAULT_KEYS[provider] });
    if (wasActive) await this.bridge.vaultDelete({ key: 'llm.api-key' });
    this._connectedProviders.update(
      (set) => new Set([...set].filter((connected) => connected !== provider)),
    );
    return wasActive;
  }

  /** Points the next worker start at the ChatGPT plan with this chat model; the sign-in tokens never pass through the renderer. */
  async useChatGptPlan(model: string): Promise<void> {
    await this.use('ChatGptPlan', model);
  }

  /** Records whether the ChatGPT sign-in can answer (signed in with plan usage); it never changes the provider in use. */
  setChatGptConnected(connected: boolean): void {
    this._connectedProviders.update((set) => {
      const next = new Set(set);
      if (connected) next.add('ChatGptPlan');
      else next.delete('ChatGptPlan');
      return next;
    });
  }

  /** After a ChatGPT sign-out: when it was the provider in use, Scout switches to another connected cloud provider or to None, never to Ollama. Returns whether it was in use. */
  async disconnectChatGpt(): Promise<boolean> {
    const wasActive = this._provider() === 'ChatGptPlan';
    this.setChatGptConnected(false);
    if (wasActive) await this.moveOffActive('ChatGptPlan');
    return wasActive;
  }

  private async moveOffActive(provider: ActiveCloudProvider): Promise<void> {
    const other = ACTIVE_CLOUD_PROVIDERS.find(
      (candidate) => candidate !== provider && this._connectedProviders().has(candidate),
    );
    if (other !== undefined) {
      await this.use(other, DEFAULT_CLOUD_MODELS[other]);
      return;
    }
    await this.bridge.vaultStore({ key: 'llm.provider', value: 'None' });
    clearPersisted();
    this._provider.set('None');
    this._model.set(null);
  }

  /** Makes the local Ollama server the provider in use; its stored model and endpoint are left as they are. */
  async useOllama(): Promise<void> {
    await this.bridge.vaultStore({ key: 'llm.provider', value: 'Ollama' });
    clearPersisted();
    this._provider.set('Ollama');
    this._model.set(null);
  }

  setEndpointDraft(endpoint: string): void {
    this._endpoint.set(endpoint);
  }
}

export const LLM_CONFIG_STORAGE_KEY = STORAGE_KEY;
export const LLM_CONFIG_DEFAULT_ENDPOINT = DEFAULT_ENDPOINT;
export const LLM_CONFIG_DEFAULT_PROVIDER = DEFAULT_PROVIDER;
