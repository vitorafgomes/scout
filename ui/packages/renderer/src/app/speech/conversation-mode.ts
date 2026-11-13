import {
  Injectable,
  InjectionToken,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';

import { readIpcError, type LocalTranscriptEvent } from '@scout/shared';

import { LocalConversation } from '../core/local-conversation';
import { ScoutBridgeService } from '../core/scout-bridge';
import {
  MICROPHONE_DISCONNECTED_MESSAGE,
  NOT_16_KHZ_MESSAGE,
  WavRecorder,
  toBase64,
} from '../quick-ask/wav-recorder';
import { AudioDevices, microphoneError } from './audio-devices';
import { ReadAloud } from './read-aloud';

export type ConversationState = 'off' | 'listening' | 'thinking' | 'speaking';
export type ConversationEvent = 'start' | 'heard' | 'speaking' | 'done' | 'stop';

export const CONVERSATION_MICROPHONE_MESSAGE = 'Scout could not use the microphone.';
export const CONVERSATION_ENDED_MESSAGE = 'Scout stopped listening.';

const FINISH_WAIT_MS = 10_000;

/**
 * The owner's microphone for a conversation: mono, with echo cancellation and noise suppression, and without automatic
 * gain, so the room stays at one level between turns instead of rising in every pause.
 */
export const CONVERSATION_MICROPHONE_CONSTRAINTS: MediaTrackConstraints = Object.freeze({
  channelCount: 1,
  echoCancellation: true,
  noiseSuppression: true,
  autoGainControl: false,
});

/** The conversation's own microphone, apart from the one dictation holds; a fake in a spec. */
export const CONVERSATION_RECORDER = new InjectionToken<Pick<WavRecorder, 'start' | 'stop'>>(
  'CONVERSATION_RECORDER',
  { providedIn: 'root', factory: () => new WavRecorder() },
);

const TRANSITIONS: Readonly<
  Record<ConversationState, Partial<Record<ConversationEvent, ConversationState>>>
> = {
  off: { start: 'listening' },
  listening: { heard: 'thinking', stop: 'off' },
  thinking: { speaking: 'speaking', done: 'listening', stop: 'off' },
  speaking: { done: 'listening', stop: 'off' },
};

/** The state a conversation moves to on an event; an event that does not apply leaves it where it is. */
export function nextConversationState(
  state: ConversationState,
  event: ConversationEvent,
): ConversationState {
  return TRANSITIONS[state][event] ?? state;
}

/**
 * Talking with Scout: a live listen on the microphone under its own session, separate from dictation. Each final line,
 * heard after a pause, is sent as an ordinary question and its answer is read aloud; then Scout listens again. The
 * microphone is sent as silence from the moment a line is heard until the answer has been read, so Scout never hears
 * itself. `stop` ends it from any state; stopped while Scout listens, the listen is followed until it ends, at most
 * 10 s, and what the owner said by then is asked. `escape` ends it and drops what was in flight. A listen that ends
 * under it, or audio main refuses, turns it off with the reason on the button.
 */
@Injectable({ providedIn: 'root' })
export class ConversationMode {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly conversation = inject(LocalConversation);
  private readonly readAloud = inject(ReadAloud);
  private readonly recorder = inject(CONVERSATION_RECORDER);
  private readonly devices = inject(AudioDevices);
  private readonly _state = signal<ConversationState>('off');
  private readonly _error = signal<string | null>(null);
  private readonly asked = signal<string | null>(null);
  private readonly _partial = signal('');
  private session: string | null = null;
  private finishing: {
    readonly id: string;
    readonly lines: string[];
    readonly timer: ReturnType<typeof setTimeout>;
  } | null = null;
  private attempt = 0;

  readonly state = this._state.asReadonly();
  readonly error = this._error.asReadonly();
  /** What Scout heard so far of the line being spoken, while it listens; empty otherwise. */
  readonly heard = computed(() => (this._state() === 'listening' ? this._partial() : ''));
  readonly on = computed(() => this._state() !== 'off');
  /** True only while Scout listens; otherwise every chunk of the microphone goes out as silence. */
  readonly microphoneOpen = computed(() => this._state() === 'listening');

  constructor() {
    this.bridge.onLocalTranscript((event) => void this.onHeard(event));
    effect(() => {
      const state = this._state();
      const playing = this.readAloud.playing();
      const busy = this.readAloud.busy();
      const asked = this.asked();
      const turn = this.conversation.turns().find((t) => t.id === asked);
      untracked(() => {
        if (state === 'thinking' && playing) this.move('speaking');
        else if (
          (state === 'thinking' || state === 'speaking') &&
          turn !== undefined &&
          turn.state !== 'running' &&
          !busy
        ) {
          this.move('done');
        }
      });
    });
  }

  async toggle(): Promise<void> {
    if (this._state() === 'off') await this.start();
    else await this.stop();
  }

  async start(): Promise<void> {
    if (this._state() !== 'off') return;
    const attempt = ++this.attempt;
    this._error.set(null);
    this.dropFinishing();
    this.move('start');
    let id: string;
    try {
      ({ id } = await this.bridge.localListen({ lane: 'me' }));
    } catch (err: unknown) {
      if (attempt !== this.attempt) return;
      this._error.set(readIpcError(err).message);
      this.move('stop');
      return;
    }
    if (attempt !== this.attempt) {
      void this.bridge.localStopListening({ id }).catch(() => undefined);
      return;
    }
    this.session = id;
    try {
      await this.recorder.start(
        () => undefined,
        (pcm16) => this.send(id, pcm16),
        () => this.devices.open(CONVERSATION_MICROPHONE_CONSTRAINTS),
        () => {
          if (attempt !== this.attempt) return;
          this._error.set(MICROPHONE_DISCONNECTED_MESSAGE);
          void this.end(false);
        },
      );
    } catch (err: unknown) {
      if (attempt !== this.attempt) return;
      this._error.set(
        err instanceof Error && err.message === NOT_16_KHZ_MESSAGE
          ? NOT_16_KHZ_MESSAGE
          : (microphoneError(err) ?? CONVERSATION_MICROPHONE_MESSAGE),
      );
      await this.end(false);
    }
  }

  /**
   * Ends the conversation from any state: the voice stops, the microphone closes and the listen ends; stopped while
   * Scout listens, what the owner said by the time the listen ends is asked. True when it was on.
   */
  stop(): Promise<boolean> {
    return this.end(true);
  }

  /**
   * Esc: ends the conversation, dropping what was in flight, including the line a click left draining, and stops
   * anything read aloud; true when either was on.
   */
  escape(): boolean {
    const reading = this.readAloud.stop();
    const talking = this._state() !== 'off';
    this.dropFinishing();
    void this.end(false);
    return reading || talking;
  }

  private async end(keepWhatWasSaid: boolean): Promise<boolean> {
    if (this._state() === 'off') return false;
    const listening = this._state() === 'listening';
    this.attempt++;
    this.move('stop');
    this.asked.set(null);
    this._partial.set('');
    this.readAloud.stop();
    const id = this.session;
    this.session = null;
    if (keepWhatWasSaid && listening && id !== null) {
      this.finishing = { id, lines: [], timer: setTimeout(() => this.finish(id), FINISH_WAIT_MS) };
    }
    await this.recorder.stop();
    if (id !== null) await this.bridge.localStopListening({ id }).catch(() => undefined);
    return true;
  }

  private finish(id: string): void {
    const finishing = this.finishing;
    if (finishing === null || finishing.id !== id) return;
    this.dropFinishing();
    const text = finishing.lines.join(' ');
    if (text.length === 0) return;
    void this.conversation.askAs(crypto.randomUUID(), text).then((refused) => {
      if (refused !== null) this._error.set(refused);
    });
  }

  private dropFinishing(): void {
    if (this.finishing !== null) clearTimeout(this.finishing.timer);
    this.finishing = null;
  }

  private failed(id: string, reason: string): void {
    if (id !== this.session) return;
    this.session = null;
    this._error.set(reason);
    void this.end(false);
  }

  private send(id: string, pcm16: Uint8Array): void {
    if (!this.microphoneOpen()) pcm16.fill(0);
    void this.bridge
      .localAudio({ id, pcm16: toBase64(pcm16) })
      .catch((err: unknown) => this.failed(id, readIpcError(err).message));
  }

  private async onHeard(event: LocalTranscriptEvent): Promise<void> {
    if (event.kind === 'translation') return;
    if (this.finishing !== null && event.id === this.finishing.id) {
      const line = event.text.trim();
      if (event.ended === true) this.finish(event.id);
      else if (event.final && line.length > 0) this.finishing.lines.push(line);
      return;
    }
    if (this.session === null || event.id !== this.session) return;
    if (event.ended === true) {
      this.failed(event.id, event.error ?? CONVERSATION_ENDED_MESSAGE);
      return;
    }
    if (!event.final) {
      this._partial.set(event.text);
      return;
    }
    this._partial.set('');
    const text = event.text.trim();
    if (text.length === 0 || this._state() !== 'listening') return;
    const id = crypto.randomUUID();
    this.move('heard');
    this.asked.set(id);
    this.readAloud.follow(id);
    const refused = await this.conversation.askAs(id, text);
    if (refused !== null && this._state() === 'thinking') {
      this._error.set(refused);
      this.readAloud.stop();
      this.move('done');
    }
  }

  private move(event: ConversationEvent): void {
    this._state.update((state) => nextConversationState(state, event));
  }
}
