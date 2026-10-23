import { describe, it, expect } from 'bun:test';
import { ScoutError } from '@scout/shared';
import { TOKEN_FILE, connect, makeGitHubFake, tokenFileToken } from './github-fakes.js';

const PASTED = 'github_pat_11ABCDEFG0pastedReadOnlyToken';
const WRITE_WARNING =
  'This token can write to your repositories. A read-only fine-grained token is safer.';

function userReply(status: number, scopes: string | null = null): () => Response {
  return () =>
    new Response(JSON.stringify(status === 200 ? { login: 'vitorafgomes' } : {}), {
      status,
      headers: scopes === null ? {} : { 'X-OAuth-Scopes': scopes },
    });
}

async function rejection(promise: Promise<unknown>): Promise<ScoutError> {
  try {
    await promise;
  } catch (err: unknown) {
    return err as ScoutError;
  }
  throw new Error('expected a rejection');
}

describe('GitHubAuth with a pasted token', () => {
  it('connects as the token owner and gives the worker the token, not the token file', async () => {
    const fake = makeGitHubFake();

    const status = await fake.auth.saveToken(`  ${PASTED}  `);
    const env = await fake.auth.workerEnv();

    expect(status).toMatchObject({
      connected: true,
      method: 'token',
      login: 'vitorafgomes',
      warning: null,
    });
    expect(env).toEqual({ SCOUT__PLUGINS__GITHUB__TOKEN: PASTED });
    expect(fake.requests.at(-1)).toMatchObject({
      url: 'https://api.github.com/user',
      method: 'GET',
      authorization: `Bearer ${PASTED}`,
    });
    expect(fake.connectionChanges).toBe(1);
  });

  it('warns when a classic token carries the repo scope, and still connects', async () => {
    const fake = makeGitHubFake();
    fake.user = userReply(200, 'read:org, repo');

    const status = await fake.auth.saveToken(PASTED);

    expect(status.connected).toBe(true);
    expect(status.warning).toBe(WRITE_WARNING);
  });

  it('does not warn for a classic token with read-only scopes', async () => {
    const fake = makeGitHubFake();
    fake.user = userReply(200, 'read:org, read:user');

    const status = await fake.auth.saveToken(PASTED);

    expect(status.warning).toBeNull();
  });

  it('says GitHub rejected the token on a 401 and stores nothing', async () => {
    const fake = makeGitHubFake();
    fake.user = userReply(401);

    const err = await rejection(fake.auth.saveToken(PASTED));

    expect(err.code).toBe('github/token-rejected');
    expect(err.message).toBe('GitHub rejected this token.');
    expect(fake.vault.has('github.token')).toBe(false);
    expect((await fake.auth.status()).connected).toBe(false);
    expect(fake.connectionChanges).toBe(0);
  });

  it('fails with a typed network error and stores nothing when GitHub is unreachable', async () => {
    const fake = makeGitHubFake();
    fake.user = () => {
      throw new TypeError('fetch failed');
    };

    const err = await rejection(fake.auth.saveToken(PASTED));

    expect(err.code).toBe('github/network-error');
    expect(err.message).toBe('Could not reach GitHub.');
    expect(fake.vault.has('github.token')).toBe(false);
    expect(await fake.auth.workerEnv()).toEqual({});
  });

  it('replaces a browser connection: the session and the token file are removed', async () => {
    const fake = makeGitHubFake();
    await connect(fake);

    const status = await fake.auth.saveToken(PASTED);

    expect(status.method).toBe('token');
    expect(fake.vault.has('github.session')).toBe(false);
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(await fake.auth.workerEnv()).toEqual({ SCOUT__PLUGINS__GITHUB__TOKEN: PASTED });
  });

  it('a browser connection replaces a saved token', async () => {
    const fake = makeGitHubFake();
    await fake.auth.saveToken(PASTED);

    await connect(fake);
    const status = await fake.auth.status();

    expect(status.method).toBe('browser');
    expect(fake.vault.has('github.token')).toBe(false);
    expect(tokenFileToken(fake)).toBe('ghu_first');
    expect(await fake.auth.workerEnv()).toEqual({
      SCOUT__PLUGINS__GITHUB__TOKENFILE: TOKEN_FILE,
    });
  });

  it('disconnect clears the saved token and tells the worker to restart', async () => {
    const fake = makeGitHubFake();
    await fake.auth.saveToken(PASTED);

    const status = await fake.auth.signOut();

    expect(status).toMatchObject({ connected: false, method: null, login: null });
    expect(fake.vault.has('github.token')).toBe(false);
    expect(await fake.auth.workerEnv()).toEqual({});
    expect(fake.connectionChanges).toBe(2);
  });

  it('never logs the pasted token and never puts it in the status', async () => {
    const fake = makeGitHubFake();
    fake.user = userReply(200, 'repo');

    const saved = await fake.auth.saveToken(PASTED);
    const status = await fake.auth.status();
    await fake.auth.signOut();

    const everything = [...fake.logs, JSON.stringify(saved), JSON.stringify(status)].join('\n');
    expect(fake.logs.length).toBeGreaterThan(0);
    expect(everything).not.toContain(PASTED);
  });
});
