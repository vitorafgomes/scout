import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { FeatureDto, YouTubeStatus } from '@scout/shared';

import { FeaturesStore } from '../core/features.store';
import { ScoutBridgeService } from '../core/scout-bridge';

import { FeaturesComponent } from './features.component';

const CLIENT_FILE = '/home/owner/.config/Scout/google-oauth-client.json';

const youtube: FeatureDto = {
  id: 'youtube',
  displayName: 'YouTube',
  description: '',
  requiresCredentials: false,
  hasCredentials: false,
  enabled: false,
  status: 'disabled',
  lastError: null,
  credentialFields: [],
  settingsFields: [],
  settings: {},
  scopeMatrix: null,
  setupSteps: null,
  writeClass: 'none',
  granted: false,
  grantedAt: null,
};

const notConnected: YouTubeStatus = {
  configured: true,
  connected: false,
  method: null,
  channelId: null,
  channelTitle: null,
  pending: false,
  error: null,
  clientConfigPath: CLIENT_FILE,
  source: 'file',
  scopes: [],
};

const pending: YouTubeStatus = { ...notConnected, pending: true };

const signedIn: YouTubeStatus = {
  ...notConnected,
  connected: true,
  method: 'oauth',
  channelId: 'UCscoutTestChannel',
  channelTitle: 'Scout Test Channel',
};

const withKey: YouTubeStatus = { ...notConnected, connected: true, method: 'apiKey' };

function makeBridge(first: YouTubeStatus) {
  let current = first;
  return {
    accessGet: vi.fn(async () => ({ userIds: ['111'] })),
    setStatus(next: YouTubeStatus): void {
      current = next;
    },
    onFeaturesChanged: vi.fn(() => () => undefined),
    featuresList: vi.fn(async () => ({ features: [youtube] })),
    youtubeStatus: vi.fn(async () => current),
    youtubeSignIn: vi.fn(async () => {
      current = pending;
      return pending;
    }),
    youtubeCancelSignIn: vi.fn(async () => {
      current = notConnected;
    }),
    youtubeSignOut: vi.fn(async () => {
      current = notConnected;
      return notConnected;
    }),
    youtubeSaveApiKey: vi.fn(async (_req: { apiKey: string }) => {
      current = withKey;
      return withKey;
    }),
    youtubeClearApiKey: vi.fn(async () => {
      current = notConnected;
      return notConnected;
    }),
    appOpenExternal: vi.fn(async () => undefined),
    operatorOpenWindow: vi.fn(async () => undefined),
  };
}

async function render(
  bridge: ReturnType<typeof makeBridge>,
): Promise<ComponentFixture<FeaturesComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      FeaturesStore,
    ],
  });
  const fixture = TestBed.createComponent(FeaturesComponent);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<FeaturesComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el(fixture: ComponentFixture<FeaturesComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function text(fixture: ComponentFixture<FeaturesComponent>, test: string): string | undefined {
  return el(fixture, test)?.textContent?.replace(/\s+/g, ' ').trim();
}

describe('YouTube card', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('Sign in with Google starts the browser flow and shows the connected channel', async () => {
    const bridge = makeBridge(notConnected);
    const fixture = await render(bridge);
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });

    el(fixture, 'youtube-sign-in')!.click();
    await settle(fixture);
    const waiting = text(fixture, 'youtube-pending');
    bridge.setStatus(signedIn);
    await vi.advanceTimersByTimeAsync(3000);
    await settle(fixture);

    expect(bridge.youtubeSignIn).toHaveBeenCalledTimes(1);
    expect(waiting).toBe('Waiting for the browser...');
    expect(text(fixture, 'youtube-connected')).toBe('Connected to Scout Test Channel');
    expect(text(fixture, 'youtube-method')).toBe('Signed in with Google');
    expect(el(fixture, 'youtube-pending')).toBeNull();
    expect(text(fixture, 'badge-youtube')).toBe('Connected');
  });

  it('the connected state links to YouTube Studio and to the channel', async () => {
    const bridge = makeBridge(signedIn);
    const fixture = await render(bridge);

    const studio = text(fixture, 'youtube-open-studio');
    const channel = text(fixture, 'youtube-open-channel');
    el(fixture, 'youtube-open-studio')?.click();
    el(fixture, 'youtube-open-channel')?.click();
    await settle(fixture);

    expect(studio).toBe('Open YouTube Studio');
    expect(channel).toBe('Open channel');
    expect(bridge.appOpenExternal.mock.calls).toEqual([
      [{ url: 'https://studio.youtube.com/channel/UCscoutTestChannel' }],
      [{ url: 'https://www.youtube.com/channel/UCscoutTestChannel' }],
    ]);
  });

  it('without a channel id the card opens YouTube Studio itself and offers no channel link', async () => {
    const bridge = makeBridge(withKey);
    const fixture = await render(bridge);

    el(fixture, 'youtube-open-studio')?.click();
    await settle(fixture);

    expect(el(fixture, 'youtube-open-channel')).toBeNull();
    expect(bridge.appOpenExternal.mock.calls).toEqual([[{ url: 'https://studio.youtube.com/' }]]);
  });

  it('Cancel stops a sign-in that waits for the browser and offers Sign in with Google again', async () => {
    const bridge = makeBridge(pending);
    const fixture = await render(bridge);

    el(fixture, 'youtube-cancel')!.click();
    await settle(fixture);

    expect(bridge.youtubeCancelSignIn).toHaveBeenCalledTimes(1);
    expect(el(fixture, 'youtube-pending')).toBeNull();
    expect(text(fixture, 'youtube-sign-in')).toBe('Sign in with Google');
  });

  it('explains where the OAuth client file goes when sign-in is not configured', async () => {
    const fixture = await render(makeBridge({ ...notConnected, configured: false }));

    expect(text(fixture, 'youtube-not-configured')).toBe(
      `Sign in needs a Google OAuth client: enable the YouTube Data API, create an OAuth client of type Desktop app, then download its JSON as it is to ${CLIENT_FILE} (or write {"client_id": ..., "client_secret": ...} there). Or use an API key below.`,
    );
    expect((el(fixture, 'youtube-sign-in') as HTMLButtonElement).disabled).toBe(true);
    expect((el(fixture, 'youtube-use-key') as HTMLButtonElement).disabled).toBe(false);
  });

  it('hides the client file hint and enables Sign in with Google when Scout ships its own client', async () => {
    const fixture = await render(makeBridge({ ...notConnected, source: 'embedded' }));

    expect(el(fixture, 'youtube-not-configured')).toBeNull();
    expect(el(fixture, 'youtube-open-credentials')).toBeNull();
    expect((el(fixture, 'youtube-sign-in') as HTMLButtonElement).disabled).toBe(false);
  });

  it('the not-configured state opens the Google Cloud Console pages for credentials and for enabling the API', async () => {
    const bridge = makeBridge({ ...notConnected, configured: false });
    const fixture = await render(bridge);

    const enable = text(fixture, 'youtube-enable-api');
    const credentials = text(fixture, 'youtube-open-credentials');
    el(fixture, 'youtube-enable-api')?.click();
    el(fixture, 'youtube-open-credentials')?.click();
    await settle(fixture);

    expect(enable).toBe('Enable the YouTube Data API');
    expect(credentials).toBe('Open Google Cloud Console credentials');
    expect(bridge.appOpenExternal.mock.calls).toEqual([
      [{ url: 'https://console.cloud.google.com/apis/library/youtube.googleapis.com' }],
      [{ url: 'https://console.cloud.google.com/apis/credentials' }],
    ]);
  });

  it('says the client must be a Desktop app when the file holds a Web application client', async () => {
    const hint =
      'The Google OAuth client in the file is a Web application; Sign in with Google needs an OAuth client of type Desktop app.';
    const fixture = await render(makeBridge({ ...notConnected, configured: false, error: hint }));

    expect(el(fixture, 'youtube-not-configured')).not.toBeNull();
    expect(text(fixture, 'youtube-error')).toBe(hint);
    expect((el(fixture, 'youtube-sign-in') as HTMLButtonElement).disabled).toBe(true);
  });

  it('Use an API key saves the pasted key and shows the card connected with the key', async () => {
    const bridge = makeBridge({ ...notConnected, configured: false });
    const fixture = await render(bridge);

    el(fixture, 'youtube-use-key')!.click();
    await settle(fixture);
    const input = el(fixture, 'youtube-key-input') as HTMLInputElement;
    input.value = 'AIzaSyRendererKey';
    el(fixture, 'youtube-key-save')!.click();
    await settle(fixture);

    expect(bridge.youtubeSaveApiKey).toHaveBeenCalledWith({ apiKey: 'AIzaSyRendererKey' });
    expect(input.value).toBe('');
    expect(text(fixture, 'youtube-connected')).toBe('Connected with an API key');
    expect(text(fixture, 'youtube-method')).toBe('Public data only.');
    expect(text(fixture, 'badge-youtube')).toBe('Connected');
  });

  it('links to the Google Cloud Console page that creates a key', async () => {
    const bridge = makeBridge(notConnected);
    const fixture = await render(bridge);

    el(fixture, 'youtube-use-key')!.click();
    await settle(fixture);
    const link = text(fixture, 'youtube-create-key');
    el(fixture, 'youtube-create-key')!.click();
    await settle(fixture);

    expect(link).toBe('Create a key in Google Cloud Console');
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({
      url: 'https://console.cloud.google.com/apis/credentials',
    });
  });

  it('Disconnect signs out a Google sign-in and offers Sign in with Google again', async () => {
    const bridge = makeBridge(signedIn);
    const fixture = await render(bridge);

    el(fixture, 'youtube-disconnect')!.click();
    await settle(fixture);

    expect(bridge.youtubeSignOut).toHaveBeenCalledTimes(1);
    expect(bridge.youtubeClearApiKey).not.toHaveBeenCalled();
    expect(el(fixture, 'youtube-connected')).toBeNull();
    expect(text(fixture, 'badge-youtube')).toBe('Not connected');
  });

  it('Disconnect on a pasted key removes the key', async () => {
    const bridge = makeBridge(withKey);
    const fixture = await render(bridge);

    el(fixture, 'youtube-disconnect')!.click();
    await settle(fixture);

    expect(bridge.youtubeClearApiKey).toHaveBeenCalledTimes(1);
    expect(bridge.youtubeSignOut).not.toHaveBeenCalled();
    expect(text(fixture, 'youtube-sign-in')).toBe('Sign in with Google');
  });

  it('shows why the last sign-in failed next to Sign in with Google', async () => {
    const fixture = await render(
      makeBridge({
        ...notConnected,
        error: 'Google could not finish the sign-in (HTTP 503). Sign in again in a moment.',
      }),
    );

    expect(text(fixture, 'youtube-error')).toBe(
      'Google could not finish the sign-in (HTTP 503). Sign in again in a moment.',
    );
  });

  it('shows a refused sign-in from main as an error under the card', async () => {
    const bridge = makeBridge(notConnected);
    bridge.youtubeSignIn.mockImplementationOnce(async () => {
      throw new Error(
        "Error invoking remote method 'scout:youtube.signIn': Error: [scout:youtube/browser-failed] Could not open the browser to sign in.",
      );
    });
    const fixture = await render(bridge);

    el(fixture, 'youtube-sign-in')!.click();
    await settle(fixture);

    expect(text(fixture, 'youtube-action-error')).toBe('Could not open the browser to sign in.');
    expect(el(fixture, 'youtube-pending')).toBeNull();
  });

  it('Open YouTube operator asks main for the operator window once YouTube is connected', async () => {
    const bridge = makeBridge(signedIn);
    const fixture = await render(bridge);

    const button = el(fixture, 'youtube-open-operator') as HTMLButtonElement;
    button.click();
    await settle(fixture);

    expect(button.textContent?.trim()).toBe('Open YouTube operator');
    expect(button.disabled).toBe(false);
    expect(bridge.operatorOpenWindow).toHaveBeenCalledTimes(1);
  });

  it('Open YouTube operator is disabled and opens nothing while YouTube is not connected', async () => {
    const bridge = makeBridge(notConnected);
    const fixture = await render(bridge);

    const button = el(fixture, 'youtube-open-operator') as HTMLButtonElement;
    button.click();
    await settle(fixture);

    expect(button.disabled).toBe(true);
    expect(bridge.operatorOpenWindow).not.toHaveBeenCalled();
  });

  it('shows why the operator window did not open under the card', async () => {
    const bridge = makeBridge(signedIn);
    bridge.operatorOpenWindow.mockImplementationOnce(async () => {
      throw new Error(
        "Error invoking remote method 'scout:operator.window': Error: [scout:operator/window-failed] The operator window could not open.",
      );
    });
    const fixture = await render(bridge);

    el(fixture, 'youtube-open-operator')!.click();
    await settle(fixture);

    expect(text(fixture, 'youtube-action-error')).toBe('The operator window could not open.');
  });

  describe('polling', () => {
    it('stops asking for the status once the card is gone', async () => {
      const bridge = makeBridge(pending);
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      const fixture = await render(bridge);

      await vi.advanceTimersByTimeAsync(3000);
      const callsWhenDestroyed = bridge.youtubeStatus.mock.calls.length;
      fixture.destroy();
      await vi.advanceTimersByTimeAsync(30_000);

      expect(callsWhenDestroyed).toBe(2);
      expect(bridge.youtubeStatus).toHaveBeenCalledTimes(2);
    });

    it('stops polling once the sign-in is no longer waiting for the browser', async () => {
      const bridge = makeBridge(pending);
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      await render(bridge);

      bridge.setStatus(signedIn);
      await vi.advanceTimersByTimeAsync(3000);
      await vi.advanceTimersByTimeAsync(30_000);

      expect(bridge.youtubeStatus).toHaveBeenCalledTimes(2);
    });
  });

  describe('granted scopes', () => {
    const read = [
      'https://www.googleapis.com/auth/youtube.readonly',
      'https://www.googleapis.com/auth/yt-analytics.readonly',
    ];
    const write = [
      'https://www.googleapis.com/auth/youtube.upload',
      'https://www.googleapis.com/auth/youtube.force-ssl',
    ];

    it('a Google sign-in shows the scopes Google granted by their short names; an API key shows none', async () => {
      const fixture = await render(makeBridge({ ...signedIn, scopes: read }));
      const shown = text(fixture, 'youtube-scopes');
      TestBed.resetTestingModule();
      const keyed = await render(makeBridge(withKey));

      expect(shown).toBe('Google access: youtube.readonly, yt-analytics.readonly');
      expect(el(keyed, 'youtube-scopes')).toBeNull();
    });

    it('the card reads its status again when main announces a feature change, so the write scopes show once writes are allowed', async () => {
      const bridge = makeBridge({ ...signedIn, scopes: read });
      const fixture = await render(bridge);

      bridge.setStatus({ ...signedIn, scopes: [...read, ...write] });
      for (const [listener] of bridge.onFeaturesChanged.mock.calls as unknown as [() => void][]) {
        listener();
      }
      await settle(fixture);

      expect(text(fixture, 'youtube-scopes')).toBe(
        'Google access: youtube.readonly, yt-analytics.readonly, youtube.upload, youtube.force-ssl',
      );
    });

    it('a sign-in waiting for the browser while still connected, as when writes are allowed or revoked, shows Waiting for the browser with Cancel', async () => {
      const bridge = makeBridge({ ...signedIn, scopes: read, pending: true });
      const fixture = await render(bridge);
      const waiting = [
        text(fixture, 'youtube-pending-connected'),
        text(fixture, 'youtube-connected'),
      ];

      bridge.setStatus({ ...signedIn, scopes: read });
      el(fixture, 'youtube-cancel-connected')!.click();
      await settle(fixture);

      expect(waiting).toEqual(['Waiting for the browser...', 'Connected to Scout Test Channel']);
      expect(bridge.youtubeCancelSignIn).toHaveBeenCalledTimes(1);
      expect(el(fixture, 'youtube-pending-connected')).toBeNull();
    });
  });
});
