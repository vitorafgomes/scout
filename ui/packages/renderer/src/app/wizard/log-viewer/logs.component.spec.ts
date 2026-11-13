import { readFileSync } from 'node:fs';

import { InteractivityChecker } from '@angular/cdk/a11y';
import { ApplicationRef, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { provideRouter } from '@angular/router';
import type { WorkerEvent, WorkerStatus } from '@scout/shared';

import { ICONS } from '../../core/icon.component';
import { LAID_OUT } from '../../core/laid-out.testing';
import { LogStore } from '../../core/log.store';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { LogsComponent } from './logs.component';

function makeLogStore(state: WorkerStatus['state'] = 'running') {
  const eventsSig = signal<readonly WorkerEvent[]>([]);
  return {
    events: eventsSig.asReadonly(),
    status: signal<WorkerStatus>({ state, restarts: 0 }),
    _set: (events: readonly WorkerEvent[]) => eventsSig.set(events),
    clear: () => {},
    copy: async () => 0,
  };
}

function makeBridge(
  start: () => Promise<unknown> = async () => ({ ok: true }),
  status: () => Promise<WorkerStatus> = async () => ({ state: 'idle', restarts: 0 }),
) {
  return {
    workerStart: vi.fn(start),
    workerStatus: vi.fn(status),
    noticeRecord: vi.fn(async () => undefined),
  };
}

function stderr(line: string): WorkerEvent {
  return { kind: 'stderr', line, ts: '2026-05-29T16:00:00.000Z' };
}

async function setup(events: readonly WorkerEvent[]) {
  const store = makeLogStore();
  store._set(events);

  TestBed.configureTestingModule({
    imports: [LogsComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: LogStore, useValue: store },
      { provide: ScoutBridgeService, useValue: makeBridge() },
    ],
  });

  const fixture = TestBed.createComponent(LogsComponent);
  fixture.detectChanges();
  return fixture.componentInstance;
}

describe('LogsComponent level classification', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('classifies info-prefixed stderr lines as info, not error', async () => {
    // The .NET worker host routes every level to stderr by design, so the
    // level must be read from the line content, not from the stream.
    const component = await setup([stderr('info: Microsoft.Hosting.Lifetime[0]')]);

    const levels = component.visibleLines().map((l) => l.level);
    expect(levels).toEqual(['info']);
  });

  it('still classifies fail/crit stderr lines as error', async () => {
    const component = await setup([
      stderr('fail: Scout.Host.Console.ConversationWorker[0]'),
      stderr('crit: Scout.Host.Console.ConversationWorker[0]'),
    ]);

    const levels = component.visibleLines().map((l) => l.level);
    expect(levels).toEqual(['error', 'error']);
  });

  it('classifies warn and debug stderr lines by content', async () => {
    const component = await setup([stderr('warn: some warning'), stderr('dbug: trace details')]);

    const levels = component.visibleLines().map((l) => l.level);
    expect(levels).toEqual(['warn', 'debug']);
  });

  it('keeps the whole stack of a fail block visible when only errors are shown', async () => {
    const block = [
      'fail: Scout.Host.Console.ConversationWorker[0]',
      '      Message handling failed',
      '      System.InvalidOperationException: provider rejected the request',
      '         at Scout.Llm.ChatGateway.StreamAsync()',
      '      --- End of stack trace from previous location ---',
      '         at Scout.Host.Console.ConversationWorker.HandleAsync()',
    ];
    const component = await setup([
      ...block.map(stderr),
      stderr('info: Microsoft.Hosting.Lifetime[0]'),
      stderr('      Application started.'),
    ]);

    component.enabledLevels.set({ info: false, warn: false, error: true, debug: false });

    expect(component.visibleLines().map((l) => l.text)).toEqual(block);
  });
});

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

function notices(): { text: string; action: string | null; classes: string[] }[] {
  return Array.from(
    document.querySelectorAll<HTMLElement>('.cdk-overlay-container .sc-notice'),
  ).map((el) => {
    const label = el.querySelector('.mat-mdc-snack-bar-label [aria-hidden="true"]') ?? el;
    const copy = label.cloneNode(true) as HTMLElement;
    copy.querySelectorAll('button').forEach((button) => button.remove());
    return {
      text: (copy.textContent ?? '').replace(/\s+/g, ' ').trim(),
      action: el.querySelector('button')?.textContent?.trim() ?? null,
      classes: Array.from(el.classList).filter((c) => c.startsWith('sc-notice--')),
    };
  });
}

async function render(
  events: readonly WorkerEvent[],
  copy: () => Promise<number> = async () => events.length,
  options: {
    readonly state?: WorkerStatus['state'];
    readonly start?: () => Promise<unknown>;
    readonly mainStatus?: () => Promise<WorkerStatus>;
  } = {},
): Promise<{
  fixture: ComponentFixture<LogsComponent>;
  store: ReturnType<typeof makeLogStore>;
  bridge: ReturnType<typeof makeBridge>;
}> {
  const store = { ...makeLogStore(options.state), copy: vi.fn(copy), clear: vi.fn() };
  const bridge = makeBridge(options.start, options.mainStatus);
  store._set(events);
  TestBed.configureTestingModule({
    imports: [LogsComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: LogStore, useValue: store },
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
    ],
  });
  const fixture = TestBed.createComponent(LogsComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, store, bridge };
}

async function answerConfirm(
  fixture: ComponentFixture<LogsComponent>,
  answer: 'confirm-ok' | 'confirm-cancel',
): Promise<void> {
  const button = document.querySelector<HTMLButtonElement>(`[data-test="${answer}"]`);
  if (button === null) throw new Error('the confirmation dialog did not open');
  button.click();
  for (let i = 0; i < 3; i += 1) {
    await fixture.whenStable();
    await TestBed.inject(ApplicationRef).whenStable();
    fixture.detectChanges();
  }
}

function texts(fixture: ComponentFixture<LogsComponent>, selector: string): string[] {
  return Array.from(
    (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>(selector),
    (node) => (node.textContent ?? '').replace(/\s+/g, ' ').trim(),
  );
}

async function click(fixture: ComponentFixture<LogsComponent>, test: string): Promise<void> {
  (fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLButtonElement).click();
  await fixture.whenStable();
  await TestBed.inject(ApplicationRef).whenStable();
  fixture.detectChanges();
}

describe('LogsComponent page', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('fills the shell content area with one scroll: the page is 100% tall, never a viewport height, and only the viewer scrolls', async () => {
    const { fixture } = await render([stderr('info: started')]);
    const host = fixture.nativeElement as HTMLElement;
    const page = host.querySelector('.logs')!;
    const viewer = host.querySelector('.viewer')!;

    expect(declared(host, 'height')).toBe('100%');
    expect(declared(page, 'height')).toBe('100%');
    expect(declared(page, 'overflow')).toBe('hidden');
    expect(declared(viewer, 'overflow-y')).toBe('auto');
    expect(declared(viewer, 'min-height')).toBe('0');
    expect(
      [host, page, viewer].map((node) => declared(node, 'height') ?? '').join(' '),
    ).not.toMatch(/vh/);
  });

  it('Copy all tells the owner how many lines it copied', async () => {
    const { fixture, store } = await render(
      [stderr('info: one'), stderr('info: two')],
      async () => 2,
    );

    await click(fixture, 'copy');

    expect(store.copy).toHaveBeenCalledOnce();
    expect(notices()).toEqual([
      { text: 'Copied 2 lines', action: 'OK', classes: ['sc-notice--success'] },
    ]);
  });

  it('as in the prototype: Copy all says Nothing to copy when there is no line', async () => {
    const { fixture, store } = await render([], async () => 0);

    await click(fixture, 'copy');

    expect(notices()).toEqual([
      { text: 'Nothing to copy', action: 'OK', classes: ['sc-notice--info'] },
    ]);
    expect(store.copy).not.toHaveBeenCalled();
  });

  it('as in the prototype: the log output is not read aloud line by line, only the error count is', async () => {
    const { fixture } = await render([stderr('info: one'), stderr('error: two')], async () => 2);
    const root = fixture.nativeElement as HTMLElement;

    expect([
      root.querySelector('[role="log"]')?.getAttribute('aria-live'),
      root.querySelector('[data-test="error-count"]')?.getAttribute('aria-live'),
    ]).toEqual(['off', 'polite']);
  });

  it('Copy all says it could not copy when the clipboard refuses', async () => {
    const { fixture } = await render([stderr('info: one')], async () => {
      throw new Error('Write permission denied');
    });

    await click(fixture, 'copy');

    expect(notices()).toEqual([
      {
        text: 'Could not copy the logs: Write permission denied',
        action: 'OK',
        classes: ['sc-notice--error'],
      },
    ]);
  });

  it('Clear asks first and Cancel, which has the focus, keeps every line', async () => {
    const { fixture, store } = await render([stderr('info: one'), stderr('info: two')]);

    await click(fixture, 'clear');
    await fixture.whenStable();
    fixture.detectChanges();
    const title = document.querySelector('[role="alertdialog"] h2')?.textContent?.trim();
    const body = document.querySelector('[role="alertdialog"] p')?.textContent?.trim();
    const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
    await answerConfirm(fixture, 'confirm-cancel');

    expect({ title, body, focused }).toEqual({
      title: 'Clear the log?',
      body: 'Removes the 2 lines from this window. The worker keeps running and new lines keep arriving. It cannot be undone.',
      focused: 'confirm-cancel',
    });
    expect(store.clear).not.toHaveBeenCalled();
    expect(notices()).toEqual([]);
  });

  it('Clear confirmed empties the view and says so', async () => {
    const { fixture, store } = await render([stderr('info: one')]);

    await click(fixture, 'clear');
    await answerConfirm(fixture, 'confirm-ok');

    expect(store.clear).toHaveBeenCalledOnce();
    expect(notices()).toEqual([
      { text: 'Log cleared', action: 'OK', classes: ['sc-notice--info'] },
    ]);
  });

  describe('the empty states the prototype draws', () => {
    const cases = [
      { lines: false, state: 'running', filtersOff: false, shown: 'empty-none' },
      { lines: false, state: 'starting', filtersOff: true, shown: 'empty-none' },
      { lines: false, state: 'stopped', filtersOff: false, shown: 'empty-stopped' },
      { lines: false, state: 'crashed', filtersOff: false, shown: 'empty-stopped' },
      { lines: false, state: 'idle', filtersOff: true, shown: 'empty-stopped' },
      { lines: true, state: 'running', filtersOff: true, shown: 'empty-filtered' },
      { lines: true, state: 'stopped', filtersOff: true, shown: 'empty-filtered' },
      { lines: true, state: 'running', filtersOff: false, shown: null },
      { lines: true, state: 'stopped', filtersOff: false, shown: null },
    ] as const;

    const TEXT = {
      'empty-none':
        'Scout is running and has not written anything yet. New lines appear here as it works.',
      'empty-stopped': 'Scout is not running, so it writes nothing. Start it to see its log here.',
      'empty-filtered': 'No line matches the search and the levels you chose. 2 lines are hidden.',
    } as const;

    for (const { lines, state, filtersOff, shown } of cases) {
      it(`${lines ? 'with log lines' : 'with no logs yet'}, the worker ${state}${filtersOff ? ' and every level filtered out' : ''}: ${shown ?? 'the lines'} shown`, async () => {
        const { fixture } = await render(
          lines ? [stderr('info: one'), stderr('warn: two')] : [],
          undefined,
          {
            state,
          },
        );
        if (filtersOff) {
          fixture.componentInstance.enabledLevels.set({
            info: false,
            warn: false,
            error: false,
            debug: false,
          });
          fixture.detectChanges();
        }
        const host = fixture.nativeElement as HTMLElement;
        const text = (test: string) =>
          host.querySelector(`[data-test="${test}"]`)?.textContent?.replace(/\s+/g, ' ').trim() ??
          null;

        for (const test of ['empty-none', 'empty-stopped', 'empty-filtered'] as const)
          expect(text(test), test).toBe(shown === test ? TEXT[test] : null);
        expect(host.querySelectorAll('.line').length).toBe(shown === null ? 2 : 0);
      });
    }
  });

  it('Show every line clears the search and turns every level back on', async () => {
    const { fixture } = await render([stderr('info: one'), stderr('warn: two')]);
    fixture.componentInstance.enabledLevels.set({
      info: false,
      warn: true,
      error: true,
      debug: true,
    });
    fixture.componentInstance.query.set('nothing like this');
    fixture.detectChanges();

    await click(fixture, 'show-every-line');

    expect(texts(fixture, '.line .msg')).toEqual(['info: one', 'warn: two']);
  });

  it('Start Scout on the stopped state asks main to start the worker', async () => {
    const { fixture, bridge } = await render([], undefined, { state: 'stopped' });

    await click(fixture, 'start-worker');

    expect(bridge.workerStart).toHaveBeenCalledOnce();
    expect(notices()).toEqual([]);
  });

  it('Start Scout says why when main refuses to start the worker', async () => {
    const { fixture } = await render([], undefined, {
      state: 'crashed',
      start: async () => {
        throw new Error('[scout:worker/start-failed] The worker binary is missing.');
      },
    });

    await click(fixture, 'start-worker');

    expect(notices()).toEqual([
      {
        text: 'Could not start Scout: The worker binary is missing.',
        action: 'OK',
        classes: ['sc-notice--error'],
      },
    ]);
  });

  it('the running-but-silent state links to Settings', async () => {
    const { fixture } = await render([], undefined, { state: 'running' });

    const link = fixture.nativeElement.querySelector(
      '[data-test="open-settings"]',
    ) as HTMLAnchorElement;

    expect(link.getAttribute('href')).toBe('/settings');
  });
});

describe('Logs follows the theme and the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('its styles paint only with the theme tokens, no hex, rgb() or hsl() colour left', () => {
    const source = readFileSync('src/app/wizard/log-viewer/logs.component.ts', 'utf8');
    const start = source.indexOf('  styles:');
    const css = source.slice(start, source.indexOf('\n})\n', start));

    expect(start).toBeGreaterThanOrEqual(0);
    expect(css).toContain('var(--sc-');
    expect(
      Array.from(
        css.matchAll(
          /#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(|(?<![\w-])(?:white|black)(?![\w-])/g,
        ),
        (match) => match[0],
      ),
    ).toEqual([]);
  });

  it('opens with the lede, and Pause, Copy all and Clear each carry an icon and their text', async () => {
    const { fixture } = await render([stderr('info: one')]);
    const host = fixture.nativeElement as HTMLElement;

    expect(
      host.querySelector('[data-test="logs-lede"]')?.textContent?.replace(/\s+/g, ' ').trim(),
    ).toBe(
      'What the Scout worker writes while it runs. Lines stay in this window until you clear them.',
    );
    expect(
      ['toggle-pause', 'copy', 'clear'].map((test) => {
        const button = host.querySelector(`[data-test="${test}"]`)!;
        return [button.querySelector('sc-icon') !== null, button.textContent?.trim()];
      }),
    ).toEqual([
      [true, 'Pause'],
      [true, 'Copy all'],
      [true, 'Clear'],
    ]);
  });

  it('Pause becomes Resume, pressed, and the line under the levels says the new lines are kept', async () => {
    const { fixture } = await render([stderr('info: one')]);

    const before = texts(fixture, '[data-test="follow-line"]');
    await click(fixture, 'toggle-pause');
    const pause = fixture.nativeElement.querySelector('[data-test="toggle-pause"]') as HTMLElement;

    expect(before).toEqual(['Following new lines']);
    expect([pause.textContent?.trim(), pause.getAttribute('aria-pressed')]).toEqual([
      'Resume',
      'true',
    ]);
    expect(texts(fixture, '[data-test="follow-line"]')).toEqual([
      'Paused: new lines are kept and shown when you resume',
    ]);
  });

  for (const [state, chip, follow] of [
    ['running', 'Worker running', 'Following new lines'],
    ['starting', 'Worker starting', 'Following new lines'],
    ['idle', 'Worker stopped', 'No new lines while the worker is stopped'],
    ['stopped', 'Worker stopped', 'No new lines while the worker is stopped'],
    ['crashed', 'Worker stopped', 'No new lines while the worker is stopped'],
  ] as const) {
    it(`with the worker ${state} the chip reads "${chip}" and the line "${follow}"`, async () => {
      const { fixture } = await render([stderr('info: one')], undefined, { state });

      expect([
        ...texts(fixture, '[data-test="worker-chip"]'),
        ...texts(fixture, '[data-test="follow-line"]'),
      ]).toEqual([chip, follow]);
    });
  }

  const MIXED = [
    stderr('info: started'),
    stderr('warn: slow reply'),
    stderr('fail: provider timeout'),
    stderr('dbug: trace'),
    stderr('info: Timeout raised to 30 s'),
  ];

  it('each level is a pressed switch with its icon, its name and how many lines it holds', async () => {
    const { fixture } = await render(MIXED);
    const host = fixture.nativeElement as HTMLElement;

    expect(
      Array.from(
        host.querySelectorAll<HTMLElement>('[role="group"][aria-label="Show levels"] button'),
        (b) => [
          b.textContent?.replace(/\s+/g, ' ').trim(),
          b.getAttribute('aria-pressed'),
          b.querySelector('svg path') !== null,
        ],
      ),
    ).toEqual([
      ['Info 2', 'true', true],
      ['Warning 1', 'true', true],
      ['Error 1', 'true', true],
      ['Debug 1', 'true', true],
    ]);
  });

  for (const [level, left] of [
    ['info', ['warn: slow reply', 'fail: provider timeout', 'dbug: trace']],
    [
      'warn',
      ['info: started', 'fail: provider timeout', 'dbug: trace', 'info: Timeout raised to 30 s'],
    ],
    ['error', ['info: started', 'warn: slow reply', 'dbug: trace', 'info: Timeout raised to 30 s']],
    [
      'debug',
      [
        'info: started',
        'warn: slow reply',
        'fail: provider timeout',
        'info: Timeout raised to 30 s',
      ],
    ],
  ] as const) {
    it(`clicking ${level} hides its lines, unpresses it and keeps its count`, async () => {
      const { fixture } = await render(MIXED);

      await click(fixture, `filter-${level}`);
      const button = fixture.nativeElement.querySelector(
        `[data-test="filter-${level}"]`,
      ) as HTMLElement;

      expect(texts(fixture, '.line .msg')).toEqual(left);
      expect(button.getAttribute('aria-pressed')).toBe('false');
      expect(texts(fixture, `[data-test="count-${level}"]`)).toEqual([
        String(level === 'info' ? 2 : 1),
      ]);
    });
  }

  it('the search keeps only the lines that contain it, in any case', async () => {
    const { fixture } = await render(MIXED);
    const input = fixture.nativeElement.querySelector('[data-test="search"]') as HTMLInputElement;

    input.value = 'TIMEOUT';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(texts(fixture, '.line .msg')).toEqual([
      'fail: provider timeout',
      'info: Timeout raised to 30 s',
    ]);
  });

  it('error lines sit on the error container and the session says how many errors it saw', async () => {
    const { fixture } = await render(MIXED);
    const error = fixture.nativeElement.querySelector('.line[data-level="error"]') as HTMLElement;
    const info = fixture.nativeElement.querySelector('.line[data-level="info"]') as HTMLElement;

    expect(declared(error, 'background')).toBe('var(--sc-error-container)');
    expect(declared(info, 'background')).toBeNull();
    expect(texts(fixture, '[data-test="error-count"]')).toEqual(['1 error in this session']);
  });
});

describe('Logs reads the worker state from main when it opens', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const [store, main, chip, shown] of [
    ['idle', 'running', 'Worker running', 'empty-none'],
    ['idle', 'stopped', 'Worker stopped', 'empty-stopped'],
    ['running', 'stopped', 'Worker running', 'empty-none'],
  ] as const) {
    it(`a store that says ${store} and main that says ${main}: the chip reads "${chip}" and ${shown} shows`, async () => {
      const { fixture } = await render([], undefined, {
        state: store,
        mainStatus: async () => ({ state: main, restarts: 0 }),
      });
      await fixture.whenStable();
      fixture.detectChanges();

      expect(texts(fixture, '[data-test="worker-chip"]')).toEqual([chip]);
      expect(fixture.nativeElement.querySelector(`[data-test="${shown}"]`)).not.toBeNull();
    });
  }

  it('when main does not answer, the store state stays and the screen still opens', async () => {
    const { fixture } = await render([], undefined, {
      state: 'idle',
      mainStatus: async () => {
        throw new Error('[scout:worker/status-failed] no answer');
      },
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(texts(fixture, '[data-test="worker-chip"]')).toEqual(['Worker stopped']);
    expect(fixture.nativeElement.querySelector('[data-test="empty-stopped"]')).not.toBeNull();
  });
});

describe('Logs while lines keep arriving', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  const numbered = (count: number, from = 0) =>
    Array.from({ length: count }, (_, i) => stderr(`info: line-${from + i}`));

  function laidOut(viewer: HTMLElement): { readonly scrolledTo: () => number } {
    let top = 0;
    const height = () => viewer.querySelectorAll('.line').length * 20;
    Object.defineProperty(viewer, 'scrollHeight', { configurable: true, get: height });
    Object.defineProperty(viewer, 'clientHeight', { configurable: true, get: () => 300 });
    Object.defineProperty(viewer, 'scrollTop', {
      configurable: true,
      get: () => top,
      set: (value: number) => {
        top = Math.max(0, Math.min(value, height() - 300));
      },
    });
    return { scrolledTo: () => top };
  }

  async function arrive(
    fixture: ComponentFixture<LogsComponent>,
    store: ReturnType<typeof makeLogStore>,
    events: readonly WorkerEvent[],
  ): Promise<void> {
    store._set(events);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function viewerOf(fixture: ComponentFixture<LogsComponent>): HTMLElement {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('.viewer')!;
  }

  it('following new lines keeps the newest line in view when one more arrives', async () => {
    const { fixture, store } = await render(numbered(30));
    const viewer = viewerOf(fixture);
    laidOut(viewer);
    viewer.scrollTop = viewer.scrollHeight;

    await arrive(fixture, store, numbered(31));

    expect(viewer.scrollTop + viewer.clientHeight).toBe(viewer.scrollHeight);
  });

  it('once the owner scrolls up, a new line leaves the view where the owner put it', async () => {
    const { fixture, store } = await render(numbered(30));
    const viewer = viewerOf(fixture);
    const { scrolledTo } = laidOut(viewer);
    viewer.scrollTop = 100;
    viewer.dispatchEvent(new Event('scroll'));

    await arrive(fixture, store, numbered(31));

    expect(scrolledTo()).toBe(100);
  });

  it('while paused, a new line arriving does not move the view', async () => {
    const { fixture, store } = await render(numbered(30));
    const viewer = viewerOf(fixture);
    const { scrolledTo } = laidOut(viewer);
    viewer.scrollTop = viewer.scrollHeight;
    await click(fixture, 'toggle-pause');
    viewer.scrollTop = 120;

    await arrive(fixture, store, numbered(31));

    expect(scrolledTo()).toBe(120);
  });

  it('when the oldest line drops off, the row of a line that stays keeps showing that line', async () => {
    const first = numbered(20);
    const { fixture, store } = await render(first);
    const row = Array.from(viewerOf(fixture).querySelectorAll<HTMLElement>('.line')).find(
      (node) => node.querySelector('.msg')?.textContent === 'info: line-5',
    )!;

    await arrive(fixture, store, [...first.slice(1), stderr('info: line-20')]);

    expect({ connected: row.isConnected, text: row.querySelector('.msg')?.textContent }).toEqual({
      connected: true,
      text: 'info: line-5',
    });
  });

  async function renderWithTheRealStore() {
    let send: (event: WorkerEvent) => void = () => undefined;
    const writeText = vi.fn(async (_text: string) => undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    TestBed.configureTestingModule({
      imports: [LogsComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        {
          provide: ScoutBridgeService,
          useValue: {
            ...makeBridge(),
            workerLogSnapshot: async () => [],
            onWorkerEvent: (listener: (event: WorkerEvent) => void) => {
              send = listener;
              return () => undefined;
            },
          },
        },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: InteractivityChecker, useValue: LAID_OUT },
      ],
    });
    const fixture = TestBed.createComponent(LogsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    const emit = (line: string) => {
      send(stderr(line));
      fixture.detectChanges();
    };
    const copied = () => (writeText.mock.calls.at(-1)?.[0] ?? '').split('\n');
    return { fixture, emit, copied, send: (event: WorkerEvent) => send(event) };
  }

  it('the screen holds at most the 2,000 newest lines: one more drops the oldest, and every line held is a row', async () => {
    const { fixture, send } = await renderWithTheRealStore();
    for (let i = 0; i <= 2000; i += 1) send(stderr(`info: line-${i}`));
    fixture.detectChanges();

    const rows = texts(fixture, '.line .msg');

    expect({ count: rows.length, first: rows[0], last: rows.at(-1) }).toEqual({
      count: 2000,
      first: 'info: line-1',
      last: 'info: line-2000',
    });
  });

  it('Copy all while paused copies only the lines on screen, not the ones that arrived after the pause', async () => {
    const { fixture, emit, copied } = await renderWithTheRealStore();
    emit('info: seen');

    await click(fixture, 'toggle-pause');
    emit('info: arrived-after-pause-1');
    emit('info: arrived-after-pause-2');
    await click(fixture, 'copy');

    expect(copied()).toEqual(['[2026-05-29T16:00:00.000Z] [INFO] info: seen']);
  });

  it('Copy all copies only the lines the level switches and the search show, and says how many of the lines held', async () => {
    const { fixture, emit, copied } = await renderWithTheRealStore();
    emit('info: started');
    emit('fail: Scout.Host.Console[0]');
    emit('      the provider refused');
    emit('warn: slow answer');

    await click(fixture, 'filter-info');
    await click(fixture, 'filter-warn');
    await click(fixture, 'copy');

    expect(copied()).toEqual([
      '[2026-05-29T16:00:00.000Z] [ERROR] fail: Scout.Host.Console[0]',
      '[2026-05-29T16:00:00.000Z] [ERROR]       the provider refused',
    ]);
    expect(notices()).toEqual([
      { text: 'Copied 2 of 4 lines', action: 'OK', classes: ['sc-notice--success'] },
    ]);
  });

  it('Copy all with every line shown says how many it copied, without "of"', async () => {
    const { fixture, emit } = await renderWithTheRealStore();
    emit('info: one');
    emit('info: two');

    await click(fixture, 'copy');

    expect(notices()).toEqual([
      { text: 'Copied 2 lines', action: 'OK', classes: ['sc-notice--success'] },
    ]);
  });
});

describe('Logs as Logs.dc.html draws it, state by state', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  const LEVEL_GLYPHS = {
    info: 'M12 8v.5M12 11v5',
    warn: 'M12 3l9 16H3zM12 10v4',
    error: 'M6 6l12 12M18 6L6 18',
    debug: 'M8 6h8M6 10h12M8 14h8M10 18h4',
  } as const;

  function glyph(node: Element | null): {
    d: string | null;
    stroke: string | null;
    size: string | null;
  } {
    const svg = node?.querySelector('svg') ?? null;
    return {
      d: svg?.querySelector('path')?.getAttribute('d') ?? null,
      stroke: svg?.getAttribute('stroke-width') ?? null,
      size: svg?.getAttribute('width') ?? null,
    };
  }

  it('the page sets its text at 14 px and is at most 1200 px wide, as the artboard main is', async () => {
    const { fixture } = await render([stderr('info: one')]);
    const page = (fixture.nativeElement as HTMLElement).querySelector('.logs')!;

    expect({
      size: declared(page, 'font-size'),
      maxWidth: declared(page, 'max-width'),
      width: declared(page, 'width'),
    }).toEqual({ size: '14px', maxWidth: '1200px', width: '100%' });
  });

  for (const [token, value] of [
    ['--mat-button-outlined-container-height', '36px'],
    ['--mat-button-outlined-horizontal-padding', '14px'],
    ['--mat-button-outlined-label-text-weight', '400'],
    ['--mat-button-outlined-label-text-size', '14px'],
    ['--mat-button-outlined-label-text-tracking', '0'],
    ['--mat-button-filled-container-height', '36px'],
    ['--mat-button-filled-horizontal-padding', '14px'],
    ['--mat-button-filled-label-text-weight', '400'],
    ['--mat-button-filled-label-text-size', '14px'],
    ['--mat-button-filled-label-text-tracking', '0'],
  ] as const) {
    it(`Pause, Copy all, Clear, Show every line, Open Settings and Start Scout are the artboard's 36 px pills: ${token} is ${value} on the page`, async () => {
      const { fixture } = await render([stderr('info: one')]);
      const page = (fixture.nativeElement as HTMLElement).querySelector('.logs')!;

      expect(declared(page, token)).toBe(value);
    });
  }

  for (const [test, state] of [
    ['toggle-pause', 'running'],
    ['copy', 'running'],
    ['clear', 'running'],
    ['start-worker', 'stopped'],
    ['open-settings', 'running'],
  ] as const) {
    it(`${test}: its icon sits 8 px before its label, as the artboard's buttons gap them`, async () => {
      const { fixture } = await render(
        test === 'start-worker' || test === 'open-settings' ? [] : [stderr('info: one')],
        undefined,
        { state },
      );
      const icon = (fixture.nativeElement as HTMLElement).querySelector(
        `[data-test="${test}"] sc-icon`,
      )!;

      expect(declared(icon, 'margin-right')).toBe('8px');
    });
  }

  for (const level of ['info', 'warn', 'error', 'debug'] as const) {
    it(`the ${level} switch carries the artboard's ${level} glyph, 14 px at a 2.4 stroke`, async () => {
      const { fixture } = await render([stderr('info: one')]);

      expect(
        glyph(
          (fixture.nativeElement as HTMLElement).querySelector(`[data-test="filter-${level}"]`),
        ),
      ).toEqual({ d: LEVEL_GLYPHS[level], stroke: '2.4', size: '14' });
    });
  }

  for (const [state, d] of [
    ['running', 'M20 6L9 17l-5-5'],
    ['starting', 'M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7'],
    ['stopped', 'M6 6h12v12H6z'],
    ['crashed', 'M6 6h12v12H6z'],
    ['idle', 'M6 6h12v12H6z'],
  ] as const) {
    it(`with the worker ${state} the chip's glyph is the artboard's, 13 px at a 2.4 stroke`, async () => {
      const { fixture } = await render([stderr('info: one')], undefined, { state });

      expect(
        glyph((fixture.nativeElement as HTMLElement).querySelector('[data-test="worker-chip"]')),
      ).toEqual({ d, stroke: '2.4', size: '13' });
    });
  }

  for (const [shown, lines, state, d] of [
    ['empty-filtered', true, 'running', 'M3 5h18l-7 8v6l-4-2v-4z'],
    ['empty-none', false, 'running', 'M4 6h16M4 12h16M4 18h10'],
    ['empty-stopped', false, 'stopped', 'M6 6h12v12H6z'],
  ] as const) {
    it(`${shown}: the round icon above the text is the artboard's glyph, 24 px at a 2 stroke`, async () => {
      const { fixture } = await render(lines ? [stderr('info: one')] : [], undefined, { state });
      if (lines) {
        fixture.componentInstance.query.set('no line has this');
        fixture.detectChanges();
      }
      const host = fixture.nativeElement as HTMLElement;

      expect({
        shown: host.querySelector(`[data-test="${shown}"]`) !== null,
        icon: glyph(host.querySelector('.empty-icon')),
      }).toEqual({ shown: true, icon: { d, stroke: '2', size: '24' } });
    });
  }

  it("a line shows the time of day it was written, 14:02:11.384, and keeps the whole date as the time's machine value", async () => {
    const written = new Date(2026, 10, 10, 14, 2, 11, 384).toISOString();
    const { fixture } = await render([{ kind: 'stderr', line: 'info: started', ts: written }]);
    const row = (fixture.nativeElement as HTMLElement).querySelector('.line')!;
    const time = row.querySelector('.ts')!;

    expect({
      tag: time.tagName.toLowerCase(),
      text: time.textContent,
      datetime: time.getAttribute('datetime'),
      level: row.querySelector('.lvl')?.textContent,
    }).toEqual({ tag: 'time', text: '14:02:11.384', datetime: written, level: 'INFO' });
  });

  it('a line whose timestamp cannot be read shows that timestamp as written, in the row, its title and its description', async () => {
    const { fixture } = await render([{ kind: 'stderr', line: 'info: one', ts: 'not a time' }]);
    const time = (fixture.nativeElement as HTMLElement).querySelector('.line .ts')!;

    expect({
      text: time.textContent,
      title: time.getAttribute('title'),
      description: time.getAttribute('aria-description'),
    }).toEqual({ text: 'not a time', title: 'not a time', description: 'not a time' });
  });

  it('two lines either side of midnight each carry their whole local date and time as the time title and description, as text', async () => {
    const before = new Date(2026, 10, 10, 23, 59, 59, 900).toISOString();
    const after = new Date(2026, 10, 11, 0, 0, 0, 100).toISOString();
    const { fixture } = await render([
      { kind: 'stderr', line: 'info: before', ts: before },
      { kind: 'stderr', line: 'info: after', ts: after },
    ]);

    expect(
      Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.line .ts'), (time) => [
        time.textContent,
        time.getAttribute('title'),
        time.getAttribute('aria-description'),
      ]),
    ).toEqual([
      ['23:59:59.900', '2026-11-10 23:59:59.900', '2026-11-10 23:59:59.900'],
      ['00:00:00.100', '2026-11-11 00:00:00.100', '2026-11-11 00:00:00.100'],
    ]);
  });

  it('a line is given its time once: the screen working out its lines again does not read the date of a line it already shows', async () => {
    const written = new Date(2026, 10, 10, 14, 2, 11, 384).toISOString();
    const hours = vi.spyOn(Date.prototype, 'getHours');
    try {
      const { fixture } = await render([{ kind: 'stderr', line: 'info: started', ts: written }]);
      const first = hours.mock.calls.length;
      for (const level of ['warn', 'warn', 'debug', 'debug'] as const)
        await click(fixture, `filter-${level}`);
      fixture.componentInstance.query.set('started');
      fixture.detectChanges();

      expect({
        first,
        after: hours.mock.calls.length,
        text: (fixture.nativeElement as HTMLElement).querySelector('.line .ts')?.textContent,
      }).toEqual({ first: 1, after: 1, text: '14:02:11.384' });
    } finally {
      hours.mockRestore();
    }
  });

  it('Nothing matches draws its funnel from the icon registry, as the screen draws its other icons', async () => {
    const { fixture } = await render([stderr('info: one')]);
    fixture.componentInstance.query.set('no line has this');
    fixture.detectChanges();
    const icon = (fixture.nativeElement as HTMLElement).querySelector('.empty-icon sc-icon');

    expect(icon?.querySelector('path')?.getAttribute('d')).toBe('M3 5h18l-7 8v6l-4-2v-4z');
  });

  it('a row is the artboard row: the time at its own width, the level in 64 px, the text taking the rest and keeping its indentation, wrapping under a narrow window', async () => {
    const { fixture } = await render([stderr('info: one')]);
    const row = (fixture.nativeElement as HTMLElement).querySelector('.line')!;
    const at = (selector: string) => row.querySelector(selector)!;

    expect({
      display: declared(row, 'display'),
      wrap: declared(row, 'flex-wrap'),
      gap: declared(row, 'column-gap'),
      template: declared(row, 'grid-template-columns'),
      time: declared(at('.ts'), 'width'),
      level: declared(at('.lvl'), 'flex'),
      text: [
        declared(at('.msg'), 'flex'),
        declared(at('.msg'), 'min-width'),
        declared(at('.msg'), 'white-space'),
      ],
    }).toEqual({
      display: 'flex',
      wrap: 'wrap',
      gap: '12px',
      template: null,
      time: null,
      level: '0 0 64px',
      text: ['1 1 360px', '0', 'pre-wrap'],
    });
  });

  it('the log pane is as tall as its lines and scrolls inside once they fill the page', async () => {
    const { fixture } = await render([stderr('info: one')]);
    const viewer = (fixture.nativeElement as HTMLElement).querySelector('.viewer')!;

    expect({
      flex: declared(viewer, 'flex'),
      minHeight: declared(viewer, 'min-height'),
      overflow: declared(viewer, 'overflow-y'),
    }).toEqual({ flex: '0 1 auto', minHeight: '0', overflow: 'auto' });
  });

  it('Clear asks in the shared confirmation card, its Clear carrying the trash glyph the artboard draws', async () => {
    const { fixture } = await render([stderr('info: one')]);

    await click(fixture, 'clear');
    const facts = {
      panel: document.querySelector('.cdk-overlay-pane')?.classList.contains('sc-dialog'),
      glyph: document.querySelector('[data-test="confirm-ok"] svg path')?.getAttribute('d'),
      label: document.querySelector('[data-test="confirm-ok"]')?.textContent?.trim(),
    };
    await answerConfirm(fixture, 'confirm-cancel');

    expect(facts).toEqual({ panel: true, glyph: ICONS.trash, label: 'Clear' });
  });

  it("a line is the worker's text, never markup: a tag in it shows as typed and builds no element", async () => {
    const typed = 'info: reply <b>bold</b> <img src="x" onerror="alert(1)">';
    const { fixture } = await render([stderr(typed)]);
    const message = (fixture.nativeElement as HTMLElement).querySelector('.line .msg')!;

    expect({ text: message.textContent, elements: message.children.length }).toEqual({
      text: typed,
      elements: 0,
    });
  });

  it("only the worker's output lines are rows: a state change or an exit the store holds never shows as a line", async () => {
    const { fixture } = await render([
      stderr('info: one'),
      { kind: 'state', status: { state: 'running', restarts: 0 } },
      { kind: 'exit', code: 1, signal: null },
      { kind: 'stdout', line: 'two', ts: '2026-05-29T16:00:01.000Z' },
    ]);

    expect(texts(fixture, '.line .msg')).toEqual(['info: one', 'two']);
  });
});
