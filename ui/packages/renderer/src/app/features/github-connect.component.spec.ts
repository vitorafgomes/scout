import { provideRouter } from '@angular/router';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { InteractivityChecker } from '@angular/cdk/a11y';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest';

import {
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type FeatureDto,
  type GitHubBotStatus,
  type GitHubStatus,
} from '@scout/shared';

import { FeaturesStore } from '../core/features.store';
import { LAID_OUT } from '../core/laid-out.testing';
import { NoticeService } from '../core/notice.service';
import { ScoutBridgeService } from '../core/scout-bridge';

import { COLLAPSIBLE_LISTS_STORAGE_KEY } from './collapsible-list.component';
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
  repositories: null,
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
  name: null,
  nameError: null,
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
    name: 'Scout (octocat)',
    installUrl: BOT_INSTALL_URL,
    settingsUrl: BOT_SETTINGS_URL,
    writes: ALL_WRITES,
  },
  installed: {
    ...botNone,
    state: 'installed',
    login: 'scout-octocat[bot]',
    name: 'Scout (octocat)',
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
    setBot(next: GitHubBotStatus): void {
      bot = next;
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
      provideRouter([]),
      { provide: InteractivityChecker, useValue: LAID_OUT },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      FeaturesStore,
    ],
  });
  const fixture = TestBed.createComponent(FeaturesComponent);
  await settle(fixture);
  card(fixture, 'details-github')!.click();
  fixture.detectChanges();
  if (panelSection(fixture, 'github') === null)
    throw new Error('Details did not open the GitHub panel');
  return fixture;
}

async function settle(fixture: ComponentFixture<FeaturesComponent>): Promise<void> {
  for (let i = 0; i < 6; i++) {
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

function panelSection(
  fixture: ComponentFixture<FeaturesComponent>,
  id: string,
): HTMLElement | null {
  return el(fixture, `panel-${id}`)?.hidden === false ? el(fixture, `panel-${id}`) : null;
}

async function compileOnce(): Promise<void> {
  const fixture = await render(makeBridge(notConnected));
  fixture.destroy();
  TestBed.resetTestingModule();
  document.body.innerHTML = '';
}

await compileOnce();

describe('GitHub card', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(COLLAPSIBLE_LISTS_STORAGE_KEY);
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

  describe('Use a token dialog', () => {
    const TYPED = 'github_pat_11ABCDEFG0renderer';
    const REFUSED = 'GitHub rejected this token.';
    const HELP =
      'Read access to Metadata, Contents, Checks, Commit statuses, Pull requests and Issues, on the repositories Scout should see.';
    const EMPTY_ALERT = 'One required field is empty. Nothing was saved.';

    type Bridge = ReturnType<typeof makeBridge>;
    type State = 'empty' | 'blank' | 'typed' | 'refused';
    type Action = 'Save' | 'Enter' | 'Esc' | 'Cancel';

    const refusal = (): Error =>
      new Error(
        `Error invoking remote method 'scout:github.saveToken': Error: ${encodeIpcErrorMessage(
          serializeError(new ScoutError('github/token-rejected', REFUSED)),
        )}`,
      );

    function input(fixture: ComponentFixture<FeaturesComponent>): HTMLInputElement {
      return el(fixture, 'github-token-input') as HTMLInputElement;
    }

    function type(fixture: ComponentFixture<FeaturesComponent>, value: string): void {
      input(fixture).value = value;
      input(fixture).dispatchEvent(new Event('input'));
    }

    function focused(): string | null {
      return (document.activeElement as HTMLElement | null)?.getAttribute('data-test') ?? null;
    }

    function notices(): string[] {
      return Array.from(document.querySelectorAll('.cdk-overlay-container .sc-notice'), (n) => {
        const label = n.querySelector('.mat-mdc-snack-bar-label [aria-hidden="true"]') ?? n;
        return label.textContent?.replace(/\s+/g, ' ').trim() ?? '';
      });
    }

    function dialog(fixture: ComponentFixture<FeaturesComponent>) {
      if (el(fixture, 'github-token-dialog') === null)
        return { open: false, focused: focused(), panel: panelSection(fixture, 'github') !== null };
      const text = (test: string) =>
        el(fixture, test)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
      return {
        open: true,
        title: text('github-token-title'),
        focused: focused(),
        field: {
          type: input(fixture).type,
          value: input(fixture).value,
          invalid: input(fixture).getAttribute('aria-invalid'),
          help: text('github-token-help'),
        },
        alert: text('github-token-alert'),
        save: text('github-token-save'),
        disabled: [
          (el(fixture, 'github-token-cancel') as HTMLButtonElement).disabled,
          (el(fixture, 'github-token-save') as HTMLButtonElement).disabled,
        ],
      };
    }

    const opened = (over: Partial<Record<string, unknown>> = {}) => ({
      open: true,
      title: 'Connect GitHub',
      focused: 'github-token-cancel',
      field: { type: 'password', value: '', invalid: null, help: HELP },
      alert: null,
      save: 'Save',
      disabled: [false, false],
      ...over,
    });
    const nothingSaved = opened({
      focused: 'github-token-save',
      field: { type: 'password', value: '', invalid: 'true', help: 'Enter a GitHub token.' },
      alert: EMPTY_ALERT,
    });
    const saving = opened({
      focused: 'github-token-save',
      field: { type: 'password', value: TYPED, invalid: null, help: HELP },
      save: 'Saving…',
      disabled: [true, true],
    });
    const closed = { open: false, focused: 'github-use-token', panel: true };

    async function reach(
      state: State,
    ): Promise<{ fixture: ComponentFixture<FeaturesComponent>; bridge: Bridge }> {
      const bridge = makeBridge(notConnected);
      const fixture = await render(bridge);
      el(fixture, 'github-use-token')!.click();
      await settle(fixture);
      if (state === 'blank') type(fixture, '   ');
      if (state === 'typed' || state === 'refused') type(fixture, TYPED);
      if (state === 'refused') {
        bridge.githubSaveToken.mockImplementationOnce(async () => {
          throw refusal();
        });
        el(fixture, 'github-token-save')!.click();
        await settle(fixture);
      }
      bridge.githubSaveToken.mockImplementation(() => new Promise<GitHubStatus>(() => undefined));
      await settle(fixture);
      return { fixture, bridge };
    }

    async function act(
      fixture: ComponentFixture<FeaturesComponent>,
      action: Action,
    ): Promise<void> {
      if (action === 'Save') {
        el(fixture, 'github-token-save')!.focus();
        el(fixture, 'github-token-save')!.click();
      }
      if (action === 'Cancel') el(fixture, 'github-token-cancel')!.click();
      if (action === 'Enter' || action === 'Esc') {
        input(fixture).focus();
        input(fixture).dispatchEvent(
          new KeyboardEvent('keydown', {
            key: action === 'Enter' ? 'Enter' : 'Escape',
            bubbles: true,
          }),
        );
      }
      await settle(fixture);
    }

    const onceTyped = [[{ token: TYPED }]];
    const rows: readonly {
      state: State;
      action: Action;
      after: { readonly open: boolean; readonly [fact: string]: unknown };
      calls: unknown[];
    }[] = [
      { state: 'empty', action: 'Save', after: nothingSaved, calls: [] },
      {
        state: 'empty',
        action: 'Enter',
        after: { ...nothingSaved, focused: 'github-token-input' },
        calls: [],
      },
      { state: 'empty', action: 'Esc', after: closed, calls: [] },
      { state: 'empty', action: 'Cancel', after: closed, calls: [] },
      {
        state: 'blank',
        action: 'Save',
        after: { ...nothingSaved, field: { ...nothingSaved.field, value: '   ' } },
        calls: [],
      },
      { state: 'typed', action: 'Save', after: saving, calls: onceTyped },
      {
        state: 'typed',
        action: 'Enter',
        after: { ...saving, focused: 'github-token-input' },
        calls: onceTyped,
      },
      { state: 'typed', action: 'Esc', after: closed, calls: [] },
      { state: 'typed', action: 'Cancel', after: closed, calls: [] },
      { state: 'refused', action: 'Save', after: nothingSaved, calls: onceTyped },
      {
        state: 'refused',
        action: 'Enter',
        after: { ...nothingSaved, focused: 'github-token-input' },
        calls: onceTyped,
      },
      { state: 'refused', action: 'Esc', after: closed, calls: onceTyped },
      { state: 'refused', action: 'Cancel', after: closed, calls: onceTyped },
    ];

    it.each(rows)(
      'state $state + $action: the dialog, the token sent to main and the notice',
      async ({ state, action, after, calls }) => {
        const { fixture, bridge } = await reach(state);

        await act(fixture, action);
        const facts = dialog(fixture);
        const sent = bridge.githubSaveToken.mock.calls;
        const shown = notices();
        let reopened: string | null = null;
        if (!facts.open) {
          el(fixture, 'github-use-token')!.click();
          await settle(fixture);
          reopened = input(fixture).value;
        }

        expect({ facts, sent, shown, reopened }).toEqual({
          facts: after,
          sent: calls,
          shown: [],
          reopened: after.open ? null : '',
        });
      },
    );

    it('closed + Use a token: the dialog opens titled Connect GitHub with Cancel focused and the token field empty and masked, and main is not called', async () => {
      const bridge = makeBridge(notConnected);
      const fixture = await render(bridge);

      el(fixture, 'github-use-token')!.click();
      await settle(fixture);

      expect({
        facts: dialog(fixture),
        lede: el(fixture, 'github-token-lede')?.textContent?.trim(),
        label: (fixture.nativeElement as HTMLElement)
          .querySelector('label[for="github-token-input"]')
          ?.textContent?.trim(),
        sent: bridge.githubSaveToken.mock.calls,
      }).toEqual({
        facts: opened(),
        lede: "Fields marked * are required. Values stay in this computer's protected storage.",
        label: 'Personal access token *',
        sent: [],
      });
    });

    const WRITE_WARNING =
      'This token can write to your repositories. A read-only fine-grained token is safer.';
    it.each([
      {
        answer: 'refused',
        fails: true,
        expected: {
          facts: opened({ focused: 'github-token-input', alert: REFUSED }),
          sent: onceTyped,
          shown: [],
          reads: 0,
          connected: null,
          warning: null,
          install: null,
          badge: 'Not connected',
        },
      },
      {
        answer: 'saved',
        fails: false,
        expected: {
          facts: { open: false, focused: 'feature-panel-close', panel: true },
          sent: onceTyped,
          shown: ['GitHub credentials saved'],
          reads: 1,
          connected: 'Connected as vitorafgomes',
          warning: WRITE_WARNING,
          install: null,
          badge: 'Connected',
        },
      },
    ])('typed + Save, main answers $answer', async ({ fails, expected }) => {
      const { fixture, bridge } = await reach('typed');
      bridge.githubSaveToken.mockImplementation(async () => {
        if (fails) throw refusal();
        const status: GitHubStatus = {
          ...connected,
          method: 'token',
          installed: false,
          warning: WRITE_WARNING,
        };
        bridge.setStatus(status);
        return status;
      });
      const readsBefore = bridge.githubStatus.mock.calls.length;

      await act(fixture, 'Save');
      await settle(fixture);

      expect({
        facts: dialog(fixture),
        sent: bridge.githubSaveToken.mock.calls,
        shown: notices(),
        reads: bridge.githubStatus.mock.calls.length - readsBefore,
        connected: el(fixture, 'github-connected')?.textContent?.trim() ?? null,
        warning: el(fixture, 'github-warning')?.textContent?.trim() ?? null,
        install: el(fixture, 'github-install'),
        badge: card(fixture, 'badge-github')?.textContent?.trim(),
      }).toEqual(expected);
    });

    it('a kept token never leaves the keyboard on Disconnect, the one control that undoes it', async () => {
      const { fixture, bridge } = await reach('typed');
      bridge.githubSaveToken.mockImplementation(async () => {
        const status: GitHubStatus = { ...connected, method: 'token', installed: false };
        bridge.setStatus(status);
        return status;
      });

      await act(fixture, 'Save');
      await settle(fixture);

      expect({
        disconnectShown: el(fixture, 'github-disconnect') !== null,
        onDisconnect: document.activeElement === el(fixture, 'github-disconnect'),
        insidePanel: el(fixture, 'feature-panel-close') === document.activeElement,
      }).toEqual({ disconnectShown: true, onDisconnect: false, insidePanel: true });
    });

    it('saving + Esc: the save on its way is not abandoned, the dialog stays open and the panel too', async () => {
      const { fixture, bridge } = await reach('typed');
      await act(fixture, 'Save');

      await act(fixture, 'Esc');

      expect({
        facts: dialog(fixture),
        panel: panelSection(fixture, 'github') !== null,
        sent: bridge.githubSaveToken.mock.calls,
      }).toEqual({
        facts: { ...saving, focused: 'github-token-input' },
        panel: true,
        sent: onceTyped,
      });
    });

    it('Create a fine-grained token on GitHub that main refuses to open: its sentence in the alert, the dialog open, nothing saved', async () => {
      const { fixture, bridge } = await reach('typed');
      bridge.appOpenExternal.mockImplementation(async () => {
        throw new Error(
          `Error invoking remote method 'scout:app.openExternal': Error: ${encodeIpcErrorMessage(
            serializeError(
              new ScoutError('app/external-host-not-allowed', 'Scout does not open that address.'),
            ),
          )}`,
        );
      });

      el(fixture, 'github-create-token')!.click();
      await settle(fixture);

      expect({
        facts: dialog(fixture),
        sent: bridge.githubSaveToken.mock.calls,
      }).toEqual({
        facts: opened({
          field: { type: 'password', value: TYPED, invalid: null, help: HELP },
          alert: 'Scout does not open that address.',
        }),
        sent: [],
      });
    });

    it('Create a fine-grained token on GitHub opens the page that creates one, through main', async () => {
      const { fixture, bridge } = await reach('empty');

      el(fixture, 'github-create-token')!.click();
      await settle(fixture);

      expect({
        opened: bridge.appOpenExternal.mock.calls,
        link: el(fixture, 'github-create-token')?.textContent?.trim(),
      }).toEqual({
        opened: [[{ url: 'https://github.com/settings/personal-access-tokens/new' }]],
        link: 'Create a fine-grained token on GitHub',
      });
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
    const asked = await answer(fixture, 'confirm-ok');

    expect(asked).toEqual({
      title:
        'Disconnect GitHub? | Scout stops reading and writing on GitHub and forgets the sign-in on this computer. The Scout GitHub App stays installed on your account until you remove it there.',
      focused: 'confirm-cancel',
    });
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

    const github = card(fixture, 'card-github')!.textContent ?? '';
    expect(card(fixture, 'badge-github')?.textContent?.trim()).toBe('Connected');
    expect(github).not.toContain('Needs credentials');
    expect(el(fixture, 'toggle-github')).toBeNull();
    expect(card(fixture, 'badge-kubernetes')?.textContent?.trim()).toBe('Not connected');
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

    expect(card(fixture, 'badge-github')?.textContent?.trim()).toBe('Connected');
    expect(el(fixture, 'access-warning-github')).toBeNull();
  });

  it('a GitHub card that is not connected says Not connected and has no toggle', async () => {
    const fixture = await render(makeBridge(notConnected));

    expect(card(fixture, 'badge-github')?.textContent?.trim()).toBe('Not connected');
    expect(el(fixture, 'toggle-github')).toBeNull();
  });

  it('replaces the credential dialog button on the GitHub card with the GitHub connect section in its panel', async () => {
    const fixture = await render(makeBridge(notConnected));

    expect(el(fixture, 'cta-github')).toBeNull();
    expect(card(fixture, 'cta-github')).toBeNull();
    expect(
      panelSection(fixture, 'github')?.querySelector('[data-test="github-connect-section"]'),
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
          'list-toggle-github-bot-repos',
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
      'state %s: while GitHub is not connected the section shows with its reason, the select on and only Create off',
      async (state) => {
        const bridge = makeBridge(notConnected, [], botStates[state]);
        const fixture = await render(bridge);
        const create = el(fixture, 'github-bot-create') as HTMLButtonElement;

        create.click();
        await settle(fixture);

        expect(el(fixture, 'github-bot-section')?.querySelector('h3')?.textContent?.trim()).toBe(
          'Use a Scout bot on GitHub (optional)',
        );
        expect(create.disabled).toBe(true);
        expect(create.getAttribute('aria-describedby')).toBe('github-bot-reason');
        expect(el(fixture, 'github-bot-reason')?.textContent?.trim()).toBe(
          'Connect GitHub first to create the bot.',
        );
        expect((el(fixture, 'github-bot-target') as HTMLSelectElement).disabled).toBe(false);
        expect(bridge.githubBotStatus).not.toHaveBeenCalled();
        expect(bridge.githubBotCreate).not.toHaveBeenCalled();
      },
    );

    it.each([
      ['none', 'stays hidden', false],
      ['creating', 'stays hidden', false],
      ['error', 'stays hidden', false],
      ['created-not-installed', 'shows', true],
      ['installed', 'shows', true],
    ] as const)(
      'state %s: Add a picture on GitHub %s, since it needs the bot to exist',
      async (state, _words, shown) => {
        const fixture = await render(
          makeBridge(connected, [], { ...botStates[state]!, settingsUrl: BOT_SETTINGS_URL }),
        );

        expect(el(fixture, 'github-bot-picture-open') !== null).toBe(shown);
      },
    );

    it.each([
      ['a user-owned App', BOT_SETTINGS_URL],
      [
        'an organisation-owned App',
        'https://github.com/organizations/acme/settings/apps/scout-acme',
      ],
    ])(
      'Add a picture on GitHub, with its icon, opens the settings page of %s through main and says whose picture changes',
      async (_owner, settingsUrl) => {
        const bridge = makeBridge(connected, [], { ...botStates['installed']!, settingsUrl });
        const fixture = await render(bridge);
        const button = el(fixture, 'github-bot-picture-open') as HTMLButtonElement;

        button.click();
        await settle(fixture);

        expect({
          label: button.textContent?.trim(),
          icon: button.querySelector('svg') !== null,
          help: document
            .getElementById(button.getAttribute('aria-describedby') ?? '')
            ?.textContent?.replace(/\s+/g, ' ')
            .trim(),
          url: el(fixture, 'github-bot-picture-url')?.textContent?.trim(),
        }).toEqual({
          label: 'Add a picture on GitHub',
          icon: true,
          help: 'GitHub shows this picture on everything the bot signs. Actions you sign yourself keep your own picture.',
          url: settingsUrl,
        });
        expect(bridge.appOpenExternal.mock.calls).toEqual([[{ url: settingsUrl }]]);
        expect(bridge.githubBotSetWrites).not.toHaveBeenCalled();
      },
    );

    it.each([
      ['none', 'stays hidden', null],
      ['creating', 'stays hidden', null],
      ['error', 'stays hidden', null],
      ['created-not-installed', 'shows', 'Ask for a review with @scout-octocat'],
      ['installed', 'shows', 'Ask for a review with @scout-octocat'],
    ] as const)(
      'state %s: the mention that asks the bot for a review %s, since it needs the bot to exist',
      async (state, _words, line) => {
        const fixture = await render(makeBridge(connected, [], botStates[state]!));

        expect(
          el(fixture, 'github-bot-mention-line')?.textContent?.replace(/\s+/g, ' ').trim() ?? null,
        ).toBe(line);
      },
    );

    it("Copy puts the bot's current mention on the clipboard, following a renamed slug", async () => {
      const writeText = vi.fn(async (_text: string) => undefined);
      vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
      try {
        const bridge = makeBridge(connected, [], {
          ...botStates['installed']!,
          login: 'scout-review[bot]',
        });
        const fixture = await render(bridge);
        const copy = el(fixture, 'github-bot-mention-copy') as HTMLButtonElement;

        copy.click();
        await settle(fixture);

        expect(copy.getAttribute('aria-label')).toBe('Copy @scout-review');
        expect(writeText.mock.calls).toEqual([['@scout-review']]);
        expect(bridge.appOpenExternal).not.toHaveBeenCalled();
        expect(bridge.githubBotSetWrites).not.toHaveBeenCalled();
      } finally {
        vi.unstubAllGlobals();
      }
    });

    it.each([
      ['created-not-installed', 'Scout (octocat)', '@scout-octocat'],
      ['installed', 'Scout (octocat)', '@scout-octocat'],
    ] as const)(
      "state %s: the bot's current name and handle from GitHub show with the mention",
      async (state, name, handle) => {
        const fixture = await render(makeBridge(connected, [], botStates[state]!));

        expect(el(fixture, 'github-bot-name')?.textContent?.trim()).toBe(`Name: ${name}`);
        expect(el(fixture, 'github-bot-handle')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
          `Handle: ${handle}`,
        );
        expect(el(fixture, 'github-bot-name-error')).toBeNull();
      },
    );

    it.each([
      ['a user-owned App', BOT_SETTINGS_URL],
      [
        'an organisation-owned App',
        'https://github.com/organizations/acme/settings/apps/scout-acme',
      ],
    ])(
      'Change the name on GitHub opens the settings page of %s through main and says Scout reads the new name back',
      async (_owner, settingsUrl) => {
        const bridge = makeBridge(connected, [], { ...botStates['installed']!, settingsUrl });
        const fixture = await render(bridge);
        const button = el(fixture, 'github-bot-rename') as HTMLButtonElement;

        button.click();
        await settle(fixture);

        expect(button.textContent?.trim()).toBe('Change the name on GitHub');
        expect(
          document
            .getElementById(button.getAttribute('aria-describedby') ?? '')
            ?.textContent?.replace(/\s+/g, ' ')
            .trim(),
        ).toBe(
          'GitHub renames an App only on its settings page; Scout reads the new name when this card opens again or when you click Refresh.',
        );
        expect(bridge.appOpenExternal.mock.calls).toEqual([[{ url: settingsUrl }]]);
        expect(bridge.githubBotSetWrites).not.toHaveBeenCalled();
      },
    );

    it('Refresh reads the bot again and shows a name and handle renamed on GitHub at once, the mention with the new handle', async () => {
      const bridge = makeBridge(connected, [], botStates['installed']);
      const fixture = await render(bridge);
      const readsBefore = bridge.githubBotStatus.mock.calls.length;
      bridge.githubBotStatus.mockResolvedValue({
        ...botStates['installed']!,
        name: 'Review Helper',
        login: 'review-helper[bot]',
      });

      el(fixture, 'github-bot-refresh')!.click();
      await settle(fixture);

      expect(bridge.githubBotStatus.mock.calls.length).toBe(readsBefore + 1);
      expect(el(fixture, 'github-bot-name')?.textContent?.trim()).toBe('Name: Review Helper');
      expect(el(fixture, 'github-bot-handle')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
        'Handle: @review-helper',
      );
      expect(el(fixture, 'github-bot-mention-line')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
        'Ask for a review with @review-helper',
      );
    });

    it('a bot whose App could not be read keeps its last name and handle with a line saying so', async () => {
      const fixture = await render(
        makeBridge(connected, [], {
          ...botStates['installed']!,
          nameError:
            "Scout could not refresh the bot's name from GitHub; this is the last name it read.",
        }),
      );

      expect(el(fixture, 'github-bot-name')?.textContent?.trim()).toBe('Name: Scout (octocat)');
      expect(el(fixture, 'github-bot-handle')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
        'Handle: @scout-octocat',
      );
      expect(el(fixture, 'github-bot-name-error')?.textContent?.trim()).toBe(
        "Scout could not refresh the bot's name from GitHub; this is the last name it read.",
      );
    });

    it('a picture link main refuses says why under the GitHub section', async () => {
      const bridge = makeBridge(connected, [], botStates['installed']);
      bridge.appOpenExternal.mockRejectedValueOnce(
        new Error('The host is not on the list of pages Scout opens.'),
      );
      const fixture = await render(bridge);

      el(fixture, 'github-bot-picture-open')!.click();
      await settle(fixture);

      expect(el(fixture, 'github-action-error')?.textContent?.trim()).toBe(
        'The host is not on the list of pages Scout opens.',
      );
    });

    it('state none while connected says GitHub opens in the browser to confirm the App', async () => {
      const fixture = await render(makeBridge(connected, [], botNone));

      expect((el(fixture, 'github-bot-create') as HTMLButtonElement).disabled).toBe(false);
      expect(el(fixture, 'github-bot-reason')?.textContent?.trim()).toBe(
        'GitHub opens in your browser to confirm the App.',
      );
    });

    it('state error says why the last creation failed', async () => {
      const fixture = await render(makeBridge(connected, [], botStates['error']));

      expect(el(fixture, 'github-bot-error')?.textContent?.trim()).toBe(
        'GitHub did not send the new App back within 5 minutes, so Scout saved nothing. Try again.',
      );
    });

    it('state installed lists the repositories the bot can act on once the list is opened', async () => {
      const fixture = await render(makeBridge(connected, [], botStates['installed']));

      el(fixture, 'list-toggle-github-bot-repos')!.click();
      await settle(fixture);

      const repos = [
        ...fixture.nativeElement.querySelectorAll('[data-test="list-row-github-bot-repos"]'),
      ].map((li) => (li as HTMLElement).textContent?.trim());
      expect(repos).toEqual(['octocat/shop', 'octocat/api']);
    });

    it('connected with the bot, the projects do not all show expanded: the repositories start folded behind one line and the layout stays short', async () => {
      const repositories = Array.from({ length: 14 }, (_, i) => `octocat/repo-${i + 1}`);
      const fixture = await render(
        makeBridge(connected, [], { ...botStates['installed']!, repositories }),
      );
      const shownRows = () =>
        fixture.nativeElement.querySelectorAll('[data-test="list-row-github-bot-repos"]').length;
      const folded = [
        shownRows(),
        el(fixture, 'list-toggle-github-bot-repos')?.getAttribute('aria-expanded'),
        el(fixture, 'list-summary-github-bot-repos')?.textContent?.trim(),
      ];

      el(fixture, 'list-toggle-github-bot-repos')!.click();
      await settle(fixture);
      const opened = shownRows();
      el(fixture, 'list-toggle-github-bot-repos')!.click();
      await settle(fixture);

      expect(folded).toEqual([
        0,
        'false',
        'Repositories: 14 (scout-octocat[bot] can post in these)',
      ]);
      expect(opened).toBe(14);
      expect(shownRows()).toBe(0);
    });

    it('state installed sums the repositories up on the header: count and who posts in them', async () => {
      const fixture = await render(makeBridge(connected, [], botStates['installed']));

      expect(el(fixture, 'list-summary-github-bot-repos')?.textContent?.trim()).toBe(
        'Repositories: 2 (scout-octocat[bot] can post in these)',
      );
      expect(el(fixture, 'list-toggle-github-bot-repos')?.getAttribute('aria-expanded')).toBe(
        'false',
      );
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
      const org = el(fixture, 'github-bot-org') as HTMLInputElement;
      const reasonWithoutOrg = el(fixture, 'github-bot-reason')?.textContent?.trim();
      const disabledWithoutOrg = (el(fixture, 'github-bot-create') as HTMLButtonElement).disabled;
      org.value = 'acme';
      org.dispatchEvent(new Event('input'));
      await settle(fixture);
      el(fixture, 'github-bot-create')!.click();
      await settle(fixture);

      expect([reasonWithoutOrg, disabledWithoutOrg]).toEqual([
        'Type the organization first.',
        true,
      ]);
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
        await answer(fixture, 'confirm-ok');
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

describe('GitHub card: what it shows matches what main holds', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(COLLAPSIBLE_LISTS_STORAGE_KEY);
  });

  it('a Scout bot action switch main refuses goes back to off', async () => {
    const bridge = makeBridge(connected, [], { ...botStates['installed']!, writes: ['review'] });
    bridge.githubBotSetWrites.mockRejectedValueOnce(
      new Error('[scout:github-bot/unreachable] GitHub could not be reached.'),
    );
    const fixture = await render(bridge);

    (el(fixture, 'github-bot-write-verdict') as HTMLInputElement).click();
    await settle(fixture);

    expect((el(fixture, 'github-bot-write-verdict') as HTMLInputElement).checked).toBe(false);
  });

  it('a connected GitHub card shows Connect with GitHub again once main has dropped the session and says the features changed', async () => {
    const listeners: (() => void)[] = [];
    const bridge = makeBridge(connected);
    bridge.onFeaturesChanged.mockImplementation((...args: unknown[]) => {
      listeners.push(args[0] as () => void);
      return () => undefined;
    });
    const fixture = await render(bridge);

    bridge.setStatus(notConnected);
    for (const listener of listeners) listener();
    await settle(fixture);

    expect(el(fixture, 'github-connect')).not.toBeNull();
  });

  it('a Scout bot status main cannot read while GitHub is connected keeps the bot section with the reason inside it', async () => {
    const bridge = makeBridge(connected);
    bridge.githubBotStatus.mockRejectedValue(
      new Error('[scout:github-bot/unreachable] GitHub could not be reached.'),
    );
    const fixture = await render(bridge);

    expect(
      el(fixture, 'github-bot-section')
        ?.querySelector('[data-test="github-bot-error"]')
        ?.textContent?.trim() ?? null,
    ).toBe('GitHub could not be reached.');
  });
});

describe('the GitHub card says Docker would be good', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  type DockerKind = 'notInstalled' | 'notRunning' | 'running';

  function withDocker(github: GitHubStatus, docker: DockerKind, switchOn: boolean) {
    return Object.assign(makeBridge(github, ['111']), {
      dockerState: vi.fn(async () =>
        docker === 'running' ? { state: 'running' as const, version: '28.5.1' } : { state: docker },
      ),
      dockerImages: vi.fn(async () => ({ images: [] })),
      dockerWorkFolder: vi.fn(async () => ({
        path: '/home/owner/.local/share/Scout/sandbox',
        bytes: 0,
      })),
      vaultGetPublic: vi.fn(async () => ({ value: switchOn ? 'true' : null })),
    });
  }

  const matrix = [true, false].flatMap((isConnected) =>
    (['notInstalled', 'notRunning', 'running'] as const).flatMap((docker) =>
      [true, false].map((switchOn) => ({
        isConnected,
        docker,
        switchOn,
        shown: isConnected && (docker !== 'running' || !switchOn),
      })),
    ),
  );

  it.each(matrix)(
    'GitHub connected $isConnected, Docker $docker, switch on $switchOn: the hint shows $shown',
    async ({ isConnected, docker, switchOn, shown }) => {
      const fixture = await render(
        withDocker(isConnected ? connected : notConnected, docker, switchOn),
      );

      expect(el(fixture, 'github-docker-hint') !== null).toBe(shown);
    },
  );

  it("says the ADR's sentence, and Set up Docker switches the panel to Docker and moves the focus to its close button", async () => {
    const fixture = await render(withDocker(connected, 'notRunning', false));

    const hint = [
      el(fixture, 'github-docker-hint')?.querySelector('.docker-hint-text')?.textContent?.trim(),
      el(fixture, 'github-setup-docker')?.textContent?.trim(),
    ].join(' ');
    el(fixture, 'github-setup-docker')!.click();
    await settle(fixture);
    await TestBed.inject(ApplicationRef).whenStable();

    expect(hint).toBe(
      "Turn on Docker to let Scout run a pull request's tests in an isolated container before it gives a verdict. Set up Docker",
    );
    expect([panelSection(fixture, 'github'), panelSection(fixture, 'docker') !== null]).toEqual([
      null,
      true,
    ]);
    expect(el(fixture, 'feature-panel-title')?.textContent?.trim()).toBe('Docker');
    expect(document.activeElement).toBe(el(fixture, 'feature-panel-close'));
  });
});

describe('the Docker card in Features', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('sits beside GitHub: right after the GitHub card and before Kubernetes', async () => {
    const bridge = Object.assign(makeBridge(connected, ['111']), {
      dockerState: vi.fn(async () => ({ state: 'notInstalled' as const })),
      dockerWorkFolder: vi.fn(async () => ({ path: '/w', bytes: 0 })),
      vaultGetPublic: vi.fn(async () => ({ value: null })),
    });
    const fixture = await render(bridge);

    const cards = Array.from(
      fixture.nativeElement.querySelectorAll(
        '[data-test="features-grid"] [data-test^="card-"]',
      ) as NodeListOf<HTMLElement>,
      (card) => card.getAttribute('data-test'),
    );

    expect(cards).toEqual(['card-github', 'card-docker', 'card-kubernetes']);
  });
});

describe('GitHub card: Disconnect and Remove the bot ask first', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('Cancel on Disconnect keeps GitHub connected and signs nothing out', async () => {
    const bridge = makeBridge(connected);
    const fixture = await render(bridge);

    el(fixture, 'github-disconnect')!.click();
    const asked = await answer(fixture, 'confirm-cancel');

    expect(asked.focused).toBe('confirm-cancel');
    expect(bridge.githubSignOut).not.toHaveBeenCalled();
    expect(el(fixture, 'github-connected')).not.toBeNull();
  });

  it('Cancel on Remove the bot keeps the bot and removes nothing', async () => {
    const bridge = makeBridge(connected, [], botStates['installed']);
    const fixture = await render(bridge);

    el(fixture, 'github-bot-remove')!.click();
    const asked = await answer(fixture, 'confirm-cancel');

    expect(asked).toEqual({
      title:
        'Remove the Scout bot? | Scout stops signing as the bot and acts as you again. The App stays on GitHub until you delete it there.',
      focused: 'confirm-cancel',
    });
    expect(bridge.githubBotRemove).not.toHaveBeenCalled();
    expect(el(fixture, 'github-bot-installed')).not.toBeNull();
  });
});

describe('GitHub card: how many repositories the Scout GitHub App is installed on', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it.each([
    [null, 'Connected as vitorafgomes', false],
    [0, 'Connected as vitorafgomes', true],
    [1, 'Connected as vitorafgomes · installed on 1 repository', false],
    [6, 'Connected as vitorafgomes · installed on 6 repositories', false],
    [14, 'Connected as vitorafgomes · installed on 14 repositories', false],
  ] as const)(
    'with a count of %s the line after the login reads exactly what main counted',
    async (repositories, line, install) => {
      const fixture = await render(
        makeBridge({ ...connected, installed: repositories !== 0, repositories }),
      );

      const who = el(fixture, 'github-connected');
      expect({
        line: who?.textContent?.replace(/\s+/g, ' ').trim(),
        login: who?.querySelector('strong')?.textContent?.trim(),
        install: el(fixture, 'github-install') !== null,
      }).toEqual({ line, login: 'vitorafgomes', install });
    },
  );
});

describe('GitHub card: the bot select while GitHub is not connected', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('the owner chooses an organization and types it before connecting: Create stays off with its reason, and once connected the choice and the name are kept and Create sends them', async () => {
    const listeners: (() => void)[] = [];
    const bridge = makeBridge(notConnected);
    bridge.onFeaturesChanged.mockImplementation((...args: unknown[]) => {
      listeners.push(args[0] as () => void);
      return () => undefined;
    });
    const fixture = await render(bridge);

    const target = el(fixture, 'github-bot-target') as HTMLSelectElement;
    target.value = 'org';
    target.dispatchEvent(new Event('change'));
    await settle(fixture);
    const org = el(fixture, 'github-bot-org') as HTMLInputElement | null;
    expect(org).not.toBeNull();
    org!.value = 'acme';
    org!.dispatchEvent(new Event('input'));
    await settle(fixture);
    const before = {
      org: org?.closest('label')?.firstChild?.textContent?.trim(),
      create: (el(fixture, 'github-bot-create') as HTMLButtonElement).disabled,
      reason: el(fixture, 'github-bot-reason')?.textContent?.trim(),
    };
    bridge.setStatus(connected);
    for (const listener of listeners) listener();
    await settle(fixture);
    const after = {
      target: (el(fixture, 'github-bot-target') as HTMLSelectElement).value,
      org: (el(fixture, 'github-bot-org') as HTMLInputElement).value,
      create: (el(fixture, 'github-bot-create') as HTMLButtonElement).disabled,
    };
    el(fixture, 'github-bot-create')!.click();
    await settle(fixture);

    expect(before).toEqual({
      org: 'Organization',
      create: true,
      reason: 'Connect GitHub first to create the bot.',
    });
    expect(after).toEqual({ target: 'org', org: 'acme', create: false });
    expect(bridge.githubBotCreate.mock.calls).toEqual([[{ target: 'org', org: 'acme' }]]);
  });
});

describe("GitHub card: the bot's toasts, once each, only for a change seen while the panel is open", () => {
  type State = 'none' | 'creating' | 'created' | 'installed';
  type BotEvent =
    | 'creation completes'
    | 'installation appears'
    | 'panel reloaded with no change'
    | 'Remove confirmed'
    | 'Remove cancelled'
    | 'the call fails';

  const STATES: Readonly<Record<State, GitHubBotStatus>> = {
    none: botNone,
    creating: botStates['creating']!,
    created: botStates['created-not-installed']!,
    installed: botStates['installed']!,
  };
  const LOGIN = botStates['installed']!.login;
  const CREATED = `${LOGIN} was created on GitHub`;
  const INSTALLED = `${LOGIN} is installed on 2 repositories`;
  const REMOVED = 'Scout no longer uses the bot';
  const REMOVED_LINE =
    'Scout no longer uses the bot. The App stays on GitHub until you delete it there.';
  const REFUSED = 'The protected storage on this computer could not be reached.';

  let shown: MockInstance<NoticeService['show']>;

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(COLLAPSIBLE_LISTS_STORAGE_KEY);
    shown = vi.spyOn(NoticeService.prototype, 'show');
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  const toasts = () => shown.mock.calls.map(([message, kind]) => [message, kind]);

  async function mount(state: State) {
    const listeners: (() => void)[] = [];
    const bridge = makeBridge(connected, [], STATES[state]);
    bridge.onFeaturesChanged.mockImplementation((...args: unknown[]) => {
      listeners.push(args[0] as () => void);
      return () => undefined;
    });
    const fixture = await render(bridge);
    const reload = async (): Promise<void> => {
      for (const listener of listeners) listener();
      await settle(fixture);
    };
    return { bridge, fixture, reload };
  }

  async function happen(
    event: BotEvent,
    state: State,
    { bridge, fixture, reload }: Awaited<ReturnType<typeof mount>>,
  ): Promise<boolean> {
    if (event === 'creation completes') bridge.setBot(STATES.created);
    if (event === 'installation appears') bridge.setBot(STATES.installed);
    if (event === 'panel reloaded with no change') bridge.setBot(STATES[state]);
    if (event === 'creation completes' || event === 'installation appears') {
      await reload();
      return true;
    }
    if (event === 'panel reloaded with no change') {
      await reload();
      await reload();
      return true;
    }
    const remove = el(fixture, 'github-bot-remove');
    if (remove === null) return false;
    if (event === 'the call fails')
      bridge.githubBotRemove.mockRejectedValueOnce(
        new Error(`[scout:github-bot/remove-failed] ${REFUSED}`),
      );
    remove.click();
    await answer(fixture, event === 'Remove cancelled' ? 'confirm-cancel' : 'confirm-ok');
    return true;
  }

  const quiet = { available: true, toasts: [], removedLine: null, actionError: null, removed: 0 };
  const noRemove = { ...quiet, available: false };
  const removedRows = {
    'Remove confirmed': {
      ...quiet,
      toasts: [[REMOVED, 'success']],
      removedLine: REMOVED_LINE,
      removed: 1,
    },
    'Remove cancelled': quiet,
    'the call fails': { ...quiet, actionError: REFUSED, removed: 1 },
  } as const;

  const rows: readonly {
    state: State;
    event: BotEvent;
    after: Readonly<Record<string, unknown>>;
  }[] = [
    {
      state: 'none',
      event: 'creation completes',
      after: { ...quiet, toasts: [[CREATED, 'success']] },
    },
    {
      state: 'none',
      event: 'installation appears',
      after: { ...quiet, toasts: [[INSTALLED, 'success']] },
    },
    { state: 'none', event: 'panel reloaded with no change', after: quiet },
    { state: 'none', event: 'Remove confirmed', after: noRemove },
    { state: 'none', event: 'Remove cancelled', after: noRemove },
    { state: 'none', event: 'the call fails', after: noRemove },
    {
      state: 'creating',
      event: 'creation completes',
      after: { ...quiet, toasts: [[CREATED, 'success']] },
    },
    {
      state: 'creating',
      event: 'installation appears',
      after: { ...quiet, toasts: [[INSTALLED, 'success']] },
    },
    { state: 'creating', event: 'panel reloaded with no change', after: quiet },
    { state: 'creating', event: 'Remove confirmed', after: noRemove },
    { state: 'creating', event: 'Remove cancelled', after: noRemove },
    { state: 'creating', event: 'the call fails', after: noRemove },
    { state: 'created', event: 'creation completes', after: quiet },
    {
      state: 'created',
      event: 'installation appears',
      after: { ...quiet, toasts: [[INSTALLED, 'success']] },
    },
    { state: 'created', event: 'panel reloaded with no change', after: quiet },
    ...(['Remove confirmed', 'Remove cancelled', 'the call fails'] as const).map((event) => ({
      state: 'created' as const,
      event,
      after: removedRows[event],
    })),
    { state: 'installed', event: 'creation completes', after: quiet },
    { state: 'installed', event: 'installation appears', after: quiet },
    { state: 'installed', event: 'panel reloaded with no change', after: quiet },
    ...(['Remove confirmed', 'Remove cancelled', 'the call fails'] as const).map((event) => ({
      state: 'installed' as const,
      event,
      after: removedRows[event],
    })),
  ];

  it.each(rows)(
    'bot $state + $event: the toast with its exact text or none, the inline line, the action error and the calls to main',
    async ({ state, event, after }) => {
      const mounted = await mount(state);
      const atLoad = toasts();

      const available = await happen(event, state, mounted);

      expect(atLoad).toEqual([]);
      expect({
        available,
        toasts: toasts(),
        removedLine: el(mounted.fixture, 'github-bot-removed')?.textContent?.trim() ?? null,
        actionError: el(mounted.fixture, 'github-action-error')?.textContent?.trim() ?? null,
        removed: mounted.bridge.githubBotRemove.mock.calls.length,
      }).toEqual(after);
    },
  );

  it('the whole creation, watched by the 3 second poll: one toast when GitHub sends the App back, one when it is installed on 1 repository, and none while nothing changes', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const { bridge, fixture } = await mount('none');

    el(fixture, 'github-bot-create')!.click();
    await settle(fixture);
    await vi.advanceTimersByTimeAsync(3000);
    const whileCreating = toasts();
    bridge.setBot(STATES.created);
    await vi.advanceTimersByTimeAsync(3000);
    await vi.advanceTimersByTimeAsync(3000);
    const afterCreated = toasts();
    bridge.setBot({ ...STATES.installed, repositories: ['acme/shop'] });
    await vi.advanceTimersByTimeAsync(3000);
    await vi.advanceTimersByTimeAsync(3000);
    fixture.destroy();

    expect(whileCreating).toEqual([]);
    expect(afterCreated).toEqual([[CREATED, 'success']]);
    expect(toasts()).toEqual([
      [CREATED, 'success'],
      [`${LOGIN} is installed on 1 repository`, 'success'],
    ]);
  });

  it('a bot GitHub sends back already installed, never seen as created: the installation toast only', async () => {
    const { bridge, reload } = await mount('creating');

    bridge.setBot(STATES.installed);
    await reload();

    expect(toasts()).toEqual([[INSTALLED, 'success']]);
  });

  it.each(['created', 'installed'] as const)(
    'GitHub reconnected with the bot already %s: a reload after a disconnect is no change, so no toast',
    async (state) => {
      const { bridge, reload } = await mount(state);

      bridge.setStatus(notConnected);
      await reload();
      bridge.setStatus(connected);
      await reload();

      expect(toasts()).toEqual([[`GitHub connected as ${connected.login}`, 'success']]);
    },
  );
});

describe('GitHub card: the bot select when GitHub drops the session', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('an organization chosen and typed while connected is still shown when main drops the session, with Create off and its reason', async () => {
    const listeners: (() => void)[] = [];
    const bridge = makeBridge(connected);
    bridge.onFeaturesChanged.mockImplementation((...args: unknown[]) => {
      listeners.push(args[0] as () => void);
      return () => undefined;
    });
    const fixture = await render(bridge);
    const target = el(fixture, 'github-bot-target') as HTMLSelectElement;
    target.value = 'org';
    target.dispatchEvent(new Event('change'));
    await settle(fixture);
    const org = el(fixture, 'github-bot-org') as HTMLInputElement | null;
    expect(org).not.toBeNull();
    org!.value = 'acme';
    org!.dispatchEvent(new Event('input'));
    await settle(fixture);

    bridge.setStatus(notConnected);
    for (const listener of listeners) listener();
    await settle(fixture);

    expect({
      connect: el(fixture, 'github-connect') !== null,
      target: (el(fixture, 'github-bot-target') as HTMLSelectElement).value,
      org: (el(fixture, 'github-bot-org') as HTMLInputElement | null)?.value ?? null,
      create: (el(fixture, 'github-bot-create') as HTMLButtonElement).disabled,
      reason: el(fixture, 'github-bot-reason')?.textContent?.trim(),
    }).toEqual({
      connect: true,
      target: 'org',
      org: 'acme',
      create: true,
      reason: 'Connect GitHub first to create the bot.',
    });
    expect(bridge.githubBotCreate).not.toHaveBeenCalled();
  });
});
