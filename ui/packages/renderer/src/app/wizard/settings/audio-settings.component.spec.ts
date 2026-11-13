import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { NoticeService } from '../../core/notice.service';
import { MICROPHONE_STORAGE_KEY, SPEAKER_STORAGE_KEY } from '../../speech/audio-devices';
import { MICROPHONE_TEST_LIMIT_MS } from '../../speech/level-meter';
import {
  AudioSettingsComponent,
  DEVICE_LIST_FAILED_MESSAGE,
  HIDDEN_DEVICES_HINT,
} from './audio-settings.component';

function device(kind: MediaDeviceKind, deviceId: string, label = ''): MediaDeviceInfo {
  return { kind, deviceId, label, groupId: 'g' } as MediaDeviceInfo;
}

const processors: { onaudioprocess: ((event: unknown) => void) | null }[] = [];
const tracks: { stopped: boolean }[] = [];

class FakeAudioContext {
  readonly sampleRate = 16_000;
  readonly destination = {};
  createMediaStreamSource() {
    return { connect: () => undefined, disconnect: () => undefined };
  }
  createScriptProcessor() {
    const processor = { onaudioprocess: null };
    processors.push(processor);
    return Object.assign(processor, { connect: () => undefined, disconnect: () => undefined });
  }
  createGain() {
    return { gain: { value: 1 }, connect: () => undefined, disconnect: () => undefined };
  }
  async close(): Promise<void> {
    return;
  }
}

class RoutableAudioContext extends FakeAudioContext {
  setSinkId(): Promise<void> {
    return Promise.resolve();
  }
}

function fakeMedia(devices: MediaDeviceInfo[]) {
  const listeners: (() => void)[] = [];
  const media = {
    devices,
    enumerateDevices: vi.fn(async () => media.devices),
    getUserMedia: vi.fn(async () => {
      const track = { stopped: false, onended: null, stop: () => (track.stopped = true) };
      tracks.push(track);
      return { getTracks: () => [track] };
    }),
    addEventListener: (_type: string, listener: () => void) => listeners.push(listener),
    removeEventListener: (_type: string, listener: () => void) =>
      listeners.splice(listeners.indexOf(listener), 1),
    change: (next: MediaDeviceInfo[]) => {
      media.devices = next;
      listeners.forEach((listener) => listener());
    },
    listeners,
  };
  Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: media });
  return media;
}

async function render(): Promise<ComponentFixture<AudioSettingsComponent>> {
  TestBed.configureTestingModule({
    imports: [AudioSettingsComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: NoticeService, useValue: { show: vi.fn() } },
    ],
  });
  const fixture = TestBed.createComponent(AudioSettingsComponent);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<AudioSettingsComponent>): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
    await Promise.resolve();
  }
}

function q<T extends HTMLElement>(fixture: ComponentFixture<unknown>, test: string): T | null {
  return (fixture.nativeElement as HTMLElement).querySelector<T>(`[data-test="${test}"]`);
}

function options(select: HTMLSelectElement | null): [string, string][] {
  return Array.from(select?.options ?? []).map((o) => [o.value, o.textContent?.trim() ?? '']);
}

function hear(level: number, chunks = 1): void {
  for (let i = 0; i < chunks; i++) {
    const input = new Float32Array(4096).fill(level);
    processors.at(-1)?.onaudioprocess?.({ inputBuffer: { getChannelData: () => input } });
  }
}

describe('Settings, Audio', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    processors.length = 0;
    tracks.length = 0;
    vi.stubGlobal('AudioContext', RoutableAudioContext);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('lists System default first, then every microphone and speaker, an unlabelled one by its number', async () => {
    fakeMedia([
      device('audioinput', 'default', 'Default'),
      device('audioinput', 'mic-a', ''),
      device('audioinput', 'mic-b', 'USB headset'),
      device('audiooutput', 'spk-a', 'Built-in speakers'),
    ]);
    const fixture = await render();

    expect(options(q(fixture, 'audio-microphone'))).toEqual([
      ['', 'System default'],
      ['mic-a', 'Microphone 1'],
      ['mic-b', 'USB headset'],
    ]);
    expect(options(q(fixture, 'audio-speaker'))).toEqual([
      ['', 'System default'],
      ['spk-a', 'Built-in speakers'],
    ]);
    expect(q<HTMLSelectElement>(fixture, 'audio-microphone')?.value).toBe('');
  });

  it('choosing a microphone and a speaker saves them, and the saved ones are selected when Settings opens again', async () => {
    fakeMedia([
      device('audioinput', 'mic-b', 'USB headset'),
      device('audiooutput', 'spk-a', 'HDMI'),
    ]);
    const fixture = await render();
    const mic = q<HTMLSelectElement>(fixture, 'audio-microphone')!;
    const speaker = q<HTMLSelectElement>(fixture, 'audio-speaker')!;

    mic.value = 'mic-b';
    mic.dispatchEvent(new Event('change'));
    speaker.value = 'spk-a';
    speaker.dispatchEvent(new Event('change'));

    expect(localStorage.getItem(MICROPHONE_STORAGE_KEY)).toBe('mic-b');
    expect(localStorage.getItem(SPEAKER_STORAGE_KEY)).toBe('spk-a');

    fixture.destroy();
    TestBed.resetTestingModule();
    const again = await render();
    expect(q<HTMLSelectElement>(again, 'audio-microphone')?.value).toBe('mic-b');
    expect(q<HTMLSelectElement>(again, 'audio-speaker')?.value).toBe('spk-a');
  });

  it('a saved microphone the system does not list stays selected as the chosen one', async () => {
    localStorage.setItem(MICROPHONE_STORAGE_KEY, 'mic-gone');
    fakeMedia([device('audioinput', 'mic-a', 'Built-in')]);
    const fixture = await render();

    expect(options(q(fixture, 'audio-microphone'))).toEqual([
      ['', 'System default'],
      ['mic-gone', 'The chosen microphone'],
      ['mic-a', 'Built-in'],
    ]);
    expect(q<HTMLSelectElement>(fixture, 'audio-microphone')?.value).toBe('mic-gone');
  });

  it('the speaker picker is hidden where the output device cannot be chosen', async () => {
    vi.stubGlobal('AudioContext', FakeAudioContext);
    fakeMedia([device('audiooutput', 'spk-a', 'HDMI')]);
    const fixture = await render();

    expect(q(fixture, 'audio-speaker')).toBeNull();
    expect(q(fixture, 'audio-microphone')).not.toBeNull();
  });

  it('while the system hides the devices until the first capture, a line under the picker asks for one Test, and it goes once real devices are listed', async () => {
    const media = fakeMedia([device('audioinput', ''), device('audiooutput', '')]);
    const fixture = await render();

    expect(q(fixture, 'audio-hidden-hint')?.textContent?.trim()).toBe(HIDDEN_DEVICES_HINT);
    expect(HIDDEN_DEVICES_HINT).toBe(
      'Press and hold Test once so Scout can list your microphones and speakers.',
    );

    const button = q<HTMLButtonElement>(fixture, 'audio-test')!;
    button.dispatchEvent(new Event('pointerdown'));
    await vi.waitFor(() => expect(processors).toHaveLength(1));
    media.devices = [device('audioinput', 'mic-a', 'Built-in microphone')];
    button.dispatchEvent(new Event('pointerup'));
    await settle(fixture);

    expect(q(fixture, 'audio-hidden-hint')).toBeNull();
  });

  it('no hint when the system lists its devices, or has no audio device at all', async () => {
    fakeMedia([device('audioinput', 'mic-a', 'Built-in')]);
    expect(q(await render(), 'audio-hidden-hint')).toBeNull();

    TestBed.resetTestingModule();
    fakeMedia([]);
    expect(q(await render(), 'audio-hidden-hint')).toBeNull();
  });

  it('a device list that fails says so in the section', async () => {
    const media = fakeMedia([]);
    media.enumerateDevices.mockRejectedValue(new DOMException('no', 'NotAllowedError'));
    const fixture = await render();

    expect(q(fixture, 'audio-list-error')?.textContent?.trim()).toBe(DEVICE_LIST_FAILED_MESSAGE);
    expect(options(q(fixture, 'audio-microphone'))).toEqual([['', 'System default']]);
  });

  it('a device plugged in while Settings is open joins the list', async () => {
    const media = fakeMedia([device('audioinput', 'mic-a', 'Built-in')]);
    const fixture = await render();

    media.change([device('audioinput', 'mic-a', 'Built-in'), device('audioinput', 'mic-b', 'USB')]);
    await settle(fixture);

    expect(options(q(fixture, 'audio-microphone')).map(([id]) => id)).toEqual([
      '',
      'mic-a',
      'mic-b',
    ]);
  });

  it('holding Test opens the chosen microphone, shows its level with the room and speech marks, and releasing it stops the tracks', async () => {
    localStorage.setItem(MICROPHONE_STORAGE_KEY, 'mic-b');
    const media = fakeMedia([device('audioinput', 'mic-b', 'USB headset')]);
    const fixture = await render();
    const button = q<HTMLButtonElement>(fixture, 'audio-test')!;

    button.dispatchEvent(new Event('pointerdown'));
    await vi.waitFor(() => expect(processors).toHaveLength(1));
    hear(0.01, 5);
    hear(0.2);
    await settle(fixture);

    expect(media.getUserMedia).toHaveBeenCalledExactlyOnceWith({
      audio: {
        channelCount: 1,
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: false,
        deviceId: { exact: 'mic-b' },
      },
    });
    expect(button.getAttribute('aria-pressed')).toBe('true');
    expect(q(fixture, 'audio-level')?.getAttribute('aria-valuenow')).toBe('100');
    expect(q<HTMLElement>(fixture, 'audio-room-mark')?.style.left).toBe('22%');
    expect(q<HTMLElement>(fixture, 'audio-speech-mark')?.style.left).toBe('39%');

    button.dispatchEvent(new Event('pointerup'));
    await settle(fixture);

    expect(tracks.map((t) => t.stopped)).toEqual([true]);
    expect(button.getAttribute('aria-pressed')).toBe('false');
    expect(q(fixture, 'audio-level')?.getAttribute('aria-valuenow')).toBe('0');
  });

  it.each([' ', 'Enter'])(
    'a key held on Test past 10 s, its auto-repeat included, leaves the test stopped and the microphone released (%j)',
    async (key) => {
      const media = fakeMedia([]);
      const fixture = await render();
      const button = q<HTMLButtonElement>(fixture, 'audio-test')!;
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      const press = (repeat: boolean) =>
        button.dispatchEvent(new KeyboardEvent('keydown', { key, repeat, bubbles: true }));

      press(false);
      await vi.waitFor(() => expect(processors).toHaveLength(1));
      for (let elapsed = 0; elapsed <= MICROPHONE_TEST_LIMIT_MS; elapsed += 500) {
        press(true);
        await vi.advanceTimersByTimeAsync(500);
      }
      press(true);
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();

      expect(media.getUserMedia).toHaveBeenCalledTimes(1);
      expect(tracks.map((t) => t.stopped)).toEqual([true]);
      expect(button.getAttribute('aria-pressed')).toBe('false');
    },
  );

  it('leaving Settings while Test is held releases the microphone', async () => {
    fakeMedia([]);
    const fixture = await render();

    q<HTMLButtonElement>(fixture, 'audio-test')!.dispatchEvent(new Event('pointerdown'));
    await vi.waitFor(() => expect(processors).toHaveLength(1));
    fixture.destroy();

    await vi.waitFor(() => expect(tracks.map((t) => t.stopped)).toEqual([true]));
  });

  it('after a Test the list is read again, so names the system hid before the microphone opened appear', async () => {
    const media = fakeMedia([device('audioinput', 'mic-a', '')]);
    const fixture = await render();
    const button = q<HTMLButtonElement>(fixture, 'audio-test')!;

    button.dispatchEvent(new Event('pointerdown'));
    await vi.waitFor(() => expect(processors).toHaveLength(1));
    media.devices = [device('audioinput', 'mic-a', 'Built-in microphone')];
    button.dispatchEvent(new Event('pointerup'));
    await settle(fixture);

    expect(options(q(fixture, 'audio-microphone'))).toEqual([
      ['', 'System default'],
      ['mic-a', 'Built-in microphone'],
    ]);
  });

  it.each([
    ['NotAllowedError', 'Scout was not allowed to use the microphone.'],
    ['NotFoundError', 'No microphone was found.'],
    ['NotReadableError', 'The microphone is busy: another program is using it.'],
  ])(
    'a Test whose microphone fails with %s shows its own sentence in the section',
    async (name, sentence) => {
      const media = fakeMedia([]);
      media.getUserMedia.mockRejectedValue(new DOMException('failed', name));
      const fixture = await render();

      q<HTMLButtonElement>(fixture, 'audio-test')!.dispatchEvent(new Event('pointerdown'));
      await vi.waitFor(async () => {
        await settle(fixture);
        expect(q(fixture, 'audio-test-error')?.textContent?.trim()).toBe(sentence);
      });
      expect(q(fixture, 'audio-test-error')?.getAttribute('role')).toBe('alert');
    },
  );

  it('the pickers and the meter are named for a screen reader', async () => {
    fakeMedia([]);
    const fixture = await render();
    const root = fixture.nativeElement as HTMLElement;

    const labels = Array.from(root.querySelectorAll('label')).map((l) => [
      l.querySelector('span')?.textContent?.trim(),
      l.querySelector('select')?.getAttribute('data-test'),
    ]);
    expect(labels).toEqual([
      ['Microphone', 'audio-microphone'],
      ['Speaker', 'audio-speaker'],
    ]);
    expect(q(fixture, 'audio-level')?.getAttribute('role')).toBe('meter');
    expect(q(fixture, 'audio-level')?.getAttribute('aria-label')).toBe('Microphone level');
  });
});
