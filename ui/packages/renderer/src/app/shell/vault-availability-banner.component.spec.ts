import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import type { VaultUnavailableReason } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';
import { VaultAvailabilityBannerComponent } from './vault-availability-banner.component';

function makeBridgeStub(available: boolean, reason?: VaultUnavailableReason) {
  return {
    vaultIsAvailable: vi.fn().mockResolvedValue({ available, reason }),
  } as unknown as ScoutBridgeService;
}

async function createComponent(bridgeStub: ScoutBridgeService) {
  TestBed.configureTestingModule({
    imports: [VaultAvailabilityBannerComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridgeStub },
    ],
  });

  const fixture = TestBed.createComponent(VaultAvailabilityBannerComponent);
  fixture.detectChanges();
  // Allow the async checkAvailability to complete
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture;
}

describe('VaultAvailabilityBannerComponent', () => {
  it('renders the warning banner when available is false', async () => {
    const stub = makeBridgeStub(false);
    const fixture = await createComponent(stub);

    const banner = fixture.nativeElement.querySelector('.vault-warn-banner');
    expect(banner).not.toBeNull();
    expect((banner as HTMLElement).textContent).toContain(
      'Secret storage is unavailable on this system',
    );
  });

  it('names the missing keyring service and says nothing was saved when the store is insecure', async () => {
    const fixture = await createComponent(makeBridgeStub(false, 'insecure-store'));

    const banner = fixture.nativeElement.querySelector('.vault-warn-banner') as HTMLElement;
    expect(banner.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Scout found no keyring service such as GNOME Keyring or KWallet on this computer, so it saved nothing and does not use the credentials saved before; start or install one, then restart Scout.',
    );
  });

  it('a failed vault availability check does not hide the warning: the unavailable warning shows, without the error', async () => {
    const stub = {
      vaultIsAvailable: vi.fn().mockRejectedValue(new Error('keyring daemon gone')),
    } as unknown as ScoutBridgeService;
    const fixture = await createComponent(stub);

    const banner = fixture.nativeElement.querySelector('.vault-warn-banner') as HTMLElement | null;
    expect(banner?.getAttribute('role')).toBe('alert');
    expect(banner?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Secret storage is unavailable on this system. Scout cannot save bot tokens securely until a keyring is installed. See docs for details.',
    );
  });

  it('renders nothing when available is true', async () => {
    const stub = makeBridgeStub(true);
    const fixture = await createComponent(stub);

    const banner = fixture.nativeElement.querySelector('.vault-warn-banner');
    expect(banner).toBeNull();
  });

  it('calls vaultIsAvailable exactly once on init', async () => {
    const stub = makeBridgeStub(true);
    await createComponent(stub);

    expect(stub.vaultIsAvailable).toHaveBeenCalledTimes(1);
  });
});
