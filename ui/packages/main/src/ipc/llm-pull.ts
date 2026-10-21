import { spawn } from 'node:child_process';
import { mkdir, stat } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import type { IpcMain, WebContents } from 'electron';
import {
  CHANNEL,
  OllamaUnreachableError,
  serializeError,
  type LlmDeleteOllamaModelRequest,
  type LlmDeleteOllamaModelResponse,
  type LlmPullOllamaModelRequest,
  type LlmPullOllamaModelResponse,
  type LlmPullOllamaProgressEvent,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';

const OLLAMA_DIR = path.join(os.homedir(), '.ollama');
const OLLAMA_KEY_PATH = path.join(OLLAMA_DIR, 'id_ed25519');

async function fileExists(p: string): Promise<boolean> {
  try {
    const s = await stat(p);
    return s.isFile();
  } catch {
    return false;
  }
}

function runCommand(
  cmd: string,
  args: readonly string[],
  options: { cwd?: string } = {},
): Promise<{ code: number | null; stdout: string; stderr: string }> {
  return new Promise((resolve, reject) => {
    const child = spawn(cmd, args, {
      cwd: options.cwd,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk: Buffer) => {
      stdout += chunk.toString('utf8');
    });
    child.stderr.on('data', (chunk: Buffer) => {
      stderr += chunk.toString('utf8');
    });
    child.on('error', (err) => reject(err));
    child.on('close', (code) => resolve({ code, stdout, stderr }));
  });
}

async function ensureOllamaIdentity(logger: Logger): Promise<void> {
  if (await fileExists(OLLAMA_KEY_PATH)) return;

  await mkdir(OLLAMA_DIR, { recursive: true, mode: 0o700 });
  logger.info('llm-pull: generating Ollama ed25519 identity', { path: OLLAMA_KEY_PATH });

  try {
    const res = await runCommand('ssh-keygen', [
      '-t',
      'ed25519',
      '-f',
      OLLAMA_KEY_PATH,
      '-N',
      '',
      '-q',
    ]);
    if (res.code !== 0) {
      throw new Error(
        `ssh-keygen exited with code ${res.code}: ${res.stderr.trim() || res.stdout.trim()}`,
      );
    }
  } catch (cause: unknown) {
    if (cause instanceof Error && 'code' in cause && cause.code === 'ENOENT') {
      throw new Error(
        'ssh-keygen is not available on this machine. Install OpenSSH client (Linux/macOS: openssh-client; Windows 10+: built-in) so Scout can initialise the Ollama identity automatically.',
      );
    }
    throw cause instanceof Error
      ? new Error(`Failed to create Ollama identity at ${OLLAMA_KEY_PATH}: ${cause.message}`)
      : new Error(`Failed to create Ollama identity at ${OLLAMA_KEY_PATH}: ${String(cause)}`);
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

function joinPath(endpoint: string, suffix: string): string {
  const trimmed = endpoint.endsWith('/') ? endpoint.slice(0, -1) : endpoint;
  return `${trimmed}${suffix}`;
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

export function registerLlmPullHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  fetchFn: typeof fetch,
  logger: Logger,
): () => void {
  ipcMain.handle(
    CHANNEL.llmPullOllamaModel,
    async (event, req: unknown): Promise<LlmPullOllamaModelResponse> => {
      const r = req as Partial<LlmPullOllamaModelRequest>;
      const endpoint = typeof r?.endpoint === 'string' ? r.endpoint : '';
      const tag = typeof r?.tag === 'string' ? r.tag : '';
      const requestId = typeof r?.requestId === 'string' ? r.requestId : '';

      if (!isHttpUrl(endpoint)) {
        logger.warn('llm-pull: invalid endpoint', { endpoint });
        throwOllamaError(endpoint, `Endpoint must be an absolute http or https URL: "${endpoint}"`);
      }
      if (tag.length === 0) {
        throwOllamaError(endpoint, 'tag must be a non-empty string');
      }
      if (requestId.length === 0) {
        throwOllamaError(endpoint, 'requestId must be a non-empty string');
      }

      const sender: WebContents = event.sender;

      try {
        await ensureOllamaIdentity(logger);
      } catch (cause: unknown) {
        logger.warn('llm-pull: ensureOllamaIdentity failed', { err: String(cause) });
        throwOllamaError(endpoint, cause instanceof Error ? cause.message : String(cause));
      }

      let response: Response;
      try {
        response = await fetchFn(joinPath(endpoint, '/api/pull'), {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ name: tag, stream: true }),
        });
      } catch (cause: unknown) {
        logger.warn('llm-pull: fetch failed', { endpoint, tag, err: String(cause) });
        throwOllamaError(endpoint, undefined, cause);
      }

      if (!response.ok) {
        logger.warn('llm-pull: non-2xx response', { endpoint, status: response.status });
        throwOllamaError(endpoint, `Ollama returned HTTP ${response.status}`);
      }

      const body = response.body;
      if (body === null) {
        throwOllamaError(endpoint, 'Ollama response had no body for /api/pull');
      }

      const reader = body.getReader();
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

      try {
        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          let nl = buffer.indexOf('\n');
          while (nl >= 0) {
            const line = buffer.slice(0, nl).trim();
            buffer = buffer.slice(nl + 1);
            nl = buffer.indexOf('\n');
            if (line.length === 0) continue;
            let parsed: unknown;
            try {
              parsed = JSON.parse(line);
            } catch {
              continue;
            }
            if (!isPullLine(parsed)) continue;
            if (parsed.error !== undefined && parsed.error !== null) {
              logger.warn('llm-pull: ollama returned error line', { tag, error: parsed.error });
              throwOllamaError(endpoint, parsed.error);
            }
            lastStatus = parsed.status ?? lastStatus;
            if (typeof parsed.total === 'number') lastTotal = parsed.total;
            if (typeof parsed.completed === 'number') lastCompleted = parsed.completed;
            emit(lastStatus, lastCompleted, lastTotal);
            if (lastStatus === 'success') {
              return { tag };
            }
          }
        }
      } finally {
        try {
          reader.releaseLock();
        } catch {
          // ignore
        }
      }

      // Stream ended without an explicit success status — treat as completed if we saw any progress.
      emit('success', lastTotal || lastCompleted, lastTotal || lastCompleted);
      return { tag };
    },
  );

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
    ipcMain.removeHandler(CHANNEL.llmDeleteOllamaModel);
  };
}
