import { Injectable, effect, inject, signal, untracked } from '@angular/core';

import type { LocalTurn } from '@scout/shared';

import { LocalConversation } from './local-conversation';

/** What the owner decided on the findings of one reply, kept in memory for as long as that turn is listed. */
export interface ReviewState {
  readonly picks: Readonly<Record<string, 'yes' | 'no'>>;
  readonly edited: Readonly<Record<string, string>>;
  readonly editing: string | null;
  readonly postTurnId?: string;
  readonly posted?: { readonly url: string };
  readonly preview: boolean;
}

const EMPTY: ReviewState = { picks: {}, edited: {}, editing: null, preview: false };
const GITHUB_URL = /https:\/\/github\.com\/[^\s)\]>"'<]+/;

/**
 * The owner's picks, edited comments and posting state per findings turn. It follows the conversation:
 * a post turn that completes with a GitHub link marks the review posted, one that fails or ends without
 * a link brings the buttons back, and a findings turn that leaves the list, as on Clear, is forgotten.
 */
@Injectable({ providedIn: 'root' })
export class ReviewFindingsStore {
  private readonly states = signal<Readonly<Record<string, ReviewState>>>({});

  constructor() {
    const conversation = inject(LocalConversation);
    effect(() => {
      const turns = conversation.turns();
      untracked(() => this.observe(turns));
    });
  }

  state(turnId: string): ReviewState {
    return this.states()[turnId] ?? EMPTY;
  }

  pick(turnId: string, id: string, choice: 'yes' | 'no'): void {
    this.update(turnId, (s) => ({ ...s, picks: { ...s.picks, [id]: choice } }));
  }

  yesAll(turnId: string, ids: readonly string[]): void {
    this.update(turnId, (s) => ({
      ...s,
      picks: { ...s.picks, ...Object.fromEntries(ids.map((id) => [id, 'yes' as const])) },
    }));
  }

  edit(turnId: string, id: string): void {
    this.update(turnId, (s) => ({ ...s, editing: s.editing === id ? null : id }));
  }

  useText(turnId: string, id: string, text: string): void {
    this.update(turnId, (s) => ({
      ...s,
      edited: { ...s.edited, [id]: text },
      picks: { ...s.picks, [id]: 'yes' },
      editing: null,
    }));
  }

  cancelEdit(turnId: string): void {
    this.update(turnId, (s) => ({ ...s, editing: null }));
  }

  togglePreview(turnId: string): void {
    this.update(turnId, (s) => ({ ...s, preview: !s.preview }));
  }

  markPosting(turnId: string, postTurnId: string | undefined): void {
    this.update(turnId, ({ postTurnId: _previous, ...s }) =>
      postTurnId === undefined ? s : { ...s, postTurnId },
    );
  }

  observe(turns: readonly LocalTurn[]): void {
    const byId = new Map(turns.map((turn) => [turn.id, turn]));
    const next: Record<string, ReviewState> = {};
    for (const [turnId, state] of Object.entries(this.states())) {
      if (!byId.has(turnId)) continue;
      const post = state.postTurnId === undefined ? undefined : byId.get(state.postTurnId);
      if (state.posted !== undefined || post === undefined || post.state === 'running') {
        next[turnId] = state;
        continue;
      }
      const url = post.state === 'done' ? GITHUB_URL.exec(post.reply)?.[0] : undefined;
      const { postTurnId: _done, ...rest } = state;
      next[turnId] =
        url === undefined ? rest : { ...state, posted: { url: url.replace(/[.,;:!?]+$/, '') } };
    }
    this.states.set(next);
  }

  private update(turnId: string, change: (state: ReviewState) => ReviewState): void {
    this.states.update((states) => ({ ...states, [turnId]: change(states[turnId] ?? EMPTY) }));
  }
}
