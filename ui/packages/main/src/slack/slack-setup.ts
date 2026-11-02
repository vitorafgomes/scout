import { ScoutError } from '@scout/shared';

const CHECK_TIMEOUT_MS = 5_000;
const AUTH_TEST_URL = 'https://slack.com/api/auth.test';
const CONNECTIONS_OPEN_URL = 'https://slack.com/api/apps.connections.open';
const CREATE_APP_URL = 'https://api.slack.com/apps';
const REJECTED_TOKEN_ERRORS = new Set([
  'invalid_auth',
  'not_authed',
  'token_revoked',
  'token_expired',
  'account_inactive',
]);

/**
 * The app manifest Scout asks Slack to create: Socket Mode, no request URLs, no interactivity,
 * and only the events and scopes the Slack adapter reads and writes.
 */
export const SLACK_MANIFEST = Object.freeze({
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

/** The Slack link that opens the app creation flow prefilled with Scout's manifest. */
export function manifestUrl(): string {
  return `${CREATE_APP_URL}?new_app=1&manifest_json=${encodeURIComponent(JSON.stringify(SLACK_MANIFEST))}`;
}

export type SlackTokenKind = 'bot' | 'app';

const TOKEN_NAME: Readonly<Record<SlackTokenKind, string>> = {
  bot: 'bot token',
  app: 'app-level token',
};

const TOKEN_PREFIX: Readonly<Record<SlackTokenKind, string>> = { bot: 'xoxb-', app: 'xapp-' };

/** The workspace and bot user a bot token belongs to. */
export interface SlackIdentity {
  readonly team: string;
  readonly botUser: string;
}

/** Checks a bot token with auth.test; the token goes only in the Authorization header. */
export async function checkBotToken(
  fetchFn: typeof fetch,
  token: string,
  timeoutMs: number = CHECK_TIMEOUT_MS,
): Promise<SlackIdentity> {
  const body = await callSlack(fetchFn, AUTH_TEST_URL, 'bot', token, timeoutMs);
  return {
    team: typeof body['team'] === 'string' ? body['team'] : '',
    botUser: typeof body['user'] === 'string' ? body['user'] : '',
  };
}

/**
 * Checks an app-level token by asking apps.connections.open for a Socket Mode URL; only `ok`
 * is read, the returned URL is dropped.
 */
export async function checkAppToken(
  fetchFn: typeof fetch,
  token: string,
  timeoutMs: number = CHECK_TIMEOUT_MS,
): Promise<void> {
  await callSlack(fetchFn, CONNECTIONS_OPEN_URL, 'app', token, timeoutMs);
}

/** Rejects a token that does not carry the prefix Slack gives its kind, before any request. */
export function requireTokenPrefix(kind: SlackTokenKind, token: string): void {
  if (!token.startsWith(TOKEN_PREFIX[kind])) {
    throw new ScoutError(
      'slack/wrong-prefix',
      `The ${TOKEN_NAME[kind]} starts with ${TOKEN_PREFIX[kind]}.`,
    );
  }
}

async function callSlack(
  fetchFn: typeof fetch,
  url: string,
  kind: SlackTokenKind,
  token: string,
  timeoutMs: number,
): Promise<Record<string, unknown>> {
  requireTokenPrefix(kind, token);
  let response: Response;
  try {
    response = await fetchFn(url, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${token}`,
        'Content-Type': 'application/x-www-form-urlencoded',
      },
      signal: AbortSignal.timeout(timeoutMs),
    });
  } catch {
    throw new ScoutError(
      'slack/unreachable',
      'Could not reach Slack. Check the connection and try again.',
    );
  }
  let body: Record<string, unknown>;
  try {
    body = (await response.json()) as Record<string, unknown>;
  } catch {
    throw requestFailed(kind, `HTTP ${response.status}`);
  }
  if (body['ok'] === true) return body;
  const error = typeof body['error'] === 'string' ? body['error'] : `HTTP ${response.status}`;
  if (REJECTED_TOKEN_ERRORS.has(error)) {
    throw new ScoutError('slack/token-rejected', `Slack rejected the ${TOKEN_NAME[kind]}.`);
  }
  throw requestFailed(kind, error);
}

function requestFailed(kind: SlackTokenKind, reason: string): ScoutError {
  const safeReason = /^[A-Za-z0-9_ ]{1,64}$/.test(reason) ? reason : 'unexpected answer';
  return new ScoutError(
    'slack/request-failed',
    `Slack could not check the ${TOKEN_NAME[kind]} (${safeReason}).`,
  );
}
