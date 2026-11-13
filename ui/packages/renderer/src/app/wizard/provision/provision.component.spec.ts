import { readFileSync } from 'node:fs';

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ExternalHostNotAllowedError } from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { ProvisionComponent } from './provision.component';

function makeBridge(overrides: Partial<Record<keyof ScoutBridgeService, unknown>> = {}) {
  return {
    appOpenExternal: vi.fn().mockResolvedValue(undefined),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    ...overrides,
  } as unknown as ScoutBridgeService;
}

async function setup(bridge: ScoutBridgeService) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([{ path: 'token', children: [] }]),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const fixture = TestBed.createComponent(ProvisionComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

function openButton(fixture: Awaited<ReturnType<typeof setup>>): HTMLButtonElement {
  return fixture.nativeElement.querySelector('[data-test="open-portal"]') as HTMLButtonElement;
}

function continueButton(fixture: Awaited<ReturnType<typeof setup>>): HTMLButtonElement {
  return fixture.nativeElement.querySelector('[data-test="wizard-continue"]') as HTMLButtonElement;
}

describe('ProvisionComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('calls appOpenExternal with the Discord Developer Portal URL', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);

    const btn = fixture.nativeElement.querySelector(
      '[data-test="open-portal"]',
    ) as HTMLButtonElement;
    btn.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({
      url: 'https://discord.com/developers/applications',
    });
    expect(fixture.componentInstance.portalOpened()).toBe(true);
  });

  it('surfaces the error and the copy-URL fallback when openExternal rejects', async () => {
    const bridge = makeBridge({
      appOpenExternal: vi
        .fn()
        .mockRejectedValue(new ExternalHostNotAllowedError('discord.com', 'blocked')),
    });
    const fixture = await setup(bridge);

    const openBtn = fixture.nativeElement.querySelector(
      '[data-test="open-portal"]',
    ) as HTMLButtonElement;
    openBtn.click();
    await fixture.whenStable();
    fixture.detectChanges();

    const errorBlock = fixture.nativeElement.querySelector('.error-block');
    expect(errorBlock).not.toBeNull();
    expect(errorBlock.textContent).toContain('The Developer Portal did not open.');
    const url = fixture.nativeElement.querySelector('.url');
    expect(url.textContent).toContain('https://discord.com/developers/applications');
  });

  it('navigates to /token on Continue, and Back returns to the chat app step', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);
    const router = TestBed.inject(Router);
    const spy = vi.spyOn(router, 'navigateByUrl');

    continueButton(fixture).click();
    (fixture.nativeElement.querySelector('[data-test="wizard-back"]') as HTMLButtonElement).click();

    expect(spy.mock.calls).toEqual([['/token'], ['/']]);
  });

  describe('Continue works whether or not the portal opened here, and is the main action once it did', () => {
    const cases: readonly {
      readonly opened: 'no' | 'yes' | 'failed';
      readonly primary: boolean;
      readonly portal: string;
    }[] = [
      { opened: 'no', primary: false, portal: 'Open Discord Developer Portal' },
      { opened: 'yes', primary: true, portal: 'Open the portal again' },
      { opened: 'failed', primary: false, portal: 'Open Discord Developer Portal' },
    ];

    for (const { opened, primary, portal } of cases) {
      it(`portal ${opened}: Continue enabled and ${primary ? 'primary' : 'secondary'}, the portal button reads "${portal}"`, async () => {
        const bridge = makeBridge(
          opened === 'failed'
            ? { appOpenExternal: vi.fn().mockRejectedValue(new Error('no browser')) }
            : {},
        );
        const fixture = await setup(bridge);

        if (opened !== 'no') {
          openButton(fixture).click();
          await fixture.whenStable();
          fixture.detectChanges();
        }

        expect([
          continueButton(fixture).disabled,
          continueButton(fixture).classList.contains('btn--primary'),
          openButton(fixture).textContent?.trim(),
          (fixture.nativeElement.querySelector('[data-test="wizard-hint"]') as HTMLElement)
            .textContent,
        ]).toEqual([
          false,
          primary,
          portal,
          'Continue works whether or not the portal opened here.',
        ]);
      });
    }
  });
});

/** The colour literals left in a component's styles, read from the renderer root: hex, rgb(), hsl() and the named white and black. */
function colourLiterals(relative: string): string[] {
  const source = readFileSync(relative, 'utf8');
  const start = source.indexOf('  styles:');
  const css = source.slice(start, source.indexOf('\n})\n', start));
  expect(start, `${relative} declares its styles`).toBeGreaterThanOrEqual(0);
  expect(css, `${relative} paints with the theme tokens`).toContain('var(--sc-');
  return Array.from(
    css.matchAll(
      /#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(|(?<![\w-])(?:white|black)(?![\w-])/g,
    ),
    (match) => match[0],
  );
}

describe("The wizard's Create bot page follows the theme and the prototype", () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  for (const file of ['src/app/wizard/provision/provision.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }

  it('Open Discord Developer Portal shows its icon before its text, and Continue its arrow after it, as the prototype draws them', async () => {
    const fixture = await setup(makeBridge());
    const next = continueButton(fixture);

    expect(
      iconAndText(fixture.nativeElement as HTMLElement, ['[data-test="open-portal"]']),
    ).toEqual([['sc-icon', 'Open Discord Developer Portal']]);
    expect([
      next.textContent?.trim(),
      next.querySelector('.mdc-button__label')?.lastElementChild?.tagName.toLowerCase(),
    ]).toEqual(['Continue', 'sc-icon']);
  });
});

describe('The Create bot step as in the prototype: Portal not opened, Portal opened, Could not open', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  const rows: readonly {
    readonly state: string;
    readonly open: 'not yet' | 'opens' | 'fails';
    readonly portal: readonly [string, string];
    readonly next: string;
    readonly failed: readonly string[] | null;
  }[] = [
    {
      state: 'Portal not opened',
      open: 'not yet',
      portal: ['Open Discord Developer Portal', 'mat-mdc-unelevated-button'],
      next: 'mat-mdc-outlined-button',
      failed: null,
    },
    {
      state: 'Portal opened',
      open: 'opens',
      portal: ['Open the portal again', 'mat-mdc-outlined-button'],
      next: 'mat-mdc-unelevated-button',
      failed: null,
    },
    {
      state: 'Could not open',
      open: 'fails',
      portal: ['Open Discord Developer Portal', 'mat-mdc-unelevated-button'],
      next: 'mat-mdc-outlined-button',
      failed: [
        'The Developer Portal did not open.',
        'Open this address in any browser, then come back and continue:',
        'https://discord.com/developers/applications',
        'Copy address',
      ],
    },
  ];

  for (const row of rows) {
    it(`${row.state}: the three portal steps and the Reset Token warning in one card, the portal button "${row.portal[0]}", ${row.failed === null ? 'no failure box' : 'the address to open by hand and no raw error'}`, async () => {
      const fixture = await setup(
        makeBridge(
          row.open === 'fails'
            ? { appOpenExternal: vi.fn().mockRejectedValue(new Error('spawn xdg-open ENOENT')) }
            : {},
        ),
      );
      if (row.open !== 'not yet') {
        openButton(fixture).click();
        await fixture.whenStable();
        fixture.detectChanges();
      }
      const root = fixture.nativeElement as HTMLElement;
      const text = (selector: string): string | undefined =>
        root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
      const column = getComputedStyle(root.querySelector('main') as HTMLElement);
      const failed = root.querySelector('[data-test="portal-failed"]');

      expect({
        title: text('h1'),
        lede: text('.lede'),
        column: [column.maxWidth, column.marginLeft, column.marginRight],
        steps: Array.from(root.querySelectorAll('.card [data-test="portal-steps"] li'), (li) =>
          li.textContent?.trim(),
        ),
        warning: text('.card [data-test="reset-warning"]'),
        portal: [
          text('[data-test="open-portal"]'),
          openButton(fixture).classList.contains(row.portal[1]),
        ],
        aside: text('.portal .muted'),
        next: continueButton(fixture).classList.contains(row.next),
        failed:
          failed === null
            ? null
            : [
                failed.querySelector('.alert-title')?.textContent?.trim(),
                failed.querySelector('.alert-detail')?.textContent?.trim(),
                failed.querySelector('code')?.textContent?.trim(),
                failed.querySelector('[data-test="copy-url"]')?.textContent?.trim(),
              ],
        rawError: root.textContent?.includes('ENOENT'),
      }).toEqual({
        title: 'Create your Discord bot',
        lede: 'Scout talks in Discord through a bot you own. Create it in the Discord Developer Portal.',
        column: ['760px', 'auto', 'auto'],
        steps: [
          'Click New Application and name it Scout.',
          'Open Bot and click Reset Token.',
          'Click Copy, then come back here.',
        ],
        warning:
          'Reset Token stops the old token working. If this bot already runs somewhere else, that copy goes offline.',
        portal: [row.portal[0], true],
        aside: 'Already have a bot token? Continue straight away.',
        next: true,
        failed: row.failed,
        rawError: false,
      });
    });
  }

  it('Could not open: Copy address copies the portal address and then reads Copied', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const fixture = await setup(
      makeBridge({ appOpenExternal: vi.fn().mockRejectedValue(new Error('no browser')) }),
    );
    openButton(fixture).click();
    await fixture.whenStable();
    fixture.detectChanges();
    const copy = (): HTMLButtonElement =>
      fixture.nativeElement.querySelector('[data-test="copy-url"]') as HTMLButtonElement;

    copy().click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect([writeText.mock.calls, copy().textContent?.trim()]).toEqual([
      [['https://discord.com/developers/applications']],
      'Copied',
    ]);
  });
});

/** The first tag inside each named Material button's label and its text, so a main action is checked for its icon before its label. */
function iconAndText(root: HTMLElement, selectors: readonly string[]): (string | undefined)[][] {
  return selectors.map((selector) => {
    const button = root.querySelector(selector) as HTMLElement | null;
    return [
      button?.querySelector('.mdc-button__label')?.firstElementChild?.tagName.toLowerCase(),
      (button?.textContent ?? '').replace(/\s+/g, ' ').trim(),
    ];
  });
}
