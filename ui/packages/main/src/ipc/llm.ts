import type { IpcMain } from 'electron';
import {
  CHANNEL,
  OLLAMA_EMBEDDING_MODEL,
  OllamaUnreachableError,
  encodeIpcErrorMessage,
  serializeError,
  type LlmListOllamaModelsRequest,
  type LlmListOllamaModelsResponse,
  type LlmUnloadOllamaModelsResponse,
  type OllamaModelInfo,
  type VaultKey,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';

const LIST_TIMEOUT_MS = 5_000;
const SHOW_TIMEOUT_MS = 2_000;
const SHOW_RETRY_AFTER_MS = 60_000;
const UNLOAD_TIMEOUT_MS = 5_000;
const DEFAULT_OLLAMA_ENDPOINT = 'http://localhost:11434';

interface OllamaTagsResponse {
  readonly models: ReadonlyArray<{
    readonly name: string;
    readonly modified_at: string;
    readonly size: number;
    readonly digest?: unknown;
    readonly details?: unknown;
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

function detail(details: unknown, key: string): string | undefined {
  if (typeof details !== 'object' || details === null) return undefined;
  const value = (details as Record<string, unknown>)[key];
  return typeof value === 'string' && value !== '' ? value : undefined;
}

function toModelInfo(
  raw: OllamaTagsResponse['models'][number],
  capabilities: readonly string[] | null,
): OllamaModelInfo {
  const family = detail(raw.details, 'family');
  const parameterSize = detail(raw.details, 'parameter_size');
  const quantization = detail(raw.details, 'quantization_level');
  return {
    name: raw.name,
    modifiedAt: raw.modified_at,
    sizeBytes: raw.size,
    ...(family === undefined ? {} : { family }),
    ...(parameterSize === undefined ? {} : { parameterSize }),
    ...(quantization === undefined ? {} : { quantization }),
    capabilities,
  };
}

async function readCapabilities(
  fetchFn: typeof fetch,
  endpoint: string,
  model: string,
  logger: Logger,
): Promise<readonly string[] | null | undefined> {
  try {
    const response = await fetchFn(joinPath(endpoint, '/api/show'), {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ model }),
      signal: AbortSignal.timeout(SHOW_TIMEOUT_MS),
    });
    if (!response.ok) {
      logger.warn('llm: model details read failed', { model, status: response.status });
      return undefined;
    }
    const capabilities = ((await response.json()) as { capabilities?: unknown }).capabilities;
    return Array.isArray(capabilities) && capabilities.every((c) => typeof c === 'string')
      ? capabilities
      : null;
  } catch (cause: unknown) {
    logger.warn('llm: model details read failed', { model, err: String(cause) });
    return undefined;
  }
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
  return joinPath(endpoint, '/api/tags');
}

function joinPath(endpoint: string, suffix: string): string {
  const trimmed = endpoint.endsWith('/') ? endpoint.slice(0, -1) : endpoint;
  return `${trimmed}${suffix}`;
}

function unreachable(endpoint: string, message?: string, cause?: unknown): Error {
  const serialized = serializeError(
    new OllamaUnreachableError(endpoint, message, cause === undefined ? undefined : { cause }),
  );
  return Object.assign(new Error(encodeIpcErrorMessage(serialized)), { code: serialized.code });
}

function withTag(name: string): string {
  return name.slice(name.lastIndexOf('/') + 1).includes(':') ? name : `${name}:latest`;
}

function isOllamaPsResponse(
  value: unknown,
): value is { readonly models: readonly { readonly name: string }[] } {
  if (typeof value !== 'object' || value === null) return false;
  const models = (value as { models?: unknown }).models;
  return (
    Array.isArray(models) &&
    models.every(
      (m) =>
        typeof m === 'object' && m !== null && typeof (m as { name?: unknown }).name === 'string',
    )
  );
}

/** The chat model, the translation model when it runs on Ollama, and the skill-matching model: the Ollama models Scout itself uses. */
async function scoutOllamaModels(vaultGet: VaultGet): Promise<readonly string[]> {
  const chat = await vaultGet('llm.model');
  const translation =
    (await vaultGet('llm.translation-provider')) === 'Ollama'
      ? await vaultGet('llm.translation-model')
      : null;
  const tags = [chat, translation, OLLAMA_EMBEDDING_MODEL]
    .filter((tag): tag is string => tag !== null && tag.length > 0)
    .map(withTag);
  return [...new Set(tags)];
}

async function readLoadedModels(
  fetchFn: typeof fetch,
  endpoint: string,
  logger: Logger,
): Promise<readonly string[]> {
  let response: Response;
  try {
    response = await fetchFn(joinPath(endpoint, '/api/ps'), {
      method: 'GET',
      signal: AbortSignal.timeout(UNLOAD_TIMEOUT_MS),
    });
  } catch (cause: unknown) {
    logger.warn('llm: loaded-models read failed', { endpoint, err: String(cause) });
    throw unreachable(endpoint, undefined, cause);
  }
  if (!response.ok) {
    logger.warn('llm: non-2xx loaded-models read', { endpoint, status: response.status });
    throw unreachable(endpoint, `Ollama returned HTTP ${response.status}`);
  }
  const body: unknown = await response.json().catch(() => null);
  if (!isOllamaPsResponse(body)) {
    logger.warn('llm: response shape did not match Ollama /api/ps', { endpoint });
    throw unreachable(endpoint, 'Response did not match the Ollama /api/ps shape');
  }
  return body.models.map((m) => m.name);
}

async function unloadModel(
  fetchFn: typeof fetch,
  endpoint: string,
  model: string,
  logger: Logger,
): Promise<boolean> {
  try {
    const response = await fetchFn(joinPath(endpoint, '/api/generate'), {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ model, keep_alive: 0 }),
      signal: AbortSignal.timeout(UNLOAD_TIMEOUT_MS),
    });
    if (response.ok) {
      logger.info('llm: Ollama unloaded a model Scout uses', { model });
      return true;
    }
    logger.warn('llm: Ollama refused to unload a model', { model, status: response.status });
  } catch (cause: unknown) {
    logger.warn('llm: unload request failed', { model, err: String(cause) });
  }
  return false;
}

/** Reads one of main's own vault keys. */
export type VaultGet = (key: VaultKey) => Promise<string | null>;

/**
 * Registers the Ollama model list, a read of any endpoint the window passes that adds each model's capabilities from
 * one `/api/show` (2 s each, kept by digest once read; a failed read leaves them unknown and is not asked again for a
 * minute), and the unload, which takes
 * nothing from the window: it reads the endpoint and Scout's own models from main's vault and asks Ollama to unload only those
 * Ollama reports loaded, so a model another program loaded stays loaded and the server keeps running.
 */
export function registerLlmHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  fetchFn: typeof fetch,
  logger: Logger,
  vaultGet: VaultGet,
  now: () => number = Date.now,
): () => void {
  const capabilitiesByDigest = new Map<string, readonly string[] | null>();
  const failedAtByDigest = new Map<string, number>();

  const withCapabilities = async (
    endpoint: string,
    raw: OllamaTagsResponse['models'][number],
  ): Promise<OllamaModelInfo> => {
    const digest = typeof raw.digest === 'string' && raw.digest !== '' ? raw.digest : null;
    const known = digest === null ? undefined : capabilitiesByDigest.get(digest);
    if (known !== undefined) return toModelInfo(raw, known);
    const failedAt = digest === null ? undefined : failedAtByDigest.get(digest);
    if (failedAt !== undefined && now() - failedAt < SHOW_RETRY_AFTER_MS) {
      return toModelInfo(raw, null);
    }
    const read = await readCapabilities(fetchFn, endpoint, raw.name, logger);
    if (digest !== null) {
      if (read === undefined) failedAtByDigest.set(digest, now());
      else {
        capabilitiesByDigest.set(digest, read);
        failedAtByDigest.delete(digest);
      }
    }
    return toModelInfo(raw, read ?? null);
  };

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

      return {
        models: await Promise.all(body.models.map((raw) => withCapabilities(endpoint, raw))),
      };
    },
  );

  ipcMain.handle(
    CHANNEL.llmUnloadOllamaModels,
    async (): Promise<LlmUnloadOllamaModelsResponse> => {
      const stored = await vaultGet('llm.endpoint');
      const endpoint = stored !== null && isHttpUrl(stored) ? stored : DEFAULT_OLLAMA_ENDPOINT;
      const scout = await scoutOllamaModels(vaultGet);
      const loaded = await readLoadedModels(fetchFn, endpoint, logger);
      const unloaded: string[] = [];
      const refused: string[] = [];
      for (const model of loaded.filter((name) => scout.includes(withTag(name)))) {
        ((await unloadModel(fetchFn, endpoint, model, logger)) ? unloaded : refused).push(model);
      }
      return { unloaded, refused };
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.llmListOllamaModels);
    ipcMain.removeHandler(CHANNEL.llmUnloadOllamaModels);
  };
}
