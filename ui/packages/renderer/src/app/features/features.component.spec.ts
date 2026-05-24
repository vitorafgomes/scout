import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { FeatureDto } from '@scout/shared';

import { FeaturesStore } from '../core/features.store';
import { ScoutBridgeService } from '../core/scout-bridge';

import { FeaturesComponent } from './features.component';

const github: FeatureDto = {
  id: 'github',
  displayName: 'GitHub',
  description: 'GitHub integration',
  requiresCredentials: true,
  hasCredentials: true,
  enabled: true,
  status: 'enabled',
  lastError: null,
};

const kubernetes: FeatureDto = {
  id: 'kubernetes',
  displayName: 'Kubernetes',
  description: 'Kubernetes integration',
  requiresCredentials: true,
  hasCredentials: false,
  enabled: false,
  status: 'needs-credentials',
  lastError: null,
};

async function setup(): Promise<{
  fixture: ComponentFixture<FeaturesComponent>;
  bridge: {
    featuresList: ReturnType<typeof vi.fn>;
    featureEnable: ReturnType<typeof vi.fn>;
    featureDisable: ReturnType<typeof vi.fn>;
    featureSetCredentials: ReturnType<typeof vi.fn>;
    featureDeleteCredentials: ReturnType<typeof vi.fn>;
  };
}> {
  const bridge = {
    featuresList: vi.fn(async () => ({ features: [github, kubernetes] })),
    featureEnable: vi.fn(async () => github),
    featureDisable: vi.fn(async () => ({ ...github, enabled: false, status: 'disabled' as const })),
    featureSetCredentials: vi.fn(async () => github),
    featureDeleteCredentials: vi.fn(async () => kubernetes),
  };

  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      FeaturesStore,
    ],
  });

  const fixture = TestBed.createComponent(FeaturesComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, bridge };
}

describe('FeaturesComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders one card per feature returned by the bridge', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="card-github"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="card-kubernetes"]')).not.toBeNull();
  });

  it('opens the credential dialog when a card requests it', async () => {
    const { fixture } = await setup();

    const cta = fixture.nativeElement.querySelector('[data-test="cta-github"]') as HTMLButtonElement;
    cta.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="credential-dialog"]')).not.toBeNull();
  });

  it('forwards dialog submit to store.storeCredentials and closes the dialog', async () => {
    const { fixture, bridge } = await setup();

    (fixture.nativeElement.querySelector('[data-test="cta-github"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const ta = fixture.nativeElement.querySelector(
      '[data-test="credential-textarea"]',
    ) as HTMLTextAreaElement;
    ta.value = '{"token":"abc"}';
    ta.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="credential-submit"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.featureSetCredentials).toHaveBeenCalledWith({
      id: 'github',
      body: { raw: '{"token":"abc"}' },
    });
    expect(fixture.nativeElement.querySelector('[data-test="credential-dialog"]')).toBeNull();
  });

  it('forwards toggle event to store.toggle', async () => {
    const { fixture, bridge } = await setup();

    const toggle = fixture.nativeElement.querySelector(
      '[data-test="toggle-github"]',
    ) as HTMLInputElement;
    toggle.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(bridge.featureDisable).toHaveBeenCalledWith({ id: 'github' });
  });
});
