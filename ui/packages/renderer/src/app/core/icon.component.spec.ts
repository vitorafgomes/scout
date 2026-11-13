import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import { ICONS, IconComponent, type IconName } from './icon.component';

const REQUIRED = [
  'check',
  'check-square',
  'edit',
  'trash',
  'plug',
  'save',
  'refresh',
  'copy',
  'download',
  'external',
  'sun',
  'moon',
  'monitor',
  'bell',
  'search',
  'chat',
  'sparkle',
  'grid',
  'cube',
  'calendar',
  'video',
  'list',
  'settings',
  'close',
  'play',
  'stop',
  'mic',
  'sound-wave',
  'speaker',
  'warning',
  'info',
  'error',
  'plug-off',
  'user-plus',
  'link',
  'alert',
  'chevron-down',
  'keyboard',
  'funnel',
];

function render(name: IconName, size?: number): SVGSVGElement {
  const fixture = TestBed.createComponent(IconComponent);
  fixture.componentRef.setInput('name', name);
  if (size !== undefined) fixture.componentRef.setInput('size', size);
  fixture.detectChanges();
  return fixture.nativeElement.querySelector('svg') as SVGSVGElement;
}

describe('sc-icon', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });

  it('refresh is the one-arrow circle every artboard draws (Components, Features, Models, Settings)', () => {
    expect(ICONS.refresh).toBe('M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7');
  });

  it('the registry names every icon the shell and the screens use', () => {
    expect(REQUIRED.filter((name) => !(name in ICONS))).toEqual([]);
  });

  it('every icon is drawn on the 24 grid: every number within -24 to 24 and nothing but path commands', () => {
    const offGrid = Object.entries(ICONS).filter(([, d]) => {
      const numbers = (d.match(/-?\d*\.?\d+/g) ?? []).map(Number);
      return !/^[MmLlHhVvCcSsQqTtAaZz0-9 .,-]+$/.test(d) || numbers.some((n) => n < -24 || n > 24);
    });

    expect(offGrid).toEqual([]);
  });

  for (const name of REQUIRED as IconName[]) {
    it(`${name} renders its own path, stroked in the text colour and hidden from screen readers`, () => {
      const svg = render(name);

      expect(svg.querySelector('path')?.getAttribute('d')).toBe(ICONS[name]);
      expect(svg.getAttribute('viewBox')).toBe('0 0 24 24');
      expect(svg.getAttribute('stroke')).toBe('currentColor');
      expect(svg.getAttribute('fill')).toBe('none');
      expect(svg.getAttribute('aria-hidden')).toBe('true');
    });
  }

  it('is 18 pixels unless the caller asks for another size', () => {
    const plain = render('sun');
    const large = render('bell', 24);

    expect([plain.getAttribute('width'), plain.getAttribute('height')]).toEqual(['18', '18']);
    expect([large.getAttribute('width'), large.getAttribute('height')]).toEqual(['24', '24']);
  });
});
