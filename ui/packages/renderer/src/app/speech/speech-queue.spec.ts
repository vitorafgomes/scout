import { describe, expect, it } from 'vitest';

import type { LocalSpeakResponse } from '@scout/shared';

import { SpeechQueue, type SpeechPlayer } from './speech-queue';

interface Deferred<T> {
  readonly promise: Promise<T>;
  resolve(value: T): void;
  reject(err: unknown): void;
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (err: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const audioOf = (text: string): LocalSpeakResponse => ({ contentType: 'audio/wav', base64: text });

const flush = async (): Promise<void> => {
  for (let i = 0; i < 10; i++) await Promise.resolve();
};

function setup({ stopEndsPlay = true } = {}) {
  const made = new Map<string, Deferred<LocalSpeakResponse>>();
  const asked: string[] = [];
  const played: string[] = [];
  const playing = new Map<string, Deferred<void>>();
  let stops = 0;
  const player: SpeechPlayer = {
    play(audio) {
      played.push(audio.base64);
      const end = deferred<void>();
      playing.set(audio.base64, end);
      return end.promise;
    },
    stop() {
      stops += 1;
      if (stopEndsPlay) for (const end of playing.values()) end.resolve();
    },
  };
  const queue = new SpeechQueue((text) => {
    asked.push(text);
    const d = deferred<LocalSpeakResponse>();
    made.set(text, d);
    return d.promise;
  }, player);
  return {
    queue,
    asked,
    played,
    made: (text: string) => made.get(text)!,
    end: (text: string) => playing.get(text)!.resolve(),
    failPlaying: (text: string, err: unknown) => playing.get(text)!.reject(err),
    stops: () => stops,
  };
}

describe('SpeechQueue: sentences read aloud in order, and stop', () => {
  it('plays each sentence in the order given, the next made while the one before plays', async () => {
    const t = setup();

    t.queue.enqueue('One.');
    t.queue.enqueue('Two.');
    t.queue.enqueue('Three.');
    await flush();
    expect(t.asked).toEqual(['One.']);
    t.made('One.').resolve(audioOf('One.'));
    await flush();
    expect(t.played).toEqual(['One.']);
    expect(t.queue.playing()).toBe(true);
    expect(t.asked).toEqual(['One.', 'Two.']);
    t.made('Two.').resolve(audioOf('Two.'));
    await flush();
    expect(t.played).toEqual(['One.']);
    t.end('One.');
    await flush();
    expect(t.played).toEqual(['One.', 'Two.']);
    t.made('Three.').resolve(audioOf('Three.'));
    t.end('Two.');
    await flush();
    t.end('Three.');
    await flush();

    expect(t.played).toEqual(['One.', 'Two.', 'Three.']);
    expect(t.queue.idle()).toBe(true);
    expect(t.queue.playing()).toBe(false);
  });

  it.each([
    ['while a sentence is still being made', false],
    ['while a sentence plays', true],
  ])('stop %s drops the rest and nothing more plays', async (_name, startedPlaying) => {
    const t = setup();
    t.queue.enqueue('One.');
    t.queue.enqueue('Two.');
    await flush();
    if (startedPlaying) {
      t.made('One.').resolve(audioOf('One.'));
      await flush();
    }

    t.queue.stop();
    if (!startedPlaying) t.made('One.').resolve(audioOf('One.'));
    t.made('Two.')?.resolve(audioOf('Two.'));
    await flush();

    expect(t.played).toEqual(startedPlaying ? ['One.'] : []);
    expect(t.stops()).toBe(1);
    expect(t.queue.idle()).toBe(true);
    expect(t.queue.playing()).toBe(false);
  });

  it('with a slow player at most one sentence is made ahead of the one playing, and a stop after the first leaves one extra made', async () => {
    const t = setup();
    for (const text of ['One.', 'Two.', 'Three.', 'Four.']) t.queue.enqueue(text);
    await flush();
    t.made('One.').resolve(audioOf('One.'));
    await flush();
    t.made('Two.').resolve(audioOf('Two.'));
    await flush();
    const whileFirstPlays = [...t.asked];

    t.queue.stop();
    await flush();

    expect(t.played).toEqual(['One.']);
    expect(whileFirstPlays).toEqual(['One.', 'Two.']);
    expect(t.asked).toEqual(['One.', 'Two.']);
  });

  it('a sentence the voice could not make stops the queue and leaves the reason', async () => {
    const t = setup();
    t.queue.enqueue('One.');
    t.queue.enqueue('Two.');
    await flush();

    t.made('One.').reject(new Error('[scout:local/speak-failed] No voice: connect an AI Source.'));
    await flush();

    expect(t.queue.error()).toBe('No voice: connect an AI Source.');
    expect(t.played).toEqual([]);
    expect(t.asked).toEqual(['One.']);
    expect(t.queue.idle()).toBe(true);
  });

  it('a sentence that could not be played stops the queue and leaves the reason, as one the voice could not make', async () => {
    const t = setup();
    t.queue.enqueue('One.');
    t.queue.enqueue('Two.');
    await flush();
    t.made('One.').resolve(audioOf('One.'));
    await flush();
    t.made('Two.').resolve(audioOf('Two.'));
    await flush();

    t.failPlaying('One.', new Error('The audio output could not start.'));
    await flush();

    expect(t.queue.error()).toBe('The audio output could not start.');
    expect(t.played).toEqual(['One.']);
    expect(t.queue.idle()).toBe(true);
    expect(t.queue.playing()).toBe(false);
  });

  it('a sentence whose playback fails after stop leaves no reason', async () => {
    const t = setup({ stopEndsPlay: false });
    t.queue.enqueue('One.');
    await flush();
    t.made('One.').resolve(audioOf('One.'));
    await flush();

    t.queue.stop();
    t.failPlaying('One.', new Error('The audio output could not start.'));
    await flush();

    expect(t.queue.error()).toBeNull();
    expect(t.played).toEqual(['One.']);
    expect(t.queue.idle()).toBe(true);
  });
});
