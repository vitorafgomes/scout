import path from 'node:path';
import { createServer as createNetServer } from 'node:net';
import { GoogleAuth, type GoogleAuthDeps } from '../src/oauth/google-auth.js';
import { awaitCallback } from '../src/oauth/loopback-server.js';
import type { Logger } from '../src/util/logger.js';

export const TOKEN_FILE = '/user-data/youtube-token.json';
export const CLIENT_FILE = '/user-data/google-oauth-client.json';
export const BUNDLED_FILE = '/app/packages/main/resources/google-client.json';
export const CLIENT_ID = '1234-scout.apps.googleusercontent.com';
export const CLIENT_SECRET = 'GOCSPX-desktop-client-secret';
export const AUTH_CODE = '4/0AauthCodeFromGoogle';
export const API_KEY = 'AIzaSyScoutPastedKey0123456789';
export const AUTHORIZE_URL = 'https://accounts.google.com/o/oauth2/v2/auth';
export const TOKEN_URL = 'https://oauth2.googleapis.com/token';
export const REVOKE_URL = 'https://oauth2.googleapis.com/revoke';
export const CHANNELS_URL = 'https://www.googleapis.com/youtube/v3/channels';
export const ONE_HOUR_MS = 3_599_000;

export interface SentRequest {
  url: string;
  method: string;
  form: Record<string, string>;
  authorization: string | null;
}

type Reply = Record<string, unknown> | Response | Error;

export interface GoogleFake {
  auth: GoogleAuth;
  deps: GoogleAuthDeps;
  requests: SentRequest[];
  logs: string[];
  ops: string[];
  files: Map<string, { data: string; mode: number }>;
  vault: Map<string, string>;
  connectionChanges: number;
  clock: { now: number };
  tokenReplies: Reply[];
  mine: () => Response;
  keyCheck: (url: string) => Response;
  revoke: () => Response;
  authorizeUrls: URL[];
  callbackParams: (authorize: URL) => Record<string, string>;
  browserCallsBack: boolean;
  browserFails: boolean;
  listenerUpWhenBrowserOpened: boolean | null;
  resumeListeners: (() => unknown)[];
}

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

export async function freePort(): Promise<number> {
  return new Promise<number>((resolve, reject) => {
    const srv = createNetServer();
    srv.listen(0, '127.0.0.1', () => {
      const addr = srv.address();
      if (addr !== null && typeof addr === 'object') {
        const p = addr.port;
        srv.close(() => resolve(p));
      } else {
        srv.close(() => reject(new Error('no address')));
      }
    });
    srv.on('error', reject);
  });
}

export function makeGoogleFake(overrides: Partial<GoogleAuthDeps> = {}): GoogleFake {
  const fake = {
    requests: [] as SentRequest[],
    logs: [] as string[],
    ops: [] as string[],
    files: new Map<string, { data: string; mode: number }>([
      [
        CLIENT_FILE,
        {
          data: JSON.stringify({ client_id: CLIENT_ID, client_secret: CLIENT_SECRET }),
          mode: 0o600,
        },
      ],
    ]),
    vault: new Map<string, string>(),
    connectionChanges: 0,
    clock: { now: Date.UTC(2026, 10, 2, 12, 0, 0) },
    tokenReplies: [] as Reply[],
    mine: (): Response =>
      json({ items: [{ id: 'UCscoutTestChannel', snippet: { title: 'Scout Test Channel' } }] }),
    keyCheck: (_url: string): Response => json({ items: [{ id: 'UCBR8-60-B28hp2BmDPdntcQ' }] }),
    revoke: (): Response => new Response(null, { status: 200 }),
    authorizeUrls: [] as URL[],
    callbackParams: (authorize: URL): Record<string, string> => ({
      code: AUTH_CODE,
      state: authorize.searchParams.get('state') ?? '',
    }),
    browserCallsBack: true,
    browserFails: false,
    listenerUpWhenBrowserOpened: null as boolean | null,
    resumeListeners: [] as (() => unknown)[],
  };

  const record =
    (level: string) =>
    (msg: string, ...rest: unknown[]): void => {
      fake.logs.push(`${level} ${msg} ${JSON.stringify(rest)}`);
    };
  const logger: Logger = {
    debug: record('debug'),
    info: record('info'),
    warn: record('warn'),
    error: record('error'),
    child: () => logger,
  };

  const reply = (r: Reply | undefined): Response => {
    if (r === undefined) throw new Error('no scripted reply');
    if (r instanceof Error) throw r;
    if (r instanceof Response) return r;
    return json(r);
  };

  const fetchFake = async (input: unknown, init?: RequestInit): Promise<Response> => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    const form = Object.fromEntries(
      new URLSearchParams(typeof init?.body === 'string' ? init.body : ''),
    );
    fake.requests.push({
      url,
      method,
      form,
      authorization: new Headers(init?.headers).get('authorization'),
    });
    fake.ops.push(`${method} ${url.split('?')[0]}`);
    if (url === TOKEN_URL) {
      const scripted = fake.tokenReplies.shift();
      if (scripted === undefined && form['grant_type'] === 'authorization_code') {
        return json({
          access_token: 'ya29.first-access',
          expires_in: 3599,
          refresh_token: '1//refresh-first',
          scope:
            'https://www.googleapis.com/auth/youtube.readonly https://www.googleapis.com/auth/yt-analytics.readonly',
          token_type: 'Bearer',
        });
      }
      return reply(scripted);
    }
    if (url === REVOKE_URL) return fake.revoke();
    if (url === `${CHANNELS_URL}?part=snippet&mine=true`) return fake.mine();
    if (url.startsWith(`${CHANNELS_URL}?part=id&forHandle=youtube&key=`)) return fake.keyCheck(url);
    throw new Error(`unexpected request to ${url}`);
  };

  const openExternal = async (url: string): Promise<void> => {
    const authorize = new URL(url);
    fake.authorizeUrls.push(authorize);
    if (fake.browserFails) throw new Error('xdg-open: no browser available');
    const redirect = new URL(authorize.searchParams.get('redirect_uri') ?? '');
    try {
      await (await globalThis.fetch(`${redirect.origin}/favicon.ico`)).text();
      fake.listenerUpWhenBrowserOpened = true;
    } catch {
      fake.listenerUpWhenBrowserOpened = false;
    }
    if (!fake.browserCallsBack || fake.listenerUpWhenBrowserOpened !== true) return;
    const query = new URLSearchParams(fake.callbackParams(authorize));
    void globalThis
      .fetch(`${redirect.origin}${redirect.pathname}?${query.toString()}`)
      .then((r) => r.text())
      .catch(() => undefined);
  };

  const deps: GoogleAuthDeps = {
    fetch: fetchFake as unknown as typeof globalThis.fetch,
    awaitCallback,
    findFreePort: async () => freePort(),
    openExternal,
    vault: {
      get: async (key) => fake.vault.get(key) ?? null,
      store: async (key, value) => {
        fake.ops.push(`vault.store ${key}`);
        fake.vault.set(key, value);
      },
      delete: async (key) => {
        fake.ops.push(`vault.delete ${key}`);
        return fake.vault.delete(key);
      },
    },
    tokenFilePath: TOKEN_FILE,
    clientConfigPath: CLIENT_FILE,
    env: {},
    fs: {
      readFile: async (p) => {
        const file = fake.files.get(p);
        if (file === undefined) {
          throw Object.assign(new Error(`ENOENT: no such file, open '${p}'`), { code: 'ENOENT' });
        }
        return file.data;
      },
      writeFile: async (p, data, options) => {
        fake.ops.push(`write ${path.basename(p)} mode=${options.mode.toString(8)}`);
        fake.files.set(p, { data, mode: options.mode });
      },
      rename: async (from, to) => {
        fake.ops.push(`rename ${path.basename(from)} -> ${path.basename(to)}`);
        const file = fake.files.get(from);
        if (file === undefined) throw new Error(`no file ${from}`);
        fake.files.delete(from);
        fake.files.set(to, file);
      },
      rm: async (p) => {
        fake.ops.push(`rm ${path.basename(p)}`);
        fake.files.delete(p);
      },
    },
    now: () => fake.clock.now,
    onConnectionChanged: () => {
      fake.connectionChanges += 1;
    },
    onResume: (listener) => {
      fake.resumeListeners.push(listener);
      return () => {
        fake.resumeListeners.splice(fake.resumeListeners.indexOf(listener), 1);
      };
    },
    logger,
    ...overrides,
  };

  return Object.assign(fake, { deps, auth: new GoogleAuth(deps) });
}

/** Signs in through the fake browser and waits until the background exchange has finished. */
export async function connect(fake: GoogleFake): Promise<void> {
  await fake.auth.signIn();
  await fake.auth.settled();
}

export function tokenFile(fake: GoogleFake): { access_token: string; expires_at: unknown } | null {
  const file = fake.files.get(TOKEN_FILE);
  return file === undefined
    ? null
    : (JSON.parse(file.data) as { access_token: string; expires_at: unknown });
}

export function tokenPosts(fake: GoogleFake): SentRequest[] {
  return fake.requests.filter((r) => r.url === TOKEN_URL);
}
