import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { VaultUnavailableError } from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { TokenPasteComponent } from './token-paste.component';

const VALID_TOKEN = 'A'.repeat(55) + '.B5';
const VALID_CLIENT_ID = '1234567890';

function makeBridge(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    vaultIsAvailable: vi.fn().mockResolvedValue({ available: true }),
    vaultStore: vi.fn().mockResolvedValue(undefined),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    ...overrides,
  } as unknown as ScoutBridgeService;
}

async function setup(bridge: ScoutBridgeService) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([{ path: 'oauth', children: [] }]),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const fixture = TestBed.createComponent(TokenPasteComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

describe('TokenPasteComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('keeps the submit button disabled until token, client id, and all intents are set', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);
    const cmp = fixture.componentInstance;

    const submit = fixture.nativeElement.querySelector('[data-test="submit"]') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);

    cmp.tokenValue.set(VALID_TOKEN);
    cmp.clientIdValue.set(VALID_CLIENT_ID);
    cmp.intentMessageContent.set(true);
    cmp.intentGuildMembers.set(true);
    fixture.detectChanges();
    expect(submit.disabled).toBe(true);

    cmp.intentPresence.set(true);
    fixture.detectChanges();
    expect(submit.disabled).toBe(false);
  });

  it('stores token before client id, navigates, and clears the local token buffer', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');
    const cmp = fixture.componentInstance;

    cmp.tokenValue.set(VALID_TOKEN);
    cmp.clientIdValue.set(VALID_CLIENT_ID);
    cmp.intentMessageContent.set(true);
    cmp.intentGuildMembers.set(true);
    cmp.intentPresence.set(true);
    fixture.detectChanges();

    await cmp.submit(new Event('submit'));
    await fixture.whenStable();

    const vaultStoreMock = bridge.vaultStore as unknown as {
      mock: { calls: unknown[][] };
    };
    expect(vaultStoreMock.mock.calls[0]).toEqual([
      { key: 'discord.bot-token', value: VALID_TOKEN },
    ]);
    expect(vaultStoreMock.mock.calls[1]).toEqual([
      { key: 'discord.client-id', value: VALID_CLIENT_ID },
    ]);

    expect(cmp.tokenValue()).toBe('');
    expect(cmp.probeLastSubmittedTokenLength()).toBe(VALID_TOKEN.length);
    expect(TestBed.inject(WizardStore).clientId()).toBe(VALID_CLIENT_ID);
    expect(TestBed.inject(WizardStore).intentsAcknowledged()).toBe(true);
    expect(nav).toHaveBeenCalledWith('/oauth');
  });

  it('blocks submit and shows the banner when the vault is unavailable', async () => {
    const bridge = makeBridge({
      vaultIsAvailable: vi.fn().mockResolvedValue({ available: false, reason: 'no-keyring' }),
    });
    const fixture = await setup(bridge);
    const cmp = fixture.componentInstance;

    cmp.tokenValue.set(VALID_TOKEN);
    cmp.clientIdValue.set(VALID_CLIENT_ID);
    cmp.intentMessageContent.set(true);
    cmp.intentGuildMembers.set(true);
    cmp.intentPresence.set(true);
    fixture.detectChanges();

    await cmp.submit(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(cmp.vaultBlocked()).toBe(true);
    expect(bridge.vaultStore).not.toHaveBeenCalled();
    const banner = fixture.nativeElement.querySelector('.blocking-banner');
    expect(banner).not.toBeNull();
    expect(banner.textContent).toContain('Keyring unavailable');
  });

  it('shows the serialised error message when vault store fails', async () => {
    const bridge = makeBridge({
      vaultStore: vi.fn().mockRejectedValue(new VaultUnavailableError('Keyring crashed mid-write')),
    });
    const fixture = await setup(bridge);
    const cmp = fixture.componentInstance;

    cmp.tokenValue.set(VALID_TOKEN);
    cmp.clientIdValue.set(VALID_CLIENT_ID);
    cmp.intentMessageContent.set(true);
    cmp.intentGuildMembers.set(true);
    cmp.intentPresence.set(true);
    fixture.detectChanges();

    await cmp.submit(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(cmp.submissionError()).toBe('Keyring crashed mid-write');
  });
});
