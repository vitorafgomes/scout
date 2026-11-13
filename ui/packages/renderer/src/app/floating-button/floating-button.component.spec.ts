import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalListeningResponse, LocalShortcut, LocalTurn } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { FloatingButtonComponent } from './floating-button.component';

function makeBridge(
  localOpen: () => Promise<void>,
  initial: readonly LocalTurn[] = [],
  shortcut: LocalShortcut | null = null,
  listeningOnOpen: boolean | null = false,
) {
  const listeners: ((turns: readonly LocalTurn[]) => void)[] = [];
  const listeningListeners: ((state: LocalListeningResponse) => void)[] = [];
  const unsubscribed: string[] = [];
  return {
    unsubscribed,
    localListening: vi.fn(async (): Promise<LocalListeningResponse> => {
      if (listeningOnOpen === null) throw new Error('[scout:local/not-available] boom');
      return { listening: listeningOnOpen };
    }),
    onLocalListening: vi.fn((listener: (state: LocalListeningResponse) => void) => {
      listeningListeners.push(listener);
      return () => unsubscribed.push('listening');
    }),
    pushListening: (listening: boolean) =>
      listeningListeners.forEach((listener) => listener({ listening })),
    localOpen,
    localGetShortcut: vi.fn(async (): Promise<LocalShortcut> => {
      if (shortcut === null) throw new Error('[scout:quick-ask/shortcut-unreadable] boom');
      return shortcut;
    }),
    localGetTurns: vi.fn(async () => initial),
    onLocalTurns: vi.fn((listener: (turns: readonly LocalTurn[]) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
    push: (turns: readonly LocalTurn[]) => listeners.forEach((listener) => listener(turns)),
    localBubbleCard: vi.fn(async (req: { shown: boolean }) => ({ shown: req.shown })),
  };
}

function render(
  localOpen: () => Promise<void>,
  initial: readonly LocalTurn[] = [],
  shortcut: LocalShortcut | null = null,
  listeningOnOpen: boolean | null = false,
): { fixture: ComponentFixture<FloatingButtonComponent>; bridge: ReturnType<typeof makeBridge> } {
  const bridge = makeBridge(localOpen, initial, shortcut, listeningOnOpen);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(FloatingButtonComponent);
  fixture.detectChanges();
  return { fixture, bridge };
}

function button(fixture: ComponentFixture<FloatingButtonComponent>): HTMLButtonElement | null {
  return fixture.nativeElement.querySelector('[data-test="floating-button"]');
}

function recording(fixture: ComponentFixture<FloatingButtonComponent>): boolean {
  const ring = fixture.nativeElement.querySelector('[data-test="floating-recording-ring"]');
  const dot = fixture.nativeElement.querySelector('[data-test="floating-recording-dot"]');
  expect(ring === null, 'the recording ring and dot show together').toBe(dot === null);
  return ring !== null;
}

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
    const { fixture } = render(vi.fn(async () => undefined));

    const found = button(fixture);
    expect(found?.getAttribute('type')).toBe('button');
    expect(found?.getAttribute('aria-label')).toBe('Ask Scout');
    expect(found?.textContent?.trim()).toBe('');
    expect(fixture.nativeElement.querySelectorAll('button')).toHaveLength(1);
  });

  it('click asks main to open the quick-ask window', () => {
    const localOpen = vi.fn(async () => undefined);
    const { fixture } = render(localOpen);

    button(fixture)!.click();

    expect(localOpen).toHaveBeenCalledTimes(1);
  });

  it('init makes the page background transparent and edge to edge, so only the round button shows', () => {
    document.documentElement.style.background = 'rgb(17, 17, 17)';
    document.body.style.background = 'rgb(17, 17, 17)';

    render(vi.fn(async () => undefined));

    expect(document.documentElement.style.backgroundColor).toBe('transparent');
    expect(document.body.style.backgroundColor).toBe('transparent');
    expect(document.body.style.margin).toBe('0px');
    expect(document.body.style.overflow).toBe('hidden');
  });

  it('the circle is painted with the accent tokens, the same in both themes, not a literal colour', () => {
    const { fixture } = render(vi.fn(async () => undefined));
    const found = button(fixture)!;

    expect(declared(found, 'background')).toBe('var(--sc-accent-strong)');
    expect(declared(found, 'color')).toBe('var(--sc-on-accent)');
  });
});

describe('FloatingButtonComponent: the states main sends it', () => {
  type State =
    | 'no conversation'
    | 'answering'
    | 'last answer done'
    | 'last answer failed'
    | 'last answer failed with a partial reply'
    | 'stopped by the owner'
    | 'stopped by the owner after a partial reply'
    | 'worker not running';

  const turn = (state: LocalTurn['state'], extra: Partial<LocalTurn> = {}): LocalTurn => ({
    id: 'q-1',
    question: 'is prod green?',
    reply: '',
    citations: [],
    state,
    ...extra,
  });

  const TURNS: Readonly<Record<State, readonly LocalTurn[]>> = {
    'no conversation': [],
    answering: [turn('running')],
    'last answer done': [turn('done', { reply: 'Green.' })],
    'last answer failed': [turn('failed', { reply: 'The AI Source did not answer.' })],
    'last answer failed with a partial reply': [
      turn('failed', { reply: 'It is', failure: 'The AI Source did not answer.' }),
    ],
    'stopped by the owner': [turn('failed', { reply: 'Cancelled.' })],
    'stopped by the owner after a partial reply': [
      turn('failed', { reply: 'It is', failure: 'Cancelled.' }),
    ],
    'worker not running': [turn('failed', { reply: 'Scout is not running.', notRunning: true })],
  };

  const IDLE = {
    label: 'Ask Scout',
    title: 'Ask Scout',
    ring: false,
    badge: false,
    recording: false,
  };
  const ANSWERING = {
    label: 'Ask Scout, answering',
    title: 'Scout is answering. Open to follow the answer.',
    ring: true,
    badge: false,
    recording: false,
  };
  const FAILED = {
    label: 'Ask Scout, the last answer failed',
    title: 'Scout could not answer. Open to see why.',
    ring: false,
    badge: true,
    recording: false,
  };
  const SHOWN: Readonly<Record<State, typeof IDLE>> = {
    'no conversation': IDLE,
    answering: ANSWERING,
    'last answer done': IDLE,
    'last answer failed': FAILED,
    'last answer failed with a partial reply': FAILED,
    'stopped by the owner': IDLE,
    'stopped by the owner after a partial reply': IDLE,
    'worker not running': FAILED,
  };

  function shown(fixture: ComponentFixture<FloatingButtonComponent>) {
    const found = button(fixture)!;
    return {
      label: found.getAttribute('aria-label'),
      title: found.getAttribute('title'),
      ring: fixture.nativeElement.querySelector('[data-test="floating-ring"]') !== null,
      badge: fixture.nativeElement.querySelector('[data-test="floating-badge"]') !== null,
      recording: recording(fixture),
    };
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const state of Object.keys(SHOWN) as State[]) {
    it(`${state}, as main pushes it: ${SHOWN[state].label}`, () => {
      const { fixture, bridge } = render(vi.fn(async () => undefined));

      bridge.push(TURNS[state]);
      fixture.detectChanges();

      expect(shown(fixture)).toEqual(SHOWN[state]);
    });

    it(`${state}, as main answers on open: ${SHOWN[state].label}, and a click still opens quick ask`, async () => {
      const localOpen = vi.fn(async () => undefined);
      const { fixture } = render(localOpen, TURNS[state]);
      await fixture.whenStable();
      fixture.detectChanges();

      button(fixture)!.click();

      expect(shown(fixture)).toEqual(SHOWN[state]);
      expect(localOpen).toHaveBeenCalledTimes(1);
    });
  }

  it('the badge and the ring sit outside the button, so the button keeps no text', () => {
    const { fixture, bridge } = render(vi.fn(async () => undefined));

    bridge.push(TURNS['last answer failed']);
    fixture.detectChanges();
    const failed = button(fixture)!.textContent?.trim();
    bridge.push(TURNS.answering);
    fixture.detectChanges();

    expect(failed).toBe('');
    expect(button(fixture)!.textContent?.trim()).toBe('');
    expect(
      button(fixture)!.contains(fixture.nativeElement.querySelector('[data-test="floating-ring"]')),
    ).toBe(false);
  });

  it('the answering ring stops moving when the owner asks for reduced motion', () => {
    const { fixture, bridge } = render(vi.fn(async () => undefined));
    bridge.push(TURNS.answering);
    fixture.detectChanges();
    const css = Array.from(document.querySelectorAll('style'))
      .map((style) => style.textContent ?? '')
      .join('\n');

    expect(css).toMatch(
      /@media \(prefers-reduced-motion: reduce\)\s*\{[^}]*\.ring[^{]*\{[^}]*animation:\s*none/,
    );
  });
});

describe('FloatingButtonComponent: listening, as main pushes it or answers on open', () => {
  const running: LocalTurn = {
    id: 'q-2',
    question: 'and staging?',
    reply: '',
    citations: [],
    state: 'running',
  };
  const failedTurn: LocalTurn = {
    id: 'q-1',
    question: 'is prod green?',
    reply: 'The AI Source did not answer.',
    citations: [],
    state: 'failed',
  };
  const conversation = (answering: boolean, failed: boolean): readonly LocalTurn[] => [
    ...(failed ? [failedTurn] : []),
    ...(answering ? [running] : []),
  ];

  const LISTENING = {
    label: 'Ask Scout, listening',
    title: 'Scout is listening. Open to see the transcript.',
    ring: false,
    badge: false,
    recording: true,
  };
  const IDLE = {
    label: 'Ask Scout',
    title: 'Ask Scout',
    ring: false,
    badge: false,
    recording: false,
  };
  const ANSWERING = {
    label: 'Ask Scout, answering',
    title: 'Scout is answering. Open to follow the answer.',
    ring: true,
    badge: false,
    recording: false,
  };
  const FAILED = {
    label: 'Ask Scout, the last answer failed',
    title: 'Scout could not answer. Open to see why.',
    ring: false,
    badge: true,
    recording: false,
  };

  const ROWS: readonly (readonly [boolean, boolean, boolean, typeof IDLE])[] = [
    [false, false, false, IDLE],
    [false, false, true, FAILED],
    [false, true, false, ANSWERING],
    [false, true, true, ANSWERING],
    [true, false, false, LISTENING],
    [true, false, true, LISTENING],
    [true, true, false, LISTENING],
    [true, true, true, LISTENING],
  ];

  function shown(fixture: ComponentFixture<FloatingButtonComponent>) {
    const found = button(fixture)!;
    return {
      label: found.getAttribute('aria-label'),
      title: found.getAttribute('title'),
      ring: fixture.nativeElement.querySelector('[data-test="floating-ring"]') !== null,
      badge: fixture.nativeElement.querySelector('[data-test="floating-badge"]') !== null,
      recording: recording(fixture),
    };
  }

  const name = (listening: boolean, answering: boolean, failed: boolean) =>
    `${listening ? 'listening' : 'not listening'}, ${answering ? 'answering' : 'not answering'}, ${failed ? 'an earlier answer failed' : 'no failed answer'}`;

  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const [listening, answering, failed, expected] of ROWS) {
    it(`${name(listening, answering, failed)}, as main pushes it: ${expected.label}`, () => {
      const { fixture, bridge } = render(vi.fn(async () => undefined));

      bridge.push(conversation(answering, failed));
      bridge.pushListening(listening);
      fixture.detectChanges();

      expect(shown(fixture)).toEqual(expected);
    });

    it(`${name(listening, answering, failed)}, as main answers on open: ${expected.label}, and a click still opens quick ask`, async () => {
      const localOpen = vi.fn(async () => undefined);
      const { fixture } = render(localOpen, conversation(answering, failed), null, listening);
      await fixture.whenStable();
      fixture.detectChanges();

      button(fixture)!.click();

      expect(shown(fixture)).toEqual(expected);
      expect(localOpen).toHaveBeenCalledTimes(1);
    });

    it(`${name(listening, answering, failed)}, then the listen starts and stops: listening, then ${expected === LISTENING ? 'what the conversation shows' : expected.label}`, () => {
      const { fixture, bridge } = render(vi.fn(async () => undefined));
      bridge.push(conversation(answering, failed));

      bridge.pushListening(true);
      fixture.detectChanges();
      const during = shown(fixture);
      bridge.pushListening(false);
      fixture.detectChanges();

      expect(during).toEqual(LISTENING);
      expect(shown(fixture)).toEqual(
        ROWS.find(([l, a, f]) => !l && a === answering && f === failed)![3],
      );
    });
  }

  it('a push that arrives before the answer on open wins over that answer', async () => {
    const { fixture, bridge } = render(
      vi.fn(async () => undefined),
      [],
      null,
      true,
    );

    bridge.pushListening(false);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(shown(fixture)).toEqual(IDLE);
  });

  it('an answer on open that fails leaves the button not listening', async () => {
    const { fixture } = render(
      vi.fn(async () => undefined),
      [],
      null,
      null,
    );
    await fixture.whenStable();
    fixture.detectChanges();

    expect(shown(fixture)).toEqual(IDLE);
  });

  it('the recording ring and dot paint with the recording token, not a literal colour', () => {
    const { fixture, bridge } = render(vi.fn(async () => undefined));
    bridge.pushListening(true);
    fixture.detectChanges();

    const ring = fixture.nativeElement.querySelector('[data-test="floating-recording-ring"]');
    const dot = fixture.nativeElement.querySelector('[data-test="floating-recording-dot"]');
    expect(declared(ring, 'border-color')).toBe('var(--sc-recording)');
    expect(declared(dot, 'background')).toBe('var(--sc-recording)');
  });

  it('closing the floating button stops following the listening push', () => {
    const { fixture, bridge } = render(vi.fn(async () => undefined));

    fixture.destroy();

    expect(bridge.unsubscribed).toEqual(['listening']);
  });
});

describe('FloatingButtonComponent: the shortcut in the idle title', () => {
  const SHORTCUT = (registered: boolean): LocalShortcut => ({
    accelerator: 'CommandOrControl+Alt+Space',
    registered,
    hint: null,
  });

  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const [name, shortcut, title] of [
    ['a registered shortcut', SHORTCUT(true), 'Ask Scout (Ctrl+Alt+Space)'],
    ['a shortcut the system refused', SHORTCUT(false), 'Ask Scout'],
    ['a shortcut main cannot read', null, 'Ask Scout'],
  ] as const) {
    it(`with ${name}, the idle title reads "${title}" and the label stays "Ask Scout"`, async () => {
      const { fixture } = render(
        vi.fn(async () => undefined),
        [],
        shortcut,
      );
      await fixture.whenStable();
      fixture.detectChanges();

      expect(button(fixture)!.getAttribute('title')).toBe(title);
      expect(button(fixture)!.getAttribute('aria-label')).toBe('Ask Scout');
    });
  }
});

describe('FloatingButtonComponent: each state of the FloatingButton artboard', () => {
  type ArtboardState = 'Idle' | 'Listening' | 'Answering' | 'Error';

  const failedTurn: LocalTurn = {
    id: 'q-1',
    question: 'is prod green?',
    reply: 'The AI Source did not answer.',
    citations: [],
    state: 'failed',
  };
  const runningTurn: LocalTurn = { ...failedTurn, reply: '', state: 'running' };

  const ARTBOARD: Readonly<
    Record<
      ArtboardState,
      {
        label: string;
        title: string;
        ring: string | null;
        badge: { text: string; background: string | null; color: string | null } | null;
      }
    >
  > = {
    Idle: {
      label: 'Ask Scout',
      title: 'Ask Scout (Ctrl+Alt+Space)',
      ring: null,
      badge: null,
    },
    Listening: {
      label: 'Ask Scout, listening',
      title: 'Scout is listening. Open to see the transcript.',
      ring: 'var(--sc-recording)',
      badge: { text: '', background: 'var(--sc-recording)', color: 'var(--sc-on-danger)' },
    },
    Answering: {
      label: 'Ask Scout, answering',
      title: 'Scout is answering. Open to follow the answer.',
      ring: '3px solid var(--sc-accent)',
      badge: null,
    },
    Error: {
      label: 'Ask Scout, the last answer failed',
      title: 'Scout could not answer. Open to see why.',
      ring: null,
      badge: { text: '!', background: 'var(--sc-danger)', color: 'var(--sc-on-danger)' },
    },
  };

  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const state of Object.keys(ARTBOARD) as ArtboardState[]) {
    it(`${state}: the name, the title, the ring and the badge as the artboard draws them`, async () => {
      const { fixture, bridge } = render(
        vi.fn(async () => undefined),
        state === 'Error' ? [failedTurn] : state === 'Answering' ? [runningTurn] : [],
        { accelerator: 'CommandOrControl+Alt+Space', registered: true, hint: null },
        state === 'Listening',
      );
      await fixture.whenStable();
      fixture.detectChanges();
      if (state === 'Listening') bridge.pushListening(true);
      fixture.detectChanges();
      const found = button(fixture)!;
      const ring = fixture.nativeElement.querySelector('.ring') as HTMLElement | null;
      const badge = fixture.nativeElement.querySelector('.badge') as HTMLElement | null;

      expect({
        label: found.getAttribute('aria-label'),
        title: found.getAttribute('title'),
        ring: ring === null ? null : (declared(ring, 'border-color') ?? declared(ring, 'border')),
        badge:
          badge === null
            ? null
            : {
                text: badge.textContent?.trim(),
                background: declared(badge, 'background'),
                color: declared(badge, 'color'),
              },
      }).toEqual(ARTBOARD[state]);
    });
  }
});

describe('FloatingButtonComponent: the card beside the button', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    document.documentElement.style.background = '';
    document.body.style.background = '';
    document.body.style.margin = '';
    document.body.style.overflow = '';
  });

  const flush = async (fixture: ComponentFixture<FloatingButtonComponent>) => {
    await fixture.whenStable();
    fixture.detectChanges();
  };

  it.each([
    ['the pointer enters the button', 'pointerenter', true],
    ['the pointer leaves the button', 'pointerleave', false],
  ] as const)(
    'when %s (%s), main is asked for the card with shown %s',
    async (_case, event, shown) => {
      const { fixture, bridge } = render(vi.fn(async () => undefined));

      button(fixture)!.dispatchEvent(new Event(event));
      await flush(fixture);

      expect(bridge.localBubbleCard.mock.calls).toEqual([[{ shown }]]);
    },
  );

  it('the button window takes no keyboard focus, so focus, blur and Esc ask main for nothing', async () => {
    const { fixture, bridge } = render(vi.fn(async () => undefined));

    for (const event of [
      new Event('focus'),
      new Event('blur'),
      new KeyboardEvent('keydown', { key: 'Escape' }),
    ]) {
      button(fixture)!.dispatchEvent(event);
    }
    await flush(fixture);

    expect(bridge.localBubbleCard).not.toHaveBeenCalled();
  });

  it('while the card shows the native title is off, so no second tooltip covers it; when main cannot show the card the title stays', async () => {
    const shownCase = render(vi.fn(async () => undefined));
    button(shownCase.fixture)!.dispatchEvent(new Event('pointerenter'));
    await flush(shownCase.fixture);
    const whileShown = button(shownCase.fixture)!.getAttribute('title');
    button(shownCase.fixture)!.dispatchEvent(new Event('pointerleave'));
    await flush(shownCase.fixture);
    const afterLeave = button(shownCase.fixture)!.getAttribute('title');
    TestBed.resetTestingModule();
    const refused = render(vi.fn(async () => undefined));
    refused.bridge.localBubbleCard.mockResolvedValue({ shown: false });
    button(refused.fixture)!.dispatchEvent(new Event('pointerenter'));
    await flush(refused.fixture);

    expect({ whileShown, afterLeave }).toEqual({ whileShown: null, afterLeave: 'Ask Scout' });
    expect(button(refused.fixture)!.getAttribute('title')).toBe('Ask Scout');
  });

  it('a card answer that arrives after the pointer left does not mark the card shown', async () => {
    let answer!: (value: { shown: boolean }) => void;
    const { fixture, bridge } = render(vi.fn(async () => undefined));
    bridge.localBubbleCard.mockImplementationOnce(
      () => new Promise<{ shown: boolean }>((resolve) => (answer = resolve)),
    );
    button(fixture)!.dispatchEvent(new Event('pointerenter'));
    button(fixture)!.dispatchEvent(new Event('pointerleave'));
    await flush(fixture);

    answer({ shown: true });
    await flush(fixture);

    expect(button(fixture)!.getAttribute('title')).toBe('Ask Scout');
  });

  it.each([
    [
      'idle with the shortcut registered',
      [],
      true,
      false,
      'Idle. Click, or press Ctrl+Alt+Space, to open the quick ask window.',
    ],
    ['idle without a shortcut', [], false, false, 'Idle. Click to open the quick ask window.'],
    [
      'listening',
      [],
      true,
      true,
      'Listening. A Listen, System audio or Meeting is open; this shows ahead of answering and error, so an open microphone is never hidden. Open the quick ask window to see the transcript.',
    ],
    [
      'answering',
      [{ id: 'q-1', question: 'is prod green?', reply: '', citations: [], state: 'running' }],
      true,
      false,
      'Answering. Scout is working on your question. One question at a time.',
    ],
    [
      'failed',
      [{ id: 'q-1', question: 'is prod green?', reply: 'Boom.', citations: [], state: 'failed' }],
      true,
      false,
      'Error. The last answer failed, the worker not running included; a stop by you is not a failure. Open the quick ask window to read the reason.',
    ],
  ] as const)(
    '%s: the button keeps its name and its description is the card text',
    async (_case, turns, registered, listening, description) => {
      const { fixture } = render(
        vi.fn(async () => undefined),
        turns as readonly LocalTurn[],
        registered
          ? { accelerator: 'CommandOrControl+Alt+Space', registered: true, hint: null }
          : null,
        listening,
      );
      await flush(fixture);
      const found = button(fixture)!;
      const described = fixture.nativeElement.querySelector(
        `#${found.getAttribute('aria-describedby')}`,
      ) as HTMLElement | null;

      expect(found.getAttribute('aria-label')).toMatch(/^Ask Scout/);
      expect(described?.textContent?.replace(/\s+/g, ' ').trim()).toBe(description);
    },
  );
});
