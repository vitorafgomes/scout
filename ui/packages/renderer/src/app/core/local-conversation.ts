import { Injectable, computed, inject, signal } from '@angular/core';

import { readIpcError, type LocalAskRequest, type LocalImage, type LocalTurn } from '@scout/shared';

import { ScoutBridgeService } from './scout-bridge';
import { toBase64 } from '../quick-ask/wav-recorder';

/** Why Stop is off while Scout posts a review or opens issues: a cancel mid-write, then a second click, could write twice. */
export const POSTING_CANNOT_STOP = 'Posting to GitHub; cannot be stopped.';

/**
 * The conversation with Scout on this computer, held by main and shared by the quick-ask window and
 * the Chat screen: this store mirrors main's list and sends questions, cancels and clears through it.
 */
@Injectable({ providedIn: 'root' })
export class LocalConversation {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly _turns = signal<readonly LocalTurn[]>([]);
  private pushed = false;

  readonly turns = this._turns.asReadonly();
  readonly running = computed(() => this._turns().find((turn) => turn.state === 'running') ?? null);
  /** True while the running turn posts a review or opens issues on GitHub; such a turn is never cancelled. */
  readonly posting = computed(() => {
    const turn = this.running();
    return turn !== null && (turn.review === true || turn.issues === true);
  });

  constructor() {
    this.bridge.onLocalTurns((turns) => {
      this.pushed = true;
      this._turns.set(turns);
    });
    this.bridge
      .localGetTurns()
      .then((turns) => {
        if (!this.pushed) this._turns.set(turns);
      })
      .catch(() => undefined);
  }

  /**
   * Sends a typed question, with a screenshot when one is given, marked as asked during a meeting when
   * `meeting` is true; resolves to the refusal to show when the question never became a turn, or null.
   */
  ask(text: string, image?: LocalImage, meeting = false): Promise<string | null> {
    return this.send((id) => ({
      id,
      text,
      ...(image !== undefined ? { image } : {}),
      ...(meeting ? { meeting: true as const } : {}),
    }));
  }

  /** Sends a typed question under the id the caller picked, so it can follow that turn; resolves as `ask` does. */
  askAs(id: string, text: string): Promise<string | null> {
    return this.send(() => ({ id, text }));
  }

  askByVoice(wav: Uint8Array, image?: LocalImage): Promise<string | null> {
    const base64 = toBase64(wav);
    return this.send((id) => ({
      id,
      audio: { contentType: 'audio/wav', base64 },
      ...(image !== undefined ? { image } : {}),
    }));
  }

  /** Cancels the running turn, unless it is posting to GitHub. */
  stop(): void {
    for (const turn of this._turns()) {
      if (turn.state === 'running' && !turn.review && !turn.issues) {
        void this.bridge.localCancel({ id: turn.id }).catch(() => undefined);
      }
    }
  }

  clear(): void {
    void this.bridge.localClearTurns().catch(() => undefined);
  }

  private async send(request: (id: string) => LocalAskRequest): Promise<string | null> {
    try {
      await this.bridge.localAsk(request(crypto.randomUUID()));
      return null;
    } catch (err: unknown) {
      const { code, message } = readIpcError(err);
      return code === 'local/not-running' ? null : message;
    }
  }
}
