import { Injectable, computed, inject, signal } from '@angular/core';

import { readIpcError, type LocalAskRequest, type LocalImage, type LocalTurn } from '@scout/shared';

import { ScoutBridgeService } from './scout-bridge';

/** Why Stop is off while Scout queues a card's issues for approval: a cancel midway, then a second click, could queue them twice. */
export const POSTING_CANNOT_STOP = 'Queuing cards; cannot be stopped.';

/**
 * The conversation with Scout on this computer, held by main and shared by the quick-ask window and
 * the Chat screen: this store mirrors main's list and sends questions, cancels and clears through it.
 */
@Injectable({ providedIn: 'root' })
export class LocalConversation {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly _turns = signal<readonly LocalTurn[]>([]);
  private readonly _askedHere = signal<string | null>(null);
  private readonly _loaded = signal(false);
  private pushed = false;
  private readonly sending = new Map<string, boolean>();

  readonly turns = this._turns.asReadonly();
  /** True once main's list has been read or pushed, so a screen can tell the history it opened on from turns that arrive later. */
  readonly loaded = this._loaded.asReadonly();
  /** The id of the last question this window sent, so only the window that asked reads its answer aloud. */
  readonly askedHere = this._askedHere.asReadonly();
  readonly running = computed(() => this._turns().find((turn) => turn.state === 'running') ?? null);
  /** True while the running turn queues a card's issues for the owner's approval; such a turn is never cancelled. */
  readonly posting = computed(() => {
    const turn = this.running();
    return turn !== null && turn.issues === true;
  });

  constructor() {
    this.bridge.onLocalTurns((turns) => {
      this.pushed = true;
      this._turns.set(turns);
      this._loaded.set(true);
    });
    this.bridge
      .localGetTurns()
      .then((turns) => {
        if (!this.pushed) this._turns.set(turns);
        this._loaded.set(true);
      })
      .catch(() => this._loaded.set(true));
  }

  /**
   * Sends a typed question, with a screenshot when one is given, marked as asked during a meeting when
   * `meeting` is true, and the data sent with it as `context` apart from the text; resolves to the refusal
   * to show when the question never became a turn, or null. `voice` marks a question dictated by holding the
   * microphone.
   */
  ask(
    text: string,
    image?: LocalImage,
    meeting = false,
    context = '',
    voice = false,
  ): Promise<string | null> {
    return this.send((id) => ({
      id,
      text,
      ...(image !== undefined ? { image } : {}),
      ...(meeting ? { meeting: true as const } : {}),
      ...(voice ? { voice: true as const } : {}),
      ...(context.length > 0 ? { context } : {}),
    }));
  }

  /** Sends a typed question under the id the caller picked, so it can follow that turn; resolves as `ask` does. */
  askAs(id: string, text: string): Promise<string | null> {
    return this.send(() => ({ id, text }));
  }

  /** Cancels the running turn, unless it is queuing a card's issues; a question still on its way to main is cancelled once main takes it. */
  stop(): void {
    for (const id of this.sending.keys()) {
      if (!this._turns().some((turn) => turn.id === id)) this.sending.set(id, true);
    }
    for (const turn of this._turns()) {
      if (turn.state === 'running' && !turn.issues) {
        void this.bridge.localCancel({ id: turn.id }).catch(() => undefined);
      }
    }
  }

  /** Empties the conversation, stopping an answer still running; resolves to main's refusal to show, or null. */
  async clear(): Promise<string | null> {
    try {
      await this.bridge.localClearTurns();
      return null;
    } catch (err: unknown) {
      return readIpcError(err).message;
    }
  }

  private async send(request: (id: string) => LocalAskRequest): Promise<string | null> {
    const sent = request(crypto.randomUUID());
    this.sending.set(sent.id, false);
    try {
      this._askedHere.set(sent.id);
      await this.bridge.localAsk(sent);
      const turn = this._turns().find((shown) => shown.id === sent.id);
      if (this.sending.get(sent.id) === true && turn?.issues !== true) {
        void this.bridge.localCancel({ id: sent.id }).catch(() => undefined);
      }
      return null;
    } catch (err: unknown) {
      const { code, message } = readIpcError(err);
      return code === 'local/not-running' ? null : message;
    } finally {
      this.sending.delete(sent.id);
    }
  }
}
