import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalAskRequest, LocalTurn } from '@scout/shared';

import { LocalConversation } from '../core/local-conversation';
import { ScoutBridgeService } from '../core/scout-bridge';
import { CONVERSATION_RECORDER, ConversationMode } from './conversation-mode';
import { READ_ALOUD_STORAGE_KEY, ReadAloud, SPEECH_PLAYER } from './read-aloud';
import type { SpeechPlayer } from './speech-queue';

const flush = async (): Promise<void> => {
  for (let i = 0; i < 20; i++) {
    TestBed.tick();
    await Promise.resolve();
  }
};

function makeBridge(initial: readonly LocalTurn[] = []) {
  let turns = initial;
  const listeners: ((turns: readonly LocalTurn[]) => void)[] = [];
  const push = (next: readonly LocalTurn[]) => {
    turns = next;
    listeners.forEach((listener) => listener(turns));
  };
  return {
    push,
    reply: (id: string, reply: string, state: LocalTurn['state']) =>
      push(turns.map((turn) => (turn.id === id ? { ...turn, reply, state } : turn))),
    onLocalTurns: vi.fn((listener: (turns: readonly LocalTurn[]) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
    localGetTurns: vi.fn(async () => turns),
    localAsk: vi.fn(async (req: LocalAskRequest) =>
      push([...turns, { id: req.id, question: '', reply: '', citations: [], state: 'running' }]),
    ),
    onLocalTranscript: vi.fn(() => () => undefined),
    localStopListening: vi.fn(async () => undefined),
    localSpeak: vi.fn(async (req: { text: string }) => ({
      contentType: 'audio/wav',
      base64: req.text,
    })),
  };
}

const done = (id: string, reply: string): LocalTurn => ({
  id,
  question: 'q',
  reply,
  citations: [],
  state: 'done',
});

function setup(initial: readonly LocalTurn[] = []) {
  const bridge = makeBridge(initial);
  const ends: (() => void)[] = [];
  const player = {
    played: [] as string[],
    play: vi.fn((audio: { base64: string }) => {
      player.played.push(audio.base64);
      return new Promise<void>((resolve) => ends.push(resolve));
    }),
    stop: vi.fn(() => ends.splice(0).forEach((end) => end())),
  };
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: SPEECH_PLAYER, useValue: player as unknown as SpeechPlayer },
      {
        provide: CONVERSATION_RECORDER,
        useValue: { start: vi.fn(), stop: vi.fn(async () => null) },
      },
    ],
  });
  const readAloud = TestBed.inject(ReadAloud);
  const conversation = TestBed.inject(LocalConversation);
  const endPlaying = async () => {
    ends.splice(0).forEach((end) => end());
    await flush();
  };
  return { bridge, player, readAloud, conversation, endPlaying };
}

describe('ReadAloud', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });
  afterEach(() => vi.restoreAllMocks());

  it('with "Read answers aloud" on, the reply to a question this window asked is read sentence by sentence as it streams, skipping code', async () => {
    const t = setup();
    t.readAloud.setEnabled(true);

    await t.conversation.ask('Is main green?');
    await flush();
    const id = t.bridge.localAsk.mock.calls[0]![0].id;
    t.bridge.reply(id, 'The build is green. Run', 'running');
    await flush();
    const first = [...t.player.played];
    t.bridge.reply(id, 'The build is green. Run:\n```sh\nnpm test.\n```\nThen push.', 'done');
    for (let i = 0; i < 3; i++) await t.endPlaying();

    expect(first).toEqual(['The build is green.']);
    expect(t.player.played).toEqual(['The build is green.', 'Run:', 'Then push.']);
    expect(t.readAloud.speakingId()).toBeNull();
  });

  it('with the switch off a reply is not read until its speaker is pressed, and a second press stops it at once', async () => {
    const t = setup([done('t-1', 'One. Two. Three.')]);
    await flush();
    const silent = [...t.player.played];

    t.readAloud.toggle('t-1');
    await flush();
    const reading = t.readAloud.speakingId();
    t.readAloud.toggle('t-1');
    await t.endPlaying();

    expect(silent).toEqual([]);
    expect(reading).toBe('t-1');
    expect(t.player.played).toEqual(['One.']);
    expect(t.player.stop).toHaveBeenCalled();
    expect(t.readAloud.speakingId()).toBeNull();
  });

  it('Esc stops what is being read and answers that something was', async () => {
    const t = setup([done('t-1', 'One. Two.')]);
    const mode = TestBed.inject(ConversationMode);
    await flush();
    t.readAloud.toggle('t-1');
    await flush();

    const handled = mode.escape();
    await t.endPlaying();

    expect(handled).toBe(true);
    expect(t.player.played).toEqual(['One.']);
    expect(t.readAloud.speakingId()).toBeNull();
    expect(mode.escape()).toBe(false);
  });

  it('a voice that cannot read says why', async () => {
    const t = setup([done('t-1', 'One.')]);
    t.bridge.localSpeak.mockRejectedValueOnce(
      new Error('[scout:local/speak-failed] No voice: connect an AI Source with speech.'),
    );
    await flush();

    t.readAloud.toggle('t-1');
    await flush();

    expect(t.readAloud.error()).toBe('No voice: connect an AI Source with speech.');
    expect(t.readAloud.speakingId()).toBeNull();
  });

  it.each([
    ['stored on', 'on', true],
    ['stored off', 'off', false],
    ['never stored', null, false],
  ])('the switch starts as %s and is kept when changed', (_name, stored, expected) => {
    if (stored !== null) localStorage.setItem(READ_ALOUD_STORAGE_KEY, stored);
    const t = setup();
    const initial = t.readAloud.enabled();

    t.readAloud.setEnabled(!initial);

    expect(initial).toBe(expected);
    expect(localStorage.getItem(READ_ALOUD_STORAGE_KEY)).toBe(initial ? 'off' : 'on');
  });

  it('storage that throws on read and on write leaves the switch off and still lets it turn on', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    const t = setup();
    const initial = t.readAloud.enabled();

    t.readAloud.setEnabled(true);

    expect([initial, t.readAloud.enabled()]).toEqual([false, true]);
  });
});
