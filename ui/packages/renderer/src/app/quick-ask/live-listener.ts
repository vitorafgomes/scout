import { Injectable, computed, inject, signal, type WritableSignal } from '@angular/core';

import {
  readIpcError,
  type LocalListenLane,
  type LocalTranscriptEvent,
  type LocalTranslationMode,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';
import { TranscriptStore } from './transcript-store';
import { NOT_16_KHZ_MESSAGE, WavRecorder, toBase64 } from './wav-recorder';

export const MICROPHONE_MESSAGE = 'Scout could not use the microphone.';
export const SYSTEM_AUDIO_MESSAGE = 'Scout could not hear the system audio.';
export const LOUD_RMS = 0.015;
const FRAME_SAMPLES = 320;

/** True when the last 20 ms frame of a 16 kHz PCM16 chunk has an RMS of at least 0.015 on the -1 to 1 scale. */
export function isLoud(pcm16: Uint8Array): boolean {
  const view = new DataView(pcm16.buffer, pcm16.byteOffset, pcm16.byteLength);
  const samples = view.byteLength >> 1;
  const count = Math.min(FRAME_SAMPLES, samples);
  let sum = 0;
  for (let i = samples - count; i < samples; i++) sum += (view.getInt16(i * 2, true) / 32768) ** 2;
  return count > 0 && Math.sqrt(sum / count) >= LOUD_RMS;
}

/** Asks the system for what the computer plays, through the display-media picker, and drops the video at once. */
async function openSystemAudio(): Promise<MediaStream> {
  const stream = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: true });
  stream.getVideoTracks().forEach((track) => track.stop());
  if (stream.getAudioTracks().length === 0) {
    stream.getTracks().forEach((track) => track.stop());
    throw new Error(SYSTEM_AUDIO_MESSAGE);
  }
  return stream;
}

function cancelled(err: unknown): boolean {
  return (
    err instanceof DOMException && (err.name === 'NotAllowedError' || err.name === 'AbortError')
  );
}

interface Lane {
  readonly recorder: WavRecorder;
  readonly listening: WritableSignal<boolean>;
  attempt: number;
  followed: string | null;
}

/**
 * Live listening from the quick-ask window on two lanes: `me` streams the microphone and `others` the system
 * audio, each to the worker as PCM16 chunks under its own session id. While the system audio is loud the
 * microphone is sent as silence, so the owner's microphone does not transcribe the speakers. `text` is the
 * microphone's final lines joined, then its current partial; the finals of both lanes go to the transcript.
 */
@Injectable({ providedIn: 'root' })
export class LiveListener {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly transcript = inject(TranscriptStore);
  private readonly lanes: Record<LocalListenLane, Lane> = {
    me: { recorder: inject(WavRecorder), listening: signal(false), attempt: 0, followed: null },
    others: { recorder: new WavRecorder(), listening: signal(false), attempt: 0, followed: null },
  };
  private readonly finals = signal<readonly string[]>([]);
  private readonly _partials = signal<Readonly<Record<LocalListenLane, string>>>({
    me: '',
    others: '',
  });
  private readonly _error = signal<string | null>(null);
  private othersLoud = false;

  /** Whether this platform can capture the system audio; only Linux can yet. */
  readonly systemAudioAvailable = /linux/i.test(navigator.userAgent);
  readonly listening = this.lanes.me.listening.asReadonly();
  readonly othersListening = this.lanes.others.listening.asReadonly();
  readonly error = this._error.asReadonly();
  readonly partials = this._partials.asReadonly();
  readonly text = computed(() =>
    [...this.finals(), this._partials().me].filter((line) => line.length > 0).join(' '),
  );

  constructor() {
    this.bridge.onLocalTranscript((event) => this.apply(event));
  }

  /**
   * Starts a session in main on the lane, translated in the mode given, then opens its audio; a refusal or a
   * failure shows its sentence.
   */
  async listen(
    lane: LocalListenLane = 'me',
    translate: LocalTranslationMode = 'off',
  ): Promise<void> {
    const state = this.lanes[lane];
    if (state.listening()) return;
    const attempt = ++state.attempt;
    state.followed = null;
    this.transcript.startLane(lane);
    if (lane === 'me') this.clear();
    this._error.set(null);
    state.listening.set(true);

    let id: string;
    try {
      ({ id } = await this.bridge.localListen({
        lane,
        ...(translate !== 'off' ? { translate } : {}),
      }));
    } catch (err: unknown) {
      if (attempt !== state.attempt) return;
      state.listening.set(false);
      this._error.set(readIpcError(err).message);
      return;
    }
    if (attempt !== state.attempt) {
      void this.bridge.localStopListening({ id }).catch(() => undefined);
      return;
    }
    state.followed = id;

    try {
      await state.recorder.start(
        () => undefined,
        (pcm16) => this.send(lane, id, pcm16),
        lane === 'others' ? openSystemAudio : undefined,
      );
    } catch (err: unknown) {
      if (attempt !== state.attempt) return;
      this._error.set(this.failure(lane, err));
      await this.stop(lane);
    }
  }

  /** Releases the lane's audio and ends its session in main; the text stays, and a final on its way joins it. */
  async stop(lane: LocalListenLane = 'me'): Promise<void> {
    const state = this.lanes[lane];
    if (!state.listening()) return;
    state.attempt++;
    state.listening.set(false);
    await state.recorder.stop();
    if (lane === 'others') this.othersLoud = false;
    const id = state.followed;
    if (id !== null) await this.bridge.localStopListening({ id }).catch(() => undefined);
  }

  async stopAll(): Promise<void> {
    await Promise.all([this.stop('me'), this.stop('others')]);
  }

  /** Forgets the microphone text heard so far, as once it was sent as a question. */
  clear(): void {
    this.finals.set([]);
    this._partials.update((partials) => ({ ...partials, me: '' }));
  }

  private send(lane: LocalListenLane, id: string, pcm16: Uint8Array): void {
    if (lane === 'others') this.othersLoud = isLoud(pcm16);
    else if (this.othersLoud) pcm16.fill(0);
    void this.bridge.localAudio({ id, pcm16: toBase64(pcm16) }).catch(() => undefined);
  }

  private failure(lane: LocalListenLane, err: unknown): string | null {
    if (lane === 'others') return cancelled(err) ? null : SYSTEM_AUDIO_MESSAGE;
    return err instanceof Error && err.message === NOT_16_KHZ_MESSAGE
      ? NOT_16_KHZ_MESSAGE
      : MICROPHONE_MESSAGE;
  }

  private apply(event: LocalTranscriptEvent): void {
    const state = this.lanes[event.lane] as Lane | undefined;
    if (state === undefined || state.followed === null || event.id !== state.followed) return;
    if (event.kind === 'translation') {
      const { seq, language, text, error } = event;
      if (seq === 0) this.transcript.note(event.lane, error ?? text ?? '');
      else {
        this.transcript.translate(event.lane, event.id, seq, {
          language,
          ...(text !== undefined ? { text } : {}),
          ...(error !== undefined ? { error } : {}),
        });
      }
      return;
    }
    if (event.ended === true) {
      state.followed = null;
      if (event.error !== undefined) this._error.set(event.error);
      void this.stop(event.lane);
      return;
    }
    if (event.final) {
      this.transcript.add(event.lane, event.text, Date.now(), event.seq, event.id);
      if (event.lane === 'me') this.finals.update((lines) => [...lines, event.text]);
      this._partials.update((partials) => ({ ...partials, [event.lane]: '' }));
    } else {
      this._partials.update((partials) => ({ ...partials, [event.lane]: event.text }));
    }
  }
}
