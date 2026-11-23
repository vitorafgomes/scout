import { createHash, timingSafeEqual } from 'node:crypto';
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import type { Logger } from '../util/logger.js';
import type { McpActivityRow, McpListenerState } from '@scout/shared';

/** The protocol versions the server speaks, newest first. */
export const MCP_PROTOCOL_VERSIONS = ['2025-06-18', '2025-03-26', '2024-11-05'] as const;

/** The most bytes a request body may have. */
export const MCP_MAX_BODY_BYTES = 64 * 1024;

const LOOPBACK = '127.0.0.1';

/** A tool as `tools/list` describes it. */
export interface McpToolDescription {
  readonly name: string;
  readonly description: string;
  readonly inputSchema: Readonly<Record<string, unknown>>;
}

/** What `tools/call` answers: the protocol's content blocks, and whether the tool refused. */
export interface McpToolResult {
  readonly content: readonly { readonly type: 'text'; readonly text: string }[];
  readonly isError?: boolean;
}

/** The tools the server offers; the server only routes calls to them. */
export interface McpTools {
  list(): readonly McpToolDescription[];
  call(name: string, args: Readonly<Record<string, unknown>>): Promise<McpToolResult>;
}

export interface McpServerDeps {
  settings: {
    port(): number;
    /** The SHA-256 of the token, or null when none exists, so every request is refused. */
    tokenHash(): string | null;
    setPort(port: number): Promise<void>;
  };
  tools: McpTools;
  /** Where a refused request is listed, with its status only. */
  activity?: { record(row: McpActivityRow): void };
  /** The app's version, which `initialize` names. */
  version: string;
  logger: Logger;
}

export type McpServerState = McpListenerState;

export interface McpServer {
  state(): McpServerState;
  /** Where it listens, or null while it does not. */
  address(): { address: string; port: number } | null;
  /** Listens on the settings' port; a port in use ends as `failed`, naming it, with nothing listening. */
  start(): Promise<McpServerState>;
  /** Listens on a port the system gives, and keeps it in the settings. */
  pickAnotherPort(): Promise<McpServerState>;
  stop(): Promise<void>;
}

interface RpcRequest {
  readonly id: string | number | null | undefined;
  readonly method: string;
  readonly params?: unknown;
}

/**
 * The MCP server: HTTP on 127.0.0.1 only, answering `POST /mcp` with JSON-RPC 2.0 for `initialize`, `ping`,
 * `tools/list`, `tools/call` and notifications. A request from a web page (any `Origin` header), to another host
 * name, with no token or another token, or with a body over 64 KiB is refused before it is read as JSON.
 */
export function createMcpServer(deps: McpServerDeps): McpServer {
  const { settings, tools, version, logger } = deps;
  let server: Server | null = null;
  let current: McpServerState = { state: 'off' };

  const listen = (port: number) =>
    new Promise<McpServerState>((resolve) => {
      const next = createServer((req, res) => {
        handle(req, res).catch((err: unknown) => failed(res, err));
      });
      next.once('error', (err: NodeJS.ErrnoException) => {
        next.close();
        const reason =
          err.code === 'EADDRINUSE'
            ? `Port ${port} is in use.`
            : `The server could not listen on port ${port}.`;
        logger.warn('mcp: the server did not start', { port, code: err.code });
        current = { state: 'failed', port, reason };
        resolve(current);
      });
      next.listen(port, LOOPBACK, () => {
        server = next;
        const bound = (next.address() as { port: number }).port;
        current = { state: 'listening', port: bound };
        logger.info('mcp: the server listens', { port: bound });
        resolve(current);
      });
    });

  async function stop(): Promise<void> {
    const running = server;
    server = null;
    current = { state: 'off' };
    if (running !== null) {
      running.closeAllConnections();
      await new Promise<void>((resolve) => running.close(() => resolve()));
    }
  }

  /**
   * Answers a request whose handling failed outside a tool: a JSON-RPC internal error when nothing was sent yet,
   * else the connection is closed. Only the error's kind is logged, never its message, which could carry request data.
   */
  function failed(res: ServerResponse, err: unknown): void {
    logger.warn('mcp: a request failed', { kind: errorKind(err) });
    if (res.headersSent) {
      res.destroy();
      return;
    }
    reply(res, rpcError(null, -32603, MCP_INTERNAL_ERROR));
  }

  async function handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const refuse = (status: number, why: string) => {
      logger.info('mcp: a request was refused', { status, why });
      deps.activity?.record({
        at: Date.now(),
        tool: 'request',
        target: '',
        result: `refused ${status}`,
      });
      res.writeHead(status, { 'content-type': 'text/plain; charset=utf-8', connection: 'close' });
      res.end(why);
      req.resume();
    };
    const path = (req.url ?? '').split('?')[0];
    if (path !== '/mcp') return refuse(404, 'Not found.');
    if (req.method !== 'POST') return refuse(405, 'Only POST is answered.');
    if (req.headers.origin !== undefined)
      return refuse(403, 'A request from a web page is refused.');
    const port = current.state === 'listening' ? current.port : -1;
    if (req.headers.host !== `${LOOPBACK}:${port}` && req.headers.host !== `localhost:${port}`) {
      return refuse(403, 'A request to another host name is refused.');
    }
    if (!authorised(req.headers.authorization, settings.tokenHash()))
      return refuse(401, 'The token is missing or wrong.');
    if (Number(req.headers['content-length'] ?? 0) > MCP_MAX_BODY_BYTES)
      return refuse(413, 'The request is over 64 KiB.');

    const body = await readBody(req);
    if (body === null) return refuse(413, 'The request is over 64 KiB.');
    let message: unknown;
    try {
      message = JSON.parse(body);
    } catch {
      return reply(res, rpcError(null, -32700, 'The request is not JSON.'));
    }
    if (Array.isArray(message))
      return reply(res, rpcError(null, -32600, 'A batch is not answered.'));
    const call = asRequest(message);
    if (call === null)
      return reply(res, rpcError(null, -32600, 'The request is not a JSON-RPC 2.0 request.'));
    if (call.id === undefined) {
      res.writeHead(202);
      res.end();
      return;
    }
    reply(res, await answer(call));
  }

  async function answer(call: RpcRequest): Promise<unknown> {
    const id = call.id ?? null;
    const params =
      typeof call.params === 'object' && call.params !== null
        ? (call.params as Record<string, unknown>)
        : {};
    switch (call.method) {
      case 'initialize': {
        const asked = params['protocolVersion'];
        const known = MCP_PROTOCOL_VERSIONS.find((v) => v === asked) ?? MCP_PROTOCOL_VERSIONS[0];
        return rpcResult(id, {
          protocolVersion: known,
          capabilities: { tools: {} },
          serverInfo: { name: 'scout', version },
        });
      }
      case 'ping':
        return rpcResult(id, {});
      case 'tools/list':
        return rpcResult(id, { tools: tools.list() });
      case 'tools/call': {
        const name = params['name'];
        const args = params['arguments'] ?? {};
        if (
          typeof name !== 'string' ||
          typeof args !== 'object' ||
          args === null ||
          Array.isArray(args)
        ) {
          return rpcError(id, -32602, 'tools/call needs a tool name and an object of arguments.');
        }
        try {
          return rpcResult(id, await tools.call(name, args as Record<string, unknown>));
        } catch (err) {
          logger.warn('mcp: a tool failed', { tool: name, kind: errorKind(err) });
          deps.activity?.record({ at: Date.now(), tool: name, target: '', result: 'failed' });
          return rpcError(id, -32603, MCP_INTERNAL_ERROR);
        }
      }
      default:
        return rpcError(id, -32601, `The method ${call.method} is not offered.`);
    }
  }

  return {
    state: () => current,
    address: () => {
      const bound = server?.address();
      return bound && typeof bound === 'object'
        ? { address: bound.address, port: bound.port }
        : null;
    },
    async start() {
      if (server !== null) return current;
      return listen(settings.port());
    },
    async pickAnotherPort() {
      await stop();
      const state = await listen(0);
      if (state.state === 'listening') await settings.setPort(state.port);
      return state;
    },
    stop,
  };
}

/** What a request or a tool that failed is answered, with no detail of the error. */
export const MCP_INTERNAL_ERROR = 'Scout could not answer this request.';

/** The kind of an error, its constructor's name, logged in place of its message. */
function errorKind(err: unknown): string {
  return err instanceof Error ? err.name : typeof err;
}

/** True when the header carries a bearer token whose SHA-256 is `hash`, compared in constant time; never with no hash. */
function authorised(header: string | undefined, hash: string | null): boolean {
  if (hash === null || header === undefined || !header.startsWith('Bearer ')) return false;
  const given = createHash('sha256').update(header.slice('Bearer '.length)).digest();
  const kept = Buffer.from(hash, 'hex');
  return kept.length === given.length && timingSafeEqual(given, kept);
}

/** The body as text, or null once it passes 64 KiB. */
function readBody(req: IncomingMessage): Promise<string | null> {
  return new Promise((resolve) => {
    const chunks: Buffer[] = [];
    let size = 0;
    let over = false;
    req.on('data', (chunk: Buffer) => {
      size += chunk.length;
      if (size > MCP_MAX_BODY_BYTES) over = true;
      else if (!over) chunks.push(chunk);
    });
    req.on('end', () => resolve(over ? null : Buffer.concat(chunks).toString('utf8')));
    req.on('error', () => resolve(null));
  });
}

function asRequest(message: unknown): RpcRequest | null {
  if (typeof message !== 'object' || message === null) return null;
  const m = message as Record<string, unknown>;
  if (m['jsonrpc'] !== '2.0' || typeof m['method'] !== 'string') return null;
  const id = m['id'];
  if (id !== undefined && id !== null && typeof id !== 'string' && typeof id !== 'number')
    return null;
  return { id: id as RpcRequest['id'], method: m['method'], params: m['params'] };
}

function rpcResult(id: RpcRequest['id'], result: unknown) {
  return { jsonrpc: '2.0', id, result };
}

function rpcError(id: RpcRequest['id'] | null, code: number, message: string) {
  return { jsonrpc: '2.0', id, error: { code, message } };
}

function reply(res: ServerResponse, body: unknown): void {
  res.writeHead(200, { 'content-type': 'application/json' });
  res.end(JSON.stringify(body));
}
