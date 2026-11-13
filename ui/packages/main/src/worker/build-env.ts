import {
  CLOUD_API_KEY_VAULT_KEYS,
  OLLAMA_EMBEDDING_MODEL,
  VaultUnavailableError,
  serializeError,
  type AccessPairing,
  type CloudLlmProvider,
  type KubernetesLabelledContext,
  type VaultKey,
} from '@scout/shared';
import type { ControlPlaneClient } from '../control-plane/client.js';
import type { WorkerGateAccess } from '../control-plane/launcher.js';
import type { Logger } from '../util/logger.js';
import { replayPendingRevokes, type PendingRevokes } from '../ipc/write-grant-revoke.js';
import path from 'node:path';
import type { MediaToolPaths } from './resolve-binary.js';
import type { GitHubBotWorkerConfig } from '../oauth/github-bot.js';

export interface BuildEnvDeps {
  vaultGet: (key: VaultKey) => Promise<string | null>;
  chatGptTokenFile?: string;
  gitHubEnv?: Readonly<Record<string, string>>;
  /** The Scout bot's token file, the writes switched to it and its login; absent while there is no bot token. */
  gitHubBot?: GitHubBotWorkerConfig | null;
  youTubeEnv?: Readonly<Record<string, string>>;
  kubernetesSelection?: KubernetesSelection | null;
  allowedUserIds?: readonly string[];
  pairing?: AccessPairing | null;
  slackTokens?: SlackTokens | null;
  whisperModelPath?: string | null;
  /** Ids of the features whose writes the owner granted; the worker's guard allows writes only for these. */
  grantedWriteFeatures?: readonly string[];
  /** The control plane's write gate for the worker; null while the control plane is not launched. */
  workerGate?: WorkerGateAccess | null;
  /**
   * The bundled ffmpeg, font, piper, voices folder, rhubarb, mouths folder, presenters folder and face models
   * folder, which the worker always gets, empty when not bundled; and tesseract and its language data folder, set only when bundled.
   */
  mediaTools?: MediaToolPaths;
  /** The app's locale, such as `pt-BR`; the worker picks the languages it reads text from a screenshot in. */
  locale?: string;
  /** The app's userData folder; the worker keeps the meetings the owner chooses to keep in `meetings.db` there, and its local voice renders a sentence in `speech`. */
  userDataDir?: string;
  /** The sandbox's work folder, which main owns and the Docker card shows and clears. */
  sandboxWorkDirectory?: string;
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
  if (deps.gitHubBot != null) {
    env['SCOUT__PLUGINS__GITHUB__BOTTOKENFILE'] = deps.gitHubBot.tokenFile;
    env['SCOUT__PLUGINS__GITHUB__BOTWRITES'] = deps.gitHubBot.writes.join(',');
    env['SCOUT__PLUGINS__GITHUB__BOTLOGIN'] = deps.gitHubBot.login;
    env['SCOUT__PLUGINS__GITHUB__BOTREPOS'] = deps.gitHubBot.repositories.join(',');
  }
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
  env['SCOUT__FEATURES__GRANTEDWRITES'] = (deps.grantedWriteFeatures ?? []).join(',');
  env['SCOUT__WORKERGATE__SOCKETPATH'] = deps.workerGate?.socketPath ?? '';
  env['SCOUT__WORKERGATE__TOKEN'] = deps.workerGate?.token ?? '';
  env['SCOUT__OPERATOR__FFMPEGPATH'] = deps.mediaTools?.ffmpeg ?? '';
  env['SCOUT__OPERATOR__FONTPATH'] = deps.mediaTools?.font ?? '';
  env['SCOUT__OPERATOR__PIPERPATH'] = deps.mediaTools?.piper ?? '';
  env['SCOUT__OPERATOR__VOICESPATH'] = deps.mediaTools?.voices ?? '';
  env['SCOUT__OPERATOR__RHUBARBPATH'] = deps.mediaTools?.rhubarb ?? '';
  env['SCOUT__OPERATOR__MOUTHSPATH'] = deps.mediaTools?.mouths ?? '';
  env['SCOUT__OPERATOR__PRESENTERSPATH'] = deps.mediaTools?.presenters ?? '';
  env['SCOUT__OPERATOR__FACEMODELSPATH'] = deps.mediaTools?.faceModels ?? '';
  const { tesseract = '', tessdata = '' } = deps.mediaTools ?? {};
  if (tesseract !== '') env['SCOUT__TOOLS__TESSERACTPATH'] = tesseract;
  if (tessdata !== '') env['SCOUT__TOOLS__TESSDATAPATH'] = tessdata;
  if (deps.locale !== undefined) env['SCOUT__TOOLS__LOCALE'] = deps.locale;
  env['SCOUT__MEETINGS__DBPATH'] =
    deps.userDataDir !== undefined ? path.join(deps.userDataDir, 'meetings.db') : '';
  env['SCOUT__SPEECH__DIRECTORY'] =
    deps.userDataDir !== undefined ? path.join(deps.userDataDir, 'speech') : '';

  if (deps.sandboxWorkDirectory !== undefined)
    env['SCOUT__SANDBOX__WORKDIRECTORY'] = deps.sandboxWorkDirectory;
  if ((await vaultGet('sandbox.enabled')) === 'true') env['SCOUT__SANDBOX__ENABLED'] = 'true';

  const guardKey = await vaultGet('guard.typesafe-api-key');
  if (guardKey !== null) env['SCOUT__GUARD__TYPESAFE__APIKEY'] = guardKey;

  const translationModel = await vaultGet('llm.translation-model');
  if (
    llmProvider !== 'None' &&
    translationModel !== null &&
    (await vaultGet('llm.translation-provider')) === llmProvider
  ) {
    env['SCOUT__LLM__TRANSLATION__MODEL'] = translationModel;
  }

  switch (llmProvider) {
    case 'Ollama': {
      const llmModel = await vaultGet('llm.model');
      const llmEndpoint = await vaultGet('llm.endpoint');
      if (llmModel !== null) env['SCOUT__LLM__OLLAMA__CHAT__MODEL'] = llmModel;
      if (llmEndpoint !== null) env['SCOUT__LLM__OLLAMA__ENDPOINT'] = llmEndpoint;
      env['SCOUT__LLM__OLLAMA__EMBEDDINGS__MODEL'] = OLLAMA_EMBEDDING_MODEL;
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
    case 'Anthropic':
      env['SCOUT__LLM__ANTHROPIC__APIKEY'] = await requireApiKey(vaultGet, llmProvider);
      env['SCOUT__LLM__ANTHROPIC__CHAT__MODEL'] =
        (await vaultGet('llm.cloud-model')) ?? 'claude-opus-5-5';
      return env;
    case 'OpenRouter':
      env['SCOUT__LLM__OPENROUTER__APIKEY'] = await requireApiKey(vaultGet, llmProvider);
      env['SCOUT__LLM__OPENROUTER__CHAT__MODEL'] =
        (await vaultGet('llm.cloud-model')) ?? 'openai/gpt-4o-mini';
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

type GrantsClient = Pick<ControlPlaneClient, 'listFeatures' | 'setWriteGrant'>;

/**
 * The ids of the features whose writes the owner granted, as the control plane reports them at worker start.
 * A control plane still connecting is awaited for up to `waitMs`; none when it is not there, does not connect in
 * time or does not answer, so the worker starts without writes instead of failing. The revokes main could not send
 * earlier are sent first, and a feature whose revoke is still pending never counts as granted.
 */
export async function readGrantedWriteFeatures(
  client: GrantsClient | Promise<GrantsClient> | null,
  logger: Logger,
  pending: PendingRevokes,
  waitMs = 10_000,
): Promise<string[]> {
  const notConnected = 'worker starts with no write grants: the control plane is not connected yet';
  if (client === null) {
    logger.warn(notConnected);
    return [];
  }
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timedOut = new Promise<null>((resolve) => {
    timer = setTimeout(() => resolve(null), waitMs);
  });
  try {
    const connected = await Promise.race([Promise.resolve(client), timedOut]);
    if (connected === null) {
      logger.warn(notConnected);
      return [];
    }
    const stillPending = await replayPendingRevokes(connected, pending, logger);
    return (await connected.listFeatures())
      .filter((f) => f.granted && !stillPending.includes(f.id))
      .map((f) => f.id);
  } catch (err: unknown) {
    logger.warn('worker starts with no write grants: the control plane did not answer', {
      code: serializeError(err).code,
    });
    return [];
  } finally {
    clearTimeout(timer);
  }
}

async function requireApiKey(
  vaultGet: BuildEnvDeps['vaultGet'],
  provider: CloudLlmProvider,
): Promise<string> {
  const apiKey =
    (await vaultGet(CLOUD_API_KEY_VAULT_KEYS[provider])) ??
    (provider === 'OpenAI' || provider === 'Gemini' ? await vaultGet('llm.api-key') : null);
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
