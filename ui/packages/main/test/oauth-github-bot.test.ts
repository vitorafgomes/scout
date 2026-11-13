import { describe, expect, it } from 'bun:test';
import { verify } from 'node:crypto';
import { appJwt } from '../src/oauth/github-bot.js';
import {
  ACCESS_TOKENS_URL,
  APP_URL,
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

  it('exchanges the code once and keeps the App id, slug, name, client id and private key in the vault, discarding both secrets', async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-octocat', name: 'Scout (octocat)' } });
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
      name: 'Scout (octocat)',
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
      name: 'Scout (octocat)',
      nameError: null,
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
      ['GET', APP_URL],
      ['GET', INSTALLATIONS_URL],
      ['POST', ACCESS_TOKENS_URL],
      ['GET', REPOSITORIES_URL],
    ]);
    expect(fake.requests[2]!.authorization).toMatch(/^Bearer eyJ[\w-]+\.[\w-]+\.[\w-]+$/);
    expect(fake.requests[4]!.authorization).toBe('Bearer ghs_installation_one');
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
      repositories: ['octocat/shop', 'octocat/api'],
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

  it('a refresh restarts the worker with the new repository list when the installation covers other repositories, and not when the list is the same', async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    installed(fake);
    await createBot(fake);
    await fake.bot.refreshIfDue();
    const afterFirstToken = fake.changes;

    fake.clock.now += HOUR_MS;
    reply(fake, INSTALLATIONS_URL, { status: 200, body: [{ id: 77 }] });
    reply(
      fake,
      ACCESS_TOKENS_URL,
      installationToken(new Date(fake.clock.now + HOUR_MS).toISOString()),
    );
    reply(fake, REPOSITORIES_URL, {
      status: 200,
      body: { repositories: [{ full_name: 'octocat/api' }, { full_name: 'octocat/shop' }] },
    });
    await fake.bot.refreshIfDue();
    const afterSameList = fake.changes;
    fake.clock.now += HOUR_MS;
    reply(fake, INSTALLATIONS_URL, { status: 200, body: [{ id: 77 }] });
    reply(
      fake,
      ACCESS_TOKENS_URL,
      installationToken(new Date(fake.clock.now + HOUR_MS).toISOString()),
    );
    reply(fake, REPOSITORIES_URL, {
      status: 200,
      body: { repositories: [{ full_name: 'octocat/shop' }, { full_name: 'octocat/web' }] },
    });
    await fake.bot.refreshIfDue();

    expect(afterSameList).toBe(afterFirstToken);
    expect(fake.changes).toBe(afterFirstToken + 1);
    expect((await fake.bot.workerConfig())?.repositories).toEqual(['octocat/shop', 'octocat/web']);
  });

  it("the card's status re-reads the installation's repositories: a changed list restarts the worker with it, the same list does not", async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    installed(fake);
    await createBot(fake);
    await fake.bot.status();
    const afterInstall = fake.changes;

    reply(fake, REPOSITORIES_URL, {
      status: 200,
      body: { repositories: [{ full_name: 'octocat/api' }, { full_name: 'octocat/shop' }] },
    });
    await fake.bot.status();
    const afterSameList = fake.changes;
    reply(fake, REPOSITORIES_URL, {
      status: 200,
      body: { repositories: [{ full_name: 'octocat/shop' }, { full_name: 'octocat/web' }] },
    });
    const changed = await fake.bot.status();

    expect(afterSameList).toBe(afterInstall);
    expect(fake.changes).toBe(afterInstall + 1);
    expect(changed.repositories).toEqual(['octocat/shop', 'octocat/web']);
    expect((await fake.bot.workerConfig())?.repositories).toEqual(['octocat/shop', 'octocat/web']);
    expect(fake.requests.filter((r) => r.url === ACCESS_TOKENS_URL)).toHaveLength(1);
  });

  it.each([
    ['times out', timeout()],
    ['answers 401', { status: 401, body: {} }],
    ['answers 503', { status: 503, body: {} }],
  ] as const)(
    'when re-reading the repositories for the card %s, the card keeps the last list and the worker is not restarted',
    async (_case, failure) => {
      const fake = makeBotFake();
      reply(fake, CONVERSIONS_URL, conversion());
      installed(fake);
      await createBot(fake);
      await fake.bot.status();
      const afterInstall = fake.changes;

      reply(fake, REPOSITORIES_URL, failure);
      const status = await fake.bot.status();

      expect(status.repositories).toEqual(['octocat/shop', 'octocat/api']);
      expect((await fake.bot.workerConfig())?.repositories).toEqual([
        'octocat/shop',
        'octocat/api',
      ]);
      expect(fake.changes).toBe(afterInstall);
      expect(fake.logs.some((l) => l.startsWith('warn github-bot: repositories not listed'))).toBe(
        true,
      );
    },
  );

  const SECOND_PAGE_URL = REPOSITORIES_URL.replace(/page=1$/, 'page=2');
  const repositoryPage = (count: number, from: number) => ({
    status: 200,
    body: {
      total_count: 120,
      repositories: Array.from({ length: count }, (_, i) => ({
        full_name: `octocat/repo-${from + i}`,
      })),
    },
  });

  it('lists every repository of an installation that covers more than one page', async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    installed(fake);
    await createBot(fake);
    await fake.bot.status();

    reply(fake, REPOSITORIES_URL, repositoryPage(100, 1));
    reply(fake, SECOND_PAGE_URL, repositoryPage(20, 101));
    const status = await fake.bot.status();

    const every = Array.from({ length: 120 }, (_, i) => `octocat/repo-${i + 1}`);
    expect(status.repositories).toEqual(every);
    expect((await fake.bot.workerConfig())?.repositories).toEqual(every);
  });

  it.each([
    ['times out', timeout()],
    ['answers 503', { status: 503, body: {} }],
  ] as const)(
    'when a later page of the repositories %s, the card keeps the last full list and the worker is not restarted',
    async (_case, failure) => {
      const fake = makeBotFake();
      reply(fake, CONVERSIONS_URL, conversion());
      installed(fake);
      await createBot(fake);
      await fake.bot.status();
      const afterInstall = fake.changes;

      reply(fake, REPOSITORIES_URL, repositoryPage(100, 1));
      reply(fake, SECOND_PAGE_URL, failure);
      const status = await fake.bot.status();

      expect(fake.replies.get(SECOND_PAGE_URL)).toEqual([]);
      expect(status.repositories).toEqual(['octocat/shop', 'octocat/api']);
      expect((await fake.bot.workerConfig())?.repositories).toEqual([
        'octocat/shop',
        'octocat/api',
      ]);
      expect(fake.changes).toBe(afterInstall);
      expect(fake.logs.some((l) => l.startsWith('warn github-bot: repositories not listed'))).toBe(
        true,
      );
    },
  );

  it('a listing that never ends stops after the bound and keeps the last full list', async () => {
    const fake = makeBotFake();
    reply(fake, CONVERSIONS_URL, conversion());
    installed(fake);
    await createBot(fake);
    await fake.bot.status();
    const afterInstall = fake.changes;
    for (let page = 1; page <= 51; page++) {
      reply(fake, REPOSITORIES_URL.replace(/page=1$/, `page=${page}`), repositoryPage(100, 1));
    }
    const before = fake.requests.length;

    const status = await fake.bot.status();

    const listed = fake.requests
      .slice(before)
      .filter((r) => r.url.includes('/installation/repositories'));
    expect(listed).toHaveLength(50);
    expect(status.repositories).toEqual(['octocat/shop', 'octocat/api']);
    expect((await fake.bot.workerConfig())?.repositories).toEqual(['octocat/shop', 'octocat/api']);
    expect(fake.changes).toBe(afterInstall);
    const warned = fake.logs.filter((l) =>
      l.startsWith('warn github-bot: repositories not listed'),
    );
    expect(warned).toHaveLength(1);
    expect(warned[0]).not.toContain('ghs_installation_one');
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
      repositories: ['octocat/shop', 'octocat/api'],
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

function storedBot(fake: BotFake, slug = 'scout-octocat'): void {
  fake.vault.set(
    'github.bot',
    JSON.stringify({
      appId: 4242,
      clientId: 'Iv23liBotClientId',
      slug,
      name: 'Scout (octocat)',
      account: 'octocat',
      accountType: 'user',
      privateKey: PRIVATE_KEY,
      writes: ['review', 'verdict', 'comment', 'issue'],
    }),
  );
}

function keptSlug(fake: BotFake): string {
  return (JSON.parse(fake.vault.get('github.bot')!) as { slug: string }).slug;
}

describe('GitHubBot: the slug follows a rename', () => {
  it("when the card opens, the App's current slug is read with the App's JSON Web Token; a rename is kept, logged at info and reaches the worker", async () => {
    const fake = makeBotFake();
    storedBot(fake);
    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-octocat' } });
    installed(fake);
    await fake.bot.status();
    const before = fake.changes;

    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-review' } });
    reply(fake, REPOSITORIES_URL, {
      status: 200,
      body: { repositories: [{ full_name: 'octocat/shop' }, { full_name: 'octocat/api' }] },
    });
    const status = await fake.bot.status();

    const appRead = fake.requests.filter((r) => r.url === APP_URL).at(-1)!;
    expect(appRead.method).toBe('GET');
    expect(appRead.authorization).toMatch(/^Bearer eyJ[\w-]+\.[\w-]+\.[\w-]+$/);
    expect(keptSlug(fake)).toBe('scout-review');
    expect(status).toMatchObject({
      login: 'scout-review[bot]',
      installUrl: 'https://github.com/apps/scout-review/installations/new',
      settingsUrl: 'https://github.com/settings/apps/scout-review',
      error: null,
    });
    expect((await fake.bot.mentionTarget())?.slug).toBe('scout-review');
    expect((await fake.bot.workerConfig())?.login).toBe('scout-review[bot]');
    expect(
      fake.logs.some(
        (l) =>
          l.startsWith('info github-bot: the App was renamed on GitHub') &&
          l.includes('"from":"scout-octocat"') &&
          l.includes('"to":"scout-review"'),
      ),
    ).toBe(true);
    expect(fake.changes).toBe(before + 1);
  });

  it('the same slug changes nothing and restarts nothing', async () => {
    const fake = makeBotFake();
    storedBot(fake);
    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-octocat' } });
    installed(fake);
    await fake.bot.status();
    const before = { changes: fake.changes, saved: fake.vault.get('github.bot') };

    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-octocat' } });
    reply(fake, REPOSITORIES_URL, {
      status: 200,
      body: { repositories: [{ full_name: 'octocat/shop' }, { full_name: 'octocat/api' }] },
    });
    await fake.bot.status();

    expect(fake.changes).toBe(before.changes);
    expect(fake.vault.get('github.bot')).toBe(before.saved);
    expect(fake.logs.some((l) => l.includes('renamed'))).toBe(false);
  });

  it('at start a renamed App is read before the first worker is built, which gets the new login without a restart', async () => {
    const fake = makeBotFake();
    storedBot(fake);
    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-review' } });
    installed(fake);

    const starting = fake.bot.start();
    const config = await fake.bot.workerConfig();
    await starting;
    fake.bot.stop();

    expect(fake.requests[0]).toMatchObject({ method: 'GET', url: APP_URL });
    expect(keptSlug(fake)).toBe('scout-review');
    expect(config?.login).toBe('scout-review[bot]');
    expect(fake.changes).toBe(0);
  });

  it.each([
    ['times out', timeout(), 'github-bot/network-error'],
    ['answers 401', { status: 401, body: {} }, 'github-bot/app-failed'],
    ['answers 503', { status: 503, body: {} }, 'github-bot/app-failed'],
  ] as const)(
    'when reading the App %s, the kept slug stays, the card shows no error and the failure is logged',
    async (_case, failure, code) => {
      const fake = makeBotFake();
      storedBot(fake);
      reply(fake, APP_URL, failure);
      installed(fake);

      const status = await fake.bot.status();

      expect(keptSlug(fake)).toBe('scout-octocat');
      expect(status).toMatchObject({
        state: 'installed',
        login: 'scout-octocat[bot]',
        error: null,
      });
      expect((await fake.bot.mentionTarget())?.slug).toBe('scout-octocat');
      expect(
        fake.logs.some(
          (l) =>
            l.startsWith('warn github-bot: the App was not read; keeping its slug') &&
            l.includes(code),
        ),
      ).toBe(true);
    },
  );

  it("the bell gets the bot's slug and the account its installation is on only while the bot is installed", async () => {
    const fake = makeBotFake();
    storedBot(fake);
    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-octocat' } });
    reply(fake, INSTALLATIONS_URL, {
      status: 200,
      body: [{ id: 77, account: { login: 'acme', type: 'Organization' } }],
    });
    reply(
      fake,
      ACCESS_TOKENS_URL,
      installationToken(new Date(fake.clock.now + HOUR_MS).toISOString()),
    );
    reply(fake, REPOSITORIES_URL, { status: 200, body: { repositories: [] } });
    const beforeInstalled = makeBotFake();
    storedBot(beforeInstalled);

    await fake.bot.status();

    expect(await beforeInstalled.bot.mentionTarget()).toBeNull();
    expect(await fake.bot.mentionTarget()).toEqual({
      slug: 'scout-octocat',
      account: 'acme',
      accountType: 'org',
    });
  });
});

describe('GitHubBot: the mention target at launch', () => {
  it("the bell's first read after a start waits for the bot's first token and gets the slug and the installation's account, never null", async () => {
    const fake = makeBotFake();
    storedBot(fake);
    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-octocat' } });
    installed(fake);

    const starting = fake.bot.start();
    const target = await fake.bot.mentionTarget();
    await starting;
    fake.bot.stop();

    expect(target).toEqual({ slug: 'scout-octocat', account: 'octocat', accountType: 'user' });
  });
});

describe('GitHubBot: the name the card shows', () => {
  it("the App's display name is read with its slug when the card opens; a new name is kept, shown and logged, and restarts nothing", async () => {
    const fake = makeBotFake();
    storedBot(fake);
    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-octocat', name: 'Scout (octocat)' } });
    installed(fake);
    await fake.bot.status();
    const before = fake.changes;

    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-octocat', name: 'Review Helper' } });
    reply(fake, REPOSITORIES_URL, {
      status: 200,
      body: { repositories: [{ full_name: 'octocat/shop' }, { full_name: 'octocat/api' }] },
    });
    const status = await fake.bot.status();

    expect(status).toMatchObject({
      name: 'Review Helper',
      nameError: null,
      login: 'scout-octocat[bot]',
    });
    expect((JSON.parse(fake.vault.get('github.bot')!) as { name: string }).name).toBe(
      'Review Helper',
    );
    expect(
      fake.logs.some(
        (l) =>
          l.startsWith('info github-bot: the App has another name on GitHub') &&
          l.includes('"to":"Review Helper"'),
      ),
    ).toBe(true);
    expect(fake.changes).toBe(before);
  });

  it('when reading the App fails, the card keeps the last name and handle with a line saying they could not be refreshed, and the next read that answers clears it', async () => {
    const fake = makeBotFake();
    storedBot(fake);
    reply(fake, APP_URL, { status: 503, body: {} });
    installed(fake);

    const failed = await fake.bot.status();
    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-octocat', name: 'Scout (octocat)' } });
    reply(fake, REPOSITORIES_URL, {
      status: 200,
      body: { repositories: [{ full_name: 'octocat/shop' }, { full_name: 'octocat/api' }] },
    });
    const answered = await fake.bot.status();

    expect(failed).toMatchObject({
      name: 'Scout (octocat)',
      login: 'scout-octocat[bot]',
      nameError:
        "Scout could not refresh the bot's name from GitHub; this is the last name it read.",
      error: null,
    });
    expect(answered.nameError).toBeNull();
  });
});

describe('GitHubBot: a vault that fails while following a rename', () => {
  it('a rename at start that the vault cannot store keeps the kept slug, logs it, and the worker and the bell still get the bot', async () => {
    const fake = makeBotFake();
    storedBot(fake);
    fake.deps.vault.store = async () => {
      throw new Error('locked');
    };
    reply(fake, APP_URL, { status: 200, body: { slug: 'scout-review', name: 'Review Helper' } });
    installed(fake);

    const rejected = (err: unknown) => `rejected: ${(err as Error).message}`;
    const started = await fake.bot.start().then(() => 'started', rejected);
    const config = await fake.bot.workerConfig().catch(rejected);
    const target = await fake.bot.mentionTarget().catch(rejected);
    fake.bot.stop();

    expect(started).toBe('started');
    expect(config).toMatchObject({ login: 'scout-octocat[bot]' });
    expect(target).toMatchObject({ slug: 'scout-octocat' });
    expect(keptSlug(fake)).toBe('scout-octocat');
    expect(
      fake.logs.some((l) =>
        l.startsWith('warn github-bot: the new name and slug were not kept; keeping the last ones'),
      ),
    ).toBe(true);
  });
});
