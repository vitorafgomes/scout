import { describe, it, expect } from 'bun:test';
import { createHash } from 'node:crypto';
import { ScoutError } from '@scout/shared';
import { findFreePort } from '../src/oauth/loopback-server.js';
import {
  API_KEY,
  AUTHORIZE_URL,
  AUTH_CODE,
  CHANNELS_URL,
  BUNDLED_FILE,
  CLIENT_FILE,
  CLIENT_ID,
  CLIENT_SECRET,
  ONE_HOUR_MS,
  REVOKE_URL,
  TOKEN_FILE,
  TOKEN_URL,
  connect,
  freePort,
  json,
  makeGoogleFake,
  tokenFile,
  tokenPosts,
} from './google-fakes.js';

const SCOPES =
  'https://www.googleapis.com/auth/youtube.readonly https://www.googleapis.com/auth/yt-analytics.readonly';

async function rejection(promise: Promise<unknown>): Promise<ScoutError> {
  try {
    await promise;
  } catch (err: unknown) {
    return err as ScoutError;
  }
  throw new Error('expected a rejection');
}

async function eventually(condition: () => Promise<boolean>): Promise<boolean> {
  const deadline = Date.now() + 1000;
  while (!(await condition())) {
    if (Date.now() > deadline) return false;
    await new Promise((r) => setTimeout(r, 5));
  }
  return true;
}

async function portIsFree(port: number): Promise<boolean> {
  return findFreePort(port, 0).then(
    (found) => found === port,
    () => false,
  );
}

describe('Google sign-in through the browser', () => {
  it('opens the browser only after the loopback listener is up, with PKCE S256, state, offline access and the two read scopes', async () => {
    const fake = makeGoogleFake();

    await fake.auth.signIn();
    expect(fake.listenerUpWhenBrowserOpened).toBe(true);
    await fake.auth.settled();

    const authorize = fake.authorizeUrls[0];
    const q = authorize?.searchParams;
    const verifier = tokenPosts(fake)[0]?.form['code_verifier'] ?? 'missing';
    expect(`${authorize?.origin}${authorize?.pathname}`).toBe(AUTHORIZE_URL);
    expect(q?.get('client_id')).toBe(CLIENT_ID);
    expect(q?.get('redirect_uri')).toMatch(/^http:\/\/127\.0\.0\.1:\d+\/auth\/callback$/);
    expect(q?.get('response_type')).toBe('code');
    expect(q?.get('scope')).toBe(SCOPES);
    expect(q?.get('code_challenge_method')).toBe('S256');
    expect(q?.get('code_challenge')).toBe(
      createHash('sha256').update(verifier).digest('base64url'),
    );
    expect(verifier.length).toBeGreaterThanOrEqual(43);
    expect(q?.get('state')?.length).toBeGreaterThanOrEqual(43);
    expect(q?.get('access_type')).toBe('offline');
    expect(q?.get('prompt')).toBe('consent');
    expect(authorize?.toString()).not.toContain(CLIENT_SECRET);
  });

  it('exchanges the code with the client secret and stores the session in the vault and the token file with mode 0600 through a temporary file', async () => {
    const fake = makeGoogleFake();

    await connect(fake);
    const status = await fake.auth.status();

    const redirectUri = fake.authorizeUrls[0]?.searchParams.get('redirect_uri');
    const posts = tokenPosts(fake);
    expect(posts).toHaveLength(1);
    expect(posts[0]?.method).toBe('POST');
    expect(posts[0]?.form).toEqual({
      code: AUTH_CODE,
      client_id: CLIENT_ID,
      client_secret: CLIENT_SECRET,
      redirect_uri: redirectUri ?? 'missing',
      grant_type: 'authorization_code',
      code_verifier: posts[0]?.form['code_verifier'] ?? 'missing',
    });
    expect(JSON.parse(fake.vault.get('youtube.session') ?? '{}')).toEqual({
      accessToken: 'ya29.first-access',
      refreshToken: '1//refresh-first',
      expiresAt: fake.clock.now + ONE_HOUR_MS,
      channelId: 'UCscoutTestChannel',
      channelTitle: 'Scout Test Channel',
    });
    expect(fake.files.get(TOKEN_FILE)?.mode).toBe(0o600);
    expect(tokenFile(fake)).toEqual({
      access_token: 'ya29.first-access',
      expires_at: fake.clock.now + ONE_HOUR_MS,
    });
    expect(fake.ops).toContain('write youtube-token.json.tmp mode=600');
    expect(fake.ops.indexOf('rename youtube-token.json.tmp -> youtube-token.json')).toBe(
      fake.ops.indexOf('write youtube-token.json.tmp mode=600') + 1,
    );
    expect(fake.files.has(`${TOKEN_FILE}.tmp`)).toBe(false);
    expect(status).toEqual({
      configured: true,
      connected: true,
      method: 'oauth',
      channelId: 'UCscoutTestChannel',
      channelTitle: 'Scout Test Channel',
      pending: false,
      error: null,
      clientConfigPath: CLIENT_FILE,
      source: 'file',
    });
    expect(await fake.auth.workerEnv()).toEqual({ SCOUT__PLUGINS__YOUTUBE__TOKENFILE: TOKEN_FILE });
    expect(fake.connectionChanges).toBe(1);
  });

  it('reads the channel identity once with the bearer token, and an account without a channel is still connected', async () => {
    const fake = makeGoogleFake();
    fake.mine = () => json({ kind: 'youtube#channelListResponse', items: [] });

    await connect(fake);
    const status = await fake.auth.status();

    const identity = fake.requests.filter((r) => r.url.startsWith(CHANNELS_URL));
    expect(identity).toEqual([
      {
        url: `${CHANNELS_URL}?part=snippet&mine=true`,
        method: 'GET',
        form: {},
        authorization: 'Bearer ya29.first-access',
      },
    ]);
    expect(status.connected).toBe(true);
    expect(status.channelTitle).toBe('No channel on this account');
  });

  it('reads the client id and secret from the local file when the env vars are absent, and reports not configured when neither exists', async () => {
    const fromFile = makeGoogleFake();
    expect((await fromFile.auth.status()).configured).toBe(true);
    await connect(fromFile);
    const neither = makeGoogleFake();
    neither.files.delete(CLIENT_FILE);

    const err = await rejection(neither.auth.signIn());
    const notConfigured = await neither.auth.status();

    expect(fromFile.authorizeUrls[0]?.searchParams.get('client_id')).toBe(CLIENT_ID);
    expect(tokenPosts(fromFile)[0]?.form['client_secret']).toBe(CLIENT_SECRET);
    expect(err.code).toBe('youtube/not-configured');
    expect(notConfigured).toMatchObject({
      configured: false,
      connected: false,
      pending: false,
      clientConfigPath: CLIENT_FILE,
    });
    expect(neither.authorizeUrls).toEqual([]);
    expect(neither.requests).toEqual([]);
  });

  it('reads the client file Google Cloud Console downloads for a Desktop app as it is', async () => {
    const fake = makeGoogleFake();
    fake.files.set(CLIENT_FILE, {
      data: JSON.stringify({
        installed: {
          client_id: CLIENT_ID,
          project_id: 'scout-youtube',
          auth_uri: 'https://accounts.google.com/o/oauth2/auth',
          token_uri: 'https://oauth2.googleapis.com/token',
          client_secret: CLIENT_SECRET,
          redirect_uris: ['http://localhost'],
        },
      }),
      mode: 0o600,
    });

    expect((await fake.auth.status()).configured).toBe(true);
    await connect(fake);

    expect(fake.authorizeUrls[0]?.searchParams.get('client_id')).toBe(CLIENT_ID);
    expect(tokenPosts(fake)[0]?.form['client_secret']).toBe(CLIENT_SECRET);
    expect((await fake.auth.status()).connected).toBe(true);
  });

  it('reports not configured, and says the client must be a Desktop app, for a Web application client file', async () => {
    const fake = makeGoogleFake();
    fake.files.set(CLIENT_FILE, {
      data: JSON.stringify({ web: { client_id: CLIENT_ID, client_secret: CLIENT_SECRET } }),
      mode: 0o600,
    });

    const status = await fake.auth.status();
    const err = await rejection(fake.auth.signIn());

    const hint =
      'The Google OAuth client in the file is a Web application; Sign in with Google needs an OAuth client of type Desktop app.';
    expect(status.configured).toBe(false);
    expect(status.error).toBe(hint);
    expect(err.code).toBe('youtube/not-configured');
    expect(err.message).toBe(hint);
    expect(fake.authorizeUrls).toEqual([]);
    expect(fake.requests).toEqual([]);
  });

  it('uses the embedded client when neither the env nor the file provides one', async () => {
    const fake = makeGoogleFake({
      defaultClient: {
        clientId: 'embedded-client.apps.googleusercontent.com',
        clientSecret: 'GOCSPX-embedded-secret',
      },
    });
    fake.files.delete(CLIENT_FILE);

    expect(await fake.auth.status()).toMatchObject({ configured: true, source: 'embedded' });
    await connect(fake);

    expect(fake.authorizeUrls[0]?.searchParams.get('client_id')).toBe(
      'embedded-client.apps.googleusercontent.com',
    );
    expect(tokenPosts(fake)[0]?.form['client_secret']).toBe('GOCSPX-embedded-secret');
    expect((await fake.auth.status()).connected).toBe(true);
    expect(fake.logs.join('\n')).not.toContain('GOCSPX-embedded-secret');
  });

  it('uses the bundled client file from the app resources when env and user file are absent', async () => {
    const fake = makeGoogleFake({ bundledClientPath: BUNDLED_FILE });
    fake.files.delete(CLIENT_FILE);
    fake.files.set(BUNDLED_FILE, {
      data: JSON.stringify({
        client_id: 'bundled-client.apps.googleusercontent.com',
        client_secret: 'GOCSPX-bundled-secret',
      }),
      mode: 0o644,
    });

    expect(await fake.auth.status()).toMatchObject({ configured: true, source: 'bundled' });
    await connect(fake);

    expect(fake.authorizeUrls[0]?.searchParams.get('client_id')).toBe(
      'bundled-client.apps.googleusercontent.com',
    );
    expect(tokenPosts(fake)[0]?.form['client_secret']).toBe('GOCSPX-bundled-secret');
    expect(fake.logs.join('\n')).not.toContain('GOCSPX-bundled-secret');
  });

  it.each([
    { source: 'env', from: ['env', 'file', 'bundled', 'embedded'] },
    { source: 'file', from: ['file', 'bundled', 'embedded'] },
    { source: 'bundled', from: ['bundled', 'embedded'] },
    { source: 'embedded', from: ['embedded'] },
  ] as const)(
    'the $source client wins over every source after it',
    async ({ source, from: list }) => {
      const from: readonly string[] = list;
      const id = (name: string): string => `${name}-client.apps.googleusercontent.com`;
      const fake = makeGoogleFake({
        env: from.includes('env')
          ? { SCOUT_GOOGLE_CLIENT_ID: id('env'), SCOUT_GOOGLE_CLIENT_SECRET: 'GOCSPX-env' }
          : {},
        bundledClientPath: BUNDLED_FILE,
        defaultClient: { clientId: id('embedded'), clientSecret: 'GOCSPX-embedded' },
      });
      fake.files.delete(CLIENT_FILE);
      for (const [name, file] of [
        ['file', CLIENT_FILE],
        ['bundled', BUNDLED_FILE],
      ] as const) {
        if (!from.includes(name)) continue;
        fake.files.set(file, {
          data: JSON.stringify({ client_id: id(name), client_secret: `GOCSPX-${name}` }),
          mode: 0o600,
        });
      }

      const status = await fake.auth.status();
      await connect(fake);

      expect(status.source).toBe(source);
      expect(fake.authorizeUrls[0]?.searchParams.get('client_id')).toBe(id(source));
      expect(tokenPosts(fake)[0]?.form['client_secret']).toBe(`GOCSPX-${source}`);
    },
  );

  it.each([
    { clientId: '', clientSecret: '' },
    { clientId: 'embedded-client.apps.googleusercontent.com', clientSecret: '' },
  ])('empty embedded constants leave sign-in not configured (%o)', async (defaultClient) => {
    const fake = makeGoogleFake({ defaultClient });
    fake.files.delete(CLIENT_FILE);

    const status = await fake.auth.status();
    const err = await rejection(fake.auth.signIn());

    expect(status).toMatchObject({ configured: false, source: null });
    expect(err.code).toBe('youtube/not-configured');
    expect(fake.requests).toEqual([]);
  });

  it('prefers SCOUT_GOOGLE_CLIENT_ID and SCOUT_GOOGLE_CLIENT_SECRET over the file when both are set', async () => {
    const fake = makeGoogleFake({
      env: {
        SCOUT_GOOGLE_CLIENT_ID: 'env-client.apps.googleusercontent.com',
        SCOUT_GOOGLE_CLIENT_SECRET: 'GOCSPX-env-secret',
      },
    });

    await connect(fake);

    expect(fake.authorizeUrls[0]?.searchParams.get('client_id')).toBe(
      'env-client.apps.googleusercontent.com',
    );
    expect(tokenPosts(fake)[0]?.form['client_secret']).toBe('GOCSPX-env-secret');
  });

  it('treats a client file without both fields as not configured', async () => {
    const fake = makeGoogleFake({ env: { SCOUT_GOOGLE_CLIENT_ID: 'only-the-id' } });
    fake.files.set(CLIENT_FILE, { data: JSON.stringify({ client_id: CLIENT_ID }), mode: 0o600 });

    const status = await fake.auth.status();

    expect(status.configured).toBe(false);
  });

  it('rejects a callback whose state does not match', async () => {
    const fake = makeGoogleFake({
      awaitCallback: async () => ({ code: AUTH_CODE, state: 'forged-state' }),
    });

    await connect(fake);
    const status = await fake.auth.status();

    expect(status.error).toBe(
      'The answer from the browser did not belong to this sign-in, so nothing was saved. Sign in again.',
    );
    expect(status.connected).toBe(false);
    expect(tokenPosts(fake)).toHaveLength(0);
    expect(fake.vault.has('youtube.session')).toBe(false);
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(fake.connectionChanges).toBe(0);
  });

  it('a denied consent is reported and nothing is stored', async () => {
    const fake = makeGoogleFake();
    fake.callbackParams = (authorize) => ({
      error: 'access_denied',
      state: authorize.searchParams.get('state') ?? '',
    });

    await connect(fake);
    const status = await fake.auth.status();

    expect(status.error).toBe(
      'The Google sign-in was cancelled or timed out. Click Sign in with Google to try again.',
    );
    expect(status.connected).toBe(false);
    expect(status.pending).toBe(false);
    expect(tokenPosts(fake)).toHaveLength(0);
    expect(fake.vault.has('youtube.session')).toBe(false);
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
  });

  it('says it is waiting for the browser and refuses a second sign-in until the first one ends', async () => {
    const fake = makeGoogleFake();
    fake.browserCallsBack = false;

    const first = await fake.auth.signIn();
    const second = await rejection(fake.auth.signIn());
    fake.auth.cancelSignIn();
    await fake.auth.settled();

    expect(first.pending).toBe(true);
    expect(second.code).toBe('youtube/already-in-progress');
    expect(fake.authorizeUrls).toHaveLength(1);
  });

  it('Cancel stops the sign-in without an error, exchanges nothing and releases the port', async () => {
    const port = await freePort();
    const fake = makeGoogleFake({ findFreePort: async () => port });
    fake.browserCallsBack = false;

    await fake.auth.signIn();
    fake.auth.cancelSignIn();
    await fake.auth.settled();
    const status = await fake.auth.status();

    expect(status.pending).toBe(false);
    expect(status.error).toBeNull();
    expect(await eventually(() => portIsFree(port))).toBe(true);
    expect(tokenPosts(fake)).toHaveLength(0);
  });
});

describe('Google sign-in failing calls', () => {
  it('the token endpoint answering 5xx or timing out leaves the user signed out with a clear message', async () => {
    const unavailable = makeGoogleFake();
    unavailable.tokenReplies.push(json({ error: 'internal_failure' }, 503));
    const timedOut = makeGoogleFake();
    timedOut.tokenReplies.push(new DOMException('The operation timed out.', 'TimeoutError'));

    await connect(unavailable);
    await connect(timedOut);
    const afterUnavailable = await unavailable.auth.status();
    const afterTimeout = await timedOut.auth.status();

    expect(afterUnavailable.error).toBe(
      'Google could not finish the sign-in (HTTP 503). Sign in again in a moment.',
    );
    expect(afterTimeout.error).toBe(
      'Could not reach Google to finish the sign-in. Check the connection and sign in again.',
    );
    for (const [fake, status] of [
      [unavailable, afterUnavailable],
      [timedOut, afterTimeout],
    ] as const) {
      expect(status.connected).toBe(false);
      expect(status.pending).toBe(false);
      expect(fake.vault.has('youtube.session')).toBe(false);
      expect(fake.files.has(TOKEN_FILE)).toBe(false);
      expect(fake.connectionChanges).toBe(0);
    }
  });

  it('stores nothing when YouTube cannot say which channel signed in', async () => {
    const fake = makeGoogleFake();
    fake.mine = () => {
      throw new TypeError('fetch failed');
    };

    await connect(fake);
    const status = await fake.auth.status();

    expect(status.error).toBe('Could not reach YouTube to read the signed-in channel.');
    expect(status.connected).toBe(false);
    expect(fake.vault.has('youtube.session')).toBe(false);
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
  });

  it('fails when the browser cannot be opened, and releases the loopback port', async () => {
    const port = await freePort();
    const fake = makeGoogleFake({ findFreePort: async () => port });
    fake.browserFails = true;

    const err = await rejection(fake.auth.signIn());

    expect(err.code).toBe('youtube/browser-failed');
    expect((await fake.auth.status()).pending).toBe(false);
    expect(await eventually(() => portIsFree(port))).toBe(true);
    expect(tokenPosts(fake)).toHaveLength(0);
  });
});

describe('Google session refresh', () => {
  function failNextWrites(fake: ReturnType<typeof makeGoogleFake>, count: number): void {
    const realWrite = fake.deps.fs.writeFile;
    let failures = count;
    fake.deps.fs.writeFile = async (p, data, options) => {
      if (failures-- > 0) {
        throw Object.assign(new Error(`EACCES: open ${p} with ${data}`), { name: 'EACCES' });
      }
      return realWrite(p, data, options);
    };
  }

  it('keeps the session when the token file cannot be written at sign-in, and the next check writes it through the temporary file', async () => {
    const fake = makeGoogleFake();
    failNextWrites(fake, 1);

    await connect(fake);
    const fileAfterSignIn = fake.files.has(TOKEN_FILE);
    const status = await fake.auth.status();
    const env = await fake.auth.workerEnv();
    fake.ops.length = 0;
    await fake.auth.refreshIfDue();

    expect(fileAfterSignIn).toBe(false);
    expect(fake.vault.has('youtube.session')).toBe(true);
    expect(status).toMatchObject({ connected: true, method: 'oauth', error: null });
    expect(env).toEqual({ SCOUT__PLUGINS__YOUTUBE__TOKENFILE: TOKEN_FILE });
    expect(
      fake.logs.filter((line) => line.includes('could not write the token file')),
    ).toHaveLength(1);
    expect(fake.logs.join('\n')).not.toContain('ya29.');
    expect(fake.ops).toEqual([
      'write youtube-token.json.tmp mode=600',
      'rename youtube-token.json.tmp -> youtube-token.json',
    ]);
    expect(tokenFile(fake)).toEqual({
      access_token: 'ya29.first-access',
      expires_at: fake.clock.now + ONE_HOUR_MS,
    });
  });

  it('keeps the rotated session when the token file cannot be written after a refresh, and the next check rewrites it without another refresh', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    fake.tokenReplies.push({ access_token: 'ya29.second-access', expires_in: 3599 });
    fake.clock.now += ONE_HOUR_MS - 4 * 60_000;
    failNextWrites(fake, 1);

    await fake.auth.refreshIfDue();
    const fileAfterRefresh = tokenFile(fake)?.access_token;
    await fake.auth.refreshIfDue();

    const session = JSON.parse(fake.vault.get('youtube.session') ?? '{}') as {
      accessToken?: string;
    };
    expect(fileAfterRefresh).toBe('ya29.first-access');
    expect(session.accessToken).toBe('ya29.second-access');
    expect(tokenPosts(fake).filter((r) => r.form['grant_type'] === 'refresh_token')).toHaveLength(
      1,
    );
    expect(tokenFile(fake)?.access_token).toBe('ya29.second-access');
    expect(fake.files.has(`${TOKEN_FILE}.tmp`)).toBe(false);
    expect(fake.logs.join('\n')).not.toContain('ya29.');
  });

  it('refreshes with the client secret before the token expires and rewrites the token file', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    fake.tokenReplies.push(
      { access_token: 'ya29.second-access', expires_in: 3599, token_type: 'Bearer' },
      { access_token: 'ya29.third-access', expires_in: 3599, token_type: 'Bearer' },
    );

    fake.clock.now += ONE_HOUR_MS - 4 * 60_000;
    await fake.auth.refreshIfDue();
    const afterFirst = tokenFile(fake);
    fake.clock.now += ONE_HOUR_MS - 4 * 60_000;
    await fake.auth.refreshIfDue();

    const refreshes = tokenPosts(fake)
      .filter((r) => r.form['grant_type'] === 'refresh_token')
      .map((r) => r.form);
    expect(refreshes).toEqual([
      {
        grant_type: 'refresh_token',
        refresh_token: '1//refresh-first',
        client_id: CLIENT_ID,
        client_secret: CLIENT_SECRET,
      },
      {
        grant_type: 'refresh_token',
        refresh_token: '1//refresh-first',
        client_id: CLIENT_ID,
        client_secret: CLIENT_SECRET,
      },
    ]);
    expect(afterFirst?.access_token).toBe('ya29.second-access');
    expect(tokenFile(fake)).toEqual({
      access_token: 'ya29.third-access',
      expires_at: fake.clock.now + ONE_HOUR_MS,
    });
    expect(fake.files.get(TOKEN_FILE)?.mode).toBe(0o600);
    expect(fake.connectionChanges).toBe(1);
  });

  it('does not refresh while more than five minutes remain', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    const before = fake.requests.length;

    fake.clock.now += ONE_HOUR_MS - 6 * 60_000;
    await fake.auth.refreshIfDue();

    expect(fake.requests.length).toBe(before);
  });

  it('invalid_grant on refresh clears the session and the token file', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    fake.tokenReplies.push(
      json(
        { error: 'invalid_grant', error_description: 'Token has been expired or revoked.' },
        400,
      ),
    );

    fake.clock.now += ONE_HOUR_MS;
    fake.ops.length = 0;
    await fake.auth.refreshIfDue();
    const status = await fake.auth.status();

    expect(status.connected).toBe(false);
    expect(status.error).toBe(
      'Google no longer accepts the saved sign-in. Sign in with Google again.',
    );
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(fake.vault.has('youtube.session')).toBe(false);
    expect(fake.ops.indexOf('rm youtube-token.json')).toBeLessThan(
      fake.ops.indexOf('vault.delete youtube.session'),
    );
    expect(fake.ops.indexOf('rm youtube-token.json')).toBeGreaterThan(-1);
    expect(fake.connectionChanges).toBe(2);
    expect(await fake.auth.workerEnv()).toEqual({});
  });

  it('a 5xx or a network failure on refresh keeps the session for the next attempt', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    fake.tokenReplies.push(json({ error: 'backend_error' }, 503), new TypeError('fetch failed'));

    fake.clock.now += ONE_HOUR_MS;
    await fake.auth.refreshIfDue();
    await fake.auth.refreshIfDue();

    expect((await fake.auth.status()).connected).toBe(true);
    expect(tokenFile(fake)?.access_token).toBe('ya29.first-access');
    expect(fake.connectionChanges).toBe(1);
  });

  it('checks the token as soon as the system wakes from sleep, and stops listening when stopped', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    await fake.auth.start();
    fake.tokenReplies.push({ access_token: 'ya29.second-access', expires_in: 3599 });

    fake.clock.now += ONE_HOUR_MS;
    await Promise.all(fake.resumeListeners.map((wake) => wake()));
    fake.auth.stop();

    expect(tokenFile(fake)?.access_token).toBe('ya29.second-access');
    expect(fake.resumeListeners).toHaveLength(0);
  });
});

describe('Google sign-out', () => {
  it('sign-out revokes the token and removes the file before the vault entry', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    fake.ops.length = 0;

    const status = await fake.auth.signOut();

    const revokes = fake.requests.filter((r) => r.url === REVOKE_URL);
    expect(revokes.map((r) => [r.method, r.form])).toEqual([
      ['POST', { token: '1//refresh-first' }],
    ]);
    expect(fake.ops.slice(0, 4)).toEqual([
      `POST ${REVOKE_URL}`,
      'rm youtube-token.json',
      'rm youtube-token.json.tmp',
      'vault.delete youtube.session',
    ]);
    expect(status).toMatchObject({ connected: false, method: null, channelTitle: null });
    expect(fake.connectionChanges).toBe(2);
    expect(await fake.auth.workerEnv()).toEqual({});
  });

  it('still signs out on this computer when the revocation cannot reach Google', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    fake.revoke = () => {
      throw new TypeError('fetch failed');
    };

    const status = await fake.auth.signOut();

    expect(status.connected).toBe(false);
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(fake.vault.has('youtube.session')).toBe(false);
  });

  it('stays connected and says why when the token file cannot be removed', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    fake.deps.fs.rm = async () => {
      throw Object.assign(new Error('EBUSY'), { name: 'EBUSY' });
    };

    const err = await rejection(fake.auth.signOut());

    expect(err.code).toBe('youtube/token-file-not-removed');
    expect(fake.vault.has('youtube.session')).toBe(true);
    expect((await fake.auth.status()).connected).toBe(true);
  });
});

describe('Google boundaries', () => {
  it('never logs the client secret, the code, a token or the key', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    const verifier = tokenPosts(fake)[0]?.form['code_verifier'] ?? 'missing';
    const statuses: unknown[] = [await fake.auth.status()];
    fake.tokenReplies.push(
      { access_token: 'ya29.second-access', expires_in: 3599 },
      json({ error: 'backend_error' }, 503),
      json({ error: 'invalid_grant' }, 400),
    );
    fake.clock.now += ONE_HOUR_MS;
    await fake.auth.refreshIfDue();
    fake.clock.now += ONE_HOUR_MS;
    await fake.auth.refreshIfDue();
    await fake.auth.refreshIfDue();
    statuses.push(await fake.auth.status());
    await connect(fake);
    fake.revoke = () => json({ error: 'invalid_token' }, 400);
    await fake.auth.signOut();
    fake.keyCheck = (url) => {
      throw new TypeError(`fetch failed: ${url}`);
    };
    statuses.push(await rejection(fake.auth.saveApiKey(API_KEY)).then((e) => e.message));
    fake.keyCheck = () => json({ items: [] });
    statuses.push(await fake.auth.saveApiKey(API_KEY));
    await fake.auth.signOut();

    const everything = [...fake.logs, JSON.stringify(statuses)].join('\n');
    expect(fake.logs.length).toBeGreaterThan(8);
    for (const secret of [CLIENT_SECRET, AUTH_CODE, verifier, 'ya29.', '1//refresh', API_KEY]) {
      expect(everything).not.toContain(secret);
    }
  });

  it("only POSTs to Google's token and revoke endpoints and only GETs to googleapis.com", async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    fake.tokenReplies.push({ access_token: 'ya29.second-access', expires_in: 3599 });
    fake.clock.now += ONE_HOUR_MS;
    await fake.auth.refreshIfDue();
    await fake.auth.status();
    await fake.auth.saveApiKey(API_KEY);
    await fake.auth.signOut();

    const asLine = (r: { method: string; url: string }): string => `${r.method} ${r.url}`;
    const posts = fake.requests.filter((r) => r.method === 'POST');
    const others = fake.requests.filter((r) => r.method !== 'POST');
    expect(posts.length).toBeGreaterThan(0);
    expect(others.length).toBeGreaterThan(0);
    expect(posts.filter((r) => r.url !== TOKEN_URL && r.url !== REVOKE_URL).map(asLine)).toEqual(
      [],
    );
    expect(
      others
        .filter(
          (r) => r.method !== 'GET' || !r.url.startsWith('https://www.googleapis.com/youtube/v3/'),
        )
        .map(asLine),
    ).toEqual([]);
    expect(
      fake.requests.filter((r) => r.url.startsWith('https://accounts.google.com/')).map(asLine),
    ).toEqual([]);
  });
});
