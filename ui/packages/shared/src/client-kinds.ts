export type ClientKind = 'discord' | 'slack' | 'teams';

export type VaultKey =
  | 'discord.bot-token'
  | 'discord.client-id'
  | 'discord.guild-id'
  | 'discord.guild-name'
  | 'llm.provider'
  | 'llm.model'
  | 'llm.endpoint'
  | 'llm.api-key'
  | 'llm.openai.api-key'
  | 'llm.gemini.api-key'
  | 'llm.anthropic.api-key'
  | 'llm.openrouter.api-key'
  | 'llm.cloud-model'
  | 'llm.translation-model'
  | 'llm.translation-provider'
  | 'stt.provider'
  | 'stt.model'
  | 'guard.typesafe-api-key'
  | 'sandbox.enabled'
  | 'speech.voice.provider'
  | 'speech.voice.local';

export const VAULT_KEYS: readonly VaultKey[] = Object.freeze([
  'discord.bot-token',
  'discord.client-id',
  'discord.guild-id',
  'discord.guild-name',
  'llm.provider',
  'llm.model',
  'llm.endpoint',
  'llm.api-key',
  'llm.openai.api-key',
  'llm.gemini.api-key',
  'llm.anthropic.api-key',
  'llm.openrouter.api-key',
  'llm.cloud-model',
  'llm.translation-model',
  'llm.translation-provider',
  'stt.provider',
  'stt.model',
  'guard.typesafe-api-key',
  'sandbox.enabled',
  'speech.voice.provider',
  'speech.voice.local',
] as const);

export type PublicVaultKey =
  | 'llm.provider'
  | 'llm.cloud-model'
  | 'llm.translation-model'
  | 'llm.translation-provider'
  | 'sandbox.enabled'
  | 'discord.client-id'
  | 'speech.voice.provider'
  | 'speech.voice.local';

/** The only vault keys the renderer may read back: non-secret settings and the Discord application id, never a key or token. */
export const PUBLIC_VAULT_KEYS: readonly PublicVaultKey[] = Object.freeze([
  'llm.provider',
  'llm.cloud-model',
  'llm.translation-model',
  'llm.translation-provider',
  'sandbox.enabled',
  'discord.client-id',
  'speech.voice.provider',
  'speech.voice.local',
] as const);

export type CloudLlmProvider = 'OpenAI' | 'Gemini' | 'Anthropic' | 'OpenRouter';

/** The vault key that holds each cloud provider's own API key. */
export const CLOUD_API_KEY_VAULT_KEYS: Readonly<Record<CloudLlmProvider, VaultKey>> = Object.freeze(
  {
    OpenAI: 'llm.openai.api-key',
    Gemini: 'llm.gemini.api-key',
    Anthropic: 'llm.anthropic.api-key',
    OpenRouter: 'llm.openrouter.api-key',
  },
);

export const CLIENT_KINDS: readonly ClientKind[] = Object.freeze([
  'discord',
  'slack',
  'teams',
] as const);

export function assertNeverClient(kind: never): never {
  throw new Error(`assertNeverClient: unhandled ClientKind value ${String(kind)}`);
}

export type DetectionMethod =
  'registry' | 'bundle-id' | 'desktop-file' | 'package-manager' | 'uri-scheme' | 'none';

export interface DetectedClient {
  readonly kind: ClientKind;
  readonly installed: boolean;
  readonly method: DetectionMethod;
  readonly version?: string;
  readonly location?: string;
}
