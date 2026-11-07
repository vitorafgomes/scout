import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { FeatureDto, GitHubBotStatus, GitHubStatus } from '@scout/shared';

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
  writeClass: 'none',
  granted: false,
  grantedAt: null,
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

const ALL_WRITES = ['review', 'verdict', 'comment', 'issue'] as const;
const BOT_INSTALL_URL = 'https://github.com/apps/scout-octocat/installations/new';
const BOT_SETTINGS_URL = 'https://github.com/settings/apps/scout-octocat';

const botNone: GitHubBotStatus = {
  state: 'none',
  login: null,
  installUrl: null,
  settingsUrl: null,
  repositories: [],
  writes: [],
  error: null,
};

const botStates: Readonly<Record<string, GitHubBotStatus>> = {
  none: botNone,
  creating: { ...botNone, state: 'creating' },
  'created-not-installed': {
    ...botNone,
    state: 'created',
    login: 'scout-octocat[bot]',
    installUrl: BOT_INSTALL_URL,
    settingsUrl: BOT_SETTINGS_URL,
    writes: ALL_WRITES,
  },
  installed: {
    ...botNone,
    state: 'installed',
    login: 'scout-octocat[bot]',
    installUrl: BOT_INSTALL_URL,
    settingsUrl: BOT_SETTINGS_URL,
    repositories: ['octocat/shop', 'octocat/api'],
    writes: ALL_WRITES,
  },
  error: {
    ...botNone,
    error: {
      code: 'github-bot/timeout',
      message:
        'GitHub did not send the new App back within 5 minutes, so Scout saved nothing. Try again.',
    },
  },
};

function makeBridge(
  first: GitHubStatus,
  allowedUserIds: readonly string[] = [],
  firstBot: GitHubBotStatus = botNone,
) {
  let current = first;
  let bot = firstBot;
  return {
    githubBotStatus: vi.fn(async () => bot),
    githubBotCreate: vi.fn(async (_req: unknown) => {
      bot = botStates['creating']!;
      return bot;
    }),
    githubBotCancel: vi.fn(async () => {
      bot = botNone;
      return bot;
    }),
    githubBotRemove: vi.fn(async () => {
      bot = botNone;
      return bot;
    }),
    githubBotSetWrites: vi.fn(async (req: { writes: readonly string[] }) => {
      bot = { ...bot, writes: req.writes as GitHubBotStatus['writes'] };
      return bot;
    }),
    accessGet: vi.fn(async () => ({ userIds: allowedUserIds })),
    setStatus(next: GitHubStatus): void {
      current = next;
    },
    onFeaturesChanged: vi.fn(() => () => undefined),
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
  for (let i = 0; i < 6; i++) {
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
      'Recommended: a fine-grained token with read access to Metadata and Contents and to Pull requests and Issues, read & write on those two only if you will allow Scout to post reviews and open issues, for the repositories Scout should see.',
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

  describe('Scout bot (optional)', () => {
    const visibility: ReadonlyArray<[string, readonly string[], readonly string[]]> = [
      [
        'none',
        ['github-bot-help', 'github-bot-target', 'github-bot-create'],
        ['github-bot-creating', 'github-bot-install', 'github-bot-remove', 'github-bot-error'],
      ],
      [
        'creating',
        ['github-bot-creating', 'github-bot-cancel'],
        ['github-bot-create', 'github-bot-install', 'github-bot-remove'],
      ],
      [
        'created-not-installed',
        ['github-bot-created', 'github-bot-install', 'github-bot-remove'],
        ['github-bot-create', 'github-bot-cancel', 'github-bot-write-review'],
      ],
      [
        'installed',
        [
          'github-bot-installed',
          'github-bot-repos',
          'github-bot-install',
          'github-bot-remove',
          ...ALL_WRITES.map((w) => `github-bot-write-${w}`),
        ],
        ['github-bot-create', 'github-bot-cancel', 'github-bot-created'],
      ],
      ['error', ['github-bot-error', 'github-bot-create'], ['github-bot-install']],
    ];

    it.each(visibility)('state %s shows exactly its controls', async (state, shown, hidden) => {
      const fixture = await render(makeBridge(connected, [], botStates[state]));

      expect(shown.filter((t) => el(fixture, t) === null)).toEqual([]);
      expect(hidden.filter((t) => el(fixture, t) !== null)).toEqual([]);
    });

    it.each(Object.keys(botStates))(
      'state %s: the section is hidden until GitHub is connected',
      async (state) => {
        const fixture = await render(makeBridge(notConnected, [], botStates[state]));

        expect(el(fixture, 'github-bot-section')).toBeNull();
      },
    );

    it('state error says why the last creation failed', async () => {
      const fixture = await render(makeBridge(connected, [], botStates['error']));

      expect(el(fixture, 'github-bot-error')?.textContent?.trim()).toBe(
        'GitHub did not send the new App back within 5 minutes, so Scout saved nothing. Try again.',
      );
    });

    it('state installed lists the repositories the bot can act on', async () => {
      const fixture = await render(makeBridge(connected, [], botStates['installed']));

      const repos = [...el(fixture, 'github-bot-repos')!.querySelectorAll('li')].map((li) =>
        li.textContent?.trim(),
      );
      expect(repos).toEqual(['octocat/shop', 'octocat/api']);
    });

    it('state none x create on my account asks main to create it on the user account', async () => {
      const bridge = makeBridge(connected, [], botNone);
      const fixture = await render(bridge);

      el(fixture, 'github-bot-create')!.click();
      await settle(fixture);

      expect(bridge.githubBotCreate).toHaveBeenCalledWith({ target: 'user' });
      expect(el(fixture, 'github-bot-creating')).not.toBeNull();
    });

    it('state none x create on an organization asks main to create it on that organization', async () => {
      const bridge = makeBridge(connected, [], botNone);
      const fixture = await render(bridge);

      const target = el(fixture, 'github-bot-target') as HTMLSelectElement;
      target.value = 'org';
      target.dispatchEvent(new Event('change'));
      await settle(fixture);
      (el(fixture, 'github-bot-org') as HTMLInputElement).value = 'acme';
      el(fixture, 'github-bot-create')!.click();
      await settle(fixture);

      expect(bridge.githubBotCreate).toHaveBeenCalledWith({ target: 'org', org: 'acme' });
    });

    it('state creating x cancel stops the creation', async () => {
      const bridge = makeBridge(connected, [], botStates['creating']);
      const fixture = await render(bridge);

      el(fixture, 'github-bot-cancel')!.click();
      await settle(fixture);

      expect(bridge.githubBotCancel).toHaveBeenCalledTimes(1);
      expect(el(fixture, 'github-bot-create')).not.toBeNull();
    });

    it.each(['created-not-installed', 'installed'])(
      'state %s x install opens the page that installs the bot',
      async (state) => {
        const bridge = makeBridge(connected, [], botStates[state]);
        const fixture = await render(bridge);

        el(fixture, 'github-bot-install')!.click();
        await settle(fixture);

        expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: BOT_INSTALL_URL });
      },
    );

    it.each(['created-not-installed', 'installed'])(
      "state %s x remove forgets the bot and links to the App's settings on GitHub",
      async (state) => {
        const bridge = makeBridge(connected, [], botStates[state]);
        const fixture = await render(bridge);

        el(fixture, 'github-bot-remove')!.click();
        await settle(fixture);
        el(fixture, 'github-bot-settings')!.click();
        await settle(fixture);

        expect(bridge.githubBotRemove).toHaveBeenCalledTimes(1);
        expect(el(fixture, 'github-bot-removed')?.textContent?.trim()).toBe(
          'Scout no longer uses the bot. The App stays on GitHub until you delete it there.',
        );
        expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: BOT_SETTINGS_URL });
        expect(el(fixture, 'github-bot-create')).not.toBeNull();
      },
    );

    it.each([
      ['review', true, ['verdict', 'comment', 'issue']],
      ['verdict', true, ['review', 'comment', 'issue']],
      ['comment', true, ['review', 'verdict', 'issue']],
      ['issue', true, ['review', 'verdict', 'comment']],
      ['verdict', false, ['review', 'verdict', 'comment', 'issue']],
    ] as const)(
      'state installed x switch %s (on: %s) sends the writes the bot signs afterwards',
      async (write, on, after) => {
        const writes = on ? ALL_WRITES : ALL_WRITES.filter((w) => w !== write);
        const bridge = makeBridge(connected, [], { ...botStates['installed']!, writes });
        const fixture = await render(bridge);

        const box = el(fixture, `github-bot-write-${write}`) as HTMLInputElement;
        const checkedBefore = box.checked;
        box.click();
        await settle(fixture);

        expect(checkedBefore).toBe(on);
        expect(bridge.githubBotSetWrites).toHaveBeenCalledWith({ writes: after });
        expect((el(fixture, `github-bot-write-${write}`) as HTMLInputElement).checked).toBe(!on);
      },
    );

    it.each([
      ['creating', 2],
      ['created-not-installed', 2],
      ['installed', 1],
      ['none', 1],
    ] as const)(
      'state %s polls for a change only while waiting for GitHub (%d status reads in 3 seconds)',
      async (state, reads) => {
        const bridge = makeBridge(connected, [], botStates[state]);
        vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
        const fixture = await render(bridge);

        await vi.advanceTimersByTimeAsync(3000);
        fixture.destroy();

        expect(bridge.githubBotStatus).toHaveBeenCalledTimes(reads);
      },
    );
  });
});
