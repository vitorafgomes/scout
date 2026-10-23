import { VaultUnavailableError, type VaultKey } from '@scout/shared';

export interface BuildEnvDeps {
  vaultGet: (key: VaultKey) => Promise<string | null>;
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

  const llmProvider = (await vaultGet('llm.provider')) ?? 'Ollama';

  const env: Record<string, string> = {
    SCOUT__LLM__PROVIDER: llmProvider,
    DOTNET_ENVIRONMENT: 'Production',
    SCOUT__ADAPTERS__DISCORD__BOTTOKEN: token,
  };

  const guardKey = await vaultGet('guard.typesafe-api-key');
  if (guardKey !== null) env['SCOUT__GUARD__TYPESAFE__APIKEY'] = guardKey;

  switch (llmProvider) {
    case 'Ollama': {
      const llmModel = await vaultGet('llm.model');
      const llmEndpoint = await vaultGet('llm.endpoint');
      if (llmModel !== null) env['SCOUT__LLM__OLLAMA__CHAT__MODEL'] = llmModel;
      if (llmEndpoint !== null) env['SCOUT__LLM__OLLAMA__ENDPOINT'] = llmEndpoint;
      return env;
    }
    case 'OpenAI':
      env['SCOUT__LLM__OPENAI__APIKEY'] = await requireApiKey(vaultGet, llmProvider);
      env['SCOUT__LLM__OPENAI__CHAT__MODEL'] = (await vaultGet('llm.cloud-model')) ?? 'gpt-4o-mini';
      return env;
    case 'Gemini':
      env['SCOUT__LLM__GEMINI__APIKEY'] = await requireApiKey(vaultGet, llmProvider);
      env['SCOUT__LLM__GEMINI__CHAT__MODEL'] =
        (await vaultGet('llm.cloud-model')) ?? 'gemini-2.5-flash';
      env['SCOUT__LLM__GEMINI__EMBEDDINGS__MODEL'] = 'text-embedding-004';
      return env;
    default:
      throw new Error(`llm.provider "${llmProvider}" is not supported by the desktop app`);
  }
}

async function requireApiKey(
  vaultGet: BuildEnvDeps['vaultGet'],
  provider: string,
): Promise<string> {
  const apiKey = await vaultGet('llm.api-key');
  if (apiKey === null) {
    throw new VaultUnavailableError(
      `No API key stored for ${provider}: add the key on the AI Sources screen first`,
    );
  }
  return apiKey;
}
