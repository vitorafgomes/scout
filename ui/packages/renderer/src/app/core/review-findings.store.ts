import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';

import {
  readIpcError,
  type GitHubVerdictKey,
  type GitHubVerdictRequest,
  type GitHubVerdictStatus,
  type LocalFindingDecision,
  type LocalTurn,
  type ReviewFindings,
} from '@scout/shared';

import { LocalConversation } from './local-conversation';
import { parseReviewFindings } from './review-findings';
import { ScoutBridgeService } from './scout-bridge';

/** The text the owner wrote for a finding in the editor: what is wrong and the fix. */
export interface EditedText {
  readonly why: string;
  readonly fix: string;
}

/**
 * What the owner decided on the findings of one reply: the picks and edited texts come from the decisions main
 * holds on the findings turn, shared by every window; the open editor, the preview and the posting state are
 * this window's.
 */
export interface ReviewState {
  readonly picks: Readonly<Record<string, 'yes' | 'no'>>;
  readonly edited: Readonly<Record<string, EditedText>>;
  readonly editing: string | null;
  readonly postTurnId?: string;
  readonly posted?: Posted;
  readonly target?: string;
  readonly preview: boolean;
}

/**
 * Where an audit's issues went: the links of the issues opened on its repository. A review's Post is an action of
 * its own and follows its verdict, never a turn.
 */
export interface Posted {
  readonly urls: readonly string[];
}

/** Where the owner's verdict on a pull request stands in this window: being sent, then the action's state. */
export interface VerdictView {
  readonly verdict: GitHubVerdictRequest['verdict'];
  readonly state: 'sending' | GitHubVerdictStatus['state'];
  readonly actionId?: string;
  readonly url?: string | null;
  readonly reason?: string | null;
}

/**
 * The cards a turn shows: the findings, and the turn whose decisions and posting state they use; `moved` on an
 * earlier batch of an audit, whose findings are in the cards of a later turn.
 */
export interface TurnCards {
  readonly anchor: string;
  readonly review: ReviewFindings;
  readonly moved?: true;
}

interface AuditGroup {
  readonly anchor: string;
  readonly last: string;
  readonly members: readonly string[];
  readonly review: ReviewFindings;
}

type Decisions = Readonly<Record<string, LocalFindingDecision>>;
type WindowState = Omit<ReviewState, 'picks' | 'edited'>;

const EMPTY: WindowState = { editing: null, preview: false };
const NO_DECISIONS: Decisions = {};
const ISSUES_TARGET = / on ([A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+)$/;
const ISSUE_URL = /https:\/\/github\.com\/([A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+)\/issues\/\d+/g;

/**
 * The owner's decisions and posting state per findings turn. A pick, an undo or an edited text is written to
 * main with `scout:local.setDecisions` and read back from the turn main broadcasts, so both windows show the
 * same; until that broadcast arrives the write shows as pending here. An audit's issues follow the conversation:
 * a finished issues turn marks the findings whose titles it carried as sent, whatever the reply says, one with
 * issue links of the audited repository marks the cards opened, one that fails sends nothing and brings the buttons
 * back, and a findings turn that leaves the list, as on Clear, is forgotten. A verdict, and a review's Post, which is the
 * verdict `comment`, follows its action.
 */
@Injectable({ providedIn: 'root' })
export class ReviewFindingsStore {
  private readonly states = signal<Readonly<Record<string, WindowState>>>({});
  private readonly pending = signal<Readonly<Record<string, Decisions>>>({});
  private readonly verdicts = signal<Readonly<Record<string, VerdictView>>>({});
  private readonly lookedUp = new Set<string>();

  private readonly conversation = inject(LocalConversation);
  private readonly bridge = inject(ScoutBridgeService);

  /** Which turn shows which cards: every review on its own turn, and an audit's batches merged on the latest one. */
  readonly cards = computed(() => findingsCards(this.conversation.turns()));

  /** The issue titles each repository, in lower case, was already asked to open by a finished issues turn. */
  readonly sent = computed(() => sentTitles(this.conversation.turns()));

  constructor() {
    effect(() => {
      const turns = this.conversation.turns();
      untracked(() => this.observe(turns));
    });
    this.bridge.onGitHubVerdict((status) => this.applyVerdict(status));
  }

  state(turnId: string): ReviewState {
    const decisions = this.decisions(turnId);
    const picks: Record<string, 'yes' | 'no'> = {};
    const edited: Record<string, EditedText> = {};
    for (const [id, decision] of Object.entries(decisions)) {
      if (decision.pick !== null) picks[id] = decision.pick;
      if (decision.edited !== undefined) edited[id] = decision.edited;
    }
    return { ...(this.states()[turnId] ?? EMPTY), picks, edited };
  }

  register(turnId: string, repository: string, number?: number): void {
    const target = number === undefined ? repository.toLowerCase() : `${repository}#${number}`;
    untracked(() => {
      this.update(turnId, (s) => ({ ...s, target }));
      this.observe(this.conversation.turns(), false);
    });
  }

  pick(turnId: string, id: string, choice: 'yes' | 'no'): void {
    this.decide(turnId, (d) => ({ ...d, [id]: decision(choice, d[id]?.edited) }));
  }

  /** Marks Yes on every finding still undecided or Yes; a dismissed finding stays dismissed. */
  yesAll(turnId: string, ids: readonly string[]): void {
    this.decide(turnId, (d) => ({
      ...d,
      ...Object.fromEntries(
        ids.filter((id) => d[id]?.pick !== 'no').map((id) => [id, decision('yes', d[id]?.edited)]),
      ),
    }));
  }

  /** Takes back the pick on one finding, so it is undecided again. */
  undo(turnId: string, id: string): void {
    this.decide(turnId, (d) => ({ ...d, [id]: decision(null, d[id]?.edited) }));
  }

  edit(turnId: string, id: string): void {
    this.update(turnId, (s) => ({ ...s, editing: s.editing === id ? null : id }));
  }

  useText(turnId: string, id: string, text: EditedText): void {
    this.decide(turnId, (d) => ({ ...d, [id]: decision('yes', text) }));
    this.update(turnId, (s) => ({ ...s, editing: null }));
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

  /**
   * Asks main once per findings turn and pull request whether a verdict on it is in progress or given, so a window
   * opened after the click, or a new findings turn, shows it; a push or a click this window saw first wins.
   */
  lookUpVerdict(turnId: string, key: GitHubVerdictKey): void {
    const memo = `${turnId}|${verdictKey(key.repository, key.number, key.commit)}`;
    if (this.lookedUp.has(memo)) return;
    this.lookedUp.add(memo);
    this.bridge
      .githubVerdictFor(key)
      .then((status) => {
        if (status !== null) this.applyVerdict(status);
      })
      .catch(() => undefined);
  }

  /** The verdict on a pull request at the reviewed commit, as this window last saw its action. */
  verdict(repository: string, number: number, commit: string): VerdictView | null {
    return this.verdicts()[verdictKey(repository, number, commit)] ?? null;
  }

  /**
   * Gives the owner's verdict as one action; nothing is sent while a verdict on the same pull request and commit
   * is being sent, queued, running or given. A refusal shows as the failed verdict's reason.
   */
  async giveVerdict(request: GitHubVerdictRequest): Promise<void> {
    const key = verdictKey(request.repository, request.number, request.commit);
    const current = this.verdicts()[key];
    if (current !== undefined && current.state !== 'failed') return;
    this.verdicts.update((v) => ({ ...v, [key]: { verdict: request.verdict, state: 'sending' } }));
    try {
      const { actionId } = await this.bridge.githubGiveVerdict(request);
      this.verdicts.update((v) => ({
        ...v,
        [key]: { verdict: request.verdict, state: 'queued', actionId },
      }));
      const status = await this.bridge.githubVerdictStatus({ actionId }).catch(() => null);
      if (status !== null) this.applyVerdict(status);
    } catch (err: unknown) {
      this.verdicts.update((v) => ({
        ...v,
        [key]: { verdict: request.verdict, state: 'failed', reason: readIpcError(err).message },
      }));
    }
  }

  observe(turns: readonly LocalTurn[], prune = true): void {
    const byId = new Map(turns.map((turn) => [turn.id, turn]));
    this.pending.update((pending) =>
      Object.fromEntries(
        Object.entries(pending).filter(([turnId, decisions]) => {
          const turn = byId.get(turnId);
          return turn !== undefined && !sameDecisions(turn.decisions ?? NO_DECISIONS, decisions);
        }),
      ),
    );
    const postedBy = new Map<string, Posted & { readonly at: number }>();
    turns.forEach((turn, at) => {
      if (turn.state !== 'done') return;
      const target = turn.issues
        ? ISSUES_TARGET.exec(turn.question)?.[1]?.toLowerCase()
        : undefined;
      const where = target === undefined ? null : outcome(target, turn);
      if (target !== undefined && where !== null) postedBy.set(target, { ...where, at });
    });
    const next: Record<string, WindowState> = {};
    for (const [turnId, state] of Object.entries(this.states())) {
      if (prune && !byId.has(turnId)) continue;
      const elsewhere = state.target === undefined ? undefined : postedBy.get(state.target);
      if (
        state.posted === undefined &&
        elsewhere !== undefined &&
        turns.findIndex((turn) => turn.id === turnId) < elsewhere.at
      ) {
        const { postTurnId: _pending, ...rest } = state;
        const { at: _at, ...where } = elsewhere;
        next[turnId] = { ...rest, posted: where };
        continue;
      }
      const post = state.postTurnId === undefined ? undefined : byId.get(state.postTurnId);
      if (state.posted !== undefined || post === undefined || post.state === 'running') {
        next[turnId] = state;
        continue;
      }
      const where = post.state === 'done' ? outcome(state.target, post) : null;
      const { postTurnId: _done, ...rest } = state;
      next[turnId] = where === null ? rest : { ...state, posted: where };
    }
    this.states.set(next);
  }

  private decisions(turnId: string): Decisions {
    return (
      this.pending()[turnId] ??
      this.conversation.turns().find((turn) => turn.id === turnId)?.decisions ??
      NO_DECISIONS
    );
  }

  private decide(turnId: string, change: (decisions: Decisions) => Decisions): void {
    const decisions = change(this.decisions(turnId));
    this.pending.update((pending) => ({ ...pending, [turnId]: decisions }));
    this.bridge.localSetDecisions({ turnId, decisions }).catch(() =>
      this.pending.update((pending) => {
        if (pending[turnId] !== decisions) return pending;
        const { [turnId]: _refused, ...rest } = pending;
        return rest;
      }),
    );
  }

  private applyVerdict(status: GitHubVerdictStatus): void {
    this.verdicts.update((verdicts) => {
      const key = verdictKey(status.repository, status.number, status.commit);
      const current = verdicts[key];
      if (
        current?.actionId !== undefined &&
        current.actionId !== status.actionId &&
        current.state !== 'failed'
      ) {
        return verdicts;
      }
      const { verdict, actionId, state, url, reason } = status;
      return { ...verdicts, [key]: { verdict, actionId, state, url, reason } };
    });
  }

  private update(turnId: string, change: (state: WindowState) => WindowState): void {
    this.states.update((states) => ({ ...states, [turnId]: change(states[turnId] ?? EMPTY) }));
  }
}

function decision(pick: 'yes' | 'no' | null, edited: EditedText | undefined): LocalFindingDecision {
  return edited === undefined
    ? { pick, dismissed: pick === 'no' }
    : { pick, dismissed: pick === 'no', edited };
}

function sameDecisions(a: Decisions, b: Decisions): boolean {
  const ids = Object.keys(a);
  return (
    ids.length === Object.keys(b).length &&
    ids.every((id) => {
      const x = a[id]!;
      const y = b[id];
      return (
        y !== undefined &&
        x.pick === y.pick &&
        x.dismissed === y.dismissed &&
        x.edited?.why === y.edited?.why &&
        x.edited?.fix === y.edited?.fix
      );
    })
  );
}

/**
 * The cards of every findings turn: a review shows its own; the batches of one audit (same repository and commit)
 * merge by finding id into the latest batch's turn, the first batch's turn keeping the decisions and the posting
 * state, so a later batch adds cards and never repeats one, and the earlier batches say their findings are in the
 * cards below; an issues turn that only queued the issues keeps the group, so the next batch joins the same cards
 * beside the findings already sent; once issues are opened on that repository, with links, the next batch starts
 * new cards with only the findings not shown before.
 */
export function findingsCards(turns: readonly LocalTurn[]): ReadonlyMap<string, TurnCards> {
  const shown = new Map<string, TurnCards>();
  const open = new Map<string, AuditGroup>();
  const closed: AuditGroup[] = [];
  const seen = new Map<string, Set<string>>();
  for (const turn of turns) {
    if (turn.state !== 'done') continue;
    const opened = turn.issues ? ISSUES_TARGET.exec(turn.question)?.[1]?.toLowerCase() : undefined;
    if (opened !== undefined && outcome(opened, turn) !== null) {
      for (const [key, group] of open) {
        if (group.review.repository.toLowerCase() !== opened) continue;
        closed.push(group);
        open.delete(key);
      }
    }
    const review = parseReviewFindings(turn.reply)?.review;
    if (review === undefined) continue;
    if (review.kind !== 'audit') {
      shown.set(turn.id, { anchor: turn.id, review });
      continue;
    }
    const key = `${review.repository.toLowerCase()}@${review.commit ?? ''}`;
    const ids = seen.get(key) ?? new Set<string>();
    seen.set(key, ids);
    const added = review.findings.filter((f) => !ids.has(f.id));
    added.forEach((f) => ids.add(f.id));
    const group = open.get(key);
    if (group === undefined) {
      if (added.length > 0) {
        open.set(key, {
          anchor: turn.id,
          last: turn.id,
          members: [turn.id],
          review: { ...review, findings: added },
        });
      }
      continue;
    }
    const known = [...new Set([...(group.review.known ?? []), ...(review.known ?? [])])];
    open.set(key, {
      anchor: group.anchor,
      last: turn.id,
      members: [...group.members, turn.id],
      review: {
        ...review,
        ...(known.length === 0 ? {} : { known }),
        findings: [...group.review.findings, ...added],
      },
    });
  }
  for (const group of [...closed, ...open.values()]) {
    for (const member of group.members) {
      shown.set(
        member,
        member === group.last
          ? { anchor: group.anchor, review: group.review }
          : { anchor: group.anchor, review: group.review, moved: true },
      );
    }
  }
  return shown;
}

function verdictKey(repository: string, number: number, commit: string): string {
  return `${repository.toLowerCase()}#${number}@${commit}`;
}

/**
 * The titles every finished issues turn carried, per repository in lower case: a finding with one of those titles,
 * trimmed, was sent and is never asked for again; a turn that failed or still runs sent nothing.
 */
export function sentTitles(turns: readonly LocalTurn[]): ReadonlyMap<string, ReadonlySet<string>> {
  const sent = new Map<string, Set<string>>();
  for (const turn of turns) {
    if (turn.state !== 'done' || turn.issues !== true || turn.issueTitles === undefined) continue;
    const repository = ISSUES_TARGET.exec(turn.question)?.[1]?.toLowerCase();
    if (repository === undefined) continue;
    const titles = sent.get(repository) ?? new Set<string>();
    turn.issueTitles.forEach((title) => titles.add(title.trim()));
    sent.set(repository, titles);
  }
  return sent;
}

/** Where a finished post turn left its findings: at the issue links of the target, or null when it gave none. */
function outcome(target: string | undefined, turn: LocalTurn): Posted | null {
  const urls = links(target, turn.reply);
  return urls.length === 0 ? null : { urls };
}

function links(target: string | undefined, reply: string): readonly string[] {
  if (target === undefined) return [];
  const found = [...reply.matchAll(ISSUE_URL)].filter((m) => m[1]!.toLowerCase() === target);
  return [...new Set(found.map((m) => m[0]))];
}
