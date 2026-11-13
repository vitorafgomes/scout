import { TemplateRef, provideZonelessChangeDetection, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Component } from '@angular/core';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { PendingActionDto, UpdateNotice, YouTubeStatus } from '@scout/shared';

import { LlmConfigStore } from '../core/llm-config.store';
import { MenuPreferences } from '../core/menu-preferences';
import { ScoutBridgeService } from '../core/scout-bridge';
import { PostConnectShellComponent } from './post-connect-shell.component';

@Component({ selector: 'sc-stub-llm', standalone: true, template: 'llm-page' })
class StubLlm {}
@Component({ selector: 'sc-stub-logs', standalone: true, template: 'logs-page' })
class StubLogs {}
@Component({ selector: 'sc-stub-settings', standalone: true, template: 'settings-page' })
class StubSettings {}
@Component({ selector: 'sc-stub-page', standalone: true, template: 'page' })
class StubPage {}
@Component({
  selector: 'sc-stub-actions-page',
  standalone: true,
  template: `<ng-template #topBarActions
      ><button type="button" data-test="page-action">Clear</button></ng-template
    >page with actions`,
})
class StubActionsPage {
  readonly topBarActions = viewChild.required<TemplateRef<unknown>>('topBarActions');
}

function updateBridge(notice: UpdateNotice | null) {
  return {
    updateGetState: async () => ({
      checkEnabled: true,
      lastCheckAt: null,
      lastResult: null,
      notice,
      packaged: true,
    }),
    onUpdateNotice: () => () => undefined,
    onUpdateProgress: () => () => undefined,
    notificationsList: async () => [],
    notificationsMarkRead: async () => undefined,
    onNotifications: () => () => undefined,
    onFeaturesChanged: () => () => undefined,
    onActionsChanged: () => () => undefined,
    actionsList: async () => ({ actions: [] }),
    youtubeStatus: async () => {
      throw new Error('[scout:youtube/status-failed] not in this test');
    },
  } as unknown as ScoutBridgeService;
}

async function setup(bridge?: ScoutBridgeService): Promise<{
  fixture: ComponentFixture<PostConnectShellComponent>;
  router: Router;
}> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      ...(bridge === undefined ? [] : [{ provide: ScoutBridgeService, useValue: bridge }]),
      {
        provide: LlmConfigStore,
        useValue: {
          provider: signal('ChatGptPlan'),
          model: signal('gpt-5.6-luna'),
          loadFromVault: async () => undefined,
        },
      },
      provideRouter([
        { path: 'chat', component: StubLlm },
        { path: 'sources', component: StubPage },
        { path: 'page-with-actions', component: StubActionsPage },
        { path: 'llm', component: StubLlm },
        { path: 'features', component: StubPage },
        { path: 'review', component: StubPage },
        { path: 'meetings', component: StubPage },
        { path: 'logs', component: StubLogs },
        { path: 'settings', component: StubSettings },
        { path: 'sources/local-models', component: StubPage },
        { path: 'slack', component: StubPage },
        { path: '', component: StubPage },
        { path: 'provision', component: StubPage },
        { path: 'token', component: StubPage },
        { path: 'oauth', component: StubPage },
        { path: 'launch', component: StubPage },
        { path: 'ready', component: StubPage },
      ]),
    ],
  });
  const fixture = TestBed.createComponent(PostConnectShellComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, router: TestBed.inject(Router) };
}

function declared(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found !== undefined && selector!.split(',').some((one) => node.matches(one.trim()))) {
      value = found;
    }
  }
  return value;
}

describe('PostConnectShellComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('shows no update bar while main knows no newer version', async () => {
    const { fixture } = await setup(updateBridge(null));

    expect(fixture.nativeElement.querySelector('[data-test="update-notice"]')).toBeNull();
  });

  it('shows the update bar above the page while a newer version is known', async () => {
    const { fixture } = await setup(
      updateBridge({
        currentVersion: '0.1.0',
        latestVersion: '0.2.0',
        notes: '',
        releaseUrl: 'https://github.com/vitorafgomes/scout/releases/tag/v0.2.0',
        downloadUrl: 'https://github.com/vitorafgomes/scout/releases/tag/v0.2.0',
        publishedAt: '2026-11-01T18:00:00Z',
        canInstall: false,
      }),
    );

    const bar = fixture.nativeElement.querySelector('[data-test="update-notice"]') as HTMLElement;
    expect(bar).not.toBeNull();
    expect(bar.textContent).toContain('Scout 0.2.0 is available. You have 0.1.0.');
    const main = fixture.nativeElement.querySelector('main.main') as HTMLElement;
    expect(bar.compareDocumentPosition(main) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(main.contains(bar)).toBe(false);
  });

  it('shows Logs in the menu while "Show Logs in the menu" is on', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="nav-logs"]')).not.toBeNull();
  });

  it('drops Logs from the menu once the owner turned "Show Logs in the menu" off', async () => {
    localStorage.setItem('scout:show-logs', '0');
    const { fixture } = await setup();
    expect(TestBed.inject(MenuPreferences).showLogs()).toBe(false);

    const labels = Array.from(fixture.nativeElement.querySelectorAll('.nav-label')).map((el) =>
      ((el as HTMLElement).textContent ?? '').trim(),
    );
    expect(labels).toEqual([
      'Chat',
      'AI Sources',
      'Features',
      'Models',
      'Meetings',
      'Approvals',
      'Settings',
    ]);
  });

  it('renders the navigation in the order Chat, AI Sources, Features, Models, Meetings, Approvals, then Logs and Settings, with no Status and no Production entry', async () => {
    const { fixture } = await setup();

    const links = fixture.nativeElement.querySelectorAll('.nav-link');
    expect(links.length).toBe(8);
    const labels = Array.from(links).map((el) =>
      ((el as HTMLElement).querySelector('.nav-label')?.textContent ?? '').trim(),
    );
    expect(labels).toEqual([
      'Chat',
      'AI Sources',
      'Features',
      'Models',
      'Meetings',
      'Approvals',
      'Logs',
      'Settings',
    ]);
  });

  it('exposes data-test attributes for every nav item', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="nav-chat"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="nav-sources"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="nav-models"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="nav-features"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="nav-production"]')).toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-test="nav-review"]')?.getAttribute('href'),
    ).toBe('/review');
    expect(fixture.nativeElement.querySelector('[data-test="nav-status"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="nav-logs"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="nav-settings"]')).not.toBeNull();
  });

  it('marks the matching nav item as active when navigated', async () => {
    const { fixture, router } = await setup();

    await router.navigateByUrl('/chat');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const active = fixture.nativeElement.querySelector('.nav-link--active');
    expect(active).not.toBeNull();
    expect(active?.getAttribute('data-test')).toBe('nav-chat');
  });

  it('switches the active marker when route changes', async () => {
    const { fixture, router } = await setup();

    await router.navigateByUrl('/logs');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    let active = fixture.nativeElement.querySelector('.nav-link--active');
    expect(active?.getAttribute('data-test')).toBe('nav-logs');

    await router.navigateByUrl('/settings');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    active = fixture.nativeElement.querySelector('.nav-link--active');
    expect(active?.getAttribute('data-test')).toBe('nav-settings');
  });

  it('one scroll: the shell is fixed to the window, so only the main area scrolls and the page never does', async () => {
    const { fixture } = await setup();
    const host = fixture.nativeElement as HTMLElement;
    const shell = host.querySelector('.shell')!;

    expect(declared(host, 'position')).toBe('fixed');
    expect(declared(host, 'inset')).toBe('0');
    expect(declared(host, 'height')).toBeNull();
    expect(declared(shell, 'height')).toBe('100%');
  });

  it('renders the Scout brand: the logo mark, an S on the accent square, beside the name', async () => {
    const { fixture } = await setup();
    const brand = fixture.nativeElement.querySelector('.brand') as HTMLElement;
    const mark = brand.querySelector('.brand-mark')!;

    expect(Array.from(brand.children, (part) => part.textContent?.trim())).toEqual(['S', 'Scout']);
    expect({
      width: declared(mark, 'width'),
      height: declared(mark, 'height'),
      radius: declared(mark, 'border-radius'),
      background: declared(mark, 'background'),
      color: declared(mark, 'color'),
    }).toEqual({
      width: '28px',
      height: '28px',
      radius: '8px',
      background: 'var(--sc-accent-strong)',
      color: 'var(--sc-on-accent)',
    });
  });

  it('the navigation is 200 px wide beside the page', async () => {
    const { fixture } = await setup();

    expect(declared(fixture.nativeElement.querySelector('.shell')!, 'grid-template-columns')).toBe(
      '200px 1fr',
    );
  });

  it('Approvals shows a checked box, not a bare check', async () => {
    const { fixture } = await setup();

    expect(
      fixture.nativeElement
        .querySelector('[data-test="nav-review"] sc-icon path')
        ?.getAttribute('d'),
    ).toBe('M9 11l3 3 8-8M20 12v7a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h9');
  });

  async function open(
    fixture: ComponentFixture<PostConnectShellComponent>,
    router: Router,
    url: string,
  ) {
    await router.navigateByUrl(url);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  for (const [url, title] of [
    ['/chat', 'Chat'],
    ['/llm', 'Models'],
    ['/settings', 'Settings'],
    ['/sources/local-models', 'AI Sources'],
    ['/slack', 'Scout'],
    ['/review', 'Approvals'],
  ] as const) {
    it(`the top bar titles ${url} as ${title} and shows the AI Source and model in use`, async () => {
      const { fixture, router } = await setup();

      await open(fixture, router, url);

      const bar = fixture.nativeElement.querySelector('sc-top-bar') as HTMLElement;
      expect(bar.querySelector('[data-test="top-bar-title"]')?.textContent?.trim()).toBe(title);
      expect(bar.querySelector('[data-test="top-bar-source"]')?.textContent?.trim()).toBe(
        'ChatGPT plan · gpt-5.6-luna',
      );
    });
  }

  it("a page's top-bar actions show in the top bar after the title and before the theme switch, and leave with the page", async () => {
    const { fixture, router } = await setup();

    await open(fixture, router, '/page-with-actions');
    const bar = fixture.nativeElement.querySelector('[data-test="top-bar"]') as HTMLElement;
    const order = Array.from(bar.children).map(
      (child) =>
        child.getAttribute('data-test') ??
        (child.classList.contains('heading')
          ? 'heading'
          : child.getAttribute('role') === 'group'
            ? 'theme'
            : child.tagName.toLowerCase()),
    );
    await open(fixture, router, '/settings');

    expect(order).toEqual(['heading', 'page-action', 'theme', 'sc-notification-bell']);
    expect(fixture.nativeElement.querySelector('[data-test="page-action"]')).toBeNull();
  });

  it('the top bar sits above the update bar and the page, outside the one scrolling area', async () => {
    const { fixture } = await setup(
      updateBridge({
        currentVersion: '0.1.0',
        latestVersion: '0.2.0',
        notes: '',
        releaseUrl: 'https://github.com/vitorafgomes/scout/releases/tag/v0.2.0',
        downloadUrl: 'https://github.com/vitorafgomes/scout/releases/tag/v0.2.0',
        publishedAt: '2026-11-01T18:00:00Z',
        canInstall: false,
      }),
    );
    const host = fixture.nativeElement as HTMLElement;
    const bar = host.querySelector('sc-top-bar')!;
    const notice = host.querySelector('[data-test="update-notice"]')!;
    const main = host.querySelector('main.main')!;

    expect(host.querySelectorAll('sc-top-bar').length).toBe(1);
    expect(bar.compareDocumentPosition(notice) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(bar.compareDocumentPosition(main) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(main.contains(bar)).toBe(false);
    expect(declared(main, 'overflow-y')).toBe('auto');
    expect(declared(host.querySelector('.content')!, 'overflow-y')).toBeNull();
  });

  it('every rail item shows its icon from sc-icon before its label', async () => {
    const { fixture } = await setup();

    const icons = Array.from(
      fixture.nativeElement.querySelectorAll('.nav-link') as NodeListOf<HTMLElement>,
    ).map((link) => [
      link.querySelector('.nav-label')?.textContent?.trim(),
      link.firstElementChild?.tagName.toLowerCase(),
      link.querySelector('sc-icon path')?.getAttribute('d')?.length ?? 0,
    ]);

    expect(icons.map(([label, tag]) => [label, tag])).toEqual(
      ['Chat', 'AI Sources', 'Features', 'Models', 'Meetings', 'Approvals', 'Logs', 'Settings'].map(
        (label) => [label, 'sc-icon'],
      ),
    );
    expect(icons.every(([, , length]) => (length as number) > 0)).toBe(true);
  });

  it('the active item is marked as the current page and painted with the nav-active accent', async () => {
    const { fixture, router } = await setup();

    await open(fixture, router, '/settings');

    const current = Array.from(
      fixture.nativeElement.querySelectorAll('[aria-current="page"]') as NodeListOf<HTMLElement>,
    ).map((link) => link.getAttribute('data-test'));
    const active = fixture.nativeElement.querySelector('.nav-link--active') as HTMLElement;
    expect(current).toEqual(['nav-settings']);
    expect(declared(active, 'background')).toBe('var(--sc-nav-active)');
  });

  const THEMED = { cls: false, background: 'var(--sc-bg)', color: 'var(--sc-fg)' };
  const PINNED = { cls: true, background: '#0f1117', color: '#e4e8f0' };

  for (const [url, expected] of [
    ['/chat', THEMED],
    ['/chat?draft=1', THEMED],
    ['/sources', THEMED],
    ['/sources#ollama-models', THEMED],
    ['/sources/local-models', THEMED],
    ['/llm', THEMED],
    ['/llm?tab=transcription', THEMED],
    ['/features', THEMED],
    ['/review', THEMED],
    ['/meetings', THEMED],
    ['/logs', THEMED],
    ['/settings', THEMED],
    ['/features?tab=x', THEMED],
    ['/logs#end', THEMED],
    ['/slack', PINNED],
    ['/', THEMED],
    ['/provision', THEMED],
    ['/token', THEMED],
    ['/oauth', THEMED],
    ['/launch', THEMED],
    ['/ready', THEMED],
    ['/ready?from=launch', THEMED],
  ] as const) {
    it(`on ${url} the page area ${expected.cls ? 'stays on the dark canvas' : 'follows the dark and light theme'}`, async () => {
      const { fixture, router } = await setup();

      await open(fixture, router, url);

      const main = fixture.nativeElement.querySelector('main.main') as HTMLElement;
      expect({
        cls: main.classList.contains('sc-dark-canvas'),
        background: declared(main, 'background'),
        color: declared(main, 'color'),
      }).toEqual(expected);
    });
  }

  it('the page area follows the theme on Chat, goes back to the dark canvas on Slack, a page not yet migrated, and follows it again on Chat', async () => {
    const { fixture, router } = await setup();
    const pinned: boolean[] = [];

    for (const url of ['/chat', '/slack', '/chat', '/sources', '/slack', '/features']) {
      await open(fixture, router, url);
      pinned.push(
        (fixture.nativeElement.querySelector('main.main') as HTMLElement).classList.contains(
          'sc-dark-canvas',
        ),
      );
    }

    expect(pinned).toEqual([false, true, false, false, true, false]);
  });
});

describe('PostConnectShellComponent: YouTube operator in the menu only when YouTube is connected', () => {
  type YouTube = 'connected' | 'not connected' | 'status failing';

  function status(connected: boolean): YouTubeStatus {
    return {
      configured: true,
      connected,
      method: connected ? 'oauth' : null,
      channelId: connected ? 'UCscoutTestChannel' : null,
      channelTitle: connected ? 'Scout test channel' : null,
      pending: false,
      error: null,
      clientConfigPath: '',
      source: 'embedded',
      scopes: [],
    };
  }

  function youTubeBridge(initial: YouTube) {
    let current: YouTube = initial;
    const featureListeners: (() => void)[] = [];
    const bridge = {
      ...(updateBridge(null) as unknown as Record<string, unknown>),
      youtubeStatus: vi.fn(async (): Promise<YouTubeStatus> => {
        if (current === 'status failing') throw new Error('[scout:youtube/status-failed] boom');
        return status(current === 'connected');
      }),
      onFeaturesChanged: vi.fn((listener: () => void) => {
        featureListeners.push(listener);
        return () => undefined;
      }),
      operatorOpenWindow: vi.fn(async () => undefined),
    };
    return {
      bridge,
      become(next: YouTube) {
        current = next;
      },
      pushFeaturesChanged() {
        featureListeners.forEach((listener) => listener());
      },
    };
  }

  async function settled(fixture: ComponentFixture<PostConnectShellComponent>): Promise<void> {
    for (let i = 0; i < 3; i++) {
      await fixture.whenStable();
      fixture.detectChanges();
    }
  }

  function rail(fixture: ComponentFixture<PostConnectShellComponent>): string[] {
    return Array.from(
      fixture.nativeElement.querySelectorAll('.nav-link .nav-label') as NodeListOf<HTMLElement>,
    ).map((label) => (label.textContent ?? '').trim());
  }

  const MENU = [
    'Chat',
    'AI Sources',
    'Features',
    'Models',
    'Meetings',
    'Approvals',
    'Logs',
    'Settings',
  ];
  const SHOWN: Readonly<Record<YouTube, readonly string[]>> = {
    connected: [...MENU, 'YouTube operator'],
    'not connected': MENU,
    'status failing': MENU,
  };

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  for (const state of Object.keys(SHOWN) as YouTube[]) {
    it(`YouTube ${state}: the menu ${state === 'connected' ? 'ends with YouTube operator' : 'has no YouTube operator'}`, async () => {
      const { bridge } = youTubeBridge(state);
      const { fixture } = await setup(bridge as unknown as ScoutBridgeService);
      await settled(fixture);

      expect(rail(fixture)).toEqual(SHOWN[state]);
    });
  }

  it('YouTube operator opens the operator window, as the Features screen does, and shows its video icon', async () => {
    const { bridge } = youTubeBridge('connected');
    const { fixture } = await setup(bridge as unknown as ScoutBridgeService);
    await settled(fixture);
    const item = fixture.nativeElement.querySelector(
      '[data-test="nav-youtube-operator"]',
    ) as HTMLButtonElement;

    item.click();
    await settled(fixture);

    expect(item.tagName).toBe('BUTTON');
    expect(item.querySelector('sc-icon path')?.getAttribute('d')).toBe(
      'M3 6h18v12H3zM10 9l5 3-5 3z',
    );
    expect(bridge.operatorOpenWindow).toHaveBeenCalledTimes(1);
  });

  it('an operator window main cannot open leaves the menu as it was and throws nothing', async () => {
    const { bridge } = youTubeBridge('connected');
    bridge.operatorOpenWindow.mockRejectedValueOnce(
      new Error('[scout:operator/window-failed] boom'),
    );
    const { fixture } = await setup(bridge as unknown as ScoutBridgeService);
    await settled(fixture);
    const errors: unknown[] = [];
    const record = (event: PromiseRejectionEvent) => errors.push(event.reason);
    window.addEventListener('unhandledrejection', record);

    (
      fixture.nativeElement.querySelector('[data-test="nav-youtube-operator"]') as HTMLButtonElement
    ).click();
    await settled(fixture);
    window.removeEventListener('unhandledrejection', record);

    expect(errors).toEqual([]);
    expect(rail(fixture)).toEqual(SHOWN.connected);
  });

  for (const [from, to, how] of [
    ['connected', 'not connected', 'the features-changed push after a sign-out'],
    ['not connected', 'connected', 'the next navigation after a sign-in'],
    ['not connected', 'connected', 'the window gaining focus after a sign-in in the browser'],
    ['connected', 'status failing', 'the features-changed push'],
  ] as const) {
    it(`YouTube ${from} then ${to}: the menu follows on ${how}`, async () => {
      const youTube = youTubeBridge(from);
      const { fixture, router } = await setup(youTube.bridge as unknown as ScoutBridgeService);
      await settled(fixture);
      const before = rail(fixture);

      youTube.become(to);
      if (how.startsWith('the features-changed push')) youTube.pushFeaturesChanged();
      else if (how.startsWith('the next navigation')) await router.navigateByUrl('/settings');
      else window.dispatchEvent(new Event('focus'));
      await settled(fixture);

      expect(before).toEqual(SHOWN[from]);
      expect(rail(fixture)).toEqual(SHOWN[to]);
    });
  }

  describe('the badge on Approvals', () => {
    function action(id: string, status: string): PendingActionDto {
      return {
        id,
        featureId: 'github',
        action: 'Pull request verdicts',
        target: 'octocat/hello#42',
        summary: 'Approve octocat/hello#42',
        requestedAt: '2026-11-08T09:00:00Z',
        status,
        decidedAt: null,
      };
    }

    function approvalsBridge(initial: readonly PendingActionDto[]) {
      let listed = initial;
      const listeners: (() => void)[] = [];
      const bridge = {
        ...(updateBridge(null) as unknown as Record<string, unknown>),
        actionsList: vi.fn(async () => ({ actions: listed })),
        onActionsChanged: vi.fn((listener: () => void) => {
          listeners.push(listener);
          return () => undefined;
        }),
      };
      return {
        bridge: bridge as unknown as ScoutBridgeService,
        list(next: readonly PendingActionDto[]) {
          listed = next;
        },
        push() {
          listeners.forEach((listener) => listener());
        },
      };
    }

    async function settle(fixture: ComponentFixture<PostConnectShellComponent>): Promise<void> {
      for (let i = 0; i < 3; i++) {
        await fixture.whenStable();
        fixture.detectChanges();
      }
    }

    function badge(fixture: ComponentFixture<PostConnectShellComponent>): HTMLElement | null {
      return fixture.nativeElement.querySelector('[data-test="nav-review-badge"]');
    }

    for (const [listed, count, label] of [
      [[], null, null],
      [[action('a', 'approved'), action('b', 'executing')], null, null],
      [[action('a', 'pending')], '1', '1 action waiting for your approval'],
      [
        [action('a', 'pending'), action('b', 'pending'), action('c', 'approved')],
        '2',
        '2 actions waiting for your approval',
      ],
    ] as const) {
      it(`with ${listed.length} listed actions it shows ${count ?? 'no badge'}`, async () => {
        const { bridge } = approvalsBridge(listed);
        const { fixture } = await setup(bridge);
        await settle(fixture);

        expect(badge(fixture)?.textContent?.trim() ?? null).toBe(count);
        expect(badge(fixture)?.getAttribute('aria-label') ?? null).toBe(label);
        expect(
          badge(fixture) === null ||
            fixture.nativeElement
              .querySelector('[data-test="nav-review"]')
              .contains(badge(fixture)),
        ).toBe(true);
      });
    }

    it('the count sits at the right end of the Approvals entry, a 20 px pill on the warn container', async () => {
      const { bridge } = approvalsBridge([action('a', 'pending')]);
      const { fixture } = await setup(bridge);
      await settle(fixture);
      const pill = badge(fixture)!;

      expect({
        last: fixture.nativeElement.querySelector('[data-test="nav-review"]').lastElementChild,
        margin: declared(pill, 'margin-left'),
        height: declared(pill, 'height'),
        minWidth: declared(pill, 'min-width'),
        radius: declared(pill, 'border-radius'),
        background: declared(pill, 'background'),
        color: declared(pill, 'color'),
      }).toEqual({
        last: pill,
        margin: 'auto',
        height: '20px',
        minWidth: '20px',
        radius: '10px',
        background: 'var(--sc-warn-container)',
        color: 'var(--sc-on-warn-container)',
      });
    });

    it('follows the actions push: a new action shows the badge and the last decision hides it', async () => {
      const approvals = approvalsBridge([]);
      const { fixture } = await setup(approvals.bridge);
      await settle(fixture);
      const before = badge(fixture);

      approvals.list([action('a', 'pending')]);
      approvals.push();
      await settle(fixture);
      const shown = badge(fixture)?.textContent?.trim();
      approvals.list([action('a', 'approved')]);
      approvals.push();
      await settle(fixture);

      expect([before, shown, badge(fixture)]).toEqual([null, '1', null]);
    });

    it('reads the list again every 15 seconds, so an action queued without a push still shows', async () => {
      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
      try {
        const approvals = approvalsBridge([]);
        const { fixture } = await setup(approvals.bridge);
        await settle(fixture);

        approvals.list([action('a', 'pending')]);
        vi.advanceTimersByTime(15_000);
        await settle(fixture);

        expect(badge(fixture)?.textContent?.trim()).toBe('1');
      } finally {
        vi.useRealTimers();
      }
    });
  });
});
