import { describe, expect, it } from 'bun:test';
import { CHANNEL, VaultUnavailableError, readIpcError, type WorkerStatus } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { restartWorkerIfRunning } from '../src/ipc/github.js';
import { readSlackTokens, registerSlackHandlers } from '../src/ipc/slack.js';
import type { SlackTokens } from '../src/worker/build-env.js';
import { recordingLogger } from './kubernetes-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;
const BOT_TOKEN = 'xoxb-1111-2222-bot-secret';
const APP_TOKEN = 'xapp-1-A111-3333-app-secret';
const SOCKET_URL = 'wss://wss-primary.slack.com/link/?ticket=socket-secret';
const SECRETS = [BOT_TOKEN, APP_TOKEN, SOCKET_URL, 'socket-secret'];

type SlackAnswer = (url: string) => Promise<Response>;

const ok: SlackAnswer = async (url) =>
  Response.json(
    url.endsWith('/auth.test')
      ? { ok: true, team: 'Acme', user: 'scout', user_id: 'U0BOT' }
      : { ok: true, url: SOCKET_URL },
  );

function setup(answer: SlackAnswer = ok, workerState: WorkerStatus['state'] = 'running') {
  const handlers = new Map<string, IpcHandler>();
  const ipc: Pick<IpcMain, 'handle' | 'removeHandler'> = {
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
  const entries = new Map<string, string>();
  const vault = {
    get: async (key: string) => entries.get(key) ?? null,
    store: async (key: string, value: string) => {
      entries.set(key, value);
    },
    delete: async (key: string) => entries.delete(key),
  };
  const calls: string[] = [];
  const worker = {
    status: { state: workerState, restarts: 0 } as WorkerStatus,
    async stop() {
      calls.push('stop');
      return { exitCode: 0 };
    },
    async start() {
      calls.push('start');
      return { pid: 42 };
    },
  };
  const { logger, lines } = recordingLogger();
  let restart: Promise<void> = Promise.resolve();
  const fetchFn = ((input: string | URL | Request) => answer(String(input))) as typeof fetch;
  const dispose = registerSlackHandlers(ipc, {
    vault,
    fetch: fetchFn,
    onConnectionChanged: () => {
      restart = restartWorkerIfRunning(worker, logger);
    },
    logger,
  });
  const call = async (channel: string, req?: unknown): Promise<unknown> => {
    const handler = handlers.get(channel);
    if (handler === undefined) throw new Error(`no handler for ${channel}`);
    const result = await handler(NULL_EVENT, req);
    await restart;
    return result;
  };
  const save = (botToken: unknown = BOT_TOKEN, appToken: unknown = APP_TOKEN) =>
    call(CHANNEL.slackSave, { botToken, appToken });
  return { handlers, entries, vault, calls, lines, call, save, dispose };
}

async function rejection(
  promise: Promise<unknown>,
): Promise<{ code: string | null; message: string; raw: string }> {
  try {
    await promise;
  } catch (err: unknown) {
    const raw = (err as Error).message;
    return { ...readIpcError(new Error(raw)), raw };
  }
  throw new Error('expected a rejection');
}

function expectNoSecret(texts: readonly string[]): void {
  for (const text of texts) {
    for (const secret of SECRETS) expect(text).not.toContain(secret);
  }
}

describe('Slack IPC', () => {
  it('manifestUrl gives the renderer the prefilled Slack app creation link', async () => {
    const s = setup();

    const { url } = (await s.call(CHANNEL.slackManifestUrl)) as { url: string };

    expect(url.startsWith('https://api.slack.com/apps?new_app=1&manifest_json=')).toBe(true);
  });

  it('save checks both tokens, stores both, restarts a running worker and answers with the team and bot user only', async () => {
    const s = setup();

    const status = await s.save(` ${BOT_TOKEN} `, APP_TOKEN);

    expect(status).toEqual({ connected: true, team: 'Acme', botUser: 'scout' });
    expect([...s.entries.keys()]).toEqual(['slack.connection']);
    expect(JSON.parse(s.entries.get('slack.connection') ?? 'null')).toEqual({
      botToken: BOT_TOKEN,
      appToken: APP_TOKEN,
      team: 'Acme',
      botUser: 'scout',
    });
    expect(await readSlackTokens(s.vault)).toEqual({ botToken: BOT_TOKEN, appToken: APP_TOKEN });
    expect(s.calls).toEqual(['stop', 'start']);
  });

  it('save leaves a stopped worker stopped', async () => {
    const s = setup(ok, 'stopped');

    await s.save();

    expect(s.calls).toEqual([]);
  });

  it('never logs or returns a token or the socket URL', async () => {
    const s = setup();

    const saved = await s.save();
    const status = await s.call(CHANNEL.slackStatus);
    const disconnected = await s.call(CHANNEL.slackDisconnect);

    expectNoSecret([
      JSON.stringify(saved),
      JSON.stringify(status),
      JSON.stringify(disconnected),
      ...s.lines,
    ]);
    expect(s.lines.length).toBeGreaterThan(0);
  });

  it('a rejected app-level token stores nothing, leaves the worker alone and keeps both tokens out of the error and the logs', async () => {
    const s = setup(async (url) =>
      url.endsWith('/auth.test')
        ? Response.json({ ok: true, team: 'Acme', user: 'scout' })
        : Response.json({ ok: false, error: 'invalid_auth' }),
    );

    const err = await rejection(s.save());

    expect(err.code).toBe('slack/token-rejected');
    expect(err.message).toBe('Slack rejected the app-level token.');
    expect(s.entries.size).toBe(0);
    expect(s.calls).toEqual([]);
    expectNoSecret([err.raw, ...s.lines]);
  });

  it('a rejected bot token stores nothing and never sends the app-level token', async () => {
    const urls: string[] = [];
    const s = setup(async (url) => {
      urls.push(url);
      return Response.json({ ok: false, error: 'invalid_auth' });
    });

    const err = await rejection(s.save());

    expect(err.message).toBe('Slack rejected the bot token.');
    expect(urls).toEqual(['https://slack.com/api/auth.test']);
    expect(s.entries.size).toBe(0);
  });

  it('Slack unreachable while saving: nothing is stored and the owner is told to retry', async () => {
    const s = setup(() => Promise.reject(new TypeError('fetch failed')));

    const err = await rejection(s.save());

    expect(err.code).toBe('slack/unreachable');
    expect(s.entries.size).toBe(0);
    expect(s.calls).toEqual([]);
  });

  it('tokens pasted in the wrong fields are refused before any call to Slack', async () => {
    const urls: string[] = [];
    const s = setup(async (url) => {
      urls.push(url);
      return Response.json({ ok: true });
    });

    const err = await rejection(s.save(APP_TOKEN, BOT_TOKEN));

    expect(err.code).toBe('slack/wrong-prefix');
    expect(err.message).toBe('The bot token starts with xoxb-.');
    expect(urls).toEqual([]);
    expectNoSecret([err.raw]);
  });

  it('status is not connected when the stored connection lacks a token', async () => {
    const s = setup();
    await s.vault.store(
      'slack.connection',
      JSON.stringify({ botToken: BOT_TOKEN, team: 'Acme', botUser: 'scout' }),
    );

    expect(await s.call(CHANNEL.slackStatus)).toEqual({
      connected: false,
      team: null,
      botUser: null,
    });
    expect(await readSlackTokens(s.vault)).toBeNull();
  });

  it('a vault that fails to store on a re-save keeps the previous connection whole, reports a typed error and restarts nothing', async () => {
    const s = setup();
    await s.save();
    const before = s.entries.get('slack.connection');
    s.calls.length = 0;
    s.vault.store = async () => {
      throw new VaultUnavailableError();
    };

    const err = await rejection(s.save('xoxb-9999-new-bot', 'xapp-1-B999-new-app'));

    expect(err.code).toBe('vault/unavailable');
    expect(s.entries.get('slack.connection')).toBe(before);
    expect(await readSlackTokens(s.vault)).toEqual({ botToken: BOT_TOKEN, appToken: APP_TOKEN });
    expect(s.calls).toEqual([]);
  });

  it.each([0, 1, 2])(
    'a vault that stops storing after %i writes during a re-save leaves either the old pair or the new pair, never a mix',
    async (writesBeforeFailure) => {
      const s = setup();
      await s.save();
      const store = s.vault.store;
      let writes = 0;
      s.vault.store = async (key: string, value: string) => {
        if (writes++ >= writesBeforeFailure) throw new VaultUnavailableError();
        await store(key, value);
      };

      await s.save('xoxb-9999-new-bot', 'xapp-1-B999-new-app').catch(() => undefined);

      const pairs: (SlackTokens | null)[] = [
        { botToken: BOT_TOKEN, appToken: APP_TOKEN },
        { botToken: 'xoxb-9999-new-bot', appToken: 'xapp-1-B999-new-app' },
      ];
      expect(pairs).toContainEqual(await readSlackTokens(s.vault));
    },
  );

  it('disconnect clears both tokens and restarts a running worker', async () => {
    const s = setup();
    await s.save();
    s.calls.length = 0;

    const status = await s.call(CHANNEL.slackDisconnect);

    expect(status).toEqual({ connected: false, team: null, botUser: null });
    expect(s.entries.size).toBe(0);
    expect(s.calls).toEqual(['stop', 'start']);
  });
});
