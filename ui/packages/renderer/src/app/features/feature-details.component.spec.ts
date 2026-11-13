import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import type { FeatureViewModel } from '../core/features.store';

import { FeatureDetailsComponent } from './feature-details.component';

function setFeature(
  fixture: ComponentFixture<FeatureDetailsComponent>,
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
    {
      scope: 'youtube.readonly',
      summary: 'The channel',
      actions: ['Read the channel'],
      access: 'read',
    },
    {
      scope: 'drafts',
      summary: 'Scripts and renders',
      actions: ['Save scripts', 'Save renders'],
      access: 'write',
    },
    {
      scope: 'youtube.upload',
      summary: 'Public or private',
      actions: ['Upload a video as public or private'],
      access: 'irreversible',
    },
  ],
  writeClass: 'executive',
};

function connectionCard(
  feature: FeatureViewModel,
  connection: 'connected' | 'not-connected',
): ComponentFixture<FeatureDetailsComponent> {
  const fixture = create();
  fixture.componentInstance.connection = connection;
  setFeature(fixture, feature);
  return fixture;
}

function text(fixture: ComponentFixture<FeatureDetailsComponent>, testId: string): string {
  const el = fixture.nativeElement.querySelector(`[data-test="${testId}"]`) as HTMLElement | null;
  return (el?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function create(): ComponentFixture<FeatureDetailsComponent> {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection()],
  });
  return TestBed.createComponent(FeatureDetailsComponent);
}

describe('FeatureDetailsComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the "needs credentials" state with toggle disabled', () => {
    const fixture = create();
    setFeature(fixture, base);

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

    const toggle = fixture.nativeElement.querySelector(
      '[data-test="toggle-github"]',
    ) as HTMLInputElement;
    expect(toggle.checked).toBe(true);
    expect(toggle.disabled).toBe(false);
  });

  it('renders the "error" state with the last-error message', () => {
    const fixture = create();
    setFeature(fixture, {
      ...base,
      hasCredentials: true,
      status: 'error',
      lastError: 'boom-from-worker',
    });

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
      ['youtube.readonly', ''],
      ['drafts', 'Write'],
      ['youtube.upload', 'Cannot be undone'],
    ]);
    expect(before).toBe('Write permission: not granted');
    expect(text(granted, 'grant-youtube')).toBe('Write permission: granted on 2026-11-02');
  });

  it('a grant error is in sight under the grant box, in the Permissions section, which never folds', () => {
    const fixture = connectionCard(
      { ...youtube, lastError: 'GitHub rejected the grant.' },
      'connected',
    );
    const permissions = fixture.nativeElement.querySelector(
      '[data-test="permissions-youtube"]',
    ) as HTMLElement;

    expect(permissions.tagName).toBe('SECTION');
    expect(permissions.querySelector('[data-test="grant-err-youtube"]')).not.toBeNull();
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
        { scope: 'Screen', summary: 'A screenshot', actions: ['read'], access: 'read' },
        { scope: 'Microphone', summary: 'Your voice', actions: ['read'], access: 'read' },
      ],
    });

    expect(text(fixture, 'reads-desktop')).toContain('Reads');
    expect(text(fixture, 'label-Screen')).toBe('Screen');
    expect(text(fixture, 'label-Microphone')).toBe('Microphone');
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

  it('Reads and Permissions are open sections, as Features.dc.html draws them: a 14 px heading with its count, the rows always in sight', () => {
    const fixture = connectionCard(youtube, 'connected');
    const section = (test: string) =>
      fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement;

    expect({
      reads: [section('reads-youtube').tagName, text(fixture, 'reads-summary-youtube')],
      permissions: [
        section('permissions-youtube').tagName,
        text(fixture, 'permissions-summary-youtube'),
      ],
      headings: [
        section('reads-summary-youtube').tagName,
        section('permissions-summary-youtube').tagName,
      ],
      named: [
        section('reads-youtube').getAttribute('aria-labelledby'),
        section('permissions-youtube').getAttribute('aria-labelledby'),
      ],
      details: fixture.nativeElement.querySelector('details'),
    }).toEqual({
      reads: ['SECTION', 'Reads 1'],
      permissions: ['SECTION', 'Permissions 2'],
      headings: ['H3', 'H3'],
      named: ['reads-title-youtube', 'permissions-title-youtube'],
      details: null,
    });
  });

  it('since when writes are allowed is in the grant chip, not the heading', () => {
    const fixture = connectionCard(
      { ...youtube, granted: true, grantedAt: '2026-11-02T10:00:00Z' },
      'connected',
    );

    expect([text(fixture, 'permissions-summary-youtube'), text(fixture, 'grant-youtube')]).toEqual([
      'Permissions 2',
      'Write permission: granted on 2026-11-02',
    ]);
  });

  it("a read is a row with its label and the row's one short sentence, never its actions joined", () => {
    const fixture = connectionCard(
      {
        ...youtube,
        scopeMatrix: [
          {
            scope: 'youtube.readonly',
            summary: 'The channel and its uploads',
            actions: ['Read the channel', 'List the uploads'],
            access: 'read',
          },
        ],
      },
      'connected',
    );

    expect(text(fixture, 'label-youtube.readonly')).toBe('youtube.readonly');
    expect(text(fixture, 'what-youtube.readonly')).toBe('The channel and its uploads');
  });

  it("a permission row shows its access pill and the row's one short sentence, never its actions joined", () => {
    const fixture = connectionCard(youtube, 'connected');
    const pill = (scope: string) =>
      fixture.nativeElement.querySelector(`[data-test="access-${scope}"]`) as HTMLElement;

    expect(pill('drafts').classList.contains('write')).toBe(true);
    expect(pill('youtube.upload').classList.contains('irreversible')).toBe(true);
    expect(text(fixture, 'what-drafts')).toBe('Scripts and renders');
    expect(text(fixture, 'what-youtube.upload')).toBe('Public or private');
  });

  it('a read-only feature shows no permissions block', () => {
    const fixture = create();
    setFeature(fixture, { ...base, hasCredentials: true, status: 'disabled' });

    expect(fixture.nativeElement.querySelector('[data-test="permissions-github"]')).toBeNull();
  });
});

describe('FeatureDetailsComponent: the reason beside Allow writes', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const [connection, reason, disabled] of [
    ['connected', 'You review the list before anything is allowed.', false],
    ['not-connected', 'Connect YouTube first to allow writes.', true],
  ] as const) {
    it(`a ${connection} card says "${reason}" and describes Allow writes with it`, () => {
      const fixture = connectionCard(youtube, connection);
      const allow = fixture.nativeElement.querySelector(
        '[data-test="allow-writes-youtube"]',
      ) as HTMLButtonElement;

      expect(text(fixture, 'grant-reason-youtube')).toBe(reason);
      expect(allow.getAttribute('aria-describedby')).toBe('grant-reason-youtube');
      expect(allow.disabled).toBe(disabled);
      expect([allow.querySelector('sc-icon') !== null, allow.textContent?.trim()]).toEqual([
        true,
        'Allow writes',
      ]);
    });
  }

  it('a granted card offers Revoke writes with its icon and no reason', () => {
    const fixture = connectionCard({ ...youtube, granted: true }, 'connected');

    expect(fixture.nativeElement.querySelector('[data-test="grant-reason-youtube"]')).toBeNull();
    expect(text(fixture, 'revoke-writes-youtube')).toBe('Revoke writes');
  });
});

describe('Feature details as in the prototype', () => {
  beforeEach(() => TestBed.resetTestingModule());

  const youtubeCatalog: FeatureViewModel = {
    ...youtube,
    scopeMatrix: [
      {
        scope: 'channels.list',
        summary: 'Title, handle and counts',
        actions: ['Read a channel overview'],
        access: 'read',
      },
      {
        scope: 'commentThreads.list',
        summary: 'The comments on your videos',
        actions: ['Read the comments'],
        access: 'read',
      },
      {
        scope: "Scout's data folder",
        summary: 'Scripts',
        actions: ['Save scripts'],
        access: 'write',
      },
      {
        scope: 'youtubeReporting jobs.create',
        summary: 'The job',
        actions: ['Create the job'],
        access: 'write',
      },
      {
        scope: 'videos.insert',
        summary: 'Public or private',
        actions: ['Upload a video'],
        access: 'irreversible',
      },
      {
        scope: 'comments.setModerationStatus',
        summary: 'Hold a comment',
        actions: ['Hold a comment'],
        access: 'irreversible',
      },
    ],
  };

  it('reads show as label and description rows, never as scope chips', () => {
    const fixture = connectionCard(youtubeCatalog, 'connected');
    const reads = fixture.nativeElement.querySelector('[data-test="reads-youtube"]') as HTMLElement;
    const rows = [...reads.querySelectorAll('li')].map((li) => [
      (li.querySelector('.label')?.textContent ?? '').trim(),
      (li.querySelector('.what')?.textContent ?? '').trim(),
    ]);

    expect(rows).toEqual([
      ['Channel overview', 'Title, handle and counts'],
      ['Comments', 'The comments on your videos'],
    ]);
    expect(reads.querySelector('.chip, code')).toBeNull();
  });

  it('a permission row shows a human label with Write or Cannot be undone, never the code scope', () => {
    const fixture = connectionCard(youtubeCatalog, 'connected');
    const permissions = fixture.nativeElement.querySelector(
      '[data-test="permissions-youtube"]',
    ) as HTMLElement;
    const rows = [...permissions.querySelectorAll('li')].map((li) => [
      (li.querySelector('.label')?.textContent ?? '').trim(),
      (li.querySelector('.access')?.textContent ?? '').trim(),
    ]);

    expect(rows).toEqual([
      ["Scout's data folder", 'Write'],
      ['Reach reporting job', 'Write'],
      ['Upload a video', 'Cannot be undone'],
      ['Moderate a comment', 'Cannot be undone'],
    ]);
    expect(permissions.querySelector('code')).toBeNull();
    expect(permissions.textContent).not.toContain('videos.insert');
  });
});
