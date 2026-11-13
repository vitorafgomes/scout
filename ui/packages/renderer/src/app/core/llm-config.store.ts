import { Injectable, computed, inject, signal } from '@angular/core';
import {
  CLOUD_API_KEY_VAULT_KEYS,
  readIpcError,
  type ProviderKeyValidateResponse,
} from '@scout/shared';

import { ScoutBridgeService } from './scout-bridge';

export type LlmProvider =
  'Ollama' | 'OpenAI' | 'Gemini' | 'Anthropic' | 'OpenRouter' | 'ChatGptPlan' | 'None';
/** Cloud providers connected by an API key. */
export type CloudLlmProvider = 'OpenAI' | 'Gemini' | 'Anthropic' | 'OpenRouter';
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
const CLOUD_PROVIDERS: readonly CloudLlmProvider[] = [
  'OpenAI',
  'Gemini',
  'Anthropic',
  'OpenRouter',
];
const ACTIVE_CLOUD_PROVIDERS: readonly ActiveCloudProvider[] = [
  'OpenAI',
  'Gemini',
  'ChatGptPlan',
  'Anthropic',
  'OpenRouter',
];

/** The chat model a cloud provider gets when Scout switches to it without the user choosing one. */
export const DEFAULT_CLOUD_MODELS: Readonly<Record<ActiveCloudProvider, string>> = Object.freeze({
  OpenAI: 'gpt-4o-mini',
  Gemini: 'gemini-2.5-flash',
  ChatGptPlan: 'gpt-5.6-luna',
  Anthropic: 'claude-opus-5-5',
  OpenRouter: 'openai/gpt-4o-mini',
});

const PROVIDER_CARD_NAMES: Readonly<Record<Exclude<LlmProvider, 'None'>, string>> = Object.freeze({
  Ollama: 'Ollama',
  OpenAI: 'OpenAI API',
  Gemini: 'Google Gemini',
  Anthropic: 'Claude API',
  ChatGptPlan: 'ChatGPT plan',
  OpenRouter: 'OpenRouter',
});

/** The name of the provider's card on the AI Sources screen, the name every message about the provider uses. */
export function providerLabel(provider: Exclude<LlmProvider, 'None'>): string {
  return PROVIDER_CARD_NAMES[provider];
}

/** What came of asking Ollama to free Scout's models when the provider in use moved away from it. */
export type OllamaFreeOutcome = 'freed' | 'could-not-ask' | 'nothing-loaded';

/** Thrown by connect when the key was stored but Scout could not make it the provider in use. */
export class KeyStoredNotSwitchedError extends Error {
  constructor(reason: unknown) {
    super(reason instanceof Error ? reason.message : 'Could not switch the provider.');
    this.name = 'KeyStoredNotSwitchedError';
  }
}

function isActiveCloudProvider(value: unknown): value is ActiveCloudProvider {
  return (
    value === 'OpenAI' ||
    value === 'Gemini' ||
    value === 'ChatGptPlan' ||
    value === 'Anthropic' ||
    value === 'OpenRouter'
  );
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

  private readonly _readFailed = signal(false);
  private readonly _mirroredProvider = signal<Exclude<LlmProvider, 'None'> | null>(null);
  /** True when the last load could not read the vault, so a provider of null means unknown, not unconfigured. */
  readonly readFailed = this._readFailed.asReadonly();
  /** The provider the localStorage mirror held as the last load began: the last one known when the vault read fails. */
  readonly mirroredProvider = this._mirroredProvider.asReadonly();

  readonly isConfigured = computed<boolean>(() => this._model() !== null);

  private readonly _ollamaFreed = signal<OllamaFreeOutcome | null>(null);
  /** Set when the provider in use last moved away from Ollama and Scout asked it to free its models; null otherwise. */
  readonly ollamaFreed = this._ollamaFreed.asReadonly();
  private ollamaAsk = 0;

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
    this._readFailed.set(false);
    this._mirroredProvider.set(persisted?.provider ?? null);
    if (persisted !== null) {
      this._provider.set(persisted.provider);
      this._model.set(persisted.model);
      this._endpoint.set(persisted.endpoint);
    }

    const connected = new Set<ActiveCloudProvider>();
    const unchecked = new Set<LlmProvider>();
    for (const provider of CLOUD_PROVIDERS) {
      const res = await this.bridge
        .vaultHas({ key: CLOUD_API_KEY_VAULT_KEYS[provider] })
        .catch(() => {
          this._readFailed.set(true);
          unchecked.add(provider);
          return { exists: false };
        });
      if (res.exists) connected.add(provider);
    }
    if (this._connectedProviders().has('ChatGptPlan')) connected.add('ChatGptPlan');
    this._connectedProviders.set(connected);

    const vaultProvider = await this.readPublic('llm.provider');
    if (isActiveCloudProvider(vaultProvider)) {
      if (vaultProvider !== 'ChatGptPlan' && !connected.has(vaultProvider)) {
        this.resetUnconfigured(unchecked.has(vaultProvider));
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
        this._readFailed.set(true);
        unchecked.add(persisted.provider);
      }
    } else {
      requiredInVault = connected.has(persisted.provider);
    }

    if (!requiredInVault) this.resetUnconfigured(unchecked.has(persisted.provider));
  }

  private async readPublic(
    key: 'llm.provider' | 'llm.cloud-model',
  ): Promise<string | null | undefined> {
    try {
      return (await this.bridge.vaultGetPublic({ key })).value;
    } catch {
      this._readFailed.set(true);
      return undefined;
    }
  }

  private resetUnconfigured(keyCheckFailed: boolean): void {
    console.warn(
      keyCheckFailed
        ? 'llm-config: the vault could not check the key of the provider localStorage advertises — keeping the mirror, provider unknown'
        : 'llm-config: localStorage advertises a provider but the vault does not back it — treating as unconfigured',
    );
    if (!keyCheckFailed) clearPersisted();
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
    this._ollamaFreed.set(null);
    this.ollamaAsk += 1;
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
    const active = this.activeCloudProvider();
    if (active === null || (active === 'ChatGptPlan' && !this._connectedProviders().has(active))) {
      try {
        await this.use(provider, DEFAULT_CLOUD_MODELS[provider]);
      } catch (err: unknown) {
        throw new KeyStoredNotSwitchedError(err);
      }
    }
    return result;
  }

  /** Makes a connected cloud provider the one the next worker start uses, with this chat model. */
  async use(provider: ActiveCloudProvider, model: string): Promise<void> {
    const before = this._provider();
    const trimmedModel = model.trim();
    if (trimmedModel.length === 0) {
      throw new Error('llm-config: model must be non-empty');
    }
    const storedModel = this.activeCloudProvider() === null ? null : this._model();
    await this.bridge.vaultStore({ key: 'llm.cloud-model', value: trimmedModel });
    try {
      await this.bridge.vaultStore({ key: 'llm.provider', value: provider });
    } catch (err: unknown) {
      if (storedModel !== null) {
        await this.bridge
          .vaultStore({ key: 'llm.cloud-model', value: storedModel })
          .catch(() => undefined);
      }
      throw err;
    }

    writePersisted({ provider, model: trimmedModel, endpoint: this._endpoint() });

    this._provider.set(provider);
    this._model.set(trimmedModel);
    this._ollamaFreed.set(null);
    this.ollamaAsk += 1;
    if (before === null || before === 'Ollama') void this.freeOllama(before);
  }

  private async freeOllama(before: 'Ollama' | null): Promise<void> {
    const ask = this.ollamaAsk;
    let outcome: OllamaFreeOutcome | null;
    try {
      const { unloaded, refused } = await this.bridge.llmUnloadOllamaModels();
      outcome =
        refused.length > 0 ? 'could-not-ask' : unloaded.length > 0 ? 'freed' : 'nothing-loaded';
    } catch (err: unknown) {
      const unreachable = readIpcError(err).code === 'llm/ollama-unreachable';
      outcome = before === null && unreachable ? null : 'could-not-ask';
    }
    if (ask === this.ollamaAsk) this._ollamaFreed.set(outcome);
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
    if (provider !== 'ChatGptPlan' && this._connectedProviders().has('ChatGptPlan')) {
      const status = await this.bridge.chatgptStatus().catch(() => null);
      this.setChatGptConnected(status !== null && status.signedIn && status.planUsageGranted);
    }
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
    this._ollamaFreed.set(null);
    this.ollamaAsk += 1;
  }

  setEndpointDraft(endpoint: string): void {
    this._endpoint.set(endpoint);
  }
}

export const LLM_CONFIG_STORAGE_KEY = STORAGE_KEY;
export const LLM_CONFIG_DEFAULT_ENDPOINT = DEFAULT_ENDPOINT;
export const LLM_CONFIG_DEFAULT_PROVIDER = DEFAULT_PROVIDER;
