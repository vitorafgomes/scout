import type { IpcMain } from 'electron';
import {
  CHANNEL,
  CLOUD_API_KEY_VAULT_KEYS,
  encodeIpcErrorMessage,
  type CloudLlmProvider,
  type LlmListModelsResponse,
  type LlmModel,
  type ProviderKeyValidateResponse,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { Vault } from '../vault/safe-storage-vault.js';

const VALIDATE_TIMEOUT_MS = 5_000;

const MODELS_URL: Readonly<Record<CloudLlmProvider, string>> = Object.freeze({
  OpenAI: 'https://api.openai.com/v1/models',
  Gemini: 'https://generativelanguage.googleapis.com/v1beta/models',
});

function authHeaders(provider: CloudLlmProvider, apiKey: string): Record<string, string> {
  return provider === 'OpenAI'
    ? { Authorization: `Bearer ${apiKey}` }
    : { 'x-goog-api-key': apiKey };
}

async function isGeminiInvalidKey(response: Response): Promise<boolean> {
  try {
    const body = (await response.json()) as {
      error?: { details?: ReadonlyArray<{ reason?: unknown }> };
    };
    const details = body.error?.details;
    return Array.isArray(details) && details.some((d) => d?.reason === 'API_KEY_INVALID');
  } catch {
    return false;
  }
}

function isCloudProvider(value: unknown): value is CloudLlmProvider {
  return value === 'OpenAI' || value === 'Gemini';
}

/** Checks a cloud API key by listing the provider's models; the key is sent only in that request's header and never logged or returned. */
export function registerProviderKeyHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  fetchFn: typeof fetch,
  logger: Logger,
  timeoutMs: number = VALIDATE_TIMEOUT_MS,
): () => void {
  ipcMain.handle(
    CHANNEL.providersValidateKey,
    async (_event, req: unknown): Promise<ProviderKeyValidateResponse> => {
      const r = (req ?? {}) as Record<string, unknown>;
      const provider = r['provider'];
      const apiKey = r['apiKey'];
      if (!isCloudProvider(provider) || typeof apiKey !== 'string' || apiKey.trim().length === 0) {
        throw Object.assign(
          new Error(
            'providers/invalid-request: provider must be OpenAI or Gemini and apiKey a non-empty string',
          ),
          { code: 'providers/invalid-request' },
        );
      }

      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeoutMs);
      try {
        const response = await fetchFn(MODELS_URL[provider], {
          method: 'GET',
          headers: authHeaders(provider, apiKey.trim()),
          signal: controller.signal,
        });
        logger.info('providers: key check answered', { provider, status: response.status });
        if (response.ok) return { ok: true };
        if (response.status === 401 || response.status === 403) {
          return { ok: false, reason: 'invalid-key' };
        }
        if (
          provider === 'Gemini' &&
          response.status === 400 &&
          (await isGeminiInvalidKey(response))
        ) {
          return { ok: false, reason: 'invalid-key' };
        }
        return { ok: false, reason: 'unexpected', status: response.status };
      } catch (err: unknown) {
        logger.warn('providers: key check could not reach the provider', {
          provider,
          error: err instanceof Error ? err.name : typeof err,
        });
        return { ok: false, reason: 'unreachable' };
      } finally {
        clearTimeout(timer);
      }
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.providersValidateKey);
  };
}

const LIST_MODELS_URL: Readonly<Record<CloudLlmProvider, string>> = Object.freeze({
  OpenAI: MODELS_URL.OpenAI,
  // ponytail: one page of up to 1000 Gemini models; follow nextPageToken if the list ever grows past it.
  Gemini: `${MODELS_URL.Gemini}?pageSize=1000`,
});

const OPENAI_CHAT_PREFIXES: readonly string[] = ['gpt-', 'o', 'chatgpt-'];
const OPENAI_NON_CHAT_WORDS: readonly string[] = [
  'embedding',
  'whisper',
  'tts',
  'dall-e',
  'moderation',
  'realtime',
  'audio',
  'image',
  'transcribe',
  'search',
];

function openAiChatModels(body: unknown): LlmModel[] | null {
  const data = (body as { data?: unknown } | null)?.data;
  if (!Array.isArray(data)) return null;
  return data
    .map((entry: unknown) => (entry as { id?: unknown } | null)?.id)
    .filter(
      (id): id is string =>
        typeof id === 'string' &&
        OPENAI_CHAT_PREFIXES.some((prefix) => id.startsWith(prefix)) &&
        !OPENAI_NON_CHAT_WORDS.some((word) => id.includes(word)),
    )
    .sort()
    .map((id) => ({ id, displayName: id }));
}

function geminiChatModels(body: unknown): LlmModel[] | null {
  const models = (body as { models?: unknown } | null)?.models;
  if (!Array.isArray(models)) return null;
  const chat: LlmModel[] = [];
  for (const entry of models as ReadonlyArray<Record<string, unknown> | null>) {
    const name = entry?.['name'];
    const methods = entry?.['supportedGenerationMethods'];
    if (typeof name !== 'string' || !Array.isArray(methods)) continue;
    if (!methods.includes('generateContent')) continue;
    const id = name.replace(/^models\//, '');
    const displayName = entry?.['displayName'];
    chat.push({ id, displayName: typeof displayName === 'string' ? displayName : id });
  }
  return chat;
}

function listModelsError(code: string, message: string): Error {
  return Object.assign(new Error(encodeIpcErrorMessage({ __scoutError: true, code, message })), {
    code,
  });
}

/** Lists the chat models a connected cloud provider offers, with the key stored in the vault; the key is sent only in that request's header and never logged, returned or put in an error. */
export function registerProviderModelHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  fetchFn: typeof fetch,
  vault: Pick<Vault, 'get'>,
  logger: Logger,
  timeoutMs: number = VALIDATE_TIMEOUT_MS,
): () => void {
  ipcMain.handle(
    CHANNEL.llmListModels,
    async (_event, req: unknown): Promise<LlmListModelsResponse> => {
      const provider = ((req ?? {}) as Record<string, unknown>)['provider'];
      if (!isCloudProvider(provider)) {
        throw listModelsError('providers/invalid-request', 'provider must be OpenAI or Gemini');
      }
      const apiKey = (await vault.get(CLOUD_API_KEY_VAULT_KEYS[provider]))?.trim() ?? '';
      if (apiKey.length === 0) {
        throw listModelsError('providers/not-connected', `${provider} is not connected.`);
      }

      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeoutMs);
      let status: number;
      let rejected: boolean;
      let body: unknown = null;
      try {
        const response = await fetchFn(LIST_MODELS_URL[provider], {
          method: 'GET',
          headers: authHeaders(provider, apiKey),
          signal: controller.signal,
        });
        status = response.status;
        rejected =
          status === 401 ||
          status === 403 ||
          (provider === 'Gemini' && status === 400 && (await isGeminiInvalidKey(response)));
        if (response.ok) body = await response.json().catch(() => null);
      } catch (err: unknown) {
        logger.warn('providers: model list could not reach the provider', {
          provider,
          error: err instanceof Error ? err.name : typeof err,
        });
        throw listModelsError('providers/unreachable', `${provider} could not be reached.`);
      } finally {
        clearTimeout(timer);
      }
      logger.info('providers: model list answered', { provider, status });
      if (rejected) {
        throw listModelsError('providers/key-rejected', `${provider} rejected the stored key.`);
      }
      const models = provider === 'OpenAI' ? openAiChatModels(body) : geminiChatModels(body);
      if (models === null) {
        throw listModelsError(
          'providers/request-failed',
          `${provider} did not return a model list (HTTP ${status}).`,
        );
      }
      return { models };
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.llmListModels);
  };
}
