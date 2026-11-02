import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { MAX_SAMPLES, WavRecorder, encodeWav, toBase64 } from './wav-recorder';

describe('encodeWav', () => {
  it('writes a 44-byte PCM header and clamps samples to 16 bits', () => {
    const wav = encodeWav([Float32Array.from([1, -1, 2])], 16_000);
    const view = new DataView(wav.buffer);

    expect(wav.length).toBe(44 + 6);
    expect(view.getUint32(4, true)).toBe(36 + 6);
    expect(view.getUint32(28, true)).toBe(32_000);
    expect([view.getInt16(44, true), view.getInt16(46, true), view.getInt16(48, true)]).toEqual([
      32767, -32768, 32767,
    ]);
  });

  it('base64-encodes a recording larger than one chunk', () => {
    const bytes = new Uint8Array(0x8000 * 2 + 3).map((_, i) => i % 251);

    expect(Uint8Array.from(atob(toBase64(bytes)), (c) => c.charCodeAt(0))).toEqual(bytes);
  });
});

describe('WavRecorder', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function fakeAudio() {
    const state = { contexts: 0, closed: 0 };
    vi.stubGlobal(
      'AudioContext',
      class {
        readonly sampleRate = 16_000;
        readonly destination = {};
        constructor() {
          state.contexts++;
        }
        createMediaStreamSource() {
          return { connect: () => undefined, disconnect: () => undefined };
        }
        createScriptProcessor() {
          return { onaudioprocess: null, connect: () => undefined, disconnect: () => undefined };
        }
        createGain() {
          return { gain: { value: 1 }, connect: () => undefined, disconnect: () => undefined };
        }
        async close() {
          state.closed++;
        }
      },
    );
    return state;
  }

  function track() {
    const t = { stopped: false, stop: () => (t.stopped = true) };
    return t;
  }

  it('a stop before the microphone opens still stops the stream and closes the context, and records nothing', async () => {
    const audio = fakeAudio();
    const mic = track();
    let grant: (stream: unknown) => void = () => undefined;
    const getUserMedia = vi.fn(() => new Promise((resolve) => (grant = resolve)));
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia },
    });
    const recorder = TestBed.inject(WavRecorder);

    const starting = recorder.start(() => undefined);
    const stopping = recorder.stop();
    await vi.waitFor(() => expect(getUserMedia).toHaveBeenCalledTimes(1));
    grant({ getTracks: () => [mic] });
    await starting;
    const wav = await stopping;

    expect(wav).toBeNull();
    expect(mic.stopped).toBe(true);
    expect(audio.closed).toBe(audio.contexts);
  });

  it('a new start releases a microphone that was never stopped', async () => {
    const audio = fakeAudio();
    const first = track();
    const second = track();
    const streams = [first, second];
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia: async () => ({ getTracks: () => [streams.shift()!] }) },
    });
    const recorder = TestBed.inject(WavRecorder);

    await recorder.start(() => undefined);
    await recorder.start(() => undefined);

    expect(first.stopped).toBe(true);
    expect(second.stopped).toBe(false);
    expect(audio.closed).toBe(1);
  });

  it('the microphone is never played back: the processor reaches the destination through a muted gain node', async () => {
    const links: [string, string][] = [];
    const node = <T extends object>(name: string, extra: T) =>
      Object.assign(
        {
          name,
          connect: (target: { name: string }) => links.push([name, target.name]),
          disconnect: () => undefined,
        },
        extra,
      );
    const destination = { name: 'speakers' };
    const gain = node('gain', { gain: { value: 1 } });
    const processor = node('processor', { onaudioprocess: null });
    vi.stubGlobal(
      'AudioContext',
      class {
        readonly sampleRate = 16_000;
        readonly destination = destination;
        createMediaStreamSource() {
          return node('source', {});
        }
        createScriptProcessor() {
          return processor;
        }
        createGain() {
          return gain;
        }
        async close() {}
      },
    );
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia: async () => ({ getTracks: () => [] }) },
    });
    const recorder = TestBed.inject(WavRecorder);

    await recorder.start(() => undefined);

    expect(links).toEqual([
      ['source', 'processor'],
      ['processor', 'gain'],
      ['gain', 'speakers'],
    ]);
    expect(gain.gain.value).toBe(0);
    await recorder.stop();
  });

  it('stops at 5 minutes of audio and tells the caller', async () => {
    let processor: { onaudioprocess: ((event: unknown) => void) | null } | null = null;
    vi.stubGlobal(
      'AudioContext',
      class {
        readonly sampleRate = 16_000;
        readonly destination = {};
        createMediaStreamSource() {
          return { connect: () => undefined, disconnect: () => undefined };
        }
        createScriptProcessor() {
          processor = {
            onaudioprocess: null,
            connect: () => undefined,
            disconnect: () => undefined,
          } as never;
          return processor;
        }
        createGain() {
          return { gain: { value: 1 }, connect: () => undefined, disconnect: () => undefined };
        }
        async close() {}
      },
    );
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia: async () => ({ getTracks: () => [] }) },
    });
    const recorder = TestBed.inject(WavRecorder);
    const onLimit = vi.fn();

    await recorder.start(onLimit);
    const block = new Float32Array(MAX_SAMPLES - 10);
    processor!.onaudioprocess!({ inputBuffer: { getChannelData: () => block } });
    processor!.onaudioprocess!({ inputBuffer: { getChannelData: () => new Float32Array(4096) } });
    processor!.onaudioprocess!({ inputBuffer: { getChannelData: () => new Float32Array(4096) } });
    const wav = await recorder.stop();

    expect(onLimit).toHaveBeenCalledTimes(1);
    expect(new DataView(wav!.buffer).getUint32(40, true)).toBe(5 * 60 * 16_000 * 2);
  });
});
