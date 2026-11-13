import {
  Injectable,
  InjectionToken,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';

import type { LocalTurn } from '@scout/shared';

import { LocalConversation } from '../core/local-conversation';
import { ScoutBridgeService } from '../core/scout-bridge';
import { AudioDevices } from './audio-devices';
import { WebAudioPlayer } from './audio-player';
import { SentenceStream } from './sentences';
import { SpeechQueue, type SpeechPlayer } from './speech-queue';

export const READ_ALOUD_STORAGE_KEY = 'scout:read-aloud';

/** What plays the audio of a sentence; Web Audio on the speaker chosen in Settings in the app, a fake in a spec. */
export const SPEECH_PLAYER = new InjectionToken<SpeechPlayer>('SPEECH_PLAYER', {
  providedIn: 'root',
  factory: () => {
    const devices = inject(AudioDevices);
    return new WebAudioPlayer(undefined, (context) => devices.route(context));
  },
});

function storage(): Storage | null {
  try {
    return globalThis.localStorage ?? null;
  } catch {
    return null;
  }
}

function storedEnabled(): boolean {
  try {
    return storage()?.getItem(READ_ALOUD_STORAGE_KEY) === 'on';
  } catch {
    return false;
  }
}

/**
 * Reads Scout's replies aloud in this window. The speaker on a reply reads that reply, or stops it; with "Read answers
 * aloud" on, every reply to a question this window asked is read as it streams, one sentence at a time. A conversation
 * follows the turn it asked the same way. Each sentence is made by the worker's voice and played in order; `stop`
 * ends it at once.
 */
@Injectable({ providedIn: 'root' })
export class ReadAloud {
  private readonly conversation = inject(LocalConversation);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly queue = new SpeechQueue(
    (text) => this.bridge.localSpeak({ text }),
    inject(SPEECH_PLAYER),
  );
  private readonly _enabled = signal(storedEnabled());
  private readonly _following = signal<string | null>(null);
  private stream = new SentenceStream();
  private ended = false;
  private readonly finished = new Set<string>();

  /** "Read answers aloud", kept in this computer's renderer storage. */
  readonly enabled = this._enabled.asReadonly();
  /** The turn being read, from its first sentence until its last one has played. */
  readonly speakingId = this._following.asReadonly();
  /** True while a sentence's audio plays. */
  readonly playing = this.queue.playing;
  /** Why the voice could not read, from the worker. */
  readonly error = this.queue.error;
  readonly busy = computed(() => this._following() !== null);

  constructor() {
    effect(() => {
      const turns = this.conversation.turns();
      const asked = this.conversation.askedHere();
      const enabled = this._enabled();
      untracked(() => {
        if (enabled && asked !== null && !this.finished.has(asked) && this._following() === null) {
          this.begin(asked);
        }
        this.sync(turns);
      });
    });
    effect(() => {
      const failed = this.queue.error() !== null;
      if ((this.queue.idle() && this.ended) || failed) untracked(() => this.release());
    });
  }

  setEnabled(on: boolean): void {
    this._enabled.set(on);
    if (!on) this.stop();
    try {
      storage()?.setItem(READ_ALOUD_STORAGE_KEY, on ? 'on' : 'off');
    } catch {
      return;
    }
  }

  /** The speaker on a reply: reads that reply from its start, or stops it when it is the one being read. */
  toggle(turnId: string): void {
    if (this._following() === turnId) {
      this.stop();
      return;
    }
    this.follow(turnId);
  }

  /** Reads the turn `turnId` as it streams in, from its start, in place of anything being read. */
  follow(turnId: string): void {
    this.stop();
    this.finished.delete(turnId);
    this.begin(turnId);
    this.sync(this.conversation.turns());
  }

  /** Ends what is being read at once; true when something was. */
  stop(): boolean {
    const following = this._following();
    if (following !== null) this.finished.add(following);
    const was = following !== null || !this.queue.idle();
    this.queue.stop();
    this.ended = false;
    this._following.set(null);
    return was;
  }

  private begin(turnId: string): void {
    this.stream = new SentenceStream();
    this.ended = false;
    this._following.set(turnId);
  }

  private sync(turns: readonly LocalTurn[]): void {
    const following = this._following();
    if (following === null || this.ended) return;
    const turn = turns.find((t) => t.id === following);
    if (turn === undefined) return;
    const done = turn.state !== 'running';
    const sentences = done ? this.stream.end(turn.reply) : this.stream.push(turn.reply);
    for (const sentence of sentences) this.queue.enqueue(sentence);
    if (done) {
      this.ended = true;
      if (this.queue.idle()) this.release();
    }
  }

  private release(): void {
    const following = this._following();
    if (following !== null) this.finished.add(following);
    this.ended = false;
    this._following.set(null);
  }
}
