import { describe, it, expect, spyOn } from 'bun:test';
import type { IpcMainInvokeEvent } from 'electron';
import { EventEmitter } from 'node:events';
import { mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  buildEnv,
  migrateLegacyApiKey,
  readGrantedWriteFeatures,
} from '../src/worker/build-env.js';
import {
  connectControlPlane,
  type ControlPlaneLauncherDeps,
} from '../src/control-plane/launcher.js';
import { registerFeaturesHandlers, type FeaturesClient } from '../src/ipc/features.js';
import { restartWorkerIfRunning } from '../src/ipc/github.js';
import type { Logger } from '../src/util/logger.js';
import {
  CHANNEL,
  ControlPlaneUnavailableError,
  VaultUnavailableError,
  type FeatureDto,
  type FeatureId,
  type VaultKey,
  type WorkerStatus,
} from '@scout/shared';
import { API_KEY, TOKEN_FILE, connect, makeGoogleFake } from './google-fakes.js';

function makeVault(
  entries: Record<string, string | null>,
): (key: string) => Promise<string | null> {
  return async (key) => entries[key] ?? null;
}

describe('buildEnv', () => {
  it('with neither Discord nor Slack connected, starts the worker with only the local adapter', async () => {
    const env = await buildEnv({ vaultGet: makeVault({}), slackTokens: null });

    expect(env['SCOUT__ADAPTERS__LOCAL__ENABLED']).toBe('true');
    expect(Object.keys(env).filter((key) => /DISCORD|SLACK/.test(key))).toEqual([]);
  });

  it('Slack only: the worker gets both Slack tokens under the keys SlackOptions binds, and no Discord token', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({}),
      slackTokens: { botToken: 'xoxb-b', appToken: 'xapp-a' },
    });

    expect(env['SCOUT__ADAPTERS__SLACK__BOTTOKEN']).toBe('xoxb-b');
    expect(env['SCOUT__ADAPTERS__SLACK__APPLEVELTOKEN']).toBe('xapp-a');
    expect('SCOUT__ADAPTERS__DISCORD__BOTTOKEN' in env).toBe(false);
  });

  it('Discord and Slack: the worker gets both adapters', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      slackTokens: { botToken: 'xoxb-b', appToken: 'xapp-a' },
    });

    expect(env['SCOUT__ADAPTERS__DISCORD__BOTTOKEN']).toBe('tok');
    expect(env['SCOUT__ADAPTERS__SLACK__BOTTOKEN']).toBe('xoxb-b');
    expect(env['SCOUT__ADAPTERS__SLACK__APPLEVELTOKEN']).toBe('xapp-a');
  });

  it('leaves the Slack variables out when Slack is not connected', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      slackTokens: null,
    });

    expect(Object.keys(env).filter((k) => k.startsWith('SCOUT__ADAPTERS__SLACK__'))).toEqual([]);
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
      SCOUT__FEATURES__GRANTEDWRITES: '',
      SCOUT__WORKERGATE__SOCKETPATH: '',
      SCOUT__WORKERGATE__TOKEN: '',
      SCOUT__OPERATOR__FFMPEGPATH: '',
      SCOUT__OPERATOR__FONTPATH: '',
      SCOUT__OPERATOR__PIPERPATH: '',
      SCOUT__OPERATOR__VOICESPATH: '',
      SCOUT__OPERATOR__RHUBARBPATH: '',
      SCOUT__OPERATOR__MOUTHSPATH: '',
      SCOUT__LLM__PROVIDER: 'OpenAI',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__LOCAL__ENABLED: 'true',
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
      SCOUT__FEATURES__GRANTEDWRITES: '',
      SCOUT__WORKERGATE__SOCKETPATH: '',
      SCOUT__WORKERGATE__TOKEN: '',
      SCOUT__OPERATOR__FFMPEGPATH: '',
      SCOUT__OPERATOR__FONTPATH: '',
      SCOUT__OPERATOR__PIPERPATH: '',
      SCOUT__OPERATOR__VOICESPATH: '',
      SCOUT__OPERATOR__RHUBARBPATH: '',
      SCOUT__OPERATOR__MOUTHSPATH: '',
      SCOUT__LLM__PROVIDER: 'Gemini',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__LOCAL__ENABLED: 'true',
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
      SCOUT__FEATURES__GRANTEDWRITES: '',
      SCOUT__WORKERGATE__SOCKETPATH: '',
      SCOUT__WORKERGATE__TOKEN: '',
      SCOUT__OPERATOR__FFMPEGPATH: '',
      SCOUT__OPERATOR__FONTPATH: '',
      SCOUT__OPERATOR__PIPERPATH: '',
      SCOUT__OPERATOR__VOICESPATH: '',
      SCOUT__OPERATOR__RHUBARBPATH: '',
      SCOUT__OPERATOR__MOUTHSPATH: '',
      SCOUT__LLM__PROVIDER: 'Ollama',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__LOCAL__ENABLED: 'true',
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
      SCOUT__FEATURES__GRANTEDWRITES: '',
      SCOUT__WORKERGATE__SOCKETPATH: '',
      SCOUT__WORKERGATE__TOKEN: '',
      SCOUT__OPERATOR__FFMPEGPATH: '',
      SCOUT__OPERATOR__FONTPATH: '',
      SCOUT__OPERATOR__PIPERPATH: '',
      SCOUT__OPERATOR__VOICESPATH: '',
      SCOUT__OPERATOR__RHUBARBPATH: '',
      SCOUT__OPERATOR__MOUTHSPATH: '',
      SCOUT__LLM__PROVIDER: 'Ollama',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__LOCAL__ENABLED: 'true',
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
      SCOUT__FEATURES__GRANTEDWRITES: '',
      SCOUT__WORKERGATE__SOCKETPATH: '',
      SCOUT__WORKERGATE__TOKEN: '',
      SCOUT__OPERATOR__FFMPEGPATH: '',
      SCOUT__OPERATOR__FONTPATH: '',
      SCOUT__OPERATOR__PIPERPATH: '',
      SCOUT__OPERATOR__VOICESPATH: '',
      SCOUT__OPERATOR__RHUBARBPATH: '',
      SCOUT__OPERATOR__MOUTHSPATH: '',
      SCOUT__LLM__PROVIDER: 'Ollama',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__LOCAL__ENABLED: 'true',
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
      SCOUT__FEATURES__GRANTEDWRITES: '',
      SCOUT__WORKERGATE__SOCKETPATH: '',
      SCOUT__WORKERGATE__TOKEN: '',
      SCOUT__OPERATOR__FFMPEGPATH: '',
      SCOUT__OPERATOR__FONTPATH: '',
      SCOUT__OPERATOR__PIPERPATH: '',
      SCOUT__OPERATOR__VOICESPATH: '',
      SCOUT__OPERATOR__RHUBARBPATH: '',
      SCOUT__OPERATOR__MOUTHSPATH: '',
      SCOUT__LLM__PROVIDER: 'ChatGptPlan',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__LOCAL__ENABLED: 'true',
      SCOUT__ADAPTERS__DISCORD__BOTTOKEN: 'tok',
      SCOUT__LLM__CHATGPTPLAN__TOKENFILE: '/home/owner/.config/Scout/chatgpt-token.json',
      SCOUT__LLM__CHATGPTPLAN__CHAT__MODEL: 'gpt-6.1-sol',
      SCOUT__ACCESS__REQUIREALLOWLIST: 'true',
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
      SCOUT__FEATURES__GRANTEDWRITES: '',
      SCOUT__WORKERGATE__SOCKETPATH: '',
      SCOUT__WORKERGATE__TOKEN: '',
      SCOUT__OPERATOR__FFMPEGPATH: '',
      SCOUT__OPERATOR__FONTPATH: '',
      SCOUT__OPERATOR__PIPERPATH: '',
      SCOUT__OPERATOR__VOICESPATH: '',
      SCOUT__OPERATOR__RHUBARBPATH: '',
      SCOUT__OPERATOR__MOUTHSPATH: '',
      SCOUT__LLM__PROVIDER: 'None',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__LOCAL__ENABLED: 'true',
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

  it('gives the worker the kubeconfig path and a NAME and LABEL pair per allowed context', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      kubernetesSelection: {
        path: '/home/me/.kube/config',
        contexts: [
          { name: 'AKSDEV01-admin', label: 'dev' },
          { name: 'AKSPRD01-admin', label: 'prod' },
        ],
      },
    });

    expect(
      Object.entries(env).filter(([key]) => key.startsWith('SCOUT__PLUGINS__KUBERNETES')),
    ).toEqual([
      ['SCOUT__PLUGINS__KUBERNETES__KUBECONFIGPATH', '/home/me/.kube/config'],
      ['SCOUT__PLUGINS__KUBERNETES__CONTEXTS__0__NAME', 'AKSDEV01-admin'],
      ['SCOUT__PLUGINS__KUBERNETES__CONTEXTS__0__LABEL', 'dev'],
      ['SCOUT__PLUGINS__KUBERNETES__CONTEXTS__1__NAME', 'AKSPRD01-admin'],
      ['SCOUT__PLUGINS__KUBERNETES__CONTEXTS__1__LABEL', 'prod'],
    ]);
  });

  it('leaves the Kubernetes variables out when Kubernetes is not connected', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      kubernetesSelection: null,
    });

    expect(Object.keys(env).filter((key) => key.startsWith('SCOUT__PLUGINS__KUBERNETES'))).toEqual(
      [],
    );
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
      SCOUT__FEATURES__GRANTEDWRITES: '',
      SCOUT__WORKERGATE__SOCKETPATH: '',
      SCOUT__WORKERGATE__TOKEN: '',
      SCOUT__OPERATOR__FFMPEGPATH: '',
      SCOUT__OPERATOR__FONTPATH: '',
      SCOUT__OPERATOR__PIPERPATH: '',
      SCOUT__OPERATOR__VOICESPATH: '',
      SCOUT__OPERATOR__RHUBARBPATH: '',
      SCOUT__OPERATOR__MOUTHSPATH: '',
      SCOUT__LLM__PROVIDER: 'None',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__LOCAL__ENABLED: 'true',
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
      SCOUT__FEATURES__GRANTEDWRITES: '',
      SCOUT__WORKERGATE__SOCKETPATH: '',
      SCOUT__WORKERGATE__TOKEN: '',
      SCOUT__OPERATOR__FFMPEGPATH: '',
      SCOUT__OPERATOR__FONTPATH: '',
      SCOUT__OPERATOR__PIPERPATH: '',
      SCOUT__OPERATOR__VOICESPATH: '',
      SCOUT__OPERATOR__RHUBARBPATH: '',
      SCOUT__OPERATOR__MOUTHSPATH: '',
      SCOUT__LLM__PROVIDER: 'Ollama',
      DOTNET_ENVIRONMENT: 'Production',
      SCOUT__ADAPTERS__LOCAL__ENABLED: 'true',
      SCOUT__ADAPTERS__DISCORD__BOTTOKEN: 'tok',
    });
  });
});

describe('buildEnv allowed users', () => {
  const accessKeys = (env: Record<string, string>): string[] =>
    Object.keys(env).filter((key) => key.startsWith('SCOUT__ACCESS__'));

  it('gives the worker each allowed user id', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      allowedUserIds: ['111', 'U02ABC'],
    });

    expect(env['SCOUT__ACCESS__ALLOWEDUSERIDS__0']).toBe('111');
    expect(env['SCOUT__ACCESS__ALLOWEDUSERIDS__1']).toBe('U02ABC');
  });

  it('fails closed when GitHub is connected: the worker must require an allow list', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      gitHubEnv: { SCOUT__PLUGINS__GITHUB__TOKENFILE: '/user-data/github-token.json' },
    });

    expect(env['SCOUT__ACCESS__REQUIREALLOWLIST']).toBe('true');
  });

  it('fails closed when a Kubernetes selection exists: the worker must require an allow list', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      kubernetesSelection: {
        path: '/home/me/.kube/config',
        contexts: [{ name: 'prod-admin', label: 'prod' }],
      },
    });

    expect(env['SCOUT__ACCESS__REQUIREALLOWLIST']).toBe('true');
  });

  it('fails closed when the ChatGPT plan is the provider: the worker must require an allow list', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok', 'llm.provider': 'ChatGptPlan' }),
      chatGptTokenFile: '/user-data/chatgpt-token.json',
    });

    expect(env['SCOUT__ACCESS__REQUIREALLOWLIST']).toBe('true');
  });

  it('Slack is a chat platform, not an owner credential: connecting it does not require an allow list', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'llm.provider': 'Ollama' }),
      slackTokens: { botToken: 'xoxb-b', appToken: 'xapp-a' },
    });

    expect('SCOUT__ACCESS__REQUIREALLOWLIST' in env).toBe(false);
  });

  it('local first: with no owner credential and no allowed users the worker gets no access variable', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok', 'llm.provider': 'Ollama' }),
      gitHubEnv: {},
      kubernetesSelection: null,
      allowedUserIds: [],
    });

    expect(accessKeys(env)).toEqual([]);
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

describe('buildEnv YouTube', () => {
  it('a YouTube session hands the worker the token file path and turns on the allow-list gate', async () => {
    const google = makeGoogleFake();
    await connect(google);

    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      youTubeEnv: await google.auth.workerEnv(),
    });

    expect(env['SCOUT__PLUGINS__YOUTUBE__TOKENFILE']).toBe(TOKEN_FILE);
    expect('SCOUT__PLUGINS__YOUTUBE__APIKEY' in env).toBe(false);
    expect(env['SCOUT__ACCESS__REQUIREALLOWLIST']).toBe('true');
  });

  it('a pasted YouTube key reaches the worker as SCOUT__PLUGINS__YOUTUBE__APIKEY', async () => {
    const google = makeGoogleFake();
    await google.auth.saveApiKey(API_KEY);

    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      youTubeEnv: await google.auth.workerEnv(),
    });

    expect(env['SCOUT__PLUGINS__YOUTUBE__APIKEY']).toBe(API_KEY);
    expect('SCOUT__PLUGINS__YOUTUBE__TOKENFILE' in env).toBe(false);
    expect(env['SCOUT__ACCESS__REQUIREALLOWLIST']).toBe('true');
  });

  it('without a YouTube connection the worker gets no YouTube variable and no allow-list gate from it', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      youTubeEnv: await makeGoogleFake().auth.workerEnv(),
    });

    expect(Object.keys(env).filter((key) => key.includes('YOUTUBE'))).toEqual([]);
    expect('SCOUT__ACCESS__REQUIREALLOWLIST' in env).toBe(false);
  });
});

describe('buildEnv voice transcription', () => {
  it('gives the worker the installed Whisper model path', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      whisperModelPath: '/user-data/whisper-models/ggml-large-v3-turbo.bin',
    });

    expect(env['SCOUT__TRANSCRIPTION__MODELPATH']).toBe(
      '/user-data/whisper-models/ggml-large-v3-turbo.bin',
    );
  });

  it('sets no transcription variable when no Whisper model is installed', async () => {
    const env = await buildEnv({
      vaultGet: makeVault({ 'discord.bot-token': 'tok' }),
      whisperModelPath: null,
    });

    expect(Object.keys(env).filter((key) => key.startsWith('SCOUT__TRANSCRIPTION__'))).toEqual([]);
  });
});

describe('buildEnv quick ask', () => {
  it('turns on the local adapter so the quick-ask window can reach the worker', async () => {
    const env = await buildEnv({ vaultGet: makeVault({ 'discord.bot-token': 'tok' }) });

    expect(env['SCOUT__ADAPTERS__LOCAL__ENABLED']).toBe('true');
  });
});

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

describe('buildEnv write grants', () => {
  const silent: Logger = {
    debug() {},
    info() {},
    warn() {},
    error() {},
    child() {
      return silent;
    },
  };

  function feature(id: FeatureDto['id'], granted: boolean): FeatureDto {
    return {
      id,
      displayName: id,
      description: '',
      requiresCredentials: false,
      hasCredentials: false,
      enabled: granted,
      status: granted ? 'enabled' : 'disabled',
      lastError: null,
      credentialFields: [],
      settingsFields: [],
      settings: {},
      scopeMatrix: null,
      setupSteps: null,
      writeClass: id === 'youtube' ? 'executive' : 'none',
      granted,
      grantedAt: granted ? '2026-11-02T10:00:00Z' : null,
    };
  }

  it('granted write features reach the worker as SCOUT__FEATURES__GRANTEDWRITES and a grant change restarts the worker', async () => {
    let granted: FeatureId[] = [];
    const ids: FeatureId[] = ['github', 'kubernetes', 'youtube'];
    const client = {
      listFeatures: async () => ids.map((id) => feature(id, granted.includes(id))),
      setWriteGrant: async (id: FeatureId, grant: boolean) => {
        granted = grant ? [...granted, id] : granted.filter((g) => g !== id);
        return feature(id, grant);
      },
      enableFeature: async (id: FeatureId) => {
        granted = [...granted, id];
        return feature(id, true);
      },
      disableFeature: async (id: FeatureId) => {
        granted = granted.filter((g) => g !== id);
        return feature(id, false);
      },
    };
    const envs: Record<string, string>[] = [];
    let state: WorkerStatus['state'] = 'stopped';
    const worker = {
      get status(): WorkerStatus {
        return { state } as WorkerStatus;
      },
      async start() {
        envs.push(
          await buildEnv({
            vaultGet: async () => null,
            grantedWriteFeatures: await readGrantedWriteFeatures(client, silent),
          }),
        );
        state = 'running';
        return { pid: 1 };
      },
      async stop() {
        state = 'stopped';
        return { exitCode: 0 };
      },
    };
    const handlers = new Map<
      string,
      (event: IpcMainInvokeEvent, req: unknown) => Promise<unknown>
    >();
    let restart: Promise<void> = Promise.resolve();
    registerFeaturesHandlers(
      { handle: (channel, fn) => void handlers.set(channel, fn), removeHandler: () => {} },
      async () => client as unknown as FeaturesClient,
      silent,
      { onWriteGrantChanged: () => (restart = restartWorkerIfRunning(worker, silent)) },
    );

    const send = async (channel: string, req: object): Promise<void> => {
      await handlers.get(channel)!(null as unknown as IpcMainInvokeEvent, req);
      await restart;
    };

    await worker.start();
    await send(CHANNEL.featureWriteGrant, { id: 'youtube', granted: true, confirmWrites: true });
    await send(CHANNEL.featureWriteGrant, { id: 'youtube', granted: false, confirmWrites: false });
    await send(CHANNEL.featureEnable, { id: 'youtube', confirmWrites: true });
    await send(CHANNEL.featureDisable, { id: 'youtube' });

    expect(envs.map((env) => env['SCOUT__FEATURES__GRANTEDWRITES'] || 'no grants')).toEqual([
      'no grants',
      'youtube',
      'no grants',
      'youtube',
      'no grants',
    ]);
  });

  it('no feature granted sets SCOUT__FEATURES__GRANTEDWRITES empty so a value inherited from the desktop cannot grant writes', async () => {
    const granted = await buildEnv({ vaultGet: async () => null, grantedWriteFeatures: [] });
    const unknown = await buildEnv({ vaultGet: async () => null });

    expect(granted['SCOUT__FEATURES__GRANTEDWRITES']).toBe('');
    expect(unknown['SCOUT__FEATURES__GRANTEDWRITES']).toBe('');
  });

  it('a worker started while the control plane is still connecting gets the grants once it connects', async () => {
    const warnings: string[] = [];
    const logger: Logger = { ...silent, warn: (msg: string) => void warnings.push(msg) };
    let connect: (client: { listFeatures(): Promise<FeatureDto[]> }) => void = () => {};
    const connecting = new Promise<{ listFeatures(): Promise<FeatureDto[]> }>((resolve) => {
      connect = resolve;
    });

    const reading = readGrantedWriteFeatures(connecting, logger, 5_000);
    setTimeout(
      () =>
        connect({
          listFeatures: async () => [
            { id: 'youtube', granted: true } as FeatureDto,
            { id: 'github', granted: false } as FeatureDto,
          ],
        }),
      20,
    );

    expect(await reading).toEqual(['youtube']);
    expect(warnings).toEqual([]);
  });

  it('a control plane that does not connect within the wait starts the worker with no grants and a warning', async () => {
    const warnings: string[] = [];
    const logger: Logger = { ...silent, warn: (msg: string) => void warnings.push(msg) };
    const never = new Promise<{ listFeatures(): Promise<FeatureDto[]> }>(() => {});

    const granted = await readGrantedWriteFeatures(never, logger, 20);

    expect(granted).toEqual([]);
    expect(warnings).toEqual([
      'worker starts with no write grants: the control plane is not connected yet',
    ]);
  });

  it('a control plane that is not connected yet or does not answer gives the worker no grants and is logged', async () => {
    const warnings: string[] = [];
    const logger: Logger = { ...silent, warn: (msg: string) => void warnings.push(msg) };
    const failing = {
      listFeatures: async (): Promise<FeatureDto[]> => {
        throw new ControlPlaneUnavailableError('socket reset');
      },
    };

    const notConnected = await readGrantedWriteFeatures(null, logger);
    const unanswered = await readGrantedWriteFeatures(failing, logger);

    expect([notConnected, unanswered]).toEqual([[], []]);
    expect(warnings).toHaveLength(2);
  });
});

describe('buildEnv worker gate', () => {
  const silent: Logger = {
    debug() {},
    info() {},
    warn() {},
    error() {},
    child() {
      return silent;
    },
  };

  it('always gives the worker the bundled ffmpeg, font, piper, voices, rhubarb and mouths paths, empty for any the build did not bundle, so inherited values cannot override them', async () => {
    const bundled = await buildEnv({
      vaultGet: async () => null,
      mediaTools: {
        ffmpeg: '/opt/Scout/resources/media-tools/linux-x64/ffmpeg',
        font: '/opt/Scout/resources/fonts/NotoSans-Regular.ttf',
        piper: '/opt/Scout/resources/media-tools/linux-x64/piper/piper',
        voices: '/opt/Scout/resources/voices',
        rhubarb: '/opt/Scout/resources/media-tools/linux-x64/rhubarb/rhubarb',
        mouths: '/opt/Scout/resources/mouths',
      },
    });
    const notBundled = await buildEnv({
      vaultGet: async () => null,
      mediaTools: { ffmpeg: '', font: '', piper: '', voices: '', rhubarb: '', mouths: '' },
    });
    const absent = await buildEnv({ vaultGet: async () => null });
    const paths = (env: Record<string, string>) => [
      env['SCOUT__OPERATOR__FFMPEGPATH'],
      env['SCOUT__OPERATOR__FONTPATH'],
      env['SCOUT__OPERATOR__PIPERPATH'],
      env['SCOUT__OPERATOR__VOICESPATH'],
      env['SCOUT__OPERATOR__RHUBARBPATH'],
      env['SCOUT__OPERATOR__MOUTHSPATH'],
    ];

    expect(paths(bundled)).toEqual([
      '/opt/Scout/resources/media-tools/linux-x64/ffmpeg',
      '/opt/Scout/resources/fonts/NotoSans-Regular.ttf',
      '/opt/Scout/resources/media-tools/linux-x64/piper/piper',
      '/opt/Scout/resources/voices',
      '/opt/Scout/resources/media-tools/linux-x64/rhubarb/rhubarb',
      '/opt/Scout/resources/mouths',
    ]);
    expect(paths(notBundled)).toEqual(['', '', '', '', '', '']);
    expect(paths(absent)).toEqual(['', '', '', '', '', '']);
    expect(Object.keys(bundled).filter((key) => key.toUpperCase().includes('FFPROBE'))).toEqual([]);
  });

  it('always sets the worker gate socket and token, empty while the control plane is not launched, so inherited values cannot override them', async () => {
    const absent = await buildEnv({ vaultGet: async () => null });
    const notLaunched = await buildEnv({ vaultGet: async () => null, workerGate: null });
    const launched = await buildEnv({
      vaultGet: async () => null,
      workerGate: { socketPath: '/run/user/1000/scout/controlplane-7.sock', token: 'w'.repeat(64) },
    });

    for (const env of [absent, notLaunched]) {
      expect([env['SCOUT__WORKERGATE__SOCKETPATH'], env['SCOUT__WORKERGATE__TOKEN']]).toEqual([
        '',
        '',
      ]);
    }
    expect([
      launched['SCOUT__WORKERGATE__SOCKETPATH'],
      launched['SCOUT__WORKERGATE__TOKEN'],
    ]).toEqual(['/run/user/1000/scout/controlplane-7.sock', 'w'.repeat(64)]);
  });

  it('a worker env built while the control plane holds the desktop token carries only the worker token, under SCOUT__WORKERGATE__TOKEN', async () => {
    const desktopToken = 'd'.repeat(64);
    const workerToken = 'e'.repeat(64);
    const socketDir = mkdtempSync(path.join(os.tmpdir(), 'scout-wg-'));
    const child = Object.assign(new EventEmitter(), { pid: 7, kill: () => true });
    const hostEnvs: Record<string, string | undefined>[] = [];
    const spawn = ((_cmd: string, _args: string[], options: { env: Record<string, string> }) => {
      hostEnvs.push(options.env);
      return child;
    }) as unknown as ControlPlaneLauncherDeps['spawn'];
    try {
      const connection = connectControlPlane({
        spawn,
        resolveBinary: () => '/fake/Scout.Host.ControlPlane',
        logger: silent,
        platform: 'linux',
        socketDir,
        mintToken: () => desktopToken,
        mintWorkerToken: () => workerToken,
        protoDir: '/unused',
        socketWaitMs: 50,
      });
      connection.client.catch(() => undefined);

      const env = await buildEnv({
        vaultGet: async () => null,
        workerGate: connection.workerGate(),
      });
      const workerEnv = { ...process.env, ...env };

      expect(hostEnvs[0]?.['SCOUT_CONTROLPLANE_TOKEN']).toBe(desktopToken);
      expect(hostEnvs[0]?.['SCOUT_CONTROLPLANE_WORKER_TOKEN']).toBe(workerToken);
      expect(
        ['SCOUT_CONTROLPLANE_TOKEN', 'SCOUT_CONTROLPLANE_WORKER_TOKEN'].filter(
          (k) => k in workerEnv,
        ),
      ).toEqual([]);
      expect(Object.values(workerEnv).filter((value) => value?.includes(desktopToken))).toEqual([]);
      expect(Object.entries(env).filter(([, value]) => value === workerToken)).toEqual([
        ['SCOUT__WORKERGATE__TOKEN', workerToken],
      ]);
      expect(env['SCOUT__WORKERGATE__SOCKETPATH']).toBe(
        path.join(socketDir, `controlplane-${process.pid}.sock`),
      );
      connection.dispose();
    } finally {
      rmSync(socketDir, { recursive: true, force: true });
    }
  });
});
