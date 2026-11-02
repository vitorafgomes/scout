import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ScoutError, encodeIpcErrorMessage, serializeError } from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

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
      '[data-test="continue"]',
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
      '[data-test="continue"]',
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
      (fixture.nativeElement.querySelector('[data-test="continue"]') as HTMLButtonElement).disabled,
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
});
