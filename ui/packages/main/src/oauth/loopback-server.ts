import { createServer } from 'node:http';
import { createServer as createNetServer } from 'node:net';
import type { Server, IncomingMessage, ServerResponse, RequestListener } from 'node:http';
import {
  OAuthUserCancelledError,
  OAuthStateMismatchError,
  OAuthLoopbackPortInUseError,
  DiscordTokenExchangeError,
} from '@scout/shared';
import type { Logger } from '../util/logger.js';

export interface LoopbackResult {
  code: string;
  state: string;
}

export interface LoopbackOptions {
  expectedState: string;
  port: number;
  hostname: '127.0.0.1';
  timeoutMs: number;
  logger: Logger;
  httpFactory?: (listener: RequestListener) => Server;
}

const HTML_SUCCESS = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>Scout</title></head><body><p>Authorization complete. Return to Scout Desktop.</p></body></html>`;
const HTML_REJECTED = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>Scout</title></head><body><p>Authorization rejected by Scout for security reasons. You may close this window.</p></body></html>`;
const HTML_NOT_FOUND = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>Scout</title></head><body><p>Not found.</p></body></html>`;

type Outcome = { kind: 'resolve'; value: LoopbackResult } | { kind: 'reject'; error: Error };

export async function awaitCallback(opts: LoopbackOptions): Promise<LoopbackResult> {
  const { expectedState, port, hostname, timeoutMs, logger } = opts;
  const factory = opts.httpFactory ?? ((listener) => createServer(listener));

  return new Promise<LoopbackResult>((resolve, reject) => {
    let settled = false;
    let outcome: Outcome | null = null;

    // settle() stores the outcome and closes the server.
    // resolve/reject are called from the server 'close' callback so they
    // fire outside the HTTP request listener's call stack — this avoids
    // Bun's unhandled-rejection detection triggering on errors that are
    // legitimately caught by the Promise chain.
    function settle(o: Outcome): void {
      if (settled) return;
      settled = true;
      outcome = o;
      clearTimeout(timeoutHandle);
      server.close(() => {
        const o2 = outcome;
        if (o2 === null) return;
        if (o2.kind === 'resolve') {
          resolve(o2.value);
        } else {
          reject(o2.error);
        }
      });
    }

    const server = factory((req: IncomingMessage, res: ServerResponse) => {
      const raw = req.url ?? '/';
      const parsed = new URL(raw, `http://${hostname}:${port}`);

      if (parsed.pathname !== '/callback') {
        res.writeHead(404, { 'Content-Type': 'text/html' });
        res.end(HTML_NOT_FOUND);
        logger.warn('loopback: unexpected path', { path: parsed.pathname });
        return;
      }

      const code = parsed.searchParams.get('code');
      const state = parsed.searchParams.get('state');
      const error = parsed.searchParams.get('error');
      const errorDescription = parsed.searchParams.get('error_description') ?? 'Unknown error';

      if (error !== null) {
        res.writeHead(200, { 'Content-Type': 'text/html' });
        res.end(HTML_REJECTED);
        const err =
          error === 'access_denied'
            ? new OAuthUserCancelledError('User denied the Discord authorization')
            : new DiscordTokenExchangeError(error, errorDescription);
        settle({ kind: 'reject', error: err });
        return;
      }

      if (state !== expectedState) {
        res.writeHead(400, { 'Content-Type': 'text/html' });
        res.end(HTML_REJECTED);
        logger.warn('loopback: state mismatch');
        settle({ kind: 'reject', error: new OAuthStateMismatchError() });
        return;
      }

      if (code === null || code.length === 0) {
        res.writeHead(400, { 'Content-Type': 'text/html' });
        res.end(HTML_REJECTED);
        settle({
          kind: 'reject',
          error: new DiscordTokenExchangeError('missing_code', 'No code in callback'),
        });
        return;
      }

      res.writeHead(200, { 'Content-Type': 'text/html' });
      res.end(HTML_SUCCESS);
      settle({ kind: 'resolve', value: { code, state } });
    });

    server.on('error', (err) => {
      settle({ kind: 'reject', error: err instanceof Error ? err : new Error(String(err)) });
    });

    server.listen(port, hostname, () => {
      logger.info('loopback: listening', { port, hostname });
    });

    // After timeoutMs of silence, treat as user cancellation.
    const timeoutHandle = setTimeout(() => {
      logger.warn('loopback: timeout reached', { timeoutMs });
      settle({
        kind: 'reject',
        error: new OAuthUserCancelledError('OAuth flow timed out waiting for callback'),
      });
    }, timeoutMs);
  });
}

async function probePort(port: number): Promise<boolean> {
  return new Promise<boolean>((resolve) => {
    const srv = createNetServer();
    srv.once('error', () => resolve(false));
    srv.once('listening', () => {
      srv.close(() => resolve(true));
    });
    srv.listen(port, '127.0.0.1');
  });
}

export async function findFreePort(
  preferred: number,
  maxFallbacks: number,
  probe: (port: number) => Promise<boolean> = probePort,
): Promise<number> {
  for (let i = 0; i <= maxFallbacks; i++) {
    const candidate = preferred + i;
    const free = await probe(candidate);
    if (free) return candidate;
  }
  throw new OAuthLoopbackPortInUseError(
    `All ports ${preferred}–${preferred + maxFallbacks} are in use`,
  );
}
