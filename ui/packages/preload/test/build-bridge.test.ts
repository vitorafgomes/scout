import { describe, it, expect, mock } from 'bun:test';
import { buildBridge, type IpcSubset } from '../src/build-bridge.js';
import { CHANNEL, type FeatureDto, type ScoutBridge, type WorkerEvent } from '@scout/shared';

function makeFakeIpc(): IpcSubset {
  return {
    invoke: mock((_channel: string, ..._args: unknown[]) => Promise.resolve(undefined as unknown)),
    on: mock((_channel: string, _listener: (event: unknown, ...args: unknown[]) => void) => {}),
    removeListener: mock(
      (_channel: string, _listener: (event: unknown, ...args: unknown[]) => void) => {},
    ),
  };
}

describe('buildBridge', () => {
  it('returns an object with exactly the keys of ScoutBridge', () => {
    const bridge = buildBridge(makeFakeIpc());

    const expectedKeys: (keyof ScoutBridge)[] = [
      'detectClients',
      'discordBuildOAuthUrl',
      'discordStartOAuthInstall',
      'discordCancelOAuthInstall',
      'vaultStore',
      'vaultHas',
      'vaultDelete',
      'vaultIsAvailable',
      'workerStart',
      'workerStop',
      'workerStatus',
      'onWorkerEvent',
      'appOpenExternal',
      'appGetVersion',
      'appQuit',
      'diagnosticsExport',
      'llmListOllamaModels',
      'llmListLmStudioModels',
      'llmPullOllamaModel',
      'onLlmPullOllamaProgress',
      'llmDeleteOllamaModel',
      'whisperListInstalled',
      'whisperDownloadModel',
      'onWhisperDownloadProgress',
      'whisperDeleteModel',
      'providersList',
      'providerStatus',
      'providerStart',
      'providerStop',
      'providerRestart',
      'providerSetAutostart',
      'featuresList',
      'featureSetCredentials',
      'featureSetup',
      'featureDeleteCredentials',
      'featureEnable',
      'featureDisable',
    ];

    const actualKeys = Object.keys(bridge).sort();
    expect(actualKeys).toEqual(expectedKeys.slice().sort());
    expect(actualKeys.length).toBe(expectedKeys.length);
  });

  it('detectClients invokes the correct channel exactly once and returns resolved value', async () => {
    const ipc = makeFakeIpc();
    const expected = { clients: [] };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.detectClients();

    expect(ipc.invoke).toHaveBeenCalledTimes(1);
    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.detectClients);
    expect(result).toBe(expected);
  });

  it('discordBuildOAuthUrl passes the request payload through without mutation', async () => {
    const ipc = makeFakeIpc();
    const req = {
      clientId: 'abc123',
      permissions: '8',
      scopes: ['bot', 'applications.commands'] as const,
    };
    const expected = { url: 'https://discord.com/oauth2/authorize?...', state: 'xyz' };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.discordBuildOAuthUrl(req);

    expect(ipc.invoke).toHaveBeenCalledTimes(1);
    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.discordBuildOAuthUrl, req);
    expect(result).toBe(expected);
  });

  it('onWorkerEvent registers a handler via ipc.on and forwards events stripping the ipc event arg', () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    const received: WorkerEvent[] = [];
    bridge.onWorkerEvent((ev) => received.push(ev));

    expect(ipc.on).toHaveBeenCalledTimes(1);
    expect(ipc.on).toHaveBeenCalledWith(CHANNEL.workerEvents, expect.any(Function));

    const calls = (ipc.on as ReturnType<typeof mock>).mock.calls as [
      string,
      (event: unknown, payload: WorkerEvent) => void,
    ][];
    const registeredHandler = calls[0]![1];

    const fakeEvent = {};
    const payload: WorkerEvent = { kind: 'stdout', line: 'hello', ts: '2026-01-01T00:00:00Z' };
    registeredHandler(fakeEvent, payload);

    expect(received).toHaveLength(1);
    expect(received[0]).toBe(payload);
  });

  it('onWorkerEvent returns an unsubscribe function that calls removeListener', () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    const unsubscribe = bridge.onWorkerEvent(() => {});
    expect(ipc.removeListener).toHaveBeenCalledTimes(0);

    unsubscribe();

    expect(ipc.removeListener).toHaveBeenCalledTimes(1);
    expect(ipc.removeListener).toHaveBeenCalledWith(CHANNEL.workerEvents, expect.any(Function));
  });

  it('onWorkerEvent passes the same handler reference to on and removeListener', () => {
    const ipc = makeFakeIpc();
    const bridge = buildBridge(ipc);

    const unsubscribe = bridge.onWorkerEvent(() => {});
    unsubscribe();

    const onCalls = (ipc.on as ReturnType<typeof mock>).mock.calls as [string, unknown][];
    const removeCalls = (ipc.removeListener as ReturnType<typeof mock>).mock.calls as [
      string,
      unknown,
    ][];

    expect(onCalls[0]![1]).toBe(removeCalls[0]![1]);
  });

  it('propagates rejection from ipc.invoke unchanged', async () => {
    const ipc = makeFakeIpc();
    const error = new Error('No handler registered for scout:detect.clients');
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.reject(error));

    const bridge = buildBridge(ipc);

    await expect(bridge.detectClients()).rejects.toBe(error);
  });

  it('appGetVersion takes no arguments (length === 0)', () => {
    const bridge = buildBridge(makeFakeIpc());
    expect(bridge.appGetVersion.length).toBe(0);
  });

  it('appGetVersion invokes the correct channel', async () => {
    const ipc = makeFakeIpc();
    const expected = { version: '1.0.0' };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.appGetVersion();

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.appGetVersion);
    expect(result).toBe(expected);
  });

  it('vaultHas passes key request through and returns response', async () => {
    const ipc = makeFakeIpc();
    const req = { key: 'discord.bot-token' as const };
    const expected = { exists: true };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.vaultHas(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.vaultHas, req);
    expect(result).toBe(expected);
  });

  it('workerStart invokes the correct channel with no extra args', async () => {
    const ipc = makeFakeIpc();
    const expected = { pid: 12345 };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.workerStart();

    expect(ipc.invoke).toHaveBeenCalledTimes(1);
    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.workerStart);
    expect(result).toBe(expected);
  });

  it('diagnosticsExport invokes the correct channel and returns bundle', async () => {
    const ipc = makeFakeIpc();
    const expected = { bundle: '{"version":"1.0.0"}' };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.diagnosticsExport();

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.diagnosticsExport);
    expect(result).toBe(expected);
  });

  it('appQuit propagates rejection from invoke', async () => {
    const ipc = makeFakeIpc();
    const error = new Error('No handler registered for scout:app.quit');
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.reject(error));

    const bridge = buildBridge(ipc);

    await expect(bridge.appQuit()).rejects.toBe(error);
  });

  it('featuresList invokes the correct channel with no arguments', async () => {
    const ipc = makeFakeIpc();
    const expected = { features: [] };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.featuresList();

    expect(ipc.invoke).toHaveBeenCalledTimes(1);
    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featuresList);
    expect(result).toBe(expected);
  });

  it('featureSetCredentials forwards the payload to the matching channel', async () => {
    const ipc = makeFakeIpc();
    const req = { id: 'github' as const, body: { pat: 'ghp_xxx' } };
    const expected: FeatureDto = {
      id: 'github',
      displayName: 'GitHub',
      description: '',
      requiresCredentials: true,
      hasCredentials: true,
      enabled: false,
      status: 'disabled',
      lastError: null,
      credentialFields: [],
      settingsFields: [],
      settings: {},
      scopeMatrix: null,
      setupSteps: null,
    };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.featureSetCredentials(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featureSetCredentials, req);
    expect(result).toBe(expected);
  });

  it('featureSetup forwards the credentials and settings to the matching channel', async () => {
    const ipc = makeFakeIpc();
    const req = {
      id: 'github' as const,
      credentials: { pat: 'ghp_xxx' },
      settings: { default_repo_url: 'https://github.com/acme/repo' },
    };
    const expected: FeatureDto = {
      id: 'github',
      displayName: 'GitHub',
      description: '',
      requiresCredentials: true,
      hasCredentials: true,
      enabled: false,
      status: 'disabled',
      lastError: null,
      credentialFields: [],
      settingsFields: [],
      settings: {},
      scopeMatrix: null,
      setupSteps: null,
    };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve(expected));

    const bridge = buildBridge(ipc);
    const result = await bridge.featureSetup(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featureSetup, req);
    expect(result).toBe(expected);
  });

  it('featureEnable invokes the correct channel with the id request', async () => {
    const ipc = makeFakeIpc();
    const req = { id: 'kubernetes' as const };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve({}));

    const bridge = buildBridge(ipc);
    await bridge.featureEnable(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featureEnable, req);
  });

  it('featureDisable invokes the correct channel with the id request', async () => {
    const ipc = makeFakeIpc();
    const req = { id: 'github' as const };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve({}));

    const bridge = buildBridge(ipc);
    await bridge.featureDisable(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featureDisable, req);
  });

  it('featureDeleteCredentials invokes the correct channel with the id request', async () => {
    const ipc = makeFakeIpc();
    const req = { id: 'github' as const };
    (ipc.invoke as ReturnType<typeof mock>).mockImplementation(() => Promise.resolve({}));

    const bridge = buildBridge(ipc);
    await bridge.featureDeleteCredentials(req);

    expect(ipc.invoke).toHaveBeenCalledWith(CHANNEL.featureDeleteCredentials, req);
  });
});
