import type { IpcMain } from 'electron';
import { CHANNEL, type CloudLlmProvider, type ProviderKeyValidateResponse } from '@scout/shared';
import type { Logger } from '../util/logger.js';

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
