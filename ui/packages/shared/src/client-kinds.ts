import { LLM_PROVIDER_CATALOG } from './llm-providers.js';

export type ClientKind = 'discord' | 'slack' | 'teams';

// Kept as an explicit enumerated union (not derived from the catalog) so
// build-env and the vault preload stay compile-checked against every
// concrete key — a typo here is a type error, not a silent runtime miss.
export type VaultKey =
  | 'discord.bot-token'
  | 'discord.client-id'
  | 'discord.guild-id'
  | 'discord.guild-name'
  | 'llm.provider'
  | 'llm.active-id'
  | 'llm.model'
  | 'llm.endpoint'
  | 'llm.openai.api-key'
  | 'llm.openai.model'
  | 'llm.openai.endpoint'
  | 'llm.gemini.api-key'
  | 'llm.gemini.model'
  | 'llm.openrouter.api-key'
  | 'llm.openrouter.model'
  | 'llm.openrouter.embeddings-model'
  | 'llm.openrouter.embeddings-endpoint'
  | 'llm.openrouter.embeddings-api-key'
  | 'llm.azureopenai.api-key'
  | 'llm.azureopenai.endpoint'
  | 'llm.azureopenai.chat-deployment'
  | 'llm.azureopenai.embeddings-deployment'
  | 'llm.lm-studio.endpoint'
  | 'llm.lm-studio.api-key'
  | 'llm.lm-studio.model'
  | 'stt.provider'
  | 'stt.model'
  | 'settings.timezone';

// The LLM slice is generated from the provider catalog so adding a provider
// field there automatically allow-lists its vault key here — the only
// manual step left is adding the literal to the VaultKey union above.
export const VAULT_KEYS: readonly VaultKey[] = Object.freeze([
  'discord.bot-token',
  'discord.client-id',
  'discord.guild-id',
  'discord.guild-name',
  'llm.provider',
  'llm.active-id',
  ...LLM_PROVIDER_CATALOG.flatMap((descriptor) => descriptor.fields.map((field) => field.vaultKey)),
  'stt.provider',
  'stt.model',
  'settings.timezone',
]);

export const CLIENT_KINDS: readonly ClientKind[] = Object.freeze([
  'discord',
  'slack',
  'teams',
] as const);

export function assertNeverClient(kind: never): never {
  throw new Error(`assertNeverClient: unhandled ClientKind value ${String(kind)}`);
}

export type DetectionMethod =
  | 'registry'
  | 'bundle-id'
  | 'desktop-file'
  | 'package-manager'
  | 'uri-scheme'
  | 'none';

export interface DetectedClient {
  readonly kind: ClientKind;
  readonly installed: boolean;
  readonly method: DetectionMethod;
  readonly version?: string;
  readonly location?: string;
}
