import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalBubbleStatus, LocalShortcut, LocalTurn } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { FloatingCardComponent } from './floating-card.component';

function makeBridge(
  turns: readonly LocalTurn[],
  shortcut: LocalShortcut | null,
  listening: boolean,
  corner: LocalBubbleStatus['corner'],
) {
  const bubbleListeners: ((status: LocalBubbleStatus) => void)[] = [];
  const status = (c: LocalBubbleStatus['corner']): LocalBubbleStatus => ({
    enabled: true,
    corner: c,
    display: 'cursor',
    monitors: [],
    hint: null,
  });
  return {
    localListening: vi.fn(async () => ({ listening })),
    onLocalListening: vi.fn(() => () => undefined),
    localGetShortcut: vi.fn(async (): Promise<LocalShortcut> => {
      if (shortcut === null) throw new Error('[scout:quick-ask/shortcut-unreadable] boom');
      return shortcut;
    }),
    localGetTurns: vi.fn(async () => turns),
    onLocalTurns: vi.fn(() => () => undefined),
    localGetBubble: vi.fn(async () => status(corner)),
    onLocalBubble: vi.fn((listener: (s: LocalBubbleStatus) => void) => {
      bubbleListeners.push(listener);
      return () => undefined;
    }),
    pushCorner: (c: LocalBubbleStatus['corner']) =>
      bubbleListeners.forEach((listener) => listener(status(c))),
  };
}

async function render(
  turns: readonly LocalTurn[] = [],
  shortcut: LocalShortcut | null = null,
  listening = false,
  corner: LocalBubbleStatus['corner'] = 'bottom-right',
): Promise<{
  fixture: ComponentFixture<FloatingCardComponent>;
  bridge: ReturnType<typeof makeBridge>;
}> {
  const bridge = makeBridge(turns, shortcut, listening, corner);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(FloatingCardComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, bridge };
}

function declared(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found === undefined) continue;
    const matches = selector!.split(',').some((one) => {
      try {
        return node.matches(one.trim());
      } catch {
        return false;
      }
    });
    if (matches) value = found;
  }
  return value;
}

const text = (fixture: ComponentFixture<FloatingCardComponent>, test: string) =>
  (fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement).textContent?.trim();

describe('FloatingCardComponent: the card the FloatingButton artboard draws beside the button', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    document.documentElement.style.background = '';
    document.body.style.background = '';
    document.body.style.margin = '';
    document.body.style.overflow = '';
  });

  const SHORTCUT: LocalShortcut = {
    accelerator: 'CommandOrControl+Alt+Space',
    registered: true,
    hint: null,
  };

  it.each([
    ['Idle', [], SHORTCUT, false, 'Click, or press Ctrl+Alt+Space, to open the quick ask window.'],
    ['Idle', [], null, false, 'Click to open the quick ask window.'],
    [
      'Listening',
      [],
      SHORTCUT,
      true,
      'A Listen, System audio or Meeting is open; this shows ahead of answering and error, so an open microphone is never hidden. Open the quick ask window to see the transcript.',
    ],
    [
      'Answering',
      [{ id: 'q-1', question: 'is prod green?', reply: '', citations: [], state: 'running' }],
      SHORTCUT,
      false,
      'Scout is working on your question. One question at a time.',
    ],
    [
      'Error',
      [{ id: 'q-1', question: 'is prod green?', reply: 'Boom.', citations: [], state: 'failed' }],
      SHORTCUT,
      false,
      'The last answer failed, the worker not running included; a stop by you is not a failure. Open the quick ask window to read the reason.',
    ],
  ] as const)(
    '%s: the card reads its title and its line',
    async (title, turns, shortcut, listening, line) => {
      const { fixture } = await render(turns as readonly LocalTurn[], shortcut, listening);

      expect({
        title: text(fixture, 'floating-card-title'),
        line: text(fixture, 'floating-card-line'),
        role: fixture.nativeElement
          .querySelector('[data-test="floating-card"]')
          .getAttribute('role'),
      }).toEqual({ title, line, role: 'status' });
    },
  );

  it.each([
    ['bottom-right', 'flex-end', 'flex-end', '0 12px 10px'],
    ['bottom-left', 'flex-end', 'flex-start', '0 12px 10px'],
    ['top-right', 'flex-start', 'flex-end', '10px 12px 0'],
    ['top-left', 'flex-start', 'flex-start', '10px 12px 0'],
  ] as const)(
    'with the button in the %s corner the card sits on the side of its window next to the button, 10 px from it',
    async (corner, vertical, horizontal, padding) => {
      const { fixture, bridge } = await render();

      bridge.pushCorner(corner);
      fixture.detectChanges();
      const host = fixture.nativeElement as HTMLElement;

      expect({
        corner: host.getAttribute('data-corner'),
        vertical: declared(host, 'justify-content'),
        horizontal: declared(host, 'align-items'),
        padding: declared(host, 'padding'),
      }).toEqual({ corner, vertical, horizontal, padding });
    },
  );

  it('the card paints with the tokens, as the artboard draws it: surface, line border, 10 px corners, a 220 px width at most', async () => {
    const { fixture } = await render();
    const card = fixture.nativeElement.querySelector('[data-test="floating-card"]') as HTMLElement;
    const title = fixture.nativeElement.querySelector('[data-test="floating-card-title"]');
    const line = fixture.nativeElement.querySelector('[data-test="floating-card-line"]');

    expect({
      background: declared(card, 'background'),
      border: declared(card, 'border'),
      radius: declared(card, 'border-radius'),
      width: declared(card, 'max-width'),
      padding: declared(card, 'padding'),
      shadow: declared(card, 'box-shadow'),
      title: [declared(title, 'font-weight'), declared(title, 'color')],
      line: [declared(line, 'font-size'), declared(line, 'color')],
    }).toEqual({
      background: 'var(--sc-surface)',
      border: '1px solid var(--sc-line)',
      radius: '10px',
      width: '220px',
      padding: '8px 10px',
      shadow: '0 6px 18px var(--card-shadow)',
      title: ['600', 'var(--sc-fg)'],
      line: ['12px', 'var(--sc-fg-2)'],
    });
  });

  it('the page around the card is transparent and edge to edge', async () => {
    await render();

    expect(document.documentElement.style.backgroundColor).toBe('transparent');
    expect(document.body.style.backgroundColor).toBe('transparent');
    expect(document.body.style.margin).toBe('0px');
  });
});
