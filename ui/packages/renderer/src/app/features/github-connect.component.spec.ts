import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { FeatureDto, GitHubStatus } from '@scout/shared';

import { FeaturesStore } from '../core/features.store';
import { ScoutBridgeService } from '../core/scout-bridge';

import { FeaturesComponent } from './features.component';

const feature = (id: 'github' | 'kubernetes'): FeatureDto => ({
  id,
  displayName: id === 'github' ? 'GitHub' : 'Kubernetes',
  description: '',
  requiresCredentials: true,
  hasCredentials: false,
  enabled: false,
  status: 'needs-credentials',
  lastError: null,
  credentialFields: [],
  settingsFields: [],
  settings: {},
  scopeMatrix: null,
  setupSteps: null,
});

const INSTALL_URL = 'https://github.com/apps/scout-chatops/installations/new';

const notConnected: GitHubStatus = {
  configured: true,
  connected: false,
  method: null,
  login: null,
  warning: null,
  installed: false,
  installUrl: INSTALL_URL,
  pending: null,
  error: null,
};

const pending: GitHubStatus = {
  ...notConnected,
  pending: { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device' },
};

const connected: GitHubStatus = {
  ...notConnected,
  connected: true,
  method: 'browser',
  login: 'vitorafgomes',
  installed: true,
};

function makeBridge(first: GitHubStatus, allowedUserIds: readonly string[] = []) {
  let current = first;
  return {
    accessGet: vi.fn(async () => ({ userIds: allowedUserIds })),
    setStatus(next: GitHubStatus): void {
      current = next;
    },
    featuresList: vi.fn(async () => ({ features: [feature('github'), feature('kubernetes')] })),
    githubStatus: vi.fn(async () => current),
    githubSignIn: vi.fn(async () => {
      current = pending;
      return pending.pending;
    }),
    githubCancel: vi.fn(async () => {
      current = notConnected;
    }),
    githubSignOut: vi.fn(async () => {
      current = notConnected;
      return notConnected;
    }),
    githubSaveToken: vi.fn(async (_req: { token: string }) => {
      current = {
        ...connected,
        method: 'token' as const,
        installed: false,
        warning:
          'This token can write to your repositories. A read-only fine-grained token is safer.',
      };
      return current;
    }),
    appOpenExternal: vi.fn(async () => undefined),
    kubernetesStatus: vi.fn(async () => ({ path: null, contexts: [], connected: false })),
    kubernetesDetect: vi.fn(async () => ({ path: null, contexts: [], error: null })),
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

describe('GitHub card', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('asks permission through the browser: shows the code and Open GitHub, then Connected as the account', async () => {
    const bridge = makeBridge(notConnected);
    const fixture = await render(bridge);
    vi.useFakeTimers();

    el(fixture, 'github-connect')!.click();
    await settle(fixture);
    const code = el(fixture, 'github-user-code')?.textContent?.trim();
    el(fixture, 'github-open')!.click();
    bridge.setStatus(connected);
    await vi.advanceTimersByTimeAsync(3000);
    await settle(fixture);

    expect(code).toBe('WDJB-MJHT');
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({
      url: 'https://github.com/login/device',
    });
    expect(el(fixture, 'github-connected')?.textContent?.trim()).toBe('Connected as vitorafgomes');
    expect(el(fixture, 'github-user-code')).toBeNull();
  });

  it('a pasted token connects the card as the token owner and shows the write warning', async () => {
    const bridge = makeBridge({ ...notConnected, configured: false });
    const fixture = await render(bridge);

    el(fixture, 'github-use-token')!.click();
    await settle(fixture);
    const help = el(fixture, 'github-token-help')?.textContent?.replace(/\s+/g, ' ').trim();
    const input = el(fixture, 'github-token-input') as HTMLInputElement;
    input.value = 'github_pat_11ABCDEFG0renderer';
    el(fixture, 'github-token-save')!.click();
    await settle(fixture);

    expect(help).toBe(
      'Recommended: a fine-grained token with read-only access (Metadata, Contents, Pull requests, Issues) to the repositories Scout should see.',
    );
    expect(bridge.githubSaveToken).toHaveBeenCalledWith({ token: 'github_pat_11ABCDEFG0renderer' });
    expect(el(fixture, 'github-connected')?.textContent?.trim()).toBe('Connected as vitorafgomes');
    expect(el(fixture, 'github-warning')?.textContent?.trim()).toBe(
      'This token can write to your repositories. A read-only fine-grained token is safer.',
    );
    expect(el(fixture, 'github-install')).toBeNull();
    expect(el(fixture, 'badge-github')?.textContent?.trim()).toBe('Connected');
  });

  it('links to the page that creates a fine-grained token', async () => {
    const bridge = makeBridge(notConnected);
    const fixture = await render(bridge);

    el(fixture, 'github-use-token')!.click();
    await settle(fixture);
    el(fixture, 'github-create-token')!.click();
    await settle(fixture);

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({
      url: 'https://github.com/settings/personal-access-tokens/new',
    });
  });

  it('says the App is not configured and disables Connect when Scout has no client id', async () => {
    const fixture = await render(makeBridge({ ...notConnected, configured: false }));

    expect(el(fixture, 'github-not-configured')?.textContent?.trim()).toBe(
      'The Scout GitHub App is not configured yet.',
    );
    expect((el(fixture, 'github-connect') as HTMLButtonElement).disabled).toBe(true);
  });

  it('offers to install Scout on the account when the App is not installed yet', async () => {
    const bridge = makeBridge({ ...connected, installed: false });
    const fixture = await render(bridge);

    const install = el(fixture, 'github-install');
    install!.click();
    await settle(fixture);

    expect(install?.textContent?.trim()).toBe('Install Scout on your GitHub account');
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: INSTALL_URL });
  });

  it('disconnects and offers Connect with GitHub again', async () => {
    const bridge = makeBridge(connected);
    const fixture = await render(bridge);

    el(fixture, 'github-disconnect')!.click();
    await settle(fixture);

    expect(bridge.githubSignOut).toHaveBeenCalledTimes(1);
    expect(el(fixture, 'github-connected')).toBeNull();
    expect(el(fixture, 'github-connect')?.textContent?.trim()).toBe('Connect with GitHub');
  });

  it('shows why the last sign-in failed next to Connect with GitHub', async () => {
    const fixture = await render(
      makeBridge({
        ...notConnected,
        error: { code: 'github/access-denied', message: 'The sign-in was cancelled on GitHub.' },
      }),
    );

    expect(el(fixture, 'github-error')?.textContent?.trim()).toBe(
      'The sign-in was cancelled on GitHub.',
    );
  });

  it('a connected GitHub card shows a Connected badge and no control-plane badge or toggle', async () => {
    const fixture = await render(makeBridge(connected));

    const github = el(fixture, 'card-github')!.textContent ?? '';
    expect(el(fixture, 'badge-github')?.textContent?.trim()).toBe('Connected');
    expect(github).not.toContain('Needs credentials');
    expect(el(fixture, 'toggle-github')).toBeNull();
    expect(el(fixture, 'badge-kubernetes')?.textContent?.trim()).toBe('Not connected');
  });

  it('a connected GitHub card warns that nobody can use it from chat while no user is allowed', async () => {
    const fixture = await render(makeBridge(connected, []));

    expect(el(fixture, 'access-warning-github')?.textContent?.trim()).toBe(
      'Nobody can use this connection from chat until you add allowed users in Settings.',
    );
    expect(el(fixture, 'access-warning-kubernetes')).toBeNull();
  });

  it('a connected GitHub card does not warn once a user is allowed', async () => {
    const fixture = await render(makeBridge(connected, ['111']));

    expect(el(fixture, 'badge-github')?.textContent?.trim()).toBe('Connected');
    expect(el(fixture, 'access-warning-github')).toBeNull();
  });

  it('a GitHub card that is not connected says Not connected and has no toggle', async () => {
    const fixture = await render(makeBridge(notConnected));

    expect(el(fixture, 'badge-github')?.textContent?.trim()).toBe('Not connected');
    expect(el(fixture, 'toggle-github')).toBeNull();
  });

  it('replaces the credential dialog button on the GitHub card with the GitHub connect section', async () => {
    const fixture = await render(makeBridge(notConnected));

    expect(el(fixture, 'cta-github')).toBeNull();
    expect(
      el(fixture, 'card-github')?.querySelector('[data-test="github-connect-section"]'),
    ).not.toBeNull();
  });

  describe('polling', () => {
    function deferred(): { promise: Promise<GitHubStatus>; resolve(value: GitHubStatus): void } {
      let resolve: (value: GitHubStatus) => void = () => {};
      const promise = new Promise<GitHubStatus>((r) => {
        resolve = r;
      });
      return { promise, resolve };
    }

    it('stops asking for the status once the card is gone, even when a refresh was in flight', async () => {
      const bridge = makeBridge(pending);
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      const fixture = await render(bridge);
      const slow = deferred();
      bridge.githubStatus.mockImplementationOnce(() => slow.promise);

      await vi.advanceTimersByTimeAsync(3000);
      const callsWhenDestroyed = bridge.githubStatus.mock.calls.length;
      fixture.destroy();
      slow.resolve(pending);
      await vi.advanceTimersByTimeAsync(30_000);

      expect(callsWhenDestroyed).toBe(2);
      expect(bridge.githubStatus).toHaveBeenCalledTimes(2);
    });

    it('never starts a second status request while a slow one is still in flight', async () => {
      const bridge = makeBridge(pending);
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      await render(bridge);
      const slow = deferred();
      bridge.githubStatus.mockImplementationOnce(() => slow.promise);

      await vi.advanceTimersByTimeAsync(3000);
      await vi.advanceTimersByTimeAsync(20_000);
      const callsWhileSlow = bridge.githubStatus.mock.calls.length;
      slow.resolve(pending);
      await vi.advanceTimersByTimeAsync(3000);

      expect(callsWhileSlow).toBe(2);
      expect(bridge.githubStatus).toHaveBeenCalledTimes(3);
    });

    it('does not poll for an installation when there is no install page to open', async () => {
      const bridge = makeBridge({ ...connected, installed: false, installUrl: null });
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      await render(bridge);

      await vi.advanceTimersByTimeAsync(30_000);

      expect(bridge.githubStatus).toHaveBeenCalledTimes(1);
    });
  });
});
