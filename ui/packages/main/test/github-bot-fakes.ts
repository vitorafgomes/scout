import { generateKeyPairSync } from 'node:crypto';
import { createServer as createNetServer } from 'node:net';
import { GitHubBot, type GitHubBotDeps } from '../src/oauth/github-bot.js';
import { awaitCallback } from '../src/oauth/loopback-server.js';
import type { Logger } from '../src/util/logger.js';

export const BOT_TOKEN_FILE = '/user-data/github-bot-token.json';
export const CONVERSIONS_URL = 'https://api.github.com/app-manifests/manifest-code-1/conversions';
export const APP_URL = 'https://api.github.com/app';
export const INSTALLATIONS_URL = 'https://api.github.com/app/installations';
export const ACCESS_TOKENS_URL = 'https://api.github.com/app/installations/77/access_tokens';
export const REPOSITORIES_URL =
  'https://api.github.com/installation/repositories?per_page=100&page=1';

const keys = generateKeyPairSync('rsa', {
  modulusLength: 2048,
  publicKeyEncoding: { type: 'spki', format: 'pem' },
  privateKeyEncoding: { type: 'pkcs1', format: 'pem' },
});
export const PRIVATE_KEY = keys.privateKey;
export const PUBLIC_KEY = keys.publicKey;

export interface BotRequest {
  url: string;
  method: string;
  authorization: string | null;
}

type Reply = { status: number; body: unknown } | Error;

export interface BotFake {
  bot: GitHubBot;
  deps: GitHubBotDeps;
  requests: BotRequest[];
  logs: string[];
  files: Map<string, { data: string; mode: number }>;
  written: string[];
  vault: Map<string, string>;
  changes: number;
  opened: string[];
  clock: { now: number };
  replies: Map<string, Reply[]>;
  resumeListeners: (() => unknown)[];
}

export function conversion(overrides: Record<string, unknown> = {}): {
  status: number;
  body: unknown;
} {
  return {
    status: 201,
    body: {
      id: 4242,
      slug: 'scout-octocat',
      name: 'Scout (octocat)',
      client_id: 'Iv23liBotClientId',
      client_secret: 'bot-client-secret-value',
      webhook_secret: 'bot-webhook-secret-value',
      pem: PRIVATE_KEY,
      owner: { login: 'octocat', type: 'User' },
      ...overrides,
    },
  };
}

export function installationToken(
  expiresAt: string,
  token = 'ghs_installation_one',
): { status: number; body: unknown } {
  return { status: 201, body: { token, expires_at: expiresAt } };
}

async function freePort(): Promise<number> {
  return new Promise<number>((resolve, reject) => {
    const srv = createNetServer();
    srv.listen(0, '127.0.0.1', () => {
      const addr = srv.address();
      const port = addr !== null && typeof addr === 'object' ? addr.port : 0;
      srv.close(() => resolve(port));
    });
    srv.on('error', reject);
  });
}

export function makeBotFake(overrides: Partial<GitHubBotDeps> = {}): BotFake {
  const fake = {
    requests: [] as BotRequest[],
    logs: [] as string[],
    files: new Map<string, { data: string; mode: number }>(),
    written: [] as string[],
    vault: new Map<string, string>(),
    changes: 0,
    opened: [] as string[],
    clock: { now: Date.UTC(2026, 10, 7, 12, 0, 0) },
    replies: new Map<string, Reply[]>(),
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

  const fetchFake = async (input: unknown, init?: RequestInit): Promise<Response> => {
    const url = String(input);
    const headers = (init?.headers ?? {}) as Record<string, string>;
    fake.requests.push({
      url,
      method: init?.method ?? 'GET',
      authorization: headers['Authorization'] ?? null,
    });
    const reply = fake.replies.get(url)?.shift();
    if (reply === undefined) throw new Error(`unexpected request to ${url}`);
    if (reply instanceof Error) throw reply;
    return new Response(JSON.stringify(reply.body), {
      status: reply.status,
      headers: { 'Content-Type': 'application/json' },
    });
  };

  const deps: GitHubBotDeps = {
    fetch: fetchFake as unknown as typeof globalThis.fetch,
    vault: {
      get: async (key) => fake.vault.get(key) ?? null,
      store: async (key, value) => {
        fake.vault.set(key, value);
      },
      delete: async (key) => fake.vault.delete(key),
    },
    tokenFilePath: BOT_TOKEN_FILE,
    fs: {
      writeFile: async (path, data, options) => {
        fake.written.push(data);
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
    awaitCallback,
    findFreePort: () => freePort(),
    openExternal: async (url) => {
      fake.opened.push(url);
    },
    ownerLogin: async () => 'octocat',
    onChanged: () => {
      fake.changes += 1;
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

  return Object.assign(fake, { deps, bot: new GitHubBot(deps) });
}

export function reply(fake: BotFake, url: string, ...replies: Reply[]): void {
  fake.replies.set(url, [...(fake.replies.get(url) ?? []), ...replies]);
}

/** Reads the page the browser opens and returns the form GitHub would receive. */
export async function openedForm(
  fake: BotFake,
): Promise<{ action: URL; manifest: Record<string, unknown> }> {
  const page = await (await fetch(fake.opened.at(-1)!)).text();
  const action = /action="([^"]+)"/.exec(page)![1]!;
  const value = /name="manifest" value="([^"]+)"/.exec(page)![1]!;
  const unescape = (text: string): string =>
    text
      .replace(/&quot;/g, '"')
      .replace(/&#39;/g, "'")
      .replace(/&lt;/g, '<')
      .replace(/&gt;/g, '>')
      .replace(/&amp;/g, '&');
  return {
    action: new URL(unescape(action)),
    manifest: JSON.parse(unescape(value)) as Record<string, unknown>,
  };
}

/** Creates the bot as GitHub would: the browser posts the form and GitHub redirects back with a code. */
export async function createBot(
  fake: BotFake,
  target: { target: 'user' } | { target: 'org'; org: string } = { target: 'user' },
): Promise<{ action: URL; manifest: Record<string, unknown> }> {
  await fake.bot.create(target);
  const form = await openedForm(fake);
  const state = form.action.searchParams.get('state')!;
  await fetch(`${String(form.manifest['redirect_url'])}?code=manifest-code-1&state=${state}`);
  await fake.bot.settled();
  return form;
}
