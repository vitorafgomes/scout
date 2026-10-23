import { describe, it, expect, beforeEach } from 'bun:test';
import { registerVaultHandlers } from '../src/ipc/vault.js';
import { createVault } from '../src/vault/safe-storage-vault.js';
import type { VaultDeps } from '../src/vault/safe-storage-vault.js';
import { CHANNEL } from '@scout/shared';
import type { Logger } from '../src/util/logger.js';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

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

interface CaptureLogger extends Logger {
  lines: Array<{ level: string; msg: string; ctx?: Record<string, unknown> }>;
}

function pushLine(
  lines: Array<{ level: string; msg: string; ctx?: Record<string, unknown> }>,
  level: string,
  msg: string,
  ctx: Record<string, unknown> | undefined,
): void {
  if (ctx !== undefined) {
    lines.push({ level, msg, ctx });
  } else {
    lines.push({ level, msg });
  }
}

function makeCaptureLogger(): CaptureLogger {
  const lines: Array<{ level: string; msg: string; ctx?: Record<string, unknown> }> = [];
  const logger: CaptureLogger = {
    lines,
    debug(msg, ctx) {
      pushLine(lines, 'debug', msg, ctx);
    },
    info(msg, ctx) {
      pushLine(lines, 'info', msg, ctx);
    },
    warn(msg, ctx) {
      pushLine(lines, 'warn', msg, ctx);
    },
    error(msg, _err, ctx) {
      pushLine(lines, 'error', msg, ctx);
    },
    child() {
      return makeCaptureLogger();
    },
  };
  return logger;
}

function makeVaultDeps(available = true): VaultDeps {
  const files = new Map<string, string>();
  return {
    safeStorage: {
      isEncryptionAvailable: () => available,
      encryptString: (s: string) => Buffer.from('ENC:' + s, 'utf8'),
      decryptString: (b: Buffer) => b.toString('utf8').replace(/^ENC:/, ''),
    },
    readFile: async (p: string) => files.get(p) ?? null,
    writeFile: async (p: string, c: string) => {
      files.set(p, c);
    },
    rename: async (from: string, to: string) => {
      const c = files.get(from);
      if (c !== undefined) {
        files.set(to, c);
        files.delete(from);
      }
    },
    vaultPath: '/test/vault.enc',
    logger: makeCaptureLogger(),
  };
}

const NULL_EVENT = null as unknown as IpcMainInvokeEvent;

describe('registerVaultHandlers', () => {
  let ipcMain: FakeIpcMain;
  let captureLogger: CaptureLogger;

  beforeEach(() => {
    ipcMain = makeFakeIpcMain();
    captureLogger = makeCaptureLogger();
  });

  describe('case 1: vaultStore with known key + non-empty value succeeds', () => {
    it('resolves without error', async () => {
      const vault = createVault(makeVaultDeps());
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const handler = ipcMain.handlers.get(CHANNEL.vaultStore)!;
      await expect(
        handler(NULL_EVENT, { key: 'discord.bot-token', value: 'token123' }),
      ).resolves.toBeUndefined();
    });
  });

  describe('case 2: vaultStore with unknown key rejects with validation error', () => {
    it('throws with code vault/invalid-key', async () => {
      const vault = createVault(makeVaultDeps());
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const handler = ipcMain.handlers.get(CHANNEL.vaultStore)!;
      let caught: Error | undefined;
      try {
        await handler(NULL_EVENT, { key: 'unknown.key', value: 'val' });
      } catch (e: unknown) {
        caught = e as Error;
      }
      expect(caught).toBeDefined();
      expect((caught as Error & { code?: string }).code).toBe('vault/invalid-key');
    });

    it('error message references the unknown key', async () => {
      const vault = createVault(makeVaultDeps());
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const handler = ipcMain.handlers.get(CHANNEL.vaultStore)!;
      let caught: Error | undefined;
      try {
        await handler(NULL_EVENT, { key: 'some.bad.key', value: 'v' });
      } catch (e: unknown) {
        caught = e as Error;
      }
      expect(caught?.message).toContain('some.bad.key');
    });
  });

  describe('case 3: vaultStore with empty value rejects', () => {
    it('throws with code vault/invalid-value', async () => {
      const vault = createVault(makeVaultDeps());
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const handler = ipcMain.handlers.get(CHANNEL.vaultStore)!;
      let caught: Error | undefined;
      try {
        await handler(NULL_EVENT, { key: 'discord.bot-token', value: '' });
      } catch (e: unknown) {
        caught = e as Error;
      }
      expect(caught).toBeDefined();
      expect((caught as Error & { code?: string }).code).toBe('vault/invalid-value');
    });
  });

  describe('case 4: vaultHas, vaultDelete, vaultIsAvailable pass through', () => {
    it('vaultHas returns { exists: false } for absent key', async () => {
      const vault = createVault(makeVaultDeps());
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const handler = ipcMain.handlers.get(CHANNEL.vaultHas)!;
      const result = await handler(NULL_EVENT, { key: 'discord.bot-token' });
      expect(result).toEqual({ exists: false });
    });

    it('vaultHas returns { exists: true } after store', async () => {
      const vault = createVault(makeVaultDeps());
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const storeHandler = ipcMain.handlers.get(CHANNEL.vaultStore)!;
      await storeHandler(NULL_EVENT, { key: 'discord.bot-token', value: 'x' });

      const hasHandler = ipcMain.handlers.get(CHANNEL.vaultHas)!;
      const result = await hasHandler(NULL_EVENT, { key: 'discord.bot-token' });
      expect(result).toEqual({ exists: true });
    });

    it('vaultDelete returns { deleted: false } for absent key', async () => {
      const vault = createVault(makeVaultDeps());
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const handler = ipcMain.handlers.get(CHANNEL.vaultDelete)!;
      const result = await handler(NULL_EVENT, { key: 'discord.bot-token' });
      expect(result).toEqual({ deleted: false });
    });

    it('vaultDelete returns { deleted: true } after store', async () => {
      const vault = createVault(makeVaultDeps());
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const storeHandler = ipcMain.handlers.get(CHANNEL.vaultStore)!;
      await storeHandler(NULL_EVENT, { key: 'discord.client-id', value: 'cid' });

      const deleteHandler = ipcMain.handlers.get(CHANNEL.vaultDelete)!;
      const result = await deleteHandler(NULL_EVENT, { key: 'discord.client-id' });
      expect(result).toEqual({ deleted: true });
    });

    it('vaultIsAvailable returns { available: true } when safeStorage available', async () => {
      const vault = createVault(makeVaultDeps(true));
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const handler = ipcMain.handlers.get(CHANNEL.vaultIsAvailable)!;
      const result = await handler(NULL_EVENT);
      expect(result).toEqual({ available: true });
    });

    it('vaultIsAvailable returns { available: false, reason: "no-keyring" } when unavailable', async () => {
      const vault = createVault(makeVaultDeps(false));
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const handler = ipcMain.handlers.get(CHANNEL.vaultIsAvailable)!;
      const result = await handler(NULL_EVENT);
      expect(result).toEqual({ available: false, reason: 'no-keyring' });
    });
  });

  describe('case 5: logs key but never value', () => {
    it('info log contains key for store', async () => {
      const vault = createVault(makeVaultDeps());
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const handler = ipcMain.handlers.get(CHANNEL.vaultStore)!;
      await handler(NULL_EVENT, { key: 'discord.bot-token', value: 'super-secret' });

      const allCtx = captureLogger.lines.flatMap((l) => JSON.stringify(l.ctx ?? {}));
      const allMsgs = captureLogger.lines.map((l) => l.msg + JSON.stringify(l.ctx ?? {}));

      expect(allMsgs.some((m) => m.includes('discord.bot-token'))).toBe(true);
      expect(allCtx.some((c) => c.includes('super-secret'))).toBe(false);
    });

    it('value never appears in any log line for store', async () => {
      const vault = createVault(makeVaultDeps());
      registerVaultHandlers(ipcMain, vault, captureLogger);

      const handler = ipcMain.handlers.get(CHANNEL.vaultStore)!;
      await handler(NULL_EVENT, { key: 'discord.client-id', value: 'DO-NOT-LOG-THIS' });

      const allText = captureLogger.lines.map((l) => JSON.stringify(l)).join('\n');
      expect(allText).not.toContain('DO-NOT-LOG-THIS');
    });
  });

  describe('case 6: cleanup removes all four channels', () => {
    it('unregister removes vaultStore, vaultHas, vaultDelete, vaultIsAvailable', () => {
      const vault = createVault(makeVaultDeps());
      const unregister = registerVaultHandlers(ipcMain, vault, captureLogger);

      expect(ipcMain.handlers.has(CHANNEL.vaultStore)).toBe(true);
      expect(ipcMain.handlers.has(CHANNEL.vaultHas)).toBe(true);
      expect(ipcMain.handlers.has(CHANNEL.vaultDelete)).toBe(true);
      expect(ipcMain.handlers.has(CHANNEL.vaultIsAvailable)).toBe(true);

      unregister();

      expect(ipcMain.handlers.has(CHANNEL.vaultStore)).toBe(false);
      expect(ipcMain.handlers.has(CHANNEL.vaultHas)).toBe(false);
      expect(ipcMain.handlers.has(CHANNEL.vaultDelete)).toBe(false);
      expect(ipcMain.handlers.has(CHANNEL.vaultIsAvailable)).toBe(false);
      expect(ipcMain.handlers.has(CHANNEL.vaultGetPublic)).toBe(false);
    });
  });

  describe('vaultGetPublic: only non-secret keys can be read back', () => {
    function spyVault() {
      const vault = createVault(makeVaultDeps());
      const getCalls: string[] = [];
      const realGet = vault.get.bind(vault);
      vault.get = async (key) => {
        getCalls.push(key);
        return realGet(key);
      };
      return { vault, getCalls };
    }

    it('returns the stored llm.provider and llm.cloud-model, and null when absent', async () => {
      const { vault } = spyVault();
      await vault.store('llm.provider', 'OpenAI');
      registerVaultHandlers(ipcMain, vault, captureLogger);
      const handler = ipcMain.handlers.get(CHANNEL.vaultGetPublic)!;

      expect(await handler(NULL_EVENT, { key: 'llm.provider' })).toEqual({ value: 'OpenAI' });
      expect(await handler(NULL_EVENT, { key: 'llm.cloud-model' })).toEqual({ value: null });
    });

    for (const key of ['llm.openai.api-key', 'guard.typesafe-api-key', 'discord.bot-token']) {
      it(`rejects ${key} with vault/invalid-key and never reads the vault`, async () => {
        const { vault, getCalls } = spyVault();
        await vault.store('llm.openai.api-key', 'sk-secret');
        await vault.store('discord.bot-token', 'bot-secret');
        registerVaultHandlers(ipcMain, vault, captureLogger);
        const handler = ipcMain.handlers.get(CHANNEL.vaultGetPublic)!;

        let caught: (Error & { code?: string }) | undefined;
        try {
          await handler(NULL_EVENT, { key });
        } catch (e: unknown) {
          caught = e as Error & { code?: string };
        }

        expect(caught?.code).toBe('vault/invalid-key');
        expect(caught?.message).not.toContain('secret');
        expect(getCalls).toEqual([]);
      });
    }
  });
});
