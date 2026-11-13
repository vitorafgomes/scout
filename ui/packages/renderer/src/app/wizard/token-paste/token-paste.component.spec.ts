import { readFileSync } from 'node:fs';

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ScoutError, encodeIpcErrorMessage, serializeError } from '@scout/shared';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { PERSISTENT_STATE_STORAGE_KEY } from '../../core/persistent-state';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { TokenPasteComponent } from './token-paste.component';

const TOKEN = 'MTEx.pasted-bot-token.secret';
const BOT = {
  applicationId: '111222333',
  applicationName: 'Scout',
  botUsername: 'scout-bot',
  messageContent: 'off' as const,
};
const PORTAL = 'https://discord.com/developers/applications/111222333/bot';

function ipcError(code: string, message: string): Error {
  return new Error(encodeIpcErrorMessage(serializeError(new ScoutError(code, message))));
}

function makeBridge(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    vaultIsAvailable: vi.fn().mockResolvedValue({ available: true }),
    vaultStore: vi.fn().mockResolvedValue(undefined),
    discordCheckToken: vi.fn().mockResolvedValue(BOT),
    discordEnableIntent: vi.fn().mockResolvedValue({ enabled: true }),
    appOpenExternal: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  } as unknown as ScoutBridgeService & Record<string, ReturnType<typeof vi.fn>>;
}

async function setup(bridge: ScoutBridgeService) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: 'oauth', children: [] },
        { path: '', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const fixture = TestBed.createComponent(TokenPasteComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

async function pasteAndSave(fixture: Awaited<ReturnType<typeof setup>>, token = TOKEN) {
  fixture.componentInstance.tokenValue.set(token);
  await fixture.componentInstance.submit(new Event('submit'));
  fixture.detectChanges();
  await fixture.whenStable();
}

function text(fixture: Awaited<ReturnType<typeof setup>>, selector: string): string {
  return (fixture.nativeElement.querySelector(selector)?.textContent ?? '').trim();
}

describe('TokenPasteComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('as in the prototype: Continue waits with its reason until Discord accepts a token, and Back returns to Create bot without forgetting the setup', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');
    const next = () =>
      fixture.nativeElement.querySelector('[data-test="wizard-continue"]') as HTMLButtonElement;
    const before = [next().disabled, text(fixture, '[data-test="wizard-blocked"]')];

    await pasteAndSave(fixture);
    const after = [
      next().disabled,
      fixture.nativeElement.querySelector('[data-test="wizard-blocked"]'),
    ];
    (fixture.nativeElement.querySelector('[data-test="wizard-back"]') as HTMLButtonElement).click();

    expect(before).toEqual([true, 'Save a token Discord accepts to continue.']);
    expect(after).toEqual([false, null]);
    expect(nav).toHaveBeenCalledWith('/provision');
    expect(TestBed.inject(WizardStore).clientId()).toBe('111222333');
  });

  it('Back is disabled while Discord checks the token, as Cancel was', async () => {
    const bridge = makeBridge({ discordCheckToken: vi.fn(() => new Promise(() => undefined)) });
    const fixture = await setup(bridge);
    const back = () =>
      fixture.nativeElement.querySelector('[data-test="wizard-back"]') as HTMLButtonElement;
    const idle = back().disabled;

    fixture.componentInstance.tokenValue.set(TOKEN);
    void fixture.componentInstance.submit(new Event('submit'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect([idle, back().disabled]).toEqual([false, true]);
  });

  it('the token step has one field and continues on a valid token', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');
    const inputs = fixture.nativeElement.querySelectorAll('input');
    expect(inputs).toHaveLength(1);
    expect(inputs[0].getAttribute('type')).toBe('password');

    await pasteAndSave(fixture);
    const continueBtn = fixture.nativeElement.querySelector(
      '[data-test="wizard-continue"]',
    ) as HTMLButtonElement;
    expect(continueBtn.disabled).toBe(false);
    continueBtn.click();

    expect(bridge.discordCheckToken).toHaveBeenCalledWith({ token: TOKEN });
    expect(text(fixture, '[data-test="bot-found"]')).toContain('Found Scout (bot scout-bot)');
    expect(TestBed.inject(WizardStore).clientId()).toBe('111222333');
    expect(nav).toHaveBeenCalledWith('/oauth');
  });

  it('checks the token as soon as it is pasted', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);
    const preventDefault = vi.fn();

    fixture.componentInstance.onTokenPaste({
      clipboardData: { getData: () => TOKEN },
      preventDefault,
    } as unknown as ClipboardEvent);
    await fixture.whenStable();

    expect(bridge.discordCheckToken).toHaveBeenCalledWith({ token: TOKEN });
    expect(fixture.componentInstance.intent()).toBe('on');
  });

  it('turns message content on automatically and shows it is on', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);

    await pasteAndSave(fixture);

    expect(bridge.discordEnableIntent).toHaveBeenCalledTimes(1);
    expect(text(fixture, '[data-test="intent-on"]')).toContain('Message Content Intent is on');
  });

  it('skips the intent call when message content is already on', async () => {
    const bridge = makeBridge({
      discordCheckToken: vi.fn().mockResolvedValue({ ...BOT, messageContent: 'on' }),
    });
    const fixture = await setup(bridge);

    await pasteAndSave(fixture);

    expect(bridge.discordEnableIntent).not.toHaveBeenCalled();
    expect(fixture.componentInstance.intent()).toBe('on');
  });

  it('shows the exact fix with the portal link, keeps Continue off, and Check again retries', async () => {
    const message =
      'Scout is in 100 or more servers, so Discord only lets you turn on Message Content Intent in the Developer Portal (Bot, Privileged Gateway Intents).';
    const bridge = makeBridge({
      discordEnableIntent: vi
        .fn()
        .mockResolvedValueOnce({ enabled: false, message, portalUrl: PORTAL })
        .mockResolvedValueOnce({ enabled: true }),
    });
    const fixture = await setup(bridge);

    await pasteAndSave(fixture);

    expect(text(fixture, '[data-test="intent-fix"]')).toBe(message);
    const continueBtn = fixture.nativeElement.querySelector(
      '[data-test="wizard-continue"]',
    ) as HTMLButtonElement;
    expect(continueBtn.disabled).toBe(true);
    fixture.componentInstance.openPortal();
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: PORTAL });

    (fixture.nativeElement.querySelector('[data-test="check-again"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.discordEnableIntent).toHaveBeenCalledTimes(2);
    expect(continueBtn.disabled).toBe(false);
  });

  it('says Discord rejected this bot token and does not continue', async () => {
    const bridge = makeBridge({
      discordCheckToken: vi
        .fn()
        .mockRejectedValue(ipcError('discord/token-rejected', 'Discord rejected this bot token.')),
    });
    const fixture = await setup(bridge);

    await pasteAndSave(fixture);

    expect(text(fixture, '[data-test="token-error"]')).toBe('Discord rejected this bot token.');
    expect(
      (fixture.nativeElement.querySelector('[data-test="wizard-continue"]') as HTMLButtonElement)
        .disabled,
    ).toBe(true);
  });

  it('offers Check again when the network is down while turning the intent on', async () => {
    const bridge = makeBridge({
      discordEnableIntent: vi
        .fn()
        .mockRejectedValue(ipcError('discord/network-error', 'Could not reach Discord.')),
    });
    const fixture = await setup(bridge);

    await pasteAndSave(fixture);

    expect(fixture.componentInstance.intent()).toBe('failed');
    expect(fixture.nativeElement.querySelector('[data-test="check-again"]')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Could not reach Discord.');
  });

  it('sends the token to main only through checkToken and clears it from the field', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);

    await pasteAndSave(fixture);

    expect(bridge.vaultStore).not.toHaveBeenCalled();
    expect(fixture.componentInstance.tokenValue()).toBe('');
    expect(fixture.nativeElement.textContent).not.toContain(TOKEN);
  });

  it('blocks when the keyring is unavailable without sending the token', async () => {
    const bridge = makeBridge({
      vaultIsAvailable: vi.fn().mockResolvedValue({ available: false, reason: 'no-keyring' }),
    });
    const fixture = await setup(bridge);

    await pasteAndSave(fixture);

    expect(fixture.nativeElement.querySelector('.blocking-banner')).not.toBeNull();
    expect(bridge.discordCheckToken).not.toHaveBeenCalled();
  });

  it('the keyring banner goes away once the keyring is available and the token is saved', async () => {
    const bridge = makeBridge({
      vaultIsAvailable: vi
        .fn()
        .mockResolvedValueOnce({ available: false, reason: 'no-keyring' })
        .mockResolvedValue({ available: true }),
      discordCheckToken: vi.fn().mockResolvedValue({ ...BOT, messageContent: 'on' }),
    });
    const fixture = await setup(bridge);

    await pasteAndSave(fixture);
    await pasteAndSave(fixture);

    expect(fixture.nativeElement.querySelector('.blocking-banner')).toBeNull();
  });

  it('coming Back from Add to server with the bot already saved lets the owner Continue without a new token', async () => {
    const bridge = makeBridge({
      vaultHas: vi.fn(async ({ key }: { key: string }) => ({
        exists: key === 'discord.bot-token' || key === 'discord.client-id',
      })),
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([
          { path: 'oauth', children: [] },
          { path: '', children: [] },
        ]),
        { provide: ScoutBridgeService, useValue: bridge },
      ],
    });
    TestBed.inject(WizardStore).setClientId(BOT.applicationId);
    const fixture = TestBed.createComponent(TokenPasteComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();

    expect(
      (fixture.nativeElement.querySelector('[data-test="wizard-continue"]') as HTMLButtonElement)
        .disabled,
    ).toBe(false);
  });

  it('coming Back to Bot token while the vault cannot answer keeps Continue waiting for a token, with no error', async () => {
    const bridge = makeBridge({
      vaultHas: vi
        .fn()
        .mockRejectedValue(
          new Error('[scout:vault/unavailable] The credential store is unavailable.'),
        ),
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([
          { path: 'oauth', children: [] },
          { path: '', children: [] },
        ]),
        { provide: ScoutBridgeService, useValue: bridge },
      ],
    });
    TestBed.inject(WizardStore).setClientId(BOT.applicationId);
    const fixture = TestBed.createComponent(TokenPasteComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();

    expect({
      continueDisabled: (
        fixture.nativeElement.querySelector('[data-test="wizard-continue"]') as HTMLButtonElement
      ).disabled,
      blocked: text(fixture, '[data-test="wizard-blocked"]'),
      error: fixture.nativeElement.querySelector('[data-test="token-error"]'),
    }).toEqual({
      continueDisabled: true,
      blocked: 'Save a token Discord accepts to continue.',
      error: null,
    });
  });
});

describe('A token for another Discord application', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    localStorage.clear();
  });

  it('forgets the old server, so the next launch shows no server for the new bot', async () => {
    localStorage.setItem(
      PERSISTENT_STATE_STORAGE_KEY,
      JSON.stringify({ clientId: '999', guildId: '42', guildName: 'Old Team' }),
    );
    const fixture = await setup(
      makeBridge({
        discordCheckToken: vi.fn().mockResolvedValue({ ...BOT, messageContent: 'on' }),
      }),
    );
    await pasteAndSave(fixture);
    TestBed.resetTestingModule();

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: ScoutBridgeService,
          useValue: {
            vaultHas: vi.fn(async ({ key }: { key: string }) => ({
              exists: key === 'discord.bot-token' || key === 'discord.client-id',
            })),
            slackStatus: vi.fn().mockResolvedValue({ connected: false }),
          },
        },
      ],
    });
    const nextLaunch = TestBed.inject(WizardStore);
    await nextLaunch.loadFromVault();

    expect(nextLaunch.guild()).toBeNull();
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

describe("The wizard's Bot token page follows the theme and the prototype", () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  for (const file of ['src/app/wizard/token-paste/token-paste.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }

  it('Save shows its icon before its text, as the prototype draws it', async () => {
    const fixture = await setup(makeBridge());

    expect(iconAndText(fixture.nativeElement as HTMLElement, ['[data-test="submit"]'])).toEqual([
      ['sc-icon', 'Save'],
    ]);
  });
});

type BotTokenFixture = Awaited<ReturnType<typeof setup>>;

const NEEDS_PORTAL =
  'Scout is in 100 or more servers, so Discord only lets you turn on Message Content Intent in the Developer Portal (Bot, Privileged Gateway Intents).';
const UNREACHABLE = 'Could not reach Discord. Check the internet connection and try again.';
const PORTAL_REFUSED = `Error invoking remote method 'scout:app.openExternal': Error: No application knows how to open URL ${PORTAL}`;
const PORTAL_DID_NOT_OPEN = `The Developer Portal did not open. Open this address in any browser, then come back and check again: ${PORTAL}`;

type BotTokenState =
  | 'Empty'
  | 'Saved'
  | 'Saved earlier'
  | 'Rejected'
  | 'Keyring unavailable'
  | 'No keyring service'
  | 'Checking'
  | 'Needs the portal'
  | 'Needs the portal, the portal did not open'
  | 'Check failed';

/** Brings the Bot token page to one of its states, the way the owner reaches it. */
async function reach(state: BotTokenState): Promise<BotTokenFixture> {
  if (state === 'Empty') return setup(makeBridge());
  if (state === 'Saved earlier') {
    const bridge = makeBridge({ vaultHas: vi.fn().mockResolvedValue({ exists: true }) });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([
          { path: 'oauth', children: [] },
          { path: '', children: [] },
        ]),
        { provide: ScoutBridgeService, useValue: bridge },
      ],
    });
    TestBed.inject(WizardStore).setClientId(BOT.applicationId);
    const fixture = TestBed.createComponent(TokenPasteComponent);
    fixture.detectChanges();
    await vi.mocked(bridge.vaultHas).mock.results[0]!.value;
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }
  if (state === 'Checking') {
    let asked = (): void => undefined;
    const turningOn = new Promise<void>((resolve) => (asked = resolve));
    const fixture = await setup(
      makeBridge({
        discordEnableIntent: vi.fn(() => {
          asked();
          return new Promise(() => undefined);
        }),
      }),
    );
    fixture.componentInstance.tokenValue.set(TOKEN);
    void fixture.componentInstance.submit(new Event('submit'));
    await turningOn;
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }
  const portalAsked = { enabled: false, message: NEEDS_PORTAL, portalUrl: PORTAL };
  const bridges: Record<Exclude<BotTokenState, 'Empty' | 'Saved earlier' | 'Checking'>, object> = {
    Saved: {},
    Rejected: {
      discordCheckToken: vi
        .fn()
        .mockRejectedValue(ipcError('discord/token-rejected', 'Discord rejected this bot token.')),
    },
    'Keyring unavailable': {
      vaultIsAvailable: vi.fn().mockResolvedValue({ available: false, reason: 'no-keyring' }),
    },
    'No keyring service': {
      vaultIsAvailable: vi.fn().mockResolvedValue({ available: false, reason: 'insecure-store' }),
    },
    'Needs the portal': { discordEnableIntent: vi.fn().mockResolvedValue(portalAsked) },
    'Needs the portal, the portal did not open': {
      discordEnableIntent: vi.fn().mockResolvedValue(portalAsked),
      appOpenExternal: vi.fn().mockRejectedValue(new Error(PORTAL_REFUSED)),
    },
    'Check failed': {
      discordEnableIntent: vi
        .fn()
        .mockRejectedValue(ipcError('discord/network-error', UNREACHABLE)),
    },
  };
  const bridge = makeBridge(bridges[state]);
  const fixture = await setup(bridge);
  await pasteAndSave(fixture);
  if (state === 'Needs the portal, the portal did not open') {
    const open = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('.check button'),
    ).find((button) => button.textContent?.trim() === 'Open Developer Portal');
    open?.click();
    await (vi.mocked(bridge.appOpenExternal).mock.results[0]!.value as Promise<void>).catch(
      () => undefined,
    );
    fixture.detectChanges();
    await fixture.whenStable();
  }
  return fixture;
}

describe('The Bot token step as in the prototype, state by state', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  const HELP = 'Paste it here; Scout saves and checks it as soon as you paste.';
  const WAITING = 'Save a token Discord accepts to continue.';
  const FOUND = [
    'ok',
    'Found Scout (bot scout-bot)',
    'The token belongs to the application you created.',
  ];
  const rows: readonly {
    readonly state: BotTokenState;
    readonly banner: string | null;
    readonly field: readonly [type: string, inError: boolean, value: string];
    readonly save: readonly [label: string, look: string, disabled: boolean];
    readonly help: readonly [string, string];
    readonly checks: readonly (readonly string[])[];
    readonly portal: string | null;
    readonly backDisabled: boolean;
    readonly blocked: string | null;
  }[] = [
    {
      state: 'Empty',
      banner: null,
      field: ['password', false, ''],
      save: ['Save', 'mat-mdc-unelevated-button', true],
      help: ['token-help', HELP],
      checks: [],
      portal: null,
      backDisabled: false,
      blocked: WAITING,
    },
    {
      state: 'Saved',
      banner: null,
      field: ['password', false, ''],
      save: ['Saved', 'mat-mdc-outlined-button', true],
      help: ['token-help', HELP],
      checks: [
        FOUND,
        ['ok', 'Message Content Intent is on', 'Lets Scout read the messages that mention it.'],
      ],
      portal: null,
      backDisabled: false,
      blocked: null,
    },
    {
      state: 'Saved earlier',
      banner: null,
      field: ['password', false, ''],
      save: ['Saved', 'mat-mdc-outlined-button', true],
      help: ['token-help', HELP],
      checks: [['ok', 'The bot token is saved and Message Content Intent is on']],
      portal: null,
      backDisabled: false,
      blocked: null,
    },
    {
      state: 'Rejected',
      banner: null,
      field: ['password', true, TOKEN],
      save: ['Try again', 'mat-mdc-unelevated-button', false],
      help: ['token-error', 'Discord rejected this bot token.'],
      checks: [],
      portal: null,
      backDisabled: false,
      blocked: WAITING,
    },
    {
      state: 'Keyring unavailable',
      banner: 'Keyring unavailable. Install gnome-keyring / kwallet / libsecret to proceed.',
      field: ['password', false, TOKEN],
      save: ['Save', 'mat-mdc-unelevated-button', false],
      help: ['token-help', HELP],
      checks: [],
      portal: null,
      backDisabled: false,
      blocked: WAITING,
    },
    {
      state: 'No keyring service',
      banner:
        'Scout found no keyring service such as GNOME Keyring or KWallet on this computer, so it saved nothing and does not use the credentials saved before; start or install one, then restart Scout.',
      field: ['password', false, TOKEN],
      save: ['Save', 'mat-mdc-unelevated-button', false],
      help: ['token-help', HELP],
      checks: [],
      portal: null,
      backDisabled: false,
      blocked: WAITING,
    },
    {
      state: 'Checking',
      banner: null,
      field: ['password', false, ''],
      save: ['Checking...', 'mat-mdc-unelevated-button', true],
      help: ['token-help', HELP],
      checks: [FOUND, ['busy', 'Turning on Message Content Intent...']],
      portal: null,
      backDisabled: true,
      blocked: WAITING,
    },
    {
      state: 'Needs the portal',
      banner: null,
      field: ['password', false, ''],
      save: ['Save', 'mat-mdc-unelevated-button', true],
      help: ['token-help', HELP],
      checks: [FOUND, ['warn', NEEDS_PORTAL, 'Open Developer Portal', 'Check again']],
      portal: null,
      backDisabled: false,
      blocked: WAITING,
    },
    {
      state: 'Needs the portal, the portal did not open',
      banner: null,
      field: ['password', false, ''],
      save: ['Save', 'mat-mdc-unelevated-button', true],
      help: ['token-help', HELP],
      checks: [FOUND, ['warn', NEEDS_PORTAL, 'Open Developer Portal', 'Check again']],
      portal: PORTAL_DID_NOT_OPEN,
      backDisabled: false,
      blocked: WAITING,
    },
    {
      state: 'Check failed',
      banner: null,
      field: ['password', false, ''],
      save: ['Save', 'mat-mdc-unelevated-button', true],
      help: ['token-help', HELP],
      checks: [FOUND, ['error', UNREACHABLE, 'Check again']],
      portal: null,
      backDisabled: false,
      blocked: WAITING,
    },
  ];

  for (const row of rows) {
    it(`${row.state}: the field ${row.field[2] === '' ? 'empty' : 'still holding the token'}, its help line "${row.help[1]}", the save button "${row.save[0]}", ${row.checks.length} checks, Continue ${row.blocked === null ? 'free' : 'waiting'}, and the token nowhere outside the field`, async () => {
      const fixture = await reach(row.state);
      const root = fixture.nativeElement as HTMLElement;
      const column = getComputedStyle(root.querySelector('main') as HTMLElement);
      const input = root.querySelector('[data-test="token-input"]') as HTMLInputElement;
      const save = root.querySelector('[data-test="submit"]') as HTMLButtonElement;
      const back = root.querySelector('[data-test="wizard-back"]') as HTMLButtonElement;
      const next = root.querySelector('[data-test="wizard-continue"]') as HTMLButtonElement;
      const said = (element: Element): string =>
        (element.textContent ?? '').replace(/\s+/g, ' ').trim();

      expect({
        title: text(fixture, 'h1'),
        lede: text(fixture, '.lede'),
        column: [column.maxWidth, column.marginLeft, column.marginRight],
        banner: root.querySelector('.blocking-banner')?.textContent?.trim() ?? null,
        label: text(fixture, '.card .label'),
        field: [
          input.getAttribute('type'),
          root.querySelector('.box')?.classList.contains('box--error'),
          input.value,
        ],
        eye: root.querySelector('[data-test="reveal"]')?.getAttribute('aria-label'),
        save: [save.textContent?.trim(), save.classList.contains(row.save[1]), save.disabled],
        help: [
          root
            .querySelector('.card [data-test="token-help"], .card [data-test="token-error"]')
            ?.getAttribute('data-test'),
          text(fixture, '.card [data-test="token-help"], .card [data-test="token-error"]'),
        ],
        checks: Array.from(root.querySelectorAll('.card .check'), (check) => [
          check.getAttribute('data-tone'),
          ...Array.from(check.querySelectorAll('.check-text > span'), said),
          ...Array.from(check.querySelectorAll('.check-text button'), said),
        ]),
        portal:
          root.querySelector('.card [data-test="portal-failed"]') === null
            ? null
            : said(root.querySelector('.card [data-test="portal-failed"]') as Element),
        kept: text(fixture, '.card [data-test="token-kept"]'),
        backDisabled: back.disabled,
        blocked: [next.disabled, root.querySelector('[data-test="wizard-blocked"]')?.textContent],
        tokenInMarkup: root.innerHTML.includes(TOKEN),
        rawFailureOnPage: said(root).includes('Error invoking'),
      }).toEqual({
        title: 'Paste the bot token',
        lede: 'Scout checks the token with Discord and turns on what it needs to read your messages.',
        column: ['760px', 'auto', 'auto'],
        banner: row.banner,
        label: 'Discord bot token',
        field: row.field,
        eye: 'Show token',
        save: [row.save[0], true, row.save[2]],
        help: row.help,
        checks: row.checks,
        portal: row.portal,
        kept: 'The token is kept encrypted on this computer, under a key from its keyring.',
        backDisabled: row.backDisabled,
        blocked: [row.blocked !== null, row.blocked ?? undefined],
        tokenInMarkup: false,
        rawFailureOnPage: false,
      });
    });
  }

  it('with a token typed, the eye button shows it in the field and nowhere else on the page, then hides it again', async () => {
    const fixture = await setup(makeBridge());
    const root = fixture.nativeElement as HTMLElement;
    const input = root.querySelector('[data-test="token-input"]') as HTMLInputElement;
    const eye = root.querySelector('[data-test="reveal"]') as HTMLButtonElement;
    input.value = TOKEN;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    const read = (): Record<string, unknown> => ({
      field: [input.getAttribute('type'), input.value],
      eye: [eye.getAttribute('aria-label'), eye.querySelector('svg')?.getAttribute('aria-hidden')],
      inMarkup: root.innerHTML.includes(TOKEN),
      inText: (root.textContent ?? '').includes(TOKEN),
      inAttributes: [root, ...root.querySelectorAll('*')].flatMap((element) =>
        [...element.attributes]
          .filter((attribute) => attribute.value.includes(TOKEN))
          .map((attribute) => `${element.tagName.toLowerCase()}[${attribute.name}]`),
      ),
      inOtherFields: [...root.querySelectorAll('input, textarea')]
        .filter((field) => field !== input)
        .some((field) => (field as HTMLInputElement).value.includes(TOKEN)),
    });
    const nowhereElse = { inMarkup: false, inText: false, inAttributes: [], inOtherFields: false };
    const hidden = read();

    eye.click();
    fixture.detectChanges();
    const shown = read();
    eye.click();
    fixture.detectChanges();

    expect([hidden, shown, read()]).toEqual([
      { field: ['password', TOKEN], eye: ['Show token', 'true'], ...nowhereElse },
      { field: ['text', TOKEN], eye: ['Hide token', 'true'], ...nowhereElse },
      { field: ['password', TOKEN], eye: ['Show token', 'true'], ...nowhereElse },
    ]);
  });

  for (const [outcome, state, kept] of [
    ['Discord accepts it', 'Saved', ''],
    ['Discord rejects it', 'Rejected', TOKEN],
    ['the keyring is unavailable', 'Keyring unavailable', TOKEN],
    ['no keyring service was selected', 'No keyring service', TOKEN],
  ] as const) {
    it(`a token shown with the eye is hidden again by a save, when ${outcome}: the field is a password field ${kept === '' ? 'and empty' : 'still holding it'}, the eye reads Show token`, async () => {
      const fixture = await reach('Empty');
      const root = fixture.nativeElement as HTMLElement;
      const input = root.querySelector('[data-test="token-input"]') as HTMLInputElement;
      const eye = root.querySelector('[data-test="reveal"]') as HTMLButtonElement;
      const bridge = TestBed.inject(ScoutBridgeService);
      if (state === 'Rejected') {
        vi.mocked(bridge.discordCheckToken).mockRejectedValue(
          ipcError('discord/token-rejected', 'Discord rejected this bot token.'),
        );
      }
      if (state === 'Keyring unavailable') {
        vi.mocked(bridge.vaultIsAvailable).mockResolvedValue({
          available: false,
          reason: 'no-keyring',
        });
      }
      if (state === 'No keyring service') {
        vi.mocked(bridge.vaultIsAvailable).mockResolvedValue({
          available: false,
          reason: 'insecure-store',
        });
      }
      eye.click();
      fixture.detectChanges();
      const shown = input.getAttribute('type');

      await pasteAndSave(fixture);

      expect({
        shown,
        afterSave: [input.getAttribute('type'), input.value, eye.getAttribute('aria-label')],
      }).toEqual({ shown: 'text', afterSave: ['password', kept, 'Show token'] });
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
