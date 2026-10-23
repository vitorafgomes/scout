import { GitHubAuth, type GitHubAuthDeps } from '../src/oauth/github-auth.js';
import type { Logger } from '../src/util/logger.js';

export const TOKEN_FILE = '/user-data/github-token.json';
export const DEVICE_CODE = 'device-3584d83530557fdd1f46af8289938c8ef79f9dc5';

export interface SentRequest {
  url: string;
  method: string;
  form: Record<string, string>;
  authorization: string | null;
}

type Reply = Record<string, unknown> | Error;

export interface GitHubFake {
  auth: GitHubAuth;
  deps: GitHubAuthDeps;
  requests: SentRequest[];
  sleeps: number[];
  logs: string[];
  files: Map<string, { data: string; mode: number }>;
  vault: Map<string, string>;
  connectionChanges: number;
  clock: { now: number };
  tokenReplies: Reply[];
  installations: Reply;
  user: () => Response;
  resumeListeners: (() => unknown)[];
}

function json(body: Record<string, unknown>): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

export function makeGitHubFake(overrides: Partial<GitHubAuthDeps> = {}): GitHubFake {
  const fake = {
    requests: [] as SentRequest[],
    sleeps: [] as number[],
    logs: [] as string[],
    files: new Map<string, { data: string; mode: number }>(),
    vault: new Map<string, string>(),
    connectionChanges: 0,
    clock: { now: Date.UTC(2026, 9, 23, 12, 0, 0) },
    tokenReplies: [] as Reply[],
    installations: { total_count: 1 } as Reply,
    user: (): Response => json({ login: 'vitorafgomes' }),
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
    return json(r);
  };

  const fetchFake = async (input: unknown, init?: RequestInit): Promise<Response> => {
    const url = String(input);
    const headers = (init?.headers ?? {}) as Record<string, string>;
    fake.requests.push({
      url,
      method: init?.method ?? 'GET',
      form: Object.fromEntries(
        new URLSearchParams(typeof init?.body === 'string' ? init.body : ''),
      ),
      authorization: headers['Authorization'] ?? null,
    });
    switch (url) {
      case 'https://github.com/login/device/code':
        return json({
          device_code: DEVICE_CODE,
          user_code: 'WDJB-MJHT',
          verification_uri: 'https://github.com/login/device',
          expires_in: 900,
          interval: 5,
        });
      case 'https://github.com/login/oauth/access_token':
        return reply(fake.tokenReplies.shift());
      case 'https://api.github.com/user':
        return fake.user();
      case 'https://api.github.com/user/installations':
        return reply(fake.installations);
      default:
        throw new Error(`unexpected request to ${url}`);
    }
  };

  const deps: GitHubAuthDeps = {
    fetch: fetchFake as unknown as typeof globalThis.fetch,
    vault: {
      get: async (key) => fake.vault.get(key) ?? null,
      store: async (key, value) => {
        fake.vault.set(key, value);
      },
      delete: async (key) => fake.vault.delete(key),
    },
    tokenFilePath: TOKEN_FILE,
    fs: {
      writeFile: async (path, data, options) => {
        fake.files.set(path, { data, mode: options.mode });
      },
      rename: async (from, to) => {
        const file = fake.files.get(from);
        if (file === undefined) throw new Error(`no file ${from}`);
        fake.files.delete(from);
        fake.files.set(to, file);
      },
      rm: async (path) => {
        fake.files.delete(path);
      },
    },
    now: () => fake.clock.now,
    sleep: async (ms) => {
      fake.sleeps.push(ms);
    },
    clientId: 'Iv23liScoutClientId',
    appSlug: 'scout-chatops',
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

  const result = Object.assign(fake, { deps, auth: new GitHubAuth(deps) });
  return result;
}

/** Signs in with GitHub approving on the second poll, and waits until the connection is stored. */
export async function connect(fake: GitHubFake): Promise<void> {
  fake.tokenReplies.push(
    { error: 'authorization_pending' },
    {
      access_token: 'ghu_first',
      expires_in: 28800,
      refresh_token: 'ghr_first',
      refresh_token_expires_in: 15897600,
      token_type: 'bearer',
    },
  );
  await fake.auth.signIn();
  await fake.auth.settled();
}

export function tokenFileToken(fake: GitHubFake): string | null {
  const file = fake.files.get(TOKEN_FILE);
  return file === undefined
    ? null
    : (JSON.parse(file.data) as { access_token: string }).access_token;
}
