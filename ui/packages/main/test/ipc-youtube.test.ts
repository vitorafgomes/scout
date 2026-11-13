import { describe, it, expect } from 'bun:test';
import {
  CHANNEL,
  ControlPlaneUnavailableError,
  readIpcError,
  type FeatureDto,
  type FeatureId,
  type WorkerStatus,
} from '@scout/shared';
import type { IpcMain, IpcMainInvokeEvent } from 'electron';

import { restartWorkerIfRunning } from '../src/ipc/github.js';
import { restartPath } from './restart-fakes.js';
import { memoryPendingRevokes } from './pending-revokes-fake.js';
import {
  registerFeaturesHandlers,
  type FeaturesClient,
  type FeaturesHooks,
} from '../src/ipc/features.js';
import {
  registerYouTubeHandlers,
  revokeYouTubeWriteGrant,
  youTubeWriteGrantHooks,
} from '../src/ipc/youtube.js';
import {
  API_KEY,
  AUTH_CODE,
  CLIENT_FILE,
  CLIENT_SECRET,
  ONE_HOUR_MS,
  TOKEN_FILE,
  connect,
  json,
  makeGoogleFake,
  type GoogleFake,
} from './google-fakes.js';

const READ_SCOPES =
  'https://www.googleapis.com/auth/youtube.readonly https://www.googleapis.com/auth/yt-analytics.readonly';
const WRITE_SCOPES =
  'https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.force-ssl';

function grantRecorder(fake: GoogleFake, grants: string[], deletes: string[] = []) {
  const ipc = makeFakeIpcMain();
  const client = {
    async setWriteGrant(id: FeatureId, granted: boolean) {
      const session = JSON.parse(fake.vault.get('youtube.session') ?? '{}') as {
        scopes?: string[];
      };
      grants.push(`${id} ${granted} with ${(session.scopes ?? []).length} scopes`);
      return { id, granted } as FeatureDto;
    },
  };
  registerFeaturesHandlers(ipc, async () => client as unknown as FeaturesClient, fake.deps.logger, {
    ...FROM_MAIN_WINDOW,
    ...youTubeWriteGrantHooks(fake.auth, async () => {
      deletes.push('youtube');
    }),
  });
  return (id: FeatureId, granted: boolean) =>
    invoke(ipc, CHANNEL.featureWriteGrant, { id, granted, confirmWrites: granted });
}

type IpcHandler = (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown;

function makeFakeIpcMain(): Pick<IpcMain, 'handle' | 'removeHandler'> & {
  handlers: Map<string, IpcHandler>;
} {
  const handlers = new Map<string, IpcHandler>();
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

const MAIN_FRAME = { frameTreeNodeId: 1, url: 'file:///scout/index.html#/features' };
const MAIN_EVENT = {
  sender: { mainFrame: MAIN_FRAME },
  senderFrame: MAIN_FRAME,
} as unknown as IpcMainInvokeEvent;
const FROM_MAIN_WINDOW: Pick<FeaturesHooks, 'windowOf' | 'isAppPage'> = {
  windowOf: () => 'main',
  isAppPage: () => true,
};

function recordingControlPlane(calls: string[], deletes: string[] = []) {
  return async () => ({
    async setWriteGrant(id: FeatureId, granted: boolean, confirmWrites: boolean) {
      calls.push(`grant ${id} ${granted} ${confirmWrites}`);
      return {} as FeatureDto;
    },
    async deleteFeatureCredentials(id: FeatureId) {
      deletes.push(id);
      return {} as FeatureDto;
    },
  });
}

function recordingWorker(calls: string[]) {
  return {
    status: { state: 'running', restarts: 0 } as WorkerStatus,
    async stop() {
      calls.push('stop');
      return { exitCode: 0 };
    },
    async start() {
      calls.push('start');
      return { pid: 42 };
    },
  };
}

function wiredToControlPlane(
  calls: string[],
  controlPlane: Parameters<typeof revokeYouTubeWriteGrant>[0] = recordingControlPlane(calls),
) {
  const worker = recordingWorker(calls);
  let restart: Promise<void> = Promise.resolve();
  const fake = makeGoogleFake({
    onConnectionChanged: () => {
      restart = restartWorkerIfRunning(restartPath(worker), fake.deps.logger);
    },
    revokeWriteGrant: () =>
      revokeYouTubeWriteGrant(controlPlane, memoryPendingRevokes(), fake.deps.logger, () =>
        calls.push('features changed'),
      ),
  });
  return { fake, restarted: () => restart };
}

async function connectedWithRunningWorker(calls: string[], method: 'oauth' | 'apiKey') {
  const worker = recordingWorker(calls);
  let restart: Promise<void> = Promise.resolve();
  const fake = makeGoogleFake({
    onConnectionChanged: () => {
      restart = restartWorkerIfRunning(restartPath(worker), fake.deps.logger);
    },
  });
  if (method === 'oauth') await connect(fake);
  else await fake.auth.saveApiKey(API_KEY);
  await restart;
  calls.length = 0;
  return { fake, restarted: () => restart };
}

function invoke(
  ipc: ReturnType<typeof makeFakeIpcMain>,
  channel: string,
  ...args: unknown[]
): Promise<unknown> {
  const handler = ipc.handlers.get(channel);
  if (handler === undefined) throw new Error(`no handler for ${channel}`);
  return Promise.resolve(handler(MAIN_EVENT, ...args));
}

describe('YouTube IPC', () => {
  it('sends the renderer the channel title and the method, never a token, the code or the client secret', async () => {
    const fake = makeGoogleFake();
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      recordingControlPlane([]),
      memoryPendingRevokes(),
    );

    const pending = await invoke(ipc, CHANNEL.youtubeSignIn);
    await fake.auth.settled();
    const status = await invoke(ipc, CHANNEL.youtubeStatus);

    expect(pending).toMatchObject({ pending: true, connected: false });
    expect(status).toEqual({
      configured: true,
      connected: true,
      method: 'oauth',
      channelId: 'UCscoutTestChannel',
      channelTitle: 'Scout Test Channel',
      pending: false,
      error: null,
      clientConfigPath: CLIENT_FILE,
      source: 'file',
      scopes: [
        'https://www.googleapis.com/auth/youtube.readonly',
        'https://www.googleapis.com/auth/yt-analytics.readonly',
      ],
    });
    const payloads = JSON.stringify([pending, status]);
    for (const secret of ['ya29.', '1//refresh', AUTH_CODE, CLIENT_SECRET]) {
      expect(payloads).not.toContain(secret);
    }
  });

  it('keeps the error code across the IPC boundary when sign-in is not configured', async () => {
    const fake = makeGoogleFake();
    fake.files.delete(CLIENT_FILE);
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      recordingControlPlane([]),
      memoryPendingRevokes(),
    );

    const thrown = await invoke(ipc, CHANNEL.youtubeSignIn).catch((err: unknown) => err);

    expect(readIpcError(new Error((thrown as Error).message))).toEqual({
      code: 'youtube/not-configured',
      message:
        'Sign in with Google needs a Google OAuth client; use an API key instead, or add the client file.',
    });
  });

  it('accepts a pasted key from the renderer, never sends it back, and a rejected key error carries no key', async () => {
    const fake = makeGoogleFake();
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      recordingControlPlane([]),
      memoryPendingRevokes(),
    );

    const saved = await invoke(ipc, CHANNEL.youtubeSaveApiKey, { apiKey: API_KEY });
    const cleared = await invoke(ipc, CHANNEL.youtubeClearApiKey);
    fake.keyCheck = () => json({ error: { message: `bad key ${API_KEY}` } }, 400);
    const thrown = await invoke(ipc, CHANNEL.youtubeSaveApiKey, { apiKey: API_KEY }).catch(
      (err: unknown) => err,
    );

    expect(saved).toMatchObject({ connected: true, method: 'apiKey' });
    expect(cleared).toMatchObject({ connected: false, method: null });
    expect(readIpcError(new Error((thrown as Error).message)).code).toBe('youtube/key-rejected');
    expect(JSON.stringify([saved, cleared, (thrown as Error).message, ...fake.logs])).not.toContain(
      API_KEY,
    );
  });

  it('disconnect removes the token file, clears the session and restarts the running worker', async () => {
    const calls: string[] = [];
    const worker = {
      status: { state: 'running', restarts: 0 } as WorkerStatus,
      async stop() {
        calls.push('stop');
        return { exitCode: 0 };
      },
      async start() {
        calls.push('start');
        return { pid: 42 };
      },
    };
    let restart: Promise<void> = Promise.resolve();
    const fake = makeGoogleFake({
      onConnectionChanged: () => {
        restart = restartWorkerIfRunning(restartPath(worker), fake.deps.logger);
      },
    });
    await connect(fake);
    await restart;
    calls.length = 0;
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      recordingControlPlane([]),
      memoryPendingRevokes(),
    );

    const status = await invoke(ipc, CHANNEL.youtubeSignOut);
    await restart;

    expect(status).toMatchObject({ connected: false, channelTitle: null });
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(fake.vault.has('youtube.session')).toBe(false);
    expect(calls).toEqual(['stop', 'start']);
  });

  it('Disconnecting YouTube revokes its write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = await connectedWithRunningWorker(calls, 'oauth');
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      recordingControlPlane(calls),
      memoryPendingRevokes(),
      () => calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeSignOut);
    await restarted();

    expect(status).toMatchObject({ connected: false });
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it('a control plane that is down does not block the YouTube disconnect and is logged', async () => {
    const calls: string[] = [];
    const { fake, restarted } = await connectedWithRunningWorker(calls, 'oauth');
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      async () => ({
        setWriteGrant: async () => {
          throw new ControlPlaneUnavailableError();
        },
        deleteFeatureCredentials: async () => {
          throw new ControlPlaneUnavailableError();
        },
      }),
      memoryPendingRevokes(),
      () => calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeSignOut);
    await restarted();

    expect(status).toMatchObject({ connected: false, channelTitle: null });
    expect(fake.files.has(TOKEN_FILE)).toBe(false);
    expect(calls).toEqual(['stop', 'start']);
    expect(fake.logs).toContain(
      'warn youtube: write grant not revoked [{"code":"control-plane/unavailable"}]',
    );
    expect(JSON.stringify(fake.logs)).not.toContain('ya29.');
  });

  it('removing the API key that is the only YouTube credential revokes its write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = await connectedWithRunningWorker(calls, 'apiKey');
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      recordingControlPlane(calls),
      memoryPendingRevokes(),
      () => calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeClearApiKey);
    await restarted();

    expect(status).toMatchObject({ connected: false, method: null });
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it('removing the API key while Google sign-in stays connected keeps the write grant', async () => {
    const calls: string[] = [];
    const { fake, restarted } = await connectedWithRunningWorker(calls, 'oauth');
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      recordingControlPlane(calls),
      memoryPendingRevokes(),
      () => calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeClearApiKey);
    await restarted();

    expect(status).toMatchObject({ connected: true, method: 'oauth' });
    expect(calls).toEqual([]);
  });

  it('Signing in to YouTube revokes its write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = wiredToControlPlane(calls);

    await connect(fake);
    await restarted();

    expect((await fake.auth.status()).connected).toBe(true);
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it('Saving a YouTube API key revokes its write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = wiredToControlPlane(calls);
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      recordingControlPlane(calls),
      memoryPendingRevokes(),
      () => calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeSaveApiKey, { apiKey: API_KEY });
    await restarted();

    expect(status).toMatchObject({ connected: true, method: 'apiKey' });
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it('Google refusing the saved sign-in on refresh revokes the YouTube write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = wiredToControlPlane(calls);
    await connect(fake);
    await restarted();
    calls.length = 0;
    fake.tokenReplies.push(json({ error: 'invalid_grant' }, 400));

    fake.clock.now += ONE_HOUR_MS;
    await fake.auth.refreshIfDue();
    await restarted();

    expect((await fake.auth.status()).connected).toBe(false);
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it('a control plane that is down does not block the YouTube sign-in and is logged', async () => {
    const calls: string[] = [];
    const { fake, restarted } = wiredToControlPlane(calls, async () => ({
      setWriteGrant: async () => {
        throw new ControlPlaneUnavailableError();
      },
    }));

    await connect(fake);
    await restarted();

    expect(await fake.auth.status()).toMatchObject({
      connected: true,
      method: 'oauth',
      error: null,
    });
    expect(calls).toEqual(['stop', 'start']);
    expect(fake.logs).toContain(
      'warn youtube: write grant not revoked [{"code":"control-plane/unavailable"}]',
    );
  });

  it('clearing the API key when the YouTube status cannot be read still revokes its write grant and tells the renderer before the worker restarts', async () => {
    const calls: string[] = [];
    const { fake, restarted } = await connectedWithRunningWorker(calls, 'apiKey');
    const ipc = makeFakeIpcMain();
    const unreadableStatus = {
      signIn: () => fake.auth.signIn(),
      cancelSignIn: () => fake.auth.cancelSignIn(),
      signOut: () => fake.auth.signOut(),
      saveApiKey: (raw: string) => fake.auth.saveApiKey(raw),
      clearApiKey: () => fake.auth.clearApiKey(),
      status: async (): Promise<never> => {
        throw new Error('the vault could not be read');
      },
    };
    registerYouTubeHandlers(
      ipc,
      unreadableStatus,
      fake.deps.logger,
      recordingControlPlane(calls),
      memoryPendingRevokes(),
      () => calls.push('features changed'),
    );

    const status = await invoke(ipc, CHANNEL.youtubeClearApiKey);
    await restarted();

    expect(status).toMatchObject({ connected: false, method: null });
    expect(calls).toEqual(['grant youtube false false', 'features changed', 'stop', 'start']);
  });

  it("Disconnect asks the control plane to delete YouTube's credentials, so it purges the YouTube data, only once the sign-out removed the session", async () => {
    const calls: string[] = [];
    const { fake, restarted } = await connectedWithRunningWorker(calls, 'oauth');
    const sessionAtDelete: boolean[] = [];
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      async () => ({
        ...(await recordingControlPlane(calls)()),
        deleteFeatureCredentials: async (id: FeatureId) => {
          sessionAtDelete.push(fake.vault.has('youtube.session'));
          calls.push(`delete ${id}`);
          return {} as FeatureDto;
        },
      }),
      memoryPendingRevokes(),
    );

    await invoke(ipc, CHANNEL.youtubeSignOut);
    await restarted();

    expect(calls.filter((c) => c.startsWith('delete') || c.startsWith('grant'))).toEqual([
      'grant youtube false false',
      'delete youtube',
    ]);
    expect(sessionAtDelete).toEqual([false]);
  });

  it('a Disconnect that could not remove the token file keeps the YouTube data', async () => {
    const deletes: string[] = [];
    const { fake } = await connectedWithRunningWorker([], 'oauth');
    fake.deps.fs.rm = async () => {
      throw Object.assign(new Error('EBUSY'), { code: 'EBUSY' });
    };
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      recordingControlPlane([], deletes),
      memoryPendingRevokes(),
    );

    const refused = await invoke(ipc, CHANNEL.youtubeSignOut).then(
      () => null,
      (err: unknown) => readIpcError(err),
    );

    expect(refused?.code).toBe('youtube/token-file-not-removed');
    expect(deletes).toEqual([]);
  });

  it('removing the API key that is the only YouTube credential deletes the YouTube data; with Google sign-in staying connected it does not', async () => {
    const onlyKey: string[] = [];
    const keyed = await connectedWithRunningWorker([], 'apiKey');
    const keyIpc = makeFakeIpcMain();
    registerYouTubeHandlers(
      keyIpc,
      keyed.fake.auth,
      keyed.fake.deps.logger,
      recordingControlPlane([], onlyKey),
      memoryPendingRevokes(),
    );
    const besideOauth: string[] = [];
    const signedIn = await connectedWithRunningWorker([], 'oauth');
    const oauthIpc = makeFakeIpcMain();
    registerYouTubeHandlers(
      oauthIpc,
      signedIn.fake.auth,
      signedIn.fake.deps.logger,
      recordingControlPlane([], besideOauth),
      memoryPendingRevokes(),
    );

    await invoke(keyIpc, CHANNEL.youtubeClearApiKey);
    await invoke(oauthIpc, CHANNEL.youtubeClearApiKey);

    expect([onlyKey, besideOauth]).toEqual([['youtube'], []]);
  });

  it('a control plane that cannot delete the YouTube data is logged with its code and does not fail the Disconnect', async () => {
    const { fake } = await connectedWithRunningWorker([], 'oauth');
    const ipc = makeFakeIpcMain();
    registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      async () => ({
        setWriteGrant: async () => ({}) as FeatureDto,
        deleteFeatureCredentials: async () => {
          throw new ControlPlaneUnavailableError();
        },
      }),
      memoryPendingRevokes(),
    );

    const status = await invoke(ipc, CHANNEL.youtubeSignOut);

    expect(status).toMatchObject({ connected: false });
    expect(fake.logs).toContain(
      'warn youtube: data not deleted [{"code":"control-plane/unavailable"}]',
    );
  });

  it('Allow writes on YouTube with a read-only Google sign-in signs in again asking for the upload and force-ssl scopes, and only then grants writes', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    const grants: string[] = [];
    const grant = grantRecorder(fake, grants);
    fake.tokenReplies.push({
      access_token: 'ya29.write-access',
      expires_in: 3599,
      refresh_token: '1//refresh-write',
      scope: `${READ_SCOPES} ${WRITE_SCOPES}`,
    });

    await grant('youtube', true);

    expect(fake.authorizeUrls.map((u) => u.searchParams.get('scope'))).toEqual([
      READ_SCOPES,
      `${READ_SCOPES} ${WRITE_SCOPES}`,
    ]);
    expect(grants).toEqual(['youtube true with 4 scopes']);
  });

  it('Allow writes on YouTube when the owner grants only some scopes grants nothing and says which scope is missing', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    const grants: string[] = [];
    const grant = grantRecorder(fake, grants);
    fake.tokenReplies.push({
      access_token: 'ya29.write-access',
      expires_in: 3599,
      refresh_token: '1//refresh-write',
      scope: `${READ_SCOPES} https://www.googleapis.com/auth/youtube.force-ssl`,
    });

    const refused = await grant('youtube', true).then(
      () => null,
      (err: unknown) => readIpcError(err),
    );

    expect(refused).toEqual({
      code: 'youtube/scopes-missing',
      message:
        "Google did not grant youtube.upload, so writes stay off. Click Allow writes again and allow every permission on Google's page.",
    });
    expect(grants).toEqual([]);
  });

  it('Allow writes on YouTube whose grant the control plane cannot store leaves the Google sign-in with the read scopes only', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    const ipc = makeFakeIpcMain();
    const client = {
      async setWriteGrant(): Promise<FeatureDto> {
        throw new ControlPlaneUnavailableError('socket did not appear');
      },
    };
    registerFeaturesHandlers(
      ipc,
      async () => client as unknown as FeaturesClient,
      fake.deps.logger,
      { ...FROM_MAIN_WINDOW, ...youTubeWriteGrantHooks(fake.auth, async () => undefined) },
    );
    fake.tokenReplies.push({
      access_token: 'ya29.write-access',
      expires_in: 3599,
      refresh_token: '1//refresh-write',
      scope: `${READ_SCOPES} ${WRITE_SCOPES}`,
    });

    const refused = await invoke(ipc, CHANNEL.featureWriteGrant, {
      id: 'youtube',
      granted: true,
      confirmWrites: true,
    }).then(
      () => null,
      (err: unknown) => readIpcError(err),
    );
    await fake.auth.settled();

    expect(refused?.code).toBe('control-plane/unavailable');
    expect((await fake.auth.status()).scopes).toEqual(READ_SCOPES.split(' '));
  });

  it('turning YouTube on with its writes confirmed, which the control plane refuses, leaves the Google sign-in with the read scopes only', async () => {
    const fake = makeGoogleFake();
    await connect(fake);
    const ipc = makeFakeIpcMain();
    const client = {
      async enableFeature(): Promise<FeatureDto> {
        throw new ControlPlaneUnavailableError('socket did not appear');
      },
    };
    registerFeaturesHandlers(
      ipc,
      async () => client as unknown as FeaturesClient,
      fake.deps.logger,
      { ...FROM_MAIN_WINDOW, ...youTubeWriteGrantHooks(fake.auth, async () => undefined) },
    );
    fake.tokenReplies.push({
      access_token: 'ya29.write-access',
      expires_in: 3599,
      refresh_token: '1//refresh-write',
      scope: `${READ_SCOPES} ${WRITE_SCOPES}`,
    });

    const refused = await invoke(ipc, CHANNEL.featureEnable, {
      id: 'youtube',
      confirmWrites: true,
    }).then(
      () => null,
      (err: unknown) => readIpcError(err),
    );
    await fake.auth.settled();

    expect(refused?.code).toBe('control-plane/unavailable');
    expect((await fake.auth.status()).scopes).toEqual(READ_SCOPES.split(' '));
  });

  it('Revoke writes on YouTube revokes the grant, then the Google token, and starts a sign-in with the read scopes only', async () => {
    const fake = makeGoogleFake();
    fake.tokenReplies.push({
      access_token: 'ya29.write-access',
      expires_in: 3599,
      refresh_token: '1//refresh-write',
      scope: `${READ_SCOPES} ${WRITE_SCOPES}`,
    });
    await connect(fake);
    fake.ops.length = 0;
    const grants: string[] = [];
    const revoke = grantRecorder(fake, grants);

    await revoke('youtube', false);
    await fake.auth.settled();

    expect(grants).toEqual(['youtube false with 4 scopes']);
    expect(fake.ops[0]).toBe('POST https://oauth2.googleapis.com/revoke');
    expect(fake.authorizeUrls.map((u) => u.searchParams.get('scope'))).toEqual([
      READ_SCOPES,
      READ_SCOPES,
    ]);
    expect((await fake.auth.status()).scopes).toEqual(READ_SCOPES.split(' '));
  });

  describe('Revoke writes that leaves the owner signed out', () => {
    async function writeScoped(): Promise<GoogleFake> {
      const fake = makeGoogleFake();
      fake.tokenReplies.push({
        access_token: 'ya29.write-access',
        expires_in: 3599,
        refresh_token: '1//refresh-write',
        scope: `${READ_SCOPES} ${WRITE_SCOPES}`,
      });
      await connect(fake);
      return fake;
    }

    async function eventually(condition: () => boolean): Promise<boolean> {
      const deadline = Date.now() + 1000;
      while (!condition()) {
        if (Date.now() > deadline) return false;
        await new Promise((r) => setTimeout(r, 5));
      }
      return true;
    }

    it('deletes the YouTube data when the owner cancels the read-only sign-in', async () => {
      const fake = await writeScoped();
      fake.browserCallsBack = false;
      const deletes: string[] = [];
      const revoke = grantRecorder(fake, [], deletes);

      await revoke('youtube', false);
      const beforeCancel = [...deletes];
      fake.auth.cancelSignIn();
      await fake.auth.settled();

      expect(beforeCancel).toEqual([]);
      expect(await eventually(() => deletes.length > 0)).toBe(true);
      expect(deletes).toEqual(['youtube']);
      expect((await fake.auth.status()).connected).toBe(false);
    });

    it('deletes the YouTube data when the owner denies the read-only sign-in on Google', async () => {
      const fake = await writeScoped();
      fake.callbackParams = (authorize) => ({
        error: 'access_denied',
        state: authorize.searchParams.get('state') ?? '',
      });
      const deletes: string[] = [];
      const revoke = grantRecorder(fake, [], deletes);

      await revoke('youtube', false);
      await fake.auth.settled();

      expect(await eventually(() => deletes.length > 0)).toBe(true);
      expect(deletes).toEqual(['youtube']);
    });

    it('deletes the YouTube data when the browser for the read-only sign-in cannot be opened', async () => {
      const fake = await writeScoped();
      fake.browserFails = true;
      const deletes: string[] = [];
      const revoke = grantRecorder(fake, [], deletes);

      await revoke('youtube', false);

      expect(deletes).toEqual(['youtube']);
      expect((await fake.auth.status()).connected).toBe(false);
    });

    it('keeps the YouTube data when the read-only sign-in succeeds', async () => {
      const fake = await writeScoped();
      const deletes: string[] = [];
      const revoke = grantRecorder(fake, [], deletes);

      await revoke('youtube', false);
      await fake.auth.settled();
      await new Promise((r) => setTimeout(r, 30));

      expect((await fake.auth.status()).connected).toBe(true);
      expect(deletes).toEqual([]);
    });
  });

  it('a write grant for another feature never opens a Google sign-in', async () => {
    const fake = makeGoogleFake();
    const grants: string[] = [];
    const grant = grantRecorder(fake, grants);

    await grant('github', true);
    await grant('github', false);

    expect(fake.authorizeUrls).toEqual([]);
    expect(grants).toEqual(['github true with 0 scopes', 'github false with 0 scopes']);
  });

  it('removes all six handlers on dispose', () => {
    const fake = makeGoogleFake();
    const ipc = makeFakeIpcMain();
    const dispose = registerYouTubeHandlers(
      ipc,
      fake.auth,
      fake.deps.logger,
      recordingControlPlane([]),
      memoryPendingRevokes(),
    );
    const registered = [...ipc.handlers.keys()].sort();

    dispose();

    expect(registered).toEqual(
      [
        CHANNEL.youtubeCancelSignIn,
        CHANNEL.youtubeClearApiKey,
        CHANNEL.youtubeSaveApiKey,
        CHANNEL.youtubeSignIn,
        CHANNEL.youtubeSignOut,
        CHANNEL.youtubeStatus,
      ].sort(),
    );
    expect(ipc.handlers.size).toBe(0);
  });
});
