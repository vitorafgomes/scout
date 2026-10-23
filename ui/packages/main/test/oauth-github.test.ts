import { describe, it, expect } from 'bun:test';
import { ScoutError } from '@scout/shared';
import {
  DEVICE_CODE,
  TOKEN_FILE,
  connect,
  makeGitHubFake,
  tokenFileToken,
} from './github-fakes.js';

const EIGHT_HOURS_MS = 28_800_000;

describe('GitHubAuth device flow', () => {
  it('asks permission through the browser: returns the code to type on GitHub, then is connected as the account that approved', async () => {
    let approve: () => void = () => {};
    const approved = new Promise<void>((resolve) => {
      approve = resolve;
    });
    const fake = makeGitHubFake({ sleep: () => approved });
    fake.tokenReplies.push({
      access_token: 'ghu_first',
      expires_in: 28800,
      refresh_token: 'ghr_first',
    });

    const pending = await fake.auth.signIn();
    const waiting = await fake.auth.status();
    approve();
    await fake.auth.settled();
    const connected = await fake.auth.status();

    expect(pending).toEqual({
      userCode: 'WDJB-MJHT',
      verificationUri: 'https://github.com/login/device',
    });
    expect(waiting.connected).toBe(false);
    expect(waiting.pending).toEqual(pending);
    expect(connected.connected).toBe(true);
    expect(connected.login).toBe('vitorafgomes');
    expect(connected.pending).toBeNull();
    expect(connected.installed).toBe(true);
  });

  it('polls with the client id and the device code only, never a client secret', async () => {
    const fake = makeGitHubFake();
    await connect(fake);

    const polls = fake.requests.filter(
      (r) => r.url === 'https://github.com/login/oauth/access_token',
    );
    expect(polls.map((r) => r.form)).toEqual([
      {
        client_id: 'Iv23liScoutClientId',
        device_code: DEVICE_CODE,
        grant_type: 'urn:ietf:params:oauth:grant-type:device_code',
      },
      {
        client_id: 'Iv23liScoutClientId',
        device_code: DEVICE_CODE,
        grant_type: 'urn:ietf:params:oauth:grant-type:device_code',
      },
    ]);
  });

  it('never polls faster than the interval and slows down by five seconds, or to the interval GitHub sends', async () => {
    const fake = makeGitHubFake();
    fake.tokenReplies.push(
      { error: 'authorization_pending' },
      { error: 'slow_down', interval: 10 },
      { error: 'slow_down' },
      { access_token: 'ghu_first', expires_in: 28800, refresh_token: 'ghr_first' },
    );

    await fake.auth.signIn();
    await fake.auth.settled();

    expect(fake.sleeps).toEqual([5000, 5000, 10000, 15000]);
    expect((await fake.auth.status()).connected).toBe(true);
  });

  it('stores the session in the vault and writes the token file with mode 0600 through a temporary file', async () => {
    const fake = makeGitHubFake();
    await connect(fake);

    expect(fake.vault.has('github.session')).toBe(true);
    expect(fake.files.get(TOKEN_FILE)?.mode).toBe(0o600);
    expect(tokenFileToken(fake)).toBe('ghu_first');
    expect(fake.files.has(`${TOKEN_FILE}.tmp`)).toBe(false);
    expect(fake.connectionChanges).toBe(1);
  });

  it('says the owner cancelled when GitHub answers access_denied, and clears the pending code', async () => {
    const fake = makeGitHubFake();
    fake.tokenReplies.push({ error: 'access_denied' });

    await fake.auth.signIn();
    await fake.auth.settled();
    const status = await fake.auth.status();

    expect(status.error).toEqual({
      code: 'github/access-denied',
      message: 'The sign-in was cancelled on GitHub.',
    });
    expect(status.pending).toBeNull();
    expect(status.connected).toBe(false);
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
  });

  it('asks for a new code when GitHub answers expired_token', async () => {
    const fake = makeGitHubFake();
    fake.tokenReplies.push({ error: 'expired_token' });

    await fake.auth.signIn();
    await fake.auth.settled();
    const status = await fake.auth.status();

    expect(status.error?.code).toBe('github/expired-token');
    expect(status.error?.message).toBe(
      'The code expired. Click Connect with GitHub to get a new one.',
    );
    expect(status.pending).toBeNull();
  });

  it('fails with a typed network error and clears the pending code when GitHub is unreachable during the poll', async () => {
    const fake = makeGitHubFake();
    fake.tokenReplies.push(new TypeError('fetch failed'));

    await fake.auth.signIn();
    await fake.auth.settled();
    const status = await fake.auth.status();

    expect(status.error).toEqual({
      code: 'github/network-error',
      message: 'Could not reach GitHub to finish the sign-in.',
    });
    expect(status.pending).toBeNull();
    expect(status.connected).toBe(false);
  });

  it('stops polling without an error when the owner cancels', async () => {
    let wake: () => void = () => {};
    const fake = makeGitHubFake({
      sleep: (_ms, signal) =>
        new Promise<void>((resolve) => {
          wake = resolve;
          signal.addEventListener('abort', () => resolve());
        }),
    });

    await fake.auth.signIn();
    fake.auth.cancel();
    wake();
    await fake.auth.settled();
    const status = await fake.auth.status();

    expect(status.pending).toBeNull();
    expect(status.error).toBeNull();
    expect(fake.requests.some((r) => r.url.endsWith('/login/oauth/access_token'))).toBe(false);
  });

  it('refuses to start when the Scout GitHub App has no client id yet', async () => {
    const fake = makeGitHubFake({ clientId: '' });

    let thrown: unknown = null;
    try {
      await fake.auth.signIn();
    } catch (err: unknown) {
      thrown = err;
    }

    expect(thrown).toBeInstanceOf(ScoutError);
    expect((thrown as ScoutError).code).toBe('github/not-configured');
    expect((thrown as ScoutError).message).toBe('The Scout GitHub App is not configured yet.');
    expect((await fake.auth.status()).configured).toBe(false);
    expect(fake.requests).toEqual([]);
  });

  it('rejects a verification page that is not on github.com', async () => {
    const fake = makeGitHubFake();
    const realFetch = fake.deps.fetch;
    fake.deps.fetch = (async (input: unknown, init?: RequestInit) =>
      String(input) === 'https://github.com/login/device/code'
        ? new Response(
            JSON.stringify({
              device_code: DEVICE_CODE,
              user_code: 'WDJB-MJHT',
              verification_uri: 'https://github.com.evil.example/login/device',
              expires_in: 900,
              interval: 5,
            }),
          )
        : realFetch(input as string, init)) as typeof globalThis.fetch;

    await expect(fake.auth.signIn()).rejects.toMatchObject({ code: 'github/unexpected-response' });
    expect((await fake.auth.status()).pending).toBeNull();
  });

  it('reports the App as not installed and offers its install page when the account has no installation', async () => {
    const fake = makeGitHubFake();
    fake.installations = { total_count: 0, installations: [] };
    await connect(fake);

    const status = await fake.auth.status();

    expect(status.connected).toBe(true);
    expect(status.installed).toBe(false);
    expect(status.installUrl).toBe('https://github.com/apps/scout-chatops/installations/new');
  });
});

describe('GitHubAuth failing calls', () => {
  it('fails with a typed network error when GitHub is unreachable for the device code', async () => {
    const fake = makeGitHubFake();
    const realFetch = fake.deps.fetch;
    fake.deps.fetch = (async (input: unknown, init?: RequestInit) => {
      if (String(input) === 'https://github.com/login/device/code') {
        throw new TypeError('fetch failed');
      }
      return realFetch(input as string, init);
    }) as typeof globalThis.fetch;

    let thrown: unknown = null;
    try {
      await fake.auth.signIn();
    } catch (err: unknown) {
      thrown = err;
    }

    expect((thrown as ScoutError).code).toBe('github/network-error');
    expect((thrown as ScoutError).message).toBe('Could not reach GitHub to start the sign-in.');
    expect((await fake.auth.status()).pending).toBeNull();
  });

  it('stays disconnected and stores nothing when GitHub cannot say who approved', async () => {
    const fake = makeGitHubFake();
    const realFetch = fake.deps.fetch;
    fake.deps.fetch = (async (input: unknown, init?: RequestInit) => {
      if (String(input) === 'https://api.github.com/user') throw new TypeError('fetch failed');
      return realFetch(input as string, init);
    }) as typeof globalThis.fetch;
    fake.tokenReplies.push({
      access_token: 'ghu_first',
      expires_in: 28800,
      refresh_token: 'ghr_first',
    });

    await fake.auth.signIn();
    await fake.auth.settled();
    const status = await fake.auth.status();

    expect(status.error).toEqual({
      code: 'github/network-error',
      message: 'Could not reach GitHub.',
    });
    expect(status.pending).toBeNull();
    expect(status.connected).toBe(false);
    expect(fake.vault.has('github.session')).toBe(false);
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(fake.connectionChanges).toBe(0);
  });

  it('keeps the session when the token file cannot be written, and writes it on the next check', async () => {
    const fake = makeGitHubFake();
    const realWrite = fake.deps.fs.writeFile;
    let failures = 1;
    fake.deps.fs.writeFile = async (path, data, options) => {
      if (failures-- > 0) throw Object.assign(new Error('EACCES: ghu_first'), { name: 'EACCES' });
      return realWrite(path, data, options);
    };

    await connect(fake);
    const fileAfterConnect = fake.files.has(TOKEN_FILE);
    await fake.auth.refreshIfDue();

    expect(fake.vault.has('github.session')).toBe(true);
    expect(fileAfterConnect).toBe(false);
    expect(
      fake.logs.filter((line) => line.includes('could not write the token file')),
    ).toHaveLength(1);
    expect(fake.logs.join('\n')).not.toContain('ghu_');
    expect(tokenFileToken(fake)).toBe('ghu_first');
  });
});

describe('GitHubAuth refresh and sign-out', () => {
  it('does not refresh while the access token has more than five minutes left', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    const before = fake.requests.length;

    fake.clock.now += EIGHT_HOURS_MS - 6 * 60_000;
    await fake.auth.refreshIfDue();

    expect(fake.requests.length).toBe(before);
  });

  it('refreshes before expiry and rotates both tokens, using the new refresh token next time', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    fake.tokenReplies.push(
      { access_token: 'ghu_second', expires_in: 28800, refresh_token: 'ghr_second' },
      { access_token: 'ghu_third', expires_in: 28800, refresh_token: 'ghr_third' },
    );

    fake.clock.now += EIGHT_HOURS_MS - 4 * 60_000;
    await fake.auth.refreshIfDue();
    const afterFirst = tokenFileToken(fake);
    fake.clock.now += EIGHT_HOURS_MS - 4 * 60_000;
    await fake.auth.refreshIfDue();

    const refreshes = fake.requests
      .filter((r) => r.form['grant_type'] === 'refresh_token')
      .map((r) => r.form);
    expect(refreshes).toEqual([
      { client_id: 'Iv23liScoutClientId', grant_type: 'refresh_token', refresh_token: 'ghr_first' },
      {
        client_id: 'Iv23liScoutClientId',
        grant_type: 'refresh_token',
        refresh_token: 'ghr_second',
      },
    ]);
    expect(afterFirst).toBe('ghu_second');
    expect(tokenFileToken(fake)).toBe('ghu_third');
    expect(fake.connectionChanges).toBe(1);
  });

  it('signs out and deletes the token file when GitHub answers bad_refresh_token', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    fake.tokenReplies.push({ error: 'bad_refresh_token' });

    fake.clock.now += EIGHT_HOURS_MS;
    await fake.auth.refreshIfDue();
    const status = await fake.auth.status();

    expect(status.connected).toBe(false);
    expect(status.error?.code).toBe('github/signed-out');
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(fake.vault.has('github.session')).toBe(false);
    expect(fake.connectionChanges).toBe(2);
  });

  it('keeps the session and retries later when GitHub is unreachable during a refresh', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    fake.tokenReplies.push(new TypeError('fetch failed'));

    fake.clock.now += EIGHT_HOURS_MS;
    await fake.auth.refreshIfDue();

    expect((await fake.auth.status()).connected).toBe(true);
    expect(tokenFileToken(fake)).toBe('ghu_first');
  });

  it('disconnect deletes the token file, clears the session and tells the worker to restart', async () => {
    const fake = makeGitHubFake();
    await connect(fake);

    const status = await fake.auth.signOut();

    expect(status.connected).toBe(false);
    expect(status.login).toBeNull();
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(fake.vault.has('github.session')).toBe(false);
    expect(fake.connectionChanges).toBe(2);
  });
});

describe('GitHubAuth disconnect safety and timing', () => {
  function failingRm(fake: ReturnType<typeof makeGitHubFake>): void {
    fake.deps.fs.rm = async () => {
      throw Object.assign(new Error('EBUSY'), { name: 'EBUSY' });
    };
  }

  it('stays connected and reports a typed error when the token file cannot be removed on disconnect', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    failingRm(fake);

    let thrown: unknown = null;
    try {
      await fake.auth.signOut();
    } catch (err: unknown) {
      thrown = err;
    }
    const status = await fake.auth.status();

    expect((thrown as ScoutError).code).toBe('github/token-file-not-removed');
    expect(fake.vault.has('github.session')).toBe(true);
    expect(status.connected).toBe(true);
    expect(tokenFileToken(fake)).toBe('ghu_first');
    expect(fake.connectionChanges).toBe(1);
  });

  it('stays connected when GitHub answers bad_refresh_token but the token file cannot be removed', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    failingRm(fake);
    fake.tokenReplies.push({ error: 'bad_refresh_token' });

    fake.clock.now += EIGHT_HOURS_MS;
    await fake.auth.refreshIfDue();
    const status = await fake.auth.status();

    expect(status.connected).toBe(true);
    expect(status.error?.code).toBe('github/token-file-not-removed');
    expect(fake.vault.has('github.session')).toBe(true);
    expect(fake.connectionChanges).toBe(1);
  });

  it('refreshes once when two checks start together, and keeps the rotated tokens', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    fake.tokenReplies.push({
      access_token: 'ghu_second',
      expires_in: 28800,
      refresh_token: 'ghr_second',
    });

    fake.clock.now += EIGHT_HOURS_MS - 4 * 60_000;
    await Promise.all([fake.auth.refreshIfDue(), fake.auth.refreshIfDue()]);

    const refreshes = fake.requests.filter((r) => r.form['grant_type'] === 'refresh_token');
    const session = JSON.parse(fake.vault.get('github.session') ?? '{}') as {
      accessToken?: string;
      refreshToken?: string;
    };
    expect(refreshes).toHaveLength(1);
    expect(session.accessToken).toBe('ghu_second');
    expect(session.refreshToken).toBe('ghr_second');
    expect(tokenFileToken(fake)).toBe('ghu_second');
  });

  it('checks the token as soon as the system wakes from sleep, and stops listening when stopped', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    await fake.auth.start();
    fake.tokenReplies.push({
      access_token: 'ghu_second',
      expires_in: 28800,
      refresh_token: 'ghr_second',
    });

    fake.clock.now += EIGHT_HOURS_MS;
    await Promise.all(fake.resumeListeners.map((wake) => wake()));
    fake.auth.stop();

    expect(tokenFileToken(fake)).toBe('ghu_second');
    expect(fake.resumeListeners).toHaveLength(0);
  });
});

describe('GitHubAuth boundaries', () => {
  it('sends only GET requests to api.github.com and only POSTs to the GitHub sign-in endpoints', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    fake.tokenReplies.push({
      access_token: 'ghu_second',
      expires_in: 28800,
      refresh_token: 'ghr_second',
    });
    fake.clock.now += EIGHT_HOURS_MS;
    await fake.auth.refreshIfDue();
    await fake.auth.status();

    const api = fake.requests.filter((r) => r.url.startsWith('https://api.github.com/'));
    const other = fake.requests.filter((r) => !r.url.startsWith('https://api.github.com/'));
    expect(api.length).toBeGreaterThan(0);
    expect(api.every((r) => r.method === 'GET')).toBe(true);
    expect(
      other.every(
        (r) =>
          r.method === 'POST' &&
          (r.url === 'https://github.com/login/device/code' ||
            r.url === 'https://github.com/login/oauth/access_token'),
      ),
    ).toBe(true);
  });

  it('never logs a token or the device code, and never puts one in the status', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    fake.tokenReplies.push(
      { access_token: 'ghu_second', expires_in: 28800, refresh_token: 'ghr_second' },
      { error: 'bad_refresh_token' },
    );
    fake.clock.now += EIGHT_HOURS_MS;
    await fake.auth.refreshIfDue();
    const connected = await fake.auth.status();
    fake.clock.now += EIGHT_HOURS_MS;
    await fake.auth.refreshIfDue();
    await fake.auth.signOut();

    const everything = [...fake.logs, JSON.stringify(connected)].join('\n');
    expect(fake.logs.length).toBeGreaterThan(0);
    expect(everything).not.toContain('ghu_');
    expect(everything).not.toContain('ghr_');
    expect(everything).not.toContain(DEVICE_CODE);
  });
});
