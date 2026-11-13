import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { LOCAL_CANCELLED_REPLY } from '@scout/shared';

import { LocalConversation } from '../core/local-conversation';
import { ScoutBridgeService } from '../core/scout-bridge';

export type BubbleState = 'idle' | 'listening' | 'answering' | 'failed';

const CARD: Readonly<Record<Exclude<BubbleState, 'idle'>, BubbleCard>> = {
  listening: {
    title: 'Listening',
    line: 'A Listen, System audio or Meeting is open; this shows ahead of answering and error, so an open microphone is never hidden. Open the quick ask window to see the transcript.',
  },
  answering: {
    title: 'Answering',
    line: 'Scout is working on your question. One question at a time.',
  },
  failed: {
    title: 'Error',
    line: 'The last answer failed, the worker not running included; a stop by you is not a failure. Open the quick ask window to read the reason.',
  },
};

/** The title and the line the floating button's card shows. */
export interface BubbleCard {
  readonly title: string;
  readonly line: string;
}

/**
 * What the floating button shows, for the button and for its card: listening while main reports a live listen open,
 * answering while a turn runs, failed when the last turn failed for any reason but the owner's stop, idle otherwise;
 * and the quick-ask shortcut as the owner presses it, once main reports it registered.
 */
@Injectable({ providedIn: 'root' })
export class BubbleStateStore {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly conversation = inject(LocalConversation);

  /** True while main reports a live listen open, from its push or, before the first push, its answer on open. */
  readonly listening = signal(false);
  /** The quick-ask shortcut as the owner presses it, once main reports it registered; null otherwise. */
  readonly shortcut = signal<string | null>(null);
  readonly state = computed<BubbleState>(() => {
    if (this.listening()) return 'listening';
    if (this.conversation.running() !== null) return 'answering';
    const last = this.conversation.turns().at(-1);
    if (last?.state !== 'failed') return 'idle';
    return (last.failure ?? last.reply) === LOCAL_CANCELLED_REPLY ? 'idle' : 'failed';
  });
  readonly card = computed<BubbleCard>(() => {
    const state = this.state();
    if (state !== 'idle') return CARD[state];
    const shortcut = this.shortcut();
    return {
      title: 'Idle',
      line:
        shortcut === null
          ? 'Click to open the quick ask window.'
          : `Click, or press ${shortcut}, to open the quick ask window.`,
    };
  });

  /** Follows main's listening pushes until `destroyRef` ends, reads the listening state and the shortcut once. */
  start(destroyRef: DestroyRef): void {
    let pushed = false;
    destroyRef.onDestroy(
      this.bridge.onLocalListening(({ listening }) => {
        pushed = true;
        this.listening.set(listening);
      }),
    );
    this.bridge
      .localListening()
      .then(({ listening }) => {
        if (!pushed) this.listening.set(listening);
      })
      .catch(() => undefined);
    const control = /Mac/.test(globalThis.navigator?.userAgent ?? '') ? 'Cmd' : 'Ctrl';
    this.bridge
      .localGetShortcut()
      .then(({ accelerator, registered }) => {
        if (registered)
          this.shortcut.set(accelerator.replace(/CommandOrControl|CmdOrCtrl/g, control));
      })
      .catch(() => undefined);
  }
}
