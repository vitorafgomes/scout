import { provideZonelessChangeDetection, type Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalAskRequest, LocalTranscriptEvent, LocalTurn } from '@scout/shared';

import { ChatComponent } from '../chat/chat.component';
import { ScoutBridgeService } from '../core/scout-bridge';

import { QuickAskComponent } from './quick-ask.component';

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
    return { onaudioprocess: null, connect: vi.fn(), disconnect: vi.fn() };
  }
  createGain() {
    return { gain: { value: 1 }, connect: vi.fn(), disconnect: vi.fn() };
  }
  async close(): Promise<void> {}
}

/** Main and the worker as the window sees them: one conversation, and the live transcript of the session it opened. */
function makeBridge() {
  let turns: readonly LocalTurn[] = [];
  const turnListeners: ((turns: readonly LocalTurn[]) => void)[] = [];
  const heard: ((event: LocalTranscriptEvent) => void)[] = [];
  return {
    hear: (event: Omit<LocalTranscriptEvent & { kind?: undefined }, 'id' | 'lane'>) =>
      heard.forEach((listener) => listener({ id: 'live-1', lane: 'me', ...event })),
    onLocalTranscript: vi.fn((listener: (event: LocalTranscriptEvent) => void) => {
      heard.push(listener);
      return () => undefined;
    }),
    localListen: vi.fn(async () => ({ id: 'live-1' })),
    localListening: vi.fn(async () => ({ listening: false })),
    onLocalListening: vi.fn(() => () => undefined),
    actionsList: vi.fn(async () => ({ actions: [] })),
    writesList: vi.fn(async () => ({ entries: [] })),
    onActionsChanged: vi.fn(() => () => undefined),
    localSpeak: vi.fn(async () => ({ contentType: 'audio/wav', base64: '' })),
    localAudio: vi.fn(async () => undefined),
    localStopListening: vi.fn(async () => undefined),
    onLocalTurns: vi.fn((listener: (turns: readonly LocalTurn[]) => void) => {
      turnListeners.push(listener);
      return () => undefined;
    }),
    localGetTurns: vi.fn(async () => turns),
    localAsk: vi.fn(async (req: LocalAskRequest) => {
      turns = [
        ...turns,
        {
          id: req.id,
          question: req.text,
          reply: '',
          citations: [],
          state: 'running',
          ...(req.voice === true ? { voice: true as const } : {}),
        },
      ];
      turnListeners.forEach((listener) => listener(turns));
    }),
    localCancel: vi.fn(async () => undefined),
    localHide: vi.fn(async () => undefined),
    onGitHubVerdict: vi.fn(() => () => undefined),
    githubVerdictFor: vi.fn(async () => null),
    vaultHas: vi.fn(async () => ({ exists: false })),
    vaultGetPublic: vi.fn(async () => ({ value: null })),
    whisperListInstalled: vi.fn(async () => ({
      installed: [{ variant: 'base' as const, verified: true }],
      inUse: 'base' as const,
      preferred: null,
    })),
    onWhisperDownloadProgress: vi.fn(() => () => undefined),
    appOpenExternal: vi.fn(async () => undefined),
    workerStart: vi.fn(async () => ({ pid: 1 })),
  };
}

type Bridge = ReturnType<typeof makeBridge>;

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 5; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

interface Entry {
  readonly name: string;
  readonly component: Type<unknown>;
  readonly field: string;
  readonly bubble: string;
  readonly mark: string;
  press(fixture: ComponentFixture<unknown>): void;
  release(fixture: ComponentFixture<unknown>): void;
}

const mic = (fixture: ComponentFixture<unknown>) =>
  (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('[data-test="mic"]')!;

const ENTRIES: readonly Entry[] = [
  {
    name: 'the quick-ask microphone',
    component: QuickAskComponent,
    field: 'question',
    bubble: 'asked',
    mark: 'asked-voice',
    press: (fixture) => mic(fixture).dispatchEvent(new Event('pointerdown')),
    release: (fixture) => mic(fixture).dispatchEvent(new Event('pointerup')),
  },
  {
    name: 'Space held in quick ask',
    component: QuickAskComponent,
    field: 'question',
    bubble: 'asked',
    mark: 'asked-voice',
    press: () => window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', key: ' ' })),
    release: () => window.dispatchEvent(new KeyboardEvent('keyup', { code: 'Space', key: ' ' })),
  },
  {
    name: 'the Chat microphone',
    component: ChatComponent,
    field: 'draft',
    bubble: 'question',
    mark: 'question-voice',
    press: (fixture) => mic(fixture).dispatchEvent(new Event('pointerdown')),
    release: (fixture) => mic(fixture).dispatchEvent(new Event('pointerup')),
  },
];

async function render(
  entry: Entry,
): Promise<{ fixture: ComponentFixture<unknown>; bridge: Bridge }> {
  const bridge = makeBridge();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(entry.component);
  await settle(fixture);
  return { fixture, bridge };
}

function field(fixture: ComponentFixture<unknown>, entry: Entry): string {
  const host = fixture.nativeElement as HTMLElement;
  const box =
    host.querySelector<HTMLTextAreaElement>(`textarea[data-test="${entry.field}"]`) ??
    host.querySelector<HTMLTextAreaElement>('textarea');
  return box?.value ?? '';
}

function shown(fixture: ComponentFixture<unknown>, test: string): string | null {
  return (
    (fixture.nativeElement as HTMLElement)
      .querySelector(`[data-test="${test}"]`)
      ?.textContent?.replace(/\s+/g, ' ')
      .trim() ?? null
  );
}

function speaking(fixture: ComponentFixture<unknown>): boolean {
  return (
    (fixture.nativeElement as HTMLElement).querySelector(
      '[data-test="recording"] sc-icon[data-test="speaking"]',
    ) !== null
  );
}

const OUTCOMES = {
  'speech heard': {
    heard: (bridge: Bridge) => bridge.hear({ text: 'list my', final: false }),
    trailing: (bridge: Bridge) =>
      bridge.hear({ text: 'list my open pull requests', final: true, seq: 1 }),
    ended: { text: '', final: true, ended: true as const },
    asked: [{ text: 'list my open pull requests', voice: true }],
    notice: null,
    error: null,
  },
  'no speech heard': {
    heard: () => undefined,
    trailing: () => undefined,
    ended: { text: '', final: true, ended: true as const },
    asked: [],
    notice: 'No speech heard; nothing was sent.',
    error: null,
  },
  'transcription failed': {
    heard: (bridge: Bridge) => bridge.hear({ text: 'list my', final: false }),
    trailing: () => undefined,
    ended: {
      text: '',
      final: true,
      ended: true as const,
      error: 'Live transcription stopped: the Whisper model could not be loaded.',
    },
    asked: [],
    notice: null,
    error: 'Live transcription stopped: the Whisper model could not be loaded.',
  },
} as const;

beforeAll(async () => {
  for (const entry of ENTRIES) {
    const { fixture } = await render(entry);
    fixture.destroy();
    TestBed.resetTestingModule();
  }
  document.body.innerHTML = '';
}, 30_000);

describe("the owner's bubble showed (voice message) instead of the spoken text", () => {
  describe('hold to talk dictates the question: what the owner says is what is sent', () => {
    beforeEach(() => {
      TestBed.resetTestingModule();
      localStorage.clear();
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

    for (const entry of ENTRIES) {
      for (const [outcome, row] of Object.entries(OUTCOMES)) {
        it(`${entry.name}, ${outcome}: ${row.asked.length > 0 ? 'the partial fills the field while held, marked speaking with the mic, release waits for the last final and sends it as a voice question with no recording, and the bubble shows the mic and the text' : `nothing is sent and the window says "${row.notice ?? row.error}"`}`, async () => {
          const { fixture, bridge } = await render(entry);

          entry.press(fixture);
          await settle(fixture);
          row.heard(bridge);
          await settle(fixture);
          const whileHeld = field(fixture, entry);
          const markedSpeaking = speaking(fixture);
          entry.release(fixture);
          await vi.waitFor(() => expect(bridge.localStopListening).toHaveBeenCalledTimes(1));
          await settle(fixture);
          row.trailing(bridge);
          bridge.hear(row.ended);
          await vi.waitFor(() =>
            expect(bridge.localAsk.mock.calls.length + (row.asked.length === 0 ? 1 : 0)).toBe(1),
          );
          await settle(fixture);

          expect(bridge.localListen).toHaveBeenCalledTimes(1);
          expect(whileHeld).toBe(outcome === 'no speech heard' ? '' : 'list my');
          expect(markedSpeaking).toBe(true);
          expect(speaking(fixture)).toBe(false);
          expect(
            bridge.localAsk.mock.calls.map(([req]) => {
              const { id: _id, ...rest } = req as LocalAskRequest & { voice?: true };
              return rest;
            }),
          ).toEqual(row.asked);
          if (row.asked.length > 0) {
            expect(shown(fixture, entry.bubble)).toBe('list my open pull requests');
            expect(
              (fixture.nativeElement as HTMLElement).querySelector(`[data-test="${entry.mark}"]`),
            ).not.toBeNull();
            expect((fixture.nativeElement as HTMLElement).textContent).not.toContain(
              '(voice message)',
            );
          }
          await vi.waitFor(() => {
            expect(shown(fixture, 'voice-notice')).toBe(row.notice);
            expect(shown(fixture, 'error')).toBe(row.error);
          });
        });
      }
    }

    it('a release whose worker never ends the session sends what was heard after 10 s, not 20 s', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      try {
        const entry = ENTRIES.find((one) => one.name === 'the Chat microphone')!;
        const { fixture, bridge } = await render(entry);
        entry.press(fixture);
        await settle(fixture);
        bridge.hear({ text: 'list my open pull requests', final: true, seq: 1 });
        await settle(fixture);

        entry.release(fixture);
        await settle(fixture);
        await vi.advanceTimersByTimeAsync(9_999);
        await settle(fixture);
        const askedBefore = bridge.localAsk.mock.calls.length;
        await vi.advanceTimersByTimeAsync(1);
        await settle(fixture);

        expect(bridge.localStopListening).toHaveBeenCalledTimes(1);
        expect(askedBefore).toBe(0);
        expect(bridge.localAsk.mock.calls.map(([req]) => (req as LocalAskRequest).text)).toEqual([
          'list my open pull requests',
        ]);
      } finally {
        vi.useRealTimers();
      }
    });
  });
});
