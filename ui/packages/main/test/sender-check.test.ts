import { describe, expect, it } from 'bun:test';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { CHANNEL, readIpcError } from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import {
  CREDENTIAL_CHANNELS,
  CREDENTIAL_GUARDS,
  QUICK_ASK_GUARDS,
  SHORTCUT_GUARDS,
  SHOW_MAIN_GUARDS,
  guardChannels,
  senderCheck,
} from '../src/ipc/sender-check.js';
import type { Logger } from '../src/util/logger.js';
import { SENDERS, SENDER_HOOKS } from './sender-fakes.js';

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

function recordingLogger(): { logger: Logger; lines: string[] } {
  const lines: string[] = [];
  const record =
    (level: string) =>
    (...args: unknown[]): void => {
      lines.push(`${level} ${JSON.stringify(args)}`);
    };
  const logger: Logger = {
    debug: record('debug'),
    info: record('info'),
    warn: record('warn'),
    error: record('error'),
    child: () => logger,
  };
  return { logger, lines };
}

function guarded(logger: Logger) {
  const handlers = new Map<string, IpcHandler>();
  const reached: Array<[string, unknown]> = [];
  const ipc = guardChannels(
    {
      handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
      removeHandler: (channel: string) => void handlers.delete(channel),
    } as Pick<IpcMain, 'handle' | 'removeHandler'>,
    senderCheck(SENDER_HOOKS, logger),
    CREDENTIAL_GUARDS,
  );
  for (const channel of [...CREDENTIAL_CHANNELS, CHANNEL.vaultHas]) {
    ipc.handle(channel, (_event, payload: unknown) => {
      reached.push([channel, payload]);
      return 'handled';
    });
  }
  return { handlers, reached };
}

const SECRET = { key: 'llm.api-key', value: 'sk-never-logged' };

describe('credential and access channels answer only the main window', () => {
  for (const channel of CREDENTIAL_CHANNELS) {
    for (const [from, event, kind] of SENDERS) {
      if (kind === 'main') {
        it(`${channel}, credentials are changed only in the main window: a call from ${from} reaches its handler and logs no refusal`, async () => {
          const { logger, lines } = recordingLogger();
          const { handlers, reached } = guarded(logger);

          const answer = await handlers.get(channel)!(event, SECRET);

          expect(answer).toBe('handled');
          expect(reached).toEqual([[channel, SECRET]]);
          expect(lines).toEqual([]);
        });
      } else {
        it(`${channel}, credentials are changed only in the main window: a call from ${from} is refused with ipc/not-main-window, reaches nothing, and is logged at warn with the channel and the sender kind, never the payload`, async () => {
          const { logger, lines } = recordingLogger();
          const { handlers, reached } = guarded(logger);

          const refused = await Promise.resolve(handlers.get(channel)!(event, SECRET)).then(
            () => null,
            (err: unknown) => readIpcError(err),
          );

          expect(refused).toEqual({
            code: 'ipc/not-main-window',
            message: 'Credentials and access can be changed only in the Scout window.',
          });
          expect(reached).toEqual([]);
          expect(lines).toEqual([
            `warn ["ipc: call refused for its sender",{"channel":"${channel}","sender":"${kind}"}]`,
          ]);
        });
      }
    }
  }

  it('a channel outside the table, such as scout:vault.has, is not guarded and answers any window', async () => {
    const { logger, lines } = recordingLogger();
    const { handlers, reached } = guarded(logger);
    const [, fromOperator] = SENDERS.find(([, , kind]) => kind === 'operator')!;

    const answer = await handlers.get(CHANNEL.vaultHas)!(fromOperator, { key: 'llm.provider' });

    expect(answer).toBe('handled');
    expect(reached).toEqual([[CHANNEL.vaultHas, { key: 'llm.provider' }]]);
    expect(lines).toEqual([]);
  });

  it('main registers every module that owns a credential channel through the guarded ipc, and those modules own every channel in the table', () => {
    const src = path.join(import.meta.dirname, '../src');
    const main = readFileSync(path.join(src, 'main.ts'), 'utf8');
    const modules: Record<string, string> = {
      registerVaultHandlers: 'ipc/vault.ts',
      registerDiscordHandlers: 'ipc/discord.ts',
      registerChatGptHandlers: 'ipc/chatgpt.ts',
      registerGitHubHandlers: 'ipc/github.ts',
      registerGitHubBotHandlers: 'ipc/github.ts',
      registerYouTubeHandlers: 'ipc/youtube.ts',
      registerKubernetesHandlers: 'ipc/kubernetes.ts',
      registerAccessHandlers: 'ipc/access.ts',
      registerSlackHandlers: 'ipc/slack.ts',
      registerOrgSpendHandlers: 'ipc/usage.ts',
    };

    const passed = Object.keys(modules).map((name) => {
      const call = main.indexOf(`${name}(`, main.indexOf('const credentialIpc = guardChannels('));
      return [name, /^\(\s*credentialIpc,/.test(main.slice(call + name.length))];
    });
    const sources = [...new Set(Object.values(modules))]
      .map((file) => readFileSync(path.join(src, file), 'utf8'))
      .join('\n');
    const owned = Object.entries(CHANNEL)
      .filter(([, value]) => CREDENTIAL_CHANNELS.includes(value))
      .filter(([key]) => !sources.includes(`CHANNEL.${key}`))
      .map(([key]) => key);

    expect(passed).toEqual(Object.keys(modules).map((name) => [name, true]));
    expect(owned).toEqual([]);
  });
});

describe('only the main window lets the quick-ask shortcut go', () => {
  function guardedShortcut(logger: Logger) {
    const handlers = new Map<string, IpcHandler>();
    const reached: unknown[] = [];
    const ipc = guardChannels(
      {
        handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
        removeHandler: (channel: string) => void handlers.delete(channel),
      } as Pick<IpcMain, 'handle' | 'removeHandler'>,
      senderCheck(SENDER_HOOKS, logger),
      SHORTCUT_GUARDS,
    );
    ipc.handle(CHANNEL.localSuspendShortcut, (_event, payload: unknown) => {
      reached.push(payload);
    });
    return { handler: handlers.get(CHANNEL.localSuspendShortcut)!, reached };
  }

  for (const [from, event, kind] of SENDERS) {
    it(`a suspend from ${from} ${kind === 'main' ? 'reaches main' : 'is refused with ipc/not-main-window and reaches nothing'}`, async () => {
      const { logger, lines } = recordingLogger();
      const { handler, reached } = guardedShortcut(logger);

      const refused = await Promise.resolve(handler(event, { suspended: true })).then(
        () => null,
        (err: unknown) => readIpcError(err),
      );

      expect({ refused, reached, lines }).toEqual(
        kind === 'main'
          ? { refused: null, reached: [{ suspended: true }], lines: [] }
          : {
              refused: {
                code: 'ipc/not-main-window',
                message: 'The quick-ask shortcut can be recorded only in the Scout window.',
              },
              reached: [],
              lines: [
                `warn ["ipc: call refused for its sender",{"channel":"${CHANNEL.localSuspendShortcut}","sender":"${kind}"}]`,
              ],
            },
      );
    });
  }

  it('main registers quick ask through an ipc guarded by the shortcut table', () => {
    const main = readFileSync(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');
    const call = main.slice(main.indexOf('quickAsk = await registerQuickAsk({'));

    expect(call.slice(0, 200)).toMatch(/ipcMain: guardChannels\(\s*ipcMain,[\s\S]*SHORTCUT_GUARDS/);
  });
});

describe('the floating button card channel answers only the floating button', () => {
  for (const [from, event, kind] of SENDERS) {
    const allowed = kind === 'floating-button';
    it(`scout:local.bubbleCard from ${from} ${allowed ? 'reaches its handler' : 'is refused with ipc/not-floating-button and reaches nothing'}`, async () => {
      const { logger, lines } = recordingLogger();
      const handlers = new Map<string, IpcHandler>();
      const reached: unknown[] = [];
      const ipc = guardChannels(
        {
          handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
          removeHandler: (channel: string) => void handlers.delete(channel),
        } as Pick<IpcMain, 'handle' | 'removeHandler'>,
        senderCheck(SENDER_HOOKS, logger),
        QUICK_ASK_GUARDS,
      );
      ipc.handle(CHANNEL.localBubbleCard, (_event, payload: unknown) => {
        reached.push(payload);
        return { shown: true };
      });

      const outcome = await Promise.resolve(
        handlers.get(CHANNEL.localBubbleCard)!(event, { shown: true }),
      ).then(
        (answer) => ({ answer }),
        (err: unknown) => ({ refused: readIpcError(err) }),
      );

      expect(outcome).toEqual(
        allowed
          ? { answer: { shown: true } }
          : {
              refused: {
                code: 'ipc/not-floating-button',
                message: 'Only the floating button shows its card.',
              },
            },
      );
      expect(reached).toEqual(allowed ? [{ shown: true }] : []);
      expect(lines.length).toBe(allowed ? 0 : 1);
    });
  }

  it('main registers quick ask through an ipc guarded by the quick-ask guards', () => {
    const main = readFileSync(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');
    const call = main.slice(main.indexOf('quickAsk = await registerQuickAsk({'));

    expect(call.slice(0, 200)).toMatch(
      /ipcMain: guardChannels\(\s*ipcMain,[\s\S]*QUICK_ASK_GUARDS/,
    );
  });
});

describe('only the operator window brings the Scout window forward', () => {
  for (const [from, event, kind] of SENDERS) {
    const allowed = kind === 'operator';
    it(`scout:app.showMainWindow from ${from} ${allowed ? 'reaches its handler' : 'is refused with operator/not-operator-window, reaches nothing, and is logged at warn with the channel and the sender kind'}`, async () => {
      const { logger, lines } = recordingLogger();
      const handlers = new Map<string, IpcHandler>();
      let shown = 0;
      const ipc = guardChannels(
        {
          handle: (channel: string, handler: IpcHandler) => void handlers.set(channel, handler),
          removeHandler: (channel: string) => void handlers.delete(channel),
        } as Pick<IpcMain, 'handle' | 'removeHandler'>,
        senderCheck(SENDER_HOOKS, logger),
        SHOW_MAIN_GUARDS,
      );
      ipc.handle(CHANNEL.appShowMainWindow, () => {
        shown += 1;
      });

      const refused = await Promise.resolve(handlers.get(CHANNEL.appShowMainWindow)!(event)).then(
        () => null,
        (err: unknown) => readIpcError(err),
      );

      expect({ refused, shown, lines }).toEqual(
        allowed
          ? { refused: null, shown: 1, lines: [] }
          : {
              refused: {
                code: 'operator/not-operator-window',
                message: 'Only the operator window brings the Scout window forward.',
              },
              shown: 0,
              lines: [
                `warn ["ipc: call refused for its sender",{"channel":"${CHANNEL.appShowMainWindow}","sender":"${kind}"}]`,
              ],
            },
      );
    });
  }

  it('main registers the app handlers through an ipc guarded by the show-main table', () => {
    const main = readFileSync(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');
    const start = main.indexOf('unregisterApp = registerAppHandlers(');
    const call = main.slice(start, main.indexOf(');', start));

    expect(start).toBeGreaterThan(-1);
    expect(call).toMatch(
      /registerAppHandlers\(\s*guardChannels\(\s*ipcMain,\s*senderCheck\([\s\S]*SHOW_MAIN_GUARDS/,
    );
  });
});
