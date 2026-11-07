import { describe, expect, it } from 'bun:test';
import { verify } from 'node:crypto';
import { appJwt } from '../src/oauth/github-bot.js';
import {
  ACCESS_TOKENS_URL,
  BOT_TOKEN_FILE,
  CONVERSIONS_URL,
  INSTALLATIONS_URL,
  PRIVATE_KEY,
  PUBLIC_KEY,
  REPOSITORIES_URL,
  conversion,
  createBot,
  installationToken,
  makeBotFake,
  openedForm,
  reply,
  type BotFake,
} from './github-bot-fakes.js';

const HOUR_MS = 3_600_000;

function timeout(): Error {
  return new DOMException('The operation timed out.', 'TimeoutError');
}

function installed(fake: BotFake, token = 'ghs_installation_one'): void {
  reply(fake, INSTALLATIONS_URL, {
    status: 200,
    body: [{ id: 77, account: { login: 'octocat' } }],
  });
  reply(
    fake,
    ACCESS_TOKENS_URL,
    installationToken(new Date(fake.clock.now + HOUR_MS).toISOString(), token),
  );
  reply(fake, REPOSITORIES_URL, {
    status: 200,
    body: { repositories: [{ full_name: 'octocat/shop' }, { full_name: 'octocat/api' }] },
  });
}

function decode(part: string): Record<string, unknown> {
  return JSON.parse(Buffer.from(part, 'base64url').toString()) as Record<string, unknown>;
}

describe('GitHubBot: creating the App from a manifest', () => {
  it('posts a manifest with exactly the six permissions, an inactive webhook and a loopback redirect, to the account or the organization', async () => {
    const user = makeBotFake();
    const org = makeBotFake();

    const mine = await createBot(user);
    reply(org, CONVERSIONS_URL, conversion({ owner: { login: 'acme', type: 'Organization' } }));
    const theirs = await createBot(org, { target: 'org', org: ' acme ' });

    expect(mine.manifest).toMatchObject({
      name: 'Scout (octocat)',
      public: false,
      hook_attributes: { active: false },
      default_permissions: {
        metadata: 'read',
        contents: 'read',
        pull_requests: 'write',
        issues: 'write',
        checks: 'read',
        statuses: 'read',
      },
    });
    expect(Object.keys(mine.manifest['default_permissions'] as object)).toHaveLength(6);
    expect(String(mine.manifest['redirect_url'])).toMatch(/^http:\/\/127\.0\.0\.1:\d+\/callback$/);
    expect(`${mine.action.origin}${mine.action.pathname}`).toBe(
      'https://github.com/settings/apps/new',
    );
    expect(`${theirs.action.origin}${theirs.action.pathname}`).toBe(
      'https://github.com/organizations/acme/settings/apps/new',
    );
    expect(mine.action.searchParams.get('state')).toHaveLength(43);
  });

  it('exchanges the code once and keeps the App id, slug, client id and private key in the vault, discarding both secrets', async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    reply(fake, INSTALLATIONS_URL, { status: 200, body: [] });

    await createBot(fake);
    const status = await fake.bot.status();

    expect(fake.requests.filter((r) => r.url === CONVERSIONS_URL)).toEqual([
      { url: CONVERSIONS_URL, method: 'POST', authorization: null },
    ]);
    const stored = JSON.parse(fake.vault.get('github.bot')!) as Record<string, unknown>;
    expect(stored).toEqual({
      appId: 4242,
      clientId: 'Iv23liBotClientId',
      slug: 'scout-octocat',
      account: 'octocat',
      accountType: 'user',
      privateKey: PRIVATE_KEY,
      writes: ['review', 'verdict', 'comment', 'issue'],
    });
    expect(fake.vault.get('github.bot')).not.toContain('bot-client-secret-value');
    expect(fake.vault.get('github.bot')).not.toContain('bot-webhook-secret-value');
    expect(status).toEqual({
      state: 'created',
      login: 'scout-octocat[bot]',
      installUrl: 'https://github.com/apps/scout-octocat/installations/new',
      settingsUrl: 'https://github.com/settings/apps/scout-octocat',
      repositories: [],
      writes: ['review', 'verdict', 'comment', 'issue'],
      error: null,
    });
    expect(fake.changes).toBe(1);
  });

  it('refuses a return with the wrong state and stores nothing', async () => {
    const fake = makeBotFake();

    await fake.bot.create({ target: 'user' });
    const form = await openedForm(fake);
    const answer = await fetch(`${String(form.manifest['redirect_url'])}?code=x&state=forged`);
    await fake.bot.settled();
    const status = await fake.bot.status();

    expect(answer.status).toBe(400);
    expect(status.state).toBe('none');
    expect(status.error?.message).toBe(
      "GitHub's answer did not match this request, so Scout saved nothing. Try again.",
    );
    expect(fake.requests).toEqual([]);
    expect(fake.vault.size).toBe(0);
  });

  it('refuses a return with no state and stores nothing', async () => {
    const fake = makeBotFake();

    await fake.bot.create({ target: 'user' });
    const form = await openedForm(fake);
    await fetch(`${String(form.manifest['redirect_url'])}?code=x`);
    await fake.bot.settled();

    expect((await fake.bot.status()).error?.code).toBe('github-bot/state-mismatch');
    expect(fake.vault.size).toBe(0);
  });

  it('gives up after the timeout, closes the local port and stores nothing', async () => {
    const fake = makeBotFake({ createTimeoutMs: 50 });

    await fake.bot.create({ target: 'user' });
    const port = new URL(fake.opened[0]!).port;
    await fake.bot.settled();
    const late = await fetch(`http://127.0.0.1:${port}/callback?code=x&state=y`).then(
      () => 'answered',
      () => 'closed',
    );

    expect((await fake.bot.status()).error?.message).toBe(
      'GitHub did not send the new App back within 5 minutes, so Scout saved nothing. Try again.',
    );
    expect(late).toBe('closed');
    expect(fake.vault.size).toBe(0);
    expect(fake.requests).toEqual([]);
  });

  it.each([
    ['times out', timeout(), 'Could not reach GitHub.'],
    [
      'answers 401',
      { status: 401, body: { message: 'Bad credentials' } },
      'GitHub answered HTTP 401.',
    ],
    [
      'answers 422',
      { status: 422, body: { message: 'Validation Failed' } },
      'GitHub answered HTTP 422.',
    ],
    ['answers 502', { status: 502, body: {} }, 'GitHub answered HTTP 502.'],
  ] as const)(
    'when the conversion %s, the card says why and nothing is stored',
    async (_case, failure, message) => {
      const fake = makeBotFake();
      reply(fake, CONVERSIONS_URL, failure);

      await createBot(fake);
      const status = await fake.bot.status();

      expect(status).toMatchObject({ state: 'none', error: { message } });
      expect(fake.requests.map((r) => r.url)).toEqual([CONVERSIONS_URL]);
      expect(fake.vault.size).toBe(0);
      expect(fake.changes).toBe(0);
    },
  );

  it('refuses to create the bot before GitHub is connected, or for a malformed organization', async () => {
    const offline = makeBotFake({ ownerLogin: async () => null });
    const fake = makeBotFake();

    const notConnected = await offline.bot.create({ target: 'user' }).catch((e: Error) => e);
    const badOrg = await fake.bot
      .create({ target: 'org', org: 'acme/../x' })
      .catch((e: Error) => e);

    expect((notConnected as Error).message).toBe('Connect GitHub before creating the bot.');
    expect((badOrg as Error).message).toBe('Enter the organization as it appears on GitHub.');
    expect([...offline.opened, ...fake.opened]).toEqual([]);
  });

  it('Cancel stops waiting for GitHub without an error and stores nothing', async () => {
    const fake = makeBotFake();

    await fake.bot.create({ target: 'user' });
    const creating = await fake.bot.status();
    const cancelled = await fake.bot.cancel();

    expect(creating.state).toBe('creating');
    expect(cancelled).toMatchObject({ state: 'none', error: null });
    expect(fake.vault.size).toBe(0);
  });
});

describe('GitHubBot: the installation token', () => {
  it("signs a JSON Web Token with the App's key: RS256, issued 60 seconds ago, valid 9 minutes, issued by the client id", () => {
    const now = Date.UTC(2026, 10, 7, 12, 0, 0);

    const jwt = appJwt({ clientId: 'Iv23liBotClientId', privateKey: PRIVATE_KEY }, now);
    const [header, payload, signature] = jwt.split('.') as [string, string, string];

    expect(decode(header)).toEqual({ alg: 'RS256', typ: 'JWT' });
    expect(decode(payload)).toEqual({
      iat: now / 1000 - 60,
      exp: now / 1000 + 540,
      iss: 'Iv23liBotClientId',
    });
    expect(
      verify(
        'sha256',
        Buffer.from(`${header}.${payload}`),
        PUBLIC_KEY,
        Buffer.from(signature, 'base64url'),
      ),
    ).toBe(true);
  });

  it('mints the installation token with the JSON Web Token, writes it to a 0600 file and lists the repositories the bot can act on', async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    installed(fake);

    await createBot(fake);
    const status = await fake.bot.status();

    expect(fake.requests.slice(1).map((r) => [r.method, r.url])).toEqual([
      ['GET', INSTALLATIONS_URL],
      ['POST', ACCESS_TOKENS_URL],
      ['GET', REPOSITORIES_URL],
    ]);
    expect(fake.requests[1]!.authorization).toMatch(/^Bearer eyJ[\w-]+\.[\w-]+\.[\w-]+$/);
    expect(fake.requests[3]!.authorization).toBe('Bearer ghs_installation_one');
    expect(fake.files.get(BOT_TOKEN_FILE)).toEqual({
      data: '{"access_token":"ghs_installation_one"}',
      mode: 0o600,
    });
    expect(status).toMatchObject({
      state: 'installed',
      repositories: ['octocat/shop', 'octocat/api'],
    });
    expect(await fake.bot.workerConfig()).toEqual({
      tokenFile: BOT_TOKEN_FILE,
      writes: ['review', 'verdict', 'comment', 'issue'],
      login: 'scout-octocat[bot]',
    });
    expect(fake.changes).toBe(2);
  });

  it('refreshes the token when less than 5 minutes remain, and not before', async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    installed(fake);
    await createBot(fake);
    await fake.bot.refreshIfDue();
    const minted = fake.requests.length;

    fake.clock.now += HOUR_MS - 6 * 60_000;
    await fake.bot.refreshIfDue();
    const early = fake.requests.length;
    fake.clock.now += 2 * 60_000;
    installed(fake, 'ghs_installation_two');
    await fake.bot.refreshIfDue();

    expect(early).toBe(minted);
    expect(fake.requests.length).toBe(minted + 3);
    expect(fake.files.get(BOT_TOKEN_FILE)?.data).toBe('{"access_token":"ghs_installation_two"}');
  });

  it.each([
    ['times out', timeout(), 'Could not reach GitHub.'],
    ['answers 401', { status: 401, body: {} }, 'GitHub answered HTTP 401.'],
    ['answers 503', { status: 503, body: {} }, 'GitHub answered HTTP 503.'],
  ] as const)(
    'when minting the installation token %s, the bot stays created, no token file is written and the card says why',
    async (_case, failure, message) => {
      const fake = makeBotFake();
      reply(fake, CONVERSIONS_URL, conversion());
      reply(fake, INSTALLATIONS_URL, { status: 200, body: [{ id: 77 }] });
      reply(fake, ACCESS_TOKENS_URL, failure);

      await createBot(fake);
      const status = await fake.bot.status();

      expect(status).toMatchObject({ state: 'created', error: { message } });
      expect(fake.files.has(BOT_TOKEN_FILE)).toBe(false);
      expect(await fake.bot.workerConfig()).toBeNull();
    },
  );

  it.each([
    ['times out', timeout(), 'Could not reach GitHub.'],
    ['answers 401', { status: 401, body: {} }, 'GitHub answered HTTP 401.'],
    ['answers 500', { status: 500, body: {} }, 'GitHub answered HTTP 500.'],
  ] as const)(
    'when listing the installations %s, no token is minted and the card says why',
    async (_case, failure, message) => {
      const fake = makeBotFake();
      reply(fake, CONVERSIONS_URL, conversion());
      reply(fake, INSTALLATIONS_URL, failure);

      await createBot(fake);
      const status = await fake.bot.status();

      expect(status).toMatchObject({ state: 'created', error: { message } });
      expect(fake.requests.map((r) => r.url)).not.toContain(ACCESS_TOKENS_URL);
    },
  );

  it.each([
    ['times out', timeout()],
    ['answers 401', { status: 401, body: {} }],
    ['answers 502', { status: 502, body: {} }],
  ] as const)(
    'when a refresh %s, the worker keeps the current token file and the refresh is tried again',
    async (_case, failure) => {
      const fake = makeBotFake();
      reply(fake, CONVERSIONS_URL, conversion());
      installed(fake);
      await createBot(fake);
      await fake.bot.refreshIfDue();

      fake.clock.now += HOUR_MS - 4 * 60_000;
      reply(fake, INSTALLATIONS_URL, { status: 200, body: [{ id: 77 }] });
      reply(fake, ACCESS_TOKENS_URL, failure);
      await fake.bot.refreshIfDue();
      const afterFailure = fake.files.get(BOT_TOKEN_FILE)?.data;
      installed(fake, 'ghs_installation_two');
      await fake.bot.refreshIfDue();

      expect(afterFailure).toBe('{"access_token":"ghs_installation_one"}');
      expect(fake.files.get(BOT_TOKEN_FILE)?.data).toBe('{"access_token":"ghs_installation_two"}');
      expect(
        fake.logs.some((l) => l.startsWith('warn github-bot: installation token not refreshed')),
      ).toBe(true);
    },
  );

  it.each([
    ['times out', timeout()],
    ['answers 401', { status: 401, body: {} }],
    ['answers 503', { status: 503, body: {} }],
  ] as const)(
    'when listing the repositories %s, the token file is still written and the worker restarts onto the bot',
    async (_case, failure) => {
      const fake = makeBotFake();
      reply(fake, CONVERSIONS_URL, conversion());
      reply(fake, INSTALLATIONS_URL, { status: 200, body: [{ id: 77 }] });
      reply(fake, ACCESS_TOKENS_URL, installationToken('2026-11-07T13:00:00Z'));
      reply(fake, REPOSITORIES_URL, failure);

      await createBot(fake);
      const status = await fake.bot.status();

      expect(fake.files.get(BOT_TOKEN_FILE)?.data).toBe('{"access_token":"ghs_installation_one"}');
      expect(status).toMatchObject({ state: 'installed', repositories: [], error: null });
      expect((await fake.bot.workerConfig())?.tokenFile).toBe(BOT_TOKEN_FILE);
      expect(fake.changes).toBe(2);
    },
  );

  it('when the token file cannot be written, the worker gets no bot and the card names the file; the next refresh writes it', async () => {
    const fake = makeBotFake();
    const write = fake.deps.fs.writeFile.bind(fake.deps.fs);
    let failures = 1;
    fake.deps.fs.writeFile = async (path, data, options) => {
      if (failures-- > 0) throw new Error('EACCES');
      return write(path, data, options);
    };
    reply(fake, CONVERSIONS_URL, conversion());
    installed(fake);

    await createBot(fake);
    const failed = await fake.bot.status();
    const configWhenFailed = await fake.bot.workerConfig();
    installed(fake, 'ghs_installation_two');
    await fake.bot.refreshIfDue();

    expect(failed).toMatchObject({
      state: 'created',
      error: { message: "Scout could not write the bot's token file; it will try again." },
    });
    expect(configWhenFailed).toBeNull();
    expect(fake.files.get(BOT_TOKEN_FILE)?.data).toBe('{"access_token":"ghs_installation_two"}');
    expect((await fake.bot.status()).state).toBe('installed');
  });

  it('at launch a worker built while the first token is minted waits for it and is not restarted', async () => {
    const fake = makeBotFake();
    fake.vault.set(
      'github.bot',
      JSON.stringify({
        appId: 4242,
        clientId: 'Iv23liBotClientId',
        slug: 'scout-octocat',
        account: 'octocat',
        accountType: 'user',
        privateKey: PRIVATE_KEY,
        writes: ['review', 'verdict', 'comment', 'issue'],
      }),
    );
    installed(fake);

    const starting = fake.bot.start();
    const config = await fake.bot.workerConfig();
    await starting;
    fake.bot.stop();

    expect(config).toEqual({
      tokenFile: BOT_TOKEN_FILE,
      writes: ['review', 'verdict', 'comment', 'issue'],
      login: 'scout-octocat[bot]',
    });
    expect(fake.changes).toBe(0);
  });

  it('removes the token file and restarts the worker on the owner identity when the App is uninstalled', async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    installed(fake);
    await createBot(fake);
    await fake.bot.refreshIfDue();
    const changesWhenInstalled = fake.changes;

    fake.clock.now += HOUR_MS;
    reply(fake, INSTALLATIONS_URL, { status: 200, body: [] });
    await fake.bot.refreshIfDue();

    expect(fake.files.has(BOT_TOKEN_FILE)).toBe(false);
    expect((await fake.bot.status()).state).toBe('created');
    expect(await fake.bot.workerConfig()).toBeNull();
    expect(fake.changes).toBe(changesWhenInstalled + 1);
  });
});

describe('GitHubBot: switches, removal and custody', () => {
  it('switches each write between the bot and the owner and restarts the worker, refusing an unknown action', async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    installed(fake);
    await createBot(fake);
    await fake.bot.refreshIfDue();
    const before = fake.changes;

    const status = await fake.bot.setWrites(['review', 'issue']);
    const unknown = await fake.bot.setWrites(['review', 'merge']).catch((e: Error) => e);

    expect(status.writes).toEqual(['review', 'issue']);
    expect((await fake.bot.workerConfig())?.writes).toEqual(['review', 'issue']);
    expect(fake.changes).toBe(before + 1);
    expect((unknown as Error).message).toBe('Scout does not know one of those actions.');
  });

  it('Remove deletes the private key and the token file and restarts the worker', async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    installed(fake);
    await createBot(fake);
    await fake.bot.refreshIfDue();
    const before = fake.changes;

    const status = await fake.bot.remove();

    expect(status.state).toBe('none');
    expect(fake.vault.has('github.bot')).toBe(false);
    expect(fake.files.size).toBe(0);
    expect(await fake.bot.workerConfig()).toBeNull();
    expect(fake.changes).toBe(before + 1);
  });

  it('the private key and the tokens never reach a file other than the token file, a log line or the status', async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    installed(fake);
    await createBot(fake);
    const status = await fake.bot.status();

    const keyBody = PRIVATE_KEY.split('\n')[1]!;
    const files = fake.written.join('\n');
    const logs = fake.logs.join('\n');
    const seen = JSON.stringify(status);
    expect(files).not.toContain(keyBody);
    expect(logs).not.toContain(keyBody);
    expect(seen).not.toContain(keyBody);
    expect(logs).not.toContain('ghs_installation_one');
    expect(seen).not.toContain('ghs_installation_one');
    expect(fake.written).toEqual(['{"access_token":"ghs_installation_one"}']);
  });
});
