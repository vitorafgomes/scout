import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from '../core/scout-bridge';
import { VaultAvailabilityBannerComponent } from './vault-availability-banner.component';

function makeBridgeStub(available: boolean) {
  return {
    vaultIsAvailable: vi.fn().mockResolvedValue({ available }),
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
