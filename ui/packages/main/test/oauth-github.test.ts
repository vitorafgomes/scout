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

  it.each([
    [
      'times out',
      new DOMException('The operation timed out.', 'TimeoutError'),
      'github/network-error',
    ],
    ['answers 503', { httpStatus: 503 }, 'github/request-failed'],
  ] as const)(
    "when the read of the App's repositories %s, the status still answers connected without a count, and a later failure keeps the last count read",
    async (_case, failure, logged) => {
      const fake = makeGitHubFake();
      fake.installationRepositories.set(101, failure);
      await connect(fake);

      const failed = await fake.auth.status();
      fake.installationRepositories.set(101, { total_count: 6 });
      const read = await fake.auth.status();
      fake.installationRepositories.set(101, failure);
      const failedAgain = await fake.auth.status();

      expect(failed).toMatchObject({
        connected: true,
        installed: true,
        repositories: null,
        error: null,
      });
      expect(read.repositories).toBe(6);
      expect(failedAgain).toMatchObject({
        connected: true,
        installed: true,
        repositories: 6,
        error: null,
      });
      const warnings = fake.logs.filter((l) => l.includes("could not read the App's repositories"));
      expect(warnings).toHaveLength(2);
      expect(warnings.every((l) => l.startsWith('warn ') && l.includes(logged))).toBe(true);
      const everything = [...fake.logs, JSON.stringify([failed, read, failedAgain])].join('\n');
      expect(everything).not.toContain('ghu_');
      expect(everything).not.toContain('ghr_');
    },
  );
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

describe('GitHubAuth access token after a sleep', () => {
  it('never hands out an access token that has already expired', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    fake.tokenReplies.push({
      access_token: 'ghu_second',
      expires_in: 28800,
      refresh_token: 'ghr_second',
    });

    fake.clock.now += EIGHT_HOURS_MS + 60_000;
    const token = await fake.auth.accessToken();

    expect(token).not.toBe('ghu_first');
  });

  it('a token that is due is refreshed before it is handed out, and the refresh is announced once', async () => {
    let refreshed = 0;
    const fake = makeGitHubFake({ onTokenRefreshed: () => (refreshed += 1) });
    await connect(fake);
    fake.tokenReplies.push({
      access_token: 'ghu_second',
      expires_in: 28800,
      refresh_token: 'ghr_second',
    });

    fake.clock.now += EIGHT_HOURS_MS + 60_000;
    const token = await fake.auth.accessToken();

    expect(token).toBe('ghu_second');
    expect(refreshed).toBe(1);
    expect(fake.connectionChanges).toBe(1);
  });

  it('an expired token whose refresh does not reach GitHub is not handed out, and no refresh is announced', async () => {
    let refreshed = 0;
    const fake = makeGitHubFake({ onTokenRefreshed: () => (refreshed += 1) });
    await connect(fake);
    fake.tokenReplies.push(new Error('ECONNRESET'));

    fake.clock.now += EIGHT_HOURS_MS + 60_000;
    const token = await fake.auth.accessToken();

    expect(token).toBeNull();
    expect(refreshed).toBe(0);
    expect(fake.logs.join('\n')).not.toContain('ghu_');
  });

  it('a token due within five minutes whose refresh fails is still handed out until it expires', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    fake.tokenReplies.push(new Error('ECONNRESET'));

    fake.clock.now += EIGHT_HOURS_MS - 2 * 60_000;
    const token = await fake.auth.accessToken();

    expect(token).toBe('ghu_first');
  });
});

describe('GitHubAuth installation repositories', () => {
  const LISTING = 'https://api.github.com/user/installations';
  const repositoriesOf = (id: number): string =>
    `https://api.github.com/user/installations/${id}/repositories?per_page=1`;

  it.each([
    ['no installation', { total_count: 0, installations: [] }, [], 0],
    [
      'one installation with 6 repositories',
      { total_count: 1, installations: [{ id: 101 }] },
      [[101, 6]],
      6,
    ],
    [
      'two installations with 6 and 8 repositories',
      { total_count: 2, installations: [{ id: 101 }, { id: 202 }] },
      [
        [101, 6],
        [202, 8],
      ],
      14,
    ],
  ] as const)(
    'with %s, the status says how many repositories the App is installed on, summed over its installations',
    async (_case, installations, counts, expected) => {
      const fake = makeGitHubFake();
      fake.installations = installations;
      fake.installationRepositories = new Map(counts.map(([id, n]) => [id, { total_count: n }]));
      await connect(fake);
      fake.requests.length = 0;

      const status = await fake.auth.status();

      expect(status.repositories).toBe(expected);
      expect(fake.requests.map((r) => [r.method, r.url, r.authorization])).toEqual([
        ['GET', LISTING, 'Bearer ghu_first'],
        ...counts.map(([id]) => ['GET', repositoriesOf(id), 'Bearer ghu_first']),
      ]);
    },
  );

  it('with a pasted token there is no App session, so the status has no count and GitHub is not asked for one', async () => {
    const fake = makeGitHubFake();
    await fake.auth.saveToken('github_pat_11ABCDEFG0repositories');

    const status = await fake.auth.status();

    expect(status).toMatchObject({ connected: true, method: 'token', repositories: null });
    expect(fake.requests.filter((r) => r.url.startsWith(LISTING))).toEqual([]);
  });

  it('when GitHub lists fewer installations than it counts, the status has no count rather than a smaller one', async () => {
    const fake = makeGitHubFake();
    fake.installations = { total_count: 2, installations: [{ id: 101 }] };
    await connect(fake);

    const status = await fake.auth.status();

    expect(status).toMatchObject({ connected: true, installed: true, repositories: null });
  });

  it('after Disconnect the status has no count, and the next sign-in never shows the count read before it', async () => {
    const fake = makeGitHubFake();
    await connect(fake);
    const connected = await fake.auth.status();

    const disconnected = await fake.auth.signOut();
    fake.installationRepositories.set(101, { httpStatus: 503 });
    await connect(fake);
    const reconnected = await fake.auth.status();

    expect(connected.repositories).toBe(6);
    expect(disconnected).toMatchObject({ connected: false, repositories: null });
    expect(reconnected).toMatchObject({ connected: true, repositories: null });
  });
});

describe('GitHubAuth logs a failed count once per failure streak', () => {
  const failing = [
    ['the read of the App repositories', 'repositories'],
    ['the listing of the App installations', 'listing'],
  ] as const;

  function failReads(
    fake: ReturnType<typeof makeGitHubFake>,
    what: 'repositories' | 'listing',
  ): void {
    if (what === 'repositories') fake.installationRepositories.set(101, { httpStatus: 503 });
    else fake.installations = { httpStatus: 503 };
  }

  function answerReads(fake: ReturnType<typeof makeGitHubFake>): void {
    fake.installations = { total_count: 1, installations: [{ id: 101 }] };
    fake.installationRepositories.set(101, { total_count: 6 });
  }

  const warnings = (fake: ReturnType<typeof makeGitHubFake>): string[] =>
    fake.logs.filter((l) => l.includes("could not read the App's repositories"));

  it.each(failing)(
    'when %s fails three times in a row, exactly one warn line says so, with the error code and no token',
    async (_case, what) => {
      const fake = makeGitHubFake();
      failReads(fake, what);
      await connect(fake);

      await fake.auth.status();
      await fake.auth.status();

      expect(warnings(fake)).toEqual([
        `warn github: could not read the App's repositories [{"code":"github/request-failed"}]`,
      ]);
      expect(fake.logs.join('\n')).not.toContain('ghu_');
      expect(fake.logs.join('\n')).not.toContain('ghr_');
    },
  );

  it.each(failing)(
    'when %s answers again after a failure streak, the next failure logs a second line',
    async (_case, what) => {
      const fake = makeGitHubFake();
      failReads(fake, what);
      await connect(fake);
      await fake.auth.status();
      answerReads(fake);
      const answered = await fake.auth.status();
      failReads(fake, what);

      await fake.auth.status();
      await fake.auth.status();

      expect(answered.repositories).toBe(6);
      expect(warnings(fake)).toHaveLength(2);
    },
  );
});

describe("GitHubAuth never lets GitHub's answer steer the URL or the count", () => {
  const LISTING = 'https://api.github.com/user/installations';
  const repositoriesOf = (id: number): string =>
    `https://api.github.com/user/installations/${id}/repositories?per_page=1`;

  it.each([
    ['an id with a path in it', { total_count: 1, installations: [{ id: '101/../../x' }] }],
    ['an id of 0', { total_count: 1, installations: [{ id: 0 }] }],
    ['an id of -1', { total_count: 1, installations: [{ id: -1 }] }],
    ['an id of 1.5', { total_count: 1, installations: [{ id: 1.5 }] }],
    ['an installation without an id', { total_count: 1, installations: [{}] }],
    ['an installation that is not an object', { total_count: 1, installations: ['101'] }],
    ['installations that are not a list', { total_count: 1, installations: '101' }],
    ['no total_count on the listing', { installations: [{ id: 101 }] }],
  ] as const)(
    'when the listing carries %s, only the listing is read, the card stays connected and installed, and the last count read stays',
    async (_case, listing) => {
      const fake = makeGitHubFake();
      await connect(fake);
      fake.requests.length = 0;
      const logged = fake.logs.length;
      fake.installations = listing;

      const status = await fake.auth.status();

      expect(fake.requests.map((r) => r.url)).toEqual([LISTING]);
      expect(status).toMatchObject({ connected: true, installed: true, repositories: 6 });
      expect(fake.logs.slice(logged).filter((l) => l.startsWith('warn '))).toEqual([]);
    },
  );

  it.each([
    ['a total_count that is a string', { total_count: '8' }],
    ['no total_count', {}],
  ] as const)(
    "when an installation's repositories answer with %s, the card stays connected and installed, and the last count read stays",
    async (_case, reply) => {
      const fake = makeGitHubFake();
      await connect(fake);
      fake.requests.length = 0;
      fake.installationRepositories.set(101, reply);

      const status = await fake.auth.status();

      expect(fake.requests.map((r) => r.url)).toEqual([LISTING, repositoriesOf(101)]);
      expect(status).toMatchObject({ connected: true, installed: true, repositories: 6 });
    },
  );

  it('a new sign-in after Disconnect never inherits a silenced failure streak: its first failure logs again', async () => {
    const fake = makeGitHubFake();
    fake.installationRepositories.set(101, { httpStatus: 503 });
    await connect(fake);
    await fake.auth.status();

    await fake.auth.signOut();
    await connect(fake);

    expect(
      fake.logs.filter((l) => l.includes("could not read the App's repositories")),
    ).toHaveLength(2);
  });
});
