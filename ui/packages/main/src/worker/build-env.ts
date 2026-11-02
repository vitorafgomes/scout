import {
  CLOUD_API_KEY_VAULT_KEYS,
  VaultUnavailableError,
  type AccessPairing,
  type CloudLlmProvider,
  type KubernetesLabelledContext,
  type VaultKey,
} from '@scout/shared';

export interface BuildEnvDeps {
  vaultGet: (key: VaultKey) => Promise<string | null>;
  chatGptTokenFile?: string;
  gitHubEnv?: Readonly<Record<string, string>>;
  youTubeEnv?: Readonly<Record<string, string>>;
  kubernetesSelection?: KubernetesSelection | null;
  allowedUserIds?: readonly string[];
  pairing?: AccessPairing | null;
  slackTokens?: SlackTokens | null;
  whisperModelPath?: string | null;
}

/** The Slack bot token and app-level token the owner saved; the worker needs both. */
export interface SlackTokens {
  readonly botToken: string;
  readonly appToken: string;
}

/** The kubeconfig path and the contexts the owner allowed Scout to read, with their labels. */
export interface KubernetesSelection {
  readonly path: string;
  readonly contexts: readonly KubernetesLabelledContext[];
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
  const slack = deps.slackTokens ?? null;

  const llmProvider = (await vaultGet('llm.provider')) ?? 'Ollama';
  const env: Record<string, string> = {
    SCOUT__LLM__PROVIDER: llmProvider,
    SCOUT__ADAPTERS__LOCAL__ENABLED: 'true',
    DOTNET_ENVIRONMENT: 'Production',
  };
  if (token !== null) env['SCOUT__ADAPTERS__DISCORD__BOTTOKEN'] = token;
  if (slack !== null) {
    env['SCOUT__ADAPTERS__SLACK__BOTTOKEN'] = slack.botToken;
    env['SCOUT__ADAPTERS__SLACK__APPLEVELTOKEN'] = slack.appToken;
  }
  Object.assign(env, deps.gitHubEnv, deps.youTubeEnv);
  if (deps.kubernetesSelection != null) {
    env['SCOUT__PLUGINS__KUBERNETES__KUBECONFIGPATH'] = deps.kubernetesSelection.path;
    deps.kubernetesSelection.contexts.forEach((context, i) => {
      env[`SCOUT__PLUGINS__KUBERNETES__CONTEXTS__${i}__NAME`] = context.name;
      env[`SCOUT__PLUGINS__KUBERNETES__CONTEXTS__${i}__LABEL`] = context.label;
    });
  }

  deps.allowedUserIds?.forEach((id, i) => {
    env[`SCOUT__ACCESS__ALLOWEDUSERIDS__${i}`] = id;
  });
  const ownerCredentialConnected =
    Object.keys(deps.gitHubEnv ?? {}).length > 0 ||
    Object.keys(deps.youTubeEnv ?? {}).length > 0 ||
    deps.kubernetesSelection != null ||
    llmProvider === 'ChatGptPlan';
  if (ownerCredentialConnected) env['SCOUT__ACCESS__REQUIREALLOWLIST'] = 'true';
  if (deps.pairing != null) {
    env['SCOUT__ACCESS__PAIRINGCODE'] = deps.pairing.code;
    env['SCOUT__ACCESS__PAIRINGEXPIRESAT'] = deps.pairing.expiresAt;
  }

  if (deps.whisperModelPath != null) env['SCOUT__TRANSCRIPTION__MODELPATH'] = deps.whisperModelPath;

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
    case 'None':
      return env;
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
    case 'ChatGptPlan':
      if (deps.chatGptTokenFile === undefined) {
        throw new Error('ChatGptPlan needs the path of the ChatGPT token file');
      }
      env['SCOUT__LLM__CHATGPTPLAN__TOKENFILE'] = deps.chatGptTokenFile;
      env['SCOUT__LLM__CHATGPTPLAN__CHAT__MODEL'] =
        (await vaultGet('llm.cloud-model')) ?? 'gpt-5.6-luna';
      return env;
    default:
      throw new Error(`llm.provider "${llmProvider}" is not supported by the desktop app`);
  }
}

async function requireApiKey(
  vaultGet: BuildEnvDeps['vaultGet'],
  provider: CloudLlmProvider,
): Promise<string> {
  const apiKey =
    (await vaultGet(CLOUD_API_KEY_VAULT_KEYS[provider])) ?? (await vaultGet('llm.api-key'));
  if (apiKey === null) {
    throw new VaultUnavailableError(
      `No API key stored for ${provider}: add the key on the AI Sources screen first`,
    );
  }
  return apiKey;
}

export interface LegacyKeyVault {
  get(key: VaultKey): Promise<string | null>;
  store(key: VaultKey, value: string): Promise<void>;
  delete(key: VaultKey): Promise<boolean>;
}

/** Moves the single shared `llm.api-key` of an earlier version into the active cloud provider's own slot, then deletes it; safe to run on every start. */
export async function migrateLegacyApiKey(vault: LegacyKeyVault): Promise<void> {
  const provider = await vault.get('llm.provider');
  if (provider !== 'OpenAI' && provider !== 'Gemini') return;
  const legacy = await vault.get('llm.api-key');
  if (legacy === null) return;
  const ownKey = CLOUD_API_KEY_VAULT_KEYS[provider];
  if ((await vault.get(ownKey)) === null) await vault.store(ownKey, legacy);
  await vault.delete('llm.api-key');
}
