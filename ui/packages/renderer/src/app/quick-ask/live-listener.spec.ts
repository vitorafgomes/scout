import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalTranscriptEvent } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { LiveListener } from './live-listener';
import { TranscriptStore } from './transcript-store';

const NO_MODEL =
  'Live transcription needs a Whisper model. Download one in the Scout app under AI Sources, Local Models.';

function makeBridge() {
  const listeners: ((event: LocalTranscriptEvent) => void)[] = [];
  return {
    hear: (event: LocalTranscriptEvent) => listeners.forEach((listener) => listener(event)),
    onLocalTranscript: vi.fn((listener: (event: LocalTranscriptEvent) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
    localListen: vi.fn(async (_req?: { lane?: string }) => ({ id: 'live-1' })),
    localAudio: vi.fn(async (_req: { id: string; pcm16: string }) => undefined),
    localStopListening: vi.fn(async (_req: { id: string }) => undefined),
  };
}

const mic = {
  processor: null as { onaudioprocess: ((event: unknown) => void) | null } | null,
  sampleRate: 16_000,
  stopped: false,
  closed: false,
};

class FakeAudioContext {
  readonly sampleRate = mic.sampleRate;
  readonly destination = {};
  createMediaStreamSource() {
    return { connect: vi.fn(), disconnect: vi.fn() };
  }
  createScriptProcessor() {
    mic.processor = { onaudioprocess: null };
    return Object.assign(mic.processor, { connect: vi.fn(), disconnect: vi.fn() });
  }
  createGain() {
    return { gain: { value: 1 }, connect: vi.fn(), disconnect: vi.fn() };
  }
  async close(): Promise<void> {
    mic.closed = true;
  }
}

function setup(bridge = makeBridge()) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  return { bridge, live: TestBed.inject(LiveListener) };
}

const partial = (text: string): LocalTranscriptEvent => ({
  id: 'live-1',
  lane: 'me',
  text,
  final: false,
});
const final = (text: string): LocalTranscriptEvent => ({
  id: 'live-1',
  lane: 'me',
  text,
  final: true,
});

describe('LiveListener', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    mic.processor = null;
    mic.sampleRate = 16_000;
    mic.stopped = false;
    mic.closed = false;
    vi.stubGlobal('AudioContext', FakeAudioContext);
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: {
        getUserMedia: vi.fn(async () => ({
          getTracks: () => [{ stop: () => (mic.stopped = true) }],
        })),
      },
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('finals accumulate and the partial replaces', async () => {
    const { bridge, live } = setup();
    await live.listen();
    const seen: string[] = [];

    for (const event of [
      partial('is the'),
      partial('is the nightly'),
      final('Is the nightly green?'),
      partial('who'),
      { id: 'someone-else', lane: 'me' as const, text: 'not ours', final: true },
      final('Who broke it?'),
    ]) {
      bridge.hear(event);
      seen.push(live.text());
    }

    expect(live.listening()).toBe(true);
    expect(seen).toEqual([
      'is the',
      'is the nightly',
      'Is the nightly green?',
      'Is the nightly green? who',
      'Is the nightly green? who',
      'Is the nightly green? Who broke it?',
    ]);
  });

  it('a translation goes beside its line in the transcript and never into the heard text, and a seq 0 note to the panel', async () => {
    const { bridge, live } = setup();
    await live.listen('me', 'system');

    bridge.hear({ id: 'live-1', lane: 'me', text: 'Bom dia.', final: true, seq: 1 });
    bridge.hear({
      kind: 'translation',
      id: 'live-1',
      lane: 'me',
      seq: 1,
      language: 'en-US',
      text: 'Good morning.',
    });
    bridge.hear({
      kind: 'translation',
      id: 'live-1',
      lane: 'me',
      seq: 0,
      language: 'en-US',
      error: 'Translation paused: more than 20 lines a minute.',
    });
    const store = TestBed.inject(TranscriptStore);

    expect(bridge.localListen).toHaveBeenCalledExactlyOnceWith({ lane: 'me', translate: 'system' });
    expect(live.text()).toBe('Bom dia.');
    expect(store.lines().map((line) => [line.text, line.translation?.text])).toEqual([
      ['Bom dia.', 'Good morning.'],
    ]);
    expect(store.notes()).toEqual(['Translation paused: more than 20 lines a minute.']);
  });

  it('each microphone buffer goes to the worker as base64 PCM16 under the session id', async () => {
    const { bridge, live } = setup();
    await live.listen();

    mic.processor!.onaudioprocess!({
      inputBuffer: { getChannelData: () => Float32Array.from([0.5, -0.5]) },
    });

    expect(bridge.localAudio).toHaveBeenCalledExactlyOnceWith({ id: 'live-1', pcm16: '/z8AwA==' });
  });

  it('a fail event shows its sentence and stops', async () => {
    const { bridge, live } = setup();
    await live.listen();

    bridge.hear({ id: 'live-1', lane: 'me', text: '', final: true, ended: true, error: NO_MODEL });
    await vi.waitFor(() => expect(mic.closed).toBe(true));

    expect(live.error()).toBe(NO_MODEL);
    expect(live.listening()).toBe(false);
    expect(mic.stopped).toBe(true);
  });

  it('an ended event from a worker exit shows "Scout stopped listening."', async () => {
    const { bridge, live } = setup();
    await live.listen();

    bridge.hear({
      id: 'live-1',
      lane: 'me',
      text: '',
      final: true,
      ended: true,
      error: 'Scout stopped listening.',
    });
    await vi.waitFor(() => expect(mic.closed).toBe(true));

    expect(live.error()).toBe('Scout stopped listening.');
    expect(live.listening()).toBe(false);
  });

  it('stop releases the microphone, ends the session in main and keeps the text, taking the final that arrives after it', async () => {
    const { bridge, live } = setup();
    await live.listen();
    bridge.hear(partial('deploy the'));

    await live.stop();
    bridge.hear(final('Deploy the hotfix.'));
    bridge.hear({ id: 'live-1', lane: 'me', text: '', final: true, ended: true });
    bridge.hear(final('after the end'));

    expect(mic.stopped).toBe(true);
    expect(bridge.localStopListening).toHaveBeenCalledExactlyOnceWith({ id: 'live-1' });
    expect(live.listening()).toBe(false);
    expect(live.text()).toBe('Deploy the hotfix.');
    expect(live.error()).toBeNull();
  });

  it("a refused listen shows main's sentence and opens no microphone", async () => {
    const bridge = makeBridge();
    bridge.localListen.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:local.listen': Error: [scout:local/not-running] Scout is not running. Start it from the tray or the app.",
      ),
    );
    const { live } = setup(bridge);

    await live.listen();

    expect(live.error()).toBe('Scout is not running. Start it from the tray or the app.');
    expect(live.listening()).toBe(false);
    expect(navigator.mediaDevices.getUserMedia).not.toHaveBeenCalled();
  });

  it('a microphone that does not open at 16 kHz shows its sentence and ends the session in main', async () => {
    mic.sampleRate = 48_000;
    const { bridge, live } = setup();

    await live.listen();

    expect(live.error()).toBe('Scout could not open the microphone at 16 kHz.');
    expect(live.listening()).toBe(false);
    expect(mic.stopped).toBe(true);
    expect(bridge.localStopListening).toHaveBeenCalledExactlyOnceWith({ id: 'live-1' });
  });

  it('a stop before main answers ends that session as soon as main names it, with no microphone', async () => {
    const bridge = makeBridge();
    let answer: (value: { id: string }) => void = () => undefined;
    bridge.localListen.mockImplementationOnce(() => new Promise((resolve) => (answer = resolve)));
    const { live } = setup(bridge);

    const listening = live.listen();
    await live.stop();
    answer({ id: 'live-1' });
    await listening;

    expect(bridge.localStopListening).toHaveBeenCalledExactlyOnceWith({ id: 'live-1' });
    expect(navigator.mediaDevices.getUserMedia).not.toHaveBeenCalled();
    expect(live.listening()).toBe(false);
  });
});

describe('LiveListener: two lanes', () => {
  const processors: { onaudioprocess: ((event: unknown) => void) | null }[] = [];
  const tracks = { video: 'live', audio: 'live', microphone: 'live' };
  let audioTracks: number;
  const getDisplayMedia = vi.fn();

  class TwoLaneAudioContext {
    readonly sampleRate = 16_000;
    readonly destination = {};
    createMediaStreamSource() {
      return { connect: vi.fn(), disconnect: vi.fn() };
    }
    createScriptProcessor() {
      const processor = { onaudioprocess: null, connect: vi.fn(), disconnect: vi.fn() };
      processors.push(processor);
      return processor;
    }
    createGain() {
      return { gain: { value: 1 }, connect: vi.fn(), disconnect: vi.fn() };
    }
    async close(): Promise<void> {}
  }

  function hear(processor: (typeof processors)[number], samples: Float32Array): void {
    processor.onaudioprocess?.({ inputBuffer: { getChannelData: () => samples } });
  }

  function systemAudio() {
    const video = { kind: 'video', stop: () => (tracks.video = 'stopped') };
    const audio = { kind: 'audio', stop: () => (tracks.audio = 'stopped') };
    const audios = audioTracks > 0 ? [audio] : [];
    return {
      getVideoTracks: () => [video],
      getAudioTracks: () => audios,
      getTracks: () => [video, ...audios],
    };
  }

  function lanesBridge() {
    const bridge = makeBridge();
    bridge.localListen.mockImplementation(async (req?: { lane?: string }) => ({
      id: req?.lane === 'others' ? 'live-others' : 'live-me',
    }));
    return bridge;
  }

  function sent(bridge: ReturnType<typeof makeBridge>, id: string): Int16Array[] {
    return bridge.localAudio.mock.calls
      .filter(([req]) => req.id === id)
      .map(([req]) => {
        const bytes = Uint8Array.from(atob(req.pcm16), (c) => c.charCodeAt(0));
        return new Int16Array(bytes.buffer);
      });
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    processors.length = 0;
    tracks.video = 'live';
    tracks.audio = 'live';
    tracks.microphone = 'live';
    audioTracks = 1;
    vi.stubGlobal('AudioContext', TwoLaneAudioContext);
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue(
      'Mozilla/5.0 (X11; Linux x86_64) Chrome/146.0 Electron/45.0',
    );
    getDisplayMedia.mockReset();
    getDisplayMedia.mockImplementation(async () => systemAudio());
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: {
        getDisplayMedia,
        getUserMedia: vi.fn(async () => ({
          getTracks: () => [{ stop: () => (tracks.microphone = 'stopped') }],
        })),
      },
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('microphone frames are zeroed while the system audio frame RMS is at least 0.015 and pass otherwise', async () => {
    const bridge = lanesBridge();
    const { live } = setup(bridge);
    await live.listen('me');
    await live.listen('others');
    const [microphone, system] = processors as [
      (typeof processors)[number],
      (typeof processors)[number],
    ];
    const level = (value: number) => new Float32Array(4096).fill(value);
    const loudThenQuietLastFrame = level(0.5).fill(0.0149, 4096 - 320);

    hear(system, level(0.0151));
    hear(microphone, level(0.5));
    hear(system, level(0.0149));
    hear(microphone, level(0.5));
    hear(system, loudThenQuietLastFrame);
    hear(microphone, level(0.5));
    await live.stop('others');
    hear(system, level(0.5));
    hear(microphone, level(0.5));

    const heard = sent(bridge, 'live-me').map((chunk) => [...new Set(chunk)]);
    expect(heard).toEqual([[0], [16383], [16383], [16383]]);
    expect(sent(bridge, 'live-others').map((chunk) => [...new Set(chunk)])).toEqual([
      [494],
      [488],
      [16383, 488],
    ]);
  });

  it('the others lane asks the system for its audio, stops the video at once and streams the audio under its own id', async () => {
    const bridge = lanesBridge();
    const { live } = setup(bridge);

    await live.listen('others');
    hear(processors[0]!, new Float32Array(4096).fill(0.5));

    expect(bridge.localListen).toHaveBeenCalledExactlyOnceWith({ lane: 'others' });
    expect(getDisplayMedia).toHaveBeenCalledExactlyOnceWith({ video: true, audio: true });
    expect(tracks).toEqual({ video: 'stopped', audio: 'live', microphone: 'live' });
    expect(sent(bridge, 'live-others')).toHaveLength(1);
    expect(live.othersListening()).toBe(true);
    expect(live.listening()).toBe(false);
  });

  it.each([
    [
      'a capture the system refuses',
      () =>
        getDisplayMedia.mockRejectedValueOnce(new DOMException('no capturer', 'NotReadableError')),
    ],
    ['an answer with no audio track', () => (audioTracks = 0)],
  ])(
    '%s shows "Scout could not hear the system audio." and ends only the others lane',
    async (_label, arrange) => {
      const bridge = lanesBridge();
      const { live } = setup(bridge);
      await live.listen('me');
      arrange();

      await live.listen('others');

      expect(live.error()).toBe('Scout could not hear the system audio.');
      expect(live.othersListening()).toBe(false);
      expect(live.listening()).toBe(true);
      expect(bridge.localStopListening).toHaveBeenCalledExactlyOnceWith({ id: 'live-others' });
    },
  );

  it('cancelling the system dialog ends the others lane with no error', async () => {
    const bridge = lanesBridge();
    getDisplayMedia.mockRejectedValueOnce(new DOMException('cancelled', 'NotAllowedError'));
    const { live } = setup(bridge);

    await live.listen('others');

    expect(live.error()).toBeNull();
    expect(live.othersListening()).toBe(false);
    expect(bridge.localStopListening).toHaveBeenCalledExactlyOnceWith({ id: 'live-others' });
  });

  it('finals of both lanes go to the transcript as You and Others, partials do not', async () => {
    const bridge = lanesBridge();
    const { live } = setup(bridge);
    await live.listen('me');
    await live.listen('others');

    bridge.hear({ id: 'live-others', lane: 'others', text: 'can you', final: false });
    bridge.hear({ id: 'live-others', lane: 'others', text: 'Can you ship it?', final: true });
    bridge.hear({ id: 'live-me', lane: 'me', text: 'Yes, today.', final: true });
    bridge.hear({ id: 'live-me', lane: 'me', text: 'after', final: false });

    expect(
      TestBed.inject(TranscriptStore)
        .lines()
        .map((line) => [line.label, line.text]),
    ).toEqual([
      ['Others', 'Can you ship it?'],
      ['You', 'Yes, today.'],
    ]);
    expect(live.partials()).toEqual({ me: 'after', others: '' });
    expect(live.text()).toBe('Yes, today. after');
  });
});
