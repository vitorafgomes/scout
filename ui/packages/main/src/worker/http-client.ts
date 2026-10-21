import {
  WorkerHttpUnavailableError,
  ProviderUnreachableError,
  ProviderUnknownError,
  ProviderLifecycleUnsupportedError,
  ProviderNotManagedError,
  ProviderStartTimeoutError,
  ProviderRequestFailedError,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';

export interface WorkerHttpDeps {
  getEndpoint(): string | null;
  fetch: typeof fetch;
  logger: Logger;
  // Shared secret for the worker's loopback control surface. When present, it is
  // sent on every request via the x-scout-token header.
  token?: string;
}

interface WorkerErrorBody {
  code?: string;
  message?: string;
  id?: string;
}

const DEFAULT_TIMEOUT_MS = 5_000;

function mapErrorCode(code: string, message: string): never {
  switch (code) {
    case 'providers/unreachable':
      throw new ProviderUnreachableError(message);
    case 'providers/unknown':
      throw new ProviderUnknownError(message);
    case 'providers/lifecycle-unsupported':
      throw new ProviderLifecycleUnsupportedError(message);
    case 'providers/not-managed':
      throw new ProviderNotManagedError(message);
    case 'providers/start-timeout':
      throw new ProviderStartTimeoutError(message);
    default:
      throw new ProviderRequestFailedError(message || `Request failed with code: ${code}`);
  }
}

export class WorkerHttpClient {
  constructor(private readonly deps: WorkerHttpDeps) {}

  async get<T>(path: string, opts?: { timeoutMs?: number }): Promise<T> {
    return this.request<T>('GET', path, undefined, opts);
  }

  async post<T>(path: string, body?: unknown, opts?: { timeoutMs?: number }): Promise<T> {
    return this.request<T>('POST', path, body, opts);
  }

  async put<T>(path: string, body?: unknown, opts?: { timeoutMs?: number }): Promise<T> {
    return this.request<T>('PUT', path, body, opts);
  }

  async delete<T>(path: string, opts?: { timeoutMs?: number }): Promise<T> {
    return this.request<T>('DELETE', path, undefined, opts);
  }

  private async request<T>(
    method: string,
    path: string,
    body: unknown,
    opts?: { timeoutMs?: number },
  ): Promise<T> {
    const endpoint = this.deps.getEndpoint();
    if (endpoint === null) {
      throw new WorkerHttpUnavailableError();
    }

    const url = `${endpoint}${path}`;
    const timeoutMs = opts?.timeoutMs ?? DEFAULT_TIMEOUT_MS;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    let response: Response;
    const init: RequestInit = { method, signal: controller.signal };
    const headers: Record<string, string> = {};
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }
    if (this.deps.token !== undefined) {
      headers['x-scout-token'] = this.deps.token;
    }
    if (Object.keys(headers).length > 0) {
      init.headers = headers;
    }
    try {
      response = await this.deps.fetch(url, init);
    } catch (err: unknown) {
      clearTimeout(timer);
      if (err instanceof Error && err.name === 'AbortError') {
        throw new ProviderUnreachableError(`Request to ${url} timed out after ${timeoutMs}ms`);
      }
      throw new ProviderUnreachableError(
        `Could not reach worker at ${url}: ${err instanceof Error ? err.message : String(err)}`,
      );
    } finally {
      clearTimeout(timer);
    }

    if (response.status === 204) {
      return undefined as T;
    }

    if (response.ok) {
      return response.json() as Promise<T>;
    }

    let errorBody: WorkerErrorBody = {};
    try {
      errorBody = (await response.json()) as WorkerErrorBody;
    } catch {
      // body may not be JSON; fall through to generic error
    }

    const code = errorBody.code ?? 'providers/request-failed';
    const message =
      errorBody.message ?? `Worker returned ${String(response.status)} for ${method} ${path}`;

    this.deps.logger.warn('worker-http: non-2xx response', { url, status: response.status, code });
    mapErrorCode(code, message);
  }
}
