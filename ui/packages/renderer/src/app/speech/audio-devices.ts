import { Injectable, inject } from '@angular/core';

import { NoticeService } from '../core/notice.service';

export const MICROPHONE_STORAGE_KEY = 'scout:audio-input';
export const SPEAKER_STORAGE_KEY = 'scout:audio-output';
export const MICROPHONE_NOT_ALLOWED_MESSAGE = 'Scout was not allowed to use the microphone.';
export const NO_MICROPHONE_MESSAGE = 'No microphone was found.';
export const MICROPHONE_BUSY_MESSAGE = 'The microphone is busy: another program is using it.';
export const MICROPHONE_FALLBACK_NOTICE =
  'The chosen microphone is not connected; Scout uses the default one.';
export const SPEAKER_FALLBACK_NOTICE =
  'The chosen speaker is not connected; Scout uses the default one.';

/** One audio device as the Settings pickers show it. */
export interface AudioDevice {
  readonly id: string;
  readonly label: string;
}

/** An AudioContext that may choose its output device; the DOM typings do not name it yet. */
type RoutableContext = AudioContext & {
  sinkId?: unknown;
  setSinkId?: (sinkId: string) => Promise<void>;
};

const NAMED_FAILURES: Readonly<Record<string, string>> = {
  NotAllowedError: MICROPHONE_NOT_ALLOWED_MESSAGE,
  SecurityError: MICROPHONE_NOT_ALLOWED_MESSAGE,
  NotFoundError: NO_MICROPHONE_MESSAGE,
  NotReadableError: MICROPHONE_BUSY_MESSAGE,
};

const PSEUDO_DEVICES = new Set(['', 'default', 'communications']);

function errorName(err: unknown): string | null {
  const name = typeof err === 'object' && err !== null ? (err as { name?: unknown }).name : null;
  return typeof name === 'string' ? name : null;
}

/** The sentence for a microphone that would not open: not allowed, none found, or busy; null for any other failure. */
export function microphoneError(err: unknown): string | null {
  return NAMED_FAILURES[errorName(err) ?? ''] ?? null;
}

function stored(key: string): string {
  try {
    return globalThis.localStorage?.getItem(key) ?? '';
  } catch {
    return '';
  }
}

function store(key: string, id: string): void {
  try {
    if (id === '') globalThis.localStorage?.removeItem(key);
    else globalThis.localStorage?.setItem(key, id);
  } catch {
    return;
  }
}

/**
 * The microphone and speaker the owner chose in Settings, kept in this computer's renderer storage and read again on
 * every open, so a choice made in one window reaches the next capture in another. A chosen device that is not
 * connected falls back to the system default and says so once, until it is connected again.
 */
@Injectable({ providedIn: 'root' })
export class AudioDevices {
  private readonly notice = inject(NoticeService);
  private readonly missing = new Set<string>();

  constructor() {
    globalThis.navigator?.mediaDevices?.addEventListener?.(
      'devicechange',
      () => void this.checkConnected(),
    );
  }

  /** The chosen microphone's device id; empty for the system default. */
  microphone(): string {
    return stored(MICROPHONE_STORAGE_KEY);
  }

  setMicrophone(id: string): void {
    store(MICROPHONE_STORAGE_KEY, id);
  }

  /** The chosen speaker's device id; empty for the system default. */
  speaker(): string {
    return stored(SPEAKER_STORAGE_KEY);
  }

  setSpeaker(id: string): void {
    store(SPEAKER_STORAGE_KEY, id);
  }

  /** True where an AudioContext can choose its output device. */
  speakerSelectable(): boolean {
    return typeof AudioContext !== 'undefined' && 'setSinkId' in AudioContext.prototype;
  }

  /**
   * The audio inputs and outputs the system lists, an unlabelled one named by its kind and place; `hidden` is true
   * while the system lists its audio devices without ids, as it does before the first capture.
   */
  async list(): Promise<{ microphones: AudioDevice[]; speakers: AudioDevice[]; hidden: boolean }> {
    const devices = await navigator.mediaDevices.enumerateDevices();
    const pick = (kind: MediaDeviceKind, name: string): AudioDevice[] =>
      devices
        .filter((d) => d.kind === kind && !PSEUDO_DEVICES.has(d.deviceId))
        .map((d, i) => ({ id: d.deviceId, label: d.label || `${name} ${i + 1}` }));
    return {
      microphones: pick('audioinput', 'Microphone'),
      speakers: pick('audiooutput', 'Speaker'),
      hidden: devices.some((d) => d.kind !== 'videoinput' && d.deviceId === ''),
    };
  }

  /** Opens the chosen microphone with `constraints`, or the default one when none is chosen or it is not connected. */
  async open(constraints: MediaTrackConstraints): Promise<MediaStream> {
    const id = this.microphone();
    if (id === '') return navigator.mediaDevices.getUserMedia({ audio: constraints });
    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: { ...constraints, deviceId: { exact: id } },
      });
      this.missing.delete(id);
      return stream;
    } catch (err: unknown) {
      const name = errorName(err);
      if (name !== 'OverconstrainedError' && name !== 'NotFoundError') throw err;
      const stream = await navigator.mediaDevices.getUserMedia({ audio: constraints });
      this.fellBack(id, MICROPHONE_FALLBACK_NOTICE);
      return stream;
    }
  }

  /** Points `context` at the chosen speaker, or at the default one when that is not connected; never throws. */
  async route(context: AudioContext): Promise<void> {
    const routable = context as RoutableContext;
    if (typeof routable.setSinkId !== 'function') return;
    const chosen = this.speaker();
    const wanted = this.missing.has(chosen) ? '' : chosen;
    if (routable.sinkId === wanted) return;
    try {
      await routable.setSinkId(wanted);
    } catch {
      this.fellBack(wanted, SPEAKER_FALLBACK_NOTICE);
      await routable.setSinkId('').catch(() => undefined);
    }
  }

  private fellBack(id: string, message: string): void {
    if (id === '' || this.missing.has(id)) return;
    this.missing.add(id);
    this.notice.show(message);
  }

  private async checkConnected(): Promise<void> {
    let devices: MediaDeviceInfo[];
    try {
      devices = await navigator.mediaDevices.enumerateDevices();
    } catch {
      return;
    }
    const chosen: [string, MediaDeviceKind, string][] = [
      [this.microphone(), 'audioinput', MICROPHONE_FALLBACK_NOTICE],
      [this.speaker(), 'audiooutput', SPEAKER_FALLBACK_NOTICE],
    ];
    for (const [id, kind, message] of chosen) {
      const listed = devices.filter((d) => d.kind === kind);
      if (id === '' || listed.some((d) => d.deviceId === '')) continue;
      if (listed.some((d) => d.deviceId === id)) this.missing.delete(id);
      else this.fellBack(id, message);
    }
  }
}
