import { InjectionToken, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { NoticeService } from '../core/notice.service';
import { SAMPLE_PLAYER } from '../models/speech-voices.component';
import { WebAudioPlayer } from './audio-player';
import { SPEAKER_STORAGE_KEY } from './audio-devices';
import { SPEECH_PLAYER } from './read-aloud';
import type { SpeechPlayer } from './speech-queue';

function fakeContext() {
  const decoded: number[][] = [];
  const started: string[] = [];
  let finish: (() => void) | null = null;
  let release: (() => void) | null = null;
  const context = {
    state: 'running',
    destination: {},
    decodeAudioData: (buffer: ArrayBuffer) => {
      decoded.push(Array.from(new Uint8Array(buffer)));
      return new Promise((resolve) => {
        release = () => resolve({ label: 'buffer' });
      });
    },
    createBufferSource: () => {
      const source = {
        buffer: null as unknown,
        onended: null as (() => void) | null,
        connect: () => undefined,
        start: () => {
          started.push('start');
          finish = () => source.onended?.();
        },
        stop: () => source.onended?.(),
      };
      return source;
    },
  };
  return {
    context: context as unknown as AudioContext,
    decoded,
    started,
    release: () => release?.(),
    finish: () => finish?.(),
  };
}

const flush = async (): Promise<void> => {
  for (let i = 0; i < 5; i++) await Promise.resolve();
};

describe('WebAudioPlayer', () => {
  it('decodes the base64 bytes exactly, plays them and settles when the sound ends', async () => {
    const fake = fakeContext();
    const player = new WebAudioPlayer(() => fake.context);
    let ended = false;

    const playing = player
      .play({ contentType: 'audio/wav', base64: btoa('RIFF' + String.fromCharCode(0, 255, 128)) })
      .then(() => {
        ended = true;
      });
    await flush();
    fake.release();
    await flush();
    const endedBeforeFinish = ended;
    fake.finish();
    await playing;

    expect(fake.decoded).toEqual([[82, 73, 70, 70, 0, 255, 128]]);
    expect(fake.started).toEqual(['start']);
    expect([endedBeforeFinish, ended]).toEqual([false, true]);
  });

  it('a stop while the sound is still decoding means it never starts', async () => {
    const fake = fakeContext();
    const player = new WebAudioPlayer(() => fake.context);

    const playing = player.play({ contentType: 'audio/wav', base64: btoa('RIFF') });
    await flush();
    player.stop();
    fake.release();
    await playing;

    expect(fake.started).toEqual([]);
  });
});

describe('WebAudioPlayer: the speaker chosen in Settings', () => {
  const log: string[] = [];
  let refuse = new Set<string>();

  class RoutedContext {
    state = 'running';
    destination = {};
    sinkId = '';
    async setSinkId(id: string): Promise<void> {
      log.push(`sink:${id}`);
      if (refuse.has(id)) throw new DOMException('gone', 'NotFoundError');
      this.sinkId = id;
    }
    async decodeAudioData(): Promise<unknown> {
      return { label: 'buffer' };
    }
    createBufferSource() {
      const source = {
        buffer: null as unknown,
        onended: null as (() => void) | null,
        connect: () => undefined,
        start: () => {
          log.push('start');
          queueMicrotask(() => source.onended?.());
        },
        stop: () => undefined,
      };
      return source;
    }
  }

  function setup(token: InjectionToken<SpeechPlayer> = SPEECH_PLAYER) {
    const notice = { show: vi.fn() };
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: NoticeService, useValue: notice }],
    });
    return { notice, player: TestBed.inject(token) };
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    log.length = 0;
    refuse = new Set();
    vi.stubGlobal('AudioContext', RoutedContext);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('a sentence read aloud plays on the chosen speaker: the output is set to it before the sound starts', async () => {
    localStorage.setItem(SPEAKER_STORAGE_KEY, 'headphones');
    const { player } = setup();

    await player.play({ contentType: 'audio/wav', base64: btoa('RIFF') });

    expect(log).toEqual(['sink:headphones', 'start']);
  });

  it('a voice sample on the Models screen plays on the chosen speaker too', async () => {
    localStorage.setItem(SPEAKER_STORAGE_KEY, 'headphones');
    const { player } = setup(SAMPLE_PLAYER);

    await player.play({ contentType: 'audio/wav', base64: btoa('RIFF') });

    expect(log).toEqual(['sink:headphones', 'start']);
  });

  it('a speaker chosen between two sentences is the one the next sentence plays on', async () => {
    const { player } = setup();
    await player.play({ contentType: 'audio/wav', base64: btoa('RIFF') });

    localStorage.setItem(SPEAKER_STORAGE_KEY, 'headphones');
    await player.play({ contentType: 'audio/wav', base64: btoa('RIFF') });

    expect(log).toEqual(['start', 'sink:headphones', 'start']);
  });

  it('a chosen speaker that is not connected falls back to the default one, says so once, and the sentence still plays', async () => {
    localStorage.setItem(SPEAKER_STORAGE_KEY, 'unplugged');
    refuse = new Set(['unplugged']);
    const { player, notice } = setup();

    await player.play({ contentType: 'audio/wav', base64: btoa('RIFF') });
    await player.play({ contentType: 'audio/wav', base64: btoa('RIFF') });

    expect(log).toEqual(['sink:unplugged', 'sink:', 'start', 'start']);
    expect(notice.show).toHaveBeenCalledExactlyOnceWith(
      'The chosen speaker is not connected; Scout uses the default one.',
    );
  });
  it('where the context cannot choose its output, a sentence with a speaker chosen still plays, on the default one', async () => {
    localStorage.setItem(SPEAKER_STORAGE_KEY, 'headphones');
    vi.stubGlobal(
      'AudioContext',
      class extends RoutedContext {
        override setSinkId = undefined as unknown as RoutedContext['setSinkId'];
      },
    );
    const { player, notice } = setup();

    await player.play({ contentType: 'audio/wav', base64: btoa('RIFF') });

    expect(log).toEqual(['start']);
    expect(notice.show).not.toHaveBeenCalled();
  });
});
