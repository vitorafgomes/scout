import {
  VaultUnavailableError,
  findLlmProviderDescriptor,
  findLlmProviderDescriptorByBackendName,
  getLlmProviderDescriptor,
  type LlmProviderDescriptor,
  type VaultKey,
} from '@scout/shared';

export interface BuildEnvDeps {
  vaultGet: (key: VaultKey) => Promise<string | null>;
  // Shared secret for the worker's loopback HTTP control surface. When set, the
  // worker requires it on every request via the x-scout-token header.
  controlPlaneToken?: string;
}

const OLLAMA_DESCRIPTOR = getLlmProviderDescriptor('ollama');

// Resolves which provider descriptor drives the SCOUT__LLM__* env vars, and
// the exact string written to SCOUT__LLM__PROVIDER. These are kept separate
// on purpose: 'llm.active-id' (an LlmProviderId) disambiguates OpenAI-backed
// presets like lm-studio from plain OpenAI, but the legacy 'llm.provider'
// vault key stores a raw backendName and is passed through verbatim even
// when it does not match a known descriptor, preserving pre-catalog
// behaviour for any value a worker might already have been configured with.
async function resolveActiveProvider(
  vaultGet: BuildEnvDeps['vaultGet'],
): Promise<{ providerName: string; descriptor: LlmProviderDescriptor }> {
  const activeId = await vaultGet('llm.active-id');
  if (activeId !== null) {
    const byActiveId = findLlmProviderDescriptor(activeId);
    if (byActiveId !== null) {
      return { providerName: byActiveId.backendName, descriptor: byActiveId };
    }
  }

  const legacyProvider = await vaultGet('llm.provider');
  if (legacyProvider !== null) {
    const byBackendName = findLlmProviderDescriptorByBackendName(legacyProvider);
    return { providerName: legacyProvider, descriptor: byBackendName ?? OLLAMA_DESCRIPTOR };
  }

  return { providerName: OLLAMA_DESCRIPTOR.backendName, descriptor: OLLAMA_DESCRIPTOR };
}

// Assembles the environment variables passed to Scout.Host.Console at spawn
// time. The .NET configuration env-var convention maps each ":" path separator
// to "__", so Scout:Adapters:Discord:BotToken becomes
// SCOUT__ADAPTERS__DISCORD__BOTTOKEN.
//
// Never log the returned object — it contains secrets.
export async function buildEnv(deps: BuildEnvDeps): Promise<Record<string, string>> {
  const { vaultGet } = deps;

  const token = await vaultGet('discord.bot-token');
  if (token === null) {
    throw new VaultUnavailableError(
      'discord.bot-token not found in vault — complete the Discord wizard first',
    );
  }

  const { providerName, descriptor } = await resolveActiveProvider(vaultGet);

  const env: Record<string, string> = {
    SCOUT__LLM__PROVIDER: providerName,
    DOTNET_ENVIRONMENT: 'Production',
    SCOUT__ADAPTERS__DISCORD__BOTTOKEN: token,
  };

  // Every field the active descriptor declares — secret or not — is read
  // from the vault and mapped to its env var. Only the active provider's
  // fields are ever touched, so stale credentials left behind for a
  // previously-active provider never leak into the spawned process.
  for (const field of descriptor.fields) {
    const value = await vaultGet(field.vaultKey);
    if (value !== null && value.trim().length > 0) {
      env[`${descriptor.envSegment}__${field.envSuffix}`] = value;
    }
  }

  // Optional time-zone override for the daily-assistant plugins. When absent,
  // the backend falls back to the machine's local zone on its own, so we set
  // nothing here.
  const timezone = await vaultGet('settings.timezone');
  if (timezone !== null && timezone.trim() !== '') {
    env['SCOUT__PLUGINS__DATES__DEFAULTTIMEZONE'] = timezone;
    env['SCOUT__PLUGINS__WEATHER__TIMEZONE'] = timezone;
    env['SCOUT__PLUGINS__HOLIDAYS__TIMEZONE'] = timezone;
  }

  // Plain env var (not the ":" -> "__" .NET config convention) — the worker
  // reads it directly to guard its HTTP control surface.
  if (deps.controlPlaneToken !== undefined) {
    env['SCOUT_CONTROLPLANE_TOKEN'] = deps.controlPlaneToken;
  }

  return env;
}
