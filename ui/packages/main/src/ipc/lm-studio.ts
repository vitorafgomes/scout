import type { IpcMain } from 'electron';
import {
  CHANNEL,
  OllamaUnreachableError,
  serializeError,
  type LlmListLmStudioModelsRequest,
  type LlmListLmStudioModelsResponse,
  type LmStudioModelInfo,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';

const LIST_TIMEOUT_MS = 5_000;

interface LmStudioModelsResponse {
  readonly data: ReadonlyArray<{
    readonly id: string;
  }>;
}

function isLmStudioModelsResponse(value: unknown): value is LmStudioModelsResponse {
  if (typeof value !== 'object' || value === null) return false;
  const data = (value as { data?: unknown }).data;
  if (!Array.isArray(data)) return false;
  return data.every((m) => {
    if (typeof m !== 'object' || m === null) return false;
    const obj = m as Record<string, unknown>;
    return typeof obj['id'] === 'string';
  });
}

function toModelInfo(raw: LmStudioModelsResponse['data'][number]): LmStudioModelInfo {
  return { id: raw.id };
}

function isHttpUrl(raw: string): boolean {
  try {
    const parsed = new URL(raw);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:';
  } catch {
    return false;
  }
}

function joinModelsPath(endpoint: string): string {
  const trimmed = endpoint.endsWith('/') ? endpoint.slice(0, -1) : endpoint;
  return `${trimmed}/v1/models`;
}

export function registerLmStudioHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  fetchFn: typeof fetch,
  logger: Logger,
): () => void {
  ipcMain.handle(
    CHANNEL.llmListLmStudioModels,
    async (_event, req: unknown): Promise<LlmListLmStudioModelsResponse> => {
      const r = req as Partial<LlmListLmStudioModelsRequest>;
      const endpoint = typeof r?.endpoint === 'string' ? r.endpoint : '';

      if (!isHttpUrl(endpoint)) {
        logger.warn('lm-studio: rejected invalid endpoint', { endpoint });
        const err = new OllamaUnreachableError(
          endpoint,
          `Endpoint must be an absolute http or https URL: "${endpoint}"`,
        );
        const serialized = serializeError(err);
        throw Object.assign(new Error(serialized.message), { code: serialized.code });
      }

      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), LIST_TIMEOUT_MS);

      let response: Response;
      try {
        response = await fetchFn(joinModelsPath(endpoint), {
          method: 'GET',
          signal: controller.signal,
        });
      } catch (cause: unknown) {
        clearTimeout(timer);
        logger.warn('lm-studio: fetch failed', { endpoint, err: String(cause) });
        const err = new OllamaUnreachableError(endpoint, undefined, { cause });
        const serialized = serializeError(err);
        throw Object.assign(new Error(serialized.message), { code: serialized.code });
      }
      clearTimeout(timer);

      if (!response.ok) {
        logger.warn('lm-studio: non-2xx response', {
          endpoint,
          status: response.status,
        });
        const err = new OllamaUnreachableError(
          endpoint,
          `LM Studio returned HTTP ${response.status}`,
        );
        const serialized = serializeError(err);
        throw Object.assign(new Error(serialized.message), { code: serialized.code });
      }

      let body: unknown;
      try {
        body = await response.json();
      } catch (cause: unknown) {
        logger.warn('lm-studio: response body was not valid JSON', { endpoint });
        const err = new OllamaUnreachableError(
          endpoint,
          'LM Studio response body was not valid JSON',
          { cause },
        );
        const serialized = serializeError(err);
        throw Object.assign(new Error(serialized.message), { code: serialized.code });
      }

      if (!isLmStudioModelsResponse(body)) {
        logger.warn('lm-studio: response shape did not match /v1/models', { endpoint });
        const err = new OllamaUnreachableError(
          endpoint,
          'Response did not match the OpenAI-compatible /v1/models shape',
        );
        const serialized = serializeError(err);
        throw Object.assign(new Error(serialized.message), { code: serialized.code });
      }

      return { models: body.data.map(toModelInfo) };
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.llmListLmStudioModels);
  };
}
