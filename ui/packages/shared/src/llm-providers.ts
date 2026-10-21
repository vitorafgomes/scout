import type { VaultKey } from './client-kinds.js';

// Single source of truth for every LLM provider the desktop app can drive,
// shared between main (build-env mapping) and renderer (settings UI). Adding
// a provider means adding one entry here — the vault allow-list, the
// build-env mapper, and the settings UI all derive from this catalog.

export type LlmProviderId =
  | 'ollama'
  | 'openai'
  | 'gemini'
  | 'openrouter'
  | 'azure-openai'
  | 'lm-studio'
  | 'mistral'
  | 'huggingface'
  | 'azure-ai-inference';

export type LlmProviderFieldKind = 'password' | 'text';

export interface LlmProviderFieldDescriptor {
  // Key inside the per-provider values map (dialog form state, configById).
  readonly key: string;
  readonly label: string;
  readonly kind: LlmProviderFieldKind;
  // Secrets are vault-only: never mirrored to localStorage or logs.
  readonly secret: boolean;
  readonly required: boolean;
  // Persistence key in the OS keyring vault.
  readonly vaultKey: VaultKey;
  // Appended to descriptor.envSegment (joined by "__") to form the env var
  // the worker reads, e.g. SCOUT__LLM__OPENAI + __ + APIKEY.
  readonly envSuffix: string;
  readonly placeholder?: string;
  readonly defaultValue?: string;
  readonly helperText?: string;
}

export type LlmProviderKind = 'cloud' | 'local' | 'preset';

export interface LlmProviderDescriptor {
  readonly id: LlmProviderId;
  // Exact value the .NET provider switch matches on (Scout.Llm
  // LlmServiceCollectionExtensions). Case-sensitive.
  readonly backendName: string;
  readonly displayName: string;
  readonly kind: LlmProviderKind;
  // Explicit env segment, not derived, so renaming a backendName never
  // silently changes the env var the worker expects.
  readonly envSegment: string;
  readonly docsUrl: string;
  readonly fields: readonly LlmProviderFieldDescriptor[];
}

// Plain object literals passed straight to Object.freeze() infer their
// string-valued properties (kind, vaultKey) as `string`, not as the narrower
// literal unions the interfaces declare. Routing every literal through these
// identity functions gives each object contextual typing against the
// interface, so 'password' is checked as LlmProviderFieldKind instead of
// widening to string.
function field(descriptor: LlmProviderFieldDescriptor): LlmProviderFieldDescriptor {
  return descriptor;
}

function provider(descriptor: LlmProviderDescriptor): LlmProviderDescriptor {
  return descriptor;
}

export const LLM_PROVIDER_CATALOG: readonly LlmProviderDescriptor[] = Object.freeze([
  provider({
    id: 'openai',
    backendName: 'OpenAI',
    displayName: 'OpenAI API',
    kind: 'cloud',
    envSegment: 'SCOUT__LLM__OPENAI',
    docsUrl: 'https://platform.openai.com/api-keys',
    fields: Object.freeze([
      field({
        key: 'apiKey',
        label: 'API key',
        kind: 'password',
        secret: true,
        required: true,
        vaultKey: 'llm.openai.api-key',
        envSuffix: 'APIKEY',
      }),
      field({
        key: 'model',
        label: 'Model',
        kind: 'text',
        secret: false,
        required: true,
        vaultKey: 'llm.openai.model',
        envSuffix: 'CHAT__MODEL',
        placeholder: 'gpt-4o',
      }),
      field({
        key: 'endpoint',
        label: 'Endpoint',
        kind: 'text',
        secret: false,
        required: false,
        vaultKey: 'llm.openai.endpoint',
        envSuffix: 'ENDPOINT',
        helperText: 'Leave blank for api.openai.com',
      }),
    ]),
  }),
  provider({
    id: 'gemini',
    backendName: 'Gemini',
    displayName: 'Google Gemini',
    kind: 'cloud',
    envSegment: 'SCOUT__LLM__GEMINI',
    docsUrl: 'https://aistudio.google.com/app/apikey',
    fields: Object.freeze([
      field({
        key: 'apiKey',
        label: 'API key',
        kind: 'password',
        secret: true,
        required: true,
        vaultKey: 'llm.gemini.api-key',
        envSuffix: 'APIKEY',
      }),
      field({
        key: 'model',
        label: 'Model',
        kind: 'text',
        secret: false,
        required: true,
        vaultKey: 'llm.gemini.model',
        envSuffix: 'CHAT__MODEL',
        placeholder: 'gemini-2.5-flash',
      }),
    ]),
  }),
  provider({
    id: 'openrouter',
    backendName: 'OpenRouter',
    displayName: 'OpenRouter',
    kind: 'cloud',
    envSegment: 'SCOUT__LLM__OPENROUTER',
    docsUrl: 'https://openrouter.ai/keys',
    fields: Object.freeze([
      field({
        key: 'apiKey',
        label: 'API key',
        kind: 'password',
        secret: true,
        required: true,
        vaultKey: 'llm.openrouter.api-key',
        envSuffix: 'APIKEY',
      }),
      field({
        key: 'model',
        label: 'Model',
        kind: 'text',
        secret: false,
        required: true,
        vaultKey: 'llm.openrouter.model',
        envSuffix: 'CHAT__MODEL',
        placeholder: 'openai/gpt-4o',
      }),
      field({
        key: 'embeddingsModel',
        label: 'Embeddings model',
        kind: 'text',
        secret: false,
        required: true,
        vaultKey: 'llm.openrouter.embeddings-model',
        envSuffix: 'EMBEDDINGS__MODEL',
        placeholder: 'text-embedding-3-small',
      }),
      field({
        key: 'embeddingsEndpoint',
        label: 'Embeddings endpoint',
        kind: 'text',
        secret: false,
        required: true,
        vaultKey: 'llm.openrouter.embeddings-endpoint',
        envSuffix: 'EMBEDDINGS__ENDPOINT',
        defaultValue: 'https://api.openai.com/v1',
      }),
      field({
        key: 'embeddingsApiKey',
        label: 'Embeddings API key',
        kind: 'password',
        secret: true,
        required: true,
        vaultKey: 'llm.openrouter.embeddings-api-key',
        envSuffix: 'EMBEDDINGS__APIKEY',
        helperText:
          'OpenRouter has no embeddings; provide a key for a separate OpenAI-compatible host',
      }),
    ]),
  }),
  provider({
    id: 'azure-openai',
    backendName: 'AzureOpenAI',
    displayName: 'Azure OpenAI',
    kind: 'cloud',
    envSegment: 'SCOUT__LLM__AZUREOPENAI',
    docsUrl: 'https://learn.microsoft.com/azure/ai-services/openai/',
    fields: Object.freeze([
      field({
        key: 'apiKey',
        label: 'API key',
        kind: 'password',
        secret: true,
        required: true,
        vaultKey: 'llm.azureopenai.api-key',
        envSuffix: 'APIKEY',
      }),
      field({
        key: 'endpoint',
        label: 'Endpoint',
        kind: 'text',
        secret: false,
        required: true,
        vaultKey: 'llm.azureopenai.endpoint',
        envSuffix: 'ENDPOINT',
        placeholder: 'https://<res>.openai.azure.com',
      }),
      field({
        key: 'chatDeployment',
        label: 'Chat deployment',
        kind: 'text',
        secret: false,
        required: true,
        vaultKey: 'llm.azureopenai.chat-deployment',
        envSuffix: 'CHAT__DEPLOYMENT',
        placeholder: 'gpt-4o',
      }),
      field({
        key: 'embeddingsDeployment',
        label: 'Embeddings deployment',
        kind: 'text',
        secret: false,
        required: true,
        vaultKey: 'llm.azureopenai.embeddings-deployment',
        envSuffix: 'EMBEDDINGS__DEPLOYMENT',
        placeholder: 'text-embedding-3-large',
      }),
    ]),
  }),
  provider({
    id: 'ollama',
    backendName: 'Ollama',
    displayName: 'Ollama',
    kind: 'local',
    envSegment: 'SCOUT__LLM__OLLAMA',
    docsUrl: 'https://ollama.com',
    fields: Object.freeze([
      field({
        key: 'model',
        label: 'Model',
        kind: 'text',
        secret: false,
        required: true,
        vaultKey: 'llm.model',
        envSuffix: 'CHAT__MODEL',
      }),
      field({
        key: 'endpoint',
        label: 'Endpoint',
        kind: 'text',
        secret: false,
        required: true,
        vaultKey: 'llm.endpoint',
        envSuffix: 'ENDPOINT',
        defaultValue: 'http://localhost:11434',
      }),
    ]),
  }),
  provider({
    id: 'lm-studio',
    // LM Studio's local server speaks the OpenAI wire format, so it reuses
    // the OpenAI backend provider rather than needing one of its own.
    backendName: 'OpenAI',
    displayName: 'LM Studio',
    kind: 'preset',
    envSegment: 'SCOUT__LLM__OPENAI',
    docsUrl: 'https://lmstudio.ai/docs/local-server',
    fields: Object.freeze([
      field({
        key: 'endpoint',
        label: 'Endpoint',
        kind: 'text',
        secret: false,
        required: true,
        vaultKey: 'llm.lm-studio.endpoint',
        envSuffix: 'ENDPOINT',
        defaultValue: 'http://localhost:1234/v1',
      }),
      field({
        key: 'apiKey',
        label: 'API key',
        kind: 'password',
        secret: true,
        required: false,
        vaultKey: 'llm.lm-studio.api-key',
        envSuffix: 'APIKEY',
        defaultValue: 'lm-studio',
      }),
      field({
        key: 'model',
        label: 'Model',
        kind: 'text',
        secret: false,
        required: false,
        vaultKey: 'llm.lm-studio.model',
        envSuffix: 'CHAT__MODEL',
      }),
    ]),
  }),
]);

export const LLM_PROVIDER_IDS: readonly LlmProviderId[] = Object.freeze(
  LLM_PROVIDER_CATALOG.map((d) => d.id),
);

/// Strict lookup for renderer call sites that already hold a validated
/// LlmProviderId. Throws on an unknown id — that only happens if the
/// catalog and the LlmProviderId union drift apart, which is a bug.
export function getLlmProviderDescriptor(id: LlmProviderId): LlmProviderDescriptor {
  const found = LLM_PROVIDER_CATALOG.find((d) => d.id === id);
  if (found === undefined) {
    throw new Error(`getLlmProviderDescriptor: unknown provider id '${id}'`);
  }
  return found;
}

/// Lenient lookup for untyped input (e.g. a raw string read back from the
/// vault or localStorage). Returns null instead of throwing so callers can
/// fall back to a default provider.
export function findLlmProviderDescriptor(id: string): LlmProviderDescriptor | null {
  return LLM_PROVIDER_CATALOG.find((d) => d.id === id) ?? null;
}

/// Matches the .NET provider switch's exact string, used by main's
/// build-env to resolve the legacy `llm.provider` vault key (which stores a
/// backendName, not an LlmProviderId) into a descriptor for field mapping.
export function findLlmProviderDescriptorByBackendName(
  backendName: string,
): LlmProviderDescriptor | null {
  return LLM_PROVIDER_CATALOG.find((d) => d.backendName === backendName) ?? null;
}
