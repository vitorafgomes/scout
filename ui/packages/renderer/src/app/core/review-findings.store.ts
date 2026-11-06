import { Injectable, effect, inject, signal, untracked } from '@angular/core';

import type { LocalTurn } from '@scout/shared';

import { LocalConversation } from './local-conversation';

/** What the owner decided on the findings of one reply, kept in memory for as long as that turn is listed. */
export interface ReviewState {
  readonly picks: Readonly<Record<string, 'yes' | 'no'>>;
  readonly edited: Readonly<Record<string, string>>;
  readonly editing: string | null;
  readonly postTurnId?: string;
  readonly posted?: { readonly urls: readonly string[] };
  readonly target?: string;
  readonly preview: boolean;
}

const EMPTY: ReviewState = { picks: {}, edited: {}, editing: null, preview: false };
const GITHUB_URL = /https:\/\/github\.com\/[^\s)\]>"'<]+/;
const POST_TARGET = / on ([A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+#\d+)$/;
const ISSUES_TARGET = / on ([A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+)$/;
const ISSUE_URL = /https:\/\/github\.com\/([A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+)\/issues\/\d+/g;

/**
 * The owner's picks, edited comments and posting state per findings turn. It follows the conversation:
 * a post turn that completes with a GitHub link marks the review posted, an issues turn that completes
 * with issue links of the audited repository marks the audit's issues opened, one that fails or ends
 * without a link brings the buttons back, and a findings turn that leaves the list, as on Clear, is forgotten.
 */
@Injectable({ providedIn: 'root' })
export class ReviewFindingsStore {
  private readonly states = signal<Readonly<Record<string, ReviewState>>>({});

  private readonly conversation = inject(LocalConversation);

  constructor() {
    effect(() => {
      const turns = this.conversation.turns();
      untracked(() => this.observe(turns));
    });
  }

  state(turnId: string): ReviewState {
    return this.states()[turnId] ?? EMPTY;
  }

  register(turnId: string, repository: string, number?: number): void {
    const target = number === undefined ? repository.toLowerCase() : `${repository}#${number}`;
    untracked(() => {
      this.update(turnId, (s) => ({ ...s, target }));
      this.observe(this.conversation.turns(), false);
    });
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

  observe(turns: readonly LocalTurn[], prune = true): void {
    const byId = new Map(turns.map((turn) => [turn.id, turn]));
    const postedBy = new Map<string, { readonly urls: readonly string[]; readonly at: number }>();
    turns.forEach((turn, at) => {
      if (turn.state !== 'done') return;
      const target = turn.review
        ? POST_TARGET.exec(turn.question)?.[1]
        : turn.issues
          ? ISSUES_TARGET.exec(turn.question)?.[1]?.toLowerCase()
          : undefined;
      const urls = target === undefined ? [] : links(target, turn.reply);
      if (target !== undefined && urls.length > 0) postedBy.set(target, { urls, at });
    });
    const next: Record<string, ReviewState> = {};
    for (const [turnId, state] of Object.entries(this.states())) {
      if (prune && !byId.has(turnId)) continue;
      const elsewhere: { readonly urls: readonly string[]; readonly at: number } | undefined =
        state.target === undefined ? undefined : postedBy.get(state.target);
      if (
        state.posted === undefined &&
        elsewhere !== undefined &&
        turns.findIndex((turn) => turn.id === turnId) < elsewhere.at
      ) {
        const { postTurnId: _pending, ...rest } = state;
        next[turnId] = { ...rest, posted: { urls: elsewhere.urls } };
        continue;
      }
      const post = state.postTurnId === undefined ? undefined : byId.get(state.postTurnId);
      if (state.posted !== undefined || post === undefined || post.state === 'running') {
        next[turnId] = state;
        continue;
      }
      const urls = post.state === 'done' ? links(state.target, post.reply) : [];
      const { postTurnId: _done, ...rest } = state;
      next[turnId] = urls.length === 0 ? rest : { ...state, posted: { urls } };
    }
    this.states.set(next);
  }

  private update(turnId: string, change: (state: ReviewState) => ReviewState): void {
    this.states.update((states) => ({ ...states, [turnId]: change(states[turnId] ?? EMPTY) }));
  }
}

function links(target: string | undefined, reply: string): readonly string[] {
  if (target === undefined || target.includes('#')) {
    const url = GITHUB_URL.exec(reply)?.[0];
    return url === undefined ? [] : [url.replace(/[.,;:!?]+$/, '')];
  }
  const found = [...reply.matchAll(ISSUE_URL)].filter((m) => m[1]!.toLowerCase() === target);
  return [...new Set(found.map((m) => m[0]))];
}
