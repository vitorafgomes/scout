import { VaultUnavailableError } from '@scout/shared';

export interface BuildEnvDeps {
  vaultGet: (key: string) => Promise<string | null>;
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
  const llmModel = await vaultGet('llm.model');
  const llmEndpoint = await vaultGet('llm.endpoint');

  const env: Record<string, string> = {
    SCOUT__LLM__PROVIDER: llmProvider,
    DOTNET_ENVIRONMENT: 'Production',
    SCOUT__ADAPTERS__DISCORD__BOTTOKEN: token,
  };

  if (llmModel !== null) env['SCOUT__LLM__OLLAMA__CHAT__MODEL'] = llmModel;
  if (llmEndpoint !== null) env['SCOUT__LLM__OLLAMA__ENDPOINT'] = llmEndpoint;

  return env;
}
