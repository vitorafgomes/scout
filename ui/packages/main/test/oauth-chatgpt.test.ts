import { describe, it, expect, beforeEach, afterEach, jest } from 'bun:test';
import { createHash, generateKeyPairSync, sign, type KeyObject } from 'node:crypto';
import { createServer as createNetServer } from 'node:net';
import { mkdtemp, readFile, rm, stat, writeFile, rename } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {
  ChatGptAuthError,
  OAuthStateMismatchError,
  OAuthUserCancelledError,
  VaultUnavailableError,
  CHANNEL,
} from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { ChatGptAuth, type ChatGptAuthDeps } from '../src/oauth/chatgpt-auth.js';
import { awaitCallback, findFreePort } from '../src/oauth/loopback-server.js';
import { registerChatGptHandlers } from '../src/ipc/chatgpt.js';
import { registerVaultHandlers } from '../src/ipc/vault.js';
import type { Vault, StoredVaultKey } from '../src/vault/safe-storage-vault.js';
import { createLogger } from '../src/util/logger.js';

const ISSUER = 'https://auth.openai.com';
const TOKEN_URL = 'https://auth.openai.com/api/accounts/oauth/token';
const DISCOVERY_URL = 'https://auth.openai.com/.well-known/openid-configuration';
const JWKS_URL = 'https://auth.openai.com/.well-known/jwks.json';
const REVOKE_URL = 'https://auth.openai.com/oauth/revoke';
const MODELS_URL = 'https://api.openai.com/v1/models';
const MODEL_LIST = {
  models: [
    { slug: 'gpt-6.1-sol', display_name: 'GPT-6.1 Sol', visibility: 'list' },
    { slug: 'gpt-5.6-luna', visibility: 'list' },
    { slug: 'internal-eval', display_name: 'Internal eval', visibility: 'hide' },
  ],
};
const ISSUED_CLIENT_ID = 'app_issued_123';
const AUTH_CODE = 'auth-code-secret-7f3a';
const SCOPE_GRANTED =
  'chatgpt.tokens.use.direct email offline_access openid profile resource.invoke';

const signingKey = generateKeyPairSync('rsa', { modulusLength: 2048 });
const strangerKey = generateKeyPairSync('rsa', { modulusLength: 2048 });
const publishedJwk = { ...signingKey.publicKey.export({ format: 'jwk' }), kid: 'k1', alg: 'RS256' };

function b64url(value: unknown): string {
  return Buffer.from(JSON.stringify(value)).toString('base64url');
}

function signJwt(
  claims: Record<string, unknown>,
  opts: { alg?: string; kid?: string; key?: KeyObject } = {},
): string {
  const head = b64url({ alg: opts.alg ?? 'RS256', kid: opts.kid ?? 'k1', typ: 'JWT' });
  const body = b64url(claims);
  const signature = sign(
    'sha256',
    Buffer.from(`${head}.${body}`),
    opts.key ?? signingKey.privateKey,
  );
  return `${head}.${body}.${signature.toString('base64url')}`;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

async function freePort(): Promise<number> {
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

interface RecordedCall {
  url: string;
  method: string;
  form: Record<string, string> | null;
  authorization: string | null;
}

interface Scenario {
  clock: number;
  calls: RecordedCall[];
  authorizeUrls: URL[];
  callbackParams: (authorize: URL) => Record<string, string>;
  idTokenClaims: (nonce: string) => Record<string, unknown>;
  idTokenOptions: { alg?: string; kid?: string; key?: KeyObject };
  tokenScope: string;
  tokenCounter: number;
  refreshResponse: (() => Response) | null;
  tokenNetworkError: boolean;
  jwksFails: boolean;
  revokeResponse: () => Response;
  browserFails: boolean;
  browserCallsBack: boolean;
  listenerUpWhenBrowserOpened: boolean | null;
  logLines: string[];
  fileOps: string[];
  renameFailures: number;
  modelsNetworkError: boolean;
}

function newScenario(): Scenario {
  return {
    clock: Date.UTC(2026, 9, 23, 12, 0, 0),
    calls: [],
    authorizeUrls: [],
    callbackParams: (authorize) => ({
      code: AUTH_CODE,
      state: authorize.searchParams.get('state') ?? '',
      client_id: ISSUED_CLIENT_ID,
    }),
    idTokenClaims: (nonce) => ({
      iss: ISSUER,
      aud: ISSUED_CLIENT_ID,
      exp: Math.floor(scenario.clock / 1000) + 3600,
      nonce,
      email: 'owner@example.com',
    }),
    idTokenOptions: {},
    tokenScope: SCOPE_GRANTED,
    tokenCounter: 0,
    refreshResponse: null,
    tokenNetworkError: false,
    jwksFails: false,
    revokeResponse: () => new Response(null, { status: 200 }),
    browserFails: false,
    browserCallsBack: true,
    listenerUpWhenBrowserOpened: null,
    logLines: [],
    fileOps: [],
    renameFailures: 0,
    modelsNetworkError: false,
  };
}

let scenario: Scenario = newScenario();

const fakeOpenAi = (async (input: Parameters<typeof fetch>[0], init?: RequestInit) => {
  const url = String(input);
  const method = init?.method ?? 'GET';
  const form =
    typeof init?.body === 'string' ? Object.fromEntries(new URLSearchParams(init.body)) : null;
  const authorization = new Headers(init?.headers).get('authorization');
  scenario.calls.push({ url, method, form, authorization });
  if (url === MODELS_URL) {
    if (scenario.modelsNetworkError) throw new TypeError('fetch failed: ECONNRESET');
    return json(MODEL_LIST);
  }

  if (url === DISCOVERY_URL) {
    return json({ issuer: ISSUER, jwks_uri: JWKS_URL, revocation_endpoint: REVOKE_URL });
  }
  if (url === JWKS_URL) {
    if (scenario.jwksFails) throw new TypeError('fetch failed: ECONNRESET');
    return json({ keys: [publishedJwk] });
  }
  if (url === REVOKE_URL) return scenario.revokeResponse();
  if (url === TOKEN_URL) {
    if (scenario.tokenNetworkError) throw new TypeError('fetch failed: ETIMEDOUT');
    if (form?.['grant_type'] === 'refresh_token' && scenario.refreshResponse !== null) {
      return scenario.refreshResponse();
    }
    scenario.tokenCounter += 1;
    const n = scenario.tokenCounter;
    const nonce = scenario.authorizeUrls.at(-1)?.searchParams.get('nonce') ?? '';
    return json({
      access_token: `access-token-${n}`,
      refresh_token: `refresh-token-${n}`,
      id_token: signJwt(scenario.idTokenClaims(nonce), scenario.idTokenOptions),
      expires_in: 3600,
      scope: scenario.tokenScope,
      token_type: 'Bearer',
    });
  }
  return new Response('not found', { status: 404 });
}) as typeof fetch;

async function fakeBrowser(url: string): Promise<void> {
  const authorize = new URL(url);
  scenario.authorizeUrls.push(authorize);
  if (scenario.browserFails) throw new Error('xdg-open: no browser available');
  const redirect = new URL(authorize.searchParams.get('redirect_uri') ?? '');
  try {
    const probe = await globalThis.fetch(`${redirect.origin}/favicon.ico`);
    await probe.text();
    scenario.listenerUpWhenBrowserOpened = true;
  } catch {
    scenario.listenerUpWhenBrowserOpened = false;
  }
  if (!scenario.browserCallsBack) return;
  const query = new URLSearchParams(scenario.callbackParams(authorize));
  void globalThis
    .fetch(`${redirect.origin}${redirect.pathname}?${query.toString()}`)
    .then((r) => r.text())
    .catch(() => undefined);
}

function makeVault(): Pick<Vault, 'get' | 'store' | 'delete'> & {
  entries: Map<StoredVaultKey, string>;
} {
  const entries = new Map<StoredVaultKey, string>();
  return {
    entries,
    get: async (key) => entries.get(key) ?? null,
    store: async (key, value) => {
      entries.set(key, value);
    },
    delete: async (key) => entries.delete(key),
  };
}

let tmpDir: string;
let tokenFile: string;
let vault: ReturnType<typeof makeVault>;

function makeAuth(overrides: Partial<ChatGptAuthDeps> = {}): ChatGptAuth {
  return new ChatGptAuth({
    fetch: fakeOpenAi,
    awaitCallback,
    findFreePort: async () => freePort(),
    openExternal: fakeBrowser,
    vault,
    tokenFilePath: tokenFile,
    fs: {
      writeFile: async (p, data, options) => {
        scenario.fileOps.push(`write ${path.basename(p)} mode=${options.mode.toString(8)}`);
        await writeFile(p, data, options);
      },
      rename: async (from, to) => {
        scenario.fileOps.push(`rename ${path.basename(from)} -> ${path.basename(to)}`);
        if (scenario.renameFailures > 0) {
          scenario.renameFailures -= 1;
          throw Object.assign(new Error('EPERM: operation not permitted, rename'), {
            code: 'EPERM',
          });
        }
        await rename(from, to);
      },
      rm: async (p, options) => {
        scenario.fileOps.push(`rm ${path.basename(p)}`);
        await rm(p, options);
      },
    },
    now: () => scenario.clock,
    logger: createLogger('test', { level: 'debug', sink: (line) => scenario.logLines.push(line) }),
    ...overrides,
  });
}

async function readTokenFile(): Promise<Record<string, unknown> | null> {
  try {
    return JSON.parse(await readFile(tokenFile, 'utf8')) as Record<string, unknown>;
  } catch {
    return null;
  }
}

function tokenPosts(): RecordedCall[] {
  return scenario.calls.filter((c) => c.url === TOKEN_URL);
}

function storedSessionText(): string {
  return vault.entries.get('chatgpt.session') ?? '';
}

beforeEach(async () => {
  scenario = newScenario();
  vault = makeVault();
  tmpDir = await mkdtemp(path.join(os.tmpdir(), 'scout-chatgpt-'));
  tokenFile = path.join(tmpDir, 'chatgpt-token.json');
});

afterEach(async () => {
  await rm(tmpDir, { recursive: true, force: true });
});

describe('ChatGPT sign-in through the browser', () => {
  it('connects by getting the credentials through the browser: signed in with the account email and plan usage', async () => {
    const status = await makeAuth().signIn();

    expect(status).toEqual({
      signedIn: true,
      email: 'owner@example.com',
      planUsageGranted: true,
    });
    expect(vault.entries.get('chatgpt.client-id')).toBe(ISSUED_CLIENT_ID);
    expect(await readTokenFile()).toEqual({
      access_token: 'access-token-1',
      expires_at: new Date(scenario.clock + 3600_000).toISOString(),
    });
  });

  it('opens the authorize URL with every required parameter and a PKCE S256 challenge of the verifier', async () => {
    await makeAuth().signIn();

    const authorize = scenario.authorizeUrls[0];
    expect(authorize).toBeDefined();
    const q = authorize!.searchParams;
    expect(`${authorize!.origin}${authorize!.pathname}`).toBe(
      'https://auth.openai.com/api/accounts/authorize',
    );
    expect(q.get('response_type')).toBe('code');
    expect(q.get('client_id')).toBe('dynamic_agent_client');
    expect(q.get('redirect_uri')).toMatch(/^http:\/\/127\.0\.0\.1:\d+\/auth\/callback$/);
    expect(q.get('scope')).toBe(
      'openid profile email offline_access resource.invoke chatgpt.tokens.use.direct',
    );
    expect(q.get('resource')).toBe('https://api.openai.com/v1');
    expect(q.get('code_challenge_method')).toBe('S256');
    expect(q.get('state')?.length).toBeGreaterThanOrEqual(43);
    expect(q.get('nonce')?.length).toBeGreaterThanOrEqual(43);
    expect(q.get('nonce')).not.toBe(q.get('state'));
    expect(q.get('agent_name_hint')).toBe('Scout');
    expect(q.get('ext_agent_host_id')).toMatch(
      /^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
    );
    expect(q.get('ext_agent_host_id')).toBe(vault.entries.get('chatgpt.host-id') ?? null);

    const verifier = tokenPosts()[0]?.form?.['code_verifier'] ?? '';
    expect(verifier.length).toBeGreaterThanOrEqual(43);
    expect(q.get('code_challenge')).toBe(createHash('sha256').update(verifier).digest('base64url'));
  });

  it('starts the loopback listener before it opens the browser', async () => {
    await makeAuth().signIn();

    expect(scenario.listenerUpWhenBrowserOpened).toBe(true);
  });

  it('re-authenticates with the issued client id, the same host id and no agent name hint', async () => {
    const auth = makeAuth();
    await auth.signIn();
    await auth.signIn();

    const [first, second] = scenario.authorizeUrls;
    expect(second?.searchParams.get('client_id')).toBe(ISSUED_CLIENT_ID);
    expect(second?.searchParams.has('agent_name_hint')).toBe(false);
    expect(second?.searchParams.get('ext_agent_host_id')).toBe(
      first?.searchParams.get('ext_agent_host_id') ?? 'missing',
    );
    expect(second?.searchParams.get('state')).not.toBe(first?.searchParams.get('state'));
    expect(second?.searchParams.get('nonce')).not.toBe(first?.searchParams.get('nonce'));
  });

  it('exchanges the code with exactly the fields the spec names, against the issued client id', async () => {
    await makeAuth().signIn();

    const posts = tokenPosts();
    expect(posts).toHaveLength(1);
    const redirectUri = scenario.authorizeUrls[0]?.searchParams.get('redirect_uri');
    expect(posts[0]?.method).toBe('POST');
    expect(posts[0]?.form).toEqual({
      grant_type: 'authorization_code',
      client_id: ISSUED_CLIENT_ID,
      code: AUTH_CODE,
      code_verifier: posts[0]?.form?.['code_verifier'] ?? 'missing',
      redirect_uri: redirectUri ?? 'missing',
      resource: 'https://api.openai.com/v1',
    });
  });

  it('stops without a token exchange when the callback state does not match', async () => {
    scenario.callbackParams = () => ({ code: AUTH_CODE, state: 'forged-state' });

    let thrown: unknown = null;
    await makeAuth()
      .signIn()
      .catch((err: unknown) => (thrown = err));

    expect(thrown).toBeInstanceOf(OAuthStateMismatchError);
    expect(tokenPosts()).toHaveLength(0);
    expect(vault.entries.has('chatgpt.session')).toBe(false);
  });

  it('stops without a token exchange when the owner denies access', async () => {
    scenario.callbackParams = (authorize) => ({
      error: 'access_denied',
      state: authorize.searchParams.get('state') ?? '',
    });

    let thrown: unknown = null;
    await makeAuth()
      .signIn()
      .catch((err: unknown) => (thrown = err));

    expect(thrown).toBeInstanceOf(OAuthUserCancelledError);
    expect(tokenPosts()).toHaveLength(0);
    expect(vault.entries.has('chatgpt.session')).toBe(false);
  });

  it('stops without a token exchange when first registration returns no issued client id', async () => {
    scenario.callbackParams = (authorize) => ({
      code: AUTH_CODE,
      state: authorize.searchParams.get('state') ?? '',
    });

    let thrown: unknown = null;
    await makeAuth()
      .signIn()
      .catch((err: unknown) => (thrown = err));

    expect(thrown).toBeInstanceOf(ChatGptAuthError);
    expect(thrown).toMatchObject({ reason: 'missing_client_id' });
    expect(tokenPosts()).toHaveLength(0);
  });

  it('keeps the owner signed in but reports plan usage as not granted when the scope lacks chatgpt.tokens.use.direct', async () => {
    scenario.tokenScope = 'openid profile email offline_access';

    const status = await makeAuth().signIn();

    expect(status).toEqual({
      signedIn: true,
      email: 'owner@example.com',
      planUsageGranted: false,
    });
  });

  it('accepts an ID token whose audience is an array containing the issued client id', async () => {
    const base = scenario.idTokenClaims;
    scenario.idTokenClaims = (nonce) => ({ ...base(nonce), aud: ['other', ISSUED_CLIENT_ID] });

    const status = await makeAuth().signIn();

    expect(status.signedIn).toBe(true);
  });

  const rejectedIdTokens: Array<[string, () => void]> = [
    [
      'wrong audience',
      () => {
        const base = scenario.idTokenClaims;
        scenario.idTokenClaims = (nonce) => ({ ...base(nonce), aud: 'someone_else' });
      },
    ],
    [
      'wrong issuer',
      () => {
        const base = scenario.idTokenClaims;
        scenario.idTokenClaims = (nonce) => ({ ...base(nonce), iss: 'https://evil.example' });
      },
    ],
    [
      'wrong nonce',
      () => {
        const base = scenario.idTokenClaims;
        scenario.idTokenClaims = (nonce) => ({ ...base(nonce), nonce: 'replayed' });
      },
    ],
    [
      'expired',
      () => {
        const base = scenario.idTokenClaims;
        scenario.idTokenClaims = (nonce) => ({
          ...base(nonce),
          exp: Math.floor(scenario.clock / 1000) - 1,
        });
      },
    ],
    [
      'signed by a key OpenAI does not publish',
      () => {
        scenario.idTokenOptions = { key: strangerKey.privateKey };
      },
    ],
    [
      'signed with HS256',
      () => {
        scenario.idTokenOptions = { alg: 'HS256' };
      },
    ],
    [
      'unsigned (alg none)',
      () => {
        scenario.idTokenOptions = { alg: 'none' };
      },
    ],
  ];

  for (const [name, arrange] of rejectedIdTokens) {
    it(`rejects an ID token that is ${name} and stores nothing`, async () => {
      arrange();

      let thrown: unknown = null;
      await makeAuth()
        .signIn()
        .catch((err: unknown) => (thrown = err));

      expect(thrown).toBeInstanceOf(ChatGptAuthError);
      expect(thrown).toMatchObject({ reason: 'invalid_id_token' });
      expect(vault.entries.has('chatgpt.session')).toBe(false);
      expect(await readTokenFile()).toBeNull();
    });
  }
});

describe('ChatGPT model list', () => {
  it('lists only the models marked for listing, by slug, with display names falling back to the slug, using the Bearer access token', async () => {
    const auth = makeAuth();
    await auth.signIn();

    const models = await auth.listModels();

    expect(models).toEqual([
      { slug: 'gpt-6.1-sol', displayName: 'GPT-6.1 Sol' },
      { slug: 'gpt-5.6-luna', displayName: 'gpt-5.6-luna' },
    ]);
    const call = scenario.calls.find((c) => c.url === MODELS_URL);
    expect(call?.method).toBe('GET');
    expect(call?.authorization).toBe('Bearer access-token-1');
    expect(scenario.logLines.join('\n')).not.toContain('access-token-');
  });

  it('refreshes a token near expiry before listing models', async () => {
    const auth = makeAuth();
    await auth.signIn();
    scenario.clock += 56 * 60_000;

    await auth.listModels();

    expect(scenario.calls.find((c) => c.url === MODELS_URL)?.authorization).toBe(
      'Bearer access-token-2',
    );
  });

  it('models: when the model list cannot be reached, fails with network_error and no token in the message', async () => {
    const auth = makeAuth();
    await auth.signIn();
    scenario.modelsNetworkError = true;

    const thrown = await auth.listModels().catch((err: unknown) => err);

    expect(thrown).toBeInstanceOf(ChatGptAuthError);
    expect(thrown).toMatchObject({ reason: 'network_error' });
    expect(String(thrown)).not.toContain('access-token-');
  });

  it('refuses to list models when nobody is signed in, without calling OpenAI', async () => {
    const thrown = await makeAuth()
      .listModels()
      .catch((err: unknown) => err);

    expect(thrown).toMatchObject({ reason: 'not_signed_in' });
    expect(scenario.calls.some((c) => c.url === MODELS_URL)).toBe(false);
  });

  it('the renderer channel returns only slugs and display names', async () => {
    const ipc = makeIpc();
    const auth = makeAuth();
    registerChatGptHandlers(ipc, auth, createLogger('test', { sink: () => {} }));
    await auth.signIn();

    const response = await ipc.handlers.get(CHANNEL.chatgptListModels)?.({} as IpcMainInvokeEvent);

    expect(response).toEqual({
      models: [
        { slug: 'gpt-6.1-sol', displayName: 'GPT-6.1 Sol' },
        { slug: 'gpt-5.6-luna', displayName: 'gpt-5.6-luna' },
      ],
    });
  });
});

describe('ChatGPT sign-in cancel', () => {
  it('cancelling a sign-in that waits for the browser rejects it as cancelled, exchanges nothing and releases the port', async () => {
    scenario.browserCallsBack = false;
    const port = await freePort();
    const auth = makeAuth({ findFreePort: async () => port });

    const pending = auth.signIn().catch((err: unknown) => err);
    while (scenario.authorizeUrls.length === 0) await new Promise((r) => setTimeout(r, 5));
    auth.cancelSignIn();
    const outcome = await Promise.race([
      pending,
      new Promise((r) => setTimeout(() => r('still waiting for the browser'), 1000)),
    ]);
    await new Promise((r) => setTimeout(r, 20));

    expect(outcome).toBeInstanceOf(OAuthUserCancelledError);
    expect(await findFreePort(port, 0)).toBe(port);
    expect(tokenPosts()).toHaveLength(0);
    expect((await auth.status()).signedIn).toBe(false);
  });
});

describe('ChatGPT sign-in failing calls', () => {
  it('authorize: when the browser cannot be opened, sign-in fails and the loopback port is released', async () => {
    scenario.browserFails = true;
    const port = await freePort();
    const auth = makeAuth({ findFreePort: async () => port });

    let thrown: unknown = null;
    await auth.signIn().catch((err: unknown) => (thrown = err));
    await new Promise((r) => setTimeout(r, 20));

    expect(thrown).toMatchObject({ message: expect.stringContaining('no browser available') });
    expect(await findFreePort(port, 0)).toBe(port);
    expect(tokenPosts()).toHaveLength(0);
  });

  it('token: when the token endpoint cannot be reached, sign-in fails with network_error and stores nothing', async () => {
    scenario.tokenNetworkError = true;

    let thrown: unknown = null;
    await makeAuth()
      .signIn()
      .catch((err: unknown) => (thrown = err));

    expect(thrown).toBeInstanceOf(ChatGptAuthError);
    expect(thrown).toMatchObject({ reason: 'network_error' });
    expect(vault.entries.has('chatgpt.session')).toBe(false);
    expect(await readTokenFile()).toBeNull();
  });

  it('JWKS: when the signing keys cannot be fetched, sign-in fails with jwks_unavailable and stores nothing', async () => {
    scenario.jwksFails = true;

    let thrown: unknown = null;
    await makeAuth()
      .signIn()
      .catch((err: unknown) => (thrown = err));

    expect(thrown).toBeInstanceOf(ChatGptAuthError);
    expect(thrown).toMatchObject({ reason: 'jwks_unavailable' });
    expect(vault.entries.has('chatgpt.session')).toBe(false);
  });
});

describe('ChatGPT session stays signed in (refresh)', () => {
  it('refreshes under five minutes to expiry: rotates the refresh token and rewrites the token file atomically with mode 0600', async () => {
    const auth = makeAuth();
    await auth.signIn();
    scenario.fileOps = [];
    scenario.clock += 56 * 60_000;

    await auth.refreshIfDue();

    const refresh = tokenPosts()[1];
    expect(refresh?.form).toEqual({
      grant_type: 'refresh_token',
      refresh_token: 'refresh-token-1',
      resource: 'https://api.openai.com/v1',
      client_id: ISSUED_CLIENT_ID,
    });
    const stored = JSON.parse(storedSessionText()) as Record<string, unknown>;
    expect(stored['refreshToken']).toBe('refresh-token-2');
    expect(stored['accessToken']).toBe('access-token-2');
    expect(await readTokenFile()).toEqual({
      access_token: 'access-token-2',
      expires_at: new Date(scenario.clock + 3600_000).toISOString(),
    });
    expect(scenario.fileOps).toEqual([
      'write chatgpt-token.json.tmp mode=600',
      'rename chatgpt-token.json.tmp -> chatgpt-token.json',
    ]);
    if (process.platform !== 'win32') {
      expect((await stat(tokenFile)).mode & 0o777).toBe(0o600);
    }
  });

  it('does not refresh while more than five minutes remain', async () => {
    const auth = makeAuth();
    await auth.signIn();
    scenario.clock += 54 * 60_000;

    await auth.refreshIfDue();

    expect(tokenPosts()).toHaveLength(1);
  });

  for (const body of [{ error: 'invalid_grant' }, { error: { code: 'refresh_token_reused' } }]) {
    it(`a terminal refresh error (${JSON.stringify(body)}) clears the session and deletes the token file`, async () => {
      const auth = makeAuth();
      await auth.signIn();
      scenario.clock += 58 * 60_000;
      scenario.refreshResponse = () => json(body, 400);

      await auth.refreshIfDue();

      expect(await auth.status()).toEqual({
        signedIn: false,
        email: null,
        planUsageGranted: false,
      });
      expect(await readTokenFile()).toBeNull();
      expect(vault.entries.get('chatgpt.client-id')).toBe(ISSUED_CLIENT_ID);
    });
  }

  it('refresh: a 5xx keeps the session and the current token file for the next attempt', async () => {
    const auth = makeAuth();
    await auth.signIn();
    scenario.clock += 58 * 60_000;
    scenario.refreshResponse = () => json({ error: 'server_error' }, 503);

    await auth.refreshIfDue();

    expect((await auth.status()).signedIn).toBe(true);
    expect((await readTokenFile())?.['access_token']).toBe('access-token-1');
  });

  it('refresh: a network failure keeps the session for the next attempt', async () => {
    const auth = makeAuth();
    await auth.signIn();
    scenario.clock += 58 * 60_000;
    scenario.tokenNetworkError = true;

    await auth.refreshIfDue();

    expect((await auth.status()).signedIn).toBe(true);
    expect(JSON.parse(storedSessionText())['refreshToken']).toBe('refresh-token-1');
  });

  it('the refresh timer started with the app refreshes on its own once the token is under five minutes from expiry', async () => {
    const auth = makeAuth();
    await auth.signIn();
    jest.useFakeTimers();
    try {
      await auth.start();
      scenario.clock += 56 * 60_000;
      jest.advanceTimersByTime(60_000);
    } finally {
      jest.useRealTimers();
    }
    await new Promise((r) => setTimeout(r, 20));
    auth.stop();

    expect(tokenPosts()).toHaveLength(2);
    expect(tokenPosts()[1]?.form?.['grant_type']).toBe('refresh_token');
    expect((await readTokenFile())?.['access_token']).toBe('access-token-2');
  });

  async function tickWithFailingVault(failure: () => Error, ticks: number): Promise<string[]> {
    const auth = makeAuth({
      vault: {
        get: async () => {
          throw failure();
        },
        store: async () => {},
        delete: async () => false,
      },
    });
    jest.useFakeTimers();
    try {
      await auth.start().catch(() => undefined);
      for (let n = 0; n < ticks; n++) {
        jest.advanceTimersByTime(60_000);
        await new Promise((r) => process.nextTick(r));
      }
    } finally {
      jest.useRealTimers();
    }
    await new Promise((r) => setTimeout(r, 20));
    auth.stop();
    return scenario.logLines.filter((l) => l.includes('"lvl":"warn"'));
  }

  it('the refresh tick stays silent when the keyring is unavailable, over two ticks', async () => {
    const warnings = await tickWithFailingVault(() => new VaultUnavailableError(), 2);

    expect(warnings).toEqual([]);
  });

  it('the refresh tick logs one warning when the vault read fails for another reason', async () => {
    const warnings = await tickWithFailingVault(() => new Error('EIO: i/o error'), 1);

    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain('chatgpt: refresh check failed');
  });

  it('token file: when writing it fails after a refresh, the rotated session is kept and the next check rewrites the file without another refresh', async () => {
    const auth = makeAuth();
    await auth.signIn();
    scenario.clock += 56 * 60_000;
    scenario.renameFailures = 1;

    await auth.refreshIfDue();

    expect(JSON.parse(storedSessionText())['refreshToken']).toBe('refresh-token-2');
    expect((await readTokenFile())?.['access_token']).toBe('access-token-1');

    scenario.clock += 60_000;
    await auth.refreshIfDue();

    expect(tokenPosts()).toHaveLength(2);
    expect((await readTokenFile())?.['access_token']).toBe('access-token-2');
  });

  it('on start, rewrites the token file from the stored session', async () => {
    const first = makeAuth();
    await first.signIn();
    await rm(tokenFile);

    const restarted = makeAuth();
    await restarted.start();
    restarted.stop();

    expect((await readTokenFile())?.['access_token']).toBe('access-token-1');
  });

  it('a refresh queued behind sign-out does not bring the session back', async () => {
    const auth = makeAuth();
    await auth.signIn();
    scenario.clock += 58 * 60_000;

    await Promise.all([auth.signOut(), auth.refreshIfDue()]);

    expect((await auth.status()).signedIn).toBe(false);
    expect(await readTokenFile()).toBeNull();
  });
});

describe('ChatGPT sign-out', () => {
  it('revokes the refresh token, then clears the session and deletes the token file, keeping client id and host id', async () => {
    const auth = makeAuth();
    await auth.signIn();
    const hostId = vault.entries.get('chatgpt.host-id');

    const result = await auth.signOut();

    const revoke = scenario.calls.find((c) => c.url === REVOKE_URL);
    expect(revoke?.form).toEqual({
      token: 'refresh-token-1',
      token_type_hint: 'refresh_token',
      client_id: ISSUED_CLIENT_ID,
    });
    expect(result).toEqual({ revocationConfirmed: true });
    expect(vault.entries.has('chatgpt.session')).toBe(false);
    expect(await readTokenFile()).toBeNull();
    expect(vault.entries.get('chatgpt.client-id')).toBe(ISSUED_CLIENT_ID);
    expect(vault.entries.get('chatgpt.host-id')).toBe(hostId ?? 'missing');
  });

  it('revoke: when revocation cannot reach OpenAI, still clears locally and reports it unconfirmed', async () => {
    const auth = makeAuth();
    await auth.signIn();
    scenario.revokeResponse = () => {
      throw new TypeError('fetch failed: ECONNREFUSED');
    };

    const result = await auth.signOut();

    expect(result).toEqual({ revocationConfirmed: false });
    expect(vault.entries.has('chatgpt.session')).toBe(false);
    expect(await readTokenFile()).toBeNull();
  });

  it('revoke: when OpenAI refuses the revocation, still clears locally and reports it unconfirmed', async () => {
    const auth = makeAuth();
    await auth.signIn();
    scenario.revokeResponse = () => json({ error: 'server_error' }, 500);

    const result = await auth.signOut();

    expect(result).toEqual({ revocationConfirmed: false });
    expect((await auth.status()).signedIn).toBe(false);
  });
});

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => Promise<unknown>;

function makeIpc(): Pick<IpcMain, 'handle' | 'removeHandler'> & {
  handlers: Map<string, IpcHandler>;
} {
  const handlers = new Map<string, IpcHandler>();
  return {
    handlers,
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
}

describe('ChatGPT tokens never leak', () => {
  it('no token, authorization code or code verifier appears in any log line across sign-in, refresh, failures and sign-out', async () => {
    const auth = makeAuth();
    await auth.signIn();
    const firstIdToken = JSON.parse(storedSessionText())['idToken'] as string;
    const verifier = tokenPosts()[0]?.form?.['code_verifier'] ?? 'missing';
    scenario.clock += 56 * 60_000;
    await auth.refreshIfDue();
    scenario.clock += 58 * 60_000;
    scenario.refreshResponse = () => json({ error: 'server_error' }, 503);
    await auth.refreshIfDue();
    scenario.refreshResponse = null;
    scenario.revokeResponse = () => json({ error: 'server_error' }, 500);
    await auth.signOut();
    scenario.idTokenOptions = { key: strangerKey.privateKey };
    await auth.signIn().catch(() => undefined);
    scenario.idTokenOptions = {};
    scenario.jwksFails = true;
    await auth.signIn().catch(() => undefined);
    scenario.jwksFails = false;
    await auth.signIn();
    scenario.clock += 58 * 60_000;
    scenario.refreshResponse = () => json({ error: 'invalid_grant' }, 400);
    await auth.refreshIfDue();

    const logged = scenario.logLines.join('\n');
    expect(scenario.logLines.length).toBeGreaterThan(5);
    for (const secret of [
      'access-token-',
      'refresh-token-',
      firstIdToken,
      firstIdToken.split('.')[1] ?? 'missing',
      AUTH_CODE,
      verifier,
    ]) {
      expect(logged).not.toContain(secret);
    }
  });

  it('the renderer channels return only signedIn, email and planUsageGranted, and errors carry no token', async () => {
    const ipc = makeIpc();
    const auth = makeAuth();
    registerChatGptHandlers(ipc, auth, createLogger('test', { sink: () => {} }));
    const event = {} as IpcMainInvokeEvent;

    const signedIn = await ipc.handlers.get(CHANNEL.chatgptSignIn)?.(event);
    const status = await ipc.handlers.get(CHANNEL.chatgptStatus)?.(event);
    const signedOut = await ipc.handlers.get(CHANNEL.chatgptSignOut)?.(event);
    scenario.idTokenOptions = { key: strangerKey.privateKey };
    const failure = await ipc.handlers
      .get(CHANNEL.chatgptSignIn)?.(event)
      .catch((err: unknown) => err);

    expect(Object.keys(signedIn as object).sort()).toEqual([
      'email',
      'planUsageGranted',
      'signedIn',
    ]);
    expect(Object.keys(status as object).sort()).toEqual(['email', 'planUsageGranted', 'signedIn']);
    expect(signedOut).toEqual({ revocationConfirmed: true });
    const returned = JSON.stringify([signedIn, status, signedOut, String(failure)]);
    expect(returned).not.toContain('access-token-');
    expect(returned).not.toContain('refresh-token-');
    expect(returned).not.toContain(AUTH_CODE);
  });

  it('the renderer vault channels refuse to store, test or delete the ChatGPT session keys', async () => {
    const ipc = makeIpc();
    registerVaultHandlers(ipc, vault as unknown as Vault, createLogger('test', { sink: () => {} }));
    vault.entries.set('chatgpt.session', '{"accessToken":"access-token-9"}');
    const event = {} as IpcMainInvokeEvent;

    for (const key of ['chatgpt.session', 'chatgpt.client-id', 'chatgpt.host-id']) {
      const stored = await ipc.handlers
        .get(CHANNEL.vaultStore)?.(event, { key, value: 'x' })
        .catch((err: unknown) => err);
      const has = await ipc.handlers
        .get(CHANNEL.vaultHas)?.(event, { key })
        .catch((err: unknown) => err);
      const deleted = await ipc.handlers
        .get(CHANNEL.vaultDelete)?.(event, { key })
        .catch((err: unknown) => err);
      for (const outcome of [stored, has, deleted]) {
        expect(outcome).toMatchObject({ code: 'vault/invalid-key' });
      }
    }
    expect(vault.entries.get('chatgpt.session')).toBe('{"accessToken":"access-token-9"}');
  });
});
