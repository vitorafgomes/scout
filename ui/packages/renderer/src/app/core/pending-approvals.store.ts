import { Injectable, computed, inject, signal } from '@angular/core';

import { readIpcError, type PendingActionDto, type WriteLogEntryDto } from '@scout/shared';

import { ScoutBridgeService } from './scout-bridge';

/** The sentence a decision shows when another window, or another click, decided the action first. */
export const ALREADY_DECIDED = 'Already decided';

const ALREADY_DECIDED_CODE = 'control-plane/already-decided';
const ACTION_REJECTED_CODE = 'control-plane/action-rejected';
const WRITES_READ = 50;

const FEATURE_NAMES: Readonly<Record<string, string>> = {
  github: 'GitHub',
  kubernetes: 'Kubernetes',
  youtube: 'YouTube',
  desktop: 'Desktop',
};

/** Where an action stands on its card: waiting, approved and running, done, rejected, failed, or decided elsewhere first. */
export type ApprovalState = 'pending' | 'running' | 'done' | 'rejected' | 'failed' | 'already';

/** One card of the approval strip: the action, its title in words, where it stands and why, and a failed decision's sentence. */
export interface ApprovalCard {
  readonly action: PendingActionDto;
  readonly title: string;
  readonly feature: string;
  readonly state: ApprovalState;
  readonly detail: string | null;
  readonly error: string | null;
  readonly deciding: boolean;
}

interface Decided {
  readonly action: PendingActionDto;
  readonly state: 'running' | 'rejected' | 'already';
  readonly detail: string | null;
  readonly outcome: string | null;
}

/** The feature's name as the desktop writes it. */
export function featureName(id: string): string {
  return FEATURE_NAMES[id] ?? id;
}

/**
 * The sentence a failed decision shows: Already decided when the control plane kept an earlier one, Rejected and the reason
 * when it rejected the action instead, else its own reason.
 */
export function decisionError(err: unknown): string {
  const { code, message } = readIpcError(err);
  if (code === ALREADY_DECIDED_CODE) return ALREADY_DECIDED;
  return code === ACTION_REJECTED_CODE ? `Rejected: ${message}` : message;
}

/** True when the control plane refused the decision because the action was already decided. */
export function isAlreadyDecided(err: unknown): boolean {
  return readIpcError(err).code === ALREADY_DECIDED_CODE;
}

const ACTIONS_IN_WORDS: ReadonlyMap<string, string> = new Map([
  ['videos.insert', 'Upload a video'],
  ['videos.update', "Update a video's details"],
  ['thumbnails.set', 'Set a video thumbnail'],
  ['playlistItems.insert', 'Add a video to a playlist'],
  ['comments.insert', 'Publish a reply to a comment'],
  ['comments.setModerationStatus', 'Hold, publish or reject a comment'],
  ['youtubeReporting jobs.create', 'Create the reach reporting job'],
  ['Pull request reviews', 'Post a review with comments'],
  ['Pull request comments', 'Comment on a pull request'],
  ['New issues', 'Open an issue'],
  ['Issue comments', 'Comment on an issue'],
]);

function field(summary: string, key: string): string | null {
  const match = new RegExp(`(?:^|[(,]\\s*)${key}=([A-Za-z0-9._-]+)(?=[,)])`).exec(summary);
  return match?.[1] ?? null;
}

function verdictOf(summary: string): string | null {
  return (
    field(summary, 'verdict') ??
    (summary.startsWith('Approve ')
      ? 'approve'
      : summary.startsWith('Request changes')
        ? 'request_changes'
        : null)
  );
}

/**
 * The action in words, read from the pull request or repository its target or summary names: "Approve pull request #12
 * on acme/api", "Open an issue on acme/api"; the action's own name when neither names one.
 */
export function describeAction(action: PendingActionDto): string {
  const card = /^([A-Za-z0-9._-]+)\/([A-Za-z0-9._-]+)#(\d+)$/.exec(action.target);
  const owner = card?.[1] ?? field(action.summary, 'owner');
  const repo = card?.[2] ?? field(action.summary, 'repo');
  const number = card?.[3] ?? field(action.summary, 'number');
  const verdict = verdictOf(action.summary);
  if (owner === null || repo === null) return action.action;
  const where = `${owner}/${repo}`;
  if (action.action === 'New issues') return `Open an issue on ${where}`;
  if (number === null) return action.action;
  switch (action.action) {
    case 'Pull request verdicts':
      if (verdict === 'approve') return `Approve pull request #${number} on ${where}`;
      if (verdict === 'request_changes') {
        return `Request changes on pull request #${number} on ${where}`;
      }
      return action.action;
    case 'Pull request comments':
      return `Comment on pull request #${number} on ${where}`;
    case 'Issue comments':
      return `Comment on issue #${number} on ${where}`;
    case 'Pull request reviews':
      return `Post a review on pull request #${number} on ${where}`;
    default:
      return action.action;
  }
}

/**
 * The action as the Approvals screen writes it, from the scope name the queue and the write log hold: "Upload a video",
 * "Approve a pull request"; a verdict reads by its kind when `summary` names one. Any other scope reads unchanged.
 */
export function actionInWords(action: string, summary?: string): string {
  if (action === 'Pull request verdicts') {
    const verdict = summary === undefined ? null : verdictOf(summary);
    if (verdict === 'approve') return 'Approve a pull request';
    if (verdict === 'request_changes') return 'Request changes on a pull request';
    return 'Give a verdict on a pull request';
  }
  return ACTIONS_IN_WORDS.get(action) ?? action;
}

/** How a write ended, in words: `succeeded` reads Done, every other outcome with its first letter capitalised. */
export function outcomeInWords(outcome: string): string {
  if (outcome === 'succeeded' || outcome.startsWith('succeeded: ')) {
    return `Done${outcome.slice('succeeded'.length)}`;
  }
  return outcome.charAt(0).toUpperCase() + outcome.slice(1);
}

/** Who approved a write: You for the owner, Automatic (writes allowed) for a write the grant let run without asking. */
export function approverInWords(approvedBy: string | null): string {
  if (approvedBy === null) return 'Automatic (writes allowed)';
  return approvedBy === 'owner' ? 'You' : approvedBy;
}

/**
 * When something happened, on this computer's calendar days: "Today 14:02", "Yesterday 17:55", else "2026-11-10 09:05";
 * as `requested`, "Requested today 14:02", "Requested yesterday 17:55" or "Requested 2026-11-10 09:05".
 */
export function whenInWords(iso: string, form: 'history' | 'requested'): string {
  const at = new Date(iso);
  const now = new Date();
  const yesterday = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
  const two = (part: number): string => String(part).padStart(2, '0');
  const day =
    at.toDateString() === now.toDateString()
      ? 'Today'
      : at.toDateString() === yesterday.toDateString()
        ? 'Yesterday'
        : `${at.getFullYear()}-${two(at.getMonth() + 1)}-${two(at.getDate())}`;
  const time = `${two(at.getHours())}:${two(at.getMinutes())}`;
  return form === 'requested' ? `Requested ${day.toLowerCase()} ${time}` : `${day} ${time}`;
}

/** True for the entry that records the owner's approval, with or without a reason, which is not how the action ended. */
function isApproval(outcome: string): boolean {
  return outcome === 'approved' || outcome.startsWith('approved: ');
}

function finalState(outcome: string): {
  state: 'done' | 'failed' | 'rejected';
  detail: string | null;
} {
  if (outcome.startsWith('failed')) {
    const reason = outcome.replace(/^failed:?\s*/, '');
    return { state: 'failed', detail: reason.length > 0 ? reason : null };
  }
  if (outcome.startsWith('rejected')) {
    const reason = outcome.replace(/^rejected:?\s*/, '');
    return { state: 'rejected', detail: reason.length > 0 ? reason : null };
  }
  return { state: 'done', detail: null };
}

/**
 * The actions waiting for the owner, as the chat and the quick-ask window show them, one store per window. It reads
 * `scout:actions.list` and follows `scout:actions.changed`, so a decision taken in any window drops the action from
 * every window's cards; a decision taken here goes through `scout:actions.approve` or `scout:actions.reject`, the
 * channels the Approvals screen uses, and its card then shows the outcome the write log records for it.
 */
@Injectable({ providedIn: 'root' })
export class PendingApprovals {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly listed = signal<readonly PendingActionDto[]>([]);
  private readonly decided = signal<ReadonlyMap<string, Decided>>(new Map());
  private readonly errors = signal<ReadonlyMap<string, string>>(new Map());
  private readonly inFlight = signal<ReadonlySet<string>>(new Set());
  private readonly claimed = new Set<string>();

  /** Every card, newest request first: the actions still pending and the ones decided in this window. */
  readonly cards = computed<readonly ApprovalCard[]>(() => {
    const decided = this.decided();
    const errors = this.errors();
    const inFlight = this.inFlight();
    const waiting: ApprovalCard[] = this.listed()
      .filter((a) => a.status === 'pending' && !decided.has(a.id))
      .map((a) => ({
        action: a,
        title: describeAction(a),
        feature: featureName(a.featureId),
        state: 'pending',
        detail: null,
        error: errors.get(a.id) ?? null,
        deciding: inFlight.has(a.id),
      }));
    const outcomes: ApprovalCard[] = [...decided.values()].map((d) => {
      const ended = d.outcome === null ? null : finalState(d.outcome);
      return {
        action: d.action,
        title: describeAction(d.action),
        feature: featureName(d.action.featureId),
        state: ended?.state ?? d.state,
        detail: ended?.detail ?? d.detail,
        error: null,
        deciding: false,
      };
    });
    return [...waiting, ...outcomes].sort(
      (a, b) => Date.parse(b.action.requestedAt) - Date.parse(a.action.requestedAt),
    );
  });

  /** How many actions wait for a decision. */
  readonly waiting = computed(() => this.cards().filter((c) => c.state === 'pending').length);

  constructor() {
    this.bridge.onActionsChanged(() => void this.load());
  }

  /** Reads the pending list and, for the actions approved here, the write log; a failed read keeps what was shown. */
  async load(): Promise<void> {
    try {
      this.listed.set((await this.bridge.actionsList()).actions);
    } catch {
      return;
    }
    const open = [...this.decided().values()].filter(
      (d) => d.state === 'running' && d.outcome === null,
    );
    if (open.length === 0) return;
    let entries: readonly WriteLogEntryDto[];
    try {
      entries = (await this.bridge.writesList({ limit: WRITES_READ })).entries;
    } catch {
      return;
    }
    const next = new Map(this.decided());
    for (const d of open) {
      const entry = entries.find(
        (e) => e.actionId === d.action.id && !isApproval(e.outcome) && e.outcome.length > 0,
      );
      if (entry !== undefined) next.set(d.action.id, { ...d, outcome: entry.outcome });
    }
    this.decided.set(next);
  }

  /**
   * The owner's decision on one action, sent once: a second click while the first is in flight does nothing. A
   * refusal shows on the card, Already decided when another window decided it first; an approval the control plane turned
   * into a rejection, the action being asked before the current grant, keeps the card as Rejected with that reason.
   */
  async decide(
    action: PendingActionDto,
    decision: 'approve' | 'reject',
    reason = '',
  ): Promise<void> {
    if (this.claimed.has(action.id)) return;
    this.claimed.add(action.id);
    this.inFlight.set(new Set(this.claimed));
    this.setError(action.id, null);
    const trimmed = reason.trim();
    const req = trimmed.length > 0 ? { id: action.id, reason: trimmed } : { id: action.id };
    try {
      const answer =
        decision === 'approve'
          ? await this.bridge.actionsApprove(req)
          : await this.bridge.actionsReject(req);
      this.remember({
        action: { ...action, ...answer },
        state: decision === 'approve' ? 'running' : 'rejected',
        detail: decision === 'reject' && trimmed.length > 0 ? trimmed : null,
        outcome: null,
      });
    } catch (err: unknown) {
      const { code, message } = readIpcError(err);
      if (isAlreadyDecided(err)) {
        this.remember({ action, state: 'already', detail: null, outcome: null });
      } else if (code === ACTION_REJECTED_CODE) {
        this.remember({ action, state: 'rejected', detail: message, outcome: null });
      } else {
        this.setError(action.id, decisionError(err));
      }
    } finally {
      this.claimed.delete(action.id);
      this.inFlight.set(new Set(this.claimed));
    }
    await this.load();
  }

  /** Takes a decided card off the strip; the decision stays in the Approvals history. */
  dismiss(id: string): void {
    const next = new Map(this.decided());
    next.delete(id);
    this.decided.set(next);
  }

  private remember(decided: Decided): void {
    this.decided.set(new Map(this.decided()).set(decided.action.id, decided));
  }

  private setError(id: string, message: string | null): void {
    const next = new Map(this.errors());
    if (message === null) next.delete(id);
    else next.set(id, message);
    this.errors.set(next);
  }
}
