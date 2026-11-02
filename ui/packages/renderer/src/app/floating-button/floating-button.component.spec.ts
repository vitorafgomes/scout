import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from '../core/scout-bridge';

import { FloatingButtonComponent } from './floating-button.component';

function render(localOpen: () => Promise<void>): ComponentFixture<FloatingButtonComponent> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: { localOpen } as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(FloatingButtonComponent);
  fixture.detectChanges();
  return fixture;
}

function button(fixture: ComponentFixture<FloatingButtonComponent>): HTMLButtonElement | null {
  return fixture.nativeElement.querySelector('[data-test="floating-button"]');
}

describe('FloatingButtonComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    document.documentElement.style.background = '';
    document.body.style.background = '';
    document.body.style.margin = '';
    document.body.style.overflow = '';
  });

  it('renders one button labelled "Ask Scout" and no text', () => {
    const fixture = render(vi.fn(async () => undefined));

    const found = button(fixture);
    expect(found?.getAttribute('type')).toBe('button');
    expect(found?.getAttribute('aria-label')).toBe('Ask Scout');
    expect(found?.textContent?.trim()).toBe('');
    expect(fixture.nativeElement.querySelectorAll('button')).toHaveLength(1);
  });

  it('click asks main to open the quick-ask window', () => {
    const localOpen = vi.fn(async () => undefined);
    const fixture = render(localOpen);

    button(fixture)!.click();

    expect(localOpen).toHaveBeenCalledTimes(1);
  });

  it('init makes the page background transparent and edge to edge, so only the round button shows', () => {
    document.documentElement.style.background = 'rgb(17, 17, 17)';
    document.body.style.background = 'rgb(17, 17, 17)';

    render(vi.fn(async () => undefined));

    expect(document.documentElement.style.background).toBe('transparent');
    expect(document.body.style.background).toBe('transparent');
    expect(document.body.style.margin).toBe('0px');
    expect(document.body.style.overflow).toBe('hidden');
  });
});
