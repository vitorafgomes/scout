import { describe, it, expect } from 'bun:test';
import { createVault } from '../src/vault/safe-storage-vault.js';
import type { VaultDeps } from '../src/vault/safe-storage-vault.js';
import { VaultUnavailableError } from '@scout/shared';
import type { Logger } from '../src/util/logger.js';

function makeNullLogger(): Logger {
  return {
    debug() {},
    info() {},
    warn() {},
    error() {},
    child() {
      return makeNullLogger();
    },
  };
}

interface FakeFs {
  files: Map<string, string>;
  renames: Array<{ from: string; to: string }>;
  writeCallCount: number;
}

function makeFakeFs(initial?: string): FakeFs {
  const files: Map<string, string> = new Map();
  if (initial !== undefined) {
    files.set('/data/vault.enc', initial);
  }
  return { files, renames: [], writeCallCount: 0 };
}

function makeDeps(
  opts: {
    available?: boolean;
    fs?: FakeFs;
    warnLines?: string[];
  } = {},
): { deps: VaultDeps; fake: FakeFs; warnLines: string[]; encryptCalls: string[] } {
  const available = opts.available ?? true;
  const fake = opts.fs ?? makeFakeFs();
  const warnLines: string[] = opts.warnLines ?? [];
  const encryptCalls: string[] = [];

  const deps: VaultDeps = {
    safeStorage: {
      isEncryptionAvailable: () => available,
      encryptString: (plaintext: string) => {
        encryptCalls.push(plaintext);
        return Buffer.from('ENC:' + plaintext, 'utf8');
      },
      decryptString: (buf: Buffer) => {
        const s = buf.toString('utf8');
        return s.startsWith('ENC:') ? s.slice(4) : s;
      },
    },
    readFile: async (path: string) => {
      return fake.files.get(path) ?? null;
    },
    writeFile: async (path: string, content: string) => {
      fake.writeCallCount++;
      fake.files.set(path, content);
    },
    rename: async (from: string, to: string) => {
      fake.renames.push({ from, to });
      const content = fake.files.get(from);
      if (content !== undefined) {
        fake.files.set(to, content);
        fake.files.delete(from);
      }
    },
    vaultPath: '/data/vault.enc',
    logger: {
      ...makeNullLogger(),
      warn(msg: string) {
        warnLines.push(msg);
      },
    },
  };

  return { deps, fake, warnLines, encryptCalls };
}

describe('createVault', () => {
  describe('case 1: store writes correct on-disk shape via tmp+rename', () => {
    it('writes {v:1, entries} to tmp then renames', async () => {
      const { deps, fake, encryptCalls } = makeDeps();
      const vault = createVault(deps);

      await vault.store('discord.bot-token', 'abc');

      expect(encryptCalls).toEqual(['abc']);

      const expectedB64 = Buffer.from('ENC:abc', 'utf8').toString('base64');
      const diskContent = fake.files.get('/data/vault.enc');
      expect(diskContent).toBeDefined();
      const parsed = JSON.parse(diskContent!) as { v: number; entries: Record<string, string> };
      expect(parsed.v).toBe(1);
      expect(parsed.entries['discord.bot-token']).toBe(expectedB64);

      expect(fake.renames).toHaveLength(1);
      expect(fake.renames[0]).toEqual({ from: '/data/vault.enc.tmp', to: '/data/vault.enc' });
    });
  });

  describe('case 2: store then has / store then delete', () => {
    it('has returns true after store', async () => {
      const { deps } = makeDeps();
      const vault = createVault(deps);

      await vault.store('discord.bot-token', 'secret');
      expect(await vault.has('discord.bot-token')).toBe(true);
    });

    it('delete returns true after store; has returns false after delete', async () => {
      const { deps } = makeDeps();
      const vault = createVault(deps);

      await vault.store('discord.bot-token', 'secret');
      const deleted = await vault.delete('discord.bot-token');
      expect(deleted).toBe(true);
      expect(await vault.has('discord.bot-token')).toBe(false);
    });
  });

  describe('case 3: unavailable vault rejects store without writing', () => {
    it('store rejects with VaultUnavailableError', async () => {
      const { deps, fake } = makeDeps({ available: false });
      fake.writeCallCount = 0;
      const vault = createVault(deps);

      await expect(vault.store('discord.bot-token', 'abc')).rejects.toBeInstanceOf(
        VaultUnavailableError,
      );
    });

    it('store rejects with code vault/unavailable', async () => {
      const { deps } = makeDeps({ available: false });
      const vault = createVault(deps);

      await expect(vault.store('discord.bot-token', 'abc')).rejects.toMatchObject({
        code: 'vault/unavailable',
      });
    });

    it('writeFile is never called when unavailable', async () => {
      const { deps, fake } = makeDeps({ available: false });
      const vault = createVault(deps);

      try {
        await vault.store('discord.bot-token', 'abc');
      } catch {
        // expected
      }
      expect(fake.writeCallCount).toBe(0);
    });
  });

  describe('case 4: availability()', () => {
    it('returns {available:false, reason:"no-keyring"} when unavailable', () => {
      const { deps } = makeDeps({ available: false });
      const vault = createVault(deps);
      expect(vault.availability()).toEqual({ available: false, reason: 'no-keyring' });
    });

    it('returns {available:true} (no reason) when available', () => {
      const { deps } = makeDeps({ available: true });
      const vault = createVault(deps);
      expect(vault.availability()).toEqual({ available: true });
    });

    it('availability().reason is undefined when available', () => {
      const { deps } = makeDeps({ available: true });
      const vault = createVault(deps);
      expect(vault.availability().reason).toBeUndefined();
    });
  });

  describe('case 5: corrupt on-disk JSON treated as empty', () => {
    it('logs a warning and treats file as empty', async () => {
      const fs = makeFakeFs('NOT_VALID_JSON{{{');
      const { deps, warnLines } = makeDeps({ fs });
      const vault = createVault(deps);

      expect(await vault.has('discord.bot-token')).toBe(false);
      expect(
        warnLines.some((l) => l.includes('invalid JSON') || l.includes('unexpected shape')),
      ).toBe(true);
    });

    it('subsequent store writes a fresh valid file', async () => {
      const fs = makeFakeFs('CORRUPT');
      const { deps, fake } = makeDeps({ fs });
      const vault = createVault(deps);

      await vault.store('discord.bot-token', 'hello');

      const disk = fake.files.get('/data/vault.enc');
      expect(disk).toBeDefined();
      const parsed = JSON.parse(disk!) as { v: number; entries: Record<string, string> };
      expect(parsed.v).toBe(1);
      expect(typeof parsed.entries['discord.bot-token']).toBe('string');
    });
  });

  describe('case 6: absent file on first call', () => {
    it('operates on empty state when file does not exist', async () => {
      const { deps } = makeDeps();
      const vault = createVault(deps);

      expect(await vault.has('discord.bot-token')).toBe(false);
      expect(await vault.delete('discord.bot-token')).toBe(false);
    });
  });

  describe('case 7: concurrent store calls serialise correctly', () => {
    it('both parallel stores persist; final file contains both keys', async () => {
      const { deps, fake } = makeDeps();
      const vault = createVault(deps);

      await Promise.all([
        vault.store('discord.bot-token', 'token-value'),
        vault.store('discord.client-id', 'client-value'),
      ]);

      const disk = fake.files.get('/data/vault.enc');
      expect(disk).toBeDefined();
      const parsed = JSON.parse(disk!) as { v: number; entries: Record<string, string> };
      expect(Object.keys(parsed.entries)).toHaveLength(2);
      expect(typeof parsed.entries['discord.bot-token']).toBe('string');
      expect(typeof parsed.entries['discord.client-id']).toBe('string');
    });
  });

  describe('case 8: ciphertext is base64', () => {
    it('stored entry is valid base64 string', async () => {
      const { deps, fake } = makeDeps();
      const vault = createVault(deps);

      await vault.store('discord.bot-token', 'my-secret');

      const disk = fake.files.get('/data/vault.enc');
      const parsed = JSON.parse(disk!) as { v: number; entries: Record<string, string> };
      const entry = parsed.entries['discord.bot-token']!;

      // Must be valid base64 (no raw bytes that break JSON)
      expect(() => Buffer.from(entry, 'base64')).not.toThrow();
      const reEncoded = Buffer.from(entry, 'base64').toString('base64');
      expect(reEncoded).toBe(entry);
    });
  });

  describe('case 9: no cross-key leakage', () => {
    it('has returns false for a different key after storing one key', async () => {
      const { deps } = makeDeps();
      const vault = createVault(deps);

      await vault.store('discord.bot-token', 'secret');
      expect(await vault.has('discord.client-id')).toBe(false);
    });
  });

  describe('case 10: delete on absent key', () => {
    it('returns false without writing', async () => {
      const { deps, fake } = makeDeps();
      const vault = createVault(deps);
      const initialWriteCount = fake.writeCallCount;

      const result = await vault.delete('discord.bot-token');
      expect(result).toBe(false);
      expect(fake.writeCallCount).toBe(initialWriteCount);
    });
  });
});
