import { readFileSync } from 'node:fs';

import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  COLLAPSIBLE_LISTS_STORAGE_KEY,
  CollapsibleListComponent,
} from './collapsible-list.component';

@Component({
  standalone: true,
  imports: [CollapsibleListComponent],
  template: `
    <sc-collapsible-list
      key="repos"
      filterLabel="Filter the repositories"
      [summary]="'Repositories: ' + items().length + ' (bot can post in these)'"
      [items]="items()"
    >
      <ng-template let-repo>{{ repo }}</ng-template>
    </sc-collapsible-list>
  `,
})
class HostComponent {
  readonly items = signal<readonly string[]>(['acme/shop', 'acme/api']);
}

const many = Array.from({ length: 12 }, (_, i) => `acme/repo-${i + 1}`);

function render(items?: readonly string[]): ComponentFixture<HostComponent> {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(HostComponent);
  if (items !== undefined) fixture.componentInstance.items.set(items);
  fixture.detectChanges();
  return fixture;
}

function el(fixture: ComponentFixture<HostComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function toggle(fixture: ComponentFixture<HostComponent>): void {
  el(fixture, 'list-toggle-repos')!.click();
  fixture.detectChanges();
}

function rows(fixture: ComponentFixture<HostComponent>): string[] {
  return [...fixture.nativeElement.querySelectorAll('[data-test="list-row-repos"]')].map(
    (li) => (li as HTMLElement).textContent?.trim() ?? '',
  );
}

function expanded(fixture: ComponentFixture<HostComponent>): string | null {
  return el(fixture, 'list-toggle-repos')!.getAttribute('aria-expanded');
}

describe('A collapsible list on Features', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(COLLAPSIBLE_LISTS_STORAGE_KEY);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('starts collapsed: the summary shows, the rows do not', () => {
    const fixture = render();

    expect(expanded(fixture)).toBe('false');
    expect(el(fixture, 'list-body-repos')).toBeNull();
    expect(rows(fixture)).toEqual([]);
  });

  it('the header is a button that controls the list body and sums the list up with its count', () => {
    const fixture = render();
    const head = el(fixture, 'list-toggle-repos')!;

    toggle(fixture);

    expect(head.tagName).toBe('BUTTON');
    expect(head.getAttribute('aria-controls')).toBe('list-body-repos');
    expect(el(fixture, 'list-body-repos')?.id).toBe('list-body-repos');
    expect(el(fixture, 'list-summary-repos')?.textContent?.trim()).toBe(
      'Repositories: 2 (bot can post in these)',
    );
  });

  it('a click opens it and a second click closes it again', () => {
    const fixture = render();

    toggle(fixture);
    const opened = [expanded(fixture), rows(fixture)];
    toggle(fixture);

    expect(opened).toEqual(['true', ['acme/shop', 'acme/api']]);
    expect(expanded(fixture)).toBe('false');
    expect(rows(fixture)).toEqual([]);
  });

  it('the open or closed choice is remembered when the list is shown again', () => {
    const first = render();
    toggle(first);
    first.destroy();
    TestBed.resetTestingModule();

    const again = render();

    expect(expanded(again)).toBe('true');
    expect(rows(again)).toEqual(['acme/shop', 'acme/api']);
  });

  it('when the storage cannot be read or written, the list falls back to collapsed and still toggles', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('storage is off');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage is off');
    });
    const fixture = render();
    const atFirst = expanded(fixture);

    toggle(fixture);

    expect(atFirst).toBe('false');
    expect(expanded(fixture)).toBe('true');
  });

  it('a short list has the filter above it, as Features.dc.html draws it, and no capped height', () => {
    const fixture = render();
    toggle(fixture);

    expect((el(fixture, 'list-filter-repos') as HTMLInputElement | null)?.type).toBe('search');
    expect(fixture.nativeElement.querySelector('.rows--long')).toBeNull();
  });

  it('a list longer than 8 rows scrolls inside a capped height and its filter narrows the rows', () => {
    const fixture = render(many);
    toggle(fixture);
    const filter = el(fixture, 'list-filter-repos') as HTMLInputElement;
    const before = rows(fixture).length;

    filter.value = 'REPO-1';
    filter.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(before).toBe(12);
    expect(fixture.nativeElement.querySelector('.rows--long')).not.toBeNull();
    expect(filter.getAttribute('aria-label')).toBe('Filter the repositories');
    expect(rows(fixture)).toEqual(['acme/repo-1', 'acme/repo-10', 'acme/repo-11', 'acme/repo-12']);
  });

  it('the capped height in the style source holds about 8 rows', () => {
    const source = readFileSync('src/app/features/collapsible-list.component.ts', 'utf8');

    expect(source).toMatch(
      /\.rows--long \{\s*max-height: var\(--sc-list-max-height, 256px\);\s*overflow-y: auto;/,
    );
  });
});
