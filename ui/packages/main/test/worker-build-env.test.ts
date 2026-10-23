import { describe, it, expect, spyOn } from 'bun:test';
import { buildEnv, migrateLegacyApiKey } from '../src/worker/build-env.js';
import { VaultUnavailableError, type VaultKey } from '@scout/shared';

function makeVault(
  entries: Record<string, string | null>,
): (key: string) => Promise<string | null> {
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

  it('throws naming the provider when vault llm.provider is not one the worker supports', async () => {
    let thrown: unknown = null;
    try {
      await buildEnv({
        vaultGet: makeVault({ 'discord.bot-token': 'tok', 'llm.provider': 'LmStudio' }),
      });
    } catch (err) {
      thrown = err;
    }
    expect(thrown).toBeInstanceOf(Error);
    expect((thrown as Error).message).toContain('LmStudio');
  });

  it('OpenAI: the worker uses OpenAI with the stored API key and cloud model, and no Ollama variables', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'OpenAI',
        'llm.api-key': 'sk-openai-key',
        'llm.cloud-model': 'gpt-4o',
        'llm.model': 'llama3.1',
        'llm.endpoint': 'http://localhost:11434',
      }),
    });
    expect(env).toEqual({
      SCOUT__LLM__PROVIDER: 'OpenAI',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__DISCORD__BOTTOKEN: 'tok',
      SCOUT__LLM__OPENAI__APIKEY: 'sk-openai-key',
      SCOUT__LLM__OPENAI__CHAT__MODEL: 'gpt-4o',
    });
  });

  it('OpenAI: chat model defaults to gpt-4o-mini when no cloud model is stored', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'OpenAI',
        'llm.api-key': 'sk-openai-key',
      }),
    });
    expect(env['SCOUT__LLM__OPENAI__CHAT__MODEL']).toBe('gpt-4o-mini');
  });

  it('Gemini: the worker uses Gemini with the stored API key, cloud model and an explicit embeddings model, and no Ollama variables', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'Gemini',
        'llm.api-key': 'gemini-key',
        'llm.cloud-model': 'gemini-2.5-pro',
        'llm.model': 'llama3.1',
        'llm.endpoint': 'http://localhost:11434',
      }),
    });
    expect(env).toEqual({
      SCOUT__LLM__PROVIDER: 'Gemini',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__DISCORD__BOTTOKEN: 'tok',
      SCOUT__LLM__GEMINI__APIKEY: 'gemini-key',
      SCOUT__LLM__GEMINI__CHAT__MODEL: 'gemini-2.5-pro',
      SCOUT__LLM__GEMINI__EMBEDDINGS__MODEL: 'text-embedding-004',
    });
  });

  it('Gemini: chat model defaults to gemini-2.5-flash when no cloud model is stored', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'Gemini',
        'llm.api-key': 'gemini-key',
      }),
    });
    expect(env['SCOUT__LLM__GEMINI__CHAT__MODEL']).toBe('gemini-2.5-flash');
  });

  for (const provider of ['OpenAI', 'Gemini']) {
    it(`${provider} without a stored API key: error names the provider, points to AI Sources, and contains no secret`, async () => {
      const SECRET = 'bot-token-must-not-leak';
      let thrown: unknown = null;
      try {
        await buildEnv({
          vaultGet: makeVault({
            'discord.bot-token': SECRET,
            'llm.provider': provider,
            'llm.cloud-model': 'some-model',
          }),
        });
      } catch (err) {
        thrown = err;
      }
      expect(thrown).toBeInstanceOf(VaultUnavailableError);
      const message = (thrown as VaultUnavailableError).message;
      expect(message).toContain(provider);
      expect(message).toContain('AI Sources');
      expect(message).not.toContain(SECRET);
    });
  }

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

  it('local default unchanged: no provider gives the same Ollama env as before, with no APIKEY even when a key is stored', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.model': 'llama3.1',
        'llm.endpoint': 'http://localhost:11434',
        'llm.api-key': 'sk-stored-but-unused',
        'llm.cloud-model': 'gpt-4o',
      }),
    });
    expect(env).toEqual({
      SCOUT__LLM__PROVIDER: 'Ollama',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__DISCORD__BOTTOKEN: 'tok',
      SCOUT__LLM__OLLAMA__CHAT__MODEL: 'llama3.1',
      SCOUT__LLM__OLLAMA__ENDPOINT: 'http://localhost:11434',
    });
  });

  it('local default unchanged: provider Ollama gives the same Ollama env as before, with no APIKEY even when a key is stored', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'Ollama',
        'llm.model': 'qwen2.5',
        'llm.endpoint': 'http://10.0.0.5:11434',
        'llm.api-key': 'sk-stored-but-unused',
        'llm.cloud-model': 'gpt-4o',
      }),
    });
    expect(env).toEqual({
      SCOUT__LLM__PROVIDER: 'Ollama',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__DISCORD__BOTTOKEN: 'tok',
      SCOUT__LLM__OLLAMA__CHAT__MODEL: 'qwen2.5',
      SCOUT__LLM__OLLAMA__ENDPOINT: 'http://10.0.0.5:11434',
    });
  });

  it('read-only guard: a stored TypeSafe key reaches the worker on Ollama', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.model': 'llama3.1',
        'guard.typesafe-api-key': 'ts-key',
      }),
    });
    expect(env).toEqual({
      SCOUT__LLM__PROVIDER: 'Ollama',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__DISCORD__BOTTOKEN: 'tok',
      SCOUT__GUARD__TYPESAFE__APIKEY: 'ts-key',
      SCOUT__LLM__OLLAMA__CHAT__MODEL: 'llama3.1',
    });
  });

  it('read-only guard: a stored TypeSafe key reaches the worker on OpenAI', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'OpenAI',
        'llm.api-key': 'sk-openai-key',
        'guard.typesafe-api-key': 'ts-key',
      }),
    });
    expect(env['SCOUT__GUARD__TYPESAFE__APIKEY']).toBe('ts-key');
    expect(env['SCOUT__LLM__OPENAI__APIKEY']).toBe('sk-openai-key');
  });

  it('read-only guard: with no TypeSafe key stored the worker gets no guard variable', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'Gemini',
        'llm.api-key': 'g',
      }),
    });
    expect('SCOUT__GUARD__TYPESAFE__APIKEY' in env).toBe(false);
  });

  it('read-only guard: the TypeSafe key is never written to the console', async () => {
    const SECRET = 'ts-key-must-not-be-logged';
    const spies = (['log', 'info', 'warn', 'error', 'debug'] as const).map((level) =>
      spyOn(console, level).mockImplementation(() => undefined),
    );
    try {
      await buildEnv({
        vaultGet: makeVault({ 'discord.bot-token': 'tok', 'guard.typesafe-api-key': SECRET }),
      });
      const logged = JSON.stringify(spies.flatMap((spy) => spy.mock.calls));
      expect(logged).not.toContain(SECRET);
    } finally {
      for (const spy of spies) spy.mockRestore();
    }
  });

  it('ChatGptPlan: the worker gets the token file path and the stored cloud model, and no API key or Ollama variables', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'ChatGptPlan',
        'llm.cloud-model': 'gpt-6.1-sol',
        'llm.api-key': 'sk-stored-but-unused',
        'llm.model': 'llama3.1',
      }),
      chatGptTokenFile: '/home/owner/.config/Scout/chatgpt-token.json',
    });
    expect(env).toEqual({
      SCOUT__LLM__PROVIDER: 'ChatGptPlan',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__DISCORD__BOTTOKEN: 'tok',
      SCOUT__LLM__CHATGPTPLAN__TOKENFILE: '/home/owner/.config/Scout/chatgpt-token.json',
      SCOUT__LLM__CHATGPTPLAN__CHAT__MODEL: 'gpt-6.1-sol',
    });
  });

  it('ChatGptPlan: chat model defaults to gpt-5.6-luna when no cloud model is stored', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok', 'llm.provider': 'ChatGptPlan' }),
      chatGptTokenFile: '/tmp/chatgpt-token.json',
    });
    expect(env['SCOUT__LLM__CHATGPTPLAN__CHAT__MODEL']).toBe('gpt-5.6-luna');
  });

  it('None: the worker starts with no AI provider and no provider variables instead of falling back to Ollama', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'None',
        'llm.model': 'llama3.1',
        'llm.endpoint': 'http://localhost:11434',
        'llm.cloud-model': 'gpt-5.6-luna',
      }),
      chatGptTokenFile: '/tmp/chatgpt-token.json',
    });
    expect(env).toEqual({
      SCOUT__LLM__PROVIDER: 'None',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__DISCORD__BOTTOKEN: 'tok',
    });
  });

  it('token value does not appear in keys or serialised env snapshot', async () => {
    const SECRET = 'super-secret-do-not-log';
    const env = await buildEnv({ vaultGet: makeVault({ 'discord.bot-token': SECRET }) });
    const serialised = JSON.stringify(Object.keys(env));
    expect(serialised).not.toContain(SECRET);
  });

  it('passes the GitHub credential variables to the worker when GitHub is connected', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      gitHubEnv: { SCOUT__PLUGINS__GITHUB__TOKENFILE: '/user-data/github-token.json' },
    });
    expect(env['SCOUT__PLUGINS__GITHUB__TOKENFILE']).toBe('/user-data/github-token.json');
  });

  it('leaves the GitHub variables out when GitHub is not connected', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      gitHubEnv: {},
    });
    expect(Object.keys(env).filter((key) => key.startsWith('SCOUT__PLUGINS__GITHUB'))).toEqual([]);
  });

  it('one key per provider: OpenAI reads its own key, not the Gemini one', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'OpenAI',
        'llm.openai.api-key': 'sk-openai-own',
        'llm.gemini.api-key': 'gemini-own',
      }),
    });
    expect(env['SCOUT__LLM__OPENAI__APIKEY']).toBe('sk-openai-own');
    expect(JSON.stringify(env)).not.toContain('gemini-own');
  });

  it('one key per provider: Gemini reads its own key, not the OpenAI one', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'Gemini',
        'llm.openai.api-key': 'sk-openai-own',
        'llm.gemini.api-key': 'gemini-own',
      }),
    });
    expect(env['SCOUT__LLM__GEMINI__APIKEY']).toBe('gemini-own');
    expect(JSON.stringify(env)).not.toContain('sk-openai-own');
  });

  it('one key per provider: OpenAI active with only a Gemini key stored fails naming OpenAI', async () => {
    let thrown: unknown = null;
    try {
      await buildEnv({
        vaultGet: makeVault({
          'discord.bot-token': 'tok',
          'llm.provider': 'OpenAI',
          'llm.gemini.api-key': 'gemini-own',
        }),
      });
    } catch (err) {
      thrown = err;
    }
    expect(thrown).toBeInstanceOf(VaultUnavailableError);
    expect((thrown as Error).message).toContain('OpenAI');
    expect((thrown as Error).message).not.toContain('gemini-own');
  });

  it('a provider own key wins over the legacy shared key', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'OpenAI',
        'llm.openai.api-key': 'sk-openai-own',
        'llm.api-key': 'sk-legacy',
      }),
    });
    expect(env['SCOUT__LLM__OPENAI__APIKEY']).toBe('sk-openai-own');
  });

  it('provider None: the worker gets SCOUT__LLM__PROVIDER=None and no provider variables, even with keys and models stored', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.provider': 'None',
        'llm.model': 'llama3.1',
        'llm.endpoint': 'http://localhost:11434',
        'llm.openai.api-key': 'sk-openai-own',
        'llm.cloud-model': 'gpt-4o',
      }),
    });
    expect(env).toEqual({
      SCOUT__LLM__PROVIDER: 'None',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__DISCORD__BOTTOKEN: 'tok',
    });
  });

  it('no provider and both provider keys stored still gives the plain Ollama env', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'llm.openai.api-key': 'sk-openai-own',
        'llm.gemini.api-key': 'gemini-own',
      }),
    });
    expect(env).toEqual({
      SCOUT__LLM__PROVIDER: 'Ollama',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__DISCORD__BOTTOKEN: 'tok',
    });
  });
});

function makeVaultStore(initial: Partial<Record<VaultKey, string>>) {
  const entries = new Map<VaultKey, string>(Object.entries(initial) as [VaultKey, string][]);
  const writes: string[] = [];
  return {
    entries,
    writes,
    vault: {
      get: async (key: VaultKey) => entries.get(key) ?? null,
      store: async (key: VaultKey, value: string) => {
        writes.push(`store ${key}`);
        entries.set(key, value);
      },
      delete: async (key: VaultKey) => {
        writes.push(`delete ${key}`);
        return entries.delete(key);
      },
    },
  };
}

describe('migrateLegacyApiKey', () => {
  for (const [provider, ownKey] of [
    ['OpenAI', 'llm.openai.api-key'],
    ['Gemini', 'llm.gemini.api-key'],
  ] as const) {
    it(`existing users keep their key: the legacy key moves to the ${provider} slot and the legacy entry is deleted`, async () => {
      const { entries, vault } = makeVaultStore({
        'llm.provider': provider,
        'llm.api-key': 'legacy-key',
      });

      await migrateLegacyApiKey(vault);

      expect(entries.get(ownKey)).toBe('legacy-key');
      expect(entries.has('llm.api-key')).toBe(false);
    });
  }

  it('is idempotent: a second run writes nothing', async () => {
    const { entries, writes, vault } = makeVaultStore({
      'llm.provider': 'OpenAI',
      'llm.api-key': 'legacy-key',
    });
    await migrateLegacyApiKey(vault);
    writes.length = 0;

    await migrateLegacyApiKey(vault);

    expect(writes).toEqual([]);
    expect(entries.get('llm.openai.api-key')).toBe('legacy-key');
  });

  it('a provider that already has its own key keeps it, and the stale legacy key is deleted', async () => {
    const { entries, vault } = makeVaultStore({
      'llm.provider': 'OpenAI',
      'llm.openai.api-key': 'own-key',
      'llm.api-key': 'legacy-key',
    });

    await migrateLegacyApiKey(vault);

    expect(entries.get('llm.openai.api-key')).toBe('own-key');
    expect(entries.has('llm.api-key')).toBe(false);
  });

  it('a failed copy rejects and never deletes the legacy key, so the only stored key survives', async () => {
    const { entries, writes, vault } = makeVaultStore({
      'llm.provider': 'Gemini',
      'llm.api-key': 'legacy-key',
    });
    const failingVault = {
      ...vault,
      store: async () => {
        throw new Error('keyring locked');
      },
    };

    await expect(migrateLegacyApiKey(failingVault)).rejects.toThrow('keyring locked');

    expect(writes).not.toContain('delete llm.api-key');
    expect(entries.get('llm.api-key')).toBe('legacy-key');
  });

  for (const provider of ['Ollama', null]) {
    it(`is a no-op when the provider is ${String(provider)}`, async () => {
      const { entries, writes, vault } = makeVaultStore({
        ...(provider === null ? {} : { 'llm.provider': provider }),
        'llm.api-key': 'legacy-key',
      });

      await migrateLegacyApiKey(vault);

      expect(writes).toEqual([]);
      expect(entries.get('llm.api-key')).toBe('legacy-key');
    });
  }
});
