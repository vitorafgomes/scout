import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import { StatusChipComponent, type ChipTone } from './status-chip.component';

function declared(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found !== undefined && selector!.split(',').some((one) => node.matches(one.trim()))) {
      value = found;
    }
  }
  return value;
}

function render(tone: ChipTone, label: string): HTMLElement {
  const fixture = TestBed.createComponent(StatusChipComponent);
  fixture.componentRef.setInput('tone', tone);
  fixture.componentRef.setInput('label', label);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

const CHECK = 'M20 6L9 17l-5-5';
const TRIANGLE = 'M12 3l10 18H2zM12 10v4M12 17v.5';
const CROSSED_CIRCLE = 'M15 9l-6 6M9 9l6 6M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z';
const ARC = 'M12 3a9 9 0 1 0 9 9';
const DOT = 'M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z';

const TONES: readonly (readonly [ChipTone, string, string, string, string, string])[] = [
  ['ok', 'Connected', 'the check', CHECK, 'var(--sc-ok-container)', 'var(--sc-on-ok-container)'],
  [
    'warn',
    'Near the usage limit',
    'the triangle',
    TRIANGLE,
    'var(--sc-warn-container)',
    'var(--sc-on-warn-container)',
  ],
  [
    'error',
    'Stopped unexpectedly',
    'the crossed circle',
    CROSSED_CIRCLE,
    'var(--sc-error-container)',
    'var(--sc-on-error-container)',
  ],
  [
    'info',
    'Checking...',
    'the arc',
    ARC,
    'var(--sc-info-container)',
    'var(--sc-on-info-container)',
  ],
  ['neutral', 'Not connected', 'the dot', DOT, 'var(--sc-surface-2)', 'var(--sc-muted)'],
];

describe('sc-status-chip', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });

  for (const [tone, label, glyphName, glyph, background, color] of TONES) {
    it(`the ${tone} chip reads "${label}" after ${glyphName}, on ${background} under ${color}`, () => {
      const chip = render(tone, label);
      const lead = chip.firstElementChild!;

      expect({
        text: chip.textContent?.trim(),
        leadingGlyph: [lead.tagName.toLowerCase(), lead.querySelector('path')?.getAttribute('d')],
        glyphHiddenFromScreenReaders: lead.getAttribute('aria-hidden'),
        tone: chip.getAttribute('data-tone'),
        background: declared(chip, 'background'),
        color: declared(chip, 'color'),
      }).toEqual({
        text: label,
        leadingGlyph: ['svg', glyph],
        glyphHiddenFromScreenReaders: 'true',
        tone,
        background,
        color,
      });
    });
  }

  it('every tone draws the same pill: 12 px text at weight 600, a 13 px glyph 5 px before it', () => {
    const pills = TONES.map(([tone, label]) => {
      const chip = render(tone, label);
      const glyph = chip.querySelector('svg')!;
      return {
        tone,
        display: declared(chip, 'display'),
        gap: declared(chip, 'gap'),
        padding: declared(chip, 'padding'),
        radius: declared(chip, 'border-radius'),
        size: declared(chip, 'font-size'),
        weight: declared(chip, 'font-weight'),
        glyph: [glyph.getAttribute('width'), glyph.getAttribute('height')],
      };
    });

    expect(pills).toEqual(
      (['ok', 'warn', 'error', 'info', 'neutral'] as const).map((tone) => ({
        tone,
        display: 'inline-flex',
        gap: '5px',
        padding: '3px 10px',
        radius: '12px',
        size: '12px',
        weight: '600',
        glyph: ['13', '13'],
      })),
    );
  });

  it('a chip whose tone changes redraws its glyph and container', () => {
    const fixture = TestBed.createComponent(StatusChipComponent);
    fixture.componentRef.setInput('tone', 'info');
    fixture.componentRef.setInput('label', 'Checking...');
    fixture.detectChanges();
    const chip = fixture.nativeElement as HTMLElement;

    fixture.componentRef.setInput('tone', 'ok');
    fixture.componentRef.setInput('label', 'Connected');
    fixture.detectChanges();

    expect([
      chip.textContent?.trim(),
      chip.querySelector('path')?.getAttribute('d'),
      declared(chip, 'background'),
    ]).toEqual(['Connected', CHECK, 'var(--sc-ok-container)']);
  });
});
