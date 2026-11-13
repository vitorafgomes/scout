import { computed, signal } from '@angular/core';

import { MICROPHONE_DISCONNECTED_MESSAGE, type WavRecorder } from '../quick-ask/wav-recorder';
import { microphoneError } from './audio-devices';

/** The worker's speech floor: a 20 ms frame below this RMS is never speech. */
export const SPEECH_RMS_FLOOR = 0.015;
/** A frame is speech only at this many times the room. */
export const ROOM_FACTOR = 3;
/** The room counts as at most this RMS, so the threshold never rises above three times it. */
export const ROOM_CAP = 0.05;
/** How long one press of Test listens at most. */
export const MICROPHONE_TEST_LIMIT_MS = 10_000;
/** How many 20 ms frames of sound, one second, the room is learned from. */
export const ROOM_LEARN_FRAMES = 50;

const FRAME_SAMPLES = 320;
const DECAY = 0.85;
const FULL_SCALE_RMS = 0.2;
const GENERAL_FAILURE = 'Scout could not use the microphone.';

/** The level a frame must reach to count as speech over `room`, as the worker decides it. */
export function speechThreshold(room: number): number {
  return Math.max(SPEECH_RMS_FLOOR, ROOM_FACTOR * Math.min(room, ROOM_CAP));
}

/** The RMS, on the -1 to 1 scale, of every whole 20 ms frame of a 16 kHz PCM16 chunk. */
export function frameLevels(pcm16: Uint8Array): number[] {
  const view = new DataView(pcm16.buffer, pcm16.byteOffset, pcm16.byteLength);
  const levels: number[] = [];
  for (let start = 0; start + FRAME_SAMPLES <= view.byteLength >> 1; start += FRAME_SAMPLES) {
    let sum = 0;
    for (let i = start; i < start + FRAME_SAMPLES; i++) {
      sum += (view.getInt16(i * 2, true) / 32768) ** 2;
    }
    levels.push(Math.sqrt(sum / FRAME_SAMPLES));
  }
  return levels;
}

/** Where an RMS sits on the meter, 0 to 1, on a square-root scale so a quiet room is still visible. */
export function meterPosition(rms: number): number {
  return Math.min(1, Math.sqrt(rms / FULL_SCALE_RMS));
}

/**
 * One press of Test in Settings, Audio: opens the microphone, shows its level per 20 ms frame, rising at once and
 * falling smoothly, learns the room from the first second of sound, digital silence left out, and marks the speech
 * threshold over it. It stops on release, after 10 s, or when the microphone goes away, and releases the microphone.
 */
export class MicrophoneTest {
  private readonly _level = signal(0);
  private readonly _room = signal<number | null>(null);
  private readonly _running = signal(false);
  private readonly _error = signal<string | null>(null);
  private heardFrames = 0;
  private quietest = Number.POSITIVE_INFINITY;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private attempt = 0;

  readonly level = this._level.asReadonly();
  readonly room = this._room.asReadonly();
  readonly running = this._running.asReadonly();
  readonly error = this._error.asReadonly();
  readonly threshold = computed(() => {
    const room = this._room();
    return room === null ? null : speechThreshold(room);
  });

  constructor(
    private readonly recorder: Pick<WavRecorder, 'start' | 'stop'>,
    private readonly open: () => Promise<MediaStream>,
  ) {}

  async start(): Promise<void> {
    if (this._running()) return;
    const attempt = ++this.attempt;
    this.heardFrames = 0;
    this.quietest = Number.POSITIVE_INFINITY;
    this._level.set(0);
    this._room.set(null);
    this._error.set(null);
    this._running.set(true);
    this.timer = setTimeout(() => void this.stop(), MICROPHONE_TEST_LIMIT_MS);
    try {
      await this.recorder.start(
        () => undefined,
        (pcm16) => this.hear(pcm16),
        this.open,
        () => {
          if (attempt !== this.attempt) return;
          this._error.set(MICROPHONE_DISCONNECTED_MESSAGE);
          void this.stop();
        },
      );
    } catch (err: unknown) {
      if (attempt !== this.attempt) return;
      this._error.set(microphoneError(err) ?? GENERAL_FAILURE);
      await this.stop();
    }
  }

  async stop(): Promise<void> {
    if (!this._running()) return;
    this.attempt++;
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
    this._running.set(false);
    this._level.set(0);
    await this.recorder.stop();
  }

  private hear(pcm16: Uint8Array): void {
    let level = this._level();
    for (const frame of frameLevels(pcm16)) {
      level = Math.max(frame, level * DECAY);
      if (frame > 0 && this.heardFrames < ROOM_LEARN_FRAMES) {
        this.quietest = Math.min(this.quietest, frame);
        this.heardFrames++;
        if (this.heardFrames === ROOM_LEARN_FRAMES) this._room.set(this.quietest);
      }
    }
    this._level.set(level);
  }
}
