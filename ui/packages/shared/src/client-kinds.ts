export type ClientKind = 'discord' | 'slack' | 'teams';

export type VaultKey =
  | 'discord.bot-token'
  | 'discord.client-id'
  | 'discord.guild-id'
  | 'discord.guild-name';

export const VAULT_KEYS: readonly VaultKey[] = Object.freeze([
  'discord.bot-token',
  'discord.client-id',
  'discord.guild-id',
  'discord.guild-name',
] as const);

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
