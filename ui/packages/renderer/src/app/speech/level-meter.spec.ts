import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { NoticeService } from '../core/notice.service';
import { WavRecorder, toPcm16 } from '../quick-ask/wav-recorder';
import { AudioDevices } from './audio-devices';
import { CONVERSATION_MICROPHONE_CONSTRAINTS } from './conversation-mode';
import {
  MICROPHONE_TEST_LIMIT_MS,
  MicrophoneTest,
  ROOM_CAP,
  ROOM_FACTOR,
  SPEECH_RMS_FLOOR,
  frameLevels,
  speechThreshold,
} from './level-meter';

const SEGMENTER = '../../../../../../src/Scout.Transcription.Whisper/LiveUtteranceSegmenter.cs';

/** The worker's segmenter, resolved from this spec's own location as styles.spec.ts resolves its files. */
function segmenterSource(relative: string = SEGMENTER): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

function workerConstant(source: string, name: string): number {
  const match = new RegExp(`const float ${name} = ([0-9.]+)f;`).exec(source);
  expect(match, `${name} is a float constant of LiveUtteranceSegmenter`).not.toBeNull();
  return Number(match![1]);
}

describe('the speech threshold the meter marks', () => {
  it("uses the worker's own constants: the floor, the room factor and the room cap of LiveUtteranceSegmenter", () => {
    const source = segmenterSource();

    expect({
      floor: SPEECH_RMS_FLOOR,
      factor: ROOM_FACTOR,
      cap: ROOM_CAP,
    }).toEqual({
      floor: workerConstant(source, 'RmsThreshold'),
      factor: workerConstant(source, 'RoomFactor'),
      cap: workerConstant(source, 'RoomCap'),
    });
  });

  it.each([
    [0, 0.015],
    [0.004, 0.015],
    [0.01, 0.03],
    [0.05, 0.15],
    [0.3, 0.15],
  ])(
    'a room at %f puts the threshold at %f: max(0.015, 3 x the room counted as at most 0.05)',
    (room, threshold) => {
      expect(speechThreshold(room)).toBeCloseTo(threshold, 10);
    },
  );

  it('frameLevels gives the RMS of every whole 20 ms frame of a 16 kHz PCM16 chunk', () => {
    const samples = new Float32Array(700);
    samples.fill(0.5, 0, 320);
    samples.fill(0.1, 320, 640);
    samples.fill(0.9, 640);

    const levels = frameLevels(toPcm16(samples));

    expect(levels).toHaveLength(2);
    expect(levels[0]).toBeCloseTo(0.5, 3);
    expect(levels[1]).toBeCloseTo(0.1, 3);
  });
});

describe('MicrophoneTest', () => {
  const mic = {
    processor: null as { onaudioprocess: ((event: unknown) => void) | null } | null,
    closed: 0,
  };
  const tracks: { stopped: boolean }[] = [];
  const getUserMedia = vi.fn();

  class FakeAudioContext {
    readonly sampleRate = 16_000;
    readonly destination = {};
    createMediaStreamSource() {
      return { connect: () => undefined, disconnect: () => undefined };
    }
    createScriptProcessor() {
      mic.processor = { onaudioprocess: null };
      return Object.assign(mic.processor, {
        connect: () => undefined,
        disconnect: () => undefined,
      });
    }
    createGain() {
      return { gain: { value: 1 }, connect: () => undefined, disconnect: () => undefined };
    }
    async close(): Promise<void> {
      mic.closed++;
    }
  }

  function hear(level: number, chunks = 1): void {
    for (let i = 0; i < chunks; i++) {
      const input = new Float32Array(4096).fill(level);
      mic.processor?.onaudioprocess?.({ inputBuffer: { getChannelData: () => input } });
    }
  }

  function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: NoticeService, useValue: { show: vi.fn() } },
      ],
    });
    const devices = TestBed.inject(AudioDevices);
    const recorder = TestBed.runInInjectionContext(() => new WavRecorder());
    return new MicrophoneTest(recorder, () => devices.open(CONVERSATION_MICROPHONE_CONSTRAINTS));
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    mic.processor = null;
    mic.closed = 0;
    tracks.length = 0;
    vi.stubGlobal('AudioContext', FakeAudioContext);
    getUserMedia.mockReset();
    getUserMedia.mockImplementation(async () => {
      const track = { stopped: false, onended: null, stop: () => (track.stopped = true) };
      tracks.push(track);
      return { getTracks: () => [track] };
    });
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia },
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('opens the chosen microphone with the conversation constraints', async () => {
    localStorage.setItem('scout:audio-input', 'usb-mic');
    const test = setup();

    await test.start();

    expect(getUserMedia).toHaveBeenCalledExactlyOnceWith({
      audio: { ...CONVERSATION_MICROPHONE_CONSTRAINTS, deviceId: { exact: 'usb-mic' } },
    });
    await test.stop();
  });

  it('learns the room from the first second of sound, digital silence left out, and marks the threshold over it', async () => {
    const test = setup();
    await test.start();

    hear(0, 2);
    hear(0.01, 4);
    expect(test.room()).toBeNull();
    expect(test.threshold()).toBeNull();

    hear(0.01);
    expect(test.room()).toBeCloseTo(0.01, 3);
    expect(test.threshold()).toBeCloseTo(0.03, 3);

    hear(0.2);
    expect(test.room()).toBeCloseTo(0.01, 3);
    await test.stop();
  });

  it('the level rises at once with speech and falls back smoothly, never below what is heard', async () => {
    const test = setup();
    await test.start();

    hear(0.2);
    const speaking = test.level();
    hear(0.01);
    const after = test.level();

    expect(speaking).toBeCloseTo(0.2, 3);
    expect(after).toBeLessThan(speaking);
    expect(after).toBeGreaterThan(0.01);
    await test.stop();
  });

  it('a release stops the test and the microphone: its tracks stop and the level drops to zero', async () => {
    const test = setup();
    await test.start();
    hear(0.2);

    await test.stop();

    expect(test.running()).toBe(false);
    expect(tracks.map((t) => t.stopped)).toEqual([true]);
    expect(mic.closed).toBe(1);
    expect(test.level()).toBe(0);
  });

  it('a release before the microphone opened still stops its tracks', async () => {
    let grant: () => void = () => undefined;
    getUserMedia.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          const track = { stopped: false, onended: null, stop: () => (track.stopped = true) };
          tracks.push(track);
          grant = () => resolve({ getTracks: () => [track] });
        }),
    );
    const test = setup();

    const starting = test.start();
    await vi.waitFor(() => expect(getUserMedia).toHaveBeenCalledTimes(1));
    const stopping = test.stop();
    grant();
    await Promise.all([starting, stopping]);

    expect(test.running()).toBe(false);
    expect(tracks.map((t) => t.stopped)).toEqual([true]);
  });

  it('held for 10 s the test stops by itself and releases the microphone', async () => {
    vi.useFakeTimers();
    const test = setup();
    await test.start();

    await vi.advanceTimersByTimeAsync(MICROPHONE_TEST_LIMIT_MS - 1);
    const runningBefore = test.running();
    const stoppedBefore = tracks[0]?.stopped;
    await vi.advanceTimersByTimeAsync(1);

    expect(MICROPHONE_TEST_LIMIT_MS).toBe(10_000);
    expect([runningBefore, stoppedBefore]).toEqual([true, false]);
    expect(test.running()).toBe(false);
    expect(tracks.map((t) => t.stopped)).toEqual([true]);
  });

  it('a second press while it runs opens nothing more', async () => {
    const test = setup();
    await test.start();
    await test.start();

    expect(getUserMedia).toHaveBeenCalledTimes(1);
    await test.stop();
  });

  it.each([
    ['NotAllowedError', 'Scout was not allowed to use the microphone.'],
    ['NotFoundError', 'No microphone was found.'],
    ['NotReadableError', 'The microphone is busy: another program is using it.'],
  ])(
    'a microphone that fails with %s stops the test with its own sentence',
    async (name, sentence) => {
      getUserMedia.mockRejectedValue(new DOMException('failed', name));
      const test = setup();

      await test.start();

      expect(test.error()).toBe(sentence);
      expect(test.running()).toBe(false);
    },
  );

  it('any other failure stops the test with the general sentence, and a new press clears it', async () => {
    getUserMedia.mockRejectedValueOnce(new Error('boom'));
    const test = setup();

    await test.start();
    expect(test.error()).toBe('Scout could not use the microphone.');

    await test.start();
    expect(test.error()).toBeNull();
    await test.stop();
  });

  it('a microphone unplugged during the test stops it and says so', async () => {
    const test = setup();
    await test.start();

    (tracks[0] as unknown as { onended: () => void }).onended();
    await vi.waitFor(() => expect(test.running()).toBe(false));

    expect(test.error()).toBe('The microphone was disconnected.');
  });
});
