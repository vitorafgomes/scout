import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';

import {
  ISSUES_TARGET,
  issueLinks,
  readIpcError,
  type GitHubNotePreview,
  type GitHubVerdictKey,
  type GitHubVerdictRequest,
  type GitHubVerdictStatus,
  type GitHubWaitingApproval,
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

/**
 * Where the owner's verdict on a pull request stands in this window: being sent, then the action's state, the verdict
 * as the control plane reports it, who signed it, the verdict given before it at the same commit, and `unchanged` when
 * the last click asked for the very verdict already given, so nothing new was sent.
 */
export interface VerdictView {
  readonly verdict: GitHubVerdictRequest['verdict'];
  readonly state: 'sending' | GitHubVerdictStatus['state'];
  readonly actionId?: string;
  readonly url?: string | null;
  readonly reason?: string | null;
  readonly afterChecks?: true;
  readonly signedAs?: string | null;
  readonly earlier?: GitHubVerdictRequest['verdict'];
  readonly unchanged?: true;
}

/** The note the worker composed for the verdict a card previews, or why it could not compose it. */
export interface NotePreview {
  readonly verdict: GitHubVerdictRequest['verdict'];
  readonly note?: GitHubNotePreview;
  readonly error?: string;
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

/** How long the card's preview waits after the last change of a burst before it asks the worker. */
export const PREVIEW_QUIET_MS = 400;
const NO_DECISIONS: Decisions = {};

/**
 * The owner's decisions and posting state per findings turn. A pick, an undo or an edited text is written to
 * main with `scout:local.setDecisions` and read back from the turn main broadcasts, so both windows show the
 * same; until that broadcast arrives the write shows as pending here. An audit's issues follow the conversation:
 * a finished issues turn marks the findings whose titles it carried as sent, whatever the reply says, one with
 * issue links of the audited repository marks the cards opened, one that fails sends nothing and brings the buttons
 * back, and a findings turn that leaves the list, as on Clear, is forgotten. A verdict, and a review's Post, which is the
 * verdict `comment`, follows its action; an approval waiting for the checks follows what main pushes for its key.
 */
@Injectable({ providedIn: 'root' })
export class ReviewFindingsStore {
  private readonly states = signal<Readonly<Record<string, WindowState>>>({});
  private readonly pending = signal<Readonly<Record<string, Decisions>>>({});
  private readonly verdicts = signal<Readonly<Record<string, VerdictView>>>({});
  private readonly waitings = signal<Readonly<Record<string, GitHubWaitingApproval>>>({});
  private readonly previews = signal<Readonly<Record<string, NotePreview>>>({});
  private readonly previewAsked = new Map<string, { readonly request: string }>();
  private readonly previewTimers = new Map<string, ReturnType<typeof setTimeout>>();
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
    this.bridge.onGitHubWaitingApproval((status) => this.applyWaiting(status));
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
    if (this.state(turnId).preview) this.forgetPreview(turnId);
    this.update(turnId, (s) => ({ ...s, preview: !s.preview }));
  }

  markPosting(turnId: string, postTurnId: string | undefined): void {
    this.update(turnId, ({ postTurnId: _previous, ...s }) =>
      postTurnId === undefined ? s : { ...s, postTurnId },
    );
  }

  /**
   * Asks main once per findings turn and pull request whether a verdict on it is in progress or given, and whether
   * an approval waits for its checks, so a window opened after the click, or a new findings turn, shows it; a push or
   * a click this window saw first wins.
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
    this.bridge
      .githubWaitingApprovalFor(key)
      .then((status) => {
        const memoKey = verdictKey(key.repository, key.number, key.commit);
        if (status !== null && this.waitings()[memoKey] === undefined) this.applyWaiting(status);
      })
      .catch(() => undefined);
  }

  /** The approval waiting for the checks of a pull request at the reviewed commit, or the last one that ended. */
  waiting(repository: string, number: number, commit: string): GitHubWaitingApproval | null {
    return this.waitings()[verdictKey(repository, number, commit)] ?? null;
  }

  /**
   * Asks main to give this approval once every check of the commit passed; the click is the owner's approval of
   * exactly this request. A verdict that failed on the key gives way to the waiting approval. Answers main's refusal,
   * or null.
   */
  async approveWhenChecksPass(request: GitHubVerdictRequest): Promise<string | null> {
    try {
      const status = await this.bridge.githubApproveWhenChecksPass(request);
      const key = verdictKey(request.repository, request.number, request.commit);
      this.verdicts.update(({ [key]: current, ...rest }) =>
        current === undefined || current.state === 'failed' ? rest : { ...rest, [key]: current },
      );
      this.applyWaiting(status);
      return null;
    } catch (err: unknown) {
      return readIpcError(err).message;
    }
  }

  /** Asks main to stop waiting and give nothing; the card draws the review as before the click. Answers main's refusal, or null. */
  async cancelWaiting(key: GitHubVerdictKey): Promise<string | null> {
    try {
      await this.bridge.githubCancelWaitingApproval(key);
      const current = this.waiting(key.repository, key.number, key.commit);
      if (current?.state === 'waiting') {
        this.applyWaiting({ ...current, state: 'ended', reason: 'cancelled', detail: null });
      }
      return null;
    } catch (err: unknown) {
      return readIpcError(err).message;
    }
  }

  /**
   * Asks the worker, through main, for the note and comments `request` would post. The first request after the
   * preview opens is sent at once; a burst of later changes is sent as its last request, `PREVIEW_QUIET_MS` after the
   * last change. An answer to a request the card no longer shows is dropped, and none is cached, so the head and the
   * checks are read again each time. Nothing is sent to GitHub.
   */
  preview(turnId: string, request: GitHubVerdictRequest): void {
    const asked = { request: JSON.stringify(request) };
    const before = this.previewAsked.get(turnId);
    if (before?.request === asked.request) return;
    this.previewAsked.set(turnId, asked);
    clearTimeout(this.previewTimers.get(turnId));
    const keep = (preview: NotePreview) => {
      if (this.previewAsked.get(turnId) === asked) {
        this.previews.update((p) => ({ ...p, [turnId]: preview }));
      }
    };
    const send = () => {
      this.previewTimers.delete(turnId);
      this.bridge.githubPreviewVerdict(request).then(
        (note) => keep({ verdict: request.verdict, note }),
        (err: unknown) => keep({ verdict: request.verdict, error: readIpcError(err).message }),
      );
    };
    if (before === undefined) send();
    else this.previewTimers.set(turnId, setTimeout(send, PREVIEW_QUIET_MS));
  }

  /**
   * Drops the preview of a card that left the view: its note is no longer shown, its waiting request is never sent,
   * an answer still on its way is dropped, and its next request is asked at once.
   */
  forgetPreview(turnId: string): void {
    clearTimeout(this.previewTimers.get(turnId));
    this.previewTimers.delete(turnId);
    this.previewAsked.delete(turnId);
    this.previews.update(({ [turnId]: _dropped, ...rest }) => rest);
  }

  /** The last note the worker composed for a card's preview, or why it could not; null before the first answer. */
  notePreview(turnId: string): NotePreview | null {
    return this.previews()[turnId] ?? null;
  }

  /** The verdict on a pull request at the reviewed commit, as this window last saw its action. */
  verdict(repository: string, number: number, commit: string): VerdictView | null {
    return this.verdicts()[verdictKey(repository, number, commit)] ?? null;
  }

  /**
   * Gives the owner's verdict as one action; nothing is sent while a verdict on the same pull request and commit
   * is being sent. A verdict given before it stays named as the earlier one; when the control plane answers with the
   * action already given, the request was the same and the view says nothing new was sent. A refusal shows as the
   * failed verdict's reason.
   */
  async giveVerdict(request: GitHubVerdictRequest): Promise<void> {
    const key = verdictKey(request.repository, request.number, request.commit);
    const before = this.verdicts()[key];
    if (before?.state === 'sending') return;
    const earlier = before?.state === 'given' ? { earlier: before.verdict } : {};
    this.verdicts.update((v) => ({
      ...v,
      [key]: { verdict: request.verdict, state: 'sending', ...earlier },
    }));
    try {
      const { actionId } = await this.bridge.githubGiveVerdict(request);
      if (before?.state === 'given' && before.actionId === actionId) {
        this.verdicts.update((v) => ({ ...v, [key]: { ...before, unchanged: true } }));
        return;
      }
      this.verdicts.update((v) => ({
        ...v,
        [key]: { verdict: request.verdict, state: 'queued', actionId, ...earlier },
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
        current.state !== 'failed' &&
        current.state !== 'given'
      ) {
        return verdicts;
      }
      const { verdict, actionId, state, url, reason, afterChecks, signedAs } = status;
      const earlier =
        current?.actionId === actionId ||
        current?.state === 'sending' ||
        current?.state === 'queued'
          ? current.earlier
          : current?.state === 'given'
            ? current.verdict
            : undefined;
      return {
        ...verdicts,
        [key]: {
          verdict,
          actionId,
          state,
          url,
          reason,
          signedAs,
          ...(afterChecks ? { afterChecks } : {}),
          ...(earlier === undefined ? {} : { earlier }),
        },
      };
    });
  }

  private applyWaiting(status: GitHubWaitingApproval): void {
    const key = verdictKey(status.repository, status.number, status.commit);
    this.waitings.update((waitings) => ({ ...waitings, [key]: status }));
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
    const review = parseReviewFindings(turn.reply, turn.notice === true)?.review;
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
  const urls = issueLinks(target, turn.reply);
  return urls.length === 0 ? null : { urls };
}
