import { afterEach, describe, expect, it } from 'bun:test';
import { request as httpRequest } from 'node:http';
import { createServer as createNetServer, type Server as NetServer } from 'node:net';
import {
  MCP_INTERNAL_ERROR,
  MCP_PROTOCOL_VERSIONS,
  createMcpServer,
  type McpServer,
  type McpServerDeps,
  type McpTools,
} from '../src/mcp/mcp-server.js';
import { hashToken } from '../src/mcp/mcp-settings.js';
import { McpActivity } from '../src/mcp/mcp-tools.js';
import { recordingLogger } from './kubernetes-fakes.js';

const TOKEN = 'scout_mcp_test-token-of-the-owner';

const tools: McpTools = {
  list: () => [
    {
      name: 'list_reviews',
      description: 'The reviews.',
      inputSchema: { type: 'object', properties: {} },
    },
  ],
  call: async (name, args) => ({
    content: [{ type: 'text', text: `${name} ${JSON.stringify(args)}` }],
  }),
};

const servers: McpServer[] = [];
const blockers: NetServer[] = [];

afterEach(async () => {
  await Promise.all(servers.splice(0).map((s) => s.stop()));
  await Promise.all(
    blockers.splice(0).map((b) => new Promise<void>((resolve) => b.close(() => resolve()))),
  );
});

function make(over: Partial<{ port: number; hash: string | null; tools: McpTools }> = {}) {
  let port = over.port ?? 0;
  let hash = over.hash === undefined ? hashToken(TOKEN) : over.hash;
  const stored: number[] = [];
  const { logger, lines } = recordingLogger();
  const activity = new McpActivity();
  const deps: McpServerDeps = {
    activity,
    settings: {
      port: () => port,
      tokenHash: () => hash,
      setPort: async (value) => {
        stored.push(value);
        port = value;
      },
    },
    tools: over.tools ?? tools,
    version: '1.2.3',
    logger,
  };
  const server = createMcpServer(deps);
  servers.push(server);
  return { server, stored, lines, activity, setHash: (next: string | null) => (hash = next) };
}

async function listening(server: McpServer): Promise<number> {
  const state = await server.start();
  if (state.state !== 'listening') throw new Error(`not listening: ${JSON.stringify(state)}`);
  return state.port;
}

interface Answer {
  status: number;
  body: string;
}

function send(
  port: number,
  init: { method?: string; path?: string; headers?: Record<string, string>; body?: string } = {},
) {
  return new Promise<Answer>((resolve, reject) => {
    const req = httpRequest(
      {
        host: '127.0.0.1',
        port,
        method: init.method ?? 'POST',
        path: init.path ?? '/mcp',
        headers: {
          host: `127.0.0.1:${port}`,
          'content-type': 'application/json',
          authorization: `Bearer ${TOKEN}`,
          ...init.headers,
        },
      },
      (res) => {
        let body = '';
        res.setEncoding('utf8');
        res.on('data', (chunk: string) => (body += chunk));
        res.on('end', () => resolve({ status: res.statusCode ?? 0, body }));
      },
    );
    req.on('error', reject);
    req.end(init.body ?? JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'ping' }));
  });
}

const rpc = (method: string, params?: unknown) =>
  JSON.stringify({ jsonrpc: '2.0', id: 1, method, ...(params === undefined ? {} : { params }) });

function occupied(): Promise<{ port: number; blocker: NetServer }> {
  return new Promise((resolve) => {
    const blocker = createNetServer();
    blocker.listen(0, '127.0.0.1', () => {
      blockers.push(blocker);
      resolve({ port: (blocker.address() as { port: number }).port, blocker });
    });
  });
}

describe('the MCP server', () => {
  it('is off until started, and then listens on this computer only', async () => {
    const { server } = make();

    const before = server.state();
    const port = await listening(server);

    expect(before, 'the server listens on nothing until it is started').toEqual({ state: 'off' });
    expect(server.address(), 'it listens on the loopback address only').toEqual({
      address: '127.0.0.1',
      port,
    });
    await server.stop();
    expect(server.state(), 'stopping it closes the listener').toEqual({ state: 'off' });
  });

  it('answers only POST /mcp', async () => {
    const port = await listening(make().server);

    const get = await send(port, { method: 'GET' });
    const elsewhere = await send(port, { path: '/other' });
    const ok = await send(port);

    expect(
      [get.status, elsewhere.status, ok.status],
      'a GET and another path are refused, POST /mcp answers',
    ).toEqual([405, 404, 200]);
  });

  it('refuses a body over 64 KiB', async () => {
    const port = await listening(make().server);

    const answer = await send(port, {
      body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'ping', pad: 'x'.repeat(65 * 1024) }),
    });

    expect(answer.status, 'a body over 64 KiB is refused before it is read').toBe(413);
  });

  it('refuses a request from a web page or to another host name', async () => {
    const port = await listening(make().server);

    const page = await send(port, { headers: { origin: 'https://example.org' } });
    const host = await send(port, { headers: { host: `scout.example:${port}` } });
    const localhost = await send(port, { headers: { host: `localhost:${port}` } });

    expect(
      [page.status, page.body, host.status, host.body],
      'a request with an Origin header or another Host is refused, each for its own reason',
    ).toEqual([
      403,
      'A request from a web page is refused.',
      403,
      'A request to another host name is refused.',
    ]);
    expect(localhost.status, 'localhost with the port is this computer').toBe(200);
  });

  it('refuses a request with no token, a wrong token, an old token, or when no token exists', async () => {
    const { server, setHash, lines } = make();
    const port = await listening(server);

    const none = await send(port, { headers: { authorization: '' } });
    const wrong = await send(port, {
      headers: { authorization: 'Bearer scout_mcp_not-the-token' },
    });
    setHash(hashToken('scout_mcp_the-new-token'));
    const old = await send(port);
    setHash(null);
    const noToken = await send(port);

    expect([none.status, wrong.status, old.status, noToken.status], 'each is refused 401').toEqual([
      401, 401, 401, 401,
    ]);
    expect(
      lines.some((line) => line.includes(TOKEN.slice('scout_mcp_'.length))),
      'no log line holds the token',
    ).toBe(false);
  });

  it('answers initialize with a protocol version it knows and its name and version', async () => {
    const port = await listening(make().server);

    const known = JSON.parse(
      (await send(port, { body: rpc('initialize', { protocolVersion: '2025-03-26' }) })).body,
    );
    const unknown = JSON.parse(
      (await send(port, { body: rpc('initialize', { protocolVersion: '1999-01-01' }) })).body,
    );

    expect(known.result, 'a protocol version the server knows is answered as it was asked').toEqual(
      {
        protocolVersion: '2025-03-26',
        capabilities: { tools: {} },
        serverInfo: { name: 'scout', version: '1.2.3' },
      },
    );
    expect(
      unknown.result.protocolVersion,
      'an unknown version is answered with the newest the server knows',
    ).toBe(MCP_PROTOCOL_VERSIONS[0]);
  });

  it('answers a notification 202 with no body, ping, tools/list and tools/call through its tools', async () => {
    const port = await listening(make().server);

    const notice = await send(port, {
      body: JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }),
    });
    const ping = JSON.parse((await send(port, { body: rpc('ping') })).body);
    const list = JSON.parse((await send(port, { body: rpc('tools/list') })).body);
    const call = JSON.parse(
      (await send(port, { body: rpc('tools/call', { name: 'list_reviews', arguments: { a: 1 } }) }))
        .body,
    );

    expect([notice.status, notice.body], 'a notification is accepted with no body').toEqual([
      202,
      '',
    ]);
    expect(ping).toEqual({ jsonrpc: '2.0', id: 1, result: {} });
    expect(
      list.result.tools.map((t: { name: string }) => t.name),
      'tools/list answers the tools it was given',
    ).toEqual(['list_reviews']);
    expect(call.result, 'tools/call goes to the named tool with its arguments').toEqual({
      content: [{ type: 'text', text: 'list_reviews {"a":1}' }],
    });
  });

  it('answers a tool that rejects or throws with an internal error, lists it as failed, and keeps serving', async () => {
    const secret = 'https://github.com/acme/shop/pull/12 and the token';
    const failing: McpTools = {
      list: tools.list,
      call: (name) => {
        if (name === 'throws') throw new TypeError(secret);
        return Promise.reject(new Error(secret));
      },
    };
    const unhandled: unknown[] = [];
    const onUnhandled = (reason: unknown) => unhandled.push(reason);
    process.on('unhandledRejection', onUnhandled);
    try {
      const { server, activity, lines } = make({ tools: failing });
      const port = await listening(server);

      const rejected = JSON.parse(
        (await send(port, { body: rpc('tools/call', { name: 'rejects', arguments: {} }) })).body,
      );
      const thrown = JSON.parse(
        (await send(port, { body: rpc('tools/call', { name: 'throws', arguments: {} }) })).body,
      );
      const next = JSON.parse((await send(port, { body: rpc('ping') })).body);
      await new Promise((resolve) => setTimeout(resolve, 20));

      for (const answer of [rejected, thrown]) {
        expect(answer, 'a tool that fails is answered an internal error with no detail').toEqual({
          jsonrpc: '2.0',
          id: 1,
          error: { code: -32603, message: MCP_INTERNAL_ERROR },
        });
      }
      expect(next, 'the next request is served').toEqual({ jsonrpc: '2.0', id: 1, result: {} });
      expect(unhandled, 'no rejection is left unhandled').toEqual([]);
      expect(
        activity.list().map((row) => [row.tool, row.result]),
        'each failed call is listed as failed',
      ).toEqual([
        ['rejects', 'failed'],
        ['throws', 'failed'],
      ]);
      expect(
        JSON.stringify(lines),
        'the log names the tool and the kind of error, never its message',
      ).not.toContain('acme/shop');
    } finally {
      process.off('unhandledRejection', onUnhandled);
    }
  });

  it('answers an unknown method -32601, malformed JSON -32700 and a batch -32600', async () => {
    const port = await listening(make().server);

    const unknown = JSON.parse((await send(port, { body: rpc('resources/list') })).body);
    const malformed = JSON.parse((await send(port, { body: '{"jsonrpc":' })).body);
    const batch = JSON.parse((await send(port, { body: `[${rpc('ping')}]` })).body);

    expect([unknown.error.code, malformed.error.code, batch.error.code]).toEqual([
      -32601, -32700, -32600,
    ]);
    expect([malformed.id, batch.id], 'an error with no request id answers id null').toEqual([
      null,
      null,
    ]);
  });

  it('on a port in use is failed, names the port and listens on nothing; picking another port listens where the system says', async () => {
    const { port: taken } = await occupied();
    const { server, stored } = make({ port: taken });

    const failed = await server.start();
    const nothing = server.address();
    const picked = await server.pickAnotherPort();

    expect(failed, 'the state names the port in use').toEqual({
      state: 'failed',
      port: taken,
      reason: `Port ${taken} is in use.`,
    });
    expect(nothing, 'nothing listens after the failure').toBeNull();
    expect(picked.state).toBe('listening');
    const port = (picked as { port: number }).port;
    expect(port, 'another port is the one the system gave').not.toBe(taken);
    expect(stored, 'the port the system gave is kept').toEqual([port]);
    expect((await send(port)).status).toBe(200);
  });

  it('lists a refused request without any part of its headers', async () => {
    const { server, activity } = make();
    const port = await listening(server);

    await send(port, { headers: { authorization: 'Bearer scout_mcp_guessed-by-a-stranger' } });
    await send(port, { headers: { origin: 'https://example.org' } });

    expect(
      activity.list().map((row) => [row.tool, row.target, row.result]),
      'each refused request is listed with its status only',
    ).toEqual([
      ['request', '', 'refused 401'],
      ['request', '', 'refused 403'],
    ]);
    expect(JSON.stringify(activity.list()), 'no part of a header is kept').not.toContain(
      'guessed-by-a-stranger',
    );
    expect(JSON.stringify(activity.list()), 'no Origin header is kept').not.toContain(
      'example.org',
    );
  });
});
