import type { IpcMain } from 'electron';
import {
  CHANNEL,
  OllamaUnreachableError,
  serializeError,
  type LlmListOllamaModelsRequest,
  type LlmListOllamaModelsResponse,
  type OllamaModelInfo,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';

const LIST_TIMEOUT_MS = 5_000;

interface OllamaTagsResponse {
  readonly models: ReadonlyArray<{
    readonly name: string;
    readonly modified_at: string;
    readonly size: number;
  }>;
}

function isOllamaTagsResponse(value: unknown): value is OllamaTagsResponse {
  if (typeof value !== 'object' || value === null) return false;
  const models = (value as { models?: unknown }).models;
  if (!Array.isArray(models)) return false;
  return models.every((m) => {
    if (typeof m !== 'object' || m === null) return false;
    const obj = m as Record<string, unknown>;
    return (
      typeof obj['name'] === 'string' &&
      typeof obj['modified_at'] === 'string' &&
      typeof obj['size'] === 'number'
    );
  });
}

function toModelInfo(raw: OllamaTagsResponse['models'][number]): OllamaModelInfo {
  return {
    name: raw.name,
    modifiedAt: raw.modified_at,
    sizeBytes: raw.size,
  };
}

function isHttpUrl(raw: string): boolean {
  try {
    const parsed = new URL(raw);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:';
  } catch {
    return false;
  }
}

function joinTagsPath(endpoint: string): string {
  const trimmed = endpoint.endsWith('/') ? endpoint.slice(0, -1) : endpoint;
  return `${trimmed}/api/tags`;
}

export function registerLlmHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  fetchFn: typeof fetch,
  logger: Logger,
): () => void {
  ipcMain.handle(
    CHANNEL.llmListOllamaModels,
    async (_event, req: unknown): Promise<LlmListOllamaModelsResponse> => {
      const r = req as Partial<LlmListOllamaModelsRequest>;
      const endpoint = typeof r?.endpoint === 'string' ? r.endpoint : '';

      if (!isHttpUrl(endpoint)) {
        logger.warn('llm: rejected invalid endpoint', { endpoint });
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
        response = await fetchFn(joinTagsPath(endpoint), {
          method: 'GET',
          signal: controller.signal,
        });
      } catch (cause: unknown) {
        clearTimeout(timer);
        logger.warn('llm: fetch failed', { endpoint, err: String(cause) });
        const err = new OllamaUnreachableError(endpoint, undefined, { cause });
        const serialized = serializeError(err);
        throw Object.assign(new Error(serialized.message), { code: serialized.code });
      }
      clearTimeout(timer);

      if (!response.ok) {
        logger.warn('llm: non-2xx response from Ollama', {
          endpoint,
          status: response.status,
        });
        const err = new OllamaUnreachableError(endpoint, `Ollama returned HTTP ${response.status}`);
        const serialized = serializeError(err);
        throw Object.assign(new Error(serialized.message), { code: serialized.code });
      }

      let body: unknown;
      try {
        body = await response.json();
      } catch (cause: unknown) {
        logger.warn('llm: response body was not valid JSON', { endpoint });
        const err = new OllamaUnreachableError(
          endpoint,
          'Ollama response body was not valid JSON',
          { cause },
        );
        const serialized = serializeError(err);
        throw Object.assign(new Error(serialized.message), { code: serialized.code });
      }

      if (!isOllamaTagsResponse(body)) {
        logger.warn('llm: response shape did not match Ollama /api/tags', { endpoint });
        const err = new OllamaUnreachableError(
          endpoint,
          'Response did not match the Ollama /api/tags shape',
        );
        const serialized = serializeError(err);
        throw Object.assign(new Error(serialized.message), { code: serialized.code });
      }

      return { models: body.models.map(toModelInfo) };
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.llmListOllamaModels);
  };
}
