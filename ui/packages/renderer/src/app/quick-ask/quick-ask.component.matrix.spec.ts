import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalAskRequest, LocalTranscriptEvent, LocalTurn } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';
import { QuickAskComponent } from './quick-ask.component';

const BUSY = 'Scout is queuing cards for your approval; wait until it finishes.';
const TYPED = 'next question';

type State =
  | 'idle'
  | 'answering'
  | 'answering with a card post running'
  | 'a card post from the other window main has not pushed yet';
type Action =
  | 'Enter in the field'
  | 'Send click'
  | 'Mic hold'
  | 'Space in the field'
  | 'Space in the card editor'
  | 'Space in the screen-text box'
  | 'Escape in the field'
  | 'Escape in the card editor'
  | 'Stop click'
  | 'Clear';

interface Outcome {
  readonly available: boolean;
  readonly sent: number;
  readonly refusal: string | null;
  readonly stop: boolean;
  readonly hidden: boolean;
  readonly cancelled: string | null;
  readonly field: string;
  readonly cleared: boolean;
}

const STATES: readonly State[] = [
  'idle',
  'answering',
  'answering with a card post running',
  'a card post from the other window main has not pushed yet',
];
const ACTIONS: readonly Action[] = [
  'Enter in the field',
  'Send click',
  'Mic hold',
  'Space in the field',
  'Space in the card editor',
  'Space in the screen-text box',
  'Escape in the field',
  'Escape in the card editor',
  'Stop click',
  'Clear',
];

const SAID = 'what changed since yesterday';
const STARTS_EMPTY: ReadonlySet<Action> = new Set(['Mic hold', 'Space in the field']);

function o(
  available: boolean,
  sent: number,
  refusal: string | null,
  stop: boolean,
  hidden: boolean,
  cancelled: string | null,
  field: string,
  cleared = false,
): Outcome {
  return { available, sent, refusal, stop, hidden, cancelled, field, cleared };
}

const EXPECTED: Readonly<Record<Action, Readonly<Record<State, Outcome>>>> = {
  'Enter in the field': {
    idle: o(true, 1, null, true, false, null, ''),
    answering: o(true, 1, null, true, false, null, ''),
    'answering with a card post running': o(false, 0, null, true, false, null, TYPED),
    'a card post from the other window main has not pushed yet': o(
      true,
      1,
      BUSY,
      false,
      false,
      null,
      TYPED,
    ),
  },
  'Send click': {
    idle: o(true, 1, null, true, false, null, ''),
    answering: o(true, 1, null, true, false, null, ''),
    'answering with a card post running': o(false, 0, null, true, false, null, TYPED),
    'a card post from the other window main has not pushed yet': o(
      true,
      1,
      BUSY,
      false,
      false,
      null,
      TYPED,
    ),
  },
  'Mic hold': {
    idle: o(true, 1, null, true, false, null, ''),
    answering: o(false, 0, null, true, false, null, ''),
    'answering with a card post running': o(false, 0, null, true, false, null, ''),
    'a card post from the other window main has not pushed yet': o(
      true,
      1,
      BUSY,
      false,
      false,
      null,
      SAID,
    ),
  },
  'Space in the field': {
    idle: o(true, 1, null, true, false, null, ''),
    answering: o(true, 0, null, true, false, null, ''),
    'answering with a card post running': o(false, 0, null, true, false, null, ''),
    'a card post from the other window main has not pushed yet': o(
      true,
      1,
      BUSY,
      false,
      false,
      null,
      SAID,
    ),
  },
  'Space in the card editor': {
    idle: o(true, 0, null, false, false, null, TYPED),
    answering: o(true, 0, null, true, false, null, TYPED),
    'answering with a card post running': o(true, 0, null, true, false, null, TYPED),
    'a card post from the other window main has not pushed yet': o(
      true,
      0,
      null,
      false,
      false,
      null,
      TYPED,
    ),
  },
  'Space in the screen-text box': {
    idle: o(true, 0, null, false, false, null, TYPED),
    answering: o(true, 0, null, true, false, null, TYPED),
    'answering with a card post running': o(true, 0, null, true, false, null, TYPED),
    'a card post from the other window main has not pushed yet': o(
      true,
      0,
      null,
      false,
      false,
      null,
      TYPED,
    ),
  },
  'Escape in the field': {
    idle: o(true, 0, null, false, true, null, TYPED),
    answering: o(true, 0, null, true, true, null, TYPED),
    'answering with a card post running': o(false, 0, null, true, true, null, TYPED),
    'a card post from the other window main has not pushed yet': o(
      true,
      0,
      null,
      false,
      true,
      null,
      TYPED,
    ),
  },
  'Escape in the card editor': {
    idle: o(true, 0, null, false, false, null, TYPED),
    answering: o(true, 0, null, true, false, null, TYPED),
    'answering with a card post running': o(true, 0, null, true, false, null, TYPED),
    'a card post from the other window main has not pushed yet': o(
      true,
      0,
      null,
      false,
      false,
      null,
      TYPED,
    ),
  },
  'Stop click': {
    idle: o(false, 0, null, false, false, null, TYPED),
    answering: o(true, 0, null, true, false, 'q-run', TYPED),
    'answering with a card post running': o(false, 0, null, true, false, null, TYPED),
    'a card post from the other window main has not pushed yet': o(
      false,
      0,
      null,
      false,
      false,
      null,
      TYPED,
    ),
  },
  Clear: {
    idle: o(true, 0, null, false, false, null, TYPED, true),
    answering: o(true, 0, null, true, false, null, TYPED, true),
    'answering with a card post running': o(false, 0, null, true, false, null, TYPED),
    'a card post from the other window main has not pushed yet': o(
      true,
      0,
      BUSY,
      false,
      false,
      null,
      TYPED,
      true,
    ),
  },
};

const BLOCK = JSON.stringify({
  repository: 'acme/shop',
  number: 5,
  verdict: 'merge after the fix',
  findings: [
    {
      id: 'lock',
      severity: 'fix',
      title: 'Move has no lock',
      path: 'src/Cart.cs',
      line: 12,
      why: 'Two moves lose an item.',
      fix: 'Take the lock.',
    },
  ],
});
const FINDINGS_TURN: LocalTurn = {
  id: 'f-1',
  question: 'review acme/shop#5',
  reply: `\`\`\`scout-findings\n${BLOCK}\n\`\`\``,
  citations: [],
  state: 'done',
};
const RUNNING: Readonly<Record<State, readonly LocalTurn[]>> = {
  idle: [],
  answering: [
    { id: 'q-run', question: 'is prod green?', reply: '', citations: [], state: 'running' },
  ],
  'answering with a card post running': [
    {
      id: 'post-1',
      question: 'Open 1 issue on acme/shop',
      reply: '',
      citations: [],
      state: 'running',
      issues: true,
    },
  ],
  'a card post from the other window main has not pushed yet': [],
};

const heard: ((event: LocalTranscriptEvent) => void)[] = [];

/** The worker's live transcript of the held microphone: a final line, or with null the end of the session. */
function speak(text: string | null): void {
  for (const listener of heard) {
    listener(
      text === null
        ? { id: 'live-1', lane: 'me', text: '', final: true, ended: true }
        : { id: 'live-1', lane: 'me', text, final: true, seq: 1 },
    );
  }
}

function makeBridge(state: State) {
  heard.length = 0;
  let turns: readonly LocalTurn[] = [FINDINGS_TURN, ...RUNNING[state]];
  const listeners: ((turns: readonly LocalTurn[]) => void)[] = [];
  const push = (next: readonly LocalTurn[]) => {
    turns = next;
    listeners.forEach((listener) => listener(turns));
  };
  const refuses = state === 'a card post from the other window main has not pushed yet';
  return {
    onLocalTurns: vi.fn((listener: (turns: readonly LocalTurn[]) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
    localGetTurns: vi.fn(async (): Promise<readonly LocalTurn[]> => turns),
    actionsList: vi.fn(async () => ({ actions: [] })),
    writesList: vi.fn(async () => ({ entries: [] })),
    onActionsChanged: vi.fn(() => () => undefined),
    localAsk: vi.fn(async (req: LocalAskRequest): Promise<void> => {
      if (refuses) throw new Error(`[scout:local/busy] ${BUSY}`);
      push([
        ...turns,
        {
          id: req.id,
          question: req.text,
          reply: '',
          citations: [],
          state: 'running',
        },
      ]);
    }),
    localCancel: vi.fn(async (_req: { id: string }): Promise<void> => undefined),
    localSetDecisions: vi.fn(async (): Promise<void> => undefined),
    onGitHubVerdict: vi.fn(() => () => undefined),
    githubVerdictFor: vi.fn(async () => null),
    localClearTurns: vi.fn(async (): Promise<void> => {
      if (refuses) throw new Error(`[scout:local/busy] ${BUSY}`);
    }),
    localHide: vi.fn(async (): Promise<void> => undefined),
    onLocalTranscript: vi.fn((listener: (event: LocalTranscriptEvent) => void) => {
      heard.push(listener);
      return () => undefined;
    }),
    localListen: vi.fn(async () => ({ id: 'live-1' })),
    localAudio: vi.fn(async (): Promise<void> => undefined),
    localStopListening: vi.fn(async (): Promise<void> => undefined),
    vaultHas: vi.fn(async () => ({ exists: false })),
    vaultGetPublic: vi.fn(async () => ({ value: null })),
    whisperListInstalled: vi.fn(async () => ({
      installed: [{ variant: 'base' as const, verified: true }],
    })),
    onWhisperDownloadProgress: vi.fn(() => () => undefined),
    appOpenExternal: vi.fn(async (): Promise<void> => undefined),
  };
}

const audio = { processor: null as { hear(samples: number[]): void } | null };

class FakeAudioContext {
  readonly sampleRate: number;
  readonly destination = {};
  constructor(options: { sampleRate: number }) {
    this.sampleRate = options.sampleRate;
  }
  createMediaStreamSource() {
    return { connect: vi.fn(), disconnect: vi.fn() };
  }
  createScriptProcessor() {
    const processor = {
      onaudioprocess: null as ((event: unknown) => void) | null,
      connect: vi.fn(),
      disconnect: vi.fn(),
      hear(samples: number[]) {
        processor.onaudioprocess?.({
          inputBuffer: { getChannelData: () => Float32Array.from(samples) },
        });
      },
    };
    audio.processor = processor;
    return processor;
  }
  createGain() {
    return { gain: { value: 1 }, connect: vi.fn(), disconnect: vi.fn() };
  }
  async close(): Promise<void> {
    return;
  }
}

type Fixture = ComponentFixture<QuickAskComponent>;

async function settle(fixture: Fixture): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el(fixture: Fixture, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

async function key(
  fixture: Fixture,
  target: HTMLElement,
  code: string,
  name: string,
): Promise<void> {
  target.focus();
  target.dispatchEvent(
    new KeyboardEvent('keydown', { key: name, code, bubbles: true, cancelable: true }),
  );
  await settle(fixture);
  speak(SAID);
  target.dispatchEvent(
    new KeyboardEvent('keyup', { key: name, code, bubbles: true, cancelable: true }),
  );
  await settle(fixture);
  speak(null);
  await settle(fixture);
}

async function act(fixture: Fixture, action: Action): Promise<boolean> {
  const question = el(fixture, 'question') as HTMLInputElement;
  const editor = el(fixture, 'comment-text') as HTMLTextAreaElement;
  const screen = el(fixture, 'screen-text') as HTMLTextAreaElement;
  switch (action) {
    case 'Enter in the field': {
      const usable = !question.disabled;
      (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(
        new Event('submit', { cancelable: true }),
      );
      await settle(fixture);
      return usable;
    }
    case 'Send click': {
      const send = el(fixture, 'send') as HTMLButtonElement;
      const usable = !send.disabled;
      send.click();
      await settle(fixture);
      return usable;
    }
    case 'Mic hold': {
      const mic = el(fixture, 'mic') as HTMLButtonElement;
      const usable = !mic.disabled;
      mic.dispatchEvent(new Event('pointerdown'));
      await settle(fixture);
      speak(SAID);
      mic.dispatchEvent(new Event('pointerup'));
      await settle(fixture);
      speak(null);
      await settle(fixture);
      return usable;
    }
    case 'Space in the field': {
      const usable = !question.disabled;
      await key(fixture, question, 'Space', ' ');
      return usable;
    }
    case 'Space in the card editor':
      await key(fixture, editor, 'Space', ' ');
      return true;
    case 'Space in the screen-text box':
      await key(fixture, screen, 'Space', ' ');
      return true;
    case 'Escape in the field': {
      const usable = !question.disabled;
      await key(fixture, question, 'Escape', 'Escape');
      return usable;
    }
    case 'Escape in the card editor':
      await key(fixture, editor, 'Escape', 'Escape');
      return true;
    case 'Stop click': {
      const stop = el(fixture, 'stop') as HTMLButtonElement | null;
      const usable = stop !== null && !stop.disabled;
      stop?.click();
      await settle(fixture);
      return usable;
    }
    case 'Clear': {
      const clear = el(fixture, 'clear-conversation') as HTMLButtonElement;
      clear.click();
      await settle(fixture);
      return !clear.disabled;
    }
  }
}

const ROWS = STATES.flatMap((state) => ACTIONS.map((action) => ({ state, action })));

beforeAll(async () => {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      {
        provide: ScoutBridgeService,
        useValue: makeBridge('idle') as unknown as ScoutBridgeService,
      },
    ],
  });
  const fixture = TestBed.createComponent(QuickAskComponent);
  await settle(fixture);
  fixture.destroy();
  TestBed.resetTestingModule();
  document.body.innerHTML = '';
}, 30_000);

describe(`quick ask matrix (${ROWS.length} rows)`, () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    audio.processor = null;
    vi.stubGlobal('AudioContext', FakeAudioContext);
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: {
        getUserMedia: vi.fn(async () => ({ getTracks: () => [{ stop: () => undefined }] })),
      },
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it.each(ROWS.map(({ state, action }) => [`${action}, ${state}`, state, action] as const))(
    '%s',
    async (_name, state, action) => {
      const bridge = makeBridge(state);
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
        ],
      });
      const fixture = TestBed.createComponent(QuickAskComponent);
      await settle(fixture);
      const quickAsk = fixture.componentInstance;
      quickAsk.screenText.set('Error 502');
      quickAsk.question.set(STARTS_EMPTY.has(action) ? '' : TYPED);
      await settle(fixture);
      el(fixture, 'edit')!.click();
      await settle(fixture);
      const editor = el(fixture, 'comment-text');

      const available = await act(fixture, action);
      await vi.waitFor(() => {
        fixture.detectChanges();
        expect(bridge.localAsk.mock.calls.length).toBe(EXPECTED[action][state].sent);
      });
      await settle(fixture);

      expect(editor).not.toBeNull();
      expect({
        available,
        sent: bridge.localAsk.mock.calls.length,
        refusal: el(fixture, 'error')?.textContent?.trim() ?? null,
        stop: el(fixture, 'stop') !== null,
        hidden: bridge.localHide.mock.calls.length > 0,
        cancelled:
          (bridge.localCancel.mock.calls[0]?.[0] as { id: string } | undefined)?.id ?? null,
        field: quickAsk.question(),
        cleared: bridge.localClearTurns.mock.calls.length > 0,
      }).toEqual(EXPECTED[action][state]);
    },
  );
});
