import { provideRouter } from '@angular/router';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { InteractivityChecker } from '@angular/cdk/a11y';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  encodeIpcErrorMessage,
  INSECURE_STORE_MESSAGE,
  ScoutError,
  serializeError,
  VaultUnavailableError,
  type FeatureDto,
  type YouTubeStatus,
} from '@scout/shared';

import { FeaturesStore } from '../core/features.store';
import { LAID_OUT } from '../core/laid-out.testing';
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
      provideRouter([]),
      { provide: InteractivityChecker, useValue: LAID_OUT },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      FeaturesStore,
    ],
  });
  const fixture = TestBed.createComponent(FeaturesComponent);
  await settle(fixture);
  card(fixture, 'details-youtube')!.click();
  fixture.detectChanges();
  if (el(fixture, 'panel-youtube')?.hidden !== false)
    throw new Error('Details did not open the youtube panel');
  return fixture;
}

async function settle(fixture: ComponentFixture<FeaturesComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

async function answer(
  fixture: ComponentFixture<FeaturesComponent>,
  choice: 'confirm-ok' | 'confirm-cancel',
): Promise<{ title: string | null; focused: string | null }> {
  await settle(fixture);
  const title = document.querySelector('sc-confirm-dialog h2')?.textContent?.trim() ?? null;
  const body = document.querySelector('sc-confirm-dialog .body')?.textContent?.trim() ?? null;
  const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test') ?? null;
  const button = document.querySelector<HTMLElement>(`[data-test="${choice}"]`);
  if (button === null) throw new Error('the confirmation dialog did not open');
  button.click();
  await TestBed.inject(ApplicationRef).whenStable();
  await settle(fixture);
  await settle(fixture);
  return { title: title === null ? null : `${title} | ${body}`, focused };
}

function el(fixture: ComponentFixture<FeaturesComponent>, test: string): HTMLElement | null {
  const panel = fixture.nativeElement.querySelector(
    '[data-test="feature-panel"]:not([hidden])',
  ) as HTMLElement | null;
  return panel?.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function card(fixture: ComponentFixture<FeaturesComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(
    `[data-test="features-grid"] [data-test="${test}"]`,
  ) as HTMLElement | null;
}

function cardText(fixture: ComponentFixture<FeaturesComponent>, test: string): string | undefined {
  return card(fixture, test)?.textContent?.replace(/\s+/g, ' ').trim();
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
    expect(cardText(fixture, 'badge-youtube')).toBe('Connected');
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

  it('Add an API key saves the pasted key through the Connect YouTube dialog and shows the card connected with the key', async () => {
    const bridge = makeBridge({ ...notConnected, configured: false });
    const fixture = await render(bridge);

    el(fixture, 'youtube-use-key')!.click();
    await settle(fixture);
    const title = text(fixture, 'youtube-key-title');
    const input = el(fixture, 'youtube-key-input') as HTMLInputElement;
    input.value = 'AIzaSyRendererKey';
    input.dispatchEvent(new Event('input'));
    el(fixture, 'youtube-key-save')!.click();
    await settle(fixture);
    await settle(fixture);

    expect(title).toBe('Connect YouTube');
    expect(bridge.youtubeSaveApiKey).toHaveBeenCalledWith({ apiKey: 'AIzaSyRendererKey' });
    expect(el(fixture, 'youtube-key-dialog')).toBeNull();
    expect(text(fixture, 'youtube-connected')).toBe('Connected with an API key');
    expect(text(fixture, 'youtube-method')).toBe('Public data only.');
    expect(cardText(fixture, 'badge-youtube')).toBe('Connected');
  });

  it('the Connect YouTube dialog draws no link, as the artboard does, so Add an API key opens no page in the browser', async () => {
    const bridge = makeBridge(notConnected);
    const fixture = await render(bridge);

    el(fixture, 'youtube-use-key')!.click();
    await settle(fixture);

    expect({
      dialog: el(fixture, 'youtube-key-dialog') !== null,
      links: el(fixture, 'youtube-key-dialog')?.querySelectorAll('a').length,
      opened: bridge.appOpenExternal.mock.calls,
    }).toEqual({ dialog: true, links: 0, opened: [] });
  });

  it('Disconnect signs out a Google sign-in and offers Sign in with Google again', async () => {
    const bridge = makeBridge(signedIn);
    const fixture = await render(bridge);

    el(fixture, 'youtube-disconnect')!.click();
    const asked = await answer(fixture, 'confirm-ok');

    expect(asked).toEqual({
      title:
        'Disconnect YouTube? | Scout signs out of the channel on this computer and the YouTube operator leaves the menu. Drafts already on YouTube stay there.',
      focused: 'confirm-cancel',
    });

    expect(bridge.youtubeSignOut).toHaveBeenCalledTimes(1);
    expect(bridge.youtubeClearApiKey).not.toHaveBeenCalled();
    expect(el(fixture, 'youtube-connected')).toBeNull();
    expect(cardText(fixture, 'badge-youtube')).toBe('Not connected');
  });

  it('Disconnect on a pasted key removes the key', async () => {
    const bridge = makeBridge(withKey);
    const fixture = await render(bridge);

    el(fixture, 'youtube-disconnect')!.click();
    const asked = await answer(fixture, 'confirm-ok');

    expect(asked.title).toBe(
      'Disconnect YouTube? | Scout removes the YouTube API key from this computer and the YouTube operator leaves the menu. Drafts already on YouTube stay there.',
    );

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

  it.each([
    ['not connected', notConnected],
    ['waiting for the browser', pending],
  ] as const)(
    'Open YouTube operator is not drawn while YouTube is %s, so nothing can open it',
    async (_state, status) => {
      const bridge = makeBridge(status);
      const fixture = await render(bridge);

      expect(el(fixture, 'youtube-open-operator')).toBeNull();
      expect(bridge.operatorOpenWindow).not.toHaveBeenCalled();
    },
  );

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

describe('YouTube card: Disconnect asks first', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('Cancel on Disconnect keeps YouTube connected and neither signs out nor removes the key', async () => {
    const bridge = makeBridge(signedIn);
    const fixture = await render(bridge);

    el(fixture, 'youtube-disconnect')!.click();
    const asked = await answer(fixture, 'confirm-cancel');

    expect(asked.focused).toBe('confirm-cancel');
    expect(bridge.youtubeSignOut).not.toHaveBeenCalled();
    expect(bridge.youtubeClearApiKey).not.toHaveBeenCalled();
    expect(el(fixture, 'youtube-connected')).not.toBeNull();
  });
});

describe('YouTube card: Add an API key opens the Connect YouTube dialog', () => {
  const KEY = 'AIzaSyInventedRendererKey00';
  const SIGN_IN_FAILED =
    'Google could not finish the sign-in (HTTP 503). Sign in again in a moment.';

  type State =
    | 'not connected'
    | 'connected with Google'
    | 'connected with an API key'
    | 'waiting for the browser'
    | 'error';
  type Action = 'Add an API key' | 'Save' | 'Cancel' | 'Esc' | 'Disconnect';

  const STATES: Readonly<Record<State, YouTubeStatus>> = {
    'not connected': notConnected,
    'connected with Google': signedIn,
    'connected with an API key': withKey,
    'waiting for the browser': pending,
    error: { ...notConnected, error: SIGN_IN_FAILED },
  };

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  function notices(): string[] {
    return Array.from(document.querySelectorAll('.cdk-overlay-container .sc-notice'), (n) => {
      const label = n.querySelector('.mat-mdc-snack-bar-label [aria-hidden="true"]') ?? n;
      return label.textContent?.replace(/\s+/g, ' ').trim() ?? '';
    });
  }

  function focused(): string | null {
    return (document.activeElement as HTMLElement | null)?.getAttribute('data-test') ?? null;
  }

  function keyHeld(): boolean {
    return Array.from(document.querySelectorAll('input')).some((input) => input.value === KEY);
  }

  async function openDialog(fixture: ComponentFixture<FeaturesComponent>): Promise<boolean> {
    const button = el(fixture, 'youtube-use-key');
    if (button === null) return false;
    button.click();
    await settle(fixture);
    return el(fixture, 'youtube-key-dialog') !== null;
  }

  async function typeKey(fixture: ComponentFixture<FeaturesComponent>): Promise<void> {
    const input = el(fixture, 'youtube-key-input') as HTMLInputElement;
    input.value = KEY;
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  async function act(
    fixture: ComponentFixture<FeaturesComponent>,
    action: Action,
  ): Promise<boolean> {
    if (action === 'Disconnect') {
      const button = el(fixture, 'youtube-disconnect');
      if (button === null) return false;
      button.click();
      await answer(fixture, 'confirm-ok');
      return true;
    }
    if (!(await openDialog(fixture))) return false;
    if (action === 'Add an API key') return true;
    await typeKey(fixture);
    if (action === 'Save') el(fixture, 'youtube-key-save')!.click();
    if (action === 'Cancel') el(fixture, 'youtube-key-cancel')!.click();
    if (action === 'Esc')
      el(fixture, 'youtube-key-input')!.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
      );
    await settle(fixture);
    await settle(fixture);
    return true;
  }

  const untouched = (connected: string | null, badge: string) => ({
    available: false,
    dialog: false,
    focused: 'feature-panel-close',
    connected,
    badge,
    saved: [],
    signedOut: 0,
    cleared: 0,
    toasts: [],
    keyHeld: false,
  });
  const notConnectedRows = (state: State) =>
    [
      {
        state,
        action: 'Add an API key',
        after: {
          ...untouched(null, 'Not connected'),
          available: true,
          dialog: true,
          focused: 'youtube-key-cancel',
        },
      },
      {
        state,
        action: 'Save',
        after: {
          ...untouched('Connected with an API key', 'Connected'),
          available: true,
          saved: [[{ apiKey: KEY }]],
          toasts: ['YouTube connected with an API key'],
        },
      },
      {
        state,
        action: 'Cancel',
        after: { ...untouched(null, 'Not connected'), available: true, focused: 'youtube-use-key' },
      },
      {
        state,
        action: 'Esc',
        after: { ...untouched(null, 'Not connected'), available: true, focused: 'youtube-use-key' },
      },
      { state, action: 'Disconnect', after: untouched(null, 'Not connected') },
    ] as const;
  const noDialogRows = (state: State, connected: string | null, badge: string) =>
    (['Add an API key', 'Save', 'Cancel', 'Esc'] as const).map((action) => ({
      state,
      action,
      after: untouched(connected, badge),
    }));

  const rows: readonly {
    state: State;
    action: Action;
    after: Readonly<Record<string, unknown>>;
  }[] = [
    ...notConnectedRows('not connected'),
    ...notConnectedRows('error'),
    ...noDialogRows('connected with Google', 'Connected to Scout Test Channel', 'Connected'),
    {
      state: 'connected with Google',
      action: 'Disconnect',
      after: {
        ...untouched(null, 'Not connected'),
        available: true,
        signedOut: 1,
        toasts: ['YouTube disconnected'],
      },
    },
    ...noDialogRows('connected with an API key', 'Connected with an API key', 'Connected'),
    {
      state: 'connected with an API key',
      action: 'Disconnect',
      after: {
        ...untouched(null, 'Not connected'),
        available: true,
        cleared: 1,
        toasts: ['YouTube disconnected'],
      },
    },
    ...noDialogRows('waiting for the browser', null, 'Not connected'),
    {
      state: 'waiting for the browser',
      action: 'Disconnect',
      after: untouched(null, 'Not connected'),
    },
  ];

  it.each(rows)(
    'state $state + $action: the dialog, the focus, what the card says, what was sent to main and the toast',
    async ({ state, action, after }) => {
      const bridge = makeBridge(STATES[state]);
      const fixture = await render(bridge);
      await settle(fixture);

      const available = await act(fixture, action);

      expect({
        available,
        dialog: el(fixture, 'youtube-key-dialog') !== null,
        focused: focused(),
        connected: text(fixture, 'youtube-connected') ?? null,
        badge: cardText(fixture, 'badge-youtube'),
        saved: bridge.youtubeSaveApiKey.mock.calls,
        signedOut: bridge.youtubeSignOut.mock.calls.length,
        cleared: bridge.youtubeClearApiKey.mock.calls.length,
        toasts: notices(),
        keyHeld: keyHeld(),
      }).toEqual(after);
    },
  );

  const ipcFailure = (err: ScoutError): Error =>
    new Error(
      `Error invoking remote method 'scout:youtube.saveApiKey': Error: ${encodeIpcErrorMessage(serializeError(err))}`,
    );

  const FAILURES = [
    {
      failure: 'Google refuses the key',
      error: new ScoutError(
        'youtube/key-rejected',
        'Google does not recognise this API key. Copy it again from Google Cloud Console credentials.',
      ),
    },
    {
      failure: 'YouTube does not answer',
      error: new ScoutError(
        'youtube/network-error',
        'Could not reach YouTube to check the API key.',
      ),
    },
    {
      failure: 'YouTube answers 503',
      error: new ScoutError(
        'youtube/request-failed',
        'YouTube answered HTTP 503 when Scout checked the API key.',
      ),
    },
    {
      failure: 'the key cannot be stored',
      error: new VaultUnavailableError(INSECURE_STORE_MESSAGE),
    },
  ] as const;

  it.each(FAILURES)(
    "Save when $failure: the dialog stays open with main's sentence and never the key, the field emptied and focused for a retry, Save back on, the card still not connected",
    async ({ error }) => {
      const bridge = makeBridge(notConnected);
      bridge.youtubeSaveApiKey.mockImplementationOnce(async () => {
        throw ipcFailure(error);
      });
      const fixture = await render(bridge);

      await act(fixture, 'Save');

      expect({
        dialog: el(fixture, 'youtube-key-dialog') !== null,
        alert: text(fixture, 'youtube-key-alert') ?? null,
        field: (el(fixture, 'youtube-key-input') as HTMLInputElement | null)?.value ?? null,
        focused: focused(),
        save: [
          text(fixture, 'youtube-key-save') ?? null,
          (el(fixture, 'youtube-key-save') as HTMLButtonElement | null)?.disabled ?? null,
        ],
        connected: text(fixture, 'youtube-connected') ?? null,
        actionError: text(fixture, 'youtube-action-error') ?? null,
        toasts: notices(),
        keyShown: document.body.textContent?.includes(KEY),
      }).toEqual({
        dialog: true,
        alert: error.message,
        field: '',
        focused: 'youtube-key-input',
        save: ['Save', false],
        connected: null,
        actionError: null,
        toasts: [],
        keyShown: false,
      });
    },
  );

  it('saving + Esc: the save on its way is not abandoned, the dialog stays open and the panel too', async () => {
    const bridge = makeBridge(notConnected);
    bridge.youtubeSaveApiKey.mockImplementation(() => new Promise<YouTubeStatus>(() => undefined));
    const fixture = await render(bridge);
    await openDialog(fixture);
    await typeKey(fixture);
    el(fixture, 'youtube-key-save')!.click();
    await settle(fixture);

    el(fixture, 'youtube-key-input')!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
    );
    await settle(fixture);

    expect({
      dialog: el(fixture, 'youtube-key-dialog') !== null,
      panel: el(fixture, 'panel-youtube') === null ? null : 'open',
      save: text(fixture, 'youtube-key-save'),
      sent: bridge.youtubeSaveApiKey.mock.calls.length,
    }).toEqual({ dialog: true, panel: 'open', save: 'Saving…', sent: 1 });
  });

  it.each([
    { answer: 'refused', fails: true },
    { answer: 'saved', fails: false },
  ])(
    'a typed key, then $answer: the field is a password, the key is in no text, attribute, title or log line, no field holds it once the dialog is closed, and the dialog opens empty again',
    async ({ fails }) => {
      const logged: unknown[] = [];
      const spies = (['log', 'info', 'warn', 'error', 'debug'] as const).map((level) =>
        vi.spyOn(console, level).mockImplementation((...args: unknown[]) => {
          logged.push(...args);
        }),
      );
      const bridge = makeBridge(notConnected);
      if (fails)
        bridge.youtubeSaveApiKey.mockImplementationOnce(async () => {
          throw ipcFailure(FAILURES[0].error);
        });
      const fixture = await render(bridge);
      const leaks = (): string[] => [
        ...(document.body.textContent?.includes(KEY) ? ['text'] : []),
        ...(document.title.includes(KEY) ? ['title'] : []),
        ...Array.from(document.querySelectorAll('*')).flatMap((node) =>
          Array.from(node.attributes)
            .filter((a) => a.value.includes(KEY))
            .map((a) => a.name),
        ),
      ];

      await openDialog(fixture);
      const type = (el(fixture, 'youtube-key-input') as HTMLInputElement).type;
      await typeKey(fixture);
      const typed = leaks();
      el(fixture, 'youtube-key-save')!.click();
      await settle(fixture);
      await settle(fixture);
      const answered = leaks();
      if (fails) {
        await typeKey(fixture);
        el(fixture, 'youtube-key-cancel')!.click();
        await settle(fixture);
      }
      const afterClose = { dialog: el(fixture, 'youtube-key-dialog') !== null, held: keyHeld() };
      if (fails) await openDialog(fixture);
      const reopened = fails ? (el(fixture, 'youtube-key-input') as HTMLInputElement).value : null;
      spies.forEach((spy) => spy.mockRestore());

      expect({
        type,
        typed,
        answered,
        afterClose,
        reopened,
        logged: logged.some((entry) => String(entry).includes(KEY)),
      }).toEqual({
        type: 'password',
        typed: [],
        answered: [],
        afterClose: { dialog: false, held: false },
        reopened: fails ? '' : null,
        logged: false,
      });
    },
  );

  async function pressEnter(fixture: ComponentFixture<FeaturesComponent>): Promise<void> {
    const input = el(fixture, 'youtube-key-input')!;
    input.focus();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await settle(fixture);
  }

  it('Enter in the key field saves it, as Save does', async () => {
    const bridge = makeBridge(notConnected);
    const fixture = await render(bridge);
    await openDialog(fixture);
    await typeKey(fixture);

    await pressEnter(fixture);
    await settle(fixture);

    expect({
      sent: bridge.youtubeSaveApiKey.mock.calls,
      dialog: el(fixture, 'youtube-key-dialog') !== null,
      connected: text(fixture, 'youtube-connected') ?? null,
    }).toEqual({
      sent: [[{ apiKey: KEY }]],
      dialog: false,
      connected: 'Connected with an API key',
    });
  });

  it('Enter with the field empty says the key is required and sends nothing', async () => {
    const bridge = makeBridge(notConnected);
    const fixture = await render(bridge);
    await openDialog(fixture);

    await pressEnter(fixture);

    expect({
      sent: bridge.youtubeSaveApiKey.mock.calls.length,
      dialog: el(fixture, 'youtube-key-dialog') !== null,
      alert: text(fixture, 'youtube-key-alert') ?? null,
      help: text(fixture, 'youtube-key-help') ?? null,
      invalid: el(fixture, 'youtube-key-input')?.getAttribute('aria-invalid') ?? null,
      focused: focused(),
    }).toEqual({
      sent: 0,
      dialog: true,
      alert: 'One required field is empty. Nothing was saved.',
      help: 'Enter the API key.',
      invalid: 'true',
      focused: 'youtube-key-input',
    });
  });

  it('Enter pressed twice while the save is on its way sends the key once', async () => {
    const bridge = makeBridge(notConnected);
    bridge.youtubeSaveApiKey.mockImplementation(() => new Promise<YouTubeStatus>(() => undefined));
    const fixture = await render(bridge);
    await openDialog(fixture);
    await typeKey(fixture);

    await pressEnter(fixture);
    await pressEnter(fixture);

    expect({
      sent: bridge.youtubeSaveApiKey.mock.calls.length,
      dialog: el(fixture, 'youtube-key-dialog') !== null,
      save: text(fixture, 'youtube-key-save') ?? null,
    }).toEqual({ sent: 1, dialog: true, save: 'Saving…' });
  });
});
