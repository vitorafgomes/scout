import type { IpcMain, WebContents } from 'electron';
import {
  CHANNEL,
  OLLAMA_MODEL_NAME_RULE,
  OLLAMA_MODEL_NOT_FOUND,
  OLLAMA_PULL_CANCELLED,
  OLLAMA_PULL_FAILED,
  OllamaUnreachableError,
  encodeIpcErrorMessage,
  isOllamaModelName,
  serializeError,
  type LlmCancelOllamaPullRequest,
  type LlmDeleteOllamaModelRequest,
  type LlmDeleteOllamaModelResponse,
  type LlmPullOllamaModelRequest,
  type LlmPullOllamaModelResponse,
  type LlmPullOllamaProgressEvent,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';

/** How long a pull may go without a byte from Ollama, while it reports byte progress, before it stops. */
export const OLLAMA_PULL_STALL_MS = 60_000;

/** How long a pull may stay in a phase Ollama reports only by status (manifest, digest check) before it stops. */
export const OLLAMA_PULL_STATUS_STALL_MS = 600_000;

function spoken(ms: number): string {
  if (ms <= 60_000) return `${Math.round(ms / 1000)} s`;
  const minutes = Math.round(ms / 60_000);
  return `${minutes} minute${minutes === 1 ? '' : 's'}`;
}

function isHttpUrl(raw: string): boolean {
  try {
    const parsed = new URL(raw);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:';
  } catch {
    return false;
  }
}

const THIS_COMPUTER: ReadonlySet<string> = new Set(['localhost', '127.0.0.1', '[::1]']);

function isLocalOllama(raw: string): boolean {
  return isHttpUrl(raw) && THIS_COMPUTER.has(new URL(raw).hostname);
}

function joinPath(endpoint: string, suffix: string): string {
  const trimmed = endpoint.endsWith('/') ? endpoint.slice(0, -1) : endpoint;
  return `${trimmed}${suffix}`;
}

/** A pull's rejection: its code crosses IPC in the message, so the window tells cancelled, not found and the rest apart. */
function throwPull(code: string, message: string): never {
  throw Object.assign(new Error(encodeIpcErrorMessage({ __scoutError: true, code, message })), {
    code,
  });
}

function throwCancelled(): never {
  throwPull(OLLAMA_PULL_CANCELLED, 'The download was cancelled.');
}

/** A failure in Ollama's own words, as not found when it says its library has no such model. */
function throwPullFailed(message: string): never {
  throwPull(
    /manifest: file does not exist/i.test(message) ? OLLAMA_MODEL_NOT_FOUND : OLLAMA_PULL_FAILED,
    message,
  );
}

function throwOllamaError(endpoint: string, message?: string, cause?: unknown): never {
  const err = new OllamaUnreachableError(
    endpoint,
    message,
    cause === undefined ? undefined : { cause },
  );
  const serialized = serializeError(err);
  throw Object.assign(new Error(serialized.message), { code: serialized.code });
}

function throwUnreachable(endpoint: string, cause: unknown): never {
  const serialized = serializeError(new OllamaUnreachableError(endpoint, undefined, { cause }));
  throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), { code: serialized.code });
}

interface PullLine {
  readonly status?: string;
  readonly error?: string;
  readonly total?: number;
  readonly completed?: number;
}

function isPullLine(value: unknown): value is PullLine {
  if (typeof value !== 'object' || value === null) return false;
  const obj = value as Record<string, unknown>;
  return obj['status'] === undefined || typeof obj['status'] === 'string';
}

/**
 * Pulls and deletes Ollama models through the local Ollama server. A pull asks only an Ollama on this computer for a
 * model name of the library (`isOllamaModelName`), refusing anything else before any request; it reports its progress
 * to the window that asked, ends only on Ollama's success line, and stops when the owner cancels it, when Ollama sends
 * nothing for `stallMs` while it reports download progress, or when a phase it reports only by status (pulling the
 * manifest, verifying the digest, writing the manifest) lasts longer than `statusStallMs`. It rejects with
 * `llm/ollama-unreachable` when Ollama does not answer, `OLLAMA_MODEL_NOT_FOUND` when Ollama says its library has no
 * such model, `OLLAMA_PULL_CANCELLED` after Cancel and `OLLAMA_PULL_FAILED`, with Ollama's or main's reason, otherwise.
 */
export function registerLlmPullHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  fetchFn: typeof fetch,
  logger: Logger,
  stallMs: number = OLLAMA_PULL_STALL_MS,
  statusStallMs: number = OLLAMA_PULL_STATUS_STALL_MS,
): () => void {
  const running = new Map<string, () => void>();

  ipcMain.handle(
    CHANNEL.llmPullOllamaModel,
    async (event, req: unknown): Promise<LlmPullOllamaModelResponse> => {
      const r = req as Partial<LlmPullOllamaModelRequest>;
      const endpoint = typeof r?.endpoint === 'string' ? r.endpoint : '';
      const tag = typeof r?.tag === 'string' ? r.tag : '';
      const requestId = typeof r?.requestId === 'string' ? r.requestId : '';

      if (!isLocalOllama(endpoint)) {
        logger.warn('llm-pull: refused an endpoint that is not an Ollama on this computer');
        throwPull(OLLAMA_PULL_FAILED, 'Scout pulls models only from the Ollama on this computer.');
      }
      if (!isOllamaModelName(tag)) {
        logger.warn('llm-pull: refused a name that is not a model name');
        throwPull(OLLAMA_PULL_FAILED, OLLAMA_MODEL_NAME_RULE);
      }
      if (requestId.length === 0) {
        throwPull(OLLAMA_PULL_FAILED, 'requestId must be a non-empty string');
      }

      const sender: WebContents = event.sender;
      const controller = new AbortController();
      let reader: ReadableStreamDefaultReader<Uint8Array> | null = null;
      let stopped: string | null = null;
      let cancelled = false;
      let stallTimer: ReturnType<typeof setTimeout> | undefined;
      const stop = (reason: string): void => {
        if (stopped !== null) return;
        stopped = reason;
        controller.abort();
        void reader?.cancel().catch(() => undefined);
      };
      const throwStopped = (reason: string): never => {
        if (cancelled) throwCancelled();
        throwPull(OLLAMA_PULL_FAILED, reason);
      };
      const armStall = (): void => {
        clearTimeout(stallTimer);
        stallTimer = setTimeout(
          () => stop(`Ollama sent nothing for ${spoken(stallMs)}; the download stopped.`),
          stallMs,
        );
      };
      const armStatusStall = (): void => {
        clearTimeout(stallTimer);
        stallTimer = setTimeout(
          () =>
            stop(`Ollama reported no progress for ${spoken(statusStallMs)}; the download stopped.`),
          statusStallMs,
        );
      };
      running.set(requestId, () => {
        if (stopped === null) cancelled = true;
        stop('The download was cancelled.');
      });

      try {
        armStall();
        let response: Response;
        try {
          response = await fetchFn(joinPath(endpoint, '/api/pull'), {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ name: tag, stream: true }),
            signal: controller.signal,
          });
        } catch (cause: unknown) {
          if (stopped !== null) throwStopped(stopped);
          logger.warn('llm-pull: fetch failed', { endpoint, tag, err: String(cause) });
          throwUnreachable(endpoint, cause);
        }

        if (!response.ok) {
          logger.warn('llm-pull: non-2xx response', { endpoint, status: response.status });
          const said = await response.json().then(
            (answer: unknown) =>
              typeof (answer as { error?: unknown } | null)?.error === 'string'
                ? (answer as { error: string }).error
                : null,
            () => null,
          );
          throwPullFailed(said ?? `Ollama returned HTTP ${response.status}`);
        }

        const body = response.body;
        if (body === null) {
          throwPullFailed('Ollama response had no body for /api/pull');
        }

        reader = body.getReader();
        if (stopped !== null) void reader.cancel().catch(() => undefined);
        const decoder = new TextDecoder();
        let buffer = '';
        let lastStatus = '';
        let lastTotal = 0;
        let lastCompleted = 0;

        const emit = (status: string, completed: number, total: number): void => {
          const percent =
            total > 0
              ? Math.min(100, Math.round((completed / total) * 100))
              : status === 'success'
                ? 100
                : 0;
          const payload: LlmPullOllamaProgressEvent = {
            requestId,
            tag,
            status,
            percent,
            completedBytes: completed,
            totalBytes: total,
          };
          if (!sender.isDestroyed()) {
            sender.send(CHANNEL.llmPullOllamaProgress, payload);
          }
        };

        const readLine = (raw: string): 'success' | { error: string } | null => {
          const line = raw.trim();
          if (line.length === 0) return null;
          let parsed: unknown;
          try {
            parsed = JSON.parse(line);
          } catch {
            return null;
          }
          if (!isPullLine(parsed)) return null;
          if (parsed.error !== undefined && parsed.error !== null) return { error: parsed.error };
          lastStatus = parsed.status ?? lastStatus;
          const bytes = typeof parsed.total === 'number' || typeof parsed.completed === 'number';
          if (bytes) armStall();
          else armStatusStall();
          if (typeof parsed.total === 'number') lastTotal = parsed.total;
          if (typeof parsed.completed === 'number') lastCompleted = parsed.completed;
          emit(lastStatus, lastCompleted, lastTotal);
          return lastStatus === 'success' ? 'success' : null;
        };

        let outcome: 'success' | { error: string } | null = null;
        try {
          while (outcome === null) {
            const { done, value } = await reader.read();
            if (done) break;
            buffer += decoder.decode(value, { stream: true });
            let nl = buffer.indexOf('\n');
            while (nl >= 0 && outcome === null) {
              outcome = readLine(buffer.slice(0, nl));
              buffer = buffer.slice(nl + 1);
              nl = buffer.indexOf('\n');
            }
          }
        } catch (cause: unknown) {
          if (stopped !== null) throwStopped(stopped);
          logger.warn('llm-pull: the stream broke off', { tag, err: String(cause) });
          throwPullFailed(`the download from Ollama broke off (${String(cause)})`);
        }

        if (stopped !== null) throwStopped(stopped);
        if (outcome === null) outcome = readLine(buffer + decoder.decode());
        if (outcome === 'success') return { tag };
        if (outcome !== null) {
          logger.warn('llm-pull: ollama returned error line', { tag, error: outcome.error });
          throwPullFailed(outcome.error);
        }
        logger.warn('llm-pull: the stream ended without success', { tag, lastStatus });
        throwPullFailed('Ollama ended the download before it finished.');
      } finally {
        clearTimeout(stallTimer);
        running.delete(requestId);
        try {
          reader?.releaseLock();
        } catch (err: unknown) {
          logger.debug('llm-pull: the reader lock was not released', { err: String(err) });
        }
      }
    },
  );

  ipcMain.handle(CHANNEL.llmCancelOllamaPull, (_event, req: unknown): void => {
    const r = req as Partial<LlmCancelOllamaPullRequest>;
    if (typeof r?.requestId !== 'string') return;
    running.get(r.requestId)?.();
  });

  ipcMain.handle(
    CHANNEL.llmDeleteOllamaModel,
    async (_event, req: unknown): Promise<LlmDeleteOllamaModelResponse> => {
      const r = req as Partial<LlmDeleteOllamaModelRequest>;
      const endpoint = typeof r?.endpoint === 'string' ? r.endpoint : '';
      const tag = typeof r?.tag === 'string' ? r.tag : '';

      if (!isHttpUrl(endpoint)) {
        throwOllamaError(endpoint, `Endpoint must be an absolute http or https URL: "${endpoint}"`);
      }
      if (tag.length === 0) {
        throwOllamaError(endpoint, 'tag must be a non-empty string');
      }

      let response: Response;
      try {
        response = await fetchFn(joinPath(endpoint, '/api/delete'), {
          method: 'DELETE',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ name: tag }),
        });
      } catch (cause: unknown) {
        logger.warn('llm-pull: delete fetch failed', { endpoint, tag, err: String(cause) });
        throwOllamaError(endpoint, undefined, cause);
      }

      if (!response.ok && response.status !== 404) {
        logger.warn('llm-pull: delete non-2xx', { endpoint, status: response.status });
        throwOllamaError(endpoint, `Ollama returned HTTP ${response.status} on delete`);
      }

      return { tag };
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.llmPullOllamaModel);
    ipcMain.removeHandler(CHANNEL.llmCancelOllamaPull);
    ipcMain.removeHandler(CHANNEL.llmDeleteOllamaModel);
  };
}
