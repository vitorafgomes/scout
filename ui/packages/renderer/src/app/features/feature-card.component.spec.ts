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
  writeClass: 'none',
  granted: false,
  grantedAt: null,
  busy: false,
};

const youtube: FeatureViewModel = {
  ...base,
  id: 'youtube',
  displayName: 'YouTube',
  hasCredentials: true,
  status: 'disabled',
  scopeMatrix: [
    { scope: 'youtube.readonly', actions: ['Read the channel'], access: 'read' },
    { scope: 'drafts', actions: ['Save scripts and renders'], access: 'write' },
    { scope: 'youtube.upload', actions: ['Upload a video'], access: 'irreversible' },
  ],
  writeClass: 'executive',
};

function connectionCard(
  feature: FeatureViewModel,
  connection: 'connected' | 'not-connected',
): ComponentFixture<FeatureCardComponent> {
  const fixture = create();
  fixture.componentInstance.connection = connection;
  setFeature(fixture, feature);
  return fixture;
}

function text(fixture: ComponentFixture<FeatureCardComponent>, testId: string): string {
  const el = fixture.nativeElement.querySelector(`[data-test="${testId}"]`) as HTMLElement | null;
  return (el?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

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

  it('the YouTube card shows its read, write and irreversible rows and the grant line', () => {
    const notGranted = connectionCard(youtube, 'connected');
    const rows = ['youtube.readonly', 'drafts', 'youtube.upload'].map((scope) => [
      scope,
      text(notGranted, 'access-' + scope),
    ]);
    const before = text(notGranted, 'grant-youtube');
    TestBed.resetTestingModule();
    const granted = connectionCard(
      { ...youtube, granted: true, grantedAt: '2026-11-02T10:00:00Z' },
      'connected',
    );

    expect(rows).toEqual([
      ['youtube.readonly', 'read'],
      ['drafts', 'write'],
      ['youtube.upload', 'irreversible'],
    ]);
    expect(before).toBe('Write permission: not granted');
    expect(text(granted, 'grant-youtube')).toBe('Write permission: granted on 2026-11-02');
  });

  it('a grant error opens the Permissions block so the error is in sight', () => {
    const fixture = connectionCard(
      { ...youtube, lastError: 'GitHub rejected the grant.' },
      'connected',
    );
    const details = fixture.nativeElement.querySelector(
      '[data-test="permissions-youtube"]',
    ) as HTMLDetailsElement;

    expect(details.open).toBe(true);
    expect(text(fixture, 'grant-err-youtube')).toBe('GitHub rejected the grant.');
  });

  it('Allow writes is disabled while YouTube is not connected', () => {
    const disconnected = connectionCard(youtube, 'not-connected');
    const allowWhileDisconnected = disconnected.nativeElement.querySelector(
      '[data-test="allow-writes-youtube"]',
    ) as HTMLButtonElement;
    const disabledWhileDisconnected = allowWhileDisconnected.disabled;
    TestBed.resetTestingModule();
    const connected = connectionCard(youtube, 'connected');
    const allowWhileConnected = connected.nativeElement.querySelector(
      '[data-test="allow-writes-youtube"]',
    ) as HTMLButtonElement;

    expect(disabledWhileDisconnected).toBe(true);
    expect(allowWhileConnected.disabled).toBe(false);
  });

  it('a read-only feature lists its read rows under Reads and has no toggle for writes', () => {
    const fixture = create();
    setFeature(fixture, {
      ...base,
      id: 'desktop',
      requiresCredentials: false,
      scopeMatrix: [
        { scope: 'Screen', actions: ['read'], access: 'read' },
        { scope: 'Microphone', actions: ['read'], access: 'read' },
      ],
    });

    expect(text(fixture, 'reads-desktop')).toContain('Reads');
    expect(text(fixture, 'access-Screen')).toBe('read');
    expect(text(fixture, 'access-Microphone')).toBe('read');
    expect(fixture.nativeElement.querySelector('[data-test="permissions-desktop"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="grant-desktop"]')).toBeNull();
  });

  it('a write feature lists its reads and its writes in two groups', () => {
    const fixture = connectionCard(youtube, 'connected');
    const reads = fixture.nativeElement.querySelector('[data-test="reads-youtube"]') as HTMLElement;
    const writes = fixture.nativeElement.querySelector(
      '[data-test="permissions-youtube"]',
    ) as HTMLElement;

    expect(reads.textContent).toContain('youtube.readonly');
    expect(reads.textContent).not.toContain('drafts');
    expect(writes.textContent).toContain('drafts');
    expect(writes.textContent).toContain('youtube.upload');
    expect(writes.textContent).not.toContain('youtube.readonly');
  });

  it('Reads and Permissions start closed with their count and the grant state on the summary, and open on a click', () => {
    const fixture = connectionCard(youtube, 'connected');
    const reads = fixture.nativeElement.querySelector(
      '[data-test="reads-youtube"]',
    ) as HTMLDetailsElement;
    const permissions = fixture.nativeElement.querySelector(
      '[data-test="permissions-youtube"]',
    ) as HTMLDetailsElement;
    const closedAtFirst = [reads.open, permissions.open];
    const readsSummary = text(fixture, 'reads-summary-youtube');
    const permissionsSummary = text(fixture, 'permissions-summary-youtube');

    (
      fixture.nativeElement.querySelector('[data-test="reads-summary-youtube"]') as HTMLElement
    ).click();
    (
      fixture.nativeElement.querySelector(
        '[data-test="permissions-summary-youtube"]',
      ) as HTMLElement
    ).click();

    expect(closedAtFirst).toEqual([false, false]);
    expect(readsSummary).toBe('Reads 1');
    expect(permissionsSummary).toBe('Permissions 2 Writes not allowed');
    expect([reads.open, permissions.open]).toEqual([true, true]);
  });

  it('the Permissions summary says since when writes are allowed', () => {
    const fixture = connectionCard(
      { ...youtube, granted: true, grantedAt: '2026-11-02T10:00:00Z' },
      'connected',
    );

    expect(text(fixture, 'permissions-summary-youtube')).toBe(
      'Permissions 2 Writes allowed since 2026-11-02',
    );
  });

  it('a read is a chip titled with its actions', () => {
    const fixture = connectionCard(
      {
        ...youtube,
        scopeMatrix: [
          {
            scope: 'youtube.readonly',
            actions: ['Read the channel', 'List the uploads'],
            access: 'read',
          },
        ],
      },
      'connected',
    );
    const chip = fixture.nativeElement.querySelector(
      '[data-test="scope-row-youtube.readonly"]',
    ) as HTMLElement;

    expect(chip.classList.contains('chip')).toBe(true);
    expect(chip.getAttribute('title')).toBe('Read the channel, List the uploads');
  });

  it('a permission row shows its access pill and what it allows', () => {
    const fixture = connectionCard(youtube, 'connected');
    const pill = (scope: string) =>
      fixture.nativeElement.querySelector(`[data-test="access-${scope}"]`) as HTMLElement;

    expect(pill('drafts').classList.contains('write')).toBe(true);
    expect(pill('youtube.upload').classList.contains('irreversible')).toBe(true);
    expect(text(fixture, 'what-drafts')).toBe('Save scripts and renders');
    expect(text(fixture, 'what-youtube.upload')).toBe('Upload a video');
  });

  it('a read-only feature shows no permissions block', () => {
    const fixture = create();
    setFeature(fixture, { ...base, hasCredentials: true, status: 'disabled' });

    expect(fixture.nativeElement.querySelector('[data-test="permissions-github"]')).toBeNull();
  });
});
