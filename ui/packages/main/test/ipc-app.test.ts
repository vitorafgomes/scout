import { describe, it, expect, beforeEach } from 'bun:test';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';
import { registerAppHandlers } from '../src/ipc/app.js';
import { manifestUrl } from '../src/slack/slack-setup.js';
import { createLogger } from '../src/util/logger.js';

const logger = createLogger('test', { sink: () => {} });
const noShow = (): void => undefined;

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => Promise<unknown>;

interface FakeIpcMain extends Pick<IpcMain, 'handle' | 'removeHandler'> {
  handlers: Map<string, IpcHandler>;
}

function makeFakeIpcMain(): FakeIpcMain {
  const handlers: Map<string, IpcHandler> = new Map();
  return {
    handlers,
    handle(channel: string, handler: IpcHandler): void {
      handlers.set(channel, handler);
    },
    removeHandler(channel: string): void {
      handlers.delete(channel);
    },
  };
}

function invoke(ipc: FakeIpcMain, channel: string, req?: unknown): Promise<unknown> {
  const fn = ipc.handlers.get(channel);
  if (!fn) throw new Error(`No handler for ${channel}`);
  return fn(null as unknown as IpcMainInvokeEvent, req);
}

function makeShell() {
  const calls: string[] = [];
  return {
    calls,
    openExternal: async (url: string) => {
      calls.push(url);
    },
  };
}

function makeApp(version = '1.2.3') {
  let quit = false;
  return {
    getVersion: () => version,
    quit: () => {
      quit = true;
    },
    wasQuitCalled: () => quit,
  };
}

describe('registerAppHandlers — appOpenExternal', () => {
  let ipc: FakeIpcMain;
  let shell: ReturnType<typeof makeShell>;
  let app: ReturnType<typeof makeApp>;

  beforeEach(() => {
    ipc = makeFakeIpcMain();
    shell = makeShell();
    app = makeApp();
    registerAppHandlers(ipc, shell, app, noShow, logger);
  });

  it('allows https://discord.com URLs and calls shell.openExternal', async () => {
    await invoke(ipc, 'scout:app.openExternal', {
      url: 'https://discord.com/oauth2/authorize?client_id=1',
    });
    expect(shell.calls).toContain('https://discord.com/oauth2/authorize?client_id=1');
  });

  it('opens the Slack app creation link with the manifest it was given', async () => {
    const url = manifestUrl();

    await invoke(ipc, 'scout:app.openExternal', { url });

    expect(shell.calls).toEqual([url]);
  });

  it('allows https://discordapp.com', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://discordapp.com/path' });
    expect(shell.calls.length).toBe(1);
  });

  it('allows https://support-dev.discord.com', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://support-dev.discord.com/hc' });
    expect(shell.calls.length).toBe(1);
  });

  it('allows https://github.com', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://github.com/scout-org/scout' });
    expect(shell.calls.length).toBe(1);
  });

  it('opens the Scout repository and its MIT licence, the two links in Settings, About', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://github.com/vitorafgomes/scout' });
    await invoke(ipc, 'scout:app.openExternal', {
      url: 'https://github.com/vitorafgomes/scout/blob/main/LICENSE',
    });

    expect(shell.calls).toEqual([
      'https://github.com/vitorafgomes/scout',
      'https://github.com/vitorafgomes/scout/blob/main/LICENSE',
    ]);
  });

  it('opens the Google Cloud Console page that creates a YouTube API key', async () => {
    await invoke(ipc, 'scout:app.openExternal', {
      url: 'https://console.cloud.google.com/apis/credentials',
    });
    expect(shell.calls).toEqual(['https://console.cloud.google.com/apis/credentials']);
  });

  it('opens YouTube Studio for the connected channel', async () => {
    await invoke(ipc, 'scout:app.openExternal', {
      url: 'https://studio.youtube.com/channel/UCscoutTestChannel',
    });
    expect(shell.calls).toEqual(['https://studio.youtube.com/channel/UCscoutTestChannel']);
  });

  it('opens the connected channel on YouTube', async () => {
    await invoke(ipc, 'scout:app.openExternal', {
      url: 'https://www.youtube.com/channel/UCscoutTestChannel',
    });
    expect(shell.calls).toEqual(['https://www.youtube.com/channel/UCscoutTestChannel']);
  });

  it("opens Docker's install guide that the Docker card links to", async () => {
    await invoke(ipc, 'scout:app.openExternal', {
      url: 'https://docs.docker.com/get-started/get-docker/',
    });
    expect(shell.calls).toEqual(['https://docs.docker.com/get-started/get-docker/']);
  });

  it('opens the youtu.be link of a video the operator uploaded', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://youtu.be/abc123XYZ_-' });
    expect(shell.calls).toEqual(['https://youtu.be/abc123XYZ_-']);
  });

  it('opens the usage page of OpenAI, Google AI Studio and ChatGPT that the AI Sources cards link to', async () => {
    const urls = [
      'https://platform.openai.com/usage',
      'https://aistudio.google.com/usage',
      'https://chatgpt.com/#settings',
    ];
    for (const url of urls) await invoke(ipc, 'scout:app.openExternal', { url });
    expect(shell.calls).toEqual(urls);
  });

  it('opens the Download Ollama link of the AI Sources card', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://ollama.com/download' });
    expect(shell.calls).toEqual(['https://ollama.com/download']);
  });

  it('opens the Browse the Ollama library link of the Local models in Ollama dialog in the system browser', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://ollama.com/library' });
    expect(shell.calls).toEqual(['https://ollama.com/library']);
  });

  it('opens the Download LM Studio link of the AI Sources card', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://lmstudio.ai' });
    expect(shell.calls).toEqual(['https://lmstudio.ai']);
  });

  it('opens the Anthropic usage page that the Claude API card links to', async () => {
    await invoke(ipc, 'scout:app.openExternal', { url: 'https://platform.claude.com/usage' });
    expect(shell.calls).toEqual(['https://platform.claude.com/usage']);
  });

  it('rejects https://evil.example and does NOT call shell.openExternal', async () => {
    let caught: unknown;
    try {
      await invoke(ipc, 'scout:app.openExternal', { url: 'https://evil.example/steal' });
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(Error);
    expect((caught as Error & { code?: string }).code).toBe('app/external-host-not-allowed');
    expect(shell.calls.length).toBe(0);
  });

  it('rejects http:// URLs (non-HTTPS)', async () => {
    let caught: unknown;
    try {
      await invoke(ipc, 'scout:app.openExternal', { url: 'http://discord.com/oauth2' });
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(Error);
    expect((caught as Error & { code?: string }).code).toBe('app/external-host-not-allowed');
    expect(shell.calls.length).toBe(0);
  });

  it('rejects javascript: scheme', async () => {
    let caught: unknown;
    try {
      await invoke(ipc, 'scout:app.openExternal', { url: 'javascript:alert(1)' });
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(Error);
    expect((caught as Error & { code?: string }).code).toBe('app/external-host-not-allowed');
    expect(shell.calls.length).toBe(0);
  });

  it('rejects file:// scheme', async () => {
    let caught: unknown;
    try {
      await invoke(ipc, 'scout:app.openExternal', { url: 'file:///etc/passwd' });
    } catch (e) {
      caught = e;
    }
    expect((caught as Error & { code?: string }).code).toBe('app/external-host-not-allowed');
    expect(shell.calls.length).toBe(0);
  });

  for (const [reason, logged] of [
    ['spawn xdg-open ENOENT', 'spawn xdg-open ENOENT'],
    [
      'No application knows how to open URL https://discord.com/oauth2/authorize?client_id=111222333&state=abc',
      'No application knows how to open URL [address]',
    ],
  ] as const) {
    it(`a browser that cannot be opened ("${reason.slice(0, 40)}") is logged with its reason and the host, never the address, and the window is still answered with the failure`, async () => {
      const lines: Record<string, unknown>[] = [];
      const failing = makeFakeIpcMain();
      registerAppHandlers(
        failing,
        {
          openExternal: async () => {
            throw new Error(reason);
          },
        },
        app,
        noShow,
        createLogger('test', { sink: (line) => lines.push(JSON.parse(line)) }),
      );

      const answer = await invoke(failing, 'scout:app.openExternal', {
        url: 'https://discord.com/oauth2/authorize?client_id=111222333&state=abc',
      }).then(
        () => 'opened',
        (err: unknown) => (err instanceof Error ? err.message : String(err)),
      );
      const written = JSON.stringify(lines);

      expect({
        answer,
        logged: lines.map((line) => [line['lvl'], line['msg'], line['ctx']]),
        addressInLog: ['client_id', '111222333', '/oauth2', 'state=', 'abc', 'https://'].filter(
          (part) => written.includes(part),
        ),
      }).toEqual({
        answer: `Error: ${reason}`,
        logged: [['error', 'app: openExternal failed', { host: 'discord.com', reason: logged }]],
        addressInLog: [],
      });
    });
  }
});

describe('registerAppHandlers — appGetVersion', () => {
  it('returns the app version', async () => {
    const ipc = makeFakeIpcMain();
    const app = makeApp('2.0.0');
    registerAppHandlers(ipc, makeShell(), app, noShow, logger);
    const result = (await invoke(ipc, 'scout:app.getVersion')) as { version: string };
    expect(result.version).toBe('2.0.0');
  });
});

describe('registerAppHandlers — appQuit', () => {
  it('calls app.quit', async () => {
    const ipc = makeFakeIpcMain();
    const app = makeApp();
    registerAppHandlers(ipc, makeShell(), app, noShow, logger);
    await invoke(ipc, 'scout:app.quit');
    expect(app.wasQuitCalled()).toBe(true);
  });
});

describe('registerAppHandlers - appShowMainWindow', () => {
  it('appShowMainWindow shows the Scout window: one show per call, answered with nothing', async () => {
    const ipc = makeFakeIpcMain();
    let shown = 0;
    registerAppHandlers(
      ipc,
      makeShell(),
      makeApp(),
      () => {
        shown += 1;
      },
      logger,
    );

    const answer = await invoke(ipc, 'scout:app.showMainWindow');

    expect({ answer, shown }).toEqual({ answer: undefined, shown: 1 });
  });
});

describe('registerAppHandlers — unregister', () => {
  it('removes all four handlers', () => {
    const ipc = makeFakeIpcMain();
    const unregister = registerAppHandlers(ipc, makeShell(), makeApp(), noShow, logger);
    unregister();
    expect(ipc.handlers.has('scout:app.openExternal')).toBe(false);
    expect(ipc.handlers.has('scout:app.getVersion')).toBe(false);
    expect(ipc.handlers.has('scout:app.quit')).toBe(false);
    expect(ipc.handlers.has('scout:app.showMainWindow')).toBe(false);
    expect(ipc.handlers.size).toBe(0);
  });
});
