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

describe('ProvisionComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('calls appOpenExternal with the Discord Developer Portal URL', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);

    const btn = fixture.nativeElement.querySelector(
      'button.primary:not([data-test="created-continue"])',
    ) as HTMLButtonElement;
    btn.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({
      url: 'https://discord.com/developers/applications',
    });
    expect(fixture.componentInstance.portalOpened()).toBe(true);
  });

  it('enables the continue button only after the portal opens', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);

    const continueBtn = fixture.nativeElement.querySelector(
      '[data-test="created-continue"]',
    ) as HTMLButtonElement;
    expect(continueBtn.disabled).toBe(true);

    const openBtn = fixture.nativeElement.querySelector(
      'button.primary:not([data-test="created-continue"])',
    ) as HTMLButtonElement;
    openBtn.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(continueBtn.disabled).toBe(false);
  });

  it('surfaces the error and the copy-URL fallback when openExternal rejects', async () => {
    const bridge = makeBridge({
      appOpenExternal: vi
        .fn()
        .mockRejectedValue(new ExternalHostNotAllowedError('discord.com', 'blocked')),
    });
    const fixture = await setup(bridge);

    const openBtn = fixture.nativeElement.querySelector(
      'button.primary:not([data-test="created-continue"])',
    ) as HTMLButtonElement;
    openBtn.click();
    await fixture.whenStable();
    fixture.detectChanges();

    const errorBlock = fixture.nativeElement.querySelector('.error-block');
    expect(errorBlock).not.toBeNull();
    expect(errorBlock.textContent).toContain('Could not open');
    const url = fixture.nativeElement.querySelector('.url');
    expect(url.textContent).toContain('https://discord.com/developers/applications');
  });

  it('enables continue via the fallback when opening the portal fails', async () => {
    const bridge = makeBridge({
      appOpenExternal: vi
        .fn()
        .mockRejectedValue(new ExternalHostNotAllowedError('discord.com', 'blocked')),
    });
    const fixture = await setup(bridge);

    const continueBtn = fixture.nativeElement.querySelector(
      '[data-test="created-continue"]',
    ) as HTMLButtonElement;
    expect(continueBtn.disabled).toBe(true);

    const openBtn = fixture.nativeElement.querySelector(
      'button.primary:not([data-test="created-continue"])',
    ) as HTMLButtonElement;
    openBtn.click();
    await fixture.whenStable();
    fixture.detectChanges();

    // The browser handoff failed, but the copy-URL fallback is on screen, so the
    // user can still create the app manually — Continue must not dead-end.
    expect(fixture.componentInstance.portalOpened()).toBe(false);
    expect(continueBtn.disabled).toBe(false);
  });

  it('navigates to /token on continue', async () => {
    const bridge = makeBridge();
    const fixture = await setup(bridge);
    const router = TestBed.inject(Router);
    const spy = vi.spyOn(router, 'navigateByUrl');

    // Force portalOpened so the continue button is active
    fixture.componentInstance.portalOpened.set(true);
    fixture.detectChanges();
    const continueBtn = fixture.nativeElement.querySelector(
      '[data-test="created-continue"]',
    ) as HTMLButtonElement;
    continueBtn.click();

    expect(spy).toHaveBeenCalledWith('/token');
  });
});
