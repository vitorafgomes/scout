import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalAskRequest, LocalTranscriptEvent, LocalTurn } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';
import { toBase64 } from '../quick-ask/wav-recorder';
import { MICROPHONE_STORAGE_KEY } from './audio-devices';
import {
  CONVERSATION_RECORDER,
  ConversationMode,
  nextConversationState,
  type ConversationEvent,
  type ConversationState,
} from './conversation-mode';
import { SPEECH_PLAYER } from './read-aloud';
import type { SpeechPlayer } from './speech-queue';

const flush = async (): Promise<void> => {
  for (let i = 0; i < 20; i++) {
    TestBed.tick();
    await Promise.resolve();
  }
};

function makeBridge() {
  let turns: readonly LocalTurn[] = [];
  const turnListeners: ((turns: readonly LocalTurn[]) => void)[] = [];
  const heardListeners: ((event: LocalTranscriptEvent) => void)[] = [];
  const push = (next: readonly LocalTurn[]) => {
    turns = next;
    turnListeners.forEach((listener) => listener(turns));
  };
  return {
    push,
    reply: (id: string, reply: string, state: LocalTurn['state']) =>
      push(turns.map((turn) => (turn.id === id ? { ...turn, reply, state } : turn))),
    hear: (event: LocalTranscriptEvent) => heardListeners.forEach((listener) => listener(event)),
    onLocalTurns: vi.fn((listener: (turns: readonly LocalTurn[]) => void) => {
      turnListeners.push(listener);
      return () => undefined;
    }),
    localGetTurns: vi.fn(async () => turns),
    localAsk: vi.fn(async (req: LocalAskRequest) => {
      push([
        ...turns,
        {
          id: req.id,
          question: 'text' in req ? req.text : '',
          reply: '',
          citations: [],
          state: 'running',
        },
      ]);
    }),
    onLocalTranscript: vi.fn((listener: (event: LocalTranscriptEvent) => void) => {
      heardListeners.push(listener);
      return () => undefined;
    }),
    localListen: vi.fn(async (_req?: { lane?: string }) => ({ id: 'talk-1' })),
    localAudio: vi.fn(async (_req: { id: string; pcm16: string }) => undefined),
    localStopListening: vi.fn(async (_req: { id: string }) => undefined),
    localSpeak: vi.fn(async (req: { text: string }) => ({
      contentType: 'audio/wav',
      base64: req.text,
    })),
  };
}

function setup() {
  const bridge = makeBridge();
  const ends: (() => void)[] = [];
  const player = {
    played: [] as string[],
    play: vi.fn((audio: { base64: string }) => {
      player.played.push(audio.base64);
      return new Promise<void>((resolve) => ends.push(resolve));
    }),
    stop: vi.fn(() => ends.splice(0).forEach((end) => end())),
  };
  const mic = {
    onChunk: null as ((pcm16: Uint8Array) => void) | null,
    source: undefined as (() => Promise<MediaStream>) | undefined,
    start: vi.fn(
      async (
        _onLimit: () => void,
        onChunk?: (pcm16: Uint8Array) => void,
        source?: () => Promise<MediaStream>,
      ) => {
        mic.onChunk = onChunk ?? null;
        mic.source = source;
      },
    ),
    stop: vi.fn(async () => null),
  };
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: SPEECH_PLAYER, useValue: player as unknown as SpeechPlayer },
      { provide: CONVERSATION_RECORDER, useValue: mic },
    ],
  });
  const mode = TestBed.inject(ConversationMode);
  const asked = () => (bridge.localAsk.mock.calls.at(-1)?.[0] as { id: string; text: string }).id;
  const chunk = () => {
    const pcm16 = new Uint8Array([1, 2, 3, 4]);
    mic.onChunk?.(pcm16);
    return (bridge.localAudio.mock.calls.at(-1)?.[0] as { pcm16: string }).pcm16;
  };
  const finalLine = (text: string, id = 'talk-1'): LocalTranscriptEvent => ({
    id,
    lane: 'me',
    text,
    final: true,
  });
  const ended = (id = 'talk-1', error?: string): LocalTranscriptEvent => ({
    id,
    lane: 'me',
    text: '',
    final: true,
    ended: true,
    ...(error !== undefined ? { error } : {}),
  });
  const questions = () =>
    bridge.localAsk.mock.calls.map(([req]) => ('text' in req ? req.text : ''));
  const endPlaying = async () => {
    ends.splice(0).forEach((end) => end());
    await flush();
  };
  return { bridge, player, mic, mode, asked, chunk, finalLine, ended, questions, endPlaying };
}

const SPOKEN = toBase64(new Uint8Array([1, 2, 3, 4]));
const SILENCE = toBase64(new Uint8Array(4));

const TABLE: readonly (readonly [ConversationState, ConversationEvent, ConversationState])[] = [
  ['off', 'start', 'listening'],
  ['off', 'heard', 'off'],
  ['off', 'speaking', 'off'],
  ['off', 'done', 'off'],
  ['off', 'stop', 'off'],
  ['listening', 'start', 'listening'],
  ['listening', 'heard', 'thinking'],
  ['listening', 'speaking', 'listening'],
  ['listening', 'done', 'listening'],
  ['listening', 'stop', 'off'],
  ['thinking', 'start', 'thinking'],
  ['thinking', 'heard', 'thinking'],
  ['thinking', 'speaking', 'speaking'],
  ['thinking', 'done', 'listening'],
  ['thinking', 'stop', 'off'],
  ['speaking', 'start', 'speaking'],
  ['speaking', 'heard', 'speaking'],
  ['speaking', 'speaking', 'speaking'],
  ['speaking', 'done', 'listening'],
  ['speaking', 'stop', 'off'],
];

describe('nextConversationState: listen, final, ask, reply, speak, listen; stop from each state', () => {
  it.each(TABLE)('%s on %s goes to %s', (state, event, expected) => {
    expect(nextConversationState(state, event)).toBe(expected);
  });
});

describe('ConversationMode', () => {
  beforeEach(() => TestBed.resetTestingModule());
  afterEach(() => localStorage.clear());

  it('listens, sends the final line heard after a pause as a question, reads the reply aloud, then listens again', async () => {
    const t = setup();

    await t.mode.toggle();
    await flush();
    const listening = t.mode.state();
    t.bridge.hear({ id: 'talk-1', lane: 'me', text: 'what is the', final: false });
    t.bridge.hear(t.finalLine('What is the build on main?'));
    await flush();
    const thinking = t.mode.state();
    t.bridge.reply(t.asked(), 'The build is green. ', 'running');
    await flush();
    const speaking = t.mode.state();
    t.bridge.reply(t.asked(), 'The build is green. Two checks ran.', 'done');
    await flush();
    await t.endPlaying();
    await t.endPlaying();

    expect(t.bridge.localListen).toHaveBeenCalledExactlyOnceWith({ lane: 'me' });
    expect(t.bridge.localAsk).toHaveBeenCalledExactlyOnceWith({
      id: t.asked(),
      text: 'What is the build on main?',
    });
    expect([listening, thinking, speaking, t.mode.state()]).toEqual([
      'listening',
      'thinking',
      'speaking',
      'listening',
    ]);
    expect(t.player.played).toEqual(['The build is green.', 'Two checks ran.']);
  });

  it('the microphone is muted from the moment a line is heard until the answer has been read, and open while listening', async () => {
    const t = setup();
    await t.mode.start();
    await flush();
    const listening = t.chunk();

    t.bridge.hear(t.finalLine('Is main green?'));
    await flush();
    const thinking = t.chunk();
    t.bridge.reply(t.asked(), 'Yes. ', 'running');
    await flush();
    const speaking = t.chunk();
    t.bridge.reply(t.asked(), 'Yes. ', 'done');
    await t.endPlaying();
    const again = t.chunk();

    expect([listening, thinking, speaking, again]).toEqual([SPOKEN, SILENCE, SILENCE, SPOKEN]);
  });

  it.each(['listening', 'thinking', 'speaking'] as const)(
    'a click while %s ends it: the voice stops, the microphone closes and the listen ends',
    async (from) => {
      const t = setup();
      await t.mode.start();
      await flush();
      if (from !== 'listening') {
        t.bridge.hear(t.finalLine('Is main green?'));
        await flush();
      }
      if (from === 'speaking') {
        t.bridge.reply(t.asked(), 'Yes. ', 'running');
        await flush();
      }
      const before = t.mode.state();

      await t.mode.toggle();
      await flush();

      expect(before).toBe(from);
      expect(t.mode.state()).toBe('off');
      expect(t.mic.stop).toHaveBeenCalledTimes(1);
      expect(t.bridge.localStopListening).toHaveBeenCalledExactlyOnceWith({ id: 'talk-1' });
      expect(t.player.stop).toHaveBeenCalled();
    },
  );

  it.each([
    ['on', true, true],
    ['off', false, false],
  ])(
    'Esc while the conversation is %s answers %s and leaves it off',
    async (_name, on, expected) => {
      const t = setup();
      if (on) {
        await t.mode.start();
        await flush();
      }

      const handled = t.mode.escape();
      await flush();

      expect(handled).toBe(expected);
      expect(t.mode.state()).toBe('off');
    },
  );

  it('a line from another listen is not sent, and a line heard while Scout answers is not sent either', async () => {
    const t = setup();
    await t.mode.start();
    await flush();

    t.bridge.hear(t.finalLine('From dictation.', 'live-9'));
    t.bridge.hear(t.finalLine('First question.'));
    await flush();
    t.bridge.hear(t.finalLine('Heard while thinking.'));
    await flush();

    expect(t.bridge.localAsk.mock.calls.map(([req]) => ('text' in req ? req.text : ''))).toEqual([
      'First question.',
    ]);
  });

  it('a click while the owner is still speaking keeps following the listen until it ends, and what was said becomes the question', async () => {
    const t = setup();
    await t.mode.start();
    await flush();

    await t.mode.toggle();
    t.bridge.hear(t.finalLine('What is the build'));
    t.bridge.hear(t.finalLine('on main?'));
    await flush();
    const beforeEnded = t.questions();
    t.bridge.hear(t.ended());
    await flush();

    expect(beforeEnded).toEqual([]);
    expect(t.questions()).toEqual(['What is the build on main?']);
    expect(t.mode.state()).toBe('off');
    expect(t.mode.error()).toBeNull();
  });

  it('Esc while the owner is still speaking ends the conversation and drops what was in flight', async () => {
    const t = setup();
    await t.mode.start();
    await flush();

    t.mode.escape();
    await flush();
    t.bridge.hear(t.finalLine('Never mind.'));
    t.bridge.hear(t.ended());
    await flush();

    expect(t.questions()).toEqual([]);
    expect(t.mode.error()).toBeNull();
  });

  it('Esc after a click, while the last listen drains, drops what was in flight, and the end that follows asks nothing', async () => {
    const t = setup();
    await t.mode.start();
    await flush();
    await t.mode.toggle();

    t.mode.escape();
    t.bridge.hear(t.finalLine('Never mind.'));
    t.bridge.hear(t.ended());
    await flush();

    expect(t.questions()).toEqual([]);
  });

  it('turning the conversation back on while the last listen drains never asks the old line', async () => {
    const t = setup();
    await t.mode.start();
    await flush();
    await t.mode.toggle();
    t.bridge.localListen.mockResolvedValueOnce({ id: 'talk-2' });

    await t.mode.toggle();
    await flush();
    t.bridge.hear(t.finalLine('The old line.'));
    t.bridge.hear(t.ended('talk-1', 'Another listen took the microphone.'));
    await flush();

    expect(t.questions()).toEqual([]);
    expect(t.mode.state()).toBe('listening');
    expect(t.mode.error()).toBeNull();
  });

  describe('a last listen that never ends', () => {
    afterEach(() => vi.useRealTimers());

    it('Esc after a click drops the draining line, so the 10 s bound asks nothing either', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      const t = setup();
      await t.mode.start();
      await flush();
      await t.mode.toggle();
      t.bridge.hear(t.finalLine('Never mind.'));
      await flush();

      t.mode.escape();
      vi.advanceTimersByTime(10_000);
      await flush();

      expect(t.questions()).toEqual([]);
    });

    it('is given up after 10 s, what was heard by then is asked, and a late end asks nothing more', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      const t = setup();
      await t.mode.start();
      await flush();
      await t.mode.toggle();
      t.bridge.hear(t.finalLine('Is main green?'));
      await flush();

      vi.advanceTimersByTime(9_999);
      await flush();
      const before = t.questions();
      vi.advanceTimersByTime(1);
      await flush();
      const atTenSeconds = t.questions();
      t.bridge.hear(t.ended());
      await flush();

      expect(before).toEqual([]);
      expect(atTenSeconds).toEqual(['Is main green?']);
      expect(t.questions()).toEqual(['Is main green?']);
    });
  });

  it('what was heard so far is shown while Scout listens, and cleared once the line is final or the conversation ends', async () => {
    const t = setup();
    await t.mode.start();
    await flush();

    t.bridge.hear({ id: 'talk-1', lane: 'me', text: 'what is the', final: false });
    t.bridge.hear({ id: 'live-9', lane: 'me', text: 'from dictation', final: false });
    await flush();
    const partial = t.mode.heard();
    t.bridge.hear(t.finalLine('What is the build?'));
    await flush();
    const afterFinal = t.mode.heard();
    t.bridge.reply(t.asked(), 'Green.', 'done');
    await flush();
    await t.endPlaying();
    const listeningAgain = t.mode.state();
    t.bridge.hear({ id: 'talk-1', lane: 'me', text: 'and the', final: false });
    await flush();
    const again = t.mode.heard();
    await t.mode.stop();

    expect(listeningAgain).toBe('listening');
    expect([partial, afterFinal, again, t.mode.heard()]).toEqual([
      'what is the',
      '',
      'and the',
      '',
    ]);
  });

  it.each([
    ['Another listen took the microphone.', 'Another listen took the microphone.'],
    [undefined, 'Scout stopped listening.'],
  ])(
    'a listen ended under the conversation (reason %s) turns it off and says why on the button',
    async (reason, shown) => {
      const t = setup();
      await t.mode.start();
      await flush();

      t.bridge.hear(t.ended('talk-1', reason));
      await flush();

      expect(t.mode.state()).toBe('off');
      expect(t.mode.error()).toBe(shown);
      expect(t.mic.stop).toHaveBeenCalledTimes(1);
      expect(t.bridge.localStopListening).not.toHaveBeenCalled();
    },
  );

  it.each(['listening', 'thinking'] as const)(
    'main refusing the audio while %s turns the conversation off and shows its reason',
    async (from) => {
      const t = setup();
      await t.mode.start();
      await flush();
      if (from === 'thinking') {
        t.bridge.hear(t.finalLine('Is main green?'));
        await flush();
      }
      t.bridge.localAudio.mockRejectedValue(
        Object.assign(new Error('Scout is not listening.'), { code: 'local/invalid-request' }),
      );

      for (let i = 0; i < 5; i++) t.mic.onChunk?.(new Uint8Array(8));
      await flush();

      expect(t.mode.state()).toBe('off');
      expect(t.mode.error()).toBe('Scout is not listening.');
      expect(t.mic.stop).toHaveBeenCalledTimes(1);
    },
  );

  it('audio refused for a listen that already ended does not end the next conversation', async () => {
    const t = setup();
    await t.mode.start();
    await flush();
    let refuseOld: (err: unknown) => void = () => undefined;
    t.bridge.localAudio.mockImplementationOnce(
      () =>
        new Promise<undefined>((_resolve, reject) => {
          refuseOld = reject;
        }),
    );
    t.mic.onChunk?.(new Uint8Array(8));
    await t.mode.toggle();
    t.bridge.localListen.mockResolvedValueOnce({ id: 'talk-2' });
    await t.mode.toggle();
    await flush();

    refuseOld(new Error('Scout is not listening.'));
    await flush();

    expect(t.mode.state()).toBe('listening');
    expect(t.mode.error()).toBeNull();
  });

  it('the conversation opens the microphone with automatic gain off, keeping echo cancellation and noise suppression', async () => {
    const getUserMedia = vi.fn(async () => ({ getTracks: () => [] }) as unknown as MediaStream);
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia },
    });
    const t = setup();
    await t.mode.start();
    await flush();

    await t.mic.source?.();

    expect(getUserMedia).toHaveBeenCalledExactlyOnceWith({
      audio: {
        channelCount: 1,
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: false,
      },
    });
  });
  it('the conversation opens the microphone chosen in Settings, with automatic gain still off', async () => {
    localStorage.setItem(MICROPHONE_STORAGE_KEY, 'usb-mic');
    const getUserMedia = vi.fn(async () => ({ getTracks: () => [] }) as unknown as MediaStream);
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia },
    });
    const t = setup();
    await t.mode.start();
    await flush();

    await t.mic.source?.();

    expect(getUserMedia).toHaveBeenCalledExactlyOnceWith({
      audio: {
        channelCount: 1,
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: false,
        deviceId: { exact: 'usb-mic' },
      },
    });
  });

  it.each([
    ['NotAllowedError', 'Scout was not allowed to use the microphone.'],
    ['NotFoundError', 'No microphone was found.'],
    ['NotReadableError', 'The microphone is busy: another program is using it.'],
  ])(
    'a microphone that fails with %s turns the conversation off with its own sentence on the button',
    async (name, sentence) => {
      const t = setup();
      t.mic.start.mockRejectedValueOnce(new DOMException('failed', name));

      await t.mode.start();
      await flush();

      expect(t.mode.error()).toBe(sentence);
      expect(t.mode.state()).toBe('off');
      expect(t.bridge.localStopListening).toHaveBeenCalledExactlyOnceWith({ id: 'talk-1' });
    },
  );

  it('any other microphone failure keeps the general sentence', async () => {
    const t = setup();
    t.mic.start.mockRejectedValueOnce(new Error('boom'));

    await t.mode.start();
    await flush();

    expect(t.mode.error()).toBe('Scout could not use the microphone.');
  });
});
