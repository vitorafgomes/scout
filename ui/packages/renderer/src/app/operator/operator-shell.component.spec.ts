import { Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { FeatureDto, PendingActionDto, YouTubeStatus } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { OperatorShellComponent } from './operator-shell.component';

@Component({ selector: 'sc-stub-production', standalone: true, template: 'production-page' })
class StubProduction {}
@Component({ selector: 'sc-stub-assembly', standalone: true, template: 'assembly-page' })
class StubAssembly {}
@Component({ selector: 'sc-stub-approvals', standalone: true, template: 'approvals-page' })
class StubApprovals {}

async function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: 'operator/production', component: StubProduction },
        { path: 'operator/assembly', component: StubAssembly },
        { path: 'operator/approvals', component: StubApprovals },
      ]),
    ],
  });
  const fixture = TestBed.createComponent(OperatorShellComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, router: TestBed.inject(Router) };
}

describe('OperatorShellComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it("its nav shows Production, Assembly, Comments, Reports, Experiments and Approvals, in the artboards' order, and nothing from the main window sidebar", async () => {
    const { fixture } = await setup();

    const links = Array.from(
      fixture.nativeElement.querySelectorAll('.nav-link') as NodeListOf<HTMLElement>,
    ).map((link) => [link.textContent?.trim(), link.getAttribute('href')]);

    expect(links).toEqual([
      ['Production', '/operator/production'],
      ['Assembly', '/operator/assembly'],
      ['Comments', '/operator/comments'],
      ['Reports', '/operator/reports'],
      ['Experiments', '/operator/experiments'],
      ['Approvals', '/operator/approvals'],
    ]);
    expect(fixture.nativeElement.querySelector('[data-test="nav-features"]')).toBeNull();
  });

  it('shows the page of the nav entry the owner opened and marks it active', async () => {
    const { fixture, router } = await setup();

    await router.navigateByUrl('/operator/approvals');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('approvals-page');
    expect(
      fixture.nativeElement.querySelector('.nav-link--active')?.getAttribute('data-test'),
    ).toBe('operator-nav-approvals');
  });

  for (const [url, title] of [
    ['/operator/production', 'Production'],
    ['/operator/assembly', 'Assembly'],
    ['/operator/approvals', 'Approvals'],
  ] as const) {
    it(`the operator window has the same top bar, titled ${title} on ${url}, with the theme switch and the bell`, async () => {
      const { fixture, router } = await setup();

      await router.navigateByUrl(url);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      const bar = fixture.nativeElement.querySelector('sc-top-bar') as HTMLElement;
      const main = fixture.nativeElement.querySelector('main.main') as HTMLElement;
      expect(bar.querySelector('[data-test="top-bar-title"]')?.textContent?.trim()).toBe(title);
      expect(bar.querySelectorAll('[role="group"][aria-label="Theme"] button').length).toBe(3);
      expect(bar.querySelector('sc-notification-bell button')?.getAttribute('aria-label')).toBe(
        'Notifications',
      );
      expect(main.contains(bar)).toBe(false);
      expect(main.classList.contains('sc-dark-canvas')).toBe(false);
      expect(
        Array.from(
          fixture.nativeElement.querySelectorAll(
            '[aria-current="page"]',
          ) as NodeListOf<HTMLElement>,
        ).map((link) => link.getAttribute('data-test')),
      ).toEqual([`operator-nav-${title.toLowerCase()}`]);
    });
  }

  it('every operator nav entry shows its icon before its label', async () => {
    const { fixture } = await setup();

    const firstChildren = Array.from(
      fixture.nativeElement.querySelectorAll('.nav-link') as NodeListOf<HTMLElement>,
    ).map((link) => link.firstElementChild?.tagName.toLowerCase());

    expect(firstChildren).toEqual(Array(6).fill('svg'));
  });
});

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

describe('OperatorShellComponent: the theme per page and the one scroll', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  async function openAt(urls: readonly string[]) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter(
          [
            'production',
            'assembly',
            'reports',
            'experiments',
            'comments',
            'approvals',
            'unknown',
          ].map((page) => ({ path: `operator/${page}`, component: StubProduction })),
        ),
      ],
    });
    const fixture = TestBed.createComponent(OperatorShellComponent);
    const router = TestBed.inject(Router);
    const pinned: boolean[] = [];
    for (const url of urls) {
      await router.navigateByUrl(url);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      pinned.push(
        (fixture.nativeElement.querySelector('main.main') as HTMLElement).classList.contains(
          'sc-dark-canvas',
        ),
      );
    }
    return { host: fixture.nativeElement as HTMLElement, pinned };
  }

  const THEMED = { cls: false, background: 'var(--sc-bg)', color: 'var(--sc-fg)' };
  const PINNED = { cls: true, background: '#0f1117', color: '#e4e8f0' };

  for (const [url, expected] of [
    ['/operator/production', THEMED],
    ['/operator/production?project=p1', THEMED],
    ['/operator/assembly', THEMED],
    ['/operator/reports', THEMED],
    ['/operator/experiments', THEMED],
    ['/operator/comments', THEMED],
    ['/operator/comments#replies', THEMED],
    ['/operator/approvals', THEMED],
    ['/operator/unknown', PINNED],
  ] as const) {
    it(`on ${url} the page area ${expected.cls ? 'stays on the dark canvas' : 'follows the dark and light theme'}`, async () => {
      const { host } = await openAt([url]);

      const main = host.querySelector('main.main') as HTMLElement;
      expect({
        cls: main.classList.contains('sc-dark-canvas'),
        background: declared(main, 'background'),
        color: declared(main, 'color'),
      }).toEqual(expected);
    });
  }

  it('the page area follows the theme on every operator page, Approvals included, from one page to the next', async () => {
    const { pinned } = await openAt([
      '/operator/production',
      '/operator/approvals',
      '/operator/comments',
      '/operator/approvals',
      '/operator/reports',
    ]);

    expect(pinned).toEqual([false, false, false, false, false]);
  });

  it('the operator top bar stays in view: the window is fixed, only the page area scrolls, the top bar sits outside it', async () => {
    const { host } = await openAt(['/operator/production']);
    const main = host.querySelector('main.main')!;

    expect(declared(host, 'position')).toBe('fixed');
    expect(declared(host, 'inset')).toBe('0');
    expect(declared(host, 'min-height')).toBeNull();
    expect(declared(host.querySelector('.shell')!, 'height')).toBe('100%');
    expect(declared(host.querySelector('.shell')!, 'min-height')).toBeNull();
    expect(declared(main, 'overflow-y')).toBe('auto');
    expect(declared(host.querySelector('.content')!, 'overflow-y')).toBeNull();
    expect(main.contains(host.querySelector('sc-top-bar'))).toBe(false);
  });
});

describe('The operator rail as in the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  async function railWith(status: () => Promise<YouTubeStatus>) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'operator/production', component: StubProduction }]),
      ],
    });
    const youtubeStatus = vi
      .spyOn(TestBed.inject(ScoutBridgeService), 'youtubeStatus')
      .mockImplementation(status);
    const fixture = TestBed.createComponent(OperatorShellComponent);
    await TestBed.inject(Router).navigateByUrl('/operator/production');
    for (let i = 0; i < 3; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
    return {
      host: fixture.nativeElement as HTMLElement,
      youtubeStatus,
      shell: fixture.componentInstance,
    };
  }

  const signedIn = {
    configured: true,
    connected: true,
    method: 'oauth',
    channelId: 'UC1',
    channelTitle: 'Acme Tech Explained',
    pending: false,
    error: null,
    clientConfigPath: '',
    source: 'bundled',
    scopes: [],
  } satisfies YouTubeStatus;

  it('the brand is a tile with the video icon, YouTube operator, and the signed-in channel under it', async () => {
    const { host } = await railWith(async () => signedIn);
    const brand = host.querySelector('[data-test="operator-brand"]')!;
    const tile = brand.querySelector('.brand-tile')!;

    expect([
      tile.getAttribute('aria-hidden'),
      tile.firstElementChild?.tagName.toLowerCase(),
      declared(tile, 'background'),
      brand.querySelector('.brand-name')?.textContent?.trim(),
      brand.querySelector('[data-test="operator-channel"]')?.textContent?.trim(),
    ]).toEqual([
      'true',
      'sc-icon',
      'var(--sc-accent-strong)',
      'YouTube operator',
      'Acme Tech Explained',
    ]);
  });

  it('when the YouTube status cannot be read the brand still shows, without a channel line', async () => {
    const { host, youtubeStatus, shell } = await railWith(async () => {
      throw new Error('the main process did not answer');
    });
    const calledOnLoad = youtubeStatus.mock.calls.length;

    await expect(shell.ngOnInit()).resolves.toBeUndefined();
    expect(calledOnLoad).toBe(1);
    expect(
      host.querySelector('[data-test="operator-brand"] .brand-name')?.textContent?.trim(),
    ).toBe('YouTube operator');
    expect(host.querySelector('[data-test="operator-channel"]')).toBeNull();
  });

  it('the active item sits on surface-2 without the accent bar', async () => {
    const { host } = await railWith(async () => signedIn);
    const active = host.querySelector('.nav-link--active')!;

    expect([
      active.getAttribute('data-test'),
      declared(active, 'background'),
      declared(active, 'box-shadow'),
    ]).toEqual(['operator-nav-production', 'var(--sc-surface-2)', null]);
  });
});

describe('The operator shell as the five operator artboards draw it, state by state', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function feature(id: string, granted: boolean): FeatureDto {
    return { id, granted } as unknown as FeatureDto;
  }

  function action(id: string, featureId: string, status = 'pending'): PendingActionDto {
    return {
      id,
      featureId,
      action: 'videos.insert',
      target: 't',
      summary: 's',
      requestedAt: '2026-11-10T10:00:00Z',
      status,
      decidedAt: null,
    };
  }

  async function shellWith(opts: {
    features?: () => Promise<{ features: FeatureDto[] }>;
    actions?: () => Promise<{ actions: PendingActionDto[] }>;
    showMain?: () => Promise<void>;
  }) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'operator/production', component: StubProduction }]),
      ],
    });
    const bridge = TestBed.inject(ScoutBridgeService);
    let featuresChanged: () => void = () => undefined;
    let actionsChanged: () => void = () => undefined;
    vi.spyOn(bridge, 'youtubeStatus').mockRejectedValue(new Error('no status'));
    const featuresList = vi
      .spyOn(bridge, 'featuresList')
      .mockImplementation(opts.features ?? (async () => ({ features: [] })));
    vi.spyOn(bridge, 'actionsList').mockImplementation(
      opts.actions ?? (async () => ({ actions: [] })),
    );
    vi.spyOn(bridge, 'onFeaturesChanged').mockImplementation((listener) => {
      featuresChanged = listener;
      return () => undefined;
    });
    vi.spyOn(bridge, 'onActionsChanged').mockImplementation((listener) => {
      actionsChanged = listener;
      return () => undefined;
    });
    const showMain = vi
      .spyOn(bridge, 'appShowMainWindow')
      .mockImplementation(opts.showMain ?? (async () => undefined));
    const fixture = TestBed.createComponent(OperatorShellComponent);
    await TestBed.inject(Router).navigateByUrl('/operator/production');
    const settle = async () => {
      for (let i = 0; i < 3; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
      fixture.detectChanges();
    };
    await settle();
    return {
      host: fixture.nativeElement as HTMLElement,
      shell: fixture.componentInstance,
      featuresList,
      showMain,
      settle,
      featuresChanged: () => featuresChanged(),
      actionsChanged: () => actionsChanged(),
    };
  }

  for (const [label, glyph] of [
    ['Production', 'M3 6h18v12H3zM10 9l5 3-5 3z'],
    ['Assembly', 'M4 4h16v6H4zM4 14h7v6H4zM15 14h5v6h-5z'],
    ['Comments', 'M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z'],
    ['Reports', 'M4 20V10M10 20V4M16 20v-7M22 20H2'],
    ['Experiments', 'M9 3h6M10 3v6l-5 9a2 2 0 0 0 1.7 3h10.6a2 2 0 0 0 1.7-3l-5-9V3'],
    ['Approvals', 'M9 11l3 3 8-8M20 12v7a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h9'],
  ] as const) {
    it(`${label} carries its own icon, the artboards' 18 px stroke glyph at 1.8, hidden from a screen reader`, async () => {
      const { host } = await shellWith({});
      const link = Array.from(host.querySelectorAll('.nav-link')).find(
        (node) => node.textContent?.trim() === label,
      )!;
      const svg = link.querySelector('svg')!;

      expect([
        svg.getAttribute('width'),
        svg.getAttribute('stroke-width'),
        svg.getAttribute('aria-hidden'),
        svg.querySelector('path')?.getAttribute('d'),
      ]).toEqual(['18', '1.8', 'true', glyph]);
    });
  }

  it('the rail is 16 px by 12 px with its entries 4 px apart, each entry 9 px by 10 px at 500, the active one at 600 on surface-2', async () => {
    const { host } = await shellWith({});
    const rail = host.querySelector('nav.rail')!;
    const idle = host.querySelector('[data-test="operator-nav-assembly"]')!;
    const active = host.querySelector('[data-test="operator-nav-production"]')!;

    expect({
      railPadding: declared(rail, 'padding'),
      railGap: declared(rail, 'gap'),
      padding: declared(idle, 'padding'),
      weight: declared(idle, 'font-weight'),
      color: declared(idle, 'color'),
      activeWeight: declared(active, 'font-weight'),
      activeBackground: declared(active, 'background'),
    }).toEqual({
      railPadding: '16px 12px',
      railGap: '4px',
      padding: '9px 10px',
      weight: '500',
      color: 'var(--sc-fg-2)',
      activeWeight: '600',
      activeBackground: 'var(--sc-surface-2)',
    });
  });

  it("the rail has Back to Scout with the artboards' arrow: a button, the 15 px stroke arrow at 2, hidden from a screen reader", async () => {
    const { host } = await shellWith({});
    const back = host.querySelector('[data-test="operator-back-to-scout"]')!;
    const svg = back.querySelector('svg')!;

    expect({
      tag: back.tagName.toLowerCase(),
      type: back.getAttribute('type'),
      label: back.textContent?.trim(),
      first: back.firstElementChild?.tagName.toLowerCase(),
      width: svg.getAttribute('width'),
      height: svg.getAttribute('height'),
      viewBox: svg.getAttribute('viewBox'),
      stroke: svg.getAttribute('stroke'),
      strokeWidth: svg.getAttribute('stroke-width'),
      caps: svg.getAttribute('stroke-linecap'),
      joins: svg.getAttribute('stroke-linejoin'),
      hidden: svg.getAttribute('aria-hidden'),
      glyph: svg.querySelector('path')?.getAttribute('d'),
    }).toEqual({
      tag: 'button',
      type: 'button',
      label: 'Back to Scout',
      first: 'svg',
      width: '15',
      height: '15',
      viewBox: '0 0 24 24',
      stroke: 'currentColor',
      strokeWidth: '2',
      caps: 'round',
      joins: 'round',
      hidden: 'true',
      glyph: 'M19 12H5M12 19l-7-7 7-7',
    });
  });

  it('Back to Scout sits under the brand, before Production', async () => {
    const { host } = await shellWith({});
    const back = host.querySelector('[data-test="operator-back-to-scout"]')!;

    expect({
      inRail: back.parentElement?.classList.contains('rail'),
      after: back.previousElementSibling?.getAttribute('data-test'),
      before: back.nextElementSibling?.classList.contains('nav-list'),
      firstEntry: back.nextElementSibling?.querySelector('.nav-link')?.textContent?.trim(),
    }).toEqual({
      inRail: true,
      after: 'operator-brand',
      before: true,
      firstEntry: 'Production',
    });
  });

  it("Back to Scout is the artboards' link: 13 px in the accent colour, no underline, 6/10/14 padding, the arrow 8 px from the words, under a brand padded 4 px below", async () => {
    const { host } = await shellWith({});
    const back = host.querySelector('[data-test="operator-back-to-scout"]')!;

    expect({
      display: declared(back, 'display'),
      gap: declared(back, 'gap'),
      padding: declared(back, 'padding'),
      size: declared(back, 'font-size'),
      color: declared(back, 'color'),
      decoration: declared(back, 'text-decoration'),
      border: declared(back, 'border'),
      background: declared(back, 'background'),
      outline: declared(back, 'outline'),
      brandPadding: declared(host.querySelector('[data-test="operator-brand"]')!, 'padding'),
    }).toEqual({
      display: 'flex',
      gap: '8px',
      padding: '6px 10px 14px',
      size: '13px',
      color: 'var(--sc-accent)',
      decoration: 'none',
      border: '0',
      background: 'none',
      outline: null,
      brandPadding: '4px 8px 4px',
    });
  });

  for (const [state, granted] of [
    ['YouTube granted', true],
    ['YouTube not granted', false],
  ] as const) {
    it(`Back to Scout shows: ${state}`, async () => {
      const { host } = await shellWith({
        features: async () => ({ features: [feature('youtube', granted)] }),
      });

      expect({
        footer: host.querySelector('[data-test="operator-granted"]') !== null,
        back: host.querySelector('[data-test="operator-back-to-scout"]')?.textContent?.trim(),
      }).toEqual({ footer: granted, back: 'Back to Scout' });
    });
  }

  it('a click asks main to show the Scout window, once, and leaves the operator window on its page', async () => {
    const { host, showMain, settle } = await shellWith({});

    (host.querySelector('[data-test="operator-back-to-scout"]') as HTMLButtonElement).click();
    await settle();

    expect(showMain).toHaveBeenCalledTimes(1);
    expect(showMain).toHaveBeenCalledWith();
    expect(TestBed.inject(Router).url).toBe('/operator/production');
  });

  it('when main does not answer, Back to Scout throws nothing to the page and the rail stays whole', async () => {
    const { host, shell, showMain, settle } = await shellWith({
      showMain: async () => {
        throw new Error('The main process did not answer.');
      },
    });

    await expect(shell.showScout()).resolves.toBeUndefined();
    await settle();

    expect(showMain).toHaveBeenCalledTimes(1);
    expect(host.querySelector('[data-test="operator-back-to-scout"]')?.textContent?.trim()).toBe(
      'Back to Scout',
    );
    expect(host.querySelectorAll('.nav-link').length).toBe(6);
  });

  for (const [state, features, shown] of [
    [
      'granted',
      async () => ({ features: [feature('github', false), feature('youtube', true)] }),
      true,
    ],
    [
      'not granted',
      async () => ({ features: [feature('github', true), feature('youtube', false)] }),
      false,
    ],
    ['not listed', async () => ({ features: [feature('github', true)] }), false],
    [
      'unreadable',
      async () => {
        throw new Error('The Scout control plane did not answer.');
      },
      false,
    ],
  ] as const) {
    it(`the footer says "YouTube feature granted" only while YouTube's writes are granted: ${state}`, async () => {
      const { host } = await shellWith({ features });
      const footer = host.querySelector('[data-test="operator-granted"]');

      expect(footer?.textContent?.trim() ?? null).toBe(shown ? 'YouTube feature granted' : null);
      expect(host.querySelector('[data-test="operator-nav-production"]')).not.toBeNull();
    });
  }

  it("the footer is the artboards' line: at the foot of the rail, 12 px, the ok colour, a check before the words", async () => {
    const { host } = await shellWith({
      features: async () => ({ features: [feature('youtube', true)] }),
    });
    const footer = host.querySelector('[data-test="operator-granted"]')!;

    expect({
      inRail: footer.parentElement?.classList.contains('rail'),
      after: footer.previousElementSibling?.classList.contains('nav-list'),
      marginTop: declared(footer, 'margin-top'),
      size: declared(footer, 'font-size'),
      color: declared(footer, 'color'),
      glyph: footer.querySelector('svg path')?.getAttribute('d'),
    }).toEqual({
      inRail: true,
      after: true,
      marginTop: 'auto',
      size: '12px',
      color: 'var(--sc-on-ok-container)',
      glyph: 'M20 6L9 17l-5-5',
    });
  });

  it('the footer follows a grant made or revoked in the Scout window, read again when main says the features changed', async () => {
    let granted = false;
    const { host, settle, featuresChanged, featuresList } = await shellWith({
      features: async () => ({ features: [feature('youtube', granted)] }),
    });
    const before = host.querySelector('[data-test="operator-granted"]');
    granted = true;
    featuresChanged();
    await settle();

    expect(before).toBeNull();
    expect(featuresList).toHaveBeenCalledTimes(2);
    expect(host.querySelector('[data-test="operator-granted"]')?.textContent?.trim()).toBe(
      'YouTube feature granted',
    );
  });

  for (const [state, actions, badge] of [
    [
      'two YouTube actions wait, beside a GitHub one',
      [action('a1', 'youtube'), action('a2', 'youtube'), action('a3', 'github')],
      ['2', '2 actions waiting for your approval'],
    ],
    [
      'one YouTube action waits',
      [action('a1', 'youtube')],
      ['1', '1 action waiting for your approval'],
    ],
    ['only a GitHub action waits', [action('a3', 'github')], null],
    ['a YouTube action already decided', [action('a1', 'youtube', 'approved')], null],
    ['nothing waits', [], null],
  ] as const) {
    it(`Approvals carries the count of the YouTube actions its page lists, as the main window's badge: ${state}`, async () => {
      const { host } = await shellWith({ actions: async () => ({ actions: [...actions] }) });
      const node = host.querySelector('[data-test="operator-nav-approvals-badge"]');

      expect(
        node === null ? null : [node.textContent?.trim(), node.getAttribute('aria-label')],
      ).toEqual(badge === null ? null : [...badge]);
    });
  }

  it("the badge is the main window's pill: at the right of the entry, 20 px, the warn container pair, 12 px at 600", async () => {
    const { host } = await shellWith({
      actions: async () => ({ actions: [action('a1', 'youtube')] }),
    });
    const node = host.querySelector('[data-test="operator-nav-approvals-badge"]')!;

    expect({
      inEntry: node.parentElement?.getAttribute('data-test'),
      role: node.getAttribute('role'),
      margin: declared(node, 'margin-left'),
      height: declared(node, 'height'),
      background: declared(node, 'background'),
      color: declared(node, 'color'),
      size: declared(node, 'font-size'),
      weight: declared(node, 'font-weight'),
    }).toEqual({
      inEntry: 'operator-nav-approvals',
      role: 'img',
      margin: 'auto',
      height: '20px',
      background: 'var(--sc-warn-container)',
      color: 'var(--sc-on-warn-container)',
      size: '12px',
      weight: '600',
    });
  });

  it('the badge follows a decision taken in any window: main says the actions changed and the count is read again', async () => {
    let listed = [action('a1', 'youtube'), action('a2', 'youtube')];
    const { host, settle, actionsChanged } = await shellWith({
      actions: async () => ({ actions: listed }),
    });
    const before = host.querySelector('[data-test="operator-nav-approvals-badge"]')?.textContent;
    listed = [action('a2', 'youtube')];
    actionsChanged();
    await settle();

    expect([
      before?.trim(),
      host.querySelector('[data-test="operator-nav-approvals-badge"]')?.textContent?.trim(),
    ]).toEqual(['2', '1']);
  });

  it('when the actions cannot be read the rail shows no badge and every entry stays', async () => {
    const { host } = await shellWith({
      actions: async () => {
        throw new Error('The Scout control plane did not answer.');
      },
    });

    expect(host.querySelector('[data-test="operator-nav-approvals-badge"]')).toBeNull();
    expect(host.querySelectorAll('.nav-link').length).toBe(6);
  });
});
