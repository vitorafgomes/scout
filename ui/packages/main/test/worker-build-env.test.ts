import { describe, it, expect } from 'bun:test';
import { buildEnv } from '../src/worker/build-env.js';
import { VaultUnavailableError } from '@scout/shared';

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

  it('emits SCOUT_CONTROLPLANE_TOKEN when controlPlaneToken provided', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      controlPlaneToken: 'cp-token-abc',
    });
    expect(env['SCOUT_CONTROLPLANE_TOKEN']).toBe('cp-token-abc');
  });

  it('omits SCOUT_CONTROLPLANE_TOKEN when controlPlaneToken absent', async () => {
    const env = await buildEnv({ vaultGet: makeVault({ 'discord.bot-token': 'tok' }) });
    expect('SCOUT_CONTROLPLANE_TOKEN' in env).toBe(false);
  });

  it('applies settings.timezone to all three plugin env vars when present', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({
        'discord.bot-token': 'tok',
        'settings.timezone': 'America/Sao_Paulo',
      }),
    });
    expect(env['SCOUT__PLUGINS__DATES__DEFAULTTIMEZONE']).toBe('America/Sao_Paulo');
    expect(env['SCOUT__PLUGINS__WEATHER__TIMEZONE']).toBe('America/Sao_Paulo');
    expect(env['SCOUT__PLUGINS__HOLIDAYS__TIMEZONE']).toBe('America/Sao_Paulo');
  });

  it('omits all three plugin timezone env vars when settings.timezone is null', async () => {
    const env = await buildEnv({ vaultGet: makeVault({ 'discord.bot-token': 'tok' }) });
    expect('SCOUT__PLUGINS__DATES__DEFAULTTIMEZONE' in env).toBe(false);
    expect('SCOUT__PLUGINS__WEATHER__TIMEZONE' in env).toBe(false);
    expect('SCOUT__PLUGINS__HOLIDAYS__TIMEZONE' in env).toBe(false);
  });

  describe('cloud providers (catalog-driven)', () => {
    it('no active-id and no legacy provider still defaults to Ollama, byte-identical to before', async () => {
      const env = await buildEnv({ vaultGet: makeVault({ 'discord.bot-token': 'tok' }) });
      expect(env['SCOUT__LLM__PROVIDER']).toBe('Ollama');
      expect('SCOUT__LLM__OLLAMA__CHAT__MODEL' in env).toBe(false);
      expect('SCOUT__LLM__OLLAMA__ENDPOINT' in env).toBe(false);
    });

    it('OpenAI: llm.active-id resolves the descriptor and emits provider + apiKey + model + endpoint', async () => {
      const env = await buildEnv({
        vaultGet: makeVault({
          'discord.bot-token': 'tok',
          'llm.active-id': 'openai',
          'llm.openai.api-key': 'sk-abc',
          'llm.openai.model': 'gpt-4o',
          'llm.openai.endpoint': 'https://custom.openai.example/v1',
        }),
      });
      expect(env['SCOUT__LLM__PROVIDER']).toBe('OpenAI');
      expect(env['SCOUT__LLM__OPENAI__APIKEY']).toBe('sk-abc');
      expect(env['SCOUT__LLM__OPENAI__CHAT__MODEL']).toBe('gpt-4o');
      expect(env['SCOUT__LLM__OPENAI__ENDPOINT']).toBe('https://custom.openai.example/v1');
    });

    it('OpenAI: omits ENDPOINT when left blank in the vault', async () => {
      const env = await buildEnv({
        vaultGet: makeVault({
          'discord.bot-token': 'tok',
          'llm.active-id': 'openai',
          'llm.openai.api-key': 'sk-abc',
          'llm.openai.model': 'gpt-4o',
        }),
      });
      expect('SCOUT__LLM__OPENAI__ENDPOINT' in env).toBe(false);
    });

    it('Gemini: emits provider + apiKey + model', async () => {
      const env = await buildEnv({
        vaultGet: makeVault({
          'discord.bot-token': 'tok',
          'llm.active-id': 'gemini',
          'llm.gemini.api-key': 'g-key',
          'llm.gemini.model': 'gemini-2.5-flash',
        }),
      });
      expect(env['SCOUT__LLM__PROVIDER']).toBe('Gemini');
      expect(env['SCOUT__LLM__GEMINI__APIKEY']).toBe('g-key');
      expect(env['SCOUT__LLM__GEMINI__CHAT__MODEL']).toBe('gemini-2.5-flash');
    });

    it('OpenRouter: emits provider + chat model + all three embeddings keys', async () => {
      const env = await buildEnv({
        vaultGet: makeVault({
          'discord.bot-token': 'tok',
          'llm.active-id': 'openrouter',
          'llm.openrouter.api-key': 'or-key',
          'llm.openrouter.model': 'openai/gpt-4o',
          'llm.openrouter.embeddings-model': 'text-embedding-3-small',
          'llm.openrouter.embeddings-endpoint': 'https://api.openai.com/v1',
          'llm.openrouter.embeddings-api-key': 'emb-key',
        }),
      });
      expect(env['SCOUT__LLM__PROVIDER']).toBe('OpenRouter');
      expect(env['SCOUT__LLM__OPENROUTER__APIKEY']).toBe('or-key');
      expect(env['SCOUT__LLM__OPENROUTER__CHAT__MODEL']).toBe('openai/gpt-4o');
      expect(env['SCOUT__LLM__OPENROUTER__EMBEDDINGS__MODEL']).toBe('text-embedding-3-small');
      expect(env['SCOUT__LLM__OPENROUTER__EMBEDDINGS__ENDPOINT']).toBe('https://api.openai.com/v1');
      expect(env['SCOUT__LLM__OPENROUTER__EMBEDDINGS__APIKEY']).toBe('emb-key');
    });

    it('Azure OpenAI: emits provider + endpoint + chat/embeddings deployments', async () => {
      const env = await buildEnv({
        vaultGet: makeVault({
          'discord.bot-token': 'tok',
          'llm.active-id': 'azure-openai',
          'llm.azureopenai.api-key': 'az-key',
          'llm.azureopenai.endpoint': 'https://res.openai.azure.com',
          'llm.azureopenai.chat-deployment': 'gpt-4o',
          'llm.azureopenai.embeddings-deployment': 'text-embedding-3-large',
        }),
      });
      expect(env['SCOUT__LLM__PROVIDER']).toBe('AzureOpenAI');
      expect(env['SCOUT__LLM__AZUREOPENAI__APIKEY']).toBe('az-key');
      expect(env['SCOUT__LLM__AZUREOPENAI__ENDPOINT']).toBe('https://res.openai.azure.com');
      expect(env['SCOUT__LLM__AZUREOPENAI__CHAT__DEPLOYMENT']).toBe('gpt-4o');
      expect(env['SCOUT__LLM__AZUREOPENAI__EMBEDDINGS__DEPLOYMENT']).toBe('text-embedding-3-large');
    });

    it('provider isolation: an active Gemini emits none of the OpenAI/OpenRouter/Azure keys, even when stale vault entries exist for them', async () => {
      const env = await buildEnv({
        vaultGet: makeVault({
          'discord.bot-token': 'tok',
          'llm.active-id': 'gemini',
          'llm.gemini.api-key': 'g-key',
          'llm.gemini.model': 'gemini-2.5-flash',
          'llm.openai.api-key': 'stale-openai',
          'llm.openrouter.api-key': 'stale-openrouter',
          'llm.azureopenai.api-key': 'stale-azure',
        }),
      });
      const keys = Object.keys(env);
      expect(keys.some((k) => k.startsWith('SCOUT__LLM__OPENAI'))).toBe(false);
      expect(keys.some((k) => k.startsWith('SCOUT__LLM__OPENROUTER'))).toBe(false);
      expect(keys.some((k) => k.startsWith('SCOUT__LLM__AZUREOPENAI'))).toBe(false);
    });

    it('secret values never appear among Object.keys(env) for a cloud provider', async () => {
      const SECRET = 'sk-super-secret-value';
      const env = await buildEnv({
        vaultGet: makeVault({
          'discord.bot-token': 'tok',
          'llm.active-id': 'openai',
          'llm.openai.api-key': SECRET,
          'llm.openai.model': 'gpt-4o',
        }),
      });
      const serialisedKeys = JSON.stringify(Object.keys(env));
      expect(serialisedKeys).not.toContain(SECRET);
      expect(env['SCOUT__LLM__OPENAI__APIKEY']).toBe(SECRET);
    });

    it('unmatched legacy llm.provider passes the raw value through unchanged (pre-catalog compatibility)', async () => {
      const env = await buildEnv({
        vaultGet: makeVault({ 'discord.bot-token': 'tok', 'llm.provider': 'LmStudio' }),
      });
      expect(env['SCOUT__LLM__PROVIDER']).toBe('LmStudio');
    });

    it('llm.active-id takes priority over legacy llm.provider when both are set', async () => {
      const env = await buildEnv({
        vaultGet: makeVault({
          'discord.bot-token': 'tok',
          'llm.active-id': 'gemini',
          'llm.provider': 'OpenAI',
          'llm.gemini.api-key': 'g-key',
          'llm.gemini.model': 'gemini-2.5-flash',
        }),
      });
      expect(env['SCOUT__LLM__PROVIDER']).toBe('Gemini');
    });
  });
});
