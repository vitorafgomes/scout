import { computed, signal } from '@angular/core';

import { readIpcError, type LocalSpeakResponse } from '@scout/shared';

/** Plays one sentence's audio; `play` settles when it ends or is stopped, and `stop` ends what plays at once. */
export interface SpeechPlayer {
  play(audio: LocalSpeakResponse): Promise<void>;
  stop(): void;
}

/**
 * Sentences read aloud in the order they were given: each is synthesized once the one before it starts playing, so at
 * most one sentence is made ahead of the one playing, and played only after every sentence before it. `stop` drops the rest and ends what plays at once. A sentence
 * the voice could not make, or one that could not be played, stops the queue and leaves the reason in `error`.
 */
export class SpeechQueue {
  private readonly _pending = signal(0);
  private readonly _playing = signal(false);
  private readonly _error = signal<string | null>(null);
  private generation = 0;
  private played: Promise<void> = Promise.resolve();
  private started: Promise<void> = Promise.resolve();

  /** True while a sentence's audio plays. */
  readonly playing = this._playing.asReadonly();
  /** True when no sentence waits or plays. */
  readonly idle = computed(() => this._pending() === 0);
  readonly error = this._error.asReadonly();

  constructor(
    private readonly synthesize: (text: string) => Promise<LocalSpeakResponse>,
    private readonly player: SpeechPlayer,
  ) {}

  enqueue(text: string): void {
    const generation = this.generation;
    this._error.set(null);
    this._pending.update((count) => count + 1);
    const audio = this.started.then(() =>
      generation === this.generation ? this.synthesize(text) : null,
    );
    let start!: () => void;
    this.started = new Promise<void>((resolve) => (start = resolve));
    this.played = this.played
      .then(() => this.playOne(generation, audio, start))
      .finally(() => {
        if (generation === this.generation) this._pending.update((count) => count - 1);
      });
  }

  stop(): void {
    this.generation += 1;
    this.played = Promise.resolve();
    this.started = Promise.resolve();
    this._pending.set(0);
    this._playing.set(false);
    this.player.stop();
  }

  private async playOne(
    generation: number,
    audio: Promise<LocalSpeakResponse | null>,
    start: () => void,
  ): Promise<void> {
    try {
      if (generation !== this.generation) return;
      let made: LocalSpeakResponse | null;
      try {
        made = await audio;
      } catch (err: unknown) {
        if (generation !== this.generation) return;
        const reason = readIpcError(err).message;
        this.stop();
        this._error.set(reason);
        return;
      }
      if (made === null || generation !== this.generation) return;
      this._playing.set(true);
      start();
      try {
        await this.player.play(made);
      } catch (err: unknown) {
        if (generation !== this.generation) return;
        const reason = readIpcError(err).message;
        this.stop();
        this._error.set(reason);
      } finally {
        if (generation === this.generation) this._playing.set(false);
      }
    } finally {
      start();
    }
  }
}
