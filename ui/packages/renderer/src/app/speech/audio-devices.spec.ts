import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { NoticeService } from '../core/notice.service';
import {
  AudioDevices,
  MICROPHONE_BUSY_MESSAGE,
  MICROPHONE_FALLBACK_NOTICE,
  MICROPHONE_NOT_ALLOWED_MESSAGE,
  MICROPHONE_STORAGE_KEY,
  NO_MICROPHONE_MESSAGE,
  SPEAKER_FALLBACK_NOTICE,
  SPEAKER_STORAGE_KEY,
  microphoneError,
} from './audio-devices';

const CONSTRAINTS = { channelCount: 1, echoCancellation: true, noiseSuppression: true };

function device(kind: MediaDeviceKind, deviceId: string, label = ''): MediaDeviceInfo {
  return { kind, deviceId, label, groupId: 'g' } as MediaDeviceInfo;
}

function fakeMedia(devices: MediaDeviceInfo[] = []) {
  const listeners: (() => void)[] = [];
  const media = {
    devices,
    getUserMedia: vi.fn(async (_constraints: MediaStreamConstraints) => ({
      getTracks: () => [],
    })),
    enumerateDevices: vi.fn(async () => media.devices),
    addEventListener: (type: string, listener: () => void) => {
      if (type === 'devicechange') listeners.push(listener);
    },
    change: async (next: MediaDeviceInfo[]) => {
      media.devices = next;
      listeners.forEach((listener) => listener());
      for (let i = 0; i < 5; i++) await Promise.resolve();
    },
  };
  Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: media });
  return media;
}

function setup() {
  const notice = { show: vi.fn() };
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), { provide: NoticeService, useValue: notice }],
  });
  return { notice, devices: TestBed.inject(AudioDevices) };
}

function named(name: string): Error {
  const err = new Error(name);
  err.name = name;
  return err;
}

describe('AudioDevices', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('lists the audio inputs and outputs, names an unlabelled one Microphone 1, 2 or Speaker 1, and leaves out the system pseudo-devices', async () => {
    fakeMedia([
      device('audioinput', 'default', 'Default - Built-in'),
      device('audioinput', 'communications', 'Communications'),
      device('audioinput', 'mic-a', ''),
      device('audioinput', 'mic-b', 'USB headset'),
      device('audioinput', 'mic-c', ''),
      device('videoinput', 'cam', 'Camera'),
      device('audiooutput', 'default', 'Default'),
      device('audiooutput', 'spk-a', ''),
      device('audiooutput', 'spk-b', 'HDMI'),
    ]);
    const { devices } = setup();

    const listed = await devices.list();

    expect(listed.microphones).toEqual([
      { id: 'mic-a', label: 'Microphone 1' },
      { id: 'mic-b', label: 'USB headset' },
      { id: 'mic-c', label: 'Microphone 3' },
    ]);
    expect(listed.speakers).toEqual([
      { id: 'spk-a', label: 'Speaker 1' },
      { id: 'spk-b', label: 'HDMI' },
    ]);
  });

  it('a device list without ids, before the microphone was ever allowed, lists nothing to choose and says the devices are hidden', async () => {
    fakeMedia([device('audioinput', ''), device('audiooutput', '')]);
    const { devices } = setup();

    expect(await devices.list()).toEqual({ microphones: [], speakers: [], hidden: true });
  });

  it('a list with ids, or with no audio device at all, is not hidden', async () => {
    const media = fakeMedia([device('audioinput', 'mic-a'), device('videoinput', '')]);
    const { devices } = setup();

    expect((await devices.list()).hidden).toBe(false);
    media.devices = [];
    expect((await devices.list()).hidden).toBe(false);
  });

  it('the chosen microphone and speaker are saved in renderer storage, and System default clears the choice', () => {
    fakeMedia();
    const { devices } = setup();

    devices.setMicrophone('mic-b');
    devices.setSpeaker('spk-b');
    expect(localStorage.getItem(MICROPHONE_STORAGE_KEY)).toBe('mic-b');
    expect(localStorage.getItem(SPEAKER_STORAGE_KEY)).toBe('spk-b');
    expect(devices.microphone()).toBe('mic-b');
    expect(devices.speaker()).toBe('spk-b');

    devices.setMicrophone('');
    devices.setSpeaker('');
    expect(localStorage.getItem(MICROPHONE_STORAGE_KEY)).toBeNull();
    expect(devices.microphone()).toBe('');
    expect(devices.speaker()).toBe('');
  });

  it('a choice saved in another window is the one the next open uses, with no reload', async () => {
    const media = fakeMedia();
    const { devices } = setup();

    localStorage.setItem(MICROPHONE_STORAGE_KEY, 'mic-b');
    await devices.open(CONSTRAINTS);

    expect(media.getUserMedia).toHaveBeenCalledExactlyOnceWith({
      audio: { ...CONSTRAINTS, deviceId: { exact: 'mic-b' } },
    });
  });

  it('with System default the microphone opens with the constraints alone, no device named', async () => {
    const media = fakeMedia();
    const { devices } = setup();

    await devices.open(CONSTRAINTS);

    expect(media.getUserMedia).toHaveBeenCalledExactlyOnceWith({ audio: CONSTRAINTS });
  });

  it.each(['OverconstrainedError', 'NotFoundError'])(
    'a chosen microphone that is not connected (%s) falls back to the default one, and says so once over two opens',
    async (name) => {
      const media = fakeMedia();
      const stream = { getTracks: () => [] };
      media.getUserMedia.mockImplementation(async (constraints: MediaStreamConstraints) => {
        if ((constraints.audio as MediaTrackConstraints).deviceId !== undefined) throw named(name);
        return stream;
      });
      const { devices, notice } = setup();
      devices.setMicrophone('mic-gone');

      const first = await devices.open(CONSTRAINTS);
      await devices.open(CONSTRAINTS);

      expect(first).toBe(stream);
      expect(media.getUserMedia.mock.calls.map(([c]) => c)).toEqual([
        { audio: { ...CONSTRAINTS, deviceId: { exact: 'mic-gone' } } },
        { audio: CONSTRAINTS },
        { audio: { ...CONSTRAINTS, deviceId: { exact: 'mic-gone' } } },
        { audio: CONSTRAINTS },
      ]);
      expect(notice.show).toHaveBeenCalledExactlyOnceWith(MICROPHONE_FALLBACK_NOTICE);
      expect(devices.microphone()).toBe('mic-gone');
    },
  );

  it('with no microphone at all and one chosen, only "No microphone was found." reaches the owner: no fallback notice', async () => {
    const media = fakeMedia();
    media.getUserMedia.mockRejectedValue(named('NotFoundError'));
    const { devices, notice } = setup();
    devices.setMicrophone('mic-gone');

    const failure = await devices.open(CONSTRAINTS).catch((err: unknown) => err);

    expect(microphoneError(failure)).toBe(NO_MICROPHONE_MESSAGE);
    expect(media.getUserMedia).toHaveBeenCalledTimes(2);
    expect(notice.show).not.toHaveBeenCalled();
  });

  it('a chosen microphone Scout is not allowed to use is not retried on the default one', async () => {
    const media = fakeMedia();
    media.getUserMedia.mockRejectedValue(named('NotAllowedError'));
    const { devices, notice } = setup();
    devices.setMicrophone('mic-b');

    await expect(devices.open(CONSTRAINTS)).rejects.toMatchObject({ name: 'NotAllowedError' });

    expect(media.getUserMedia).toHaveBeenCalledTimes(1);
    expect(notice.show).not.toHaveBeenCalled();
  });

  it('unplugging the chosen microphone says once that Scout uses the default one, and again only after it came back and went', async () => {
    const media = fakeMedia([device('audioinput', 'mic-b', 'USB headset')]);
    const { devices, notice } = setup();
    devices.setMicrophone('mic-b');

    await media.change([device('audioinput', 'mic-a', 'Built-in')]);
    await media.change([device('audioinput', 'mic-a', 'Built-in')]);
    expect(notice.show).toHaveBeenCalledExactlyOnceWith(MICROPHONE_FALLBACK_NOTICE);

    await media.change([device('audioinput', 'mic-b', 'USB headset')]);
    await media.change([]);
    expect(notice.show).toHaveBeenCalledTimes(2);
  });

  it('unplugging the chosen speaker says once that Scout uses the default one', async () => {
    const media = fakeMedia([device('audiooutput', 'spk-b', 'HDMI')]);
    const { devices, notice } = setup();
    devices.setSpeaker('spk-b');

    await media.change([device('audiooutput', 'spk-a', 'Built-in')]);
    await media.change([device('audiooutput', 'spk-a', 'Built-in')]);

    expect(notice.show).toHaveBeenCalledExactlyOnceWith(SPEAKER_FALLBACK_NOTICE);
  });

  it('a camera listed without an id, as before camera permission, does not hide an unplugged microphone', async () => {
    const media = fakeMedia([device('audioinput', 'mic-b'), device('videoinput', '')]);
    const { devices, notice } = setup();
    devices.setMicrophone('mic-b');

    await media.change([device('audioinput', 'mic-a', 'Built-in'), device('videoinput', '')]);

    expect(notice.show).toHaveBeenCalledExactlyOnceWith(MICROPHONE_FALLBACK_NOTICE);
  });

  it('a device change whose list has no ids says nothing, since it cannot tell what is connected', async () => {
    const media = fakeMedia([device('audioinput', 'mic-b')]);
    const { devices, notice } = setup();
    devices.setMicrophone('mic-b');

    await media.change([device('audioinput', '')]);

    expect(notice.show).not.toHaveBeenCalled();
  });

  it('a device change whose listing fails says nothing and throws nothing', async () => {
    const media = fakeMedia([device('audioinput', 'mic-b')]);
    const { devices, notice } = setup();
    devices.setMicrophone('mic-b');
    media.enumerateDevices.mockRejectedValue(named('NotAllowedError'));

    await media.change([]);

    expect(notice.show).not.toHaveBeenCalled();
  });

  it('a listing that fails rejects, so the screen can say so', async () => {
    const media = fakeMedia();
    media.enumerateDevices.mockRejectedValue(named('NotAllowedError'));
    const { devices } = setup();

    await expect(devices.list()).rejects.toMatchObject({ name: 'NotAllowedError' });
  });

  describe('the speaker read aloud plays on', () => {
    function context(sinkId = '', fail = new Set<string>()) {
      const calls: string[] = [];
      const ctx = {
        sinkId,
        setSinkId: vi.fn(async (id: string) => {
          calls.push(id);
          if (fail.has(id)) throw named('NotFoundError');
          ctx.sinkId = id;
        }),
      };
      return { ctx, calls, audio: ctx as unknown as AudioContext };
    }

    it('the context is pointed at the chosen speaker before it plays', async () => {
      fakeMedia();
      const { devices } = setup();
      devices.setSpeaker('spk-b');
      const c = context();

      await devices.route(c.audio);

      expect(c.calls).toEqual(['spk-b']);
      expect(c.ctx.sinkId).toBe('spk-b');
    });

    it('a context already on the chosen speaker is left alone, and System default moves it back to the default one', async () => {
      fakeMedia();
      const { devices } = setup();
      devices.setSpeaker('spk-b');
      const c = context('spk-b');

      await devices.route(c.audio);
      expect(c.calls).toEqual([]);

      devices.setSpeaker('');
      await devices.route(c.audio);
      expect(c.calls).toEqual(['']);
    });

    it('a chosen speaker that is not connected falls back to the default one, says so once, and is not tried again until it is back', async () => {
      const media = fakeMedia([device('audiooutput', 'spk-a')]);
      const { devices, notice } = setup();
      devices.setSpeaker('spk-gone');
      const gone = new Set(['spk-gone']);
      const c = context('', gone);

      await devices.route(c.audio);
      await devices.route(c.audio);

      expect(c.calls).toEqual(['spk-gone', '']);
      expect(c.ctx.sinkId).toBe('');
      expect(notice.show).toHaveBeenCalledExactlyOnceWith(SPEAKER_FALLBACK_NOTICE);

      gone.clear();
      await media.change([device('audiooutput', 'spk-gone')]);
      c.calls.length = 0;
      await devices.route(c.audio);
      expect(c.calls).toEqual(['spk-gone']);
    });

    it('the speaker picker shows only where an AudioContext can choose its output', () => {
      fakeMedia();
      vi.stubGlobal('AudioContext', class {});
      expect(setup().devices.speakerSelectable()).toBe(false);

      TestBed.resetTestingModule();
      vi.stubGlobal(
        'AudioContext',
        class {
          setSinkId(): Promise<void> {
            return Promise.resolve();
          }
        },
      );
      expect(setup().devices.speakerSelectable()).toBe(true);
    });
  });
});

describe('microphoneError', () => {
  it('not allowed, no device and a busy device each have their own sentence', () => {
    const sentences = ['NotAllowedError', 'NotFoundError', 'NotReadableError'].map((name) =>
      microphoneError(named(name)),
    );

    expect(sentences).toEqual([
      MICROPHONE_NOT_ALLOWED_MESSAGE,
      NO_MICROPHONE_MESSAGE,
      MICROPHONE_BUSY_MESSAGE,
    ]);
    expect(new Set(sentences).size).toBe(3);
  });

  it('a DOMException carries its name the same way', () => {
    expect(microphoneError(new DOMException('busy', 'NotReadableError'))).toBe(
      MICROPHONE_BUSY_MESSAGE,
    );
  });

  it('any other failure has no sentence of its own', () => {
    expect(microphoneError(new Error('boom'))).toBeNull();
    expect(microphoneError(null)).toBeNull();
    expect(microphoneError('NotAllowedError')).toBeNull();
  });
});
