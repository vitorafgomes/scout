import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import type { FeatureViewModel } from '../core/features.store';

import { FeatureCardComponent } from './feature-card.component';

function setFeature(
  fixture: ComponentFixture<FeatureCardComponent>,
  feature: FeatureViewModel,
): void {
  fixture.componentInstance.feature = feature;
  fixture.detectChanges();
}

const base: FeatureViewModel = {
  id: 'github',
  displayName: 'GitHub',
  description: 'GitHub integration',
  requiresCredentials: true,
  hasCredentials: false,
  enabled: false,
  status: 'needs-credentials',
  lastError: null,
  credentialFields: [],
  settingsFields: [],
  settings: {},
  scopeMatrix: null,
  setupSteps: null,
  busy: false,
};

function create(): ComponentFixture<FeatureCardComponent> {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection()],
  });
  return TestBed.createComponent(FeatureCardComponent);
}

describe('FeatureCardComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the "needs credentials" state with toggle disabled', () => {
    const fixture = create();
    setFeature(fixture, base);

    const badge = fixture.nativeElement.querySelector('[data-test="badge-github"]') as HTMLElement;
    expect(badge.textContent?.trim()).toBe('Needs credentials');

    const toggle = fixture.nativeElement.querySelector(
      '[data-test="toggle-github"]',
    ) as HTMLInputElement;
    expect(toggle.disabled).toBe(true);

    const cta = fixture.nativeElement.querySelector(
      '[data-test="cta-github"]',
    ) as HTMLButtonElement;
    expect(cta.textContent?.trim()).toBe('Add credentials');
    expect(fixture.nativeElement.querySelector('[data-test="clear-github"]')).toBeNull();
  });

  it('renders the "disabled" state (creds present, toggle off, toggle enabled)', () => {
    const fixture = create();
    setFeature(fixture, { ...base, hasCredentials: true, status: 'disabled', enabled: false });

    const badge = fixture.nativeElement.querySelector('[data-test="badge-github"]') as HTMLElement;
    expect(badge.textContent?.trim()).toBe('Disabled');

    const toggle = fixture.nativeElement.querySelector(
      '[data-test="toggle-github"]',
    ) as HTMLInputElement;
    expect(toggle.disabled).toBe(false);
    expect(toggle.checked).toBe(false);

    const clear = fixture.nativeElement.querySelector(
      '[data-test="clear-github"]',
    ) as HTMLButtonElement;
    expect(clear).not.toBeNull();
    expect(clear.disabled).toBe(false);

    const cta = fixture.nativeElement.querySelector(
      '[data-test="cta-github"]',
    ) as HTMLButtonElement;
    expect(cta.textContent?.trim()).toBe('Update credentials');
  });

  it('renders the "enabled" state with toggle checked', () => {
    const fixture = create();
    setFeature(fixture, { ...base, hasCredentials: true, status: 'enabled', enabled: true });

    const badge = fixture.nativeElement.querySelector('[data-test="badge-github"]') as HTMLElement;
    expect(badge.textContent?.trim()).toBe('Enabled');

    const toggle = fixture.nativeElement.querySelector(
      '[data-test="toggle-github"]',
    ) as HTMLInputElement;
    expect(toggle.checked).toBe(true);
    expect(toggle.disabled).toBe(false);
  });

  it('renders the "error" state with the last-error message and Error badge', () => {
    const fixture = create();
    setFeature(fixture, {
      ...base,
      hasCredentials: true,
      status: 'error',
      lastError: 'boom-from-worker',
    });

    const badge = fixture.nativeElement.querySelector('[data-test="badge-github"]') as HTMLElement;
    expect(badge.textContent?.trim()).toBe('Error');

    const err = fixture.nativeElement.querySelector('[data-test="err-github"]') as HTMLElement;
    expect(err.textContent?.trim()).toBe('boom-from-worker');
  });

  it('renders the "busy" state with all interactive controls disabled', () => {
    const fixture = create();
    setFeature(fixture, { ...base, hasCredentials: true, status: 'disabled', busy: true });

    const toggle = fixture.nativeElement.querySelector(
      '[data-test="toggle-github"]',
    ) as HTMLInputElement;
    expect(toggle.disabled).toBe(true);

    const cta = fixture.nativeElement.querySelector(
      '[data-test="cta-github"]',
    ) as HTMLButtonElement;
    expect(cta.disabled).toBe(true);

    const clear = fixture.nativeElement.querySelector(
      '[data-test="clear-github"]',
    ) as HTMLButtonElement;
    expect(clear.disabled).toBe(true);
  });
});
