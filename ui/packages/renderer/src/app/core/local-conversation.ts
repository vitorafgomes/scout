import { Injectable, computed, inject, signal } from '@angular/core';

import { readIpcError, type LocalAskRequest, type LocalTurn } from '@scout/shared';

import { ScoutBridgeService } from './scout-bridge';
import { toBase64 } from '../quick-ask/wav-recorder';

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

  /** Resolves to the refusal to show when the question never became a turn, or null. */
  ask(text: string): Promise<string | null> {
    return this.send((id) => ({ id, text }));
  }

  askByVoice(wav: Uint8Array): Promise<string | null> {
    const base64 = toBase64(wav);
    return this.send((id) => ({ id, audio: { contentType: 'audio/wav', base64 } }));
  }

  stop(): void {
    for (const turn of this._turns()) {
      if (turn.state === 'running') {
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
