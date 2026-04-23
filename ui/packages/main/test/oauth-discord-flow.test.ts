import { describe, it, expect } from 'bun:test';
import { FlowController } from '../src/oauth/discord-flow.js';
import type { DiscordFlowDeps, DiscordFlowConfig } from '../src/oauth/discord-flow.js';
import { OAuthUserCancelledError, DiscordTokenExchangeError } from '@scout/shared';
import { createLogger } from '../src/util/logger.js';

const logger = createLogger('test', { sink: () => {} });

type FakeFetch = (
  url: Parameters<typeof fetch>[0],
  init?: Parameters<typeof fetch>[1],
) => Promise<Response>;

function makeGoodTokenResponse(guildId = 'g1', guildName = 'MyGuild'): Response {
  return new Response(
    JSON.stringify({
      access_token: 'tok',
      token_type: 'Bearer',
      guild: { id: guildId, name: guildName },
    }),
    { status: 200, headers: { 'Content-Type': 'application/json' } },
  );
}

function makeDeps(overrides?: Partial<DiscordFlowDeps>): DiscordFlowDeps {
  const fakeFetch: FakeFetch = async () => makeGoodTokenResponse();
  return {
    awaitCallback: async () => ({ code: 'authcode', state: 'fixed-state' }),
    fetch: fakeFetch as typeof fetch,
    shell: { openExternal: async (_url: string) => {} },
    logger,
    findFreePort: async () => 53682,
    ...overrides,
  };
}

const baseConfig: DiscordFlowConfig = {
  clientId: 'client1',
  permissions: '8',
};

describe('FlowController.start — happy path', () => {
  it('resolves with guildId and guildName', async () => {
    const ctrl = new FlowController();
    const result = await ctrl.start(baseConfig, makeDeps());
    expect(result.guildId).toBe('g1');
    expect(result.guildName).toBe('MyGuild');
  });

  it('calls shell.openExternal exactly once', async () => {
    const calls: string[] = [];
    const deps = makeDeps({
      shell: {
        openExternal: async (u) => {
          calls.push(u);
        },
      },
    });
    const ctrl = new FlowController();
    await ctrl.start(baseConfig, deps);
    expect(calls.length).toBe(1);
    expect(calls[0]).toContain('discord.com/oauth2/authorize');
  });

  it('calls fetch exactly once against the token endpoint', async () => {
    const calls: string[] = [];
    const fakeFetch: FakeFetch = async (url, _init) => {
      calls.push(String(url));
      return makeGoodTokenResponse();
    };
    const deps = makeDeps({ fetch: fakeFetch as typeof fetch });
    const ctrl = new FlowController();
    await ctrl.start(baseConfig, deps);
    expect(calls.length).toBe(1);
    expect(calls[0]).toBe('https://discord.com/api/oauth2/token');
  });

  it('includes code_verifier in the token request body', async () => {
    let body = '';
    const fakeFetch: FakeFetch = async (_url, init) => {
      body = String(init?.body ?? '');
      return makeGoodTokenResponse();
    };
    const deps = makeDeps({ fetch: fakeFetch as typeof fetch });
    const ctrl = new FlowController();
    await ctrl.start(baseConfig, deps);
    expect(body).toContain('code_verifier=');
  });
});

describe('FlowController.start — Discord error responses', () => {
  it('rejects with DiscordTokenExchangeError on 400 invalid_grant', async () => {
    const fakeFetch: FakeFetch = async () =>
      new Response(JSON.stringify({ error: 'invalid_grant', error_description: 'Invalid code' }), {
        status: 400,
        headers: { 'Content-Type': 'application/json' },
      });
    const deps = makeDeps({ fetch: fakeFetch as typeof fetch });
    const ctrl = new FlowController();
    await expect(ctrl.start(baseConfig, deps)).rejects.toBeInstanceOf(DiscordTokenExchangeError);
  });

  it('error description is propagated in the rejection', async () => {
    const fakeFetch: FakeFetch = async () =>
      new Response(
        JSON.stringify({ error: 'invalid_grant', error_description: 'Code already used' }),
        { status: 400, headers: { 'Content-Type': 'application/json' } },
      );
    const deps = makeDeps({ fetch: fakeFetch as typeof fetch });
    const ctrl = new FlowController();
    let caught: unknown;
    try {
      await ctrl.start(baseConfig, deps);
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(DiscordTokenExchangeError);
    expect((caught as DiscordTokenExchangeError).message).toContain('Code already used');
  });

  it('rejects with DiscordTokenExchangeError when guild is missing from response', async () => {
    const fakeFetch: FakeFetch = async () =>
      new Response(JSON.stringify({ access_token: 'tok', token_type: 'Bearer' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    const deps = makeDeps({ fetch: fakeFetch as typeof fetch });
    const ctrl = new FlowController();
    await expect(ctrl.start(baseConfig, deps)).rejects.toBeInstanceOf(DiscordTokenExchangeError);
  });

  it('rejects with DiscordTokenExchangeError on fetch AbortError (timeout)', async () => {
    const fakeFetch: FakeFetch = async () => {
      const err = new Error('The operation was aborted');
      err.name = 'AbortError';
      throw err;
    };
    const deps = makeDeps({ fetch: fakeFetch as typeof fetch });
    const ctrl = new FlowController();
    await expect(ctrl.start(baseConfig, deps)).rejects.toBeInstanceOf(DiscordTokenExchangeError);
  });
});

describe('FlowController.cancel', () => {
  it('cancel during install causes start to reject with OAuthUserCancelledError', async () => {
    let triggerAbort!: () => void;

    const deps = makeDeps({
      awaitCallback: () =>
        new Promise<{ code: string; state: string }>((_resolve, reject) => {
          triggerAbort = () => reject(new OAuthUserCancelledError('cancelled'));
        }),
    });

    const ctrl = new FlowController();
    const startPromise = ctrl.start(baseConfig, deps);

    await new Promise<void>((r) => setTimeout(r, 10));
    triggerAbort();

    await expect(startPromise).rejects.toBeInstanceOf(OAuthUserCancelledError);
  });

  it('subsequent start succeeds after a cancel', async () => {
    let triggerAbort!: () => void;

    const deps = makeDeps({
      awaitCallback: () =>
        new Promise<{ code: string; state: string }>((_resolve, reject) => {
          triggerAbort = () => reject(new OAuthUserCancelledError('cancelled'));
        }),
    });

    const ctrl = new FlowController();
    const first = ctrl.start(baseConfig, deps);
    await new Promise<void>((r) => setTimeout(r, 10));
    triggerAbort();
    await expect(first).rejects.toBeInstanceOf(OAuthUserCancelledError);

    const result = await ctrl.start(baseConfig, makeDeps());
    expect(result.guildId).toBe('g1');
  });
});

describe('FlowController — concurrent install rejection', () => {
  it('second start rejects while first is in flight', async () => {
    const deps = makeDeps({
      awaitCallback: () =>
        new Promise<{ code: string; state: string }>(() => {
          // never resolves
        }),
    });

    const ctrl = new FlowController();
    void ctrl.start(baseConfig, deps);

    await new Promise<void>((r) => setTimeout(r, 10));

    await expect(ctrl.start(baseConfig, makeDeps())).rejects.toBeInstanceOf(
      DiscordTokenExchangeError,
    );
  });

  it('concurrent rejection message mentions already in progress', async () => {
    const deps = makeDeps({
      awaitCallback: () => new Promise<{ code: string; state: string }>(() => {}),
    });

    const ctrl = new FlowController();
    void ctrl.start(baseConfig, deps);
    await new Promise<void>((r) => setTimeout(r, 10));

    let caught: unknown;
    try {
      await ctrl.start(baseConfig, makeDeps());
    } catch (e) {
      caught = e;
    }
    expect((caught as DiscordTokenExchangeError).message.toLowerCase()).toContain('progress');
  });
});

describe('FlowController — shell.openExternal throws', () => {
  it('rejects when shell.openExternal throws', async () => {
    const deps = makeDeps({
      shell: {
        openExternal: async () => {
          throw new Error('shell error');
        },
      },
    });
    const ctrl = new FlowController();
    await expect(ctrl.start(baseConfig, deps)).rejects.toThrow('shell error');
  });
});
