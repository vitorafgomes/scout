import { describe, it, expect } from 'bun:test';
import { buildEnv } from '../src/worker/build-env.js';
import { VaultUnavailableError } from '@scout/shared';

function makeVault(entries: Record<string, string | null>): (key: string) => Promise<string | null> {
  return async (key) => entries[key] ?? null;
}

describe('buildEnv', () => {
  it('throws VaultUnavailableError when discord.bot-token is absent', async () => {
    let thrown: unknown = null;
    try {
      await buildEnv({ vaultGet: makeVault({}) });
    } catch (err) {
      thrown = err;
    }
    expect(thrown).toBeInstanceOf(VaultUnavailableError);
    expect((thrown as VaultUnavailableError).code).toBe('vault/unavailable');
  });

  it('emits SCOUT__ADAPTERS__DISCORD__BOTTOKEN from vault discord.bot-token', async () => {
    const env = await buildEnv({ vaultGet: makeVault({ 'discord.bot-token': 'tok123' }) });
    expect(env['SCOUT__ADAPTERS__DISCORD__BOTTOKEN']).toBe('tok123');
  });

  it('does not emit SCOUT__CHATADAPTER', async () => {
    const env = await buildEnv({ vaultGet: makeVault({ 'discord.bot-token': 'tok' }) });
    expect('SCOUT__CHATADAPTER' in env).toBe(false);
  });

  it('defaults LLM provider to Ollama when vault key absent', async () => {
    const env = await buildEnv({ vaultGet: makeVault({ 'discord.bot-token': 'tok' }) });
    expect(env['SCOUT__LLM__PROVIDER']).toBe('Ollama');
  });

  it('uses vault llm.provider when present', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok', 'llm.provider': 'LmStudio' }),
    });
    expect(env['SCOUT__LLM__PROVIDER']).toBe('LmStudio');
  });

  it('emits SCOUT__LLM__OLLAMA__CHAT__MODEL (not __OLLAMA__MODEL) when llm.model set', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok', 'llm.model': 'llama3' }),
    });
    expect(env['SCOUT__LLM__OLLAMA__CHAT__MODEL']).toBe('llama3');
    expect('SCOUT__LLM__OLLAMA__MODEL' in env).toBe(false);
  });

  it('omits SCOUT__LLM__OLLAMA__CHAT__MODEL when llm.model absent', async () => {
    const env = await buildEnv({ vaultGet: makeVault({ 'discord.bot-token': 'tok' }) });
    expect('SCOUT__LLM__OLLAMA__CHAT__MODEL' in env).toBe(false);
  });

  it('emits SCOUT__LLM__OLLAMA__ENDPOINT when llm.endpoint present', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok', 'llm.endpoint': 'http://localhost:11434' }),
    });
    expect(env['SCOUT__LLM__OLLAMA__ENDPOINT']).toBe('http://localhost:11434');
  });

  it('emits DOTNET_ENVIRONMENT=Production', async () => {
    const env = await buildEnv({ vaultGet: makeVault({ 'discord.bot-token': 'tok' }) });
    expect(env['DOTNET_ENVIRONMENT']).toBe('Production');
  });

  it('token value does not appear in keys or serialised env snapshot', async () => {
    const SECRET = 'super-secret-do-not-log';
    const env = await buildEnv({ vaultGet: makeVault({ 'discord.bot-token': SECRET }) });
    const serialised = JSON.stringify(Object.keys(env));
    expect(serialised).not.toContain(SECRET);
  });
});
