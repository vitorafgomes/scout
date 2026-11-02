import { describe, expect, it } from 'bun:test';
import { ScoutError } from '@scout/shared';

import { checkAppToken, checkBotToken, manifestUrl } from '../src/slack/slack-setup.js';

const BOT_TOKEN = 'xoxb-1111-2222-bot-secret';
const APP_TOKEN = 'xapp-1-A111-3333-app-secret';
const SOCKET_URL = 'wss://wss-primary.slack.com/link/?ticket=socket-secret';

interface Call {
  readonly url: string;
  readonly method: string | undefined;
  readonly authorization: string | null;
}

function fakeFetch(answer: () => Promise<Response>): { fetch: typeof fetch; calls: Call[] } {
  const calls: Call[] = [];
  const fn = (async (input: string | URL | Request, init?: RequestInit) => {
    calls.push({
      url: String(input),
      method: init?.method,
      authorization: new Headers(init?.headers).get('Authorization'),
    });
    return answer();
  }) as typeof fetch;
  return { fetch: fn, calls };
}

const json =
  (body: unknown, status = 200) =>
  async () =>
    new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

async function failure(promise: Promise<unknown>): Promise<ScoutError> {
  try {
    await promise;
  } catch (err: unknown) {
    if (err instanceof ScoutError) return err;
    throw err;
  }
  throw new Error('expected a rejection');
}

describe('Slack manifest link', () => {
  it('opens Slack app creation prefilled with a Socket Mode manifest and exactly the scopes and events the adapter needs', () => {
    const url = new URL(manifestUrl());

    expect(`${url.origin}${url.pathname}`).toBe('https://api.slack.com/apps');
    expect(url.searchParams.get('new_app')).toBe('1');
    expect(JSON.parse(url.searchParams.get('manifest_json') ?? 'null')).toEqual({
      display_information: { name: 'Scout' },
      features: {
        app_home: { messages_tab_enabled: true, messages_tab_read_only_enabled: false },
        bot_user: { display_name: 'scout', always_online: true },
      },
      oauth_config: {
        scopes: { bot: ['channels:history', 'groups:history', 'im:history', 'chat:write'] },
      },
      settings: {
        event_subscriptions: { bot_events: ['message.channels', 'message.groups', 'message.im'] },
        interactivity: { is_enabled: false },
        org_deploy_enabled: false,
        socket_mode_enabled: true,
        token_rotation_enabled: false,
      },
    });
  });
});

describe('checkBotToken', () => {
  it('returns the workspace and bot user from auth.test, sending the token only as a Bearer header', async () => {
    const f = fakeFetch(json({ ok: true, team: 'Acme', user: 'scout', user_id: 'U1' }));

    const identity = await checkBotToken(f.fetch, BOT_TOKEN);

    expect(identity).toEqual({ team: 'Acme', botUser: 'scout' });
    expect(f.calls).toEqual([
      {
        url: 'https://slack.com/api/auth.test',
        method: 'POST',
        authorization: `Bearer ${BOT_TOKEN}`,
      },
    ]);
  });

  it.each(['invalid_auth', 'not_authed', 'token_revoked'])(
    'reports a token Slack answers with %s as rejected, without the token in the message',
    async (error) => {
      const err = await failure(
        checkBotToken(fakeFetch(json({ ok: false, error })).fetch, BOT_TOKEN),
      );

      expect(err.code).toBe('slack/token-rejected');
      expect(err.message).toBe('Slack rejected the bot token.');
    },
  );

  it('reports a network failure as unreachable', async () => {
    const f = fakeFetch(() => Promise.reject(new TypeError('fetch failed: ECONNRESET')));

    const err = await failure(checkBotToken(f.fetch, BOT_TOKEN));

    expect(err.code).toBe('slack/unreachable');
    expect(err.message).toBe('Could not reach Slack. Check the connection and try again.');
  });

  it('reports a 5xx with a non-JSON body as a failed request naming the status', async () => {
    const f = fakeFetch(async () => new Response('<html>bad gateway</html>', { status: 502 }));

    const err = await failure(checkBotToken(f.fetch, BOT_TOKEN));

    expect(err.code).toBe('slack/request-failed');
    expect(err.message).toBe('Slack could not check the bot token (HTTP 502).');
  });

  it('refuses a token without the xoxb- prefix before calling Slack', async () => {
    const f = fakeFetch(json({ ok: true }));

    const err = await failure(checkBotToken(f.fetch, APP_TOKEN));

    expect(err.code).toBe('slack/wrong-prefix');
    expect(err.message).toBe('The bot token starts with xoxb-.');
    expect(f.calls).toEqual([]);
  });
});

describe('checkAppToken', () => {
  it('accepts an app-level token apps.connections.open answers ok for, and returns nothing of the socket URL', async () => {
    const f = fakeFetch(json({ ok: true, url: SOCKET_URL }));

    const result = await checkAppToken(f.fetch, APP_TOKEN);

    expect(result).toBeUndefined();
    expect(f.calls).toEqual([
      {
        url: 'https://slack.com/api/apps.connections.open',
        method: 'POST',
        authorization: `Bearer ${APP_TOKEN}`,
      },
    ]);
  });

  it('reports an app-level token Slack answers with invalid_auth as rejected', async () => {
    const err = await failure(
      checkAppToken(fakeFetch(json({ ok: false, error: 'invalid_auth' })).fetch, APP_TOKEN),
    );

    expect(err.code).toBe('slack/token-rejected');
    expect(err.message).toBe('Slack rejected the app-level token.');
  });

  it('names a Slack error that is not a rejection, such as a token without connections:write', async () => {
    const err = await failure(
      checkAppToken(fakeFetch(json({ ok: false, error: 'missing_scope' })).fetch, APP_TOKEN),
    );

    expect(err.code).toBe('slack/request-failed');
    expect(err.message).toBe('Slack could not check the app-level token (missing_scope).');
  });

  it('reports a network failure as unreachable', async () => {
    const f = fakeFetch(() => Promise.reject(new DOMException('timed out', 'TimeoutError')));

    const err = await failure(checkAppToken(f.fetch, APP_TOKEN));

    expect(err.code).toBe('slack/unreachable');
  });

  it('refuses a token without the xapp- prefix before calling Slack', async () => {
    const f = fakeFetch(json({ ok: true }));

    const err = await failure(checkAppToken(f.fetch, BOT_TOKEN));

    expect(err.code).toBe('slack/wrong-prefix');
    expect(err.message).toBe('The app-level token starts with xapp-.');
    expect(f.calls).toEqual([]);
  });
});
