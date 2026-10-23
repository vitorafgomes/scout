import { describe, expect, test } from 'bun:test';
import { CHANNEL, type ChannelName } from '../src/ipc-contract.js';

describe('CHANNEL constant', () => {
  test('all values are unique', () => {
    const values = Object.values(CHANNEL);
    const unique = new Set(values);
    expect(unique.size).toBe(values.length);
  });

  test('all values start with scout: prefix', () => {
    for (const value of Object.values(CHANNEL)) {
      expect(value.startsWith('scout:')).toBe(true);
    }
  });

  test('exposes exactly the expected channels', () => {
    expect(Object.keys(CHANNEL).sort()).toEqual(
      [
        'appGetVersion',
        'appOpenExternal',
        'appQuit',
        'detectClients',
        'diagnosticsExport',
        'discordBuildOAuthUrl',
        'discordCancelOAuthInstall',
        'discordStartOAuthInstall',
        'featureDeleteCredentials',
        'featureDisable',
        'featureEnable',
        'featureSetCredentials',
        'featureSetup',
        'featuresList',
        'llmDeleteOllamaModel',
        'llmDetectEnvironment',
        'llmListLmStudioModels',
        'llmListOllamaModels',
        'llmPullOllamaModel',
        'llmPullOllamaProgress',
        'providerRestart',
        'providerSetAutostart',
        'providerStart',
        'providerStatus',
        'providerStop',
        'providersList',
        'providersValidateKey',
        'shellOpenExternal',
        'whisperDeleteModel',
        'whisperDownloadModel',
        'whisperDownloadProgress',
        'whisperListInstalled',
        'vaultDelete',
        'vaultGetPublic',
        'vaultHas',
        'vaultIsAvailable',
        'vaultStore',
        'workerEvents',
        'workerStart',
        'workerStatus',
        'workerStop',
      ].sort(),
    );
  });
});

describe('ChannelName type', () => {
  test('accepts exact literal values only (compile-time check)', () => {
    // Compile-time: each literal is a valid ChannelName. If any CHANNEL value
    // drifts from this map, tsc fails.
    const valid: Record<ChannelName, true> = {
      'scout:detect.clients': true,
      'scout:discord.buildOAuthUrl': true,
      'scout:discord.startOAuthInstall': true,
      'scout:discord.cancelOAuthInstall': true,
      'scout:vault.store': true,
      'scout:vault.has': true,
      'scout:vault.delete': true,
      'scout:vault.getPublic': true,
      'scout:vault.isAvailable': true,
      'scout:worker.start': true,
      'scout:worker.stop': true,
      'scout:worker.status': true,
      'scout:worker.events': true,
      'scout:app.openExternal': true,
      'scout:app.getVersion': true,
      'scout:app.quit': true,
      'scout:diagnostics.export': true,
      'scout:llm.listOllamaModels': true,
      'scout:llm.pullOllamaModel': true,
      'scout:llm.pullOllamaProgress': true,
      'scout:llm.deleteOllamaModel': true,
      'scout:llm.listLmStudioModels': true,
      'scout:llm.detectEnvironment': true,
      'scout:shell.openExternal': true,
      'scout:whisper.listInstalled': true,
      'scout:whisper.downloadModel': true,
      'scout:whisper.downloadProgress': true,
      'scout:whisper.deleteModel': true,
      'scout:providers.list': true,
      'scout:providers.status': true,
      'scout:providers.start': true,
      'scout:providers.stop': true,
      'scout:providers.restart': true,
      'scout:providers.setAutostart': true,
      'scout:providers.validateKey': true,
      'scout:features.list': true,
      'scout:features.setCredentials': true,
      'scout:features.setup': true,
      'scout:features.deleteCredentials': true,
      'scout:features.enable': true,
      'scout:features.disable': true,
    };
    expect(Object.values(valid).every((v) => v === true)).toBe(true);
  });
});

describe('no-external-write sanity scan', () => {
  // Channels whose name contains a write verb but which do NOT perform an
  // external-system write. Each entry justifies why the channel is read-only
  // from Scout's perspective (Discord, GitHub, cloud APIs, etc. are never
  // mutated via IPC; all outbound mutation is blocked by Scout.Core).
  //
  // - scout:vault.store           -> local safeStorage write, never leaves the machine
  // - scout:vault.delete          -> local safeStorage delete, never leaves the machine
  // - scout:worker.stop           -> local child-process signal, not an external API call
  // - scout:llm.listOllamaModels  -> READ on the user's local Ollama instance (GET /api/tags); no mutation
  // - scout:llm.deleteOllamaModel -> DELETE on the user's local Ollama (/api/delete); model is on user's
  //                                  disk, not an external system
  const LOCAL_WRITE_ALLOWLIST: readonly string[] = [
    'scout:vault.store',
    'scout:vault.delete',
    'scout:worker.stop',
    'scout:llm.listOllamaModels',
    'scout:llm.deleteOllamaModel',
    'scout:whisper.downloadModel',
    'scout:whisper.deleteModel',
    // - scout:features.setCredentials    -> writes a feature credential blob to the OS keyring via the worker;
    //                                       never reaches an external system (credentials only enable later reads).
    // - scout:features.deleteCredentials -> deletes that same local keyring entry; no external mutation.
    'scout:features.setCredentials',
    'scout:features.deleteCredentials',
  ];

  // Verbs that typically imply mutation against an external system. If a new
  // channel name contains one of these and is NOT in the allow-list above,
  // the test fails — do not rename to bypass the scan, surface the concern.
  const WRITE_VERBS: readonly string[] = ['create', 'post', 'write', 'send', 'update', 'delete'];

  test('every write-verb channel is local-only and justified', () => {
    for (const value of Object.values(CHANNEL)) {
      const lower = value.toLowerCase();
      const matchedVerb = WRITE_VERBS.find((verb) => lower.includes(verb));
      if (matchedVerb === undefined) continue;
      expect(
        LOCAL_WRITE_ALLOWLIST,
        `channel ${value} matches write verb "${matchedVerb}" but is not in LOCAL_WRITE_ALLOWLIST`,
      ).toContain(value);
    }
  });

  test('allow-list entries are all real channels', () => {
    const channelValues = new Set<string>(Object.values(CHANNEL));
    for (const allowed of LOCAL_WRITE_ALLOWLIST) {
      expect(channelValues.has(allowed)).toBe(true);
    }
  });
});
