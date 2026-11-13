import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ICONS } from '../core/icon.component';
import { ConversationMode, type ConversationState } from './conversation-mode';
import { TalkButtonComponent } from './talk-button.component';

function fakeMode(initial: ConversationState) {
  const state = signal<ConversationState>(initial);
  return {
    state,
    on: computed(() => state() !== 'off'),
    error: signal<string | null>(null),
    heard: signal(''),
    toggle: vi.fn(async () => undefined),
  };
}

function stubReducedMotion(reduce: boolean): void {
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({ matches: reduce && query === '(prefers-reduced-motion: reduce)' })),
  );
}

async function render(state: ConversationState) {
  const mode = fakeMode(state);
  TestBed.configureTestingModule({
    imports: [TalkButtonComponent],
    providers: [provideZonelessChangeDetection(), { provide: ConversationMode, useValue: mode }],
  });
  const fixture = TestBed.createComponent(TalkButtonComponent);
  await fixture.whenStable();
  const button = fixture.nativeElement.querySelector('[data-test="talk"]') as HTMLButtonElement;
  const label = () =>
    (
      fixture.nativeElement.querySelector('[data-test="talk-state"]') as HTMLElement | null
    )?.textContent?.trim() ?? null;
  return { fixture, mode, button, label };
}

describe('the voice conversation button', () => {
  beforeEach(() => TestBed.resetTestingModule());
  afterEach(() => vi.unstubAllGlobals());

  it.each([
    ['off', 'Start a voice conversation', 'false', null],
    ['listening', 'End the voice conversation', 'true', 'Listening'],
    ['thinking', 'End the voice conversation', 'true', 'Thinking'],
    ['speaking', 'End the voice conversation', 'true', 'Speaking'],
  ] as const)(
    'while %s it is named "%s", pressed is %s and the state written beside it is %s',
    async (state, name, pressed, written) => {
      stubReducedMotion(false);
      const t = await render(state);

      expect(t.button.getAttribute('aria-label')).toBe(name);
      expect(t.button.getAttribute('aria-pressed')).toBe(pressed);
      expect(t.label()).toBe(written);
    },
  );

  it('draws the sound-wave glyph, hidden from screen readers, and a click toggles the conversation', async () => {
    stubReducedMotion(false);
    const t = await render('off');
    const svg = t.button.querySelector('svg')!;

    t.button.click();

    expect(svg.getAttribute('aria-hidden')).toBe('true');
    expect(svg.querySelector('path')?.getAttribute('d')).toBe(ICONS['sound-wave']);
    expect(t.mode.toggle).toHaveBeenCalledTimes(1);
  });

  it.each([
    ['listening', false, true],
    ['speaking', false, true],
    ['thinking', false, false],
    ['off', false, false],
    ['listening', true, false],
    ['speaking', true, false],
  ] as const)(
    'while %s with reduced motion %s the wave moves: %s',
    async (state, reduce, moving) => {
      stubReducedMotion(reduce);
      const t = await render(state);

      expect(t.button.classList.contains('moving')).toBe(moving);
    },
  );

  it('the label stays readable to a screen reader with reduced motion', async () => {
    stubReducedMotion(true);
    const t = await render('listening');

    expect(t.label()).toBe('Listening');
    expect(
      (
        t.fixture.nativeElement.querySelector('[data-test="talk-state"]') as HTMLElement
      ).getAttribute('aria-live'),
    ).toBe('polite');
  });

  it('is disabled while something else holds the microphone, but a conversation already on can always be ended', async () => {
    stubReducedMotion(false);
    const t = await render('off');
    t.fixture.componentRef.setInput('disabled', true);
    await t.fixture.whenStable();
    const offDisabled = t.button.disabled;

    t.mode.state.set('listening');
    await t.fixture.whenStable();

    expect([offDisabled, t.button.disabled]).toEqual([true, false]);
  });

  it('writes what Scout heard so far beside the state, announced politely, and nothing when it heard nothing', async () => {
    stubReducedMotion(false);
    const t = await render('listening');
    const heard = () =>
      t.fixture.nativeElement.querySelector('[data-test="talk-heard"]') as HTMLElement | null;
    const before = heard();

    t.mode.heard.set('what is the build');
    await t.fixture.whenStable();

    expect(before).toBeNull();
    expect(heard()?.textContent?.trim()).toBe('what is the build');
    expect(heard()?.getAttribute('aria-live')).toBe('polite');
    expect(t.label()).toBe('Listening');
  });

  it('is drawn as both artboards draw it: a 32 px round button, white glyph on the strong accent, idle and on', async () => {
    stubReducedMotion(false);
    const t = await render('off');
    const idle = [declared(t.button, 'background'), declared(t.button, 'color')];
    t.mode.state.set('listening');
    await t.fixture.whenStable();

    expect({
      size: [declared(t.button, 'width'), declared(t.button, 'height')],
      radius: declared(t.button, 'border-radius'),
      idle,
      on: [declared(t.button, 'background'), declared(t.button, 'color')],
    }).toEqual({
      size: ['32px', '32px'],
      radius: '50%',
      idle: ['var(--sc-accent-strong)', 'var(--sc-on-accent)'],
      on: ['var(--sc-accent-strong)', 'var(--sc-on-accent)'],
    });
  });

  it.each([
    [true, 'Connect an AI Source first', 'Connect an AI Source first'],
    [false, 'Connect an AI Source first', 'Start a voice conversation'],
    [true, null, 'Start a voice conversation'],
  ] as const)(
    'off is %s with the reason %s: the title, which a screen reader reads as the description, is "%s"',
    async (disabled, reason, title) => {
      stubReducedMotion(false);
      const t = await render('off');
      t.fixture.componentRef.setInput('disabled', disabled);
      t.fixture.componentRef.setInput('offReason', reason);
      await t.fixture.whenStable();

      expect({
        name: t.button.getAttribute('aria-label'),
        title: t.button.title,
        disabled: t.button.disabled,
      }).toEqual({ name: 'Start a voice conversation', title, disabled });
    },
  );
});

/** The value the last rule matching `node` in the page's style elements declares for `property`; null when none does. */
function declared(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found === undefined) continue;
    if (
      selector!.split(',').some((one) => {
        try {
          return node.matches(one.trim());
        } catch {
          return false;
        }
      })
    )
      value = found;
  }
  return value;
}
