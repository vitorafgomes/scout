import { describe, it, expect } from 'bun:test';
import { ScoutError, serializeError } from '@scout/shared';
import {
  API_KEY,
  CHANNELS_URL,
  REVOKE_URL,
  TOKEN_FILE,
  connect,
  json,
  makeGoogleFake,
} from './google-fakes.js';

async function rejection(promise: Promise<unknown>): Promise<ScoutError> {
  try {
    await promise;
  } catch (err: unknown) {
    return err as ScoutError;
  }
  throw new Error('expected a rejection');
}

describe('YouTube with a pasted API key', () => {
  it('a pasted key is checked with one public GET and stored; the worker env carries it', async () => {
    const fake = makeGoogleFake();

    const status = await fake.auth.saveApiKey(`  ${API_KEY}  `);
    const env = await fake.auth.workerEnv();

    expect(fake.requests).toEqual([
      {
        url: `${CHANNELS_URL}?part=id&forHandle=youtube&key=${API_KEY}`,
        method: 'GET',
        form: {},
        authorization: null,
      },
    ]);
    expect(fake.vault.get('youtube.api-key')).toBe(API_KEY);
    expect(env).toEqual({ SCOUT__PLUGINS__YOUTUBE__APIKEY: API_KEY });
    expect(status).toMatchObject({ connected: true, method: 'apiKey', channelTitle: null });
    expect(fake.connectionChanges).toBe(1);
  });

  it('a rejected key stores nothing and the message never contains it', async () => {
    const outcomes: ScoutError[] = [];
    const fake = makeGoogleFake();
    for (const answer of [
      () => json({ error: { code: 400, message: `API key not valid: ${API_KEY}` } }, 400),
      () => json({ error: { code: 403, message: 'YouTube Data API v3 has not been used' } }, 403),
      (url: string) => {
        throw new TypeError(`fetch failed: ${url}`);
      },
    ]) {
      fake.keyCheck = answer;
      outcomes.push(await rejection(fake.auth.saveApiKey(API_KEY)));
    }

    expect(outcomes.map((e) => e.code)).toEqual([
      'youtube/key-rejected',
      'youtube/key-rejected',
      'youtube/network-error',
    ]);
    expect(outcomes[0]?.message).toBe(
      'Google rejected this API key. Check that the YouTube Data API v3 is enabled for it.',
    );
    const shown = JSON.stringify([
      ...outcomes.map((e) => serializeError(e)),
      ...fake.logs,
      await fake.auth.status(),
    ]);
    expect(shown).not.toContain(API_KEY);
    expect(fake.vault.has('youtube.api-key')).toBe(false);
    expect(await fake.auth.workerEnv()).toEqual({});
    expect(fake.connectionChanges).toBe(0);
  });

  it.each([
    {
      why: 'a key Google does not know',
      status: 400,
      body: {
        error: {
          code: 400,
          message: 'API key not valid. Please pass a valid API key.',
          errors: [{ message: 'API key not valid.', domain: 'global', reason: 'badRequest' }],
          status: 'INVALID_ARGUMENT',
          details: [
            {
              '@type': 'type.googleapis.com/google.rpc.ErrorInfo',
              reason: 'API_KEY_INVALID',
              domain: 'googleapis.com',
              metadata: { service: 'youtube.googleapis.com' },
            },
          ],
        },
      },
      message:
        'Google does not recognise this API key. Copy it again from Google Cloud Console credentials.',
    },
    {
      why: 'the YouTube Data API not enabled for the key project',
      status: 403,
      body: {
        error: {
          code: 403,
          message:
            'YouTube Data API v3 has not been used in project 123456789012 before or it is disabled.',
          errors: [{ message: 'disabled', domain: 'usageLimits', reason: 'accessNotConfigured' }],
          status: 'PERMISSION_DENIED',
          details: [
            {
              '@type': 'type.googleapis.com/google.rpc.ErrorInfo',
              reason: 'SERVICE_DISABLED',
              domain: 'googleapis.com',
              metadata: {
                service: 'youtube.googleapis.com',
                consumer: 'projects/123456789012',
              },
            },
          ],
        },
      },
      message:
        "The YouTube Data API v3 is not enabled for this key's project. Enable it in Google Cloud Console and try again in a minute. (projects/123456789012)",
    },
    {
      why: 'a key restricted to other APIs',
      status: 403,
      body: {
        error: {
          code: 403,
          message: 'Requests to this API youtube method are blocked.',
          errors: [{ message: 'blocked', domain: 'global', reason: 'forbidden' }],
          status: 'PERMISSION_DENIED',
          details: [
            {
              '@type': 'type.googleapis.com/google.rpc.ErrorInfo',
              reason: 'API_KEY_SERVICE_BLOCKED',
              domain: 'googleapis.com',
            },
          ],
        },
      },
      message:
        "This key's restrictions do not allow the YouTube Data API v3. Edit the key's API restrictions in Google Cloud Console.",
    },
    {
      why: 'the daily quota spent',
      status: 403,
      body: {
        error: {
          code: 403,
          message: 'The request cannot be completed because you have exceeded your quota.',
          errors: [{ message: 'quota', domain: 'youtube.quota', reason: 'quotaExceeded' }],
        },
      },
      message: "YouTube's daily quota for this key is spent; it resets at midnight Pacific time.",
    },
    {
      why: 'a reason Scout does not know',
      status: 403,
      body: { error: { code: 403, errors: [{ reason: 'ipRefererBlocked' }] } },
      message:
        'Google rejected this API key. Check that the YouTube Data API v3 is enabled for it. (ipRefererBlocked)',
    },
  ])('says why Google refused the key: $why', async ({ status, body, message }) => {
    const fake = makeGoogleFake();
    fake.keyCheck = () => json(body, status);

    const err = await rejection(fake.auth.saveApiKey(API_KEY));

    expect(err.message).toBe(message);
    expect(err.message).not.toContain(API_KEY);
    expect(err.message).not.toContain('googleapis.com/youtube');
    expect(fake.vault.has('youtube.api-key')).toBe(false);
  });

  it('refuses an empty key without asking Google', async () => {
    const fake = makeGoogleFake();

    const err = await rejection(fake.auth.saveApiKey('   '));

    expect(err.code).toBe('youtube/key-empty');
    expect(err.message).toBe('Enter a YouTube Data API key.');
    expect(fake.requests).toEqual([]);
  });

  it('one credential at a time', async () => {
    const keyAfterSignIn = makeGoogleFake();
    await connect(keyAfterSignIn);
    const signInAfterKey = makeGoogleFake();
    await signInAfterKey.auth.saveApiKey(API_KEY);

    const keyStatus = await keyAfterSignIn.auth.saveApiKey(API_KEY);
    await connect(signInAfterKey);
    const signInStatus = await signInAfterKey.auth.status();

    expect(keyStatus.method).toBe('apiKey');
    expect(keyAfterSignIn.vault.has('youtube.session')).toBe(false);
    expect(keyAfterSignIn.files.has(TOKEN_FILE)).toBe(false);
    expect(keyAfterSignIn.requests.filter((r) => r.url === REVOKE_URL)).toHaveLength(1);
    expect(await keyAfterSignIn.auth.workerEnv()).toEqual({
      SCOUT__PLUGINS__YOUTUBE__APIKEY: API_KEY,
    });
    expect(signInStatus.method).toBe('oauth');
    expect(signInAfterKey.vault.has('youtube.api-key')).toBe(false);
    expect(await signInAfterKey.auth.workerEnv()).toEqual({
      SCOUT__PLUGINS__YOUTUBE__TOKENFILE: TOKEN_FILE,
    });
  });

  it('removing the key disconnects and tells the worker to restart', async () => {
    const fake = makeGoogleFake();
    await fake.auth.saveApiKey(API_KEY);

    const status = await fake.auth.clearApiKey();

    expect(status).toMatchObject({ connected: false, method: null });
    expect(fake.vault.has('youtube.api-key')).toBe(false);
    expect(await fake.auth.workerEnv()).toEqual({});
    expect(fake.connectionChanges).toBe(2);
  });
});
