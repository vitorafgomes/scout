import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { readFileSync } from 'node:fs';
import { beforeEach, describe, expect, it } from 'vitest';

import type { FeatureViewModel } from '../core/features.store';

import {
  FeatureCardComponent,
  featureChip,
  permissionCounts,
  type CardAction,
} from './feature-card.component';

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

const youtubeCatalog: FeatureViewModel = {
  ...base,
  id: 'youtube',
  displayName: 'YouTube',
  writeClass: 'executive',
  scopeMatrix: [
    {
      scope: 'channels.list',
      summary: 'Read a channel overview',
      actions: ['Read a channel overview'],
      access: 'read',
    },
    {
      scope: 'commentThreads.list',
      summary: 'Read the comments',
      actions: ['Read the comments'],
      access: 'read',
    },
    {
      scope: 'videos.list',
      summary: 'Read the videos',
      actions: ['Read the videos'],
      access: 'read',
    },
    {
      scope: "Scout's data folder",
      summary: 'Save scripts',
      actions: ['Save scripts'],
      access: 'write',
    },
    {
      scope: 'youtubeReporting jobs.create',
      summary: 'Create the job',
      actions: ['Create the job'],
      access: 'write',
    },
    {
      scope: 'videos.insert',
      summary: 'Upload a video',
      actions: ['Upload a video'],
      access: 'irreversible',
    },
  ],
};

function card(inputs: Record<string, unknown>): ComponentFixture<FeatureCardComponent> {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(FeatureCardComponent);
  for (const [key, value] of Object.entries({ id: 'github', name: 'GitHub', fact: '', ...inputs }))
    fixture.componentRef.setInput(key, value);
  fixture.detectChanges();
  return fixture;
}

function summaryOf(
  feature: FeatureViewModel,
  connection: 'connected' | 'not-connected' | 'unknown' | null,
): ComponentFixture<FeatureCardComponent> {
  return card({
    id: feature.id,
    name: feature.displayName,
    chip: featureChip(feature, connection),
    counts: permissionCounts(feature),
  });
}

function text(fixture: ComponentFixture<FeatureCardComponent>, testId: string): string | null {
  const el = fixture.nativeElement.querySelector(`[data-test="${testId}"]`) as HTMLElement | null;
  return el === null ? null : (el.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function cardStyles(): string {
  const source = readFileSync('src/app/features/feature-card.component.ts', 'utf8');
  return source.slice(source.indexOf('  styles:'));
}

function rule(css: string, selector: string): string {
  const at = css.indexOf(`      ${selector} {`);
  return css.slice(at, css.indexOf('}', at));
}

describe('a Features card: the state chip', () => {
  beforeEach(() => TestBed.resetTestingModule());

  for (const [status, chip] of [
    ['needs-credentials', 'Needs credentials'],
    ['disabled', 'Disabled'],
    ['enabled', 'Enabled'],
    ['error', 'Error'],
  ] as const) {
    it(`a control-plane feature that is ${status} reads "${chip}"`, () => {
      const fixture = summaryOf({ ...base, status }, null);

      expect(text(fixture, 'badge-github')).toBe(chip);
    });
  }

  for (const row of [
    { id: 'desktop', connection: 'connected', chip: 'On' },
    { id: 'github', connection: 'connected', chip: 'Connected' },
    { id: 'github', connection: 'not-connected', chip: 'Not connected' },
    { id: 'youtube', connection: 'connected', chip: 'Connected' },
    { id: 'youtube', connection: 'not-connected', chip: 'Not connected' },
  ] as const) {
    it(`the ${row.id} chip reads "${row.chip}" when ${row.connection}`, () => {
      const fixture = summaryOf({ ...base, id: row.id }, row.connection);

      expect(text(fixture, 'badge-' + row.id)).toBe(row.chip);
    });
  }

  it('a connection main has not answered yet shows no chip rather than a guess', () => {
    const fixture = summaryOf(base, 'unknown');

    expect(text(fixture, 'badge-github')).toBeNull();
  });
});

describe('a Features card: a summary of the same size for every feature', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('counts the permissions as Read, Write and Cannot be undone chips, never the rows', () => {
    const fixture = summaryOf(youtubeCatalog, 'connected');
    const chips = [...fixture.nativeElement.querySelectorAll('[data-test="perms-youtube"] li')].map(
      (li) => (li as HTMLElement).textContent?.trim(),
    );

    expect(chips).toEqual(['Read 3', 'Write 2', 'Cannot be undone 1']);
    expect(fixture.nativeElement.textContent).not.toContain('Upload a video');
  });

  it('a read-only feature shows its read count only', () => {
    const fixture = summaryOf(
      {
        ...base,
        id: 'desktop',
        scopeMatrix: [
          { scope: 'Screen', summary: 'read', actions: ['read'], access: 'read' },
          { scope: 'Microphone', summary: 'read', actions: ['read'], access: 'read' },
        ],
      },
      'connected',
    );

    expect(text(fixture, 'perms-desktop')).toBe('Read 2');
  });

  it('a feature with no rows of its own says what its writes ride on', () => {
    const fixture = card({
      id: 'docker',
      name: 'Docker',
      noPermissions: "Runs also need GitHub's write permission",
    });

    expect(text(fixture, 'perms-docker')).toBe("Runs also need GitHub's write permission");
  });

  it('the fact is one line cut with an ellipsis, the full text in its title', () => {
    const long =
      'Could not download the large-v3 voice model: The model download stopped: connection reset by the peer after 1.2 GB.';
    const fixture = card({ id: 'desktop', name: 'Desktop', fact: long });
    const fact = fixture.nativeElement.querySelector('[data-test="fact-desktop"]') as HTMLElement;
    const css = rule(cardStyles(), '.fact');

    expect(fact.getAttribute('title')).toBe(long);
    expect(fact.textContent?.trim()).toBe(long);
    expect(css).toContain('white-space: nowrap');
    expect(css).toContain('text-overflow: ellipsis');
    expect(css).toContain('overflow: hidden');
  });

  it('the card fills the grid row and clips, and its template projects nothing a feature could make long', () => {
    const source = readFileSync('src/app/features/feature-card.component.ts', 'utf8');
    const template = source.slice(source.indexOf('template: `'), source.indexOf('  styles:'));
    const css = cardStyles();

    expect(rule(css, ':host')).toContain('height: 100%');
    expect(rule(css, '.card')).toContain('height: 100%');
    expect(rule(css, '.card')).toContain('overflow: hidden');
    expect(template).not.toContain('<ng-content');
    expect(template).not.toContain('<details');
    expect(template).not.toContain('@for');
  });
});

describe('a Features card: its action and Details', () => {
  beforeEach(() => TestBed.resetTestingModule());

  const connect: CardAction = {
    label: 'Connect with GitHub',
    icon: 'plug',
    filled: true,
    disabled: false,
  };

  it('Details, with its icon, names the feature, says it opens a dialog, and asks to open the panel', () => {
    const fixture = card({ expanded: false });
    let asked = 0;
    fixture.componentInstance.details.subscribe(() => (asked += 1));
    const details = fixture.nativeElement.querySelector(
      '[data-test="details-github"]',
    ) as HTMLButtonElement;

    details.click();

    expect({
      label: details.getAttribute('aria-label'),
      popup: details.getAttribute('aria-haspopup'),
      expanded: details.getAttribute('aria-expanded'),
      icon: details.querySelector('svg') !== null,
      text: details.textContent?.trim(),
      asked,
    }).toEqual({
      label: 'GitHub details',
      popup: 'dialog',
      expanded: 'false',
      icon: true,
      text: 'Details',
      asked: 1,
    });
  });

  it('Details says it is expanded while its panel is open', () => {
    const fixture = card({ expanded: true });

    expect(
      fixture.nativeElement
        .querySelector('[data-test="details-github"]')
        .getAttribute('aria-expanded'),
    ).toBe('true');
  });

  it('the action, with its icon, asks the screen to run it; a disabled one cannot be clicked', () => {
    const fixture = card({ action: connect });
    let ran = 0;
    fixture.componentInstance.act.subscribe(() => (ran += 1));
    const act = fixture.nativeElement.querySelector(
      '[data-test="act-github"]',
    ) as HTMLButtonElement;
    act.click();
    fixture.componentRef.setInput('action', { ...connect, disabled: true });
    fixture.detectChanges();
    act.click();

    expect([act.querySelector('sc-icon') !== null, act.textContent?.trim()]).toEqual([
      true,
      'Connect with GitHub',
    ]);
    expect(ran).toBe(1);
    expect(act.disabled).toBe(true);
  });

  it('a card with nothing to do offers Details alone', () => {
    const fixture = card({ action: null });

    expect(fixture.nativeElement.querySelector('[data-test="act-github"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="details-github"]')).not.toBeNull();
  });
});
