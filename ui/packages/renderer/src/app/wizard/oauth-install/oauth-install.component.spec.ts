import { readFileSync } from 'node:fs';

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ScoutError, encodeIpcErrorMessage, serializeError } from '@scout/shared';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { PersistentStateService } from '../../core/persistent-state';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { OAuthInstallComponent } from './oauth-install.component';

const INVITE =
  'https://discord.com/oauth2/authorize?client_id=111222333&scope=bot&permissions=274877991936';
const OLD = { id: '1', name: 'Old Server' };
const NEW = { id: '2', name: 'Team' };

function ipcError(code: string, message: string): Error {
  return new Error(encodeIpcErrorMessage(serializeError(new ScoutError(code, message))));
}

function makeBridge(guilds: ReturnType<typeof vi.fn>) {
  return {
    discordGuilds: guilds,
    discordInviteUrl: vi.fn().mockResolvedValue({ url: INVITE }),
    appOpenExternal: vi.fn().mockResolvedValue(undefined),
    vaultStore: vi.fn().mockResolvedValue(undefined),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
  } as unknown as ScoutBridgeService & Record<string, ReturnType<typeof vi.fn>>;
}

async function setup(bridge: ScoutBridgeService, clientId: string | null = '111222333') {
  const persistent = { get: vi.fn().mockReturnValue(null), set: vi.fn(), clear: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: '', children: [] },
        { path: 'token', children: [] },
        { path: 'launch', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: PersistentStateService, useValue: persistent },
    ],
  });
  if (clientId !== null) TestBed.inject(WizardStore).setClientId(clientId);
  const nav = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');
  const fixture = TestBed.createComponent(OAuthInstallComponent);
  fixture.detectChanges();
  await vi.advanceTimersByTimeAsync(0);
  fixture.detectChanges();
  return { fixture, nav, persistent };
}

describe('OAuthInstallComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('as in the prototype: Continue waits, with its reason, until Scout is in a server, and Back returns to the token', async () => {
    const bridge = makeBridge(vi.fn().mockResolvedValue({ guilds: [OLD] }));
    const { fixture, nav } = await setup(bridge);
    const next = () =>
      fixture.nativeElement.querySelector('[data-test="wizard-continue"]') as HTMLButtonElement;
    const blocked = [
      next().disabled,
      fixture.nativeElement.querySelector('[data-test="wizard-blocked"]')?.textContent,
    ];

    (
      fixture.nativeElement.querySelector('[data-test="guild-option"]') as HTMLButtonElement
    ).click();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    const joined = next().disabled;
    (fixture.nativeElement.querySelector('[data-test="wizard-back"]') as HTMLButtonElement).click();

    expect(blocked).toEqual([true, 'Add Scout to a server, or pick one it is already in.']);
    expect(joined).toBe(false);
    expect(nav).toHaveBeenCalledWith('/token');
  });

  it('opens the invite and continues when the bot appears in a new server, storing it', async () => {
    const guilds = vi
      .fn()
      .mockResolvedValueOnce({ guilds: [OLD] })
      .mockResolvedValueOnce({ guilds: [OLD] })
      .mockResolvedValueOnce({ guilds: [OLD, NEW] });
    const bridge = makeBridge(guilds);
    const { fixture, nav, persistent } = await setup(bridge);

    await fixture.componentInstance.openInvite();
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: INVITE });
    await vi.advanceTimersByTimeAsync(3_000);
    expect(nav).not.toHaveBeenCalledWith('/launch');
    await vi.advanceTimersByTimeAsync(3_000);
    fixture.detectChanges();

    expect(bridge.vaultStore).toHaveBeenCalledWith({ key: 'discord.guild-id', value: '2' });
    expect(bridge.vaultStore).toHaveBeenCalledWith({ key: 'discord.guild-name', value: 'Team' });
    expect(persistent.set).toHaveBeenCalledWith({
      clientId: '111222333',
      guildId: '2',
      guildName: 'Team',
    });
    expect(TestBed.inject(WizardStore).guild()).toEqual(NEW);
    expect(fixture.nativeElement.querySelector('[data-test="joined"]').textContent).toContain(
      'Scout joined Team',
    );
    expect(nav).toHaveBeenCalledWith('/launch');
  });

  it('offers the servers the bot is already in and stores the one the owner picks', async () => {
    const bridge = makeBridge(vi.fn().mockResolvedValue({ guilds: [OLD] }));
    const { fixture, nav } = await setup(bridge);

    const option = fixture.nativeElement.querySelector(
      '[data-test="guild-option"]',
    ) as HTMLButtonElement;
    expect(option.textContent?.trim()).toBe('Old Server');
    option.click();
    await vi.advanceTimersByTimeAsync(0);

    expect(bridge.vaultStore).toHaveBeenCalledWith({ key: 'discord.guild-id', value: '1' });
    expect(nav).toHaveBeenCalledWith('/launch');
  });

  it('never picks a server the bot was already in when the first server list was slow', async () => {
    let releaseFirst: (value: { guilds: (typeof OLD)[] }) => void = () => undefined;
    const guilds = vi
      .fn()
      .mockReturnValueOnce(new Promise((resolve) => (releaseFirst = resolve)))
      .mockResolvedValue({ guilds: [OLD] });
    const bridge = makeBridge(guilds);
    const { fixture, nav } = await setup(bridge);

    const invite = fixture.componentInstance.openInvite();
    await vi.advanceTimersByTimeAsync(3_000 * 2);
    releaseFirst({ guilds: [OLD] });
    await invite;
    await vi.advanceTimersByTimeAsync(3_000 * 3);

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: INVITE });
    expect(bridge.vaultStore).not.toHaveBeenCalled();
    expect(nav).not.toHaveBeenCalledWith('/launch');
  });

  it('does not open the invite while the current server list cannot be read', async () => {
    const guilds = vi
      .fn()
      .mockRejectedValue(ipcError('discord/network-error', 'Could not reach Discord.'));
    const bridge = makeBridge(guilds);
    const { fixture, nav } = await setup(bridge);

    await fixture.componentInstance.openInvite();
    guilds.mockResolvedValue({ guilds: [OLD] });
    await vi.advanceTimersByTimeAsync(3_000 * 3);
    fixture.detectChanges();

    expect(bridge.appOpenExternal).not.toHaveBeenCalled();
    expect(bridge.vaultStore).not.toHaveBeenCalled();
    expect(nav).not.toHaveBeenCalledWith('/launch');
    expect(fixture.nativeElement.querySelector('[data-test="error-copy"]').textContent).toContain(
      'Could not reach Discord.',
    );
  });

  it('keeps polling through a network error and stops at the bound', async () => {
    const guilds = vi
      .fn()
      .mockResolvedValueOnce({ guilds: [] })
      .mockRejectedValue(ipcError('discord/network-error', 'Could not reach Discord.'));
    const bridge = makeBridge(guilds);
    const { fixture } = await setup(bridge);

    await fixture.componentInstance.openInvite();
    await vi.advanceTimersByTimeAsync(3_000 * 120);
    fixture.detectChanges();

    expect(guilds).toHaveBeenCalledTimes(1 + 100);
    expect(fixture.componentInstance.waiting()).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-test="error-copy"]').textContent).toContain(
      'Scout has not joined a server yet',
    );
  });

  it('stops polling and says so when Discord rejects the bot token', async () => {
    const guilds = vi
      .fn()
      .mockResolvedValueOnce({ guilds: [] })
      .mockRejectedValue(ipcError('discord/token-rejected', 'Discord rejected this bot token.'));
    const bridge = makeBridge(guilds);
    const { fixture } = await setup(bridge);

    await fixture.componentInstance.openInvite();
    await vi.advanceTimersByTimeAsync(3_000 * 5);
    fixture.detectChanges();

    expect(guilds).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.querySelector('[data-test="error-copy"]').textContent).toContain(
      'Discord rejected this bot token.',
    );
  });

  it('stops polling when the step is left', async () => {
    const guilds = vi.fn().mockResolvedValue({ guilds: [] });
    const bridge = makeBridge(guilds);
    const { fixture } = await setup(bridge);

    await fixture.componentInstance.openInvite();
    fixture.destroy();
    await vi.advanceTimersByTimeAsync(3_000 * 10);

    expect(guilds).toHaveBeenCalledTimes(1);
  });

  it('a double click on Add Scout to a server opens the invite once', async () => {
    const bridge = makeBridge(vi.fn().mockResolvedValue({ guilds: [OLD] }));
    const { fixture } = await setup(bridge);
    const invite = fixture.nativeElement.querySelector('[data-test="invite"]') as HTMLButtonElement;

    invite.click();
    invite.click();
    await vi.advanceTimersByTimeAsync(0);

    expect(bridge.appOpenExternal).toHaveBeenCalledTimes(1);
  });

  it('after a double click on Add Scout to a server, Discord is asked for the servers once per interval', async () => {
    const guilds = vi.fn().mockResolvedValue({ guilds: [OLD] });
    const bridge = makeBridge(guilds);
    const { fixture } = await setup(bridge);
    const invite = fixture.nativeElement.querySelector('[data-test="invite"]') as HTMLButtonElement;

    invite.click();
    invite.click();
    await vi.advanceTimersByTimeAsync(0);
    const baseline = guilds.mock.calls.length;
    await vi.advanceTimersByTimeAsync(3_000 * 2);

    expect(guilds.mock.calls.length - baseline).toBe(2);
  });

  it('redirects to / when no application id is known', async () => {
    const bridge = makeBridge(vi.fn().mockResolvedValue({ guilds: [] }));
    const { nav } = await setup(bridge, null);

    expect(nav).toHaveBeenCalledWith('/');
    expect(bridge.discordGuilds).not.toHaveBeenCalled();
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

describe("The wizard's Add to server page follows the theme and the prototype", () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const file of ['src/app/wizard/oauth-install/oauth-install.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }

  it('Add Scout to a server shows its icon before its text, as the prototype draws it', async () => {
    vi.useFakeTimers();
    const { fixture } = await setup(makeBridge(vi.fn().mockResolvedValue({ guilds: [] })));
    const shown = iconAndText(fixture.nativeElement as HTMLElement, ['[data-test="invite"]']);
    fixture.destroy();
    vi.useRealTimers();

    expect(shown).toEqual([['sc-icon', 'Add Scout to a server']]);
  });
});

describe('The Add to server step as in the prototype: Not started, Waiting, Joined', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const ALREADY = [
    'Scout is already in these servers',
    'Picking one uses it right away; you can change it later in Settings.',
    'O',
    'Old Server',
  ];
  const rows: readonly {
    readonly state: 'Not started' | 'Waiting' | 'Joined';
    readonly main: readonly (string | boolean | null)[];
    readonly already: readonly string[] | null;
    readonly blocked: string | null;
  }[] = [
    {
      state: 'Not started',
      main: [
        'Discord opens in your browser so you can pick the server.',
        'Add Scout to a server',
        true,
        null,
        null,
      ],
      already: ALREADY,
      blocked: 'Add Scout to a server, or pick one it is already in.',
    },
    {
      state: 'Waiting',
      main: [
        null,
        null,
        false,
        'Pick your server in the browser and click Authorize. Scout continues on its own.',
        null,
      ],
      already: ALREADY,
      blocked: 'Add Scout to a server, or pick one it is already in.',
    },
    {
      state: 'Joined',
      main: [null, null, false, null, 'Scout joined Old Server'],
      already: null,
      blocked: null,
    },
  ];

  for (const row of rows) {
    it(`${row.state}: ${row.state === 'Not started' ? 'the invite button' : row.state === 'Waiting' ? 'the spinner and what to do in the browser' : 'the joined line on the ok banner'}, ${row.already === null ? 'no server list' : 'the servers Scout is already in'}, Continue ${row.blocked === null ? 'free' : 'waiting'}`, async () => {
      const bridge = makeBridge(vi.fn().mockResolvedValue({ guilds: [OLD] }));
      const { fixture } = await setup(bridge);
      if (row.state === 'Waiting') await fixture.componentInstance.openInvite();
      if (row.state === 'Joined') await fixture.componentInstance.choose(OLD);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      const read = (selector: string): string | null =>
        root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
      const column = getComputedStyle(root.querySelector('main') as HTMLElement);
      const already = root.querySelector('.card [data-test="already-in"]');
      const next = root.querySelector('[data-test="wizard-continue"]') as HTMLButtonElement;

      expect({
        title: read('h1'),
        lede: read('.lede'),
        column: [column.maxWidth, column.marginLeft, column.marginRight],
        main: [
          read('.card .intro'),
          read('.card [data-test="invite"]'),
          root
            .querySelector('[data-test="invite"]')
            ?.classList.contains('mat-mdc-unelevated-button') ?? false,
          root.querySelector('.card .waiting .spinner') === null
            ? null
            : read('.card [role="status"] [data-test="waiting"]'),
          root.querySelector('.card .joined sc-icon') === null
            ? null
            : read('.card [role="status"] [data-test="joined"]'),
        ],
        already:
          already === null
            ? null
            : [
                already.querySelector('.already-title')?.textContent?.trim(),
                already.querySelector('.muted')?.textContent?.trim(),
                already.querySelector('.guild-mark')?.getAttribute('data-initial'),
                already.querySelector('[data-test="guild-option"]')?.textContent?.trim(),
              ],
        blocked: [next.disabled, read('[data-test="wizard-blocked"]')],
      }).toEqual({
        title: 'Add Scout to your server',
        lede: 'Invite the bot to the server where your team will ask it questions.',
        column: ['760px', 'auto', 'auto'],
        main: row.main,
        already: row.already,
        blocked: [row.blocked !== null, row.blocked],
      });
    });
  }
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
